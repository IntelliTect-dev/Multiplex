using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplex.Web.HtmlSites;

namespace Multiplex.Web.Services.Amazon;

public static class AmazonServiceRegistration
{
    /// <summary>
    /// Registers what the Amazon service needs. Its tools are discovered by
    /// <c>WithToolsFromAssembly</c> and served at <c>/amazon/mcp</c> by <c>[McpEndpoint("amazon")]</c>.
    /// It reads Amazon over plain HTTPS and uses no browser.
    /// </summary>
    public static IServiceCollection AddAmazonService(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        // One gate for the whole host: Amazon sees one address, however many people use Multiplex.
        services.TryAddSingleton(sp => new AmazonGate(new SiteGate(new SiteGateOptions { Site = "Amazon" },
            sp.GetRequiredService<TimeProvider>())));
        services.AddHttpClient(AmazonClient.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);
                c.MaxResponseContentBufferSize = 16 * 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // The client reads redirects itself (one to sign-in or a robot check is a block), and
                // sends no cookies the handler chose for it.
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
            });
        // The link a person opens to answer a robot check is on this server's public address.
        services.TryAddSingleton(sp => new AmazonLinks(() => sp.GetRequiredService<Auth.AuthIssuer>().Issuer));
        services.TryAddSingleton<AmazonClient>();
        services.AddSingleton<ISiteHandOver>(sp => sp.GetRequiredService<AmazonClient>());
        return services;
    }
}
