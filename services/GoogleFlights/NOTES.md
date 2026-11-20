# Google Flights -- decisions and traps

Read this before touching the client, the captures or the live tests.

## Built from scratch, against Google's own endpoints

The data comes from the requests Google Flights' own page sends to
`www.google.com/_/FlightsFrontendUi/data/...`. No third-party flight API or
scraping service (HasData, SerpApi, ...) is used, and none may be added.

An earlier TypeScript project by the same owner (GoogleFlightsEx) reached the
same endpoints. Its protocol notes were used only as a cross-check: every field
position documented here was re-derived from this repository's own captures.

## Throttling is the main operational risk

Google answers anonymous requests but soft-blocks a client that sends too many.
Measured by the earlier project: about 230 requests in about 35 minutes
produced a block in which **every** call answered gRPC status 13 (`INTERNAL`),
still in force more than 6 hours later. A block applies to the network, not
just the tool, so it costs the owner their own Google Flights too.

Rules for anything in this folder that talks to Google:

- **Discovery captures** are driven at a human pace: one action at a time, at
  least 8 seconds between actions that make Google search, and breaks between
  capture sessions. A capture session stops at the first `INTERNAL`, 429 or
  "unusual traffic" page.
- **Live tests** run on a small, fixed budget and never in a loop.
- **The client** paces itself and caches; see the client's own docs when it
  exists.

## No browser: the client is direct HTTP, with an honest-bot User-Agent

**The rule (owner, 2026-11-20):** any browser use by Multiplex needs the
owner's approval per provider and per use case. Google Flights has none, so
its client fetches over **direct HTTP only**. The browser in the discovery
captures was a recording tool, not part of the client.

**The User-Agent (owner's decision, 2026-11-20):** the client identifies
itself as `Mozilla/5.0 (compatible; Multiplex-GoogleFlights/<version>; +<url>)`,
the conventional form for an honest automated client. It names the project; it
does not pose as a browser.

**Why that form, and not a plain product token:** Google refuses a direct
request whose User-Agent is not in the `Mozilla/5.0 (compatible; ...)` form
(bare gRPC 13) and answers the identical request in that form: P3 refused,
P8 answered 45 minutes later; the whole flow answered (F1-F4), then C1 refused
again with the old User-Agent minutes later. See `docs/protocol.md` section 2.
Do not "simplify" the User-Agent back to a plain product token.

What the client sends: that User-Agent, `content-type`, `x-same-domain: 1` and
the `x-goog-ext-259736195-jspb` header (language, region, currency). No page
token, no browser headers, no cookies except the `NID` Google itself sets.

Not available to it, and not needed: the search page (`GET
/travel/flights/search` is redirected to `/travel/flights/unsupported` for
this User-Agent too) and `flights.google.com` (a redirect to
`www.google.com/travel/flights`; no data endpoints, 405).

## Gotcha: gRPC code 13 is ambiguous

Google answers a refused request with HTTP 200 and a `wrb.fr` error item
carrying gRPC code 13 (`INTERNAL`). It meant a User-Agent not in the
`Mozilla/5.0 (compatible; ...)` form (P1-P3, C1); something about the automated
capture browser (B, E, G); and, for the earlier project, a block lasting more
than six hours. Code 13 alone does not say why. Treat it as "stop and back
off", never as "retry now".

## Gotcha: holding the page's requests is not silent

Holding a request inside the browser (to record what a filter sends without
sending it) makes the page report a `TransportError` to Google through
`POST /_/FlightsFrontendUi/jserror`, once per held request. Answering the held
request locally with an earlier response avoids the report. Anything that
interferes with the page's own requests must assume Google sees it.

## Captures leak the operator's location unless you watch for it

Google pre-fills "Where from?" from the network's location. The home page and
the first autocomplete call therefore carry the operator's approximate
location. Never extract them into a reference. A city can still appear in a
search answer as a connecting airport; that is route data, not a leak.

## Two values the scrub must replace

`.har-policy.project.json` at the repository root adds the page's anti-bot
token (`x-goog-batchexecute-bgr` header) and page session id (`f.sid` query
parameter) to the scrub's secret names. Neither is a credential, but both are
session-scoped and nothing in the client needs them.

`extract-har-reference.js` does not apply that policy yet: it scrubs a copy
staged outside the repository, where the policy file cannot be found. After
extracting, scrub each reference once more in place with `sanitize-har.js`
(the substitution tables go outside the repository) and gate it again.

## Evidence, not the spec

The HAR references under `references/` record what Google's page did on one
day. They are evidence: the source of test fixtures and the baseline to diff
against when Google changes something. The field maps in `docs/` are the spec,
and each position there names the capture that proves it.
