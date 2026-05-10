using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripIt.Models;

/// <summary>
/// Google Tag Manager dataLayer payload from <c>/api/v2/gtmDataAsJson</c>.
/// </summary>
public sealed record GtmData
{
    [JsonPropertyName("user_id")]       public string? UserId { get; init; }
    [JsonPropertyName("member_status")] public string? MemberStatus { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement> AdditionalData { get; init; } = new();
}
