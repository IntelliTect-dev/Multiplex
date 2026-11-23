using System.Net;
using System.Text;
using Multiplex.Tests.Auth;

namespace Multiplex.Tests.Services.GoogleFlights;

/// <summary>
/// Google Flights acts for no one's site account, so, like Time, it needs a sign-in token from anywhere but the
/// machine Multiplex runs on (#80's access rule). That is what lets an assistant reach it over HTTPS.
/// </summary>
public class GoogleFlightsAccessTests : IClassFixture<AuthTestApp>
{
    private const string Challenge =
        "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/google-flights/mcp\", scope=\"google-flights\"";

    private readonly AuthTestApp _app;

    public GoogleFlightsAccessTests(AuthTestApp app) => _app = app;

    private static HttpRequestMessage Initialize()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/google-flights/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    [Fact]
    public async Task From_the_internet_without_a_token_it_asks_for_sign_in()
    {
        using var request = Initialize();
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");

        var response = await _app.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Challenge, response.Headers.NonValidated["WWW-Authenticate"].ToString());
    }

    [Fact]
    public async Task From_this_machine_it_works_without_a_token()
    {
        using var request = Initialize();
        request.Headers.Host = "localhost";

        var response = await _app.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Its_resource_metadata_names_it_for_the_assistant_that_connects()
    {
        var response = await _app.Client().GetAsync("/.well-known/oauth-protected-resource/google-flights/mcp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"resource_name\":\"Google Flights\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
