using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TripIt;

namespace TripItMcp;

/// <summary>
/// MCP tools that expose the TripIt web SPA backend (api/v2/*).
/// Each method maps 1:1 to an endpoint observed in the HAR capture.
/// Returns JSON strings (re-serialized from the typed library DTOs) so the model can
/// consume the full response.
/// </summary>
[McpServerToolType]
public sealed class TripItTools(TripItClient client)
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, PrettyJson);

    // ── Trips ────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_list_trips")]
    [Description(
        "Lists the user's trips. Wraps GET /api/v2/list/trip. " +
        "Returns trip objects (id, display_name, start_date, end_date, primary_location, " +
        "is_private, image_url, etc).")]
    public async Task<string> ListTrips(
        [Description("If true, returns past trips. If false (default), returns upcoming trips.")]
        bool past = false,
        [Description("Page size; TripIt's default is 25.")]
        int? pageSize = null,
        [Description("1-based page number.")]
        int? pageNum = null,
        [Description(
            "Trip permission filter. Observed values: 'your_upcoming', 'your_past', " +
            "'your_shared', 'your_private'. Optional.")]
        string? tripPermissionFilter = null,
        [Description("Optional traveler id filter.")]
        string? traveler = null,
        [Description(
            "Comma-separated list of object types to exclude (e.g. 'WeatherObject,MapObject'). Optional.")]
        string? excludeTypes = null,
        [Description("If true, sort the trip list by start_date.")]
        bool? shouldSortTripsByDate = null)
    {
        var result = await client.ListTripsAsync(
            past, pageSize, pageNum, tripPermissionFilter, traveler, excludeTypes,
            shouldSortTripsByDate).ConfigureAwait(false);
        return ToJson(result);
    }

    // ── Profile ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_get_profile")]
    [Description(
        "Gets the signed-in user's TripIt profile. Wraps GET /api/v2/get/profile. " +
        "Returns profile attributes such as display_name, screen_name, public_display_name, " +
        "company, home_city, locale, profile_email_addresses, ts_pre_check_id, etc.")]
    public async Task<string> GetProfile()
        => ToJson(await client.GetProfileAsync().ConfigureAwait(false));

    [McpServerTool(Name = "tripit_get_traveler_profile")]
    [Description(
        "Gets the user's traveler-profile (passport, frequent traveler numbers, known traveler " +
        "numbers, redress numbers, meal preferences, seat preferences, etc). " +
        "Wraps GET /api/v2/travelerProfile/get.")]
    public async Task<string> GetTravelerProfile(
        [Description("If true, exclude template fields from the response.")]
        bool? excludeTemplates = null)
        => ToJson(await client.GetTravelerProfileAsync(excludeTemplates).ConfigureAwait(false));

    // ── Alerts / pro features ────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_list_pro_alerts")]
    [Description(
        "Lists active TripIt Pro alerts (price drops, refund alerts, fare watches, point tracker " +
        "notifications, etc). Wraps GET /api/v2/listProAlerts.")]
    public async Task<string> ListProAlerts()
        => ToJson(await client.ListProAlertsAsync().ConfigureAwait(false));

    [McpServerTool(Name = "tripit_get_purchased_products")]
    [Description(
        "Gets information about the user's TripIt subscriptions and one-time purchased products " +
        "(Pro tier, product entitlements, expiration dates). " +
        "Wraps GET /api/v2/purchasedProductInfo.")]
    public async Task<string> GetPurchasedProducts()
        => ToJson(await client.GetPurchasedProductsAsync().ConfigureAwait(false));

    // ── Application meta ─────────────────────────────────────────────────────

    [McpServerTool(Name = "tripit_get_app_config")]
    [Description(
        "Gets the TripIt web SPA's runtime feature flags / configuration. " +
        "Wraps GET /api/v2/appConfig. Useful for discovering which features are enabled for " +
        "the current account.")]
    public async Task<string> GetAppConfig()
        => ToJson(await client.GetAppConfigAsync().ConfigureAwait(false));

    [McpServerTool(Name = "tripit_get_gtm_data")]
    [Description(
        "Gets the analytics / Google Tag Manager dataLayer payload TripIt builds for the user " +
        "(includes user_id, member_status, and various flags). Wraps GET /api/v2/gtmDataAsJson.")]
    public async Task<string> GetGtmData()
        => ToJson(await client.GetGtmDataAsync().ConfigureAwait(false));

    // ── Diagnostics / generic escape hatch ───────────────────────────────────

    [McpServerTool(Name = "tripit_session_status")]
    [Description(
        "Returns whether the MCP server has a configured TripIt session and where it was loaded " +
        "from. Does NOT return the cookie value.")]
    public string SessionStatus()
        => JsonSerializer.Serialize(new
        {
            configured = client.IsConfigured,
            source = client.SessionSource,
        }, PrettyJson);

    [McpServerTool(Name = "tripit_raw_get")]
    [Description(
        "Escape hatch: performs an authenticated GET against an arbitrary path on www.tripit.com " +
        "and returns the response body. Use this only when no dedicated tool exists for the " +
        "endpoint you need. The path must begin with '/' (e.g. '/api/v2/list/trip').")]
    public Task<string> RawGet(
        [Description("URL path beginning with '/' (no scheme/host).")]
        string path,
        [Description(
            "Optional query parameters as a JSON object, e.g. {\"past\":\"true\",\"page_size\":\"50\"}.")]
        string? queryJson = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            throw new ArgumentException("path must start with '/'");

        List<KeyValuePair<string, string?>>? q = null;
        if (!string.IsNullOrWhiteSpace(queryJson))
        {
            using var doc = JsonDocument.Parse(queryJson);
            q = new List<KeyValuePair<string, string?>>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                q.Add(new(p.Name, p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString()
                    : p.Value.ToString()));
            }
        }
        return client.SendRawAsync(path, q);
    }
}
