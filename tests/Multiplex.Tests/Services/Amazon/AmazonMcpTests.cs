using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Tests.Services.TripIt;
using Multiplex.Web.Browser;
using Multiplex.Web.HtmlSites;
using Multiplex.Web.Services.Amazon;
using Multiplex.Web.SignIn;

namespace Multiplex.Tests.Services.Amazon;

/// <summary>The Amazon service through its MCP address, on the real host with a fake Amazon behind it.</summary>
public class AmazonMcpTests : IDisposable
{
    private readonly FakeAmazon _amazon = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly McpTestClient _mcp;

    public AmazonMcpTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(AmazonClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _amazon);
            // The same gate, without the real waits between requests.
            services.AddSingleton(new AmazonGate(new SiteGate(
                new SiteGateOptions { Site = "Amazon", PageSpacing = TimeSpan.Zero, PageJitter = TimeSpan.Zero, SequenceSpacing = TimeSpan.Zero, SequenceJitter = TimeSpan.Zero, ImageSpacing = TimeSpan.Zero },
                TimeProvider.System)));
        }));
        _mcp = new McpTestClient(_factory.CreateClient(), token: null, path: "/amazon/mcp");
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task TheAmazonAddress_ListsExactlyItsTwoTools()
    {
        var tools = await _mcp.ListToolsAsync();

        Assert.Equal(["amazon_get_product", "amazon_search"], tools.Select(t => t.GetProperty("name").GetString()).Order());
    }

    [Fact]
    public async Task NoOtherAddress_ListsTheAmazonTools()
    {
        var time = new McpTestClient(_factory.CreateClient(), token: null, path: "/time/mcp");

        var tools = await time.ListToolsAsync();

        Assert.DoesNotContain(tools, t => t.GetProperty("name").GetString()!.StartsWith("amazon_"));
    }

    [Fact]
    public async Task Sort_IsAdvertisedAsAnEnum_OfRelevancePriceAndRating()
    {
        var search = (await _mcp.ListToolsAsync()).Single(t => t.GetProperty("name").GetString() == "amazon_search");

        var schema = search.GetProperty("inputSchema");
        var sort = schema.GetProperty("properties").GetProperty("sort").GetRawText();
        Assert.Contains("\"relevance\"", sort);
        Assert.Contains("\"price\"", sort);
        Assert.Contains("\"rating\"", sort);
        Assert.Equal(["query"], schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Search_ReturnsSortedResults_WithEverySortableValueAPlainNumber()
    {
        var (isError, text) = await _mcp.CallAsync("amazon_search", new { query = "usb c charging cable", sort = "rating", pages = 2 });

        Assert.False(isError, text);
        var answer = JsonDocument.Parse(text).RootElement;
        Assert.Equal("usb c charging cable", answer.GetProperty("query").GetString());
        Assert.Equal("rating", answer.GetProperty("sort").GetString());
        Assert.Equal("USD", answer.GetProperty("currency").GetString());
        Assert.Equal(3, answer.GetProperty("appliedMinimums").GetProperty("minRating").GetDouble());
        Assert.Equal(20, answer.GetProperty("appliedMinimums").GetProperty("minRatingsCount").GetInt32());
        Assert.Equal(JsonValueKind.Number, answer.GetProperty("ratingBaseline").ValueKind);
        Assert.Equal([1, 2], answer.GetProperty("pagesRead").EnumerateArray().Select(p => p.GetInt32()));
        Assert.Equal(3, answer.GetProperty("nextPage").GetInt32());
        Assert.Equal(12, answer.GetProperty("sponsoredRemoved").GetInt32());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("handOver").ValueKind);
        Assert.Contains(answer.GetProperty("availableFilters").EnumerateArray(), g => g.GetProperty("name").GetString() == "Brands");

        var results = answer.GetProperty("results").EnumerateArray().ToList();
        Assert.NotEmpty(results);
        foreach (var result in results)
        {
            // Plain numbers, so a caller can re-sort: $r.results | Sort-Object price
            foreach (var number in new[] { "amazonRank", "price", "rating", "ratingsCount", "weightedRating" })
                Assert.Equal(JsonValueKind.Number, result.GetProperty(number).ValueKind);
            Assert.Contains(result.GetProperty("boughtInPastMonth").ValueKind, new[] { JsonValueKind.Number, JsonValueKind.Null });
            Assert.StartsWith("https://www.amazon.com/dp/", result.GetProperty("url").GetString());
            Assert.True(result.GetProperty("rating").GetDouble() >= 3);
            Assert.True(result.GetProperty("ratingsCount").GetInt32() >= 20);
        }
        var weighted = results.Select(r => r.GetProperty("weightedRating").GetDouble()).ToList();
        Assert.Equal(weighted.OrderDescending(), weighted);
    }

    [Fact]
    public async Task Search_ByPrice_PutsTheCheapestFirst_AndAsksAmazonForItsPriceOrder()
    {
        var (isError, text) = await _mcp.CallAsync("amazon_search", new { query = "cable", sort = "price", maxPrice = 10 });

        Assert.False(isError, text);
        var prices = JsonDocument.Parse(text).RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("price").GetDecimal()).ToList();
        Assert.Equal(prices.Order(), prices);
        Assert.All(prices, p => Assert.True(p <= 10));
        var request = _amazon.Fetches.Single();
        Assert.Contains("s=price-asc-rank", request.Url.Query);
        Assert.Contains(WebUtility.UrlEncode("p_36:-1000,p_72:1248879011"), request.Url.Query);
    }

    [Fact]
    public async Task GetProduct_ReturnsTheProductAsJson_AndItsMainImageAsAnImage()
    {
        var (status, message) = await _mcp.SendAsync("tools/call", new { name = "amazon_get_product", arguments = new { product = "https://www.amazon.com/dp/B088NRLMPV?th=1" } });

        Assert.Equal(HttpStatusCode.OK, status);
        var content = message!.Value.GetProperty("result").GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, content.Count);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        var product = JsonDocument.Parse(content[0].GetProperty("text").GetString()!).RootElement;
        Assert.Equal("B088NRLMPV", product.GetProperty("asin").GetString());
        Assert.Equal(7.99m, product.GetProperty("price").GetDecimal());
        Assert.Equal(4.7, product.GetProperty("rating").GetDouble());
        Assert.Equal(91951, product.GetProperty("ratingsCount").GetInt32());
        Assert.Equal(87, product.GetProperty("ratingHistogram").GetProperty("5").GetInt32());
        Assert.Equal(8, product.GetProperty("customersSay").GetProperty("aspects").GetArrayLength());
        Assert.False(product.TryGetProperty("mainImageUrl", out _));

        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("image/jpeg", content[1].GetProperty("mimeType").GetString());
        Assert.Equal(FakeAmazon.Jpeg, Convert.FromBase64String(content[1].GetProperty("data").GetString()!));
    }

    [Fact]
    public async Task ARobotCheck_IsAToolError_ThatSaysAmazonRequestsArePaused()
    {
        _amazon.On("/s", () => FakeAmazon.Fixture("blocked-continue-shopping.html"));

        var (isError, text) = await _mcp.CallAsync("amazon_search", new { query = "cable" });

        Assert.True(isError);
        Assert.Contains("it showed a robot check", text);
        Assert.Contains("Amazon requests are paused until", text);
    }

    [Fact]
    public async Task AnUnknownAsin_IsAToolError_NamingIt()
    {
        _amazon.On("/dp/B0ZZZZZZZ8", () => FakeAmazon.Fixture("not-found.html", 404));

        var (isError, text) = await _mcp.CallAsync("amazon_get_product", new { product = "B0ZZZZZZZ8" });

        Assert.True(isError);
        Assert.Contains("Amazon has no product with the ASIN B0ZZZZZZZ8.", text);
    }

    [Theory]
    [InlineData("""{"query":"cable","pages":11}""", "pages must be 1 to 10")]
    [InlineData("""{"query":"cable","minRating":2}""", "nothing rated below 3 stars")]
    [InlineData("""{"query":"cable","filters":["p_123:3271","p_123:234478"]}""", "one per group")]
    [InlineData("""{"query":"cable","filters":["p_123:3271&k=x"]}""", "not a filter code")]
    public async Task AnInputOutOfRange_IsAToolError_AndAmazonIsNotContacted(string arguments, string expected)
    {
        var (isError, text) = await _mcp.CallAsync("amazon_search", JsonSerializer.Deserialize<JsonElement>(arguments));

        Assert.True(isError);
        Assert.Contains(expected, text);
        Assert.Empty(_amazon.Requests);
    }

    [Fact]
    public async Task SomethingThatIsNotAProduct_IsAToolError_AndAmazonIsNotContacted()
    {
        var (isError, text) = await _mcp.CallAsync("amazon_get_product", new { product = "usb c cable" });

        Assert.True(isError);
        Assert.Contains("is not an Amazon product", text);
        Assert.Empty(_amazon.Requests);
    }

    [Fact]
    public void TheAmazonService_UsesNoBrowser()
    {
        // The owner's rule (#96): no provider uses a browser without approval. This service reads
        // over plain HTTP, so neither its tools nor its client may take a browser.
        var browserTypes = new[] { typeof(BrowserService), typeof(SiteBrowserManager) };
        var parameters = typeof(AmazonTools).GetMethods(BindingFlags.Public | BindingFlags.Static).SelectMany(m => m.GetParameters())
            .Concat(typeof(AmazonClient).GetConstructors().SelectMany(c => c.GetParameters()));

        Assert.DoesNotContain(parameters, p => browserTypes.Contains(p.ParameterType));
        Assert.DoesNotContain(typeof(AmazonClient).Assembly.GetTypes().Where(t => t.Namespace == typeof(AmazonClient).Namespace)
            .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)),
            f => f.FieldType.Namespace?.StartsWith("PuppeteerSharp") == true || browserTypes.Contains(f.FieldType));
    }
}
