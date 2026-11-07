using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TripIt.Json;

namespace TripIt.Models;

/// <summary>A TripIt date/time block (<c>StartDateTime</c>, <c>EndDateTime</c>, <c>DateTime</c>).</summary>
public sealed record TripItDateTime
{
    [JsonPropertyName("date")]       public string? Date { get; init; }
    [JsonPropertyName("time")]       public string? Time { get; init; }
    [JsonPropertyName("timezone")]   public string? Timezone { get; init; }
    [JsonPropertyName("utc_offset")] public string? UtcOffset { get; init; }

    /// <summary>Local ISO-8601 form (<c>2026-11-25T14:15:00-08:00</c>), as precise as the data allows.</summary>
    [JsonIgnore]
    public string? Iso
    {
        get
        {
            if (Date is null) return null;
            if (Time is null) return Date;
            return UtcOffset is null ? $"{Date}T{Time}" : $"{Date}T{Time}{UtcOffset}";
        }
    }

    /// <summary>Sort key in UTC where an offset is known; otherwise local.</summary>
    [JsonIgnore]
    public DateTimeOffset? SortKey
        => Iso is { } iso && DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.AssumeUniversal, out var dto) ? dto : null;

    internal static TripItDateTime? From(JsonElement? e)
    {
        if (e is not { } el) return null;
        var dt = new TripItDateTime
        {
            Date = TripItJson.Str(el, "date"),
            Time = TripItJson.Str(el, "time"),
            Timezone = TripItJson.Str(el, "timezone"),
            UtcOffset = TripItJson.Str(el, "utc_offset"),
        };
        return dt.Date is null ? null : dt;
    }
}

/// <summary>
/// One chronological line of a trip: a flight leg, a hotel stay, a car rental, a note…
/// Produced by <see cref="ItineraryBuilder"/> from the raw objects of
/// <c>get/trip/…/include_objects/true</c>.
/// </summary>
public sealed record ItineraryItem
{
    /// <summary>Object type path segment (<c>air</c>, <c>lodging</c>, …).</summary>
    [JsonPropertyName("type")]          public required string Type { get; init; }
    /// <summary>UUID of the reservation/plan object (use with get/replace/delete).</summary>
    [JsonPropertyName("object_uuid")]   public string? ObjectUuid { get; init; }
    /// <summary>UUID of the segment, for multi-segment objects (flights, rail, transport, cruise).</summary>
    [JsonPropertyName("segment_uuid")]  public string? SegmentUuid { get; init; }
    [JsonPropertyName("title")]         public required string Title { get; init; }
    [JsonPropertyName("start")]         public string? Start { get; init; }
    [JsonPropertyName("start_timezone")] public string? StartTimezone { get; init; }
    [JsonPropertyName("end")]           public string? End { get; init; }
    [JsonPropertyName("end_timezone")]  public string? EndTimezone { get; init; }
    [JsonPropertyName("from")]          public string? From { get; init; }
    [JsonPropertyName("to")]            public string? To { get; init; }
    [JsonPropertyName("location")]      public string? Location { get; init; }
    [JsonPropertyName("confirmation")]  public string? Confirmation { get; init; }
    [JsonPropertyName("supplier")]      public string? Supplier { get; init; }
    /// <summary>Type-specific highlights (seat, flight status, room type, …).</summary>
    [JsonPropertyName("details")]       public Dictionary<string, string>? Details { get; init; }

    [JsonIgnore] internal DateTimeOffset? SortKey { get; init; }
}

/// <summary>A trip plus everything in it, as returned by <c>TripsClient.GetAsync(includeObjects: true)</c>.</summary>
public sealed class TripDetail
{
    public required Trip Trip { get; init; }

    /// <summary>Raw travel objects keyed by type. Each element is the unmodified TripIt JSON.</summary>
    public required IReadOnlyDictionary<TripItObjectType, IReadOnlyList<JsonElement>> Objects { get; init; }

    /// <summary>Profiles of the people the trip is shared with (raw).</summary>
    public IReadOnlyList<JsonElement> Profiles { get; init; } = [];

    /// <summary>All objects flattened into chronological itinerary lines.</summary>
    public IReadOnlyList<ItineraryItem> Itinerary => ItineraryBuilder.Build(Objects);

    /// <summary>Total number of travel objects (excluding weather).</summary>
    public int ObjectCount => Objects.Where(kv => kv.Key != TripItObjectType.Weather).Sum(kv => kv.Value.Count);
}

/// <summary>A page of travel objects from <c>list/object</c>.</summary>
public sealed class TripObjectList
{
    public required IReadOnlyDictionary<TripItObjectType, IReadOnlyList<JsonElement>> Objects { get; init; }
    public int? PageNum { get; init; }
    public int? PageSize { get; init; }
    public int? MaxPage { get; init; }
    public int? TotalItems { get; init; }

    public IReadOnlyList<ItineraryItem> Itinerary => ItineraryBuilder.Build(Objects);
}

/// <summary>Turns raw TripIt travel objects into <see cref="ItineraryItem"/>s.</summary>
public static class ItineraryBuilder
{
    /// <summary>Extracts every travel object found in a TripIt response envelope.</summary>
    public static IReadOnlyDictionary<TripItObjectType, IReadOnlyList<JsonElement>> ExtractObjects(JsonElement envelope)
    {
        var result = new Dictionary<TripItObjectType, IReadOnlyList<JsonElement>>();
        if (envelope.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in envelope.EnumerateObject())
        {
            if (TripItObjectTypes.FromEnvelopeKey(prop.Name) is { } type)
            {
                result[type] = TripItJson.AsList(prop.Value);
            }
        }
        return result;
    }

    public static IReadOnlyList<ItineraryItem> Build(
        IReadOnlyDictionary<TripItObjectType, IReadOnlyList<JsonElement>> objects,
        bool includeWeather = false)
    {
        var items = new List<ItineraryItem>();
        foreach (var (type, list) in objects)
        {
            if (type == TripItObjectType.Weather && !includeWeather) continue;
            foreach (var obj in list)
            {
                items.AddRange(FromObject(type, obj));
            }
        }
        return [.. items
            .OrderBy(i => i.SortKey ?? DateTimeOffset.MaxValue)
            .ThenBy(i => i.Type, StringComparer.Ordinal)];
    }

    /// <summary>Produces one item per segment (flights, rail, transport, cruise) or per object.</summary>
    public static IEnumerable<ItineraryItem> FromObject(TripItObjectType type, JsonElement obj)
    {
        var segments = TripItJson.AsList(obj, "Segment");
        if (segments.Count == 0)
        {
            yield return Make(type, obj, obj, segmentUuid: null, isSegment: false);
            yield break;
        }
        foreach (var seg in segments)
        {
            yield return Make(type, obj, seg, TripItJson.Str(seg, "uuid"), isSegment: true);
        }
    }

    private static ItineraryItem Make(TripItObjectType type, JsonElement obj, JsonElement seg, string? segmentUuid, bool isSegment)
    {
        var start = TripItDateTime.From(TripItJson.Obj(seg, "StartDateTime") ?? TripItJson.Obj(seg, "DateTime"))
                    ?? TripItDateTime.From(TripItJson.Obj(obj, "StartDateTime") ?? TripItJson.Obj(obj, "DateTime"));
        var end = TripItDateTime.From(TripItJson.Obj(seg, "EndDateTime"))
                  ?? (isSegment ? TripItDateTime.From(TripItJson.Obj(obj, "EndDateTime")) : null);
        if (type == TripItObjectType.Weather && start is null && TripItJson.Str(obj, "date") is { } wd)
        {
            start = new TripItDateTime { Date = wd };
        }

        string S(JsonElement e, string n) => TripItJson.Str(e, n) ?? "";
        string? Join(string sep, params string?[] parts)
        {
            var p = parts.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            return p.Length == 0 ? null : string.Join(sep, p);
        }
        string? Addr(JsonElement e, string name)
            => TripItJson.Obj(e, name) is { } a ? TripItJson.Str(a, "address") : null;

        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        void D(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) details[key] = value!;
        }

        string? from = null, to = null, location = null, title;
        var supplier = TripItJson.Str(obj, "supplier_name") ?? TripItJson.Str(obj, "booking_site_name");
        var confirmation = TripItJson.Str(obj, "supplier_conf_num")
                           ?? TripItJson.Str(seg, "confirmation_num")
                           ?? TripItJson.Str(obj, "booking_site_conf_num");

        switch (type)
        {
            case TripItObjectType.Air:
            {
                var airline = TripItJson.Str(seg, "marketing_airline") ?? TripItJson.Str(seg, "marketing_airline_code");
                var flight = Join(" ", TripItJson.Str(seg, "marketing_airline_code"), TripItJson.Str(seg, "marketing_flight_number"));
                from = Join(" ", TripItJson.Str(seg, "start_airport_code"), Paren(TripItJson.Str(seg, "start_city_name")));
                to = Join(" ", TripItJson.Str(seg, "end_airport_code"), Paren(TripItJson.Str(seg, "end_city_name")));
                title = Join(" ", "Flight", flight, Join(" → ", TripItJson.Str(seg, "start_airport_code"), TripItJson.Str(seg, "end_airport_code"))) ?? "Flight";
                supplier = airline ?? supplier;
                D("operating", Join(" ", TripItJson.Str(seg, "operating_airline"), TripItJson.Str(seg, "operating_flight_number")));
                D("seats", TripItJson.Str(seg, "seats"));
                D("class", TripItJson.Str(seg, "service_class"));
                D("aircraft", TripItJson.Str(seg, "aircraft_display_name"));
                D("duration", TripItJson.Str(seg, "duration"));
                D("stops", TripItJson.Str(seg, "stops"));
                D("start_terminal", TripItJson.Str(seg, "start_terminal"));
                D("start_gate", TripItJson.Str(seg, "start_gate"));
                D("end_terminal", TripItJson.Str(seg, "end_terminal"));
                D("end_gate", TripItJson.Str(seg, "end_gate"));
                if (TripItJson.Obj(seg, "Status") is { } st)
                {
                    D("flight_status", FlightStatus(TripItJson.Str(st, "flight_status")));
                    D("departure_terminal", TripItJson.Str(st, "departure_terminal"));
                    D("departure_gate", TripItJson.Str(st, "departure_gate"));
                    D("arrival_gate", TripItJson.Str(st, "arrival_gate"));
                    D("baggage_claim", TripItJson.Str(st, "baggage_claim"));
                }
                break;
            }
            case TripItObjectType.Lodging:
                title = Join(" ", "Stay:", supplier ?? S(obj, "display_name")) ?? "Lodging";
                location = Addr(obj, "Address");
                D("room_type", TripItJson.Str(obj, "room_type"));
                D("guests", TripItJson.Str(obj, "number_guests"));
                D("rooms", TripItJson.Str(obj, "number_rooms"));
                D("phone", TripItJson.Str(obj, "supplier_phone"));
                break;
            case TripItObjectType.Car:
                title = Join(" ", "Car rental:", supplier ?? S(obj, "display_name")) ?? "Car rental";
                from = TripItJson.Str(obj, "start_location_name") ?? Addr(obj, "StartLocationAddress");
                to = TripItJson.Str(obj, "end_location_name") ?? Addr(obj, "EndLocationAddress");
                D("car_type", TripItJson.Str(obj, "car_type"));
                D("phone", TripItJson.Str(obj, "start_location_phone") ?? TripItJson.Str(obj, "supplier_phone"));
                break;
            case TripItObjectType.Rail:
                from = TripItJson.Str(seg, "start_station_name") ?? Addr(seg, "StartStationAddress");
                to = TripItJson.Str(seg, "end_station_name") ?? Addr(seg, "EndStationAddress");
                supplier = TripItJson.Str(seg, "carrier_name") ?? supplier;
                title = Join(" ", "Train", TripItJson.Str(seg, "train_number"), Join(" → ", from, to)) ?? "Train";
                D("coach", TripItJson.Str(seg, "coach_number"));
                D("seats", TripItJson.Str(seg, "seats"));
                D("class", TripItJson.Str(seg, "service_class"));
                break;
            case TripItObjectType.Transport:
                from = TripItJson.Str(seg, "start_location_name") ?? Addr(seg, "StartLocationAddress");
                to = TripItJson.Str(seg, "end_location_name") ?? Addr(seg, "EndLocationAddress");
                supplier = TripItJson.Str(seg, "carrier_name") ?? supplier;
                title = Join(" ", "Transport:", TripItJson.Str(seg, "detail_type_code") is { } dtc ? dtc : null,
                    Join(" → ", from, to) ?? S(obj, "display_name")) ?? "Transport";
                D("vehicle", TripItJson.Str(seg, "vehicle_description"));
                D("confirmation", TripItJson.Str(seg, "confirmation_num"));
                break;
            case TripItObjectType.Cruise:
                location = TripItJson.Str(seg, "location_name") ?? Addr(seg, "LocationAddress");
                title = Join(" ", "Cruise:", TripItJson.Str(obj, "ship_name") ?? supplier, location is null ? null : $"@ {location}") ?? "Cruise";
                D("cabin", TripItJson.Str(obj, "cabin_number"));
                D("dining", TripItJson.Str(obj, "dining"));
                break;
            case TripItObjectType.Restaurant:
                title = Join(" ", "Dining:", TripItJson.Str(obj, "restaurant_name") ?? supplier ?? S(obj, "display_name")) ?? "Restaurant";
                location = Addr(obj, "Address");
                D("party_size", TripItJson.Str(obj, "number_patrons"));
                D("cuisine", TripItJson.Str(obj, "cuisine"));
                break;
            case TripItObjectType.Activity:
                title = Join(" ", "Activity:", TripItJson.Str(obj, "display_name") ?? supplier) ?? "Activity";
                location = TripItJson.Str(obj, "location_name") ?? Addr(obj, "Address");
                break;
            case TripItObjectType.Note:
                title = Join(" ", "Note:", TripItJson.Str(obj, "display_name")) ?? "Note";
                location = Addr(obj, "Address");
                D("text", Truncate(TripItJson.Str(obj, "text"), 500));
                break;
            case TripItObjectType.Directions:
                from = Addr(obj, "StartAddress");
                to = Addr(obj, "EndAddress");
                title = Join(" ", "Directions:", Join(" → ", from, to)) ?? "Directions";
                break;
            case TripItObjectType.Parking:
                title = Join(" ", "Parking:", supplier ?? TripItJson.Str(obj, "display_name")) ?? "Parking";
                location = TripItJson.Str(obj, "location_name") ?? Addr(obj, "Address");
                break;
            case TripItObjectType.Map:
                title = Join(" ", "Map:", TripItJson.Str(obj, "display_name")) ?? "Map";
                location = Addr(obj, "Address");
                break;
            case TripItObjectType.Weather:
                location = TripItJson.Str(obj, "location");
                title = Join(" ", "Weather:", location) ?? "Weather";
                D("avg_high_c", TripItJson.Str(obj, "avg_high_temp_c"));
                D("avg_low_c", TripItJson.Str(obj, "avg_low_temp_c"));
                D("precipitation", TripItJson.Str(obj, "avg_precipitation_cm"));
                break;
            default:
                title = TripItJson.Str(obj, "display_name") ?? type.ToDisplayName();
                break;
        }

        D("cost", TripItJson.Str(obj, "total_cost"));
        D("notes", Truncate(TripItJson.Str(obj, "notes"), 300));
        if (TripItJson.AsList(obj, "Traveler") is { Count: > 0 } travelers)
        {
            D("travelers", string.Join(", ", travelers
                .Select(t => Join(" ", TripItJson.Str(t, "first_name"), TripItJson.Str(t, "last_name")))
                .Where(n => n is not null)));
        }

        return new ItineraryItem
        {
            Type = type.ToPathSegment(),
            ObjectUuid = TripItJson.Str(obj, "uuid"),
            SegmentUuid = segmentUuid,
            Title = title,
            Start = start?.Iso,
            StartTimezone = start?.Timezone,
            End = end?.Iso,
            EndTimezone = end?.Timezone,
            From = from,
            To = to,
            Location = location,
            Confirmation = confirmation,
            Supplier = supplier,
            Details = details.Count == 0 ? null : details,
            SortKey = start?.SortKey,
        };
    }

    private static string? Paren(string? s) => s is null ? null : $"({s})";

    private static string? Truncate(string? s, int max)
        => s is null || s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// TripIt flight_status codes (public v1 docs): 100 not monitorable, 200 not monitored,
    /// 3xx scheduled/on-time/early/delayed, 4xx in-flight/landed/diverted/cancelled.
    /// </summary>
    public static string? FlightStatus(string? code) => code switch
    {
        null => null,
        "100" => "not monitorable",
        "200" => "not monitored",
        "300" => "scheduled",
        "301" => "on time",
        "302" => "in flight - on time",
        "303" => "arrived - on time",
        "400" => "cancelled",
        "401" => "delayed",
        "402" => "in flight - late",
        "403" => "arrived - late",
        "404" => "diverted",
        "405" => "possibly delayed",
        "406" => "in flight - possibly late",
        "407" => "arrived - possibly late",
        "408" => "unknown",
        _ => code,
    };

    /// <summary>Compact JSON for an object (empty and UI-plumbing fields removed).</summary>
    public static JsonNode? Compact(JsonElement obj) => TripItJson.Prune(obj);
}
