using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TripIt.Json;
using TripIt.Models;

namespace TripIt;

/// <summary>
/// The fields of a trip TripIt accepts on create/replace — exactly what the web app's trip
/// serializer sends (captured: <c>{"Trip":{"is_owner_traveler":"true","description":…,
/// "display_name":…,"primary_location":…,"start_date":…,"end_date":…,"is_private":"true",
/// "TripPurposes":[{"purpose_type_code":"L"}]}}</c>).
/// </summary>
public sealed record TripInput
{
    /// <summary>Trip name. TripIt generates one from the destination and dates when empty.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Destination, e.g. "Reykjavik, Iceland". Required by the web form.</summary>
    public string? PrimaryLocation { get; init; }
    /// <summary>yyyy-MM-dd. Required.</summary>
    public string? StartDate { get; init; }
    /// <summary>yyyy-MM-dd. Required.</summary>
    public string? EndDate { get; init; }
    public string? Description { get; init; }
    /// <summary>
    /// True keeps the trip out of the user's Inner Circle. The web form's
    /// "Add Inner Circle members to this trip" checkbox is the inverse.
    /// </summary>
    public bool IsPrivate { get; init; }
    /// <summary>Whether the trip owner is travelling (vs. planning for others). Default true.</summary>
    public bool IsOwnerTraveler { get; init; } = true;
    /// <summary>Trip purpose code: L (leisure, the web default) or B (business).</summary>
    public string? PurposeTypeCode { get; init; } = "L";

    /// <summary>Serializes as the web app does. Null fields are omitted.</summary>
    public JsonObject ToPayload()
    {
        var trip = new JsonObject
        {
            ["is_owner_traveler"] = IsOwnerTraveler ? "true" : "false",
        };
        void Set(string key, string? value)
        {
            if (value is not null) trip[key] = value;
        }
        Set("description", Description);
        Set("display_name", DisplayName);
        Set("primary_location", PrimaryLocation);
        Set("start_date", TripItWrite.NormalizeDate(StartDate));
        Set("end_date", TripItWrite.NormalizeDate(EndDate));
        trip["is_private"] = IsPrivate ? "true" : "false";
        if (!string.IsNullOrWhiteSpace(PurposeTypeCode))
        {
            trip["TripPurposes"] = new JsonArray(new JsonObject { ["purpose_type_code"] = PurposeTypeCode });
        }
        return new JsonObject { ["Trip"] = trip };
    }

    /// <summary>Builds the input that reproduces an existing trip, for fetch → modify → replace.</summary>
    public static TripInput FromTrip(Trip trip)
    {
        string? purpose = null;
        if (trip.AdditionalData.TryGetValue("TripPurposes", out var tp))
        {
            var first = TripItJson.AsList(tp).FirstOrDefault();
            purpose = TripItJson.Str(first, "purpose_type_code");
        }
        var isOwnerTraveler = true;
        if (trip.AdditionalData.TryGetValue("TripInvitees", out var inv))
        {
            var owner = TripItJson.AsList(inv, "Invitee").FirstOrDefault(i => TripItJson.Bool(i, "is_owner") == true);
            if (owner.ValueKind == JsonValueKind.Object) isOwnerTraveler = TripItJson.Bool(owner, "is_traveler") ?? true;
        }
        return new TripInput
        {
            DisplayName = trip.DisplayName,
            PrimaryLocation = trip.PrimaryLocation,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            Description = trip.Description,
            IsPrivate = trip.IsPrivate ?? false,
            IsOwnerTraveler = isOwnerTraveler,
            PurposeTypeCode = purpose ?? "L",
        };
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(StartDate) || string.IsNullOrWhiteSpace(EndDate))
            throw new ArgumentException("A trip needs StartDate and EndDate (yyyy-MM-dd).");
        if (string.IsNullOrWhiteSpace(PrimaryLocation) && string.IsNullOrWhiteSpace(DisplayName))
            throw new ArgumentException("A trip needs a PrimaryLocation (destination) or a DisplayName.");
        if (string.CompareOrdinal(TripItWrite.NormalizeDate(StartDate), TripItWrite.NormalizeDate(EndDate)) > 0)
            throw new ArgumentException($"StartDate {StartDate} is after EndDate {EndDate}.");
    }
}

public sealed partial class TripsClient
{
    /// <summary>Creates a trip (<c>POST v2/create/trip</c>) and returns it.</summary>
    public async Task<Trip> CreateAsync(TripInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.Validate();
        var body = await _client.SendAsync(HttpMethod.Post, "/api/v2/create/trip", null, input.ToPayload().ToJsonString(), ct)
            .ConfigureAwait(false);
        var trip = ParseTrip(body);
        // Measured 2026-11-07: create/trip ignores is_owner_traveler=false (the owner is always
        // made a traveler), but replace/trip honours it. Re-apply it with a replace.
        if (!input.IsOwnerTraveler && trip.Uuid is { } createdUuid)
        {
            try
            {
                trip = await ReplaceAsync(createdUuid, input, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The trip exists now. Say so with its uuid, so a retry updates it instead of
                // creating a duplicate.
                throw new TripItPartialWriteException(createdUuid,
                    $"The trip was created (uuid {createdUuid}, '{trip.DisplayName}'), but marking the owner as not " +
                    $"travelling on it failed ({TripItErrorText.Describe(ex)}). Do not create it again: update trip " +
                    $"{createdUuid} to set the owner as not travelling.", ex);
            }
        }
        return trip;
    }

    /// <summary>
    /// Replaces a trip's editable fields (<c>POST v2/replace/trip/uuid/{uuid}</c>). TripIt
    /// treats the payload as the complete new state; prefer <see cref="UpdateAsync"/>.
    /// </summary>
    public async Task<Trip> ReplaceAsync(string uuid, TripInput input, CancellationToken ct = default)
    {
        ValidateUuid(uuid);
        ArgumentNullException.ThrowIfNull(input);
        input.Validate();
        var body = await _client.SendAsync(HttpMethod.Post, $"/api/v2/replace/trip/uuid/{Uri.EscapeDataString(uuid)}",
            null, input.ToPayload().ToJsonString(), ct).ConfigureAwait(false);
        return ParseTrip(body);
    }

    /// <summary>Fetches the trip, applies <paramref name="change"/> to its current values, and replaces it.</summary>
    public async Task<Trip> UpdateAsync(string uuid, Func<TripInput, TripInput> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var current = await GetAsync(uuid, includeObjects: false, ct).ConfigureAwait(false);
        return await ReplaceAsync(uuid, change(TripInput.FromTrip(current.Trip)), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a trip (<c>POST v2/delete/trip/uuid/{uuid}</c>). Permanent. TripIt also
    /// deletes the trip's plans.
    /// </summary>
    public async Task DeleteAsync(string uuid, CancellationToken ct = default)
    {
        ValidateUuid(uuid);
        await _client.SendAsync(HttpMethod.Post, $"/api/v2/delete/trip/uuid/{Uri.EscapeDataString(uuid)}", null, null, ct)
            .ConfigureAwait(false);
    }

    private static Trip ParseTrip(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var el = TripItJson.AsList(doc.RootElement, "Trip").FirstOrDefault();
        return el.ValueKind == JsonValueKind.Object
            ? el.Deserialize<Trip>(TripItJson.Options)!
            : throw new TripItApiException(200, "trip", "Response contained no Trip.");
    }
}

/// <summary>Result of a plan write: the object as TripIt stored it, plus any warnings.</summary>
public sealed record PlanWriteResult(TripItObjectType Type, JsonElement Object, IReadOnlyList<string> Warnings)
{
    public string? Uuid => TripItJson.Str(Object, "uuid");
    public string? TripUuid => TripItJson.Str(Object, "trip_uuid");
}

public sealed partial class ObjectsClient
{
    /// <summary>
    /// Creates a travel object (<c>POST v2/create</c> with <c>{"&lt;Type&gt;Object":{…}}</c>).
    /// <paramref name="fields"/> uses TripIt's own field names (see <see cref="PlanSchema"/>);
    /// booleans and numbers are converted to TripIt's string form, and dates/times are
    /// normalized. Pass <paramref name="tripUuid"/> to file it in a trip; without one TripIt
    /// auto-files it by date or leaves it in Unfiled Items.
    /// </summary>
    public async Task<PlanWriteResult> CreateAsync(TripItObjectType type, JsonObject fields, string? tripUuid,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (tripUuid is not null)
        {
            TripsClient.ValidateUuid(tripUuid);
            fields = (JsonObject)fields.DeepClone();
            fields["trip_uuid"] = tripUuid;
        }
        var payload = TripItWrite.PreparePlan(type, fields);
        var body = await _client.SendAsync(HttpMethod.Post, "/api/v2/create", null,
            new JsonObject { [type.ToEnvelopeKey()] = payload }.ToJsonString(), ct).ConfigureAwait(false);
        return ParseWrite(type, body);
    }

    /// <summary>
    /// Replaces a travel object (<c>POST v2/replacePlan/{type}/uuid/{uuid}</c>). The payload
    /// is the complete new state — omitted fields are cleared. Prefer <see cref="UpdateAsync"/>.
    /// </summary>
    public async Task<PlanWriteResult> ReplaceAsync(TripItObjectType type, string uuid, JsonObject fields,
        CancellationToken ct = default)
    {
        TripsClient.ValidateUuid(uuid);
        var payload = TripItWrite.PreparePlan(type, fields);
        var body = await _client.SendAsync(HttpMethod.Post,
            $"/api/v2/replacePlan/{type.ToPathSegment()}/uuid/{Uri.EscapeDataString(uuid)}", null,
            new JsonObject { [type.ToEnvelopeKey()] = payload }.ToJsonString(), ct).ConfigureAwait(false);
        return ParseWrite(type, body);
    }

    /// <summary>
    /// Updates selected fields of a travel object: fetches it, projects it to its writable
    /// fields, deep-merges <paramref name="changes"/> (objects merge, arrays and scalars
    /// replace; a JSON null removes a field), and replaces it.
    /// For segmented types, use <paramref name="segmentUuid"/> + segment fields to change one
    /// leg, or pass a whole <c>Segment</c> array that names every existing leg by its
    /// <c>uuid</c> (new legs have none). A <c>Segment</c> that leaves out an existing leg is
    /// refused: the replace would delete that leg.
    /// </summary>
    public async Task<PlanWriteResult> UpdateAsync(TripItObjectType type, string uuid, JsonObject changes,
        string? segmentUuid = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (segmentUuid is not null && changes.ContainsKey("uuid"))
        {
            throw new ArgumentException("A leg's uuid cannot be changed; leave 'uuid' out of the segment changes.");
        }
        var current = await GetAsync(type, uuid, ct).ConfigureAwait(false);
        var writable = PlanSchema.ToWritable(type, current);
        if (segmentUuid is not null)
        {
            var segs = writable["Segment"] as JsonArray
                       ?? throw new ArgumentException($"{type.ToPathSegment()} objects have no segments.");
            var seg = segs.OfType<JsonObject>().FirstOrDefault(s => (string?)s["uuid"] == segmentUuid)
                      ?? throw new ArgumentException($"Segment {segmentUuid} is not part of {type.ToPathSegment()} {uuid}.");
            TripItWrite.DeepMerge(seg, (JsonObject)changes.DeepClone());
        }
        else
        {
            EnsureSegmentPatchKeepsLegs(type, writable, changes);
            TripItWrite.DeepMerge(writable, MergeLegsByUuid(writable, changes));
        }
        return await ReplaceAsync(type, uuid, writable, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a copy of <paramref name="changes"/> whose <c>Segment</c> legs that carry a uuid
    /// are merged into the existing leg (so a leg patch need not repeat its flight number and
    /// airports); legs without a uuid are new.
    /// </summary>
    private static JsonObject MergeLegsByUuid(JsonObject writable, JsonObject changes)
    {
        var copy = (JsonObject)changes.DeepClone();
        if (copy["Segment"] is not { } patch || writable["Segment"] is not JsonArray current) return copy;
        var legs = patch switch
        {
            JsonArray a => a.OfType<JsonObject>().ToList(),
            JsonObject o => [o],
            _ => [],
        };
        var merged = new JsonArray();
        foreach (var leg in legs)
        {
            var id = leg["uuid"] is JsonValue v && v.TryGetValue<string>(out var u) ? u : null;
            var existing = id is null
                ? null
                : current.OfType<JsonObject>().FirstOrDefault(s => string.Equals((string?)s["uuid"], id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                merged.Add(leg.DeepClone());
                continue;
            }
            var target = (JsonObject)existing.DeepClone();
            TripItWrite.DeepMerge(target, (JsonObject)leg.DeepClone());
            merged.Add(target);
        }
        copy["Segment"] = merged;
        return copy;
    }

    /// <summary>
    /// Refuses a whole-<c>Segment</c> change that would drop existing legs: replacePlan takes
    /// the complete state, so a leg whose uuid is missing from the new array is deleted.
    /// </summary>
    internal static void EnsureSegmentPatchKeepsLegs(TripItObjectType type, JsonObject writable, JsonObject changes)
    {
        if (!changes.TryGetPropertyValue("Segment", out var patch)) return;
        var existing = (writable["Segment"] as JsonArray)?.OfType<JsonObject>()
            .Select(s => (string?)s["uuid"]).OfType<string>().ToList() ?? [];
        if (existing.Count == 0) return;

        var legs = patch switch
        {
            JsonArray a => a.OfType<JsonObject>().ToList(),
            JsonObject o => [o],
            _ => [],
        };
        var given = legs.Select(s => s["uuid"] is JsonValue v && v.TryGetValue<string>(out var u) ? u : null)
            .OfType<string>().ToList();
        var unknown = given.Except(existing, StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                $"Segment uuid(s) {string.Join(", ", unknown)} are not legs of this {type.ToDisplayName()}. " +
                $"Its legs are: {string.Join(", ", existing)}.");
        }
        var missing = existing.Except(given, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException(
                $"This {type.ToDisplayName()} has {existing.Count} leg(s) ({string.Join(", ", existing)}). " +
                "A Segment change replaces every leg, so it must include each existing leg with its uuid; " +
                $"the leg(s) left out ({string.Join(", ", missing)}) would be deleted. To change one leg, " +
                "update it by its segment uuid; to remove a leg, delete that segment. Nothing was changed.");
        }
    }

    /// <summary>Deletes a travel object (<c>POST v2/delete/{type}/uuid/{uuid}</c>). Permanent.</summary>
    public async Task DeleteAsync(TripItObjectType type, string uuid, CancellationToken ct = default)
    {
        TripsClient.ValidateUuid(uuid);
        await _client.SendAsync(HttpMethod.Post,
            $"/api/v2/delete/{type.ToPathSegment()}/uuid/{Uri.EscapeDataString(uuid)}", null, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes one segment (a flight leg, train leg, transfer or cruise port) without
    /// deleting the rest of its reservation (<c>POST v2/delete/segment/uuid/{uuid}</c>).
    /// </summary>
    public async Task DeleteSegmentAsync(string segmentUuid, CancellationToken ct = default)
    {
        TripsClient.ValidateUuid(segmentUuid);
        await _client.SendAsync(HttpMethod.Post, $"/api/v2/delete/segment/uuid/{Uri.EscapeDataString(segmentUuid)}",
            null, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a travel object to another trip, or to Unfiled Items when
    /// <paramref name="tripUuid"/> is null (<c>GET v2/move/{type}/uuid/{uuid}?trip_uuid=</c>).
    /// </summary>
    public async Task<PlanWriteResult> MoveAsync(TripItObjectType type, string uuid, string? tripUuid,
        CancellationToken ct = default)
    {
        TripsClient.ValidateUuid(uuid);
        if (tripUuid is not null) TripsClient.ValidateUuid(tripUuid);
        var body = await _client.SendAsync(HttpMethod.Get,
            $"/api/v2/move/{type.ToPathSegment()}/uuid/{Uri.EscapeDataString(uuid)}",
            [new("trip_uuid", tripUuid ?? "")], null, ct).ConfigureAwait(false);
        return ParseWrite(type, body);
    }

    private static PlanWriteResult ParseWrite(TripItObjectType type, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();
        var obj = TripItJson.AsList(root, type.ToEnvelopeKey()).FirstOrDefault();
        if (obj.ValueKind != JsonValueKind.Object)
        {
            throw new TripItApiException(200, type.ToPathSegment(), $"Response contained no {type.ToEnvelopeKey()}.");
        }
        var warnings = TripItJson.AsList(root, "Warning")
            .Select(w => TripItJson.Str(w, "description"))
            .Where(w => w is not null)
            .Cast<string>()
            .ToList();
        return new PlanWriteResult(type, obj, warnings);
    }
}

/// <summary>Payload shaping shared by the write methods.</summary>
public static class TripItWrite
{
    /// <summary>
    /// Validates and normalizes caller fields into a TripIt plan payload: unknown fields are
    /// rejected, scalars become strings, dates/times are normalized, a single Segment or
    /// traveler becomes an array, and date blocks get <c>is_timezone_manual</c>.
    /// </summary>
    public static JsonObject PreparePlan(TripItObjectType type, JsonObject fields)
    {
        var payload = (JsonObject)fields.DeepClone();
        if (payload["Segment"] is JsonObject single) payload["Segment"] = new JsonArray(single.DeepClone());
        var schema = PlanSchema.For(type);
        if (schema.TravelerKey is { } tk && payload[tk] is JsonObject oneTraveler)
        {
            payload[tk] = new JsonArray(oneTraveler.DeepClone());
        }
        PlanSchema.Validate(type, payload);
        Normalize(payload);
        return PlanSchema.Order(type, payload);
    }

    private static readonly HashSet<string> s_dateBlocks = new(StringComparer.Ordinal) { "StartDateTime", "EndDateTime", "DateTime" };

    private static void Normalize(JsonNode? node, string? key = null)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var k in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[k];
                    if (child is JsonValue v && !v.TryGetValue<string>(out _))
                    {
                        obj[k] = ScalarToString(v);
                    }
                    else
                    {
                        Normalize(child, k);
                    }
                }
                if (key is not null && s_dateBlocks.Contains(key)) NormalizeDateBlock(obj);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v && !v.TryGetValue<string>(out _)) arr[i] = ScalarToString(v);
                    else Normalize(arr[i], key);
                }
                break;
        }
    }

    private static JsonNode? ScalarToString(JsonValue v) => v.GetValueKind() switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => v.ToJsonString(),
        JsonValueKind.Null => null,
        _ => v.DeepClone(),
    };

    private static void NormalizeDateBlock(JsonObject block)
    {
        if ((string?)block["date"] is { } d) block["date"] = NormalizeDate(d);
        if ((string?)block["time"] is { } t) block["time"] = NormalizeTime(t);
        var tz = (string?)block["timezone"];
        if (string.IsNullOrWhiteSpace(tz) || tz == "null")
        {
            block.Remove("timezone");
            block["is_timezone_manual"] = "false";
        }
        else if (block["is_timezone_manual"] is null)
        {
            block["is_timezone_manual"] = "true";
        }
    }

    /// <summary>
    /// Accepts ISO dates only (<c>yyyy-MM-dd</c>) and returns them unchanged. Other forms are
    /// rejected rather than guessed: <c>03/04/2026</c> is March 4 in the US and 3 April elsewhere.
    /// </summary>
    public static string? NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var trimmed = value.Trim();
        if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        throw new ArgumentException(
            $"'{value}' is not a date in yyyy-MM-dd form (for example 2027-01-18). " +
            "Other forms are not accepted because they are ambiguous (03/04/2026 is March 4 or 3 April).");
    }

    /// <summary>Accepts HH:mm, HH:mm:ss, or h:mm tt; returns HH:mm:ss.</summary>
    public static string? NormalizeTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        string[] formats = ["HH:mm:ss", "HH:mm", "H:mm", "h:mm tt", "h:mmtt", "h tt", "htt", "hh:mm tt"];
        if (TimeOnly.TryParseExact(value.Trim().ToUpperInvariant(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            return t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        throw new ArgumentException($"'{value}' is not a time; use HH:mm (24-hour).");
    }

    /// <summary>
    /// Merges <paramref name="patch"/> into <paramref name="target"/>: objects merge
    /// recursively, arrays and scalars replace, and a JSON null removes the key.
    /// </summary>
    public static void DeepMerge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (value is null)
            {
                target.Remove(key);
            }
            else if (value is JsonObject po && target[key] is JsonObject to)
            {
                DeepMerge(to, po);
            }
            else
            {
                patch.Remove(key);
                target[key] = value;
            }
        }
    }
}
