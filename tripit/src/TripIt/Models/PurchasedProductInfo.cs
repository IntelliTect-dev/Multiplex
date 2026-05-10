using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripIt.Models;

/// <summary>
/// Subscription / one-time-purchase info from <c>/api/v2/purchasedProductInfo</c>.
/// </summary>
public sealed record PurchasedProductInfo
{
    [JsonPropertyName("is_pro")]         public bool? IsPro { get; init; }
    [JsonPropertyName("expiration_date")] public string? ExpirationDate { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement> AdditionalData { get; init; } = new();
}
