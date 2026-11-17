using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplex.Web.Sites;

namespace Multiplex.Web.SignIn;

public static class SignInServiceCollectionExtensions
{
    /// <summary>
    /// Registers the connect pages' services: passcode gate, live sign-in browsers, encrypted
    /// site sessions (<see cref="ISiteSessionStore"/>) and their refresher
    /// (<see cref="ISiteSessionRefresher"/>). Add each site with <see cref="SiteSignInBuilder.AddSite{T}"/>.
    /// Needs <see cref="IAuthorizationCompleter"/> and <see cref="IGrantRevoker"/> from Auth/ at run
    /// time; without them the connect pages say so instead of failing to start.
    /// </summary>
    public static SiteSignInBuilder AddSiteSignIn(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SignInOptions>().Bind(configuration.GetSection("SignIn"));
        services.AddMultiplexDataProtection();
        services.AddAntiforgery();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<SignInPaths>();
        services.TryAddSingleton<SiteBrowserManager>();
        services.TryAddSingleton<SiteSessionStore>();
        services.TryAddSingleton<ISiteSessionStore>(sp => sp.GetRequiredService<SiteSessionStore>());
        services.TryAddSingleton<SiteAccountIndex>();
        services.TryAddSingleton<SiteAllowlist>();
        services.TryAddSingleton<PasscodeGate>();
        services.TryAddSingleton<GateCookie>();
        services.TryAddSingleton<LiveSignInRegistry>();
        services.TryAddSingleton<SiteSessionRefresher>();
        services.TryAddSingleton<ISiteSessionRefresher>(sp => sp.GetRequiredService<SiteSessionRefresher>());
        services.TryAddSingleton<SiteProfileAdopter>();
        return new SiteSignInBuilder(services);
    }

}

public sealed class SiteSignInBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    /// <summary>Adds one site's sign-in definition (resolved from DI, so it may take configuration).</summary>
    public SiteSignInBuilder AddSite<TDefinition>() where TDefinition : class, ISiteSignInDefinition
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<ISiteSignInDefinition, TDefinition>());
        return this;
    }
}
