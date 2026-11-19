using System.Net;
using System.Text;
using System.Text.Json;
using Multiplex.Tests.Services.TripIt;
using Multiplex.Web.Services.LinkedIn;
using Multiplex.Web.Services.LinkedIn.SignIn;
using static Multiplex.Tests.Services.LinkedIn.FakeLinkedInAccount;

namespace Multiplex.Tests.Services.LinkedIn;

/// <summary>
/// What the independent review (Sonnet 5.5, 2026-11-19) asked to be shown: a change whose result
/// is unknown says so; a confirm token covers what the preview showed; success is read from
/// LinkedIn's own response; ids are matched exactly; a security-check page never completes a sign-in.
/// </summary>
public sealed class LinkedInReviewFindingsTests : IClassFixture<LinkedInTestApp>
{
    private const string Me = "ACoAAMember000000000000000000000000000001";
    private const string Thread = "2-VGhyZWFkT25lAA==";
    private const string InvitationId = "7000000000000000001";

    private readonly LinkedInTestApp _app;
    private readonly FakeLinkedInHttp _http = new();
    private readonly string _account = "member-" + Guid.NewGuid().ToString("N")[..8];

    public LinkedInReviewFindingsTests(LinkedInTestApp app) => _app = app;

    private LinkedInClient Client(Func<LinkedInSession, CancellationToken, Task<LinkedInSession?>>? renew = null) => new(new LinkedInSession
    {
        Cookie = "li_at=a",
        CsrfToken = "ajax:1",
        ProfileId = Me,
    }, new HttpClient(_http), new LinkedInQueryIds(), renew);

    // ── A change whose result is unknown ────────────────────────────────────

    public static TheoryData<string> Changes => ["reply", "accept", "ignore"];

    private Task Change(string which, LinkedInClient client) => which switch
    {
        "reply" => client.ReplyAsync(Thread, "Hi", CancellationToken.None),
        "accept" => client.AcceptInvitationAsync(InvitationId, CancellationToken.None),
        _ => client.IgnoreInvitationAsync(InvitationId, CancellationToken.None),
    };

    private void ServeInvitationsPage() =>
        _http.Respond(r => r.Method == HttpMethod.Get, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(InvitationsPage(), Encoding.UTF8, "text/html"),
        });

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_change_LinkedIn_answers_with_a_server_error_is_reported_as_maybe_done_and_sent_once(string which)
    {
        ServeInvitationsPage();
        _http.Respond(r => r.Method == HttpMethod.Post, _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var error = await Assert.ThrowsAsync<LinkedInApiException>(() => Change(which, Client()));

        Assert.Single(_http.Requests, r => r.Method == "POST");
        Assert.Contains("may or may not have taken effect", error.Message, StringComparison.Ordinal);
        Assert.Contains("check LinkedIn", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_change_whose_connection_drops_is_reported_as_maybe_done(string which)
    {
        ServeInvitationsPage();
        _http.Respond(r => r.Method == HttpMethod.Post, _ => throw new HttpRequestException("connection reset"));

        var error = await Assert.ThrowsAsync<LinkedInApiException>(() => Change(which, Client()));

        Assert.Contains("may or may not have taken effect", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_change_LinkedIn_rejects_the_session_for_is_never_renewed_or_resent(string which)
    {
        ServeInvitationsPage();
        _http.Respond(r => r.Method == HttpMethod.Post, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var renewals = 0;

        await Assert.ThrowsAsync<LinkedInApiException>(() =>
            Change(which, Client((_, _) => { renewals++; return Task.FromResult<LinkedInSession?>(null); })));

        Assert.Single(_http.Requests, r => r.Method == "POST");
        Assert.Equal(0, renewals);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("ignore")]
    public async Task An_invitation_change_stopped_by_LinkedIns_automation_block_is_sent_once(string which)
    {
        ServeInvitationsPage();
        _http.Respond(r => r.Method == HttpMethod.Post, _ => new HttpResponseMessage((HttpStatusCode)999));

        await Assert.ThrowsAsync<LinkedInChallengeException>(() => Change(which, Client()));

        Assert.Single(_http.Requests, r => r.Method == "POST");
    }

    // ── Success is read from LinkedIn's own response ────────────────────────

    [Theory]
    [InlineData("""0:{"response":{"$type":"proto.sdui.responses.ServerRequestResponse","errors":[],"isRetryable":false}}""", true)]
    [InlineData("""0:{"response":{"$type":"proto.sdui.responses.ServerRequestResponse","errors":[{"message":"x"}]}}""", false)]
    [InlineData("""0:{"other":{"errors":[]},"response":{"$type":"proto.sdui.responses.ServerRequestResponse","errors":[{"m":1}]}}""", false)]
    [InlineData("""0:{"other":{"errors":[]}}""", null)]
    [InlineData("", null)]
    public void Success_is_the_server_request_responses_own_empty_error_list(string flight, bool? expected)
        => Assert.Equal(expected, LinkedInClient.ServerRequestSucceeded(flight));

    // ── Exact ids ───────────────────────────────────────────────────────────

    [Fact]
    public async Task An_id_or_cursor_with_a_trailing_line_break_is_refused_before_LinkedIn_is_asked()
    {
        var client = Client();

        await Assert.ThrowsAsync<LinkedInApiException>(() => client.ReadConversationAsync(Thread + "\n", null, CancellationToken.None));
        await Assert.ThrowsAsync<LinkedInApiException>(() => client.ReadConversationAsync(Thread, "1794943000000\n", CancellationToken.None));
        await Assert.ThrowsAsync<LinkedInApiException>(() => client.AcceptInvitationAsync(InvitationId + "\n", CancellationToken.None));
        await Assert.ThrowsAsync<LinkedInApiException>(() => client.ReplyAsync(Thread + "\n", "Hi", CancellationToken.None));

        Assert.Empty(_http.Requests);
    }

    // ── The confirm token covers what the preview showed ────────────────────

    [Fact]
    public async Task A_warning_LinkedIn_adds_after_the_preview_voids_the_confirm_token()
    {
        var linkedIn = _app.LinkedIn.Account(_account);
        var sam = Person("sam-roe", "Sam Roe");
        linkedIn.Received.Add(new LinkedInInvitation("inv-1", sam, null, null));
        var mcp = _app.McpFor(_account);
        var (_, text) = await mcp.CallAsync("linkedin_accept_invitation", new { invitationId = "inv-1" });
        var token = JsonDocument.Parse(text).RootElement.GetProperty("confirm_token").GetString();

        linkedIn.Received[0] = linkedIn.Received[0] with { Warning = "Take care when connecting" };
        var (isError, refused) = await mcp.CallAsync("linkedin_accept_invitation", new { invitationId = "inv-1", confirm_token = token });

        Assert.True(isError);
        Assert.Contains("different request", refused, StringComparison.Ordinal);
        Assert.Empty(linkedIn.Changes);
    }

    [Fact]
    public async Task Someone_joining_the_conversation_after_the_preview_voids_the_confirm_token()
    {
        var linkedIn = _app.LinkedIn.Account(_account);
        var jane = Person("jane-doe", "Jane Doe");
        linkedIn.Conversations.Add(new LinkedInConversation("conv-1", [jane], null, null, Unread: false));
        var mcp = _app.McpFor(_account);
        var (_, text) = await mcp.CallAsync("linkedin_reply", new { conversationId = "conv-1", text = "Hi" });
        var token = JsonDocument.Parse(text).RootElement.GetProperty("confirm_token").GetString();

        linkedIn.Conversations[0] = linkedIn.Conversations[0] with { Participants = [jane, Person("sam-roe", "Sam Roe")] };
        var (isError, _) = await mcp.CallAsync("linkedin_reply", new { conversationId = "conv-1", text = "Hi", confirm_token = token });

        Assert.True(isError);
        Assert.Empty(linkedIn.Changes);
    }

    // ── Sign-in ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.linkedin.com/checkpoint/challenge/AgE?ut=x", true)]
    [InlineData("https://www.linkedin.com/checkpoint/lg/login-submit", true)]
    [InlineData("https://www.linkedin.com/feed/", false)]
    [InlineData("https://evil.example/checkpoint/", false)]
    public void A_security_check_page_is_recognised_so_it_never_completes_a_sign_in(string url, bool checkpoint)
        => Assert.Equal(checkpoint, LinkedInSignInDefinition.IsCheckpointPage(url));

    // ── Fixture: one invitation, shaped like the recorded page ──────────────

    private static string InvitationsPage()
    {
        object Action(string type) => new Dictionary<string, object?>
        {
            ["$type"] = "proto.sdui.actions.core.ServerRequest",
            ["value"] = new Dictionary<string, object?>
            {
                ["requestId"] = "com.linkedin.sdui.requests.mynetwork.addaInvitationAction",
                ["requestedArguments"] = new Dictionary<string, object?>
                {
                    ["payload"] = new Dictionary<string, object?>
                    {
                        ["inviteeActionType"] = "InviteeActionType_" + type,
                        ["invitationUrn"] = new { invitationId = InvitationId },
                        ["validationToken"] = "t",
                        ["profileUrn"] = new { profileId = "ACoAAJane0000000000000000000000000000002" },
                    },
                },
            },
        };
        var flight = JsonSerializer.Serialize(new { actions = new[] { Action("IGNORE"), Action("ACCEPT") } })
            .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"""<div componentkey="invitation_manager_row_urn:li:invitation:{InvitationId}"><p><a aria-label="View Jane Doe’s profile" href="x"><strong>Jane Doe</strong></a> wants to connect</p></div>""" +
               "<script>self.__next_f.push([1,\"" + flight + "\"])</script>";
    }
}
