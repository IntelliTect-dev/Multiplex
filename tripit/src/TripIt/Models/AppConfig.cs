using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripIt.Models;

/// <summary>
/// SPA runtime configuration / feature flags from <c>/api/v2/appConfig</c>.
/// </summary>
public sealed record AppConfig
{
    [JsonExtensionData] public Dictionary<string, JsonElement> AdditionalData { get; init; } = new();
}
