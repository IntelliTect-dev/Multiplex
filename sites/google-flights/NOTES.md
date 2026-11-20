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

## No browser: the client is direct HTTP, and direct HTTP is refused

**The rule (owner, 2026-11-20):** any browser use by Multiplex needs the
owner's approval per provider and per use case. Google Flights has none, so
its client fetches over **direct HTTP only**. The browser in the discovery
captures was a recording tool, not part of the client.

**The obstacle, as of 2026-11-20, from the owner's network:**

- Google's data endpoint refuses a plain HTTP search (bare gRPC 13), and did
  so on 2026-11-19 and again 24 hours later.
- A plain HTTP `GET` of a search page is redirected to
  `/travel/flights/unsupported`: Google treats a client that names itself
  honestly as an unsupported browser.
- `flights.google.com` only redirects to `www.google.com/travel/flights`; the
  data endpoints do not exist there (405).

Google's own page in Chrome is answered on the same network, but only for
searches that start with a **page load** of a search URL: the page load issues
a search session id, and the search page's HTML already carries the first
answer (block `ds:1`, the same layout as `GetShoppingResults`). Every search
the page started without that id (the home-page form, the trip-type menu, a
filter change) was refused. The earlier project saw the same split.

So a direct client would need to fetch the search page (`tfs`, built by the
client) and read `ds:1`, or send its session id back. Getting the page needs a
client Google does not redirect to "unsupported", and how the client may
identify itself is the owner's decision, not this code's. Until then the
provider is blocked from this network.

## Gotcha: gRPC code 13 is ambiguous

Google answers a refused request with HTTP 200 and a `wrb.fr` error item
carrying gRPC code 13 (`INTERNAL`). Every refused search on 2026-11-19 sent
selection `[]` (no session id); but the same code also answered two date-picker
calls in B, and the earlier project's long block. Code 13 alone does not say
why. Treat it as "stop and back off", never as "retry now".

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
