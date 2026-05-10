using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripIt.Models;

/// <summary>
/// A trip as returned by <c>/api/v2/list/trip</c>. Only commonly-observed fields are
/// exposed as typed properties; everything else is preserved in <see cref="AdditionalData"/>.
/// </summary>
public sealed record Trip
{
    [JsonPropertyName("id")]                public string? Id { get; init; }
    [JsonPropertyName("display_name")]      public string? DisplayName { get; init; }
    [JsonPropertyName("start_date")]        public string? StartDate { get; init; }
    [JsonPropertyName("end_date")]          public string? EndDate { get; init; }
    [JsonPropertyName("primary_location")]  public string? PrimaryLocation { get; init; }
    [JsonPropertyName("is_private")]        public bool? IsPrivate { get; init; }
    [JsonPropertyName("image_url")]         public string? ImageUrl { get; init; }
    [JsonPropertyName("relative_url")]      public string? RelativeUrl { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement> AdditionalData { get; init; } = new();
}
