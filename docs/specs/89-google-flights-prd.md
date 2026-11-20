# Google Flights site

- Issue: https://github.com/IntelliTect-dev/Multiplex/issues/89
- PR:    https://github.com/IntelliTect-dev/Multiplex/pull/90
- Slug:  89-google-flights

The owner's requirements, as given on 2026-11-19 and confirmed as the spec.

Build the Google Flights module for Multiplex. Use the web-api-discovery skill
for the whole process.

## Ground rules

1. Build from scratch against Google Flights' own web endpoints. Do not use any
   third-party flight API or scraping service (no HasData, SerpApi, etc.).
2. Follow the standard Multiplex per-site layout: scrubbed HAR files, a C#
   client library that everything else calls, an MCP server, a PowerShell
   module, and docs. No CLI tool.
3. The client library is the single source of truth. MCP tools and cmdlets are
   thin wrappers over it, with no duplicated logic.
4. Docs are written once and feed the MCP tool descriptions and PowerShell
   help. Don't duplicate them.

## Phase 1: Discovery (web-api-discovery skill)

1. Capture HAR sessions covering one-way, round-trip and multi-city searches,
   each filter (airlines, alliances, stops, cabin, passengers, time windows,
   max price, max duration, sort), the cheapest-dates calendar, the
   return-flight step after picking an outbound, booking options, and airport
   autocomplete.
2. Scrub cookies, consent tokens, IPs and anything personal before committing.
3. Identify the endpoints and decode the positional nested-array payloads
   (including the `tfs` URL parameter). Write the field maps into the docs.
4. Stop and report findings before writing the client, including anything that
   blocks direct HTTP access (consent walls, bot detection).

## Phase 2: Client library (C#)

1. `GoogleFlightsClient` with `SearchAsync`, `GetReturnFlightsAsync`,
   `GetCalendarAsync`, `GetBookingOptionsAsync`, `LookupAirportsAsync`, plus
   airline/alliance lookup from a versioned data file.
2. Keep request encoding and response parsing in their own layer so Google
   format changes stay contained.
3. Immutable, strongly typed models. No emissions data.
4. Currency is selectable per request (ISO 4217), defaulting to USD; region
   defaults to US. Both can also be set as client defaults.
5. Caching: cache results briefly (configurable, about 10 to 15 minutes), keyed
   on the normalized request. A `Verify`/`ForceRefresh` option bypasses it.
   Results expose `RetrievedAt` and `FromCache`. Booking options are always
   fetched fresh.
6. Use the shared resilience package for retries, backoff, rate limiting,
   timeouts and cancellation. Fall back to a read-only browser only if direct
   HTTP is blocked.

## Phase 3: MCP server and PowerShell module

1. MCP tools: `search_flights`, `get_return_flights`, `get_price_calendar`,
   `get_booking_options`, `lookup_airport`, `lookup_airline`, each with a
   `verify` flag. The descriptions tell the AI to set `verify: true` when the
   user asks to verify, confirm or double-check.
2. Cmdlets: `Find-Flight`, `Get-FlightReturn`, `Get-FlightPriceCalendar`,
   `Get-FlightBookingOption`, `Find-Airport`, `Get-Airline`. They output
   objects, are pipeline friendly and support `-Verify`.

## Phase 4: Tests

1. Mocked unit tests driven by the scrubbed HAR fixtures: encoding, parsing,
   filters, validation and caching behavior.
2. Live tests: one per trip type, a sample of the filter matrix, the calendar,
   and booking options. They assert on structure, never on exact prices.
3. A contract-drift live check that fails clearly when Google changes its
   payloads.

## Done means

1. All spec features work end to end through the client, MCP and PowerShell.
2. Unit and live tests pass.
3. Docs include the payload field maps.
4. Remaining follow-ups are filed as GitHub issues on Multiplex rather than
   TODO comments.

## Added during the work

- **Throttling (owner, 2026-11-19):** "Be careful of throttling as you work...
  take your time." Discovery and live tests run on a small, paced request
  budget; see `sites/google-flights/NOTES.md`.
- **Browser use needs approval (owner, 2026-11-20):** "WE should not use a
  browser by default - for any provider", "Browser for auth only", then "any
  browser use by the MCP needs approval on per provider, per use case basis".
  This **replaces** Phase 2 item 6's "fall back to a read-only browser only if
  direct HTTP is blocked": the client fetches over direct HTTP, and a browser
  is used only for a provider and use case the owner has approved. Google
  Flights has no such approval.
- **Client identity (owner, 2026-11-20):** the client identifies itself with
  the honest-bot User-Agent `Mozilla/5.0 (compatible; Multiplex-GoogleFlights/<version>; +<url>)`.
  Google refuses a plain product token and answers this form; nothing poses as
  a browser.
- **Design scenario (owner, 2026-11-19):** a sample multi-country
  island-hopping research question, not to be run for real, recorded without
  personal details on the issue:
  https://github.com/IntelliTect-dev/Multiplex/issues/89#issuecomment-7022985109
