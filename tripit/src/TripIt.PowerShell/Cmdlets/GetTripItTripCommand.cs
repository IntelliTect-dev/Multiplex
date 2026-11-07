using System.Management.Automation;
using TripIt.Models;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">Lists TripIt trips, or gets one trip with all of its objects.</para>
/// <para type="description">Without -Uuid, lists upcoming trips (or past trips with -Past).
/// -All pages through every trip. With -Uuid, returns a TripDetail whose Itinerary property
/// lists flights, lodging, cars and other plans in chronological order.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TripItTrip", DefaultParameterSetName = ListSet)]
[OutputType(typeof(Trip), ParameterSetName = [ListSet])]
[OutputType(typeof(TripDetail), ParameterSetName = [UuidSet])]
public sealed class GetTripItTripCommand : TripItCmdletBase
{
    private const string ListSet = "List";
    private const string UuidSet = "Uuid";

    [Parameter(ParameterSetName = UuidSet, Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrEmpty]
    public string? Uuid { get; set; }

    [Parameter(ParameterSetName = ListSet)] public SwitchParameter Past { get; set; }
    [Parameter(ParameterSetName = ListSet)] public SwitchParameter All { get; set; }
    [Parameter(ParameterSetName = ListSet)] public int? PageSize { get; set; }
    [Parameter(ParameterSetName = ListSet)] public int? PageNum { get; set; }
    [Parameter(ParameterSetName = ListSet)] public string? TripPermissionFilter { get; set; }
    [Parameter(ParameterSetName = ListSet)] public string? Traveler { get; set; }
    [Parameter(ParameterSetName = ListSet)] public string? ExcludeTypes { get; set; }
    [Parameter(ParameterSetName = ListSet)] public SwitchParameter SortByDate { get; set; }

    protected override void ProcessRecord()
    {
        var client = GetClient();
        if (ParameterSetName == UuidSet)
        {
            WriteObject(RunSync(ct => client.Trips.GetAsync(Uuid!, includeObjects: true, ct)));
            return;
        }

        if (All.IsPresent)
        {
            var timeframe = Past.IsPresent ? TripTimeframe.Past : TripTimeframe.Upcoming;
            var trips = RunSync(async ct =>
            {
                var list = new List<Trip>();
                await foreach (var t in client.Trips.ListAllAsync(timeframe, PageSize ?? 500, ct).ConfigureAwait(false))
                {
                    list.Add(t);
                }
                return list;
            });
            WriteObject(trips, enumerateCollection: true);
            return;
        }

        var result = RunSync(ct => client.ListTripsAsync(
            Past.IsPresent, PageSize, PageNum, TripPermissionFilter, Traveler,
            ExcludeTypes, SortByDate.IsPresent ? true : null, ct));
        foreach (var trip in result.ItemsOf("Trip"))
        {
            WriteObject(trip);
        }
    }
}
