using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Multiplex.Sites.GoogleFlights.Wire;

/// <summary>
/// Reads the first answer a Google Flights search page carries in its HTML: the data block <c>ds:1</c>,
/// <c>AF_initDataCallback({key: 'ds:1', hash: '..', data:[...], sideChannel: {}});</c>, laid out exactly like a
/// <c>GetShoppingResults</c> payload (protocol.md section 5).
/// </summary>
internal static partial class SearchPageData
{
    /// <summary>The page's <c>ds:1</c> data, or <c>null</c> when the page has no <c>ds:1</c> block at all.</summary>
    /// <exception cref="GoogleFlightsFormatException">The page has a <c>ds:1</c> block whose data is not a JSON array.</exception>
    public static JsonElement? Read(string html)
    {
        var block = Ds1Block().Match(html);
        if (!block.Success)
            return null;

        // The data opens right after "data:": never at the next '[' somewhere later on the page.
        var start = block.Index + block.Length;
        while (start < html.Length && char.IsWhiteSpace(html[start]))
            start++;
        if (start >= html.Length || html[start] != '[')
            throw new GoogleFlightsFormatException("Google Flights' search page holds its flight data (ds:1) in a form other than the array the client knows.");

        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(end >= 0 ? html[start..end] : html[start..]);
        try
        {
            // Reads the one array and stops: what follows it in the script is not the client's business.
            var reader = new Utf8JsonReader(bytes);
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new GoogleFlightsFormatException($"Google Flights' search page holds flight data (ds:1) that is not valid JSON: {e.Message}", e);
        }
    }

    [GeneratedRegex(@"AF_initDataCallback\s*\(\s*\{[^}]*?key\s*:\s*['""]ds:1['""][^}]*?\bdata\s*:")]
    private static partial Regex Ds1Block();
}
