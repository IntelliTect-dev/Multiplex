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
| `google-flights/google-flights-date-picker-prices-2026-11-19.har` | Opened the Departure date picker for a SEA-DEN round trip: GetCalendarPicker asked for 2026-11-19..2026-12-31 with a 7-day trip and answered 43 priced cells. Then chose Tue 2027-01-12: the page asked twice for return-date prices 2027-01-12..31 and BOTH answered HTTP 200 carrying gRPC code 13 and a travel.frontend.flights.ErrorResponse -- the failure path, recorded on purpose. | 3 | 3 | 2026-11-19 | #89 |
| `google-flights/google-flights-direct-search-refused-2026-11-19.har` | One direct HTTP GetShoppingResults (not a browser), one-way SEA-DEN 2026-12-15, sent with an honest User-Agent, no cookies, no browser headers and no anti-bot token, 60 minutes after the browser refusals. Answered HTTP 200 with a BARE gRPC 13 item ([13], no ErrorResponse detail) in about 200 ms, and set an NID cookie. The browser refusals carried an ErrorResponse with a search session id; this one did not. Redirects were not followed (there was none). | 1 | 1 | 2026-11-19 | #89 |
| `google-flights/google-flights-one-way-search-2026-11-18.har` | Signed out, browsed by hand: one-way SEA-SFO 2026-12-15. GetShoppingResults streamed 6 payload frames: frames 0, 1 and 5 carry the 3 best and 15 other flights (index 17 only in frame 0), frames 2-4 are status only. The fresh-search selection was [null,null,null,<search session id>]. Source of the itinerary, segment, itinerary-protobuf and selection-token field maps in docs/protocol.md. | 1 | 1 | 2026-11-18 | #89 #70 |
| `google-flights/google-flights-round-trip-search-refused-2026-11-19.har` | Searched SEA-DEN round trip 2027-01-12/19 from the home page form: GetShoppingResults answered HTTP 200 carrying gRPC code 13 and a travel.frontend.flights.ErrorResponse, with no flights. The request is the page's own fresh-search filter (selection [], sort 0), so this pins how a refused search looks on the wire, not why it was refused. | 1 | 1 | 2026-11-19 | #89 |

<!-- END GENERATED CATALOGUE -->

## Naming

`google-flights/google-flights-<action>-<yyyy-MM-dd>.har`, where `<action>`
names what a person did on the page and the date is the capture date.

## Provenance

| Capture (`.har-captures/www.google.com/`, gitignored) | How it was made |
|---|---|
| `2026-11-18-124419` | Signed out, throwaway profile, browsed by hand. |
| `2026-11-19-133031` | Issue #89. Signed out, dedicated `google-flights` capture profile, native Chrome, driven one paced step at a time over CDP. |

Every request was made signed out: no account, cookie or personal identifier
is involved. Two things were taken out on purpose:

- **The pre-filled origin.** Google pre-fills "Where from?" from the
  network's location, so the home page and the first autocomplete call carry
  the operator's approximate location. Neither is extracted. (A city can still
  appear in a search answer as a connecting airport; that is route data.)
- **Session-scoped values.** The page's anti-bot token
  (`x-goog-batchexecute-bgr`) and page session id (`f.sid`) are redacted by the
  repository's `.har-policy.project.json`. `extract-har-reference.js` does not
  apply that policy yet (it scrubs a copy staged outside the repository), so
  each reference is scrubbed once more in place with `sanitize-har.js` after
  extraction, and gated again.

## Re-capture

Captures are made at a human pace on a strict request budget: Google
soft-blocks a client that sends too many requests. Read `../NOTES.md` before
recording.
