using Microsoft.AspNetCore.Hosting;
using Multiplex.Web.Identity;

namespace Multiplex.Tests.Identity;

public static class NoMicrosoftSignIn
{
    /// <summary>
    /// Starts a test host with no Microsoft app registration, whatever the PC's own environment
    /// variables say: a host's settings override them (#159). Call it before the host's own
    /// settings, so a host that wants a registration still sets one.
    /// </summary>
    public static IWebHostBuilder WithoutMicrosoftSignIn(this IWebHostBuilder builder) => builder
        .UseSetting(MultiplexIdentity.ClientIdKey, "")
        .UseSetting(MultiplexIdentity.ClientSecretKey, "");
}
