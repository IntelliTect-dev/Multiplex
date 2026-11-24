using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Tests.Auth;
using Multiplex.Tests.Services.TripIt;
using Multiplex.Tests.SignIn;
using Multiplex.Web.Auth;
using Multiplex.Web.Auth.Store;
using Multiplex.Web.Identity;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.Identity;

/// <summary>
/// Every grant and every saved site session belongs to the person who made it (#80, D4), and what
/// belongs to no one is refused and removed (D5).
/// </summary>
public sealed class OwnershipTests
{
    [Fact]
    public async Task Two_people_connected_to_the_same_site_account_each_use_only_their_own_session()
    {
        using var host = new TripItTestHost();
        host.SignIn("shared-acct", "cookie-of-owner", TestPeople.Owner);
        host.SignIn("shared-acct", "cookie-of-friend", TestPeople.Friend);

        var owners = await host.McpFor("shared-acct", TestPeople.Owner).CallAsync("tripit_get_profile");
        var friends = await host.McpFor("shared-acct", TestPeople.Friend).CallAsync("tripit_get_profile");

        Assert.False(owners.IsError, owners.Text);
        Assert.False(friends.IsError, friends.Text);
        Assert.Contains("cookie-of-owner", owners.Text);
        Assert.DoesNotContain("cookie-of-friend", owners.Text);
        Assert.Contains("cookie-of-friend", friends.Text);
        Assert.DoesNotContain("cookie-of-owner", friends.Text);
    }

    [Fact]
    public async Task A_person_with_no_session_of_their_own_never_gets_someone_elses()
    {
        using var host = new TripItTestHost();
        host.SignIn("shared-acct", "cookie-of-owner", TestPeople.Owner);

        var friends = await host.McpFor("shared-acct", TestPeople.Friend).CallAsync("tripit_get_profile");

        Assert.True(friends.IsError);
        Assert.DoesNotContain("cookie-of-owner", friends.Text);
        Assert.All(host.TripIt.Requests, r => Assert.NotEqual("cookie-of-owner", r.Cookie));
    }

    [Fact]
    public async Task A_token_carries_its_person_and_stops_working_when_they_leave_the_list()
    {
        using var app = new AuthTestApp();
        var driver = new OAuthDriver(app);
        var tokens = await driver.ConnectAsync();
        var endpoint = app.Services.GetRequiredService<AuthenticatedEndpoints>().FindBySlug("tripit")!;
        var tokenService = app.Services.GetRequiredService<TokenService>();

        var principal = await tokenService.ValidateAsync(tokens.AccessToken, endpoint, CancellationToken.None);
        Assert.Equal(TestPeople.OwnerOf(), SiteClaims.AccountOf(principal)!.Value.Owner);

        app.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()[AllowedPeople.Key] = TestPeople.Friend;

        Assert.Null(await tokenService.ValidateAsync(tokens.AccessToken, endpoint, CancellationToken.None));
        var (status, body, _) = await driver.TokenAsync(OAuthDriver.Refresh(tokens.ClientId, tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Only_the_person_who_started_connecting_can_finish_it()
    {
        using var app = new AuthTestApp();
        var owner = new OAuthDriver(app);
        var pending = await owner.StartAsync(OAuthDriver.AuthorizeParameters(await owner.RegisterAsync(), OAuthDriver.NewPkce()));
        var friend = (await TestPeople.SignInAsync(app.Services, TestPeople.Friend)).Person;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            owner.Completer.CompleteAsync(pending, friend.Owner, OAuthDriver.Account(), CancellationToken.None));

        var back = await owner.Completer.CompleteAsync(pending, await owner.OwnerAsync(), OAuthDriver.Account(), CancellationToken.None);
        Assert.NotNull(OAuthDriver.Query(back)["code"]);
    }

    [Fact]
    public async Task Revoking_a_persons_site_account_leaves_another_persons_grant_for_it_alone()
    {
        using var app = new AuthTestApp();
        var ownerTokens = await new OAuthDriver(app, TestPeople.Owner).ConnectAsync(OAuthDriver.Account("shared"));
        var friendTokens = await new OAuthDriver(app, TestPeople.Friend).ConnectAsync(OAuthDriver.Account("shared"));
        var endpoint = app.Services.GetRequiredService<AuthenticatedEndpoints>().FindBySlug("tripit")!;
        var tokenService = app.Services.GetRequiredService<TokenService>();

        await app.Services.GetRequiredService<IGrantRevoker>().RevokeAccountAsync(TestPeople.OwnerOf(TestPeople.Owner), "tripit", "shared", CancellationToken.None);

        Assert.Null(await tokenService.ValidateAsync(ownerTokens.AccessToken, endpoint, CancellationToken.None));
        Assert.NotNull(await tokenService.ValidateAsync(friendTokens.AccessToken, endpoint, CancellationToken.None));
    }

    [Fact]
    public async Task Grants_saved_before_people_signed_in_are_dropped_when_the_store_loads()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "multiplex-tests", Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        using (var before = new FileStoreAuthTestApp(dataPath))
        {
            await before.Services.GetRequiredService<IAuthStore>().WriteAsync(s =>
            {
                s.Grants["old"] = new GrantRecord("old", "client", "tripit", "acct", AuthTestApp.TripItResource, "tripit", now);
                s.AccessTokens["old-token"] = new AccessTokenRecord("old", "client", "tripit", "acct", AuthTestApp.TripItResource, "tripit", now.AddHours(1));
                s.RefreshTokens["old-refresh"] = new RefreshTokenRecord("old", "client", now.AddDays(30));
                return StoreWrite.Changed(0);
            }, CancellationToken.None);
        }

        using var after = new FileStoreAuthTestApp(dataPath);
        var left = await after.Services.GetRequiredService<IAuthStore>().ReadAsync(
            s => (s.Grants.Count, s.AccessTokens.Count, s.RefreshTokens.Count), CancellationToken.None);

        Assert.Equal((0, 0, 0), left);
    }

    [Fact]
    public async Task Amazon_connects_with_a_consent_page_and_its_token_works_from_the_internet()
    {
        using var app = new AuthTestApp();
        var driver = new OAuthDriver(app);
        var clientId = await driver.RegisterAsync();
        var pkce = OAuthDriver.NewPkce();
        var resource = AuthTestApp.BaseUrl + "/amazon/mcp";
        var pending = await driver.StartAsync(OAuthDriver.AuthorizeParameters(clientId, pkce, resource: resource), "amazon");

        // The person's browser: their sign-in cookie, and whatever cookies the pages set (antiforgery).
        var jar = new CookieContainer();
        jar.SetCookies(new Uri("https://localhost"), TestPeople.CookieFor(app.Services, (await TestPeople.SignInAsync(app.Services)).Person));
        var browser = app.CreateDefaultClient(new Uri("https://localhost"), new Microsoft.AspNetCore.Mvc.Testing.Handlers.CookieContainerHandler(jar));

        var page = await browser.GetAsync($"/connect/amazon?p={pending}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Connect Claude to Amazon", html);
        Assert.Contains(TestPeople.Owner, html);
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var allowed = await browser.PostAsync($"/connect/amazon/allow?p={pending}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token) }));

        Assert.Equal(HttpStatusCode.SeeOther, allowed.StatusCode);
        var code = QueryHelpers.ParseQuery(allowed.Headers.Location!.Query)["code"].ToString();
        var exchange = OAuthDriver.CodeExchange(clientId, code, pkce);
        exchange["resource"] = resource;
        var (status, body, _) = await driver.TokenAsync(exchange);
        Assert.Equal(HttpStatusCode.OK, status);

        using var call = new HttpRequestMessage(HttpMethod.Post, "/amazon/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
                Encoding.UTF8, "application/json"),
        };
        call.Headers.Accept.ParseAdd("application/json");
        call.Headers.Accept.ParseAdd("text/event-stream");
        call.Headers.Add("X-Forwarded-For", "203.0.113.7");
        call.Headers.Authorization = new("Bearer", body.GetProperty("access_token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await app.Client().SendAsync(call)).StatusCode);
    }

    [Fact]
    public async Task A_service_that_acts_on_a_site_account_cannot_be_connected_without_one()
    {
        using var app = new AuthTestApp();
        var driver = new OAuthDriver(app);
        var pending = await driver.StartAsync(OAuthDriver.AuthorizeParameters(await driver.RegisterAsync(), OAuthDriver.NewPkce()));
        var owner = await driver.OwnerAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            driver.Completer.CompleteAsync(pending, owner, account: null, CancellationToken.None));
    }

    [Fact]
    public async Task Site_sessions_saved_before_they_belonged_to_a_person_are_removed_at_start()
    {
        await using var host = new SignInTestHost();
        var unowned = Path.Combine(host.DataPath, "sites", "tripit");
        Directory.CreateDirectory(Path.Combine(unowned, "sessions"));
        Directory.CreateDirectory(Path.Combine(unowned, "profiles", "acct-1", "Default"));
        await File.WriteAllTextAsync(Path.Combine(unowned, "sessions", "x.bin"), "old session");
        await File.WriteAllTextAsync(Path.Combine(unowned, "accounts.bin"), "old list");

        _ = host.Services; // starts the server

        Assert.False(Directory.Exists(Path.Combine(host.DataPath, "sites")));
    }

    [Fact]
    public async Task A_session_file_copied_into_another_persons_folder_does_not_open()
    {
        await using var host = new SignInTestHost();
        var store = host.Get<SiteSessionStore>();
        var paths = host.Get<SignInPaths>();
        var owner = TestPeople.OwnerOf(TestPeople.Owner);
        var friend = TestPeople.OwnerOf(TestPeople.Friend);
        await store.SetAsync(owner, "stub", "acct-1", """{"cookie":"owners"}""", CancellationToken.None);

        var copy = paths.SessionFile(friend, "stub", "acct-1");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(paths.SessionFile(owner, "stub", "acct-1"), copy);

        Assert.Equal("""{"cookie":"owners"}""", await store.GetAsync(owner, "stub", "acct-1", CancellationToken.None));
        Assert.Null(await store.GetAsync(friend, "stub", "acct-1", CancellationToken.None));
    }

    [Fact]
    public void A_person_id_is_always_a_safe_folder_name()
    {
        var owner = PersonAdmission.OwnerFor(MicrosoftAccounts.Provider, "../../etc/passwd");

        Assert.Matches("^u[0-9a-f]{32}$", owner);
        Assert.Throws<ArgumentException>(() => SignInPaths.CheckOwner("../x"));
        Assert.Throws<ArgumentException>(() => SignInPaths.CheckOwner("U" + new string('a', 32)));
    }
}
