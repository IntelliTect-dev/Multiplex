# Google Flights service

The MCP tools at `/google-flights/mcp`: `search_flights`, `get_return_flights`,
`get_booking_options`, `get_price_calendar`, `lookup_airport` and
`lookup_airline`. They are thin wrappers over the Google Flights client in
`services/GoogleFlights/`, where everything else lives: the client, its docs,
the protocol field maps, the references and the tests. Start with
[its README](../../../../services/GoogleFlights/README.md).

- `GoogleFlightsTools.cs`: the tools. Parameters go through the client's
  `SearchParameters` and descriptions come from its `GoogleFlightsDocs`, so the
  PowerShell commands take the same inputs and say the same things.
- `GoogleFlightsServiceRegistration.cs`: one shared client for the whole
  server, so its cache, pacing and refusal cool-down cover every request.
  Settings come from the `GoogleFlights` section (`Currency`, `Region`,
  `CacheMinutes`).

The endpoint needs no sign-in, so it answers only requests from this PC unless
`Multiplex:PublicOpenEndpoints` lists `google-flights`.

These files move into `services/GoogleFlights/` with the host-contracts work in
#83.

## Icon

`wwwroot/icons/google-flights.png` (256x256, at most 10 KB), the icon Google Flights' own page declares (`<link rel="icon" sizes="192x192">`), from `https://www.gstatic.com/images/branding/product/2x/travel_flights_96dp.png`. It is fitted to 256x256 and at most 10 KB, the one file ChatGPT's "Add custom MCP server" form takes as an app's icon (#138). Clients that read `serverInfo` show it beside the service, with its title and website (#135); ChatGPT does not, so upload this file in that form when adding the service there. To refresh it, fetch the same URL again and fit it the same way (Phase 1 of the `web-api-discovery` skill).
