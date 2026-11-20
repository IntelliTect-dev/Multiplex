using System.Text.Json;

namespace Multiplex.Sites.GoogleFlights.Tests;

/// <summary>
/// Reads the committed, scrubbed HAR references (services/GoogleFlights/references) as fixtures.
/// Nothing here is hand-written: every request and answer is what Google's page or the direct
/// client actually sent and received.
/// </summary>
internal sealed class HarFixture
{
    private readonly JsonElement[] _entries;

    private HarFixture(string name, JsonElement[] entries)
    {
        Name = name;
        _entries = entries;
    }

    public string Name { get; }

    public int Count => _entries.Length;

    /// <summary>Loads <c>google-flights-&lt;name&gt;.har</c>, e.g. <c>direct-http-flow-2026-11-20</c>.</summary>
    public static HarFixture Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "References", $"google-flights-{name}.har");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var entries = doc.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray().Select(e => e.Clone()).ToArray();
        return new HarFixture(name, entries);
    }

    public static IEnumerable<string> AllNames() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "References"), "google-flights-*.har")
            .Select(p => Path.GetFileNameWithoutExtension(p)["google-flights-".Length..])
            .Order(StringComparer.Ordinal);

    public HarEntry this[int index] => new(_entries[index]);

    public IEnumerable<HarEntry> Entries => _entries.Select(e => new HarEntry(e));
}

internal sealed class HarEntry(JsonElement entry)
{
    public string Url => entry.GetProperty("request").GetProperty("url").GetString()!;

    public string Rpc
    {
        get
        {
            var uri = new Uri(Url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            return query["rpcids"] ?? uri.AbsolutePath.Split('/').Last();
        }
    }

    public int Status => entry.GetProperty("response").GetProperty("status").GetInt32();

    public string ResponseText =>
        entry.GetProperty("response").GetProperty("content").TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";

    public string RequestBody =>
        entry.GetProperty("request").TryGetProperty("postData", out var p) && p.TryGetProperty("text", out var t)
            ? t.GetString() ?? ""
            : "";

    public string? RequestHeader(string name) =>
        entry.GetProperty("request").GetProperty("headers").EnumerateArray()
            .Where(h => string.Equals(h.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
            .Select(h => h.GetProperty("value").GetString())
            .FirstOrDefault();

    public string? ResponseHeader(string name) =>
        entry.GetProperty("response").GetProperty("headers").EnumerateArray()
            .Where(h => string.Equals(h.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
            .Select(h => h.GetProperty("value").GetString())
            .FirstOrDefault();

    /// <summary>The <c>f.req</c> form field, URL-decoded: the outer JSON envelope.</summary>
    public string FReq
    {
        get
        {
            foreach (var pair in RequestBody.Split('&'))
            {
                if (pair.StartsWith("f.req=", StringComparison.Ordinal))
                    return Uri.UnescapeDataString(pair["f.req=".Length..].Replace('+', ' '));
            }
            throw new InvalidOperationException("The request has no f.req field.");
        }
    }

    /// <summary>The service RPC's inner request JSON (the string inside <c>[null, "..."]</c>).</summary>
    public string InnerRequest
    {
        get
        {
            using var doc = JsonDocument.Parse(FReq);
            var root = doc.RootElement;
            return root[0].ValueKind == JsonValueKind.Null
                ? root[1].GetString()!
                : root[0][0][1].GetString()!; // batchexecute: [[[rpcId, "<inner>", null, "generic"]]]
        }
    }

    /// <summary>The <c>tfs</c> value of the page URL the request was sent from, when the page sent it.</summary>
    public string? RefererTfs
    {
        get
        {
            var referer = RequestHeader("referer");
            if (referer is null)
                return null;
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(referer).Query);
            return query["tfs"];
        }
    }

    public string? Comment => entry.TryGetProperty("comment", out var c) ? c.GetString() : null;
}
