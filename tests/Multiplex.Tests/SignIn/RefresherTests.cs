using Multiplex.Tests.Identity;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;
using PuppeteerSharp;

namespace Multiplex.Tests.SignIn;

/// <summary>The real refresher against a real Chromium and the stub site; every session and profile belongs to a person.</summary>
[Collection(SignInBrowserCollection.Name)]
public sealed class RefresherTests
{
    /// <summary>The person the sessions and profiles belong to.</summary>
    private static readonly string Owner = TestPeople.OwnerOf();

    private static readonly SiteAccount s_account =
        new(StubSignInDefinition.SiteSlug, StubLoginSite.AccountId, [StubLoginSite.Email], StubLoginSite.DisplayName);

    [ChromiumFact]
    public async Task Signed_out_profile_removes_the_session_and_revokes_the_account()
    {
        await using var site = await StubLoginSite.StartAsync();
        await using var host = new SignInTestHost(new SignInTestOptions { StubBaseUrl = site.BaseUrl });
        // A kept profile that holds no session (as after TripIt signed the person out).
        var profile = host.Get<SignInPaths>().AccountProfile(Owner, "stub", s_account.AccountId);
        Directory.CreateDirectory(profile);
        await host.Get<SiteSessionStore>().SetAsync(Owner, "stub", s_account.AccountId, """{"cookie":"stale"}""", CancellationToken.None);
        await host.Get<SiteAccountIndex>().UpsertAsync(Owner, s_account, CancellationToken.None);

        var result = await host.Get<ISiteSessionRefresher>().RefreshAsync(Owner, "stub", s_account.AccountId, CancellationToken.None);

        Assert.Null(result);
        Assert.Null(await host.Get<ISiteSessionStore>().GetAsync(Owner, "stub", s_account.AccountId, CancellationToken.None));
        Assert.Empty(await host.Get<SiteAccountIndex>().ListAsync(Owner, "stub", CancellationToken.None));
        Assert.Equal([(Owner, "stub", s_account.AccountId)], host.Revoker.Revoked);
        Assert.False(Directory.Exists(profile)); // its browser profile goes too, once Chrome has let go of it

        // Within the cool-down a second call does not relaunch anything and still reports signed out.
        Assert.Null(await host.Get<ISiteSessionRefresher>().RefreshAsync(Owner, "stub", s_account.AccountId, CancellationToken.None));
        Assert.Single(host.Revoker.Revoked);
    }

    [ChromiumFact]
    public async Task Profile_now_signed_in_as_another_account_is_a_sign_out_and_is_deleted()
    {
        await using var site = await StubLoginSite.StartAsync();
        await using var host = new SignInTestHost(new SignInTestOptions { StubBaseUrl = site.BaseUrl });
        // The kept profile of "earlier-account" is, by now, signed in as the stub's own account.
        const string earlier = "earlier-account";
        var profile = host.Get<SignInPaths>().AccountProfile(Owner, "stub", earlier);
        await SignInDirectlyAsync(profile, site);
        await host.Get<SiteSessionStore>().SetAsync(Owner, "stub", earlier, """{"cookie":"old"}""", CancellationToken.None);

        var result = await host.Get<ISiteSessionRefresher>().RefreshAsync(Owner, "stub", earlier, CancellationToken.None);

        Assert.Null(result);
        Assert.Null(await host.Get<ISiteSessionStore>().GetAsync(Owner, "stub", earlier, CancellationToken.None));
        Assert.Equal([(Owner, "stub", earlier)], host.Revoker.Revoked);
        Assert.False(Directory.Exists(profile)); // it holds another account's session
    }

    [ChromiumFact]
    public async Task Unreachable_site_is_a_refresh_failure_not_a_sign_out()
    {
        // The definition points at a port nobody listens on: neither signed in nor signed out.
        await using var host = new SignInTestHost();
        var profile = host.Get<SignInPaths>().AccountProfile(Owner, "stub", s_account.AccountId);
        Directory.CreateDirectory(profile);
        await host.Get<SiteSessionStore>().SetAsync(Owner, "stub", s_account.AccountId, """{"cookie":"kept"}""", CancellationToken.None);

        await Assert.ThrowsAsync<SiteRefreshException>(() =>
            host.Get<ISiteSessionRefresher>().RefreshAsync(Owner, "stub", s_account.AccountId, CancellationToken.None));

        Assert.Equal("""{"cookie":"kept"}""", await host.Get<ISiteSessionStore>().GetAsync(Owner, "stub", s_account.AccountId, CancellationToken.None));
        Assert.Empty(host.Revoker.Revoked);
        Assert.True(Directory.Exists(profile));
    }

    [Fact]
    public async Task Refresh_without_a_kept_profile_fails_without_touching_anything()
    {
        await using var host = new SignInTestHost(new SignInTestOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chrome.exe") });
        await host.Get<SiteSessionStore>().SetAsync(Owner, "stub", s_account.AccountId, "x", CancellationToken.None);

        await Assert.ThrowsAsync<SiteRefreshException>(() =>
            host.Get<ISiteSessionRefresher>().RefreshAsync(Owner, "stub", s_account.AccountId, CancellationToken.None));

        Assert.Equal("x", await host.Get<ISiteSessionStore>().GetAsync(Owner, "stub", s_account.AccountId, CancellationToken.None));
        Assert.Empty(host.Revoker.Revoked);
    }

    private static Task<IBrowser> LaunchAsync(string profile) => Puppeteer.LaunchAsync(new LaunchOptions
    {
        HeadlessMode = HeadlessMode.True,
        ExecutablePath = ChromiumLocator.Path,
        UserDataDir = profile,
        Args = ["--no-first-run", "--no-default-browser-check"],
    });

    private static async Task SignInDirectlyAsync(string profile, StubLoginSite site)
    {
        await using var browser = await LaunchAsync(profile);
        var page = (await browser.PagesAsync()).First();
        await page.GoToAsync(new Uri(site.BaseUrl, "/login").AbsoluteUri);
        await page.TypeAsync("#email", StubLoginSite.Email);
        await page.TypeAsync("#password", StubLoginSite.Password);
        await Task.WhenAll(page.WaitForNavigationAsync(), page.ClickAsync("#submit"));
        Assert.EndsWith("/app", new Uri(page.Url).AbsolutePath);
        await browser.CloseAsync();
    }
}
