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

    private static readonly SiteAccount s_otherAccount =
        new(StubSignInDefinition.SiteSlug, StubLoginSite.OtherAccountId, [StubLoginSite.OtherEmail], StubLoginSite.OtherDisplayName);

    private readonly FakeRefresher _refresher = new();
    private SignInTestHost _host = null!;

    public Task InitializeAsync() => StartAsync();

    private async Task StartAsync(Dictionary<string, string>? settings = null)
    {
        if (_host is not null) await _host.DisposeAsync();
        var options = new SignInTestOptions
        {
            Refresher = _refresher,
            ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chrome.exe"),
        };
        foreach (var (key, value) in settings ?? []) options.Settings[key] = value;
        _host = new SignInTestHost(options);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>What a finished earlier sign-in leaves behind: index entry, stored session, kept profile.</summary>
    private async Task SeedKnownAccountAsync(SiteAccount? known = null)
    {
        var account = known ?? s_account;
        await _host.Get<SiteAccountIndex>().UpsertAsync(account, CancellationToken.None);
        await _host.Get<SiteSessionStore>().SetAsync(account.Site, account.AccountId, """{"cookie":"old"}""", CancellationToken.None);
        Directory.CreateDirectory(_host.Get<SignInPaths>().AccountProfile(account.Site, account.AccountId));
    }

    /// <summary>One connection being added: the passcode, then "Continue as" <paramref name="accountId"/>.</summary>
    private async Task<HttpResponseMessage> ContinueAsAsync(string pendingId, string accountId)
    {
        var client = _host.NewClient();
        var (gate, _) = await client.PassGateAsync(pendingId);
        var (_, choose) = await client.GetAsync(gate.Headers.Location!.OriginalString);
        var (done, _) = await client.PostAsync($"/connect/stub/continue?p={pendingId}", choose, ("account", accountId));
        return done;
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
    public async Task Two_connections_continue_as_different_accounts_and_each_is_bound_to_its_own()
    {
        // No account list is configured: the passcode is the gate, and each connection gets the account chosen for it.
        await SeedKnownAccountAsync(s_account);
        await SeedKnownAccountAsync(s_otherAccount);
        _host.Completer.Add("p1", "stub");
        _host.Completer.Add("p2", "stub");

        var first = await ContinueAsAsync("p1", s_account.AccountId);
        var second = await ContinueAsAsync("p2", s_otherAccount.AccountId);

        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        Assert.Equal(HttpStatusCode.SeeOther, second.StatusCode);
        Assert.Equal(
            [("p1", s_account.AccountId), ("p2", s_otherAccount.AccountId)],
            _host.Completer.Completed.Select(c => (c.PendingId, c.Account.AccountId)));
    }

    [Fact]
    public async Task Every_saved_account_is_offered_after_the_gate()
    {
        await SeedKnownAccountAsync(s_account);
        await SeedKnownAccountAsync(s_otherAccount);
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();

        var (gate, _) = await client.PassGateAsync("p1");
        var (_, choose) = await client.GetAsync(gate.Headers.Location!.OriginalString);

        Assert.Contains($"Continue as {StubLoginSite.DisplayName}", choose);
        Assert.Contains($"Continue as {StubLoginSite.OtherDisplayName}", choose);
    }

    [Fact]
    public async Task An_old_account_list_in_configuration_is_ignored_with_one_log_line()
    {
        // Left by an earlier install: it names someone else, and must neither stop the server nor limit who connects.
        await StartAsync(new() { [$"Multiplex:Sites:{StubSignInDefinition.SiteSlug}:AllowedAccounts:0"] = "someone-else@example.test" });
        await SeedKnownAccountAsync();
        _host.Completer.Add("p1", "stub");

        var response = await ContinueAsAsync("p1", StubLoginSite.AccountId);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(StubLoginSite.AccountId, Assert.Single(_host.Completer.Completed).Account.AccountId);
        var notice = Assert.Single(_host.Logs.Entries, e => e.AllText.Contains("AllowedAccounts", StringComparison.Ordinal));
        Assert.Contains("no longer used", notice.Message);
    }

    [Fact]
    public async Task Continue_as_an_account_the_server_holds_no_session_for_is_refused()
    {
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();
        await client.PassGateAsync("p1"); // nothing to offer: this starts a live sign-in, which cannot launch here
        var (_, choose) = await client.GetAsync("/connect/stub?p=p1");

        var (forced, _) = await client.PostAsync("/connect/stub/continue?p=p1", choose, ("account", StubLoginSite.AccountId));

        Assert.Equal(HttpStatusCode.Forbidden, forced.StatusCode);
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
