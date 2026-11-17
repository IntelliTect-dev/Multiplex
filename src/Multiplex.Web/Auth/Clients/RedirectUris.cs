namespace Multiplex.Web.Auth.Clients;

internal static class RedirectUris
{
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "[::1]" };

    /// <summary>
    /// A redirect URI a client may register: absolute, no fragment or user info, and either HTTPS or
    /// plain HTTP to a loopback host (any port; used by Claude Code, mcp-remote and MCP Inspector).
    /// </summary>
    public static bool IsAllowed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Contains('#')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0)
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && LoopbackHosts.Contains(uri.Host));
    }

    /// <summary>
    /// Whether an authorization request's redirect URI matches a registered one: exactly, or, for
    /// a plain-HTTP loopback URI, exactly except for the port. RFC 8252 section 7.3 requires the
    /// latter because native clients (Claude Code, mcp-remote) pick a free port each time they
    /// sign in but register once.
    /// </summary>
    public static bool Matches(string registered, string requested)
    {
        if (string.Equals(registered, requested, StringComparison.Ordinal)) return true;
        if (!Uri.TryCreate(registered, UriKind.Absolute, out var r) || !Uri.TryCreate(requested, UriKind.Absolute, out var q))
            return false;
        return r.Scheme == Uri.UriSchemeHttp && q.Scheme == Uri.UriSchemeHttp
            && LoopbackHosts.Contains(r.Host)
            && string.Equals(r.Host, q.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.PathAndQuery, q.PathAndQuery, StringComparison.Ordinal)
            && q.UserInfo.Length == 0 && q.Fragment.Length == 0;
    }
}
