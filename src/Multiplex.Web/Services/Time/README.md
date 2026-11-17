# Time Service

**Address:** `/time/mcp` · **Tool:** `get_local_time` · **Source site:** [time.now](https://time.now/)

The Time service is the MCP server that Multiplex hosts for time.now. It looks up the current local time and UTC offset for a city or country. It does so the way a person would: it searches time.now in Multiplex's shared headless browser, opens the matching clock page and reads it.

This folder holds everything specific to the Time service. For what Multiplex is and the list of services, see the [product overview](../../../../spec/product.md). For setup, the shared browser settings and running the tests, see the [root README](../../../../README.md).

## `get_local_time`

- **Input:** `location`, a city or country (for example, "Tokyo" or "Japan").
- **Output:**

  | Field | Meaning |
  |---|---|
  | `location` | The place time.now matched, as the page names it |
  | `time` | Local time, 24-hour (`hh:mm:ss`) |
  | `timeZone` | The zone abbreviation the page shows (for example, `JST`) |
  | `date` | Local date, such as `Monday, November 16, 2026` |
  | `utcOffset` | The current offset, such as `UTC+09:00` or `UTC-05:00` |
  | `source` | The time.now page it read |

  For example:

  ```json
  { "location": "Japan", "time": "08:11:27", "timeZone": "JST",
    "date": "Monday, November 16, 2026", "utcOffset": "UTC+09:00",
    "source": "https://time.now/countries/japan/" }
  ```

## Behavior worth knowing

- **Matching.** time.now's search results mix cities, countries, time zones and converter tools, and only cities and countries count. An exact name match (ignoring case and punctuation, including flag emoji) wins. Otherwise, the first city or country listed is used. In other words, a partial or ambiguous name ("Japa") returns a best guess rather than an error.
- **No match.** When the results list no city or country at all, the call returns a tool error: `time.now has no city or country matching '<location>'.`
- **The offset is calculated, not read.** time.now's static UTC links show the *standard* offset even while daylight saving time is active (New Zealand in November, for example). Instead, the offset is the difference between the page's live clock and UTC, rounded to 15 minutes (which absorbs the few seconds between reading the page and doing the math). As such, it's only as accurate as the clock of the machine hosting Multiplex.
- **Stale clock.** The clock that time.now renders on its server can be out of date, so the tool waits for the page's first live tick before reading it.

## How a call works

1. Rent a tab from the shared browser and set a desktop width (at the default width, time.now collapses its menu and hides the search box).
2. Open time.now, type the location into the search box and press Enter.
3. Pick the result (see Matching above) and open its clock page.
4. Wait for the live clock tick, then read the location, time, zone and date.
5. Calculate the UTC offset and return the tab to the shared browser.

When `Browser:StepDelayMs` is set, the tool pauses between steps so a visible run can be followed by eye.

## Files

| File | What it holds |
|---|---|
| `TimeNowTool.cs` | The `get_local_time` tool and the steps above |
| `TimeNowPage.cs` | Everything the tool knows about time.now's pages (selectors, result matching, reading the clock) |
| `UtcOffset.cs` | The offset calculation |

The tests and saved time.now pages live in `tests/Multiplex.Tests/Services/Time/`.

## Risks

- **time.now's page layout.** A redesign breaks the tool until `TimeNowPage.cs` is updated. Only the live tests detect that; the offline tests run against saved copies of the old pages.
- **time.now's availability.** If the site is down or slow, so is the tool.
- **The host machine's clock.** A wrong system clock produces a wrong `utcOffset` (see above).

## Tests

| Tests | Needs | What they cover |
|---|---|---|
| Offset math | nothing | Daylight saving time, half-hour and 45-minute zones, both sides of the date line, and rejecting a 12-hour clock or a date without its weekday |
| Page parsing | Chromium | Matching and reading against saved, trimmed copies of time.now pages (in `Fixtures/`), offline |
| Live, end to end | Chromium and `MULTIPLEX_LIVE_TESTS=1` | A real `get_local_time` call over HTTP against the real time.now for Tokyo, Japan, New Zealand (daylight saving time), India (half-hour zone) and Peru (negative offset), plus a place that doesn't exist |

The Chromium tests look for it at `MULTIPLEX_CHROMIUM`, else `/usr/bin/chromium`, and skip when neither exists. The live tests take their expected values from .NET's own time zone database, so they don't depend on time.now being right.

To run only the Time service's tests:

```bash
dotnet test --filter "FullyQualifiedName~Multiplex.Tests.Services.Time"
```

When time.now changes its layout, update `TimeNowPage.cs`, then refresh the saved pages in `Fixtures/` so the offline tests match the new layout.
