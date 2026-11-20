using System.Text.RegularExpressions;

namespace Multiplex.Sites.GoogleFlights;

/// <summary>The kind of trip Google searches.</summary>
public enum TripType
{
    RoundTrip = 1,
    OneWay = 2,
    MultiCity = 3,
}

/// <summary>The cabin searched.</summary>
public enum CabinClass
{
    Economy = 1,
    PremiumEconomy = 2,
    Business = 3,
    First = 4,
}

/// <summary>The most stops a flight on a leg may make.</summary>
public enum MaxStops
{
    Any = 0,
    Nonstop = 1,
    OneOrFewer = 2,
    TwoOrFewer = 3,
}

/// <summary>An airline alliance, as Google's airline filter names it.</summary>
public enum Alliance
{
    Oneworld,
    SkyTeam,
    StarAlliance,
}

/// <summary>The order Google lists flights in. There is no emissions order: the client exposes no emissions data.</summary>
public enum FlightSort
{
    /// <summary>Google's default: its own "top flights" ranking.</summary>
    Best = 0,
    Price = 2,
    DepartureTime = 3,
    ArrivalTime = 4,
    Duration = 5,
}

/// <summary>
/// A range of hours as Google's time sliders show them: <see cref="From"/> 0-23 and <see cref="To"/> 1-24,
/// so <c>new HourRange(6, 20)</c> is "between 6:00 and 20:00".
/// </summary>
public sealed record HourRange(int From, int To)
{
    public static HourRange AllDay { get; } = new(0, 24);

    internal void Validate(string name)
    {
        if (From is < 0 or > 23 || To is < 1 or > 24 || From >= To)
            throw new ArgumentException($"{name} must be whole hours with 0 <= from < to <= 24; got {From}-{To}.", name);
    }
}

/// <summary>Travelers. Google allows at most 9, and no more infants on a lap than adults.</summary>
public sealed record Passengers(int Adults = 1, int Children = 0, int InfantsOnLap = 0, int InfantsInSeat = 0)
{
    internal void Validate()
    {
        if (Adults < 1)
            throw new ArgumentException("At least one adult must travel.", nameof(Adults));
        if (Children < 0 || InfantsOnLap < 0 || InfantsInSeat < 0)
            throw new ArgumentException("Passenger counts cannot be negative.");
        if (Adults + Children + InfantsOnLap + InfantsInSeat > 9)
            throw new ArgumentException("Google Flights searches for at most 9 travelers.");
        if (InfantsOnLap > Adults)
            throw new ArgumentException("Each infant on a lap needs an adult.", nameof(InfantsOnLap));
    }
}

/// <summary>Filters that apply to one leg. In a multi-city trip each leg keeps its own.</summary>
public sealed record LegFilters
{
    public static LegFilters None { get; } = new();

    public MaxStops Stops { get; init; } = MaxStops.Any;

    /// <summary>IATA codes of the airlines to include, e.g. <c>DL</c>, <c>AS</c>. Empty means any airline.</summary>
    public IReadOnlyList<string> Airlines { get; init; } = [];

    /// <summary>Alliances to include, alongside <see cref="Airlines"/>.</summary>
    public IReadOnlyList<Alliance> Alliances { get; init; } = [];

    /// <summary>When a flight may depart, in the airport's local time.</summary>
    public HourRange? DepartureHours { get; init; }

    /// <summary>When a flight may arrive, in the airport's local time.</summary>
    public HourRange? ArrivalHours { get; init; }

    /// <summary>The longest total travel time, rounded down to whole minutes.</summary>
    public TimeSpan? MaxDuration { get; init; }
}

/// <summary>One flight of a trip: from an airport to an airport on a date.</summary>
public sealed record FlightLeg(string Origin, string Destination, DateOnly Date)
{
    public LegFilters Filters { get; init; } = LegFilters.None;
}

/// <summary>
/// A flight search. Build one with <see cref="OneWay"/>, <see cref="RoundTrip"/> or <see cref="MultiCity"/>.
/// The client validates and normalizes it before sending anything to Google.
/// </summary>
public sealed partial record FlightSearchRequest
{
    public required TripType TripType { get; init; }

    public required IReadOnlyList<FlightLeg> Legs { get; init; }

    public CabinClass Cabin { get; init; } = CabinClass.Economy;

    public Passengers Passengers { get; init; } = new();

    /// <summary>The highest price, in whole units of the request's currency. <c>null</c> means no limit.</summary>
    public int? MaxPrice { get; init; }

    /// <summary>Economy without Basic Economy fares (Google's "Economy (exclude basic)").</summary>
    public bool ExcludeBasicEconomy { get; init; }

    public FlightSort Sort { get; init; } = FlightSort.Best;

    /// <summary>ISO 4217 currency, e.g. <c>USD</c>. <c>null</c> uses the client's default.</summary>
    public string? Currency { get; init; }

    /// <summary>Two-letter region (country) Google prices for, e.g. <c>US</c>. <c>null</c> uses the client's default.</summary>
    public string? Region { get; init; }

    public static FlightSearchRequest OneWay(string origin, string destination, DateOnly date, LegFilters? filters = null) => new()
    {
        TripType = TripType.OneWay,
        Legs = [new FlightLeg(origin, destination, date) { Filters = filters ?? LegFilters.None }],
    };

    public static FlightSearchRequest RoundTrip(string origin, string destination, DateOnly departure, DateOnly @return, LegFilters? filters = null) => new()
    {
        TripType = TripType.RoundTrip,
        Legs =
        [
            new FlightLeg(origin, destination, departure) { Filters = filters ?? LegFilters.None },
            new FlightLeg(destination, origin, @return) { Filters = filters ?? LegFilters.None },
        ],
    };

    public static FlightSearchRequest MultiCity(params IEnumerable<FlightLeg> legs) => new()
    {
        TripType = TripType.MultiCity,
        Legs = [.. legs],
    };

    /// <summary>
    /// Checks the request and returns it in the one form the client sends and caches: codes upper-case,
    /// airline lists de-duplicated, currency and region filled from the defaults.
    /// </summary>
    /// <exception cref="ArgumentException">The request cannot be searched; the message says which input.</exception>
    public FlightSearchRequest Normalize(string defaultCurrency, string defaultRegion)
    {
        if (Legs is null || Legs.Count == 0)
            throw new ArgumentException("A search needs at least one leg.", nameof(Legs));
        if (!Enum.IsDefined(TripType))
            throw new ArgumentException($"Unknown trip type {TripType}.", nameof(TripType));
        if (!Enum.IsDefined(Cabin))
            throw new ArgumentException($"Unknown cabin {Cabin}.", nameof(Cabin));
        if (!Enum.IsDefined(Sort))
            throw new ArgumentException($"Unknown sort order {Sort}.", nameof(Sort));

        var legs = Legs.Select((leg, i) => NormalizeLeg(leg ?? throw new ArgumentException($"Leg {i + 1} is missing."), i)).ToArray();
        switch (TripType)
        {
            case TripType.OneWay when legs.Length != 1:
                throw new ArgumentException("A one-way search has exactly one leg.", nameof(Legs));
            case TripType.RoundTrip when legs.Length != 2:
                throw new ArgumentException("A round trip has exactly two legs: there and back.", nameof(Legs));
            case TripType.RoundTrip when legs[1].Origin != legs[0].Destination || legs[1].Destination != legs[0].Origin:
                throw new ArgumentException("A round trip's return leg must fly back from the destination to the origin.", nameof(Legs));
            case TripType.MultiCity when legs.Length < 2:
                throw new ArgumentException("A multi-city search has at least two legs.", nameof(Legs));
        }
        for (var i = 1; i < legs.Length; i++)
        {
            if (legs[i].Date < legs[i - 1].Date)
                throw new ArgumentException($"Leg {i + 1} ({legs[i].Date:yyyy-MM-dd}) is before leg {i} ({legs[i - 1].Date:yyyy-MM-dd}).", nameof(Legs));
        }

        (Passengers ?? throw new ArgumentException("Passengers are missing.", nameof(Passengers))).Validate();
        if (MaxPrice is <= 0)
            throw new ArgumentException("The maximum price must be a positive whole amount.", nameof(MaxPrice));

        return this with
        {
            Legs = legs,
            Currency = NormalizeCurrency(Currency ?? defaultCurrency),
            Region = NormalizeRegion(Region ?? defaultRegion),
        };
    }

    internal static string NormalizeCurrency(string currency)
    {
        var c = currency.Trim().ToUpperInvariant();
        if (!CurrencyPattern().IsMatch(c))
            throw new ArgumentException($"'{currency}' is not an ISO 4217 currency code such as USD.", nameof(Currency));
        return c;
    }

    internal static string NormalizeRegion(string region)
    {
        var r = region.Trim().ToUpperInvariant();
        if (!RegionPattern().IsMatch(r))
            throw new ArgumentException($"'{region}' is not a two-letter region code such as US.", nameof(Region));
        return r;
    }

    private static FlightLeg NormalizeLeg(FlightLeg leg, int index)
    {
        var name = $"leg {index + 1}";
        var origin = NormalizeAirport(leg.Origin, $"{name} origin");
        var destination = NormalizeAirport(leg.Destination, $"{name} destination");
        if (origin == destination)
            throw new ArgumentException($"The {name} origin and destination are both {origin}.");

        var f = leg.Filters ?? LegFilters.None;
        if (!Enum.IsDefined(f.Stops))
            throw new ArgumentException($"Unknown stops filter {f.Stops} on {name}.");
        f.DepartureHours?.Validate($"{name} departure hours");
        f.ArrivalHours?.Validate($"{name} arrival hours");
        if (f.MaxDuration is { } d && d < TimeSpan.FromMinutes(1))
            throw new ArgumentException($"The {name} maximum duration must be at least a minute.");

        // Order is kept as given: it is the order Google's page sends them in, and Google answers either way.
        var airlines = (f.Airlines ?? []).Select(a => NormalizeAirline(a, name)).Distinct(StringComparer.Ordinal).ToArray();
        var alliances = (f.Alliances ?? []).Distinct().ToArray();
        foreach (var a in alliances)
        {
            if (!Enum.IsDefined(a))
                throw new ArgumentException($"Unknown alliance {a} on {name}.");
        }

        return new FlightLeg(origin, destination, leg.Date)
        {
            Filters = f with
            {
                Airlines = airlines,
                Alliances = alliances,
                DepartureHours = f.DepartureHours == HourRange.AllDay ? null : f.DepartureHours,
                ArrivalHours = f.ArrivalHours == HourRange.AllDay ? null : f.ArrivalHours,
                MaxDuration = f.MaxDuration is { } m ? TimeSpan.FromMinutes(Math.Floor(m.TotalMinutes)) : null,
            },
        };
    }

    private static string NormalizeAirport(string? code, string what)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        if (!AirportPattern().IsMatch(c))
            throw new ArgumentException($"The {what} '{code}' is not a three-letter IATA airport code such as SEA. Look the airport up first.");
        return c;
    }

    private static string NormalizeAirline(string? code, string leg)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        if (!AirlinePattern().IsMatch(c))
            throw new ArgumentException($"'{code}' on {leg} is not a two-character IATA airline code such as DL.");
        return c;
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex AirportPattern();

    [GeneratedRegex("^[A-Z0-9]{2}$")]
    private static partial Regex AirlinePattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex RegionPattern();
}
