using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Tests.Auth;
using Multiplex.Web.Auth;
using Multiplex.Web.Mcp;

namespace Multiplex.Tests.Mcp;

/// <summary>
/// Each service says what it is (#135): its name, the website it wraps and that website's icon, in the
/// <c>serverInfo</c> of its <c>initialize</c> answer (MCP 2025-11-25). Without them a client such as
/// ChatGPT shows a placeholder icon for every service.
/// </summary>
public sealed class ServiceIdentityTests(AuthTestApp app) : IClassFixture<AuthTestApp>
{
    /// <summary>Every service this host serves, so a new one fails here until it has an icon and a website.</summary>
    public static TheoryData<string> Slugs =>
        new(McpEndpointRouting.EndpointPaths.Select(path => path.Trim('/')[..^"/mcp".Length]));

    [Theory]
    [InlineData("time", "Time", "https://time.now/")]
    [InlineData("tripit", "TripIt", "https://www.tripit.com/")]
    [InlineData("linkedin", "LinkedIn", "https://www.linkedin.com/")]
    [InlineData("amazon", "Amazon", "https://www.amazon.com/")]
    [InlineData("google-flights", "Google Flights", "https://www.google.com/travel/flights")]
    public async Task A_service_names_itself_and_the_website_it_wraps(string slug, string title, string website)
    {
        var serverInfo = await ServerInfoAsync(slug);

        Assert.Equal($"multiplex-{slug}", serverInfo.GetProperty("name").GetString());
        Assert.Equal(title, serverInfo.GetProperty("title").GetString());
        Assert.Equal(website, serverInfo.GetProperty("websiteUrl").GetString());
    }

    [Theory]
    [MemberData(nameof(Slugs))]
    public async Task Every_service_names_a_website(string slug)
    {
        var serverInfo = await ServerInfoAsync(slug);

        Assert.True(serverInfo.TryGetProperty("websiteUrl", out var website), $"{slug} names no website");
        Assert.True(Uri.TryCreate(website.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
            $"{slug}'s website is not an https URL: {website}");
    }

    [Theory]
    [MemberData(nameof(Slugs))]
    public async Task Every_service_shows_its_websites_icon_served_from_this_server(string slug)
    {
        var serverInfo = await ServerInfoAsync(slug);

        Assert.True(serverInfo.TryGetProperty("icons", out var icons), $"{slug} has no icon");
        var icon = Assert.Single(icons.EnumerateArray());
        // On this server's public address, which the client can reach; not on the website, which the spec
        // says a client should not trust by default.
        var src = icon.GetProperty("src").GetString()!;
        Assert.Equal($"{AuthTestApp.BaseUrl}/icons/{slug}.png", src);
        Assert.Equal("image/png", icon.GetProperty("mimeType").GetString());

        var response = await app.Client().GetAsync(new Uri(src).AbsolutePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var (width, height) = PngSize(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(width, height);
        Assert.Equal([$"{width}x{height}"], icon.GetProperty("sizes").EnumerateArray().Select(s => s.GetString()));
    }

    /// <summary>
    /// ChatGPT ignores <c>serverInfo.icons</c>: a custom app shows only the icon uploaded in its "Add custom MCP
    /// server" form, which takes a PNG of at most 10 KB, best at 256x256 (#138). Each service's icon is that file.
    /// </summary>
    [Theory]
    [MemberData(nameof(Slugs))]
    public async Task Every_services_icon_can_be_uploaded_to_ChatGPT(string slug)
    {
        var png = await app.Client().GetByteArrayAsync($"/icons/{slug}.png");

        Assert.True(png.Length <= 10 * 1024, $"{slug}'s icon is {png.Length} bytes; ChatGPT takes at most 10 KB");
        Assert.Equal((256, 256), PngSize(png));
    }

    [Theory]
    [InlineData("http://www.example.com/")]
    [InlineData("www.example.com")]
    [InlineData("ftp://www.example.com/")]
    public void A_website_that_is_not_an_absolute_https_address_is_refused_at_registration(string website)
    {
        var auth = new ServiceCollection().AddMultiplexAuth(new ConfigurationBuilder().Build());

        var error = Assert.Throws<ArgumentException>(() => auth.RequireAuthFor("example", "Example", websiteUrl: website));

        Assert.Equal("websiteUrl", error.ParamName);
    }

    /// <summary>
    /// <c>/icons</c> is open to anyone, so a service registered as <c>icons</c> would be served at
    /// <c>/icons/mcp</c> without a token. Registration refuses it, which also fails startup for such an endpoint.
    /// </summary>
    [Fact]
    public void The_icons_folder_cannot_be_a_service()
    {
        var auth = new ServiceCollection().AddMultiplexAuth(new ConfigurationBuilder().Build());

        var error = Assert.Throws<ArgumentException>(() => auth.RequireAuthFor("icons", "Icons"));

        Assert.Equal("slug", error.ParamName);
    }

    private async Task<JsonElement> ServerInfoAsync(string slug)
    {
        var endpoint = app.Services.GetRequiredService<AuthenticatedEndpoints>().FindBySlug(slug)!;
        var oauth = new OAuthDriver(app);
        // A service that acts on a person's site account needs a token even from this host.
        var token = endpoint.NeedsSiteAccount
            ? (await oauth.ConnectAsync(OAuthDriver.Account(site: slug), endpoint.Resource, slug)).AccessToken
            : null;

        var response = await oauth.InitializeAsync($"/{slug}/mcp", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var json = body.TrimStart().StartsWith('{')
            ? body
            : body.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))["data:".Length..].Trim();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("result").GetProperty("serverInfo").Clone();
    }

    /// <summary>A PNG's width and height, from its IHDR chunk (always first, right after the signature).</summary>
    private static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.True(png.Length > 24 && png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "not a PNG");
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }
}
