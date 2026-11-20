using System.Security.Claims;
using Multiplex.Web.Auth.Clients;
using Multiplex.Web.Auth.Store;
using Multiplex.Web.Identity;
using Multiplex.Web.Sites;

namespace Multiplex.Web.Auth;

/// <summary>A token or revocation endpoint answer: HTTP status and JSON body (null for an empty 200).</summary>
public sealed record OAuthResponse(int Status, object? Body)
{
    public static OAuthResponse Error(string error, string description, int status = StatusCodes.Status400BadRequest) =>
        new(status, new Dictionary<string, string> { ["error"] = error, ["error_description"] = description });
}

/// <summary>
/// Issues, rotates, revokes and validates tokens. Tokens are opaque (32 random bytes, base64url); only
/// their SHA-256 hashes are stored. Every check and the update it guards run in one store write. Every
/// grant belongs to the person who made it, and works only while that person is still allowed in.
/// </summary>
public sealed partial class TokenService(IAuthStore store, AllowedPeople allowed, TimeProvider time, ILogger<TokenService> logger)
    : IGrantRevoker
{
    private const string NotAllowedDescription = "The person this grant belongs to is no longer allowed to use this server.";

    /// <summary>The token endpoint (RFC 6749 section 4.1.3 and 6, with PKCE and RFC 8707 resource).</summary>
    public Task<OAuthResponse> ExchangeAsync(IFormCollection form, string? basicClientId, CancellationToken ct)
    {
        if (form.FirstOrDefault(p => p.Value.Count > 1).Key is { } repeated)
        {
            return Task.FromResult(OAuthResponse.Error("invalid_request", $"The {repeated} parameter is repeated."));
        }

        var clientId = NonEmpty(form["client_id"]) ?? basicClientId;
        if (basicClientId is not null && clientId != basicClientId)
        {
            return Task.FromResult(OAuthResponse.Error("invalid_request", "client_id differs from the Authorization header."));
        }
        if (clientId is null) return Task.FromResult(OAuthResponse.Error("invalid_request", "client_id is required."));

        return NonEmpty(form["grant_type"]) switch
        {
            null => Task.FromResult(OAuthResponse.Error("invalid_request", "grant_type is required.")),
            "authorization_code" => RedeemCodeAsync(form, clientId, ct),
            "refresh_token" => RefreshAsync(form, clientId, ct),
            _ => Task.FromResult(OAuthResponse.Error("unsupported_grant_type", "Only authorization_code and refresh_token are supported.")),
        };
    }

    private async Task<OAuthResponse> RedeemCodeAsync(IFormCollection form, string clientId, CancellationToken ct)
    {
        var code = NonEmpty(form["code"]);
        var redirectUri = NonEmpty(form["redirect_uri"]);
        var verifier = NonEmpty(form["code_verifier"]);
        var resource = NonEmpty(form["resource"]);
        if (code is null || redirectUri is null) return OAuthResponse.Error("invalid_request", "code and redirect_uri are required.");
        if (!OAuthCrypto.IsValidPkceString(verifier)) return OAuthResponse.Error("invalid_request", "A valid PKCE code_verifier is required.");

        var now = time.GetUtcNow();
        var access = OAuthCrypto.NewToken();
        var refresh = OAuthCrypto.NewToken();
        var codeHash = OAuthCrypto.Hash(code);
        var people = allowed.Current();

        var (grant, failure) = await store.WriteAsync(s =>
        {
            if (!s.AuthorizationCodes.TryGetValue(codeHash, out var c) || c.ExpiresAt <= now)
            {
                return TokenWrite.Refused("invalid_grant", "The authorization code is invalid or has expired.");
            }
            if (c.RedeemedGrantId is { } earlier)
            {
                // A second redemption means the code leaked: revoke what the first one issued (RFC 6749 4.1.2).
                s.RevokeGrant(earlier);
                s.AuthorizationCodes.Remove(codeHash);
                LogCodeReused(logger, c.ClientId, c.Site, c.AccountId ?? "(none)");
                return TokenWrite.RefusedAfterRevoking("The authorization code was already used.");
            }
            if (c.ClientId != clientId) return TokenWrite.Refused("invalid_grant", "The authorization code was issued to another client.");
            if (c.RedirectUri != redirectUri) return TokenWrite.Refused("invalid_grant", "redirect_uri does not match the authorization request.");
            if (!OAuthCrypto.VerifyS256(verifier!, c.CodeChallenge)) return TokenWrite.Refused("invalid_grant", "code_verifier does not match the code_challenge.");
            if (resource is not null && resource != c.Resource) return TokenWrite.Refused("invalid_target", "resource does not match the authorization request.");
            if (!PersonAdmission.Allows(s, c.Owner!, people)) return TokenWrite.Refused("invalid_grant", NotAllowedDescription);

            var g = new GrantRecord(OAuthCrypto.NewId(), c.ClientId, c.Site, c.AccountId, c.Resource, c.Scope, now, c.Owner);
            s.Grants[g.Id] = g;
            s.AuthorizationCodes[codeHash] = c with { RedeemedGrantId = g.Id };
            Issue(s, g, access, refresh, now);
            OAuthClients.Touch(s, clientId, now);
            return TokenWrite.IssuedIn(g);
        }, ct);

        if (failure is not null)
        {
            LogTokenRefused(logger, clientId, "authorization_code", ErrorOf(failure));
            return failure;
        }
        LogIssued(logger, grant!.ClientId, grant.Site, grant.AccountId ?? "(none)", "authorization_code");
        return Success(access, refresh, grant.Scope);
    }

    private async Task<OAuthResponse> RefreshAsync(IFormCollection form, string clientId, CancellationToken ct)
    {
        var token = NonEmpty(form["refresh_token"]);
        var resource = NonEmpty(form["resource"]);
        if (token is null) return OAuthResponse.Error("invalid_request", "refresh_token is required.");

        var now = time.GetUtcNow();
        var access = OAuthCrypto.NewToken();
        var refresh = OAuthCrypto.NewToken();
        var hash = OAuthCrypto.Hash(token);
        var people = allowed.Current();

        var (grant, failure) = await store.WriteAsync(s =>
        {
            if (!s.RefreshTokens.TryGetValue(hash, out var r) || r.ExpiresAt <= now || !s.Grants.TryGetValue(r.GrantId, out var g))
            {
                return TokenWrite.Refused("invalid_grant", "The refresh token is invalid, expired or revoked.");
            }
            if (!PersonAdmission.Allows(s, g.Owner!, people)) return TokenWrite.Refused("invalid_grant", NotAllowedDescription);
            if (r.ClientId != clientId) return TokenWrite.Refused("invalid_grant", "The refresh token was issued to another client.");
            if (r.Used)
            {
                // Rotation: a used refresh token presented again means it leaked; end the whole family.
                s.RevokeGrant(r.GrantId);
                LogRefreshReused(logger, g.ClientId, g.Site, g.AccountId ?? "(none)");
                return TokenWrite.RefusedAfterRevoking("The refresh token was already used; the grant has been revoked.");
            }
            if (resource is not null && resource != g.Resource) return TokenWrite.Refused("invalid_target", "resource does not match the grant.");

            s.RefreshTokens[hash] = r with { Used = true };
            Issue(s, g, access, refresh, now);
            OAuthClients.Touch(s, clientId, now);
            return TokenWrite.IssuedIn(g);
        }, ct);

        if (failure is not null)
        {
            LogTokenRefused(logger, clientId, "refresh_token", ErrorOf(failure));
            return failure;
        }
        LogIssued(logger, grant!.ClientId, grant.Site, grant.AccountId ?? "(none)", "refresh_token");
        return Success(access, refresh, grant.Scope);
    }

    /// <summary>
    /// RFC 7009 revocation. A refresh token ends its whole grant; an access token ends only itself.
    /// Unknown tokens, and tokens of another client, are answered 200 without effect.
    /// </summary>
    public async Task<OAuthResponse> RevokeAsync(IFormCollection form, string? basicClientId, CancellationToken ct)
    {
        var token = NonEmpty(form["token"]);
        if (token is null) return OAuthResponse.Error("invalid_request", "token is required.");
        var clientId = NonEmpty(form["client_id"]) ?? basicClientId;

        var hash = OAuthCrypto.Hash(token);
        var revoked = await store.WriteAsync(s =>
        {
            if (s.RefreshTokens.TryGetValue(hash, out var r) && (clientId is null || r.ClientId == clientId))
            {
                s.RevokeGrant(r.GrantId);
                return StoreWrite.Changed<string?>(r.ClientId);
            }
            if (s.AccessTokens.TryGetValue(hash, out var a) && (clientId is null || a.ClientId == clientId))
            {
                s.AccessTokens.Remove(hash);
                return StoreWrite.Changed<string?>(a.ClientId);
            }
            return StoreWrite.Unchanged<string?>(null);
        }, ct);

        if (revoked is not null) LogRevokedByClient(logger, revoked);
        return new OAuthResponse(StatusCodes.Status200OK, null);
    }

    public async Task RevokeAccountAsync(string owner, string site, string accountId, CancellationToken ct)
    {
        bool Matches(string? o, string s, string? a) => o == owner && s == site && a == accountId;
        var grants = await store.WriteAsync(s =>
        {
            var ids = s.Grants.Values.Where(g => Matches(g.Owner, g.Site, g.AccountId)).Select(g => g.Id).ToList();
            foreach (var id in ids) s.RevokeGrant(id);

            // Tokens of grants already pruned, and codes not yet redeemed, must not outlive the account either.
            var tokens = s.AccessTokens.Where(kv => Matches(kv.Value.Owner, kv.Value.Site, kv.Value.AccountId)).Select(kv => kv.Key).ToList();
            foreach (var key in tokens) s.AccessTokens.Remove(key);
            var codes = s.AuthorizationCodes.Where(kv => Matches(kv.Value.Owner, kv.Value.Site, kv.Value.AccountId)).Select(kv => kv.Key).ToList();
            foreach (var key in codes) s.AuthorizationCodes.Remove(key);

            return new StoreWrite<int>(ids.Count, Changed: ids.Count + tokens.Count + codes.Count > 0);
        }, ct);

        LogAccountRevoked(logger, site, accountId, grants);
    }

    /// <summary>
    /// The principal for a bearer token presented to <paramref name="endpoint"/>, or null when the token
    /// is unknown, expired, revoked, issued for another resource, or belongs to a person who is no longer
    /// allowed in. Never writes the store.
    /// </summary>
    public async Task<ClaimsPrincipal?> ValidateAsync(string token, AuthenticatedEndpoint endpoint, CancellationToken ct)
    {
        var hash = OAuthCrypto.Hash(token);
        var people = allowed.Current();
        var record = await store.ReadAsync(
            s => s.AccessTokens.TryGetValue(hash, out var t) && s.Grants.ContainsKey(t.GrantId)
                 && t.Owner is { } owner && PersonAdmission.Allows(s, owner, people)
                ? t
                : null, ct);

        if (record is null || record.ExpiresAt <= time.GetUtcNow() || !string.Equals(record.Audience, endpoint.Resource, StringComparison.Ordinal))
        {
            return null;
        }

        List<Claim> claims =
        [
            new(SiteClaims.Owner, record.Owner!),
            new(SiteClaims.Site, record.Site),
            new(SiteClaims.ClientId, record.ClientId),
            new("scope", record.Scope),
        ];
        if (record.AccountId is { } accountId) claims.Add(new Claim(SiteClaims.AccountId, accountId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer", nameType: SiteClaims.Owner, roleType: null));
    }

    private static void Issue(AuthState s, GrantRecord g, string access, string refresh, DateTimeOffset now)
    {
        s.AccessTokens[OAuthCrypto.Hash(access)] = new AccessTokenRecord(
            g.Id, g.ClientId, g.Site, g.AccountId, g.Resource, g.Scope, now + AuthLifetimes.AccessToken, g.Owner);
        s.RefreshTokens[OAuthCrypto.Hash(refresh)] = new RefreshTokenRecord(g.Id, g.ClientId, now + AuthLifetimes.RefreshToken);
    }

    /// <summary>What a token store write produced: the grant tokens were issued in, or the refusal.</summary>
    private readonly record struct TokenWrite(GrantRecord? Grant, OAuthResponse? Failure)
    {
        public static StoreWrite<TokenWrite> IssuedIn(GrantRecord grant) => StoreWrite.Changed(new TokenWrite(grant, null));

        public static StoreWrite<TokenWrite> Refused(string error, string description) =>
            StoreWrite.Unchanged(new TokenWrite(null, OAuthResponse.Error(error, description)));

        /// <summary>Refused after revoking a leaked grant; the revocation must still be persisted.</summary>
        public static StoreWrite<TokenWrite> RefusedAfterRevoking(string description) =>
            StoreWrite.Changed(new TokenWrite(null, OAuthResponse.Error("invalid_grant", description)));
    }

    private static OAuthResponse Success(string access, string refresh, string scope) =>
        new(StatusCodes.Status200OK, new Dictionary<string, object>
        {
            ["access_token"] = access,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)AuthLifetimes.AccessToken.TotalSeconds,
            ["refresh_token"] = refresh,
            ["scope"] = scope,
        });

    private static string? NonEmpty(Microsoft.Extensions.Primitives.StringValues values) =>
        values is [{ Length: > 0 } value] ? value : null;

    private static string ErrorOf(OAuthResponse response) =>
        response.Body is Dictionary<string, string> body && body.TryGetValue("error", out var e) ? e : "error";

    [LoggerMessage(Level = LogLevel.Information, Message = "Tokens issued ({GrantType}): client {ClientId}, site {Site}, account {AccountId}.")]
    private static partial void LogIssued(ILogger logger, string clientId, string site, string accountId, string grantType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token request refused ({GrantType}): client {ClientId}, {Error}.")]
    private static partial void LogTokenRefused(ILogger logger, string clientId, string grantType, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Authorization code reused; its grant was revoked: client {ClientId}, site {Site}, account {AccountId}.")]
    private static partial void LogCodeReused(ILogger logger, string clientId, string site, string accountId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh token reused; its grant family was revoked: client {ClientId}, site {Site}, account {AccountId}.")]
    private static partial void LogRefreshReused(ILogger logger, string clientId, string site, string accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token revoked by client {ClientId}.")]
    private static partial void LogRevokedByClient(ILogger logger, string clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Revoked {Grants} grant(s) for site {Site}, account {AccountId}.")]
    private static partial void LogAccountRevoked(ILogger logger, string site, string accountId, int grants);
}
