#!/usr/bin/env node
// The declared gate/scrubber parity exceptions -- one place (issue #42).
//
// ## What belongs here
//
// The gate and the scrubber do not cover identical kind sets, and some of that
// asymmetry is correct. `har-scrub-gate-parity.test.js` measures three
// directions and fails on any gap it finds; a row here is how a gap says "this
// one is known", with either a reason it is right (`by-design`) or the issue
// that will close it (`tracked-by`).
//
// The point is NOT to make the test pass. #395's cost was never the gap itself
// -- it was that the gap was indistinguishable from an oversight, so nobody
// could tell a deliberate omission from a rule somebody forgot to write. This
// file is the difference.
//
// ## What does not belong here
//
// A row whose honest reason is "nobody has written the rule yet" is not
// `by-design`; it is `tracked-by` plus an issue. If neither is true, the gap is
// a defect and the fix goes in the detector, not in this table.
//
// ## Why the table cannot rot
//
// The parity test asserts every row is LIVE -- a row for a gap that no longer
// exists FAILS, naming the kind. So closing a tracked issue turns CI red until
// its row is deleted, and a row can never survive to excuse the next real
// instance of the same gap. That is the one failure mode a hand-maintained
// waiver list always reaches, and the reason the issue asked for a mechanism
// rather than a checklist.
//
// `gap` must be one of the direction keys the parity test measures:
//   - `gated-not-scrubbed`    the gate reports it, nothing rewrites it
//   - `scrubbed-not-declared` it is rewritten, no policy class names it
//   - `scrubbed-not-gated`    it is rewritten, the gate cannot detect it

'use strict';

// A NAME-MATCHED kind has no value pattern anywhere in this codebase -- the
// scrubber finds it by field name, because nothing about the value distinguishes
// it. There is no shape for the gate to mirror, so the asymmetry is structural
// rather than an omission.
//
// Use it ONLY for kinds that are genuinely pattern-free. An earlier draft reused
// it for `ip-address` and `device-id`, which DO have value patterns; those rows
// then read as "no rule is possible" when the truth was "a rule is possible and
// is deliberately not mirrored, for a different reason". A row a reader cannot
// trust is worse than no row, because the entire purpose of this table is that a
// gap with a reason can be told from an oversight. Those two now carry their own.
const NAME_MATCHED =
    'matched by field name, not by value shape: the scrubber has no value pattern for this kind ' +
    'either, because nothing about the value distinguishes it from ordinary prose. There is no ' +
    'shape for the gate to mirror, so this asymmetry is structural, not a missing rule.';

const EXCEPTIONS = [
    // --- Direction 1: gated, nothing rewrites it. One row, and it is a defect. ---
    {
        gap: 'gated-not-scrubbed',
        kindName: 'bearer',
        disposition: 'tracked-by',
        trackedBy: '#71',
        reason:
            'a real fails-closed defect, not a design choice. The gate reports a bearer token by ' +
            'shape anywhere; the scrubber rewrites it on neither path, so a bearer value in a body ' +
            'under a benign field name is refused with no remedy. #71 adds the scrub rule.',
    },

    // --- Direction 2: rewritten, governed by no policy class. Both are defects. ---
    {
        gap: 'scrubbed-not-declared',
        kindName: 'upload-handle',
        disposition: 'tracked-by',
        trackedBy: '#77',
        reason:
            'ungovernable scrub: a credential kind the scrubber rewrites and no policy class ' +
            'names, so no consuming project can tune it and no waiver can cover it. #77 decides ' +
            'its class deliberately, because the choice changes behaviour for every consumer.',
    },
    {
        gap: 'scrubbed-not-declared',
        kindName: 'uuid',
        disposition: 'tracked-by',
        trackedBy: '#77',
        reason:
            'ungovernable scrub, and the one with history: a UUID is identifier-shaped, and ' +
            'rewriting identifiers by shape is what deleted 1413 trip ids. With no class, a ' +
            'project cannot say "these are my primary keys". #77 declares it.',
    },

    // --- Direction 3: rewritten, the gate cannot detect it. ---
    // Most of these are correct and structural. Two are not, and say so.
    {
        gap: 'scrubbed-not-gated',
        kindName: 'upload-handle',
        disposition: 'tracked-by',
        trackedBy: '#72',
        reason:
            'a real fails-open exposure, not a design choice. This is a credential the scrubber ' +
            'rewrites by shape with no gate rule to verify the rewrite happened, so a scrub rule ' +
            'that silently stops firing yields a live credential that passes as verified. #72 ' +
            'adds the gate rule.',
    },
    {
        gap: 'scrubbed-not-gated',
        kindName: 'uuid',
        disposition: 'by-design',
        reason:
            'gating on identifier-shaped values is the failure that deleted 1413 trip ids. A UUID ' +
            'carries no provenance -- it is equally a session token and a primary key -- so shape ' +
            'alone is too weak to refuse a capture on, which is the identity-advises rule. The ' +
            'scrub is deliberately the only action taken on it. (Whether that scrub should be ' +
            'tunable is a separate question, tracked as #77.)',
    },
    {
        gap: 'scrubbed-not-gated',
        kindName: 'dob',
        disposition: 'by-design',
        reason:
            'the scrubber matches this as an ISO date, which is indistinguishable from any other ' +
            'timestamp. A gate rule would report every created_at, updated_at and expiry in every ' +
            'capture -- the 1134-findings-3-real failure that made findings worthless. The field ' +
            'name is the only usable evidence, and the gate has no field-name path for identity.',
    },
    {
        gap: 'scrubbed-not-gated',
        kindName: 'ip-address',
        disposition: 'by-design',
        reason:
            'NOT for want of a shape -- the scrubber matches this with value patterns for v4 and ' +
            'v6, context-free, with no field-name gating. The shape exists and is deliberately not ' +
            'mirrored into the gate: a dotted quad appears in every capture as the server\'s own ' +
            'address and inside version strings, so a gate rule would report overwhelmingly ' +
            'non-findings -- and an identity finding that is usually wrong is what made findings ' +
            'worthless. Scrubbing a false positive costs one rewritten value; gating on one costs ' +
            'the whole capture.',
    },
    {
        gap: 'scrubbed-not-gated',
        kindName: 'geo-coordinates',
        disposition: 'by-design',
        reason:
            NAME_MATCHED +
            ' A float pair is additionally indistinguishable from any other pair of numbers, so ' +
            'the field name is the only evidence that one is a location.',
    },
    {
        gap: 'scrubbed-not-gated',
        kindName: 'postal-code',
        disposition: 'by-design',
        reason:
            NAME_MATCHED +
            ' A short digit run is additionally indistinguishable from an id, a count or a price.',
    },
    { gap: 'scrubbed-not-gated', kindName: 'person-name', disposition: 'by-design', reason: NAME_MATCHED },
    { gap: 'scrubbed-not-gated', kindName: 'street-address', disposition: 'by-design', reason: NAME_MATCHED },
    { gap: 'scrubbed-not-gated', kindName: 'city', disposition: 'by-design', reason: NAME_MATCHED },
    { gap: 'scrubbed-not-gated', kindName: 'region', disposition: 'by-design', reason: NAME_MATCHED },
    { gap: 'scrubbed-not-gated', kindName: 'country', disposition: 'by-design', reason: NAME_MATCHED },
    {
        gap: 'scrubbed-not-gated',
        kindName: 'device-id',
        disposition: 'by-design',
        reason:
            'NOT for want of a shape -- the scrubber matches a UUID-shaped value here, but only ' +
            'under a field name it recognises, because the shape alone does not tell a device id ' +
            'from a primary key. Mirroring it into the gate would mean gating on identifier-shaped ' +
            'values without the field-name evidence, which is what deleted 1413 trip ids. Same ' +
            'reasoning as the uuid row, and for the same measured reason.',
    },
];

/** Every declared exception, as `{ gap, kindName, disposition, reason, trackedBy }`. */
function all() {
    return EXCEPTIONS.slice();
}

/** The exception declared for `kindName` in direction `gap`, or `undefined`. */
function find(gap, kindName) {
    return EXCEPTIONS.find((e) => e.gap === gap && e.kindName === kindName);
}

module.exports = { all, find, EXCEPTIONS };
