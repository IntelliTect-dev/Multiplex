using System.Globalization;
using System.Text.Json;

namespace Multiplex.Web.Services.LinkedIn;

/// <summary>
/// One signed-in LinkedIn browser session, as the sign-in captured it: the Cookie header, the
/// CSRF token (LinkedIn's <c>csrf-token</c> header is the <c>JSESSIONID</c> cookie's value), the
/// headers the LinkedIn page itself sent with its API requests, and the member's own profile id
/// (the mailbox every messaging request names). A client builds every request from one session.
/// </summary>
/// <remarks><see cref="ToString"/> never prints the cookie or the token.</remarks>
public sealed record LinkedInSession
{
    public static LinkedInSession Empty { get; } = new();

    /// <summary>The full Cookie header value.</summary>
    public string Cookie { get; init; } = "";

    /// <summary>The <c>csrf-token</c> header value (the <c>JSESSIONID</c> cookie, without quotes).</summary>
    public string CsrfToken { get; init; } = "";

    /// <summary>The signed-in member's profile id, e.g. <c>ACoAA…</c> (the part after <c>urn:li:fsd_profile:</c>).</summary>
    public string ProfileId { get; init; } = "";

    /// <summary>Browser headers to replay (user agent, client hints, x-li-track, …); never cookie or token.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? CapturedAt { get; init; }

    public bool IsConfigured => Cookie.Length > 0 && CsrfToken.Length > 0 && ProfileId.Length > 0;

    /// <summary><c>urn:li:fsd_profile:{ProfileId}</c>: the member's mailbox.</summary>
    public string MailboxUrn => "urn:li:fsd_profile:" + ProfileId;

    public override string ToString() =>
        $"LinkedInSession {{ ProfileId = {ProfileId}, Headers = {Headers.Count}, CapturedAt = {CapturedAt:O} }}";

    /// <summary>
    /// Parses the snapshot the sign-in stores: <c>{ "cookie", "csrf_token", "profile_id",
    /// "headers": { name: value }, "captured_at" }</c>. The error never contains a value.
    /// </summary>
    public static bool TryFromSnapshotJson(string? json, out LinkedInSession session, out string? error)
    {
        session = Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "the snapshot is empty";
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the snapshot is not a JSON object";
                return false;
            }
            string Str(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in h.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String && !IsSecretHeader(p.Name)) headers[p.Name] = p.Value.GetString()!;
                }
            }
            DateTimeOffset? capturedAt = DateTimeOffset.TryParse(Str("captured_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var at) ? at : null;

            session = new LinkedInSession
            {
                Cookie = Str("cookie"),
                CsrfToken = Str("csrf_token").Trim('"'),
                ProfileId = Str("profile_id"),
                Headers = headers,
                CapturedAt = capturedAt,
            };
            if (!session.IsConfigured)
            {
                error = "the snapshot lacks the cookie, the csrf token or the profile id";
                session = Empty;
                return false;
            }
            return true;
        }
        catch (JsonException)
        {
            error = "the snapshot is not valid JSON";
            return false;
        }
    }

    /// <summary>Headers a session never replays from the snapshot: the client sets these itself.</summary>
    internal static bool IsSecretHeader(string name) =>
        name.Equals("cookie", StringComparison.OrdinalIgnoreCase)
        || name.Equals("csrf-token", StringComparison.OrdinalIgnoreCase)
        || name.Equals("authorization", StringComparison.OrdinalIgnoreCase);
}
