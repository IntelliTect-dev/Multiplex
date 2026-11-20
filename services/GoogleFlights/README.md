# Google Flights

Flight search for Multiplex, built from scratch against the endpoints Google
Flights' own web page calls (`www.google.com/_/FlightsFrontendUi/...`), over
direct HTTP. No browser, no sign-in, and no third-party flight API or scraping
service. Read-only: nothing is booked or bought.

- Issue: https://github.com/IntelliTect-dev/Multiplex/issues/89
- Layout: one folder per provider, `services/<Name>/` (the owner's decision in
  #83, which refines #4's per-site outputs).

## What it does

| Operation | C# client | MCP tool | PowerShell |
|---|---|---|---|
| Flights for a one-way, round-trip or multi-city search, with filters | `SearchAsync` | `search_flights` | `Find-Flight` |
| The next leg's flights after a choice (a round trip's returns) | `GetReturnFlightsAsync` | `get_return_flights` | `Get-FlightReturn` |
| Sellers, fare brands and prices for a fully chosen trip | `GetBookingOptionsAsync` | `get_booking_options` | `Get-FlightBookingOption` |
| Lowest round-trip price per departure date, or per departure-by-return pair | `GetCalendarAsync` | `get_price_calendar` | `Get-FlightPriceCalendar` |
| Airports for a place name or code | `LookupAirportsAsync` | `lookup_airport` | `Find-Airport` |
| Airlines and alliances, from a dated data file | `GoogleFlightsClient.Airlines` | `lookup_airline` | `Get-Airline` |

Filters: stops, airlines, alliances, departure and arrival hours, maximum
price, maximum duration, cabin (and economy without Basic), passengers, sort
order. Currency and region per request (default USD and US). Every result says
when Google answered (`RetrievedAt`) and whether it came from the cache
(`FromCache`); every call can skip the cache (`verify` / `-Verify`).

**Not available:** emissions data (deliberately not exposed), one-way price
calendars (no recorded request yet, and the one form tried was refused), and
anything that books.

The tools, the commands' help and the client's docs share one set of
descriptions (`src/GoogleFlights/GoogleFlightsDocs.cs`).

## Use it

**From an AI client:** Multiplex serves the tools at `/google-flights/mcp`.
The endpoint needs no sign-in, so, like every open Multiplex endpoint, it
answers only requests from the PC Multiplex runs on unless
`Multiplex:PublicOpenEndpoints` lists `google-flights`. Listing it lets anyone
who finds the address spend this network's Google Flights requests.

**From PowerShell:**

```powershell
dotnet build services/GoogleFlights/src/GoogleFlights.PowerShell
Import-Module ./services/GoogleFlights/src/GoogleFlights.PowerShell/bin/Debug/net10.0/GoogleFlights.psd1
Find-Flight SEA DEN 2026-12-15 -ReturnDate 2026-12-22 -MaxStops Nonstop |
    Select-Object -First 1 | Get-FlightReturn
Get-Help Find-Flight -Full
```

**From C#:** reference `src/GoogleFlights` and keep one `GoogleFlightsClient`
for the process:

```csharp
using var flights = new GoogleFlightsClient();
var outbound = await flights.SearchAsync(FlightSearchRequest.RoundTrip("SEA", "DEN", new(2026, 12, 15), new(2026, 12, 22)));
var returns = await flights.GetReturnFlightsAsync(outbound.BestFlights[0].Selection);
var booking = await flights.GetBookingOptionsAsync(returns.BestFlights[0].Selection);
```

## Limits and safety

- **Google throttles, and allows this client little.** A block applies to the
  whole network. On 2026-11-20 Google started refusing the client after about
  16 requests from the network that day; the allowance and how long a refusal
  lasts are not measured yet. The client paces itself (4 requests back to back,
  then one per 20 seconds), caches answers for 12 minutes, never retries a
  refusal, and after one stops sending anything for 15 minutes (doubling with
  each refusal in a row, up to 6 hours). See `NOTES.md`.
- **Identity.** The client says who it is:
  `Mozilla/5.0 (compatible; Multiplex-GoogleFlights/<version>; +<url>)`. It
  does not pose as a browser and sends no browser headers or page tokens.
- **Not affiliated with Google.** These endpoints are undocumented and may
  change without notice; the contract-drift live test is how a change is
  noticed.

## Tests

| Kind | Command | What it needs |
|---|---|---|
| Unit (encoding, parsing, cache, pacing, refusals) | `dotnet test services/GoogleFlights/tests/GoogleFlights.Tests` | Nothing: fixtures are the committed references |
| MCP tools over the real pipeline | `dotnet test tests/Multiplex.Tests --filter GoogleFlights` | Nothing |
| PowerShell module, including the help-drift check | `Invoke-Pester services/GoogleFlights/tests/GoogleFlights.PowerShell.Tests` | A build first |
| Live: the client, every MCP tool, the module | the same commands with `MULTIPLEX_LIVE_TESTS=1` | Internet access. About 20 paced requests in all; never run them in a loop |

`tests/Invoke-LiveTests.ps1` runs all of the live tests in one go on the
machine's own connection: build, offline tests, one search first, then the
rest, stopping at the first failure or refusal. Run it on a connection whose
Google Flights allowance you can spare.

Live tests assert structure and meaning, never prices. Every live exchange is
saved in full to the repository's gitignored capture store
(`.har-captures/www.google.com/<stamp>-live/`), so a refusal is kept as
evidence. After a refusal the remaining live tests fail at once without
reaching Google.

The data file's alliance memberships are a dated snapshot
(`src/GoogleFlights/Data/airlines.json`): check them against the alliances'
own member lists when updating it, and change its `version`.

## What is here

| Path | What it is |
|---|---|
| `NOTES.md` | Decisions and traps: read before changing anything |
| `docs/protocol.md` | The decoded protocol: endpoints, framing and the field maps (the spec for `src/GoogleFlights/Wire/`) |
| `references/` | Trimmed, scrubbed HAR extracts, their catalogue, and the re-capture recipe (evidence and test fixtures, not the spec) |
| `src/GoogleFlights/` | The client library (`Multiplex.Sites.GoogleFlights`); `Wire/` is Google's format, nothing else is |
| `src/GoogleFlights.PowerShell/` | The PowerShell module `GoogleFlights` |
| `tests/` | Unit and live tests, and the module's Pester tests |

The MCP tools themselves are in `src/Multiplex.Web/Services/GoogleFlights/`
until the host-contracts library of #83 lets a provider's host piece live here.
