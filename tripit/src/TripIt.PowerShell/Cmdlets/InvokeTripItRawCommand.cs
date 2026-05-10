using System.Collections;
using System.Management.Automation;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">
/// Performs an authenticated GET against an arbitrary path on www.tripit.com and
/// returns the raw JSON body as a string.
/// </para>
/// <para type="description">
/// Escape hatch for endpoints that do not yet have a typed cmdlet. Pipe the result through
/// <c>ConvertFrom-Json</c> if you want PSCustomObject access:
///
///   Invoke-TripItRaw -Path '/api/v2/list/trip' | ConvertFrom-Json
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "TripItRaw")]
[OutputType(typeof(string))]
public sealed class InvokeTripItRawCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Path { get; set; } = "";

    [Parameter]
    public Hashtable? Query { get; set; }

    protected override void ProcessRecord()
    {
        if (!Path.StartsWith('/'))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException("Path must start with '/'."),
                "InvalidPath",
                ErrorCategory.InvalidArgument,
                Path));
            return;
        }

        List<KeyValuePair<string, string?>>? q = null;
        if (Query is not null)
        {
            q = new List<KeyValuePair<string, string?>>();
            foreach (DictionaryEntry entry in Query)
            {
                q.Add(new KeyValuePair<string, string?>(
                    entry.Key.ToString() ?? "",
                    entry.Value?.ToString()));
            }
        }

        var client = GetClient();
        var json = RunSync(ct => client.SendRawAsync(Path, q, ct));
        WriteObject(json);
    }
}
