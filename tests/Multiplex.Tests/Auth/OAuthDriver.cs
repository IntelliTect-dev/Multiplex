using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.Auth;

/// <summary>
/// Plays the part of an MCP client (Claude, ChatGPT), of the person's browser (signed in to Multiplex as
/// <paramref name="person"/> before the first authorize), and of the connect pages.
/// </summary>
public sealed class OAuthDriver(AuthTestApp app, string person = Identity.TestPeople.Owner)
{
    public const string ClaudeRedirect = "https://claude.ai/api/mcp/auth_callback";

    private string? _owner;

    public HttpClient Http { get; } = app.Client();

    /// <summary>The Multiplex user the driver's browser is signed in as (signs in on first use).</summary>
    public async Task<string> OwnerAsync()
    {
        if (_owner is null)
        {
            var (signedIn, cookie) = await Identity.TestPeople.SignInAsync(app.Services, person);
            Identity.TestPeople.WithCookie(Http, cookie);
            _owner = signedIn.Owner;
        }
        return _owner;
    }

    /// <summary>
    /// Signs the driver's browser in to Multiplex again, as the person must once their sign-in has lapsed
    /// (it lasts <see cref="Multiplex.Web.Identity.MultiplexIdentity.SessionLifetime"/>, by the app's clock).
    /// </summary>
    public async Task SignInAgainAsync()
    {
        _owner = null;
        await OwnerAsync();
    }

    public IAuthorizationCompleter Completer => app.Services.GetRequiredService<IAuthorizationCompleter>();

    public IGrantRevoker Revoker => app.Services.GetRequiredService<IGrantRevoker>();

    public sealed record Pkce(string Verifier, string Challenge);

    public sealed record Tokens(string ClientId, string AccessToken, string RefreshToken);

    public static Pkce NewPkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return new Pkce(verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public static SiteAccount Account(string accountId = "acct-1", string site = "tripit") =>
        new(site, accountId, [$"{accountId}@example.test"], "Test Traveller");

    public async Task<HttpResponseMessage> RegisterRawAsync(object body) =>
        await Http.PostAsJsonAsync("/oauth/register", body);

    public async Task<string> RegisterAsync(string redirectUri = ClaudeRedirect, string name = "Claude")
    {
        var response = await RegisterRawAsync(new
        {
            client_name = name,
            redirect_uris = new[] { redirectUri },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    public static Dictionary<string, string?> AuthorizeParameters(
        string clientId, Pkce pkce, string redirectUri = ClaudeRedirect, string resource = AuthTestApp.TripItResource) => new()
    {
        ["response_type"] = "code",
        ["client_id"] = clientId,
        ["redirect_uri"] = redirectUri,
        ["state"] = "state-123",
        ["code_challenge"] = pkce.Challenge,
        ["code_challenge_method"] = "S256",
        ["resource"] = resource,
        ["scope"] = "tripit",
    };

    /// <summary>Authorizes as the signed-in person (signing in first if needed).</summary>
    public async Task<HttpResponseMessage> AuthorizeAsync(IDictionary<string, string?> parameters)
    {
        await OwnerAsync();
        return await Http.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", parameters.Where(p => p.Value is not null)));
    }

    /// <summary>Authorizes and returns the pending id from the redirect to the connect page.</summary>
    public async Task<string> StartAsync(IDictionary<string, string?> parameters, string slug = "tripit")
    {
        var response = await AuthorizeAsync(parameters);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith($"/connect/{slug}?p=", location);
        return QueryHelpers.ParseQuery(new Uri("http://x" + location).Query)["p"].ToString();
    }

    public async Task<string> CodeAsync(IDictionary<string, string?> parameters, SiteAccount? account, string slug = "tripit")
    {
        var pending = await StartAsync(parameters, slug);
        var redirect = await Completer.CompleteAsync(pending, await OwnerAsync(), account, CancellationToken.None);
        return Query(redirect)["code"]!;
    }

    public async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> TokenAsync(IDictionary<string, string> form)
    {
        var response = await Http.PostAsync("/oauth/token", new FormUrlEncodedContent(form));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement, response);
    }

    public static Dictionary<string, string> CodeExchange(string clientId, string code, Pkce pkce, string redirectUri = ClaudeRedirect) => new()
    {
        ["grant_type"] = "authorization_code",
        ["client_id"] = clientId,
        ["code"] = code,
        ["redirect_uri"] = redirectUri,
        ["code_verifier"] = pkce.Verifier,
        ["resource"] = AuthTestApp.TripItResource,
    };

    public static Dictionary<string, string> Refresh(string clientId, string refreshToken) => new()
    {
        ["grant_type"] = "refresh_token",
        ["client_id"] = clientId,
        ["refresh_token"] = refreshToken,
    };

    /// <summary>Register, authorize, complete as <paramref name="account"/>, and exchange the code.</summary>
    public async Task<Tokens> ConnectAsync(SiteAccount? account = null, string resource = AuthTestApp.TripItResource, string slug = "tripit")
    {
        var clientId = await RegisterAsync();
        var pkce = NewPkce();
        var code = await CodeAsync(AuthorizeParameters(clientId, pkce, resource: resource), account ?? Account(site: slug), slug);
        var exchange = CodeExchange(clientId, code, pkce);
        exchange["resource"] = resource;
        var (status, body, _) = await TokenAsync(exchange);
        Assert.Equal(HttpStatusCode.OK, status);
        return new Tokens(clientId, body.GetProperty("access_token").GetString()!, body.GetProperty("refresh_token").GetString()!);
    }

    public async Task<HttpResponseMessage> InitializeAsync(string path, string? accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"auth-tests","version":"1.0"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await Http.SendAsync(request);
    }

    public static Dictionary<string, string?> Query(Uri uri) =>
        QueryHelpers.ParseQuery(uri.Query).ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString());
}
