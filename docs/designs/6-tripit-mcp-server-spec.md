# TripIt MCP server: spec

Issue: #6. Status: approved decisions recorded 2026-11-17; implementation in progress on
`claude/tripit-mcp-server-spec-1ab43d`.

## 1. What we are building, and the one requirement that matters most

Multiplex serves several MCP servers from one ASP.NET Core host at `/{slug}/mcp`. This adds
TripIt at `/tripit/mcp`, built from the existing TripItEx toolkit (imported under `tripit/`
as four condensed commits).

**The defining requirement:** a person connects to the TripIt server from Claude or ChatGPT
on *any* device, including a phone, and signs in to TripIt as part of that connect step.
Afterwards the Multiplex server makes the TripIt calls on their behalf. TripIt was picked as
the first real site precisely because it requires authentication, so this design is the
template every later authenticated site follows.

Two consequences shape everything below:

1. **Claude and ChatGPT call the server from their own cloud**, even from Claude Desktop's
   chat tab (custom connectors are brokered by Anthropic's cloud; `localhost` is
   unreachable). So the server must be reachable over public HTTPS, and it must be an
   OAuth 2.1 protected resource with an authorization server, per the MCP authorization
   spec. A local stdio bridge (`mcp-remote`) is a secondary path for one machine only.
2. **The person signing in may be holding a phone**, nowhere near the machine running the
   server. TripIt has no OAuth for new developers, so the server must hold a TripIt *browser
   session*. The authorize step therefore shows TripIt's real sign-in page, running in a
   browser the server owns, mirrored live into the person's own (phone) browser. They tap
   and type into TripIt's page themselves, including any CAPTCHA or code. TripIt passwords
   never touch Multiplex storage.

## 2. Decisions (2026-11-17, owner's answers)

| Topic | Decision |
|---|---|
| Hosting | Runs on the owner's PC now, reachable over HTTPS through a tunnel; a real host later. Base URL is configuration, never derived from the request. |
| Tunnel | Tailscale Funnel preferred (stable `https://<machine>.<tailnet>.ts.net`, already installed); Cloudflare quick tunnel for overnight testing because Funnel is not yet enabled on the tailnet. |
| Who may connect | Owner passcode on the connect page (stored as a salted hash) **and** an allowlist of TripIt accounts. Checked before any browser launches. |
| TripIt sign-in UX | Live mirrored TripIt page in the connect flow. No Multiplex password form. TripIt password never stored. |
| Tools | All read and write tools; deletes, merges, participant removal and invitations (which email people) need an explicit confirm step. |
| Clients | Claude Desktop chat, claude.ai web and mobile, ChatGPT web and mobile. Claude Code also works (it reaches localhost directly). |
| History | TripItEx copied with only its significant commits (4), sample HAR captures left out. |
| Automation stance | Polite pacing and honouring throttling; no techniques meant to hide automation (fingerprint spoofing, stealth patches, CAPTCHA solving). When TripIt blocks, hand control to the person through the live page. |

## 3. Is the existing implementation a good starting point? Yes, with changes

Verified 2026-11-17:

- Builds on .NET 10 once the SDK pin may roll forward (the imported `global.json` pinned
  10.0.203 exactly and failed on this machine).
- All 107 unit tests pass.
- **Live:** the library's plain HTTP replay (cookie + `X-CSRF-Token-WA` + the SPA's
  `X-Requested-With`/`X-TRIPIT-APP-INFO` headers) reached TripIt and identified the signed-in
  account as the test account. So per-call browser fetches are not needed; the browser is
  needed only to sign in and to keep the session's short-lived bot-management cookies fresh.

An independent review (read-only, 2026-11-17) gave these verdicts:

| Part | Verdict |
|---|---|
| Library `tripit/src/TripIt` | Keep. HTTP, payload ordering, schema and lenient JSON are solid. Session layer must become per-user and immutable. |
| MCP tool classes | Keep, with fixes below. The stdio host (`Program.cs`) is replaced by Multiplex registration. |
| PowerShell module | Keep as-is for local use; the host does not reference it. |
| Node tools | Local use only; the host uses its own browser instead. |

Defects that the port fixes (C = critical for an HTTP host):

- **C1** One process-wide session and a cached "my profile ref": every caller would act as
  the first user. → per-account session provider; client created per request.
- **C2** `tripit_raw_get` can reach state-changing GETs (`v2/move/...`) and ignores read-only
  mode. → removed from the hosted server.
- **C3** An environment-variable session can never refresh, and each auth failure spawns a
  `node` process for up to 120 s. → no process refresher in the host; refresh comes from the
  server's own browser, with a cool-down.
- **C4** Cookie and CSRF are updated field by field while requests read them. → immutable
  session snapshots swapped atomically.
- Refresher discovery walks up the directory tree and runs the first
  `tools/extract-session.js` it finds through a shell. → not used by the host.
- A malformed session file is silently ignored (seen live during validation). → load
  failures are logged without values.
- Create-then-replace can fail half way and invite a duplicate trip on retry; segment
  patches can drop leg UUIDs; error text can echo up to 400 bytes of TripIt's body. → fix
  or contain in the port; sanitize errors at the tool boundary.
- The live test fixture prefers a session saved in user secrets over the one supplied, so on
  the owner's machine it would run write tests against the **real** account. → live tests
  now load only an explicitly named test session and refuse any account but the expected one.
- No handling of 429/5xx. → polite pacing and `Retry-After` (section 9).
- Weather tools describe climate averages as "forecasts". → wording fixed.

## 4. Architecture

```
Claude / ChatGPT (phone, web, desktop)            the person's own browser (phone)
          │  MCP over HTTPS + Bearer token                 │  OAuth authorize + live TripIt page
          ▼                                                ▼
   ┌───────────────────────── tunnel (Funnel / Cloudflare) ─────────────────────────┐
   │                         Multiplex.Web (this PC, later a host)                  │
   │  /.well-known/oauth-protected-resource/tripit/mcp   (resource metadata)        │
   │  /.well-known/oauth-authorization-server            (AS metadata)              │
   │  /oauth/register  /oauth/authorize  /oauth/token  /oauth/revoke                │
   │  /connect/{slug}/...   gate (passcode) → live sign-in page ↔ WebSocket mirror  │
   │  /tripit/mcp   (requires a token whose audience is this URL)                   │
   │      └─ TripIt tools ─ TripItClient (per request) ─ session snapshot for the   │
   │                                                     token's TripIt account     │
   │  Site browser manager: one Chrome profile per TripIt account (persistent),     │
   │      launched on demand, mirrored during sign-in, used to refresh cookies      │
   │  Encrypted store (ASP.NET Data Protection): clients, grants, token hashes,     │
   │      session snapshots, account list                                           │
   └────────────────────────────────────────────────────────────────────────────────┘
                                         │ HTTPS, cookie + CSRF replay, paced
                                         ▼
                                  www.tripit.com/api/v2
```

Server code lives in `src/Multiplex.Web/Servers/TripIt/` (the per-server folder convention
an in-flight branch introduces for the time server). Generic sign-in and OAuth code lives in
`src/Multiplex.Web/Auth/` and `src/Multiplex.Web/SignIn/` so the next site reuses it.

## 5. Authentication

There are two separate layers. A token for layer 1 is never sent to TripIt.

### 5.1 Layer 1: Claude/ChatGPT → Multiplex (OAuth 2.1, MCP authorization spec)

Multiplex is both the resource server and the authorization server (the C# SDK provides only
the resource-server half: the `McpAuthenticationHandler` that serves protected resource
metadata and the 401 challenge).

**Resource server**

- **Every** request to `/tripit/mcp` without a valid token, `initialize` included, gets
  `401` with
  `WWW-Authenticate: Bearer resource_metadata="<base>/.well-known/oauth-protected-resource/tripit/mcp", scope="tripit"`.
  Whole-server auth is the one shape both clients handle at connect time: Claude starts
  sign-in only on a transport-level HTTP 401 (a JSON-RPC error or `isError` result shows no
  Connect card), while ChatGPT's per-tool linking needs a different shape
  (`securitySchemes` + `_meta["mcp/www_authenticate"]`). The 401 comes from middleware ahead
  of the MCP endpoint.
- Protected resource metadata, served at **both** the path-suffixed
  `/.well-known/oauth-protected-resource/tripit/mcp` (Claude probes it first; the SDK default)
  and the root `/.well-known/oauth-protected-resource` (the only one ChatGPT documents; it
  describes the single authenticated server, or lists the first when there are several):
  `resource` = the exact public MCP URL (`<base>/tripit/mcp`, one canonical spelling, no
  trailing slash), `authorization_servers` = `[<base>]` (Claude uses only the first; ChatGPT
  compares it to `issuer` byte for byte), `scopes_supported` = `["tripit"]` (ChatGPT requests
  all advertised scopes, so keep it minimal), `bearer_methods_supported` = `["header"]`.
- Token validation: opaque token, looked up by SHA-256 hash; must be unexpired, unrevoked,
  and its audience must equal this endpoint's resource URL. The validated principal carries
  the TripIt account id used to pick the session.

**Authorization server**

- Metadata at `/.well-known/oauth-authorization-server` (also served at
  `/.well-known/openid-configuration` for clients that only try OIDC discovery): issuer =
  base URL; `authorization_endpoint`, `token_endpoint`, `registration_endpoint`,
  `revocation_endpoint`; `response_types_supported: ["code"]`;
  `grant_types_supported: ["authorization_code","refresh_token"]`;
  `code_challenge_methods_supported: ["S256"]` (clients refuse to proceed without it);
  `token_endpoint_auth_methods_supported: ["none"]`;
  `client_id_metadata_document_supported: true`.
- Client registration: **dynamic client registration** (RFC 7591; what Claude and
  `mcp-remote` use today) **and client ID metadata documents** (CIMD: an HTTPS URL as
  `client_id`; ChatGPT prefers it with `https://chatgpt.com/oauth/client.json`, and the
  2026-07-28 MCP revision deprecates DCR). CIMD documents are fetched with a short timeout,
  size cap and cache, and their `redirect_uris` are what the request is checked against.
  Redirect URIs must be HTTPS or loopback. Registrations are rate-limited and **never expire
  while in use** (ChatGPT reuses its one registration; an expired client strands the user on
  `invalid_client`).
- Issuer identification (RFC 9207): `authorization_response_iss_parameter_supported: true`,
  and every authorization response, success or error, carries `iss` equal to the issuer.
  This also gives ChatGPT its stable redirect URI.
- `/oauth/authorize`: requires `response_type=code`, a registered `redirect_uri` (exact
  match), PKCE `S256`, `state`, and the RFC 8707 `resource` parameter naming a known MCP
  endpoint. It then runs the connect flow (5.3). On success it redirects with a single-use
  code (60 s lifetime) bound to client, redirect URI, PKCE challenge, resource and account.
- `/oauth/token`: `authorization_code` (verifies PKCE) and `refresh_token` (rotation: each
  refresh token is single-use; reuse revokes the family). Access tokens live 1 hour; refresh
  tokens 30 days, but **never longer than the TripIt session they front**: when the TripIt
  session is gone, refresh returns `invalid_grant` so the client must reconnect.
- Known redirect URIs to expect: `https://claude.ai/api/mcp/auth_callback` (Claude web,
  mobile and Desktop connectors), `https://chatgpt.com/connector_platform_oauth_redirect`
  (ChatGPT with issuer identification; `https://chatgpt.com/connector/oauth/{callback_id}`
  without), and loopback ports (Claude Code, `mcp-remote`, MCP Inspector).
- Call origins, for any future network allowlist: Anthropic `160.79.104.0/21`; OpenAI
  publishes its connector ranges at `https://openai.com/chatgpt-connectors.json`.
- Deadlines that bound the implementation: Claude allows ~10 s for discovery, registration
  and token calls, 30 s for refresh, and caches discovery for ~5 minutes.

### 5.2 Layer 2: Multiplex → TripIt (browser session, server-held)

- One persistent Chrome profile per TripIt account under
  `%LOCALAPPDATA%\Multiplex\data\sites\tripit\profiles\<account-id>`, owned by the server (Chrome
  locks a profile to one process).
- After sign-in the server reads the cookie jar over CDP (never intercepted request headers,
  which lack the `httpOnly` session cookies), plus the CSRF token and the SPA's request
  headers, into an immutable **session snapshot**, stored encrypted with ASP.NET Data
  Protection.
- API calls use the existing `TripItClient` (plain HTTPS replay), created per request with
  that snapshot. Observed lifetimes: `it_session_id` ≈ 2 weeks, CSRF cookies ≈ 2 days, bot
  cookies ≈ 2 hours.
- Refresh: on an authentication failure (302, 401, or 403 with the small HTML block page) the
  server relaunches the account's profile headless, reloads `/app/trips`, re-reads the jar,
  swaps the snapshot and retries once. One refresh per account at a time, with a cool-down.
- If refresh shows the person is signed out (TripIt redirects to its login page), the
  account's grants are revoked. The next tool call gets an HTTP 401, Claude's refresh gets
  `invalid_grant`, and Claude shows **Connect** again — which runs the live sign-in.
- If TripIt shows a challenge the server cannot pass, it does not try to: it hands control to
  the person through the same live page.

### 5.3 The connect flow (what the person sees, on a phone)

1. In Claude or ChatGPT they add the connector (or tap **Connect** on a 401 card). Their
   browser opens `<base>/oauth/authorize?...`.
2. **Gate page:** "Multiplex passcode". Wrong passcode → retry with back-off; five failures
   lock the gate for 15 minutes. No browser launches before the gate passes.
3. **Live TripIt page:** the server starts a fresh, empty Chrome profile for this sign-in at
   a phone-sized viewport, opens TripIt's sign-in page, and streams it (CDP screencast frames
   over a WebSocket) into the page. Taps become pointer events; typing goes through a text
   field and is inserted into the focused element. Navigation is limited to TripIt and the
   identity providers its own sign-in page links to; anything else is blocked.
4. The server watches for the session cookies and confirms with `v2/get/profile`. It reads
   the account's email addresses and checks them against the allowlist. Not allowed → the
   temporary profile is wiped and the person sees "this TripIt account is not allowed".
5. Allowed → the temporary profile becomes the account's profile (replacing an older one),
   the session snapshot is saved, and the browser redirects back to Claude/ChatGPT with the
   authorization code. The whole flow times out after 10 minutes.

The same live page is the "hand control to the person" mechanism: any time TripIt needs a
human (re-sign-in, a challenge), the person is sent to it.

### 5.4 Gate and abuse limits

- Passcode: set by the owner, stored as PBKDF2-SHA256 (≥ 210 000 iterations) with salt in
  user secrets / the encrypted store; compared in constant time. Generated at install if
  absent, and its location (never its value) printed.
- Allowlist: `Multiplex:Sites:TripIt:AllowedAccounts` (emails), in local configuration only.
- One live browser at a time per gate session; at most two concurrent sign-in browsers.
- Every authorization, token issue, refresh failure and destructive tool call is logged with
  client id, account id and tool name, never token, cookie or request body.

## 6. Tools

Served at `/tripit/mcp` (same names as TripItEx so existing prompts keep working):

- **Read** (`readOnlyHint`): `tripit_get_upcoming_itinerary`, `tripit_list_trips`,
  `tripit_search_trips`, `tripit_get_trip`, `tripit_list_objects`, `tripit_get_object`,
  `tripit_get_profile`, `tripit_get_traveler_profile`, `tripit_list_alerts`,
  `tripit_get_purchased_products`, `tripit_lookup`, `tripit_lookup_flight`,
  `tripit_describe_plan_fields`, `tripit_session_status`.
- **Write**: `tripit_create_trip`, `tripit_update_trip`, `tripit_add_flight`,
  `tripit_add_lodging`, `tripit_add_note`, `tripit_add_plan`, `tripit_update_plan`,
  `tripit_move_plan`, `tripit_change_participant_role`.
- **Write with confirm** (`destructiveHint`): `tripit_delete_trip`, `tripit_delete_plan`,
  `tripit_delete_segment`, `tripit_merge_trips`, `tripit_remove_participant`,
  `tripit_add_participants` (sends email).
- **Removed:** `tripit_raw_get`.

**Confirm step.** The host is stateless, so it cannot ask the client mid-call (elicitation
needs a session, and Claude does not document support for it). A risky tool called without
`confirm_token` changes nothing and returns a preview ("will delete trip *X* and its 7
plans") plus a `confirm_token` valid for 5 minutes, bound to account, tool and the exact
arguments. Calling again with that token performs it. The model has to show the preview to
the person to get a meaningful second call, and both MCP hosts also prompt on
`destructiveHint` tools.

## 7. Installing into each client

### 7.1 Server on this PC (unattended)

`Install-Multiplex.ps1` (repo root, idempotent, `-WhatIf` aware):

1. Builds and publishes `Multiplex.Web` to `%LOCALAPPDATA%\Multiplex\app`.
2. Ensures configuration: public base URL, TripIt allowlist, passcode (generated if absent;
   location printed), Data Protection key folder.
3. Registers a per-user scheduled task "Multiplex" that starts the server at logon and
   restarts it on failure (no admin rights needed).
4. Optionally starts the tunnel: `tailscale funnel --bg 5190` (when Funnel is enabled), else
   reports what to enable; `-Tunnel Cloudflare` runs a quick tunnel for testing.
5. Verifies: health check, protected resource metadata, AS metadata, and an unauthenticated
   MCP call returning 401 with the right `resource_metadata` — locally and through the
   public URL.
6. Prints the connector URL `<base>/tripit/mcp` for the clients below.

### 7.2 Claude

- **claude.ai web, mobile, and Desktop chat (primary):** Settings → Connectors → Add custom
  connector → paste `<base>/tripit/mcp`. Claude registers itself, opens the authorize page,
  and the person passes the gate and signs in to TripIt. Connectors added on the web appear
  on mobile and Desktop for the same Claude account. This step is a UI action in Claude; it
  is the one part that cannot be scripted.
- **Claude Desktop, local only (secondary, scriptable):** the install script can add a stdio
  bridge entry to `%APPDATA%\Claude\claude_desktop_config.json`:
  `npx -y mcp-remote@0.16.6 http://localhost:5190/tripit/mcp --auth-timeout 600` (the
  default 30 s callback wait is too short for a human sign-in), then restart Desktop. The
  bridge opens the system browser for the same authorize page. Works only on this PC.
- **Claude Code:** `claude mcp add --transport http --scope user tripit <url>`; it performs
  the same OAuth flow with a loopback redirect and can reach `localhost` directly.

### 7.3 ChatGPT

ChatGPT (now under "Plugins"): chatgpt.com/plugins → **+** → "Add custom MCP server" → name
and `<base>/tripit/mcp` → OAuth → create. ChatGPT discovers the server, identifies itself by
its client ID metadata document (or registers dynamically), and opens the same authorize
page. ChatGPT asks the person to approve write actions unless a tool carries
`readOnlyHint`, so every tool declares `readOnlyHint`, `destructiveHint` and
`openWorldHint` explicitly. Unverified (OpenAI's help center refused automated reads): which
plans may add servers with write tools (third parties say Business/Enterprise/Edu), and
whether a server added on the web appears in the mobile apps. See section 12.

### 7.4 Testing the install without a person

- Integration tests (in-process, `WebApplicationFactory`): discovery → registration →
  authorize (gate + a fake sign-in provider) → token → `tools/list` → a read tool against a
  fake TripIt → refresh → revoke → 401. Plus negative cases: wrong PKCE, wrong resource,
  wrong redirect URI, expired code, reused refresh token, disallowed account.
- Live end to end (opt-in, owner's machine): the real server, the **test account's** already
  signed-in profile adopted as the account's session, MCP Inspector CLI or a scripted client
  doing the full OAuth flow, then real TripIt reads and a create-then-delete of a throwaway
  trip on the test account.
- Through the public URL: metadata, registration and the 401 challenge.
- Not automatable: typing a real TripIt password, and adding the connector in Claude/ChatGPT
  apps. Both are manual checks for the owner (section 11).

## 8. Hosting later

The design does not assume the PC: base URL, data folder and browser executable are
configuration. On a server the sign-in browser runs headless on the host and is mirrored the
same way. Open risk: TripIt's bot management may challenge a data-centre IP more than a home
connection; the live page lets the person clear such challenges themselves.

## 9. Throttling and politeness

- One in-flight TripIt request per account; at least 250 ms between requests, with small
  random jitter; no background polling.
- `429` and `503`: honour `Retry-After` (seconds or date, capped at 60 s), else exponential
  back-off with jitter (1 s, 2 s, 4 s), for idempotent GETs only; never auto-retry POSTs.
- A `User-Agent` and headers matching the signed-in browser (they come from the same
  profile), not an invented fingerprint.
- The same rules go into the web-api-discovery skill's capture driver and generated clients.

## 10. Insights from TripIt's old public API (v1)

TripIt no longer issues v1 keys, but its docs and live XSDs are the best field reference
(`https://api.tripit.com/xsd/tripit-api-obj-v1.xsd` comments mention v2 `trip_uuid`):

- Worth probing on v2 and exposing if honoured: `modified_since` on `list/trip` and
  `list/object` (incremental sync; deletions are not reported), v1's `type` filter on
  `list/object` (our `object_type` attempt was ignored), and `list/points_program`.
- Fields the schema says responses carry: flight `Status` (gates, delays, connection risk;
  Pro only), trip invitees with `profile_ref`, cancellation fields, check-in URLs, images.
- Field order is fixed only for `xs:sequence` types (`Object`, `ReservationObject`, trip
  purposes, invitations, points programs); trips and segments are `xs:all`. Keeping the
  current ordering is harmless.
- Enumerations for validation: airport `[A-Z]{3}`, airline code 2 chars, trip purpose `B|L`,
  flight status codes, TripIt UUID shape.
- No published rate limits; the docs ask for an identifying User-Agent and gzip.

## 11. Risks and items for the owner

- **TripIt's User Agreement (updated 2023-12-01) forbids accessing the service through
  interfaces TripIt has not provided or authorized, including scripts or bots, without
  written permission.** TripItEx already worked this way; Multiplex does too. Risk: account
  suspension. This is the owner's call, and the reason the server paces itself and never
  tries to look like a human.
- Funnel must be enabled on the tailnet (admin console: HTTPS certificates + the `funnel`
  node attribute) for a stable public URL. Until then the Cloudflare quick-tunnel URL changes
  on every start, and connectors registered against it must be re-added.
- Unverified until tried by hand: Claude's and ChatGPT's connector flow against this server
  on a phone, and a real TripIt password sign-in through the mirrored page.
- DCR is deprecated in the newest MCP revision; we support both DCR and client ID metadata
  documents.

## 12. Client specifics (to be confirmed)

| | Claude (web, mobile, Desktop connectors) | ChatGPT (web; mobile unverified) |
|---|---|---|
| Reaches localhost | No (Anthropic cloud) | No (OpenAI cloud), or OpenAI's own tunnel client |
| Client identity | DCR, or CIMD when advertised with `none` auth | CIMD preferred (`https://chatgpt.com/oauth/client.json`), DCR, or static |
| Redirect URI | `https://claude.ai/api/mcp/auth_callback` | `https://chatgpt.com/connector_platform_oauth_redirect` (with RFC 9207 `iss`) |
| Resource metadata | path-suffixed first; `resource` must equal the URL entered | root `/.well-known/oauth-protected-resource` or the `WWW-Authenticate` pointer |
| Sign-in trigger | HTTP 401 with `WWW-Authenticate` (inline Connect card; retries the call) | connect-time OAuth; per-tool needs `securitySchemes` + `_meta["mcp/www_authenticate"]` |
| Timeouts | ~10 s discovery/registration/token, 30 s refresh, 240 s per tool call | not documented |
| Write confirmation | prompts on tools per its own policy | prompts unless `readOnlyHint` |

Sources: claude.com/docs/connectors/building (authentication, lazy-authentication,
testing), modelcontextprotocol.io/specification/2026-07-28, developers.openai.com
(apps-sdk/build/auth, plugins/build/auth, api/docs/guides/developer-mode), all read
2026-11-17.

## 13. Work plan

1. Spec (this document) and the condensed import.
2. Guard live tests to the test account; fix the SDK pin.
3. OAuth authorization server + resource protection, generic and slug-aware.
4. Site sign-in: browser manager, live mirror page, gate, allowlist, session snapshots.
5. TripIt tools at `/tripit/mcp` per account, confirm tokens, sanitized errors, pacing.
6. Install script, integration tests, live end-to-end test on the test account, public-URL
   check.
7. Throttling handling in the web-api-discovery skill (separate change).

## 14. As built and verified (2026-11-17)

Everything in section 13 except the public-URL check is built on this branch.

**Verified against real TripIt with the dedicated test account**, through the real server
(published build, Production settings) and `scripts/Test-MultiplexConnect.ps1 -WriteCheck`:
the 401 challenge and both discovery documents; dynamic registration; authorize through the
owner passcode and **Continue as** the held account (which re-checks the live TripIt session);
token exchange with PKCE; `initialize`; `tools/list` (29 tools); `tripit_get_profile`
returning the test account; creating a throwaway trip, a delete that only previews, a confirm
token refused for other arguments, the confirmed delete; refresh-token rotation and refusal
of the spent token. The imported library's own live suite also passed on that account (25
passed, 1 skipped), including every write test.

**Session renewal verified past TripIt's two-hour cookie lifetime.** After a connect, the
server sat idle for 135 minutes with no TripIt traffic. The next tool call got 403 from
TripIt (its bot-manager cookies had expired); the server renewed the session from its own
visible browser profile without the owner, retried, and `tripit_get_profile` returned the
test account (about 14 s including the renewal). `tripit_list_trips` followed in under 3 s.
The Claude-side grant was renewed with its refresh token in the same run.

**Installer verified** on this PC into a scratch folder: publish, configuration and passcode,
per-user logon task (no administrator rights needed), start, health check, then a connect
through the installed server both by `Test-MultiplexConnect.ps1` and by an independent client
built on the official MCP C# SDK (`scripts/McpSdkConnect.cs`, its own OAuth handling).

**Verified with a stub sign-in site** (automated tests, real Chrome): the live mirrored
sign-in end to end from a simulated touch phone (passcode, taps, typing, redirect back with a
code), allowlist rejection, gate lockout, the navigation lock, timeouts and the refresher's
signed-out path.

**Found while building, now part of the design:**

- TripIt answers headless Chrome with "Access Denied" on its app pages. TripIt's server
  browser therefore runs as a normal visible window by default
  (`Multiplex:Sites:tripit:Headless=false`); nothing disguises it. A session refresh briefly
  opens that window on the PC.
- TripIt's only third-party sign-in is Google; allowed navigation hosts default to
  `www.tripit.com`, `tripit.com`, `accounts.google.com`.
- Allowlist matching uses only email addresses TripIt marks confirmed.
- "Keep me signed in" must be ticked during the mirrored sign-in, or the session ends when
  the browser closes; the page says so.
- **Continue as** lets a reconnect (an expired Claude/ChatGPT grant) skip retyping the TripIt
  password while the server's TripIt session is still valid; it is behind the passcode.
- The server listens on `localhost` (both 127.0.0.1 and ::1); listening only on 127.0.0.1
  cost every `localhost` request about 2 s, against Claude's ~10 s discovery limit.
- Loopback redirect URIs match on any port (RFC 8252): Claude Code and `mcp-remote` register
  once and pick a free port each sign-in.
- One app-wide Data Protection key ring, keys encrypted with DPAPI.
- Behind a local tunnel every request arrives from loopback, so lockouts and rate limits key
  on the tunnel's last `X-Forwarded-For` entry, and MCP endpoints without sign-in (e.g.
  `/time/mcp`, which drives a browser on this PC) answer 404 to any request from outside the
  PC unless listed in `Multiplex:PublicOpenEndpoints`.
- The passcode page names where the browser goes afterwards (the client's redirect host), so
  the owner can spot a connect request that did not come from their own Claude or ChatGPT.

**Not verified yet (needs the owner):** the internet-facing address (Tailscale Funnel not yet
enabled; the Cloudflare tool's install is waiting on an administrator prompt); adding the
connector in Claude and ChatGPT, on the web and on a phone; a real TripIt password sign-in
through the mirrored page on a phone (including Google sign-in and CAPTCHAs); ChatGPT plan
limits for write tools.
