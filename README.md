# Multiplex

Many services in one ASP.NET Core app, one per website or subject. Each service
is an MCP server at its own `/{name}/mcp` address, and its tools call that
website's own API. For what it's for, and the list of services, see the
[product overview](spec/product.md).

## Overview

Multiplex hosts [Model Context Protocol](https://modelcontextprotocol.io) tools
over stateless Streamable HTTP. Each tool class names the service it belongs
to, so one deployment serves several separately addressable services, and an
MCP client connects only to the ones it needs.

A service calls the website's own API: the requests the site's pages send to
its server. A browser is only for what the API can't do, such as signing in.
Services that read a site's pages (only Time, today) rent a page from one
shared headless Chromium (PuppeteerSharp).

The repository also carries the **web-api-discovery** skill, which records a
site's traffic and turns it into an API reference and, optionally, a .NET
wrapper. It's how a developer finds the API a service calls. It is
self-contained; see [`web-api-discovery/README.md`](web-api-discovery/README.md).

## Services

The list of services, with each one's address and status, is the catalog in
the [product overview](spec/product.md#services). Each service's own README
covers its tools and details. The health check is at `/healthz`.

## Quick Start

Requires the .NET 10 SDK.

```powershell
git clone https://github.com/IntelliTect-dev/Multiplex.git
cd Multiplex
git config core.hooksPath .githooks   # once per clone -- see "Working with AI coding agents"
dotnet run --project src/Multiplex.Web --launch-profile http
```

Point an MCP client at a service's address, for example
`http://localhost:5190/time/mcp`. The first call that needs a browser
downloads a Puppeteer-managed Chromium unless `Browser:ExecutablePath` is set.

## Configuration

Services that read a website's pages share one Chromium. Set these in
`appsettings.json`, or as environment variables (for example,
`Browser__Headless=false`).

| Key | Default | Description |
|---|---|---|
| `Browser:ExecutablePath` | unset | A system Chrome/Chromium to drive. Unset downloads one managed by Puppeteer on first use |
| `Browser:Headless` | `true` | `false` shows the browser window |
| `Browser:StepDelayMs` | `0` | Pause between tool steps, so a headed run can be followed by eye |

## Running it on a Linux server

`scripts/Deploy-MultiplexLinux.ps1` deploys Multiplex from this PC to a Linux
server over SSH, where it runs as two Docker containers (`deploy/linux`): the
server, with Google Chrome and a virtual display for the sign-in browsers, and
a Tailscale device named `multiplex` that serves it over HTTPS at
`https://multiplex.<your-tailnet>.ts.net`, to your tailnet only.

```powershell
./scripts/Deploy-MultiplexLinux.ps1 -Target me@192.168.1.20
```

- **The server** needs an x64 processor (Google Chrome is built for x64 only on
  Linux), Docker with the Compose plugin, and an SSH user that may run `docker`.
  It must accept this PC's SSH key; if it asks for a password, add the key once:

  ```powershell
  Get-Content $HOME\.ssh\id_ed25519.pub | ssh me@192.168.1.20 "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"
  ```

- **The first deploy** prints a Tailscale link: open it and sign in to add the
  `multiplex` device to your tailnet. The tailnet must issue HTTPS certificates
  (Tailscale admin console, DNS page).
- **The passcode** is the one this PC already keeps (`./scripts/Install-Multiplex.ps1
  -ShowPasscode`), or a new one kept the same way. Only its hash is sent.
- **Updating** is running the script again. Until the new image has built and the
  Tailscale device is up, the running server is left as it was. The server keeps
  the last three images (`docker image ls multiplex-server`); to go back to one of
  them, set `MULTIPLEX_IMAGE` in `~/multiplex/.env` on the server and run
  `docker compose up -d` in `~/multiplex`.
- **Chrome** is checked at the end of every deploy, with its sandbox on, as the
  server's own user. If it cannot start, the deploy says so: every site sign-in
  would fail.
- **Tailnet only.** Claude Code on a tailnet device can use it
  (`claude mcp add --transport http tripit https://multiplex.<your-tailnet>.ts.net/tripit/mcp`).
  Claude's web, phone and Desktop connectors and ChatGPT call from their own
  clouds and cannot reach it.
- **Data** lives in the `multiplex_multiplex-data` Docker volume, readable by the
  container's own user only. Nothing moves over from a Windows install: its keys
  and browser sign-ins are encrypted to that Windows account, so connectors are
  added and sites signed in to again.

## Adding a service

1. Find the website's API first, with the
   [web-api-discovery](web-api-discovery/README.md) skill. A service calls that
   API; the [product overview](spec/product.md) says where a browser fits.
2. Give the service its own folder, `src/Multiplex.Web/Services/<Name>/`, with
   its tests in `tests/Multiplex.Tests/Services/<Name>/`.
3. Mark its tool class with `[McpServerToolType]` and `[McpEndpoint("<name>")]`.
   The host then serves that class's tools, and only those, at `/<name>/mcp`,
   with no other registration. A tool class without `[McpEndpoint]` is not
   served at all.
4. Write a README in its folder covering its tools, their inputs and outputs,
   how it reaches the site (its API, or the browser and why), its quirks, its
   risks and its tests.
5. Add one row for it to the catalog in the [product overview](spec/product.md#services).

## Development and Testing

```powershell
dotnet build
dotnet test
```

| Variable | Effect |
|---|---|
| `MULTIPLEX_CHROMIUM` | Path to Chromium for the browser-driven tests (default `/usr/bin/chromium`). Without one they are skipped |
| `MULTIPLEX_LIVE_TESTS=1` | Also run the tests that call the real websites end to end (needs Chromium too). These are the tests that notice when a site changes its API or its pages |

The default Chromium path is Linux-only, so on Windows a bare `dotnet test`
skips the browser tests, including the page-parsing ones. Point it at Chrome:

```powershell
$env:MULTIPLEX_CHROMIUM = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
```

Each service's README describes its own tests.

## Working with AI coding agents

This repository's AI instructions -- `CLAUDE.md`, `.github/copilot-instructions.md`,
`.github/agents/`, `.github/skills/`, `.github/instructions/` -- come from
[IntelliSDLC.ai](https://github.com/IntelliTect-Samples/IntelliSDLC.ai). Claude
Code and GitHub Copilot read them automatically; there is nothing to install.

**Once per clone**, turn on the commit guard:

```powershell
git config core.hooksPath .githooks
```

It refuses commits made on `main` or from the primary checkout. Work goes on a
feature branch in a worktree, linked to an issue, and merges through a pull
request:

```powershell
git worktree add .worktrees/<issue#>-<name> -b <type>/<issue#>-<name> main
```

**Which files you may edit:**

| Edit here | Do not edit here |
|---|---|
| `.github/instructions/project.instructions.md` -- this project's architecture, conventions and glossary | `CLAUDE.md`, `.github/copilot-instructions.md` |
| `CLAUDE.project.md` -- Claude-only additions (create it if you need it) | `.github/agents/*`, `.github/skills/*` |
| `.github/skills/project-<name>/` -- skills for this repository only | the generic `.github/instructions/*.instructions.md` |
| `README.md`, `docs/`, `.gitattributes`, `run.Tests.ps1`, `.github/workflows/copilot-setup-steps.yml` | `Pull-SDLC.ai.ps1`, `Cleanup-Worktree.ps1`, `Consolidate-Specs.ps1`, `Start-IssueAgent.ps1`, `.githooks/*` |

The right-hand files are overwritten by the next sync. (`run.ps1` is synced
too, but once you edit it the sync leaves your copy alone; put local behavior
in `run.project.ps1` instead.) To change one, change it
in IntelliSDLC.ai. To pull the latest shared instructions, run
`./Pull-SDLC.ai.ps1`; it opens a sync pull request.

**Claude Code users -- optional, once per clone:** `.claude/settings.json`
enables the IntelliSDLC.ai Claude Code plugin, which makes the shared skills
invocable here as `/intellisdlc:<skill>` (e.g. `/intellisdlc:behavior-first-testing`)
and adds the IntelliSDLC.ai global rules at session start. The committed
setting alone installs nothing (Claude Code may offer to when you first trust
the folder). To install it yourself, from this repository:

```powershell
claude plugin marketplace add IntelliTect-Samples/IntelliSDLC.ai
claude plugin install intellisdlc@intellisdlc-ai --scope project
```

To update it -- `--scope project`, because that is where it is installed:

```powershell
claude plugin marketplace update intellisdlc-ai
claude plugin update intellisdlc@intellisdlc-ai --scope project
```

If you already have the IntelliSDLC.ai global install (`Install-GlobalSkills.ps1`)
on that machine, do not install or accept the plugin -- it would load the same
skills and rules twice.

## Support

File an [issue](https://github.com/IntelliTect-dev/Multiplex/issues).
