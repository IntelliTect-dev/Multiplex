using System.Management.Automation;
using System.Text.Json;
using TripIt.Json;
using TripIt.Models;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">Gets the chronological itinerary of a trip, or of every upcoming trip.</para>
/// <para type="description">Emits one ItineraryItem per flight leg, hotel stay, car rental, note
/// and so on, with local times, time zones and confirmation numbers. Each item also carries
/// TripName and TripUuid. Pipe Get-TripItTrip output in to scope it to those trips.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TripItItinerary")]
[OutputType(typeof(ItineraryItem))]
public sealed class GetTripItItineraryCommand : TripItCmdletBase
{
    [Parameter(Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("Uuid")]
    public string? TripUuid { get; set; }

    [Parameter] public SwitchParameter IncludeWeather { get; set; }

    protected override void ProcessRecord()
    {
        var client = GetClient();
        var details = TripUuid is null
            ? RunSync(ct => client.Trips.ListWithObjectsAsync(TripTimeframe.Upcoming, 100, 1, ct))
            : [RunSync(ct => client.Trips.GetAsync(TripUuid, includeObjects: true, ct))];

        foreach (var detail in details.OrderBy(d => d.Trip.StartDate, StringComparer.Ordinal))
        {
            foreach (var item in ItineraryBuilder.Build(detail.Objects, IncludeWeather.IsPresent))
            {
                var pso = PSObject.AsPSObject(item);
                pso.Properties.Add(new PSNoteProperty("TripName", detail.Trip.DisplayName));
                pso.Properties.Add(new PSNoteProperty("TripUuid", detail.Trip.Uuid));
                WriteObject(pso);
            }
        }
    }
}

/// <summary>
/// <para type="synopsis">Gets TripIt travel objects (flights, lodging, cars, notes, …).</para>
/// <para type="description">-Type and -Uuid get one object with every field TripIt holds.
/// -TripUuid lists a trip's objects; -Unfiled lists Unfiled Items. Objects are emitted as
/// PSCustomObjects (empty fields removed) with an added ObjectType property.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TripItObject", DefaultParameterSetName = ByUuidSet)]
[OutputType(typeof(PSObject))]
public sealed class GetTripItObjectCommand : TripItCmdletBase
{
    private const string ByUuidSet = "ByUuid";
    private const string ByTripSet = "ByTrip";
    private const string UnfiledSet = "Unfiled";

    [Parameter(ParameterSetName = ByUuidSet, Mandatory = true, Position = 0)]
    [Parameter(ParameterSetName = ByTripSet)]
    [Parameter(ParameterSetName = UnfiledSet)]
    [ArgumentCompleter(typeof(ObjectTypeCompleter))]
    public string? Type { get; set; }

    [Parameter(ParameterSetName = ByUuidSet, Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
    [Alias("ObjectUuid")]
    public string? Uuid { get; set; }

    [Parameter(ParameterSetName = ByTripSet, Mandatory = true, ValueFromPipelineByPropertyName = true)]
    public string? TripUuid { get; set; }

    [Parameter(ParameterSetName = UnfiledSet, Mandatory = true)]
    public SwitchParameter Unfiled { get; set; }

    [Parameter(ParameterSetName = ByTripSet)]
    [Parameter(ParameterSetName = UnfiledSet)]
    public int PageSize { get; set; } = 100;

    [Parameter(ParameterSetName = ByTripSet)]
    [Parameter(ParameterSetName = UnfiledSet)]
    public int PageNum { get; set; } = 1;

    protected override void ProcessRecord()
    {
        var client = GetClient();
        TripItObjectType? filter = Type is null ? null : TripItObjectTypes.Parse(Type);

        if (ParameterSetName == ByUuidSet)
        {
            var obj = RunSync(ct => client.Objects.GetAsync(filter!.Value, Uuid!, ct));
            WriteObject(ToPSObject(filter!.Value, obj));
            return;
        }

        var list = RunSync(ct => client.Objects.ListAsync(
            ParameterSetName == ByTripSet ? TripUuid : null, Unfiled.IsPresent, PageSize, PageNum, ct));
        foreach (var (type, objects) in list.Objects)
        {
            if (filter is not null && type != filter) continue;
            foreach (var obj in objects)
            {
                WriteObject(ToPSObject(type, obj));
            }
        }
    }

    private PSObject ToPSObject(TripItObjectType type, JsonElement obj)
    {
        var json = TripItJson.Prune(obj)?.ToJsonString() ?? "{}";
        var converted = InvokeCommand.InvokeScript("param($j) $j | ConvertFrom-Json -Depth 64", json)
            .FirstOrDefault() ?? new PSObject();
        converted.Properties.Add(new PSNoteProperty("ObjectType", type.ToPathSegment()));
        return converted;
    }
}

/// <summary>Tab completion for TripIt object types.</summary>
public sealed class ObjectTypeCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(string commandName, string parameterName,
        string wordToComplete, System.Management.Automation.Language.CommandAst commandAst,
        System.Collections.IDictionary fakeBoundParameters)
        => TripItObjectTypes.All
            .Select(t => t.ToPathSegment())
            .Where(p => p.StartsWith(wordToComplete ?? "", StringComparison.OrdinalIgnoreCase))
            .Select(p => new CompletionResult(p));
}

/// <summary>
/// <para type="synopsis">Searches trips by name, location, description or start date.</para>
/// </summary>
[Cmdlet(VerbsCommon.Search, "TripItTrip")]
[OutputType(typeof(Trip))]
public sealed class SearchTripItTripCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Text { get; set; } = "";

    [Parameter] public SwitchParameter UpcomingOnly { get; set; }
    [Parameter] public int MaxResults { get; set; } = 25;

    protected override void ProcessRecord()
    {
        var client = GetClient();
        var trips = RunSync(ct => client.Trips.SearchAsync(Text, !UpcomingOnly.IsPresent, MaxResults, ct));
        WriteObject(trips, enumerateCollection: true);
    }
}

/// <summary>
/// <para type="synopsis">Looks up airports, airlines, places, countries, states or addresses.</para>
/// </summary>
[Cmdlet(VerbsCommon.Find, "TripItReference")]
[OutputType(typeof(LookupResult))]
public sealed class FindTripItReferenceCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateSet("Airport", "Airline", "Place", "Country", "State", "Address")]
    public string Kind { get; set; } = "Airport";

    [Parameter(Mandatory = true, Position = 1)]
    [ValidateNotNullOrEmpty]
    public string Query { get; set; } = "";

    protected override void ProcessRecord()
    {
        var lookup = GetClient().Lookup;
        var results = RunSync(ct => Kind switch
        {
            "Airline" => lookup.AirlinesAsync(Query, ct),
            "Place" => lookup.PlacesAsync(Query, ct),
            "Country" => lookup.CountriesAsync(Query, ct),
            "State" => lookup.StatesAsync(Query, ct),
            "Address" => lookup.AddressesAsync(Query, ct),
            _ => lookup.AirportsAsync(Query, ct),
        });
        WriteObject(results, enumerateCollection: true);
    }
}
