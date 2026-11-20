using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Multiplex.Tests.Auth;

namespace Multiplex.Tests.Identity;

/// <summary>
/// Signing in to Multiplex with Microsoft, end to end through the app's real OpenID Connect handler
/// against <see cref="FakeMicrosoft"/>: Claude starts connecting, the person is sent to Microsoft from
/// inside <c>/oauth/authorize</c>, comes back, and is let in only with a verified, allowlisted email.
/// </summary>
public sealed class MicrosoftSignInTests : IDisposable
{
    private readonly MicrosoftTestApp _app = new();

    public void Dispose() => _app.Dispose();

    /// <summary>Registers a client and returns an authorize URL for TripIt, as Claude would build it.</summary>
    private async Task<string> AuthorizeUrlAsync()
    {
        var driver = new OAuthDriver(_app);
        var clientId = await driver.RegisterAsync();
        return QueryHelpers.AddQueryString("/oauth/authorize",
            OAuthDriver.AuthorizeParameters(clientId, OAuthDriver.NewPkce()).Where(p => p.Value is not null));
    }

    /// <summary>Starts connecting in <paramref name="browser"/>; returns Microsoft's sign-in request.</summary>
    private static async Task<(Uri SignIn, Dictionary<string, string> Query)> ChallengeAsync(HttpClient browser, string authorize)
    {
        var response = await browser.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var signIn = response.Headers.Location!;
        return (signIn, QueryHelpers.ParseQuery(signIn.Query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString()));
    }

    /// <summary>Microsoft sends the browser back with a code, as after the person signed in there.</summary>
    private static Task<HttpResponseMessage> ReturnFromMicrosoftAsync(HttpClient browser, Dictionary<string, string> signIn) =>
        browser.GetAsync($"/signin-microsoft?code=microsoft-code&state={Uri.EscapeDataString(signIn["state"])}");

    [Fact]
    public async Task Connecting_without_a_sign_in_sends_the_person_to_Microsoft_from_authorize()
    {
        var (signIn, query) = await ChallengeAsync(_app.Browser(), await AuthorizeUrlAsync());

        Assert.StartsWith(FakeMicrosoft.AuthorizationEndpoint, signIn.AbsoluteUri);
        Assert.Equal(FakeMicrosoft.ClientId, query["client_id"]);
        // From the configured public address, never the request's own host or scheme.
        Assert.Equal(AuthTestApp.BaseUrl + "/signin-microsoft", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        // Back as a top-level GET (query is the code flow's default), so the Lax correlation cookie comes along.
        Assert.True(!query.TryGetValue("response_mode", out var mode) || mode == "query", $"response_mode={mode}");
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("select_account", query["prompt"]);
        Assert.Contains("email", query["scope"].Split(' '));
    }

    [Fact]
    public async Task An_allowlisted_verified_email_is_signed_in_and_goes_on_to_the_connect_page()
    {
        var browser = _app.Browser();
        var authorize = await AuthorizeUrlAsync();
        var (_, signIn) = await ChallengeAsync(browser, authorize);
        _app.Microsoft.NextIdToken = _app.Microsoft.IdToken(signIn["nonce"], TestPeople.Owner, verified: true);

        var back = await ReturnFromMicrosoftAsync(browser, signIn);

        Assert.True(back.StatusCode == HttpStatusCode.Redirect,
            $"HTTP {(int)back.StatusCode}; log:\n{string.Join("\n", _app.Logs.Entries.Where(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Information && !e.Category.StartsWith("Microsoft.AspNetCore.Hosting")).Select(e => $"{e.Level} {e.Category}: {e.Message}"))}");
        Assert.Equal(authorize, back.Headers.Location!.OriginalString);
        var redemption = Assert.Single(_app.Microsoft.Redemptions);
        Assert.Equal(AuthTestApp.BaseUrl + "/signin-microsoft", redemption["redirect_uri"]);
        Assert.False(string.IsNullOrEmpty(redemption["code_verifier"]));

        // Signed in now: authorize goes on to the connect page instead of to Microsoft.
        var again = await browser.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.StartsWith("/connect/tripit?p=", again.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_personal_Microsoft_account_signs_in_the_same_way()
    {
        var browser = _app.Browser();
        var authorize = await AuthorizeUrlAsync();
        var (_, signIn) = await ChallengeAsync(browser, authorize);
        _app.Microsoft.NextIdToken = _app.Microsoft.IdToken(signIn["nonce"], TestPeople.Owner, verified: true, tenant: FakeMicrosoft.PersonalTenant);

        var back = await ReturnFromMicrosoftAsync(browser, signIn);

        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.StartsWith("/connect/tripit?p=", (await browser.GetAsync(authorize)).Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task An_email_Microsoft_does_not_vouch_for_is_refused_even_on_the_list(bool? verified)
    {
        var browser = _app.Browser();
        var authorize = await AuthorizeUrlAsync();
        var (_, signIn) = await ChallengeAsync(browser, authorize);
        _app.Microsoft.NextIdToken = _app.Microsoft.IdToken(signIn["nonce"], TestPeople.Owner, verified);

        var back = await ReturnFromMicrosoftAsync(browser, signIn);

        Assert.Equal(HttpStatusCode.Forbidden, back.StatusCode);
        Assert.Contains("did not confirm", await back.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync(authorize)).StatusCode);
        Assert.StartsWith(FakeMicrosoft.AuthorizationEndpoint, (await browser.GetAsync(authorize)).Headers.Location!.AbsoluteUri);
    }

    [Fact]
    public async Task An_email_not_on_the_list_is_refused()
    {
        var browser = _app.Browser();
        var authorize = await AuthorizeUrlAsync();
        var (_, signIn) = await ChallengeAsync(browser, authorize);
        _app.Microsoft.NextIdToken = _app.Microsoft.IdToken(signIn["nonce"], "stranger@example.test", verified: true);

        var back = await ReturnFromMicrosoftAsync(browser, signIn);

        Assert.Equal(HttpStatusCode.Forbidden, back.StatusCode);
        var html = await back.Content.ReadAsStringAsync();
        Assert.Contains("not allowed", html);
        Assert.DoesNotContain(TestPeople.Owner, html);
    }

    [Fact]
    public async Task A_token_whose_issuer_is_not_its_own_tenant_is_refused()
    {
        var browser = _app.Browser();
        var authorize = await AuthorizeUrlAsync();
        var (_, signIn) = await ChallengeAsync(browser, authorize);
        _app.Microsoft.NextIdToken = _app.Microsoft.IdToken(signIn["nonce"], TestPeople.Owner, verified: true,
            issuerTenant: "11111111-1111-1111-1111-111111111111");

        var back = await ReturnFromMicrosoftAsync(browser, signIn);

        Assert.Equal(HttpStatusCode.BadRequest, back.StatusCode);
        Assert.StartsWith(FakeMicrosoft.AuthorizationEndpoint, (await browser.GetAsync(authorize)).Headers.Location!.AbsoluteUri);
    }

    [Fact]
    public async Task Once_bound_the_address_admits_only_the_account_that_first_signed_in_with_it()
    {
        var first = _app.Browser();
        var authorize = await AuthorizeUrlAsync();
        var (_, signIn) = await ChallengeAsync(first, authorize);
        _app.Microsoft.NextIdToken = _app.Microsoft.IdToken(signIn["nonce"], TestPeople.Owner, verified: true);
        Assert.Equal(HttpStatusCode.Redirect, (await ReturnFromMicrosoftAsync(first, signIn)).StatusCode);

        // The same address, verified, but from a different Microsoft account (it changed hands).
        var other = _app.Browser();
        var (_, otherSignIn) = await ChallengeAsync(other, authorize);
        _app.Microsoft.NextIdToken = _app.Microsoft.IdToken(otherSignIn["nonce"], TestPeople.Owner, verified: true,
            objectId: "00000000-0000-0000-0000-00000000beef");

        Assert.Equal(HttpStatusCode.Forbidden, (await ReturnFromMicrosoftAsync(other, otherSignIn)).StatusCode);
    }

    [Fact]
    public async Task Without_the_Microsoft_app_registration_nothing_outside_this_PC_can_sign_in()
    {
        using var app = new AuthTestApp();
        var driver = new OAuthDriver(app);
        var clientId = await driver.RegisterAsync();
        var browser = app.Client();

        var response = await browser.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize",
            OAuthDriver.AuthorizeParameters(clientId, OAuthDriver.NewPkce()).Where(p => p.Value is not null)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Sign-in is not set up", await response.Content.ReadAsStringAsync());
    }
}
