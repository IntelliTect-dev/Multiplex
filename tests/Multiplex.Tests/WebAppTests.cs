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
    public async Task TimeMcp_ListsTimeTool()
    {
        var body = await ListTools("/time/mcp");

        Assert.Contains("\"get_local_time\"", body);
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
