using System.Text;

namespace Multiplex.Web.Services.LinkedIn;

/// <summary>
/// Reads LinkedIn's server-driven pages (its "flagship-web" app): the page's HTML carries the
/// screen's data as React flight text inside script strings, where every JSON quote is escaped
/// (<c>\"</c>). The actions the page's buttons send (<c>proto.sdui.actions.core.ServerRequest</c>,
/// <c>…NavigateToScreen</c>) are JSON objects in that text.
/// </summary>
internal static class LinkedInSdui
{
    /// <summary>Removes one level of string escaping, so the embedded JSON reads as JSON (its own escapes intact).</summary>
    public static string Unescape(string html)
    {
        var sb = new StringBuilder(html.Length);
        for (var i = 0; i < html.Length; i++)
        {
            var c = html[i];
            if (c == '\\' && i + 1 < html.Length && html[i + 1] is '"' or '\\')
            {
                sb.Append(html[i + 1]);
                i++;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Every complete JSON object in <paramref name="text"/> that starts with
    /// <paramref name="startMarker"/> (which must begin with <c>{</c>), braces inside strings ignored.
    /// </summary>
    public static IEnumerable<string> Objects(string text, string startMarker, int maxLength = 200_000)
    {
        var from = 0;
        while ((from = text.IndexOf(startMarker, from, StringComparison.Ordinal)) >= 0)
        {
            var end = MatchBrace(text, from, maxLength);
            if (end > 0)
            {
                yield return text[from..(end + 1)];
                from = end + 1;
            }
            else
            {
                from += startMarker.Length;
            }
        }
    }

    /// <summary>The index of the <c>}</c> closing the object that opens at <paramref name="start"/>, or -1.</summary>
    public static int MatchBrace(string text, int start, int maxLength = 200_000)
    {
        var depth = 0;
        var inString = false;
        var limit = Math.Min(text.Length, start + maxLength);
        for (var i = start; i < limit; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }
        return -1;
    }
}
