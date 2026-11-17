using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700::1111", true)]
    public void MetadataDocumentFetch_OnlyConnectsToPublicAddresses(string address, bool allowed)
    {
        Assert.Equal(allowed, ClientMetadataDocuments.IsPublic(IPAddress.Parse(address)));
    }
}
