using System.Globalization;

namespace Multiplex.Sites.GoogleFlights;

/// <summary>
/// A search as the MCP tools and PowerShell commands take it: flat parameters, filters applied to every leg.
/// Both front ends build their searches here, so they accept the same inputs with the same rules and messages.
/// </summary>
public sealed record SearchParameters
{
    public string? Origin { get; init; }
    public string? Destination { get; init; }
    public DateOnly? DepartureDate { get; init; }
    public DateOnly? ReturnDate { get; init; }

    /// <summary>Multi-city legs, instead of <see cref="Origin"/>, <see cref="Destination"/> and the dates.</summary>
    public IReadOnlyList<FlightLeg>? Legs { get; init; }

    public MaxStops Stops { get; init; } = MaxStops.Any;
    public IReadOnlyList<string> Airlines { get; init; } = [];
    public IReadOnlyList<Alliance> Alliances { get; init; } = [];
    public int? DepartAfterHour { get; init; }
    public int? DepartBeforeHour { get; init; }
    public int? ArriveAfterHour { get; init; }
    public int? ArriveBeforeHour { get; init; }
    public double? MaxDurationHours { get; init; }
    public CabinClass Cabin { get; init; } = CabinClass.Economy;
    /// <summary>Leave out Basic Economy fares such as Delta Main Basic: on unless asked otherwise (owner's decision, 2026-11-22).</summary>
    public bool ExcludeBasicEconomy { get; init; } = true;
    public int Adults { get; init; } = 1;
    public int Children { get; init; }
    public int InfantsOnLap { get; init; }
    public int InfantsInSeat { get; init; }
    public int? MaxPrice { get; init; }
    public FlightSort Sort { get; init; } = FlightSort.Best;
    public string? Currency { get; init; }
    public string? Region { get; init; }

    /// <summary>The search: multi-city when <see cref="Legs"/> are given, a round trip with a return date, else one way.</summary>
    /// <exception cref="ArgumentException">A parameter is missing, contradictory or out of range.</exception>
    public FlightSearchRequest ToRequest()
    {
        var filters = Filters();
        FlightSearchRequest request;
        if (Legs is { Count: > 0 })
        {
            if (Origin is not null || Destination is not null || DepartureDate is not null || ReturnDate is not null)
                throw new ArgumentException("Give either multi-city legs or origin, destination and dates, not both.");
            request = FlightSearchRequest.MultiCity(Legs.Select(l => l with { Filters = filters }));
        }
        else
        {
            var (origin, destination, departure) = Required();
            request = ReturnDate is { } back
                ? FlightSearchRequest.RoundTrip(origin, destination, departure, back, filters)
                : FlightSearchRequest.OneWay(origin, destination, departure, filters);
        }
        return request with
        {
            Cabin = Cabin,
            ExcludeBasicEconomy = ExcludeBasicEconomy,
            Passengers = new Passengers(Adults, Children, InfantsOnLap, InfantsInSeat),
            MaxPrice = MaxPrice,
            Sort = Sort,
            Currency = Currency,
            Region = Region,
        };
    }

    /// <summary>
    /// A price calendar for departures from <paramref name="from"/> to <paramref name="to"/>: one way, a round trip
    /// of <paramref name="tripLengthDays"/>, or a round trip's grid of departures by returns.
    /// </summary>
    public CalendarRequest ToCalendarRequest(DateOnly from, DateOnly to, int? tripLengthDays, DateOnly? returnFrom, DateOnly? returnTo)
    {
        if (Legs is { Count: > 0 })
            throw new ArgumentException("Google Flights has no price calendar for multi-city trips.");
        if (tripLengthDays is < 0 or > 330)
            throw new ArgumentException("The trip length must be 0 to 330 days.", nameof(tripLengthDays));
        if (tripLengthDays is not null && (returnFrom is not null || returnTo is not null))
            throw new ArgumentException("Give a trip length or a return date range, not both.");

        DateOnly? back = tripLengthDays is { } days ? from.AddDays(days) : returnFrom;
        var search = (this with { DepartureDate = from, ReturnDate = back }).ToRequest();
        return new CalendarRequest(search, from, to) { ReturnFrom = returnFrom, ReturnTo = returnTo };
    }

    private (string Origin, string Destination, DateOnly Departure) Required()
    {
        if (string.IsNullOrWhiteSpace(Origin))
            throw new ArgumentException("Give the departure airport (origin), e.g. SEA.");
        if (string.IsNullOrWhiteSpace(Destination))
            throw new ArgumentException("Give the arrival airport (destination), e.g. DEN.");
        if (DepartureDate is not { } departure)
            throw new ArgumentException("Give the departure date, yyyy-MM-dd.");
        return (Origin, Destination, departure);
    }

    private LegFilters Filters() => new()
    {
        Stops = Stops,
        Airlines = Airlines ?? [],
        Alliances = Alliances ?? [],
        DepartureHours = Hours(DepartAfterHour, DepartBeforeHour, "departure"),
        ArrivalHours = Hours(ArriveAfterHour, ArriveBeforeHour, "arrival"),
        MaxDuration = MaxDurationHours switch
        {
            null => null,
            <= 0 or > 99 => throw new ArgumentException("The maximum duration must be more than 0 and at most 99 hours."),
            { } h => TimeSpan.FromMinutes(Math.Floor(h * 60)),
        },
    };

    private static HourRange? Hours(int? after, int? before, string what)
    {
        if (after is null && before is null)
            return null;
        var range = new HourRange(after ?? 0, before ?? 24);
        range.Validate($"The {what} hours");
        return range;
    }

    /// <summary>Reads the text forms the MCP tools take. Each throws an <see cref="ArgumentException"/> naming the input.</summary>
    public static class Text
    {
        public static DateOnly Date(string value, string name) =>
            DateOnly.TryParseExact((value ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : throw new ArgumentException($"{name} '{value}' is not a date in the form yyyy-MM-dd.", name);

        public static DateOnly? OptionalDate(string? value, string name) =>
            string.IsNullOrWhiteSpace(value) ? null : Date(value, name);

        /// <summary>A multi-city leg written <c>ORIGIN-DESTINATION-yyyy-MM-dd</c>, e.g. <c>SEA-DEN-2026-12-15</c>.</summary>
        public static FlightLeg Leg(string value)
        {
            var parts = (value ?? "").Trim().Split('-', 3);
            if (parts.Length != 3)
                throw new ArgumentException($"Leg '{value}' is not in the form ORIGIN-DESTINATION-yyyy-MM-dd, e.g. SEA-DEN-2026-12-15.");
            return new FlightLeg(parts[0], parts[1], Date(parts[2], $"the date of leg '{value}'"));
        }

        public static MaxStops Stops(string? value) => Key(value) switch
        {
            "" or "ANY" => MaxStops.Any,
            "NONSTOP" or "0" or "NONE" => MaxStops.Nonstop,
            "ONE" or "1" or "ONEORFEWER" => MaxStops.OneOrFewer,
            "TWO" or "2" or "TWOORFEWER" => MaxStops.TwoOrFewer,
            _ => throw new ArgumentException($"max_stops '{value}' is not one of any, nonstop, one or two."),
        };

        public static CabinClass Cabin(string? value) => Key(value) switch
        {
            "" or "ECONOMY" => CabinClass.Economy,
            "PREMIUMECONOMY" or "PREMIUM" => CabinClass.PremiumEconomy,
            "BUSINESS" => CabinClass.Business,
            "FIRST" => CabinClass.First,
            _ => throw new ArgumentException($"cabin '{value}' is not one of economy, premium_economy, business or first."),
        };

        public static FlightSort Sort(string? value) => Key(value) switch
        {
            "" or "BEST" or "TOP" => FlightSort.Best,
            "PRICE" or "CHEAPEST" => FlightSort.Price,
            "DEPARTURETIME" or "DEPARTURE" => FlightSort.DepartureTime,
            "ARRIVALTIME" or "ARRIVAL" => FlightSort.ArrivalTime,
            "DURATION" => FlightSort.Duration,
            _ => throw new ArgumentException($"sort '{value}' is not one of best, price, departure_time, arrival_time or duration."),
        };

        public static Alliance Alliance(string? value) => Key(value) switch
        {
            "ONEWORLD" => GoogleFlights.Alliance.Oneworld,
            "SKYTEAM" => GoogleFlights.Alliance.SkyTeam,
            "STARALLIANCE" or "STAR" => GoogleFlights.Alliance.StarAlliance,
            _ => throw new ArgumentException($"alliance '{value}' is not one of oneworld, skyteam or star_alliance."),
        };

        private static string Key(string? value) =>
            new([.. (value ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);
    }
}
