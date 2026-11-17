using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Multiplex.Web.Sites;
using TripIt;

namespace Multiplex.Web.Services.TripIt;

/// <summary>The account's TripIt session is gone for good (the person signed out); the connector must reconnect.</summary>
public sealed class TripItSessionEndedException : Exception
{
    public TripItSessionEndedException()
        : base("The TripIt session ended (signed out).")
    {
    }
}

/// <summary>Sign-in storage is not registered on this server.</summary>
public sealed class TripItSignInUnavailableException(string message) : Exception(message);

/// <summary>
/// Per-account session state shared by every request: reads the account's snapshot from
/// <see cref="ISiteSessionStore"/>, and renews it through <see cref="ISiteSessionRefresher"/>
/// when TripIt rejects it, one refresh per account at a time, with a cool-down.
/// </summary>
/// <remarks>
/// The store and refresher are resolved from the calling request's services on each call (the
/// sign-in part may register them as scoped); only locks and cool-downs live here.
/// </remarks>
public sealed class TripItAccountSessions(
    TimeProvider time,
    IOptions<TripItServiceOptions> options,
    ILogger<TripItAccountSessions> log)
{
    public const string Site = "tripit";

    private readonly ConcurrentDictionary<string, AccountState> _accounts = new(StringComparer.Ordinal);

    /// <summary>The account's stored snapshot, or <see cref="TripItSession.Empty"/> when there is none or it is unreadable.</summary>
    public async Task<TripItSession> LoadAsync(IServiceProvider services, string accountId, CancellationToken ct)
    {
        var json = await Store(services).GetAsync(Site, accountId, ct).ConfigureAwait(false);
        return Parse(accountId, json, "stored");
    }

    /// <summary>
    /// Called after TripIt rejected <paramref name="rejected"/> for <paramref name="accountId"/>.
    /// Returns a snapshot with a different cookie to retry with, or null when none is available
    /// right now. Throws <see cref="TripItSessionEndedException"/> when the person is signed out.
    /// </summary>
    public async Task<TripItSession?> RenewAsync(IServiceProvider services, string accountId, TripItSession rejected,
        CancellationToken ct)
    {
        var state = _accounts.GetOrAdd(accountId, static _ => new AccountState());
        await state.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another request may have refreshed while this one waited (or the sign-in part
            // stored a new snapshot): use it without refreshing again.
            var stored = await LoadAsync(services, accountId, ct).ConfigureAwait(false);
            if (stored.IsConfigured && stored.Cookie != rejected.Cookie) return stored;

            var refresher = services.GetService<ISiteSessionRefresher>();
            if (refresher is null)
            {
                log.LogWarning("TripIt rejected the session of account {Account}, and no session refresher is registered.", accountId);
                return null;
            }

            var now = time.GetUtcNow();
            var cooldown = TimeSpan.FromSeconds(options.Value.RefreshCooldownSeconds);
            if (state.LastRefresh is { } last && now - last < cooldown)
            {
                log.LogWarning("TripIt rejected the session of account {Account} again within {Cooldown}; not refreshing yet.",
                    accountId, cooldown);
                return null;
            }
            state.LastRefresh = now;

            log.LogInformation("Refreshing the TripIt session of account {Account}.", accountId);
            string? json;
            try
            {
                json = await refresher.RefreshAsync(Site, accountId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("Refreshing the TripIt session of account {Account} failed ({Error}).", accountId, ex.GetType().Name);
                log.LogDebug(ex, "TripIt session refresh failure detail.");
                return null;
            }
            if (json is null)
            {
                // The refresher has already revoked the account's grants.
                log.LogWarning("TripIt account {Account} is signed out; the connector must reconnect.", accountId);
                throw new TripItSessionEndedException();
            }

            var renewed = Parse(accountId, json, "refreshed");
            if (!renewed.IsConfigured) return null;
            // Keep the store current, so the next request starts from the refreshed snapshot.
            var store = Store(services);
            var current = await store.GetAsync(Site, accountId, ct).ConfigureAwait(false);
            if (!string.Equals(current, json, StringComparison.Ordinal))
            {
                await store.SetAsync(Site, accountId, json, ct).ConfigureAwait(false);
            }
            return renewed.Cookie != rejected.Cookie ? renewed : null;
        }
        finally
        {
            state.Lock.Release();
        }
    }

    private TripItSession Parse(string accountId, string? json, string what)
    {
        if (json is null) return TripItSession.Empty;
        if (TripItSession.TryFromSnapshotJson(json, $"{Site}:{accountId}", out var session, out var error)) return session;
        log.LogWarning("The {What} TripIt session of account {Account} is unusable: {Reason}", what, accountId, error);
        return TripItSession.Empty;
    }

    private static ISiteSessionStore Store(IServiceProvider services)
        => services.GetService<ISiteSessionStore>()
           ?? throw new TripItSignInUnavailableException("TripIt sign-in is not set up on this server (no session store).");

    private sealed class AccountState
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public DateTimeOffset? LastRefresh { get; set; }
    }
}
