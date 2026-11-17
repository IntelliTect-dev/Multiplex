using System.Text.Json;
using Multiplex.Web.Auth.Store;

namespace Multiplex.Web.Auth.Clients;

/// <summary>A client the authorization server knows, from registration or a metadata document.</summary>
public sealed record OAuthClient(string ClientId, string ClientName, IReadOnlyList<string> RedirectUris, bool IsMetadataDocument)
{
    public const int MaxNameLength = 100;

    /// <summary>True when <paramref name="redirectUri"/> is one of the client's redirect URIs, compared exactly.</summary>
    public bool AllowsRedirect(string redirectUri) => RedirectUris.Contains(redirectUri, StringComparer.Ordinal);

    internal static string DisplayNameOr(string? name, string fallback)
    {
        name = name is null ? null : new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (string.IsNullOrEmpty(name)) return fallback;
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength];
    }
}

/// <summary>A client lookup: the client, or why it is not usable.</summary>
public sealed record ClientLookup(OAuthClient? Client, string? Error)
{
    public static ClientLookup Found(OAuthClient client) => new(client, null);
    public static ClientLookup Fail(string error) => new(null, error);
}

/// <summary>Resolves client ids (registered or metadata-document URLs) and handles RFC 7591 registration.</summary>
public sealed partial class OAuthClients(
    IAuthStore store,
    ClientMetadataDocuments documents,
    TimeProvider time,
    ILogger<OAuthClients> logger)
{
    private const int MaxRedirectUris = 10;

    public async Task<ClientLookup> ResolveAsync(string clientId, CancellationToken ct)
    {
        if (clientId.StartsWith("https://", StringComparison.Ordinal))
        {
            return await documents.GetAsync(clientId, ct);
        }

        var now = time.GetUtcNow();
        var registered = await store.ReadAsync(s => s.FindClient(clientId, now), ct);
        return registered is null
            ? ClientLookup.Fail("The client is not registered with this server.")
            : ClientLookup.Found(new OAuthClient(
                registered.ClientId,
                OAuthClient.DisplayNameOr(registered.ClientName, "An unnamed client"),
                registered.RedirectUris,
                IsMetadataDocument: false));
    }

    /// <summary>Records that a registered client was used; written at most hourly per client.</summary>
    internal static void Touch(AuthState state, string clientId, DateTimeOffset now)
    {
        if (state.Clients.TryGetValue(clientId, out var client)
            && now - client.LastUsedAt >= AuthLifetimes.ClientTouchGranularity)
        {
            state.Clients[clientId] = client with { LastUsedAt = now };
        }
    }

    /// <summary>
    /// RFC 7591 dynamic registration of a public client. Returns the response body and status code.
    /// Only <c>none</c> client authentication exists here: any other requested method is replaced by
    /// <c>none</c> and echoed back, as RFC 7591 section 3.2.1 allows.
    /// </summary>
    public async Task<(int Status, object Body)> RegisterAsync(JsonElement request, CancellationToken ct)
    {
        if (request.ValueKind != JsonValueKind.Object) return Invalid("invalid_client_metadata", "The body must be a JSON object.");

        if (!request.TryGetProperty("redirect_uris", out var urisElement) || urisElement.ValueKind != JsonValueKind.Array
            || urisElement.GetArrayLength() is 0 or > MaxRedirectUris)
        {
            return Invalid("invalid_redirect_uri", $"redirect_uris must be an array of 1 to {MaxRedirectUris} URIs.");
        }

        var redirectUris = new List<string>();
        foreach (var uri in urisElement.EnumerateArray())
        {
            var value = uri.ValueKind == JsonValueKind.String ? uri.GetString() : null;
            if (!RedirectUris.IsAllowed(value))
            {
                return Invalid("invalid_redirect_uri",
                    "Each redirect URI must be an absolute HTTPS URI, or HTTP to 127.0.0.1, localhost or [::1], without a fragment.");
            }
            if (!redirectUris.Contains(value!, StringComparer.Ordinal)) redirectUris.Add(value!);
        }

        if (request.TryGetProperty("grant_types", out var grants)
            && (grants.ValueKind != JsonValueKind.Array
                || !grants.EnumerateArray().Any(g => g.ValueKind == JsonValueKind.String && g.GetString() == "authorization_code")))
        {
            return Invalid("invalid_client_metadata", "grant_types must include authorization_code.");
        }

        if (request.TryGetProperty("response_types", out var responses)
            && (responses.ValueKind != JsonValueKind.Array
                || !responses.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == "code")))
        {
            return Invalid("invalid_client_metadata", "response_types must include code.");
        }

        var requestedAuth = request.TryGetProperty("token_endpoint_auth_method", out var auth) && auth.ValueKind == JsonValueKind.String
            ? auth.GetString()
            : null;
        var name = request.TryGetProperty("client_name", out var n) && n.ValueKind == JsonValueKind.String
            ? OAuthClient.DisplayNameOr(n.GetString(), "")
            : "";

        var now = time.GetUtcNow();
        var client = new RegisteredClient(OAuthCrypto.NewId(), name.Length > 0 ? name : null, redirectUris, now, now);
        await store.WriteAsync(s => s.Clients[client.ClientId] = client, ct);

        LogRegistered(logger, client.ClientId, client.ClientName ?? "", requestedAuth ?? "(none given)");

        var body = new Dictionary<string, object>
        {
            ["client_id"] = client.ClientId,
            ["client_id_issued_at"] = now.ToUnixTimeSeconds(),
            ["redirect_uris"] = redirectUris,
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
        };
        if (client.ClientName is not null) body["client_name"] = client.ClientName;
        return (StatusCodes.Status201Created, body);
    }

    private static (int, object) Invalid(string error, string description) =>
        (StatusCodes.Status400BadRequest, new Dictionary<string, string> { ["error"] = error, ["error_description"] = description });

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Registered client {ClientId} ({ClientName}); requested token_endpoint_auth_method {RequestedAuth}, registered as none.")]
    private static partial void LogRegistered(ILogger logger, string clientId, string clientName, string requestedAuth);
}
