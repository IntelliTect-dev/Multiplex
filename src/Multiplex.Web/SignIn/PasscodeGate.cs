using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Multiplex.Web.SignIn;

public enum GateResult
{
    Passed,
    Wrong,
    LockedOut,

    /// <summary>No (valid) <c>Multiplex:Gate:PasscodeHash</c>: the gate fails closed.</summary>
    NotConfigured,
}

/// <summary>
/// The owner passcode in front of every connect flow. Five wrong tries for one pending
/// authorization, or from one client address, lock that key for 15 minutes.
/// </summary>
public sealed class PasscodeGate(IConfiguration config, TimeProvider time, ILogger<PasscodeGate> logger)
{
    public const string HashKey = "Multiplex:Gate:PasscodeHash";
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, FailureRecord> _failures = new();

    public bool IsConfigured => PasscodeHasher.IsValidFormat(config[HashKey]);

    public bool IsLockedOut(string pendingId, string clientIp) =>
        LockedUntil("p:" + pendingId) is not null || LockedUntil("ip:" + clientIp) is not null;

    public GateResult Check(string pendingId, string clientIp, string? passcode)
    {
        var stored = config[HashKey];
        if (!PasscodeHasher.IsValidFormat(stored))
        {
            logger.LogWarning("Connect gate refused: {Key} is not configured. Run the install script to set a passcode.", HashKey);
            return GateResult.NotConfigured;
        }

        if (IsLockedOut(pendingId, clientIp))
        {
            logger.LogWarning("Connect gate locked out for pending {PendingId} / client {ClientIp}.", pendingId, clientIp);
            return GateResult.LockedOut;
        }

        if (PasscodeHasher.Verify(passcode, stored))
        {
            _failures.TryRemove("p:" + pendingId, out _);
            return GateResult.Passed;
        }

        var lockedPending = RecordFailure("p:" + pendingId);
        var lockedIp = RecordFailure("ip:" + clientIp);
        logger.LogWarning("Wrong connect passcode for pending {PendingId} from {ClientIp}.", pendingId, clientIp);
        return lockedPending || lockedIp ? GateResult.LockedOut : GateResult.Wrong;
    }

    private DateTimeOffset? LockedUntil(string key)
    {
        if (!_failures.TryGetValue(key, out var record)) return null;
        lock (record)
        {
            if (record.LockedUntil is { } until && until > time.GetUtcNow()) return until;
            if (record.LockedUntil is not null)
            {
                // Lock served; start counting afresh.
                record.LockedUntil = null;
                record.Count = 0;
            }
            return null;
        }
    }

    private bool RecordFailure(string key)
    {
        if (_failures.Count > 10_000) Sweep();
        var record = _failures.GetOrAdd(key, _ => new FailureRecord());
        lock (record)
        {
            var now = time.GetUtcNow();
            // Failures older than the lock window no longer count towards a lock.
            if (now - record.LastFailure > LockDuration) record.Count = 0;
            record.LastFailure = now;
            record.Count++;
            if (record.Count < MaxFailures) return false;
            record.LockedUntil = now + LockDuration;
            return true;
        }
    }

    /// <summary>Drops records that no longer lock anything, so a flood of pending ids cannot grow memory without bound.</summary>
    private void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var (key, record) in _failures)
        {
            bool stale;
            lock (record) stale = record.LockedUntil is not { } until || until <= now ? now - record.LastFailure > LockDuration : false;
            if (stale) _failures.TryRemove(key, out _);
        }
    }

    private sealed class FailureRecord
    {
        public int Count;
        public DateTimeOffset LastFailure;
        public DateTimeOffset? LockedUntil;
    }
}

/// <summary>
/// Proof that this browser passed the gate for one pending authorization: an HttpOnly,
/// SameSite=Strict, data-protected, time-limited cookie scoped to <c>/connect/{slug}</c>.
/// The live WebSocket, "Continue", "Cancel" and "Done" all require it.
/// </summary>
public sealed class GateCookie(IDataProtectionProvider provider, IOptions<SignInOptions> options, TimeProvider time)
{
    private readonly ITimeLimitedDataProtector _protector =
        provider.CreateProtector("Multiplex.SignIn.Gate.v1").ToTimeLimitedDataProtector();

    public static string Name(string slug) => "mx_gate_" + slug;

    public void Issue(HttpContext context, string slug, string pendingId)
    {
        var lifetime = options.Value.GateLifetime;
        var value = _protector.Protect(slug + "\n" + pendingId, time.GetUtcNow() + lifetime);
        context.Response.Cookies.Append(Name(slug), value, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/connect/" + slug,
            MaxAge = lifetime,
            IsEssential = true,
        });
    }

    /// <summary>True when the request carries a valid gate cookie for exactly this site and pending id.</summary>
    public bool HasPassed(HttpContext context, string slug, string pendingId) =>
        PendingIdOf(context, slug) is { } passed && string.Equals(passed, pendingId, StringComparison.Ordinal);

    /// <summary>The pending id the request's gate cookie was issued for, if it is valid.</summary>
    public string? PendingIdOf(HttpContext context, string slug)
    {
        if (!context.Request.Cookies.TryGetValue(Name(slug), out var value) || string.IsNullOrEmpty(value)) return null;
        try
        {
            var text = _protector.Unprotect(value, out var expiration);
            if (expiration < time.GetUtcNow()) return null;
            var parts = text.Split('\n');
            return parts.Length == 2 && parts[0] == slug ? parts[1] : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
