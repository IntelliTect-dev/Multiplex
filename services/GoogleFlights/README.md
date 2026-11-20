# Google Flights

Flight search for Multiplex, built from scratch against the endpoints Google
Flights' own web page calls (`www.google.com/travel/flights`). No third-party
flight API or scraping service is involved.

- Issue: https://github.com/IntelliTect-dev/Multiplex/issues/89
- Layout: one folder per provider, `services/<Name>/` (the owner's decision in
  #83, which refines #4's per-site outputs).

## Status

**Phase 1, discovery: complete.** Every flow in the spec is recorded and its
field maps are in `docs/protocol.md`. The client fetches over direct HTTP, with
no browser, identifying itself as
`Mozilla/5.0 (compatible; Multiplex-GoogleFlights/<version>; +<url>)`; the
whole flow (search, return flights, booking options, calendar) has been
answered that way (`NOTES.md`). The client library, MCP server and PowerShell
module come next.

## What is here

| Path | What it is |
|---|---|
| `NOTES.md` | Decisions and traps: read before changing anything |
| `docs/` | The decoded protocol: endpoints, framing and the field maps |
| `references/` | Trimmed, scrubbed HAR extracts, their catalogue, and the re-capture recipe (evidence, not the spec) |

## Not affiliated with Google

These endpoints are undocumented, may change without notice, and Google
rate-limits them aggressively. See `NOTES.md` for the request budget.
