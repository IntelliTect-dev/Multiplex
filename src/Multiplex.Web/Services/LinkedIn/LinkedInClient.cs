using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TripIt.Http;

namespace Multiplex.Web.Services.LinkedIn;

/// <summary>
/// The names LinkedIn's web app gives its messaging queries. LinkedIn changes them when it ships
/// a new web app; configure <c>Multiplex:Sites:LinkedIn:QueryIds</c> to override. The defaults
/// were recorded on 2026-11-19.
/// </summary>
public sealed class LinkedInQueryIds
{
    /// <summary>The conversation list (<c>messengerConversationsBySyncToken</c>).</summary>
    public string Conversations { get; set; } = "messengerConversations.05bbed349f58585b3270fc478e2fc81c";

    /// <summary>A conversation's latest messages (<c>messengerMessagesBySyncToken</c>).</summary>
    public string Messages { get; set; } = "messengerMessages.b88ffdf26effae7a346a4df9ee0e764c";

    /// <summary>The messages before a timestamp (<c>messengerMessagesByAnchorTimestamp</c>).</summary>
    public string OlderMessages { get; set; } = "messengerMessages.1fa287944f845fac3c97c31780d94b1a";
}

/// <summary>
/// Calls LinkedIn's own web API for one signed-in account, with the requests LinkedIn's web app
/// was recorded sending on 2026-11-19: the messaging GraphQL queries and <c>createMessage</c>, and
/// the invitations page with the accept and ignore actions it carries. Only those; none of the read
/// receipts, typing indicators, delivery acknowledgements or tracking calls the pages also send. Every request carries the session's cookie, its CSRF token and the browser
/// headers the page itself sent.
/// </summary>
public sealed partial class LinkedInClient(
    LinkedInSession session,
    HttpClient http,
    LinkedInQueryIds queryIds,
    Func<LinkedInSession, CancellationToken, Task<LinkedInSession?>>? renew = null,
    string? pacingKey = null) : ILinkedInApi
{
    private const string Origin = "https://www.linkedin.com";
    private const string GraphQlPath = "/voyager/api/voyagerMessagingGraphQL/graphql";
    private const int OlderPageSize = 20;

    private const string ReceivedScreen = "com.linkedin.sdui.flagshipnav.mynetwork.invitations.InvitationReceivedWithType";
    private const string ActionMarker =
        """{"$type":"proto.sdui.actions.core.ServerRequest","value":{"requestId":"com.linkedin.sdui.requests.mynetwork.addaInvitationAction""";

    private LinkedInSession _session = session;

    // ── Messaging ───────────────────────────────────────────────────────────

    public async Task<LinkedInConversationPage> ListConversationsAsync(CancellationToken ct)
    {
        var s = _session;
        using var doc = await GraphQlAsync(queryIds.Conversations, $"(mailboxUrn:{Escape(s.MailboxUrn)})", "the conversation list", ct)
            .ConfigureAwait(false);
        var mailbox = _session.MailboxUrn;
        var elements = Elements(doc.RootElement, "messengerConversationsBySyncToken");
        return new LinkedInConversationPage(elements.Select(e => ReadConversation(e, mailbox)).OfType<LinkedInConversation>().ToList());
    }

    public async Task<LinkedInConversation?> FindConversationAsync(string conversationId, CancellationToken ct)
    {
        CheckThread(conversationId);
        var page = await ListConversationsAsync(ct).ConfigureAwait(false);
        return page.Conversations.FirstOrDefault(c => string.Equals(c.ConversationId, conversationId, StringComparison.Ordinal));
    }

    public async Task<LinkedInMessagePage> ReadConversationAsync(string conversationId, string? olderCursor, CancellationToken ct)
    {
        CheckThread(conversationId);
        if (olderCursor is not null && !TimestampPattern().IsMatch(olderCursor))
            throw new LinkedInApiException("older_cursor is not one this server gave out. Read the conversation without it first.");

        var conversationUrn = Escape(ConversationUrn(conversationId));
        var (queryId, variables, key) = olderCursor is null
            ? (queryIds.Messages, $"(conversationUrn:{conversationUrn})", "messengerMessagesBySyncToken")
            : (queryIds.OlderMessages,
                $"(deliveredAt:{olderCursor},conversationUrn:{conversationUrn},countBefore:{OlderPageSize},countAfter:0)",
                "messengerMessagesByAnchorTimestamp");
        using var doc = await GraphQlAsync(queryId, variables, "the conversation's messages", ct).ConfigureAwait(false);

        var mailbox = _session.MailboxUrn;
        var messages = Elements(doc.RootElement, key)
            .Select(e => ReadMessage(e, mailbox))
            .OfType<LinkedInMessage>()
            .OrderBy(m => m.SentAt)
            .ToList();
        var more = olderCursor is null ? messages.Count > 0 : messages.Count >= OlderPageSize;
        var cursor = more ? messages[0].SentAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) : null;
        return new LinkedInMessagePage(messages, cursor);
    }

    public async Task ReplyAsync(string conversationId, string text, CancellationToken ct)
    {
        CheckThread(conversationId);
        using var response = await SendAsync(s =>
        {
            var request = Request(HttpMethod.Post, Origin + "/voyager/api/voyagerMessagingDashMessengerMessages?action=createMessage", s,
                "application/json");
            request.Content = new StringContent(CreateMessageBody(s, conversationId, text), Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain;charset=UTF-8");
            return request;
        }, isRead: false, "the message", ct).ConfigureAwait(false);
    }

    // ── Invitations (LinkedIn's server-driven invitations page) ─────────────

    public async Task<LinkedInInvitationPage> ListInvitationsAsync(InvitationDirection direction, CancellationToken ct)
    {
        if (direction == InvitationDirection.Sent)
            throw new LinkedInApiException("Listing sent invitations is not connected to LinkedIn yet on this server. Nothing was read.");
        var page = await ReadInvitationsPageAsync(ct).ConfigureAwait(false);
        return new LinkedInInvitationPage(page.Select(ToInvitation).ToList());
    }

    public async Task<LinkedInInvitation?> FindReceivedInvitationAsync(string invitationId, CancellationToken ct)
    {
        CheckInvitationId(invitationId);
        var page = await ReadInvitationsPageAsync(ct).ConfigureAwait(false);
        return page.FirstOrDefault(i => i.InvitationId == invitationId) is { } found ? ToInvitation(found) : null;
    }

    public async Task AcceptInvitationAsync(string invitationId, CancellationToken ct)
    {
        var invitation = await WaitingInvitationAsync(invitationId, ct).ConfigureAwait(false);
        if (invitation.AcceptAction is { } accept)
        {
            await PostActionAsync(accept, ReceivedScreen, "the acceptance", ct).ConfigureAwait(false);
            return;
        }
        // LinkedIn warns before this one: open its warning as the page would, and send what its Accept invite button sends.
        var fromWarning = await WarningAcceptActionAsync(invitation, ct).ConfigureAwait(false);
        await PostActionAsync(fromWarning, LinkedInInvitationsPage.WarningScreen, "the acceptance", ct).ConfigureAwait(false);
    }

    public async Task IgnoreInvitationAsync(string invitationId, CancellationToken ct)
    {
        var invitation = await WaitingInvitationAsync(invitationId, ct).ConfigureAwait(false);
        await PostActionAsync(invitation.IgnoreAction, ReceivedScreen, "the ignore", ct).ConfigureAwait(false);
    }

    private async Task<PageInvitation> WaitingInvitationAsync(string invitationId, CancellationToken ct)
    {
        CheckInvitationId(invitationId);
        var page = await ReadInvitationsPageAsync(ct).ConfigureAwait(false);
        return page.FirstOrDefault(i => i.InvitationId == invitationId)
               ?? throw new LinkedInApiException("That invitation is no longer waiting on LinkedIn's invitations page. Nothing was changed.");
    }

    private async Task<IReadOnlyList<PageInvitation>> ReadInvitationsPageAsync(CancellationToken ct)
    {
        using var response = await SendAsync(
            s => Request(HttpMethod.Get, Origin + "/mynetwork/invitation-manager/received/", s, "text/html,application/xhtml+xml"),
            isRead: true, "the invitations page", ct).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return LinkedInInvitationsPage.Parse(html);
    }

    /// <summary>Opens LinkedIn's accept warning for <paramref name="invitation"/> and returns its Accept invite action.</summary>
    private async Task<string> WarningAcceptActionAsync(PageInvitation invitation, CancellationToken ct)
    {
        var navigation = JsonNode.Parse(invitation.WarningNavigation!)!.AsObject();
        var screen = navigation["screenId"]?.GetValue<string>() ?? LinkedInInvitationsPage.WarningScreen;
        var arguments = navigation["requestedArguments"]!.DeepClone().AsObject();
        arguments["states"] = new JsonArray();
        arguments["screenId"] = screen;
        arguments["knownTemplateIds"] = new JsonArray();
        var body = new JsonObject { ["clientArguments"] = arguments, ["isModal"] = true }.ToJsonString();
        var url = $"{Origin}/flagship-web/rsc-action/actions/navigation?screenId={Uri.EscapeDataString(screen)}&sduiid={Uri.EscapeDataString(screen)}";

        using var response = await SendAsync(s =>
        {
            var request = Request(HttpMethod.Post, url, s, "*/*", rsc: true);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }, isRead: true, "LinkedIn's warning", ct).ConfigureAwait(false);
        var flight = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        foreach (var obj in LinkedInSdui.Objects(flight, ActionMarker))
        {
            try
            {
                using var doc = JsonDocument.Parse(obj);
                var value = doc.RootElement.GetProperty("value");
                var payload = value.GetProperty("requestedArguments").GetProperty("payload");
                if (payload.TryGetProperty("inviteeActionType", out var type) && type.GetString() == "InviteeActionType_ACCEPT"
                    && payload.TryGetProperty("invitationUrn", out var urn) && urn.TryGetProperty("invitationId", out var id)
                    && id.GetString() == invitation.InvitationId)
                {
                    return value.GetRawText();
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }
        }
        throw new LinkedInApiException(
            "LinkedIn's warning for this invitation did not offer an Accept invite button this server recognises. Nothing was " +
            "changed; accept it on LinkedIn yourself if you want to.");
    }

    /// <summary>Sends one of the page's addaInvitationAction actions, in the envelope LinkedIn's page sends it in.</summary>
    private async Task PostActionAsync(string actionValueJson, string screenId, string what, CancellationToken ct)
    {
        var body = ActionEnvelope(actionValueJson, screenId);
        using var response = await SendAsync(s =>
        {
            var request = Request(HttpMethod.Post, Origin + "/flagship-web/rsc-action/actions/server-request?sduiid=" +
                                                   LinkedInInvitationsPage.ActionRequestId, s, "*/*", rsc: true);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }, isRead: false, what, ct).ConfigureAwait(false);
        var answer = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        switch (ServerRequestSucceeded(answer))
        {
            case true:
                return;
            case false:
                throw new LinkedInApiException($"LinkedIn refused {what}. Nothing more was tried.");
            default:
                throw new LinkedInApiException(OutcomeUnknown(what, "LinkedIn's answer was not one this server recognises"));
        }
    }

    /// <summary>
    /// Reads the <c>ServerRequestResponse</c> in a flight answer: true when its <c>errors</c> array is
    /// empty, false when it lists errors, null when there is no such response to read.
    /// </summary>
    internal static bool? ServerRequestSucceeded(string flight)
    {
        bool? result = null;
        foreach (var obj in LinkedInSdui.Objects(flight, """{"$type":"proto.sdui.responses.ServerRequestResponse"""))
        {
            try
            {
                using var doc = JsonDocument.Parse(obj);
                if (!doc.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return null;
                if (errors.GetArrayLength() > 0) return false;
                result = true;
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return result;
    }

    /// <summary>
    /// The body LinkedIn's page posts for a ServerRequest action (recorded 2026-11-19): the action's own
    /// <c>requestedArguments</c>, unchanged, once inside <c>serverRequest</c> and once at the top with the screen.
    /// </summary>
    internal static string ActionEnvelope(string actionValueJson, string screenId)
    {
        var value = JsonNode.Parse(actionValueJson)!.AsObject();
        var requestId = value["requestId"]!.GetValue<string>();
        var arguments = value["requestedArguments"]!;
        var top = arguments.DeepClone().AsObject();
        top["states"] = new JsonArray();
        top["screenId"] = screenId;
        top["knownTemplateIds"] = new JsonArray();
        var server = new JsonObject
        {
            ["requestId"] = requestId,
            ["requestedArguments"] = arguments.DeepClone(),
            ["onClientRequestFailureAction"] = value["onClientRequestFailureAction"]?.DeepClone() ?? new JsonObject { ["actions"] = new JsonArray() },
            ["isApfcEnabled"] = false,
            ["isStreaming"] = false,
            ["rumPageKey"] = "",
        };
        return new JsonObject
        {
            ["requestId"] = requestId,
            ["serverRequest"] = server,
            ["states"] = new JsonArray(),
            ["requestedArguments"] = top,
        }.ToJsonString();
    }

    private static LinkedInInvitation ToInvitation(PageInvitation p) => new(p.InvitationId, p.Person, p.Note, null, p.WarningTitle);

    private static void CheckInvitationId(string? invitationId)
    {
        if (invitationId is null || !InvitationIdPattern().IsMatch(invitationId))
            throw new LinkedInApiException("That is not an invitation_id from linkedin_list_invitations. Nothing was read or changed.");
    }

    // ── Requests ────────────────────────────────────────────────────────────

    /// <summary>The recorded createMessage body, field for field.</summary>
    internal static string CreateMessageBody(LinkedInSession s, string conversationId, string text)
    {
        // 16 random bytes, each written as one character, as LinkedIn's web app does.
        var tracking = new string(RandomNumberGenerator.GetBytes(16).Select(b => (char)b).ToArray());
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartObject("message");
            w.WriteStartObject("body");
            w.WriteStartArray("attributes");
            w.WriteEndArray();
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteStartArray("renderContentUnions");
            w.WriteEndArray();
            w.WriteString("conversationUrn", $"urn:li:msg_conversation:({s.MailboxUrn},{conversationId})");
            w.WriteString("originToken", Guid.NewGuid().ToString());
            w.WriteEndObject();
            w.WriteString("mailboxUrn", s.MailboxUrn);
            w.WriteString("trackingId", tracking);
            w.WriteBoolean("dedupeByClientGeneratedToken", false);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<JsonDocument> GraphQlAsync(string queryId, string variables, string what, CancellationToken ct)
    {
        if (!QueryIdPattern().IsMatch(queryId))
            throw new LinkedInApiException($"The configured LinkedIn query name for {what} is not valid. Check Multiplex:Sites:LinkedIn:QueryIds.");
        using var response = await SendAsync(
            s => Request(HttpMethod.Get, $"{Origin}{GraphQlPath}?queryId={queryId}&variables={variables}", s, "application/graphql"),
            isRead: true, what, ct, staleQuery: queryId).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new LinkedInApiException($"LinkedIn answered the request for {what} with something other than JSON.");
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string url, LinkedInSession s, string accept, bool rsc = false)
    {
        var request = new HttpRequestMessage(method, new Uri(url, UriKind.Absolute));
        foreach (var (name, value) in s.Headers)
        {
            if (!LinkedInSession.IsSecretHeader(name) && !IsContentHeader(name)) request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Headers.Remove("Accept");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.Remove("x-restli-protocol-version");
        request.Headers.TryAddWithoutValidation("x-restli-protocol-version", "2.0.0");
        request.Headers.TryAddWithoutValidation("Cookie", s.Cookie);
        request.Headers.TryAddWithoutValidation("csrf-token", s.CsrfToken);
        if (!request.Headers.Contains("Origin") && method != HttpMethod.Get) request.Headers.TryAddWithoutValidation("Origin", Origin);
        if (rsc) request.Headers.TryAddWithoutValidation("x-li-rsc-stream", "true");
        if (pacingKey is not null) request.Options.Set(RequestPacer.KeyOption, pacingKey);
        return request;
    }

    /// <summary>
    /// Sends one request built from the current session. A read whose session LinkedIn rejects is
    /// asked again once with a renewed session; a change is never sent twice, because LinkedIn may
    /// have acted on it already.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<LinkedInSession, HttpRequestMessage> build, bool isRead, string what,
        CancellationToken ct, string? staleQuery = null)
    {
        var s = _session;
        HttpResponseMessage response;
        try
        {
            response = await SendOnceAsync(build(s), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!isRead && (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested)))
        {
            // The request may have reached LinkedIn before the connection failed or timed out.
            throw new LinkedInApiException(OutcomeUnknown(what, "the connection failed before LinkedIn answered"), ex);
        }
        if (IsRejected(response))
        {
            response.Dispose();
            if (!isRead)
            {
                throw new LinkedInApiException(
                    $"LinkedIn did not accept this account's session, so {what} may not have been sent. Please check LinkedIn " +
                    "before trying again; if it keeps happening, reconnect the LinkedIn connector.");
            }
            var renewed = renew is null ? null : await renew(s, ct).ConfigureAwait(false);
            if (renewed is null || !renewed.IsConfigured || renewed.Cookie == s.Cookie) throw NotAccepted();
            _session = renewed;
            response = await SendOnceAsync(build(renewed), ct).ConfigureAwait(false);
            if (IsRejected(response))
            {
                response.Dispose();
                throw NotAccepted();
            }
        }
        if (response.IsSuccessStatusCode) return response;

        var status = (int)response.StatusCode;
        response.Dispose();
        if (!isRead) throw new LinkedInApiException(OutcomeUnknown(what, $"LinkedIn answered HTTP {status}"));
        if (status == 429)
            throw new LinkedInApiException("LinkedIn is limiting requests from this account right now. Nothing more was tried; try again later.");
        if (staleQuery is not null && status is 400 or 404)
        {
            throw new LinkedInApiException(
                $"LinkedIn changed its request for {what} (HTTP {status}), so this server's copy is out of date. Record LinkedIn's " +
                "messaging pages again and update Multiplex:Sites:LinkedIn:QueryIds.");
        }
        throw new LinkedInApiException($"LinkedIn answered HTTP {status} to the request for {what}.");
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if ((int)response.StatusCode == 999)
            {
                response.Dispose();
                throw new LinkedInChallengeException(
                    "LinkedIn refused the request with status 999, which it uses when it suspects automated access. Nothing more " +
                    "was tried. Open LinkedIn yourself to check the account, and leave the connector alone for a while.");
            }
            if (IsRedirect(response) && response.Headers.Location is { } location && PathOf(location).StartsWith("/checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                response.Dispose();
                throw new LinkedInChallengeException(
                    "LinkedIn asked for a security check. Open LinkedIn yourself to answer it, then try again; this server never answers one.");
            }
            return response;
        }
    }

    /// <summary>For a change whose result LinkedIn did not confirm: it may or may not have happened, and must not simply be repeated.</summary>
    private static string OutcomeUnknown(string what, string why) =>
        $"{char.ToUpperInvariant(why[0])}{why[1..]}, so {what} may or may not have taken effect. Please check LinkedIn before " +
        "trying again, so it does not happen twice.";

    private static LinkedInApiException NotAccepted() => new(
        "LinkedIn did not accept this account's session, and it could not be renewed right now. Try again in a minute; if it keeps " +
        "failing, reconnect the LinkedIn connector.");

    private static bool IsRejected(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return true;
        if (!IsRedirect(response) || response.Headers.Location is not { } location) return false;
        var path = PathOf(location);
        return path.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/uas/login", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/authwall", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirect(HttpResponseMessage response) => (int)response.StatusCode is >= 300 and < 400;

    private static string PathOf(Uri location) => location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];

    private static bool IsContentHeader(string name) =>
        name.StartsWith("content-", StringComparison.OrdinalIgnoreCase);

    // ── Reading LinkedIn's answers ──────────────────────────────────────────

    private static IEnumerable<JsonElement> Elements(JsonElement root, string key)
    {
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(key, out var collection) && collection.ValueKind == JsonValueKind.Object
            && collection.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
        {
            return elements.EnumerateArray().ToList();
        }
        throw new LinkedInApiException("LinkedIn's answer did not have the shape this server expects; LinkedIn may have changed it.");
    }

    private static LinkedInConversation? ReadConversation(JsonElement e, string mailbox)
    {
        var id = ThreadOf(Str(e, "entityUrn")) ?? ThreadOf(Str(e, "backendUrn"));
        if (id is null) return null;
        var participants = Arr(e, "conversationParticipants")
            .Where(p => !string.Equals(Str(p, "hostIdentityUrn"), mailbox, StringComparison.Ordinal))
            .Select(ReadPerson)
            .ToList();
        var last = Obj(e, "messages") is { } messages
            ? Arr(messages, "elements").OrderByDescending(m => Long(m, "deliveredAt") ?? 0).FirstOrDefault()
            : default;
        var lastText = last.ValueKind == JsonValueKind.Object && Obj(last, "body") is { } body ? Str(body, "text") : null;
        var unread = (Long(e, "unreadCount") ?? 0) > 0 || (e.TryGetProperty("read", out var read) && read.ValueKind == JsonValueKind.False);
        DateTimeOffset? at = Long(e, "lastActivityAt") is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        return new LinkedInConversation(id, participants, lastText, at, unread);
    }

    private static LinkedInMessage? ReadMessage(JsonElement e, string mailbox)
    {
        if (Long(e, "deliveredAt") is not { } ms) return null;
        var sender = Obj(e, "sender");
        var from = sender is { } s && !string.Equals(Str(s, "hostIdentityUrn"), mailbox, StringComparison.Ordinal) ? ReadPerson(s) : null;
        var text = Obj(e, "body") is { } body ? Str(body, "text") ?? "" : "";
        return new LinkedInMessage(Str(e, "entityUrn") ?? "", from, text, DateTimeOffset.FromUnixTimeMilliseconds(ms));
    }

    private static LinkedInPerson ReadPerson(JsonElement participant)
    {
        var urn = Str(participant, "hostIdentityUrn") ?? "";
        var id = urn.StartsWith("urn:li:fsd_profile:", StringComparison.Ordinal) ? urn["urn:li:fsd_profile:".Length..] : urn;
        var member = Obj(participant, "participantType") is { } type ? Obj(type, "member") : null;
        if (member is not { } m) return new LinkedInPerson(id, "(not a member)", null);
        var name = $"{Text(m, "firstName")} {Text(m, "lastName")}".Trim();
        return new LinkedInPerson(id, name.Length > 0 ? name : "(no name)", Text(m, "headline"));
    }

    /// <summary>The thread part of <c>urn:li:msg_conversation:(urn:li:fsd_profile:X,THREAD)</c> or <c>urn:li:messagingThread:THREAD</c>.</summary>
    private static string? ThreadOf(string? urn)
    {
        if (urn is null) return null;
        if (urn.StartsWith("urn:li:messagingThread:", StringComparison.Ordinal)) return urn["urn:li:messagingThread:".Length..];
        var comma = urn.LastIndexOf(',');
        return comma > 0 && urn.EndsWith(')') ? urn[(comma + 1)..^1] : null;
    }

    private string ConversationUrn(string thread) => $"urn:li:msg_conversation:({_session.MailboxUrn},{thread})";

    private static void CheckThread(string? conversationId)
    {
        if (conversationId is null || !ThreadPattern().IsMatch(conversationId))
        {
            throw new LinkedInApiException(
                "That is not a conversation_id from linkedin_list_conversations. Nothing was read or sent.");
        }
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    /// <summary>An AttributedText's <c>text</c>, e.g. <c>firstName.text</c>.</summary>
    private static string? Text(JsonElement e, string name) => Obj(e, name) is { } t ? Str(t, "text") : null;

    [GeneratedRegex(@"^2-[A-Za-z0-9+/=_-]{4,256}\z")]
    private static partial Regex ThreadPattern();

    [GeneratedRegex(@"^[0-9]{10,16}\z")]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"^[0-9]{6,25}\z")]
    private static partial Regex InvitationIdPattern();

    [GeneratedRegex(@"^messenger[A-Za-z]+\.[0-9a-f]{6,64}\z")]
    private static partial Regex QueryIdPattern();
}
