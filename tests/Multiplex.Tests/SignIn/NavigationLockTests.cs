using System.Text.Json;
using Multiplex.Web.SignIn;

namespace Multiplex.Tests.SignIn;

/// <summary>
/// The navigation lock's decisions, without a browser: what it answers for a paused Document
/// request, and what it does about a page that has committed a URL.
/// </summary>
public sealed class NavigationLockTests
{
    private static readonly string[] s_hosts = ["www.tripit.com", "accounts.google.com"];

    private readonly List<(string Method, Dictionary<string, object> Args)> _sent = [];

    private Task Send(string method, Dictionary<string, object> args)
    {
        _sent.Add((method, args));
        return Task.CompletedTask;
    }

    private static JsonElement Paused(string url, string? frameId = "frame-1") =>
        JsonDocument.Parse(JsonSerializer.Serialize(frameId is null
            ? (object)new { requestId = "req-1", request = new { url } }
            : new { requestId = "req-1", request = new { url }, frameId })).RootElement.Clone();

    [Fact]
    public async Task Allowed_host_is_let_through()
    {
        var decision = await NavigationLock.DecideAsync(Paused("https://www.tripit.com/account/login"), s_hosts, _ => Task.FromResult(true), Send);

        Assert.Equal(PausedRequestOutcome.Continued, decision.Outcome);
        Assert.Equal("Fetch.continueRequest", Assert.Single(_sent).Method);
    }

    [Fact]
    public async Task Top_level_navigation_to_another_host_is_failed()
    {
        var decision = await NavigationLock.DecideAsync(Paused("https://evil.example/"), s_hosts, _ => Task.FromResult(true), Send);

        Assert.Equal(PausedRequestOutcome.Blocked, decision.Outcome);
        Assert.Equal("https://evil.example/", decision.Url);
        var (method, args) = Assert.Single(_sent);
        Assert.Equal("Fetch.failRequest", method);
        Assert.Equal("req-1", args["requestId"]);
    }

    [Fact]
    public async Task Frame_loading_another_host_is_let_through()
    {
        var decision = await NavigationLock.DecideAsync(Paused("https://captcha.example/frame"), s_hosts, _ => Task.FromResult(false), Send);

        Assert.Equal(PausedRequestOutcome.Continued, decision.Outcome);
        Assert.Equal("Fetch.continueRequest", Assert.Single(_sent).Method);
    }

    /// <summary>Without a frame id nobody can say it is only a frame, so it is judged as a top-level page.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Request_without_a_frame_id_is_treated_as_top_level(string? frameId)
    {
        // Even a frame check that would call everything a sub-frame cannot let it through.
        var decision = await NavigationLock.DecideAsync(Paused("https://evil.example/", frameId), s_hosts, _ => Task.FromResult(false), Send);

        Assert.Equal(PausedRequestOutcome.Blocked, decision.Outcome);
        Assert.Equal("Fetch.failRequest", Assert.Single(_sent).Method);
    }

    /// <summary>A decision that throws must not leave the request paused (the tab hangs) or let it through.</summary>
    [Fact]
    public async Task Request_the_lock_cannot_judge_is_failed_not_left_hanging()
    {
        var decision = await NavigationLock.DecideAsync(Paused("https://evil.example/"), s_hosts,
            _ => throw new InvalidOperationException("Target.getTargets failed"), Send);

        Assert.Equal(PausedRequestOutcome.FailedClosed, decision.Outcome);
        Assert.IsType<InvalidOperationException>(decision.Error);
        var (method, args) = Assert.Single(_sent);
        Assert.Equal("Fetch.failRequest", method);
        Assert.Equal("req-1", args["requestId"]);
    }

    /// <summary>
    /// The second line of the lock: a page may only show an allowed http(s) host. Any other
    /// scheme a main frame commits (local files, browser pages, data, script) is sent back too.
    /// </summary>
    [Theory]
    [InlineData("", "Ignore")]
    [InlineData("about:blank", "Ignore")]
    [InlineData("chrome-error://chromewebdata/", "ErrorPage")]
    [InlineData("https://www.tripit.com/app/trips", "Allowed")]
    [InlineData("https://ACCOUNTS.google.com/signin", "Allowed")]
    [InlineData("http://www.tripit.com/", "Allowed")]
    [InlineData("https://evil.example/", "Blocked")]
    [InlineData("file:///C:/Users/someone/secret.txt", "Blocked")]
    [InlineData("chrome://settings/passwords", "Blocked")]
    [InlineData("view-source:https://www.tripit.com/", "Blocked")]
    [InlineData("data:text/html,<h1>hi</h1>", "Blocked")]
    [InlineData("javascript:alert(1)", "Blocked")]
    [InlineData("blob:https://www.tripit.com/0b5c3f6e-1111-2222-3333-444455556666", "Blocked")]
    [InlineData("devtools://devtools/bundled/inspector.html", "Blocked")]
    [InlineData("about:srcdoc", "Blocked")]
    [InlineData("not a url", "Blocked")]
    public void Committed_page_is_judged_by_scheme_and_host(string url, string expected)
    {
        Assert.Equal(Enum.Parse<CommittedPageAction>(expected), NavigationLock.AfterNavigation(url, s_hosts));
    }

    [Fact]
    public async Task Request_that_can_no_longer_be_answered_does_not_throw()
    {
        var decision = await NavigationLock.DecideAsync(Paused("https://evil.example/"), s_hosts, _ => Task.FromResult(true),
            (_, _) => throw new InvalidOperationException("The request went away"));

        Assert.Equal(PausedRequestOutcome.FailedClosed, decision.Outcome);
    }
}
