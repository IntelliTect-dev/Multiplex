using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Web.Auth;
using Multiplex.Web.Auth.Clients;

namespace Multiplex.Tests.Auth;

/// <summary>Startup rules for the public base URL, and the rate limits; each test gets its own host.</summary>
public class AuthHostingTests
{
    private sealed class TightRegistrationLimit : AuthTestApp
    {
        protected override IDictionary<string, string?> Settings
        {
            get
            {
                var settings = base.Settings;
                settings["Multiplex:Auth:RegisterPermitLimit"] = "2";
                return settings;
            }
        }
    }

    [Fact]
    public async Task FileStore_KeepsRegistrationsAcrossRestarts_InsideTheDataFolder()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "multiplex-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string clientId;
            using (var first = new FileStoreAuthTestApp(dataPath))
            {
                var metadata = await first.Client().GetAsync("/.well-known/oauth-authorization-server");
                Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
                // The app-wide Data Protection key ring may be created at startup; the auth store itself must not be.
                Assert.False(File.Exists(Path.Combine(dataPath, "auth.dat")), "Starting the host must not create the auth store.");

                clientId = await new OAuthDriver(first).RegisterAsync();
            }

            Assert.True(File.Exists(Path.Combine(dataPath, "auth.dat")));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(dataPath, "keys"), "key-*.xml"));

            using var second = new FileStoreAuthTestApp(dataPath);
            await new OAuthDriver(second).StartAsync(OAuthDriver.AuthorizeParameters(clientId, OAuthDriver.NewPkce()));
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public async Task Tokens_ExpireAfterAnHour_AndRefreshAfter30Days()
    {
        using var app = new AuthTestApp();
        var driver = new OAuthDriver(app);
        var tokens = await driver.ConnectAsync();

        app.Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await driver.InitializeAsync("/tripit/mcp", tokens.AccessToken)).StatusCode);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.InitializeAsync("/tripit/mcp", tokens.AccessToken)).StatusCode);

        var (status, refreshed, _) = await driver.TokenAsync(OAuthDriver.Refresh(tokens.ClientId, tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, status);

        app.Clock.Advance(TimeSpan.FromDays(30));
        var (expiredStatus, expired, _) = await driver.TokenAsync(
            OAuthDriver.Refresh(tokens.ClientId, refreshed.GetProperty("refresh_token").GetString()!));
        Assert.Equal(HttpStatusCode.BadRequest, expiredStatus);
        Assert.Equal("invalid_grant", expired.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Register_IsRateLimitedPerAddress()
    {
        using var app = new TightRegistrationLimit();
        var driver = new OAuthDriver(app);

        await driver.RegisterAsync();
        await driver.RegisterAsync();
        var third = await driver.RegisterRawAsync(new { redirect_uris = new[] { OAuthDriver.ClaudeRedirect } });

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Registration_LastsWhileUsed_AndEndsAfter90DaysUnused()
    {
        using var app = new AuthTestApp();
        var driver = new OAuthDriver(app);
        var clientId = await driver.RegisterAsync();

        app.Clock.Advance(TimeSpan.FromDays(89));
        await driver.StartAsync(OAuthDriver.AuthorizeParameters(clientId, OAuthDriver.NewPkce()));
        app.Clock.Advance(TimeSpan.FromDays(89));
        await driver.StartAsync(OAuthDriver.AuthorizeParameters(clientId, OAuthDriver.NewPkce()));

        app.Clock.Advance(TimeSpan.FromDays(91));
        var response = await driver.AuthorizeAsync(OAuthDriver.AuthorizeParameters(clientId, OAuthDriver.NewPkce()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task PendingAuthorization_ExpiresAfter10Minutes_ButCanStillBeFailed()
    {
        using var app = new AuthTestApp();
        var driver = new OAuthDriver(app);
        var clientId = await driver.RegisterAsync();
        var pendingId = await driver.StartAsync(OAuthDriver.AuthorizeParameters(clientId, OAuthDriver.NewPkce()));

        app.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Null(await driver.Completer.FindAsync(pendingId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.Completer.CompleteAsync(pendingId, OAuthDriver.Account(), CancellationToken.None));
        var redirect = await driver.Completer.FailAsync(pendingId, "access_denied", "Timed out.", CancellationToken.None);
        Assert.Equal("access_denied", OAuthDriver.Query(redirect)["error"]);
    }

    [Fact]
    public void Production_WithoutPublicBaseUrl_FailsAtStartup()
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Multiplex:PublicBaseUrl", "");
        });

        var ex = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("Multiplex:PublicBaseUrl", ex.ToString());
    }

    [Theory]
    [InlineData(null, AuthIssuer.DevelopmentFallback)]
    [InlineData("http://+:6123;https://+:7123", "http://localhost:6123")]
    public async Task Development_WithoutPublicBaseUrl_FallsBackToFirstConfiguredUrl(string? urls, string expected)
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Multiplex:PublicBaseUrl", "");
            b.UseSetting("urls", urls);
        });

        var metadata = await app.CreateClient().GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");

        Assert.Equal(expected, metadata.GetProperty("issuer").GetString());
    }

    [Theory]
    [InlineData("https://mcp.example.test/", "https://mcp.example.test")]
    [InlineData("https://MCP.Example.Test:443", "https://mcp.example.test")]
    public async Task PublicBaseUrl_IsNormalizedToOneSpelling(string configured, string expected)
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Multiplex:PublicBaseUrl", configured));

        var metadata = await app.CreateClient().GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");

        Assert.Equal(expected + "/tripit/mcp", metadata.GetProperty("resource").GetString());
        Assert.Equal(expected, metadata.GetProperty("authorization_servers")[0].GetString());
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.100.1.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd7a:115c:a1e0::1", false)]
    [InlineData("192.0.0.9", false)]
    [InlineData("192.0.2.10", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.19.255.254", false)]
    [InlineData("198.51.100.7", false)]
    [InlineData("203.0.113.7", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("64:ff9b::808:808", false)]
    [InlineData("2002:a00:1::1", false)]
    [InlineData("2002:808:808::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("192.0.1.1", true)]
    [InlineData("198.17.255.255", true)]
    [InlineData("198.20.0.1", true)]
    [InlineData("203.0.114.1", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("2003::1", true)]
    public void MetadataDocumentFetch_OnlyConnectsToPublicAddresses(string address, bool allowed)
    {
        Assert.Equal(allowed, ClientMetadataDocuments.IsPublic(IPAddress.Parse(address)));
    }

    [Fact]
    public void MetadataDocumentFetch_ConnectsDirectly_NeverThroughAProxy()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMultiplexAuth(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(ClientMetadataDocuments.HttpClientName);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;

        // Through a proxy, the public-address check would see the proxy rather than the document's host.
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.UseProxy);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.NotNull(sockets.ConnectCallback);
    }
}
