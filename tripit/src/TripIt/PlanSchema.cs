using System.Text.Json;
using System.Text.Json.Nodes;

namespace TripIt;

/// <summary>
/// The writable shape of every travel-object type, as the TripIt web app's own serializers
/// define it (decoded from the SPA bundle's <c>getModelMap()</c> tables on 2026-11-06 and
/// confirmed against captured <c>v2/create</c> / <c>v2/replacePlan</c> requests).
///
/// Used to (a) project a fetched object down to the fields TripIt accepts back, so an
/// update can be a fetch → merge → replace, and (b) reject unknown field names with a
/// helpful message instead of letting TripIt silently ignore them.
/// </summary>
public static class PlanSchema
{
    /// <summary>Fields on every plan (<c>Gc</c> in the bundle).</summary>
    private static readonly string[] s_common = ["trip_uuid", "display_name"];

    /// <summary>Reservation fields (<c>q</c>): everything but map, note and directions.</summary>
    private static readonly string[] s_reservation =
    [
        "booking_date", "booking_rate", "booking_site_conf_num", "booking_site_name", "booking_site_phone",
        "booking_site_email_address", "booking_site_url", "supplier_conf_num", "supplier_contact",
        "supplier_email_address", "supplier_name", "supplier_phone", "supplier_url", "is_purchased",
        "notes", "restrictions", "total_cost", "Agency",
    ];

    /// <summary>Nested object shapes shared by several types.</summary>
    public static IReadOnlyDictionary<string, string[]> NestedShapes { get; } = new Dictionary<string, string[]>
    {
        ["DateTime"] = ["date", "time", "timezone", "is_timezone_manual", "preferred_timezone"],
        ["Address"] = ["address", "addr1", "addr2", "city", "state", "zip", "country", "latitude", "longitude"],
        ["Agency"] = ["agency_conf_num", "agency_name", "agency_phone", "agency_email_address", "agency_url", "agency_contact"],
        ["Traveler"] = ["first_name", "middle_name", "last_name", "frequent_traveler_num", "frequent_traveler_supplier",
                        "meal_preference", "seat_preference", "ticket_num"],
    };

    /// <summary>Which nested shape a property name uses.</summary>
    private static readonly Dictionary<string, string> s_nestedKind = new(StringComparer.Ordinal)
    {
        ["StartDateTime"] = "DateTime", ["EndDateTime"] = "DateTime", ["DateTime"] = "DateTime",
        ["Address"] = "Address", ["StartAddress"] = "Address", ["EndAddress"] = "Address",
        ["StartLocationAddress"] = "Address", ["EndLocationAddress"] = "Address",
        ["StartStationAddress"] = "Address", ["EndStationAddress"] = "Address", ["LocationAddress"] = "Address",
        ["Agency"] = "Agency",
        ["Traveler"] = "Traveler", ["Guest"] = "Traveler", ["Driver"] = "Traveler",
        ["Attendee"] = "Traveler", ["Participant"] = "Traveler",
    };

    /// <summary>Per-type definition.</summary>
    public sealed record TypeSchema(
        TripItObjectType Type,
        IReadOnlyList<string> Fields,
        IReadOnlyList<string>? SegmentFields,
        string? TravelerKey,
        string Summary);

    /// <summary>
    /// Builds a reservation schema. <paramref name="own"/> is in serializer order and may
    /// contain "Segment" and the traveler key at their serialized positions: TripIt converts
    /// JSON to XML and validates it against an XSD, so element order matters.
    /// </summary>
    private static TypeSchema Res(TripItObjectType t, string[] own, string[]? seg, string? traveler, string summary)
        => new(t, [.. s_common, .. s_reservation, .. own], seg, traveler, summary);

    private static TypeSchema Plan(TripItObjectType t, string[] own, string summary)
        => new(t, [.. s_common, .. own], null, null, summary);

    public static IReadOnlyDictionary<TripItObjectType, TypeSchema> Types { get; } = new Dictionary<TripItObjectType, TypeSchema>
    {
        [TripItObjectType.Air] = Res(TripItObjectType.Air, ["Segment", "Traveler"],
            ["uuid", "marketing_airline", "marketing_airline_code", "marketing_flight_number", "operating_airline",
             "operating_airline_code", "operating_flight_number", "start_airport_code", "start_city_name",
             "start_country_code", "start_gate", "start_terminal", "end_airport_code", "end_city_name",
             "end_country_code", "end_gate", "end_terminal", "seats", "aircraft", "StartDateTime", "EndDateTime",
             "is_international", "distance", "duration", "entertainment", "meal", "notes", "ontime_perc",
             "service_class", "stops", "baggage_claim", "check_in_url"],
            "Traveler",
            "Flight reservation. One Segment per leg: marketing_airline_code + marketing_flight_number + " +
            "start/end_airport_code + StartDateTime/EndDateTime are enough; TripIt fills in airport names, " +
            "time zones, aircraft and duration."),
        [TripItObjectType.Lodging] = Res(TripItObjectType.Lodging,
            ["StartDateTime", "EndDateTime", "Address", "Guest", "number_guests", "number_rooms", "room_type"], null, "Guest",
            "Hotel or other stay. StartDateTime = check-in, EndDateTime = check-out; supplier_name = property name."),
        [TripItObjectType.Car] = Res(TripItObjectType.Car,
            ["StartDateTime", "EndDateTime", "StartLocationAddress", "EndLocationAddress", "Driver", "start_location_hours",
             "start_location_name", "start_location_phone", "end_location_hours", "end_location_name",
             "end_location_phone", "car_description", "car_type", "mileage_charges"], null, "Driver",
            "Car rental. StartDateTime = pick-up, EndDateTime = drop-off; supplier_name = rental company."),
        [TripItObjectType.Rail] = Res(TripItObjectType.Rail, ["Segment", "Traveler"],
            ["uuid", "StartDateTime", "EndDateTime", "StartStationAddress", "EndStationAddress", "start_station_name",
             "end_station_name", "carrier_name", "coach_number", "confirmation_num", "seats", "service_class",
             "train_number", "train_type"],
            "Traveler", "Train reservation, one Segment per leg."),
        [TripItObjectType.Transport] = Res(TripItObjectType.Transport, ["Segment", "Traveler"],
            ["uuid", "StartDateTime", "EndDateTime", "StartLocationAddress", "EndLocationAddress", "start_location_name",
             "end_location_name", "carrier_name", "number_passengers", "confirmation_num", "vehicle_description",
             "detail_type_code"],
            "Traveler", "Ground transportation or ferry, one Segment per leg (detail_type_code G = ground, F = ferry)."),
        [TripItObjectType.Cruise] = Res(TripItObjectType.Cruise,
            ["Segment", "Traveler", "cabin_number", "cabin_type", "dining", "ship_name"],
            ["uuid", "StartDateTime", "EndDateTime", "LocationAddress", "location_name", "detail_type_code"],
            "Traveler", "Cruise. Segments are ports: the first is embarkation; detail_type_code P marks a port of call."),
        [TripItObjectType.Restaurant] = Res(TripItObjectType.Restaurant,
            ["DateTime", "Address", "Attendee", "cuisine", "dress_code", "hours", "number_patrons", "price_range"], null, "Attendee",
            "Restaurant reservation; supplier_name = restaurant name."),
        [TripItObjectType.Activity] = Res(TripItObjectType.Activity,
            ["StartDateTime", "EndDateTime", "Address", "Participant", "detail_type_code", "location_name"], null, "Participant",
            "Activity, meeting, concert, tour or theatre."),
        [TripItObjectType.Parking] = Res(TripItObjectType.Parking,
            ["StartDateTime", "EndDateTime", "Address", "location_hours", "location_name", "valet_ticket_num",
             "location_phone"], null, null, "Parking reservation."),
        [TripItObjectType.Note] = Plan(TripItObjectType.Note, ["DateTime", "Address", "source", "text", "url", "notes"],
            "Free-form note; display_name is the title and text the body."),
        [TripItObjectType.Map] = Plan(TripItObjectType.Map, ["DateTime", "Address"], "A pinned map location."),
        [TripItObjectType.Directions] = Plan(TripItObjectType.Directions,
            ["DateTime", "StartAddress", "EndAddress", "detail_type_code"], "Directions between two addresses."),
    };

    /// <summary>Returns the schema, throwing for read-only types (weather).</summary>
    public static TypeSchema For(TripItObjectType type)
        => Types.TryGetValue(type, out var s)
            ? s
            : throw new ArgumentException($"TripIt does not accept writes for '{type.ToPathSegment()}' objects.");

    /// <summary>
    /// Projects a TripIt object (as returned by a get) to the fields TripIt accepts in a
    /// create/replace payload. Read-only fields (ids, names TripIt derives, status, urls) are dropped.
    /// </summary>
    public static JsonObject ToWritable(TripItObjectType type, JsonElement obj)
    {
        var schema = For(type);
        var source = JsonNode.Parse(obj.GetRawText())!.AsObject();
        var result = new JsonObject();
        foreach (var field in schema.Fields)
        {
            if (source[field] is not { } v) continue;
            if (field == "Segment" && schema.SegmentFields is { } segFields)
            {
                var arr = new JsonArray();
                foreach (var seg in v is JsonArray a ? a : new JsonArray(v.DeepClone()))
                {
                    if (seg is not JsonObject so) continue;
                    var ps = new JsonObject();
                    foreach (var f in segFields)
                    {
                        if (so[f] is { } sv) ps[f] = ProjectNested(f, sv.DeepClone());
                    }
                    arr.Add(ps);
                }
                result["Segment"] = arr;
            }
            else
            {
                result[field] = ProjectNested(field, v.DeepClone());
            }
        }
        return result;
    }

    /// <summary>
    /// Returns a copy of a validated payload with every level in serializer order. TripIt
    /// validates the XML it derives from the JSON against an XSD whose sequences are ordered.
    /// </summary>
    public static JsonObject Order(TripItObjectType type, JsonObject payload)
    {
        var schema = For(type);
        var result = new JsonObject();
        foreach (var field in schema.Fields)
        {
            if (!payload.TryGetPropertyValue(field, out var v) || v is null) continue;
            if (field == "Segment" && schema.SegmentFields is { } segFields && v is JsonArray segs)
            {
                result[field] = new JsonArray([.. segs.OfType<JsonObject>().Select(seg => (JsonNode)OrderBy(seg, segFields))]);
            }
            else
            {
                result[field] = OrderNested(field, v);
            }
        }
        return result;
    }

    private static JsonObject OrderBy(JsonObject obj, IReadOnlyList<string> order)
    {
        var r = new JsonObject();
        foreach (var k in order)
        {
            if (obj.TryGetPropertyValue(k, out var v) && v is not null) r[k] = OrderNested(k, v);
        }
        return r;
    }

    private static JsonNode OrderNested(string field, JsonNode value)
    {
        if (!s_nestedKind.TryGetValue(field, out var kind)) return value.DeepClone();
        var allowed = NestedShapes[kind];
        return value switch
        {
            JsonObject o => OrderBy(o, allowed),
            JsonArray arr => new JsonArray([.. arr.OfType<JsonObject>().Select(o => (JsonNode)OrderBy(o, allowed))]),
            _ => value.DeepClone(),
        };
    }

    private static JsonNode? ProjectNested(string field, JsonNode? value)
    {
        if (!s_nestedKind.TryGetValue(field, out var kind)) return value;
        var allowed = NestedShapes[kind];
        JsonObject Project(JsonObject o)
        {
            var r = new JsonObject();
            foreach (var k in allowed)
            {
                if (o[k] is { } v) r[k] = v.DeepClone();
            }
            return r;
        }
        return value switch
        {
            JsonObject o => kind == "Traveler" ? new JsonArray(Project(o)) : Project(o),
            JsonArray arr => new JsonArray([.. arr.OfType<JsonObject>().Select(Project)]),
            _ => value,
        };
    }

    /// <summary>
    /// Validates caller-supplied fields against the schema. Throws <see cref="ArgumentException"/>
    /// listing unknown names and the valid ones, so a typo is reported instead of ignored.
    /// </summary>
    public static void Validate(TripItObjectType type, JsonObject fields)
    {
        var schema = For(type);
        var unknown = new List<string>();
        foreach (var (key, value) in fields)
        {
            if (key == "Segment" && schema.SegmentFields is { } segFields)
            {
                foreach (var seg in value is JsonArray a ? a.OfType<JsonObject>() : value is JsonObject o ? [o] : [])
                {
                    unknown.AddRange(seg.Select(kv => kv.Key).Where(k => !segFields.Contains(k)).Select(k => "Segment." + k));
                    ValidateNested(seg, unknown, "Segment.");
                }
                continue;
            }
            if (!schema.Fields.Contains(key)) unknown.Add(key);
        }
        ValidateNested(fields, unknown, "");
        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown {type.ToPathSegment()} field(s): {string.Join(", ", unknown.Distinct())}. " +
                $"Valid: {Describe(type)}");
        }
    }

    private static void ValidateNested(JsonObject obj, List<string> unknown, string prefix)
    {
        foreach (var (key, value) in obj)
        {
            if (!s_nestedKind.TryGetValue(key, out var kind)) continue;
            var allowed = NestedShapes[kind];
            foreach (var item in value is JsonArray a ? a.OfType<JsonObject>() : value is JsonObject o ? [o] : [])
            {
                unknown.AddRange(item.Select(kv => kv.Key).Where(k => !allowed.Contains(k)).Select(k => $"{prefix}{key}.{k}"));
            }
        }
    }

    /// <summary>A one-line description of the writable fields of a type, for errors and tool docs.</summary>
    public static string Describe(TripItObjectType type)
    {
        var s = For(type);
        var top = string.Join(", ", s.Fields);
        return s.SegmentFields is null ? top : $"{top}; Segment[]: {string.Join(", ", s.SegmentFields.Where(f => f != "uuid"))}";
    }
}
