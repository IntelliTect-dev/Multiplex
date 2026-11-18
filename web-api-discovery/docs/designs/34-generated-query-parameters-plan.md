# Generated client methods and tools take query parameters

- Issue: https://github.com/IntelliTect-dev/Multiplex/issues/34
- PR:    (filled in at Phase 7)
- Slug:  34-generated-query-parameters
- Base:  `feat/17-generated-mcp-tools` (the tool class this adds parameters to)

## Overview

`generate-wrapper.js` emits every REST method with `query: null`. A capture of
17 calls to `GET /suggestions?prefix=<typed text>&limit=11&alias=aps&...` becomes
`GetSuggestionsAsync(CancellationToken ct)` and an MCP tool `get_suggestions`
with no parameters: it asks the site for `/suggestions` and nothing else. The
client's `SendRawAsync` already takes a `query` argument and `BuildQuery`
already encodes it; nothing fills it.

Measured before any change, on a wrapper generated from the new fixture
(`tests/fixtures/har/rest-query.har`): `tools/list` gives `get_search_suggestions`
an `inputSchema` with no properties, and the request that reaches the wire is
`GET /api/search/suggestions`.

## Approved design

Taken from the issue and the hand-off brief; the choices they leave open are
under **Judgement calls**.

### 1. Collect what the capture showed, per endpoint

`dedupePatterns()` already groups calls into endpoint patterns and keeps every
call as a sample carrying its parsed URL. `parseUrl()` now also keeps the
query's ordered `name, value` pairs, decoded the way a server decodes them
(`URLSearchParams`: percent-escapes, and `+` as a space). Nothing is re-parsed:
the samples of a pattern ARE its calls.

New module `scripts/codegen/query-params.js` turns a pattern's samples into an
ordered list of query parameters, in first-seen order (the order of the first
call, then anything a later call adds). Each call contributes, per name, the
ordered list of values it sent -- so `a=1&a=2` in every call is one constant
repeated parameter, not a varying one.

### 2. Classify each parameter

One rule decides, in this order:

| Kind | When | Default when the caller supplies nothing |
|---|---|---|
| `scrubbed` | any observed value was put there by the scrub, or the name is one the scrub policy treats as a secret | left out |
| `nonce` | it looks like a per-request timestamp or nonce | left out |
| `single` | the endpoint was captured once | the captured value |
| `constant` | every captured call sent the same value(s) | the captured value(s) |
| `varying` | anything else: values differ, or some calls left it out | left out |

Every parameter is optional on the client method and on the MCP tool.

**Recognising a value the scrub replaced** has two layers, and neither needs a
new option:

- *Marker shapes.* The scrubber's own output is recognisable: `<UserEmail>`,
  `<REDACTED-...>` and `<BLUNTED:...>` sentinels, `redacted-<hex>`, the
  `f00ded` / `deaf00` hex fakes, `@example.invalid`, `+1555...` and the other
  fakes the leak gate exempts. Those predicates are imported from
  `har-secrets.js` (`isRedacted`) and `har-shapes.js` (`LEAK_PATTERNS[].isFake`),
  not re-spelled. A value carrying a shape the scrub always replaces (a JWT, a
  UUID, a 32/64-hex run, an email: `sanitize-har.js` `PATTERNS`) counts too --
  in a scrubbed capture it IS a fake, and in an unscrubbed one it is exactly
  what must not be written into source.
- *The substitution tables, by convention.* Typed-PII fakes (a city, a name)
  carry no marker. `run-agent.js` writes the scrubbed capture to
  `.run-agent/scrubbed.har` and both tables to `.run-agent/.har-captures/`, so
  the generator looks beside each `--har` (`<dir>/.har-captures/` and `<dir>/`)
  for `.har-substitutions.json` and `.substitutions.json`. It reads only the
  replacement values, never the keys (which are the plaintext originals), and
  never writes either to output. The pipeline already hands the knowledge over;
  `run-agent.js` does not change.

**Recognising a nonce**:

- every call that sent it sent a different value, and each value is a digit run
  of 10 or more or an opaque token (16+ characters, no spaces, letters and
  digits mixed); or
- the value is a 10- or 13-digit number within a day of the call's own
  `startedDateTime` -- a timestamp. This is what catches `_=1794986993187` on an
  endpoint captured once, where "differs on every call" cannot be seen; or
- the name is a conventional cache-buster (`_`, `_t`, `ts`, `nocache`, `rnd`,
  ...) and the value is a number of 10 or more digits.

`page=1,2,3` and `offset=0,20,40` vary and are short: they are `varying`.

### 3. Emit

Client method (`<Name>Client.Generated.cs`), signature order: path
parameters, `body`, query parameters in first-seen order, `ct`.

```csharp
public async Task<SuggestionsResponse> GetSearchSuggestionsAsync(
    string? limit = "11", string? prefix = null,
    IReadOnlyList<string>? suggestionType = null, string? sessionId = null,
    CancellationToken ct = default)
{
    var wireQuery = new List<KeyValuePair<string, string?>>();
    AddQuery(wireQuery, "limit", limit);
    AddQuery(wireQuery, "prefix", prefix);
    AddQuery(wireQuery, "suggestion-type", suggestionType ?? new[] { "KEYWORD", "WIDGET" });
    AddQuery(wireQuery, "session-id", sessionId);
    var raw = await SendRawAsync("/api/search/suggestions", HttpMethod.Get, jsonBody: null, query: wireQuery, ct: ct).ConfigureAwait(false);
```

- Every query parameter is `string?` (a list of strings when a call repeated
  it): the wire is text, and inferring `int` or `bool` changes its spelling
  (`True`, `01`).
- `null` leaves a parameter out; `""` sends `name=`. A repeated parameter with
  captured values sends them when it is `null`, and nothing when it is empty.
- The existing `BuildQuery` encodes names and values with
  `Uri.EscapeDataString`; nothing in it changes.
- An endpoint with no query parameters is emitted exactly as before
  (`query: null`), so a capture without any produces the same bytes as today.

Tool method (`<Name>Tools.Generated.cs`): the same parameters in the same
order, each with a `[Description]` built from what was observed -- the wire
name, what the calls sent, examples marked as examples, and what happens when
it is omitted.

**Names.** The wire name stays exact. The C# parameter name -- which the MCP
SDK also uses as the tool's property name -- is the wire name in camelCase with
everything but ASCII letters and digits dropped (`page-size` -> `pageSize`,
`q[0]` -> `q0`, `filter.name` -> `filterName`, `_` -> `underscore`), prefixed
`@` when it is a C# keyword (`@class`, listed as `class`), suffixed `Query`
when it collides with a path parameter, `body`, `ct` or an identifier the
emitted bodies use (`raw`, `envelope`, `wireQuery`, `client`), numbered when
two wire names give one identifier, and capped at 48 characters.

### 4. Tests the generator emits

- `tests/<Name>.Tests/QueryStringTests.cs` (only when an endpoint has query
  parameters): four facts per endpoint -- the client called with no query
  argument (the captured defaults) and with every parameter supplied (each a
  distinct value that needs encoding), and the same two through the tool class.
  Each asserts the exact query string that reached the handler (`Uri.Query`:
  .NET rewrites a path it considers equivalent, `%7E` to `~`, so the path is
  left out of the comparison). The expected strings are computed by the
  generator from the capture, not read back from the emitted client. It is a file of its own because `ClientTests.*` and
  `ToolsTests.cs` are one fact per endpoint / per tool, and it is rendered in
  JavaScript (`query-emit.js`) like the tool class is, so no template and no
  `{{Token}}` is added.
- `MockHandler` gains an overload that records the request URIs.
- `Mcp.Tests.ps1` asserts each tool's `inputSchema` lists the endpoint's query
  parameters, none of them required. The expected names are baked in from the
  capture, on the tool's existing `ExpectedTools` line.

### 5. Repo-level tests

- `tests/fixtures/har/rest-query.har`: one path called five times with
  different queries (a constant, a varying search term, a paging number, a
  repeated parameter, an empty value, a scrub-marked session id, a per-call
  timestamp), an endpoint with awkward and colliding names, an endpoint
  captured once, a POST with a query and a body, and one with no query.
- `tests/fixtures/har/rest-query-typed-pii.har`: a city that is personal data
  in the response and a query value too, so the scrub leaves a fake city in the
  query that carries no marker.
- `tests/fixtures/har/rest-query-hostile.har`: query names and values that are
  everything a C# source file must not contain raw.
- `scripts/codegen/query-params.test.js`, `query-emit.test.js`: the rules, and
  what the generator writes for the fixture.
- `scripts/codegen/query-pipeline.test.js`: runs `run-agent.js` end to end and
  proves the substitution-table hand-off, with its falsifier (the same scrubbed
  capture without its tables bakes the fake in).
- `tests/generated-query-parameters.Tests.ps1`: generates, builds with 0
  warnings, runs the wrapper's own xUnit and smoke tests, asks the running
  server for `tools/list`, makes real `tools/call`s against a stand-in site on
  loopback (`tests/stand-in-site.js`) and reads the request lines it received,
  and falsifies: with a parameter removed from the tool class the wrapper's
  smoke test fails, and with the query emission reverted in the generated
  client all sixteen of its query facts fail. A second wrapper, from the
  hostile fixture, must build with 0 warnings and pass its own query facts.

## Evidence Plan

- **Change type**: CLI / library (a generated program's protocol output)
- **Artifact format**: markdown (before / after transcript)
- **Capture command**: generate from `tests/fixtures/har/rest-query.har` with a
  loopback stand-in as base URL, `dotnet build`, `dotnet test`, then
  `Invoke-McpStdioSession` with `tools/list` and one `tools/call`; the same
  against the parent commit for "before".
- **Entry-point file**: `.evidence/<phase-id>/evidence.md`

## Acceptance criteria

- [x] Query parameters are collected per endpoint pattern across all its calls.
- [x] A varying parameter is an optional method and tool parameter, described
      from what was observed, with examples marked as examples.
- [x] A constant parameter is sent as captured by default and can be overridden.
- [x] A scrubbed or nonce parameter is never baked in; the README says so.
- [x] A single-call endpoint exposes every parameter with its captured value as
      the default, except scrubbed and nonce ones.
- [x] Encoding is exact (spaces, reserved characters, unicode); repeated and
      empty values work; order is stable.
- [x] Awkward names map to valid, unique C# and MCP names; the wire name stays
      exact.
- [x] Generated unit tests assert the exact query string on the wire; the
      generated smoke test asserts the `inputSchema`.
- [x] No new command-line option, switch or environment variable.

## Implementation checklist

- [x] `tests/fixtures/har/rest-query.har`, `rest-query-typed-pii.har`,
      `rest-query-hostile.har`.
- [x] `scripts/codegen/query-params.js` + `query-params.test.js`: collect,
      classify, name, recognise scrubbed values, load the tables, encode.
- [x] `scripts/codegen/query-emit.js` + `query-emit.test.js`: descriptions, C#
      declarations, client body lines, README section, xUnit facts, smoke
      expectations.
- [x] `scripts/codegen/query-pipeline.test.js`: the hand-off through
      `run-agent.js`.
- [x] `generate-wrapper.js`: `parseUrl` keeps the query; `run()` attaches
      `queryParameters`; `emitClient` and the README use them.
- [x] `mcp-tools-emit.js`: two insertions in `renderRestTool`.
- [x] `tests-emit.js`, `MockHandler.cs.tmpl`, `Mcp.Tests.ps1.tmpl`.
- [x] `tests/generated-query-parameters.Tests.ps1`, `tests/stand-in-site.js`.
- [x] `SKILL.md` Phases 5 and 6; the skill README's tree; the generated README.

## Judgement calls

Made without the owner; each is in the hand-off report.

1. **Every query parameter is a string.** See "Emit".
2. **A constant is a tool parameter too**, with its default in the schema, so a
   model can change `limit` without the tool being regenerated.
3. **A parameter some calls left out is `varying`**, not constant: leaving it
   out was observed to work.
4. **A constant opaque token is baked in.** A token that did not change across
   the captured calls is, by the issue's rule, a constant. The scrub decides
   what is a credential; the generator does not second-guess it by entropy.
5. **Tables are found by convention**, beside the `--har`. A `--subs` option on
   the generator would be more explicit and is the owner's call.
6. **A key with no `=`** (`?flag`) is sent back as `flag=`: `BuildQuery` and
   HAR's own `queryString[]` do not carry the difference.
7. **Parameter order on the wire is first-seen order by name**, so
   `a=1&b=2&a=3` is sent as `a=1&a=3&b=2`.
8. **A shape the scrub always replaces is never baked in, scrubbed or not.** A
   UUID, a 32/64-hex run, a JWT or an email in a query value is treated as
   scrubbed even when no table and no marker says so. On a scrubbed capture
   that is exact (the scrub replaces every one); on a raw capture it keeps a
   live identifier out of source. The cost: a constant app id that happens to
   be a UUID has no default, and the README says why.
9. **A `<Word>` inside a value counts as a scrub sentinel.** An operator's
   sentinel may sit inside a larger value (`/users/<AccountId>/orders`), so the
   check is not anchored; a query value that carries an HTML tag is therefore
   not baked in either. Erring this way costs a default, never a leak.
10. **The tool's property name is the C# name**, not the wire name
    (`suggestionType`, not `suggestion-type`): the SDK names a property after
    its parameter, and a hyphen is not valid in one. The description of every
    parameter opens with its wire name.
11. **No cap on the number of parameters.** An endpoint whose calls carried a
    hundred distinct names gets a hundred parameters.
12. **A long value that differs on every call is not called a nonce.** It is
    never baked in, like one; but it may be the id or the cursor the caller has
    to choose, so its description and its README cell say only what was seen
    ("a different long number or token on every call") and ask for "the one
    you mean". A value that is the time of its own call, or sits under a
    cache-buster name, is called a timestamp or nonce outright.
13. **The generated query facts compare `Uri.Query`, not the path.** .NET
    rewrites a path it considers equivalent (`%7E` to `~`, `%c3` to `%C3`), so
    a captured path spelled that way would fail a path comparison while sending
    the right request.

## Found along the way (not changed here)

- `sanitize-har.js`'s fake JWT is `eyJ` + 18 hex + `.` + 40 hex + `.` + **6**
  hex: its third run is cut from a 64-character digest at offset 58. The JWT
  shape needs 8 or more there and `har-shapes.js`'s `isFake` for it expects 43,
  so neither matches what is emitted. `query-params.js` recognises the fake as
  it really is; the scrubber and its byte oracle are left alone.
- `mcp-tools-emit.js`'s `csString()` does not escape U+0085 (NEL), which ends a
  line in C# as U+2028 and U+2029 do. A response field name carrying one would
  break the tool file's build. `query-emit.js` escapes it for its own literals
  (`csLiteral`); `csString` is under review with #17 and is not edited here.
- Two generated tests fail on a capture with a non-JSON streaming endpoint
  (no fixture is written for it): issue #35. The query facts do not depend on
  fixtures, and pass for that endpoint.
