using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Multiplex.Web.Auth.Store;

/// <summary>
/// Persists the auth state to <c>&lt;DataPath&gt;/auth.dat</c>, encrypted with ASP.NET Core Data Protection
/// using a key ring in <c>&lt;DataPath&gt;/keys</c> (DPAPI-protected on Windows). Writes go to a temporary
/// file that then replaces the store, so a crash never leaves half a file. Nothing (store or key ring)
/// touches the disk until the first read or write. Assumes one server process per data folder.
/// </summary>
public sealed partial class FileAuthStore(
    IOptions<MultiplexAuthOptions> options,
    TimeProvider time,
    ILogger<FileAuthStore> logger) : AuthStoreBase(time)
{
    private const string Purpose = "Multiplex.Auth.Store.v1";

    private IDataProtector? _protector;

    private string DataPath => options.Value.ResolveDataPath();

    private string StorePath => Path.Combine(DataPath, "auth.dat");

    /// <summary>Created on first use; the app-wide Data Protection setup is left alone.</summary>
    private IDataProtector Protector => _protector ??= DataProtectionProvider
        .Create(new DirectoryInfo(Path.Combine(DataPath, "keys")), builder =>
        {
            builder.SetApplicationName("Multiplex");
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        })
        .CreateProtector(Purpose);

    protected override async Task<AuthState> LoadAsync(CancellationToken ct)
    {
        var path = StorePath;
        if (!File.Exists(path)) return new AuthState();

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(path, ct);
            return AuthStateSerializer.Deserialize(Protector.Unprotect(protectedBytes));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Unreadable (key ring lost or file damaged): start empty so clients reconnect, and keep the file for inspection.
            var aside = $"{path}.unreadable-{Time.GetUtcNow():yyyyMMddHHmmss}";
            File.Move(path, aside, overwrite: true);
            LogUnreadable(logger, aside, ex.GetType().Name);
            return new AuthState();
        }
    }

    protected override async Task SaveAsync(AuthState state, CancellationToken ct)
    {
        var path = StorePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var bytes = Protector.Protect(AuthStateSerializer.Serialize(state));
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "The auth store could not be read ({Reason}); starting empty, so every client must reconnect. The old file was moved to {Path}.")]
    private static partial void LogUnreadable(ILogger logger, string path, string reason);
}
