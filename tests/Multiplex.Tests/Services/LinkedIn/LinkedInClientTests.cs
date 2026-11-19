using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Multiplex.Web.Services.LinkedIn;

namespace Multiplex.Tests.Services.LinkedIn;

/// <summary>
/// The LinkedIn client against a fake www.linkedin.com that answers in the shapes recorded on
/// 2026-11-19 (hand-written fixtures: no recorded names, ids or text). It checks the exact
/// requests the client sends, how it reads LinkedIn's answers, and what it does when LinkedIn
/// refuses.
/// </summary>
public sealed class LinkedInClientTests
{
    private const string Me = "ACoAAMember000000000000000000000000000001";
    private const string JaneId = "ACoAAJane0000000000000000000000000000002";
    private const string SamId = "ACoAASam00000000000000000000000000000003";
    private const string Thread = "2-VGhyZWFkT25lAA==";
    private const string GroupThread = "2-R3JvdXBUaHJlYWQ=";

    private static readonly LinkedInQueryIds s_queryIds = new()
    {
        Conversations = "messengerConversations.c0c0c0",
        Messages = "messengerMessages.a0a0a0",
        OlderMessages = "messengerMessages.b0b0b0",
    };

    private readonly FakeLinkedInHttp _http = new();

    private static LinkedInSession Session(string cookie = "li_at=a; JSESSIONID=\"ajax:1\"") => new()
    {
        Cookie = cookie,
        CsrfToken = "ajax:1",
        ProfileId = Me,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["user-agent"] = "Mozilla/5.0 Test",
            ["x-li-lang"] = "en_US",
        },
    };

    private LinkedInClient Client(Func<LinkedInSession, CancellationToken, Task<LinkedInSession?>>? renew = null) =>
        new(Session(), new HttpClient(_http), s_queryIds, renew);

    // ── Conversations ───────────────────────────────────────────────────────

    [Fact]
    public async Task Listing_conversations_sends_the_recorded_query_for_the_members_own_mailbox()
    {
        _http.Respond(r => r.RequestUri!.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal), _ => Json(Conversations()));

        await Client().ListConversationsAsync(CancellationToken.None);

        var request = Assert.Single(_http.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://www.linkedin.com/voyager/api/voyagerMessagingGraphQL/graphql", request.Url.Split('?')[0]);
        Assert.Equal($"?queryId=messengerConversations.c0c0c0&variables=(mailboxUrn:urn%3Ali%3Afsd_profile%3A{Me})", request.Query);
        Assert.Equal("li_at=a; JSESSIONID=\"ajax:1\"", request.Headers["cookie"]);
        Assert.Equal("ajax:1", request.Headers["csrf-token"]);
        Assert.Equal("application/graphql", request.Headers["accept"]);
        Assert.Equal("2.0.0", request.Headers["x-restli-protocol-version"]);
        Assert.Equal("Mozilla/5.0 Test", request.Headers["user-agent"]);
    }

    [Fact]
    public async Task A_conversation_lists_everyone_but_the_member_with_its_last_message()
    {
        _http.Respond(_ => true, _ => Json(Conversations()));

        var page = await Client().ListConversationsAsync(CancellationToken.None);

        Assert.Equal([Thread, GroupThread], page.Conversations.Select(c => c.ConversationId));
        var single = page.Conversations[0];
        var jane = Assert.Single(single.Participants);
        Assert.Equal(("Jane Doe", "Engineer at Contoso", JaneId), (jane.Name, jane.Headline, jane.ProfileId));
        Assert.Equal("Lunch on Friday?", single.LastMessage);
        Assert.True(single.Unread);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794943053997), single.LastActivityAt);
        var group = page.Conversations[1];
        Assert.Equal(["Jane Doe", "Sam Roe"], group.Participants.Select(p => p.Name));
        Assert.False(group.Unread);
    }

    [Fact]
    public async Task Finding_a_conversation_looks_among_the_recent_ones()
    {
        _http.Respond(_ => true, _ => Json(Conversations()));
        var client = Client();

        Assert.Equal(GroupThread, (await client.FindConversationAsync(GroupThread, CancellationToken.None))?.ConversationId);
        Assert.Null(await client.FindConversationAsync("2-Tm90SGVyZQ==", CancellationToken.None));
    }

    // ── Messages ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reading_a_conversation_asks_for_its_latest_messages_and_returns_them_oldest_first()
    {
        _http.Respond(_ => true, _ => Json(Messages("messengerMessagesBySyncToken",
            (JaneId, "Jane", "Doe", "Lunch on Friday?", 1794943053997), (Me, "Mark", "Member", "Sure", 1794943000000))));

        var page = await Client().ReadConversationAsync(Thread, null, CancellationToken.None);

        var request = Assert.Single(_http.Requests);
        Assert.Equal("?queryId=messengerMessages.a0a0a0&variables=(conversationUrn:urn%3Ali%3Amsg_conversation%3A%28urn%3Ali%3Afsd_profile%3A" +
                     Me + "%2C2-VGhyZWFkT25lAA%3D%3D%29)", request.Query);
        Assert.Equal(["Sure", "Lunch on Friday?"], page.Messages.Select(m => m.Text));
        Assert.Null(page.Messages[0].From);
        Assert.Equal("Jane Doe", page.Messages[1].From?.Name);
        Assert.Equal("1794943000000", page.OlderCursor);
    }

    [Fact]
    public async Task Reading_further_back_asks_for_the_twenty_before_the_oldest_seen()
    {
        _http.Respond(_ => true, _ => Json(Messages("messengerMessagesByAnchorTimestamp")));

        var page = await Client().ReadConversationAsync(Thread, "1794943000000", CancellationToken.None);

        Assert.Equal("?queryId=messengerMessages.b0b0b0&variables=(deliveredAt:1794943000000,conversationUrn:urn%3Ali%3Amsg_conversation%3A%28urn%3Ali%3Afsd_profile%3A" +
                     Me + "%2C2-VGhyZWFkT25lAA%3D%3D%29,countBefore:20,countAfter:0)", Assert.Single(_http.Requests).Query);
        Assert.Empty(page.Messages);
        Assert.Null(page.OlderCursor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("urn:li:msg_conversation:(urn:li:fsd_profile:x,2-abc)")]
    [InlineData("2-abc),countBefore:999")]
    [InlineData("2-ab cd")]
    [InlineData("3-VGhyZWFk")]
    public async Task Anything_but_a_thread_id_is_refused_before_LinkedIn_is_asked(string conversationId)
    {
        await Assert.ThrowsAsync<LinkedInApiException>(() => Client().ReadConversationAsync(conversationId, null, CancellationToken.None));
        await Assert.ThrowsAsync<LinkedInApiException>(() => Client().ReplyAsync(conversationId, "Hi", CancellationToken.None));
        Assert.Empty(_http.Requests);
    }

    [Theory]
    [InlineData("12)")]
    [InlineData("abc")]
    public async Task An_older_cursor_that_is_not_a_timestamp_is_refused(string cursor)
    {
        await Assert.ThrowsAsync<LinkedInApiException>(() => Client().ReadConversationAsync(Thread, cursor, CancellationToken.None));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_reply_posts_the_recorded_create_message_body_with_the_text_exactly()
    {
        const string text = "Thanks, but I'm not interested.\nBest";
        _http.Respond(_ => true, _ => Json(JsonSerializer.Serialize(new { value = new { body = Text(text), conversationUrn = "x", deliveredAt = 1 } })));

        await Client().ReplyAsync(Thread, text, CancellationToken.None);

        var request = Assert.Single(_http.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://www.linkedin.com/voyager/api/voyagerMessagingDashMessengerMessages?action=createMessage", request.Url);
        Assert.Equal("text/plain; charset=UTF-8", request.Headers["content-type"]);
        Assert.Equal("application/json", request.Headers["accept"]);
        var body = JsonDocument.Parse(request.Body!).RootElement;
        var message = body.GetProperty("message");
        Assert.Equal(text, message.GetProperty("body").GetProperty("text").GetString());
        Assert.Empty(message.GetProperty("body").GetProperty("attributes").EnumerateArray());
        Assert.Empty(message.GetProperty("renderContentUnions").EnumerateArray());
        Assert.Equal($"urn:li:msg_conversation:(urn:li:fsd_profile:{Me},{Thread})", message.GetProperty("conversationUrn").GetString());
        Assert.True(Guid.TryParse(message.GetProperty("originToken").GetString(), out _));
        Assert.Equal($"urn:li:fsd_profile:{Me}", body.GetProperty("mailboxUrn").GetString());
        Assert.Equal(16, body.GetProperty("trackingId").GetString()!.Length);
        Assert.False(body.GetProperty("dedupeByClientGeneratedToken").GetBoolean());
    }

    // ── When LinkedIn refuses ───────────────────────────────────────────────

    [Fact]
    public async Task LinkedIns_automation_block_is_a_security_stop_not_a_retry()
    {
        _http.Respond(_ => true, _ => new HttpResponseMessage((HttpStatusCode)999));

        var error = await Assert.ThrowsAsync<LinkedInChallengeException>(() => Client().ListConversationsAsync(CancellationToken.None));

        Assert.Single(_http.Requests);
        Assert.Contains("999", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_to_a_security_check_is_a_security_stop()
    {
        _http.Respond(_ => true, _ => Redirect("https://www.linkedin.com/checkpoint/challenge/AgE?ut=x"));

        await Assert.ThrowsAsync<LinkedInChallengeException>(() => Client().ListConversationsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_rejected_session_is_renewed_once_and_the_read_is_asked_again_with_the_new_cookie()
    {
        _http.Respond(r => r.Headers.GetValues("Cookie").Single() == "old", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        _http.Respond(r => r.Headers.GetValues("Cookie").Single() == "new", _ => Json(Conversations()));
        var renewals = 0;
        var client = new LinkedInClient(Session("old"), new HttpClient(_http), s_queryIds, (rejected, _) =>
        {
            renewals++;
            Assert.Equal("old", rejected.Cookie);
            return Task.FromResult<LinkedInSession?>(Session("new"));
        });

        var page = await client.ListConversationsAsync(CancellationToken.None);

        Assert.Equal(1, renewals);
        Assert.Equal(["old", "new"], _http.Requests.Select(r => r.Headers["cookie"]));
        Assert.Equal(2, page.Conversations.Count);
    }

    [Fact]
    public async Task A_session_that_cannot_be_renewed_is_reported_as_not_accepted()
    {
        _http.Respond(_ => true, _ => Redirect("https://www.linkedin.com/login?session_redirect=x"));

        var error = await Assert.ThrowsAsync<LinkedInApiException>(() =>
            Client((_, _) => Task.FromResult<LinkedInSession?>(null)).ListConversationsAsync(CancellationToken.None));

        Assert.Contains("did not accept", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_LinkedIn_rejects_is_never_sent_twice()
    {
        _http.Respond(_ => true, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var renewals = 0;

        var error = await Assert.ThrowsAsync<LinkedInApiException>(() =>
            Client((_, _) => { renewals++; return Task.FromResult<LinkedInSession?>(Session("new")); })
                .ReplyAsync(Thread, "Hi", CancellationToken.None));

        Assert.Single(_http.Requests);
        Assert.Equal(0, renewals);
        Assert.Contains("check LinkedIn", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_query_LinkedIn_no_longer_knows_says_to_record_it_again()
    {
        _http.Respond(_ => true, _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") });

        var error = await Assert.ThrowsAsync<LinkedInApiException>(() => Client().ListConversationsAsync(CancellationToken.None));

        Assert.Contains("LinkedIn changed", error.Message, StringComparison.Ordinal);
        Assert.Contains("QueryIds", error.Message, StringComparison.Ordinal);
    }

    // ── Fixtures: the recorded shapes, with made-up people ──────────────────

    private static object Text(string text) => new { attributes = Array.Empty<object>(), text };

    private static object Participant(string id, string first, string last, string? headline = null) => new
    {
        hostIdentityUrn = "urn:li:fsd_profile:" + id,
        entityUrn = "urn:li:msg_messagingParticipant:urn:li:fsd_profile:" + id,
        participantType = new
        {
            member = new
            {
                profileUrl = "https://www.linkedin.com/in/" + id,
                firstName = Text(first),
                lastName = Text(last),
                distance = "DISTANCE_1",
                headline = headline is null ? null : Text(headline),
            },
            agent = (object?)null,
            organization = (object?)null,
        },
    };

    private static object Message(string senderId, string first, string last, string text, long at) => new
    {
        body = Text(text),
        deliveredAt = at,
        entityUrn = $"urn:li:msg_message:(urn:li:fsd_profile:{Me},2-TXNn{at})",
        sender = Participant(senderId, first, last),
        actor = Participant(senderId, first, last),
    };

    private static string Conversations() => JsonSerializer.Serialize(new
    {
        data = new
        {
            messengerConversationsBySyncToken = new
            {
                metadata = new { newSyncToken = "t" },
                elements = new object[]
                {
                    new
                    {
                        entityUrn = $"urn:li:msg_conversation:(urn:li:fsd_profile:{Me},{Thread})",
                        backendUrn = "urn:li:messagingThread:" + Thread,
                        conversationParticipants = new[] { Participant(JaneId, "Jane", "Doe", "Engineer at Contoso"), Participant(Me, "Mark", "Member") },
                        messages = new { elements = new[] { Message(JaneId, "Jane", "Doe", "Lunch on Friday?", 1794943053997) } },
                        unreadCount = 2, read = false, groupChat = false, lastActivityAt = 1794943053997L, categories = new[] { "INBOX" },
                    },
                    new
                    {
                        entityUrn = $"urn:li:msg_conversation:(urn:li:fsd_profile:{Me},{GroupThread})",
                        backendUrn = "urn:li:messagingThread:" + GroupThread,
                        conversationParticipants = new[] { Participant(JaneId, "Jane", "Doe"), Participant(SamId, "Sam", "Roe"), Participant(Me, "Mark", "Member") },
                        messages = new { elements = new[] { Message(Me, "Mark", "Member", "See you all", 1794900000000) } },
                        unreadCount = 0, read = true, groupChat = true, lastActivityAt = 1794900000000L, categories = new[] { "INBOX" },
                    },
                },
            },
        },
    });

    private static string Messages(string key, params (string Id, string First, string Last, string Text, long At)[] messages) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["data"] = new Dictionary<string, object>
            {
                [key] = new
                {
                    metadata = new { newSyncToken = "t", nextCursor = (string?)null, prevCursor = (string?)null },
                    elements = messages.Select(m => Message(m.Id, m.First, m.Last, m.Text, m.At)).ToArray(),
                },
            },
        });

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/graphql") };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }
}

/// <summary>Records each request (headers lower-cased, cookie included) and answers from the first matching rule.</summary>
public sealed class FakeLinkedInHttp : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> When, Func<HttpRequestMessage, HttpResponseMessage> Answer)> _rules = [];

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public void Respond(Func<HttpRequestMessage, bool> when, Func<HttpRequestMessage, HttpResponseMessage> answer) => _rules.Add((when, answer));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // As sent on the wire: User-Agent's product tokens are separated by spaces, other lists by commas.
        foreach (var (name, values) in request.Headers)
            headers[name] = string.Join(name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ? " " : ", ", values);
        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers) headers[name] = string.Join(", ", values);
        }
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue(new RecordedRequest(request.Method.Method, request.RequestUri!.AbsoluteUri, request.RequestUri.Query, headers, body));
        foreach (var (when, answer) in _rules)
        {
            if (when(request)) return answer(request);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

public sealed record RecordedRequest(string Method, string Url, string Query, IReadOnlyDictionary<string, string> Headers, string? Body);
