using System.Text.Json;

namespace Multiplex.Web.SignIn;

/// <summary>Input the phone page sends over the live WebSocket. Coordinates are 0..1 of the mirrored frame.</summary>
public abstract record MirrorInput;

public sealed record TapInput(double X, double Y) : MirrorInput;

public sealed record ScrollInput(double X, double Y, double DeltaX, double DeltaY) : MirrorInput;

public sealed record TextInput(string Text) : MirrorInput;

public sealed record KeyInput(string Key) : MirrorInput;

public sealed record BackInput : MirrorInput;

public sealed record ReloadInput : MirrorInput;

/// <summary>The size, in CSS pixels, of the area the phone shows the mirror in.</summary>
public sealed record ViewportInput(int Width, int Height) : MirrorInput;

/// <summary>
/// The live WebSocket protocol. Client → server: small JSON text messages (parsed here,
/// everything else ignored). Server → client: binary messages are JPEG frames; text messages
/// are JSON <c>{t:"status"|"url"|"blocked", …}</c>. Payloads are never logged: they carry
/// what the person types, including passwords.
/// </summary>
public static class MirrorProtocol
{
    public const int MaxMessageBytes = 8 * 1024;

    /// <summary>Close code for a page whose place was taken by a newer connection (another tab); it must not reconnect.</summary>
    public const System.Net.WebSockets.WebSocketCloseStatus ReplacedCloseStatus = (System.Net.WebSockets.WebSocketCloseStatus)4000;
    public const int MaxTextLength = 512;

    /// <summary>The only keys the phone may press; names as Puppeteer's keyboard knows them.</summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "Enter", "Backspace", "Tab", "Escape", "Delete", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
    };

    public static MirrorInput? Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > MaxMessageBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var type)
                || type.ValueKind != JsonValueKind.String)
                return null;

            return type.GetString() switch
            {
                "tap" when Fraction(root, "x") is { } x && Fraction(root, "y") is { } y => new TapInput(x, y),
                "scroll" when Fraction(root, "x") is { } x && Fraction(root, "y") is { } y =>
                    new ScrollInput(x, y, Delta(root, "dx"), Delta(root, "dy")),
                "text" when root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.String
                            && v.GetString() is { Length: > 0 and <= MaxTextLength } text => new TextInput(text),
                "key" when root.TryGetProperty("k", out var k) && k.ValueKind == JsonValueKind.String
                           && AllowedKeys.Contains(k.GetString()!) => new KeyInput(k.GetString()!),
                "back" => new BackInput(),
                "reload" => new ReloadInput(),
                "viewport" when Int(root, "w") is { } w && Int(root, "h") is { } h => new ViewportInput(w, h),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? Fraction(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d)
        && double.IsFinite(d) && d is >= 0 and <= 1
            ? d
            : null;

    /// <summary>A scroll distance in fractions of the frame, clamped to two screens.</summary>
    private static double Delta(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && double.IsFinite(d)
            ? Math.Clamp(d, -2, 2)
            : 0;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) ? i : null;

    public static string Status(string state, string message, string? next = null) =>
        JsonSerializer.Serialize(new { t = "status", s = state, msg = message, next });

    public static string Url(string host, bool secure) => JsonSerializer.Serialize(new { t = "url", host, secure });

    public static string Blocked(string host) => JsonSerializer.Serialize(new { t = "blocked", host });

    public static string Viewport(int width, int height) => JsonSerializer.Serialize(new { t = "viewport", w = width, h = height });
}
