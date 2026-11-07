namespace TripIt;

/// <summary>
/// The travel-object ("plan") types TripIt stores inside a trip. Each has three spellings:
/// the URL path segment (<c>air</c>), the response envelope key (<c>AirObject</c>) and a
/// display name. See <c>docs/api-surface.md</c> § Object types.
/// </summary>
public enum TripItObjectType
{
    Air,
    Lodging,
    Car,
    Rail,
    Transport,
    Cruise,
    Restaurant,
    Activity,
    Note,
    Directions,
    Parking,
    Map,
    Weather,
}

public static class TripItObjectTypes
{
    private static readonly (TripItObjectType Type, string Path, string Key, string Display)[] s_map =
    [
        (TripItObjectType.Air,        "air",        "AirObject",        "flight"),
        (TripItObjectType.Lodging,    "lodging",    "LodgingObject",    "lodging"),
        (TripItObjectType.Car,        "car",        "CarObject",        "car rental"),
        (TripItObjectType.Rail,       "rail",       "RailObject",       "rail"),
        (TripItObjectType.Transport,  "transport",  "TransportObject",  "ground transportation"),
        (TripItObjectType.Cruise,     "cruise",     "CruiseObject",     "cruise"),
        (TripItObjectType.Restaurant, "restaurant", "RestaurantObject", "restaurant"),
        (TripItObjectType.Activity,   "activity",   "ActivityObject",   "activity"),
        (TripItObjectType.Note,       "note",       "NoteObject",       "note"),
        (TripItObjectType.Directions, "directions", "DirectionsObject", "directions"),
        (TripItObjectType.Parking,    "parking",    "ParkingObject",    "parking"),
        (TripItObjectType.Map,        "map",        "MapObject",        "map"),
        (TripItObjectType.Weather,    "weather",    "WeatherObject",    "weather"),
    ];

    /// <summary>All object types, in display order.</summary>
    public static IReadOnlyList<TripItObjectType> All { get; } = [.. s_map.Select(m => m.Type)];

    /// <summary>The URL path segment, e.g. <c>air</c>.</summary>
    public static string ToPathSegment(this TripItObjectType type) => Find(type).Path;

    /// <summary>The response envelope key, e.g. <c>AirObject</c>.</summary>
    public static string ToEnvelopeKey(this TripItObjectType type) => Find(type).Key;

    /// <summary>A human-readable name, e.g. <c>flight</c>.</summary>
    public static string ToDisplayName(this TripItObjectType type) => Find(type).Display;

    /// <summary>Parses a path segment (<c>air</c>), envelope key (<c>AirObject</c>) or enum name.</summary>
    public static bool TryParse(string? value, out TripItObjectType type)
    {
        type = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var m in s_map)
        {
            if (string.Equals(m.Path, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Key, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Type.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Display, value, StringComparison.OrdinalIgnoreCase))
            {
                type = m.Type;
                return true;
            }
        }
        return false;
    }

    /// <summary>Parses, throwing <see cref="ArgumentException"/> with the valid values on failure.</summary>
    public static TripItObjectType Parse(string value)
        => TryParse(value, out var t)
            ? t
            : throw new ArgumentException(
                $"Unknown TripIt object type '{value}'. Valid: {string.Join(", ", s_map.Select(m => m.Path))}.",
                nameof(value));

    /// <summary>Maps an envelope key back to its type, or null if it is not a travel object.</summary>
    public static TripItObjectType? FromEnvelopeKey(string key)
    {
        foreach (var m in s_map)
        {
            if (string.Equals(m.Key, key, StringComparison.Ordinal)) return m.Type;
        }
        return null;
    }

    private static (TripItObjectType Type, string Path, string Key, string Display) Find(TripItObjectType type)
    {
        foreach (var m in s_map)
        {
            if (m.Type == type) return m;
        }
        throw new ArgumentOutOfRangeException(nameof(type), type, null);
    }
}
