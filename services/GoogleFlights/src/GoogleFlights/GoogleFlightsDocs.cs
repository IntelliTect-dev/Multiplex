namespace Multiplex.Sites.GoogleFlights;

/// <summary>
/// The one place the operations and their parameters are described. The MCP tools' descriptions and the
/// PowerShell commands' help are both built from these strings, so they cannot drift apart.
/// </summary>
public static class GoogleFlightsDocs
{
    private const string VerifyNote =
        " Answers are cached for about 12 minutes; set verify to true when the user asks to verify, confirm, double-check or refresh.";

    public const string SearchFlights =
        "Searches Google Flights for one-way, round-trip or multi-city flights and lists the options for the first leg, " +
        "with prices, times, stops, layovers and airlines. A round trip's or multi-city trip's prices are for the whole trip. " +
        "Each option has a selection: pass it to get_return_flights (Get-FlightReturn) for the next leg, or, once every leg is " +
        "chosen, to get_booking_options (Get-FlightBookingOption). Airports are three-letter IATA codes; look them up first " +
        "with lookup_airport (Find-Airport)." + VerifyNote;

    public const string GetReturnFlights =
        "Lists the flights for the next leg after one is chosen: a round trip's return flights, or the next flight of a " +
        "multi-city trip. Pass the selection of the option chosen. On the last leg, separate_tickets says when Google sells " +
        "that choice as separate tickets." + VerifyNote;

    public const string GetBookingOptions =
        "Lists where a fully chosen trip can be bought: each seller (the airline or an agency), fare brand and price, and " +
        "a Google Flights link to the booking page for the person to open. Pass the selection of the last leg chosen. " +
        "Booking options are always fetched fresh, never from the cache; verify changes nothing here. Nothing is booked.";

    public const string GetPriceCalendar =
        "Lists the lowest price for each departure date in a range, for a one-way or round trip. A round trip keeps " +
        "trip_length_days fixed, or, given return_date_from and return_date_to, prices every departure-by-return pair. " +
        "Use it to find the cheapest dates before searching." + VerifyNote;

    public const string LookupAirport =
        "Finds airports for a place name, city or code, as Google Flights' own airport box does, e.g. 'Denver', 'Paris' or " +
        "'SEA'. Returns each matching place with its airports' IATA codes, to use in the other tools." + VerifyNote;

    public const string LookupAirline =
        "Finds airlines and alliances in the client's data file by IATA code ('DL'), name ('delta') or alliance " +
        "('SkyTeam', 'Star Alliance', 'oneworld', which lists its members). Use the codes and alliance names in " +
        "search_flights' (Find-Flight's) airlines and alliances. The file is a dated snapshot; verify changes nothing here.";

    public const string Verify =
        "Ask Google again instead of using a cached answer. Set it when the user asks to verify, confirm, double-check or refresh.";

    public const string Selection = "The selection of the chosen option, exactly as a previous result gave it.";

    public const string Origin = "Departure airport: a three-letter IATA code such as SEA.";

    public const string Destination = "Arrival airport: a three-letter IATA code such as DEN.";

    public const string DepartureDate = "Departure date, yyyy-MM-dd.";

    public const string ReturnDate = "Return date for a round trip, yyyy-MM-dd. Omit for one way.";

    public const string Legs =
        "Multi-city legs, in order, each 'ORIGIN-DESTINATION-yyyy-MM-dd' (e.g. 'SEA-DEN-2026-12-15'). Use instead of origin, " +
        "destination and the dates.";

    public const string MaxStops = "Most stops allowed: any (default), nonstop, one or two.";

    public const string Airlines = "Only these airlines: IATA codes such as DL or AS. Find codes with lookup_airline (Get-Airline).";

    public const string Alliances = "Only these alliances: oneworld, skyteam or star_alliance.";

    public const string DepartAfterHour = "Earliest departure hour, 0-23, local time at the departure airport.";

    public const string DepartBeforeHour = "Departures before this hour, 1-24, local time at the departure airport.";

    public const string ArriveAfterHour = "Earliest arrival hour, 0-23, local time at the arrival airport.";

    public const string ArriveBeforeHour = "Arrivals before this hour, 1-24, local time at the arrival airport.";

    public const string MaxDurationHours = "Longest total travel time per leg, in hours.";

    public const string Cabin = "Cabin: economy (default), premium_economy, business or first.";

    public const string ExcludeBasicEconomy = "Economy without Basic Economy fares.";

    public const string Adults = "Adults (default 1). At most 9 travelers in all.";

    public const string Children = "Children aged 2-11.";

    public const string InfantsOnLap = "Infants on a lap; no more than adults.";

    public const string InfantsInSeat = "Infants in their own seat.";

    public const string MaxPrice = "Highest price, in whole units of the currency.";

    public const string Sort = "Order: best (Google's default), price, departure_time, arrival_time or duration.";

    public const string Currency = "ISO 4217 currency for prices, e.g. USD (default), EUR, GBP.";

    public const string Region = "Two-letter country Google prices for, e.g. US (default).";

    public const string DepartureDateFrom = "First departure date to price, yyyy-MM-dd.";

    public const string DepartureDateTo = "Last departure date to price, yyyy-MM-dd.";

    public const string TripLengthDays = "For a round trip: days between departure and return. Omit for one way.";

    public const string ReturnDateFrom = "For a round trip's date grid: first return date, yyyy-MM-dd.";

    public const string ReturnDateTo = "For a round trip's date grid: last return date, yyyy-MM-dd.";

    public const string AirportQuery = "What to look up: a city, airport name or code, e.g. 'Denver'.";

    public const string AirlineQuery = "An IATA code, part of a name, or an alliance. Empty lists every airline in the file.";
}
