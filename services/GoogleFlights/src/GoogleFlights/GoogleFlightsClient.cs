using Multiplex.Sites.GoogleFlights.Http;
using Multiplex.Sites.GoogleFlights.Wire;

namespace Multiplex.Sites.GoogleFlights;

/// <summary>
/// Google Flights by direct HTTPS: no browser, no sign-in, no third-party service. Flights come from Google Flights'
/// search pages, which carry their first answer in their HTML; airports from the page's airport-lookup RPC.
/// What Google provides only through its page's own script (booking options, price calendars, multi-city trips)
/// is not available: those calls send nothing and raise <see cref="GoogleFlightsUnavailableException"/> with the
/// Google Flights link instead (protocol.md section 2).
/// Answers are cached briefly (<see cref="GoogleFlightsClientOptions.CacheDuration"/>) and every call can bypass
/// the cache with <c>verify</c>. Requests are paced, and a refusal from Google stops the client asking for a while:
/// see <see cref="GoogleFlightsThrottledException"/>. One instance should serve a whole process; it is safe to share
/// between threads.
/// </summary>
public sealed class GoogleFlightsClient : IDisposable
{
    private readonly GoogleFlightsClientOptions _options;
    private readonly TimeProvider _time;
    private readonly GoogleFlightsTransport _transport;
    private readonly ResponseCache _cache;

    public GoogleFlightsClient(GoogleFlightsClientOptions? options = null, TimeProvider? timeProvider = null)
        : this(options, timeProvider, transport: null)
    {
    }

    /// <param name="transport">
    /// The handler that reaches Google, in place of the client's own (tests use a fake). Pacing, retries, timeouts
    /// and the refusal cool-down still wrap it. It must not follow redirects: they are how Google signals a block.
    /// </param>
    public GoogleFlightsClient(GoogleFlightsClientOptions? options, TimeProvider? timeProvider, HttpMessageHandler? transport)
    {
        _options = options ?? new GoogleFlightsClientOptions();
        _time = timeProvider ?? TimeProvider.System;
        // Fail fast on bad defaults rather than on the first search.
        FlightSearchRequest.NormalizeCurrency(_options.Currency);
        FlightSearchRequest.NormalizeRegion(_options.Region);
        _transport = new GoogleFlightsTransport(_options, _time, transport);
        _cache = new ResponseCache(_options.CacheDuration, _options.CacheCapacity, _time);
    }

    /// <summary>The airlines and alliances in the client's versioned data file.</summary>
    public static AirlineDirectory Airlines => AirlineDirectory.Default;

    /// <summary>Flights for a one-way or round-trip search's first leg.</summary>
    /// <param name="verify">Ask Google again even when a recent answer is cached.</param>
    /// <exception cref="GoogleFlightsUnavailableException">A multi-city trip: its search page carries no flights.</exception>
    public Task<FlightSearchResult> SearchAsync(FlightSearchRequest request, bool verify = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(request);
        return FlightsAsync(normalized, selection: null, verify, ct);
    }

    /// <summary>
    /// A round trip's return flights after its outbound is chosen: the search page with the chosen flight pinned in
    /// its link. Pass the <see cref="FlightOption.Selection"/> of the option chosen.
    /// </summary>
    /// <exception cref="ArgumentException">Every leg is already chosen: ask for booking options instead.</exception>
    public Task<FlightSearchResult> GetReturnFlightsAsync(FlightSelection selection, bool verify = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var request = Normalize(selection.Request);
        if (selection.ChosenLegs.Count >= request.Legs.Count)
        {
            throw new ArgumentException(
                "Every leg of this trip is already chosen; ask for its booking options instead.", nameof(selection));
        }
        selection = selection with { Request = request };
        selection.Validate();
        return FlightsAsync(request, selection, verify, ct);
    }

    /// <summary>
    /// Where the chosen trip can be bought: <b>not available</b>. Google Flights' booking page carries no sellers or
    /// fares; it loads them through its own script, whose request carries a token the client cannot make. Nothing is
    /// sent; the task fails with a <see cref="GoogleFlightsUnavailableException"/> holding the booking page's link for
    /// a person to open.
    /// </summary>
    /// <exception cref="ArgumentException">A leg is not chosen yet: get its flights first.</exception>
    public Task<BookingOptions> GetBookingOptionsAsync(FlightSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var request = Normalize(selection.Request);
        if (selection.ChosenLegs.Count != request.Legs.Count)
        {
            throw new ArgumentException(
                $"Only {selection.ChosenLegs.Count} of {request.Legs.Count} legs are chosen; choose the next flight first.", nameof(selection));
        }
        selection = selection with { Request = request };
        selection.Validate();

        var url = TfsEncoder.PageUrl(request, RequestEncoder.Filter(request, selection.ChosenLegs), booking: true);
        return Task.FromException<BookingOptions>(new GoogleFlightsUnavailableException(
            "Booking options are not available: Google Flights lists sellers and fares only through its page's own script, " +
            $"which this client does not run. Open the booking page to see them: {url}", url));
    }

    /// <summary>
    /// The lowest price per date over a range: <b>not available</b>. Google Flights prices dates only through its
    /// page's own script, whose request carries a token the client cannot make. The request is checked as before (a
    /// round trip, ranges no wider than the page's own), then nothing is sent; the task fails with a
    /// <see cref="GoogleFlightsUnavailableException"/> holding the search page's link.
    /// </summary>
    public Task<PriceCalendar> GetCalendarAsync(CalendarRequest request, bool verify = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var search = Normalize(request.Search ?? throw new ArgumentException("The calendar needs a search.", nameof(request)));
        if (search.TripType == TripType.MultiCity)
            throw new ArgumentException("Google Flights has no price calendar for multi-city trips.", nameof(request));
        if (search.TripType == TripType.OneWay)
        {
            // The page was never recorded asking for a one-way calendar, and the one form tried was refused
            // (protocol.md section 5). Until a capture shows the page's own request, the client does not guess.
            throw new ArgumentException(
                "The price calendar is available for round trips only: give a trip length or a return date range.", nameof(request));
        }
        if (request.ReturnFrom is { } returnFrom && request.ReturnTo is { } returnTo)
        {
            if (search.TripType != TripType.RoundTrip)
                throw new ArgumentException("Return dates apply only to a round trip.", nameof(request));
            CheckCalendarRange(request.DepartureFrom, request.DepartureTo, MaxGridDays, "departure");
            CheckCalendarRange(returnFrom, returnTo, MaxGridDays, "return");
            if (returnTo < request.DepartureFrom)
                throw new ArgumentException("Every return date is before the first departure.", nameof(request));
        }
        else if (request.ReturnFrom is not null || request.ReturnTo is not null)
        {
            throw new ArgumentException("Give both ends of the return range, or neither.", nameof(request));
        }
        else
        {
            CheckCalendarRange(request.DepartureFrom, request.DepartureTo, MaxGraphDays, "departure");
        }

        var url = TfsEncoder.PageUrl(search, RequestEncoder.Filter(search), booking: false);
        return Task.FromException<PriceCalendar>(new GoogleFlightsUnavailableException(
            "Price calendars are not available: Google Flights prices dates only through its page's own script, which " +
            $"this client does not run. Search the dates that matter one at a time, or open the search page and its Date grid: {url}", url));
    }

    /// <summary>Places and their airports matching what a person typed, e.g. <c>Denver</c> or <c>SEA</c>.</summary>
    public async Task<AirportLookup> LookupAirportsAsync(string query, bool verify = false, CancellationToken ct = default)
    {
        var text = (query ?? "").Trim();
        if (text.Length is 0 or > 100)
            throw new ArgumentException("Give a place or airport name of 1 to 100 characters.", nameof(query));

        var jspb = RequestEncoder.JspbHeader(FlightSearchRequest.NormalizeCurrency(_options.Currency), FlightSearchRequest.NormalizeRegion(_options.Region));
        var inner = RequestEncoder.Autocomplete(text);
        return await CachedAsync($"H028ib\n{jspb}\n{inner}", verify, ct,
            () => _transport.BatchAsync("H028ib", inner, jspb, CancellationToken.None),
            frames => new AirportLookup(text, ResponseParser.Places(frames), _time.GetUtcNow(), FromCache: false),
            r => r with { FromCache = true }).ConfigureAwait(false);
    }

    /// <summary>A leg's flights, from the search page whose link pins every flight chosen before it.</summary>
    private Task<FlightSearchResult> FlightsAsync(FlightSearchRequest request, FlightSelection? selection, bool verify, CancellationToken ct)
    {
        var url = TfsEncoder.PageUrl(request, RequestEncoder.Filter(request, selection?.ChosenLegs), booking: false);
        if (request.TripType == TripType.MultiCity)
        {
            // H4: the page loads a multi-city trip's flights only through its own script; its HTML carries none.
            return Task.FromException<FlightSearchResult>(new GoogleFlightsUnavailableException(
                "Multi-city trips are not available: Google Flights loads their flights only through its page's own script, " +
                "which this client does not run. Search each leg as one way instead (those are one-way prices, not the " +
                $"multi-city price), or open the search page: {url}", url));
        }

        return CachedAsync(url.AbsoluteUri, verify, ct,
            () => _transport.SearchPageAsync(url, CancellationToken.None),
            frames =>
            {
                var parsed = ResponseParser.Flights(frames, request, selection);
                return new FlightSearchResult(request, selection?.NextLeg ?? 0, parsed.Best, parsed.Other, parsed.Airlines, url,
                    _time.GetUtcNow(), FromCache: false);
            },
            r => r with { FromCache = true });
    }

    private async Task<T> CachedAsync<T>(
        string key, bool verify, CancellationToken ct,
        Func<Task<RpcFrames>> fetch, Func<RpcFrames, T> parse, Func<T, T> markCached) where T : class
    {
        if (!verify && _cache.TryGet<T>(key, out var cached))
            return markCached(cached);

        ct.ThrowIfCancellationRequested();
        // The shared request takes no caller's token: the pipeline's total timeout bounds it.
        return await _cache.SingleFlightAsync(key, async () =>
        {
            var result = parse(await fetch().ConfigureAwait(false));
            _cache.Set(key, result);
            return result;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The longest departure range a price graph asks for: about the two months the page's own graph showed (55 days).</summary>
    public const int MaxGraphDays = 61;

    /// <summary>The widest departure or return range a date grid asks for: the page's own grid is 7 by 7.</summary>
    public const int MaxGridDays = 7;

    private FlightSearchRequest Normalize(FlightSearchRequest request)
    {
        var normalized = request.Normalize(_options.Currency, _options.Region);
        foreach (var (leg, i) in normalized.Legs.Select((l, i) => (l, i)))
            CheckDate(leg.Date, $"Leg {i + 1}'s date");
        return normalized;
    }

    /// <summary>
    /// A date Google sells: not before today anywhere on Earth, and within a year. A date it will not search can
    /// earn a refusal that stops the client for everyone, so it is caught here.
    /// </summary>
    private void CheckDate(DateOnly date, string what)
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        if (date < today.AddDays(-1))
            throw new ArgumentException($"{what} {date:yyyy-MM-dd} is in the past.");
        if (date > today.AddDays(366))
            throw new ArgumentException($"{what} {date:yyyy-MM-dd} is more than a year away; airlines do not sell that far ahead.");
    }

    private void CheckCalendarRange(DateOnly from, DateOnly to, int maxDays, string what)
    {
        if (to < from)
            throw new ArgumentException($"The {what} range ends before it starts.");
        if (to.DayNumber - from.DayNumber + 1 > maxDays)
            throw new ArgumentException($"The {what} range covers {to.DayNumber - from.DayNumber + 1} days; ask for at most {maxDays} at a time.");
        CheckDate(from, $"The {what} range's first date");
        CheckDate(to, $"The {what} range's last date");
    }

    public void Dispose() => _transport.Dispose();
}
