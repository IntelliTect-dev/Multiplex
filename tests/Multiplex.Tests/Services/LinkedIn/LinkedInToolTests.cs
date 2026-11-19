using System.Text.Json;
using Multiplex.Tests.Services.TripIt;
using Multiplex.Web.Services.LinkedIn;
using static Multiplex.Tests.Services.LinkedIn.FakeLinkedInAccount;

namespace Multiplex.Tests.Services.LinkedIn;

/// <summary>
/// The LinkedIn tools over real MCP JSON-RPC and real OAuth, in front of a stand-in LinkedIn:
/// what each account sees, and that every change previews first, happens only with its confirm
/// token, happens once, and reaches exactly one invitation or conversation.
/// </summary>
public sealed class LinkedInToolTests : IClassFixture<LinkedInTestApp>
{
    private static readonly string[] s_readTools = ["linkedin_list_invitations", "linkedin_list_conversations", "linkedin_read_conversation"];

    private static readonly string[] s_changeTools = ["linkedin_accept_invitation", "linkedin_ignore_invitation", "linkedin_reply"];

    private static readonly LinkedInPerson s_jane = Person("jane-doe", "Jane Doe", "Engineer at Contoso");
    private static readonly LinkedInPerson s_sam = Person("sam-roe", "Sam Roe", "Recruiter at Fabrikam");

    private readonly LinkedInTestApp _app;
    private readonly string _account = "member-" + Guid.NewGuid().ToString("N")[..8];

    public LinkedInToolTests(LinkedInTestApp app) => _app = app;

    private FakeLinkedInAccount LinkedIn => _app.LinkedIn.Account(_account);

    private McpTestClient Mcp => _app.McpFor(_account);

    private static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement;

    /// <summary>Calls a change tool without a token, asserts it only previewed, and returns the preview and token.</summary>
    private async Task<(string Preview, string Token)> PreviewAsync(string tool, Dictionary<string, object?> arguments)
    {
        var (isError, text) = await Mcp.CallAsync(tool, arguments);
        Assert.False(isError, text);
        var result = Parse(text);
        Assert.True(result.GetProperty("confirmation_required").GetBoolean(), text);
        Assert.True(result.GetProperty("nothing_changed").GetBoolean(), text);
        Assert.Empty(LinkedIn.Changes);
        return (result.GetProperty("preview").GetString()!, result.GetProperty("confirm_token").GetString()!);
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> arguments, string token) =>
        new(arguments) { ["confirm_token"] = token };

    // ── The tool list ───────────────────────────────────────────────────────

    [Fact]
    public async Task Serves_only_the_invitation_and_messaging_tools_each_taking_at_most_one_person()
    {
        var mcp = Mcp;
        await mcp.InitializeAsync();

        var tools = await mcp.ListToolsAsync();

        var names = tools.Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(s_readTools.Concat(s_changeTools).Order(StringComparer.Ordinal), names);
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString()!;
            var hints = tool.GetProperty("annotations");
            var properties = tool.GetProperty("inputSchema").GetProperty("properties");
            var isChange = s_changeTools.Contains(name);

            Assert.Equal(!isChange, hints.GetProperty("readOnlyHint").GetBoolean());
            Assert.Equal(isChange, hints.GetProperty("openWorldHint").GetBoolean());
            Assert.Equal(isChange, properties.TryGetProperty("confirm_token", out _));
            foreach (var property in properties.EnumerateObject())
            {
                // A list of people (or of anything) would make bulk sending one call away.
                Assert.False(property.Value.TryGetProperty("type", out var type) && type.ToString().Contains("array", StringComparison.Ordinal),
                    $"{name}.{property.Name} takes a list");
            }
        }
    }

    // ── Reading ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_account_sees_only_its_own_invitations()
    {
        var other = "member-other-" + Guid.NewGuid().ToString("N")[..8];
        LinkedIn.Received.Add(new LinkedInInvitation("inv-mine", s_jane, "Met at the conference", DateTimeOffset.Parse("2026-11-18T10:00:00Z")));
        _app.LinkedIn.Account(other).Received.Add(new LinkedInInvitation("inv-theirs", s_sam, null, null));

        var (isError, text) = await Mcp.CallAsync("linkedin_list_invitations");

        Assert.False(isError, text);
        var invitation = Assert.Single(Parse(text).GetProperty("invitations").EnumerateArray());
        Assert.Equal("inv-mine", invitation.GetProperty("invitation_id").GetString());
        Assert.Equal("Jane Doe", invitation.GetProperty("person").GetProperty("name").GetString());
        Assert.Equal("https://www.linkedin.com/in/jane-doe/", invitation.GetProperty("person").GetProperty("profile_url").GetString());
        Assert.Equal("Met at the conference", invitation.GetProperty("note").GetString());
        Assert.DoesNotContain("inv-theirs", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sent_invitations_are_listed_separately()
    {
        LinkedIn.Received.Add(new LinkedInInvitation("inv-in", s_jane, null, null));
        LinkedIn.Sent.Add(new LinkedInInvitation("inv-out", s_sam, "Hello", null));

        var (_, text) = await Mcp.CallAsync("linkedin_list_invitations", new { direction = "sent" });

        var invitation = Assert.Single(Parse(text).GetProperty("invitations").EnumerateArray());
        Assert.Equal("inv-out", invitation.GetProperty("invitation_id").GetString());
        Assert.Equal("sent", Parse(text).GetProperty("direction").GetString());
    }

    [Fact]
    public async Task Reading_a_conversation_shows_who_sent_each_message()
    {
        LinkedIn.Conversations.Add(new LinkedInConversation("conv-1", [s_jane], "See you then", null, Unread: true));
        LinkedIn.Messages["conv-1"] =
        [
            new LinkedInMessage("m1", s_jane, "Lunch on Friday?", DateTimeOffset.Parse("2026-11-18T09:00:00Z")),
            new LinkedInMessage("m2", null, "See you then", DateTimeOffset.Parse("2026-11-18T09:05:00Z")),
        ];

        var (isError, text) = await Mcp.CallAsync("linkedin_read_conversation", new { conversationId = "conv-1" });

        Assert.False(isError, text);
        var messages = Parse(text).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["Jane Doe", "you"], messages.Select(m => m.GetProperty("from").GetString()));
        Assert.Equal("Lunch on Friday?", messages[0].GetProperty("text").GetString());
    }

    // ── Invitations ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Accepting_previews_first_then_accepts_once_with_the_token()
    {
        LinkedIn.Received.Add(new LinkedInInvitation("inv-1", s_jane, null, null));
        var arguments = new Dictionary<string, object?> { ["invitationId"] = "inv-1" };

        var (preview, token) = await PreviewAsync("linkedin_accept_invitation", arguments);
        Assert.Contains("Jane Doe (Engineer at Contoso)", preview, StringComparison.Ordinal);

        var (isError, text) = await Mcp.CallAsync("linkedin_accept_invitation", With(arguments, token));
        Assert.False(isError, text);
        Assert.True(Parse(text).GetProperty("accepted").GetBoolean());
        Assert.Equal(["accept inv-1"], LinkedIn.Changes);

        // Put it back, so only the token can stop a second acceptance.
        LinkedIn.Received.Add(new LinkedInInvitation("inv-1", s_jane, null, null));
        var (replayError, replay) = await Mcp.CallAsync("linkedin_accept_invitation", With(arguments, token));
        Assert.True(replayError);
        Assert.Contains("already been used", replay, StringComparison.Ordinal);
        Assert.Single(LinkedIn.Changes);
    }

    [Fact]
    public async Task A_token_for_one_invitation_does_not_ignore_another()
    {
        LinkedIn.Received.Add(new LinkedInInvitation("inv-1", s_jane, null, null));
        LinkedIn.Received.Add(new LinkedInInvitation("inv-2", s_sam, null, null));
        var (_, token) = await PreviewAsync("linkedin_ignore_invitation", new() { ["invitationId"] = "inv-1" });

        var (isError, text) = await Mcp.CallAsync("linkedin_ignore_invitation", new { invitationId = "inv-2", confirm_token = token });

        Assert.True(isError);
        Assert.Contains("different request", text, StringComparison.Ordinal);
        Assert.Empty(LinkedIn.Changes);
    }

    [Fact]
    public async Task An_invitation_that_is_no_longer_waiting_is_refused()
    {
        var (isError, text) = await Mcp.CallAsync("linkedin_accept_invitation", new { invitationId = "inv-gone" });

        Assert.True(isError);
        Assert.Contains("no waiting received invitation", text, StringComparison.Ordinal);
        Assert.Empty(LinkedIn.Changes);
    }

    // ── Messages ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reply_in_a_group_names_everyone_who_will_receive_it_and_sends_the_text_exactly()
    {
        LinkedIn.Conversations.Add(new LinkedInConversation("conv-g", [s_jane, s_sam], null, null, Unread: false));
        const string message = "Thanks both!\nTalk soon.";
        var arguments = new Dictionary<string, object?> { ["conversationId"] = "conv-g", ["text"] = message };

        var (preview, token) = await PreviewAsync("linkedin_reply", arguments);
        Assert.Contains("everyone in this group conversation", preview, StringComparison.Ordinal);
        Assert.Contains("Jane Doe", preview, StringComparison.Ordinal);
        Assert.Contains("Sam Roe", preview, StringComparison.Ordinal);
        Assert.Contains(message, preview, StringComparison.Ordinal);

        var (isError, text) = await Mcp.CallAsync("linkedin_reply", With(arguments, token));

        Assert.False(isError, text);
        Assert.Equal([$"reply conv-g: {message}"], LinkedIn.Changes);
    }

    [Fact]
    public async Task A_token_for_one_message_does_not_send_different_text()
    {
        LinkedIn.Conversations.Add(new LinkedInConversation("conv-1", [s_jane], null, null, Unread: false));
        var (_, token) = await PreviewAsync("linkedin_reply", new() { ["conversationId"] = "conv-1", ["text"] = "Yes" });

        var (isError, _) = await Mcp.CallAsync("linkedin_reply", new { conversationId = "conv-1", text = "No", confirm_token = token });

        Assert.True(isError);
        Assert.Empty(LinkedIn.Changes);
    }

    [Fact]
    public async Task An_empty_message_is_refused()
    {
        LinkedIn.Conversations.Add(new LinkedInConversation("conv-1", [s_jane], null, null, Unread: false));

        var (isError, text) = await Mcp.CallAsync("linkedin_reply", new { conversationId = "conv-1", text = "   " });

        Assert.True(isError);
        Assert.Contains("empty", text, StringComparison.Ordinal);
    }

    // ── When LinkedIn fails ─────────────────────────────────────────────────

    [Fact]
    public async Task A_security_check_is_reported_and_not_worked_around()
    {
        LinkedIn.Fail = new LinkedInChallengeException("LinkedIn asked for a security check. Open LinkedIn yourself to answer it, then try again.");

        var (isError, text) = await Mcp.CallAsync("linkedin_list_conversations");

        Assert.True(isError);
        Assert.Contains("security check", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_out_account_is_told_to_reconnect()
    {
        LinkedIn.Fail = new LinkedInSessionEndedException();

        var (isError, text) = await Mcp.CallAsync("linkedin_list_invitations");

        Assert.True(isError);
        Assert.Contains(LinkedInToolContext.ReconnectMessage, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unexpected_failure_reveals_no_details()
    {
        LinkedIn.Fail = new InvalidOperationException("secret internals: cookie=abc");

        var (isError, text) = await Mcp.CallAsync("linkedin_list_invitations");

        Assert.True(isError);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.Contains("server log", text, StringComparison.Ordinal);
    }
}
