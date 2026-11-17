using System.ComponentModel;
using ModelContextProtocol.Server;
using Multiplex.Web.Mcp;

namespace Multiplex.Web.Servers.TripIt;

/// <summary>
/// TripIt tools that change data, at <c>/tripit/mcp</c>, for the request's signed-in account.
/// Payloads follow the TripIt web app's own serializers: field names are TripIt's
/// (<c>supplier_conf_num</c>, <c>StartDateTime</c>, …), dates are <c>yyyy-MM-dd</c> only and
/// times <c>HH:mm</c>, local to the place.
/// </summary>
/// <remarks>
/// Deletes, merges, participant removal and invitations (which email people) take a confirm
/// step: called without <c>confirm_token</c> they change nothing and return a preview plus a
/// token; called again with the same arguments and that token, they act. See
/// <see cref="ConfirmTokens"/>. <c>tripit_raw_get</c> is deliberately not served here.
/// </remarks>
[McpServerToolType]
[McpEndpoint("tripit")]
public sealed class TripItWriteTools(TripItToolContext tripIt)
{
    private const string ConfirmTokenHelp =
        "Leave empty on the first call: nothing changes and you get a preview plus a confirm_token. Show the " +
        "preview to the person; only if they agree, call again with the same arguments and that confirm_token " +
        "(valid 5 minutes).";

    private const string ConfirmStep =
        " Two-step: the first call (no confirm_token) only previews; the change happens on a second call with the token.";

    // ── Trips ────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_create_trip", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Creates a TripIt trip. Plans (flights, hotels, …) are added afterwards with tripit_add_flight, " +
        "tripit_add_lodging or tripit_add_plan using the returned uuid. By default the trip is NOT " +
        "shared with the user's Inner Circle. If the result says the trip was created but a follow-up " +
        "step failed, do not create it again: update the trip it names.")]
    public Task<string> CreateTrip(
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
        => tripIt.RunAsync("tripit_create_trip", ops => ops.CreateTripAsync(destination, startDate, endDate, name,
            description, shareWithInnerCircle, purpose, iAmTraveling, ct), ct);

    [McpServerTool(Name = "tripit_update_trip", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Changes a trip's name, destination, dates, description, Inner Circle sharing, purpose, or " +
        "whether the owner is travelling. Works on trips I am not travelling on too. Only the " +
        "parameters you pass change; everything else is kept. Does not move or change the trip's plans.")]
    public Task<string> UpdateTrip(
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
        => tripIt.RunAsync("tripit_update_trip", ops => ops.UpdateTripAsync(tripUuid, name, destination, startDate,
            endDate, description, shareWithInnerCircle, purpose, iAmTraveling, ct), ct);

    [McpServerTool(Name = "tripit_delete_trip", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "PERMANENTLY deletes a trip AND every plan in it (flights, hotels, notes …). There is no undo. " +
        "confirmName must equal the trip's exact current name (get it from tripit_get_trip)." + ConfirmStep)]
    public Task<string> DeleteTrip(
        [Description("The trip uuid.")] string tripUuid,
        [Description("The trip's exact current name.")] string confirmName,
        [Description(ConfirmTokenHelp)] string? confirm_token = null,
        CancellationToken ct = default)
        => tripIt.RunConfirmedAsync("tripit_delete_trip", confirm_token,
            Args(("tripUuid", tripUuid), ("confirmName", confirmName)),
            ops => ops.PreviewDeleteTripAsync(tripUuid, confirmName, ct),
            ops => ops.DeleteTripAsync(tripUuid, confirmName, ct), ct);

    [McpServerTool(Name = "tripit_merge_trips", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Merges one trip into another: every plan in the source trip moves into the destination and the " +
        "source trip is DELETED. Use it when TripIt split one journey into duplicate trips. " +
        "confirmSourceName must equal the source trip's exact name." + ConfirmStep)]
    public Task<string> MergeTrips(
        [Description("The trip to merge FROM (it will disappear).")] string sourceTripUuid,
        [Description("The trip to merge INTO (it is kept).")] string destinationTripUuid,
        [Description("The source trip's exact current name.")] string confirmSourceName,
        [Description(ConfirmTokenHelp)] string? confirm_token = null,
        CancellationToken ct = default)
        => tripIt.RunConfirmedAsync("tripit_merge_trips", confirm_token,
            Args(("sourceTripUuid", sourceTripUuid), ("destinationTripUuid", destinationTripUuid),
                ("confirmSourceName", confirmSourceName)),
            ops => ops.PreviewMergeTripsAsync(sourceTripUuid, destinationTripUuid, confirmSourceName, ct),
            ops => ops.MergeTripsAsync(sourceTripUuid, destinationTripUuid, confirmSourceName, ct), ct);

    // ── Participants ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_add_participants", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description(
        "Shares a trip with people by email. TRIPIT EMAILS EACH ADDRESS AN INVITATION. Roles: 'viewer' " +
        "(read-only), 'editor' (can edit, not travelling), 'traveler' (can edit and is travelling)." + ConfirmStep)]
    public Task<string> AddParticipants(
        [Description("The trip uuid.")] string tripUuid,
        [Description("Email addresses, comma-separated.")] string emails,
        [Description("viewer (default), editor or traveler.")] string role = "viewer",
        [Description("Optional personal message included in the invitation email.")] string? message = null,
        [Description(ConfirmTokenHelp)] string? confirm_token = null,
        CancellationToken ct = default)
        => tripIt.RunConfirmedAsync("tripit_add_participants", confirm_token,
            Args(("tripUuid", tripUuid), ("emails", emails), ("role", role), ("message", message)),
            ops => ops.PreviewAddParticipantsAsync(tripUuid, emails, role, message, ct),
            ops => ops.AddParticipantsAsync(tripUuid, emails, role, message, ct), ct);

    [McpServerTool(Name = "tripit_change_participant_role", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Changes someone's role on a trip: 'viewer', 'editor' or 'traveler'. Use the profile_ref from " +
        "tripit_get_trip's participants.")]
    public Task<string> ChangeParticipantRole(
        [Description("The trip uuid.")] string tripUuid,
        [Description("The participant's profile_ref.")] string profileRef,
        [Description("viewer, editor or traveler.")] string role,
        CancellationToken ct = default)
        => tripIt.RunAsync("tripit_change_participant_role", ops => ops.ChangeParticipantRoleAsync(tripUuid, profileRef, role, ct), ct);

    [McpServerTool(Name = "tripit_remove_participant", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Removes someone from a trip so they no longer see it. Use the profile_ref from tripit_get_trip's " +
        "participants. The owner cannot be removed." + ConfirmStep)]
    public Task<string> RemoveParticipant(
        [Description("The trip uuid.")] string tripUuid,
        [Description("The participant's profile_ref.")] string profileRef,
        [Description(ConfirmTokenHelp)] string? confirm_token = null,
        CancellationToken ct = default)
        => tripIt.RunConfirmedAsync("tripit_remove_participant", confirm_token,
            Args(("tripUuid", tripUuid), ("profileRef", profileRef)),
            ops => ops.PreviewRemoveParticipantAsync(tripUuid, profileRef, ct),
            ops => ops.RemoveParticipantAsync(tripUuid, profileRef, ct), ct);

    // ── Typed conveniences for the common plans ─────────────────────────────

    [McpServerTool(Name = "tripit_add_flight", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Adds a flight reservation with one or more legs. For each leg give airline_code + " +
        "flight_number + date; the route and scheduled times are looked up automatically when " +
        "from/to/times are omitted (tripit_lookup_flight). TripIt then fills in airport names, time " +
        "zones, aircraft and duration. Legs of one booking (e.g. outbound and return on one " +
        "confirmation) belong in one call. The result lists any assumption made (for example an " +
        "unknown arrival date).")]
    public Task<string> AddFlight(
        [Description(
            "JSON array of legs. Each: {\"airline_code\":\"FI\",\"flight_number\":\"680\",\"date\":\"2027-01-18\"," +
            " optional \"from\":\"SEA\",\"to\":\"KEF\",\"depart_time\":\"14:35\",\"arrive_date\":\"2027-01-19\"," +
            "\"arrive_time\":\"06:15\",\"seats\":\"12A\",\"service_class\":\"Economy\"}. Dates are yyyy-MM-dd.")]
        string legsJson,
        [Description("Trip uuid to file it in. Omit to let TripIt auto-file by date.")] string? tripUuid = null,
        [Description("Airline confirmation / record locator.")] string? confirmation = null,
        [Description("Where it was booked, e.g. 'Delta' or 'Expedia'.")] string? bookingSite = null,
        [Description("Total cost as text, e.g. '$412.30'.")] string? totalCost = null,
        [Description("JSON array of travelers: [{\"first_name\":\"…\",\"last_name\":\"…\",\"frequent_traveler_num\":\"…\",\"ticket_num\":\"…\"}].")]
        string? travelersJson = null,
        [Description("Notes.")] string? notes = null,
        CancellationToken ct = default)
        => tripIt.RunAsync("tripit_add_flight", ops => ops.AddFlightAsync(legsJson, tripUuid, confirmation, bookingSite,
            totalCost, travelersJson, notes, ct), ct);

    [McpServerTool(Name = "tripit_add_lodging", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a hotel or other stay to a trip.")]
    public Task<string> AddLodging(
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
        => tripIt.RunAsync("tripit_add_lodging", ops => ops.AddLodgingAsync(name, checkInDate, checkOutDate, tripUuid,
            checkInTime, checkOutTime, address, confirmation, phone, roomType, guests, totalCost, notes, ct), ct);

    [McpServerTool(Name = "tripit_add_note", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a free-form note to a trip (optionally dated and placed).")]
    public Task<string> AddNote(
        [Description("Trip uuid.")] string tripUuid,
        [Description("Note title.")] string title,
        [Description("Note body.")] string? text = null,
        [Description("Date the note belongs to, yyyy-MM-dd.")] string? date = null,
        [Description("Time, HH:mm.")] string? time = null,
        [Description("Address the note refers to.")] string? address = null,
        [Description("A related URL.")] string? url = null,
        CancellationToken ct = default)
        => tripIt.RunAsync("tripit_add_note", ops => ops.AddNoteAsync(tripUuid, title, text, date, time, address, url, ct), ct);

    // ── Generic plan CRUD ────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_add_plan", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Adds any kind of plan — car rental, train, ground transport, cruise, restaurant, activity, " +
        "parking, map, directions (or flight/lodging/note) — from TripIt field names. Call " +
        "tripit_describe_plan_fields first for the type's fields. Example car: {\"supplier_name\":\"Hertz\"," +
        "\"supplier_conf_num\":\"K123\",\"StartDateTime\":{\"date\":\"2027-01-18\",\"time\":\"09:00\"}," +
        "\"EndDateTime\":{\"date\":\"2027-01-21\",\"time\":\"12:00\"},\"start_location_name\":\"KEF Airport\"}.")]
    public Task<string> AddPlan(
        [Description("Plan type.")] string type,
        [Description("JSON object of TripIt fields.")] string fieldsJson,
        [Description("Trip uuid to file it in. Omit to let TripIt auto-file by date.")] string? tripUuid = null,
        CancellationToken ct = default)
        => tripIt.RunAsync("tripit_add_plan", ops => ops.AddPlanAsync(type, fieldsJson, tripUuid, ct), ct);

    [McpServerTool(Name = "tripit_update_plan", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Changes fields of an existing plan; everything not mentioned is kept. Nested objects merge " +
        "(e.g. {\"StartDateTime\":{\"time\":\"16:00\"}} changes only the time); a null removes a field. " +
        "For flights/trains/transfers/cruises, pass segmentUuid to change one leg (fields are then " +
        "segment fields such as seats). A Segment array must name every existing leg by its uuid (each " +
        "merges into that leg; legs without a uuid are added) — leaving a leg out is refused, because it " +
        "would delete the leg. To remove a leg use tripit_delete_segment.")]
    public Task<string> UpdatePlan(
        [Description("Plan type.")] string type,
        [Description("The plan's object uuid (object_uuid in an itinerary line).")] string uuid,
        [Description("JSON object of TripIt fields to change.")] string changesJson,
        [Description("Optional segment uuid to change a single leg.")] string? segmentUuid = null,
        CancellationToken ct = default)
        => tripIt.RunAsync("tripit_update_plan", ops => ops.UpdatePlanAsync(type, uuid, changesJson, segmentUuid, ct), ct);

    [McpServerTool(Name = "tripit_delete_plan", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "PERMANENTLY deletes one plan (a whole flight reservation with all its legs, a hotel stay, a " +
        "note …). To remove a single flight leg instead, use tripit_delete_segment." + ConfirmStep)]
    public Task<string> DeletePlan(
        [Description("Plan type.")] string type,
        [Description("The plan's object uuid.")] string uuid,
        [Description(ConfirmTokenHelp)] string? confirm_token = null,
        CancellationToken ct = default)
        => tripIt.RunConfirmedAsync("tripit_delete_plan", confirm_token,
            Args(("type", type), ("uuid", uuid)),
            ops => ops.PreviewDeletePlanAsync(type, uuid, ct),
            ops => ops.DeletePlanAsync(type, uuid, ct), ct);

    [McpServerTool(Name = "tripit_delete_segment", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "PERMANENTLY deletes one leg (segment) of a flight, train, transfer or cruise, keeping the rest " +
        "of the reservation. Use the segment_uuid from an itinerary line; pass the plan's type and " +
        "object_uuid too so the preview can say which leg it is." + ConfirmStep)]
    public Task<string> DeleteSegment(
        [Description("The segment uuid.")] string segmentUuid,
        [Description("The plan's type (air, rail, transport or cruise), to describe the leg.")] string? type = null,
        [Description("The plan's object uuid, to describe the leg and check it belongs to that plan.")] string? objectUuid = null,
        [Description(ConfirmTokenHelp)] string? confirm_token = null,
        CancellationToken ct = default)
        => tripIt.RunConfirmedAsync("tripit_delete_segment", confirm_token,
            Args(("segmentUuid", segmentUuid), ("type", type), ("objectUuid", objectUuid)),
            ops => ops.PreviewDeleteSegmentAsync(segmentUuid, type, objectUuid, ct),
            ops => ops.DeleteSegmentAsync(segmentUuid, ct), ct);

    [McpServerTool(Name = "tripit_move_plan", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Moves a plan to another trip, or to Unfiled Items when tripUuid is omitted. Useful for " +
        "filing items TripIt could not place automatically (see tripit_list_objects unfiledOnly).")]
    public Task<string> MovePlan(
        [Description("Plan type.")] string type,
        [Description("The plan's object uuid.")] string uuid,
        [Description("Destination trip uuid; omit to move to Unfiled Items.")] string? tripUuid = null,
        CancellationToken ct = default)
        => tripIt.RunAsync("tripit_move_plan", ops => ops.MovePlanAsync(type, uuid, tripUuid, ct), ct);

    /// <summary>The bound arguments of a confirm-step tool, after defaults, without the token.</summary>
    private static Dictionary<string, string?> Args(params (string Name, string? Value)[] arguments)
        => arguments.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);

}
