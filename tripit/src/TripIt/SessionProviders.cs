using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripIt.Auth;

namespace TripIt;

/// <summary>
/// Supplies the <see cref="TripItSession"/> a <see cref="TripItClient"/> sends, and a
/// replacement when TripIt rejects it. The current snapshot is swapped atomically; a client
/// reads it once per request.
/// </summary>
public interface ITripItSessionProvider
{
    /// <summary>The snapshot to send now. Never null; <see cref="TripItSession.Empty"/> when there is none.</summary>
    TripItSession Current { get; }

    /// <summary>Whether <see cref="RenewAsync"/> can obtain new cookies from a browser (for status reports).</summary>
    bool CanRefresh { get; }

    /// <summary>
    /// Called after TripIt rejected <paramref name="rejected"/> (or when no session is
    /// configured). Returns the snapshot to retry with, or null when nothing better is
    /// available. The client retries only when the returned cookie differs from the rejected one.
    /// </summary>
    Task<TripItSession?> RenewAsync(TripItSession rejected, CancellationToken ct);
}

/// <summary>
/// Holds one snapshot, optionally renewed by a caller-supplied function. Used by hosts that
/// keep sessions elsewhere (Multiplex reads a per-account snapshot per request) and by tests.
/// Without a renew function it never reloads anything, so it cannot fall back to another
/// account's session.
/// </summary>
public sealed class SnapshotSessionProvider : ITripItSessionProvider
{
    private readonly Func<TripItSession, CancellationToken, Task<TripItSession?>>? _renew;
    private TripItSession _current;

    public SnapshotSessionProvider(TripItSession session,
        Func<TripItSession, CancellationToken, Task<TripItSession?>>? renew = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _current = session;
        _renew = renew;
    }

    public TripItSession Current => Volatile.Read(ref _current);

    public bool CanRefresh => _renew is not null;

    public async Task<TripItSession?> RenewAsync(TripItSession rejected, CancellationToken ct)
    {
        if (_renew is null) return null;
        var next = await _renew(rejected, ct).ConfigureAwait(false);
        if (next is not null) Volatile.Write(ref _current, next);
        return next;
    }
}

/// <summary>
/// The local (stdio MCP server, PowerShell module) session source. Loads, in order:
/// <list type="number">
///   <item>an injected <see cref="ISessionStore"/> (user secrets, DPAPI), if it has a session;</item>
///   <item>the <c>TRIPIT_COOKIE</c> / <c>TRIPIT_CSRF</c> environment variables;</item>
///   <item>the file named by <c>TRIPIT_SESSION_FILE</c>, else <c>~/.tripit-mcp/session.json</c>.</item>
/// </list>
/// On renewal it re-reads those sources; only when they still hold the rejected cookie does it
/// run the <see cref="ISessionRefresher"/> (at most once per cool-down), then re-read again.
/// </summary>
public sealed class LocalSessionProvider : ITripItSessionProvider
{
    /// <summary>Minimum time between two refresher runs.</summary>
    public static readonly TimeSpan DefaultRefreshCooldown = TimeSpan.FromSeconds(30);

    private readonly ISessionStore? _store;
    private readonly ISessionRefresher? _refresher;
    private readonly bool _useEnvironmentAndFile;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _cooldown;
    private readonly SemaphoreSlim _renewLock = new(1, 1);
    private TripItSession _current;
    private DateTimeOffset? _lastRefresh;

    /// <param name="store">Checked first when given.</param>
    /// <param name="refresher">Obtains new cookies from the browser; null disables refreshing.</param>
    /// <param name="log">Receives load failures (never values).</param>
    /// <param name="useEnvironmentAndFile">False limits loading to <paramref name="store"/> (tests).</param>
    /// <param name="time">Clock for the refresh cool-down.</param>
    /// <param name="refreshCooldown">Defaults to <see cref="DefaultRefreshCooldown"/>.</param>
    public LocalSessionProvider(ISessionStore? store = null, ISessionRefresher? refresher = null, ILogger? log = null,
        bool useEnvironmentAndFile = true, TimeProvider? time = null, TimeSpan? refreshCooldown = null)
    {
        _store = store;
        _refresher = refresher;
        _useEnvironmentAndFile = useEnvironmentAndFile;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _cooldown = refreshCooldown ?? DefaultRefreshCooldown;
        _current = Load();
    }

    public TripItSession Current => Volatile.Read(ref _current);

    public bool CanRefresh => _refresher is not null;

    /// <summary>Re-reads the sources and swaps in what they hold.</summary>
    public TripItSession Reload()
    {
        var loaded = Load();
        Volatile.Write(ref _current, loaded);
        return loaded;
    }

    /// <summary>Persists credentials to the store (when there is one) and makes them current.</summary>
    public void Save(SessionCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _store?.Save(credentials);
        Volatile.Write(ref _current, new TripItSession(credentials.Cookie, credentials.CsrfToken,
            source: _store?.Description ?? "in-memory"));
    }

    public async Task<TripItSession?> RenewAsync(TripItSession rejected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        await _renewLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another request may already have renewed while this one waited.
            var current = Current;
            if (current.IsConfigured && current.Cookie != rejected.Cookie) return current;

            // The person may have re-run extract-session.js: re-read before refreshing.
            var loaded = Reload();
            if (loaded.IsConfigured && loaded.Cookie != rejected.Cookie) return loaded;

            // Nothing configured at all: there is no signed-in browser to refresh from.
            if (!rejected.IsConfigured || _refresher is null) return null;

            var now = _time.GetUtcNow();
            if (_lastRefresh is { } last && now - last < _cooldown)
            {
                _log.LogWarning("TripIt session was rejected again within {Cooldown}; not refreshing yet.", _cooldown);
                return null;
            }
            _lastRefresh = now;
            _log.LogWarning("TripIt rejected the session from {Source}; refreshing it.", rejected.Source ?? "(unknown)");
            try
            {
                await _refresher.RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError("TripIt session refresh failed ({Error}).", ex.GetType().Name);
                _log.LogDebug(ex, "TripIt session refresh failure detail.");
                return null;
            }
            loaded = Reload();
            return loaded.IsConfigured && loaded.Cookie != rejected.Cookie ? loaded : null;
        }
        finally
        {
            _renewLock.Release();
        }
    }

    private TripItSession Load()
    {
        if (_store is not null && _store.TryLoad(out var stored))
        {
            return new TripItSession(stored.Cookie, stored.CsrfToken, source: _store.Description);
        }
        if (!_useEnvironmentAndFile) return TripItSession.Empty;

        var envCookie = Environment.GetEnvironmentVariable("TRIPIT_COOKIE");
        if (!string.IsNullOrWhiteSpace(envCookie))
        {
            return new TripItSession(envCookie, Environment.GetEnvironmentVariable("TRIPIT_CSRF") ?? "", source: "environment");
        }

        var path = Environment.GetEnvironmentVariable("TRIPIT_SESSION_FILE")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tripit-mcp", "session.json");
        return LoadFile(path, _log) ?? TripItSession.Empty;
    }

    /// <summary>
    /// Reads a snapshot file. Returns null when the file is absent or unreadable; the reason
    /// (never a value) is logged as a warning.
    /// </summary>
    public static TripItSession? LoadFile(string path, ILogger? log = null)
    {
        log ??= NullLogger.Instance;
        if (!File.Exists(path)) return null;
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Ignoring TripIt session file {Path}: it could not be read ({Error}).", path, ex.GetType().Name);
            return null;
        }
        if (TripItSession.TryFromSnapshotJson(json, path, out var session, out var error)) return session;
        log.LogWarning("Ignoring TripIt session file {Path}: {Reason}", path, error);
        return null;
    }
}
