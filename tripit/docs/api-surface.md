# TripIt web API surface (`www.tripit.com/api/v2/*`)

Authoritative endpoint reference for the TripIt web SPA backend, derived on **2026-10-29**.

## Provenance, and why it is not the HAR

Three sources, in descending authority:

| Source | What it establishes | Confidence |
|---|---|---|
| **The SPA's own JS bundle** (`app-DVPKDADO.js`, `index-a-FZrzJu.js`) | every endpoint path, HTTP method, and request payload field the official client can send | highest — it *is* the client |
| **Live probes** against a signed-in session | which endpoints answer, response envelope shape, which parameters are honoured | high, but one account only |
| `samples/har/*.har` | a recorded session's traffic | lowest — see below |

**The HAR captures are a poor guide to this API and must not be treated as the surface.**
`har/browse.har` records a session that never left the trips list (`get/profile` ×62 is
SPA polling), and `har/tripit-api-capture-2026-05-01.har` is a *probe sweep* whose
404s and 400s were widely mistaken for real endpoints. Specifically:

- The SPA builds request paths with **ky** configured `prefixUrl: '/api'`, so the source
  contains `v2/get/profile`, never `/api/v2/get/profile`. Grepping a bundle for the full
  path finds nothing and makes the surface look an order of magnitude smaller than it is.
- `/api/v1/*` and `/web/v2/*` are **not endpoints**. Every one 404s; they appear in the
  captures only because a probe script requested them. The repository README previously
  documented `/web/v2/*` as a real (if unwrapped) family — it is not.
- `/graphql` and `/typeahead/search` **302 to `/app/404`**. The 200s recorded against them
  in the probe HAR are the SPA shell HTML, not data. There is no GraphQL API here; the
  autocomplete endpoints below are the real equivalent.

## Shape of the API

- **Everything is `GET` or `POST`.** No `PUT`/`PATCH`/`DELETE` except `v2/documents`
  (`PUT`, `DELETE`) and `v2/profilePhoto` (`DELETE`). Mutations are `POST`, including
  deletes — `POST v2/delete/trip/uuid/{uuid}`.
- **Identifiers travel as path segments, not query parameters**:
  `v2/get/{type}/uuid/{uuid}`. The query-string spelling (`v2/get/trip?uuid=…`) also
  answers 200, but the path form is what the SPA sends.
- **Responses share a v1-style envelope**: `{ timestamp, num_bytes, <TypeKey>… }`, where
  `<TypeKey>` is `Trip`, `Profile`, `AirObject`, … A list response adds `page_num`,
  `page_size`, `max_page`, `total_items`.
- **Every scalar is a JSON string.** Measured over a `list/trip` response: 41
  numeric-looking strings and **zero** real JSON numbers — `"timestamp":"1793935084"`,
  `"id":"388109389"`, `"hour_clock":"1"`, `"opted_in_ts":"0"`. Generated DTOs need
  string-tolerant converters on every numeric and boolean property; a plain `int?` throws
  `Utf8JsonReader.GetInt32()`.
- The verbs (`create`, `replace`, `delete`, `move`, `merge`) and the envelope match
  TripIt's **documented public v1 API**, which is therefore a sound oracle for field
  names and nullability even though this is a different base path.

### Object types

`{type}` in the paths below is one of these. The path segment, the response envelope key,
and the display name differ — all three are taken from the bundle's own maps:

| Path segment | Envelope key | Notes |
|---|---|---|
| `air` | `AirObject` | display name `flight` |
| `lodging` | `LodgingObject` | |
| `car` | `CarObject` | |
| `rail` | `RailObject` | |
| `transport` | `TransportObject` | |
| `cruise` | `CruiseObject` | |
| `restaurant` | `RestaurantObject` | |
| `activity` | `ActivityObject` | |
| `note` | `NoteObject` | |
| `directions` | `DirectionsObject` | display name `direction` |
| `parking` | `ParkingObject` | |
| `map` | `MapObject` | |
| `weather` | — | read-only; no create/replace path |

Single-character segment codes also appear in the bundle: `M` map, `D` directions,
`P` parking, `O` layover.

## Endpoints

`${…}` marks a path segment the caller supplies. `json{…}` and `query{…}` list the
request fields resolved from the call site; `(obj)` means the call site passes a
constructed object whose fields are not statically enumerable and need a capture to pin
down.

### Trips

| Method | Path | Request |
|---|---|---|
| GET | `v2/list/trip` | `query(obj)` — `past`, `page_size`, `page_num`, `traveler`, `exclude_types`, `trip_permission_filter`, `should_sort_trips_by_date` |
| GET | `v2/get/trip/uuid/${uuid}` | `query(obj)` |
| GET | `v2/get/trip/uuid/${uuid}/include_objects/true` | trip **and all its objects in one call** |
| GET | `v2/list/trip?include_objects=true` | every listed trip **plus all their objects** (measured 2026-11-06); objects carry `trip_uuid` |
| POST | `v2/create/trip` | `json(obj)` |
| POST | `v2/replace/trip/uuid/${uuid}` | `json(obj)` |
| POST | `v2/delete/trip/uuid/${uuid}` | — |
| POST | `v2/merge/trip` | `query(obj)` |
| POST | `v2/updateTripImage` | `json{trip_uuid,url,image_data}` |
| POST | `v2/updatePublicTripShareUrl` | `json{trip_uuid}` |

Measured 2026-11-06:

- `page_size` is honoured at any size — `past=true&page_size=500` returned all 317 past
  trips in one page. Defaults are small (5 for past trips).
- `traveler=all` equals the default; `traveler=false` returns no trips for an account that
  travels on all of its own trips.
- `v2/get/trip/id/${id}` answers **400** "you must specify an uuid" — the web API is
  UUID-only.
- A list holding one item returns it as an **object**, not a one-element array
  (`"Trip":{…}` vs `"Trip":[…]`), exactly as the v1 API does. Read every collection with
  a helper that accepts both (`TripItJson.AsList`).

### Trip objects (segments)

| Method | Path | Request |
|---|---|---|
| GET | `v2/list/object` | `query(obj)` — `trip_uuid`, `is_unfiled_items_only`, `page_size`, `page_num` |
| GET | `v2/get/${type}/uuid/${uuid}` | — |
| POST | `v2/create` | `json(obj)` |
| POST | `v2/replacePlan/${type}/uuid/${uuid}` | `json(obj)` |
| POST | `v2/delete/${type}/uuid/${uuid}` | — |
| POST | `v2/delete/segment/uuid/${uuid}` | — |
| GET | `v2/move/${type}/uuid/${uuid}?trip_uuid=${tripUuid}` | moves an object between trips |
| POST | `v2/updateMultipleCosts` | `json(obj)` |
| POST | `v2/deleteUnfiledItems` | `json(obj) query(obj)` |
| GET | `v2/flightInfo` | — |

> `v2/get/${type}/uuid/${uuid}` needs the **object's** UUID (e.g. the `AirObject`), not a
> segment's — a segment UUID answers 400 "uuid provided is for the wrong type".

> **`object_type` does not filter `list/object`.** All twelve values return a
> byte-identical response (10,009 B, measured). The honoured filters are `trip_uuid` and
> `is_unfiled_items_only`. Do not expose an `object_type` parameter — it would silently
> do nothing.

### Profile and traveler

| Method | Path | Request |
|---|---|---|
| GET | `v2/get/profile` | — |
| POST | `v2/replace/profile` | — |
| GET | `v2/travelerProfile/get?exclude_templates=true` | — |
| POST | `v2/travelerProfile/update` | `json(obj)` |
| POST | `v2/profilePhoto` / DELETE | `json{image_data}` |

### Sharing and participants

| Method | Path | Request |
|---|---|---|
| POST | `v2/invite` | `json(obj)` |
| POST | `v2/changeTripInvitations` | `json(obj)` |
| POST | `v2/delete/trip_participant/trip_uuid/${uuid}/profile_ref/${ref}` | — |

### Autocomplete (the real replacement for the dead `/typeahead/search`)

All take a single `query` parameter (resolved live 2026-11-06; `q` and `term` answer 400)
and return a bare JSON **array** (no envelope):

| Endpoint | Item shape | `id` meaning |
|---|---|---|
| `GET v2/autocompleteAirport?query=SEA` | `{id,label}` | IATA airport code |
| `GET v2/autocompleteAirline?query=Alaska` | `{id,label}` | IATA airline code |
| `GET v2/autocompletePlace?query=Paris` | `{id,label}` | opaque place hash |
| `GET v2/autocompleteCountry?query=New` | `{id,label}` | ISO 3166 alpha-2 |
| `GET v2/autocompleteState?query=Wa` | `{id,label}` | state code (`country_code` accepted) |
| `GET v2/autocompleteAddress?query=…` | `{place_id,display_name,main_name,secondary_name}` | Google place id |
| `GET v2/autocompleteAddressDetail` | `query(obj)` | resolves a `place_id` to an address |

### Documents and attachments

| Method | Path | Request |
|---|---|---|
| POST | `v2/documents` | `json{attached_to_uuid,documents}` |
| PUT | `v2/documents` | `json(obj)` |
| DELETE | `v2/documents?uuid=${uuid}` | — |

### Alerts, config, misc

`GET v2/listProAlerts`, `POST v2/updateProAlertsViewed`,
`POST v2/dismissAccountPremiumAlert json{alert_uuids}`, `GET v2/appConfig`,
`GET v2/gtmDataAsJson`, `GET v2/purchasedProductInfo`, `GET v2/utilEncode query(obj)`,
`GET v2/isValidPromo query(obj)`, `POST v2/validateRecaptcha json{token,action}`,
`GET v2/billingCountryOptions`, `GET v2/getCountryCodesForSmsSetting`,
`POST v2/configureCalendarFeed`, `POST v2/refreshFeedUrl`,
`POST v2/updateImportTripPlanSharing`, `POST v2/updateUserSettings?user_feature=AiImport json{is_opted_in}`,
`POST v2/timestampIntroducedAiImportFeature`, `POST v2/initiateAutoImportSetup`,
`POST v2/completeAutoImportSetup`, `POST v2/deleteAutoInboxImport`.

### Deliberately NOT wrapped

These resolve in the bundle but are excluded from the generated client, PowerShell module
and MCP server. An MCP tool that can delete an account or change a password is a bad idea
regardless of how carefully its description is written, and the payment endpoints would
mean handling card numbers:

`v2/deleteAccount` (`json{password}`), `v2/sendDeleteAccountConfirmationEmail`,
`v2/changePassword`, `v2/completeChangePassword`, `v2/requestChangePassword`,
`v2/removeEmail`, `v2/makePrimaryEmail`, `v2/requestAddEmail`,
`v2/requestMergeAccounts`, `v2/updatePaymentInfo`, `v2/updateProUpgrade`,
`v2/updateCancelPremium`, `v2/updateSmsPhoneNumber`, `v2/updateVerifySmsCode`,
`v2/deleteSmsPhoneNumber`.

Wrapping them would need an explicit opt-in flag and a deliberate decision.

## Authentication

Session-cookie plus CSRF header. Both are read from a signed-in browser profile by
`tools/extract-session.js`:

- `Cookie` — the full jar for `https://www.tripit.com`. The cookies that carry the
  session are `session_id`, `it_session_id` and `it_csrf`, and **all three are
  `httpOnly`**.
- `X-CSRF-Token-WA` — equals the `it_wa_csrf` cookie (not `httpOnly`).

Observed expiries: `it_wa_csrf` ~2 days, the Akamai bot cookies (`bm_sz`, `bm_sv`) ~2
hours. A *snapshot* of headers is therefore short-lived; a durable signed-in browser
profile that can be re-read on demand is the thing worth keeping.

### A stale session fails as 403, not 401

The site sits behind Akamai, and the bot-management cookies are the short-lived part of
the session. Measured: a session roughly an hour old returned a 396-byte Akamai block page
(`errors.edgesuite.net`, `Reference #18...`) from every endpoint, with **403**. Re-running
`tools/extract-session.js` against the same still-signed-in browser restored `200`
immediately — no sign-in required, because the browser keeps those cookies fresh and the
`session_id` cookie had not expired at all.

Two consequences:

- **A 403 with a small HTML body means "re-extract the session"**, not "the request is
  malformed" and not "the credentials are revoked". It is easy to misdiagnose as a header
  problem; it is not — the same header set succeeds before and fails after the bot cookies
  age out.
- `TripItClient` classifies only 302/401 as an auth failure, so this currently surfaces as
  a generic `HttpRequestException` carrying HTML. It should raise
  `TripItAuthenticationException` and name the remedy.

A long-running consumer (the MCP server especially) therefore needs a session refresh on
roughly a two-hour cadence, or a 403-triggered re-extraction, rather than a session read
once at startup.

The header set the SPA actually sends, captured into `session.json` as `spa_headers`:
`accept: application/json`, `accept-language: en-US` (not `en-US,en;q=0.9`),
`x-requested-with: XMLHttpRequest`, `x-tripit-app-info: web/0.0.2`, `x-dtpc` (Dynatrace),
`referer`, `user-agent`, and the `sec-ch-ua*` client hints.

> **Why the extractor reads the cookie jar, not the request headers.** Chrome attaches the
> `Cookie` header *after* Playwright surfaces a request, so `request.headers()['cookie']`
> is empty and the `httpOnly` session cookies never appear there at all. The previous
> implementation gated on that header being present and so could never succeed. Read
> cookies from `context.cookies(origin)`.

## Still unverified

Honest gaps, so nobody mistakes this for a settled specification:

- Every `json(obj)` above. The field lists for `create`, `create/trip`,
  `replace/trip`, `replacePlan`, `travelerProfile/update`, `invite`,
  `changeTripInvitations`, `updateMultipleCosts` and `merge/trip` are **not yet known** —
  the call sites build them dynamically. They need a capture of the SPA performing each
  mutation.
- No mutating request has ever been recorded against this API in this repository. All
  three HARs are `GET`-only, so no create/replace/delete payload is evidenced anywhere.
- Response field names and nullability are known only for the endpoints probed live
  (`get/profile`, `list/trip`, `get/trip`, `list/object`, `travelerProfile/get`,
  `purchasedProductInfo`, `list/points_program`) and only for one account's data.
- `list/points_program` returned an empty payload (43 B) because the test account has no
  points programs — its populated shape is unknown. (One probe answered 401 with a session
  that was otherwise accepted; a retry seconds later answered 200. Not reproduced.)
