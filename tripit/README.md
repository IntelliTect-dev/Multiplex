# TripItEx

A .NET 10 toolkit for the TripIt web SPA backend (`https://www.tripit.com/api/v2/*`).
The repository ships three projects on top of a shared HTTP/auth library:

| Project | Path | Description |
|---|---|---|
| `TripIt` | `src/TripIt/` | Class library: `TripItClient`, typed DTOs, session stores, `Connect-TripIt` auth flow |
| `TripItMcp` | `src/TripItMcp/` | [Model Context Protocol](https://modelcontextprotocol.io/) stdio server exposing the library to LLM clients |
| `TripIt.PowerShell` | `src/TripIt.PowerShell/` | Binary PowerShell module with `Get-TripItProfile`, `Get-TripItTrip`, `Connect-TripIt`, etc. |

Endpoints were derived from a HAR capture of the live site (sanitized samples in
`samples/har/`). Only endpoints that returned 200 / `application/json` are exposed as typed
methods; a `SendRawAsync` / `Invoke-TripItRaw` / `tripit_raw_get` escape hatch covers
everything else.

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

Run the helper (uses the existing `.chrome-cdp-profile` from `tools/capture-cdp.js`):

```powershell
node tools/extract-session.js
```

It opens (or attaches to) a Chrome instance signed in to TripIt, intercepts an
`/api/v2/*` request fired by the SPA, and writes the headers to `~/.tripit-mcp/session.json`.
Re-run it any time the server reports a 302/401.

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

* Only **GET** endpoints used by the SPA are exposed. Mutating endpoints (create trip,
  add segment, etc.) were not part of the captured surface and are intentionally not
  wrapped here.
* Session cookies expire / rotate; the helper extractor must be re-run periodically.
* `/web/v2/*` endpoints are listed in the catalogue but returned 404 for direct probes,
  so they are NOT wrapped — the SPA appears to use only `/api/v2/*`. `tripit_raw_get`
  remains available if a path is found later.
