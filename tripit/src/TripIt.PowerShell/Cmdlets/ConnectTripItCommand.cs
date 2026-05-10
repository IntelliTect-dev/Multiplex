using System.Management.Automation;
using System.Net;
using TripIt.Auth;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">Authenticates with TripIt and stores the resulting session.</para>
/// <para type="description">
/// Attempts a CLI-based credential login first. On Akamai bot challenge (or any failure),
/// falls back to launching the existing Playwright-based browser capture
/// (<c>tools/extract-session.js</c>) unless <c>-NoBrowserFallback</c> is specified.
/// The resulting cookie + CSRF token are saved via the chosen <c>-StoreLocation</c>.
/// </para>
/// </summary>
[Cmdlet(VerbsCommunications.Connect, "TripIt", DefaultParameterSetName = "Interactive",
        SupportsShouldProcess = true)]
public sealed class ConnectTripItCommand : TripItCmdletBase
{
    [Parameter(ParameterSetName = "Credentials", Mandatory = true)]
    public string? UserName { get; set; }

    [Parameter(ParameterSetName = "Credentials", Mandatory = true)]
    public System.Security.SecureString? Password { get; set; }

    [Parameter]
    public SwitchParameter NoBrowserFallback { get; set; }

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
        var auth = new TripItAuthenticator();

        SessionCredentials? creds = null;
        if (ParameterSetName == "Credentials" && UserName is not null && Password is not null)
        {
            try
            {
                var pwd = new NetworkCredential(string.Empty, Password).Password;
                creds = RunSync(ct => auth.TryCliLoginAsync(UserName, pwd, ct));
                WriteVerbose("CLI login succeeded.");
            }
            catch (LoginChallengeException ex)
            {
                WriteVerbose($"CLI login challenged: {ex.Message}");
            }
            catch (Exception ex)
            {
                WriteVerbose($"CLI login failed: {ex.Message}");
            }
        }

        if (creds is null)
        {
            if (NoBrowserFallback)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "CLI login failed and -NoBrowserFallback was specified."),
                    "CliLoginFailed",
                    ErrorCategory.AuthenticationError,
                    targetObject: null));
                return;
            }
            WriteVerbose("Launching browser-based capture.");
            creds = RunSync(ct => auth.BrowserLoginAsync(ExtractScriptPath, ct));
        }

        store.Save(creds);
        WriteObject(new
        {
            Stored = store.Description,
            HasCsrf = !string.IsNullOrEmpty(creds.CsrfToken),
        });
    }
}
