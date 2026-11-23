using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Tests.Services.TripIt;
using Multiplex.Web.HtmlSites;
using Multiplex.Web.Services.Amazon;

namespace Multiplex.Tests.Services.Amazon;

/// <summary>The hand-over page on the real host, with a fake Amazon behind it: what a person sees and can do there.</summary>
public partial class AmazonHandOverPageTests : IDisposable
{
    private readonly FakeAmazon _amazon = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _browser;
    private readonly McpTestClient _mcp;

    public AmazonHandOverPageTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(AmazonClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _amazon);
            services.AddSingleton(new AmazonGate(new SiteGate(
                new SiteGateOptions { Site = "Amazon", PageSpacing = TimeSpan.Zero, PageJitter = TimeSpan.Zero, SequenceSpacing = TimeSpan.Zero, SequenceJitter = TimeSpan.Zero, ImageSpacing = TimeSpan.Zero },
                TimeProvider.System)));
        }));
        _browser = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _mcp = new McpTestClient(_factory.CreateClient(), token: null, path: "/amazon/mcp");
    }

    public void Dispose() => _factory.Dispose();

    [GeneratedRegex("(/check/amazon/[0-9a-f]{32})")]
    private static partial Regex CheckPath();

    [GeneratedRegex("<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">")]
    private static partial Regex HiddenField();

    /// <summary>Makes Amazon answer product pages with a check, calls the tool, and returns the path of the link it gave.</summary>
    private async Task<string> MeetACheckAsync(string fixture = "blocked-continue-shopping.html")
    {
        _amazon.On(u => u.AbsolutePath == "/dp/B088NRLMPV", _ => FakeAmazon.Fixture(fixture).WithCookie("session-id=check-session; Path=/"));
        var (isError, text) = await _mcp.CallAsync("amazon_get_product", new { product = "B088NRLMPV" });
        Assert.True(isError);
        Assert.Contains("Amazon wants a person to prove they're human", text);
        return CheckPath().Match(text).Groups[1].Value;
    }

    private async Task<HttpResponseMessage> AnswerAsync(string path, string page, string answer = "")
    {
        var token = HiddenField().Match(page);
        return await _browser.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [token.Groups[1].Value] = WebUtility.HtmlDecode(token.Groups[2].Value),
            ["answer"] = answer,
        }));
    }

    [Fact]
    public async Task TheToolsError_CarriesALink_ToAPageWithOneButton()
    {
        var path = await MeetACheckAsync();
        var sent = _amazon.Requests.Count;

        var response = await _browser.GetAsync(path);
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Amazon wants to know you&#39;re a person", page);
        Assert.Contains("I'm a person: carry on", page);
        Assert.Contains($"<form method=\"post\" action=\"{path}\"", page);
        Assert.DoesNotContain("<img", page);
        Assert.DoesNotContain("TOKEN-PLACEHOLDER", page); // Amazon's own form fields stay on the server
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        // Looking at the page asks Amazon nothing.
        Assert.Equal(sent, _amazon.Requests.Count);
    }

    [Fact]
    public async Task PressingTheButton_PassesItOnToAmazon_AndTheToolWorksAgain()
    {
        var path = await MeetACheckAsync();
        _amazon.On(u => u.AbsolutePath.EndsWith("/validateCaptcha"), _ => FakeAmazon.Redirect("/").WithCookie("x-amz-captcha-1=passed; Path=/"));
        var page = await _browser.GetStringAsync(path);

        var response = await AnswerAsync(path, page);
        var done = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1>Done</h1>", done);
        Assert.Contains("Amazon accepted it. Go back to your chat and ask it to try again.", done);
        var answer = Assert.Single(_amazon.Requests, r => r.Url.AbsolutePath == "/errors_page/validateCaptcha");
        Assert.Equal("session-id=check-session", answer.Headers["Cookie"]);

        _amazon.On(u => u.AbsolutePath == "/dp/B088NRLMPV", _ => FakeAmazon.Fixture("product-popular.html"));
        var (isError, text) = await _mcp.CallAsync("amazon_get_product", new { product = "B088NRLMPV" });
        Assert.False(isError, text);
        // The link is spent.
        Assert.Equal(HttpStatusCode.NotFound, (await _browser.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task AnAnswerWithoutTheAntiforgeryToken_IsRefused_AndNothingGoesToAmazon()
    {
        var path = await MeetACheckAsync();
        var sent = _amazon.Requests.Count;

        var response = await _browser.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["answer"] = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(sent, _amazon.Requests.Count);
        // The check is still there to answer properly.
        Assert.Equal(HttpStatusCode.OK, (await _browser.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task ACallerFromOutsideThisPc_WhoIsNotSignedInToMultiplex_GetsNoPage_AndCannotAnswer()
    {
        var path = await MeetACheckAsync();
        var sent = _amazon.Requests.Count;

        using var get = new HttpRequestMessage(HttpMethod.Get, path);
        get.Headers.Add("X-Forwarded-For", "203.0.113.9");
        var page = await _browser.SendAsync(get);
        using var post = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["answer"] = "" }) };
        post.Headers.Add("X-Forwarded-For", "203.0.113.9");
        var answer = await _browser.SendAsync(post);

        using var image = new HttpRequestMessage(HttpMethod.Get, path + "/picture");
        image.Headers.Add("X-Forwarded-For", "203.0.113.9");
        var picture = await _browser.SendAsync(image);

        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, picture.StatusCode);
        Assert.DoesNotContain("carry on", await page.Content.ReadAsStringAsync());
        Assert.NotEqual(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(sent, _amazon.Requests.Count);
    }

    [Theory]
    [InlineData("/check/amazon/00000000000000000000000000000000")]
    [InlineData("/check/nosuchservice/00000000000000000000000000000000")]
    public async Task ALinkThatIsNotAnOpenCheck_SaysThereIsNothingToAnswer(string path)
    {
        var response = await _browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("This link has expired, or the check was already answered.", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _browser.GetAsync(path + "/picture")).StatusCode);
    }

    [Fact]
    public async Task TheTypeTheCharactersCheck_ShowsAmazonsPictureFromThisServer_AndSendsWhatWasTyped()
    {
        var path = await MeetACheckAsync("blocked-robot-check.html");
        var page = await _browser.GetStringAsync(path);
        Assert.Contains($"<img src=\"{path}/picture\"", page);
        Assert.Contains("name=\"answer\"", page);
        Assert.DoesNotContain("images-na.ssl-images-amazon.com", page); // the person's browser never talks to Amazon

        var picture = await _browser.GetAsync(path + "/picture");
        Assert.Equal("image/jpeg", picture.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", picture.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("default-src 'none'; sandbox", picture.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(FakeAmazon.Jpeg, await picture.Content.ReadAsByteArrayAsync());

        // A wrong answer comes back with a note and a new form; a right one is done.
        _amazon.On(u => u.AbsolutePath.EndsWith("/validateCaptcha"), _ => FakeAmazon.Fixture("blocked-robot-check.html"));
        var wrong = await (await AnswerAsync(path, page, "aaaa")).Content.ReadAsStringAsync();
        Assert.Contains("Amazon did not accept that. Try again.", wrong);
        Assert.Contains("name=\"answer\"", wrong);

        _amazon.On(u => u.AbsolutePath.EndsWith("/validateCaptcha"), _ => FakeAmazon.Redirect("/s?k=cable"));
        var right = await (await AnswerAsync(path, wrong, "kx7pq")).Content.ReadAsStringAsync();
        Assert.Contains("Amazon accepted it.", right);
        Assert.EndsWith("&field-keywords=kx7pq", _amazon.Requests[^1].Url.Query);
    }
}
