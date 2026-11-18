namespace Multiplex.Web.Auth.Store;

/// <summary>
/// Everything the authorization server persists. Mutated only inside <see cref="IAuthStore.WriteAsync{T}"/>.
/// Tokens and codes are keyed by their SHA-256 hash; the values themselves are never stored.
/// </summary>
public sealed class AuthState
{
    /// <summary>Dynamically registered clients, by client id.</summary>
    public Dictionary<string, RegisteredClient> Clients { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Authorizations waiting for the connect pages, by pending id.</summary>
    public Dictionary<string, PendingAuthorizationRecord> PendingAuthorizations { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Issued authorization codes, by code hash.</summary>
    public Dictionary<string, AuthorizationCodeRecord> AuthorizationCodes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Grant families (one per code redemption; refresh rotation stays in the family), by grant id.</summary>
    public Dictionary<string, GrantRecord> Grants { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Access tokens, by token hash.</summary>
    public Dictionary<string, AccessTokenRecord> AccessTokens { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Refresh tokens (used ones are kept until expiry to detect reuse), by token hash.</summary>
    public Dictionary<string, RefreshTokenRecord> RefreshTokens { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Removes a grant family and every token issued in it.</summary>
    public void RevokeGrant(string grantId)
    {
        Grants.Remove(grantId);
        RemoveWhere(AccessTokens, t => t.GrantId == grantId);
        RemoveWhere(RefreshTokens, t => t.GrantId == grantId);
    }

    /// <summary>Drops expired entries, empty grants and clients unused for <see cref="AuthLifetimes.UnusedClient"/>.</summary>
    public void Prune(DateTimeOffset now)
    {
        RemoveWhere(PendingAuthorizations, p => p.ExpiresAt + AuthLifetimes.PendingAuthorization <= now);
        RemoveWhere(AuthorizationCodes, c => c.ExpiresAt <= now);
        RemoveWhere(AccessTokens, t => t.ExpiresAt <= now);
        RemoveWhere(RefreshTokens, t => t.ExpiresAt <= now);

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in AccessTokens.Values) live.Add(t.GrantId);
        foreach (var t in RefreshTokens.Values) if (!t.Used) live.Add(t.GrantId);
        RemoveWhere(Grants, g => !live.Contains(g.Id));

        var clientsInUse = Grants.Values.Select(g => g.ClientId).ToHashSet(StringComparer.Ordinal);
        RemoveWhere(Clients, c => c.LastUsedAt + AuthLifetimes.UnusedClient <= now && !clientsInUse.Contains(c.ClientId));
    }

    /// <summary>The registered client, unless it has gone unused long enough to be pruned.</summary>
    public RegisteredClient? FindClient(string clientId, DateTimeOffset now) =>
        Clients.TryGetValue(clientId, out var client)
        && (client.LastUsedAt + AuthLifetimes.UnusedClient > now || Grants.Values.Any(g => g.ClientId == clientId))
            ? client
            : null;

    private static void RemoveWhere<T>(Dictionary<string, T> items, Func<T, bool> predicate)
    {
        foreach (var key in items.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).ToList())
        {
            items.Remove(key);
        }
    }
}

public sealed record RegisteredClient(
    string ClientId,
    string? ClientName,
    IReadOnlyList<string> RedirectUris,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt);

/// <param name="CallerKey">A short keyed hash of the caller's rate-limit key, never the address itself; null for records saved before it existed.</param>
public sealed record PendingAuthorizationRecord(
    string Id,
    string Site,
    string ClientId,
    string ClientName,
    string RedirectUri,
    string State,
    string CodeChallenge,
    string Resource,
    string Scope,
    DateTimeOffset ExpiresAt,
    string? CallerKey = null);

public sealed record AuthorizationCodeRecord(
    string ClientId,
    string RedirectUri,
    string CodeChallenge,
    string Resource,
    string Scope,
    string Site,
    string AccountId,
    DateTimeOffset ExpiresAt,
    string? RedeemedGrantId = null);

public sealed record GrantRecord(
    string Id,
    string ClientId,
    string Site,
    string AccountId,
    string Resource,
    string Scope,
    DateTimeOffset CreatedAt);

public sealed record AccessTokenRecord(
    string GrantId,
    string ClientId,
    string Site,
    string AccountId,
    string Audience,
    string Scope,
    DateTimeOffset ExpiresAt);

public sealed record RefreshTokenRecord(
    string GrantId,
    string ClientId,
    DateTimeOffset ExpiresAt,
    bool Used = false);
