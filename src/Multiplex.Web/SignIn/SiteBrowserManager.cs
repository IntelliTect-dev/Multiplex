using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PuppeteerSharp;

namespace Multiplex.Web.SignIn;

/// <summary>
/// Launches the server-owned Chrome/Chromium browsers used to sign in to sites and to refresh
/// their sessions. Every browser runs on a profile folder under <c>Multiplex:DataPath</c>:
/// a fresh <c>temp-*</c> one per live sign-in, or an account's persistent one for a refresh.
/// </summary>
public sealed class SiteBrowserManager(
    IConfiguration config,
    IOptions<SignInOptions> options,
    SignInPaths paths,
    ILogger<SiteBrowserManager> logger) : IDisposable
{
    private readonly SemaphoreSlim _liveSlots = new(Math.Max(1, options.Value.MaxLiveBrowsers));
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountLocks = new(StringComparer.Ordinal);
    private string? _downloadedExecutable;

    public SignInPaths Paths => paths;

    /// <summary>Takes one of the live sign-in browser slots, or returns null when all are busy.</summary>
    public IDisposable? TryAcquireLiveSlot() => _liveSlots.Wait(0) ? new Releaser(_liveSlots) : null;

    /// <summary>
    /// One lock per account, shared by refresh, profile replacement and adopt, so Chrome never
    /// opens a profile that is being replaced (Chrome also locks a profile to one process).
    /// </summary>
    public async Task<IDisposable> LockAccountAsync(string site, string accountId, CancellationToken ct)
    {
        var gate = _accountLocks.GetOrAdd(SignInPaths.CheckSlug(site) + "\n" + accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    /// <summary>Whether this site runs its browsers headless: <c>Multiplex:Sites:{slug}:Headless</c>, else <c>SignIn:Headless</c>.</summary>
    public bool IsHeadless(string site) =>
        config.GetValue<bool?>($"Multiplex:Sites:{site}:Headless") ?? options.Value.Headless;

    /// <summary>Launches a browser on <paramref name="profileDir"/>, which must be inside the data folder.</summary>
    public async Task<IBrowser> LaunchAsync(string site, string profileDir, CancellationToken ct)
    {
        var dir = paths.EnsureInsideDataPath(profileDir);
        Directory.CreateDirectory(dir);
        var o = options.Value;
        var headless = IsHeadless(site);

        List<string> args = ["--no-first-run", "--no-default-browser-check"];
        if (o.NoSandbox) args.Add("--no-sandbox");
        if (!headless) args.Add($"--window-size={o.ViewportWidth + 40},{o.ViewportHeight + 140}");

        // No user-agent override and no automation-hiding flags: the site sees the browser as it is.
        var browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            HeadlessMode = headless ? HeadlessMode.True : HeadlessMode.False,
            ExecutablePath = await ExecutablePathAsync(ct),
            UserDataDir = dir,
            Args = [.. args],
            DefaultViewport = new ViewPortOptions
            {
                Width = o.ViewportWidth,
                Height = o.ViewportHeight,
                DeviceScaleFactor = o.DeviceScaleFactor,
            },
            Timeout = 60_000,
        });
        logger.LogInformation("Launched {Mode} browser for {Site} on profile {Profile}.",
            headless ? "headless" : "visible", site, Path.GetFileName(dir));
        return browser;
    }

    /// <summary>Closes the browser and waits for Chrome to exit, so its profile folder can be moved or deleted.</summary>
    public async Task CloseAsync(IBrowser? browser)
    {
        if (browser is null) return;
        try
        {
            if (!browser.IsClosed) await browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            logger.LogWarning("Closing a site browser failed ({Error}); killing it.", ex.GetType().Name);
        }
        try
        {
            if (browser.Process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
        }
        try { browser.Dispose(); } catch (Exception) { }
    }

    /// <summary>Deletes a profile folder (inside the data folder only), retrying while Chrome lets go of its files.</summary>
    public async Task DeleteProfileAsync(string profileDir)
    {
        var dir = paths.EnsureInsideDataPath(profileDir);
        await RetryAsync(() =>
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        });
    }

    /// <summary>
    /// Makes <paramref name="tempProfile"/> the account's persistent profile, replacing an older
    /// one. The caller holds the account lock and has closed the browser.
    /// </summary>
    public async Task<string> PromoteProfileAsync(string site, string tempProfile, string accountId)
    {
        var source = paths.EnsureInsideDataPath(tempProfile);
        var target = paths.EnsureInsideDataPath(paths.AccountProfile(site, accountId));
        if (Directory.Exists(target))
        {
            var old = paths.EnsureInsideDataPath(SignInPaths.OldProfile(target));
            await RetryAsync(() => Directory.Move(target, old));
            await DeleteProfileAsync(old);
        }
        await RetryAsync(() => Directory.Move(source, target));
        return target;
    }

    /// <summary>Chrome holds <c>lockfile</c> open (Windows) or keeps a <c>SingletonLock</c> link (Linux/macOS) while it runs on a profile.</summary>
    internal static bool IsProfileInUse(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var lockFile = Path.Combine(dir, "lockfile");
            if (!File.Exists(lockFile)) return false;
            try
            {
                using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }
        return File.Exists(Path.Combine(dir, "SingletonLock")) || new FileInfo(Path.Combine(dir, "SingletonLock")).LinkTarget is not null;
    }

    private static async Task RetryAsync(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                await Task.Delay(250);
            }
        }
    }

    private async Task<string> ExecutablePathAsync(CancellationToken ct)
    {
        var configured = config["Browser:ExecutablePath"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        if (_downloadedExecutable is not null) return _downloadedExecutable;

        await _fetchLock.WaitAsync(ct);
        try
        {
            if (_downloadedExecutable is not null) return _downloadedExecutable;
            logger.LogInformation("Browser:ExecutablePath not set; downloading Chromium via Puppeteer.");
            _downloadedExecutable = (await new BrowserFetcher().DownloadAsync()).GetExecutablePath();
            return _downloadedExecutable;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    public void Dispose()
    {
        _liveSlots.Dispose();
        _fetchLock.Dispose();
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
