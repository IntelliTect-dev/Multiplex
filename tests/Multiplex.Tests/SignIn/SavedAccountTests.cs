using System.Net;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.SignIn;

/// <summary>
/// The sessions the server holds are never a shortcut into a new connection: the passcode only
/// reaches the connect page, and every connection proves its account by signing in live. The
/// refresher is faked, and no browser can start (a live sign-in fails at launch, unobserved).
/// </summary>
public sealed class SavedAccountTests : IAsyncLifetime
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
    private async Task SeedSavedAccountAsync(SiteAccount account)
    {
        await _host.Get<SiteAccountIndex>().UpsertAsync(account, CancellationToken.None);
        await _host.Get<SiteSessionStore>().SetAsync(account.Site, account.AccountId, """{"cookie":"old"}""", CancellationToken.None);
        Directory.CreateDirectory(_host.Get<SignInPaths>().AccountProfile(account.Site, account.AccountId));
    }

    [Fact]
    public async Task No_saved_account_is_offered_after_the_gate()
    {
        await SeedSavedAccountAsync(s_account);
        await SeedSavedAccountAsync(s_otherAccount);
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();

        var (gate, _) = await client.PassGateAsync("p1");
        var (_, page) = await client.GetAsync("/connect/stub?p=p1");

        // The passcode leads straight to a live sign-in; nothing the server holds is on offer.
        Assert.Equal(HttpStatusCode.SeeOther, gate.StatusCode);
        Assert.StartsWith("/connect/stub/live?a=", gate.Headers.Location!.OriginalString);
        Assert.Contains("Sign in to Stub Site", page);
        foreach (var held in new[] { s_account, s_otherAccount })
        {
            Assert.DoesNotContain(held.AccountId, page);
            Assert.DoesNotContain(held.DisplayName, page);
            Assert.DoesNotContain(held.Emails[0], page);
        }
        Assert.DoesNotContain("name=\"account\"", page);
    }

    [Fact]
    public async Task A_posted_account_id_never_binds_a_connection_to_a_saved_account()
    {
        // Someone else's account has a saved session on this server; a passcode holder names it.
        await SeedSavedAccountAsync(s_otherAccount);
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();
        await client.PassGateAsync("p1");
        var (_, page) = await client.GetAsync("/connect/stub?p=p1");

        var (continued, _) = await client.PostAsync("/connect/stub/continue?p=p1", page, ("account", s_otherAccount.AccountId));
        var (started, _) = await client.PostAsync("/connect/stub/start?p=p1", page, ("account", s_otherAccount.AccountId));

        // There is no endpoint that takes an account: the old "Continue as" post goes nowhere.
        Assert.False(continued.IsSuccessStatusCode);
        Assert.Null(continued.Headers.Location);
        Assert.StartsWith("/connect/stub/live?a=", started.Headers.Location!.OriginalString);
        // No completion means no authorization code, so no token: the connection is bound to nothing yet.
        Assert.Empty(_host.Completer.Completed);
        Assert.NotNull(await _host.Completer.FindAsync("p1", CancellationToken.None));
        Assert.Empty(_refresher.Calls);
        Assert.Equal("""{"cookie":"old"}""",
            await _host.Get<ISiteSessionStore>().GetAsync("stub", s_otherAccount.AccountId, CancellationToken.None));
    }

    [Fact]
    public async Task An_old_account_list_in_configuration_is_ignored_with_one_log_line()
    {
        // Left by an earlier install: it names someone else, and must neither stop the server nor stop a sign-in.
        await StartAsync(new() { [$"Multiplex:Sites:{StubSignInDefinition.SiteSlug}:AllowedAccounts:0"] = "someone-else@example.test" });
        _host.Completer.Add("p1", "stub");

        var (gate, _) = await _host.NewClient().PassGateAsync("p1");

        Assert.Equal(HttpStatusCode.SeeOther, gate.StatusCode);
        Assert.StartsWith("/connect/stub/live?a=", gate.Headers.Location!.OriginalString);
        var notice = Assert.Single(_host.Logs.Entries, e => e.AllText.Contains("AllowedAccounts", StringComparison.Ordinal));
        Assert.Contains("no longer used", notice.Message);
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
