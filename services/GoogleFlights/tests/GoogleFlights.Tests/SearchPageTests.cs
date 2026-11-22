using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Multiplex.Sites.GoogleFlights.Tests;

/// <summary>
/// Flights from Google Flights' search pages, fetched over plain HTTPS. A search page carries its first answer in
/// its HTML (data block <c>ds:1</c>). The page fixtures are the <c>ds:1</c> elements of pages the client fetched
/// live on 2026-11-22 (protocol.md section 2, H1 and H2).
/// </summary>
public class SearchPageTests
{
    private static readonly DateOnly Dec15 = new(2026, 12, 15);
    private static readonly DateOnly Dec22 = new(2026, 12, 22);
    private static readonly ChosenSegment Dl2759Out = new("SEA", Dec15, "DEN", "DL", "2759");
    private static readonly ChosenSegment Dl1731Back = new("DEN", Dec22, "SEA", "DL", "1731");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 20, 0, 0, TimeSpan.Zero));
    private readonly FakeGoogle _google = new();

    private GoogleFlightsClient Client() =>
        new(new GoogleFlightsClientOptions { BurstRequests = 100, MaxRetries = 0 }, _time, _google);

    private static FlightSearchRequest OneWay => FlightSearchRequest.OneWay("SEA", "DEN", Dec15);

    private static FlightSearchRequest RoundTrip => FlightSearchRequest.RoundTrip("SEA", "DEN", Dec15, Dec22).Normalize("USD", "US");

    [Fact]
    public async Task A_search_lists_the_flights_its_search_page_carries()
    {
        _google.AnswerPage(SearchPages.Load("one-way-2026-11-22"));
        using var client = Client();

        var result = await client.SearchAsync(OneWay);

        var sent = Assert.Single(_google.Sent);
        // The link H1 fetched live: the one-way search, nothing chosen.
        Assert.Equal(
            "https://www.google.com/travel/flights/search?tfs=CBwQAhoeEgoyMDI2LTEyLTE1agcIARIDU0VBcgcIARIDREVOQAFIAXABggELCP___________wGYAQI&hl=en-US&gl=US&curr=USD",
            sent.Url.AbsoluteUri);
        Assert.Equal(sent.Url, result.GoogleFlightsUrl);
        Assert.Equal(0, result.Leg);
        Assert.Equal(4, result.BestFlights.Count);
        Assert.Equal(10, result.OtherFlights.Count);
        Assert.All(result.AllFlights, f =>
        {
            Assert.Equal("SEA", f.Origin);
            Assert.Equal("DEN", f.Destination);
            Assert.Equal("USD", f.Price!.Currency);
        });
    }

    [Fact]
    public async Task A_search_asks_for_the_page_and_names_the_project_in_its_user_agent()
    {
        _google.AnswerPage(SearchPages.Load("one-way-2026-11-22"));
        using var client = Client();

        await client.SearchAsync(OneWay);

        var sent = Assert.Single(_google.Sent);
        Assert.Empty(sent.Body);
        Assert.StartsWith("text/html", sent.Headers["Accept"], StringComparison.Ordinal);
        Assert.Contains("(compatible; Multiplex-GoogleFlights/", sent.Headers["User-Agent"], StringComparison.Ordinal);
        // Nothing the page itself would add: no anti-bot token, no page session, no client hints, no cookies of ours.
        Assert.DoesNotContain(sent.Headers.Keys, k => k.StartsWith("x-goog", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("sec-", StringComparison.OrdinalIgnoreCase) || k.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_search_sorted_by_price_asks_the_page_for_that_order_and_keeps_it()
    {
        // H6: the page honours the sort in its link (tfu); sorted by price it lists every flight in price order.
        _google.AnswerPage(SearchPages.Load("one-way-by-price-2026-11-22"));
        using var client = Client();

        var result = await client.SearchAsync(OneWay with { Sort = FlightSort.Price });

        Assert.Equal(
            "https://www.google.com/travel/flights/search?tfs=CBwQAhoeEgoyMDI2LTEyLTE1agcIARIDU0VBcgcIARIDREVOQAFIAXABggELCP___________wGYAQI&tfu=EgYIAhAAGAA&hl=en-US&gl=US&curr=USD",
            Assert.Single(_google.Sent).Url.AbsoluteUri);
        Assert.Empty(result.BestFlights);
        var prices = result.AllFlights.Select(f => f.Price!.Amount).ToArray();
        Assert.Equal(14, prices.Length);
        Assert.Equal(prices.Order(), prices);
    }

    [Fact]
    public async Task Return_flights_come_from_the_search_page_with_the_chosen_outbound_pinned_in_its_link()
    {
        _google.AnswerPage(SearchPages.Load("return-leg-2026-11-22"));
        using var client = Client();

        var returns = await client.GetReturnFlightsAsync(new FlightSelection(RoundTrip, [[Dl2759Out]], "token"));

        var sent = Assert.Single(_google.Sent);
        // The link H2 fetched live: Delta DL2759 pinned on the outbound leg.
        Assert.Equal(
            "https://www.google.com/travel/flights/search?tfs=CBwQAhpAEgoyMDI2LTEyLTE1IiAKA1NFQRIKMjAyNi0xMi0xNRoDREVOKgJETDIEMjc1OWoHCAESA1NFQXIHCAESA0RFThoeEgoyMDI2LTEyLTIyagcIARIDREVOcgcIARIDU0VBQAFIAXABggELCP___________wGYAQE&hl=en-US&gl=US&curr=USD",
            sent.Url.AbsoluteUri);
        Assert.Equal(1, returns.Leg);
        Assert.Equal(3, returns.BestFlights.Count);
        Assert.Equal(14, returns.OtherFlights.Count);
        Assert.All(returns.AllFlights, f =>
        {
            Assert.Equal("DEN", f.Origin);
            Assert.Equal("SEA", f.Destination);
        });
        // A return's selection holds both legs, ready for the booking link.
        var back = returns.AllFlights.First(f => f.Flights.SequenceEqual(["DL1731"]));
        Assert.Equal([Dl2759Out], back.Selection.ChosenLegs[0]);
        Assert.Equal([Dl1731Back], back.Selection.ChosenLegs[1]);
    }

    [Fact]
    public async Task Booking_options_are_not_available_and_the_booking_page_link_is_given_instead()
    {
        // H3: the booking page carries no sellers or fares; the page loads them through its own script.
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsUnavailableException>(() =>
            client.GetBookingOptionsAsync(new FlightSelection(RoundTrip, [[Dl2759Out], [Dl1731Back]], "token")));

        Assert.Empty(_google.Sent);
        // The booking link H3 fetched: both Delta flights pinned.
        Assert.Equal(
            "https://www.google.com/travel/flights/booking?tfs=CBwQAhpAEgoyMDI2LTEyLTE1IiAKA1NFQRIKMjAyNi0xMi0xNRoDREVOKgJETDIEMjc1OWoHCAESA1NFQXIHCAESA0RFThpAEgoyMDI2LTEyLTIyIiAKA0RFThIKMjAyNi0xMi0yMhoDU0VBKgJETDIEMTczMWoHCAESA0RFTnIHCAESA1NFQUABSAFwAYIBCwj___________8BmAEB&hl=en-US&gl=US&curr=USD",
            e.GoogleFlightsUrl!.AbsoluteUri);
        Assert.Contains(e.GoogleFlightsUrl.AbsoluteUri, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Price_calendars_are_not_available_and_nothing_is_sent()
    {
        using var client = Client();

        await Assert.ThrowsAsync<GoogleFlightsUnavailableException>(() =>
            client.GetCalendarAsync(new CalendarRequest(RoundTrip, new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 20))));

        Assert.Empty(_google.Sent);
    }

    [Fact]
    public async Task Multi_city_trips_are_not_available_and_nothing_is_sent()
    {
        // H4: a multi-city search page carries no flights; the page loads them through its own script.
        using var client = Client();
        var multi = FlightSearchRequest.MultiCity(new FlightLeg("SEA", "DEN", Dec15), new FlightLeg("DEN", "LAX", new DateOnly(2026, 12, 20)));

        var e = await Assert.ThrowsAsync<GoogleFlightsUnavailableException>(() => client.SearchAsync(multi));

        Assert.Empty(_google.Sent);
        Assert.Contains("one way", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_to_the_unsupported_page_is_a_refusal_with_a_cool_down()
    {
        // P5, P9: how Google turned away a page request from a User-Agent it does not serve pages to.
        _google.Answer(HttpStatusCode.Found, "", r => r.Headers.Location = new Uri("https://www.google.com/travel/flights/unsupported?tfs=x&ucpp=y"));
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(OneWay));
        Assert.Equal(ThrottleReason.Refused, e.Reason);
        Assert.Contains("unsupported", e.Message, StringComparison.Ordinal);

        var again = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(OneWay));
        Assert.Equal(ThrottleReason.CoolingDown, again.Reason);
        Assert.Single(_google.Sent);
    }

    [Fact]
    public async Task A_check_whose_link_mentions_the_unsupported_page_is_still_a_check()
    {
        // Only a redirect TO the unsupported page is that refusal; a /sorry/ check is a check, whatever its query says.
        _google.Answer(HttpStatusCode.Found, "", r => r.Headers.Location =
            new Uri("https://www.google.com/sorry/index?continue=https://www.google.com/travel/flights/unsupported%3Ftfs%3Dx"));
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(OneWay));

        Assert.Equal(ThrottleReason.Challenge, e.Reason);
    }

    [Fact]
    public async Task A_page_that_mentions_unusual_traffic_but_carries_its_flights_is_an_answer()
    {
        // Words in a script or a help text are not a check: the flights decide.
        _google.AnswerPage("<script>var help = 'Unusual traffic? See https://www.google.com/sorry/ for help.';</script>\n"
            + SearchPages.Load("one-way-2026-11-22"));
        using var client = Client();

        var result = await client.SearchAsync(OneWay);

        Assert.Equal(14, result.AllFlights.Count());
    }

    [Fact]
    public async Task An_unusual_traffic_page_without_flights_is_a_check_for_a_person()
    {
        _google.AnswerPage("<html><body>Our systems have detected unusual traffic from your computer network.</body></html>");
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(OneWay));

        Assert.Equal(ThrottleReason.Challenge, e.Reason);
    }

    [Fact]
    public async Task A_redirect_to_sorry_is_a_check_for_a_person()
    {
        _google.Answer(HttpStatusCode.Redirect, "", r => r.Headers.Location = new Uri("https://www.google.com/sorry/index"));
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(OneWay));

        Assert.Equal(ThrottleReason.Challenge, e.Reason);
    }

    [Fact]
    public async Task A_page_without_its_flight_data_is_a_format_error()
    {
        _google.AnswerPage("<script>AF_initDataCallback({key: 'ds:0', hash: '1', data:[], sideChannel: {}});</script>");
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsFormatException>(() => client.SearchAsync(OneWay));

        Assert.Contains("ds:1", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_whose_flight_data_is_not_an_array_is_a_format_error()
    {
        // The data must open with '[' right after "data:", not at the next '[' anywhere later on the page.
        _google.AnswerPage("<script>AF_initDataCallback({key: 'ds:1', hash: '9', data:{\"not\": \"an array\"}, sideChannel: {}});</script>"
            + "<script>var later = [1, 2, 3];</script>");
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsFormatException>(() => client.SearchAsync(OneWay));

        Assert.Contains("array", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_whose_flight_data_is_not_valid_json_is_a_format_error()
    {
        _google.AnswerPage("<script>AF_initDataCallback({key: 'ds:1', hash: '9', data:[invalid, sideChannel: {}});</script>");
        using var client = Client();

        var e = await Assert.ThrowsAsync<GoogleFlightsFormatException>(() => client.SearchAsync(OneWay));

        Assert.Contains("not valid JSON", e.Message, StringComparison.Ordinal);
    }
}
