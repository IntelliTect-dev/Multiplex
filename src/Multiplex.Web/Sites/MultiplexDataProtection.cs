using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Multiplex.Web.Sites;

/// <summary>
/// The host's one ASP.NET Data Protection key ring: <c>{Multiplex:DataPath}/keys</c>,
/// application name "Multiplex", and on Windows the keys themselves encrypted with DPAPI for
/// the account the server runs as. Site sessions, confirm tokens and antiforgery all use it.
/// Safe to call more than once; the folder is read from configuration when first needed.
/// </summary>
public static class MultiplexDataProtection
{
    /// <summary><c>Multiplex:DataPath</c>, default <c>%LOCALAPPDATA%\Multiplex\data</c>.</summary>
    public static string DataPath(IConfiguration configuration) =>
        configuration["Multiplex:DataPath"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Multiplex", "data");

    public static IServiceCollection AddMultiplexDataProtection(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(Marker))) return services;
        services.AddSingleton<Marker>();

        services.AddDataProtection().SetApplicationName("Multiplex");
        services.AddOptions<KeyManagementOptions>().Configure<IConfiguration, ILoggerFactory>((options, config, loggers) =>
        {
            options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(Path.Combine(DataPath(config), "keys")), loggers);
            if (OperatingSystem.IsWindows())
                options.XmlEncryptor = new DpapiXmlEncryptor(protectToLocalMachine: false, loggers);
        });
        return services;
    }

    private sealed class Marker;
}
