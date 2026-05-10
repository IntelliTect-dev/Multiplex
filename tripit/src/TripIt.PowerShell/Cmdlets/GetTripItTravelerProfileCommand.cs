using System.Management.Automation;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">Gets the user's TripIt traveler profile (passport, KTN, etc.).</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TripItTravelerProfile")]
[OutputType(typeof(TripIt.Models.TravelerProfile))]
public sealed class GetTripItTravelerProfileCommand : TripItCmdletBase
{
    [Parameter] public SwitchParameter ExcludeTemplates { get; set; }

    protected override void ProcessRecord()
    {
        var client = GetClient();
        var profile = RunSync(ct => client.GetTravelerProfileAsync(
            ExcludeTemplates.IsPresent ? true : null, ct));
        WriteObject(profile);
    }
}
