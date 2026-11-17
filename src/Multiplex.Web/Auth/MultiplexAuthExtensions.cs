using System.Net;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplex.Web.Auth.Clients;
using Multiplex.Web.Auth.Store;
using Multiplex.Web.Sites;

namespace Multiplex.Web.Auth;

/// <summary>Returned by <see cref="MultiplexAuthExtensions.AddMultiplexAuth"/> to name the endpoints that need sign-in.</summary>
public sealed partial class MultiplexAuthBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    /// <summary>
    /// Requires a bearer token for every request to <c>/{slug}/mcp</c>, and makes the slug an OAuth
    /// scope and resource (<c>&lt;PublicBaseUrl&gt;/{slug}/mcp</c>). The first slug added is the one the
    /// root protected resource metadata describes.
    /// </summary>
    public MultiplexAuthBuilder RequireAuthFor(string slug, string? displayName = null)
    {
        if (!SlugPattern().IsMatch(slug))
        {
            throw new ArgumentException("A slug is lower-case letters, digits and hyphens, e.g. 'tripit'.", nameof(slug));
        }
        Services.Configure<AuthenticatedEndpointOptions>(o => o.Endpoints.Add((slug, displayName ?? slug)));
        return this;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SlugPattern();
}

public static class MultiplexAuthExtensions
{
    /// <summary>
    /// Registers the OAuth 2.1 authorization server, MCP bearer protection, the encrypted auth store
    /// (under <c>Multiplex:DataPath</c>) and the app's Data Protection key ring (<c>&lt;DataPath&gt;/keys</c>).
    /// Configuration is read lazily, so later configuration sources (tests, user secrets) apply.
    /// </summary>
    public static MultiplexAuthBuilder AddMultiplexAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var builder = new MultiplexAuthBuilder(services);
        if (services.Any(d => d.ServiceType == typeof(AuthIssuer))) return builder;

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
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                ConnectCallback = ClientMetadataDocuments.ConnectToPublicAddressAsync,
            });

        AddDataProtection(services);
        AddRateLimits(services);
        return builder;
    }

    /// <summary>
    /// Adds the rate limiter and MCP bearer protection. Call after <c>UseRouting</c> and before
    /// <c>UseAuthorization</c> and the MCP endpoints. Fails at startup outside Development when an
    /// endpoint requires sign-in and <c>Multiplex:PublicBaseUrl</c> is not set.
    /// </summary>
    public static IApplicationBuilder UseMultiplexAuth(this IApplicationBuilder app)
    {
        _ = app.ApplicationServices.GetRequiredService<AuthIssuer>().Issuer;
        app.UseRateLimiter();
        app.UseMiddleware<McpBearerMiddleware>();
        return app;
    }

    private static void AddDataProtection(IServiceCollection services)
    {
        services.AddDataProtection().SetApplicationName("Multiplex");

        // Applied after the framework's defaults; nothing touches the disk until a key is first needed.
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp => new ConfigureOptions<KeyManagementOptions>(options =>
        {
            var loggers = sp.GetRequiredService<ILoggerFactory>();
            var keys = new DirectoryInfo(Path.Combine(sp.GetRequiredService<IOptions<MultiplexAuthOptions>>().Value.ResolveDataPath(), "keys"));
            options.XmlRepository = new FileSystemXmlRepository(keys, loggers);
            if (OperatingSystem.IsWindows())
            {
                options.XmlEncryptor = new DpapiXmlEncryptor(protectToLocalMachine: false, loggers);
            }
        }));
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
    /// The caller's address. Behind a local tunnel (Tailscale Funnel, cloudflared) every request arrives
    /// from loopback, so the address the tunnel appended last to <c>X-Forwarded-For</c> is used instead;
    /// a remote caller cannot forge that last entry.
    /// </summary>
    internal static string ClientAddress(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && IPAddress.IsLoopback(remote)
            && context.Request.Headers["X-Forwarded-For"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                is [.., var forwarded])
        {
            return forwarded;
        }
        return remote?.ToString() ?? "unknown";
    }
}
