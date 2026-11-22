using Multiplex.Sites.GoogleFlights.Wire;

namespace Multiplex.Sites.GoogleFlights.Tests;

/// <summary>
/// Search pages as fixtures. A search page carries its first answer in its HTML, in the data block <c>ds:1</c>,
/// laid out exactly like a <c>GetShoppingResults</c> payload (protocol.md section 5).
/// </summary>
internal static class SearchPages
{
    /// <summary>
    /// Loads <c>references/pages/google-flights-search-page-data-&lt;name&gt;.html</c>: the <c>ds:1</c> element of a
    /// page the client fetched, verbatim apart from the page's one-time script nonce.
    /// </summary>
    public static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "References", "Pages", $"google-flights-search-page-data-{name}.html"));

    /// <summary>
    /// A recorded <c>GetShoppingResults</c> answer embedded the way a search page embeds its first answer, in the
    /// element the recorded pages use. For the tests about the client's own behavior (cache, pacing, refusals),
    /// which predate the search pages and are not about the page's format.
    /// </summary>
    public static string FromRpcAnswer(HarEntry recorded)
    {
        if (!ResponseFrames.TryParse(recorded.ResponseText, out var frames))
            throw new InvalidOperationException($"{recorded.Url} is not a framed answer.");
        var payload = frames.Payloads.Last(p => p.ValueKind == System.Text.Json.JsonValueKind.Array && p.GetArrayLength() > 3
            && (p[2].ValueKind == System.Text.Json.JsonValueKind.Array || p[3].ValueKind == System.Text.Json.JsonValueKind.Array));
        return $"<script class=\"ds:1\" nonce=\"redacted\">AF_initDataCallback({{key: 'ds:1', hash: '9', data:{payload.GetRawText()}, sideChannel: {{}}}});</script>";
    }
}
