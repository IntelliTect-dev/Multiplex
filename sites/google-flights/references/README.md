# Google Flights -- HAR references

Trimmed, scrubbed extracts of Google Flights' own traffic. They are
**evidence, not the spec**: `../docs/protocol.md` is the spec, and each
position there names the reference that proves it. They are also the source
of the client's test fixtures and the baseline to diff against when Google
changes something.

`catalogue.json` is the source of truth for the table below; the table is
generated from it. Edit the JSON, then run `render-har-catalogue.js --dir .`.

<!-- BEGIN GENERATED CATALOGUE -- edit catalogue.json, not this table -->

| File | Operation | Entries | Request bodies | Captured | Related |
|---|---|---|---|---|---|
| `google-flights/google-flights-airport-autocomplete-2026-11-19.har` | Typed 'Seattle' into Where from? and 'Denv' into Where to?, then chose SEA and DEN. H028ib is called once per keystroke (12 kept); origin calls send kinds [1,2,3,5], destination calls add 4. Choosing an airport calls tDoGIe for its details (name, city id, coordinates, country) and BVAT3 once for the route pair (four booleans, meaning pending). The page's first H028ib call, made with the origin Google pre-fills from the network's location, is left out on purpose: it carries the operator's approximate location. | 15 | 15 | 2026-11-19 | #89 #70 |
| `google-flights/google-flights-calendar-grid-and-graph-2026-11-19.har` | On SEA-DEN 2026-12-15/22 round-trip results: the Date grid button (GetCalendarGrid, departures 2026-12-12..18 by returns 2026-12-19..25) and the Price graph button (GetCalendarGraph, 2026-12-08..2027-01-31 with 7-day trips). Both answered; neither request carries a selection or a session id. | 2 | 2 | 2026-11-20 | #89 |
| `google-flights/google-flights-date-picker-prices-2026-11-19.har` | Opened the Departure date picker for a SEA-DEN round trip: GetCalendarPicker asked for 2026-11-19..2026-12-31 with a 7-day trip and answered 43 priced cells. Then chose Tue 2027-01-12: the page asked twice for return-date prices 2027-01-12..31 and BOTH answered HTTP 200 carrying gRPC code 13 and a travel.frontend.flights.ErrorResponse -- the failure path, recorded on purpose. | 3 | 3 | 2026-11-19 | #89 |
| `google-flights/google-flights-direct-search-refused-2026-11-19.har` | One direct HTTP GetShoppingResults (not a browser), one-way SEA-DEN 2026-12-15, sent with an honest User-Agent, no cookies, no browser headers and no anti-bot token, 60 minutes after the browser refusals. Answered HTTP 200 with a BARE gRPC 13 item ([13], no ErrorResponse detail) in about 200 ms, and set an NID cookie. The browser refusals carried an ErrorResponse with a search session id; this one did not. Redirects were not followed (there was none). | 1 | 1 | 2026-11-19 | #89 |
| `google-flights/google-flights-filter-change-refused-2026-11-19.har` | One-way SEA-DEN 2026-12-15 search page loaded and answered (not kept), then Stops: Nonstop only chosen on the results page. The page sent selection [] with leg index 3 = 1 and was refused with gRPC 13. It still wrote the filter into the URL (tfs leg field 5 = 0), which is how the nonstop URL in filter-requests-held was obtained. | 1 | 1 | 2026-11-20 | #89 |
| `google-flights/google-flights-filter-requests-held-2026-11-19.har` | Entry 1: page load of the nonstop-only one-way URL, answered -- stops proven both ways. Entries 2-23 (status 0): each filter applied once on the results page with the page's own requests HELD in the capture browser and never sent to Google: SkyTeam alliance, clearing it, airlines DL then DL+AS, departure and arrival time windows, max price, max duration, economy excluding Basic, premium economy, business, first, passengers 2/2/1/1 then 2/2/0/1. Each shows exactly what one filter puts in the request; the tfs the page wrote at the same moment is in the referer. Each held request made the page send Google a jserror report (not kept). | 23 | 23 | 2026-11-20 | #89 |
| `google-flights/google-flights-multi-city-to-booking-2026-11-19.har` | Page load of an encoder-built multi-city URL: SEA-DEN 2026-12-15, then DEN-LAX 2026-12-19, economy, 1 adult. Entry 1: leg 1 results (session id). Entry 2: Delta DL2759 chosen on leg 1, answered with leg 2 results (token from entry 1). Entry 3: Delta DL2148 chosen on leg 2, GetBookingResults answered with Delta fare brands for the whole trip. All three answered. The booking token's flight key reads DL2148#2 at 261.80 USD and is not found literally in entry 2's answer (which offered DL2148 at 263.00); how the page forms it is pending. | 3 | 3 | 2026-11-20 | #89 |
| `google-flights/google-flights-one-way-search-2026-11-18.har` | Signed out, browsed by hand: one-way SEA-SFO 2026-12-15. GetShoppingResults streamed 6 payload frames: frames 0, 1 and 5 carry the 3 best and 15 other flights (index 17 only in frame 0), frames 2-4 are status only. The fresh-search selection was [null,null,null,<search session id>]. Source of the itinerary, segment, itinerary-protobuf and selection-token field maps in docs/protocol.md. | 1 | 1 | 2026-11-18 | #89 #70 |
| `google-flights/google-flights-one-way-search-from-url-2026-11-19.har` | ONE browser page load of the one-way URL the page produced in one-way-switch-refused (SEA-DEN 2026-12-15), five minutes after that refusal. GetShoppingResults with a page-load session id and the same filter byte for byte, answered with flights. | 1 | 1 | 2026-11-20 | #89 |
| `google-flights/google-flights-one-way-switch-refused-2026-11-19.har` | On answered SEA-DEN round-trip results, the trip-type menu switched to one way. The page's own GetShoppingResults went out with selection [] (no session id) and was refused: HTTP 200, gRPC 13, ErrorResponse. Five minutes later the IDENTICAL filter sent with a page-load session id was answered (one-way-search-from-url). The pair is the evidence that searches without a session id are refused while the network is not blocked. | 1 | 1 | 2026-11-20 | #89 |
| `google-flights/google-flights-round-trip-search-2026-11-19.har` | ONE browser page load of a round-trip search URL (SEA-DEN 2026-12-15/22, tfs built by the encoder), five minutes after direct probe P2 was refused. The search page's GetShoppingResults, selection [null,null,null,<page-load session id>], answered with 3 best and 10 other flights in 5 frames: the network was not blocked, only plain requests were. Its answer also issued the selection token used by the return step in round-trip-to-booking. | 1 | 1 | 2026-11-20 | #89 |
| `google-flights/google-flights-round-trip-search-refused-2026-11-19.har` | Searched SEA-DEN round trip 2027-01-12/19 from the home page form: GetShoppingResults answered HTTP 200 carrying gRPC code 13 and a travel.frontend.flights.ErrorResponse, with no flights. The request is the page's own fresh-search filter (selection [], sort 0), so this pins how a refused search looks on the wire, not why it was refused. | 1 | 1 | 2026-11-19 | #89 |
| `google-flights/google-flights-round-trip-to-booking-2026-11-19.har` | Round trip SEA-DEN 2026-12-15/22. Entry 1: a search page's GetShoppingResults (session id), answered. Entry 2: choosing the Delta outbound DL2759 -- the return-flight step, selection [null,<selection token>] with leg 0 index 8 = the chosen flight; it was made in the round-trip-search tab, so its token comes from THAT reference's answer. Entry 3: choosing the Delta return DL1731 -- GetBookingResults with both legs chosen (token from entry 2's answer), answered with seven Delta fare brands from Main Basic to First Classic. All three answered. | 3 | 3 | 2026-11-20 | #89 |
| `google-flights/google-flights-sort-and-multi-city-held-2026-11-19.har` | All 10 requests were HELD in the capture browser and never sent to Google (status 0). Nine had been answered locally with an earlier Google response so the page kept its menus; those local answers are replaced here by an explicit placeholder. In order: sort by price (twice), departure time, arrival time, duration, emissions, top flights; then the trip type switched to multi-city, a second leg added, and its destination changed to LAX. The page load before them used a tfs BUILT BY THE ENCODER with every filter stacked; Google's server-rendered page answered it with the filtered flights (the 2 MB page itself is not kept). | 10 | 10 | 2026-11-20 | #89 |

<!-- END GENERATED CATALOGUE -->

## Naming

`google-flights/google-flights-<action>-<yyyy-MM-dd>.har`, where `<action>`
names what a person did on the page and the date is the capture date.

## Provenance

| Capture (`.har-captures/www.google.com/`, gitignored) | How it was made |
|---|---|
| `2026-11-18-124419` | Signed out, throwaway profile, browsed by hand. |
| `2026-11-19-133031` | Issue #89. Signed out, dedicated `google-flights` capture profile, native Chrome, driven one paced step at a time over CDP. |
| `2026-11-19-163932` .. `2026-11-19-171757` | Issue #89, same profile and browser. Each search starts with a page load of a search URL; later recordings attach to the running browser (`--attach`). |

Every request was made signed out: no account, cookie or personal identifier
is involved.

**Held requests.** Two references (`filter-requests-held`,
`sort-and-multi-city-held`) record what a filter or menu choice makes the page
send, without sending it: the capture browser held those requests and aborted
them locally (status 0). Where the page needed an answer to keep its menus, it
was given an earlier Google response locally; that local answer is not
Google's, so it is replaced in the reference by a placeholder (status 0, empty
body, a comment saying so). A held request's request side is exactly what the
page would have sent.

Three things were taken out on purpose:

- **The pre-filled origin.** Google pre-fills "Where from?" from the
  network's location, so the home page and the first autocomplete call carry
  the operator's approximate location. Neither is extracted. (A city can still
  appear in a search answer as a connecting airport; that is route data.)
- **Session-scoped values.** The page's anti-bot token
  (`x-goog-batchexecute-bgr`), page session id (`f.sid`) and Chrome's
  field-trial header (`x-client-data`, a weak identifier of the browser install)
  are redacted by the repository's `.har-policy.project.json`.
- **The search pages themselves.** A search URL's HTML (about 2 MB) already
  carries the results; it is described in `../docs/protocol.md` but not kept. `extract-har-reference.js` does not
  apply that policy yet (it scrubs a copy staged outside the repository), so
  each reference is scrubbed once more in place with `sanitize-har.js` after
  extraction, and gated again.

## Re-capture

Captures are made at a human pace on a strict request budget: Google
soft-blocks a client that sends too many requests. Read `../NOTES.md` before
recording.

Start every search with a **page load of a search URL**: searches started from
the page's form or menus were refused (`../NOTES.md`). Build the `tfs` from
`../docs/protocol.md` section 7, and leave at least 30 seconds between
searches. Use dates a few weeks out; a capture older than the dates in it can
no longer be re-recorded as is.

| Reference | Load | Then |
|---|---|---|
| `round-trip-search` | round trip SEA-DEN | nothing |
| `round-trip-to-booking` | round trip SEA-DEN | choose an outbound, then a return on the same airline |
| `one-way-search-from-url` | one way SEA-DEN | nothing |
| `multi-city-to-booking` | multi-city SEA-DEN, DEN-LAX | choose a flight on each leg, same airline |
| `calendar-grid-and-graph` | round trip SEA-DEN | Date grid; close it; Price graph |
| `filter-requests-held` | one way SEA-DEN, nonstop | with the page's requests held: each filter once |
| `sort-and-multi-city-held` | one way SEA-DEN, filters stacked | with the page's requests held and answered locally: each sort order; then Multi-city, Add flight, destination LAX |
| `one-way-switch-refused`, `filter-change-refused`, `round-trip-search-refused`, `date-picker-prices` | | refusals: recorded when they happened, never provoked |
| `airport-autocomplete` | the home page | type an origin and a destination; extract **without** the first autocomplete call (the pre-filled origin) |
| `direct-search-refused` | | one direct request, not a browser |

Holding the page's requests makes it report errors to Google (`../NOTES.md`);
answer them locally instead of failing them where the page allows it.
