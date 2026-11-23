using System.Net;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplex.Web.Auth.Clients;
using Multiplex.Web.Auth.Store;
using Multiplex.Web.Identity;
using Multiplex.Web.Sites;

namespace Multiplex.Web.Auth;

/// <summary>Returned by <see cref="MultiplexAuthExtensions.AddMultiplexAuth"/> to name the endpoints that need sign-in.</summary>
public sealed partial class MultiplexAuthBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    /// <summary>
    /// Requires a bearer token for every request to <c>/{slug}/mcp</c>, and makes the slug an OAuth
    /// scope and resource (<c>&lt;PublicBaseUrl&gt;/{slug}/mcp</c>). The first slug added is the one the
    /// root protected resource metadata describes. Every MCP endpoint must be added; one that is not is
    /// refused (404).
    /// </summary>
    /// <param name="needsSiteAccount">True (the default) when the service's tools act on a person's site
    /// account, so connecting it signs in to the site and every call needs a token, even from this host.
    /// False for a service that acts for no one (e.g. <c>time</c>): connecting it is a consent page, and
    /// a call from this host may come without a token.</param>
    public MultiplexAuthBuilder RequireAuthFor(string slug, string? displayName = null, bool needsSiteAccount = true)
    {
        if (!SlugPattern().IsMatch(slug))
        {
            throw new ArgumentException("A slug is lower-case letters, digits and hyphens, e.g. 'tripit'.", nameof(slug));
        }
        Services.Configure<AuthenticatedEndpointOptions>(o => o.Endpoints.Add((slug, displayName ?? slug, needsSiteAccount)));
        return this;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SlugPattern();
}

public static class MultiplexAuthExtensions
{
    /// <summary>
    /// Registers the OAuth 2.1 authorization server, the sign-in to Multiplex itself
    /// (<see cref="Identity.MultiplexIdentity"/>), the access gate and the encrypted auth store (under
    /// <c>Multiplex:DataPath</c>, key ring in its <c>keys</c> folder). Configuration is read lazily, so
    /// later configuration sources (tests, user secrets) apply.
    /// </summary>
    public static MultiplexAuthBuilder AddMultiplexAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var builder = new MultiplexAuthBuilder(services);
        if (services.Any(d => d.ServiceType == typeof(AuthIssuer))) return builder;

        services.AddMultiplexIdentity();

        services.AddOptions<MultiplexAuthOptions>().Bind(configuration.GetSection("Multiplex"));
        services.AddOptions<AuthenticatedEndpointOptions>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AuthIssuer>();
        services.AddSingleton<AuthenticatedEndpoints>();
        services.TryAddSingleton<IAuthStore, FileAuthStore>();
        services.AddSingleton<ClientMetadataDocuments>();
        services.AddSingleton<OAuthClients>();
        services.AddSingleton<AuthorizationServer>();
        services.AddSingleton<IAuthorizationCompleter>(sp => sp.GetRequiredService<AuthorizationServer>());
        services.AddSingleton<TokenService>();
        services.AddSingleton<IGrantRevoker>(sp => sp.GetRequiredService<TokenService>());

        services.AddHttpClient(ClientMetadataDocuments.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                // A proxy would make the connection, so the public-address check would judge the proxy
                // rather than the document's host.
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.All,
                // Within the 15 s the whole fetch is allowed: a busy PC was slow to connect, not the host.
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ConnectCallback = ClientMetadataDocuments.ConnectToPublicAddressAsync,
            });

        AddRateLimits(services);
        return builder;
    }

    /// <summary>
    /// Adds the sign-in cookie and Microsoft callback, the rate limiter and the access gate
    /// (<see cref="AccessGate"/>). Call after <c>UseRouting</c> and before <c>UseAuthorization</c> and the
    /// endpoints. Fails at startup outside Development when an endpoint requires sign-in and
    /// <c>Multiplex:PublicBaseUrl</c> is not set.
    /// </summary>
    public static IApplicationBuilder UseMultiplexAuth(this IApplicationBuilder app)
    {
        _ = app.ApplicationServices.GetRequiredService<AuthIssuer>().Issuer;
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseMiddleware<AccessGate>();
        return app;
    }

    private static void AddRateLimits(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected ??= (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }
                return ValueTask.CompletedTask;
            };

            AddPerIpPolicy(options, OAuthEndpoints.RegisterPolicy, l => (l.RegisterPermitLimit, l.RegisterWindow));
            AddPerIpPolicy(options, OAuthEndpoints.AuthorizePolicy, l => (l.AuthorizePermitLimit, l.AuthorizeWindow));
            AddPerIpPolicy(options, OAuthEndpoints.TokenPolicy, l => (l.TokenPermitLimit, l.TokenWindow));
        });
    }

    private static void AddPerIpPolicy(RateLimiterOptions options, string name, Func<AuthLimits, (int Permits, TimeSpan Window)> select) =>
        options.AddPolicy(name, context =>
        {
            var (permits, window) = select(context.RequestServices.GetRequiredService<IOptions<MultiplexAuthOptions>>().Value.Auth);
            return RateLimitPartition.GetFixedWindowLimiter(ClientAddress(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = window,
                QueueLimit = 0,
            });
        });

    /// <summary>
    /// The rate-limit key for the caller. Behind a local tunnel every request arrives from loopback, so
    /// the last <c>X-Forwarded-For</c> entry is used when it is an IP address: Tailscale Funnel and Serve
    /// overwrite that header with the real caller's address, and an appending proxy puts what it saw
    /// last. IPv6 callers are counted by /64. See <see cref="Sites.RequestOrigin.ClientAddress"/>.
    /// </summary>
    internal static string ClientAddress(HttpContext context) => Sites.RequestOrigin.ClientAddress(context);
}
