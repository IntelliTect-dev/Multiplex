using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TripIt;
using TripIt.Models;
using TripIt.Tools;

namespace TripItMcp;

/// <summary>
/// Read-only MCP tools over the TripIt web backend. The behaviour lives in
/// <see cref="TripItOperations"/> (shared with the Multiplex-hosted server); this class adds
/// names, descriptions and annotations for the stdio server.
/// </summary>
[McpServerToolType]
public sealed class TripItTools(TripItClient client)
{
    private readonly TripItOperations _ops = new(client);

    internal static string ToJson<T>(T value) => TripItOperations.ToJson(value);

    // ── Trips ────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_list_trips", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Lists the user's TripIt trips (upcoming by default, or past) — including trips they are on " +
        "but not travelling on, unless traveler='mine'. Returns each trip's uuid, name, dates, primary " +
        "location, web URL and my_role (traveler / editor / viewer, owner or not), plus paging info. " +
        "Use the uuid with tripit_get_trip to see flights, hotels, cars and other plans in the trip.")]
    public Task<string> ListTrips(
        [Description("'upcoming' (default) or 'past'.")] string timeframe = "upcoming",
        [Description("Trips per page. Default 25. TripIt accepts large values (e.g. 500).")] int pageSize = 25,
        [Description("1-based page number. Default 1.")] int pageNum = 1,
        [Description("Whose trips: 'all' (default) — every trip I can see; 'mine' — trips I travel on; " +
                     "'others' — trips I plan, follow or edit but don't travel on.")] string traveler = "all",
        CancellationToken ct = default)
        => _ops.ListTripsAsync(timeframe, pageSize, pageNum, traveler, ct);

    [McpServerTool(Name = "tripit_search_trips", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Finds trips whose name, location, description or start date contains the text " +
        "(case-insensitive), searching upcoming and (optionally) past trips. Examples: 'Auckland', " +
        "'VSLive', '2025-06'. Returns trip summaries with uuids.")]
    public Task<string> SearchTrips(
        [Description("Text to look for.")] string text,
        [Description("Also search past trips. Default true.")] bool includePast = true,
        [Description("Maximum results. Default 25.")] int maxResults = 25,
        [Description("Whose trips: 'all' (default) — every trip I can see; 'mine' — trips I travel on; " +
                     "'others' — trips I plan, follow or edit but don't travel on.")] string traveler = "all",
        CancellationToken ct = default)
        => _ops.SearchTripsAsync(text, includePast, maxResults, traveler, ct);

    [McpServerTool(Name = "tripit_get_trip", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Gets one trip by uuid with everything in it: a chronological itinerary of flights (one " +
        "line per leg, with seats and flight status), lodging, car rentals, rail, ground " +
        "transport, cruises, restaurants, activities, notes, parking and directions — including " +
        "confirmation numbers and times with time zones. Also lists who the trip is shared with.")]
    public Task<string> GetTrip(
        [Description("The trip uuid (from tripit_list_trips or tripit_search_trips).")] string tripUuid,
        [Description("Include the complete (pruned) TripIt objects as well as the itinerary. Default false.")]
        bool includeRaw = false,
        [Description("Include TripIt's typical weather for the destination (historical climate averages: " +
                     "average high/low and precipitation for the dates, not a forecast). Default false.")]
        bool includeWeather = false,
        CancellationToken ct = default)
        => _ops.GetTripAsync(tripUuid, includeRaw, includeWeather, ct);

    [McpServerTool(Name = "tripit_get_upcoming_itinerary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Returns every upcoming trip with its full chronological itinerary in one call — the " +
        "best starting point for questions like 'what is my next flight?', 'where am I staying " +
        "next week?' or 'what is my confirmation number for …'.")]
    public Task<string> GetUpcomingItinerary(
        [Description("Only include trips starting within this many days (0 = no limit). Default 0.")]
        int withinDays = 0,
        [Description("Include typical destination weather (historical climate averages, not forecasts). Default false.")]
        bool includeWeather = false,
        [Description("Whose trips: 'all' (default) — every trip I can see; 'mine' — trips I travel on; " +
                     "'others' — trips I plan, follow or edit but don't travel on.")] string traveler = "all",
        CancellationToken ct = default)
        => _ops.GetUpcomingItineraryAsync(withinDays, includeWeather, traveler, ct);

    // ── Travel objects ───────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_list_objects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Lists travel objects (reservations and plans) either in one trip or in 'Unfiled Items' " +
        "(objects TripIt could not assign to a trip). Returns them as itinerary lines. Specify " +
        "exactly one of trip_uuid or unfiled_only=true.")]
    public Task<string> ListObjects(
        [Description("Trip uuid whose objects to list.")] string? tripUuid = null,
        [Description("List Unfiled Items instead of a trip's objects.")] bool unfiledOnly = false,
        [Description("Optional client-side type filter: air, lodging, car, rail, transport, cruise, " +
                     "restaurant, activity, note, directions, parking, map.")] string? type = null,
        [Description("Objects per page. Default 50.")] int pageSize = 50,
        [Description("1-based page number. Default 1.")] int pageNum = 1,
        [Description("Include the complete (pruned) TripIt objects. Default false.")] bool includeRaw = false,
        CancellationToken ct = default)
        => _ops.ListObjectsAsync(tripUuid, unfiledOnly, type, pageSize, pageNum, includeRaw, ct);

    [McpServerTool(Name = "tripit_get_object", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Gets one travel object (a flight reservation, hotel stay, car rental, …) by type and " +
        "object uuid, with every field TripIt holds for it (empty fields removed). Use the " +
        "object_uuid from an itinerary line, not the segment_uuid.")]
    public Task<string> GetObject(
        [Description("Object type: air, lodging, car, rail, transport, cruise, restaurant, activity, " +
                     "note, directions, parking, map.")] string type,
        [Description("The object uuid.")] string uuid,
        CancellationToken ct = default)
        => _ops.GetObjectAsync(type, uuid, ct);

    // ── Account ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_get_profile", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Gets the signed-in user's TripIt profile: name, screen name, home city, company, email " +
        "addresses, and similar account attributes.")]
    public Task<string> GetProfile(CancellationToken ct = default) => _ops.GetProfileAsync(ct);

    [McpServerTool(Name = "tripit_get_traveler_profile", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Gets the user's traveler profile: loyalty/frequent-traveler programs, known traveler " +
        "and redress numbers, passport and license details, and seat/meal preferences as stored " +
        "in TripIt. Identity-document and membership numbers are masked to their last 4 " +
        "characters unless reveal_numbers=true.")]
    public Task<string> GetTravelerProfile(
        [Description("Return full document/membership numbers instead of masked ones. Default false.")]
        bool revealNumbers = false,
        CancellationToken ct = default)
        => _ops.GetTravelerProfileAsync(revealNumbers, ct);

    /// <summary>Masks sensitive number fields in place, keeping the last 4 characters.</summary>
    internal static void MaskSensitiveNumbers(JsonNode? node) => TripItOperations.MaskSensitiveNumbers(node);

    [McpServerTool(Name = "tripit_list_alerts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Lists TripIt Pro alerts and account notices (flight changes, check-in reminders, " +
        "premium/account alerts), with the count of new alerts.")]
    public Task<string> ListAlerts(CancellationToken ct = default) => _ops.ListAlertsAsync(ct);

    [McpServerTool(Name = "tripit_get_purchased_products", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Gets the user's TripIt subscription status (Pro tier, entitlements, expiry).")]
    public Task<string> GetPurchasedProducts(CancellationToken ct = default) => _ops.GetPurchasedProductsAsync(ct);

    // ── Lookups and reference (read-only) ────────────────────────────────────

    [McpServerTool(Name = "tripit_lookup", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Looks up reference data with TripIt's own autocomplete: airports (id = IATA code), " +
        "airlines (id = IATA code), places/cities, countries (ISO code), states, or street " +
        "addresses. Useful for resolving codes before creating or interpreting plans.")]
    public Task<string> Lookup(
        [Description("One of: airport, airline, place, country, state, address.")] string kind,
        [Description("Search text, e.g. 'SEA', 'Alaska', 'Paris'.")] string query,
        CancellationToken ct = default)
        => _ops.LookupAsync(kind, query, ct);

    [McpServerTool(Name = "tripit_lookup_flight", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Looks up a scheduled flight by airline code, flight number and date: origin and destination " +
        "airports and local departure/arrival times.")]
    public Task<string> LookupFlight(
        [Description("IATA airline code, e.g. 'FI'.")] string airlineCode,
        [Description("Flight number, e.g. '680'.")] string flightNumber,
        [Description("Departure date, yyyy-MM-dd.")] string date,
        CancellationToken ct = default)
        => _ops.LookupFlightAsync(airlineCode, flightNumber, date, ct);

    [McpServerTool(Name = "tripit_describe_plan_fields", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Lists the fields TripIt accepts for a plan type (for tripit_add_plan / tripit_update_plan), " +
        "including nested shapes for dates, addresses, agency and travelers.")]
    public string DescribePlanFields(
        [Description("air, lodging, car, rail, transport, cruise, restaurant, activity, note, directions, parking or map.")]
        string type)
        => TripItOperations.DescribePlanFields(type);

    // ── Diagnostics ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_session_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Reports whether the server has a TripIt session, where it was loaded from, whether it " +
        "can refresh it automatically, and whether TripIt currently accepts it. Never returns " +
        "cookie values.")]
    public Task<string> SessionStatus(CancellationToken ct = default) => _ops.SessionStatusAsync(client.SessionSource, ct);

    // ── Shaping helpers (kept for callers of the previous API) ───────────────

    internal static object Summarize(Trip t, string? me = null) => TripItOperations.Summarize(t, me);

    internal static object DescribeTrip(TripDetail detail, bool includeRaw, bool includeWeather, string? me = null)
        => TripItOperations.DescribeTrip(detail, includeRaw, includeWeather, me);
}
