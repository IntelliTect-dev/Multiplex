# Google Flights service

The MCP tools at `/google-flights/mcp`: `search_flights`, `get_return_flights`,
`get_booking_options`, `get_price_calendar`, `lookup_airport` and
`lookup_airline`. They are thin wrappers over the Google Flights client in
`services/GoogleFlights/`, where everything else lives: the client, its docs,
the protocol field maps, the references and the tests. Start with
[its README](../../../../services/GoogleFlights/README.md).

- `GoogleFlightsTools.cs`: the tools. Parameters go through the client's
  `SearchParameters` and descriptions come from its `GoogleFlightsDocs`, so the
  PowerShell commands take the same inputs and say the same things. A flight
  list holds at most 30 flights, with `total_flights` and, when there are more,
  `more_flights` saying how to narrow the search (owner's decisions, #134: a
  busy route's full list is past an AI's limit for one tool answer). In
  Google's own order the list is Google's best flights, then the rest by fewest
  stops, price and departure time, since Google lists the rest by price alone;
  a sort asked for is kept as the page gives it. The PowerShell commands return
  every flight, in Google's order.
- `GoogleFlightsServiceRegistration.cs`: one shared client for the whole
  server, so its cache, pacing and refusal cool-down cover every request.
  Settings come from the `GoogleFlights` section (`Currency`, `Region`,
  `CacheMinutes`).

The endpoint needs no sign-in, so it answers only requests from this PC unless
`Multiplex:PublicOpenEndpoints` lists `google-flights`.

These files move into `services/GoogleFlights/` with the host-contracts work in
#83.

## Icon

`wwwroot/icons/google-flights.png` (192x192), the icon Google Flights' own page declares (`<link rel="icon" sizes="192x192">`), from `https://www.gstatic.com/images/branding/product/2x/travel_flights_96dp.png`. Clients show it beside the service: its `serverInfo` names it, with the service's title and website (#135). To refresh it, fetch the same URL again.
