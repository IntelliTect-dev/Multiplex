using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Multiplex.Web.Sites;

namespace Multiplex.Web.Services.LinkedIn;

/// <summary>Configuration for the LinkedIn service (<c>Multiplex:Sites:LinkedIn</c>).</summary>
public sealed class LinkedInServiceOptions
{
    public const string Section = "Multiplex:Sites:LinkedIn";

    /// <summary>Minimum gap between two LinkedIn requests of one account, in milliseconds.</summary>
    public int MinRequestSpacingMs { get; set; } = 1000;

    /// <summary>Upper bound of the random amount added to the gap, in milliseconds.</summary>
    public int RequestJitterMs { get; set; } = 500;

    /// <summary>After a session refresh, how long before the same account may be refreshed again.</summary>
    public int RefreshCooldownSeconds { get; set; } = 60;

    /// <summary>LinkedIn's names for its messaging queries; the defaults are the recorded ones.</summary>
    public LinkedInQueryIds QueryIds { get; set; } = new();
}

/// <summary>
/// Per-account session state shared by every request: reads the account's snapshot from
/// <see cref="ISiteSessionStore"/>, and renews it through <see cref="ISiteSessionRefresher"/> when
/// LinkedIn rejects it, one refresh per account at a time, with a cool-down. The same rules as the
/// TripIt service's sessions.
/// </summary>
public sealed class LinkedInAccountSessions(
    TimeProvider time,
    IOptions<LinkedInServiceOptions> options,
    ILogger<LinkedInAccountSessions> log)
{
    public const string Site = LinkedInToolContext.Site;

    private readonly ConcurrentDictionary<string, AccountState> _accounts = new(StringComparer.Ordinal);

    /// <summary>The account's stored session, or <see cref="LinkedInSession.Empty"/> when there is none or it is unreadable.</summary>
    public async Task<LinkedInSession> LoadAsync(IServiceProvider services, string owner, string accountId, CancellationToken ct)
    {
        var json = await Store(services).GetAsync(owner, Site, accountId, ct).ConfigureAwait(false);
        return Parse(accountId, json, "stored");
    }

    /// <summary>
    /// Called after LinkedIn rejected <paramref name="rejected"/>. Returns a session with a different
    /// cookie to retry with, or null when none is available right now. Throws
    /// <see cref="LinkedInSessionEndedException"/> when the person is signed out.
    /// </summary>
    public async Task<LinkedInSession?> RenewAsync(IServiceProvider services, string owner, string accountId, LinkedInSession rejected, CancellationToken ct)
    {
        var state = _accounts.GetOrAdd(owner + "/" + accountId, static _ => new AccountState());
        await state.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stored = await LoadAsync(services, owner, accountId, ct).ConfigureAwait(false);
            if (stored.IsConfigured && stored.Cookie != rejected.Cookie) return stored;

            var refresher = services.GetService<ISiteSessionRefresher>();
            if (refresher is null)
            {
                log.LogWarning("LinkedIn rejected the session of account {Account}, and no session refresher is registered.", accountId);
                return null;
            }

            var now = time.GetUtcNow();
            var cooldown = TimeSpan.FromSeconds(options.Value.RefreshCooldownSeconds);
            if (state.LastRefresh is { } last && now - last < cooldown)
            {
                log.LogWarning("LinkedIn rejected the session of account {Account} again within {Cooldown}; not refreshing yet.", accountId, cooldown);
                return null;
            }
            state.LastRefresh = now;

            log.LogInformation("Refreshing the LinkedIn session of account {Account}.", accountId);
            string? json;
            try
            {
                json = await refresher.RefreshAsync(owner, Site, accountId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("Refreshing the LinkedIn session of account {Account} failed ({Error}).", accountId, ex.GetType().Name);
                log.LogDebug(ex, "LinkedIn session refresh failure detail.");
                return null;
            }
            if (json is null)
            {
                // The refresher has already revoked the account's grants.
                log.LogWarning("LinkedIn account {Account} is signed out; the connector must reconnect.", accountId);
                throw new LinkedInSessionEndedException();
            }

            var renewed = Parse(accountId, json, "refreshed");
            if (!renewed.IsConfigured) return null;
            var store = Store(services);
            if (!string.Equals(await store.GetAsync(owner, Site, accountId, ct).ConfigureAwait(false), json, StringComparison.Ordinal))
            {
                await store.SetAsync(owner, Site, accountId, json, ct).ConfigureAwait(false);
            }
            return renewed.Cookie != rejected.Cookie ? renewed : null;
        }
        finally
        {
            state.Lock.Release();
        }
    }

    private LinkedInSession Parse(string accountId, string? json, string what)
    {
        if (json is null) return LinkedInSession.Empty;
        if (LinkedInSession.TryFromSnapshotJson(json, out var session, out var error))
        {
            if (session.ProfileId == accountId) return session;
            log.LogWarning("The {What} LinkedIn session of account {Account} belongs to another member; not using it.", what, accountId);
            return LinkedInSession.Empty;
        }
        log.LogWarning("The {What} LinkedIn session of account {Account} is unusable: {Reason}", what, accountId, error);
        return LinkedInSession.Empty;
    }

    private static ISiteSessionStore Store(IServiceProvider services)
        => services.GetService<ISiteSessionStore>()
           ?? throw new LinkedInApiException("LinkedIn sign-in is not set up on this server (no session store).");

    private sealed class AccountState
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public DateTimeOffset? LastRefresh { get; set; }
    }
}
