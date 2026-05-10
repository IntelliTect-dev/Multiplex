using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripIt.Models;

/// <summary>
/// Traveler profile (passport, frequent flyer numbers, KTN, etc.) from
/// <c>/api/v2/travelerProfile/get</c>.
/// </summary>
public sealed record TravelerProfile
{
    [JsonPropertyName("traveler_profile_id")] public string? TravelerProfileId { get; init; }
    [JsonPropertyName("first_name")]          public string? FirstName { get; init; }
    [JsonPropertyName("last_name")]           public string? LastName { get; init; }
    [JsonPropertyName("middle_name")]         public string? MiddleName { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement> AdditionalData { get; init; } = new();
}
