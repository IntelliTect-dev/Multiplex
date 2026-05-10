using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripIt.Models;

/// <summary>
/// A single TripIt Pro alert (price drop, refund alert, fare watch, etc.) from
/// <c>/api/v2/listProAlerts</c>.
/// </summary>
public sealed record ProAlert
{
    [JsonPropertyName("id")]            public string? Id { get; init; }
    [JsonPropertyName("alert_type")]    public string? AlertType { get; init; }
    [JsonPropertyName("title")]         public string? Title { get; init; }
    [JsonPropertyName("description")]   public string? Description { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement> AdditionalData { get; init; } = new();
}
