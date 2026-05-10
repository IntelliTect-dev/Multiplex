using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripIt.Models;

/// <summary>
/// User profile from <c>/api/v2/get/profile</c>.
/// </summary>
public sealed record Profile
{
    [JsonPropertyName("display_name")]          public string? DisplayName { get; init; }
    [JsonPropertyName("screen_name")]           public string? ScreenName { get; init; }
    [JsonPropertyName("public_display_name")]   public string? PublicDisplayName { get; init; }
    [JsonPropertyName("company")]               public string? Company { get; init; }
    [JsonPropertyName("home_city")]             public string? HomeCity { get; init; }
    [JsonPropertyName("locale")]                public string? Locale { get; init; }
    [JsonPropertyName("ts_pre_check_id")]       public string? TsPreCheckId { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement> AdditionalData { get; init; } = new();
}
