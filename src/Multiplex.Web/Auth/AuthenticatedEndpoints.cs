using Microsoft.Extensions.Options;

namespace Multiplex.Web.Auth;

/// <summary>One <c>/{slug}/mcp</c> endpoint that requires a bearer token.</summary>
/// <param name="Slug">Lower-case endpoint slug, also the OAuth scope, e.g. <c>tripit</c>.</param>
/// <param name="DisplayName">Name shown in protected resource metadata.</param>
/// <param name="Resource">The canonical resource URL and token audience: <c>&lt;issuer&gt;/&lt;slug&gt;/mcp</c>.</param>
/// <param name="ResourceMetadataUrl">Where its RFC 9728 metadata is served.</param>
/// <param name="NeedsSiteAccount">Whether its tools act on a person's site account (see <see cref="MultiplexAuthBuilder.RequireAuthFor"/>).</param>
/// <param name="WebsiteUrl">The website the service wraps, which it names in its <c>serverInfo</c> (#135).</param>
public sealed record AuthenticatedEndpoint(
    string Slug, string DisplayName, string Resource, string ResourceMetadataUrl, bool NeedsSiteAccount = true, string? WebsiteUrl = null);

/// <summary>The registry of authenticated slugs (see <see cref="MultiplexAuthBuilder.RequireAuthFor"/>).</summary>
public sealed class AuthenticatedEndpoints
{
    private readonly Lazy<IReadOnlyList<AuthenticatedEndpoint>> _all;

    public AuthenticatedEndpoints(IOptions<AuthenticatedEndpointOptions> options, AuthIssuer issuer)
    {
        _all = new Lazy<IReadOnlyList<AuthenticatedEndpoint>>(() => options.Value.Endpoints
            .DistinctBy(e => e.Slug, StringComparer.Ordinal)
            .Select(e => new AuthenticatedEndpoint(
                e.Slug,
                e.DisplayName,
                $"{issuer.Issuer}/{e.Slug}/mcp",
                $"{issuer.Issuer}/.well-known/oauth-protected-resource/{e.Slug}/mcp",
                e.NeedsSiteAccount,
                e.WebsiteUrl))
            .ToList());
    }

    /// <summary>In registration order; the first is described by the root resource metadata.</summary>
    public IReadOnlyList<AuthenticatedEndpoint> All => _all.Value;

    /// <summary>Finds a slug, ignoring case (routing does, so protection must too).</summary>
    public AuthenticatedEndpoint? FindBySlug(string slug) =>
        All.FirstOrDefault(e => string.Equals(e.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds the endpoint whose resource URL is exactly <paramref name="resource"/>.</summary>
    public AuthenticatedEndpoint? FindByResource(string resource) =>
        All.FirstOrDefault(e => string.Equals(e.Resource, resource, StringComparison.Ordinal));

    /// <summary>
    /// The endpoint a request path belongs to, when its first two segments are <c>{slug}/mcp</c> for an
    /// authenticated slug. Matching ignores case and empty segments, so no spelling that routing would
    /// send to the MCP endpoint slips past protection.
    /// </summary>
    public AuthenticatedEndpoint? FindByPath(PathString path)
    {
        if (!path.HasValue || All.Count == 0) return null;

        var segments = path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && string.Equals(segments[1], "mcp", StringComparison.OrdinalIgnoreCase)
            ? FindBySlug(segments[0])
            : null;
    }
}
