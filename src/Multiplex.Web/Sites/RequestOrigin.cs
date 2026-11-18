using System.Net;
using System.Net.Sockets;

namespace Multiplex.Web.Sites;

/// <summary>
/// Where a request came from, for rate limits, lockouts and the local-only endpoint guard.
/// Behind a local tunnel (Tailscale Funnel or Serve, cloudflared) every request arrives from
/// loopback, so the tunnel's forwarding headers are what tell an internet caller apart from this PC.
/// </summary>
public static class RequestOrigin
{
    /// <summary>
    /// The key rate limits and lockouts count a caller under: the connection's address or, when the
    /// connection is from loopback (a tunnel on this PC), the last <c>X-Forwarded-For</c> entry if it is
    /// an IP address. Tailscale Funnel and Serve overwrite <c>X-Forwarded-For</c> with the real caller's
    /// address (measured), so a remote caller cannot choose the entry used; a proxy that appends instead
    /// also puts what it saw last. IPv6 addresses are counted by their /64, since one host commonly
    /// holds a whole /64; IPv4-mapped IPv6 addresses count as the IPv4 address.
    /// </summary>
    public static string ClientAddress(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && IPAddress.IsLoopback(Unmapped(remote))
            && LastForwardedFor(context) is { } last && IPAddress.TryParse(last, out var forwarded))
        {
            return KeyOf(forwarded);
        }
        return remote is null ? "unknown" : KeyOf(remote);
    }

    /// <summary>
    /// Whether the request reached the server from outside this PC: a non-loopback connection; a
    /// loopback connection carrying a tunnel's forwarding headers (any <c>X-Forwarded-For</c>, address
    /// or not, or <c>Forwarded</c>) or a non-loopback Host name; or a browser request whose <c>Origin</c>
    /// is not a loopback origin (a web page open on this PC can reach localhost, so only pages served
    /// from this PC count as local). MCP clients such as mcp-remote, Claude Code and the SDK send no
    /// <c>Origin</c>.
    /// </summary>
    public static bool IsFromOutsideThisPc(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(Unmapped(remote))) return true;
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
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(Unmapped(address)));

    private static IPAddress Unmapped(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>The address as text; for IPv6 (other than loopback), its /64 prefix.</summary>
    private static string KeyOf(IPAddress address)
    {
        address = Unmapped(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(address)) return address.ToString();

        var bytes = address.GetAddressBytes(); // the 16 address bytes; any scope id is dropped
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    private static string? LastForwardedFor(HttpContext context) =>
        context.Request.Headers["X-Forwarded-For"].ToString()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is [.., var last] ? last : null;
}
