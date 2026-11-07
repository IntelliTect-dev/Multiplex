using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TripIt;
using TripIt.Json;
using TripIt.Models;

namespace TripItMcp;

/// <summary>
/// MCP tools that change TripIt data. Registered unless <c>TRIPIT_MCP_READONLY=1</c>.
///
/// Payloads follow the TripIt web app's own serializers (see <see cref="PlanSchema"/>):
/// field names are TripIt's (<c>supplier_conf_num</c>, <c>StartDateTime</c>, …), dates are
/// <c>yyyy-MM-dd</c> and times <c>HH:mm</c>, local to the place. Time zones are optional —
/// TripIt infers them from airports and addresses.
/// </summary>
[McpServerToolType]
public sealed class TripItWriteTools(TripItClient client)
{
    private static string ToJson<T>(T value) => TripItTools.ToJson(value);

    // ── Trips ────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_create_trip", Destructive = false, Idempotent = false)]
    [Description(
        "Creates a TripIt trip. Plans (flights, hotels, …) are added afterwards with tripit_add_flight, " +
        "tripit_add_lodging or tripit_add_plan using the returned uuid. By default the trip is NOT " +
        "shared with the user's Inner Circle.")]
    public async Task<string> CreateTrip(
        [Description("Destination, e.g. 'Reykjavik, Iceland'.")] string destination,
        [Description("First day, yyyy-MM-dd.")] string startDate,
        [Description("Last day, yyyy-MM-dd.")] string endDate,
        [Description("Trip name. TripIt generates one from destination and dates when omitted.")] string? name = null,
        [Description("Free-text description.")] string? description = null,
        [Description("Share with the user's Inner Circle (family/colleagues who see all trips). Default false.")]
        bool shareWithInnerCircle = false,
        [Description("'leisure' (default) or 'business'.")] string purpose = "leisure",
        [Description("Whether I am travelling on this trip. false = I'm planning/organising it for others. Default true.")]
        bool iAmTraveling = true,
        CancellationToken ct = default)
    {
        var trip = await client.Trips.CreateAsync(new TripInput
        {
            DisplayName = name,
            PrimaryLocation = destination,
            StartDate = startDate,
            EndDate = endDate,
            Description = description,
            IsPrivate = !shareWithInnerCircle,
            PurposeTypeCode = PurposeCode(purpose),
            IsOwnerTraveler = iAmTraveling,
        }, ct).ConfigureAwait(false);
        var me = await client.GetMyProfileRefAsync(ct).ConfigureAwait(false);
        return ToJson(new { created = TripItTools.Summarize(trip, me) });
    }

    [McpServerTool(Name = "tripit_update_trip", Destructive = false, Idempotent = true)]
    [Description(
        "Changes a trip's name, destination, dates, description, Inner Circle sharing, purpose, or " +
        "whether the owner is travelling. Works on trips I am not travelling on too. " +
        "Only the parameters you pass change; everything else is kept. Does not move or change the " +
        "trip's plans.")]
    public async Task<string> UpdateTrip(
        [Description("The trip uuid.")] string tripUuid,
        [Description("New name.")] string? name = null,
        [Description("New destination.")] string? destination = null,
        [Description("New first day, yyyy-MM-dd.")] string? startDate = null,
        [Description("New last day, yyyy-MM-dd.")] string? endDate = null,
        [Description("New description (empty string clears it).")] string? description = null,
        [Description("Share with the Inner Circle (true) or keep private (false).")] bool? shareWithInnerCircle = null,
        [Description("'leisure' or 'business'.")] string? purpose = null,
        [Description("Whether the trip owner is travelling (true) or only planning it (false).")] bool? iAmTraveling = null,
        CancellationToken ct = default)
    {
        var trip = await client.Trips.UpdateAsync(tripUuid, t => t with
        {
            DisplayName = name ?? t.DisplayName,
            PrimaryLocation = destination ?? t.PrimaryLocation,
            StartDate = startDate ?? t.StartDate,
            EndDate = endDate ?? t.EndDate,
            Description = description ?? t.Description,
            IsPrivate = shareWithInnerCircle is { } share ? !share : t.IsPrivate,
            PurposeTypeCode = purpose is null ? t.PurposeTypeCode : PurposeCode(purpose),
            IsOwnerTraveler = iAmTraveling ?? t.IsOwnerTraveler,
        }, ct).ConfigureAwait(false);
        var me = await client.GetMyProfileRefAsync(ct).ConfigureAwait(false);
        return ToJson(new { updated = TripItTools.Summarize(trip, me) });
    }

    [McpServerTool(Name = "tripit_delete_trip", Destructive = true, Idempotent = false)]
    [Description(
        "PERMANENTLY deletes a trip AND every plan in it (flights, hotels, notes …). There is no undo. " +
        "Requires confirm_name to equal the trip's exact current name, as a guard against deleting " +
        "the wrong trip; get it from tripit_get_trip first. Confirm with the user before calling.")]
    public async Task<string> DeleteTrip(
        [Description("The trip uuid.")] string tripUuid,
        [Description("The trip's exact current name.")] string confirmName,
        CancellationToken ct = default)
    {
        var current = await client.Trips.GetAsync(tripUuid, includeObjects: true, ct).ConfigureAwait(false);
        if (!string.Equals(current.Trip.DisplayName, confirmName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"confirm_name does not match. The trip is named '{current.Trip.DisplayName}'. Nothing was deleted.");
        }
        await client.Trips.DeleteAsync(tripUuid, ct).ConfigureAwait(false);
        return ToJson(new { deleted = TripItTools.Summarize(current.Trip), plans_deleted = current.ObjectCount });
    }

    [McpServerTool(Name = "tripit_merge_trips", Destructive = true, Idempotent = false)]
    [Description(
        "Merges one trip into another: every plan in the source trip moves into the destination and the " +
        "source trip is DELETED. Use it when TripIt split one journey into duplicate trips. Requires " +
        "confirm_source_name = the source trip's exact name. Confirm with the user first.")]
    public async Task<string> MergeTrips(
        [Description("The trip to merge FROM (it will disappear).")] string sourceTripUuid,
        [Description("The trip to merge INTO (it is kept).")] string destinationTripUuid,
        [Description("The source trip's exact current name.")] string confirmSourceName,
        CancellationToken ct = default)
    {
        var source = await client.Trips.GetAsync(sourceTripUuid, includeObjects: true, ct).ConfigureAwait(false);
        if (!string.Equals(source.Trip.DisplayName, confirmSourceName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"confirm_source_name does not match. The source trip is named '{source.Trip.DisplayName}'. Nothing was merged.");
        }
        var merged = await client.Trips.MergeAsync(sourceTripUuid, destinationTripUuid, ct).ConfigureAwait(false);
        var me = await client.GetMyProfileRefAsync(ct).ConfigureAwait(false);
        return ToJson(new
        {
            merged_from = TripItTools.Summarize(source.Trip, me),
            plans_moved = source.ObjectCount,
            into = TripItTools.DescribeTrip(merged, includeRaw: false, includeWeather: false, me),
        });
    }

    // ── Participants ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_add_participants", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description(
        "Shares a trip with people by email. TRIPIT EMAILS EACH ADDRESS AN INVITATION — confirm the " +
        "addresses and role with the user first. Roles: 'viewer' (read-only), 'editor' (can edit, not " +
        "travelling), 'traveler' (can edit and is travelling).")]
    public async Task<string> AddParticipants(
        [Description("The trip uuid.")] string tripUuid,
        [Description("Email addresses, comma-separated.")] string emails,
        [Description("viewer (default), editor or traveler.")] string role = "viewer",
        [Description("Optional personal message included in the invitation email.")] string? message = null,
        CancellationToken ct = default)
    {
        var list = emails.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var r = TripsClient.ParseRole(role);
        await client.Trips.InviteAsync(tripUuid, list, r, message, ct).ConfigureAwait(false);
        return await Participants(tripUuid, new { invited = list, role = r.ToString().ToLowerInvariant() }, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "tripit_change_participant_role", Destructive = false, Idempotent = true)]
    [Description(
        "Changes someone's role on a trip: 'viewer', 'editor' or 'traveler'. Use the profile_ref from " +
        "tripit_get_trip's participants.")]
    public async Task<string> ChangeParticipantRole(
        [Description("The trip uuid.")] string tripUuid,
        [Description("The participant's profile_ref.")] string profileRef,
        [Description("viewer, editor or traveler.")] string role,
        CancellationToken ct = default)
    {
        var r = TripsClient.ParseRole(role);
        await client.Trips.ChangeRoleAsync(tripUuid, profileRef, r, ct).ConfigureAwait(false);
        return await Participants(tripUuid, new { changed = profileRef, role = r.ToString().ToLowerInvariant() }, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "tripit_remove_participant", Destructive = true, Idempotent = false)]
    [Description(
        "Removes someone from a trip so they no longer see it. Use the profile_ref from tripit_get_trip's " +
        "participants. The owner cannot be removed. Confirm with the user first.")]
    public async Task<string> RemoveParticipant(
        [Description("The trip uuid.")] string tripUuid,
        [Description("The participant's profile_ref.")] string profileRef,
        CancellationToken ct = default)
    {
        var me = await client.GetMyProfileRefAsync(ct).ConfigureAwait(false);
        var before = TripsClient.Participants(await client.Trips.GetAsync(tripUuid, includeObjects: false, ct).ConfigureAwait(false), me);
        var target = before.FirstOrDefault(p => p.ProfileRef == profileRef)
                     ?? throw new ArgumentException($"{profileRef} is not a participant of trip {tripUuid}.");
        if (target.IsOwner) throw new ArgumentException("The trip owner cannot be removed. Delete the trip instead.");
        await client.Trips.RemoveParticipantAsync(tripUuid, profileRef, ct).ConfigureAwait(false);
        return await Participants(tripUuid, new { removed = target.Name ?? target.ScreenName ?? profileRef }, ct).ConfigureAwait(false);
    }

    private async Task<string> Participants(string tripUuid, object action, CancellationToken ct)
    {
        var me = await client.GetMyProfileRefAsync(ct).ConfigureAwait(false);
        var detail = await client.Trips.GetAsync(tripUuid, includeObjects: false, ct).ConfigureAwait(false);
        return ToJson(new
        {
            action,
            participants = TripsClient.Participants(detail, me).Select(p => new
            {
                profile_ref = p.ProfileRef,
                name = p.Name ?? p.ScreenName,
                role = p.Role.ToString().ToLowerInvariant(),
                is_owner = p.IsOwner,
                is_me = p.IsMe,
            }),
        });
    }

    // ── Typed conveniences for the common plans ─────────────────────────────

    [McpServerTool(Name = "tripit_add_flight", Destructive = false, Idempotent = false)]
    [Description(
        "Adds a flight reservation with one or more legs. For each leg give airline_code + " +
        "flight_number + date; the route and scheduled times are looked up automatically when " +
        "from/to/times are omitted (tripit_lookup_flight). TripIt then fills in airport names, time " +
        "zones, aircraft and duration. Legs of one booking (e.g. outbound and return on one " +
        "confirmation) belong in one call.")]
    public async Task<string> AddFlight(
        [Description(
            "JSON array of legs. Each: {\"airline_code\":\"FI\",\"flight_number\":\"680\",\"date\":\"2027-01-18\"," +
            " optional \"from\":\"SEA\",\"to\":\"KEF\",\"depart_time\":\"14:35\",\"arrive_date\":\"2027-01-19\"," +
            "\"arrive_time\":\"06:15\",\"seats\":\"12A\",\"service_class\":\"Economy\"}.")]
        string legsJson,
        [Description("Trip uuid to file it in. Omit to let TripIt auto-file by date.")] string? tripUuid = null,
        [Description("Airline confirmation / record locator.")] string? confirmation = null,
        [Description("Where it was booked, e.g. 'Delta' or 'Expedia'.")] string? bookingSite = null,
        [Description("Total cost as text, e.g. '$412.30'.")] string? totalCost = null,
        [Description("JSON array of travelers: [{\"first_name\":\"…\",\"last_name\":\"…\",\"frequent_traveler_num\":\"…\",\"ticket_num\":\"…\"}].")]
        string? travelersJson = null,
        [Description("Notes.")] string? notes = null,
        CancellationToken ct = default)
    {
        var legs = ParseArray(legsJson, "legs_json");
        if (legs.Count == 0) throw new ArgumentException("legs_json must contain at least one leg.");
        var segments = new JsonArray();
        var looked = new List<object>();
        foreach (var leg in legs.OfType<JsonObject>())
        {
            string? S(string k) => leg[k]?.GetValue<object>()?.ToString();
            var airline = S("airline_code") ?? throw new ArgumentException("Each leg needs airline_code.");
            var number = S("flight_number") ?? throw new ArgumentException("Each leg needs flight_number.");
            var date = S("date") ?? throw new ArgumentException("Each leg needs date (yyyy-MM-dd).");
            string? from = S("from"), to = S("to"), departTime = S("depart_time"), arriveDate = S("arrive_date"), arriveTime = S("arrive_time");
            if (from is null || to is null || departTime is null || arriveTime is null)
            {
                var sched = await client.Lookup.FlightAsync(airline, number, date, ct).ConfigureAwait(false);
                if (sched is not null)
                {
                    from ??= sched.OriginAirportCode;
                    to ??= sched.DestinationAirportCode;
                    departTime ??= sched.DepartureTime;
                    arriveTime ??= sched.ArrivalTime;
                    arriveDate ??= sched.ArrivalDate;
                    looked.Add(new { flight = $"{airline} {number}", from, to, departTime, arriveDate, arriveTime });
                }
                else if (from is null || to is null)
                {
                    throw new ArgumentException(
                        $"TripIt has no schedule for {airline} {number} on {date}; give 'from' and 'to' airport codes.");
                }
            }
            var seg = new JsonObject
            {
                ["marketing_airline_code"] = airline.ToUpperInvariant(),
                ["marketing_flight_number"] = number,
                ["start_airport_code"] = from!.ToUpperInvariant(),
                ["end_airport_code"] = to!.ToUpperInvariant(),
                ["StartDateTime"] = Dt(date, departTime),
                ["EndDateTime"] = Dt(arriveDate ?? date, arriveTime),
            };
            if (S("seats") is { } seats) seg["seats"] = seats;
            if (S("service_class") is { } cls) seg["service_class"] = cls;
            segments.Add(seg);
        }

        var fields = new JsonObject { ["Segment"] = segments };
        Put(fields, "supplier_conf_num", confirmation);
        Put(fields, "booking_site_name", bookingSite);
        Put(fields, "total_cost", totalCost);
        Put(fields, "notes", notes);
        if (travelersJson is not null) fields["Traveler"] = ParseArray(travelersJson, "travelers_json");

        var result = await client.Objects.CreateAsync(TripItObjectType.Air, fields, tripUuid, ct).ConfigureAwait(false);
        return Describe(result, looked.Count == 0 ? null : looked);
    }

    [McpServerTool(Name = "tripit_add_lodging", Destructive = false, Idempotent = false)]
    [Description("Adds a hotel or other stay to a trip.")]
    public async Task<string> AddLodging(
        [Description("Property name, e.g. 'Hotel Borg'.")] string name,
        [Description("Check-in date, yyyy-MM-dd.")] string checkInDate,
        [Description("Check-out date, yyyy-MM-dd.")] string checkOutDate,
        [Description("Trip uuid to file it in. Omit to let TripIt auto-file by date.")] string? tripUuid = null,
        [Description("Check-in time, HH:mm (local).")] string? checkInTime = null,
        [Description("Check-out time, HH:mm (local).")] string? checkOutTime = null,
        [Description("Street address.")] string? address = null,
        [Description("Confirmation number.")] string? confirmation = null,
        [Description("Phone.")] string? phone = null,
        [Description("Room type.")] string? roomType = null,
        [Description("Number of guests.")] int? guests = null,
        [Description("Total cost as text, e.g. '€540'.")] string? totalCost = null,
        [Description("Notes.")] string? notes = null,
        CancellationToken ct = default)
    {
        var fields = new JsonObject
        {
            ["supplier_name"] = name,
            ["StartDateTime"] = Dt(checkInDate, checkInTime),
            ["EndDateTime"] = Dt(checkOutDate, checkOutTime),
        };
        Put(fields, "supplier_conf_num", confirmation);
        Put(fields, "supplier_phone", phone);
        Put(fields, "room_type", roomType);
        Put(fields, "total_cost", totalCost);
        Put(fields, "notes", notes);
        if (guests is { } g) fields["number_guests"] = g.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (address is not null) fields["Address"] = new JsonObject { ["address"] = address };
        var result = await client.Objects.CreateAsync(TripItObjectType.Lodging, fields, tripUuid, ct).ConfigureAwait(false);
        return Describe(result);
    }

    [McpServerTool(Name = "tripit_add_note", Destructive = false, Idempotent = false)]
    [Description("Adds a free-form note to a trip (optionally dated and placed).")]
    public async Task<string> AddNote(
        [Description("Trip uuid.")] string tripUuid,
        [Description("Note title.")] string title,
        [Description("Note body.")] string? text = null,
        [Description("Date the note belongs to, yyyy-MM-dd.")] string? date = null,
        [Description("Time, HH:mm.")] string? time = null,
        [Description("Address the note refers to.")] string? address = null,
        [Description("A related URL.")] string? url = null,
        CancellationToken ct = default)
    {
        var fields = new JsonObject { ["display_name"] = title };
        Put(fields, "text", text);
        Put(fields, "url", url);
        if (date is not null) fields["DateTime"] = Dt(date, time);
        if (address is not null) fields["Address"] = new JsonObject { ["address"] = address };
        var result = await client.Objects.CreateAsync(TripItObjectType.Note, fields, tripUuid, ct).ConfigureAwait(false);
        return Describe(result);
    }

    // ── Generic plan CRUD ────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_describe_plan_fields", ReadOnly = true, Idempotent = true)]
    [Description(
        "Lists the fields TripIt accepts for a plan type (for tripit_add_plan / tripit_update_plan), " +
        "including nested shapes for dates, addresses, agency and travelers.")]
    public string DescribePlanFields(
        [Description("air, lodging, car, rail, transport, cruise, restaurant, activity, note, directions, parking or map.")]
        string type)
    {
        var t = TripItObjectTypes.Parse(type);
        var schema = PlanSchema.For(t);
        return ToJson(new
        {
            type = t.ToPathSegment(),
            summary = schema.Summary,
            fields = schema.Fields,
            segment_fields = schema.SegmentFields?.Where(f => f != "uuid"),
            traveler_key = schema.TravelerKey,
            nested = PlanSchema.NestedShapes,
            conventions = new[]
            {
                "Dates are yyyy-MM-dd and times HH:mm, local to the place: {\"date\":\"2027-01-18\",\"time\":\"15:00\"}.",
                "timezone (IANA, e.g. Atlantic/Reykjavik) is optional; TripIt infers it from airports/addresses.",
                "Segmented types (air, rail, transport, cruise) take a Segment array, one entry per leg/port.",
                "supplier_conf_num is the confirmation number; supplier_name the hotel/company/restaurant name.",
            },
        });
    }

    [McpServerTool(Name = "tripit_add_plan", Destructive = false, Idempotent = false)]
    [Description(
        "Adds any kind of plan — car rental, train, ground transport, cruise, restaurant, activity, " +
        "parking, map, directions (or flight/lodging/note) — from TripIt field names. Call " +
        "tripit_describe_plan_fields first for the type's fields. Example car: {\"supplier_name\":\"Hertz\"," +
        "\"supplier_conf_num\":\"K123\",\"StartDateTime\":{\"date\":\"2027-01-18\",\"time\":\"09:00\"}," +
        "\"EndDateTime\":{\"date\":\"2027-01-21\",\"time\":\"12:00\"},\"start_location_name\":\"KEF Airport\"}.")]
    public async Task<string> AddPlan(
        [Description("Plan type.")] string type,
        [Description("JSON object of TripIt fields.")] string fieldsJson,
        [Description("Trip uuid to file it in. Omit to let TripIt auto-file by date.")] string? tripUuid = null,
        CancellationToken ct = default)
    {
        var t = TripItObjectTypes.Parse(type);
        var result = await client.Objects.CreateAsync(t, ParseObject(fieldsJson, "fields_json"), tripUuid, ct)
            .ConfigureAwait(false);
        return Describe(result);
    }

    [McpServerTool(Name = "tripit_update_plan", Destructive = false, Idempotent = true)]
    [Description(
        "Changes fields of an existing plan; everything not mentioned is kept. Nested objects merge " +
        "(e.g. {\"StartDateTime\":{\"time\":\"16:00\"}} changes only the time); a null removes a field. " +
        "For flights/trains/transfers/cruises, pass segment_uuid to change one leg (fields are then " +
        "segment fields such as seats), or pass a full Segment array to replace all legs.")]
    public async Task<string> UpdatePlan(
        [Description("Plan type.")] string type,
        [Description("The plan's object uuid (object_uuid in an itinerary line).")] string uuid,
        [Description("JSON object of TripIt fields to change.")] string changesJson,
        [Description("Optional segment uuid to change a single leg.")] string? segmentUuid = null,
        CancellationToken ct = default)
    {
        var t = TripItObjectTypes.Parse(type);
        var changes = ParseObject(changesJson, "changes_json");
        if (segmentUuid is not null)
        {
            var segFields = PlanSchema.For(t).SegmentFields
                ?? throw new ArgumentException($"{t.ToPathSegment()} plans have no segments.");
            var unknown = changes.Select(kv => kv.Key).Where(k => !segFields.Contains(k)).ToList();
            if (unknown.Count > 0)
            {
                throw new ArgumentException($"Unknown segment field(s): {string.Join(", ", unknown)}. Valid: {string.Join(", ", segFields)}");
            }
        }
        var result = await client.Objects.UpdateAsync(t, uuid, changes, segmentUuid, ct).ConfigureAwait(false);
        return Describe(result);
    }

    [McpServerTool(Name = "tripit_delete_plan", Destructive = true, Idempotent = false)]
    [Description(
        "PERMANENTLY deletes one plan (a whole flight reservation with all its legs, a hotel stay, a " +
        "note …). To remove a single flight leg instead, use tripit_delete_segment. Confirm with the " +
        "user before calling.")]
    public async Task<string> DeletePlan(
        [Description("Plan type.")] string type,
        [Description("The plan's object uuid.")] string uuid,
        CancellationToken ct = default)
    {
        var t = TripItObjectTypes.Parse(type);
        var current = await client.Objects.GetAsync(t, uuid, ct).ConfigureAwait(false);
        var lines = ItineraryBuilder.FromObject(t, current).ToList();
        await client.Objects.DeleteAsync(t, uuid, ct).ConfigureAwait(false);
        return ToJson(new { deleted = lines });
    }

    [McpServerTool(Name = "tripit_delete_segment", Destructive = true, Idempotent = false)]
    [Description(
        "PERMANENTLY deletes one leg (segment) of a flight, train, transfer or cruise, keeping the rest " +
        "of the reservation. Use the segment_uuid from an itinerary line. Confirm with the user first.")]
    public async Task<string> DeleteSegment(
        [Description("The segment uuid.")] string segmentUuid,
        CancellationToken ct = default)
    {
        await client.Objects.DeleteSegmentAsync(segmentUuid, ct).ConfigureAwait(false);
        return ToJson(new { deleted_segment = segmentUuid });
    }

    [McpServerTool(Name = "tripit_move_plan", Destructive = false, Idempotent = true)]
    [Description(
        "Moves a plan to another trip, or to Unfiled Items when trip_uuid is omitted. Useful for " +
        "filing items TripIt could not place automatically (see tripit_list_objects unfiled_only).")]
    public async Task<string> MovePlan(
        [Description("Plan type.")] string type,
        [Description("The plan's object uuid.")] string uuid,
        [Description("Destination trip uuid; omit to move to Unfiled Items.")] string? tripUuid = null,
        CancellationToken ct = default)
    {
        var t = TripItObjectTypes.Parse(type);
        var result = await client.Objects.MoveAsync(t, uuid, tripUuid, ct).ConfigureAwait(false);
        return Describe(result);
    }

    [McpServerTool(Name = "tripit_lookup_flight", ReadOnly = true, Idempotent = true)]
    [Description(
        "Looks up a scheduled flight by airline code, flight number and date: origin and destination " +
        "airports and local departure/arrival times.")]
    public async Task<string> LookupFlight(
        [Description("IATA airline code, e.g. 'FI'.")] string airlineCode,
        [Description("Flight number, e.g. '680'.")] string flightNumber,
        [Description("Departure date, yyyy-MM-dd.")] string date,
        CancellationToken ct = default)
    {
        var sched = await client.Lookup.FlightAsync(airlineCode, flightNumber, date, ct).ConfigureAwait(false);
        if (sched is null)
        {
            return ToJson(new { found = false, message = $"TripIt has no schedule for {airlineCode} {flightNumber} on {date}." });
        }
        return ToJson(new
        {
            found = true,
            flight = $"{sched.AirlineCode} {sched.FlightNumber}",
            from = sched.OriginAirportCode,
            from_airport = sched.OriginAirportName,
            from_location = sched.OriginLocation,
            depart_date = sched.DepartureDate,
            depart_time = sched.DepartureTime,
            to = sched.DestinationAirportCode,
            to_airport = sched.DestinationAirportName,
            to_location = sched.DestinationLocation,
            arrive_date = sched.ArrivalDate,
            arrive_time = sched.ArrivalTime,
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Describe(PlanWriteResult result, object? lookedUp = null) => ToJson(new
    {
        type = result.Type.ToPathSegment(),
        object_uuid = result.Uuid,
        trip_uuid = result.TripUuid,
        itinerary = ItineraryBuilder.FromObject(result.Type, result.Object).ToList(),
        schedule_lookups = lookedUp,
        warnings = result.Warnings.Where(w => !w.Contains("marketing_airline_code", StringComparison.Ordinal)).ToList() is { Count: > 0 } ws ? ws : null,
    });

    private static JsonObject Dt(string date, string? time)
    {
        var o = new JsonObject { ["date"] = date };
        if (!string.IsNullOrWhiteSpace(time)) o["time"] = time;
        return o;
    }

    private static void Put(JsonObject o, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) o[key] = value;
    }

    private static string PurposeCode(string purpose) => purpose.Trim().ToLowerInvariant() switch
    {
        "leisure" or "l" or "personal" => "L",
        "business" or "b" or "work" => "B",
        _ => throw new ArgumentException("purpose must be 'leisure' or 'business'."),
    };

    internal static JsonObject ParseObject(string json, string name)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException($"{name} must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{name} is not valid JSON: {ex.Message}");
        }
    }

    internal static JsonArray ParseArray(string json, string name)
    {
        try
        {
            return JsonNode.Parse(json) switch
            {
                JsonArray a => a,
                JsonObject o => new JsonArray(o),
                _ => throw new ArgumentException($"{name} must be a JSON array."),
            };
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{name} is not valid JSON: {ex.Message}");
        }
    }
}
