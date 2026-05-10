using System.Management.Automation;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">Gets the TripIt SPA app config / feature flags.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TripItAppConfig")]
[OutputType(typeof(TripIt.Models.AppConfig))]
public sealed class GetTripItAppConfigCommand : TripItCmdletBase
{
    protected override void ProcessRecord()
    {
        var client = GetClient();
        var result = RunSync(client.GetAppConfigAsync);
        WriteObject(result);
    }
}
