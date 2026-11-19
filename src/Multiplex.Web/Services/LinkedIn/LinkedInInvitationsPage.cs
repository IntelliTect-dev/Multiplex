using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Multiplex.Web.Services.LinkedIn;

/// <summary>One received invitation as LinkedIn's invitations page shows it, with the actions its buttons send.</summary>
/// <param name="IgnoreAction">The Ignore button's <c>ServerRequest</c> (its <c>value</c> object, as JSON).</param>
/// <param name="AcceptAction">The Accept button's <c>ServerRequest</c>, or null when Accept opens a warning first.</param>
/// <param name="WarningTitle">The title of LinkedIn's warning when Accept opens one, e.g. "Take care when connecting".</param>
/// <param name="WarningNavigation">The <c>NavigateToScreen</c> action that opens that warning, as JSON.</param>
internal sealed record PageInvitation(
    string InvitationId,
    LinkedInPerson Person,
    string? Note,
    string IgnoreAction,
    string? AcceptAction,
    string? WarningTitle,
    string? WarningNavigation);

/// <summary>
/// Reads the received-invitations page (<c>/mynetwork/invitation-manager/received/</c>), recorded on
/// 2026-11-19. The server-rendered HTML wraps each invitation in an element keyed
/// <c>invitation_manager_row_urn:li:invitation:{id}</c> holding the sender's name, headline and note;
/// the page's flight data carries, keyed by the same invitation id, the Ignore and Accept actions
/// (<c>addaInvitationAction</c>) with the invitation's validation token and the sender's profile id.
/// People are tied to invitations only through that id, never by position: a row without its own
/// Ignore action means the page is not what this reader expects, and nothing is returned.
/// </summary>
internal static partial class LinkedInInvitationsPage
{
    internal const string ActionRequestId = "com.linkedin.sdui.requests.mynetwork.addaInvitationAction";
    internal const string WarningScreen = "com.linkedin.sdui.flagshipnav.mynetwork.invitations.AcceptConfirmationDialog";

    public static IReadOnlyList<PageInvitation> Parse(string html)
    {
        var rows = RowPattern().Matches(html);
        var flight = LinkedInSdui.Unescape(html);
        var actions = Actions(flight);
        var warnings = Warnings(flight);
        if (rows.Count == 0)
        {
            if (actions.Count > 0) throw Unexpected();
            return [];
        }

        var invitations = new List<PageInvitation>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var id = rows[i].Groups[1].Value;
            var start = rows[i].Index;
            var end = i + 1 < rows.Count ? rows[i + 1].Index : Math.Min(html.Length, start + 40_000);
            var row = html[start..end];

            if (!actions.TryGetValue((id, "IGNORE"), out var ignore)) throw Unexpected();
            var name = NamePattern().Match(row) is { Success: true } n ? Text(n.Groups[1].Value) : null;
            if (string.IsNullOrWhiteSpace(name)) throw Unexpected();

            var hasAccept = actions.TryGetValue((id, "ACCEPT"), out var accept);
            var hasWarning = warnings.TryGetValue(id, out var warning);
            if (!hasAccept && !hasWarning) throw Unexpected();
            if (hasAccept && accept.ProfileId != ignore.ProfileId) throw Unexpected();

            invitations.Add(new PageInvitation(
                id,
                new LinkedInPerson(ignore.ProfileId, name, Headline(row)),
                Note(row),
                ignore.Json,
                hasAccept ? accept.Json : null,
                hasAccept ? null : warning.Title,
                hasAccept ? null : warning.Json));
        }
        return invitations;
    }

    /// <summary>The addaInvitationAction ServerRequests, by invitation id and action (ACCEPT / IGNORE).</summary>
    private static Dictionary<(string Id, string Type), (string Json, string ProfileId)> Actions(string flight)
    {
        var found = new Dictionary<(string, string), (string, string)>();
        foreach (var obj in LinkedInSdui.Objects(flight, """{"$type":"proto.sdui.actions.core.ServerRequest","value":{"requestId":"com.linkedin.sdui.requests.mynetwork.addaInvitationAction"""))
        {
            try
            {
                using var doc = JsonDocument.Parse(obj);
                var value = doc.RootElement.GetProperty("value");
                var payload = value.GetProperty("requestedArguments").GetProperty("payload");
                var type = payload.GetProperty("inviteeActionType").GetString() switch
                {
                    "InviteeActionType_ACCEPT" => "ACCEPT",
                    "InviteeActionType_IGNORE" => "IGNORE",
                    _ => null,
                };
                var id = payload.GetProperty("invitationUrn").GetProperty("invitationId").GetString();
                var profileId = payload.GetProperty("profileUrn").GetProperty("profileId").GetString();
                if (type is null || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(profileId)) continue;
                found.TryAdd((id, type), (value.GetRawText(), profileId));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Not an action this reader understands; a row that needs it fails the shape check.
            }
        }
        return found;
    }

    /// <summary>The NavigateToScreen actions that open LinkedIn's accept warning, by invitation id.</summary>
    private static Dictionary<string, (string Title, string Json)> Warnings(string flight)
    {
        var found = new Dictionary<string, (string, string)>();
        foreach (var obj in LinkedInSdui.Objects(flight, """{"$type":"proto.sdui.actions.core.NavigateToScreen","screenId":"com.linkedin.sdui.flagshipnav.mynetwork.invitations.AcceptConfirmationDialog"""))
        {
            try
            {
                using var doc = JsonDocument.Parse(obj);
                var root = doc.RootElement;
                var id = root.GetProperty("requestedArguments").GetProperty("payload").GetProperty("invitationUrn")
                    .GetProperty("invitationId").GetString();
                var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                found.TryAdd(id, (string.IsNullOrWhiteSpace(title) ? "LinkedIn shows a warning before accepting" : title, obj));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }
        }
        return found;
    }

    private static string? Headline(string row)
    {
        var after = row.IndexOf("wants to connect", StringComparison.Ordinal);
        if (after < 0) return null;
        foreach (Match m in ParagraphSpanPattern().Matches(row, after))
        {
            var text = Text(m.Groups[1].Value);
            if (text.Length > 0 && !text.Contains("mutual connection", StringComparison.OrdinalIgnoreCase)) return text;
        }
        return null;
    }

    private static string? Note(string row)
    {
        var m = NotePattern().Match(row);
        if (!m.Success) return null;
        var text = Text(BreakPattern().Replace(m.Groups[1].Value, "\n"));
        return text.Length > 0 ? text : null;
    }

    /// <summary>Tags removed, entities decoded, whitespace trimmed (line breaks kept).</summary>
    private static string Text(string html) => WebUtility.HtmlDecode(TagPattern().Replace(html, "")).Trim();

    private static LinkedInApiException Unexpected() => new(
        "LinkedIn's invitations page did not have the shape this server expects, so nothing was read or changed. LinkedIn " +
        "may have changed the page; record it again.");

    [GeneratedRegex("""componentkey="invitation_manager_row_urn:li:invitation:(\d+)"\s*>""")]
    private static partial Regex RowPattern();

    [GeneratedRegex("""<a[^>]*aria-label="View [^"]*"[^>]*>\s*<strong[^>]*>([^<]+)""")]
    private static partial Regex NamePattern();

    [GeneratedRegex("""<p[^>]*>\s*<span[^>]*>([^<]+)</span>\s*</p>""")]
    private static partial Regex ParagraphSpanPattern();

    [GeneratedRegex("""data-testid="expandable-text-box"[^>]*>(.*?)</span>""", RegexOptions.Singleline)]
    private static partial Regex NotePattern();

    [GeneratedRegex("""<br\s*/?>""")]
    private static partial Regex BreakPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();
}
