# Multiplex

Many small MCP servers in one ASP.NET Core app -- each at its own
`/{name}/mcp` endpoint -- with tools that drive a shared headless browser to
read websites that have no API.

## Overview

Multiplex hosts [Model Context Protocol](https://modelcontextprotocol.io) tools
over stateless Streamable HTTP. Each tool class names the endpoint it belongs
to, so one deployment serves several separately addressable MCP servers, and an
MCP client connects only to the ones it needs. Tools that read a website rent a
page from one shared headless Chromium (PuppeteerSharp).

The repository also carries the **web-api-discovery** skill, which records a
site's traffic and turns it into an API reference and, optionally, a .NET
wrapper. It is self-contained; see [`web-api-discovery/README.md`](web-api-discovery/README.md).

## Endpoints

| Endpoint | Tool | What it does |
|---|---|---|
| `/time/mcp` | `get_local_time` | Current local time and UTC offset for a city or country, read from time.now |
| `/healthz` | -- | Health check |

## Quick Start

Requires the .NET 10 SDK.

```powershell
git clone https://github.com/IntelliTect-dev/Multiplex.git
cd Multiplex
git config core.hooksPath .githooks   # once per clone -- see "Working with AI coding agents"
dotnet run --project src/Multiplex.Web --launch-profile http
```

Point an MCP client at `http://localhost:5190/time/mcp`. The first tool call
downloads a Puppeteer-managed Chromium unless `Browser:ExecutablePath` is set.

## Configuration

| Key | Default | Description |
|---|---|---|
| `Browser:ExecutablePath` | unset | A system Chrome/Chromium to drive. Unset downloads one managed by Puppeteer on first use |
| `Browser:Headless` | `true` | `false` shows the browser window |
| `Browser:StepDelayMs` | `0` | Pause between tool steps, so a headed run can be followed by eye |

## Adding an MCP server

Add a static class under `src/Multiplex.Web/Mcp/Tools/` marked
`[McpServerToolType]` and `[McpEndpoint("<name>")]`. It is served at
`/<name>/mcp` with no other registration. A tool class without
`[McpEndpoint]` is not served at all.

## Development and Testing

```powershell
dotnet build
dotnet test
```

| Variable | Effect |
|---|---|
| `MULTIPLEX_CHROMIUM` | Path to Chromium for the browser-driven tests (default `/usr/bin/chromium`). Without one they are skipped |
| `MULTIPLEX_LIVE_TESTS=1` | Also run the tests that hit the real time.now site (needs Chromium too) |

## Working with AI coding agents

This repository's AI instructions -- `CLAUDE.md`, `.github/copilot-instructions.md`,
`.github/agents/`, `.github/skills/`, `.github/instructions/` -- come from
[IntelliSDLC.ai](https://github.com/IntelliTect-Samples/IntelliSDLC.ai). Claude
Code, GitHub Copilot, Codex and Gemini read them automatically; there is
nothing to install.

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
| `README.md`, `docs/` | `Pull-SDLC.ai.ps1`, `Cleanup-Worktree.ps1`, `run.ps1` and the other synced scripts |

The right-hand files are overwritten by the next sync. To change one, change it
in IntelliSDLC.ai. To pull the latest shared instructions, run
`./Pull-SDLC.ai.ps1`; it opens a sync pull request.

## Support

File an [issue](https://github.com/IntelliTect-dev/Multiplex/issues).
