namespace Multiplex.Web.SignIn;

/// <summary>
/// There is no list of site accounts any more: each connection is bound to the account signed in
/// while it was added, and the connect passcode is the gate. A
/// <c>Multiplex:Sites:{slug}:AllowedAccounts</c> left by an earlier install is ignored; when the
/// server starts it says so in one log line, rather than failing or quietly doing nothing.
/// </summary>
public sealed class RetiredAccountListNotice(IConfiguration config, ILogger<RetiredAccountListNotice> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = config.GetSection("Multiplex:Sites").GetChildren()
            .Select(site => site.GetSection("AllowedAccounts"))
            .Where(list => list.Exists())
            .Select(list => list.Path)
            .ToList();
        if (settings.Count > 0)
        {
            logger.LogWarning(
                "{Settings} is no longer used and is ignored: each connection uses the account signed in while it was added, " +
                "behind the connect passcode. It can be removed from the configuration.",
                string.Join(", ", settings));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
