using System.Management.Automation;
using TripIt.Auth;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">Signs in to TripIt in a real browser and stores the resulting session.</para>
/// <para type="description">
/// Launches the Playwright-based browser capture (<c>tools/extract-session.js</c>), where you
/// sign in to TripIt yourself; the cmdlet never takes your password. The resulting cookie and
/// CSRF token are saved via the chosen <c>-StoreLocation</c>.
/// </para>
/// </summary>
[Cmdlet(VerbsCommunications.Connect, "TripIt", SupportsShouldProcess = true)]
public sealed class ConnectTripItCommand : TripItCmdletBase
{
    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? ExtractScriptPath { get; set; }

    protected override void ProcessRecord()
    {
        if (!ShouldProcess("TripIt session", "Connect"))
        {
            return;
        }

        var store = ResolveStore() ?? new UserSecretsSessionStore();
        WriteVerbose("Launching browser-based sign-in.");
        var creds = RunSync(ct => new TripItAuthenticator().BrowserLoginAsync(ExtractScriptPath, ct));

        store.Save(creds);
        WriteObject(new
        {
            Stored = store.Description,
            HasCsrf = !string.IsNullOrEmpty(creds.CsrfToken),
        });
    }
}
