using System.Diagnostics;
using System.Management.Automation;
using TripIt.Auth;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">
/// Pushes the currently-stored TripIt session cookie + CSRF token to a GitHub repository as
/// Actions secrets via the <c>gh</c> CLI.
/// </para>
/// <para type="description">
/// Requires the <c>gh</c> CLI to be installed and authenticated (<c>gh auth status</c>).
/// Sets <c>TRIPIT_COOKIE</c> and <c>TRIPIT_CSRF</c> as repository secrets.
/// </para>
/// </summary>
[Cmdlet(VerbsData.Sync, "TripItSecretToGitHub", SupportsShouldProcess = true)]
[OutputType(typeof(string))]
public sealed class SyncTripItSecretToGitHubCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Repository { get; set; } = "";

    protected override void ProcessRecord()
    {
        var store = ResolveStore() ?? new UserSecretsSessionStore();
        if (!store.TryLoad(out var creds))
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    "No TripIt session is stored. Run Connect-TripIt first."),
                "NoStoredSession",
                ErrorCategory.ObjectNotFound,
                targetObject: null));
            return;
        }

        EnsureGhAvailable();

        if (!ShouldProcess(Repository, "Set TRIPIT_COOKIE and TRIPIT_CSRF secrets"))
        {
            return;
        }

        SetSecret("TRIPIT_COOKIE", creds.Cookie);
        SetSecret("TRIPIT_CSRF", creds.CsrfToken);
        WriteObject($"Secrets pushed to {Repository}.");
    }

    private void EnsureGhAvailable()
    {
        var psi = new ProcessStartInfo("gh", "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException("gh --version returned non-zero.");
            }
        }
        catch (Exception ex)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    "GitHub CLI (gh) is required and was not found on PATH. " +
                    "Install from https://cli.github.com/. Underlying error: " + ex.Message),
                "GhMissing",
                ErrorCategory.NotInstalled,
                targetObject: null));
        }
    }

    private void SetSecret(string name, string value)
    {
        var psi = new ProcessStartInfo("gh",
            $"secret set {name} --repo {Repository} --body -")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(value);
        proc.StandardInput.Close();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            var stderr = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"gh secret set {name} failed (exit {proc.ExitCode}): {stderr}");
        }
        WriteVerbose($"Set {name} on {Repository}.");
    }
}
