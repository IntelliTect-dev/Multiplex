using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplex.Web.Services.TripIt;
using Multiplex.Web.Sites;
using TripIt.Http;

namespace Multiplex.Web.Services.LinkedIn;

public static class LinkedInServiceRegistration
{
    /// <summary>The named <see cref="HttpClient"/> every per-request <see cref="LinkedInClient"/> uses.</summary>
    public const string HttpClientName = "LinkedIn";

    /// <summary>
    /// Registers what the LinkedIn service needs. Its tools are discovered by
    /// <c>WithToolsFromAssembly</c> and served at <c>/linkedin/mcp</c> by <c>[McpEndpoint("linkedin")]</c>;
    /// <c>RequireAuthFor("linkedin")</c> in <c>Program.cs</c> is what keeps that address closed to
    /// anyone without a LinkedIn connection. Session storage and refresh come from the sign-in part
    /// and are resolved per request, so this works without them.
    /// </summary>
    public static IServiceCollection AddLinkedInService(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LinkedInServiceOptions>().Bind(configuration.GetSection(LinkedInServiceOptions.Section));
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddMultiplexDataProtection();
        services.TryAddSingleton<ConfirmTokens>();

        // LinkedIn's own pacing state (one request in flight per account, a gap between requests).
        services.TryAddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<LinkedInServiceOptions>>().Value;
            return new LinkedInPacer(new RequestPacer(new TripItPacingOptions
            {
                MinSpacing = TimeSpan.FromMilliseconds(o.MinRequestSpacingMs),
                MaxJitter = TimeSpan.FromMilliseconds(o.RequestJitterMs),
            }, sp.GetRequiredService<TimeProvider>()));
        });
        services.AddHttpClient(HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Each request carries its account's own Cookie header; redirects are LinkedIn's
                // way of saying "signed out" or "security check", so the client must see them.
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
            })
            .AddHttpMessageHandler(sp => new PacingHandler(sp.GetRequiredService<LinkedInPacer>().Pacer,
                sp.GetService<ILogger<PacingHandler>>()));

        services.TryAddSingleton<LinkedInAccountSessions>();
        services.TryAddSingleton<ILinkedInApiFactory, LinkedInApiFactory>();
        services.TryAddScoped<LinkedInToolContext>();
        return services;
    }
}

/// <summary>The LinkedIn service's own <see cref="RequestPacer"/>, kept apart from TripIt's.</summary>
public sealed record LinkedInPacer(RequestPacer Pacer);

/// <summary>Builds the request's account's <see cref="LinkedInClient"/> from its stored session.</summary>
public sealed class LinkedInApiFactory(
    IHttpClientFactory httpClients,
    LinkedInAccountSessions sessions,
    IOptions<LinkedInServiceOptions> options) : ILinkedInApiFactory
{
    public async Task<ILinkedInApi> CreateAsync(string owner, string accountId, IServiceProvider requestServices, CancellationToken ct)
    {
        var session = await sessions.LoadAsync(requestServices, owner, accountId, ct).ConfigureAwait(false);
        if (!session.IsConfigured)
            throw new LinkedInApiException("There is no LinkedIn session for this account yet. Reconnect the LinkedIn connector to sign in.");
        return new LinkedInClient(session, httpClients.CreateClient(LinkedInServiceRegistration.HttpClientName), options.Value.QueryIds,
            (rejected, c) => sessions.RenewAsync(requestServices, owner, accountId, rejected, c), pacingKey: accountId);
    }
}
