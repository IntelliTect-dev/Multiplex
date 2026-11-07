using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripIt.Models;

/// <summary>
/// A trip as returned by <c>v2/list/trip</c> and <c>v2/get/trip</c>. Common fields are
/// typed; everything else is preserved in <see cref="AdditionalData"/>. Every scalar
/// arrives as a JSON string — deserialize with <see cref="Json.TripItJson.Options"/>.
/// </summary>
public sealed record Trip
{
    [JsonPropertyName("id")]                public string? Id { get; init; }
    [JsonPropertyName("uuid")]              public string? Uuid { get; init; }
    [JsonPropertyName("display_name")]      public string? DisplayName { get; init; }
    [JsonPropertyName("start_date")]        public string? StartDate { get; init; }
    [JsonPropertyName("end_date")]          public string? EndDate { get; init; }
    [JsonPropertyName("primary_location")]  public string? PrimaryLocation { get; init; }
    [JsonPropertyName("description")]       public string? Description { get; init; }
    [JsonPropertyName("is_private")]        public bool? IsPrivate { get; init; }
    [JsonPropertyName("is_ai_generated")]   public bool? IsAiGenerated { get; init; }
    [JsonPropertyName("is_pro_enabled")]    public bool? IsProEnabled { get; init; }
    [JsonPropertyName("image_url")]         public string? ImageUrl { get; init; }
    [JsonPropertyName("relative_url")]      public string? RelativeUrl { get; init; }
    [JsonPropertyName("last_modified")]     public string? LastModified { get; init; }

    /// <summary>Absolute URL of the trip in the TripIt web app.</summary>
    [JsonIgnore]
    public string? WebUrl => Uuid is null ? null : $"https://www.tripit.com/app/trips/{Uuid}";

    [JsonExtensionData] public Dictionary<string, JsonElement> AdditionalData { get; init; } = new();
}

/// <summary>A page of trips from <c>v2/list/trip</c>.</summary>
public sealed class TripList
{
    public required IReadOnlyList<Trip> Trips { get; init; }
    public int? PageNum { get; init; }
    public int? PageSize { get; init; }
    public int? MaxPage { get; init; }
    public int? TotalItems { get; init; }
}
