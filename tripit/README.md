# TripItEx

A .NET 10 toolkit for the TripIt web SPA backend (`https://www.tripit.com/api/v2/*`).
The repository ships three projects on top of a shared HTTP/auth library:

| Project | Path | Description |
|---|---|---|
| `TripIt` | `src/TripIt/` | Class library: `TripItClient`, typed DTOs, session stores, `Connect-TripIt` auth flow |
| `TripItMcp` | `src/TripItMcp/` | [Model Context Protocol](https://modelcontextprotocol.io/) stdio server exposing the library to LLM clients |
| `TripIt.PowerShell` | `src/TripIt.PowerShell/` | Binary PowerShell module with `Get-TripItProfile`, `Get-TripItTrip`, `Connect-TripIt`, etc. |

**[`docs/api-surface.md`](docs/api-surface.md) is the authoritative endpoint reference** —
68 endpoints including the full create / replace / delete surface, derived from the SPA's
own JavaScript bundle and verified with live probes. The typed methods below currently
cover only a read-only subset of it.

The HAR captures in `samples/har/` are a **poor** guide to this API and should not be read
as the surface: the SPA builds paths with `prefixUrl: '/api'`, so the recorded traffic
makes the API look an order of magnitude smaller than it is, and the probe sweep in
`har/tripit-api-capture-2026-05-01.har` contains many 404/400 paths that are not endpoints
at all. See the provenance section of `docs/api-surface.md`.

> **Why not TripIt's documented public API?** `api.tripit.com/v1` still answers, but TripIt
> stopped issuing OAuth consumer keys to new developers ("The TripIt public API is no longer
> available for new developer integrations" — TripIt help center; see also
> `tripit/api#287`, `#288`). `/developer/create` now just redirects to the trips page. The
> v1 docs remain a useful oracle for field names, because the web backend shares its
> envelope and verbs.

## Trips you are not travelling on

TripIt's trip list hides, by default, every trip you are on but not travelling on (on the
test account: 75 of 392 past trips). TripItEx lists **all** trips by default; the list,
search and itinerary tools take `traveler` = `all` | `mine` | `others`, and every trip
summary carries `i_am_traveling` and `my_role`. Such trips can be edited, planned and
deleted like any other; `tripit_create_trip` / `New-TripItTrip` take `i_am_traveling` /
`-NotTraveling` to create one, and `tripit_update_trip` / `Set-TripItTrip -IAmTraveling`
switch it.

## MCP tools (read)

All tools are read-only. Output is compact JSON: trips are summarized, and each trip's
contents are flattened into a chronological **itinerary** — one line per flight leg, hotel
stay, car rental, train, transfer, reservation or note, with local times, time zones,
confirmation numbers and type-specific details (seats, flight status, room type …).

| Tool | Backing endpoint | Purpose |
|---|---|---|
| `tripit_get_upcoming_itinerary` | `GET v2/list/trip?include_objects=true` | Every upcoming trip with its full itinerary, in one call |
| `tripit_list_trips` | `GET v2/list/trip` | Upcoming or past trips (paged) |
| `tripit_search_trips` | `GET v2/list/trip` (client-side filter) | Find trips by name, location, description, start date |
| `tripit_get_trip` | `GET v2/get/trip/uuid/{uuid}/include_objects/true` | One trip: itinerary, who it is shared with, optional raw objects |
| `tripit_list_objects` | `GET v2/list/object` | A trip's objects, or Unfiled Items |
| `tripit_get_object` | `GET v2/get/{type}/uuid/{uuid}` | One reservation/plan with every field |
| `tripit_get_profile` | `GET v2/get/profile` | Signed-in user profile |
| `tripit_get_traveler_profile` | `GET v2/travelerProfile/get` | Travel documents, loyalty numbers (masked unless `reveal_numbers`) |
| `tripit_list_alerts` | `GET v2/listProAlerts` | Pro alerts and account notices |
| `tripit_get_purchased_products` | `GET v2/purchasedProductInfo` | Subscription status |
| `tripit_lookup` | `GET v2/autocomplete*` | Airports, airlines, places, countries, states, addresses |
| `tripit_session_status` | `GET v2/get/profile` | Is the session configured, refreshable, accepted? |
| `tripit_raw_get` | `GET /api/v2/…` | Escape hatch for anything else |

## MCP tools (write)

Registered by default; set `TRIPIT_MCP_READONLY=1` to serve the read tools only. Payloads
follow the TripIt web app's own serializers (field order included — TripIt validates the
XML it derives from the JSON against an ordered XSD), and were verified live against a
throwaway trip. Destructive tools carry the MCP `destructive` annotation.

| Tool | Backing endpoint | Notes |
|---|---|---|
| `tripit_create_trip` | `POST v2/create/trip` | Private (not shared with the Inner Circle) unless `share_with_inner_circle` |
| `tripit_update_trip` | `POST v2/replace/trip/uuid/{uuid}` | Fetch → change only what was passed → replace |
| `tripit_delete_trip` | `POST v2/delete/trip/uuid/{uuid}` | Deletes the trip **and its plans**; requires `confirm_name` = the trip's exact name |
| `tripit_add_flight` | `POST v2/create` | Legs from airline + number + date; route and times looked up when omitted |
| `tripit_add_lodging` | `POST v2/create` | |
| `tripit_add_note` | `POST v2/create` | |
| `tripit_add_plan` | `POST v2/create` | Any type (car, rail, transport, cruise, restaurant, activity, parking, map, directions …) from TripIt field names |
| `tripit_describe_plan_fields` | (local) | The writable fields of a type |
| `tripit_update_plan` | `GET` + `POST v2/replacePlan/{type}/uuid/{uuid}` | Deep-merges changes into the current state; `segment_uuid` targets one leg |
| `tripit_delete_plan` | `POST v2/delete/{type}/uuid/{uuid}` | Whole reservation |
| `tripit_delete_segment` | `POST v2/delete/segment/uuid/{uuid}` | One leg; the rest stays |
| `tripit_move_plan` | `GET v2/move/{type}/uuid/{uuid}?trip_uuid=` | To another trip, or Unfiled Items |
| `tripit_merge_trips` | `POST v2/merge/trip` | Moves the source trip's plans into the destination and deletes the source; requires `confirm_source_name` |
| `tripit_add_participants` | `POST v2/invite` | **Emails invitations**; role viewer / editor / traveler |
| `tripit_change_participant_role` | `POST v2/changeTripInvitations` | |
| `tripit_remove_participant` | `POST v2/delete/trip_participant/...` | Refuses the owner |
| `tripit_lookup_flight` | `GET v2/flightInfo` | Scheduled route and local times |

Tool errors (unknown fields, a mismatched `confirm_name`, TripIt's own validation
messages, an expired session) are returned as readable tool errors rather than the SDK's
generic "An error occurred".

## Authentication

TripIt's web SPA is session-cookie + CSRF-header authenticated. The MCP server replays
the user's browser session by sending two values on each request:

* `Cookie` — full Cookie header value
* `X-CSRF-Token-WA` — the SPA's CSRF token

Provide them in **one** of these ways:

1. **Environment variables** (highest priority)
   ```
   TRIPIT_COOKIE=<full cookie header>
   TRIPIT_CSRF=<csrf token>
   ```
2. **Session file** at `%USERPROFILE%\.tripit-mcp\session.json`:
   ```json
   { "cookie": "...", "csrf_token": "..." }
   ```
3. **Custom file** path via `TRIPIT_SESSION_FILE=...`.

### Auto-extracting the session

Run the helper:

```powershell
node tools/extract-session.js                      # shared capture profile (default)
node tools/extract-session.js --profile tripit     # a named profile
node tools/extract-session.js --profile .\.chrome-profile
```

It attaches to a Chrome already listening on the CDP port, or else spawns one on the
capture profile, and writes `~/.tripit-mcp/session.json`. The default profile is the
shared `%LOCALAPPDATA%\har-capture\profile` used by the `web-api-discovery` skill, so one
browser sign-in serves every project; `--profile`, `TRIPIT_CAPTURE_PROFILE` or
`HAR_CAPTURE_PROFILE` override it.

Session cookies are read from the browser's **cookie jar**, not from an intercepted
request: Chrome attaches the `Cookie` header after Playwright surfaces a request, so the
`httpOnly` cookies that actually carry the session (`session_id`, `it_session_id`,
`it_csrf`) never appear in `request.headers()`. The helper refuses to write a session file
unless those cookies are present, so "signed out" fails loudly instead of producing a file
that 302s on first use.

If no Chrome is listening on 9222, the helper also tries the port recorded in the
profile's `DevToolsActivePort` file and 9333 (the `web-api-discovery` skill's capture
browser), so it attaches to an already-open capture browser instead of failing to launch a
second Chrome on a locked profile.

Observed expiries: `it_wa_csrf` ~2 days, the Akamai bot cookies ~2 hours. A stale session
fails as **403 with an Akamai HTML block page**, which the client reports as
`TripItAuthenticationException`.

### Automatic session refresh

On an authentication failure (302, 401 or Akamai 403) `TripItClient` re-reads the session
file and, if that has not changed, runs an `ISessionRefresher`, then retries once. The MCP
server wires in `CommandSessionRefresher`, which runs `node tools/extract-session.js`
(discovered beside or above the server binary) — so as long as the signed-in capture
browser stays open, the server keeps working past the ~2-hour cookie lifetime.
`TRIPIT_SESSION_REFRESH_COMMAND` overrides the command; `none` disables refreshing.

## Build & run

```powershell
dotnet build src\TripItMcp\TripItMcp.csproj -c Release
dotnet run   --project src\TripItMcp\TripItMcp.csproj -c Release
```

The server speaks MCP over **stdio** — point your MCP client at:

```
command: dotnet
args:    ["run", "--project", "D:\\Git\\TripItEx\\src\\TripItMcp\\TripItMcp.csproj", "-c", "Release", "--no-build"]
```

or after publishing:

```powershell
dotnet publish src\TripItMcp\TripItMcp.csproj -c Release -o publish
# publish\TripItMcp.exe is the MCP server binary
```

### Claude Desktop / Copilot config snippet

```json
{
  "mcpServers": {
    "tripit": {
      "command": "D:\\Git\\TripItEx\\publish\\TripItMcp.exe",
      "env": {
        "TRIPIT_SESSION_FILE": "C:\\Users\\<you>\\.tripit-mcp\\session.json"
      }
    }
  }
}
```

## PowerShell module

```powershell
dotnet build src\TripIt.PowerShell\TripIt.PowerShell.csproj -c Release
Import-Module .\src\TripIt.PowerShell\bin\Release\net10.0\TripIt.psd1

# First-time auth: tries CLI login, falls back to browser capture.
Connect-TripIt -StoreLocation UserSecrets

Get-TripItProfile
Get-TripItTrip -PageSize 5                       # upcoming; -Past, -All
Get-TripItTrip -Uuid <trip-uuid>                 # TripDetail with .Itinerary
Get-TripItItinerary                              # every upcoming trip, chronological
Search-TripItTrip VSLive | Get-TripItItinerary
Get-TripItObject -TripUuid <trip-uuid> -Type air
Get-TripItObject -Unfiled -Type note
Find-TripItReference Airport SEA

# Writes: -WhatIf / -Confirm everywhere; Remove-* is ConfirmImpact High.
$trip = New-TripItTrip 'Reykjavik, Iceland' -StartDate 2027-01-18 -EndDate 2027-01-21 -Name 'Iceland'
New-TripItPlan lodging @{ supplier_name = 'Hotel Borg'; supplier_conf_num = 'ABC123'
    StartDateTime = @{ date = '2027-01-18'; time = '15:00' }; EndDateTime = @{ date = '2027-01-21' } } -TripUuid $trip.Uuid
Get-TripItObject -TripUuid $trip.Uuid -Type lodging | Set-TripItPlan -Changes @{ room_type = 'Suite' }
Get-TripItPlanSchema air                         # writable fields per type
Move-TripItPlan note <uuid> -Unfiled
Remove-TripItPlan lodging <uuid>
Remove-TripItTrip $trip.Uuid

Get-TripItTrip -Past -All -Traveler Others        # trips I'm on but not travelling on
New-TripItTrip 'Gulu, Uganda' -StartDate 2027-06-01 -EndDate 2027-06-20 -NotTraveling
Merge-TripItTrip -SourceUuid <dup> -DestinationUuid <keep>
Get-TripItParticipant <trip-uuid>
Add-TripItParticipant <trip-uuid> -Email a@example.com -Role Editor   # sends an email
Get-TripItParticipant <trip-uuid> | ? Name -eq 'X' | Remove-TripItParticipant
Invoke-TripItRaw -Path '/api/v2/list/trip' | ConvertFrom-Json

# Push the captured session to GitHub Actions secrets for CI:
Sync-TripItSecretToGitHub -Repository MarkMichaelis/TripItEx
```

> **Requires PowerShell 7.5+** (binary module is built against `net10.0`).
> Won't load in Windows PowerShell 5.1 or PowerShell 7.4.

## Project layout

```
TripItEx.slnx                .NET solution (src + tests)
Directory.Build.props        Common build settings (net10.0, nullable, warnings-as-errors)
src/
  TripIt/                    Library: TripItClient (+ Trips / Objects / Lookup
                             sub-clients, SendAsync), TripItSession, ISessionStore
                             (UserSecrets / DPAPI), ISessionRefresher,
                             TripItAuthenticator, Json/ (lenient converters),
                             Models/ (Trip, TripDetail, ItineraryBuilder …)
  TripItMcp/                 MCP server (references TripIt)
  TripIt.PowerShell/         Binary PowerShell module (references TripIt)
tests/
  TripIt.UnitTests/          xUnit; fake HTTP handler + synthetic fixtures (no network)
  TripIt.FunctionalTests/    xUnit; SkippableFact tests hit live TripIt when a
                             session is available
  TripIt.PowerShell.Tests/   Pester 5 tests for the module
samples/har/                 Sanitized HAR captures (no secrets)
har/                         Raw HAR captures (gitignored)
scripts/
  sanitize-har.js            Node sanitizer (~0.4s for 15 MB)
  Invoke-SanitizeHar.ps1     PowerShell wrapper around the Node sanitizer
tools/
  capture-cdp.js             Playwright-CDP HAR capture (login + browse + logout)
  extract-session.js         Pulls Cookie + CSRF from a logged-in Chrome
                             (used by Connect-TripIt's browser fallback)
  smoke-test.js              Quick smoke test of the capture pipeline
.github/workflows/ci.yml     dotnet build/test + Pester; manual functional-tests job
```

## Functional tests

`dotnet test` skips backend tests by default. To run them locally, set
`TRIPIT_COOKIE` (and optionally `TRIPIT_CSRF`) or run `Connect-TripIt -StoreLocation UserSecrets`
first; the test fixture auto-loads from user-secrets.

In CI, push the secrets via `Sync-TripItSecretToGitHub` and trigger the
`Functional tests (live API)` workflow manually.

## Notes & limitations

* **Read and write surfaces are complete** for trips and all twelve plan types: list, get,
  search, create, update (merge), replace, delete, delete one leg, move — including trips
  you are not travelling on — plus trip merge and participants (invite, change role,
  remove). Not wrapped yet: documents, cost updates, traveler-profile updates.
* Account-destruction, password and payment endpoints exist but are deliberately excluded
  from the wrapper surface; see "Deliberately NOT wrapped" in `docs/api-surface.md`.
* `list/object` ignores any object-type filter server-side (type filters are applied
  client-side, per page) and rejects `page_size` above 100.
* Trips and objects are addressed by **UUID**: `get/trip/id/{id}` answers 400.
* Live write tests (`WriteSurfaceTests`) run only with `TRIPIT_ALLOW_WRITES=1`; they create
  one dated private trip, exercise every plan type, and delete it.
