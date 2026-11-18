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
/// authorization, or from one client address, lock that key for 15 minutes. Each try is
/// reserved against both keys before the (slow) hash check runs, so a burst of concurrent
/// guesses never gets more than <see cref="MaxFailures"/> checks in before the lock.
/// </summary>
public sealed class PasscodeGate
{
    public const string HashKey = "Multiplex:Gate:PasscodeHash";
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    private const int SweepThreshold = 10_000;

    private readonly IConfiguration _config;
    private readonly TimeProvider _time;
    private readonly ILogger<PasscodeGate> _logger;
    private readonly Func<string?, string?, bool> _verify;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Tries> _tries = new(StringComparer.Ordinal);

    public PasscodeGate(IConfiguration config, TimeProvider time, ILogger<PasscodeGate> logger)
        : this(config, time, logger, PasscodeHasher.Verify)
    {
    }

    /// <summary>For tests: <paramref name="verify"/> replaces <see cref="PasscodeHasher.Verify"/>.</summary>
    internal PasscodeGate(IConfiguration config, TimeProvider time, ILogger<PasscodeGate> logger, Func<string?, string?, bool> verify)
    {
        _config = config;
        _time = time;
        _logger = logger;
        _verify = verify;
    }

    public bool IsConfigured => PasscodeHasher.IsValidFormat(_config[HashKey]);

    public bool IsLockedOut(string pendingId, string clientIp)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            return Keys(pendingId, clientIp).Any(key => _tries.TryGetValue(key, out var tries) && tries.IsLocked(now));
        }
    }

    public GateResult Check(string pendingId, string clientIp, string? passcode)
    {
        var stored = _config[HashKey];
        if (!PasscodeHasher.IsValidFormat(stored))
        {
            _logger.LogWarning("Connect gate refused: {Key} is not configured. Run the install script to set a passcode.", HashKey);
            return GateResult.NotConfigured;
        }

        var keys = Keys(pendingId, clientIp);
        if (!TryReserve(keys))
        {
            _logger.LogWarning("Connect gate locked out for pending {PendingRef} / client {ClientIp}.", SignInLog.PendingId(pendingId), clientIp);
            return GateResult.LockedOut;
        }

        bool passed;
        try
        {
            // The slow PBKDF2 check runs outside the lock; the reservation already counts it.
            passed = _verify(passcode, stored);
        }
        catch
        {
            Settle(keys, Outcome.Undecided);
            throw;
        }

        if (passed)
        {
            Settle(keys, Outcome.Passed);
            return GateResult.Passed;
        }

        var locked = Settle(keys, Outcome.Wrong);
        _logger.LogWarning("Wrong connect passcode for pending {PendingRef} from {ClientIp}.", SignInLog.PendingId(pendingId), clientIp);
        return locked ? GateResult.LockedOut : GateResult.Wrong;
    }

    private static string[] Keys(string pendingId, string clientIp) => ["p:" + pendingId, "ip:" + clientIp];

    /// <summary>
    /// Takes one try against every key, or none: refused while a key is locked or while its
    /// recorded failures plus the tries still being checked already reach the limit.
    /// </summary>
    private bool TryReserve(string[] keys)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_tries.Count > SweepThreshold) Sweep(now);
            foreach (var key in keys)
            {
                if (_tries.TryGetValue(key, out var tries) && (tries.IsLocked(now) || tries.Failures + tries.InFlight >= MaxFailures))
                    return false;
            }
            foreach (var key in keys)
            {
                if (!_tries.TryGetValue(key, out var tries)) _tries[key] = tries = new Tries();
                tries.InFlight++;
            }
            return true;
        }
    }

    /// <summary>Turns a reserved try into its outcome. Returns true when a key is now locked.</summary>
    private bool Settle(string[] keys, Outcome outcome)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var locked = false;
            foreach (var key in keys)
            {
                if (!_tries.TryGetValue(key, out var tries)) continue;
                tries.InFlight = Math.Max(0, tries.InFlight - 1);
                switch (outcome)
                {
                    case Outcome.Passed:
                        // The passcode holder is at this address: earlier wrong tries no longer count.
                        tries.Failures = 0;
                        tries.LockedUntil = null;
                        if (tries.InFlight == 0) _tries.Remove(key);
                        break;
                    case Outcome.Wrong:
                        tries.IsLocked(now); // lets a served lock or an old run of failures lapse first
                        tries.LastFailure = now;
                        tries.Failures++;
                        if (tries.Failures >= MaxFailures && tries.LockedUntil is null) tries.LockedUntil = now + LockDuration;
                        locked |= tries.LockedUntil is not null;
                        break;
                }
            }
            return locked;
        }
    }

    /// <summary>Drops records that no longer lock or count anything, so a flood of pending ids cannot grow memory without bound.</summary>
    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, tries) in _tries.ToList())
        {
            if (tries.InFlight == 0 && !tries.IsLocked(now) && tries.Failures == 0) _tries.Remove(key);
        }
    }

    private enum Outcome
    {
        Passed,
        Wrong,

        /// <summary>The check itself failed: the try is given back, neither passed nor wrong.</summary>
        Undecided,
    }

    /// <summary>One key's tries. Guarded by the gate's lock.</summary>
    private sealed class Tries
    {
        public int Failures;
        public int InFlight;
        public DateTimeOffset LastFailure;
        public DateTimeOffset? LockedUntil;

        /// <summary>Whether the key is locked now; first lets a served lock, or failures older than the lock window, lapse.</summary>
        public bool IsLocked(DateTimeOffset now)
        {
            if (LockedUntil is { } until)
            {
                if (until > now) return true;
                LockedUntil = null; // lock served; start counting afresh
                Failures = 0;
            }
            else if (Failures > 0 && now - LastFailure > LockDuration)
            {
                Failures = 0;
            }
            return false;
        }
    }
}

/// <summary>
/// Proof that this browser passed the gate for one pending authorization: an HttpOnly,
/// SameSite=Strict, data-protected, time-limited cookie scoped to <c>/connect/{slug}</c>.
/// The live WebSocket, "Sign in", "Cancel" and "Done" all require it.
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
            // Behind an HTTPS tunnel the app itself sees http; a false header can only make the cookie stricter.
            Secure = context.Request.IsHttps
                     || string.Equals(context.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase),
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
