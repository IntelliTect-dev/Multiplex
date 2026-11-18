using Microsoft.AspNetCore.WebUtilities;
using Multiplex.Web.Auth.Clients;
using Multiplex.Web.Auth.Store;
using Multiplex.Web.Sites;

namespace Multiplex.Web.Auth;

/// <summary>What <c>/oauth/authorize</c> answers: an error page (client or redirect URI not trusted) or a redirect.</summary>
public abstract record AuthorizeOutcome
{
    public sealed record ErrorPage(string Message) : AuthorizeOutcome;

    public sealed record Redirect(string Location) : AuthorizeOutcome;
}

/// <summary>
/// The authorization endpoint and the hand-off to the connect pages. A valid request becomes a
/// pending authorization that the connect pages finish through <see cref="IAuthorizationCompleter"/>.
/// </summary>
public sealed partial class AuthorizationServer(
    IAuthStore store,
    OAuthClients clients,
    AuthenticatedEndpoints endpoints,
    AuthIssuer issuer,
    TimeProvider time,
    ILogger<AuthorizationServer> logger) : IAuthorizationCompleter
{
    private static readonly string[] SingleValued =
        ["response_type", "client_id", "redirect_uri", "state", "code_challenge", "code_challenge_method", "resource", "scope"];

    /// <param name="clientAddress">The caller's rate-limit key (<see cref="RequestOrigin.ClientAddress"/>); only a
    /// keyed hash of it is stored, to cap how many pending authorizations one caller holds.</param>
    public async Task<AuthorizeOutcome> AuthorizeAsync(IQueryCollection query, PathString pathBase, string clientAddress, CancellationToken ct)
    {
        // Until the client and redirect URI are trusted, errors are shown here, never redirected (RFC 6749 4.1.2.1).
        if (query["client_id"] is not [{ Length: > 0 } clientId])
        {
            return new AuthorizeOutcome.ErrorPage("The request has no client_id, or more than one.");
        }
        var lookup = await clients.ResolveAsync(clientId, ct);
        if (lookup.Client is not { } client)
        {
            LogRejected(logger, clientId, lookup.Error!);
            return new AuthorizeOutcome.ErrorPage(lookup.Error!);
        }
        if (query["redirect_uri"] is not [{ Length: > 0 } redirectUri] || !client.AllowsRedirect(redirectUri))
        {
            LogRejected(logger, clientId, "redirect_uri missing or not registered");
            return new AuthorizeOutcome.ErrorPage("The redirect_uri is missing or is not registered for this client.");
        }

        var state = query["state"] is [{ Length: > 0 } s] ? s : null;
        AuthorizeOutcome Fail(string error, string description)
        {
            LogRejected(logger, clientId, $"{error}: {description}");
            return new AuthorizeOutcome.Redirect(ErrorRedirect(redirectUri, error, description, state));
        }

        if (SingleValued.FirstOrDefault(name => query[name].Count > 1) is { } repeated)
        {
            return Fail("invalid_request", $"The {repeated} parameter is repeated.");
        }
        if (query["response_type"] != "code")
        {
            return query["response_type"].Count == 0
                ? Fail("invalid_request", "response_type is required.")
                : Fail("unsupported_response_type", "Only response_type=code is supported.");
        }
        if (state is null) return Fail("invalid_request", "state is required.");

        var challenge = query["code_challenge"].ToString();
        if (!OAuthCrypto.IsValidPkceString(challenge)) return Fail("invalid_request", "A PKCE code_challenge is required.");
        if (query["code_challenge_method"] != "S256") return Fail("invalid_request", "code_challenge_method must be S256.");

        var resource = query["resource"].ToString();
        if (resource.Length == 0) return Fail("invalid_target", "resource is required: the MCP endpoint URL being connected.");
        if (endpoints.FindByResource(resource) is not { } endpoint)
        {
            return Fail("invalid_target", "resource is not an MCP endpoint of this server that requires sign-in.");
        }

        // Requested scopes are not widened or failed on: the grant is always exactly the endpoint's scope.
        var now = time.GetUtcNow();
        var caller = OAuthCrypto.CallerKey(clientAddress);
        var pending = new PendingAuthorizationRecord(
            OAuthCrypto.NewId(), endpoint.Slug, client.ClientId, client.ClientName, redirectUri, state, challenge,
            endpoint.Resource, endpoint.Slug, now + AuthLifetimes.PendingAuthorization, caller);
        var refusal = await store.WriteAsync(st =>
        {
            // Counted after pruning, so expired authorizations make room again.
            if (st.PendingAuthorizations.Count >= AuthCapacity.PendingAuthorizations)
            {
                return StoreWrite.Unchanged<string?>("Too many sign-ins are in progress on this server; try again in a few minutes.");
            }
            if (st.PendingAuthorizations.Values.Count(p => p.CallerKey == caller) >= AuthCapacity.PendingAuthorizationsPerCaller)
            {
                return StoreWrite.Unchanged<string?>("Too many sign-ins are in progress from your address; try again in a few minutes.");
            }
            st.PendingAuthorizations[pending.Id] = pending;
            OAuthClients.Touch(st, client.ClientId, now);
            return StoreWrite.Changed<string?>(null);
        }, ct);
        if (refusal is not null) return Fail("temporarily_unavailable", refusal);

        LogStarted(logger, client.ClientId, endpoint.Slug);
        return new AuthorizeOutcome.Redirect($"{pathBase}/connect/{endpoint.Slug}?p={Uri.EscapeDataString(pending.Id)}");
    }

    public async Task<PendingAuthorization?> FindAsync(string pendingId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var pending = await store.ReadAsync(s => s.PendingAuthorizations.GetValueOrDefault(pendingId), ct);
        return pending is null || pending.ExpiresAt <= now
            ? null
            : new PendingAuthorization(pending.Id, pending.Site, pending.ClientName, pending.ExpiresAt,
                Uri.TryCreate(pending.RedirectUri, UriKind.Absolute, out var back) ? back.Authority : null);
    }

    /// <exception cref="InvalidOperationException">The pending authorization is unknown, already ended, or expired.</exception>
    /// <exception cref="ArgumentException">The account belongs to a different site than the pending authorization.</exception>
    public async Task<Uri> CompleteAsync(string pendingId, SiteAccount account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrEmpty(account.AccountId);

        var now = time.GetUtcNow();
        var code = OAuthCrypto.NewToken();
        var (pending, problem) = await store.WriteAsync(s =>
        {
            var problem = !s.PendingAuthorizations.TryGetValue(pendingId, out var p) ? "unknown"
                : p.ExpiresAt <= now ? "expired"
                : !string.Equals(p.Site, account.Site, StringComparison.Ordinal) ? "site"
                : null;
            if (problem is null)
            {
                s.PendingAuthorizations.Remove(pendingId);
                s.AuthorizationCodes[OAuthCrypto.Hash(code)] = new AuthorizationCodeRecord(
                    p!.ClientId, p.RedirectUri, p.CodeChallenge, p.Resource, p.Scope, p.Site, account.AccountId,
                    now + AuthLifetimes.AuthorizationCode);
            }
            return new StoreWrite<(PendingAuthorizationRecord?, string?)>((p, problem), Changed: problem is null);
        }, ct);

        switch (problem)
        {
            case "unknown": throw new InvalidOperationException("The pending authorization is unknown or has already ended.");
            case "expired": throw new InvalidOperationException("The pending authorization has expired.");
            case "site":
                throw new ArgumentException(
                    $"The account is for site '{account.Site}', but the authorization is for '{pending!.Site}'.", nameof(account));
        }

        LogCompleted(logger, pending!.ClientId, pending.Site, account.AccountId);
        return new Uri(QueryHelpers.AddQueryString(pending.RedirectUri, new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = pending.State,
            ["iss"] = issuer.Issuer,
        }));
    }

    /// <exception cref="InvalidOperationException">The pending authorization is unknown or already ended.</exception>
    public async Task<Uri> FailAsync(string pendingId, string error, string description, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(error);

        // An expired authorization can still be failed, so the person is sent back to their app.
        var pending = await store.WriteAsync(s =>
            {
                var removed = s.PendingAuthorizations.Remove(pendingId, out var p);
                return new StoreWrite<PendingAuthorizationRecord?>(p, Changed: removed);
            }, ct)
            ?? throw new InvalidOperationException("The pending authorization is unknown or has already ended.");

        LogFailed(logger, pending.ClientId, pending.Site, error);
        return new Uri(ErrorRedirect(pending.RedirectUri, error, description, pending.State));
    }

    private string ErrorRedirect(string redirectUri, string error, string description, string? state)
    {
        var parameters = new Dictionary<string, string?> { ["error"] = error, ["error_description"] = description };
        if (state is not null) parameters["state"] = state;
        parameters["iss"] = issuer.Issuer;
        return QueryHelpers.AddQueryString(redirectUri, parameters);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Authorization started: client {ClientId}, site {Site}.")]
    private static partial void LogStarted(ILogger logger, string clientId, string site);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Authorization request rejected: client {ClientId}: {Reason}")]
    private static partial void LogRejected(ILogger logger, string clientId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Authorization granted: client {ClientId}, site {Site}, account {AccountId}.")]
    private static partial void LogCompleted(ILogger logger, string clientId, string site, string accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Authorization ended with {Error}: client {ClientId}, site {Site}.")]
    private static partial void LogFailed(ILogger logger, string clientId, string site, string error);
}
