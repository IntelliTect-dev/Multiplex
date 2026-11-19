# Multiplex: Product Overview

*Draft, November 17, 2026.*

## Purpose

Multiplex gives AI agents tools for websites that have no public API. One web app hosts many services (hence the name), one per website or subject. A service is an MCP server for one website, and it lists only its own tools.

A service calls the website's own API: the same requests the site's pages send to its server. The site doesn't document that API, so a developer first finds it with the web-api-discovery skill. In other words, a service is a server to the AI agent but a client of the website.

A service uses a browser only for what the API can't do. For a site that needs signing in (TripIt is the first), the person signs in on the site's own page, shown live from a browser that Multiplex runs, so the site's password never reaches Multiplex. The browser then keeps that session's cookies fresh, and the tools' calls go to the API. A service may also read a site's pages with the browser if the site truly has no usable API, but that isn't expected in practice, since most sites' pages get their data from an API behind them. (The Time service is the exception today: it still reads time.now's pages in the browser. See the known gaps.)

The requirement that matters most: a person connects a service from Claude or ChatGPT on any device, including a phone, and signs in to the website as part of connecting.

## Who uses it

- **People using Claude or ChatGPT** connect a service and sign in to its website, from whatever device they're holding.
- **AI agents and other MCP clients** call the tools.
- **Developers** add a service for each new website, finding its API first with the web-api-discovery skill.

## The two parts

**The service host.** One ASP.NET Core web app that serves every service at its own address. Setup, settings and tests are in the [README](../README.md).

**The web-api-discovery skill.** An AI agent skill (also usable as PowerShell commands) that records a website session, scrubs it of credentials and personal data, and turns the traffic into a reviewable API reference. Optionally, it generates a .NET wrapper with its own MCP server. In other words, it's how a developer finds the API a service calls. Its documentation lives in its own folder, [web-api-discovery](../web-api-discovery/README.md).

## Services

Each service lives in its own folder, with its code, its tests and a README. The README holds everything specific to that service: its tools, their inputs and outputs, how it reaches the site (its API, or the browser and why), its quirks, its risks and its tests. This document lists each service in one row and nothing more. A service that grows large may move to a repository of its own, and its row then links there.

| Service | Address | What it does | Status | Details |
|---|---|---|---|---|
| Time | `/time/mcp` | Current local time, time zone and UTC offset for a city or country, read from time.now's pages | Working | [README](../src/Multiplex.Web/Services/Time/README.md) |
| TripIt | `/tripit/mcp` | Reads and changes the signed-in person's TripIt trips (deletes and other risky changes need a confirm step) | In progress | [Issue #6](https://github.com/IntelliTect-dev/Multiplex/issues/6) |
| LinkedIn | `/linkedin/mcp` | Handles the signed-in person's LinkedIn invitations and messages, one person at a time, every change confirmed first. Uses LinkedIn's internal API, which LinkedIn's rules forbid | In progress | [README](../src/Multiplex.Web/Services/LinkedIn/README.md), [issue #86](https://github.com/IntelliTect-dev/Multiplex/issues/86) |

## What every service shares

- **Its own address.** Each service is served at `/{name}/mcp` and lists only its own tools. A tool that isn't assigned to a service isn't served at all, and there's no catch-all `/mcp`.
- **A stateless transport.** Streamable HTTP with no MCP session ID, so the host can run behind a load balancer without sticky sessions.
- **A shared browser for reading pages.** Services that read a site's pages share one Chromium, launched on the first call. Each call gets its own tab.
- **A health check** at `/healthz`.

## Where it runs

On the owner's PC for now, reachable from the internet over HTTPS through a tunnel, and on a real host later. It has to be reachable from the internet because Claude and ChatGPT call it from their own clouds, even when the person is at the same PC. (Decided with the TripIt work, issue #6.)

## Constraints and risks

- **No authentication yet.** Anyone who can reach the host can call every service's tools. The TripIt work adds it: MCP clients sign in through OAuth. The owner passcode only gates reaching the sign-in: every connection proves its site account by signing in to the site live, and uses only that account. No account is offered from what the host already holds (#52).
- **No limit on concurrent calls.** For a service that reads pages, each call that arrives while the others are busy opens another tab.
- **Chromium runs with its sandbox turned off** (`--no-sandbox`).
- **Websites change without notice.** An undocumented API can change or disappear, and page layouts tend to change more often than the API behind them (one reason services call the API). Each service's live tests are what notice.

## Known gaps

- Only one service works so far, and it's the exception: Time reads time.now's pages instead of calling an API. Nobody has checked yet whether time.now has an API it could call.

## Open questions

- Which sites get services next?
- How large does a service get before it moves to its own repository?
