using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplex.Web.Auth;
using Multiplex.Web.Identity;
using Multiplex.Web.Sites;
using static Multiplex.Tests.Auth.OAuthDriver;

namespace Multiplex.Tests.Auth;

public class OAuthFlowTests(AuthTestApp app) : IClassFixture<AuthTestApp>
{
    private const string TripItChallenge =
        "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/tripit/mcp\", scope=\"tripit\"";

    private readonly OAuthDriver _driver = new(app);

    // ---- Discovery ----

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/tripit/mcp")]
    [InlineData("/.well-known/oauth-protected-resource")]
    public async Task ProtectedResourceMetadata_DescribesTripItFromPublicBaseUrl(string path)
    {
        var json = await _driver.Http.GetFromJsonAsync<JsonElement>(path);

        Assert.Equal(AuthTestApp.TripItResource, json.GetProperty("resource").GetString());
        Assert.Equal([AuthTestApp.BaseUrl], Strings(json, "authorization_servers"));
        Assert.Equal(["tripit"], Strings(json, "scopes_supported"));
        Assert.Equal(["header"], Strings(json, "bearer_methods_supported"));
        Assert.Equal("TripIt", json.GetProperty("resource_name").GetString());
    }

    [Fact]
    public async Task ProtectedResourceMetadata_IsNotServedForUnregisteredEndpoints()
    {
        var response = await _driver.Http.GetAsync("/.well-known/oauth-protected-resource/nosuch/mcp");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedResourceMetadata_DescribesTime_NowThatItNeedsSignIn()
    {
        var json = await _driver.Http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/time/mcp");

        Assert.Equal(AuthTestApp.BaseUrl + "/time/mcp", json.GetProperty("resource").GetString());
        Assert.Equal("Time", json.GetProperty("resource_name").GetString());
    }

    [Theory]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/openid-configuration")]
    public async Task AuthorizationServerMetadata_AdvertisesWhatClientsRequire(string path)
    {
        var json = await _driver.Http.GetFromJsonAsync<JsonElement>(path);

        Assert.Equal(AuthTestApp.BaseUrl, json.GetProperty("issuer").GetString());
        Assert.Equal(AuthTestApp.BaseUrl + "/oauth/authorize", json.GetProperty("authorization_endpoint").GetString());
        Assert.Equal(AuthTestApp.BaseUrl + "/oauth/token", json.GetProperty("token_endpoint").GetString());
        Assert.Equal(AuthTestApp.BaseUrl + "/oauth/register", json.GetProperty("registration_endpoint").GetString());
        Assert.Equal(AuthTestApp.BaseUrl + "/oauth/revoke", json.GetProperty("revocation_endpoint").GetString());
        Assert.Equal(["code"], Strings(json, "response_types_supported"));
        Assert.Equal(["authorization_code", "refresh_token"], Strings(json, "grant_types_supported"));
        Assert.Equal(["S256"], Strings(json, "code_challenge_methods_supported"));
        Assert.Equal(["none"], Strings(json, "token_endpoint_auth_methods_supported"));
        Assert.True(json.GetProperty("client_id_metadata_document_supported").GetBoolean());
        Assert.True(json.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
        Assert.Equal(["tripit", "linkedin", "time", "google-flights", "other"], Strings(json, "scopes_supported"));
    }

    // ---- Happy path ----

    [Fact]
    public async Task FullFlow_DiscoveryRegistrationAuthorizeTokenMcpRefreshAndReuse()
    {
        var clientId = await _driver.RegisterAsync();
        var pkce = NewPkce();

        var pendingId = await _driver.StartAsync(AuthorizeParameters(clientId, pkce));
        var pending = await _driver.Completer.FindAsync(pendingId, CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal("tripit", pending.Site);
        Assert.Equal("Claude", pending.ClientName);
        Assert.True(pending.ExpiresAt > app.Clock.GetUtcNow());

        Assert.Equal(await _driver.OwnerAsync(), pending.Owner);

        var redirect = await _driver.Completer.CompleteAsync(pendingId, await _driver.OwnerAsync(), Account(), CancellationToken.None);
        Assert.StartsWith(ClaudeRedirect + "?", redirect.AbsoluteUri);
        var query = Query(redirect);
        Assert.Equal("state-123", query["state"]);
        Assert.Equal(AuthTestApp.BaseUrl, query["iss"]);
        Assert.Null(await _driver.Completer.FindAsync(pendingId, CancellationToken.None));

        var (status, body, response) = await _driver.TokenAsync(CodeExchange(clientId, query["code"]!, pkce));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Equal(3600, body.GetProperty("expires_in").GetInt32());
        Assert.Equal("tripit", body.GetProperty("scope").GetString());
        var access = body.GetProperty("access_token").GetString()!;
        var refresh = body.GetProperty("refresh_token").GetString()!;
        Assert.Equal(43, access.Length);

        Assert.NotEqual(HttpStatusCode.Unauthorized, (await _driver.InitializeAsync("/tripit/mcp", access)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _driver.InitializeAsync("/tripit/mcp", null)).StatusCode);

        // Rotation: a new pair, and the new access token works.
        var (refreshStatus, refreshed, _) = await _driver.TokenAsync(Refresh(clientId, refresh));
        Assert.Equal(HttpStatusCode.OK, refreshStatus);
        var access2 = refreshed.GetProperty("access_token").GetString()!;
        var refresh2 = refreshed.GetProperty("refresh_token").GetString()!;
        Assert.NotEqual(access, access2);
        Assert.NotEqual(refresh, refresh2);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await _driver.InitializeAsync("/tripit/mcp", access2)).StatusCode);

        // Reusing the spent refresh token revokes the whole family.
        await AssertTokenError(Refresh(clientId, refresh), "invalid_grant");
        await AssertTokenError(Refresh(clientId, refresh2), "invalid_grant");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _driver.InitializeAsync("/tripit/mcp", access2)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _driver.InitializeAsync("/tripit/mcp", access)).StatusCode);
    }

    [Fact]
    public async Task RevokeAccount_EndsEveryTokenOfThatAccountOnly()
    {
        var revoked = await _driver.ConnectAsync(Account("acct-revoke"));
        var kept = await _driver.ConnectAsync(Account("acct-keep"));

        await _driver.Revoker.RevokeAccountAsync(await _driver.OwnerAsync(), "tripit", "acct-revoke", CancellationToken.None);

        var response = await _driver.InitializeAsync("/tripit/mcp", revoked.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("error=\"invalid_token\"", Challenge(response));
        await AssertTokenError(Refresh(revoked.ClientId, revoked.RefreshToken), "invalid_grant");

        Assert.NotEqual(HttpStatusCode.Unauthorized, (await _driver.InitializeAsync("/tripit/mcp", kept.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task ValidToken_PutsTheSiteAccountOnTheRequest()
    {
        var tokens = await _driver.ConnectAsync(Account("acct-claims"));
        ClaimsPrincipal? seen = null;
        var middleware = new AccessGate(
            context => { seen = context.User; return Task.CompletedTask; },
            app.Services.GetRequiredService<AuthenticatedEndpoints>(),
            app.Services.GetRequiredService<TokenService>(),
            app.Services.GetRequiredService<MicrosoftSignInSettings>(),
            NullLogger<AccessGate>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/tripit/mcp";
        context.Request.Headers.Authorization = "Bearer " + tokens.AccessToken;

        await middleware.InvokeAsync(context);

        Assert.NotNull(seen);
        Assert.True(seen.Identity?.IsAuthenticated);
        Assert.Equal((await _driver.OwnerAsync(), "tripit", "acct-claims"), SiteClaims.AccountOf(seen)!.Value);
        Assert.Equal(tokens.ClientId, seen.FindFirst(SiteClaims.ClientId)?.Value);
    }

    // ---- Resource server ----

    [Theory]
    [InlineData("/tripit/mcp")]
    [InlineData("/TripIt/MCP/")]
    [InlineData("/tripit/mcp/sse")]
    public async Task McpRequestWithoutToken_Gets401PointingAtResourceMetadata(string path)
    {
        var response = await _driver.InitializeAsync(path, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(TripItChallenge, Challenge(response));
    }

    [Fact]
    public async Task McpRequestWithUnknownToken_Gets401WithInvalidToken()
    {
        var response = await _driver.InitializeAsync("/tripit/mcp", "not-a-real-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Challenge(response);
        Assert.StartsWith(TripItChallenge + ", error=\"invalid_token\"", challenge);
    }

    [Fact]
    public async Task TokenForOneEndpoint_IsRejectedByAnother()
    {
        var tripit = await _driver.ConnectAsync();
        var other = await _driver.ConnectAsync(Account(site: "other"), AuthTestApp.OtherResource, "other");

        var response = await _driver.InitializeAsync("/other/mcp", tripit.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("error=\"invalid_token\"", Challenge(response));
        Assert.Contains("oauth-protected-resource/other/mcp", Challenge(response));

        Assert.Equal(HttpStatusCode.Unauthorized, (await _driver.InitializeAsync("/tripit/mcp", other.AccessToken)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await _driver.InitializeAsync("/other/mcp", other.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task TimeMcp_FromThisPc_WorksWithoutToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/time/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _driver.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"get_local_time\"", await response.Content.ReadAsStringAsync());
    }

    // ---- Authorization endpoint ----

    [Fact]
    public async Task Authorize_UnregisteredClient_ShowsErrorPageWithoutRedirecting()
    {
        var response = await _driver.AuthorizeAsync(AuthorizeParameters("no-such-client", NewPkce()));

        await AssertErrorPage(response);
    }

    [Theory]
    [InlineData("https://evil.example/callback")]
    [InlineData(ClaudeRedirect + "/extra")]
    [InlineData(ClaudeRedirect + "?x=1")]
    [InlineData(null)]
    public async Task Authorize_UnregisteredRedirectUri_ShowsErrorPageWithoutRedirecting(string? redirectUri)
    {
        var clientId = await _driver.RegisterAsync();
        var parameters = AuthorizeParameters(clientId, NewPkce());
        parameters["redirect_uri"] = redirectUri;

        await AssertErrorPage(await _driver.AuthorizeAsync(parameters));
    }

    [Theory]
    [InlineData("code_challenge_method", "plain", "invalid_request")]
    [InlineData("code_challenge_method", null, "invalid_request")]
    [InlineData("code_challenge", null, "invalid_request")]
    [InlineData("code_challenge", "short", "invalid_request")]
    [InlineData("response_type", "token", "unsupported_response_type")]
    [InlineData("resource", AuthTestApp.BaseUrl + "/nosuch/mcp", "invalid_target")]
    [InlineData("resource", "https://elsewhere.example/tripit/mcp", "invalid_target")]
    [InlineData("resource", null, "invalid_target")]
    public async Task Authorize_InvalidRequest_RedirectsWithErrorStateAndIss(string parameter, string? value, string error)
    {
        var clientId = await _driver.RegisterAsync();
        var parameters = AuthorizeParameters(clientId, NewPkce());
        parameters[parameter] = value;

        var query = await AssertErrorRedirect(await _driver.AuthorizeAsync(parameters), error);
        Assert.Equal("state-123", query["state"]);
    }

    [Fact]
    public async Task Authorize_WithoutState_RedirectsWithErrorAndIss()
    {
        var clientId = await _driver.RegisterAsync();
        var parameters = AuthorizeParameters(clientId, NewPkce());
        parameters["state"] = null;

        var query = await AssertErrorRedirect(await _driver.AuthorizeAsync(parameters), "invalid_request");
        Assert.False(query.ContainsKey("state"));
    }

    [Fact]
    public async Task FailAsync_RedirectsWithErrorDescriptionStateAndIss()
    {
        var clientId = await _driver.RegisterAsync();
        var pendingId = await _driver.StartAsync(AuthorizeParameters(clientId, NewPkce()));

        var redirect = await _driver.Completer.FailAsync(pendingId, "access_denied", "The account is not allowed.", CancellationToken.None);

        Assert.StartsWith(ClaudeRedirect + "?", redirect.AbsoluteUri);
        var query = Query(redirect);
        Assert.Equal("access_denied", query["error"]);
        Assert.Equal("The account is not allowed.", query["error_description"]);
        Assert.Equal("state-123", query["state"]);
        Assert.Equal(AuthTestApp.BaseUrl, query["iss"]);
        Assert.Null(await _driver.Completer.FindAsync(pendingId, CancellationToken.None));
    }

    [Fact]
    public async Task CompleteAsync_RefusesUnknownPendingAndAnotherSitesAccount()
    {
        var clientId = await _driver.RegisterAsync();
        var pendingId = await _driver.StartAsync(AuthorizeParameters(clientId, NewPkce()));

        var owner = await _driver.OwnerAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _driver.Completer.CompleteAsync("no-such-pending", owner, Account(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _driver.Completer.CompleteAsync(pendingId, owner, Account(site: "other"), CancellationToken.None));
    }

    // ---- Token endpoint ----

    [Fact]
    public async Task Token_WrongOrMissingVerifier_IsRefused()
    {
        var clientId = await _driver.RegisterAsync();
        var pkce = NewPkce();
        var code = await _driver.CodeAsync(AuthorizeParameters(clientId, pkce), Account());

        await AssertTokenError(CodeExchange(clientId, code, NewPkce()), "invalid_grant");
        var missing = CodeExchange(clientId, code, pkce);
        missing.Remove("code_verifier");
        await AssertTokenError(missing, "invalid_request");
    }

    [Fact]
    public async Task Token_ExpiredCode_IsRefused()
    {
        var clientId = await _driver.RegisterAsync();
        var pkce = NewPkce();
        var code = await _driver.CodeAsync(AuthorizeParameters(clientId, pkce), Account());

        app.Clock.Advance(TimeSpan.FromSeconds(61));

        await AssertTokenError(CodeExchange(clientId, code, pkce), "invalid_grant");
    }

    [Fact]
    public async Task Token_CodeReuse_IsRefusedAndRevokesWhatTheCodeIssued()
    {
        var clientId = await _driver.RegisterAsync();
        var pkce = NewPkce();
        var code = await _driver.CodeAsync(AuthorizeParameters(clientId, pkce), Account());
        var (status, first, _) = await _driver.TokenAsync(CodeExchange(clientId, code, pkce));
        Assert.Equal(HttpStatusCode.OK, status);

        await AssertTokenError(CodeExchange(clientId, code, pkce), "invalid_grant");

        var access = first.GetProperty("access_token").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _driver.InitializeAsync("/tripit/mcp", access)).StatusCode);
        await AssertTokenError(Refresh(clientId, first.GetProperty("refresh_token").GetString()!), "invalid_grant");
    }

    [Fact]
    public async Task Token_MismatchedResourceRedirectOrClient_IsRefused()
    {
        var clientId = await _driver.RegisterAsync();
        var otherClient = await _driver.RegisterAsync();
        var pkce = NewPkce();
        var code = await _driver.CodeAsync(AuthorizeParameters(clientId, pkce), Account());

        var wrongResource = CodeExchange(clientId, code, pkce);
        wrongResource["resource"] = AuthTestApp.OtherResource;
        await AssertTokenError(wrongResource, "invalid_target");
        await AssertTokenError(CodeExchange(clientId, code, pkce, "https://claude.ai/other"), "invalid_grant");
        await AssertTokenError(CodeExchange(otherClient, code, pkce), "invalid_grant");

        // None of the refusals spent the code.
        var (status, _, _) = await _driver.TokenAsync(CodeExchange(clientId, code, pkce));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Token_RefreshByAnotherClient_IsRefused()
    {
        var tokens = await _driver.ConnectAsync();
        var otherClient = await _driver.RegisterAsync();

        await AssertTokenError(Refresh(otherClient, tokens.RefreshToken), "invalid_grant");
        var (status, _, _) = await _driver.TokenAsync(Refresh(tokens.ClientId, tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Token_BadRequests_GetOAuthErrors()
    {
        var json = await _driver.Http.PostAsJsonAsync("/oauth/token", new { grant_type = "authorization_code" });
        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);
        Assert.Equal("invalid_request", (await json.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        await AssertTokenError(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = "x" }, "unsupported_grant_type");
        await AssertTokenError(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = "x", ["refresh_token"] = "nope" }, "invalid_grant");
    }

    [Theory]
    [InlineData("/oauth/token")]
    [InlineData("/oauth/revoke")]
    public async Task FormEndpoints_RefuseAnOversizedBodySentWithoutAContentLength(string path)
    {
        var padding = new string('a', 20 * 1024);
        var response = await _driver.Http.SendAsync(ChunkedFormContent.Post(path, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "chunked-client",
            ["refresh_token"] = padding,
            ["token"] = padding,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal("The request is too large.", body.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task FormEndpoints_AcceptASmallBodySentWithoutAContentLength()
    {
        var tokens = await _driver.ConnectAsync();

        var response = await _driver.Http.SendAsync(ChunkedFormContent.Post("/oauth/token", Refresh(tokens.ClientId, tokens.RefreshToken)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task Revoke_RefreshToken_EndsTheGrant()
    {
        var tokens = await _driver.ConnectAsync();

        var response = await _driver.Http.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = tokens.RefreshToken,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = tokens.ClientId,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertTokenError(Refresh(tokens.ClientId, tokens.RefreshToken), "invalid_grant");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _driver.InitializeAsync("/tripit/mcp", tokens.AccessToken)).StatusCode);
    }

    // ---- Registration ----

    [Theory]
    [InlineData("http://127.0.0.1:33418/callback")]
    [InlineData("http://localhost:6274/oauth/callback")]
    [InlineData("http://[::1]:8080/cb")]
    [InlineData("https://chatgpt.com/connector_platform_oauth_redirect")]
    public async Task Register_AcceptsHttpsAndLoopbackRedirects_AsPublicClient(string redirectUri)
    {
        var response = await _driver.RegisterRawAsync(new
        {
            client_name = "Inspector",
            redirect_uris = new[] { redirectUri },
            token_endpoint_auth_method = "client_secret_post",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(json.GetProperty("client_id").GetString()));
        Assert.Equal("none", json.GetProperty("token_endpoint_auth_method").GetString());
        Assert.False(json.TryGetProperty("client_secret", out _));
        Assert.Equal([redirectUri], Strings(json, "redirect_uris"));
    }

    [Theory]
    [InlineData("http://example.com/callback")]
    [InlineData("http://localhost.evil.example/callback")]
    [InlineData("https://claude.ai/callback#fragment")]
    [InlineData("myapp://callback")]
    [InlineData("/relative")]
    public async Task Register_RejectsOtherRedirects(string redirectUri)
    {
        var response = await _driver.RegisterRawAsync(new { redirect_uris = new[] { redirectUri } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_redirect_uri", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    // ---- Client ID metadata documents ----

    [Fact]
    public async Task MetadataDocumentClient_CanConnectWithoutRegistering()
    {
        const string clientId = "https://client.example.test/oauth/client.json";
        const string redirect = "https://chatgpt.com/connector_platform_oauth_redirect";
        app.Documents.Json(clientId, $$"""{"client_id":"{{clientId}}","client_name":"ChatGPT","redirect_uris":["{{redirect}}"],"token_endpoint_auth_method":"none"}""");
        var pkce = NewPkce();

        var pendingId = await _driver.StartAsync(AuthorizeParameters(clientId, pkce, redirect));
        Assert.Equal("ChatGPT", (await _driver.Completer.FindAsync(pendingId, CancellationToken.None))!.ClientName);
        var code = Query(await _driver.Completer.CompleteAsync(pendingId, await _driver.OwnerAsync(), Account(), CancellationToken.None))["code"]!;
        var (status, body, _) = await _driver.TokenAsync(CodeExchange(clientId, code, pkce, redirect));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEqual(HttpStatusCode.Unauthorized,
            (await _driver.InitializeAsync("/tripit/mcp", body.GetProperty("access_token").GetString())).StatusCode);

        // Cached: a second authorization does not fetch the document again.
        await _driver.StartAsync(AuthorizeParameters(clientId, NewPkce(), redirect));
        Assert.Single(app.Documents.Requested, clientId);
    }

    [Fact]
    public async Task MetadataDocumentClient_WithMismatchedClientId_IsRefused()
    {
        const string clientId = "https://client.example.test/mismatch.json";
        app.Documents.Json(clientId, """{"client_id":"https://client.example.test/other.json","redirect_uris":["https://chatgpt.com/cb"]}""");

        await AssertErrorPage(await _driver.AuthorizeAsync(AuthorizeParameters(clientId, NewPkce(), "https://chatgpt.com/cb")));
    }

    [Fact]
    public async Task MetadataDocumentClient_RedirectUriNotInDocument_IsRefused()
    {
        const string clientId = "https://client.example.test/listed.json";
        app.Documents.Json(clientId, $$"""{"client_id":"{{clientId}}","redirect_uris":["https://chatgpt.com/cb"]}""");

        await AssertErrorPage(await _driver.AuthorizeAsync(AuthorizeParameters(clientId, NewPkce(), "https://chatgpt.com/elsewhere")));
    }

    [Fact]
    public async Task MetadataDocumentClient_FollowsHttpsRedirectsOnly()
    {
        const string viaHttps = "https://client.example.test/moved.json";
        const string target = "https://cdn.example.test/moved.json";
        app.Documents.Redirect(viaHttps, target);
        app.Documents.Json(target, $$"""{"client_id":"{{viaHttps}}","redirect_uris":["https://chatgpt.com/cb"]}""");
        await _driver.StartAsync(AuthorizeParameters(viaHttps, NewPkce(), "https://chatgpt.com/cb"));

        const string viaHttp = "https://client.example.test/downgrade.json";
        app.Documents.Redirect(viaHttp, "http://client.example.test/downgrade.json");
        await AssertErrorPage(await _driver.AuthorizeAsync(AuthorizeParameters(viaHttp, NewPkce(), "https://chatgpt.com/cb")));
        Assert.DoesNotContain("http://client.example.test/downgrade.json", app.Documents.Requested);
    }

    [Fact]
    public async Task MetadataDocumentClient_LargerThan64KB_IsRefused()
    {
        const string clientId = "https://client.example.test/huge.json";
        var padding = new string('x', 70 * 1024);
        app.Documents.Json(clientId, $$"""{"client_id":"{{clientId}}","redirect_uris":["https://chatgpt.com/cb"],"pad":"{{padding}}"}""");

        await AssertErrorPage(await _driver.AuthorizeAsync(AuthorizeParameters(clientId, NewPkce(), "https://chatgpt.com/cb")));
    }

    // ---- Helpers ----

    private async Task AssertTokenError(IDictionary<string, string> form, string error)
    {
        var (status, body, response) = await _driver.TokenAsync(form);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(error, body.GetProperty("error").GetString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    private static async Task AssertErrorPage(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("can't be used", await response.Content.ReadAsStringAsync());
    }

    private static Task<Dictionary<string, string?>> AssertErrorRedirect(HttpResponseMessage response, string error)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(ClaudeRedirect + "?", location.AbsoluteUri);
        var query = Query(location);
        Assert.Equal(error, query["error"]);
        Assert.Equal(AuthTestApp.BaseUrl, query["iss"]);
        return Task.FromResult(query);
    }

    private static string Challenge(HttpResponseMessage response) =>
        response.Headers.NonValidated["WWW-Authenticate"].ToString();

    private static string[] Strings(JsonElement json, string property) =>
        json.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();

    /// <summary>A form body of unknown length, so it is sent chunked with no Content-Length header.</summary>
    private sealed class ChunkedFormContent : HttpContent
    {
        private readonly byte[] _bytes;

        private ChunkedFormContent(IDictionary<string, string> form)
        {
            _bytes = new FormUrlEncodedContent(form).ReadAsByteArrayAsync().GetAwaiter().GetResult();
            Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        }

        public static HttpRequestMessage Post(string path, IDictionary<string, string> form)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ChunkedFormContent(form) };
            request.Headers.TransferEncodingChunked = true;
            return request;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
