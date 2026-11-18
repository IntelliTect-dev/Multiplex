using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Multiplex.Web.Sites;
using PuppeteerSharp;

namespace Multiplex.Web.SignIn;

/// <summary>A refresh that could not tell whether the person is signed in (blocked, timed out, no profile). Nothing was changed.</summary>
public sealed class SiteRefreshException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// <see cref="ISiteSessionRefresher"/>: relaunches the account's persistent profile, loads the
/// site's refresh page and re-reads the session. Three outcomes, never more:
/// signed in → new snapshot stored and returned; on the site's login page → session removed,
/// grants revoked, browser profile deleted, <c>null</c>; anything else (block page, timeout, launch failure) →
/// <see cref="SiteRefreshException"/> with nothing touched. One refresh per account at a time,
/// and a repeat within <see cref="SignInOptions.RefreshCooldown"/> returns the last result.
/// </summary>
public sealed class SiteSessionRefresher(
    IEnumerable<ISiteSignInDefinition> definitions,
    SiteBrowserManager manager,
    SiteSessionStore store,
    SiteAccountIndex index,
    IOptions<SignInOptions> options,
    IServiceProvider services,
    TimeProvider time,
    ILogger<SiteSessionRefresher> logger) : ISiteSessionRefresher
{
    private readonly ConcurrentDictionary<string, LastRefresh> _last = new(StringComparer.Ordinal);

    public async Task<string?> RefreshAsync(string site, string accountId, CancellationToken ct)
    {
        var definition = definitions.FirstOrDefault(d => string.Equals(d.Slug, site, StringComparison.OrdinalIgnoreCase))
            ?? throw new SiteRefreshException($"No sign-in definition for site '{site}'.");
        var key = definition.Slug + "\n" + accountId;

        using (await manager.LockAccountAsync(definition.Slug, accountId, ct))
        {
            if (_last.TryGetValue(key, out var last) && time.GetUtcNow() - last.At < options.Value.RefreshCooldown)
            {
                if (last.Failure is not null)
                    throw new SiteRefreshException("The last refresh for this account failed moments ago; try again shortly.", last.Failure);
                return await store.GetAsync(definition.Slug, accountId, ct);
            }

            try
            {
                var snapshot = await RefreshLockedAsync(definition, accountId, ct);
                _last[key] = new LastRefresh(time.GetUtcNow(), null);
                return snapshot;
            }
            catch (SiteRefreshException ex)
            {
                _last[key] = new LastRefresh(time.GetUtcNow(), ex);
                throw;
            }
        }
    }

    private async Task<string?> RefreshLockedAsync(ISiteSignInDefinition definition, string accountId, CancellationToken ct)
    {
        var site = definition.Slug;
        var profile = manager.Paths.AccountProfile(site, accountId);
        if (!Directory.Exists(profile))
            throw new SiteRefreshException($"No browser profile is kept for this {definition.DisplayName} account.");

        IBrowser? browser = null;
        var signedOut = false;
        try
        {
            try
            {
                browser = await manager.LaunchAsync(site, profile, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new SiteRefreshException("The site browser could not be started.", ex);
            }

            var page = (await browser.PagesAsync()).FirstOrDefault() ?? await browser.NewPageAsync();
            var outcome = await SignInProbe.WaitForOutcomeAsync(definition, page, options.Value.RefreshTimeout, ct);

            if (outcome.Account is { } account)
            {
                if (account.AccountId != accountId)
                {
                    logger.LogWarning("The {Site} profile for account {AccountId} is now signed in as {Other}; treating {AccountId} as signed out.",
                        site, accountId, account.AccountId, accountId);
                    await SignedOutAsync(site, accountId, ct);
                    signedOut = true;
                    return null;
                }

                var snapshot = await definition.CaptureSnapshotAsync(page, ct);
                await store.SetAsync(site, accountId, snapshot, ct);
                await index.UpsertAsync(account, ct);
                logger.LogInformation("Refreshed the {Site} session for account {AccountId}.", site, accountId);
                return snapshot;
            }

            if (outcome.SignedOut)
            {
                await SignedOutAsync(site, accountId, ct);
                signedOut = true;
                return null;
            }

            throw new SiteRefreshException(
                $"{definition.DisplayName} showed neither a signed-in page nor its sign-in page (it may be blocking the browser).");
        }
        finally
        {
            await manager.CloseAsync(browser);
            // Only once Chrome has exited can its profile go; it is of no use to a signed-out account.
            if (signedOut) await DeleteProfileAsync(site, accountId, profile);
        }
    }

    /// <summary>Best effort: the sign-out itself (session removed, grants revoked) already stands.</summary>
    private async Task DeleteProfileAsync(string site, string accountId, string profile)
    {
        try
        {
            if (!await manager.DiscardProfileAsync(profile))
                logger.LogWarning("Could not delete the browser profile of signed-out {Site} account {AccountId} yet.", site, accountId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning("Could not delete the browser profile of signed-out {Site} account {AccountId}: {Error}",
                site, accountId, ex.GetType().Name);
        }
    }

    private async Task SignedOutAsync(string site, string accountId, CancellationToken ct)
    {
        logger.LogWarning("The {Site} account {AccountId} is signed out; removing its session and revoking its grants.", site, accountId);
        await store.RemoveAsync(site, accountId, ct);
        await index.RemoveAsync(site, accountId, ct);
        if (services.GetService<IGrantRevoker>() is { } revoker)
            await revoker.RevokeAccountAsync(site, accountId, ct);
        else
            logger.LogWarning("No IGrantRevoker is registered; grants for {Site} account {AccountId} were not revoked.", site, accountId);
    }

    private sealed record LastRefresh(DateTimeOffset At, Exception? Failure);
}

/// <summary>Loads a site's refresh page in a profile and waits until it shows signed in or signed out.</summary>
internal static class SignInProbe
{
    public sealed record Outcome(SiteAccount? Account, bool SignedOut);

    public static async Task<Outcome> WaitForOutcomeAsync(ISiteSignInDefinition definition, IPage page, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await page.GoToAsync(definition.RefreshUrl.AbsoluteUri,
                new NavigationOptions { Timeout = (int)timeout.TotalMilliseconds, WaitUntil = [WaitUntilNavigation.DOMContentLoaded] });
        }
        catch (Exception ex) when (ex is NavigationException or TimeoutException)
        {
            // Judge by what the page shows below.
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await definition.TryGetSignedInAccountAsync(page, ct) is { } account) return new Outcome(account, false);
                if (await definition.IsSignedOutAsync(page)) return new Outcome(null, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A navigation (e.g. the site's own redirect to its login page) replaced the page context.
            }
            await Task.Delay(500, ct);
        }
        return new Outcome(null, false);
    }
}
