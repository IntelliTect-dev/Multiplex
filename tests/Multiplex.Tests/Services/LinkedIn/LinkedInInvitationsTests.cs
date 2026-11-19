using System.Net;
using System.Text;
using System.Text.Json;
using Multiplex.Web.Services.LinkedIn;

namespace Multiplex.Tests.Services.LinkedIn;

/// <summary>
/// Invitations through LinkedIn's server-driven invitations page, against hand-made pages built
/// the way the recorded one (2026-11-19) is: each invitation's HTML row keyed by its id, and the
/// Accept / Ignore actions in the page's escaped flight data keyed by the same id. People are tied
/// to invitations by that id only.
/// </summary>
public sealed class LinkedInInvitationsTests
{
    private const string Me = "ACoAAMember000000000000000000000000000001";

    private readonly FakeLinkedInHttp _http = new();

    private LinkedInClient Client() => new(new LinkedInSession
    {
        Cookie = "li_at=a; JSESSIONID=\"ajax:1\"",
        CsrfToken = "ajax:1",
        ProfileId = Me,
    }, new HttpClient(_http), new LinkedInQueryIds());

    private sealed record Row(string Id, string Name, string Headline, string ProfileId, string? Note = null, bool Warned = false);

    private static readonly Row s_jane = new("7000000000000000001", "Jane Doe", "Engineer at Contoso", "ACoAAJane0000000000000000000000000000002");
    private static readonly Row s_sam = new("7000000000000000002", "Sam Roe", "Recruiter at Fabrikam", "ACoAASam00000000000000000000000000000003",
        Note: "Hi,\nWe met at the conference.");
    private static readonly Row s_kim = new("7000000000000000003", "Kim Poe", "Data broker", "ACoAAKim00000000000000000000000000000004", Warned: true);

    // ── Reading the page ────────────────────────────────────────────────────

    [Fact]
    public void Each_person_is_tied_to_their_own_invitation_even_when_the_actions_come_in_another_order()
    {
        var html = Page([s_jane, s_sam, s_kim], actionOrder: [s_kim, s_sam, s_jane]);

        var invitations = LinkedInInvitationsPage.Parse(html);

        Assert.Equal([s_jane.Id, s_sam.Id, s_kim.Id], invitations.Select(i => i.InvitationId));
        foreach (var (row, parsed) in new[] { s_jane, s_sam, s_kim }.Zip(invitations))
        {
            Assert.Equal(row.Name, parsed.Person.Name);
            Assert.Equal(row.Headline, parsed.Person.Headline);
            Assert.Equal(row.ProfileId, parsed.Person.ProfileId);
            Assert.Contains(row.Id, parsed.IgnoreAction, StringComparison.Ordinal);
        }
        Assert.Equal("Hi,\nWe met at the conference.", invitations[1].Note);
        Assert.Null(invitations[0].Note);
        Assert.Null(invitations[2].AcceptAction);
        Assert.Equal("Take care when connecting", invitations[2].WarningTitle);
        Assert.Null(invitations[0].WarningTitle);
    }

    [Fact]
    public void A_row_without_its_own_ignore_action_means_the_page_is_not_trusted()
    {
        var html = Page([s_jane, s_sam], actionOrder: [s_jane]);

        Assert.Throws<LinkedInApiException>(() => LinkedInInvitationsPage.Parse(html));
    }

    [Fact]
    public void A_page_with_no_invitations_lists_none()
        => Assert.Empty(LinkedInInvitationsPage.Parse("<html><main>No pending invitations</main></html>"));

    // ── Through the client ──────────────────────────────────────────────────

    [Fact]
    public async Task Listing_reads_the_received_page_and_shows_LinkedIns_warning()
    {
        _http.Respond(r => r.Method == HttpMethod.Get, _ => Html(Page([s_jane, s_kim])));

        var page = await Client().ListInvitationsAsync(InvitationDirection.Received, CancellationToken.None);

        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://www.linkedin.com/mynetwork/invitation-manager/received/", request.Url);
        Assert.Equal(["Jane Doe", "Kim Poe"], page.Invitations.Select(i => i.Person.Name));
        Assert.Null(page.Invitations[0].Warning);
        Assert.Equal("Take care when connecting", page.Invitations[1].Warning);
    }

    [Fact]
    public async Task Accepting_sends_the_pages_own_action_in_the_recorded_envelope()
    {
        _http.Respond(r => r.Method == HttpMethod.Get, _ => Html(Page([s_jane, s_sam])));
        _http.Respond(r => r.Method == HttpMethod.Post, _ => Flight(Ok));

        await Client().AcceptInvitationAsync(s_sam.Id, CancellationToken.None);

        var post = _http.Requests.Single(r => r.Method == "POST");
        Assert.Equal("https://www.linkedin.com/flagship-web/rsc-action/actions/server-request?sduiid=com.linkedin.sdui.requests.mynetwork.addaInvitationAction", post.Url);
        Assert.Equal("true", post.Headers["x-li-rsc-stream"]);
        Assert.Equal("ajax:1", post.Headers["csrf-token"]);
        var body = JsonDocument.Parse(post.Body!).RootElement;
        Assert.Equal("com.linkedin.sdui.requests.mynetwork.addaInvitationAction", body.GetProperty("requestId").GetString());
        var payload = body.GetProperty("serverRequest").GetProperty("requestedArguments").GetProperty("payload");
        Assert.Equal("InviteeActionType_ACCEPT", payload.GetProperty("inviteeActionType").GetString());
        Assert.Equal(s_sam.Id, payload.GetProperty("invitationUrn").GetProperty("invitationId").GetString());
        Assert.Equal("token-" + s_sam.Id, payload.GetProperty("validationToken").GetString());
        Assert.Equal(payload.GetRawText(), body.GetProperty("requestedArguments").GetProperty("payload").GetRawText());
        Assert.Equal("com.linkedin.sdui.flagshipnav.mynetwork.invitations.InvitationReceivedWithType",
            body.GetProperty("requestedArguments").GetProperty("screenId").GetString());
    }

    [Fact]
    public async Task Ignoring_sends_the_pages_ignore_action_for_that_invitation_only()
    {
        _http.Respond(r => r.Method == HttpMethod.Get, _ => Html(Page([s_jane, s_sam])));
        _http.Respond(r => r.Method == HttpMethod.Post, _ => Flight(Ok));

        await Client().IgnoreInvitationAsync(s_jane.Id, CancellationToken.None);

        var payload = JsonDocument.Parse(_http.Requests.Single(r => r.Method == "POST").Body!).RootElement
            .GetProperty("serverRequest").GetProperty("requestedArguments").GetProperty("payload");
        Assert.Equal("InviteeActionType_IGNORE", payload.GetProperty("inviteeActionType").GetString());
        Assert.Equal(s_jane.Id, payload.GetProperty("invitationUrn").GetProperty("invitationId").GetString());
    }

    [Fact]
    public async Task Accepting_a_warned_invitation_opens_LinkedIns_warning_then_sends_its_accept_action()
    {
        _http.Respond(r => r.Method == HttpMethod.Get, _ => Html(Page([s_kim])));
        _http.Respond(r => r.RequestUri!.AbsolutePath.EndsWith("/navigation", StringComparison.Ordinal), _ => Flight(WarningAnswer(s_kim)));
        _http.Respond(r => r.RequestUri!.AbsolutePath.EndsWith("/server-request", StringComparison.Ordinal), _ => Flight(Ok));

        await Client().AcceptInvitationAsync(s_kim.Id, CancellationToken.None);

        var posts = _http.Requests.Where(r => r.Method == "POST").ToList();
        Assert.Equal(2, posts.Count);
        Assert.Contains("screenId=com.linkedin.sdui.flagshipnav.mynetwork.invitations.AcceptConfirmationDialog", posts[0].Url, StringComparison.Ordinal);
        var opened = JsonDocument.Parse(posts[0].Body!).RootElement;
        Assert.True(opened.GetProperty("isModal").GetBoolean());
        Assert.Equal(s_kim.Id, opened.GetProperty("clientArguments").GetProperty("payload").GetProperty("invitationUrn")
            .GetProperty("invitationId").GetString());
        var accepted = JsonDocument.Parse(posts[1].Body!).RootElement.GetProperty("serverRequest").GetProperty("requestedArguments").GetProperty("payload");
        Assert.Equal("InviteeActionType_ACCEPT", accepted.GetProperty("inviteeActionType").GetString());
        Assert.True(accepted.GetProperty("restoreOnServerFailure").GetBoolean());
    }

    [Fact]
    public async Task An_invitation_no_longer_on_the_page_is_not_acted_on()
    {
        _http.Respond(r => r.Method == HttpMethod.Get, _ => Html(Page([s_jane])));

        await Assert.ThrowsAsync<LinkedInApiException>(() => Client().AcceptInvitationAsync(s_sam.Id, CancellationToken.None));

        Assert.DoesNotContain(_http.Requests, r => r.Method == "POST");
    }

    [Theory]
    [InlineData("")]
    [InlineData("urn:li:invitation:7000000000000000001")]
    [InlineData("7000000000000000001&x=1")]
    public async Task Anything_but_an_invitation_id_is_refused_before_LinkedIn_is_asked(string invitationId)
    {
        await Assert.ThrowsAsync<LinkedInApiException>(() => Client().IgnoreInvitationAsync(invitationId, CancellationToken.None));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_refusal_from_LinkedIn_is_reported()
    {
        _http.Respond(r => r.Method == HttpMethod.Get, _ => Html(Page([s_jane])));
        _http.Respond(r => r.Method == HttpMethod.Post, _ => Flight("""0:{"response":{"errors":[{"message":"nope"}],"isRetryable":false}}"""));

        var error = await Assert.ThrowsAsync<LinkedInApiException>(() => Client().AcceptInvitationAsync(s_jane.Id, CancellationToken.None));

        Assert.Contains("refused", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sent_invitations_are_not_connected_yet()
        => await Assert.ThrowsAsync<LinkedInApiException>(() => Client().ListInvitationsAsync(InvitationDirection.Sent, CancellationToken.None));

    // ── Fixtures: pages shaped like the recorded one, with made-up people ───

    private const string Ok = """0:{"states":[],"response":{"$type":"proto.sdui.responses.ServerRequestResponse","errors":[],"isRetryable":false}}""";

    private static object Action(Row row, string type, bool fromWarning = false) => new Dictionary<string, object?>
    {
        ["$type"] = "proto.sdui.actions.core.ServerRequest",
        ["value"] = new Dictionary<string, object?>
        {
            ["requestId"] = "com.linkedin.sdui.requests.mynetwork.addaInvitationAction",
            ["requestedArguments"] = new Dictionary<string, object?>
            {
                ["$type"] = "proto.sdui.actions.requests.RequestedArguments",
                ["requestedStateKeys"] = Array.Empty<object>(),
                ["payload"] = new Dictionary<string, object?>
                {
                    ["inviteeActionType"] = "InviteeActionType_" + type,
                    ["invitationType"] = "GenericInvitationType_CONNECTION",
                    ["invitationUrn"] = new { invitationId = row.Id },
                    ["validationToken"] = "token-" + row.Id,
                    ["origin"] = "InvitationOrigin_INVITATION_MANAGER",
                    ["restoreOnServerFailure"] = fromWarning,
                    ["postActionSentConfigs"] = Array.Empty<object>(),
                    ["profileUrn"] = new { profileId = row.ProfileId },
                },
                ["requestMetadata"] = new Dictionary<string, object?> { ["$type"] = "proto.sdui.common.RequestMetadata" },
            },
            ["onClientRequestFailureAction"] = new { actions = Array.Empty<object>() },
            ["isApfcEnabled"] = false,
            ["isStreaming"] = false,
            ["rumPageKey"] = "",
        },
    };

    private static object Warning(Row row) => new Dictionary<string, object?>
    {
        ["$type"] = "proto.sdui.actions.core.NavigateToScreen",
        ["screenId"] = "com.linkedin.sdui.flagshipnav.mynetwork.invitations.AcceptConfirmationDialog",
        ["title"] = "Take care when connecting",
        ["requestedArguments"] = new Dictionary<string, object?>
        {
            ["$type"] = "proto.sdui.actions.requests.RequestedArguments",
            ["requestedStateKeys"] = Array.Empty<object>(),
            ["payload"] = new Dictionary<string, object?>
            {
                ["invitationUrn"] = new { invitationId = row.Id },
                ["validationToken"] = "token-" + row.Id,
                ["profileUrn"] = new { profileId = row.ProfileId },
            },
        },
    };

    private static string RowHtml(Row row) =>
        $"""<div class="x" componentkey="invitation_manager_row_urn:li:invitation:{row.Id}"><div data-view-name="pending-invitation">""" +
        $"""<div role="listitem" componentkey="urn:li:invitation:{row.Id}"><p class="y"><a aria-label="View {row.Name}’s profile" href="https://www.linkedin.com/in/x/">""" +
        $"""<strong class="z">{WebUtility.HtmlEncode(row.Name)}<span> </span></strong></a><span> </span>wants to connect</p>""" +
        $"""<div><p class="h"><span>{WebUtility.HtmlEncode(row.Headline)}</span></p></div><div><p><span>Someone is a mutual connection</span></p></div>""" +
        (row.Note is null ? "" : $"""<div><p><span tabindex="-1" data-testid="expandable-text-box">{WebUtility.HtmlEncode(row.Note).Replace("\n", "<br/>", StringComparison.Ordinal)}</span></p></div>""") +
        $"""<button aria-label="Ignore an invitation to connect from {row.Name}">Ignore</button></div></div></div>""";

    private static string Page(Row[] rows, Row[]? actionOrder = null)
    {
        var actions = new List<object>();
        foreach (var row in actionOrder ?? rows)
        {
            actions.Add(Action(row, "IGNORE"));
            actions.Add(row.Warned ? Warning(row) : Action(row, "ACCEPT"));
        }
        var flight = JsonSerializer.Serialize(new { actions });
        var escaped = flight.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return "<html><body><main>" + string.Concat(rows.Select(RowHtml)) + "</main><script>self.__next_f.push([1,\"" + escaped + "\"])</script></body></html>";
    }

    private static string WarningAnswer(Row row) =>
        "0:{\"states\":[]}\n1:[\"$\",\"$L9\",null,{\"children\":[[\"$\",\"$Lc\",null,{\"titleText\":\"Take care when connecting\"}]," +
        "{\"actions\":{\"actions\":[" + JsonSerializer.Serialize(Action(row, "ACCEPT", fromWarning: true)) + "]}}]}]\n";

    private static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Flight(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/octet-stream") };
}
