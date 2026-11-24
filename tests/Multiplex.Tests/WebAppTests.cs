using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Multiplex.Tests;

public class WebAppTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Index_ReturnsHelloWorld()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Hello, World!", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Index_ListsTheAmazonMcpEndpoint()
    {
        var body = await _client.GetStringAsync("/");

        Assert.Matches("<code>[^<]*/amazon/mcp</code>", body);
    }

    [Fact]
    public async Task Index_DoesNotAdvertiseRootMcp()
    {
        var body = await _client.GetStringAsync("/");

        Assert.DoesNotMatch("<code>[^<]*://[^/<]+/mcp</code>|<code>/mcp</code>", body);
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RootMcp_IsNotMapped()
    {
        var response = await _client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task TheTimeService_IsGone()
    {
        // Removed on the owner's decision (#144): no address, and nothing on the index page.
        var response = await _client.PostAsync("/time/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("/time/mcp", await _client.GetStringAsync("/"));
    }

    private async Task<string> ListTools(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
