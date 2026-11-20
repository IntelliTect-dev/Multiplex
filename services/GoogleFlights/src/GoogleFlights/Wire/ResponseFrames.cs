using System.Text.Json;

namespace Multiplex.Sites.GoogleFlights.Wire;

/// <summary>A gRPC status Google returned in place of a payload (protocol.md section 3).</summary>
/// <param name="Code">The gRPC status code: 13 = INTERNAL (a refusal), 3 = INVALID_ARGUMENT, 14 = UNAVAILABLE.</param>
/// <param name="HasDetail">Whether an <c>ErrorResponse</c> detail followed the code (the page's refusal shape).</param>
internal sealed record RpcError(int Code, bool HasDetail);

/// <summary>The data and error items of one framed answer.</summary>
internal sealed record RpcFrames(IReadOnlyList<JsonElement> Payloads, IReadOnlyList<RpcError> Errors)
{
    /// <summary>The last payload that carries <paramref name="index"/> as a non-null value.</summary>
    public JsonElement? LastWith(int index)
    {
        for (var i = Payloads.Count - 1; i >= 0; i--)
        {
            if (Payloads[i].At(index) is not null)
                return Payloads[i];
        }
        return null;
    }

    /// <summary>The last payload, or <c>null</c> when there is none.</summary>
    public JsonElement? Last => Payloads.Count == 0 ? null : Payloads[^1];
}

/// <summary>
/// Reads the answer framing every Google Flights RPC uses: the <c>)]}'</c> guard, then lines that
/// are JSON arrays of items. A <c>["wrb.fr", id, "&lt;payload JSON&gt;", ...]</c> item carries data;
/// one whose payload is null and whose index 5 is <c>[code, ...]</c> carries a gRPC error.
/// The length lines are not trusted: each line that starts with <c>[</c> is parsed on its own.
/// </summary>
internal static class ResponseFrames
{
    private const string Guard = ")]}'";

    public static RpcFrames Parse(string body) =>
        TryParse(body, out var frames)
            ? frames
            : throw new GoogleFlightsFormatException("Google's answer was not in its usual framed format.");

    public static bool TryParse(string body, out RpcFrames frames)
    {
        frames = new RpcFrames([], []);
        var text = body.TrimStart();
        if (!text.StartsWith(Guard, StringComparison.Ordinal))
            return false;

        var payloads = new List<JsonElement>();
        var errors = new List<RpcError>();
        foreach (var raw in text[Guard.Length..].Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('['))
                continue;

            JsonElement items;
            try
            {
                using var doc = JsonDocument.Parse(line);
                items = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue; // a partial or non-JSON line; the length lines are not relied on
            }

            if (items.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in items.EnumerateArray())
            {
                if (item.Str(0) != "wrb.fr")
                    continue;
                if (item.Str(2) is { } payload)
                {
                    using var inner = JsonDocument.Parse(payload);
                    payloads.Add(inner.RootElement.Clone());
                }
                else if (item.At(5) is { ValueKind: JsonValueKind.Array } status && status.Int(0) is { } code)
                {
                    errors.Add(new RpcError(code, status.At(2) is not null));
                }
            }
        }

        frames = new RpcFrames(payloads, errors);
        return true;
    }
}
