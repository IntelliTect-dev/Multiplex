using Xunit;

namespace TripIt.FunctionalTests;

/// <summary>
/// Functional tests that hit the live TripIt backend. Each test is a
/// <see cref="SkippableFactAttribute"/> so the suite passes locally even when no
/// session is configured (e.g. in CI without secrets).
/// </summary>
public sealed class TripItClientTests : IClassFixture<TripItSessionFixture>
{
    private readonly TripItSessionFixture _fx;

    public TripItClientTests(TripItSessionFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task GetProfileAsync_returns_populated_profile()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var profile = await _fx.Client.GetProfileAsync();
        Assert.NotNull(profile);
        Assert.False(string.IsNullOrWhiteSpace(profile.DisplayName) &&
                     string.IsNullOrWhiteSpace(profile.ScreenName),
            "Expected at least display_name or screen_name to be populated.");
    }

    [SkippableFact]
    public async Task ListTripsAsync_returns_envelope()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var trips = await _fx.Client.ListTripsAsync(pageSize: 5);
        Assert.NotNull(trips);
        // Items may legitimately be empty (no trips); just verify no exception.
    }

    [SkippableFact]
    public async Task GetTravelerProfileAsync_returns_object()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var tp = await _fx.Client.GetTravelerProfileAsync();
        Assert.NotNull(tp);
    }

    [SkippableFact]
    public async Task ListProAlertsAsync_returns_envelope()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var alerts = await _fx.Client.ListProAlertsAsync();
        Assert.NotNull(alerts);
    }

    [SkippableFact]
    public async Task GetPurchasedProductsAsync_returns_object()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var products = await _fx.Client.GetPurchasedProductsAsync();
        Assert.NotNull(products);
    }

    [SkippableFact]
    public async Task GetAppConfigAsync_returns_object()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var cfg = await _fx.Client.GetAppConfigAsync();
        Assert.NotNull(cfg);
    }

    [SkippableFact]
    public async Task GetGtmDataAsync_returns_object()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var gtm = await _fx.Client.GetGtmDataAsync();
        Assert.NotNull(gtm);
    }

    [SkippableFact]
    public async Task SendRawAsync_returns_nonempty_body_for_known_endpoint()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var body = await _fx.Client.SendRawAsync("/api/v2/get/profile");
        Assert.False(string.IsNullOrWhiteSpace(body));
        Assert.StartsWith("{", body.TrimStart());
    }
}
