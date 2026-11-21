---
description: 'Project-specific instructions and conventions. Edit this file in the consuming project; it is never overwritten by Pull-SDLC.ai.ps1.'
applyTo: '**/*'
---

# Project Instructions

## Project Overview

Multiplex is an ASP.NET Core web app that hosts several **services**, one per
website, each a Model Context Protocol (MCP) server at its own `/{slug}/mcp`
endpoint. A service calls its website's own (undocumented) API, found with the
`web-api-discovery` skill; a browser is only for what the API can't do, such as
signing in. Time is the exception today: it reads time.now's pages in a shared
headless Chromium. The repository also carries the self-contained
`web-api-discovery` skill under `web-api-discovery/`. The product overview,
with the catalog of services, is `spec/product.md`.

## Architecture

- `src/Multiplex.Web/Program.cs` -- composition root. Razor Pages, health check
  at `/healthz`, and the MCP server over **stateless** Streamable HTTP (no
  `Mcp-Session-Id`, so it runs behind a load balancer without sticky sessions).
- `Mcp/McpEndpointAttribute.cs`, `Mcp/McpEndpointRouting.cs` -- every distinct
  `[McpEndpoint("<slug>")]` slug is mapped to `/<slug>/mcp`, and each request's
  tool list is scoped to the tools of the endpoint it arrived on.
- `Services/<Name>/` -- one folder per service: its tool class, its README, and
  for a service that reads pages, a `*Page` class holding that site's DOM
  knowledge (URLs, selectors, parsing), e.g. `Services/Time/TimeNowPage.cs`.
  Its tests and saved pages are in `tests/Multiplex.Tests/Services/<Name>/`.
- `Browser/BrowserService.cs` -- one shared browser, launched lazily; tools rent
  and return pages.
- `services/<Name>/` -- one folder per provider (the owner's decision in #83):
  its client library, PowerShell module, scrubbed references, protocol docs and
  tests. Google Flights (`services/GoogleFlights/`) is the first; its MCP tool
  class stays in `src/Multiplex.Web/Services/GoogleFlights/` until #83's
  host-contracts library lets the host find tools in provider assemblies.
- `tests/Multiplex.Tests/` -- xUnit: `WebApplicationFactory<Program>`
  integration tests, HTML-fixture parsing tests, and opt-in browser and live
  tests.
- `web-api-discovery/` -- PowerShell + Node skill with its own Pester suites and
  its own CI workflow (`.github/workflows/web-api-discovery.yml`), which runs
  from that folder.

## Tech Stack

| Layer | Technology |
|---|---|
| Language/Runtime | C# / .NET 10 |
| Web framework | ASP.NET Core, Razor Pages |
| MCP | `ModelContextProtocol.AspNetCore` |
| Browser automation | PuppeteerSharp (Chromium) |
| Testing | xUnit, `Microsoft.AspNetCore.Mvc.Testing` |
| Skill tooling | PowerShell 7 + Pester, Node.js 20 (`web-api-discovery/`) |

## Build, Test, Format

```bash
dotnet build
dotnet test
dotnet format
```

- Browser-driven tests (`[ChromiumFact]`) need Chromium: set
  `MULTIPLEX_CHROMIUM` to its path (default `/usr/bin/chromium`). Without it they
  are skipped.
- Live tests (`[LiveFact]`, `[LiveTheory]`) hit the real site and run only with
  `MULTIPLEX_LIVE_TESTS=1` (and Chromium).
- `web-api-discovery/` suites run from that folder; see its README.

## Run / Debug

```bash
dotnet run --project src/Multiplex.Web --launch-profile http
```

Serves on `http://localhost:5190`; MCP endpoints at `/<slug>/mcp`. To watch a
tool drive the browser, set `Browser:Headless` to `false` and
`Browser:StepDelayMs` to e.g. `500` -- from a shell, as environment variables
(`Browser__Headless=false`, `Browser__StepDelayMs=500`) or in
`appsettings.Development.json`.

Browser-driven tests need `MULTIPLEX_CHROMIUM` on Windows (the default path is
Linux-only), e.g. `C:\Program Files\Google\Chrome\Application\chrome.exe`.

## Key Conventions

- **Call the site's API, not its pages.** Find the API with the
  `web-api-discovery` skill first; use the browser only for what the API can't
  do (such as signing in).
- **New service = new folder.** A static class with `[McpServerToolType]` and
  `[McpEndpoint("<slug>")]` in `src/Multiplex.Web/Services/<Name>/`, with a
  README there. No other registration. A tool class without `[McpEndpoint]` is
  not served. Add one row for it to the catalog in `spec/product.md`.
- **Keep the server stateless.** No per-session state; a tool call must work on
  any instance.
- **Rent pages, never close them.** Use `BrowserService.RentPageAsync` /
  `ReturnPageAsync` in a `try`/`finally`. Closing the last tab of a headed
  window closes the window and breaks the next call.
- **Site knowledge lives in the `*Page` class**, not in the tool. Selectors and
  parsing there are testable against saved HTML in
  `tests/Multiplex.Tests/Services/<Name>/Fixtures/`.
- **Report "not found" with `McpException`** and a message naming the input, so
  the client sees a tool error rather than a crash.
- Call `BrowserService.PauseAsync` between visible steps so `StepDelayMs` works.

## Domain Glossary

- **Service** -- one MCP server that Multiplex hosts for one website, in its own
  `Services/<Name>/` folder: a server to the AI agent, a client of the website.
  Say "service", not "server", for these; keep "MCP server" for the protocol
  role.
- **Endpoint / slug** -- the `<slug>` in `/<slug>/mcp`; one service's address.
- **Tool** -- an MCP tool method (`[McpServerTool]`) on a tool class.
- **Page class** -- the `*Page` static class that knows one website's DOM.
- **Live test** -- a test that hits the real website; opt-in only.

## External Dependencies & Secrets

- time.now (public, no credentials) for `get_local_time`.
- Google Flights' own web endpoints on `www.google.com` (public, no
  credentials, direct HTTP) for `/google-flights/mcp`. Google throttles a whole
  network for hours: keep the client's pacing and refusal cool-down, and run its
  live tests sparingly (`services/GoogleFlights/NOTES.md`).
- Chromium: a system install via `Browser:ExecutablePath`, else Puppeteer
  downloads one on first use (needs network).
- No secrets are configured today.

## Known Limitations / Don'ts

- Do not add sticky-session or session-scoped state to the MCP server.
- Do not make default test runs depend on a live website; mark them
  `[LiveFact]`/`[LiveTheory]`.
