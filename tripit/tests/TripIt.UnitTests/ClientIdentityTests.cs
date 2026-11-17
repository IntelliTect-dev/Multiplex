using Xunit;

namespace TripIt.UnitTests;

/// <summary>
/// The client never invents a browser identity. It sends the User-Agent of the browser whose
/// session it carries; without one it names itself.
/// </summary>
public sealed class ClientIdentityTests
{
    private static async Task<string> UserAgentSentFor(TripItSession session)
    {
        string? sent = null;
        var handler = new FakeHandler().On("/api/v2/get/profile", req =>
        {
            sent = req.Headers.TryGetValues("User-Agent", out var values) ? string.Join(" ", values) : "";
            return FakeHandler.Json("""{"Profile":{"@attributes":{"ref":"p1"},"screen_name":"tester"}}""");
        });
        var client = new TripItClient(new SnapshotSessionProvider(session), new HttpClient(handler));
        await client.GetProfileAsync();
        return sent ?? throw new InvalidOperationException("No request was sent.");
    }

    [Fact]
    public async Task Without_a_browser_user_agent_the_client_names_itself_rather_than_posing_as_Chrome()
    {
        var ua = await UserAgentSentFor(new TripItSession("session_id=abc", "csrf"));

        Assert.StartsWith("Multiplex-TripIt/", ua, StringComparison.Ordinal);
        Assert.DoesNotContain("Mozilla", ua, StringComparison.Ordinal);
        Assert.DoesNotContain("Chrome", ua, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_session_browsers_user_agent_the_client_sends_that_one()
    {
        const string browser = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";
        var session = new TripItSession("session_id=abc", "csrf",
            spaHeaders: new Dictionary<string, string> { ["user-agent"] = browser });

        Assert.Equal(browser, await UserAgentSentFor(session));
    }
}
