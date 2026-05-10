using System.Management.Automation;
using TripIt.Models;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">Lists active TripIt Pro alerts.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TripItProAlert")]
[OutputType(typeof(ProAlert))]
public sealed class GetTripItProAlertCommand : TripItCmdletBase
{
    protected override void ProcessRecord()
    {
        var client = GetClient();
        var result = RunSync(client.ListProAlertsAsync);
        foreach (var alert in result.Items)
        {
            WriteObject(alert);
        }
    }
}
