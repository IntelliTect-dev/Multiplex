using Microsoft.Extensions.Options;

namespace Multiplex.Web.Auth;

/// <summary>
/// The OAuth issuer: <c>Multiplex:PublicBaseUrl</c> without a trailing slash. Every URL this server
/// advertises (issuer, <c>iss</c>, endpoints, resource URLs) is built from this one string, so they
/// stay byte-identical. It is never derived from the incoming request.
/// </summary>
public sealed class AuthIssuer
{
    /// <summary>Used in Development when neither PublicBaseUrl nor <c>urls</c> is configured.</summary>
    public const string DevelopmentFallback = "http://localhost:5190";

    private readonly Lazy<string> _issuer;

    public AuthIssuer(
        IOptions<MultiplexAuthOptions> options,
        IOptions<AuthenticatedEndpointOptions> endpoints,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<AuthIssuer> logger)
    {
        _issuer = new Lazy<string>(() => Resolve(
            options.Value.PublicBaseUrl, endpoints.Value.Endpoints.Count > 0, configuration, environment, logger));
    }

    /// <summary>The issuer, e.g. <c>https://pc.tailnet.ts.net</c>.</summary>
    public string Issuer => _issuer.Value;

    public string AuthorizationEndpoint => Issuer + "/oauth/authorize";
    public string TokenEndpoint => Issuer + "/oauth/token";
    public string RegistrationEndpoint => Issuer + "/oauth/register";
    public string RevocationEndpoint => Issuer + "/oauth/revoke";

    private static string Resolve(
        string? configured, bool anyProtected, IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Normalize(configured, "Multiplex:PublicBaseUrl");
        }

        if (anyProtected && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Multiplex:PublicBaseUrl must be set (e.g. https://<machine>.<tailnet>.ts.net): it is the OAuth issuer " +
                "and the prefix of every protected MCP endpoint, and is never derived from requests.");
        }

        var fallback = FirstConfiguredUrl(configuration) ?? DevelopmentFallback;
        logger.LogWarning(
            "Multiplex:PublicBaseUrl is not set; using {Issuer}. Remote clients (Claude, ChatGPT) cannot sign in until it is set to the public HTTPS URL.",
            fallback);
        return fallback;
    }

    private static string? FirstConfiguredUrl(IConfiguration configuration)
    {
        var first = configuration["urls"]?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (first is null) return null;

        // Kestrel accepts wildcard hosts in "urls"; a client needs a name it can reach.
        foreach (var wildcard in new[] { "://+", "://*", "://0.0.0.0", "://[::]" })
        {
            first = first.Replace(wildcard, "://localhost", StringComparison.Ordinal);
        }
        return Uri.TryCreate(first, UriKind.Absolute, out _) ? Normalize(first, "urls") : null;
    }

    private static string Normalize(string value, string source)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                $"{source} must be an origin such as https://host[:port], with no path, query or fragment (got '{value}').");
        }
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
