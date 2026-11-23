using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplex.Sites.GoogleFlights;

namespace Multiplex.Web.Services.GoogleFlights;

/// <summary>Settings from the <c>GoogleFlights</c> configuration section.</summary>
public sealed class GoogleFlightsServiceOptions
{
    public const string Section = "GoogleFlights";

    /// <summary>Default currency for prices (ISO 4217).</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Default region Google prices for (two-letter country code).</summary>
    public string Region { get; set; } = "US";

    /// <summary>How long answers are reused, in minutes.</summary>
    public double CacheMinutes { get; set; } = 12;
}

public static class GoogleFlightsServiceRegistration
{
    /// <summary>
    /// Registers the one <see cref="GoogleFlightsClient"/> every request shares, so its cache, pacing and refusal
    /// cool-down cover the whole server. Its tools are discovered by <c>WithToolsFromAssembly</c> and served at
    /// <c>/google-flights/mcp</c>, which <c>RequireAuthFor("google-flights", ..., needsSiteAccount: false)</c> in
    /// <c>Program.cs</c> puts behind Multiplex's sign-in: like Time it acts for no one's site account, so it needs a
    /// token from anywhere but the machine Multiplex runs on, and connecting it is a consent page.
    /// </summary>
    public static IServiceCollection AddGoogleFlightsService(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GoogleFlightsServiceOptions>().Bind(configuration.GetSection(GoogleFlightsServiceOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<GoogleFlightsServiceOptions>>().Value;
            return new GoogleFlightsClient(new GoogleFlightsClientOptions
            {
                Currency = o.Currency,
                Region = o.Region,
                CacheDuration = TimeSpan.FromMinutes(o.CacheMinutes),
            }, sp.GetRequiredService<TimeProvider>());
        });
        return services;
    }
}
