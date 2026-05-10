using System.Management.Automation;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">Gets the GTM/analytics data layer payload.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TripItGtmData")]
[OutputType(typeof(TripIt.Models.GtmData))]
public sealed class GetTripItGtmDataCommand : TripItCmdletBase
{
    protected override void ProcessRecord()
    {
        var client = GetClient();
        var result = RunSync(client.GetGtmDataAsync);
        WriteObject(result);
    }
}
