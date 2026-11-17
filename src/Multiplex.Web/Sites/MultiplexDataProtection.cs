using Microsoft.AspNetCore.DataProtection;

namespace Multiplex.Web.Sites;

/// <summary>
/// The one app-wide ASP.NET Data Protection setup: key ring in <c>{Multiplex:DataPath}/keys</c>,
/// application name "Multiplex", and on Windows the keys themselves encrypted with DPAPI for
/// the account the server runs as. Site sessions, confirm tokens and antiforgery all use it.
/// Safe to call more than once.
/// </summary>
public static class MultiplexDataProtection
{
    public static string DataPath(IConfiguration configuration) =>
        configuration["Multiplex:DataPath"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Multiplex", "data");

    public static IServiceCollection AddMultiplexDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(MarkerService))) return services;
        services.AddSingleton<MarkerService>();

        var builder = services.AddDataProtection()
            .SetApplicationName("Multiplex")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(DataPath(configuration), "keys")));
        if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        return services;
    }

    private sealed class MarkerService;
}
