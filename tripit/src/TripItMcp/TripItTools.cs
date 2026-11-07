using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TripIt;
using TripIt.Json;
using TripIt.Models;

namespace TripItMcp;

/// <summary>
/// Read-only MCP tools over the TripIt web backend. Output is compact JSON: TripIt's raw
/// payloads are mostly empty fields and UI plumbing, so trips are summarized and each
/// trip's contents are flattened into a chronological itinerary. Pass
/// <c>include_raw=true</c> where offered for the complete (pruned) TripIt objects.
/// </summary>
[McpServerToolType]
public sealed class TripItTools(TripItClient client)
{
    internal static string ToJson<T>(T value) => JsonSerializer.Serialize(value, TripItJson.IndentedOptions);

    // ── Trips ────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_list_trips", ReadOnly = true, Idempotent = true)]
    [Description(
        "Lists the user's TripIt trips (upcoming by default, or past). Returns each trip's uuid, " +
        "name, dates, primary location and web URL, plus paging info. Use the uuid with " +
        "tripit_get_trip to see flights, hotels, cars and other plans in the trip.")]
    public async Task<string> ListTrips(
        [Description("'upcoming' (default) or 'past'.")] string timeframe = "upcoming",
        [Description("Trips per page. Default 25. TripIt accepts large values (e.g. 500).")] int pageSize = 25,
        [Description("1-based page number. Default 1.")] int pageNum = 1,
        CancellationToken ct = default)
    {
        var list = await client.Trips.ListAsync(ParseTimeframe(timeframe), pageSize, pageNum, ct).ConfigureAwait(false);
        return ToJson(new
        {
            timeframe = ParseTimeframe(timeframe).ToString().ToLowerInvariant(),
            page_num = list.PageNum,
            page_size = list.PageSize,
            max_page = list.MaxPage,
            total_items = list.TotalItems ?? list.Trips.Count,
            trips = list.Trips.Select(Summarize),
        });
    }

    [McpServerTool(Name = "tripit_search_trips", ReadOnly = true, Idempotent = true)]
    [Description(
        "Finds trips whose name, location, description or start date contains the text " +
        "(case-insensitive), searching upcoming and (optionally) past trips. Examples: 'Auckland', " +
        "'VSLive', '2025-06'. Returns trip summaries with uuids.")]
    public async Task<string> SearchTrips(
        [Description("Text to look for.")] string text,
        [Description("Also search past trips. Default true.")] bool includePast = true,
        [Description("Maximum results. Default 25.")] int maxResults = 25,
        CancellationToken ct = default)
    {
        var trips = await client.Trips.SearchAsync(text, includePast, maxResults, ct).ConfigureAwait(false);
        return ToJson(new { query = text, count = trips.Count, trips = trips.Select(Summarize) });
    }

    [McpServerTool(Name = "tripit_get_trip", ReadOnly = true, Idempotent = true)]
    [Description(
        "Gets one trip by uuid with everything in it: a chronological itinerary of flights (one " +
        "line per leg, with seats and flight status), lodging, car rentals, rail, ground " +
        "transport, cruises, restaurants, activities, notes, parking and directions — including " +
        "confirmation numbers and times with time zones. Also lists who the trip is shared with.")]
    public async Task<string> GetTrip(
        [Description("The trip uuid (from tripit_list_trips or tripit_search_trips).")] string tripUuid,
        [Description("Include the complete (pruned) TripIt objects as well as the itinerary. Default false.")]
        bool includeRaw = false,
        [Description("Include TripIt's destination weather forecasts in the itinerary. Default false.")]
        bool includeWeather = false,
        CancellationToken ct = default)
    {
        var detail = await client.Trips.GetAsync(tripUuid, includeObjects: true, ct).ConfigureAwait(false);
        return ToJson(DescribeTrip(detail, includeRaw, includeWeather));
    }

    [McpServerTool(Name = "tripit_get_upcoming_itinerary", ReadOnly = true, Idempotent = true)]
    [Description(
        "Returns every upcoming trip with its full chronological itinerary in one call — the " +
        "best starting point for questions like 'what is my next flight?', 'where am I staying " +
        "next week?' or 'what is my confirmation number for …'.")]
    public async Task<string> GetUpcomingItinerary(
        [Description("Only include trips starting within this many days (0 = no limit). Default 0.")]
        int withinDays = 0,
        [Description("Include destination weather forecasts. Default false.")] bool includeWeather = false,
        CancellationToken ct = default)
    {
        var trips = await client.Trips.ListWithObjectsAsync(TripTimeframe.Upcoming, 100, 1, ct).ConfigureAwait(false);
        var cutoff = withinDays > 0 ? DateOnly.FromDateTime(DateTime.Today.AddDays(withinDays)) : (DateOnly?)null;
        var selected = trips
            .Where(t => cutoff is null || !DateOnly.TryParse(t.Trip.StartDate, System.Globalization.CultureInfo.InvariantCulture, out var s) || s <= cutoff)
            .OrderBy(t => t.Trip.StartDate, StringComparer.Ordinal)
            .Select(t => DescribeTrip(t, includeRaw: false, includeWeather));
        return ToJson(new { trips = selected });
    }

    // ── Travel objects ───────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_list_objects", ReadOnly = true, Idempotent = true)]
    [Description(
        "Lists travel objects (reservations and plans) either in one trip or in 'Unfiled Items' " +
        "(objects TripIt could not assign to a trip). Returns them as itinerary lines. Specify " +
        "exactly one of trip_uuid or unfiled_only=true.")]
    public async Task<string> ListObjects(
        [Description("Trip uuid whose objects to list.")] string? tripUuid = null,
        [Description("List Unfiled Items instead of a trip's objects.")] bool unfiledOnly = false,
        [Description("Optional client-side type filter: air, lodging, car, rail, transport, cruise, " +
                     "restaurant, activity, note, directions, parking, map.")] string? type = null,
        [Description("Objects per page. Default 50.")] int pageSize = 50,
        [Description("1-based page number. Default 1.")] int pageNum = 1,
        [Description("Include the complete (pruned) TripIt objects. Default false.")] bool includeRaw = false,
        CancellationToken ct = default)
    {
        if ((tripUuid is null) == !unfiledOnly)
        {
            throw new ArgumentException("Specify exactly one of trip_uuid or unfiled_only=true.");
        }
        TripItObjectType? filter = type is null ? null : TripItObjectTypes.Parse(type);
        var list = await client.Objects.ListAsync(tripUuid, unfiledOnly, pageSize, pageNum, ct).ConfigureAwait(false);
        var objects = filter is { } f
            ? list.Objects.Where(kv => kv.Key == f).ToDictionary(kv => kv.Key, kv => kv.Value)
            : list.Objects.ToDictionary(kv => kv.Key, kv => kv.Value);
        return ToJson(new
        {
            page_num = list.PageNum,
            page_size = list.PageSize,
            max_page = list.MaxPage,
            total_items = list.TotalItems,
            note = filter is null ? null : "type filter applies to this page only; TripIt pages over all types.",
            itinerary = ItineraryBuilder.Build(objects, includeWeather: true),
            raw = includeRaw ? RawObjects(objects) : null,
        });
    }

    [McpServerTool(Name = "tripit_get_object", ReadOnly = true, Idempotent = true)]
    [Description(
        "Gets one travel object (a flight reservation, hotel stay, car rental, …) by type and " +
        "object uuid, with every field TripIt holds for it (empty fields removed). Use the " +
        "object_uuid from an itinerary line, not the segment_uuid.")]
    public async Task<string> GetObject(
        [Description("Object type: air, lodging, car, rail, transport, cruise, restaurant, activity, " +
                     "note, directions, parking, map.")] string type,
        [Description("The object uuid.")] string uuid,
        CancellationToken ct = default)
    {
        var t = TripItObjectTypes.Parse(type);
        var obj = await client.Objects.GetAsync(t, uuid, ct).ConfigureAwait(false);
        return ToJson(new
        {
            type = t.ToPathSegment(),
            itinerary = ItineraryBuilder.FromObject(t, obj).ToList(),
            @object = ItineraryBuilder.Compact(obj),
        });
    }

    // ── Account ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_get_profile", ReadOnly = true, Idempotent = true)]
    [Description(
        "Gets the signed-in user's TripIt profile: name, screen name, home city, company, email " +
        "addresses, and similar account attributes.")]
    public async Task<string> GetProfile(CancellationToken ct = default)
    {
        using var doc = await client.GetJsonAsync("/api/v2/get/profile", null, ct).ConfigureAwait(false);
        var profile = TripItJson.AsList(doc.RootElement, "Profile").FirstOrDefault();
        return ToJson(profile.ValueKind == JsonValueKind.Object ? TripItJson.Prune(profile) : null);
    }

    [McpServerTool(Name = "tripit_get_traveler_profile", ReadOnly = true, Idempotent = true)]
    [Description(
        "Gets the user's traveler profile: loyalty/frequent-traveler programs, known traveler " +
        "and redress numbers, passport and license details, and seat/meal preferences as stored " +
        "in TripIt. Identity-document and membership numbers are masked to their last 4 " +
        "characters unless reveal_numbers=true.")]
    public async Task<string> GetTravelerProfile(
        [Description("Return full document/membership numbers instead of masked ones. Default false.")]
        bool revealNumbers = false,
        CancellationToken ct = default)
    {
        using var doc = await client.GetJsonAsync("/api/v2/travelerProfile/get",
            [new("exclude_templates", "true")], ct).ConfigureAwait(false);
        var pruned = TripItJson.Prune(doc.RootElement);
        if (!revealNumbers) MaskSensitiveNumbers(pruned);
        return ToJson(pruned);
    }

    /// <summary>Field names whose values are identity-document or account numbers.</summary>
    internal static readonly HashSet<string> SensitiveNumberFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "document_number", "account_number", "membership_number", "number", "known_traveler_number",
        "redress_number", "passport_number", "license_number", "card_number",
    };

    /// <summary>Masks sensitive number fields in place, keeping the last 4 characters.</summary>
    internal static void MaskSensitiveNumbers(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (SensitiveNumberFields.Contains(key) && obj[key] is JsonValue v &&
                        v.TryGetValue<string>(out var s) && s.Length > 0)
                    {
                        obj[key] = s.Length <= 4 ? new string('*', s.Length) : new string('*', s.Length - 4) + s[^4..];
                    }
                    else
                    {
                        MaskSensitiveNumbers(obj[key]);
                    }
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) MaskSensitiveNumbers(item);
                break;
        }
    }

    [McpServerTool(Name = "tripit_list_alerts", ReadOnly = true, Idempotent = true)]
    [Description(
        "Lists TripIt Pro alerts and account notices (flight changes, check-in reminders, " +
        "premium/account alerts), with the count of new alerts.")]
    public async Task<string> ListAlerts(CancellationToken ct = default)
    {
        using var doc = await client.GetJsonAsync("/api/v2/listProAlerts", null, ct).ConfigureAwait(false);
        return ToJson(TripItJson.Prune(doc.RootElement));
    }

    [McpServerTool(Name = "tripit_get_purchased_products", ReadOnly = true, Idempotent = true)]
    [Description("Gets the user's TripIt subscription status (Pro tier, entitlements, expiry).")]
    public async Task<string> GetPurchasedProducts(CancellationToken ct = default)
    {
        using var doc = await client.GetJsonAsync("/api/v2/purchasedProductInfo", null, ct).ConfigureAwait(false);
        return ToJson(TripItJson.Prune(doc.RootElement));
    }

    // ── Lookups ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_lookup", ReadOnly = true, Idempotent = true)]
    [Description(
        "Looks up reference data with TripIt's own autocomplete: airports (id = IATA code), " +
        "airlines (id = IATA code), places/cities, countries (ISO code), states, or street " +
        "addresses. Useful for resolving codes before creating or interpreting plans.")]
    public async Task<string> Lookup(
        [Description("One of: airport, airline, place, country, state, address.")] string kind,
        [Description("Search text, e.g. 'SEA', 'Alaska', 'Paris'.")] string query,
        CancellationToken ct = default)
    {
        var results = kind.Trim().ToLowerInvariant() switch
        {
            "airport" or "airports" => await client.Lookup.AirportsAsync(query, ct).ConfigureAwait(false),
            "airline" or "airlines" => await client.Lookup.AirlinesAsync(query, ct).ConfigureAwait(false),
            "place" or "places" or "city" => await client.Lookup.PlacesAsync(query, ct).ConfigureAwait(false),
            "country" or "countries" => await client.Lookup.CountriesAsync(query, ct).ConfigureAwait(false),
            "state" or "states" => await client.Lookup.StatesAsync(query, ct).ConfigureAwait(false),
            "address" or "addresses" => await client.Lookup.AddressesAsync(query, ct).ConfigureAwait(false),
            _ => throw new ArgumentException("kind must be one of: airport, airline, place, country, state, address."),
        };
        return ToJson(results.Select(r => new { id = r.Id, label = r.Label }));
    }

    // ── Diagnostics / escape hatch ───────────────────────────────────────────

    [McpServerTool(Name = "tripit_session_status", ReadOnly = true, Idempotent = true)]
    [Description(
        "Reports whether the server has a TripIt session, where it was loaded from, whether it " +
        "can refresh it automatically, and whether TripIt currently accepts it. Never returns " +
        "cookie values.")]
    public async Task<string> SessionStatus(CancellationToken ct = default)
    {
        string state;
        try
        {
            await client.SendRawAsync("/api/v2/get/profile", null, ct).ConfigureAwait(false);
            state = "valid";
        }
        catch (TripItAuthenticationException ex)
        {
            state = "rejected: " + ex.Message;
        }
        catch (TripItApiException ex)
        {
            state = "error: " + ex.Message;
        }
        return ToJson(new
        {
            configured = client.IsConfigured,
            source = client.SessionSource,
            auto_refresh = client.CanRefreshSession,
            state,
        });
    }

    [McpServerTool(Name = "tripit_raw_get", ReadOnly = true)]
    [Description(
        "Escape hatch: authenticated GET against a TripIt web API path (must start with " +
        "'/api/v2/') returning the raw JSON. Use only when no dedicated tool covers the data.")]
    public Task<string> RawGet(
        [Description("Path beginning with '/api/v2/', e.g. '/api/v2/list/trip'.")] string path,
        [Description("Optional query parameters as a JSON object, e.g. {\"past\":\"true\"}.")] string? queryJson = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/api/v2/", StringComparison.Ordinal))
        {
            throw new ArgumentException("path must start with '/api/v2/'.");
        }
        List<KeyValuePair<string, string?>>? q = null;
        if (!string.IsNullOrWhiteSpace(queryJson))
        {
            using var doc = JsonDocument.Parse(queryJson);
            q = [.. doc.RootElement.EnumerateObject().Select(p => new KeyValuePair<string, string?>(
                p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString()))];
        }
        return client.SendRawAsync(path, q, ct);
    }

    // ── Shaping helpers ──────────────────────────────────────────────────────

    internal static object Summarize(Trip t) => new
    {
        uuid = t.Uuid,
        name = t.DisplayName,
        start_date = t.StartDate,
        end_date = t.EndDate,
        location = t.PrimaryLocation,
        description = t.Description,
        is_private = t.IsPrivate,
        url = t.WebUrl,
    };

    internal static object DescribeTrip(TripDetail detail, bool includeRaw, bool includeWeather)
    {
        var trip = detail.Trip;
        var sharedWith = TripItJson.AsList(trip.AdditionalData.TryGetValue("TripInvitees", out var inv)
                ? inv : default, "Invitee")
            .Select(i => new
            {
                is_owner = TripItJson.Bool(i, "is_owner") ?? false,
                is_traveler = TripItJson.Bool(i, "is_traveler") ?? false,
                is_read_only = TripItJson.Bool(i, "is_read_only") ?? false,
                name = ProfileName(detail.Profiles,
                    TripItJson.Obj(i, "@attributes") is { } a ? TripItJson.Str(a, "profile_ref") : null),
            })
            .ToList();
        return new
        {
            trip = Summarize(trip),
            object_count = detail.ObjectCount,
            shared_with = sharedWith.Count == 0 ? null : sharedWith,
            itinerary = ItineraryBuilder.Build(detail.Objects, includeWeather),
            raw = includeRaw ? RawObjects(detail.Objects) : null,
        };
    }

    private static string? ProfileName(IReadOnlyList<JsonElement> profiles, string? profileRef)
    {
        if (profileRef is null) return null;
        foreach (var p in profiles)
        {
            var attrs = TripItJson.Obj(p, "@attributes");
            if (attrs is { } a && TripItJson.Str(a, "ref") == profileRef)
            {
                return TripItJson.Str(p, "public_display_name") ?? TripItJson.Str(p, "screen_name");
            }
        }
        return null;
    }

    private static Dictionary<string, JsonNode?> RawObjects(
        IReadOnlyDictionary<TripItObjectType, IReadOnlyList<JsonElement>> objects)
        => objects.ToDictionary(
            kv => kv.Key.ToPathSegment(),
            kv => (JsonNode?)new JsonArray([.. kv.Value.Select(o => ItineraryBuilder.Compact(o))]));

    private static TripTimeframe ParseTimeframe(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "upcoming" or "future" => TripTimeframe.Upcoming,
        "past" => TripTimeframe.Past,
        _ => throw new ArgumentException("timeframe must be 'upcoming' or 'past'."),
    };
}
