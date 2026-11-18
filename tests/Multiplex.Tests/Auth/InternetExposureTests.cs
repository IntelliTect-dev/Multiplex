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

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://localhost.evil.example")]
    public async Task OpenEndpoint_FromAWebPageThatIsNotLocal_IsNotFound(string origin)
    {
        using var request = Initialize("/time/mcp");
        request.Headers.Host = "localhost";
        request.Headers.Add("Origin", origin);
        var response = await _app.Client().SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpenEndpoint_FromALocalWebPage_StillWorks()
    {
        using var request = Initialize("/time/mcp");
        request.Headers.Host = "localhost";
        request.Headers.Add("Origin", "http://localhost:6274");
        var response = await _app.Client().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("http://localhost:6274", false)]
    [InlineData("https://localhost", false)]
    [InlineData("http://LOCALHOST:5190", false)]
    [InlineData("http://127.0.0.1:5190", false)]
    [InlineData("http://[::1]:8080", false)]
    [InlineData("https://evil.example", true)]
    [InlineData("null", true)]
    [InlineData("http://localhost.evil.example", true)]
    [InlineData("http://127.0.0.1.evil.example", true)]
    [InlineData("chrome-extension://abcdefghijklmnop", true)]
    [InlineData("file://", true)]
    public void IsFromOutsideThisPc_JudgesTheOriginOfBrowserRequests(string? origin, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("localhost:5190");
        if (origin is not null) context.Request.Headers.Append("Origin", origin);
        Assert.Equal(expected, RequestOrigin.IsFromOutsideThisPc(context));
    }

    [Fact]
    public void IsFromOutsideThisPc_WithTwoOrigins_IsOutside()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Origin = new Microsoft.Extensions.Primitives.StringValues(["http://localhost:6274", "https://evil.example"]);
        Assert.True(RequestOrigin.IsFromOutsideThisPc(context));
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
    [InlineData("::ffff:127.0.0.1", null, "localhost", false)]
    [InlineData("::ffff:198.51.100.4", null, "localhost", true)]
    [InlineData("127.0.0.1", "not-an-address", "localhost", true)]
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

    [Theory]
    // Behind a tunnel on this PC: the last X-Forwarded-For entry, if it is an address.
    [InlineData("127.0.0.1", "203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:127.0.0.1", "203.0.113.7", "203.0.113.7")]
    [InlineData("127.0.0.1", "::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("127.0.0.1", "2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("127.0.0.1", "unknown", "127.0.0.1")]
    [InlineData("127.0.0.1", "203.0.113.7, not-an-address", "127.0.0.1")]
    // A direct connection: its own address, never a header the caller sent.
    [InlineData("198.51.100.4", "203.0.113.7", "198.51.100.4")]
    [InlineData("::ffff:198.51.100.4", null, "198.51.100.4")]
    // IPv6 callers are counted by /64, since one host commonly holds a whole /64.
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", null, "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", null, "2001:db8:1:2::/64")]
    [InlineData("fe80::1:2:3:4%12", null, "fe80::/64")]
    [InlineData("::1", null, "::1")]
    public void ClientAddress_IsTheKeyRateLimitsAndLockoutsCountACallerUnder(string remote, string? forwardedFor, string expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        Assert.Equal(expected, RequestOrigin.ClientAddress(context));
    }
}
