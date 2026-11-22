using System.ComponentModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Multiplex.Sites.GoogleFlights;
using Multiplex.Web.Mcp;
using Docs = Multiplex.Sites.GoogleFlights.GoogleFlightsDocs;

namespace Multiplex.Web.Services.GoogleFlights;

/// <summary>
/// Google Flights at <c>/google-flights/mcp</c>: thin wrappers over <see cref="GoogleFlightsClient"/>. They read
/// their parameters through <see cref="SearchParameters"/> and their descriptions from <see cref="GoogleFlightsDocs"/>,
/// so the PowerShell commands accept the same inputs and say the same things. Nothing here books or buys.
/// </summary>
[McpServerToolType]
[McpEndpoint("google-flights")]
public sealed class GoogleFlightsTools(GoogleFlightsClient client)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "search_flights", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(Docs.SearchFlights)]
    public Task<string> SearchFlights(
        [Description(Docs.Origin)] string? origin = null,
        [Description(Docs.Destination)] string? destination = null,
        [Description(Docs.DepartureDate)] string? departure_date = null,
        [Description(Docs.ReturnDate)] string? return_date = null,
        [Description(Docs.Legs)] string[]? legs = null,
        [Description(Docs.MaxStops)] string? max_stops = null,
        [Description(Docs.Airlines)] string[]? airlines = null,
        [Description(Docs.Alliances)] string[]? alliances = null,
        [Description(Docs.DepartAfterHour)] int? depart_after_hour = null,
        [Description(Docs.DepartBeforeHour)] int? depart_before_hour = null,
        [Description(Docs.ArriveAfterHour)] int? arrive_after_hour = null,
        [Description(Docs.ArriveBeforeHour)] int? arrive_before_hour = null,
        [Description(Docs.MaxDurationHours)] double? max_duration_hours = null,
        [Description(Docs.Cabin)] string? cabin = null,
        [Description(Docs.ExcludeBasicEconomy)] bool exclude_basic_economy = false,
        [Description(Docs.Adults)] int adults = 1,
        [Description(Docs.Children)] int children = 0,
        [Description(Docs.InfantsOnLap)] int infants_on_lap = 0,
        [Description(Docs.InfantsInSeat)] int infants_in_seat = 0,
        [Description(Docs.MaxPrice)] int? max_price = null,
        [Description(Docs.Sort)] string? sort = null,
        [Description(Docs.Currency)] string? currency = null,
        [Description(Docs.Region)] string? region = null,
        [Description(Docs.Verify)] bool verify = false,
        CancellationToken ct = default)
        => RunAsync(async () =>
        {
            var parameters = new SearchParameters
            {
                Origin = origin,
                Destination = destination,
                DepartureDate = SearchParameters.Text.OptionalDate(departure_date, nameof(departure_date)),
                ReturnDate = SearchParameters.Text.OptionalDate(return_date, nameof(return_date)),
                Legs = legs?.Select(SearchParameters.Text.Leg).ToArray(),
                Stops = SearchParameters.Text.Stops(max_stops),
                Airlines = airlines ?? [],
                Alliances = (alliances ?? []).Select(SearchParameters.Text.Alliance).ToArray(),
                DepartAfterHour = depart_after_hour,
                DepartBeforeHour = depart_before_hour,
                ArriveAfterHour = arrive_after_hour,
                ArriveBeforeHour = arrive_before_hour,
                MaxDurationHours = max_duration_hours,
                Cabin = SearchParameters.Text.Cabin(cabin),
                ExcludeBasicEconomy = exclude_basic_economy,
                Adults = adults,
                Children = children,
                InfantsOnLap = infants_on_lap,
                InfantsInSeat = infants_in_seat,
                MaxPrice = max_price,
                Sort = SearchParameters.Text.Sort(sort),
                Currency = currency,
                Region = region,
            };
            return Flights(await client.SearchAsync(parameters.ToRequest(), verify, ct).ConfigureAwait(false));
        });

    [McpServerTool(Name = "get_return_flights", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(Docs.GetReturnFlights)]
    public Task<string> GetReturnFlights(
        [Description(Docs.Selection)] string selection,
        [Description(Docs.Verify)] bool verify = false,
        CancellationToken ct = default)
        => RunAsync(async () =>
            Flights(await client.GetReturnFlightsAsync(FlightSelection.FromHandle(selection), verify, ct).ConfigureAwait(false)));

    [McpServerTool(Name = "get_booking_options", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(Docs.GetBookingOptions)]
    public Task<string> GetBookingOptions(
        [Description(Docs.Selection)] string selection,
        [Description(Docs.Verify)] bool verify = false,
        CancellationToken ct = default)
        => RunAsync(async () =>
        {
            _ = verify; // booking options are always fetched fresh
            var booking = await client.GetBookingOptionsAsync(FlightSelection.FromHandle(selection), ct).ConfigureAwait(false);
            return Serialize(new
            {
                retrieved_at = booking.RetrievedAt,
                from_cache = false,
                currency = booking.Selection.Request.Currency,
                google_flights_url = booking.GoogleFlightsUrl,
                note = "Nothing is booked. The person buys from the seller, or opens google_flights_url.",
                options = booking.Options.Select(o => new
                {
                    fare = o.FareName,
                    fare_code = o.FareCode,
                    price = o.Price?.Amount,
                    sellers = o.Sellers.Select(s => new { s.Name, is_airline = s.IsAirline }),
                    seller_site = o.SellerSite,
                    flights = o.Flights,
                }),
            });
        });

    [McpServerTool(Name = "get_price_calendar", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(Docs.GetPriceCalendar)]
    public Task<string> GetPriceCalendar(
        [Description(Docs.Origin)] string origin,
        [Description(Docs.Destination)] string destination,
        [Description(Docs.DepartureDateFrom)] string departure_date_from,
        [Description(Docs.DepartureDateTo)] string departure_date_to,
        [Description(Docs.TripLengthDays)] int? trip_length_days = null,
        [Description(Docs.ReturnDateFrom)] string? return_date_from = null,
        [Description(Docs.ReturnDateTo)] string? return_date_to = null,
        [Description(Docs.MaxStops)] string? max_stops = null,
        [Description(Docs.Airlines)] string[]? airlines = null,
        [Description(Docs.Cabin)] string? cabin = null,
        [Description(Docs.Adults)] int adults = 1,
        [Description(Docs.Currency)] string? currency = null,
        [Description(Docs.Region)] string? region = null,
        [Description(Docs.Verify)] bool verify = false,
        CancellationToken ct = default)
        => RunAsync(async () =>
        {
            var parameters = new SearchParameters
            {
                Origin = origin,
                Destination = destination,
                Stops = SearchParameters.Text.Stops(max_stops),
                Airlines = airlines ?? [],
                Cabin = SearchParameters.Text.Cabin(cabin),
                Adults = adults,
                Currency = currency,
                Region = region,
            };
            var request = parameters.ToCalendarRequest(
                SearchParameters.Text.Date(departure_date_from, nameof(departure_date_from)),
                SearchParameters.Text.Date(departure_date_to, nameof(departure_date_to)),
                trip_length_days,
                SearchParameters.Text.OptionalDate(return_date_from, nameof(return_date_from)),
                SearchParameters.Text.OptionalDate(return_date_to, nameof(return_date_to)));
            var calendar = await client.GetCalendarAsync(request, verify, ct).ConfigureAwait(false);
            return Serialize(new
            {
                retrieved_at = calendar.RetrievedAt,
                from_cache = calendar.FromCache,
                currency = calendar.Request.Search.Currency,
                cheapest = calendar.Cheapest is { } c ? Price(c) : null,
                prices = calendar.Prices.Select(Price),
            });
        });

    [McpServerTool(Name = "lookup_airport", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(Docs.LookupAirport)]
    public Task<string> LookupAirport(
        [Description(Docs.AirportQuery)] string query,
        [Description(Docs.Verify)] bool verify = false,
        CancellationToken ct = default)
        => RunAsync(async () =>
        {
            var lookup = await client.LookupAirportsAsync(query, verify, ct).ConfigureAwait(false);
            return Serialize(new
            {
                retrieved_at = lookup.RetrievedAt,
                from_cache = lookup.FromCache,
                places = lookup.Places.Select(p => new
                {
                    p.Name,
                    p.Description,
                    airports = p.Airports.Select(a => new { a.Code, a.Name, a.City, a.Distance, is_airport = a.IsAirport }),
                }),
            });
        });

    [McpServerTool(Name = "lookup_airline", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Docs.LookupAirline)]
    public Task<string> LookupAirline(
        [Description(Docs.AirlineQuery)] string? query = null,
        [Description(Docs.Verify)] bool verify = false)
        => RunAsync(() =>
        {
            _ = verify; // a dated data file: nothing to refresh
            var directory = GoogleFlightsClient.Airlines;
            return Task.FromResult(Serialize(new
            {
                data_version = directory.Version,
                note = "Alliance membership is a snapshot as of data_version; an airline with no alliance may simply not be listed in one.",
                airlines = directory.Lookup(query).Select(a => new { a.Code, a.Name, alliance = a.Alliance is { } al ? AllianceName(al) : null }),
            }));
        });

    private static string Flights(FlightSearchResult result)
    {
        var request = result.Request;
        var lastLeg = result.Leg == request.Legs.Count - 1;
        return Serialize(new
        {
            trip_type = request.TripType switch
            {
                TripType.OneWay => "one_way",
                TripType.RoundTrip => "round_trip",
                _ => "multi_city",
            },
            leg = result.Leg + 1,
            of_legs = request.Legs.Count,
            currency = request.Currency,
            retrieved_at = result.RetrievedAt,
            from_cache = result.FromCache,
            google_flights_url = result.GoogleFlightsUrl,
            next_step = lastLeg
                ? "Booking options are not available here: the person opens google_flights_url and buys from the seller there."
                : $"Pass a flight's selection to get_return_flights for leg {result.Leg + 2}.",
            flights = result.BestFlights.Select(f => Flight(f, best: true)).Concat(result.OtherFlights.Select(f => Flight(f, best: false))),
            airlines_in_results = result.Airlines.Select(a => new { a.Code, a.Name }),
        });
    }

    private static object Flight(FlightOption f, bool best) => new
    {
        selection = f.Selection.ToHandle(),
        best,
        price = f.Price?.Amount,
        from = f.Origin,
        to = f.Destination,
        departs = Local(f.DepartsLocal),
        arrives = Local(f.ArrivesLocal),
        duration_minutes = (int)f.Duration.TotalMinutes,
        stops = f.Stops,
        airlines = f.Airlines,
        separate_tickets = f.SeparateTickets,
        segments = f.Segments.Select(s => new
        {
            flight = s.Flight,
            s.From,
            s.To,
            departs = s.Departs?.ToString("yyyy-MM-dd'T'HH:mmzzz", CultureInfo.InvariantCulture) ?? Local(s.DepartsLocal),
            arrives = s.Arrives?.ToString("yyyy-MM-dd'T'HH:mmzzz", CultureInfo.InvariantCulture) ?? Local(s.ArrivesLocal),
            duration_minutes = (int)s.Duration.TotalMinutes,
            s.Airline,
            operated_by = s.OperatedBy,
            s.Aircraft,
            s.Legroom,
        }),
        layovers = f.Layovers.Count == 0 ? null : f.Layovers.Select(l => new
        {
            airport = l.ArrivalAirport,
            departs_from = l.ChangesAirport ? l.DepartureAirport : null,
            minutes = (int)l.Duration.TotalMinutes,
            l.City,
        }),
    };

    private static object Price(CalendarPrice p) => new
    {
        departure = p.Departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        @return = p.Return?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        price = p.Price.Amount,
    };

    private static string Local(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    private static string AllianceName(Alliance a) => a switch
    {
        Alliance.Oneworld => "oneworld",
        Alliance.SkyTeam => "skyteam",
        _ => "star_alliance",
    };

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Turns the client's errors into tool errors whose message the AI can act on.</summary>
    private static async Task<string> RunAsync(Func<Task<string>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ArgumentException e)
        {
            throw new McpException(e.Message);
        }
        catch (GoogleFlightsThrottledException e)
        {
            throw new McpException($"{e.Message} Do not retry before {e.RetryAfter:u}; tell the user instead.");
        }
        catch (GoogleFlightsException e)
        {
            throw new McpException(e.Message);
        }
    }
}
