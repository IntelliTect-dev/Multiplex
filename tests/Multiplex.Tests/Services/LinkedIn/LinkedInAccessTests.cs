using System.Net;
using Multiplex.Tests.Auth;

namespace Multiplex.Tests.Services.LinkedIn;

/// <summary>
/// The LinkedIn service sends invitations and messages as a person, so its address must refuse
/// every request that does not carry a token issued for it, whatever tools exist behind it.
/// </summary>
public sealed class LinkedInAccessTests : IClassFixture<AuthTestApp>
{
    private const string LinkedInResource = AuthTestApp.BaseUrl + "/linkedin/mcp";
    private readonly AuthTestApp _app;

    public LinkedInAccessTests(AuthTestApp app) => _app = app;

    [Fact]
    public async Task A_request_without_a_token_is_challenged_to_sign_in_to_LinkedIn()
    {
        var oauth = new OAuthDriver(_app);

        var response = await oauth.InitializeAsync("/linkedin/mcp", accessToken: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("/.well-known/oauth-protected-resource/linkedin/mcp", challenge, StringComparison.Ordinal);
        Assert.Contains("scope=\"linkedin\"", challenge, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_through_a_tunnel_without_a_token_is_challenged_too()
    {
        var oauth = new OAuthDriver(_app);
        oauth.Http.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");

        var response = await oauth.InitializeAsync("/linkedin/mcp", accessToken: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_issued_for_TripIt_is_refused()
    {
        var oauth = new OAuthDriver(_app);
        var tripIt = await oauth.ConnectAsync();

        var response = await oauth.InitializeAsync("/linkedin/mcp", tripIt.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_issued_for_LinkedIn_is_accepted()
    {
        var oauth = new OAuthDriver(_app);
        var linkedIn = await oauth.ConnectAsync(OAuthDriver.Account("member-1", "linkedin"), LinkedInResource, "linkedin");

        var response = await oauth.InitializeAsync("/linkedin/mcp", linkedIn.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
