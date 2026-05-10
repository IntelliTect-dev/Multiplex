using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripIt.Models;

/// <summary>
/// Generic envelope used by TripIt's <c>list/*</c> endpoints. The actual collection key
/// varies by endpoint (e.g. <c>Trip</c>, <c>ProAlert</c>) so all unknown fields are captured
/// via <see cref="AdditionalData"/>.
/// </summary>
public sealed record ListResponse<T>
{
    [JsonPropertyName("timestamp")] public string? Timestamp { get; init; }
    [JsonPropertyName("num_bytes")] public int? NumBytes { get; init; }
    [JsonPropertyName("page_num")]  public int? PageNum { get; init; }
    [JsonPropertyName("page_size")] public int? PageSize { get; init; }
    [JsonPropertyName("max_page")]  public int? MaxPage { get; init; }

    /// <summary>
    /// Convenience: returns the first array-typed value in <see cref="AdditionalData"/>,
    /// deserialized to <typeparamref name="T"/>. TripIt names this key after the entity type
    /// (e.g. <c>Trip</c> or <c>ProAlert</c>).
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<T> Items
    {
        get
        {
            foreach (var (_, value) in AdditionalData)
            {
                if (value.ValueKind == JsonValueKind.Array)
                {
                    var list = value.Deserialize<List<T>>(SerializerOptions);
                    if (list is not null) return list;
                }
            }
            return Array.Empty<T>();
        }
    }

    [JsonExtensionData] public Dictionary<string, JsonElement> AdditionalData { get; init; } = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
