# TripIt service (`/tripit/mcp`)

The TripIt service, the MCP server Multiplex hosts for TripIt, lets Claude or ChatGPT read and change a person's TripIt trips: trips,
every plan type (flights, lodging, cars, rail, transfers, cruises, restaurants, activities,
notes, parking, maps, directions), participants and lookups. Each signed-in TripIt account
gets its own data; nothing is shared between accounts.

Design: [`docs/designs/6-tripit-mcp-server-spec.md`](../../../../docs/designs/6-tripit-mcp-server-spec.md)
(sections 5.2, 6 and 9). The TripIt client library is [`tripit/src/TripIt`](../../../../tripit/src/TripIt);
its [`CLAUDE.md`](../../../../tripit/CLAUDE.md) holds the hard-won API knowledge.

## How a request reaches TripIt

1. **Auth/** validates the bearer token on every `/tripit/mcp` request and puts the account on
   `HttpContext.User` as `SiteClaims` (`multiplex:site` = `tripit`, `multiplex:account`).
   Without it the request never reaches a tool.
2. `TripItToolContext` (scoped, one per request) reads the account, loads its session snapshot
   from `ISiteSessionStore` (`{ cookie, csrf_token, spa_headers, captured_at }`, encrypted by
   **SignIn/**), and builds a `TripItClient` for that account only. The client sends the
   snapshot's cookie, CSRF token and browser headers, through the named `HttpClient` "TripIt":
   no cookie jar, no automatic redirects, and the pacing handler.
3. When TripIt rejects the session (302, 401, or 403 with the small Akamai block page),
   `TripItAccountSessions` re-reads the store (another request may have refreshed already),
   else asks `ISiteSessionRefresher` for a new snapshot, at most once per account per
   cool-down (30 s) and one refresh per account at a time. The TripIt request is retried once,
   and only if the cookie actually changed. A retry happens per TripIt request, never by
   re-running a tool, so a write that already succeeded is not repeated.
4. If the refresher returns `null`, the person is signed out of TripIt. It has already revoked
   the account's grants, so the client's next request gets HTTP 401 and shows **Connect**; the
   tool answers: *"Your TripIt session has ended … Reconnect the TripIt connector."*

## Tools

Same names as the TripItEx stdio server. Every tool declares `readOnlyHint`,
`destructiveHint`, `idempotentHint` and `openWorldHint` (ChatGPT asks before calling any tool
without `readOnlyHint`). Parameter names are camelCase (`tripUuid`, `confirmName`), except `confirm_token` (spec section 6).

| Tool | Kind | Notes |
|---|---|---|
| `tripit_get_upcoming_itinerary` | read | Every upcoming trip with its itinerary |
| `tripit_list_trips`, `tripit_search_trips` | read | `traveler` = all / mine / others |
| `tripit_get_trip`, `tripit_list_objects`, `tripit_get_object` | read | `includeWeather` adds climate averages, not forecasts |
| `tripit_get_profile`, `tripit_get_traveler_profile` | read | Document numbers masked unless `revealNumbers` |
| `tripit_list_alerts`, `tripit_get_purchased_products` | read | |
| `tripit_lookup`, `tripit_lookup_flight`, `tripit_describe_plan_fields` | read | Reference data; the last needs no TripIt call |
| `tripit_session_status` | read | Never returns cookie values |
| `tripit_create_trip`, `tripit_update_trip` | write | A half-finished create names the created trip, so a retry updates it |
| `tripit_add_flight`, `tripit_add_lodging`, `tripit_add_note`, `tripit_add_plan` | write | Dates `yyyy-MM-dd` only; assumptions (e.g. arrival date) are listed in the result |
| `tripit_update_plan`, `tripit_move_plan` | write | A `Segment` array must name every existing leg |
| `tripit_change_participant_role` | write | |
| `tripit_delete_trip`, `tripit_delete_plan`, `tripit_delete_segment` | write, destructive, confirm | A leg is deleted only with its plan's `type` and `objectUuid`, and only while it still belongs to that plan |
| `tripit_merge_trips`, `tripit_remove_participant` | write, destructive, confirm | |
| `tripit_add_participants` | write, destructive, open-world, confirm | TripIt emails each address |

`tripit_raw_get` is **not** served here: some TripIt GETs change data (`v2/move`).

## Confirm step

The host is stateless, so it cannot ask the client mid-call. A confirm tool called without
`confirm_token` changes nothing: it reads what it needs and returns a preview, for example
*"Will permanently delete trip 'Iceland' (2027-01-18–2027-01-21) and its 7 plans."*, plus a
`confirm_token`. The model shows the preview to the person; only if they agree does it call
again with the same arguments and the token. The existing guards stay (`confirmName` must be
the trip's exact name, the owner cannot be removed).

The token is an ASP.NET Data Protection time-limited payload (5 minutes) carrying a random id
and binding the account, the tool name and a SHA-256 of the canonical JSON of the other
arguments (keys sorted, values after defaults). Another account, another tool, other arguments
or an expired token are refused with a message telling the model to preview again and ask the
person. A token works once: its id is recorded, per account and in memory until the token
expires, before the change is attempted, so a replay (which for `tripit_add_participants`
would email the invitations again) is refused. A token refused for other arguments is not used
up. The record does not survive a restart of the server.

A change TripIt answers with 302/401 (session expired or rotated) is not sent again after the
session is renewed, because TripIt may already have made it; the tool error says to check
before trying again. Only Akamai's 403 block page, which stops the request before TripIt sees
it, is retried.

## Pacing and errors

- One TripIt request in flight per account, at least 250 ms between requests plus a little
  random jitter (polite pacing, not an attempt to look human). `429`/`503` on GETs honour
  `Retry-After` (seconds or date; one longer than 60 s is not waited out) or back off 1, 2,
  4 s with jitter; POSTs are never retried. Configure under `Multiplex:Sites:TripIt`
  (`MinRequestSpacingMs`, `RequestJitterMs`, `RefreshCooldownSeconds`).
- Tool errors are short: HTTP status plus TripIt's validation message when it is short plain
  text. Response bodies and session values never reach the client; details are logged at
  Debug. Destructive calls are logged with account, client id and tool name.

## Registration

`builder.Services.AddTripItService(builder.Configuration)` in `Program.cs`. It registers Data
Protection with the shared Multiplex settings (application name `Multiplex`, keys under
`Multiplex:DataPath`, default `%LOCALAPPDATA%\Multiplex\data\keys`), the pacing state, the
"TripIt" `HttpClient` and the per-request context. `ISiteSessionStore` and
`ISiteSessionRefresher` come from SignIn/ and are resolved per request, so Multiplex starts
(and reports "sign-in is not set up") without them.

## Tests

- `tests/Multiplex.Tests/Services/TripIt/TripItServiceTests.cs`: real MCP JSON-RPC over HTTP
  against a fake TripIt, with a test authentication scheme, in-memory store and refresher:
  tool list and hints, per-account sessions, refresh on 403, sign-out, confirm flow, errors.
- `PacingHandlerTests.cs`: the pacing handler on a fake clock.
- `TripItHostLiveTests.cs` (opt-in): the hosted service against real TripIt as the dedicated
  **test** account:

  ```powershell
  $env:TRIPIT_TEST_SESSION_FILE = 'C:\path\to\test-session.json'   # snapshot of the TEST account
  $env:TRIPIT_TEST_ACCOUNT      = '<test account email>'            # required with the file
  $env:TRIPIT_ALLOW_WRITES      = '1'                               # for the create/delete test
  dotnet test tests/Multiplex.Tests --filter TripItHostLiveTests
  ```

  The first call checks that the profile's email addresses include `TRIPIT_TEST_ACCOUNT` and
  fails before anything else when they do not. The write test creates one private, dated trip
  and deletes it through the confirm step. The library's own live suite is
  `tripit/tests/TripIt.FunctionalTests` with the same variables. Session snapshots age out in
  about two hours (Akamai cookies): a 403 with a small HTML page means capture a new one.

## Icon

`wwwroot/icons/tripit.png` (256x256, at most 10 KB), TripIt's own touch icon, from `https://www.tripit.com/apple-touch-icon.png`. It is fitted to 256x256 and at most 10 KB, the one file ChatGPT's "Add custom MCP server" form takes as an app's icon (#138). Clients that read `serverInfo` show it beside the service, with its title and website (#135). ChatGPT shows the icon uploaded when its app is added, so upload this file in that form; it cannot be changed on an existing app. To refresh it, fetch the same URL again and fit it the same way (Phase 1 of the `web-api-discovery` skill).
