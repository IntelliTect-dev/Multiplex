using System.Globalization;
using System.Text.Json;

namespace TripIt;

/// <summary>
/// An immutable snapshot of a signed-in TripIt browser session: the Cookie header, the
/// <c>X-CSRF-Token-WA</c> value and the SPA's browser headers (user agent, client hints,
/// accept-language). A <see cref="TripItClient"/> builds every request from one snapshot, so
/// the cookie, the CSRF token and the headers always belong together; a refreshed session is
/// a new snapshot swapped in whole by an <see cref="ITripItSessionProvider"/>.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> never prints the cookie or the CSRF token, so a snapshot is safe to
/// pass to a logger.
/// </remarks>
public sealed record TripItSession
{
    private static readonly IReadOnlyDictionary<string, string> s_noHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A session with no credentials (<see cref="IsConfigured"/> is false).</summary>
    public static TripItSession Empty { get; } = new();

    public TripItSession()
    {
    }

    public TripItSession(string cookie, string csrfToken,
        IReadOnlyDictionary<string, string>? spaHeaders = null, string? source = null, DateTimeOffset? capturedAt = null)
    {
        Cookie = cookie ?? "";
        CsrfToken = csrfToken ?? "";
        SpaHeaders = spaHeaders is null
            ? s_noHeaders
            : new Dictionary<string, string>(spaHeaders, StringComparer.OrdinalIgnoreCase);
        Source = source;
        CapturedAt = capturedAt;
    }

    /// <summary>The full Cookie header value.</summary>
    public string Cookie { get; init; } = "";

    /// <summary>The SPA's CSRF token, sent as <c>X-CSRF-Token-WA</c>. May be empty.</summary>
    public string CsrfToken { get; init; } = "";

    /// <summary>
    /// Browser headers captured alongside the cookies (<c>spa_headers</c>): user agent, client
    /// hints, accept-language. Names are compared case-insensitively. Empty when unknown.
    /// </summary>
    public IReadOnlyDictionary<string, string> SpaHeaders { get; init; } = s_noHeaders;

    /// <summary>When the snapshot was read from the browser, if known.</summary>
    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>Where the snapshot came from (a file path, "environment", a store); never a value.</summary>
    public string? Source { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Cookie);

    /// <summary>
    /// Parses the snapshot JSON that <c>tools/extract-session.js</c> and the Multiplex sign-in
    /// write: <c>{ "cookie", "csrf_token", "spa_headers": { name: value }, "captured_at" }</c>.
    /// Other properties are ignored. Parsing uses <see cref="JsonDocument"/> only (no
    /// reflection-based serialization).
    /// </summary>
    /// <exception cref="TripItSessionFormatException">
    /// The JSON is malformed or has no cookie. The message says what is wrong but never
    /// contains a cookie, token or header value.
    /// </exception>
    public static TripItSession FromSnapshotJson(string json, string? source = null)
        => TryFromSnapshotJson(json, source, out var session, out var error)
            ? session
            : throw new TripItSessionFormatException(error!);

    /// <summary>Like <see cref="FromSnapshotJson"/>, returning the reason (without values) instead of throwing.</summary>
    public static bool TryFromSnapshotJson(string? json, string? source, out TripItSession session, out string? error)
    {
        session = Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The session snapshot is empty.";
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // JsonException.Message can quote the offending character; report its position only.
            error = $"The session snapshot is not valid JSON (line {ex.LineNumber + 1}, byte {ex.BytePositionInLine + 1}).";
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"The session snapshot must be a JSON object, not {doc.RootElement.ValueKind}.";
                return false;
            }

            string? cookie = null, csrf = null;
            DateTimeOffset? capturedAt = null;
            Dictionary<string, string>? headers = null;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                switch (p.Name.ToLowerInvariant())
                {
                    case "cookie":
                        if (p.Value.ValueKind != JsonValueKind.String)
                        {
                            error = "The session snapshot's 'cookie' must be a string.";
                            return false;
                        }
                        cookie = p.Value.GetString();
                        break;
                    case "csrf_token":
                        if (p.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        {
                            error = "The session snapshot's 'csrf_token' must be a string.";
                            return false;
                        }
                        csrf = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                        break;
                    case "spa_headers":
                        if (p.Value.ValueKind == JsonValueKind.Null) break;
                        if (p.Value.ValueKind != JsonValueKind.Object)
                        {
                            error = "The session snapshot's 'spa_headers' must be an object.";
                            return false;
                        }
                        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var h in p.Value.EnumerateObject())
                        {
                            // Header values are strings in every capture; skip anything else.
                            if (h.Value.ValueKind == JsonValueKind.String) headers[h.Name] = h.Value.GetString()!;
                        }
                        break;
                    case "captured_at":
                        // Informational only: an unreadable timestamp does not invalidate the cookies.
                        if (p.Value.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(p.Value.GetString(), CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                        {
                            capturedAt = at;
                        }
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(cookie))
            {
                error = "The session snapshot has no 'cookie'.";
                return false;
            }
            session = new TripItSession(cookie, csrf ?? "", headers, source, capturedAt);
            return true;
        }
    }

    /// <summary>Describes the snapshot without its cookie, token or header values.</summary>
    public override string ToString()
        => $"TripItSession {{ Source = {Source ?? "(none)"}, IsConfigured = {IsConfigured}, " +
           $"CapturedAt = {CapturedAt?.ToString("o", CultureInfo.InvariantCulture) ?? "(unknown)"}, Headers = {SpaHeaders.Count} }}";
}

/// <summary>A session snapshot could not be read. The message never contains secret values.</summary>
public sealed class TripItSessionFormatException : FormatException
{
    public TripItSessionFormatException(string message) : base(message) { }
}
