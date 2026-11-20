using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Time.Testing;

namespace Multiplex.Sites.GoogleFlights.Tests;

/// <summary>The client's own behavior, against a fake Google that replays recorded answers.</summary>
public class GoogleFlightsClientTests
{
    private static readonly DateOnly Dec15 = new(2026, 12, 15);
    private static readonly DateOnly Dec22 = new(2026, 12, 22);
    private static readonly HarFixture Flow = HarFixture.Load("direct-http-flow-2026-11-20");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));
    private readonly FakeGoogle _google = new();

    private GoogleFlightsClient Client(Action<GoogleFlightsClientOptions>? configure = null)
    {
        // Generous pacing so tests are not slowed by it; pacing has its own test.
        var options = new GoogleFlightsClientOptions { BurstRequests = 100, MaxRetries = 0 };
        configure?.Invoke(options);
        return new GoogleFlightsClient(options, _time, _google);
    }

    private static FlightSearchRequest SeaDen => FlightSearchRequest.RoundTrip("SEA", "DEN", Dec15, Dec22);

    /// <summary>For the tests on the real clock (retry delays): dates that stay in the future.</summary>
    private static FlightSearchRequest SoonRoundTrip => FlightSearchRequest.RoundTrip("SEA", "DEN",
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(37)));

    [Fact]
    public async Task A_search_identifies_itself_honestly_and_sends_only_what_Google_needs()
    {
        _google.Answer(Flow[0]);
        using var client = Client();

        await client.SearchAsync(SeaDen);

        var sent = Assert.Single(_google.Sent);
        Assert.Equal("GetShoppingResults", sent.Rpc);
        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(sent.Headers["User-Agent"], @"^Mozilla/5\.0 \(compatible; Multiplex-GoogleFlights/\d+\.\d+[^;]*; \+https://\S+\)$"),
            $"User-Agent was: {sent.Headers["User-Agent"]}");
        Assert.Equal("1", sent.Headers["x-same-domain"]);
        Assert.Equal("application/x-www-form-urlencoded;charset=UTF-8", sent.Headers["Content-Type"]);
        Assert.Equal(Flow[0].RequestHeader("x-goog-ext-259736195-jspb"), sent.Headers["x-goog-ext-259736195-jspb"]);
        Assert.Equal(Flow[0].RequestBody, sent.Body);
        // Nothing a browser or the page would add: no anti-bot token, no page session, no client hints.
        Assert.DoesNotContain(sent.Headers.Keys, k => k.StartsWith("x-goog-batchexecute", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("sec-", StringComparison.OrdinalIgnoreCase) || k.Equals("x-client-data", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("f.sid", sent.Url.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_returns_flights_with_when_and_whether_from_cache()
    {
        _google.Answer(Flow[0]);
        using var client = Client();

        var result = await client.SearchAsync(SeaDen);

        Assert.Equal(13, result.AllFlights.Count());
        Assert.Equal(0, result.Leg);
        Assert.Equal(_time.GetUtcNow(), result.RetrievedAt);
        Assert.False(result.FromCache);
        Assert.Equal("www.google.com", result.GoogleFlightsUrl.Host);
        Assert.Contains("tfs=", result.GoogleFlightsUrl.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_search_again_within_the_cache_duration_is_answered_from_the_cache()
    {
        _google.Answer(Flow[0]);
        using var client = Client();
        var first = await client.SearchAsync(SeaDen);
        _time.Advance(TimeSpan.FromMinutes(11));

        // Written differently, normalized the same: lower-case codes.
        var second = await client.SearchAsync(FlightSearchRequest.RoundTrip("sea", "den", Dec15, Dec22));

        Assert.Single(_google.Sent);
        Assert.True(second.FromCache);
        Assert.Equal(first.RetrievedAt, second.RetrievedAt);
        Assert.Equal(first.AllFlights.Count(), second.AllFlights.Count());
    }

    [Fact]
    public async Task Verify_asks_Google_again_and_refreshes_the_cache()
    {
        _google.Answer(Flow[0]).Answer(Flow[0]);
        using var client = Client();
        await client.SearchAsync(SeaDen);
        _time.Advance(TimeSpan.FromMinutes(1));

        var verified = await client.SearchAsync(SeaDen, verify: true);

        Assert.Equal(2, _google.Sent.Count);
        Assert.False(verified.FromCache);
        Assert.Equal(_time.GetUtcNow(), verified.RetrievedAt);
        var cached = await client.SearchAsync(SeaDen);
        Assert.Equal(verified.RetrievedAt, cached.RetrievedAt);
    }

    [Fact]
    public async Task An_answer_older_than_the_cache_duration_is_fetched_again()
    {
        _google.Answer(Flow[0]).Answer(Flow[0]);
        using var client = Client(o => o.CacheDuration = TimeSpan.FromMinutes(12));
        await client.SearchAsync(SeaDen);
        _time.Advance(TimeSpan.FromMinutes(12));

        var later = await client.SearchAsync(SeaDen);

        Assert.Equal(2, _google.Sent.Count);
        Assert.False(later.FromCache);
    }

    [Fact]
    public async Task A_different_currency_is_a_different_request_and_a_different_cache_entry()
    {
        _google.Answer(Flow[0]).Answer(Flow[0]);
        using var client = Client();
        await client.SearchAsync(SeaDen);

        await client.SearchAsync(SeaDen with { Currency = "eur" });

        Assert.Equal(2, _google.Sent.Count);
        Assert.Equal("""["en-US","US","EUR",2,null,[480],null,null,7,[]]""", _google.Sent[1].Headers["x-goog-ext-259736195-jspb"]);
    }

    [Fact]
    public async Task The_return_step_and_booking_follow_the_chosen_selection()
    {
        _google.Answer(Flow[0]).Answer(Flow[1]).Answer(Flow[2]);
        using var client = Client();

        var outbound = (await client.SearchAsync(SeaDen)).AllFlights.First(o => o.Flights.SequenceEqual(["DL1358"]));
        var returns = await client.GetReturnFlightsAsync(outbound.Selection);
        var back = returns.AllFlights.First(o => o.Flights.SequenceEqual(["DL1731"]));
        var booking = await client.GetBookingOptionsAsync(back.Selection);

        Assert.Equal(1, returns.Leg);
        // Byte for byte what F2 and F3 sent, and Google answered.
        Assert.Equal(Flow[1].InnerRequest, _google.Sent[1].InnerRequest);
        Assert.Equal("GetBookingResults", _google.Sent[2].Rpc);
        Assert.Equal(Flow[2].InnerRequest, _google.Sent[2].InnerRequest);
        Assert.Equal(7, booking.Options.Count);
        Assert.Equal("/travel/flights/booking", booking.GoogleFlightsUrl.AbsolutePath);
    }

    [Fact]
    public async Task Booking_options_are_never_served_from_the_cache()
    {
        _google.Answer(Flow[0]).Answer(Flow[2]).Answer(Flow[2]);
        using var client = Client();
        var oneWay = await client.SearchAsync(FlightSearchRequest.OneWay("SEA", "DEN", Dec15));
        var chosen = oneWay.AllFlights.First().Selection;

        await client.GetBookingOptionsAsync(chosen);
        await client.GetBookingOptionsAsync(chosen);

        Assert.Equal(2, _google.Sent.Count(s => s.Rpc == "GetBookingResults"));
    }

    [Fact]
    public async Task Asking_for_return_flights_once_every_leg_is_chosen_is_refused_before_any_request()
    {
        using var client = Client();
        var request = FlightSearchRequest.OneWay("SEA", "DEN", Dec15).Normalize("USD", "US");
        var complete = new FlightSelection(request, [[new ChosenSegment("SEA", Dec15, "DEN", "DL", "1")]], "t");
        var incomplete = new FlightSelection(SeaDen.Normalize("USD", "US"), [[new ChosenSegment("SEA", Dec15, "DEN", "DL", "1")]], "t");

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetReturnFlightsAsync(complete));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetBookingOptionsAsync(incomplete));
        Assert.Empty(_google.Sent);
    }

    [Theory]
    [InlineData("SEAT", "DEN")]
    [InlineData("SEA", "SEA")]
    [InlineData("", "DEN")]
    public async Task An_invalid_search_is_refused_before_any_request(string origin, string destination)
    {
        using var client = Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(FlightSearchRequest.OneWay(origin, destination, Dec15)));
        Assert.Empty(_google.Sent);
    }

    [Fact]
    public async Task A_refusal_stops_every_request_until_the_cool_down_ends()
    {
        _google.Answer(HarFixture.Load("direct-search-refused-2026-11-19")[0]).Answer(Flow[0]);
        using var client = Client(o => o.RefusalCooldown = TimeSpan.FromMinutes(15));

        var refused = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));
        Assert.Equal(ThrottleReason.Refused, refused.Reason);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(15), refused.RetryAfter);

        _time.Advance(TimeSpan.FromMinutes(14));
        var cooling = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() =>
            client.LookupAirportsAsync("Denver"));
        Assert.Equal(ThrottleReason.CoolingDown, cooling.Reason);
        Assert.Single(_google.Sent);

        _time.Advance(TimeSpan.FromMinutes(1));
        var answered = await client.SearchAsync(SeaDen);
        Assert.Equal(13, answered.AllFlights.Count());
    }

    [Fact]
    public async Task Refusals_in_a_row_double_the_cool_down()
    {
        var refusal = HarFixture.Load("round-trip-search-refused-2026-11-19")[0];
        _google.Answer(refusal).Answer(refusal);
        using var client = Client(o => o.RefusalCooldown = TimeSpan.FromMinutes(15));

        await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));
        _time.Advance(TimeSpan.FromMinutes(15));
        var second = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));

        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(30), second.RetryAfter);
    }

    [Fact]
    public async Task Too_many_requests_honours_retry_after_and_is_not_retried()
    {
        _google.Answer(HttpStatusCode.TooManyRequests, "", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(2)));
        using var client = Client(o => o.MaxRetries = 2);

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));

        Assert.Equal(ThrottleReason.TooManyRequests, e.Reason);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromHours(2), e.RetryAfter);
        Assert.Single(_google.Sent);
    }

    [Fact]
    public async Task The_unusual_traffic_check_is_left_to_a_person()
    {
        _google.Answer(HttpStatusCode.Found, "", r => r.Headers.Location = new Uri("https://www.google.com/sorry/index?continue=x"));
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));

        Assert.Equal(ThrottleReason.Challenge, e.Reason);
        Assert.Single(_google.Sent);
    }

    [Fact]
    public async Task A_server_error_is_retried_then_reported_as_transient()
    {
        _google.Answer(HttpStatusCode.ServiceUnavailable).Answer(HttpStatusCode.ServiceUnavailable).Answer(HttpStatusCode.ServiceUnavailable);
        using var client = new GoogleFlightsClient(
            new GoogleFlightsClientOptions { BurstRequests = 100, MaxRetries = 2, RetryDelay = TimeSpan.FromMilliseconds(1) },
            TimeProvider.System, _google);

        await Assert.ThrowsAsync<GoogleFlightsTransientException>(() => client.SearchAsync(SoonRoundTrip));

        Assert.Equal(3, _google.Sent.Count);
    }

    [Fact]
    public async Task A_server_error_then_an_answer_succeeds()
    {
        _google.Answer(HttpStatusCode.BadGateway).Answer(Flow[0]);
        using var client = new GoogleFlightsClient(
            new GoogleFlightsClientOptions { BurstRequests = 100, MaxRetries = 2, RetryDelay = TimeSpan.FromMilliseconds(1) },
            TimeProvider.System, _google);

        var result = await client.SearchAsync(SoonRoundTrip);

        Assert.Equal(13, result.AllFlights.Count());
    }

    [Fact]
    public async Task An_invalid_argument_from_Google_is_a_request_error()
    {
        _google.Answer(HttpStatusCode.OK, ")]}'\n\n39\n[[\"wrb.fr\",null,null,null,null,[3]]]\n");
        using var client = Client();

        await Assert.ThrowsAsync<GoogleFlightsRequestException>(() => client.SearchAsync(SeaDen));
    }

    [Fact]
    public async Task Requests_beyond_the_pacing_budget_are_held_back_without_reaching_Google()
    {
        _google.Answer(Flow[0]);
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromHours(1), QueueLimit = 0, AutoReplenishment = false,
        });
        using var client = Client(o => o.RateLimiter = limiter);
        await client.SearchAsync(SeaDen);

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() =>
            client.SearchAsync(FlightSearchRequest.OneWay("SEA", "LAX", Dec15)));

        Assert.Equal(ThrottleReason.Paced, e.Reason);
        Assert.Single(_google.Sent);
    }

    [Fact]
    public async Task Identical_searches_at_the_same_time_share_one_request()
    {
        _google.Answer(Flow[0]);
        _google.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client();

        var a = client.SearchAsync(SeaDen);
        var b = client.SearchAsync(SeaDen);
        _google.Gate.SetResult();
        await Task.WhenAll(a, b);

        Assert.Single(_google.Sent);
    }

    [Fact]
    public async Task A_request_waiting_its_turn_when_Google_refuses_is_never_sent()
    {
        // Review finding I1: the cool-down is checked again after the pacing wait, not only on entry.
        _google.Answer(HarFixture.Load("direct-search-refused-2026-11-19")[0]).Answer(Flow[0]);
        _google.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            // Replenished by hand below; a manual top-up still waits out the period, so keep it short.
            TokenLimit = 1, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromMilliseconds(1), QueueLimit = 1, AutoReplenishment = false,
        });
        using var client = Client(o => o.RateLimiter = limiter);

        var first = client.SearchAsync(SeaDen);
        await Until(() => _google.Sent.Count == 1);
        var queued = client.SearchAsync(FlightSearchRequest.OneWay("SEA", "LAX", Dec15));
        await Until(() => limiter.GetStatistics()!.CurrentQueuedCount == 1);
        _google.Gate.SetResult();
        await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => first);
        limiter.TryReplenish();

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ThrottleReason.CoolingDown, e.Reason);
        Assert.Single(_google.Sent);
    }

    [Fact]
    public async Task One_caller_giving_up_does_not_fail_another_caller_sharing_the_request()
    {
        // Review finding I2.
        _google.Answer(Flow[0]);
        _google.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client();
        using var giveUp = new CancellationTokenSource();

        var leaving = client.SearchAsync(SeaDen, ct: giveUp.Token);
        await Until(() => _google.Sent.Count == 1);
        var staying = client.SearchAsync(SeaDen);
        giveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        _google.Gate.SetResult();

        Assert.Equal(13, (await staying).AllFlights.Count());
        Assert.Single(_google.Sent);
    }

    [Fact]
    public async Task Forbidden_is_a_refusal_with_a_cool_down()
    {
        _google.Answer(HttpStatusCode.Forbidden, "<html>blocked</html>");
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));
        Assert.Equal(ThrottleReason.Refused, e.Reason);

        var again = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));
        Assert.Equal(ThrottleReason.CoolingDown, again.Reason);
        Assert.Single(_google.Sent);
    }

    [Fact]
    public async Task A_retry_after_beyond_the_longest_cool_down_is_capped()
    {
        _google.Answer(HttpStatusCode.TooManyRequests, "", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(3650)));
        using var client = Client(o => o.MaxRefusalCooldown = TimeSpan.FromHours(6));

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));

        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromHours(6), e.RetryAfter);
    }

    [Fact]
    public async Task An_error_code_the_client_does_not_know_is_treated_as_a_refusal()
    {
        // 2026-11-20: a refused network answered one search with gRPC 9, never seen before. Unknown means "stop".
        _google.Answer(HttpStatusCode.OK, ")]}'\n\n38\n[[\"wrb.fr\",null,null,null,null,[9]]]\n");
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));
        Assert.Equal(ThrottleReason.Refused, e.Reason);
        Assert.Contains("gRPC 9", e.Message, StringComparison.Ordinal);

        var again = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));
        Assert.Equal(ThrottleReason.CoolingDown, again.Reason);
        Assert.Single(_google.Sent);
    }

    [Fact]
    public async Task A_throttle_status_wins_over_a_body_that_does_not_parse()
    {
        // Re-review suggestion: a 429 whose body looks framed but is garbled is still a throttle, with a cool-down.
        _google.Answer(HttpStatusCode.TooManyRequests, ")]}'\n\n30\n[[\"wrb.fr\",null,\"[1,2,\",null]]\n");
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));

        Assert.Equal(ThrottleReason.TooManyRequests, e.Reason);
    }

    [Fact]
    public async Task An_unusual_traffic_page_is_a_challenge()
    {
        _google.Answer(HttpStatusCode.OK, "<html><body>Our systems have detected unusual traffic from your computer network.</body></html>");
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));

        Assert.Equal(ThrottleReason.Challenge, e.Reason);
    }

    [Fact]
    public async Task A_garbled_data_item_is_a_format_error()
    {
        _google.Answer(HttpStatusCode.OK, ")]}'\n\n30\n[[\"wrb.fr\",null,\"[1,2,\",null]]\n");
        using var client = Client();

        await Assert.ThrowsAsync<GoogleFlightsFormatException>(() => client.SearchAsync(SeaDen));
    }

    [Theory]
    [InlineData(-3, "in the past")]
    [InlineData(400, "more than a year away")]
    public async Task A_date_Google_does_not_sell_is_refused_before_any_request(int daysFromToday, string message)
    {
        using var client = Client();
        var date = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime).AddDays(daysFromToday);

        var e = await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(FlightSearchRequest.OneWay("SEA", "DEN", date)));

        Assert.Contains(message, e.Message, StringComparison.Ordinal);
        Assert.Empty(_google.Sent);
    }

    [Fact]
    public async Task Too_many_legs_or_airlines_are_refused_before_any_request()
    {
        using var client = Client();
        var six = Enumerable.Range(0, 6).Select(i => new FlightLeg(i % 2 == 0 ? "SEA" : "DEN", i % 2 == 0 ? "DEN" : "SEA", Dec15.AddDays(i)));
        var airlines = new LegFilters { Airlines = [.. Enumerable.Range(0, 11).Select(i => $"A{i % 10}").Distinct(), "ZZ", "YY"] };

        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(FlightSearchRequest.MultiCity(six)));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(FlightSearchRequest.OneWay("SEA", "DEN", Dec15, airlines)));
        Assert.Empty(_google.Sent);
    }

    [Fact]
    public async Task Calendar_spans_wider_than_the_page_asks_for_are_refused_before_any_request()
    {
        using var client = Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetCalendarAsync(new CalendarRequest(SeaDen, Dec15, Dec15.AddDays(61))));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetCalendarAsync(
            new CalendarRequest(SeaDen, Dec15, Dec15.AddDays(7)) { ReturnFrom = Dec22, ReturnTo = Dec22.AddDays(2) }));
        Assert.Empty(_google.Sent);
    }

    [Fact]
    public async Task A_selection_holding_a_flight_not_in_Googles_form_is_refused_before_any_request()
    {
        using var client = Client();
        var request = SeaDen.Normalize("USD", "US");

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetReturnFlightsAsync(
            new FlightSelection(request, [[new ChosenSegment("SEA", Dec15, "DEN", "DELTA", "1358")]], "token")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetReturnFlightsAsync(
            new FlightSelection(request, [[new ChosenSegment("SEA", Dec15, "DEN", "DL", "1358")]], "not a token\"]")));
        Assert.Empty(_google.Sent);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "The condition was not reached in time.");
    }

    [Fact]
    public async Task A_round_trip_price_graph_keeps_the_trip_length()
    {
        _google.Answer(Flow[3]);
        using var client = Client();

        var calendar = await client.GetCalendarAsync(new CalendarRequest(SeaDen, new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 20)));

        Assert.Equal(Flow[3].InnerRequest, _google.Sent[0].InnerRequest);
        Assert.Equal(11, calendar.Prices.Count);
        // Google shows 163; the cell's token carries the exact fare.
        Assert.Equal(new Money(162.96m, "USD"), calendar.Cheapest!.Price);
    }

    [Fact]
    public async Task A_one_way_calendar_is_refused_before_any_request()
    {
        // The one form tried was refused by Google (bare gRPC 13, live 2026-11-20); the page's own is not recorded.
        using var client = Client();

        var e = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetCalendarAsync(new CalendarRequest(FlightSearchRequest.OneWay("SEA", "DEN", Dec15), Dec15, Dec22)));

        Assert.Contains("round trips only", e.Message, StringComparison.Ordinal);
        Assert.Empty(_google.Sent);
    }

    [Fact]
    public async Task A_multi_city_trip_has_no_calendar()
    {
        using var client = Client();
        var multi = FlightSearchRequest.MultiCity(new FlightLeg("SEA", "DEN", Dec15), new FlightLeg("DEN", "LAX", Dec22));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetCalendarAsync(new CalendarRequest(multi, Dec15, Dec22)));
        Assert.Empty(_google.Sent);
    }

    [Fact]
    public async Task An_airport_lookup_goes_through_batchexecute()
    {
        _google.Answer(HarFixture.Load("airport-autocomplete-2026-11-19")[12]);
        using var client = Client();

        var lookup = await client.LookupAirportsAsync("  Denv ");

        Assert.Equal("H028ib", _google.Sent[0].Rpc);
        Assert.Equal("""["Denv",[1,2,3,5],null,[2],1]""", _google.Sent[0].InnerRequest);
        Assert.Equal("DEN", lookup.Airports.First().Code);
    }
}
