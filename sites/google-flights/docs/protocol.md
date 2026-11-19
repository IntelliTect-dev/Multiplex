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
| **X** | GoogleFlightsEx `2026-11-06-211138` | The owner's earlier project's capture. **Cross-check only**: nothing here rests on X alone without saying so, and nothing from it is committed. |

## 1. Endpoints

All on `https://www.google.com`. No sign-in is involved anywhere.

| RPC | Transport | What the page uses it for | Evidence |
|---|---|---|---|
| `GetShoppingResults` | service | Flight options for one leg, given any earlier legs already chosen | A, B, X |
| `GetBookingResults` | service | Sellers, fare brands and booking links for a fully chosen trip | X |
| `GetCalendarPicker` | service | Prices shown in the date picker | B, X |
| `GetCalendarGraph` | service | Price per departure date (fixed trip length for a round trip) | X |
| `GetCalendarGrid` | service | Departure x return price matrix | X |
| `H028ib` | batchexecute | Airport/city autocomplete, one call per keystroke | B, X |
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

| Item | Meaning | Required for direct HTTP? |
|---|---|---|
| `x-goog-ext-259736195-jspb` | `[language, region, currency, 2, null, [UTC offset minutes], null, null, 7, []]`. **This is where currency and region are chosen per request**; the payload carries neither. | **pending** (direct-HTTP probe) |
| `x-same-domain: 1` | Google's CSRF guard for these endpoints | **pending** |
| `x-goog-batchexecute-bgr` | Computed by the page's own script on every request (B: present on the calls that were answered and on the ones refused alike) | **pending**. A direct client must not forge or replay it. |
| Cookies `NID`, `OTZ` | Set by Google on the first page load (B) | **pending** |
| `f.sid`, `bl`, `_reqid` | Page session, build label, request counter | **pending** |

No `at=` token is sent: the page is signed out.

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
[null, null, tripType, null, [], cabin, passengers, null, null, null, null, null, null, legs, null, null, null, 1]
```

| Index | Meaning | Values seen | Evidence |
|---|---|---|---|
| 2 | Trip type | 1 round trip, 2 one-way, 3 multi-city | A (2), B (1), X (3) |
| 4 | Always `[]` | | A, B |
| 5 | Cabin | 1 = economy; other values **pending** | A, B |
| 6 | Passengers `[adults, children, ?, ?]` | `[1,0,0,0]` (A, B), `[2,0,0,0]` (X); the order of the infant fields **pending** | A, B, X |
| 7 | **pending** (max price candidate) | absent | |
| 13 | Legs, one per flight leg | | A, B, X |
| 17 | Always 1 | | A, B |

### Leg

```
[origins, destinations, times, stops, airlines, null, "yyyy-MM-dd", null, selected, null, null, null, null, null, 3]
```

| Index | Meaning | Evidence |
|---|---|---|
| 0, 1 | Origins and destinations: `[[[id, kind], ...]]` (a list of places, wrapped once more). Kind 0 = IATA airport (A, B); 5 = Google place id for a city such as `/m/0d9jr` (X); 4 = a wider region (X, in `BVAT3`) | A, B, X |
| 2 | Time window, `null` when unset. Layout **pending** | |
| 3 | Stops: 0 = any (A, B); 1 = nonstop (X); other values **pending** | A, B, X |
| 4 | Airlines or alliances, `null` when unset. `["DL"]` seen (X); alliances **pending** | X |
| 6 | Date, `yyyy-MM-dd` | A, B, X |
| 8 | Flights already chosen for this leg: `[[from, "yyyy-MM-dd", to, null, carrier, number], ...]`, one per segment | X |
| 14 | `3`. Present on the first search of a trip, absent on some later steps | A, B, X |

The date picker's own requests send a short leg, `[origins, destinations, null, 0]` (B #86).

## 5. Requests by RPC

| RPC | Inner request | Evidence |
|---|---|---|
| `GetShoppingResults` | `[selection, filter, sort, 0, 0, 1]` | A, B, X |
| `GetBookingResults` | `[[null, selectionToken], filter, null, 0 or 1]`; every leg carries index 8. The last value's meaning is **pending** | X |
| `GetCalendarPicker` | `[null, filter, [from, to], null, [tripDays, tripDays]]` before a departure is chosen; `[null, filter, [dep, dep], [from, to]]` after | B |
| `GetCalendarGraph` | `[null, filter, [from, to], null, [minTripDays, maxTripDays]]` | X |
| `GetCalendarGrid` | `[null, filter, [depFrom, depTo], [retFrom, retTo]]` | X |
| `H028ib` | `["<text typed>", [1,2,3,5], null, [2], 1]` for an origin; `[..., [1,2,3,5,4], null, [1,1,1], 1]` for a destination | B |
| `tDoGIe` | `[null, [[id, kind]]]` | B |
| `BVAT3` | `[null, [[[[origin]], [[destination]]], ...]]`, one pair per leg | B, X |

`selection` in `GetShoppingResults`:

| Value | When | Evidence |
|---|---|---|
| `[]` | a fresh search | B, X |
| `[null, null, null, "<search session id>"]` | a fresh search the page had a session id for | A |
| `[null, "<selection token>"]` | choosing the next leg after one is chosen | X |

`sort`: 0 = Google's "top flights" order (A, B); 2 seen with nonstop (X). Other values **pending**.

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
| 10 | **pending** (arrival day offset candidate; `null` on same-day trips) | A |
| 13 | Layovers: `[[minutes, arrive airport, depart airport, null, arrive airport name, arrive city, depart airport name, depart city], ...]`. The two airports differ when the connection changes airport. All of A's itineraries were nonstop, so this is **X only** until a connecting capture lands | X |
| 22 | Emissions. **Not read: the client exposes no emissions data** | A |

`itinerary[6]` is **unknown**. Earlier notes read it as "separate tickets",
but X has it `true` on 19 of 21 itineraries of one SEA-BOS search, which
cannot all be separate tickets. Do not rely on it until a capture shows it
changing.

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

`1.0` lists booking options (X: 6 for one round trip):

| Index | Meaning | Evidence |
|---|---|---|
| 1 | Sellers `[[code, name, null, isAirline]]` | X |
| 3 | Flights `[[carrier, number], ...]` | X |
| 5 | `[displayUrl, null, ["https://www.google.com/travel/clk/f", [["u", "<opaque>"]]]]`: Google's booking redirect, an HTML form POST | X |
| 7 | `[[null, price], "<token>"]` | X |
| 19 | `"[\"<base64 protobuf>\"]"` | X |
| 21 | Fare: `[[carrier, "DELTA MAIN BASIC"], [[feature, level], ...], true, "Delta Main Basic", ...]` | X |

Booking options are **pending** in this repository's captures.

### Calendar RPCs

`GetCalendarPicker`, `GetCalendarGraph` and `GetCalendarGrid` answer alike:
index 1 is a list of cells

```
["yyyy-MM-dd departure", "yyyy-MM-dd return", [[null, price], "<token>"], flag]
```

A date with no price is **absent** (B: the picker returned 43 cells, one per
departure day in its range; X: grid 7 x 7 = 49 cells, graph 60).

### `H028ib` (autocomplete)

A list of places, each `[[kind, "Seattle, Washington", "Seattle", "<description>", "/m/0d9jr", ...], [airports]]`
(B). The airport layout inside is **pending** decoding.

### `tDoGIe` (place details)

`1` lists places: `[[id, kind], name, [cityId, cityName, ...], [lat, lng], countryCode, ?, countryName]`
(B #81: `["DEN",0]`, "Denver International Airport", `/m/02cl1` Denver, US).

## 7. The `tfs` URL parameter

The search page keeps its state in `?tfs=` (base64url protobuf) and
`?tfu=`. Decoded from the URLs the page produced in B:

| Field | Meaning | Evidence |
|---|---|---|
| 1 | 28 (constant) | B |
| 2 | 1 on the home page, 2 on the search page | B |
| 3 (repeated) | A leg: 2 = date `"yyyy-MM-dd"`, 13 = origin `{1: kind (1 = airport), 2: code}`, 14 = destination | B |
| 8 (repeated) | Passengers, one entry per traveler (1 = adult); other values **pending** | B |
| 9 | Cabin, 1 = economy; others **pending** | B |
| 14 | 1 | B |
| 16 | `{1: -1}`; **pending** (max price candidate: -1 = none) | B |
| 19 | Trip type, 1 = round trip; others **pending** | B |

`tfu` was `{5: {1: 3}}` throughout B.

The client does not need `tfs` to search. It needs it to produce a link a
person can open to see the same search on Google Flights.

**Proven both ways:** an encoder built from this table reproduces all three
`tfs` values the page produced in B, byte for byte (airports, dates, trip type
1, page type 1 and 2).

## 8. Not yet established

Pending the filter captures: cabin values, the infant order in passengers,
time windows, alliances, max price, max duration, sort values, the booking
request's last field, and every response position marked **pending** above.

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

Captured once (reference `direct-search-refused`): a direct HTTP request,
not a browser, 60 minutes after B's refusals.

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

| Shape | Seen on | Reading (one sample each: provisional) |
|---|---|---|
| `[13, null, [["...ErrorResponse", [[<header>], 0]]]]` | B, the page's own requests | the request reached the flights service, which failed it |
| `[13]` | a direct request | refused before the flights service; the throttle candidate |

The client classifies both as refusals. Whether the shape alone separates
"throttled" from "failed" is **pending** more samples.

### Other answers

| Answer | Seen | Reading |
|---|---|---|
| `wrb.fr` error, code 3 | X notes: an unknown airport or an invalid date | `INVALID_ARGUMENT`; do not retry |
| `wrb.fr` error, code 14 | X notes only | `UNAVAILABLE`; transient |
| HTTP 429 | **not yet seen** | throttled; honour `Retry-After` |
| HTTP 302 to `/sorry/index` | **not yet seen**; Google's usual "unusual traffic" page elsewhere | blocked: a person must clear a check; never automated |
| HTML body containing "unusual traffic" | **not yet seen** | blocked, as above |

Every probe and capture in this work records a refusal **in full** (status,
headers, body, any redirect, which is not followed). When one of the
not-yet-seen forms appears, it is committed as a reference and becomes a
fixture for the client's classifier. Throttling is never provoked on purpose:
a real block costs the whole network its Google Flights for hours.
