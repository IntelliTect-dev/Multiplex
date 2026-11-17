using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TripIt;
using TripIt.Http;

namespace Multiplex.Web.Servers.TripIt;

/// <summary>Configuration for the TripIt server (<c>Multiplex:Sites:TripIt</c>).</summary>
public sealed class TripItServerOptions
{
    public const string Section = "Multiplex:Sites:TripIt";

    /// <summary>Minimum gap between two TripIt requests of one account, in milliseconds.</summary>
    public int MinRequestSpacingMs { get; set; } = 250;

    /// <summary>Upper bound of the random amount added to the gap, in milliseconds.</summary>
    public int RequestJitterMs { get; set; } = 100;

    /// <summary>After a session refresh, how long before the same account may be refreshed again.</summary>
    public int RefreshCooldownSeconds { get; set; } = 30;
}

public static class TripItServerRegistration
{
    /// <summary>The named <see cref="HttpClient"/> every per-request <see cref="TripItClient"/> uses.</summary>
    public const string HttpClientName = "TripIt";

    /// <summary>
    /// Registers the TripIt MCP server's services. The tools themselves are discovered by
    /// <c>WithToolsFromAssembly</c> and served at <c>/tripit/mcp</c> by
    /// <c>[McpEndpoint("tripit")]</c>. Session storage and refresh
    /// (<see cref="Sites.ISiteSessionStore"/>, <see cref="Sites.ISiteSessionRefresher"/>) are
    /// registered by the sign-in part and resolved lazily, so this works without them.
    /// </summary>
    public static IServiceCollection AddTripItServer(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TripItServerOptions>().Bind(configuration.GetSection(TripItServerOptions.Section));
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        AddDataProtectionOnce(services, configuration);

        // Per-account pacing state is a singleton: clients are per request and handler chains are pooled.
        services.TryAddSingleton(sp =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TripItServerOptions>>().Value;
            return new RequestPacer(new TripItPacingOptions
            {
                MinSpacing = TimeSpan.FromMilliseconds(o.MinRequestSpacingMs),
                MaxJitter = TimeSpan.FromMilliseconds(o.RequestJitterMs),
            }, sp.GetRequiredService<TimeProvider>());
        });
        services.TryAddTransient<PacingHandler>();
        services.AddHttpClient(HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(150))
            .ConfigurePrimaryHttpMessageHandler(TripItClient.CreatePrimaryHandler)
            .AddHttpMessageHandler<PacingHandler>();

        services.TryAddSingleton<TripItAccountSessions>();
        services.TryAddSingleton<ConfirmTokens>();
        services.TryAddScoped<TripItToolContext>();
        return services;
    }

    /// <summary>
    /// Data Protection protects confirm tokens. Other parts register it too: the application
    /// name and key folder are the shared Multiplex ones (<c>Multiplex:DataPath</c>, default
    /// <c>%LOCALAPPDATA%\Multiplex\data</c>, keys under <c>keys</c>), so repeated registration agrees.
    /// </summary>
    private static void AddDataProtectionOnce(IServiceCollection services, IConfiguration configuration)
    {
        var dataPath = configuration["Multiplex:DataPath"];
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Multiplex", "data");
        }
        services.AddDataProtection()
            .SetApplicationName("Multiplex")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
    }
}
