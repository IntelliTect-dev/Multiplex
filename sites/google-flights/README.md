# Google Flights

Flight search for Multiplex, built from scratch against the endpoints Google
Flights' own web page calls (`www.google.com/travel/flights`). No third-party
flight API or scraping service is involved.

- Issue: https://github.com/IntelliTect-dev/Multiplex/issues/89
- Layout: the per-site design in #4 (`sites/<site>/`).

## Status

**Phase 1, discovery.** The endpoints and payload field maps are being
captured and decoded with the web-api-discovery skill. The client library, MCP
server and PowerShell module follow once the findings are reviewed.

## What is here

| Path | What it is |
|---|---|
| `NOTES.md` | Decisions and traps: read before changing anything |
| `docs/` | The decoded protocol: endpoints, framing and the field maps |
| `references/` | Trimmed, scrubbed HAR extracts and their catalogue (evidence, not the spec) |
| `scripts/` | The re-capture recipe: the flows driven to produce the references |

## Not affiliated with Google

These endpoints are undocumented, may change without notice, and Google
rate-limits them aggressively. See `NOTES.md` for the request budget.
