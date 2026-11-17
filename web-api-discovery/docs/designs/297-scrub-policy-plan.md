# 297 -- Replace the shape-based scrub gate with a class/evidence policy model

- **Issue:** [#297](https://github.com/IntelliTect-Samples/IntelliSDLC.ai/issues/297)
- **Supersedes (detection half):** [#295](https://github.com/IntelliTect-Samples/IntelliSDLC.ai/issues/295)
- **Adjacent:** [#294](https://github.com/IntelliTect-Samples/IntelliSDLC.ai/issues/294) -- shares the root cause, fixed here
- **Branch:** `feat/297-scrub-policy`
- **Worktree:** `.worktrees/297-scrub-policy`

## Design

The authoritative design lives in issue #297. This file is the implementation
breakdown; it does not restate the rationale.

The two axes, for reference while reading the stages:

| | gates (blocks) | advises (reports, non-zero exit, artifact kept) |
|---|---|---|
| **secret** -- grants access | name, literal, shape | -- |
| **identity** -- names a person | literal | shape |

The three inputs and where they merge:

```
har-policy.default.json      synced, stringent    -.
.har-policy.project.json     consumer, committed  -+-> merged policy -> scrubber
.har-profile.json            operator, gitignored -'                     + both gates
```

## Where the work lands

| File | Change |
|---|---|
| `skills/web-api-discovery/scripts/har/har-policy.js` | **new** -- loader, merge, validation, waiver matching |
| `skills/web-api-discovery/scripts/har/har-policy.default.json` | **new** -- the stringent baseline |
| `skills/web-api-discovery/scripts/har/har-policy.test.js` | **new** -- merge order, floor enforcement, waiver expiry |
| `skills/web-api-discovery/scripts/har/har-shapes.js` | Patterns gain `class`; IIN validation; findings gain location |
| `skills/web-api-discovery/scripts/har/har-secrets.js` | Value-aware sentinel recognition; names from policy |
| `skills/web-api-discovery/scripts/har/pii.js` | Key-tail matching; phone patterns; cookies; new types |
| `skills/web-api-discovery/scripts/har/verify-scrub.js` | Class-aware exit; findings report; policy fragment emission |
| `skills/web-api-discovery/scripts/har/verify-har-reference.js` | Response-truncation gate; substitution-table gate |
| `skills/web-api-discovery/scripts/har/extract-har-reference.js` | Cap becomes opt-in; structured marker only; policy stamp |
| `skills/web-api-discovery/scripts/har/sanitize-har.js` | Substitution tables move out of the output path |
| `skills/web-api-discovery/scripts/capture/capture-har.js` | Quarantine instead of `unlinkSync` |
| `skills/web-api-discovery/scripts/codegen/generate-wrapper.js` | `.gitignore` block gains `.substitutions.json` |
| `skills/web-api-discovery/SKILL.md` | Policy file, override story, dedicated-account rule |
| `skills/web-api-discovery/README.md` | Tree listing, policy file |
| `.github/agents/tests/pii-scrubbing.Tests.ps1` | Follows |
| `.github/agents/tests/scrub-scripts.Tests.ps1` | Follows |

Suggested PR boundaries are marked at each stage. Stages 1-4 are the
foundation and should land together; 5 onward are independently shippable.

---

## Stage 1 -- The policy loader (`har-policy.js`)

**Task 1.1 (test-first).** `har-policy.test.js`: the default alone loads and
validates; a project file merges over it; an unknown key is a hard error, not a
silent ignore; a project file that disables a **secret** class is rejected by
name; a project file that disables an **identity** class is accepted; a waiver
past its `expires` date does not match; a waiver with no `reason` is rejected.

**Task 1.2.** Write `har-policy.default.json` with every class enabled at its
stringent setting, the upstream `secretFields` / `secretHeaders` lists moved out
of `har-secrets.js`, and the `pii.js` `FIELD` dictionaries moved out as key-tail
patterns. This is a pure data lift; behaviour must not change yet.

**Task 1.3.** Implement `loadPolicy({ startDir, policyPath })`. Reuse
`harProfile.findUpward` for discovery of `.har-policy.project.json` -- one walk
rule, not a second copy. Merge semantics: scalars replace, arrays of *names*
append, `notSecretFields` subtracts after the append, `classes.*` replaces.
Return a frozen object plus a `version` string derived from a hash of the merged
document, for Stage 10.

**Task 1.4.** Floor enforcement lives in the loader, not the callers: a project
file setting any `classes.secret.*` to `off` throws a `PolicyError` naming the
class. Callers cannot forget a check they never make.

**Task 1.5 (added during review).** `advise` on a secret class is refused
alongside `off` -- a secret finding that does not gate is a secret finding that
ships -- and the merged policy exposes `loosenedSecretNames`: every upstream
secret name a project removed via `notSecretFields`. `named-credential` is
caught by NAME or not at all, so a subtraction is the one input that can hollow
out a secret class while its setting still reads `gate`. Forbidding it would
reintroduce the undisableable gate this issue exists to replace (the issue's own
sketch removes `x-asbd-id`), so it is allowed and recorded. Stage 2 onward must
report it on every run, per "the cost of a loosening stays visible".

**Task 1.6 (added after PR 2).** The issuer identification table is
policy-extendable. `CARD_ISSUERS` stays in `har-shapes.js` -- an IIN range is a
public payment-network standard, not a project concept -- but which markets a
consumer operates in *is* a project fact, and "patch upstream" is not an
override path. So the merged policy carries `cardIssuers`, which **appends** to
the shipped table and can never subtract from it: card detection is loosened by
lowering `classes.identity.credit-card`, which reads as a setting a reviewer can
see, not by deleting the range that would have caught something. Maestro is
deliberately **absent** from the default -- its range (`50`, `56`-`69`, lengths
12-19) overlaps Discover and UnionPay and would reopen the false-positive
surface #295 closed -- so a repo that needs it declares it and owns the
consequence. Every field of an added range is validated rather than trusted: a
range that loads and does not mean what its author wrote is worse than one that
fails, since `[5, 69]` reads as "5 through 69" and would in fact claim every
prefix from 05 to 69. The accepted `lengths` window is the DETECTOR's (13-19),
not the payment industry's -- 12 is a real Maestro length and is still refused,
because the card pattern never offers the predicate a 12-digit run and a range
that validates and then detects nothing is the silent failure the validator
exists to refuse.

> **PR 1** ends here -- **delivered**. No caller consumes the policy yet; the
> lift is verifiable in isolation.
>
> Landed: `har-policy.js`, `har-policy.default.json`, `har-policy.test.js`,
> `.github/agents/tests/har-policy.Tests.ps1`, and `FIELD` exported from
> `pii.js` so the tests can pin the lift against the constants it replaces.

## Stage 2 -- Class-tagged shapes and waivers

**Task 2.1 (test-first).** `har-shapes` cases: each pattern reports its class;
a secret-class finding is `gating: true`; an identity-class finding is
`gating: false`; a fingerprint on the waiver list is dropped and counted as
waived.

**Task 2.2.** Add `class: 'secret' | 'identity'` to every entry in
`LEAK_PATTERNS`. `jwt`, `bearer`, `hex64`, `hex32` are secret; `credit-card`,
`ssn`, `phone`, `email` are identity.

**Task 2.3.** `findLeaks(text, policy)` returns findings carrying
`{ kind, class, gating, fingerprint, length }`. Waiver matching happens here so
both verifiers inherit it.

## Stage 3 -- Structural walk, key paths, payload scoping

**Task 3.1 (test-first).** A finding inside `response.content.text` reports its
JSON key path and entry index. A value inside `log.comment` produces **no**
finding. A value inside a percent-encoded parameter still produces a finding,
with `keyPath: null` and the enclosing parameter named.

**Task 3.2.** Replace the whole-document text sweep with a walk over
`log.entries[]` only, descending request/response headers, cookies, query
string, `postData` and `content`. Keep the decoded-shadow sweep, scoped to the
same nodes. HAR envelope fields (`log.comment`, `log.creator`, `log.version`,
`log.pages[].comment`) are never scanned -- we wrote them.

**Task 3.3.** Findings gain `keyPath`, `entryIndex` and an occurrence `count`
after grouping by fingerprint. No finding ever carries the value.

## Stage 4 -- Value-aware secret checks

**Task 4.1 (test-first).** `har-secrets`: a value of `REDACTED`, `redacted`,
`<Redacted>` or `redacted-abc123` under a known secret name produces **no**
finding. A live-looking value under the same name still does.

**Task 4.2.** Replace the case-sensitive `REDACTION_PREFIXES.some(startsWith)`
with a sentinel test that is case-insensitive and recognises both the
`redacted-` and the `<Sentinel>` forms, plus a bare `REDACTED`.

**Task 4.3.** Source `KNOWN_SECRET_FIELD_NAMES` / `KNOWN_SECRET_HEADER_NAMES`
from the merged policy rather than module constants.

**Task 4.4 (added during review).** Both verifiers load the merged policy and
print, on every run including a clean one, any upstream secret name the project
removed via `notSecretFields` -- the Stage 1 carry-forward. A `blocks()`
predicate decides what fails: waived findings and disabled classes do not,
advisory identity findings still do. The design's "advise = non-zero exit,
artifact kept" is only half-implementable until Stage 7: it is the ARTIFACT
that survives, and `capture-har.js` still deletes the scrubbed file on any
non-zero exit. Exiting 0 on advisory findings was tried and three existing
suites caught it turning a real leaked card into a silent pass.

> **PR 2** ends here -- **delivered**. The gate now classifies, locates, and
> stops crying wolf on its own redactions. Measured on a fixture carrying one
> real token, one real card, a fingerprint in `log.comment` and an
> already-redacted `fb_dtsg`: **4 findings before, 2 after**, both real and both
> naming the field that holds them.
>
> Landed: `har-shapes.js` (classes, `findLeaksInHar`, `describeLeak`),
> `har-secrets.js` (`SENTINEL_RE`, policy-sourced names, options-bag walk),
> both verifiers, `har-shapes-class.test.js`, `har-shapes-walk.test.js`,
> `har-secrets-value.test.js`, `verify-scrub-policy.test.js`,
> `.github/agents/tests/har-class-evidence.Tests.ps1`.
>
> Two review findings fixed here rather than deferred: the structural walk
> skipped unquoted JSON numbers (a card serialised as a number was missed where
> the old text sweep caught it), and `listHarFiles` matched `.har`
> case-sensitively, so a `capture.HAR` was never verified at all.

### Carried into the later stages

- **The scrubber does not yet read the merged policy.** `sanitize-har.js`
  builds its field alternation at module load from the DEFAULT names, so a
  project that ADDS a secret name gets it gated by the verifiers but not
  redacted by the scrubber. The existing remedy is the literal mechanism in
  `.har-profile.json`; the proper fix is deferred to Stage 6, which reworks
  the scrubber's field matching anyway.
- **Advisory findings still block.** They stop blocking when Stage 7 lands the
  quarantine that makes a non-zero exit non-destructive. The two must ship
  together.

## Stage 5 -- Credit-card precision -- ~~STRUCK~~

Absorbed by **issue #295** (PR #299, merged `db3777e`). The `credit-card`
predicate now requires an assigned issuer identifier before Luhn;
`isPlausibleRecentUnixMs` (#87) is deleted; the #292/#293 decimal lookarounds
were retained on ablation evidence. Nothing remains to do here.

## Stage 6 -- PII coverage

**Task 6.1 (test-first).** `first_name`, `user_name`, `billing_city`,
`shipping_address_1`, `date_of_birth` are all recognised. `(555) 123-4567`,
`555-123-4567` and a bare 10-digit run in a `phone`-named field are all
scrubbed. A PII value in `request.cookies[]` is scrubbed.

**Task 6.2.** `fieldType()` matches on key **tail** with separator awareness
(split on `_`, `-` and camel-case boundaries), which is what its comment already
claims. Patterns come from the policy.

**Task 6.3.** Phone detection gains the common national spellings, gated on
field-name context for the bare-digit-run case so it does not become the next
credit-card.

**Task 6.4.** `detectPii()` walks `request.cookies` / `response.cookies`.

**Task 6.5.** New types behind policy flags, off in no release before their
detectors are tested: IBAN, MAC address, advertising/device id (IDFA/GAID).
Passport, driver's licence and non-US national ids are **deferred** -- they have
no reliable shape and belong to the literal mechanism.

## Stage 7 -- Non-destructive rejection

**Task 7.1 (test-first).** `capture-har.js`: a rejected scrub leaves no file in
`outputPath`, writes `scrubbed.rejected.har` under the session's
`.har-captures/` directory, and exits non-zero. A findings report lands beside
it.

**Task 7.2.** Replace the `unlinkSync` at `capture-har.js:1030` with a move into
the session directory. The `-OutputPath`-receives-only-verified-artifacts
invariant is preserved by *where it goes*, not by deleting it.

**Task 7.3.** `verify-scrub.js` writes `scrub-findings.json` (kind, class,
keyPath, entryIndex, count, fingerprint -- never a value) and prints a
paste-ready `.har-policy.project.json` fragment for the identity-class findings,
with `reason` left empty so the operator must fill it in.

## Stage 8 -- Truncation

**Task 8.1 (test-first).** `extract-har-reference.js` with no `--max-response-bytes`
truncates nothing. With the flag, the body is cut and
`content.truncated = { originalBytes, keptBytes }` is set, and the body text
itself is unmodified apart from the cut -- **no inline marker**.
`verify-har-reference.js` fails a reference containing any `content.truncated`.

**Task 8.2.** Remove the `65536` default (`extract-har-reference.js:207`). The
cap becomes opt-in, per requirement 7.

**Task 8.3.** Extend the existing request-body gate in `verify-har-reference.js`
to responses. This is the single highest-value change relative to its size: it
would have caught all 27 truncated entries in the consuming repo at commit time.

**Task 8.4.** Document the marker contract in `SKILL.md` so the consumer-side
`Export-HarReference.ps1` can converge on it. Its inline
`...[response body truncated for reference use]` marker corrupts the payload's
JSON *and* evades a structured audit; a second marker format must not survive.

## Stage 9 -- #294, substitution tables -- ~~STRUCK~~

Absorbed by **issue #294** (PR #298, merged `23bb3a0` + `debef30`). Both
substitution tables now default into the gitignored captures tree,
`.substitutions.json` and `.har-captures/` were added to
`SCAFFOLD_GITIGNORE_ENTRIES`, and `verify-har-reference.js --dir` fails when
either table is found in an output path. Nothing remains to do here.

## Stage 10 -- Provenance

**Task 10.1 (test-first).** A written reference records the merged policy
version; `verify-har-reference.js` reports when a reference was produced under a
different policy version than the current one (report, not fail).

**Task 10.2.** Stamp `_scrubPolicy: { version, generatedAt }` into
`digest.json` and the reference's `log.comment`. Requirement 6 exists so a later
policy change makes it knowable which references need re-extraction from the
preserved raws.

**Task 10.3 (test-first).** The stamp also records `rawAvailable` -- the session
directory the reference was extracted from, and whether it still exists.
Requirement 6 protects captures taken from now on; it says nothing about
references already committed, and in the consuming repo none of the preserved
raws produced the 12 committed references (raws are 2026-08-27 or later, the
references are 08-26). A reference whose raw is gone is re-creatable only by
re-capturing against the live provider, which is a different and far more
expensive operation than re-extracting. The verifier must be able to answer
"what would it cost to regenerate this?" without a human going to look.

## Stage 12 -- Operation identity in the digest

**Task 12.1 (test-first).** Two GraphQL POSTs to the same URL with different
`fb_api_req_friendly_name` values produce **two** digest groups. Two calls with
the same friendly name and different `doc_id` values produce **one**. A body
with a `query { ... }` document and no friendly name groups on the document's
operation name. An endpoint with none of the above groups exactly as today.

**Task 12.2.** `buildDigest()` (`capture-har.js:838`) currently keys on
`host|method|pathTemplate|status`, which is a single bucket for RPC-over-HTTP:
every Facebook GraphQL call collapses into `POST facebook.com /api/graphql/ 200`.
Add a stable operation name to the key, resolved in precedence order:

1. `fb_api_req_friendly_name` (form parameter or header)
2. GraphQL `operationName` from a JSON body
3. the first named operation in a `query` / `mutation` document
4. absent -- fall back to today's key

The source list comes from the policy, not from a hardcoded provider list.

**Task 12.3.** `doc_id` is explicitly **not** a fallback, and neither is an id
embedded in a URL. Facebook rotates `doc_id` between captures; a coverage check
keyed on it reported four operations as having zero recurrence -- including
`ComposerStoryCreateMutation` -- when each in fact recurs 31 times under its
friendly name. The failure direction is under-reporting, which manufactures
false alarms about missing captures. Grouping coarsely is better than grouping
on a value that changes between captures; add a test that pins this.

## Stage 13 -- The identity axis joins the shared walk (issue #24)

Added 2026-10-26, after re-measuring the three issues that point at this one.
Not in the original breakdown because the shared walk did not exist then: it
arrived with #454, for the gate and the secret scrubber only.

**The gap, stated as the four cells it belongs to.** There are two parity axes
and three engines. Three cells were covered and one was empty:

| | secret axis | identity axis |
|---|---|---|
| rule parity | `har-scrub-gate-parity.test.js` | `har-scrub-gate-parity.test.js` |
| reach parity | `har-nested-reach.test.js` | **empty** |

`pii.js` kept a private descent that stopped at the first parsed JSON document
-- `walkJsonForDetect` handed a string leaf to the flat detectors and never
asked whether the leaf was itself a payload -- plus a private cap
(`FORM_DEPTH_LIMIT = 4`) that did not agree numerically with the shared
`MAX_DEPTH = 8`. Two engines, two caps, one name for the idea, which is the
hazard `har-nested.js`'s own header warns about.

**Measured before the change,** with the shared walk as the oracle: it delivers
a name-keyed value as a whole leaf at hops 1-7 and the identity pass acted only
at hop 1. Six failing depths. A SHAPED value was replaced at every depth, which
is why #24 is the intersection of name-only sensitivity and nesting rather than
a general failure to descend -- the flat text pass does not need a layer decoded
to recognise a shape.

**Task 13.1 (test-first).** `pii-nested-reach.test.js`, the missing cell. It
carries **no depth literal**: each depth is asked of both sides -- does
`transformNested` deliver the value as a whole leaf, and did the identity pass
act on it -- so the boundary comes from the module and moves with it. Section 5
is the self-check that stops the oracle going blind and taking the assertions
with it; section 0 asserts the fixture premise, because a probe value inside any
`isFake*` predicate would make the whole file pass vacuously. That is a live
trap, not a hypothetical: `+1555XXXXXXX` is the phone faker's own output, and a
first measurement of #24 read a correctly-declined `+15551234567` as a reach
failure.

**Task 13.2.** `pii.js` takes the shared traversal for both halves --
`descendNestedLeaf` for detection and `replaceNestedLeaf` for replacement, which
must descend identically or a value is found in one shape and rewritten in
another. `FORM_DEPTH_LIMIT` becomes the shared `MAX_DEPTH` rather than a second
number, and the depth budget is threaded rather than restarted inside a parsed
document, so the effective reach is one cap and not the sum of two.

**Task 13.3.** `har-nested.js`'s visit node gains a `path`, composed from the
form parameter names, object keys and array indices the traversal passed
through. A location, never a value. Without it a nested finding could only say
"somewhere inside `request.postData.text`" -- which is what #297 measured when
135 of 176 findings resolved no key path. Additive: the two existing callers
ignore the field.

**Task 13.4.** The deeper reach is REPORTED, not silent. `sanitize-har.js`
prints how many typed-PII detections came from below the surface, so a capture
whose scrub quietly started doing more says so on the run rather than in an
unexplained diff weeks later; and it prints when the walk hit the shared depth
limit, because an early stop that reports nothing reads as a clean walk and
fails open -- the D3 half of #454.

**Accepted limit, disclosed rather than introduced.** The shared walk offers
STRING leaves only, so a card or a coordinate serialised as a JSON NUMBER
inside a nested document is still missed by the name-keyed rules. It was missed
entirely before, so this is a strict improvement; closing it means teaching the
shared walk about scalar leaves, which changes all three engines and belongs in
its own change.

## Stage 14 -- Escape-aware URL coverage (issue #22)

Added 2026-11-01. Stage C of the four-stage breakdown agreed on 2026-10-26;
numbered here so the plan and the issue use one set of names.

**The gap.** #479 routed a URL's **query** through the decoding path and
disclosed the rest as a known limit in `detectInUrl`'s own doc comment: the
origin, path and fragment stayed on the plain-text scanner. A path segment
carrying a percent-encoded payload -- some REST APIs put a JSON or base64 blob
in a segment rather than a query -- was therefore fully exposed to the original
corruption, and so was the fragment, and so was every part of a URL with no
`?` in it at all.

**Both directions were live, and the second is the serious one.**

| direction | what happened |
|---|---|
| false positive | `%22`'s digits completed a 14-digit id into a Luhn-valid, assigned-IIN 16-digit run; the 16-digit fake was written over a span including the `22` *inside* the escape, so `%22` became `%42` and the segment decoded with a `B` where its delimiting quote had been |
| false **negative** | the same two digits extended a genuine 16-digit card to 18 and pushed it out of every detector's range, so it was never found and shipped in the clear |

Measured end to end through the real CLI on a four-entry fixture: before,
1 of 4 URLs carried a corrupted payload **and** the genuine card survived;
after, 0 of 4 and the card is replaced.

**Task 14.1 (test-first).** `pii-url-escape-scrub.test.js`. **31 checks**, eight
of which fail behaviourally with `pii.js` reverted to `origin/main`. Sections:
the fixture premise (the escape digits really do form a Luhn-valid card-length
run); the corruption, in each location the query branch did not cover; the
falsifiers that prove the fix is not a detection kill-switch; the
meaning-preservation guards; idempotence across every component kind; and the
four regressions the review panel found.

**Task 14.2.** State the defect generally rather than per-location --
*percent-encoded text scanned as plain text has its escapes read as payload* --
and fix it the same way. `splitUrl` decomposes a URL into origin, path, query
and fragment; each path segment and the fragment are percent-decoded before
detection and rewritten on their **decoded** text.

Escape-awareness then costs nothing and is not a second mechanism: decoded text
holds no `%XX` for a write to land inside, and the re-encode happens afterwards
over a whole component. A decode **without** that write guard would have
relocated the corruption rather than removing it, which is why the two always
travel together.

**Task 14.3.** `formDecode` becomes `percentDecode(raw, plusIsSpace)`. `+` is a
space in form **data** and a literal `+` everywhere else in a URL; decoding a
path with form semantics would turn a legitimate `+` into a space and re-encode
it as `%20` -- a changed URL, in exactly the direction #22's scope note warns
about.

**Three things it deliberately does not do.**

- The **origin** keeps the plain-text treatment. A scheme and an authority are
  structure, not a place a provider hides a document, and a `/` inside the
  authority would be the path's first delimiter rather than a character of it.
- An **unchanged component is re-emitted verbatim**, never round-tripped
  through the decoder. `encodeURIComponent(urlDecode(x))` is not the identity,
  and a wrong path rewrite is worse than the corruption it prevents.
- The path is scanned **per segment**, so a value spanning a `/` is no longer
  matched. That is a tightening, not a loss: a `/` is a delimiter, and a digit
  run reaching across one was the same accidental-length reading this stage
  exists to stop.

**What the review panel changed about this stage.** Two models reviewed the
diff under explicit overrides. The first found one Critical -- a component whose
decoded text read as `name=value` was encoded twice, so `%40` became `%2540`,
text rather than an `@`. The second found four more regressions against `main`,
and they reshaped the design rather than patching it:

- A no-op scrub rewrote any JSON-shaped path segment, because
  `JSON.stringify(JSON.parse(x))` is not the identity and **rounds an integer
  past 2^53**. "Nothing changed" now means the original bytes.
- **One decode is not always enough, and half a decode is worse than none.** A
  proxied URL in a path segment carries two layers; decoding once left escapes
  the plain-text scanner then read as payload, bringing an 18-character digit
  run back into card range. A component is decoded only when the result is
  unambiguously decoded.
- Reading a form-shaped fragment with `+`-is-space made an E.164 phone
  **undetectable**, and it shipped in the clear where `main` had caught it.
  That settles what #130 was filed to decide: the two readings looked like
  mirror images, but one loses a value and the other only respells one. A
  fragment takes `+`-literal; a query keeps `+`-is-space, which is correct for
  form data.
- `mailto:` and `data:` have no `//` authority, so the whole URL read as one
  path segment and re-encoding escaped the delimiters that give it meaning.
  Non-hierarchical URLs keep the plain-text treatment.

**A third round, and what it says about the work.** Both models reviewed again
after those fixes, and found one Critical leak plus three more:

- The form branch's recursive calls did not pass `plusIsSpace` on, so depth 2
  reverted to the default inside a fragment that had asked for `+`-literal. An
  RFC 5233 subaddressed address (`user+tag@host`) was split at its `+` and only
  the email-shaped tail replaced -- **the real local-part prefix survived in the
  clear**, where `main` had replaced the whole address. The lesson generalises
  past the bug: `noForm` describes the CALLER's layer and must stop there, while
  `plusIsSpace` describes the COMPONENT's convention and holds for every layer
  inside it. One propagates and one does not, and the code now says why.
- The multi-layer refusal had been applied to the opaque route only, so a
  form-routed fragment carrying an encoded redirect URL was still half-decoded.
  Both twins now share the predicate.
- The query had never been given the component treatment: a BARE encoded query,
  one with no `=`, still went to the plain-text scanner, so #22's defect was
  fully live there and a genuine card in one survived. Now routed, with
  `+`-is-space, which is #479's reading and correct for a query.
- `encodeURIComponent` escapes `/` and `?`, which are legal in a fragment, so
  rewriting a value inside an SPA hash route produced `#%2Fusers%2F...` that no
  router matches. Each component now gets back the characters legal in it --
  except `&` and `=`, deliberately, because handing those back would let a
  rewritten opaque component start parsing as form data on a later pass and be
  classified differently from one run to the next.

**Three review rounds on one stage is itself a finding.** Every one of them
landed on the same seam: not the decoding, but **deciding which layer each rule
belongs to**. The encode/decode boundary in a URL is where this module's
hardest defects live, and it is worth approaching the next change there
expecting that rather than discovering it.

**Accepted limits, disclosed rather than discovered later.** Header, cookie and
`queryString[]` values are still plain-text-scanned, so an entry can disagree
with itself when a `Referer` mirrors its own URL (#131). The multi-layer
refusal is coarse and also declines a single-layer payload holding an encoded
string leaf, which is the common shape rather than only the proxied-URL one
(#132). A nested form value is re-encoded once per layer (#133). A query with
an unencoded `+` still hides an E.164 number from its own detector, which
scanning both spellings would fix for good (#130). And the path is read per
`/` segment. The gate's own version of this reading defect is #127, and the
userinfo mis-scan is #128.

**Accepted limit, disclosed rather than introduced.** The **gate**
(`har-shapes.js`) still reads an encoded path as plain text, so it reports the
same shape as an identity advisory. The count and disposition are unchanged by
this stage -- one advisory before, one after, artifact kept either way -- so it
is the complementary half of the defect on the other engine rather than a
regression here. Filed as its own follow-up.

## Stage 11 -- Documentation

**Task 11.1.** `SKILL.md`: the three files and what each may contain; the
values-vs-rules rule; the secret floor; the override worked example (loosen to
credentials/tokens/secrets/display-name); the quarantine path; the truncation
contract.

**Task 11.2.** `SKILL.md` capture phase: captures are recorded as a **dedicated
test account**, never a personal or default workspace (requirement 8). State it
as a precondition of starting a recording, not as advice.

**Task 11.3.** `skills/web-api-discovery/README.md` tree listing gains
`har-policy.default.json` and `.har-policy.project.json`.

**Task 11.4.** `project.instructions.md.template` gains a stub pointing at
`.har-policy.project.json` as the place a consuming project declares its scrub
posture.

## Deferred

- **Salt relocation.** Moving `salt` from `.har-profile.json` to the project
  policy makes a reference reproducible across a team, but publishes the input
  that makes a low-entropy fake brute-forceable. It is only safe once Stage 9
  guarantees the substitution table is never committed. Revisit after Stage 9,
  not before.
- **`~/.har-profile.json`.** `findUpward` already permits it; the change is
  documentation plus a test. Worth doing, but independent of this issue.
- **Assisted literal discovery.** Surfacing recurring authenticated-request
  values as candidates for human confirmation. A separate issue -- it changes
  the capture UX, not the policy model.
- **AI scrub pass.** Requirement 5 says the gate must never depend on model
  judgment. The policy model leaves room for a pass that only *adds*
  redactions, but nothing in this plan implements one.
