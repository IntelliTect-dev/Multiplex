using System.Runtime.CompilerServices;
using System.Text.Json;
using TripIt.Json;
using TripIt.Models;

namespace TripIt;

/// <summary>Which trips <see cref="TripsClient.ListAsync"/> returns.</summary>
public enum TripTimeframe
{
    /// <summary>Trips that have not ended yet (TripIt's default).</summary>
    Upcoming,
    /// <summary>Trips whose end date is before today.</summary>
    Past,
}

/// <summary>
/// Whose trips to list. TripIt's own default (when the parameter is omitted) is
/// <see cref="Mine"/>, which silently hides every trip the user is on but not travelling
/// on (measured: 75 of 392 past trips). This client defaults to <see cref="All"/>.
/// </summary>
public enum TripTravelerFilter
{
    /// <summary>Every trip the user can see (<c>traveler=all</c>).</summary>
    All,
    /// <summary>Trips the user is travelling on (<c>traveler=true</c>).</summary>
    Mine,
    /// <summary>Trips the user plans, follows or edits but is not travelling on (<c>traveler=false</c>).</summary>
    Others,
}

/// <summary>
/// Trips sub-client: <c>v2/list/trip</c>, <c>v2/get/trip/uuid/{uuid}[/include_objects/true]</c>.
/// </summary>
public sealed partial class TripsClient
{
    private readonly TripItClient _client;

    internal TripsClient(TripItClient client) => _client = client;

    /// <summary>
    /// Lists one page of trips. TripIt accepts any <paramref name="pageSize"/> (measured: 500
    /// returned all 317 past trips in one page); its default is 5 for past trips.
    /// </summary>
    public async Task<TripList> ListAsync(
        TripTimeframe timeframe = TripTimeframe.Upcoming,
        int? pageSize = null,
        int? pageNum = null,
        CancellationToken ct = default,
        TripTravelerFilter traveler = TripTravelerFilter.All)
    {
        var q = BuildListQuery(timeframe == TripTimeframe.Past, pageSize, pageNum, null, TravelerParam(traveler), null, null);
        using var doc = await _client.GetJsonAsync("/api/v2/list/trip", q, ct).ConfigureAwait(false);
        return ParseTripList(doc.RootElement);
    }

    /// <summary>Enumerates every trip in the timeframe, paging as needed.</summary>
    public async IAsyncEnumerable<Trip> ListAllAsync(
        TripTimeframe timeframe = TripTimeframe.Upcoming,
        int pageSize = 100,
        [EnumeratorCancellation] CancellationToken ct = default,
        TripTravelerFilter traveler = TripTravelerFilter.All)
    {
        for (var page = 1; ; page++)
        {
            var list = await ListAsync(timeframe, pageSize, page, ct, traveler).ConfigureAwait(false);
            foreach (var t in list.Trips) yield return t;
            if (list.Trips.Count == 0 || list.MaxPage is null || page >= list.MaxPage) yield break;
        }
    }

    /// <summary>
    /// Gets one trip by UUID. With <paramref name="includeObjects"/> (the default) the
    /// response carries every flight, hotel, car, … in the trip — one round trip.
    /// TripIt only accepts UUIDs here; <c>get/trip/id/{id}</c> answers 400.
    /// </summary>
    public async Task<TripDetail> GetAsync(string uuid, bool includeObjects = true, CancellationToken ct = default)
    {
        ValidateUuid(uuid);
        var path = $"/api/v2/get/trip/uuid/{Uri.EscapeDataString(uuid)}" + (includeObjects ? "/include_objects/true" : "");
        using var doc = await _client.GetJsonAsync(path, null, ct).ConfigureAwait(false);
        return ParseTripDetail(doc.RootElement.Clone());
    }

    /// <summary>
    /// Lists upcoming trips together with all their objects in one call
    /// (<c>list/trip?include_objects=true</c>). Objects are grouped per trip.
    /// </summary>
    public async Task<IReadOnlyList<TripDetail>> ListWithObjectsAsync(
        TripTimeframe timeframe = TripTimeframe.Upcoming,
        int? pageSize = null,
        int? pageNum = null,
        CancellationToken ct = default,
        TripTravelerFilter traveler = TripTravelerFilter.All)
    {
        var q = BuildListQuery(timeframe == TripTimeframe.Past, pageSize, pageNum, null, TravelerParam(traveler), null, null);
        q.Add(new("include_objects", "true"));
        using var doc = await _client.GetJsonAsync("/api/v2/list/trip", q, ct).ConfigureAwait(false);
        var root = doc.RootElement.Clone();
        var trips = TripItJson.AsList(root, "Trip");
        var all = ItineraryBuilder.ExtractObjects(root);
        var profiles = TripItJson.AsList(root, "Profile");
        var result = new List<TripDetail>(trips.Count);
        foreach (var t in trips)
        {
            var trip = t.Deserialize<Trip>(TripItJson.Options)!;
            var mine = all.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<JsonElement>)[.. kv.Value.Where(o => BelongsTo(o, trip))]);
            result.Add(new TripDetail
            {
                Trip = trip,
                Objects = mine.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
                Profiles = profiles,
            });
        }
        return result;
    }

    /// <summary>
    /// Finds trips whose name, location or description contains <paramref name="text"/>
    /// (case-insensitive), searching upcoming then past trips. Client-side: TripIt has no
    /// trip search endpoint.
    /// </summary>
    public async Task<IReadOnlyList<Trip>> SearchAsync(
        string text,
        bool includePast = true,
        int maxResults = 25,
        CancellationToken ct = default,
        TripTravelerFilter traveler = TripTravelerFilter.All)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var matches = new List<Trip>();
        var timeframes = includePast ? new[] { TripTimeframe.Upcoming, TripTimeframe.Past } : [TripTimeframe.Upcoming];
        foreach (var tf in timeframes)
        {
            await foreach (var trip in ListAllAsync(tf, 500, ct, traveler).ConfigureAwait(false))
            {
                if (Matches(trip, text))
                {
                    matches.Add(trip);
                    if (matches.Count >= maxResults) return matches;
                }
            }
        }
        return matches;
    }

    /// <summary>The <c>traveler</c> query value for a filter: all / true / false.</summary>
    public static string TravelerParam(TripTravelerFilter traveler) => traveler switch
    {
        TripTravelerFilter.Mine => "true",
        TripTravelerFilter.Others => "false",
        _ => "all",
    };

    /// <summary>Parses "all" / "mine" / "others" (and true/false/yes/no), for tool and cmdlet input.</summary>
    public static TripTravelerFilter ParseTravelerFilter(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" or "any" => TripTravelerFilter.All,
        "mine" or "me" or "true" or "yes" or "traveling" or "travelling" => TripTravelerFilter.Mine,
        "others" or "other" or "false" or "no" or "not_traveling" or "not-traveling" => TripTravelerFilter.Others,
        _ => throw new ArgumentException("traveler must be 'all', 'mine' (trips I travel on) or 'others' (trips I'm on but not travelling)."),
    };

    internal static bool Matches(Trip trip, string text)
    {
        static bool C(string? s, string t) => s?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false;
        return C(trip.DisplayName, text) || C(trip.PrimaryLocation, text) || C(trip.Description, text)
               || C(trip.StartDate, text) || C(trip.Uuid, text);
    }

    private static bool BelongsTo(JsonElement obj, Trip trip)
    {
        var tripUuid = TripItJson.Str(obj, "trip_uuid");
        if (tripUuid is not null && trip.Uuid is not null) return string.Equals(tripUuid, trip.Uuid, StringComparison.OrdinalIgnoreCase);
        var tripId = TripItJson.Str(obj, "trip_id");
        return tripId is not null && tripId == trip.Id;
    }

    /// <summary>Parses a <c>list/trip</c> envelope. Public for unit testing.</summary>
    public static TripList ParseTripList(JsonElement root) => new()
    {
        Trips = [.. TripItJson.AsList(root, "Trip").Select(t => t.Deserialize<Trip>(TripItJson.Options)!)],
        PageNum = TripItJson.Int(root, "page_num"),
        PageSize = TripItJson.Int(root, "page_size"),
        MaxPage = TripItJson.Int(root, "max_page"),
        TotalItems = TripItJson.Int(root, "total_items"),
    };

    /// <summary>Parses a <c>get/trip</c> envelope. Public for unit testing.</summary>
    public static TripDetail ParseTripDetail(JsonElement root)
    {
        var tripEl = TripItJson.AsList(root, "Trip").FirstOrDefault();
        if (tripEl.ValueKind != JsonValueKind.Object)
        {
            throw new TripItApiException(200, "get/trip", "Response contained no Trip.");
        }
        return new TripDetail
        {
            Trip = tripEl.Deserialize<Trip>(TripItJson.Options)!,
            Objects = ItineraryBuilder.ExtractObjects(root),
            Profiles = TripItJson.AsList(root, "Profile"),
        };
    }

    internal static List<KeyValuePair<string, string?>> BuildListQuery(
        bool past, int? pageSize, int? pageNum, string? tripPermissionFilter, string? traveler,
        string? excludeTypes, bool? shouldSortTripsByDate) =>
    [
        new("past",                      past ? "true" : "false"),
        new("page_size",                 pageSize?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("page_num",                  pageNum?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("trip_permission_filter",    tripPermissionFilter),
        new("traveler",                  traveler),
        new("exclude_types",             excludeTypes),
        new("should_sort_trips_by_date", shouldSortTripsByDate?.ToString().ToLowerInvariant()),
    ];

    internal static void ValidateUuid(string uuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);
        if (uuid.Any(c => !(char.IsAsciiHexDigit(c) || c == '-')))
        {
            throw new ArgumentException(
                $"'{uuid}' is not a TripIt UUID (e.g. 175b73da-3f0c-9000-0001-00001722144d). " +
                "TripIt's web API addresses trips and objects by UUID, not numeric id.", nameof(uuid));
        }
    }
}

/// <summary>
/// Travel-object sub-client: <c>v2/list/object</c>, <c>v2/get/{type}/uuid/{uuid}</c>.
/// </summary>
public sealed partial class ObjectsClient
{
    private readonly TripItClient _client;

    /// <summary>Largest page TripIt accepts for <c>list/object</c>.</summary>
    public const int MaxObjectPageSize = 100;

    internal ObjectsClient(TripItClient client) => _client = client;

    /// <summary>
    /// Lists travel objects: those of one trip (<paramref name="tripUuid"/>), or — with
    /// <paramref name="unfiledOnly"/> — the "Unfiled Items" not assigned to any trip.
    /// TripIt ignores any object-type filter here (measured), so filter client-side.
    /// </summary>
    public async Task<TripObjectList> ListAsync(
        string? tripUuid = null,
        bool unfiledOnly = false,
        int? pageSize = null,
        int? pageNum = null,
        CancellationToken ct = default)
    {
        if (tripUuid is not null) TripsClient.ValidateUuid(tripUuid);
        // TripIt answers 400 for page_size > 100 on list/object (measured 2026-11-06).
        if (pageSize is > MaxObjectPageSize) pageSize = MaxObjectPageSize;
        var q = new List<KeyValuePair<string, string?>>
        {
            new("trip_uuid", tripUuid),
            new("is_unfiled_items_only", unfiledOnly ? "true" : null),
            new("page_size", pageSize?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("page_num", pageNum?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        using var doc = await _client.GetJsonAsync("/api/v2/list/object", q, ct).ConfigureAwait(false);
        var root = doc.RootElement.Clone();
        return new TripObjectList
        {
            Objects = ItineraryBuilder.ExtractObjects(root),
            PageNum = TripItJson.Int(root, "page_num"),
            PageSize = TripItJson.Int(root, "page_size"),
            MaxPage = TripItJson.Int(root, "max_page"),
            TotalItems = TripItJson.Int(root, "total_items"),
        };
    }

    /// <summary>
    /// Gets one travel object. The UUID must be the <i>object's</i> UUID (e.g. the
    /// <c>AirObject</c>), not a segment's — TripIt answers "uuid provided is for the wrong
    /// type" otherwise.
    /// </summary>
    public async Task<JsonElement> GetAsync(TripItObjectType type, string uuid, CancellationToken ct = default)
    {
        TripsClient.ValidateUuid(uuid);
        var path = $"/api/v2/get/{type.ToPathSegment()}/uuid/{Uri.EscapeDataString(uuid)}";
        using var doc = await _client.GetJsonAsync(path, null, ct).ConfigureAwait(false);
        var root = doc.RootElement.Clone();
        var obj = TripItJson.AsList(root, type.ToEnvelopeKey()).FirstOrDefault();
        return obj.ValueKind == JsonValueKind.Object
            ? obj
            : throw new TripItApiException(200, path, $"Response contained no {type.ToEnvelopeKey()}.");
    }
}

/// <summary>A scheduled flight as reported by <c>v2/flightInfo</c>; times are local to each airport.</summary>
public sealed record FlightSchedule(
    string AirlineCode, string FlightNumber, string DepartureDate,
    string OriginAirportCode, string? OriginAirportName, string? OriginLocation, string? DepartureTime,
    string DestinationAirportCode, string? DestinationAirportName, string? DestinationLocation,
    string ArrivalDate, string? ArrivalTime);

/// <summary>A lookup suggestion: an id (IATA code, ISO code, place id) and a label.</summary>
public sealed record LookupResult(string Id, string Label);

/// <summary>Reference lookups backed by the SPA's <c>v2/autocomplete*</c> endpoints.</summary>
public sealed class LookupClient
{
    private readonly TripItClient _client;

    internal LookupClient(TripItClient client) => _client = client;

    /// <summary>Airports by code or name. Id is the IATA code.</summary>
    public Task<IReadOnlyList<LookupResult>> AirportsAsync(string query, CancellationToken ct = default)
        => GetAsync("autocompleteAirport", query, ct);

    /// <summary>Airlines by code or name. Id is the IATA code.</summary>
    public Task<IReadOnlyList<LookupResult>> AirlinesAsync(string query, CancellationToken ct = default)
        => GetAsync("autocompleteAirline", query, ct);

    /// <summary>Cities/places. Id is an opaque TripIt place id.</summary>
    public Task<IReadOnlyList<LookupResult>> PlacesAsync(string query, CancellationToken ct = default)
        => GetAsync("autocompletePlace", query, ct);

    /// <summary>Countries. Id is the ISO 3166 alpha-2 code.</summary>
    public Task<IReadOnlyList<LookupResult>> CountriesAsync(string query, CancellationToken ct = default)
        => GetAsync("autocompleteCountry", query, ct);

    /// <summary>States/provinces. Id is the state code.</summary>
    public Task<IReadOnlyList<LookupResult>> StatesAsync(string query, CancellationToken ct = default)
        => GetAsync("autocompleteState", query, ct);

    /// <summary>
    /// Looks up a scheduled flight (<c>v2/flightInfo</c>): origin, destination and local
    /// departure/arrival times for an airline code, flight number and departure date.
    /// Returns null when TripIt has no schedule for it.
    /// </summary>
    public async Task<FlightSchedule?> FlightAsync(string airlineCode, string flightNumber, string departureDate,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(airlineCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(flightNumber);
        var date = TripItWrite.NormalizeDate(departureDate)!;
        var code = airlineCode.Trim().ToUpperInvariant();
        var number = flightNumber.Trim();
        JsonDocument doc;
        try
        {
            doc = await _client.GetJsonAsync("/api/v2/flightInfo",
            [
                new("departure_date", date),
                new("airline_code", code),
                new("flight_num", number),
            ], ct).ConfigureAwait(false);
        }
        catch (TripItApiException ex) when (ex.StatusCode == 400)
        {
            // TripIt answers 400 with an empty description when it has no schedule.
            return null;
        }
        using (doc)
        {
            var fi = TripItJson.AsList(doc.RootElement, "FlightInfo").FirstOrDefault();
            if (fi.ValueKind != JsonValueKind.Object || TripItJson.Str(fi, "orig_airport_code") is null) return null;
            var diff = TripItJson.Int(fi, "arrival_date_diff") ?? 0;
            var arrival = DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                .AddDays(diff).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            return new FlightSchedule(
                code, number, date,
                TripItJson.Str(fi, "orig_airport_code")!, TripItJson.Str(fi, "orig_airport_display_name"),
                TripItJson.Str(fi, "orig_location_display_name"), TripItJson.Str(fi, "departure_time"),
                TripItJson.Str(fi, "dest_airport_code")!, TripItJson.Str(fi, "dest_airport_display_name"),
                TripItJson.Str(fi, "dest_location_display_name"),
                arrival, TripItJson.Str(fi, "arrival_time"));
        }
    }

    /// <summary>Street addresses (Google Places). Id is the place id; label the full address.</summary>
    public async Task<IReadOnlyList<LookupResult>> AddressesAsync(string query, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        using var doc = await _client.GetJsonAsync("/api/v2/autocompleteAddress", [new("query", query)], ct)
            .ConfigureAwait(false);
        return [.. TripItJson.AsList(doc.RootElement).Select(e => new LookupResult(
            TripItJson.Str(e, "place_id") ?? "",
            TripItJson.Str(e, "display_name") ?? ""))];
    }

    private async Task<IReadOnlyList<LookupResult>> GetAsync(string endpoint, string query, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        using var doc = await _client.GetJsonAsync($"/api/v2/{endpoint}", [new("query", query)], ct)
            .ConfigureAwait(false);
        return [.. TripItJson.AsList(doc.RootElement).Select(e => new LookupResult(
            TripItJson.Str(e, "id") ?? "",
            TripItJson.Str(e, "label") ?? ""))];
    }
}
