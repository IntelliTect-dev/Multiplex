namespace Multiplex.Sites.GoogleFlights;

/// <summary>One flight between two airports, as part of a <see cref="FlightOption"/>.</summary>
/// <param name="From">IATA code of the departure airport.</param>
/// <param name="FromName">The departure airport's name.</param>
/// <param name="To">IATA code of the arrival airport.</param>
/// <param name="ToName">The arrival airport's name.</param>
/// <param name="DepartsLocal">Departure in the departure airport's local time.</param>
/// <param name="ArrivesLocal">Arrival in the arrival airport's local time.</param>
/// <param name="Departs">Departure with its UTC offset, when Google sent it.</param>
/// <param name="Arrives">Arrival with its UTC offset, when Google sent it.</param>
/// <param name="Duration">Time in the air.</param>
/// <param name="Carrier">Marketing carrier's IATA code, e.g. <c>DL</c>.</param>
/// <param name="FlightNumber">The marketing flight number, e.g. <c>1358</c>.</param>
/// <param name="Airline">The marketing airline's name.</param>
/// <param name="OperatedBy">The operating carrier's code when it differs from the marketing carrier.</param>
/// <param name="Aircraft">The aircraft, e.g. <c>Boeing 737</c>.</param>
/// <param name="Legroom">Seat pitch as Google shows it, e.g. <c>31 in</c>.</param>
public sealed record FlightSegment(
    string From,
    string? FromName,
    string To,
    string? ToName,
    DateTime DepartsLocal,
    DateTime ArrivesLocal,
    DateTimeOffset? Departs,
    DateTimeOffset? Arrives,
    TimeSpan Duration,
    string Carrier,
    string FlightNumber,
    string? Airline,
    string? OperatedBy,
    string? Aircraft,
    string? Legroom)
{
    /// <summary>Carrier and number, e.g. <c>DL1358</c>.</summary>
    public string Flight => Carrier + FlightNumber;
}

/// <summary>A connection between two segments.</summary>
/// <param name="Duration">Time on the ground.</param>
/// <param name="ArrivalAirport">Where the previous segment lands.</param>
/// <param name="DepartureAirport">Where the next segment leaves from; different when the connection changes airport.</param>
/// <param name="AirportName">The arrival airport's name.</param>
/// <param name="City">The arrival airport's city.</param>
public sealed record Layover(TimeSpan Duration, string ArrivalAirport, string DepartureAirport, string? AirportName, string? City)
{
    /// <summary>Whether the traveler must change airports.</summary>
    public bool ChangesAirport => !string.Equals(ArrivalAirport, DepartureAirport, StringComparison.Ordinal);
}

/// <summary>One way to fly one leg, as Google lists it.</summary>
/// <param name="Segments">The flights, in order.</param>
/// <param name="Layovers">The connections between them.</param>
/// <param name="Duration">Total travel time, from first departure to last arrival.</param>
/// <param name="Airlines">The airlines' names, as Google shows them.</param>
/// <param name="Price">
/// The price Google lists with this option: for a round trip's or multi-city trip's earlier legs, the lowest
/// whole-trip price that includes this choice; on the last leg, the whole trip's price. Exact when Google's
/// token carries it, otherwise rounded to whole units. <c>null</c> when Google lists no price.
/// </param>
/// <param name="SeparateTickets">
/// On the last leg of a round trip or multi-city trip only: whether Google says this choice is sold as separate
/// tickets. <c>null</c> elsewhere, where Google's flag means something not yet established.
/// </param>
/// <param name="Selection">
/// What choosing this option makes: pass it on for a round trip's return flights. Booking options are not available
/// (<see cref="GoogleFlightsClient.GetBookingOptionsAsync"/> gives the booking page's link instead).
/// </param>
public sealed record FlightOption(
    IReadOnlyList<FlightSegment> Segments,
    IReadOnlyList<Layover> Layovers,
    TimeSpan Duration,
    IReadOnlyList<string> Airlines,
    Money? Price,
    bool? SeparateTickets,
    FlightSelection Selection)
{
    public string Origin => Segments[0].From;

    public string Destination => Segments[^1].To;

    public DateTime DepartsLocal => Segments[0].DepartsLocal;

    public DateTime ArrivesLocal => Segments[^1].ArrivesLocal;

    public int Stops => Segments.Count - 1;

    /// <summary>The flights, e.g. <c>DL1358</c>, <c>DL2148</c>.</summary>
    public IEnumerable<string> Flights => Segments.Select(s => s.Flight);
}

/// <summary>An airline Google offers as a filter for a search's results.</summary>
public sealed record AirlineName(string Code, string Name);

/// <summary>The flights Google lists for one leg of a search.</summary>
/// <param name="Request">The normalized search.</param>
/// <param name="Leg">Which leg (0-based) these options are for.</param>
/// <param name="BestFlights">Google's "top flights" for this leg.</param>
/// <param name="OtherFlights">Every other option.</param>
/// <param name="Airlines">The airlines in these results, for filtering a later search.</param>
/// <param name="GoogleFlightsUrl">The same search on Google Flights, for a person to open.</param>
/// <param name="RetrievedAt">When Google answered.</param>
/// <param name="FromCache">Whether this answer came from the client's cache rather than a new request.</param>
public sealed record FlightSearchResult(
    FlightSearchRequest Request,
    int Leg,
    IReadOnlyList<FlightOption> BestFlights,
    IReadOnlyList<FlightOption> OtherFlights,
    IReadOnlyList<AirlineName> Airlines,
    Uri GoogleFlightsUrl,
    DateTimeOffset RetrievedAt,
    bool FromCache)
{
    /// <summary>Best flights first, then the others.</summary>
    public IEnumerable<FlightOption> AllFlights => BestFlights.Concat(OtherFlights);
}

/// <summary>A seller of a booking option: the airline itself or an agency.</summary>
public sealed record BookingSeller(string Code, string Name, bool IsAirline);

/// <summary>One way to buy the chosen trip: a seller and fare.</summary>
/// <param name="Sellers">Who sells it; several when Google combines tickets.</param>
/// <param name="Flights">The flights it covers, e.g. <c>DL1358</c>.</param>
/// <param name="Price">The whole trip's price for this fare.</param>
/// <param name="FareName">The fare as Google shows it, e.g. <c>Delta Main Basic</c>.</param>
/// <param name="FareCode">The airline's fare brand code, e.g. <c>DELTA MAIN BASIC</c>.</param>
/// <param name="SellerSite">The seller's site as Google shows it, e.g. <c>www.delta.com/...</c>.</param>
public sealed record BookingOption(
    IReadOnlyList<BookingSeller> Sellers,
    IReadOnlyList<string> Flights,
    Money? Price,
    string? FareName,
    string? FareCode,
    string? SellerSite);

/// <summary>Where the chosen trip can be bought. Always fetched fresh: never from the cache.</summary>
/// <param name="Selection">The complete selection the options are for.</param>
/// <param name="Options">The sellers and fares, as Google lists them.</param>
/// <param name="GoogleFlightsUrl">Google Flights' booking page for the same trip, for a person to open and buy from.</param>
/// <param name="RetrievedAt">When Google answered.</param>
public sealed record BookingOptions(
    FlightSelection Selection,
    IReadOnlyList<BookingOption> Options,
    Uri GoogleFlightsUrl,
    DateTimeOffset RetrievedAt);

/// <summary>The lowest price Google found for one departure date (and return date, on a round trip).</summary>
public sealed record CalendarPrice(DateOnly Departure, DateOnly? Return, Money Price);

/// <summary>Prices across a range of dates.</summary>
/// <param name="Request">What was asked for.</param>
/// <param name="Prices">One price per date (or date pair) Google priced; a date without a price is left out.</param>
/// <param name="RetrievedAt">When Google answered.</param>
/// <param name="FromCache">Whether this answer came from the client's cache rather than a new request.</param>
public sealed record PriceCalendar(CalendarRequest Request, IReadOnlyList<CalendarPrice> Prices, DateTimeOffset RetrievedAt, bool FromCache)
{
    /// <summary>The cheapest date, or <c>null</c> when nothing was priced.</summary>
    public CalendarPrice? Cheapest => Prices.MinBy(p => p.Price.Amount);
}

/// <summary>
/// A request for prices across dates. For a one-way or round trip search: departures from
/// <see cref="DepartureFrom"/> to <see cref="DepartureTo"/>. A round trip keeps its trip length unless
/// <see cref="ReturnFrom"/> and <see cref="ReturnTo"/> are given, which ask for every departure-by-return pair.
/// </summary>
public sealed record CalendarRequest(FlightSearchRequest Search, DateOnly DepartureFrom, DateOnly DepartureTo)
{
    public DateOnly? ReturnFrom { get; init; }

    public DateOnly? ReturnTo { get; init; }
}

/// <summary>An airport, or a station Google also lists, near a place that matched a lookup.</summary>
/// <param name="Code">IATA code to search with, e.g. <c>SEA</c>.</param>
/// <param name="Name">e.g. <c>Seattle-Tacoma International Airport</c>.</param>
/// <param name="City">The city it serves.</param>
/// <param name="Distance">How far it is from the place, as Google shows it, e.g. <c>11 mi</c>.</param>
/// <param name="IsAirport">False for a rail or bus station Google lists alongside airports.</param>
public sealed record Airport(string Code, string Name, string? City, string? Distance, bool IsAirport);

/// <summary>A place that matched a lookup, with the airports Google offers for it.</summary>
/// <param name="Name">e.g. <c>Seattle, Washington</c>.</param>
/// <param name="Description">e.g. <c>City in Washington State</c>.</param>
/// <param name="Airports">The airports Google offers for the place, nearest first as Google lists them.</param>
public sealed record PlaceMatch(string Name, string? Description, IReadOnlyList<Airport> Airports);

/// <summary>The places and airports that matched a lookup.</summary>
public sealed record AirportLookup(string Query, IReadOnlyList<PlaceMatch> Places, DateTimeOffset RetrievedAt, bool FromCache)
{
    /// <summary>Every airport of every matching place, without repeats, in Google's order.</summary>
    public IEnumerable<Airport> Airports => Places.SelectMany(p => p.Airports).DistinctBy(a => a.Code);
}
