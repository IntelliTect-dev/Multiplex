using System.Security.Claims;

namespace Multiplex.Web.Sites;

// The seams between the three parts of an authenticated site service (see
// docs/designs/6-tripit-mcp-server-spec.md, section 4):
//
//   Auth/             OAuth 2.1 authorization + resource server for every /{slug}/mcp endpoint.
//   SignIn/           the connect pages: passcode gate, live mirrored site sign-in, browser
//                     profiles, and the encrypted per-account site sessions.
//   Services/{Site}/  the site's MCP tools, which call the site with the account's session.
//
// Each part depends only on these contracts, never on another part's classes.

/// <summary>A signed-in account on a site, e.g. one TripIt account.</summary>
/// <param name="Site">The endpoint slug, e.g. <c>tripit</c>.</param>
/// <param name="AccountId">Stable, site-issued account identifier (for TripIt, the profile ref).</param>
/// <param name="Emails">The account's email addresses, as the site reports them; used for the allowlist.</param>
/// <param name="DisplayName">A human-readable name for logs and consent text; may be empty.</param>
public sealed record SiteAccount(string Site, string AccountId, IReadOnlyList<string> Emails, string DisplayName);

/// <summary>
/// Implemented by Auth/. The connect pages call it when a pending authorization ends, and
/// send the person's browser to the returned URL (the client's redirect URI with
/// <c>code</c>/<c>error</c>, <c>state</c> and <c>iss</c>).
/// </summary>
public interface IAuthorizationCompleter
{
    /// <summary>Describes a pending authorization so the connect pages can show who is asking.</summary>
    Task<PendingAuthorization?> FindAsync(string pendingId, CancellationToken ct);

    /// <summary>Issues a single-use authorization code bound to <paramref name="account"/>.</summary>
    Task<Uri> CompleteAsync(string pendingId, SiteAccount account, CancellationToken ct);

    /// <summary>Ends the authorization with an OAuth error (e.g. <c>access_denied</c>).</summary>
    Task<Uri> FailAsync(string pendingId, string error, string description, CancellationToken ct);
}

/// <param name="Id">Opaque id carried in the connect page URL.</param>
/// <param name="Site">The slug whose endpoint the client asked for (from the <c>resource</c> parameter).</param>
/// <param name="ClientName">The client's self-declared name, e.g. "Claude" or "ChatGPT"; display only.</param>
/// <param name="ExpiresAt">When the pending authorization lapses.</param>
/// <param name="ReturnsTo">The host (and port) the browser is sent back to with the code, taken from the
/// validated redirect URI. Shown on the consent page so a client claiming someone else's name is visible.</param>
public sealed record PendingAuthorization(string Id, string Site, string ClientName, DateTimeOffset ExpiresAt, string? ReturnsTo = null);

/// <summary>Implemented by Auth/. Called when a site session is gone for good (signed out).</summary>
public interface IGrantRevoker
{
    /// <summary>Revokes every access and refresh token issued for the account, so clients must reconnect.</summary>
    Task RevokeAccountAsync(string site, string accountId, CancellationToken ct);
}

/// <summary>
/// Implemented by SignIn/. Encrypted storage of one opaque session snapshot per account
/// (for TripIt: the JSON shape <c>tools/extract-session.js</c> writes:
/// <c>{ cookie, csrf_token, spa_headers, captured_at }</c>).
/// </summary>
public interface ISiteSessionStore
{
    Task<string?> GetAsync(string site, string accountId, CancellationToken ct);
    Task SetAsync(string site, string accountId, string snapshotJson, CancellationToken ct);
    Task RemoveAsync(string site, string accountId, CancellationToken ct);
}

/// <summary>
/// Implemented by SignIn/. Re-reads the account's session from its server-owned browser
/// profile after the site rejected it. Returns the new snapshot, or <c>null</c> when the
/// person is signed out (in which case the implementation has already revoked the
/// account's grants through <see cref="IGrantRevoker"/>).
/// </summary>
public interface ISiteSessionRefresher
{
    Task<string?> RefreshAsync(string site, string accountId, CancellationToken ct);
}

/// <summary>Claim names Auth/ puts on the principal of a validated MCP request.</summary>
public static class SiteClaims
{
    public const string Site = "multiplex:site";
    public const string AccountId = "multiplex:account";
    public const string ClientId = "client_id";

    /// <summary>The account a validated MCP request acts for, or null when unauthenticated.</summary>
    public static (string Site, string AccountId)? AccountOf(ClaimsPrincipal? user) =>
        user?.FindFirst(Site)?.Value is { } site && user.FindFirst(AccountId)?.Value is { } account
            ? (site, account)
            : null;
}
