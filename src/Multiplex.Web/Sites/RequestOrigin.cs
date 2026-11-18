using System.Net;

namespace Multiplex.Web.Sites;

/// <summary>
/// Where a request came from, for rate limits, lockouts and the local-only endpoint guard.
/// Behind a local tunnel (Tailscale Funnel, cloudflared) every request arrives from loopback,
/// so the tunnel's forwarding headers are what tell an internet caller apart from this PC.
/// </summary>
public static class RequestOrigin
{
    /// <summary>
    /// The caller's address: the last <c>X-Forwarded-For</c> entry when the connection is from
    /// loopback (the entry the local tunnel appended; a remote caller cannot forge it), else the
    /// connection's own address.
    /// </summary>
    public static string ClientAddress(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && IPAddress.IsLoopback(remote) && LastForwardedFor(context) is { } forwarded)
            return forwarded;
        return remote?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Whether the request reached the server from outside this PC: a non-loopback connection; a
    /// loopback connection carrying a tunnel's forwarding headers or a non-loopback Host name; or a
    /// browser request whose <c>Origin</c> is not a loopback origin (a web page open on this PC can
    /// reach localhost, so only pages served from this PC count as local). MCP clients such as
    /// mcp-remote, Claude Code and the SDK send no <c>Origin</c>.
    /// </summary>
    public static bool IsFromOutsideThisPc(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote)) return true;
        // Loopback (or an in-process host with no address): judge by what a tunnel adds.
        if (LastForwardedFor(context) is not null || context.Request.Headers.ContainsKey("Forwarded")) return true;
        // Present at all, but not exactly one loopback origin ("null", from sandboxed or file pages, included).
        if (context.Request.Headers.TryGetValue("Origin", out var origins) && !(origins is [{ } origin] && IsLoopbackOrigin(origin)))
        {
            return true;
        }
        return !IsLoopbackHost(context.Request.Host.Host);
    }

    /// <summary>An <c>http</c> or <c>https</c> origin on localhost, 127.0.0.0/8 or [::1], any port.</summary>
    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && IsLoopbackHost(uri.Host);

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static string? LastForwardedFor(HttpContext context) =>
        context.Request.Headers["X-Forwarded-For"].ToString()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is [.., var last] ? last : null;
}
