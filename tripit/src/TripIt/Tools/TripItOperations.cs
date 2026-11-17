using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TripIt.Json;
using TripIt.Models;

namespace TripIt.Tools;

/// <summary>
/// The behaviour behind every TripIt MCP tool, shared by the stdio server (TripItMcp) and the
/// hosted server (Multiplex <c>/tripit/mcp</c>). Each method returns the tool's compact JSON
/// text and throws <see cref="ArgumentException"/> for bad input; the servers add names,
/// descriptions, annotations and error mapping.
/// </summary>
/// <remarks>
/// Output is compact JSON: TripIt's raw payloads are mostly empty fields and UI plumbing, so
/// trips are summarized and each trip's contents are flattened into a chronological itinerary.
/// </remarks>
public sealed class TripItOperations(TripItClient client)
{
    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, TripItJson.IndentedOptions);

    // ── Trips ────────────────────────────────────────────────────────────────

    public async Task<string> ListTripsAsync(string timeframe, int pageSize, int pageNum, string traveler, CancellationToken ct)
    {
        var tf = ParseTimeframe(timeframe);
        var list = await client.Trips.ListAsync(tf, pageSize, pageNum, ct, TripsClient.ParseTravelerFilter(traveler))
            .ConfigureAwait(false);
        var me = await client.GetMyProfileRefAsync(ct).ConfigureAwait(false);
        return ToJson(new
        {
            timeframe = tf.ToString().ToLowerInvariant(),
            page_num = list.PageNum,
            page_size = list.PageSize,
            max_page = list.MaxPage,
            total_items = list.TotalItems ?? list.Trips.Count,
            trips = list.Trips.Select(t => Summarize(t, me)),
        });
    }

    public async Task<string> SearchTripsAsync(string text, bool includePast, int maxResults, string traveler, CancellationToken ct)
    {
        var trips = await client.Trips.SearchAsync(text, includePast, maxResults, ct, TripsClient.ParseTravelerFilter(traveler))
            .ConfigureAwait(false);
        var me = await client.GetMyProfileRefAsync(ct).ConfigureAwait(false);
        return ToJson(new { query = text, count = trips.Count, trips = trips.Select(t => Summarize(t, me)) });
    }

    public async Task<string> GetTripAsync(string tripUuid, bool includeRaw, bool includeWeather, CancellationToken ct)
    {
        var detail = await client.Trips.GetAsync(tripUuid, includeObjects: true, ct).ConfigureAwait(false);
        var me = await client.GetMyProfileRefAsync(ct).ConfigureAwait(false);
        return ToJson(DescribeTrip(detail, includeRaw, includeWeather, me));
    }

    public async Task<string> GetUpcomingItineraryAsync(int withinDays, bool includeWeather, string traveler, CancellationToken ct)
    {
        var trips = await client.Trips.ListWithObjectsAsync(TripTimeframe.Upcoming, 100, 1, ct,
            TripsClient.ParseTravelerFilter(traveler)).ConfigureAwait(false);
        var me = await client.GetMyProfileRefAsync(ct).ConfigureAwait(false);
        var cutoff = withinDays > 0 ? DateOnly.FromDateTime(DateTime.Today.AddDays(withinDays)) : (DateOnly?)null;
        var selected = trips
            .Where(t => cutoff is null || !DateOnly.TryParse(t.Trip.StartDate, CultureInfo.InvariantCulture, out var s) || s <= cutoff)
            .OrderBy(t => t.Trip.StartDate, StringComparer.Ordinal)
            .Select(t => DescribeTrip(t, includeRaw: false, includeWeather, me));
        return ToJson(new { trips = selected });
    }

    // ── Travel objects ───────────────────────────────────────────────────────

    public async Task<string> ListObjectsAsync(string? tripUuid, bool unfiledOnly, string? type, int pageSize, int pageNum,
        bool includeRaw, CancellationToken ct)
    {
        if ((tripUuid is null) == !unfiledOnly)
        {
            throw new ArgumentException("Specify exactly one of a trip uuid or unfiled-only.");
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

    public async Task<string> GetObjectAsync(string type, string uuid, CancellationToken ct)
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

    public async Task<string> GetProfileAsync(CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync("/api/v2/get/profile", null, ct).ConfigureAwait(false);
        var profile = TripItJson.AsList(doc.RootElement, "Profile").FirstOrDefault();
        return ToJson(profile.ValueKind == JsonValueKind.Object ? TripItJson.Prune(profile) : null);
    }

    public async Task<string> GetTravelerProfileAsync(bool revealNumbers, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync("/api/v2/travelerProfile/get", [new("exclude_templates", "true")], ct)
            .ConfigureAwait(false);
        var pruned = TripItJson.Prune(doc.RootElement);
        if (!revealNumbers) MaskSensitiveNumbers(pruned);
        return ToJson(pruned);
    }

    /// <summary>Field names whose values are identity-document or account numbers.</summary>
    public static readonly IReadOnlySet<string> SensitiveNumberFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "document_number", "account_number", "membership_number", "number", "known_traveler_number",
        "redress_number", "passport_number", "license_number", "card_number",
    };

    /// <summary>Masks sensitive number fields in place, keeping the last 4 characters.</summary>
    public static void MaskSensitiveNumbers(JsonNode? node)
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

    public async Task<string> ListAlertsAsync(CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync("/api/v2/listProAlerts", null, ct).ConfigureAwait(false);
        return ToJson(TripItJson.Prune(doc.RootElement));
    }

    public async Task<string> GetPurchasedProductsAsync(CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync("/api/v2/purchasedProductInfo", null, ct).ConfigureAwait(false);
        return ToJson(TripItJson.Prune(doc.RootElement));
    }

    // ── Lookups ──────────────────────────────────────────────────────────────

    public async Task<string> LookupAsync(string kind, string query, CancellationToken ct)
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

    public async Task<string> LookupFlightAsync(string airlineCode, string flightNumber, string date, CancellationToken ct)
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

    public static string DescribePlanFields(string type)
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
                "Dates are yyyy-MM-dd (no other form is accepted) and times HH:mm, local to the place: {\"date\":\"2027-01-18\",\"time\":\"15:00\"}.",
                "timezone (IANA, e.g. Atlantic/Reykjavik) is optional; TripIt infers it from airports/addresses.",
                "Segmented types (air, rail, transport, cruise) take a Segment array, one entry per leg/port.",
                "When updating a Segment array, name every existing leg by its uuid (legs left out would be deleted).",
                "supplier_conf_num is the confirmation number; supplier_name the hotel/company/restaurant name.",
            },
        });
    }

    /// <summary>Whether the session is configured and accepted. Never returns cookie values.</summary>
    public async Task<string> SessionStatusAsync(string source, CancellationToken ct)
    {
        string state;
        try
        {
            await client.SendRawAsync("/api/v2/get/profile", null, ct).ConfigureAwait(false);
            state = "valid";
        }
        catch (TripItAuthenticationException)
        {
            state = "rejected: TripIt did not accept the session (expired or signed out).";
        }
        catch (TripItApiException ex)
        {
            state = "error: " + TripItErrorText.Describe(ex);
        }
        return ToJson(new
        {
            configured = client.IsConfigured,
            source,
            auto_refresh = client.CanRefreshSession,
            captured_at = client.SessionCapturedAt?.ToString("o", CultureInfo.InvariantCulture),
            state,
        });
    }

    // ── Trip writes ──────────────────────────────────────────────────────────

    public async Task<string> CreateTripAsync(string destination, string startDate, string endDate, string? name,
        string? description, bool shareWithInnerCircle, string purpose, bool iAmTraveling, CancellationToken ct)
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
        return ToJson(new { created = Summarize(trip, me) });
    }

    public async Task<string> UpdateTripAsync(string tripUuid, string? name, string? destination, string? startDate,
        string? endDate, string? description, bool? shareWithInnerCircle, string? purpose, bool? iAmTraveling,
        CancellationToken ct)
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
        return ToJson(new { updated = Summarize(trip, me) });
    }

    /// <summary>Checks <paramref name="confirmName"/> and describes what deleting the trip removes. Changes nothing.</summary>
    public async Task<string> PreviewDeleteTripAsync(string tripUuid, string confirmName, CancellationToken ct)
    {
        var current = await TripNamedAsync(tripUuid, confirmName, "confirm_name", "deleted", ct).ConfigureAwait(false);
        return $"Will permanently delete trip '{current.Trip.DisplayName}' ({Dates(current.Trip)}) and its " +
               $"{Plans(current.ObjectCount)}.";
    }

    public async Task<string> DeleteTripAsync(string tripUuid, string confirmName, CancellationToken ct)
    {
        var current = await TripNamedAsync(tripUuid, confirmName, "confirm_name", "deleted", ct).ConfigureAwait(false);
        await client.Trips.DeleteAsync(tripUuid, ct).ConfigureAwait(false);
        return ToJson(new { deleted = Summarize(current.Trip), plans_deleted = current.ObjectCount });
    }

    public async Task<string> PreviewMergeTripsAsync(string sourceTripUuid, string destinationTripUuid,
        string confirmSourceName, CancellationToken ct)
    {
        var source = await TripNamedAsync(sourceTripUuid, confirmSourceName, "confirm_source_name", "merged", ct)
            .ConfigureAwait(false);
        var into = await client.Trips.GetAsync(destinationTripUuid, includeObjects: false, ct).ConfigureAwait(false);
        return $"Will move {Plans(source.ObjectCount)} from trip '{source.Trip.DisplayName}' ({Dates(source.Trip)}) into " +
               $"trip '{into.Trip.DisplayName}' ({Dates(into.Trip)}), then delete trip '{source.Trip.DisplayName}'.";
    }

    public async Task<string> MergeTripsAsync(string sourceTripUuid, string destinationTripUuid, string confirmSourceName,
        CancellationToken ct)
    {
        var source = await TripNamedAsync(sourceTripUuid, confirmSourceName, "confirm_source_name", "merged", ct)
            .ConfigureAwait(false);
        var merged = await client.Trips.MergeAsync(sourceTripUuid, destinationTripUuid, ct).ConfigureAwait(false);
        var me = await client.GetMyProfileRefAsync(ct).ConfigureAwait(false);
        return ToJson(new
        {
            merged_from = Summarize(source.Trip, me),
            plans_moved = source.ObjectCount,
            into = DescribeTrip(merged, includeRaw: false, includeWeather: false, me),
        });
    }

    private async Task<TripDetail> TripNamedAsync(string tripUuid, string confirmName, string parameter, string verb,
        CancellationToken ct)
    {
        var current = await client.Trips.GetAsync(tripUuid, includeObjects: true, ct).ConfigureAwait(false);
        if (!string.Equals(current.Trip.DisplayName, confirmName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{parameter} does not match. The trip is named '{current.Trip.DisplayName}'. Nothing was {verb}.");
        }
        return current;
    }

    // ── Participants ─────────────────────────────────────────────────────────

    public static IReadOnlyList<string> SplitEmails(string emails)
        => emails.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<string> PreviewAddParticipantsAsync(string tripUuid, string emails, string role, string? message,
        CancellationToken ct)
    {
        var list = SplitEmails(emails);
        var r = TripsClient.ParseRole(role);
        if (list.Count == 0) throw new ArgumentException("At least one email address is required.");
        foreach (var e in list)
        {
            if (!System.Net.Mail.MailAddress.TryCreate(e, out _)) throw new ArgumentException($"'{e}' is not an email address.");
        }
        var trip = await client.Trips.GetAsync(tripUuid, includeObjects: false, ct).ConfigureAwait(false);
        return $"TripIt will email an invitation to {string.Join(", ", list)} to join trip '{trip.Trip.DisplayName}' " +
               $"({Dates(trip.Trip)}) as {r.ToString().ToLowerInvariant()}" +
               (string.IsNullOrWhiteSpace(message) ? "." : $", with the message \"{message}\".");
    }

    public async Task<string> AddParticipantsAsync(string tripUuid, string emails, string role, string? message,
        CancellationToken ct)
    {
        var list = SplitEmails(emails);
        var r = TripsClient.ParseRole(role);
        await client.Trips.InviteAsync(tripUuid, list, r, message, ct).ConfigureAwait(false);
        return await ParticipantsAsync(tripUuid, new { invited = list, role = r.ToString().ToLowerInvariant() }, ct)
            .ConfigureAwait(false);
    }

    public async Task<string> ChangeParticipantRoleAsync(string tripUuid, string profileRef, string role, CancellationToken ct)
    {
        var r = TripsClient.ParseRole(role);
        await client.Trips.ChangeRoleAsync(tripUuid, profileRef, r, ct).ConfigureAwait(false);
        return await ParticipantsAsync(tripUuid, new { changed = profileRef, role = r.ToString().ToLowerInvariant() }, ct)
            .ConfigureAwait(false);
    }

    public async Task<string> PreviewRemoveParticipantAsync(string tripUuid, string profileRef, CancellationToken ct)
    {
        var (detail, target) = await RemovableParticipantAsync(tripUuid, profileRef, ct).ConfigureAwait(false);
        return $"Will remove {target.Name ?? target.ScreenName ?? profileRef} ({target.Role.ToString().ToLowerInvariant()}) " +
               $"from trip '{detail.Trip.DisplayName}' ({Dates(detail.Trip)}); they will no longer see it.";
    }

    public async Task<string> RemoveParticipantAsync(string tripUuid, string profileRef, CancellationToken ct)
    {
        var (_, target) = await RemovableParticipantAsync(tripUuid, profileRef, ct).ConfigureAwait(false);
        await client.Trips.RemoveParticipantAsync(tripUuid, profileRef, ct).ConfigureAwait(false);
        return await ParticipantsAsync(tripUuid, new { removed = target.Name ?? target.ScreenName ?? profileRef }, ct)
            .ConfigureAwait(false);
    }

    private async Task<(TripDetail Detail, TripParticipant Target)> RemovableParticipantAsync(string tripUuid,
        string profileRef, CancellationToken ct)
    {
        var me = await client.GetMyProfileRefAsync(ct).ConfigureAwait(false);
        var detail = await client.Trips.GetAsync(tripUuid, includeObjects: false, ct).ConfigureAwait(false);
        var target = TripsClient.Participants(detail, me).FirstOrDefault(p => p.ProfileRef == profileRef)
                     ?? throw new ArgumentException($"{profileRef} is not a participant of trip {tripUuid}.");
        if (target.IsOwner) throw new ArgumentException("The trip owner cannot be removed. Delete the trip instead.");
        return (detail, target);
    }

    private async Task<string> ParticipantsAsync(string tripUuid, object action, CancellationToken ct)
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

    // ── Plans ────────────────────────────────────────────────────────────────

    public async Task<string> AddFlightAsync(string legsJson, string? tripUuid, string? confirmation, string? bookingSite,
        string? totalCost, string? travelersJson, string? notes, CancellationToken ct)
    {
        var legs = ParseArray(legsJson, "legs_json");
        if (legs.Count == 0) throw new ArgumentException("legs_json must contain at least one leg.");
        var segments = new JsonArray();
        var looked = new List<object>();
        var assumptions = new List<string>();
        foreach (var leg in legs.OfType<JsonObject>())
        {
            string? S(string k) => leg[k]?.GetValue<object>()?.ToString();
            var airline = S("airline_code") ?? throw new ArgumentException("Each leg needs airline_code.");
            var number = S("flight_number") ?? throw new ArgumentException("Each leg needs flight_number.");
            var date = TripItWrite.NormalizeDate(S("date") ?? throw new ArgumentException("Each leg needs date (yyyy-MM-dd)."))!;
            string? from = S("from"), to = S("to"), departTime = S("depart_time"), arriveDate = S("arrive_date"), arriveTime = S("arrive_time");
            var flight = $"{airline.ToUpperInvariant()} {number}";
            var lookupFailed = false;
            if (from is null || to is null || departTime is null || arriveTime is null || arriveDate is null)
            {
                var sched = await client.Lookup.FlightAsync(airline, number, date, ct).ConfigureAwait(false);
                if (sched is not null)
                {
                    from ??= sched.OriginAirportCode;
                    to ??= sched.DestinationAirportCode;
                    departTime ??= sched.DepartureTime;
                    arriveTime ??= sched.ArrivalTime;
                    arriveDate ??= sched.ArrivalDate;
                    looked.Add(new { flight, from, to, departTime, arriveDate, arriveTime });
                }
                else if (from is null || to is null)
                {
                    throw new ArgumentException(
                        $"TripIt has no schedule for {flight} on {date}; give 'from' and 'to' airport codes.");
                }
                else
                {
                    lookupFailed = true;
                }
            }
            if (arriveDate is null)
            {
                arriveDate = date;
                assumptions.Add(
                    $"{flight}: the arrival date is unknown ({(lookupFailed ? "TripIt has no schedule for this flight" : "no arrive_date was given")}), " +
                    $"so it was recorded as arriving on the departure date {date}. If it lands on a later day, update the leg's EndDateTime.");
            }
            if (lookupFailed && (departTime is null || arriveTime is null))
            {
                assumptions.Add($"{flight}: TripIt has no schedule for this flight, so missing times were left empty.");
            }
            var seg = new JsonObject
            {
                ["marketing_airline_code"] = airline.ToUpperInvariant(),
                ["marketing_flight_number"] = number,
                ["start_airport_code"] = from!.ToUpperInvariant(),
                ["end_airport_code"] = to!.ToUpperInvariant(),
                ["StartDateTime"] = Dt(date, departTime),
                ["EndDateTime"] = Dt(arriveDate, arriveTime),
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
        return Describe(result, looked.Count == 0 ? null : looked, assumptions);
    }

    public async Task<string> AddLodgingAsync(string name, string checkInDate, string checkOutDate, string? tripUuid,
        string? checkInTime, string? checkOutTime, string? address, string? confirmation, string? phone, string? roomType,
        int? guests, string? totalCost, string? notes, CancellationToken ct)
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
        if (guests is { } g) fields["number_guests"] = g.ToString(CultureInfo.InvariantCulture);
        if (address is not null) fields["Address"] = new JsonObject { ["address"] = address };
        var result = await client.Objects.CreateAsync(TripItObjectType.Lodging, fields, tripUuid, ct).ConfigureAwait(false);
        return Describe(result);
    }

    public async Task<string> AddNoteAsync(string tripUuid, string title, string? text, string? date, string? time,
        string? address, string? url, CancellationToken ct)
    {
        var fields = new JsonObject { ["display_name"] = title };
        Put(fields, "text", text);
        Put(fields, "url", url);
        if (date is not null) fields["DateTime"] = Dt(date, time);
        if (address is not null) fields["Address"] = new JsonObject { ["address"] = address };
        var result = await client.Objects.CreateAsync(TripItObjectType.Note, fields, tripUuid, ct).ConfigureAwait(false);
        return Describe(result);
    }

    public async Task<string> AddPlanAsync(string type, string fieldsJson, string? tripUuid, CancellationToken ct)
    {
        var t = TripItObjectTypes.Parse(type);
        var result = await client.Objects.CreateAsync(t, ParseObject(fieldsJson, "fields_json"), tripUuid, ct)
            .ConfigureAwait(false);
        return Describe(result);
    }

    public async Task<string> UpdatePlanAsync(string type, string uuid, string changesJson, string? segmentUuid,
        CancellationToken ct)
    {
        var t = TripItObjectTypes.Parse(type);
        var changes = ParseObject(changesJson, "changes_json");
        if (segmentUuid is not null)
        {
            // A leg patch may change any leg field except the leg's own uuid.
            var segFields = PlanSchema.For(t).SegmentFields?.Where(f => f != "uuid").ToList()
                ?? throw new ArgumentException($"{t.ToPathSegment()} plans have no segments.");
            var unknown = changes.Select(kv => kv.Key).Where(k => !segFields.Contains(k)).ToList();
            if (unknown.Count > 0)
            {
                throw new ArgumentException(
                    $"Unknown segment field(s): {string.Join(", ", unknown)}. Valid: {string.Join(", ", segFields)}");
            }
        }
        var result = await client.Objects.UpdateAsync(t, uuid, changes, segmentUuid, ct).ConfigureAwait(false);
        return Describe(result);
    }

    public async Task<string> PreviewDeletePlanAsync(string type, string uuid, CancellationToken ct)
    {
        var t = TripItObjectTypes.Parse(type);
        var current = await client.Objects.GetAsync(t, uuid, ct).ConfigureAwait(false);
        var lines = ItineraryBuilder.FromObject(t, current).ToList();
        var what = lines.Count == 0
            ? $"the {t.ToDisplayName()} {uuid}"
            : string.Join("; ", lines.Select(Line));
        return $"Will permanently delete this {t.ToDisplayName()} ({(lines.Count > 1 ? $"{lines.Count} legs: " : "")}{what}).";
    }

    public async Task<string> DeletePlanAsync(string type, string uuid, CancellationToken ct)
    {
        var t = TripItObjectTypes.Parse(type);
        var current = await client.Objects.GetAsync(t, uuid, ct).ConfigureAwait(false);
        var lines = ItineraryBuilder.FromObject(t, current).ToList();
        await client.Objects.DeleteAsync(t, uuid, ct).ConfigureAwait(false);
        return ToJson(new { deleted = lines });
    }

    /// <summary>
    /// Describes deleting one leg. With the plan's type and uuid it names the leg and checks
    /// that it belongs to that plan; TripIt has no lookup by segment uuid alone.
    /// </summary>
    public async Task<string> PreviewDeleteSegmentAsync(string segmentUuid, string? type, string? objectUuid,
        CancellationToken ct)
    {
        TripsClient.ValidateUuid(segmentUuid);
        if (type is null || objectUuid is null)
        {
            return $"Will permanently delete the leg (segment) {segmentUuid}; the rest of its reservation stays. " +
                   "(Pass the plan's type and uuid to see which leg this is.)";
        }
        var t = TripItObjectTypes.Parse(type);
        var current = await client.Objects.GetAsync(t, objectUuid, ct).ConfigureAwait(false);
        var legs = ItineraryBuilder.FromObject(t, current).ToList();
        var leg = legs.FirstOrDefault(l => string.Equals(l.SegmentUuid, segmentUuid, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"Segment {segmentUuid} is not a leg of {t.ToPathSegment()} {objectUuid}.");
        return $"Will permanently delete one leg of this {t.ToDisplayName()}: {Line(leg)}. " +
               $"{(legs.Count - 1 == 0 ? "It is the only leg." : $"{legs.Count - 1} other leg(s) stay.")}";
    }

    public async Task<string> DeleteSegmentAsync(string segmentUuid, CancellationToken ct)
    {
        await client.Objects.DeleteSegmentAsync(segmentUuid, ct).ConfigureAwait(false);
        return ToJson(new { deleted_segment = segmentUuid });
    }

    public async Task<string> MovePlanAsync(string type, string uuid, string? tripUuid, CancellationToken ct)
    {
        var t = TripItObjectTypes.Parse(type);
        var result = await client.Objects.MoveAsync(t, uuid, tripUuid, ct).ConfigureAwait(false);
        return Describe(result);
    }

    // ── Shaping helpers ──────────────────────────────────────────────────────

    public static object Summarize(Trip t, string? me = null)
    {
        var mine = me is null ? null : MyInvitee(t, me);
        return new
        {
            uuid = t.Uuid,
            name = t.DisplayName,
            start_date = t.StartDate,
            end_date = t.EndDate,
            location = t.PrimaryLocation,
            description = t.Description,
            is_private = t.IsPrivate,
            i_am_traveling = mine?.IsTraveler,
            my_role = mine is null ? null : RoleText(mine),
            url = t.WebUrl,
        };
    }

    private static TripParticipant? MyInvitee(Trip t, string me)
        => TripsClient.Participants(new TripDetail { Trip = t, Objects = new Dictionary<TripItObjectType, IReadOnlyList<JsonElement>>() }, me)
            .FirstOrDefault(p => p.IsMe);

    private static string RoleText(TripParticipant p)
        => (p.IsOwner ? "owner, " : "") + p.Role.ToString().ToLowerInvariant();

    public static object DescribeTrip(TripDetail detail, bool includeRaw, bool includeWeather, string? me = null)
    {
        var participants = TripsClient.Participants(detail, me)
            .Select(p => new
            {
                profile_ref = p.ProfileRef,
                name = p.Name ?? p.ScreenName,
                role = p.Role.ToString().ToLowerInvariant(),
                is_owner = p.IsOwner,
                is_me = p.IsMe,
            })
            .ToList();
        return new
        {
            trip = Summarize(detail.Trip, me),
            object_count = detail.ObjectCount,
            participants = participants.Count == 0 ? null : participants,
            itinerary = ItineraryBuilder.Build(detail.Objects, includeWeather),
            raw = includeRaw ? RawObjects(detail.Objects) : null,
        };
    }

    private static Dictionary<string, JsonNode?> RawObjects(
        IReadOnlyDictionary<TripItObjectType, IReadOnlyList<JsonElement>> objects)
        => objects.ToDictionary(
            kv => kv.Key.ToPathSegment(),
            kv => (JsonNode?)new JsonArray([.. kv.Value.Select(o => ItineraryBuilder.Compact(o))]));

    private static string Describe(PlanWriteResult result, object? lookedUp = null, IReadOnlyList<string>? assumptions = null)
        => ToJson(new
        {
            type = result.Type.ToPathSegment(),
            object_uuid = result.Uuid,
            trip_uuid = result.TripUuid,
            itinerary = ItineraryBuilder.FromObject(result.Type, result.Object).ToList(),
            schedule_lookups = lookedUp,
            assumptions = assumptions is { Count: > 0 } ? assumptions : null,
            warnings = result.Warnings.Where(w => !w.Contains("marketing_airline_code", StringComparison.Ordinal)).ToList() is { Count: > 0 } ws ? ws : null,
        });

    private static string Dates(Trip t) => $"{t.StartDate}–{t.EndDate}";

    private static string Plans(int count) => count == 1 ? "1 plan" : $"{count} plans";

    private static string Line(ItineraryItem item)
        => string.Join(", ", new[] { item.Title, item.Start, item.End is null ? null : "until " + item.End }.Where(s => !string.IsNullOrWhiteSpace(s)));

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

    public static TripTimeframe ParseTimeframe(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "upcoming" or "future" => TripTimeframe.Upcoming,
        "past" => TripTimeframe.Past,
        _ => throw new ArgumentException("timeframe must be 'upcoming' or 'past'."),
    };

    public static JsonObject ParseObject(string json, string name)
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

    public static JsonArray ParseArray(string json, string name)
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
