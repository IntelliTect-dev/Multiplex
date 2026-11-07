using System.Text.Json;
using System.Text.Json.Nodes;
using TripIt.Json;
using TripIt.Models;

namespace TripIt;

/// <summary>
/// A person's role on a trip, as the web app models it (<c>setParticipantType</c>):
/// viewer = read-only; editor = can edit, not travelling; traveler = can edit and travels.
/// </summary>
public enum TripRole
{
    Viewer,
    Editor,
    Traveler,
}

/// <summary>Someone a trip is shared with (a <c>TripInvitees.Invitee</c>).</summary>
public sealed record TripParticipant(
    string? ProfileRef,
    string? Name,
    string? ScreenName,
    bool IsOwner,
    bool IsTraveler,
    bool IsReadOnly,
    bool IsMe)
{
    public TripRole Role => IsTraveler ? TripRole.Traveler : IsReadOnly ? TripRole.Viewer : TripRole.Editor;
}

public sealed partial class TripsClient
{
    /// <summary>
    /// The people on a trip, with their roles. <paramref name="myProfileRef"/> (from
    /// <see cref="TripItClient.GetMyProfileRefAsync"/>) marks the signed-in user.
    /// </summary>
    public static IReadOnlyList<TripParticipant> Participants(TripDetail detail, string? myProfileRef)
    {
        if (!detail.Trip.AdditionalData.TryGetValue("TripInvitees", out var inv)) return [];
        return [.. TripItJson.AsList(inv, "Invitee").Select(i =>
        {
            var reference = TripItJson.Obj(i, "@attributes") is { } a ? TripItJson.Str(a, "profile_ref") : null;
            var profile = detail.Profiles.FirstOrDefault(p =>
                TripItJson.Obj(p, "@attributes") is { } pa && TripItJson.Str(pa, "ref") == reference);
            return new TripParticipant(
                reference,
                profile.ValueKind == JsonValueKind.Object ? TripItJson.Str(profile, "public_display_name") : null,
                profile.ValueKind == JsonValueKind.Object ? TripItJson.Str(profile, "screen_name") : null,
                TripItJson.Bool(i, "is_owner") ?? false,
                TripItJson.Bool(i, "is_traveler") ?? false,
                TripItJson.Bool(i, "is_read_only") ?? false,
                reference is not null && reference == myProfileRef);
        })];
    }

    /// <summary>
    /// Shares a trip by email (<c>POST v2/invite</c>). <b>TripIt emails each address an
    /// invitation.</b> Payload exactly as the web app's <c>shareTrip</c> builds it.
    /// </summary>
    public async Task InviteAsync(string tripUuid, IReadOnlyList<string> emails, TripRole role,
        string? message = null, CancellationToken ct = default)
    {
        ValidateUuid(tripUuid);
        ArgumentNullException.ThrowIfNull(emails);
        var clean = emails.Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        if (clean.Count == 0) throw new ArgumentException("At least one email address is required.");
        foreach (var e in clean)
        {
            if (!System.Net.Mail.MailAddress.TryCreate(e, out _)) throw new ArgumentException($"'{e}' is not an email address.");
        }
        await _client.SendAsync(HttpMethod.Post, "/api/v2/invite", null,
            BuildInvitePayload(tripUuid, clean, role, message).ToJsonString(), ct).ConfigureAwait(false);
    }

    /// <summary>The <c>v2/invite</c> body. Note: unlike every other payload, its booleans are real JSON booleans.</summary>
    public static JsonObject BuildInvitePayload(string tripUuid, IReadOnlyList<string> emails, TripRole role, string? message)
    {
        var (isReadOnly, isTraveler) = Flags(role);
        var invitation = new JsonObject
        {
            ["EmailAddresses"] = new JsonObject
            {
                ["email_address"] = emails.Count == 1 ? emails[0] : new JsonArray([.. emails.Select(e => (JsonNode)e)]),
            },
            ["TripShare"] = new JsonObject
            {
                ["trip_uuid"] = tripUuid,
                ["is_read_only"] = isReadOnly,
                ["is_traveler"] = isTraveler,
                ["is_sent_with_details"] = false,
            },
        };
        if (!string.IsNullOrWhiteSpace(message)) invitation["message"] = message;
        return new JsonObject { ["Invitation"] = invitation };
    }

    /// <summary>Changes a participant's role (<c>POST v2/changeTripInvitations</c>).</summary>
    public async Task ChangeRoleAsync(string tripUuid, string profileRef, TripRole role, CancellationToken ct = default)
    {
        ValidateUuid(tripUuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRef);
        await _client.SendAsync(HttpMethod.Post, "/api/v2/changeTripInvitations", null,
            BuildChangeRolePayload(tripUuid, profileRef, role).ToJsonString(), ct).ConfigureAwait(false);
    }

    /// <summary>The <c>v2/changeTripInvitations</c> body (invitee serializer: string booleans).</summary>
    public static JsonObject BuildChangeRolePayload(string tripUuid, string profileRef, TripRole role)
    {
        var (isReadOnly, isTraveler) = Flags(role);
        return new JsonObject
        {
            ["TripInvitations"] = new JsonObject
            {
                ["trip_uuid"] = tripUuid,
                ["Invitees"] = new JsonArray(new JsonObject
                {
                    ["Invitee"] = new JsonObject
                    {
                        ["@attributes"] = new JsonObject { ["profile_ref"] = profileRef },
                        ["is_read_only"] = isReadOnly ? "true" : "false",
                        ["is_traveler"] = isTraveler ? "true" : "false",
                    },
                }),
            },
        };
    }

    /// <summary>Removes someone from a trip (<c>POST v2/delete/trip_participant/trip_uuid/{uuid}/profile_ref/{ref}</c>).</summary>
    public async Task RemoveParticipantAsync(string tripUuid, string profileRef, CancellationToken ct = default)
    {
        ValidateUuid(tripUuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRef);
        await _client.SendAsync(HttpMethod.Post,
            $"/api/v2/delete/trip_participant/trip_uuid/{Uri.EscapeDataString(tripUuid)}/profile_ref/{Uri.EscapeDataString(profileRef)}",
            null, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges one trip into another (<c>POST v2/merge/trip?uuid={from}&amp;into_uuid={into}</c>):
    /// the plans of <paramref name="fromTripUuid"/> move into <paramref name="intoTripUuid"/> and
    /// the source trip is removed. Returns the merged trip as re-read afterwards.
    /// </summary>
    public async Task<TripDetail> MergeAsync(string fromTripUuid, string intoTripUuid, CancellationToken ct = default)
    {
        ValidateUuid(fromTripUuid);
        ValidateUuid(intoTripUuid);
        if (string.Equals(fromTripUuid, intoTripUuid, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A trip cannot be merged into itself.");
        await _client.SendAsync(HttpMethod.Post, "/api/v2/merge/trip",
            [new("uuid", fromTripUuid), new("into_uuid", intoTripUuid)], null, ct).ConfigureAwait(false);
        return await GetAsync(intoTripUuid, includeObjects: true, ct).ConfigureAwait(false);
    }

    private static (bool IsReadOnly, bool IsTraveler) Flags(TripRole role) => role switch
    {
        TripRole.Viewer => (true, false),
        TripRole.Editor => (false, false),
        TripRole.Traveler => (false, true),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>Parses viewer / editor / traveler (and a few synonyms).</summary>
    public static TripRole ParseRole(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "viewer" or "view" or "read" or "readonly" or "read-only" => TripRole.Viewer,
        "editor" or "edit" or "planner" => TripRole.Editor,
        "traveler" or "traveller" or "travel" => TripRole.Traveler,
        _ => throw new ArgumentException("role must be 'viewer', 'editor' or 'traveler'."),
    };
}

public sealed partial class TripItClient
{
    private string? _myProfileRef;

    /// <summary>The signed-in user's profile ref (cached), used to tell "me" apart on shared trips.</summary>
    public async Task<string?> GetMyProfileRefAsync(CancellationToken ct = default)
    {
        if (_myProfileRef is not null) return _myProfileRef;
        using var doc = await GetJsonAsync("/api/v2/get/profile", null, ct).ConfigureAwait(false);
        var profile = TripItJson.AsList(doc.RootElement, "Profile").FirstOrDefault();
        _myProfileRef = TripItJson.Obj(profile, "@attributes") is { } a ? TripItJson.Str(a, "ref") : null;
        return _myProfileRef;
    }
}
