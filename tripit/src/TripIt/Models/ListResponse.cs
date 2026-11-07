using System.Text.Json;
using System.Text.Json.Serialization;
using TripIt.Json;

namespace TripIt.Models;

/// <summary>
/// Generic envelope used by TripIt's list endpoints. The collection key varies by endpoint
/// (e.g. <c>Trip</c>, <c>AccountPremiumAlert</c>) and is a single object rather than an
/// array when it holds one item, so all unknown fields are captured via
/// <see cref="AdditionalData"/> and <see cref="Items"/> reads either shape.
/// </summary>
public sealed record ListResponse<T>
{
    [JsonPropertyName("timestamp")]   public string? Timestamp { get; init; }
    [JsonPropertyName("num_bytes")]   public int? NumBytes { get; init; }
    [JsonPropertyName("page_num")]    public int? PageNum { get; init; }
    [JsonPropertyName("page_size")]   public int? PageSize { get; init; }
    [JsonPropertyName("max_page")]    public int? MaxPage { get; init; }
    [JsonPropertyName("total_items")] public int? TotalItems { get; init; }

    /// <summary>
    /// The items under <paramref name="key"/>, whether TripIt sent an array or a single object.
    /// </summary>
    public IReadOnlyList<T> ItemsOf(string key)
        => AdditionalData.TryGetValue(key, out var value)
            ? [.. TripItJson.AsList(value).Select(e => e.Deserialize<T>(TripItJson.Options)!)]
            : [];

    /// <summary>
    /// Convenience: the items under the first array- or object-valued key in
    /// <see cref="AdditionalData"/> other than <c>Profile</c>. Prefer <see cref="ItemsOf"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<T> Items
    {
        get
        {
            foreach (var (key, value) in AdditionalData)
            {
                if (key == "Profile") continue;
                if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    return [.. TripItJson.AsList(value).Select(e => e.Deserialize<T>(TripItJson.Options)!)];
                }
            }
            return [];
        }
    }

    [JsonExtensionData] public Dictionary<string, JsonElement> AdditionalData { get; init; } = new();
}
