using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Multiplex.Web.Sites;

namespace Multiplex.Web.SignIn;

/// <summary>
/// The live sign-in attempts in flight: at most one per pending authorization (one live
/// browser per gate session) and at most <see cref="SignInOptions.MaxLiveBrowsers"/> overall.
/// Ended attempts are kept for a while so a returning phone page still learns the outcome.
/// </summary>
public sealed class LiveSignInRegistry(
    SiteBrowserManager manager,
    SiteSessionStore store,
    SiteAccountIndex index,
    IOptions<SignInOptions> options,
    IServiceProvider services,
    TimeProvider time,
    ILogger<LiveSignInRegistry> logger) : IAsyncDisposable
{
    private static readonly TimeSpan KeepEnded = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, LiveSignInAttempt> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _byPending = new(StringComparer.Ordinal);
    private readonly Lock _startLock = new();

    /// <summary>
    /// Starts a live sign-in for <paramref name="pendingId"/>, or returns the one already running
    /// for it. Returns null when every live browser slot is taken.
    /// </summary>
    public LiveSignInAttempt? StartOrResume(ISiteSignInDefinition definition, string pendingId)
    {
        Sweep();
        lock (_startLock)
        {
            if (FindForPending(pendingId) is { CanBeReplaced: false } existing && existing.Slug == definition.Slug)
                return existing;

            var slot = manager.TryAcquireLiveSlot();
            if (slot is null)
            {
                logger.LogWarning("All live sign-in browsers are busy; refusing a new {Site} sign-in.", definition.Slug);
                return null;
            }

            LiveSignInAttempt attempt;
            try
            {
                attempt = new LiveSignInAttempt(definition, pendingId, new LiveSignInServices(
                    manager, store, index, options.Value,
                    services.GetService<IAuthorizationCompleter>, time, logger));
                attempt.Start(slot);
            }
            catch
            {
                // Never set up, so nothing else will give the slot back.
                slot.Dispose();
                throw;
            }
            _attempts[attempt.Id] = attempt;
            _byPending[pendingId] = attempt.Id;
            return attempt;
        }
    }

    public LiveSignInAttempt? Find(string? attemptId) =>
        attemptId is not null && _attempts.TryGetValue(attemptId, out var attempt) ? attempt : null;

    public LiveSignInAttempt? FindForPending(string pendingId) =>
        _byPending.TryGetValue(pendingId, out var id) ? Find(id) : null;

    private void Sweep()
    {
        var cutoff = time.GetUtcNow() - KeepEnded;
        foreach (var (id, attempt) in _attempts)
        {
            if (attempt.EndedAt is { } ended && ended < cutoff && attempt.Completion.IsCompleted)
            {
                _attempts.TryRemove(id, out _);
                _byPending.TryRemove(new KeyValuePair<string, string>(attempt.PendingId, id));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var attempt in _attempts.Values)
        {
            try { await attempt.DisposeAsync(); }
            catch (Exception ex) { logger.LogWarning("Stopping a live sign-in failed: {Error}", ex.GetType().Name); }
        }
        _attempts.Clear();
    }
}
