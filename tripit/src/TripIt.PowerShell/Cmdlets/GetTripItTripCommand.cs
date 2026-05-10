using System.Management.Automation;
using TripIt.Models;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">Lists the user's TripIt trips.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TripItTrip")]
[OutputType(typeof(Trip))]
public sealed class GetTripItTripCommand : TripItCmdletBase
{
    [Parameter] public SwitchParameter Past { get; set; }
    [Parameter] public int? PageSize { get; set; }
    [Parameter] public int? PageNum { get; set; }
    [Parameter] public string? TripPermissionFilter { get; set; }
    [Parameter] public string? Traveler { get; set; }
    [Parameter] public string? ExcludeTypes { get; set; }
    [Parameter] public SwitchParameter SortByDate { get; set; }

    protected override void ProcessRecord()
    {
        var client = GetClient();
        var result = RunSync(ct => client.ListTripsAsync(
            Past.IsPresent, PageSize, PageNum, TripPermissionFilter, Traveler,
            ExcludeTypes, SortByDate.IsPresent ? true : null, ct));
        foreach (var trip in result.Items)
        {
            WriteObject(trip);
        }
    }
}
