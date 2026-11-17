using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TripIt;

namespace TripItMcp;

/// <summary>
/// The raw GET escape hatch. Registered only when write tools are (it can reach GET endpoints
/// that change state, such as <c>v2/move</c>), and never served by the Multiplex host.
/// </summary>
[McpServerToolType]
public sealed class TripItRawTools(TripItClient client)
{
    private const string Prefix = "/api/v2/";

    [McpServerTool(Name = "tripit_raw_get", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Escape hatch: authenticated GET against a TripIt web API path (must start with " +
        "'/api/v2/') returning the raw JSON. Some TripIt GETs change data (e.g. v2/move), so treat " +
        "it as a write. Use only when no dedicated tool covers the data.")]
    public Task<string> RawGet(
        [Description("Path beginning with '/api/v2/', e.g. '/api/v2/list/trip'. No '..', percent-encoding, '?' or '#'.")]
        string path,
        [Description("Optional query parameters as a JSON object, e.g. {\"past\":\"true\"}.")] string? queryJson = null,
        CancellationToken ct = default)
    {
        var checkedPath = ValidatePath(path);
        List<KeyValuePair<string, string?>>? q = null;
        if (!string.IsNullOrWhiteSpace(queryJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(queryJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("query_json must be a JSON object.");
                q = [.. doc.RootElement.EnumerateObject().Select(p => new KeyValuePair<string, string?>(
                    p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString()))];
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"query_json is not valid JSON: {ex.Message}");
            }
        }
        return client.SendRawAsync(checkedPath, q, ct);
    }

    /// <summary>
    /// Accepts only a literal path under <c>/api/v2/</c>: no dot segments, no percent-encoding
    /// (which could smuggle <c>%2e%2e</c> or <c>%2f</c> past the check), no query or fragment
    /// (query parameters go in <c>query_json</c>), no backslashes, empty segments or whitespace.
    /// </summary>
    internal static string ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(Prefix, StringComparison.Ordinal))
            throw new ArgumentException("path must start with '/api/v2/'.");
        if (path.Contains('%'))
            throw new ArgumentException("path must not contain percent-encoding (e.g. %2e or %2f); give the literal path.");
        if (path.Contains('?') || path.Contains('#'))
            throw new ArgumentException("path must not contain '?' or '#'; pass query parameters in query_json.");
        if (path.Contains('\\') || path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("path must not contain backslashes, whitespace or control characters.");
        if (path.Contains("//", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal) ||
            path.Split('/').Any(s => s == "."))
            throw new ArgumentException("path must not contain '.' or '..' segments or empty segments.");
        return path;
    }
}
