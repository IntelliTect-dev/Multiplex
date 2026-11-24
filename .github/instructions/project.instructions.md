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
signing in. One service reads pages instead: Amazon, which has no data calls for search
or product details, reads Amazon's pages over plain HTTPS with no browser. The repository also carries the self-contained
`web-api-discovery` skill under `web-api-discovery/`. The product overview,
with the catalog of services, is `spec/product.md`.

## Architecture

- `src/Multiplex.Web/Program.cs` -- composition root. Razor Pages, health check
  at `/healthz`, and the MCP server over **stateless** Streamable HTTP (no
  `Mcp-Session-Id`, so it runs behind a load balancer without sticky sessions).
- `Mcp/McpEndpointAttribute.cs`, `Mcp/McpEndpointRouting.cs` -- every distinct
  `[McpEndpoint("<slug>")]` slug is mapped to `/<slug>/mcp`, and each request's
  tool list is scoped to the tools of the endpoint it arrived on.
  `Mcp/ServiceIdentity.cs` gives each endpoint its own `serverInfo`: title,
  website and icon (`wwwroot/icons/<slug>.png`, served at `/icons/<slug>.png`
  and open without sign-in).
- `Services/<Name>/` -- one folder per service: its tool class, its README, and
  for a service that reads pages, a `*Page` class holding that site's DOM
  knowledge (URLs, selectors, parsing), e.g. `Services/Amazon/AmazonSearchPage.cs`.
  Its tests and saved pages are in `tests/Multiplex.Tests/Services/<Name>/`.
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
| HTML parsing | AngleSharp |
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
- Live tests (`[LiveHttpFact]`, or a service's own such as `[GoogleFlightsLiveFact]`)
  hit the real site over plain HTTP and run only with `MULTIPLEX_LIVE_TESTS=1`.
- `web-api-discovery/` suites run from that folder; see its README.

## Run / Debug

```bash
dotnet run --project src/Multiplex.Web --launch-profile http
```

Serves on `http://localhost:5190`; MCP endpoints at `/<slug>/mcp`.

Browser-driven tests need `MULTIPLEX_CHROMIUM` on Windows (the default path is
Linux-only), e.g. `C:\Program Files\Google\Chrome\Application\chrome.exe`.

## Key Conventions

- **Call the site's API, not its pages.** Find the API with the
  `web-api-discovery` skill first; use the browser only for what the API can't
  do (such as signing in).
- **No browser without the owner's approval.** No service uses a browser by default. Any
  browser use needs the owner's approval for that provider and that use case. The rule and
  the approvals given so far are on issue #96.
- **A site with pages but no API** is read over plain HTTP: an honest user agent, the site's
  robots.txt obeyed, every request through a `SiteGate`, and a stop at any block. The
  shared pieces are in `src/Multiplex.Web/HtmlSites/`; `Services/Amazon/` is the example.
- **New service = new folder.** A static class with `[McpServerToolType]` and
  `[McpEndpoint("<slug>")]` in `src/Multiplex.Web/Services/<Name>/`, with a
  README there. A tool class without `[McpEndpoint]` is not served. Register
  the slug in `Program.cs` with
  `RequireAuthFor("<slug>", "<Display name>", websiteUrl: "https://<site>/")`;
  startup fails for an endpoint that is not registered. Add one row for it to
  the catalog in `spec/product.md`.
- **Every service carries its website's icon** (#135). Find it as Phase 1 of
  the `web-api-discovery` skill says: what the site itself declares
  (`<link rel="icon">`, `apple-touch-icon`, the web app manifest), the largest
  square PNG, an SVG-only icon rasterized to a PNG, then fitted to a 256x256
  PNG of at most 10 KB (#138). Never draw one or take another site's. Save it
  as `src/Multiplex.Web/wwwroot/icons/<slug>.png` and write its source URL in
  the service's README under **Icon**. The service then names its title,
  website and icon in its `serverInfo` (`Mcp/ServiceIdentity.cs`), which a
  client that reads it shows instead of a placeholder; `ServiceIdentityTests`
  fails for a service without them, or with an icon ChatGPT could not take.
  **ChatGPT does not take the icon from `serverInfo`** for an existing app
  (its Refresh tools kept the placeholder, 2026-11-24). It shows the icon
  uploaded in its "Add custom MCP server" form (PNG, at most 10 KB, best at
  256x256), so upload `/icons/<slug>.png` there when adding a service. An
  existing app's icon has no edit control (openai/openai-apps-sdk-examples#232):
  to change it, delete the app and add it again, which means connecting it
  again.
- **Keep the server stateless.** No per-session state; a tool call must work on
  any instance.
- **Site knowledge lives in the `*Page` class**, not in the tool. Selectors and
  parsing there are testable against saved HTML in
  `tests/Multiplex.Tests/Services/<Name>/Fixtures/`.
- **Report "not found" with `McpException`** and a message naming the input, so
  the client sees a tool error rather than a crash.

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

- amazon.com and its image host (public, signed out, no credentials) for the Amazon service.
- Google Flights' search pages and airport lookup on `www.google.com` (public,
  no credentials, direct HTTPS) for `/google-flights/mcp`. Google can refuse a
  whole network for hours: keep the client's pacing and refusal cool-down, and run
  its live tests sparingly (`services/GoogleFlights/NOTES.md`).
- Chromium: a system install via `Browser:ExecutablePath`, else Puppeteer
  downloads one on first use (needs network).
- No secrets are configured today.

## Known Limitations / Don'ts

- Do not add sticky-session or session-scoped state to the MCP server.
- Do not make default test runs depend on a live website; mark them
  `[LiveHttpFact]` or the service's own live attribute.
