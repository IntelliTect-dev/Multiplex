using System.Text.Json;

namespace Multiplex.Sites.GoogleFlights.Wire;

/// <summary>
/// Safe reads from Google's positional arrays. Every accessor returns <c>null</c> (or nothing) for a
/// missing index, a JSON null or the wrong kind, so a shifted payload degrades to missing values
/// that the parsers then report, rather than an exception from deep inside a loop.
/// </summary>
internal static class Positional
{
    public static JsonElement? At(this JsonElement element, params ReadOnlySpan<int> path)
    {
        var current = element;
        foreach (var index in path)
        {
            if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength())
                return null;
            current = current[index];
        }
        return current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : current;
    }

    public static JsonElement? At(this JsonElement? element, params ReadOnlySpan<int> path) =>
        element is { } e ? e.At(path) : null;

    public static string? Str(this JsonElement element, params ReadOnlySpan<int> path) =>
        element.At(path) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    public static string? Str(this JsonElement? element, params ReadOnlySpan<int> path) =>
        element is { } e ? e.Str(path) : null;

    public static int? Int(this JsonElement element, params ReadOnlySpan<int> path) =>
        element.At(path) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var v) ? v : null;

    public static int? Int(this JsonElement? element, params ReadOnlySpan<int> path) =>
        element is { } e ? e.Int(path) : null;

    public static decimal? Decimal(this JsonElement element, params ReadOnlySpan<int> path) =>
        element.At(path) is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var v) ? v : null;

    public static bool? Bool(this JsonElement element, params ReadOnlySpan<int> path) =>
        element.At(path) is { } b && b.ValueKind is JsonValueKind.True or JsonValueKind.False ? b.GetBoolean() : null;

    /// <summary>
    /// A flag Google writes as <c>true</c>/<c>false</c> in one answer and <c>1</c>/<c>0</c> in another (a search
    /// page's <c>ds:1</c> writes numbers where the data endpoint wrote booleans). Anything else is <c>null</c>.
    /// </summary>
    public static bool? Flag(this JsonElement element, params ReadOnlySpan<int> path) =>
        element.At(path) switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var v) && v is 0 or 1 => v == 1,
            _ => null,
        };

    /// <summary>The items of the array at <paramref name="path"/>; empty when it is missing or not an array.</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement element, params ReadOnlySpan<int> path) =>
        element.At(path) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static IEnumerable<JsonElement> Items(this JsonElement? element, params ReadOnlySpan<int> path) =>
        element is { } e ? e.Items(path) : [];

    /// <summary>A <c>[y, m, d]</c> date; <c>null</c> when missing or not a real date.</summary>
    public static DateOnly? Date(this JsonElement element, params ReadOnlySpan<int> path)
    {
        var d = element.At(path);
        return d.Int(0) is { } y and >= 1 and <= 9999 && d.Int(1) is { } m and >= 1 and <= 12
            && d.Int(2) is { } day && day >= 1 && day <= DateTime.DaysInMonth(y, m)
            ? new DateOnly(y, m, day)
            : null;
    }

    /// <summary>An <c>[h, m]</c> time; Google omits a zero minute (<c>[11]</c> is 11:00). <c>null</c> when not a real time.</summary>
    public static TimeOnly? Time(this JsonElement element, params ReadOnlySpan<int> path)
    {
        var t = element.At(path);
        if (t is not { ValueKind: JsonValueKind.Array })
            return null;
        var (h, m) = (t.Int(0) ?? 0, t.Int(1) ?? 0);
        return h is >= 0 and <= 23 && m is >= 0 and <= 59 ? new TimeOnly(h, m) : null;
    }
}
