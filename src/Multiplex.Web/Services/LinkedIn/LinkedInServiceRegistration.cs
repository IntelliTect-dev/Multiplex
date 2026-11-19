using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplex.Web.Services.TripIt;
using Multiplex.Web.Sites;

namespace Multiplex.Web.Services.LinkedIn;

public static class LinkedInServiceRegistration
{
    /// <summary>
    /// Registers what the LinkedIn service needs. Its tools are discovered by
    /// <c>WithToolsFromAssembly</c> and served at <c>/linkedin/mcp</c> by <c>[McpEndpoint("linkedin")]</c>;
    /// <c>RequireAuthFor("linkedin")</c> in <c>Program.cs</c> is what keeps that address closed to
    /// anyone without a LinkedIn connection.
    /// </summary>
    public static IServiceCollection AddLinkedInService(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddMultiplexDataProtection();
        services.TryAddSingleton<ConfirmTokens>();
        services.TryAddSingleton<ILinkedInApiFactory, UnavailableLinkedInApiFactory>();
        services.TryAddScoped<LinkedInToolContext>();
        return services;
    }

    /// <summary>Stands in until the client that calls LinkedIn exists: every tool says so instead of acting.</summary>
    private sealed class UnavailableLinkedInApiFactory : ILinkedInApiFactory
    {
        public Task<ILinkedInApi> CreateAsync(string accountId, IServiceProvider requestServices, CancellationToken ct)
            => throw new LinkedInApiException("This Multiplex server cannot reach LinkedIn yet. Nothing was read or changed.");
    }
}
