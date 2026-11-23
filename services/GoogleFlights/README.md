# Google Flights

Flight search for Multiplex, built from scratch over direct HTTPS against
Google Flights' own web pages: the client fetches a search page and reads the
flights it carries in its HTML, and asks the page's airport-lookup endpoint for
places. No browser, no sign-in, and no third-party flight API or scraping
service. Read-only: nothing is booked or bought.

Since the evening of 2026-11-20 Google answers its flight data endpoint only
with a token the page's own script computes for each request (#101). So what
the page loads only through its script is **not available** here: booking
options, price calendars and multi-city trips. Those calls send nothing and
answer with the Google Flights link to open instead (`docs/protocol.md`
section 2).

- Issue: https://github.com/IntelliTect-dev/Multiplex/issues/89
- Layout: one folder per provider, `services/<Name>/` (the owner's decision in
  #83, which refines #4's per-site outputs).

## What it does

| Operation | C# client | MCP tool | PowerShell |
|---|---|---|---|
| Flights for a one-way or round-trip search, with filters | `SearchAsync` | `search_flights` | `Find-Flight` |
| A round trip's return flights after its outbound is chosen | `GetReturnFlightsAsync` | `get_return_flights` | `Get-FlightReturn` |
| **Not available:** sellers and fares for a chosen trip; answers with the booking page's link | `GetBookingOptionsAsync` | `get_booking_options` | `Get-FlightBookingOption` |
| **Not available:** a price calendar; answers with the search page's link | `GetCalendarAsync` | `get_price_calendar` | `Get-FlightPriceCalendar` |
| Airports for a place name or code | `LookupAirportsAsync` | `lookup_airport` | `Find-Airport` |
| Airlines and alliances, from a dated data file | `GoogleFlightsClient.Airlines` | `lookup_airline` | `Get-Airline` |

Filters: stops, airlines, alliances, departure and arrival hours, maximum
price, maximum duration, cabin, passengers, sort order. **Basic Economy fares
(such as Delta Main Basic) are left out by default** in the MCP tools and the
PowerShell commands (owner's decision, 2026-11-22): `exclude_basic_economy`
false, or `-ExcludeBasicEconomy:$false`, includes them. Currency and region per request (default USD and US). Every result says
when Google answered (`RetrievedAt`) and whether it came from the cache
(`FromCache`); every call can skip the cache (`verify` / `-Verify`).

**Not available:** booking options, price calendars and multi-city trips
(Google provides them only through its page's own script; above), emissions
data (deliberately not exposed), and anything that books.

The tools, the commands' help and the client's docs share one set of
descriptions (`src/GoogleFlights/GoogleFlightsDocs.cs`).

## Use it

**From an AI client:** Multiplex serves the tools at `/google-flights/mcp`,
behind Multiplex's sign-in like every service (#80). Google Flights acts for no
one's site account, so, like Time, connecting it is a consent page after the
Microsoft sign-in: add `https://<your Multiplex host>/google-flights/mcp` as a
connector in Claude or ChatGPT, as for TripIt and LinkedIn. On the machine
Multiplex runs on, it needs no token (for example a command-line client on
that machine pointing at `http://localhost:5190/google-flights/mcp`).

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
// Booking options are not available: this fails with a GoogleFlightsUnavailableException whose
// GoogleFlightsUrl is the booking page for the person to open.
```

## Limits and safety

- **Google can refuse this client.** A refusal applies to the whole network
  and has lasted hours (#101). The client paces itself (4 requests back to
  back, then one per 20 seconds), caches answers for 12 minutes, never retries
  a refusal, and after one stops sending anything for 15 minutes (doubling
  with each refusal in a row, up to 6 hours). A search page is about 2-3 MB.
  See `NOTES.md`.
- **Identity.** Google serves its search pages only to browser User-Agents,
  so the client sends Chrome's User-Agent form followed by its own name and
  link: `... Chrome/... Safari/537.36 (compatible;
  Multiplex-GoogleFlights/<version>; +<url>)` (owner's decision, 2026-11-22).
  It sends no other browser headers and no page tokens; the only cookie it
  sends back is the `NID` Google itself sets, as any HTTP client would.
- **Not affiliated with Google.** These endpoints are undocumented and may
  change without notice; the contract-drift live test is how a change is
  noticed.

## Tests

| Kind | Command | What it needs |
|---|---|---|
| Unit (encoding, parsing, cache, pacing, refusals) | `dotnet test services/GoogleFlights/tests/GoogleFlights.Tests` | Nothing: fixtures are the committed references |
| MCP tools over the real pipeline | `dotnet test tests/Multiplex.Tests --filter GoogleFlights` | Nothing |
| PowerShell module, including the help-drift check | `Invoke-Pester services/GoogleFlights/tests/GoogleFlights.PowerShell.Tests` | A build first |
| Live: the client, every MCP tool, the module | the same commands with `MULTIPLEX_LIVE_TESTS=1` | Internet access. About 14 paced requests in all; never run them in a loop |

`tests/Invoke-LiveTests.ps1` runs all of the live tests in one go on the
machine's own connection: build, offline tests, one search first, then the
rest, stopping at the first failure or refusal. Run it on a connection you can
spare: a refusal can stop the client there for hours.

`tests/Measure-Allowance.ps1` finds what makes Google refuse the client
(#101); since 2026-11-22 it measures the search pages the client fetches,
with files of their own (the data-endpoint run of 2026-11-20 is kept as it
was). It measures how close together two searches may be, how many may go
back to back, and what pace may follow a burst. Each experiment starts easy,
gets harder until the first refusal, then narrows in. It also times every
refusal. It runs for hours or days and continues where it stopped when run
again. Its state, log and report are kept per network in the capture store,
and the report is posted to #101 after each experiment. It is built to get
refused, so run it only on a connection you can spare.

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
| `references/` | Trimmed, scrubbed HAR extracts, their catalogue, and the re-capture recipe; `references/pages/`, the flight data of search pages the client fetched (evidence and test fixtures, not the spec) |
| `src/GoogleFlights/` | The client library (`Multiplex.Sites.GoogleFlights`); `Wire/` is Google's format, nothing else is |
| `src/GoogleFlights.PowerShell/` | The PowerShell module `GoogleFlights` |
| `tests/` | Unit and live tests, and the module's Pester tests |

The MCP tools themselves are in `src/Multiplex.Web/Services/GoogleFlights/`
until the host-contracts library of #83 lets a provider's host piece live here.
