using System.Management.Automation;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">Gets the signed-in user's TripIt profile.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TripItProfile")]
[OutputType(typeof(TripIt.Models.Profile))]
public sealed class GetTripItProfileCommand : TripItCmdletBase
{
    protected override void ProcessRecord()
    {
        var client = GetClient();
        var profile = RunSync(client.GetProfileAsync);
        WriteObject(profile);
    }
}
