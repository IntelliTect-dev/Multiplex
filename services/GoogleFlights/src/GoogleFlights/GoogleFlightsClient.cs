using Multiplex.Sites.GoogleFlights.Http;
using Multiplex.Sites.GoogleFlights.Wire;

namespace Multiplex.Sites.GoogleFlights;

/// <summary>
/// Google Flights over the endpoints Google Flights' own page calls, by direct HTTP: no browser, no sign-in,
/// no third-party service. Answers are cached briefly (<see cref="GoogleFlightsClientOptions.CacheDuration"/>)
/// and every call can bypass the cache with <c>verify</c>. Requests are paced, and a refusal from Google stops
/// the client asking for a while: see <see cref="GoogleFlightsThrottledException"/>.
/// One instance should serve a whole process; it is safe to share between threads.
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

    /// <summary>Flights for a search's first leg.</summary>
    /// <param name="verify">Ask Google again even when a recent answer is cached.</param>
    public Task<FlightSearchResult> SearchAsync(FlightSearchRequest request, bool verify = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(request);
        return FlightsAsync(normalized, selection: null, verify, ct);
    }

    /// <summary>
    /// Flights for the next leg after a choice: the return flights of a round trip, or the next flight of a
    /// multi-city trip. Pass the <see cref="FlightOption.Selection"/> of the option chosen.
    /// </summary>
    /// <exception cref="ArgumentException">Every leg is already chosen: ask for booking options instead.</exception>
    public Task<FlightSearchResult> GetReturnFlightsAsync(FlightSelection selection, bool verify = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var request = Normalize(selection.Request);
        if (selection.ChosenLegs.Count == 0 || selection.ChosenLegs.Count >= request.Legs.Count)
        {
            throw new ArgumentException(
                "Every leg of this trip is already chosen; ask for its booking options instead.", nameof(selection));
        }
        return FlightsAsync(request, selection with { Request = request }, verify, ct);
    }

    /// <summary>
    /// Where the chosen trip can be bought: sellers, fares and prices. Always asks Google: booking options are
    /// never served from the cache.
    /// </summary>
    /// <exception cref="ArgumentException">A leg is not chosen yet: get its flights first.</exception>
    public async Task<BookingOptions> GetBookingOptionsAsync(FlightSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var request = Normalize(selection.Request);
        if (selection.ChosenLegs.Count != request.Legs.Count)
        {
            throw new ArgumentException(
                $"Only {selection.ChosenLegs.Count} of {request.Legs.Count} legs are chosen; choose the next flight first.", nameof(selection));
        }
        selection = selection with { Request = request };

        var frames = await _transport.ServiceAsync("GetBookingResults", RequestEncoder.BookingResults(selection), Jspb(request), ct)
            .ConfigureAwait(false);
        var options = ResponseParser.Booking(frames, request);
        var url = TfsEncoder.PageUrl(request, RequestEncoder.Filter(request, selection.ChosenLegs), booking: true);
        return new BookingOptions(selection, options, url, _time.GetUtcNow());
    }

    /// <summary>
    /// The lowest price per date over a range. A round trip keeps its trip length, or, with
    /// <see cref="CalendarRequest.ReturnFrom"/> and <see cref="CalendarRequest.ReturnTo"/>, prices every
    /// departure-by-return pair. Multi-city trips have no calendar.
    /// </summary>
    public async Task<PriceCalendar> GetCalendarAsync(CalendarRequest request, bool verify = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var search = Normalize(request.Search ?? throw new ArgumentException("The calendar needs a search.", nameof(request)));
        if (search.TripType == TripType.MultiCity)
            throw new ArgumentException("Google Flights has no price calendar for multi-city trips.", nameof(request));
        if (request.DepartureTo < request.DepartureFrom)
            throw new ArgumentException("The departure range ends before it starts.", nameof(request));

        string rpc, inner;
        if (request.ReturnFrom is { } returnFrom && request.ReturnTo is { } returnTo)
        {
            if (search.TripType != TripType.RoundTrip)
                throw new ArgumentException("Return dates apply only to a round trip.", nameof(request));
            if (returnTo < returnFrom)
                throw new ArgumentException("The return range ends before it starts.", nameof(request));
            rpc = "GetCalendarGrid";
            inner = RequestEncoder.CalendarGrid(search, request.DepartureFrom, request.DepartureTo, returnFrom, returnTo);
        }
        else if (request.ReturnFrom is not null || request.ReturnTo is not null)
        {
            throw new ArgumentException("Give both ends of the return range, or neither.", nameof(request));
        }
        else
        {
            int? tripDays = search.TripType == TripType.RoundTrip ? search.Legs[1].Date.DayNumber - search.Legs[0].Date.DayNumber : null;
            rpc = "GetCalendarGraph";
            inner = RequestEncoder.CalendarGraph(search, request.DepartureFrom, request.DepartureTo, tripDays);
        }

        var normalized = request with { Search = search };
        return await CachedAsync(rpc, inner, Jspb(search), verify, ct,
            frames => new PriceCalendar(normalized, ResponseParser.Calendar(frames, search), _time.GetUtcNow(), FromCache: false),
            r => r with { FromCache = true }).ConfigureAwait(false);
    }

    /// <summary>Places and their airports matching what a person typed, e.g. <c>Denver</c> or <c>SEA</c>.</summary>
    public async Task<AirportLookup> LookupAirportsAsync(string query, bool verify = false, CancellationToken ct = default)
    {
        var text = (query ?? "").Trim();
        if (text.Length is 0 or > 100)
            throw new ArgumentException("Give a place or airport name of 1 to 100 characters.", nameof(query));

        var jspb = RequestEncoder.JspbHeader(FlightSearchRequest.NormalizeCurrency(_options.Currency), FlightSearchRequest.NormalizeRegion(_options.Region));
        return await CachedAsync("H028ib", RequestEncoder.Autocomplete(text), jspb, verify, ct,
            frames => new AirportLookup(text, ResponseParser.Places(frames), _time.GetUtcNow(), FromCache: false),
            r => r with { FromCache = true }, batch: true).ConfigureAwait(false);
    }

    private Task<FlightSearchResult> FlightsAsync(FlightSearchRequest request, FlightSelection? selection, bool verify, CancellationToken ct)
    {
        var inner = RequestEncoder.ShoppingResults(request, selection);
        var url = TfsEncoder.PageUrl(request, RequestEncoder.Filter(request, selection?.ChosenLegs), booking: false);
        return CachedAsync("GetShoppingResults", inner, Jspb(request), verify, ct,
            frames =>
            {
                var parsed = ResponseParser.Flights(frames, request, selection);
                return new FlightSearchResult(request, selection?.NextLeg ?? 0, parsed.Best, parsed.Other, parsed.Airlines, url,
                    _time.GetUtcNow(), FromCache: false);
            },
            r => r with { FromCache = true });
    }

    private async Task<T> CachedAsync<T>(
        string rpc, string inner, string jspb, bool verify, CancellationToken ct,
        Func<RpcFrames, T> parse, Func<T, T> markCached, bool batch = false) where T : class
    {
        var key = $"{rpc}\n{jspb}\n{inner}";
        if (!verify && _cache.TryGet<T>(key, out var cached))
            return markCached(cached);

        return await _cache.SingleFlightAsync(key, async () =>
        {
            var frames = batch
                ? await _transport.BatchAsync(rpc, inner, jspb, ct).ConfigureAwait(false)
                : await _transport.ServiceAsync(rpc, inner, jspb, ct).ConfigureAwait(false);
            var result = parse(frames);
            _cache.Set(key, result);
            return result;
        }).ConfigureAwait(false);
    }

    private FlightSearchRequest Normalize(FlightSearchRequest request) => request.Normalize(_options.Currency, _options.Region);

    private static string Jspb(FlightSearchRequest normalized) => RequestEncoder.JspbHeader(normalized.Currency!, normalized.Region!);

    public void Dispose() => _transport.Dispose();
}
