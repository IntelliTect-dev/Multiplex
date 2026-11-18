namespace Multiplex.Web.SignIn;

/// <summary>
/// When the server starts, deletes the browser profiles a crash or reboot left half-way through
/// a sign-in: <c>temp-*</c> folders of live sign-ins that never finished, and <c>*.old-*</c>
/// folders of replaced profiles. Either can hold signed-in site cookies. Account profiles are
/// never touched (<see cref="SignInPaths.ProfileName"/> never produces such a name), and neither
/// is a folder a running Chrome still holds. Runs once, before the server takes requests; a
/// folder it cannot delete is logged and retried at the next start.
/// </summary>
public sealed class StaleProfileSweeper(
    IEnumerable<ISiteSignInDefinition> definitions,
    SiteBrowserManager manager,
    ILogger<StaleProfileSweeper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var definition in definitions)
        {
            try
            {
                await SweepAsync(definition.Slug, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never stop the server from starting over a leftover folder.
                logger.LogWarning("Could not check {Site} for leftover sign-in profiles: {Error}", definition.Slug, ex.GetType().Name);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SweepAsync(string site, CancellationToken ct)
    {
        var profiles = manager.Paths.ProfilesDirectory(site);
        if (!Directory.Exists(profiles)) return;

        foreach (var folder in Directory.GetDirectories(profiles))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(folder);
            if (!SignInPaths.IsTransientProfileName(name)) continue;
            if (SiteBrowserManager.IsProfileInUse(folder))
            {
                logger.LogInformation("Left {Site} sign-in profile {Profile} alone: a browser is still using it.", site, name);
                continue;
            }
            try
            {
                await manager.DeleteProfileAsync(folder);
                logger.LogInformation("Deleted leftover {Site} sign-in profile {Profile}.", site, name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning("Could not delete leftover {Site} sign-in profile {Profile}: {Error}", site, name, ex.GetType().Name);
            }
        }
    }
}
