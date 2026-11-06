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

Only endpoints that returned 200 / `application/json` are exposed as typed methods; a
`SendRawAsync` / `Invoke-TripItRaw` / `tripit_raw_get` escape hatch covers everything else.

The available tools were derived from a HAR capture of the live site (see
`tools/capture-cdp.js` and the gitignored `har/` directory; sanitized copies live in
`samples/har/`). Only endpoints that returned 200 / `application/json` for the captured
session
are exposed as first-class tools; a generic `tripit_raw_get` escape hatch covers anything
else.

## Tools

| Tool                            | Endpoint                              | Purpose |
|---------------------------------|---------------------------------------|---------|
| `tripit_list_trips`             | `GET /api/v2/list/trip`               | Upcoming/past trips with filters |
| `tripit_get_profile`            | `GET /api/v2/get/profile`             | Signed-in user profile |
| `tripit_get_traveler_profile`   | `GET /api/v2/travelerProfile/get`     | Passport, KTN, frequent-flyer numbers, prefs |
| `tripit_list_pro_alerts`        | `GET /api/v2/listProAlerts`           | TripIt Pro alerts (price drops, fare watches…) |
| `tripit_get_purchased_products` | `GET /api/v2/purchasedProductInfo`    | Pro subscription / entitlements |
| `tripit_get_app_config`         | `GET /api/v2/appConfig`               | Web SPA feature flags |
| `tripit_get_gtm_data`           | `GET /api/v2/gtmDataAsJson`           | Analytics dataLayer (user_id, member_status…) |
| `tripit_session_status`         | (local)                               | Reports whether a session is configured |
| `tripit_raw_get`                | `GET /<arbitrary path>`               | Authenticated escape hatch |

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

Re-run it any time the client reports a 302/401. Observed expiries: `it_wa_csrf` ~2 days,
the Akamai bot cookies ~2 hours.

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
Get-TripItTrip -PageSize 5
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
  TripIt/                    Library: TripItClient (typed methods + SendRawAsync),
                             TripItSession, ISessionStore (UserSecrets / DPAPI),
                             TripItAuthenticator (CLI + browser-fallback), Models/
  TripItMcp/                 MCP server (references TripIt)
  TripIt.PowerShell/         Binary PowerShell module (references TripIt)
tests/
  TripIt.FunctionalTests/    xUnit; SkippableFact tests hit live TripIt when
                             TRIPIT_COOKIE is set
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

* Only **GET** endpoints are exposed *so far*. The mutating surface is now known and
  documented in [`docs/api-surface.md`](docs/api-surface.md) — `v2/create`,
  `v2/create/trip`, `v2/replace/trip/uuid/{uuid}`, `v2/replacePlan/{type}/uuid/{uuid}`,
  `v2/delete/{type}/uuid/{uuid}`, `v2/move/`, `v2/merge/trip` and more — but it is **not
  wrapped yet**, because the request *payloads* are built dynamically by the SPA and are
  not statically resolvable. They need a capture of the SPA performing each mutation
  before they can be typed honestly.
* Account-destruction, password and payment endpoints exist but are deliberately excluded
  from the wrapper surface; see "Deliberately NOT wrapped" in `docs/api-surface.md`.
* Session cookies expire / rotate; the helper extractor must be re-run periodically.
* `/web/v2/*` endpoints are listed in the catalogue but returned 404 for direct probes,
  so they are NOT wrapped — the SPA appears to use only `/api/v2/*`. `tripit_raw_get`
  remains available if a path is found later.
