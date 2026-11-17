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
}
