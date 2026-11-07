using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TripIt.Json;

/// <summary>
/// Shared JSON plumbing for TripIt responses.
///
/// Two quirks of the TripIt envelope (inherited from its v1 API) drive everything here:
/// <list type="number">
///   <item><b>Every scalar is a JSON string</b> — <c>"id":"388109389"</c>,
///   <c>"is_private":"false"</c>. Typed numeric/boolean properties therefore need
///   string-tolerant converters (see <see cref="Options"/>).</item>
///   <item><b>A collection of one is an object, not an array</b> — <c>"Trip":{…}</c> for a
///   single trip, <c>"Trip":[…]</c> for several. Use <see cref="AsList"/> to read either.</item>
/// </list>
/// </summary>
public static class TripItJson
{
    /// <summary>Serializer options that tolerate string-encoded numbers and booleans.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions(indented: false);

    /// <summary>Indented variant for human/LLM-facing output.</summary>
    public static JsonSerializerOptions IndentedOptions { get; } = CreateOptions(indented: true);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            WriteIndented = indented,
            // Output is read by people and LLMs: keep arrows, accents and "+" literal, not \u-escaped.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new LenientBooleanConverter());
        o.Converters.Add(new LenientNullableBooleanConverter());
        return o;
    }

    /// <summary>
    /// Reads a property that may be absent, a single object, or an array of objects, and
    /// always returns a list. This is how every TripIt collection must be read.
    /// </summary>
    public static IReadOnlyList<JsonElement> AsList(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(propertyName, out var value))
        {
            return [];
        }
        return AsList(value);
    }

    /// <inheritdoc cref="AsList(JsonElement, string)"/>
    public static IReadOnlyList<JsonElement> AsList(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => [.. value.EnumerateArray()],
        JsonValueKind.Object => [value],
        _ => [],
    };

    /// <summary>Returns a string property, or null when absent/empty/non-scalar.</summary>
    public static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>Returns a child object, or null.</summary>
    public static JsonElement? Obj(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>Parses a string-encoded integer property.</summary>
    public static int? Int(JsonElement e, string name)
        => int.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    /// <summary>Parses a string-encoded boolean property.</summary>
    public static bool? Bool(JsonElement e, string name)
        => bool.TryParse(Str(e, name), out var b) ? b : null;

    /// <summary>
    /// Returns a copy of <paramref name="element"/> with empty strings, empty objects,
    /// empty arrays and the listed noise keys removed, recursively. Used to keep MCP
    /// output compact: raw TripIt payloads are mostly empty fields and UI plumbing.
    /// </summary>
    public static JsonNode? Prune(JsonElement element, ISet<string>? dropKeys = null)
        => Prune(JsonNode.Parse(element.GetRawText()), dropKeys ?? DefaultNoiseKeys);

    private static JsonNode? Prune(JsonNode? node, ISet<string> dropKeys)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (dropKeys.Contains(key))
                    {
                        obj.Remove(key);
                        continue;
                    }
                    var pruned = Prune(obj[key], dropKeys);
                    if (pruned is null) obj.Remove(key);
                    else if (!ReferenceEquals(pruned, obj[key])) obj[key] = pruned;
                }
                return obj.Count == 0 ? null : obj;
            case JsonArray arr:
                for (var i = arr.Count - 1; i >= 0; i--)
                {
                    var pruned = Prune(arr[i], dropKeys);
                    if (pruned is null) arr.RemoveAt(i);
                }
                return arr.Count == 0 ? null : arr;
            case JsonValue val:
                if (val.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)) return null;
                return val;
            default:
                return null;
        }
    }

    /// <summary>
    /// Keys that carry no trip information for a reader: deep links into the SPA,
    /// airline marketing URLs, auto-generated-name flags, and opaque sharing refs.
    /// </summary>
    public static ISet<string> DefaultNoiseKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "relative_url", "is_display_name_auto_generated", "image_url", "refund_info_url",
        "change_reservation_url", "customer_support_url", "web_home_url", "mobile_home_url",
        "alternate_flights_url", "is_eligible_seattracker", "is_eligible_airhelp",
        "is_tripit_booking", "has_possible_cancellation", "is_concur_booked", "is_concur_linked",
        "is_client_traveler", "is_hidden", "@attributes", "public_guid",
        "is_trip_owner_inner_circle_sharer", "is_timezone_manual", "Emissions",
    };

    private sealed class LenientBooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => ReadBool(ref reader) ?? false;

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
            => writer.WriteBooleanValue(value);
    }

    private sealed class LenientNullableBooleanConverter : JsonConverter<bool?>
    {
        public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => ReadBool(ref reader);

        public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteBooleanValue(value.Value);
        }
    }

    private static bool? ReadBool(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        JsonTokenType.Null => null,
        JsonTokenType.String => reader.GetString() switch
        {
            var s when string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1" => true,
            var s when string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) || s == "0" => false,
            _ => null,
        },
        JsonTokenType.Number => reader.TryGetInt64(out var n) ? n != 0 : null,
        _ => throw new JsonException($"Cannot convert {reader.TokenType} to Boolean."),
    };
}
