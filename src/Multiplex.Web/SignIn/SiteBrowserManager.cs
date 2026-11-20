using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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

    /// <summary>
    /// How long moving or deleting a profile waits for Chrome's helper processes (and, on Windows,
    /// antivirus) to let go of its files after the browser closed. Measured: more than 5 s on a busy
    /// PC, which used to fail the sign-in at the last step.
    /// </summary>
    internal TimeSpan ProfileReleaseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long deleting a profile nobody needs any more (a cancelled or refused sign-in, an old
    /// profile already replaced) waits before leaving it to the startup sweep.
    /// </summary>
    internal TimeSpan DiscardTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Takes one of the live sign-in browser slots, or returns null when all are busy.</summary>
    public IDisposable? TryAcquireLiveSlot() => _liveSlots.Wait(0) ? new Releaser(_liveSlots) : null;

    /// <summary>
    /// One lock per person and account, shared by refresh and profile replacement, so Chrome never
    /// opens a profile that is being replaced (Chrome also locks a profile to one process).
    /// </summary>
    public async Task<IDisposable> LockAccountAsync(string owner, string site, string accountId, CancellationToken ct)
    {
        var key = SignInPaths.CheckOwner(owner) + "\n" + SignInPaths.CheckSlug(site) + "\n" + accountId;
        var gate = _accountLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
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

        var browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            HeadlessMode = headless ? HeadlessMode.True : HeadlessMode.False,
            ExecutablePath = await ExecutablePathAsync(ct),
            IgnoreDefaultArgs = true,
            Args = LaunchArguments(dir, headless, o),
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

    /// <summary>
    /// The browser is started the way a person starts Chrome on a profile of its own, as the
    /// capture tooling's native mode does: the profile, a debugging port to drive it, and no
    /// first-run screens. Puppeteer's default switches are left out, above all
    /// <c>--enable-automation</c>, and the port is a real one rather than Puppeteer's ephemeral
    /// <c>0</c>: Chrome turns on <c>navigator.webdriver</c> for either, and TripIt's bot
    /// protection refused a sign-in in such a browser (#61). Nothing is added to hide what the
    /// browser is either: no user-agent override, no automation-hiding flags.
    /// </summary>
    private static string[] LaunchArguments(string profileDir, bool headless, SignInOptions o)
    {
        List<string> args =
        [
            $"--user-data-dir=\"{Path.TrimEndingDirectorySeparator(profileDir)}\"",
            $"--remote-debugging-port={FreeLoopbackPort()}",
            "--no-first-run",
            "--no-default-browser-check",
        ];
        if (o.NoSandbox) args.Add("--no-sandbox");
        args.Add(headless ? "--headless=new" : $"--window-size={o.ViewportWidth + 40},{o.ViewportHeight + 140}");
        // The person sees this window on their phone, not on the PC's screen, which may be locked
        // or covered: keep it rendering (and its timers running) as a window in view would.
        args.AddRange(["--disable-backgrounding-occluded-windows", "--disable-renderer-backgrounding", "--disable-background-timer-throttling"]);
        args.Add("about:blank");
        return [.. args];
    }

    /// <summary>A loopback port free right now, for the browser's debugging port (Chrome listens on loopback only).</summary>
    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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

    /// <summary>
    /// Deletes a profile folder (inside the data folder only), waiting up to
    /// <see cref="ProfileReleaseTimeout"/> while Chrome lets go of its files; then the file error is thrown.
    /// </summary>
    public async Task DeleteProfileAsync(string profileDir, CancellationToken ct = default)
    {
        var dir = paths.EnsureInsideDataPath(profileDir);
        await RetryAsync(() => { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); },
            "delete", dir, TimeProvider.System.GetTimestamp(), ProfileReleaseTimeout, ct);
    }

    /// <summary>
    /// Best effort: deletes a profile nobody needs any more, waiting at most
    /// <see cref="DiscardTimeout"/>. Returns whether it is gone; a leftover is removed by the
    /// startup sweep (<c>temp-*</c>, <c>*.old-*</c>), so this never throws for a held file.
    /// </summary>
    public async Task<bool> DiscardProfileAsync(string profileDir)
    {
        var dir = paths.EnsureInsideDataPath(profileDir);
        try
        {
            await RetryAsync(() => { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); },
                "delete", dir, TimeProvider.System.GetTimestamp(), DiscardTimeout, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Left browser profile {Profile} for the startup sweep: it is still in use ({Error}).", Path.GetFileName(dir), ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Makes <paramref name="tempProfile"/> the account's persistent profile, replacing an older
    /// one. The caller holds the account lock and has closed the browser. The moves share one
    /// wait of <see cref="ProfileReleaseTimeout"/>, and stop when <paramref name="ct"/> is
    /// cancelled. If the new profile cannot move in, the old one is put back. Deleting the old
    /// profile afterwards is best effort and never costs the new sign-in.
    /// </summary>
    public async Task<string> PromoteProfileAsync(string owner, string site, string tempProfile, string accountId, CancellationToken ct = default)
    {
        var source = paths.EnsureInsideDataPath(tempProfile);
        var target = paths.EnsureInsideDataPath(paths.AccountProfile(owner, site, accountId));
        var started = TimeProvider.System.GetTimestamp();
        string? old = null;
        if (Directory.Exists(target))
        {
            old = paths.EnsureInsideDataPath(SignInPaths.OldProfile(target));
            await RetryAsync(() => Directory.Move(target, old), "move aside", target, started, ProfileReleaseTimeout, ct);
        }
        try
        {
            await RetryAsync(() => Directory.Move(source, target), "move in", source, started, ProfileReleaseTimeout, ct);
        }
        catch (Exception) when (old is not null)
        {
            try { Directory.Move(old, target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("Could not put back the previous browser profile for {Site} account {AccountId} ({Error}).", site, accountId, ex.GetType().Name);
            }
            throw;
        }
        if (old is not null) await DiscardProfileAsync(old);
        return target;
    }

    /// <summary>
    /// Whether a file error means another process still holds files in the folder: sharing or
    /// lock violation, access denied (a folder with open files cannot be moved), or a folder
    /// emptied while being deleted. Anything else (a bad path, a name already taken) is not
    /// waited out.
    /// </summary>
    private static bool IsHeld(Exception ex) => ex switch
    {
        UnauthorizedAccessException => true,
        IOException io => (io.HResult & 0xFFFF) is 5 or 32 or 33 or 145,
        _ => false,
    };

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

    /// <summary>
    /// Runs a profile move or delete, retrying while its files are still held (<see cref="IsHeld"/>)
    /// with a growing pause (250 ms doubling to 2 s), until <paramref name="budget"/> has passed
    /// since <paramref name="started"/>; then the last file error is thrown. Cancellation stops
    /// the wait at once.
    /// </summary>
    private async Task RetryAsync(Action action, string what, string dir, long started, TimeSpan budget, CancellationToken ct)
    {
        var pause = TimeSpan.FromMilliseconds(250);
        var reported = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                action();
                if (reported)
                    logger.LogDebug("Browser profile {Profile} was released for {What} after {Seconds:F1} s.", Path.GetFileName(dir), what, TimeProvider.System.GetElapsedTime(started).TotalSeconds);
                return;
            }
            catch (Exception ex) when (IsHeld(ex))
            {
                var waited = TimeProvider.System.GetElapsedTime(started);
                if (waited >= budget)
                {
                    logger.LogWarning("Gave up waiting to {What} browser profile {Profile} after {Seconds:F1} s: its files are still in use ({Error}).",
                        what, Path.GetFileName(dir), waited.TotalSeconds, ex.GetType().Name);
                    throw;
                }
                if (!reported && waited > TimeSpan.FromSeconds(2))
                {
                    reported = true;
                    logger.LogInformation("Waiting for Chrome to release browser profile {Profile} to {What} ({Error}).", Path.GetFileName(dir), what, ex.GetType().Name);
                }
                var remaining = budget - waited;
                await Task.Delay(pause < remaining ? pause : remaining, ct);
                pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, TimeSpan.FromSeconds(2).Ticks));
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
