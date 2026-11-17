using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.Auth;

/// <summary>
/// Once a tunnel publishes the server, endpoints without sign-in must not be reachable from the
/// internet, and lockouts/rate limits must key on the real caller rather than on the tunnel.
/// </summary>
public class InternetExposureTests : IClassFixture<AuthTestApp>
{
    private readonly AuthTestApp _app;
    public InternetExposureTests(AuthTestApp app) => _app = app;

    private static HttpRequestMessage Initialize(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    [Fact]
    public async Task OpenEndpoint_ThroughATunnel_IsNotFound()
    {
        using var request = Initialize("/time/mcp");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        var response = await _app.Client().SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpenEndpoint_UnderThePublicHostName_IsNotFound()
    {
        using var request = Initialize("/time/mcp");
        request.Headers.Host = "box.example.ts.net";
        var response = await _app.Client().SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpenEndpoint_FromThisPc_StillWorks()
    {
        using var request = Initialize("/time/mcp");
        request.Headers.Host = "localhost";
        var response = await _app.Client().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedEndpoint_ThroughATunnel_StillChallenges()
    {
        using var request = Initialize("/tripit/mcp");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        var response = await _app.Client().SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", null, "localhost", false)]
    [InlineData("::1", null, "[::1]", false)]
    [InlineData("127.0.0.1", "203.0.113.7", "localhost", true)]
    [InlineData("127.0.0.1", null, "box.example.ts.net", true)]
    [InlineData("198.51.100.4", null, "localhost", true)]
    public void IsFromOutsideThisPc(string remote, string? forwardedFor, string host, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Host = new HostString(host);
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        Assert.Equal(expected, RequestOrigin.IsFromOutsideThisPc(context));
    }

    [Fact]
    public void ClientAddress_BehindATunnel_IsTheForwardedCaller()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-For"] = "10.0.0.1, 203.0.113.7";
        Assert.Equal("203.0.113.7", RequestOrigin.ClientAddress(context));
    }
}
