using System.Management.Automation;
using System.Runtime.InteropServices;
using TripIt;
using TripIt.Auth;

namespace TripIt.PowerShell;

/// <summary>
/// Base class for all TripIt cmdlets. Provides a shared <see cref="TripItClient"/> instance
/// (cached per-process) and a <c>-StoreLocation</c> parameter for picking the session store.
/// </summary>
public abstract class TripItCmdletBase : PSCmdlet
{
    [Parameter]
    [ValidateSet("UserSecrets", "Dpapi")]
    public string? StoreLocation { get; set; }

    private static TripItClient? s_cachedClient;
    private static string? s_cachedStoreLocation;

    protected TripItClient GetClient()
    {
        if (s_cachedClient is not null && s_cachedStoreLocation == StoreLocation)
        {
            return s_cachedClient;
        }
        var store = ResolveStore();
        s_cachedClient = new TripItClient(new LocalSessionProvider(store));
        s_cachedStoreLocation = StoreLocation;
        return s_cachedClient;
    }

    /// <summary>
    /// Returns the configured store, or null to fall back to env / file.
    /// </summary>
    protected ISessionStore? ResolveStore()
    {
        return StoreLocation switch
        {
            "UserSecrets" => new UserSecretsSessionStore(),
            "Dpapi" => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new DpapiSessionStore()
                : throw new PlatformNotSupportedException(
                    "DPAPI is only available on Windows. Use -StoreLocation UserSecrets instead."),
            _ => null,
        };
    }

    /// <summary>
    /// Synchronous wrapper for async work. Honors <see cref="PSCmdlet.Stopping"/> via a
    /// linked CancellationTokenSource.
    /// </summary>
    protected T RunSync<T>(Func<CancellationToken, Task<T>> work)
    {
        using var cts = new CancellationTokenSource();
        // Periodically check for stop; PowerShell 7+ surfaces it via PipelineStopped.
        try
        {
            return work(cts.Token).GetAwaiter().GetResult();
        }
        catch (PipelineStoppedException)
        {
            cts.Cancel();
            throw;
        }
    }

    protected void RunSync(Func<CancellationToken, Task> work)
    {
        using var cts = new CancellationTokenSource();
        try
        {
            work(cts.Token).GetAwaiter().GetResult();
        }
        catch (PipelineStoppedException)
        {
            cts.Cancel();
            throw;
        }
    }
}
