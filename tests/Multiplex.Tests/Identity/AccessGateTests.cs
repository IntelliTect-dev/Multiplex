using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Tests.Auth;
using Multiplex.Web.Auth;

namespace Multiplex.Tests.Identity;

/// <summary>
/// The one access rule (#80, D3): every page and every service needs sign-in; the exceptions are
/// requests from the host itself, <c>/healthz</c>, and the OAuth discovery and protocol endpoints.
/// Behind Tailscale Funnel every internet request arrives from loopback, so a request that carries
/// what Funnel adds must never count as from the host.
/// </summary>
public sealed class AccessGateTests : IClassFixture<MicrosoftTestApp>
{
    private readonly MicrosoftTestApp _app;

    public AccessGateTests(MicrosoftTestApp app) => _app = app;

    /// <summary>What a Funnel request looks like to the server: from loopback, with the caller in X-Forwarded-For.</summary>
    public static TheoryData<string, string?, string?> FunnelShapes => new()
    {
        { "X-Forwarded-For", "203.0.113.7", null },
        { "X-Forwarded-For", "2001:db8::7", null },
        { "Forwarded", "for=203.0.113.7;proto=https", null },
        { "Host", "dakar.tailaca68e.ts.net", null },
        { "Host", "multiplex.tailaca68e.ts.net", null },
        { "Origin", "https://evil.example", null },
        // Funnel's own combination: forwarded caller and the public host name.
        { "X-Forwarded-For", "203.0.113.7", "dakar.tailaca68e.ts.net" },
    };

    private static HttpRequestMessage Request(HttpMethod method, string path, string? header, string? value, string? host = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (header == "Host") request.Headers.Host = value;
        else if (header is not null) request.Headers.TryAddWithoutValidation(header, value);
        if (host is not null) request.Headers.Host = host;
        return request;
    }

    private static HttpRequestMessage Initialize(string path, string? header = null, string? value = null, string? host = null)
    {
        var request = Request(HttpMethod.Post, path, header, value, host);
        request.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
            Encoding.UTF8, "application/json");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    // ------------------------------------------------------------ web pages

    [Theory]
    [MemberData(nameof(FunnelShapes))]
    public async Task A_page_requested_through_Funnel_is_never_exempt(string header, string? value, string? host)
    {
        using var request = Request(HttpMethod.Get, "/", header, value, host);

        var response = await _app.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(FakeMicrosoft.AuthorizationEndpoint, response.Headers.Location!.AbsoluteUri);
    }

    [Fact]
    public async Task A_page_opened_on_the_host_itself_needs_no_sign_in()
    {
        var response = await _app.Client().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches("<code>[^<]*/tripit/mcp</code>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_signed_in_person_sees_the_home_page_through_Funnel_with_their_address()
    {
        var client = await _app.SignedInClientAsync();
        using var request = Request(HttpMethod.Get, "/", "X-Forwarded-For", "203.0.113.7", "dakar.tailaca68e.ts.net");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(TestPeople.Owner, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example/x")]
    public async Task After_sign_in_the_browser_only_ever_returns_to_a_page_on_this_server(string path)
    {
        using var request = Request(HttpMethod.Get, "/", "X-Forwarded-For", "203.0.113.7");
        request.RequestUri = new Uri("https://localhost" + path.Replace("\\", "%5C"));
        var browser = _app.Browser();

        var challenge = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        _app.Microsoft.NextIdToken = _app.Microsoft.IdToken(state["nonce"]!, TestPeople.Owner, verified: true);
        var back = await browser.GetAsync($"/signin-microsoft?code=c&state={Uri.EscapeDataString(state["state"]!)}");

        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal("/", back.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("script")]
    [InlineData("empty")]
    public async Task Only_a_page_load_goes_to_Microsoft_anything_else_gets_401(string destination)
    {
        using var request = Request(HttpMethod.Get, "/favicon.ico", "X-Forwarded-For", "203.0.113.7");
        request.Headers.Add("Sec-Fetch-Dest", destination);

        var response = await _app.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/oauth/authorize?client_id=x")]
    [InlineData("/connect/tripit?p=x")]
    [InlineData("/connect/amazon/allow?p=x")]
    public async Task Connecting_a_service_needs_a_person_even_on_the_host(string path)
    {
        var response = await _app.Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(FakeMicrosoft.AuthorizationEndpoint, response.Headers.Location!.AbsoluteUri);
    }

    [Theory]
    [InlineData("/signout-oidc")]
    [InlineData("/signout-callback-oidc")]
    public async Task The_OpenID_Connect_handlers_own_sign_out_paths_are_not_served(string path)
    {
        using var request = Request(HttpMethod.Get, path + "?state=x&sid=y&iss=z", "X-Forwarded-For", "203.0.113.7");

        var response = await _app.Client().SendAsync(request);

        // Only the access gate answers, as for any page: Multiplex never signs out at Microsoft.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(FakeMicrosoft.AuthorizationEndpoint, response.Headers.Location!.AbsoluteUri);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/tripit/mcp")]
    [InlineData("/.well-known/oauth-protected-resource/amazon/mcp")]
    public async Task Health_and_discovery_stay_open_to_the_internet(string path)
    {
        using var request = Request(HttpMethod.Get, path, "X-Forwarded-For", "203.0.113.7", "dakar.tailaca68e.ts.net");

        var response = await _app.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A service's icon (#135) is fetched by the client without credentials (MCP 2025-11-25), and the files
    /// are public brand images, so they are open; nothing else under the same name is.
    /// </summary>
    [Theory]
    [InlineData("/icons/tripit.png")]
    [InlineData("/icons/google-flights.png")]
    [InlineData("/ICONS/amazon.png")]
    public async Task A_services_icon_stays_open_to_the_internet(string path)
    {
        using var request = Request(HttpMethod.Get, path, "X-Forwarded-For", "203.0.113.7", "dakar.tailaca68e.ts.net");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "image");

        var response = await _app.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/iconsx/tripit.png")]
    [InlineData("/icons-tripit.png")]
    public async Task A_path_that_only_starts_like_the_icons_folder_still_needs_sign_in(string path)
    {
        using var request = Request(HttpMethod.Get, path, "X-Forwarded-For", "203.0.113.7", "dakar.tailaca68e.ts.net");

        var response = await _app.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(FakeMicrosoft.AuthorizationEndpoint, response.Headers.Location!.AbsoluteUri);
    }

    [Fact]
    public async Task Registration_stays_open_to_the_internet()
    {
        using var request = Request(HttpMethod.Post, "/oauth/register", "X-Forwarded-For", "203.0.113.7");
        request.Content = new StringContent(
            """{"client_name":"Claude","redirect_uris":["https://claude.ai/api/mcp/auth_callback"],"token_endpoint_auth_method":"none"}""",
            Encoding.UTF8, "application/json");

        var response = await _app.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("/oauth/token")]
    [InlineData("/oauth/revoke")]
    public async Task The_token_endpoints_answer_the_internet_themselves(string path)
    {
        using var request = Request(HttpMethod.Post, path, "X-Forwarded-For", "203.0.113.7");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token" });

        var response = await _app.Client().SendAsync(request);

        // The OAuth endpoint's own answer (a bad request or an empty 200), never a sign-in.
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}");
        Assert.Null(response.Headers.Location);
    }

    // ------------------------------------------------------------ MCP endpoints

    [Theory]
    [MemberData(nameof(FunnelShapes))]
    public async Task A_service_that_acts_for_no_one_still_needs_a_token_through_Funnel(string header, string? value, string? host)
    {
        using var request = Initialize("/amazon/mcp", header, value, host);

        var response = await _app.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/amazon/mcp\"",
            response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task A_service_that_acts_for_no_one_needs_no_token_on_the_host()
    {
        using var request = Initialize("/amazon/mcp");

        var response = await _app.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/tripit/mcp")]
    [InlineData("/linkedin/mcp")]
    public async Task A_service_that_acts_on_a_persons_site_account_needs_a_token_even_on_the_host(string path)
    {
        using var request = Initialize(path);

        var response = await _app.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_web_sign_in_never_authorizes_a_service_call()
    {
        var client = await _app.SignedInClientAsync();
        using var request = Initialize("/amazon/mcp", "X-Forwarded-For", "203.0.113.7");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/nosuch/mcp")]
    [InlineData("/NoSuch/MCP/deeper")]
    public async Task A_service_address_that_is_not_registered_is_not_found_even_on_the_host(string path)
    {
        using var request = Initialize(path);

        var response = await _app.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Every_service_requires_sign_in_and_only_amazon_and_google_flights_act_for_no_one()
    {
        var endpoints = _app.Services.GetRequiredService<AuthenticatedEndpoints>();

        Assert.True(endpoints.FindBySlug("tripit")!.NeedsSiteAccount);
        Assert.True(endpoints.FindBySlug("linkedin")!.NeedsSiteAccount);
        Assert.False(endpoints.FindBySlug("amazon")!.NeedsSiteAccount);
        Assert.False(endpoints.FindBySlug("google-flights")!.NeedsSiteAccount);
        Multiplex.Web.Mcp.McpEndpointRouting.EnsureEveryEndpointRequiresSignIn(endpoints);
    }

    [Fact]
    public void A_service_left_unregistered_stops_the_server_from_starting()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Multiplex:PublicBaseUrl"] = AuthTestApp.BaseUrl,
        }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config)
            .AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" });
        services.AddMultiplexAuth(config).RequireAuthFor("tripit", "TripIt");
        var endpoints = services.BuildServiceProvider().GetRequiredService<AuthenticatedEndpoints>();

        var refused = Assert.Throws<InvalidOperationException>(() => Multiplex.Web.Mcp.McpEndpointRouting.EnsureEveryEndpointRequiresSignIn(endpoints));
        Assert.Contains("amazon", refused.Message);
        Assert.Contains("linkedin", refused.Message);
    }
}
