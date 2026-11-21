namespace Multiplex.Web.SignIn;

/// <summary>
/// When the server starts, removes the site sessions, account lists and browser profiles saved before
/// they belonged to a person: everything in the old <c>sites</c> folder directly under the data folder
/// (#80, decision D5). They belong to no one, so nothing may use or adopt them; each person signs in to
/// the site again when they next connect. A profile a running Chrome still holds is left for the next
/// start. Never stops the server from starting.
/// </summary>
public sealed class UnownedSiteDataSweeper(SiteBrowserManager manager, ILogger<UnownedSiteDataSweeper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var unowned = manager.Paths.UnownedSitesDirectory;
        if (!Directory.Exists(unowned)) return;

        try
        {
            manager.Paths.EnsureInsideDataPath(unowned);
            foreach (var profile in Directory.GetDirectories(unowned).SelectMany(site =>
                         Directory.Exists(Path.Combine(site, "profiles")) ? Directory.GetDirectories(Path.Combine(site, "profiles")) : []))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SiteBrowserManager.IsProfileInUse(profile))
                {
                    logger.LogWarning("Left an unowned site profile alone for now: a browser is still using it.");
                    return;
                }
                await manager.DiscardProfileAsync(profile);
            }
            Directory.Delete(unowned, recursive: true);
            logger.LogWarning(
                "Removed the site sessions saved before people signed in to Multiplex: they belong to no one. " +
                "Each person signs in to the site again when they next connect it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning("Could not remove the unowned site sessions yet ({Error}); trying again at the next start.", ex.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
