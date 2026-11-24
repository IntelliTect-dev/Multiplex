# Google Flights web protocol

What Google Flights' own page sends and receives, decoded from captures. This
document is the spec for the client's encoding and parsing layer; the HAR
references under `../references/` are the evidence.

Every position names the capture that proves it. A position marked
**pending** has not been seen changing in a capture yet and must not be
relied on.

## Evidence

| Key | Capture (`.har-captures/www.google.com/...`, gitignored) | What it holds |
|---|---|---|
| **A** | `2026-11-18-124419` | Signed out, browsed by hand: one one-way search SEA-SFO 2026-12-15. One successful `GetShoppingResults`. |
| **B** | `2026-11-19-133031` | Issue #89, signed out, dedicated profile: airport autocomplete, airport details, route flags, the date picker (1 answered, 2 answered code 13), one round-trip search SEA-DEN 2027-01-12/19 (answered code 13). |
| **C** | `2026-11-19-163932` | Diagnostic: ONE browser page load of a round-trip search URL (SEA-DEN 2026-12-15/22), 5 minutes after direct probe P2 was refused. One `GetShoppingResults`, answered with flights. |
| **D** | `2026-11-19-164238` | Round trip SEA-DEN 2026-12-15/22 from a search URL, then the Delta outbound (return-flight step), then the Delta return (booking options). Three data requests, all answered. |
| **E** | `2026-11-19-164718` | The same round-trip search URL (answered), then the trip-type menu switched to one way: the page's own search request, with selection `[]`, was refused with code 13. Recording stopped there. |
| **F** | `2026-11-19-165319` | Diagnostic: ONE browser page load of the one-way URL the page produced in E, five minutes after E's refusal. Answered with flights. |
| **G** | `2026-11-19-165541` | One-way search page (answered), then Stops: Nonstop only on the results page: the page's request, selection `[]`, refused. Reference `filter-change-refused`. |
| **H** | `2026-11-19-170149` | Page load of G's nonstop URL (answered), then every filter applied once with the page's requests **held** in the browser (never sent). Reference `filter-requests-held`. |
| **I** | `2026-11-19-171002` | Page load of an **encoder-built** URL with every filter stacked (Google's HTML carried the filtered flights), then the sort orders and the switch to multi-city, held. Reference `sort-and-multi-city-held`. |
| **J** | `2026-11-19-171505` | Encoder-built multi-city URL SEA-DEN / DEN-LAX: leg 1, leg 2, booking. All answered. Reference `multi-city-to-booking`. |
| **K** | `2026-11-19-171757` | Round-trip results, then Date grid and Price graph. Both answered. Reference `calendar-grid-and-graph`. |
| **P1, P2** | `2026-11-19-143242-direct`, `2026-11-19-163416-direct` | One direct HTTP `GetShoppingResults` each (not a browser), two hours apart. Both refused. |
| **P3-P7** | `2026-11-20-090700-direct` | Direct HTTP, a day later: the search again (refused), `flights.google.com` (301; 405), a search page `GET` ("unsupported"). Reference `direct-http-probes`. |
| **P8, P9** | `2026-11-20-095237-direct` | Direct HTTP with the honest-bot User-Agent: the search **answered**; the page `GET` still "unsupported". Reference `direct-honest-bot-user-agent`. |
| **F1-F4, C1** | `2026-11-20-095614-direct` | Direct HTTP with the honest-bot User-Agent: round-trip search, return-flight step, booking options, price graph, all answered; then a control with the old User-Agent, refused. Reference `direct-http-flow`. |
| **P10** | `2026-11-20-124901-direct` | Direct HTTP airport autocomplete (batchexecute `H028ib`) with the honest-bot User-Agent and no `f.sid`/`bl`: answered. Reference `direct-airport-autocomplete`. |
| **L1** | `2026-11-20-130758-live` | The C# client's first live request, a one-way search: answered. Reference `direct-client-search`. |
| **L2** | `2026-11-20-131931-live` | The C# client's one-way price graph (no trip length): refused, bare gRPC 13. Reference `direct-one-way-graph-refused`. |
| **L3** | `2026-11-20-133637-live` | L1's search again, after the client's 15-minute cool-down: refused, bare gRPC 13. Reference `direct-client-search-refused`. |
| **G1, G2** | another machine, on in-flight wifi (not kept) | The C# client's live script: the one-way search answered (14:08:30 local), then the same search 6 s later refused, bare gRPC 13. |
| **AB** | `2026-11-20-142549-ab` | From L1-L3's network, the same search three ways (Node `fetch` as the morning; Node with the C# client's headers; the morning's exact User-Agent): all refused. Reference `direct-network-refusal-ab`. |
| **Q1** | `2026-11-20-223406` | Diagnostic, second network, in the capture browser: ONE page load of the one-way SEA-DEN 2026-12-15 search URL. The page's own `GetShoppingResults` was answered (3 best, 13 other) and matches F's request field for field: f.req, the jspb header and the URL parameter names; only `bl` moved (`_20261118.02_p0` to `_p1`) and the page's own tokens differ. Not committed. |
| **Q2** | `2026-11-20-223639-direct` | P8 resent byte for byte (Node `fetch`), two minutes after Q1, same network: refused, bare gRPC 13, 264 ms. |
| **Q3** | the Claude desktop app's built-in browser, 2026-11-21 05:05 local (not kept) | P8's URL, body and three protocol headers sent with `fetch` from a script-free google.com page in a real, signed-out Chromium, carrying its own User-Agent, client hints and Google cookies, between two answered page searches: refused, bare gRPC 13. Settles that the data endpoint wants the page's per-request values, not a browser identity or this project's name. |
| **H1** | `2026-11-22-121104-page-direct` | The branch's client (search page over direct HTTPS, Chrome-form User-Agent naming the project): the one-way SEA-DEN 2026-12-15 search page, HTTP 200, 2.8 MB, `ds:1` carrying 4 best and 10 other flights. Fixture `pages/google-flights-search-page-data-one-way-2026-11-22.html`. |
| **H2** | `2026-11-22-124003-probe-return` | The same, the round-trip search page with Delta DL2759 pinned on the outbound leg (`tfs` leg field 4): HTTP 200, `ds:1` carrying 3 best and 14 other DEN-SEA returns for 2026-12-22, priced as round trips. Fixture `pages/google-flights-search-page-data-return-leg-2026-11-22.html`. |
| **H3** | `2026-11-22-124105-probe-booking` | The same, the booking page with DL2759 out and DL1731 back pinned: HTTP 200, 1.6 MB, data blocks `ds:0`-`ds:3` only, none with sellers, fares or prices. The page loads them through its own script (`GetBookingResults`). Not committed. |
| **H4** | `2026-11-22-124703-probe-multicity` | The same, a multi-city search page (SEA-DEN 2026-12-15, DEN-LAX 2026-12-20): HTTP 200, `ds:1` present but with no flights at indices 2 and 3. The page loads them through its own script. Not committed. |
| **H5** | `2026-11-22-124404-probe-autocomplete` | The branch's client's airport lookup (batchexecute `H028ib`, "Denv"), Chrome-form User-Agent: answered, 5 places, Denver first. Not committed. |
| **H6** | `2026-11-22-132228-probe-sort` | The same one-way search page as H1 with `tfu=EgYIAhAAGAA` (sort by price): HTTP 200, `ds:1` carrying no best flights and 14 others in price order (ties by departure), the one-stop option last. The server-rendered page honours the sort. Its fixture was replaced by H8's in #134. |
| **H7** | `curl`, 2026-11-24 06:08 UTC (not kept beyond the fixture) | H1's one-way search page fetched twice, 15 s apart, with the client's User-Agent: without `tfu`, HTTP 200, 2.8 MB, `ds:1` carrying 4 best and 10 other flights, as H1; with `tfu=EgYIABABGAA` (`{2: {1: 0, 2: 1, 3: 0}}`, what the page's URL carried after **View more flights** was clicked in a browser that morning), HTTP 200, 7.9 MB, `ds:1` carrying the same 4 best and **94** other flights. A page without field 2.2 carries only its first flights. Fixture `pages/google-flights-search-page-data-one-way-full-2026-11-24.html`. |
| **H8** | `curl`, 2026-11-24 13:14 UTC | The same with `tfu=EgYIAhABGAA` (sort by price and the full list): HTTP 200, 7.9 MB, `ds:1` carrying no best flights and all **98** in price order. The sort and the full list combine. Fixture `pages/google-flights-search-page-data-one-way-full-by-price-2026-11-24.html`. |
| **H9** | `curl`, 2026-11-24 13:14-13:15 UTC | H2's return-leg page (Delta DL2759 pinned): with `tfu=EgYIABABGAA`, HTTP 200, 2.4 MB, 4 best and **17** other returns; without it, about 30 s later, 2.3 MB, 4 best and 13 other. Fixture `pages/google-flights-search-page-data-return-leg-full-2026-11-24.html`. |
| **Q4** | `2026-11-22-125002-direct-p8` | P8 resent once more: refused, gRPC 13 now WITH the `ErrorResponse` detail and a search session id. Not committed. |
| **X** | GoogleFlightsEx `2026-11-06-211138` | The owner's earlier project's capture. **Cross-check only**: nothing here rests on X alone without saying so, and nothing from it is committed. |

## 1. Endpoints

All on `https://www.google.com`. No sign-in is involved anywhere.

| RPC | Transport | What the page uses it for | Evidence |
|---|---|---|---|
| `GetShoppingResults` | service | Flight options for one leg, given any earlier legs already chosen | A, B, C, D, X |
| `GetBookingResults` | service | Sellers, fare brands and booking links for a fully chosen trip | D, X |
| `GetCalendarPicker` | service | Prices shown in the date picker | B, X |
| `GetCalendarGraph` | service | Price per departure date (fixed trip length for a round trip) | X |
| `GetCalendarGrid` | service | Departure x return price matrix | X |
| `H028ib` | batchexecute | Airport/city autocomplete, one call per keystroke | B, P10, X |
| `tDoGIe` | batchexecute | Details of a chosen place: name, city, coordinates, country | B, X |
| `BVAT3` | batchexecute | Four booleans for a route (meaning **pending**) | B, X |
| `j0jL5`, `kecaDd`, `Xn8yoc` | batchexecute | Empty requests, empty answers; not needed | X |

## 2. Request transport

### Service RPCs

```
POST /_/FlightsFrontendUi/data/travel.frontend.flights.FlightsFrontendService/<Rpc>
     ?f.sid=<page session>&bl=<build label>&hl=en-US&soc-app=162&soc-platform=1&soc-device=1&_reqid=<counter>&rt=c
content-type: application/x-www-form-urlencoded;charset=utf-8
x-same-domain: 1
x-goog-ext-259736195-jspb: ["en-US","US","USD",2,null,[480],null,null,7,[]]
x-goog-batchexecute-bgr: <the page's own anti-bot token>

f.req=<urlencoded JSON>&
```

`f.req` is `[null, "<inner request as a JSON string>"]` (B #86, #115).

### batchexecute RPCs

```
POST /_/FlightsFrontendUi/data/batchexecute
     ?rpcids=<rpc id>&source-path=%2Ftravel%2Fflights&f.sid=..&bl=..&hl=en-US&soc-app=162&soc-platform=1&soc-device=1&_reqid=..&rt=c
```

`f.req` is `[[["<rpc id>", "<inner request as a JSON string>", null, "generic"]]]` (B #64, #81).

### Headers and cookies

| Item | Meaning | Direct client |
|---|---|---|
| `User-Agent` | | **Since 2026-11-22: Chrome's form followed by this project's name**, `Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/<v> Safari/537.36 (compatible; Multiplex-GoogleFlights/<version>; +<url>)` (owner's decision, 2026-11-22): Google serves its search pages only to a browser User-Agent and redirects anything else to `/travel/flights/unsupported` (P5, P9, H1). From 2026-11-20 to 11-22 the honest-bot form `Mozilla/5.0 (compatible; ...)` alone (owner's decision, 2026-11-20); a User-Agent without the `Mozilla/5.0 (compatible; ...)` form was refused on the data endpoint (below) |
| `x-goog-ext-259736195-jspb` | `[language, region, currency, 2, null, [UTC offset minutes], null, null, 7, []]`. **This is where currency and region are chosen per request**; the payload carries neither. | sent |
| `x-same-domain: 1` | Google's CSRF guard for these endpoints | sent |
| `content-type: application/x-www-form-urlencoded;charset=UTF-8` | | sent |
| `accept: */*` | | sent by the C# client (L1). The discovery probes ran on Node's `fetch`, which also adds `accept-language: *` and `sec-fetch-mode: cors` on its own; the C# client sends neither and is answered |
| `x-goog-batchexecute-bgr` | Ideally computed by the page's own script on every request. | **not sent; required since the evening of 2026-11-20.** From about 09:52 to 18:13 local that day, searches without it were answered (P8, F1-F4, L1, G1, M1). Since then every search without it has been refused with a bare gRPC 13, on every network tried: P8 resent byte for byte (22:36), and the same request from a signed-out real browser with its own cookies (11-21 05:05), while the page's own requests, which carry it, were answered ([#101 diagnosis](https://github.com/IntelliTect-dev/Multiplex/issues/101#issuecomment-7058758064), [browser test](https://github.com/IntelliTect-dev/Multiplex/issues/101#issuecomment-7063037192)) |
| Cookies `NID`, `OTZ`, `SEARCH_SAMESITE` | Set by Google on page loads (B, C) | none needed; `NID`, which Google sets on the first answer, is sent back like any HTTP client would |
| `f.sid`, `bl`, `_reqid` | Page session, build label, request counter | not sent, not needed |
| `x-browser-validation`, `x-client-data`, `sec-ch-ua-*` | Chrome's own client headers (C) | not sent |

No `at=` token is sent: the page is signed out.

### Direct HTTP: the User-Agent's form is the gate

| When (local) | Client | Sent | Answer |
|---|---|---|---|
| 11-19 14:32 (P1) | direct HTTP | honest non-browser User-Agent, `x-same-domain`, the jspb header; no cookies, no token, no `f.sid`/`bl`; selection `[]` | bare `[13]` |
| 11-19 16:34 (P2) | direct HTTP | the same | bare `[13]` |
| 11-19 16:39 (C) | the page, in Chrome | everything in the table above | **flights** (3 best, 10 other) |
| 11-19 16:42-16:44 (D) | the page, in Chrome | the same | flights, return flights, booking options |
| 11-20 09:07 (P3) | direct HTTP | as P1, 24 hours later | bare `[13]` |
| 11-20 09:08 (P4) | direct HTTP | `GET https://flights.google.com/` | **301** to `https://www.google.com/travel/flights` |
| 11-20 09:09 (P5) | direct HTTP | `GET /travel/flights/search?tfs=...` (C's round trip), honest User-Agent | **302** to `/travel/flights/unsupported?...&ucpp=<the original URL, base64>`, empty body; sets `NID` |
| 11-20 09:12 (P7) | direct HTTP | P1's search, sent to `flights.google.com` | **405**: no data endpoint on that host |
| 11-20 09:52 (P8) | direct HTTP | P1's search, User-Agent `Mozilla/5.0 (compatible; Multiplex-GoogleFlights/0.1; +<issue url>)` | **flights** (3 best, 11 other) |
| 11-20 09:53 (P9) | direct HTTP | P5's page `GET`, the same User-Agent | still **302** to `/travel/flights/unsupported` |
| 11-20 09:56-10:01 (F1-F4) | direct HTTP | P8's User-Agent: round-trip search, return-flight step, booking options, price graph | all answered: 13 flights, 17 returns, 7 fare brands, 11 graph cells |
| 11-20 10:01 (C1) | direct HTTP | F1 again with P1's User-Agent | bare `[13]` |
| 11-20 12:49 (P10) | direct HTTP | airport autocomplete (batchexecute `H028ib`), P8's User-Agent, no `f.sid`, `bl` or `_reqid` | **places and airports** |
| 11-20 13:07 (L1) | the C# client | a one-way search; .NET sends only the four headers above and `Accept: */*` | **flights** |
| 11-20 13:19 (L2) | the C# client | a one-way `GetCalendarGraph` with no trip length | bare `[13]` (section 9) |
| 11-20 13:36 (L3) | the C# client | L1's search, unchanged, 17 minutes after L2 | bare `[13]`: **refused** |
| 11-20 14:08 (G1, G2) | the C# client, another network (in-flight wifi) | the same search twice, 6 s apart | first **answered**, second bare `[13]` |
| 11-20 14:16-14:25 (AB) | Node `fetch` as the morning; Node with the C# client's headers; Node `fetch` with the morning's User-Agent | the same search, from L1-L3's network | all bare `[13]` |
| 11-20 15:30-17:21 (M2) | the module, one process per search (`tests/Measure-Allowance.ps1` as of `6eceebe`) | the same one-way search every 10 minutes, from L1-L3's network | gRPC 9 once (section 9), then a refusal every time |
| 11-20 15:30-18:13 (M1) | the same, from a third network | the same | **10 of 12 answered**; one gRPC 9 (15:50); one refusal (17:22, just after the machine woke and reconnected), and the next, 10 minutes later, answered |
| 11-20 21:36-22:20 | the C# client and the module, three networks (the first, still at 22:06 after nearly 5 hours with nothing sent; the evening network, on its first search; a phone hotspot that had never sent anything, on its first) | the same search | every one refused: **since about 18:13, nowhere answered** |
| 11-20 22:34 (Q1) | the page, in the capture browser, the evening network | one page load of the one-way search URL | **answered**; its request matches F's field for field |
| 11-20 22:36 (Q2) | direct HTTP | P8 exactly | bare `[13]` |
| 11-21 05:05 (Q3) | a real, signed-out Chromium (`fetch` from a google.com page) | P8's URL, body and protocol headers, with the browser's own User-Agent, client hints and cookies, no page token | bare `[13]`, between two answered page searches |
| 11-22 12:11 (H1) | the client, search page over direct HTTPS | `GET /travel/flights/search?tfs=...`, Chrome-form User-Agent naming the project | **200, 2.8 MB page, 14 flights in `ds:1`** |
| 11-22 12:40 (H2) | the same | the round-trip search page with the outbound pinned | **200, 17 returns in `ds:1`** |
| 11-22 12:41 (H3) | the same | the booking page with both flights pinned | 200, but **no sellers or fares** in the HTML |
| 11-22 12:44 (H5) | the client, batchexecute | the airport lookup, Chrome-form User-Agent | **places and airports** |
| 11-22 12:47 (H4) | the client, search page | a multi-city search page | 200, but **no flights** in `ds:1` |
| 11-22 12:50 (Q4) | direct HTTP | P8 exactly | `[13]` with the `ErrorResponse` detail |
| 11-22 13:22 (H6) | the client, search page | H1's search sorted by price (`tfu`) | **200, 14 flights in price order** |
| 11-24 06:08 (H7) | `curl` with the client's User-Agent, search page | H1's search, then the same with `tfu` field 2.2 = 1 | **200, 14 flights; then 200, 98 flights** |
| 11-24 13:14 (H8, H9) | the same | H1's search by price with the full list; H2's returns with and without it | **200, 98 flights in price order; 21 returns against 17** |

**The refusal follows the network, not the client stack.** On L1-L3's network
the same search was refused from three client stacks and with both User-Agent
strings (AB), including exactly the requests answered there that morning,
while the C# client's identical search was answered from another network
inside the same window (G1). That network then refused the next request too
(G2). **It is not a small count per network:** a third network answered one
search every 10 minutes for nearly three hours (M1), while L1-L3's network,
asked on the same cadence, stayed refused for at least four hours (M2). The
`Mozilla/5.0 (compatible; ...)` form is necessary, not sufficient. What starts
a long refusal (requests close together, as in G1/G2? the refused graph, L2?)
and what ends it are not established (section 8, #101). M1 and M2 logged only
that a search was refused, not whether as a bare `[13]` or as HTTP 403.

**The User-Agent's form decides.** P3 (refused) and P8 (answered) are the same
request 45 minutes apart with different User-Agents, and C1 flips F1 back to a
refusal minutes later. Both User-Agents name this project; only the form
differs. On 2026-11-20, until about 18:13 local, no cookie, browser header, page
token or session id was needed; since then the page's token is (below). The
search **page** is still "unsupported" for that User-Agent (P9); the client
does not need it.

So the client was **direct HTTP** with the honest-bot User-Agent, and no
browser was involved (`../NOTES.md`). Everything a browser sent beyond the
headers marked "sent" above was unnecessary on 2026-11-20 until about 18:13
local; since then the data endpoint wants the page's token, and the client
reads search pages instead (below).

Earlier readings that this replaces: on 11-19 the plain requests looked
blocked and the page looked necessary. The refusals then were the User-Agent
(P1, P2) and, in the automated capture browser, something about that browser
(section 5).
### Since the evening of 2026-11-20: the page's token, and search pages

**The data endpoint wants the page's token again.** From about 18:13 local on
11-20 every `GetShoppingResults` without it was refused, on every network
tried, including P8 byte for byte (Q2, Q4) and the same request from a real
signed-out browser with its own identity and cookies (Q3), while the page's
own requests, which carry `x-goog-batchexecute-bgr`, were answered (Q1, Q3).
Others measured the same in August: the token is computed by the page's
script and bound to the exact request bytes (punitarani/fli#223). The client
does not send it. Google did not enforce it for this client for most of 11-20
(P8 to M1).

**Search pages carry the flights.** A search page's HTML carries its first
answer in the data block `ds:1`, laid out exactly like a `GetShoppingResults`
payload (section 5). Google serves the page over plain HTTPS to a browser
User-Agent (H1) and redirects anything else to `/travel/flights/unsupported`
(P5, P9). So since 2026-11-22 the client fetches search pages, with Chrome's
User-Agent form followed by its own name (the owner's decision):

| What | Over direct HTTPS | Evidence |
|---|---|---|
| A one-way or round-trip search's first leg, in the order asked for | the search page, sort and full list in `tfu`: **yes**, every flight | H1, H6, H7, H8 |
| A round trip's returns | the search page with the outbound pinned in `tfs` (leg field 4), full list in `tfu`: **yes** | H2, H9 |
| Booking options | the booking page carries **none**; the page loads them with `GetBookingResults` | H3 |
| A multi-city trip | the search page carries **no flights**; the page loads them with `GetShoppingResults` | H4 |
| Price calendars | no page carries one; the page loads them with the calendar RPCs | section 5 |
| Airport lookup | batchexecute `H028ib`: **yes**, no token needed | P10, H5 |

What a page carries no flights for is reported as not available, with the
Google Flights link, and nothing is sent: never as "no flights found". A data
endpoint the client knows Google refuses is never asked, because its refusal
would cool the client down for every search.

## 3. Response framing

```
)]}'
<length>
[["wrb.fr", <rpc id | null>, "<payload as a JSON string>", ...], ...]
<length>
[["di", ...], ["af.httprm", ...], ...]
```

- Strip the `)]}'` guard, then read line by line. A frame is a line that
  starts with `[`. **Do not trust the length lines** to slice the body; parse
  each frame line as JSON.
- A data item is `["wrb.fr", id, "<payload JSON string>", ...]`: parse item 2
  a second time.
- An error item is `["wrb.fr", null, null, null, null, [code, null, [["type.googleapis.com/travel.frontend.flights.ErrorResponse", [...]]]]]`
  (B #96, #97, #115). `code` is a gRPC status: 13 = `INTERNAL`. See section 9.

### Streaming: `GetShoppingResults` sends several payloads

A answered with 6 payload frames:

| Frame | Non-null top-level indices | Flights in 2 / 3 |
|---|---|---|
| 0 | 0-3, 5, 7, 11, 12, 14, **17**, 18, 20, 25, 26, 29-31 | 3 / 15 |
| 1 | as 0, without 17 | 3 / 15 |
| 2, 3, 4 | 0, 1, 14, 29-31, 33 | none (status frames) |
| 5 | as 1, plus 32 | 3 / 15 |

The frames are **not** identical snapshots: index 17 arrives only in frame 0.
Take flights from the **last** frame that has index 2 or 3, and read any other
index from the last frame that carries it.

## 4. The search filter

The `filter` array appears unchanged in every service RPC request: inner
index 1 of `GetShoppingResults`, `GetBookingResults` and the three calendar
RPCs.

```
[null, null, tripType, null, [], cabin, passengers, maxPrice, null, null, null, null, null, legs, null, null, null, 1, ..., excludeBasic (28)]
```

| Index | Meaning | Values seen | Evidence |
|---|---|---|---|
| 2 | Trip type | 1 round trip, 2 one way, 3 multi-city | A, C, E, I, J |
| 4 | Always `[]` | | A-K |
| 5 | Cabin | 1 economy, 2 premium economy, 3 business, 4 first | H |
| 6 | Passengers `[adults, children 2-11, infants on lap, infants in seat]` | `[1,0,0,0]`; `[2,2,1,1]` then `[2,2,0,1]` after removing the lap infant | H |
| 7 | Maximum price `[null, amount]`, whole units of the request's currency; absent = no limit | `[null, 990]`, `[null, 400]` | H, I |
| 13 | Legs, one per flight | | A-K |
| 17 | Always 1 | | A-K |
| 28 | Economy **excluding Basic** fares: 1; absent otherwise. Stays set when the cabin changes | 1 | H, I |

Filters other than these are **per leg** (below): in multi-city, a leg keeps its
own stops, airlines, times and duration (I).

### Leg

```
[origins, destinations, times, stops, airlines, null, "yyyy-MM-dd", maxDuration, chosen, null, null, null, null, null, 3]
```

| Index | Meaning | Evidence |
|---|---|---|
| 0, 1 | Origins and destinations: `[[[id, kind], ...]]` (a list of places, wrapped once more). Kind 0 = IATA airport (A-K); 5 = Google place id for a city such as `/m/0d9jr` (X); 4 = a wider region (X, in `BVAT3`) | A-K, X |
| 2 | Time window `[departFrom, departTo, arriveFrom, arriveTo]` in whole hours, `null` when unset. The "to" ends are one less than the slider shows: the full day is `[0, 23]`, a slider set to 20 sends 19 | H |
| 3 | Stops: 0 any, 1 nonstop only. "1 stop or fewer" and "2 stops or fewer" were not captured (2 and 3 expected, **pending**) | A, G, H |
| 4 | Airlines and alliances, `null` when unset: IATA codes `["DL","AS"]`, or an alliance name `["SKYTEAM"]` (the answer's filter options list `ONEWORLD`, `SKYTEAM`, `STAR_ALLIANCE`, C) | H, C |
| 6 | Date, `yyyy-MM-dd` | A-K |
| 7 | Maximum duration `[minutes]`, `null` when unset | H, I |
| 8 | Flights already chosen on this leg: `[[from, "yyyy-MM-dd", to, null, carrier, number], ...]`, one per segment | D, J |
| 14 | 3 on every leg the page built from a URL or the home page; 1 on a leg added with "Add flight" (I). Meaning **pending** | A-K |

The date picker's own requests send a short leg, `[origins, destinations, null, 0]` (B #86).

## 5. Requests by RPC

| RPC | Inner request | Evidence |
|---|---|---|
| `GetShoppingResults` | `[selection, filter, sort, 0, 0, 1]` | A, B, C, D, E, X |
| `GetBookingResults` | `[[null, selectionToken], filter, null, 0]`; every leg carries index 8 (the chosen flights). The last value was 0 in D and J and 1 in X; meaning **pending** | D, J, X |
| `GetCalendarPicker` | `[null, filter, [from, to], null, [tripDays, tripDays]]` before a departure is chosen; `[null, filter, [dep, dep], [from, to]]` after | B |
| `GetCalendarGraph` | `[null, filter, [from, to], null, [minTripDays, maxTripDays]]`; K asked `["2026-12-08","2027-01-31"]` with `[7,7]`. For a one-way filter, `[null, filter, [from, to], null, null]` was refused (L2); the page's own one-way request is **not recorded** | K, F4, L2, X |
| `GetCalendarGrid` | `[null, filter, [depFrom, depTo], [retFrom, retTo]]`; K asked departures `2026-12-12..18` by returns `2026-12-19..25`, three days either side of the search | K, X |
| `H028ib` | `["<text typed>", [1,2,3,5], null, [2], 1]` for an origin; `[..., [1,2,3,5,4], null, [1,1,1], 1]` for a destination | B |
| `tDoGIe` | `[null, [[id, kind]]]` | B |
| `BVAT3` | `[null, [[[[origin]], [[destination]]], ...]]`, one pair per leg | B, X |

`selection` in `GetShoppingResults`:

| Value | When | Evidence |
|---|---|---|
| `[]` | a fresh search: the **direct client's** every first search; in the page, the home-page form, the trip-type menu, a filter change | B, E, G, P8, F1, X |
| `[null, null, null, "<search session id>"]` | the page's first request after a search page loads | A, C, D, E, F |
| `[null, "<selection token>"]` | choosing the next leg after one is chosen | D, J, F2, X |

**The direct client sends `[]`** for a new search and a selection token for
each later step, and both were answered (P8, F1-F3).

**Where the session id comes from.** It is first seen in the HTML of the page
load itself (`GET /travel/flights/search?tfs=...`; in A, `GET /travel/flights`
with a search). That HTML is about 2.4 MB and already carries the first answer
in block `ds:1` (same layout as `GetShoppingResults`, session id at `0.4`).
So loading a search URL makes Google run the search on the server, and the
page's following request only streams the answer for that session.

**What the capture browser showed (11-19).** In the automated Chrome used for
the captures, every search the page started with `[]` was refused (B, E, G)
and every page load was answered. E and F are the same filter five minutes
apart: refused as a menu change, answered as a page load. Over direct HTTP
with the honest-bot User-Agent, `[]` is answered (P8, F1), so that refusal was
about the automated browser, not about `[]`. B's refused date-picker calls
remain unexplained.

`sort` (request index 2), from the page's sort menu (I):

| Value | Sort order |
|---|---|
| 0 | the page's default (top flights) before a sort is chosen (A-H) |
| 1 | Top flights, chosen explicitly |
| 2 | Price |
| 3 | Departure time |
| 4 | Arrival time |
| 5 | Duration |
| 6 | Emissions (offered by the page; the client exposes no emissions data) |

The sort is not part of `tfs`; it travels in `tfu` (section 7).

The calendar RPCs carry no selection and no session id, and both were answered
(K). So the session-id rule above is about searches, and B's refused
date-picker calls remain unexplained.

## 6. Responses

### `GetShoppingResults`

| Index | Meaning | Evidence |
|---|---|---|
| 0 | `[null, [ids], 0, "<event id>", "<search session id>"]` | A |
| 2 | Best flights: `[[itinerary, ...], null, false, false, [1]]` | A |
| 3 | Other flights, same layout | A |
| 5 | Price insight: `[level, [null, lowest], [null, ?], [null, ?], [null, typical low], [null, typical high], ...]`; positions after 1 **pending** | A |
| 7 | Filter options (below) | A |
| 11 | Airline baggage-fee pages `[[code, name, url], ...]` | A |
| 12, 18 | `["<base64 protobuf>", 0]`, describe the search; not needed | A |
| 17 | 43 rows of 7 in A; **pending** (price history candidate) | A |
| 26 | Airline accessibility pages `[[code, name, url], ...]` | A |

Filter options, index 7:

| Index | Meaning | Evidence |
|---|---|---|
| 7.0 | Price range `[[null, min], [null, max]]` | A |
| 7.1 | `[alliances, airlines]`: `[["ONEWORLD","Oneworld"], ["SKYTEAM","SkyTeam"], ["STAR_ALLIANCE","Star Alliance"]]`, `[["AS","Alaska"], ...]` | A |
| 7.2 | `[[[code, city], ...] connecting airports, ?, ?]`; the two numbers **pending** (layover range candidate) | A |
| 7.3 | `[min, max]` total duration in minutes (A: `[131, 1453]` against a 144-minute nonstop) | A |
| 7.5 | The same four booleans `BVAT3` returns | A |

### Itinerary

`itinerary[0]` is the trip summary:

| Index | Meaning | Evidence |
|---|---|---|
| 0 | Carrier code (`multi` when mixed, X) | A |
| 1 | Airline names | A |
| 2 | Segments | A |
| 3, 4, 5 | Origin airport, date `[y, m, d]`, time `[h, m]`; **a zero minute is omitted** (`[11]` is 11:00) | A |
| 6, 7, 8 | Destination airport, date, time | A |
| 9 | Total minutes | A |
| 10 | Arrival day offset: 1 when the trip arrives the day after it departs; `null` on same-day trips | D (F9777+F94203 departs 22:44, arrives the next day) |
| 13 | Layovers: `[[minutes, arrive airport, depart airport, null, arrive airport name, arrive city, depart airport name, depart city], ...]`. The two airports differ when the connection changes airport | D (`[867, "LAS", "LAS", ...]`: the page's "14 hr 27 min layover at Harry Reid International Airport"), X |
| 22 | Emissions. **Not read: the client exposes no emissions data** | A |

`itinerary[6]` (written `true`/`false` by the data endpoint, `1`/`0` by a search page's `ds:1`; live run, 2026-11-23): **on the list for the last leg** (every earlier leg already
chosen), it is the page's **"Separate tickets"** label. In D's return list,
after a Delta outbound, it is `false` on every Delta return and `true` on
every Alaska, United, Southwest and Frontier return, exactly the returns the
page labelled "Separate tickets". J's leg-2 list behaves the same. On other
lists it is not that label: `true` on all 13 itineraries of a round trip's
outbound list (C), where the page shows no label, and `false` on all 13 of a
one-way list (F). Read it only on the last leg's list; its meaning elsewhere
is **pending**.

`itinerary[1]` = `[[null, price], "<selection token>"]`. **The price here is
rounded to the whole currency unit** (A: 89 where the exact fare is 88.40).

`itinerary[8]` = `"[\"<base64 protobuf>\"]"`, the trip's exact data:

| Field | Meaning | Evidence |
|---|---|---|
| 2 | Currency, e.g. `"USD"` | A |
| 3.1 | Exact price in minor units (8840 = 88.40) | A |
| 4.1.1 (repeated) | Segment: 1 from, 2 local departure **with UTC offset** (`2026-12-15T11:00:00-08:00`), 3 to, 4 local arrival with offset, 5/6 marketing carrier and number, 7/8 operating carrier and number, 10 aircraft code | A |
| 4.6.1 | Airline name | A |

The offsets are what make dates across the International Date Line exact: an
arrival "the next day but one" is read from field 4, never computed.

### Segment

`itinerary[0][2][n]`:

| Index | Meaning | Evidence |
|---|---|---|
| 3, 6 | From and to airport codes | A |
| 4, 5 | From and to airport names | A |
| 8, 10 | Departure and arrival time `[h, m]` | A |
| 11 | Minutes | A |
| 12 | Amenity flags (layout **pending**) | A |
| 14 | Legroom, short (`"31 in"`) | A |
| 17 | Aircraft name | A |
| 20, 21 | Departure and arrival date `[y, m, d]` | A |
| 22 | `[carrier, number, null, airline name]` | A |
| 31 | Emissions grams. **Not read** | A |

### Selection token (base64 protobuf)

| Field | Meaning | Evidence |
|---|---|---|
| 1 | The search session id (response index 0.4) | A |
| 2 | Flight key, e.g. `DL1412` | A |
| 3 | `{1: price in minor units, 2: decimals, 3: currency}` | A |

The token ties a choice to the search that produced it. Treat it as opaque
and pass it back unchanged.

### `GetBookingResults`

`1.0` lists booking options, one per seller and fare brand (D: 7 for a Delta
round trip, J: 8 for a Delta multi-city trip; each matched the page's list
and prices):

| Index | Meaning | Evidence |
|---|---|---|
| 1 | Sellers `[[code, name, null, isAirline]]`, e.g. `["DL","Delta",null,true]` | D, J, X |
| 3 | Flights `[[carrier, number], ...]` | D, J, X |
| 5 | `[displayUrl, null, ["https://www.google.com/travel/clk/f", [["u", "<opaque>"]]]]`: Google's booking redirect, an HTML form POST | D, X |
| 7 | `[[null, price], "<token>"]`, price rounded to the whole unit | D, J, X |
| 19 | `"[\"<base64 protobuf>\"]"` | D, X |
| 21 | Fare: `[[carrier, "DELTA MAIN BASIC"], [[feature, level], ...], true, "Delta Main Basic", ...]` | D, J, X |

Fare brands seen: Delta Main Basic / Classic / Extra, Comfort Basic / Classic /
Extra, First Basic / Classic. The fare's `[feature, level]` pairs differ
between brands in a way that suggests level 1 = included, 2 = for a fee, 4 = not
allowed (Main Basic `[4,4]`, `[1,2]` where Main Classic has `[4,1]`, `[1,1]`).
The feature codes are **pending**.

### Calendar RPCs

`GetCalendarPicker`, `GetCalendarGraph` and `GetCalendarGrid` answer alike:
index 1 is a list of cells

```
["yyyy-MM-dd departure", "yyyy-MM-dd return", [[null, price], "<token>"], flag]
```

A date with no price is **absent** (B: the picker returned 43 cells, one per
departure day in its range; K: the grid 7 x 7 = 49 cells, the graph 55, one
per departure day from 2026-12-08 to 2027-01-31, each with its 7-day return).

### `H028ib` (autocomplete)

A list of places, each `[[kind, "Seattle, Washington", "Seattle", "<description>", "/m/0d9jr", ...], [airports]]`
(B). The airport layout inside is **pending** decoding.

### `tDoGIe` (place details)

`1` lists places: `[[id, kind], name, [cityId, cityName, ...], [lat, lng], countryCode, ?, countryName]`
(B #81: `["DEN",0]`, "Denver International Airport", `/m/02cl1` Denver, US).

## 7. The `tfs` URL parameter

The search page keeps its state in `?tfs=` (base64url protobuf) and
`?tfu=`. **Loading `/travel/flights/search?tfs=...`** (or
`/travel/flights/booking?tfs=...`) makes Google run the search for that page
(section 5); in the captures, searches started any other way were refused. So
`tfs` is both the search's identity on Google's side and the link a person can
open to see the same search.

Every field is the filter (section 4) re-encoded:

| Field | Meaning | Filter position | Evidence |
|---|---|---|---|
| 1 | 28 (constant) | | B-K |
| 2 | 1 on the home page, 2 on a search or booking page | | B-K |
| 3 (repeated) | A leg (below) | `legs` | B-K |
| 8 (repeated) | One entry per traveler, listed adults, children, infants in seat, infants on lap: 1 adult, 2 child, **4 infant in seat, 3 infant on lap** (note: not the payload's order) | 6 | H |
| 9 | Cabin, as the filter: 1-4 | 5 | H |
| 12 | Maximum price, whole currency units | 7.1 | H, I |
| 14 | 1 (constant) | | B-K |
| 16 | `{1: -1}` (constant so far; **not** the maximum price). Meaning **pending** | | B-K |
| 19 | Trip type: 1 round trip, 2 one way, 3 multi-city | 2 | B, E, I, J |
| 25 | 1 = economy excluding Basic | 28 | H |

A leg (field 3):

| Field | Meaning | Leg position | Evidence |
|---|---|---|---|
| 2 | Date `"yyyy-MM-dd"` | 6 | B-K |
| 4 (repeated) | A chosen flight `{1: from, 2: date, 3: to, 5: carrier, 6: number}` | 8 | D, J |
| 5 | Most stops allowed: 0 for "Nonstop only"; absent for any number | 3 (1 -> 0) | G, H |
| 6 (repeated) | An airline code or alliance name | 4 | H |
| 8, 9, 10, 11 | Depart from, depart to, arrive from, arrive to (hours) | 2 | H |
| 12 | Maximum duration, minutes | 7 | H |
| 13, 14 | Origin, destination `{1: kind (1 = airport), 2: code}` | 0, 1 | B-K |

`tfu` is a second base64url protobuf. Field 2.1 is the **sort** (the values in
section 5): `EgYIAhAAGAA` = `{2: {1: 2, 2: 0, 3: 0}}` is sort by price (I).
Field 2.2 = 1 is **View more flights**: `EgYIABABGAA` = `{2: {1: 0, 2: 1, 3: 0}}`
is Google's order with every flight. Without it a search page's `ds:1` carries
only its first flights (14 of 98 in H7, 17 of 21 returns in H9); with it, all of
them, sorted as field 2.1 says (H8). The page's own click loads the rest with
`GetShoppingResults`, which wants the page's token (section 2); the page
fetched with the field set needs none. The client always sets it on search
pages. Field 2.3 is 0 in every value seen; its meaning is **pending**. B's home
page had `{5: {1: 3}}`. On a page reached by choosing a flight, `tfu` also
carries a token; it is not needed to load a search.

**Proven both ways.**
- *Encoding matches the page:* an encoder built from these tables reproduces,
  byte for byte, every `tfs` the page produced: the three in B, the one-way
  value in E, all 22 filter states in H, the sort and multi-city states in I,
  and the `tfs` behind every recorded search and booking request in C-H and J
  (the request's referer): 49 values in all.
- *Google accepts encoder-built URLs:* search URLs built by the encoder were
  answered for a round trip (C), a multi-city trip (J), and one way with
  nonstop, two airlines, both time windows, a maximum duration, a maximum
  price and economy excluding Basic stacked (I).

The stop counts 1 and 2 ("1 stop or fewer", "2 stops or fewer") were not
captured; the encoder's mapping for them (payload 2 -> 1, 3 -> 2) is a guess
and **pending**.

## 8. Not yet established

- The stop values for "1 stop or fewer" and "2 stops or fewer" (expected 2
  and 3).
- Leg index 14 (3, or 1 on a leg added with "Add flight"), `tfs` field 16,
  and `GetBookingResults`' last value.
- How the page forms a multi-city booking token: J's names `DL2148#2` at
  261.80 USD, which no recorded answer offered verbatim (the leg-2 answer
  offered `DL2148` at 263.00).
- Why B's date-picker calls were refused: they carry no selection, and K's
  calendar calls without one were answered.
- The Best / Cheapest tabs, which did not appear on a filtered one-way page.
- The page's price-calendar request for a one-way trip (the one form tried was
  refused, L2; with L3 refused too, L2 may have been a refusal of the network
  rather than of the form).
- What starts a long refusal of the honest-bot client, what pace Google
  answers without one, and what ends one (L3, M1, M2).
- Whether Google keeps serving search pages to the Chrome-form User-Agent,
  and at what pace: one day's evidence so far (H1-H5). Each search page is
  about 2-3 MB.
- Whether Google will stop demanding the page's token again, as it did for
  most of 2026-11-20.
- Every response position marked **pending** above.

## 9. Errors and throttling

### The refusal Google actually sends: HTTP 200, gRPC 13

Captured three times in B (references `date-picker-prices` entries 1-2 and
`round-trip-search-refused`). All three are identical in shape:

```
HTTP/1.1 200
content-type: application/json; charset=utf-8      (same headers as a success)

)]}'
<length>
[["wrb.fr",null,null,null,null,[13,null,[["type.googleapis.com/travel.frontend.flights.ErrorResponse",[[null,[[<ids>],null,null,null,null,[[0]]],0,"<event id>","<search session id>"],0]]]]],["di",<ms>],["af.httprm",<ms>,"<id>",<n>]]
<length>
[["e",4,null,null,<bytes>]]
```

How to recognise it:

- **Never by HTTP status.** The status, content type and response headers
  are the same as for a successful answer.
- A `wrb.fr` item whose payload (index 2) is `null` and whose index 5 is
  `[code, null, [[typeUrl, detail]]]`. Code 13 = `INTERNAL`.
- The detail is the same header a successful payload starts with (index 0),
  followed by `0`. **Nothing in it distinguishes a throttle from any other
  refusal**: the three captured refusals (two date-picker calls, one search)
  are byte-for-byte alike apart from ids and timings.

So a client cannot tell from one answer whether it is being throttled. It
treats every code 13 as **stop and back off**, never as **retry now**, and
treats several in a row (the earlier project used 3) as a block.

### The bare refusal: a direct request, gRPC 13 with no detail

Captured once as a reference (`direct-search-refused`, probe P1): a direct
HTTP request, not a browser, 60 minutes after B's refusals. A second identical
probe (P2) two hours later, at 16:34 local, got the same answer byte for byte
apart from timings (about 211 ms round trip). Both probes sent an honest
non-browser User-Agent, no cookies and no anti-bot token.

```
HTTP/1.1 200
content-type: application/json; charset=utf-8
set-cookie: NID=...

)]}'
39
[["wrb.fr",null,null,null,null,[13]]]
55
[["di",41],["af.httprm",41,"<id>",19]]
25
[["e",4,null,null,131]]
```

The same code 13, but **without** the `ErrorResponse` detail and its search
session id: about 41 ms of server time, against 66-111 ms for the browser
refusals. The earlier project recorded this bare shape throughout a block that
lasted more than 6 hours.

| Shape | Seen on | Reading |
|---|---|---|
| `[13, null, [["...ErrorResponse", [[<header>], 0]]]]` | the page's own requests: B (search, date picker), E, G (searches with selection `[]`) | the request reached the flights service, which refused it |
| `[13]` | direct requests whose User-Agent is not in the `Mozilla/5.0 (compatible; ...)` form (P1, P2, P3, C1); and, from the right User-Agent, every request from a network whose allowance is spent, whatever the client (L2, L3, G2, AB) | refused without detail: the User-Agent, or this network's allowance spent; it does not say which |

The client classifies both as refusals and never retries one straight away.
On 2026-11-19 neither shape meant "the network is blocked": the page's search
URLs were answered minutes after each (C after P2, F after E, H after G). A
refusal of a **search-URL page load** has not been seen; if one appears, it is
the first evidence of a block that also stops the page, and is recorded in
full.

### Other answers

| Answer | Seen | Reading |
|---|---|---|
| `wrb.fr` error, code 3 | X notes: an unknown airport or an invalid date | `INVALID_ARGUMENT`; do not retry |
| `wrb.fr` error, code 14 | X notes only | `UNAVAILABLE`; transient |
| `wrb.fr` error, code 9 | twice, 2026-11-20, from two networks: at 15:30 local from one already refusing the client (the same search 15 and 40 s later got the bare 13), and at 15:50 from one answering it (the searches 10 minutes before and after were answered). Neither body was recorded | `FAILED_PRECONDITION`. Says nothing about a block: a passing error, with no cool-down and no automatic retry. Any other code the client does not know is treated as a refusal |
| HTTP 302 to `/travel/flights/unsupported` | a search page fetched with a non-browser User-Agent (P5, P9) | refused: Google does not serve its pages to this client. A refusal, with a cool-down |
| HTTP 429 | **not yet seen** | throttled; honour `Retry-After` |
| HTTP 302 to `/sorry/index` | **not yet seen**; Google's usual "unusual traffic" page elsewhere | blocked: a person must clear a check; never automated |
| HTML body containing "unusual traffic" | **not yet seen** | blocked, as above. On a search page the flights are looked for first: a page that carries `ds:1` is an answer, whatever its scripts or help texts mention |

Every probe and capture in this work records a refusal **in full** (status,
headers, body, any redirect, which is not followed). When one of the
not-yet-seen forms appears, it is committed as a reference and becomes a
fixture for the client's classifier. Throttling is never provoked on purpose:
a real block costs the whole network its Google Flights for hours.
