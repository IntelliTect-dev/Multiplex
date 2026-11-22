using System.Reflection;
using System.Threading.RateLimiting;

namespace Multiplex.Sites.GoogleFlights;

/// <summary>
/// Settings for <see cref="GoogleFlightsClient"/>. The defaults keep well under the request rate that has
/// got a network blocked by Google (about 230 requests in 35 minutes, for an earlier project); lower them
/// rather than raise them.
/// </summary>
public sealed class GoogleFlightsClientOptions
{
    /// <summary>Where the client's own documentation lives; it is named in the User-Agent.</summary>
    public const string InfoUrl = "https://github.com/IntelliTect-dev/Multiplex/tree/main/services/GoogleFlights";

    /// <summary>Currency used when a request names none (ISO 4217).</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Region (two-letter country code) used when a request names none.</summary>
    public string Region { get; set; } = "US";

    /// <summary>How long an answer is reused for the same request. Booking options are never cached.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(12);

    /// <summary>The most answers kept in the cache; the oldest go first.</summary>
    public int CacheCapacity { get; set; } = 256;

    /// <summary>Requests that may go to Google back to back before pacing starts.</summary>
    public int BurstRequests { get; set; } = 4;

    /// <summary>After a burst, one more request may go to Google per interval.</summary>
    public TimeSpan RequestInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Requests that may wait for their turn; one more is refused locally, without reaching Google.</summary>
    public int MaxWaitingRequests { get; set; } = 4;

    /// <summary>
    /// How long the client stops sending anything to Google after Google refuses a request. It doubles with each
    /// refusal in a row, up to <see cref="MaxRefusalCooldown"/>, and resets when Google answers again.
    /// </summary>
    public TimeSpan RefusalCooldown { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan MaxRefusalCooldown { get; set; } = TimeSpan.FromHours(6);

    /// <summary>The longest one attempt may take.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest a call may take, including waiting for its turn and retries.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Retries of a transient failure (a server error, a dropped connection). Refusals are never retried.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>The wait before the first retry; it doubles for each later one, with jitter.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Replaces the client's pacing (a token bucket from <see cref="BurstRequests"/>, <see cref="RequestInterval"/>
    /// and <see cref="MaxWaitingRequests"/>). One limiter should serve every client that shares a network.
    /// </summary>
    public RateLimiter? RateLimiter { get; set; }

    /// <summary>
    /// The User-Agent the client sends: Chrome's form, followed by this project's name and link in the conventional
    /// <c>(compatible; ...)</c> comment. Google serves its search pages only to browser User-Agents and redirects
    /// anything else to its "unsupported browser" page (protocol.md section 2, P5, P9, H1). Owner's decision,
    /// 2026-11-22: the client may use this form, and must keep naming itself in it.
    /// </summary>
    public static string UserAgent { get; } =
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 (compatible; Multiplex-GoogleFlights/{Version}; +{InfoUrl})";

    private static string Version =>
        typeof(GoogleFlightsClientOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0";
}
