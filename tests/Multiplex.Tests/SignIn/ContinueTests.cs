using System.Net;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.SignIn;

/// <summary>
/// "Continue as …" for accounts the server already holds a session for. The refresher is
/// faked, and no browser can start (a fall-back live sign-in fails at launch, unobserved).
/// </summary>
public sealed class ContinueTests : IAsyncLifetime
{
    private static readonly SiteAccount s_account =
        new(StubSignInDefinition.SiteSlug, StubLoginSite.AccountId, [StubLoginSite.Email], StubLoginSite.DisplayName);

    private readonly FakeRefresher _refresher = new();
    private SignInTestHost _host = null!;

    public Task InitializeAsync() => StartAsync([StubLoginSite.Email]);

    private async Task StartAsync(string[] allowed)
    {
        if (_host is not null) await _host.DisposeAsync();
        _host = new SignInTestHost(new SignInTestOptions
        {
            Refresher = _refresher,
            AllowedAccounts = allowed,
            ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chrome.exe"),
        });
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>What a finished earlier sign-in leaves behind: index entry, stored session, kept profile.</summary>
    private async Task SeedKnownAccountAsync()
    {
        await _host.Get<SiteAccountIndex>().UpsertAsync(s_account, CancellationToken.None);
        await _host.Get<SiteSessionStore>().SetAsync(s_account.Site, s_account.AccountId, """{"cookie":"old"}""", CancellationToken.None);
        Directory.CreateDirectory(_host.Get<SignInPaths>().AccountProfile(s_account.Site, s_account.AccountId));
    }

    [Fact]
    public async Task Known_account_is_offered_after_the_gate_and_continue_completes_the_authorization()
    {
        await SeedKnownAccountAsync();
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();

        var (gate, _) = await client.PassGateAsync("p1");
        Assert.Equal(HttpStatusCode.SeeOther, gate.StatusCode);
        Assert.Equal("/connect/stub?p=p1", gate.Headers.Location!.OriginalString);
        var (_, choose) = await client.GetAsync(gate.Headers.Location!.OriginalString);
        Assert.Contains($"Continue as {StubLoginSite.DisplayName}", choose);
        Assert.Contains("Sign in with a different account", choose);

        var (done, _) = await client.PostAsync("/connect/stub/continue?p=p1", choose, ("account", StubLoginSite.AccountId));

        Assert.Equal(HttpStatusCode.SeeOther, done.StatusCode);
        Assert.Equal("https://client.example.test/callback?code=code-for-p1&state=s1", done.Headers.Location!.AbsoluteUri);
        var completed = Assert.Single(_host.Completer.Completed);
        Assert.Equal("p1", completed.PendingId);
        Assert.Equal(StubLoginSite.AccountId, completed.Account.AccountId);
        Assert.Equal([("stub", StubLoginSite.AccountId)], _refresher.Calls);
        Assert.Empty(_host.TempProfiles());
    }

    [Fact]
    public async Task Signed_out_account_falls_back_to_a_live_sign_in()
    {
        await SeedKnownAccountAsync();
        _refresher.Result = null; // the refresher found the person signed out
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();
        var (_, choose) = await client.GetAsync((await client.PassGateAsync("p1")).Response.Headers.Location!.OriginalString);

        var (response, _) = await client.PostAsync("/connect/stub/continue?p=p1", choose, ("account", StubLoginSite.AccountId));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var live = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/connect/stub/live?a=", live);
        Assert.Contains("n=signed-out", live);
        Assert.Empty(_host.Completer.Completed);
        Assert.NotNull(_host.Get<LiveSignInRegistry>().FindForPending("p1"));
        var (page, html) = await client.GetAsync(live);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("session has ended", html);
    }

    [Fact]
    public async Task Account_removed_from_the_allowlist_is_neither_offered_nor_accepted()
    {
        await StartAsync(["someone-else@example.test"]);
        await SeedKnownAccountAsync();
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();

        var (gate, _) = await client.PassGateAsync("p1");
        // No account to offer: the gate goes straight to a live sign-in.
        Assert.StartsWith("/connect/stub/live?a=", gate.Headers.Location!.OriginalString);
        var (_, choose) = await client.GetAsync("/connect/stub?p=p1");
        Assert.DoesNotContain("Continue as", choose);

        var (forced, html) = await client.PostAsync("/connect/stub/continue?p=p1", choose, ("account", StubLoginSite.AccountId));

        Assert.Equal(HttpStatusCode.Forbidden, forced.StatusCode);
        Assert.Contains("not allowed", html);
        Assert.Empty(_refresher.Calls);
        Assert.Empty(_host.Completer.Completed);
    }

    [Fact]
    public async Task Continue_needs_the_gate()
    {
        await SeedKnownAccountAsync();
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();
        var (_, gatePage) = await client.GetAsync("/connect/stub?p=p1");

        var (response, _) = await client.PostAsync("/connect/stub/continue?p=p1", gatePage, ("account", StubLoginSite.AccountId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_refresher.Calls);
        Assert.Empty(_host.Completer.Completed);
    }

    [Fact]
    public async Task Cancel_before_signing_in_fails_the_authorization_and_returns_to_the_client()
    {
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();
        var (_, gatePage) = await client.GetAsync("/connect/stub?p=p1");

        var (response, _) = await client.PostAsync("/connect/stub/cancel?p=p1", gatePage);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Contains("error=access_denied", response.Headers.Location!.AbsoluteUri);
        var failed = Assert.Single(_host.Completer.Failed);
        Assert.Equal(("p1", "access_denied", "Sign-in cancelled"), failed);
    }
}
