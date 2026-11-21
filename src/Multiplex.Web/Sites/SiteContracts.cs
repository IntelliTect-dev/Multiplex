using System.Security.Claims;

namespace Multiplex.Web.Sites;

// The seams between the parts of an authenticated site service (see
// docs/designs/6-tripit-mcp-server-spec.md, section 4):
//
//   Identity/         who the person is: sign-in with Microsoft, the email allowlist, and the
//                     access rule every request passes (pages, MCP endpoints, the host exemption).
//   Auth/             OAuth 2.1 authorization + resource server for every /{slug}/mcp endpoint.
//   SignIn/           the connect pages: live mirrored site sign-in, browser profiles, and the
//                     encrypted site sessions, kept per person and per site account.
//   Services/{Site}/  the site's MCP tools, which call the site with the person's session.
//
// Each part depends only on these contracts, never on another part's classes. An "owner" is
// the Multiplex user (a person who signed in) that a grant or a site session belongs to.

/// <summary>A signed-in account on a site, e.g. one TripIt account.</summary>
/// <param name="Site">The endpoint slug, e.g. <c>tripit</c>.</param>
/// <param name="AccountId">Stable, site-issued account identifier (for TripIt, the profile ref).</param>
/// <param name="Emails">The account's confirmed email addresses, as the site reports them; display only.</param>
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

    /// <summary>
    /// Issues a single-use authorization code for <paramref name="owner"/>, who must be the person
    /// that started the authorization, bound to <paramref name="account"/> (null for a service that
    /// uses no site account).
    /// </summary>
    /// <exception cref="InvalidOperationException">The pending authorization is unknown, already ended, or expired.</exception>
    /// <exception cref="ArgumentException">Another person started it, or the account belongs to another site.</exception>
    Task<Uri> CompleteAsync(string pendingId, string owner, SiteAccount? account, CancellationToken ct);

    /// <summary>Ends the authorization with an OAuth error (e.g. <c>access_denied</c>).</summary>
    Task<Uri> FailAsync(string pendingId, string error, string description, CancellationToken ct);
}

/// <param name="Id">Opaque id carried in the connect page URL.</param>
/// <param name="Site">The slug whose endpoint the client asked for (from the <c>resource</c> parameter).</param>
/// <param name="ClientName">The client's self-declared name, e.g. "Claude" or "ChatGPT"; display only.</param>
/// <param name="ExpiresAt">When the pending authorization lapses.</param>
/// <param name="ReturnsTo">The host (and port) the browser is sent back to with the code, taken from the
/// validated redirect URI. Shown on the consent page so a client claiming someone else's name is visible.</param>
/// <param name="Owner">The person who started it by signing in at <c>/oauth/authorize</c>; only they may finish it.</param>
/// <param name="ServiceName">The service's display name, e.g. "TripIt".</param>
public sealed record PendingAuthorization(
    string Id, string Site, string ClientName, DateTimeOffset ExpiresAt, string? ReturnsTo, string Owner, string ServiceName);

/// <summary>Implemented by Auth/. Called when a site session is gone for good (signed out).</summary>
public interface IGrantRevoker
{
    /// <summary>Revokes every access and refresh token <paramref name="owner"/> holds for the account, so their clients must reconnect.</summary>
    Task RevokeAccountAsync(string owner, string site, string accountId, CancellationToken ct);
}

/// <summary>
/// Implemented by SignIn/. Encrypted storage of one opaque session snapshot per person and site account
/// (for TripIt: the JSON shape <c>tools/extract-session.js</c> writes:
/// <c>{ cookie, csrf_token, spa_headers, captured_at }</c>). One person's sessions are never read for
/// another, even for the same site account.
/// </summary>
public interface ISiteSessionStore
{
    Task<string?> GetAsync(string owner, string site, string accountId, CancellationToken ct);
    Task SetAsync(string owner, string site, string accountId, string snapshotJson, CancellationToken ct);
    Task RemoveAsync(string owner, string site, string accountId, CancellationToken ct);
}

/// <summary>
/// Implemented by SignIn/. Re-reads the person's session for the account from its server-owned
/// browser profile after the site rejected it. Returns the new snapshot, or <c>null</c> when they
/// are signed out of the site (in which case the implementation has already revoked their grants
/// for the account through <see cref="IGrantRevoker"/>).
/// </summary>
public interface ISiteSessionRefresher
{
    Task<string?> RefreshAsync(string owner, string site, string accountId, CancellationToken ct);
}

/// <summary>Claim names on a validated request's principal.</summary>
public static class SiteClaims
{
    /// <summary>The person (Multiplex user) the request is for; on MCP tokens and on the web sign-in cookie.</summary>
    public const string Owner = "multiplex:user";
    public const string Site = "multiplex:site";
    public const string AccountId = "multiplex:account";
    public const string ClientId = "client_id";

    /// <summary>
    /// The person and site account a validated MCP request acts for, or null when it has none
    /// (unauthenticated, or a grant for a service that uses no site account).
    /// </summary>
    public static (string Owner, string Site, string AccountId)? AccountOf(ClaimsPrincipal? user) =>
        user?.FindFirst(Owner)?.Value is { } owner && user.FindFirst(Site)?.Value is { } site
            && user.FindFirst(AccountId)?.Value is { } account
            ? (owner, site, account)
            : null;
}
