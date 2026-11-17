namespace Multiplex.Web.Auth;

/// <summary>
/// Settings for the OAuth authorization server and MCP resource protection, bound from the
/// <c>Multiplex</c> configuration section (e.g. <c>Multiplex:PublicBaseUrl</c>,
/// <c>Multiplex:DataPath</c>, <c>Multiplex:Auth:TokenPermitLimit</c>).
/// </summary>
public sealed class MultiplexAuthOptions
{
    /// <summary>
    /// The public origin clients reach this server at, e.g. <c>https://pc.tailnet.ts.net</c>. It is the
    /// OAuth issuer and the prefix of every protected resource URL; never derived from a request.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Folder for the encrypted stores and the Data Protection key ring (in <c>keys</c>).
    /// Defaults to <c>%LOCALAPPDATA%\Multiplex\data</c>.</summary>
    public string? DataPath { get; set; }

    public AuthLimits Auth { get; set; } = new();

    public string ResolveDataPath() =>
        string.IsNullOrWhiteSpace(DataPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Multiplex", "data")
            : DataPath;
}

/// <summary>Lifetimes and per-IP rate limits (<c>Multiplex:Auth:*</c>).</summary>
public sealed class AuthLimits
{
    public int RegisterPermitLimit { get; set; } = 20;
    public TimeSpan RegisterWindow { get; set; } = TimeSpan.FromHours(1);

    public int TokenPermitLimit { get; set; } = 60;
    public TimeSpan TokenWindow { get; set; } = TimeSpan.FromMinutes(1);

    public int AuthorizePermitLimit { get; set; } = 60;
    public TimeSpan AuthorizeWindow { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>The slugs whose <c>/{slug}/mcp</c> endpoint requires a bearer token; filled by
/// <see cref="MultiplexAuthBuilder.RequireAuthFor"/>.</summary>
public sealed class AuthenticatedEndpointOptions
{
    internal List<(string Slug, string DisplayName)> Endpoints { get; } = [];
}

/// <summary>Fixed protocol lifetimes (not configurable: clients depend on them being short).</summary>
internal static class AuthLifetimes
{
    public static readonly TimeSpan PendingAuthorization = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AuthorizationCode = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AccessToken = TimeSpan.FromHours(1);
    public static readonly TimeSpan RefreshToken = TimeSpan.FromDays(30);
    public static readonly TimeSpan UnusedClient = TimeSpan.FromDays(90);

    /// <summary>Last-used stamps are written at most this often per client, so busy clients don't rewrite the store.</summary>
    public static readonly TimeSpan ClientTouchGranularity = TimeSpan.FromHours(1);
}
