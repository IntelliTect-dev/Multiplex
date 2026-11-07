# TripItEx

A .NET 10 toolkit wrapping the TripIt web SPA backend (`https://www.tripit.com/api/v2/*`):
a typed client library, an MCP server, and a binary PowerShell module.

**Goal:** full CRUD over every TripIt entity (trips, flights, lodging, cars, rail, …)
exposed through both a PowerShell module and an MCP server. **Read and write are done**
for trips and all twelve plan types (client sub-clients, MCP tools, cmdlets, unit + live
tests). What is left is the long tail — see **Next steps**.

**Why the web backend and not TripIt's documented public API (decided 2026-11-06, owner's
call):** `api.tripit.com/v1` is alive (401 without credentials) but TripIt no longer issues
OAuth consumer keys — its help center says the public API "is no longer available for new
developer integrations", `/developer/create` redirects a signed-in user to the trips page,
and `tripit/api#287/#288` confirm it. HTTP Basic on v1 needs support to enable it per
account. Don't re-investigate; use the v1 docs only as a field-name oracle.

## Read this before touching the API surface

**[`docs/api-surface.md`](docs/api-surface.md) is the authoritative endpoint reference.**
68 endpoints with methods and payload fields. Start there, not with the HAR captures.

**Do not derive the API from `samples/har/` or `har/`.** Those captures actively misled
this project, and four separate wrong conclusions came out of them:

1. **The SPA builds paths with `ky` configured `prefixUrl: '/api'`.** Its bundle contains
   `v2/get/profile`, never `/api/v2/get/profile`. Grepping for the full path finds
   essentially nothing and makes the API look an order of magnitude smaller than it is.
   This is why the client wrapped 7 endpoints when 68 exist.
2. **`/api/v1/*` and `/web/v2/*` are not endpoints.** Every one 404s. They appear in the
   captures only because an old probe script requested them.
3. **`/graphql` and `/typeahead/search` 302 to `/app/404`.** There is no GraphQL API here.
   The recorded 200s against them are the SPA shell HTML. The `v2/autocomplete*` family is
   the real search surface.
4. **Only three listers exist**, and the server says so outright: *"you can only list
   trips, objects, or points programs"*. `list/air`, `list/alert`, `list/traveler` and
   ~15 others are probe artifacts.

Also measured, and a trap worth not re-falling into: **`object_type` does not filter
`v2/list/object`** — all twelve values return a byte-identical 10,009-byte response. The
honoured filters are `trip_uuid` and `is_unfiled_items_only`. Never expose `object_type`.

**The SPA's JS bundle is the best source of truth** — it *is* the official client. Fetch
`https://www.tripit.com/app/trips`, extract the `<script src>` bundles (`app-*.js` is the
big one), and search for `v2/` rather than `/api/v2/`. Endpoint paths are hoisted into
short minified consts (`fc`, `pc`, `yu`…), so to find a call site search for the const
name, then look for `.post(const, {json:{…}})`. The bundle hash changes between deploys.

## Shape of the API

- Everything is `GET` or `POST`. **Deletes are POST**: `POST v2/delete/trip/uuid/{uuid}`.
  Only `v2/documents` (`PUT`/`DELETE`) and `v2/profilePhoto` (`DELETE`) differ.
- Identifiers are **path segments**: `v2/get/{type}/uuid/{uuid}`.
- Responses use a v1-style envelope: `{ timestamp, num_bytes, <TypeKey>… }`; lists add
  `page_num`, `page_size`, `max_page`, `total_items`.
- `v2/get/trip/uuid/{uuid}/include_objects/true` returns a trip **and all its objects in
  one call** — the natural backing call for an MCP composite tool.
- The verbs (`create`, `replace`, `delete`, `move`, `merge`) and the envelope match
  TripIt's **documented public v1 API**, so the v1 docs are a sound oracle for field names
  and nullability. Use them that way; do not wrap the v1 OAuth API itself.
- Classify outcomes from **status + body**, not status alone.
- **Every scalar comes back as a JSON string.** Measured on `list/trip`: 41 numeric-looking
  strings, **zero** real JSON numbers — `"timestamp":"1793935084"`, `"id":"388109389"`,
  `"hour_clock":"1"`. Any `int?` / `long?` / `bool?` DTO property therefore throws
  `Utf8JsonReader.GetInt32()` on deserialization. DTOs need string-tolerant converters for
  every numeric and boolean property; this is the v1 API's long-standing behaviour, not a
  quirk of one endpoint. Deserialize with `TripItJson.Options` (lenient converters).
- **A one-item collection is an object, not an array** (`"Trip":{…}`). Always read
  collections with `TripItJson.AsList`.

## Authentication

Session cookie + CSRF header, read from a signed-in browser profile:

```powershell
node tools/extract-session.js      # -> ~/.tripit-mcp/session.json
```

- Default capture profile is the **shared** `%LOCALAPPDATA%\har-capture\profile` (the
  `web-api-discovery` skill's own), so one browser sign-in serves every project.
  Override with `--profile <name|path>`, `TRIPIT_CAPTURE_PROFILE` or `HAR_CAPTURE_PROFILE`.
- It attaches to a Chrome already on the CDP port (9222), then tries the profile's
  `DevToolsActivePort` and 9333 (the capture browser the skill launches), else spawns one.
- Session cookies come from the **cookie jar**, not from an intercepted request: Chrome
  attaches `Cookie` *after* Playwright surfaces a request, so `request.headers()['cookie']`
  is always empty and the `httpOnly` cookies carrying the session (`session_id`,
  `it_session_id`, `it_csrf`) never appear there. A previous implementation gated on that
  header and could never succeed — don't reintroduce it.
- Expiries: `it_wa_csrf` ~2 days, Akamai bot cookies ~2 hours. A *durable signed-in
  profile* is what matters, not a header snapshot — a profile stayed valid for a week.
- **The site is behind Akamai, and a stale session fails as `403` with an HTML block page**
  — not `302`/`401`. Measured: a session ~1 hour old returned a 396-byte Akamai page
  (`errors.edgesuite.net`, `Reference #18...`) on every endpoint; re-running the extractor
  restored `200` immediately, with no sign-in needed. The `bm_sz` / `bm_sv` bot-management
  cookies are part of what the API requires, and they are the short-lived part.
  `TripItClient.Classify` maps it to `TripItAuthenticationException`, and the client then
  re-reads the session file / runs its `ISessionRefresher` (the MCP server uses
  `CommandSessionRefresher` → `node tools/extract-session.js`) and retries once.
  **A 403 with a small HTML body means re-extract the session, not "the client is broken".**
- A 302 or 401 also means the session rotated: re-run the extractor.

## Build and test

```powershell
dotnet build TripItEx.slnx -c Release
dotnet test                                  # backend tests skip without a session
pwsh -File scripts/Start-TripItShell.ps1     # module built + imported, interactive
dotnet run --project src\TripItMcp\TripItMcp.csproj -c Release   # MCP over stdio
```

Requires the .NET 10 SDK (`global.json` pins 10.0.203) and **PowerShell 7.5+** — the
binary module targets `net10.0` and will not load in 5.1 or 7.4.
`Directory.Build.props` sets `TreatWarningsAsErrors`, so warnings fail the build.

## Decisions already made (2026-10-29) — don't relitigate

- **Keep and extend** the existing code; the transport, auth and session stores are sound.
  The gap was captured surface, not code quality.
- **C#** for all three surfaces: one typed client serves both the PowerShell module and
  the MCP server with no duplication.
- **Resource sub-clients** (`client.Trips.GetAsync(uuid)`), not a flat 40-method class.
  Generated methods in `*.g.cs`, hand-tuned code in a sibling partial.
- **Mutation capture is approved, scoped to one dated throwaway trip** — create it,
  exercise one of each segment type, delete it. Nothing outside that trip.
- **Mobile app discovery: skipped.** Web-only.
- **Account-destruction, password and payment endpoints stay unwrapped**
  (`v2/deleteAccount`, `v2/changePassword`, `v2/updatePaymentInfo`, …). An MCP tool that
  can delete an account or charge a card is a bad idea however it is described. Revisit
  only behind an explicit opt-in flag. The full list is in `docs/api-surface.md`.

## Writes: how they work (read before changing PlanSchema)

- Payload shapes come from the SPA's serializer tables (`getModelMap()` per class in the
  `index-*.js` bundle) and were confirmed against bodies captured from the SPA. They are
  transcribed in `src/TripIt/PlanSchema.cs`, **in serializer order**.
- **Order is load-bearing.** TripIt converts the JSON to XML and validates it against an
  ordered XSD: right fields in the wrong order → 400 "JSON did not validate with the xsd".
  `TripItWrite.PreparePlan` validates, stringifies, normalizes dates/times, then
  `PlanSchema.Order`s every level. Add new fields at their serializer position.
- `replacePlan` / `replace/trip` take the **complete** state. Updates are fetch →
  `PlanSchema.ToWritable` → `TripItWrite.DeepMerge` → replace.
- To capture a new mutation: drive the SPA in the capture browser (port 9333) with
  Playwright and read bodies via `page.route` — `request.postData()` is null over CDP for
  the SPA's fetch calls.
- Live write tests need `TRIPIT_ALLOW_WRITES=1` and stay inside one dated, private
  throwaway trip that is deleted in `finally`. Deleting a trip deletes its plans.

- **`list/trip` defaults to `traveler=true`** and silently hides trips the user is on but
  not travelling on. Always send `traveler` (the client defaults to `all`).
- **`create/trip` ignores `is_owner_traveler:"false"`**; `replace/trip` honours it, so
  `TripsClient.CreateAsync` follows up with a replace.
- `v2/invite` uses real JSON booleans (not strings) and emails people. Live tests invite
  only `tripitex-test@example.com` (RFC 2606, undeliverable).

## Next steps, in order

1. `v2/documents` (attachments), `v2/updateMultipleCosts`, `v2/travelerProfile/update`.
2. Incremental sync (`modified_since`) and an all-objects listing with a past filter, if
   the web backend honours them (unprobed).

Done: #2 (mutation capture), #3 (read surface), #4 (CRUD), #5 (autocomplete),
#7 (lenient JSON + Akamai 403).
