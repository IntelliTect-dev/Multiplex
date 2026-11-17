#!/usr/bin/env node
// Behavior tests for NESTED-PAYLOAD REACH on the IDENTITY axis (issue #24).
//
// Zero-dep, runs with `node pii-nested-reach.test.js`. Exits non-zero on the
// first failure. No subprocess, no temp repository: every assertion drives
// `pii.js` directly, so this file is cheap enough to run on every change to it.
//
// ## The missing cell
//
// There are two parity axes between the engines, and they fail independently:
//
//   rule parity  -- does each engine know this value is sensitive?  (#395, #408)
//   reach parity -- does each engine GET to where the value sits?   (#378, #454)
//
// and there are three engines, not two. The existing coverage fills three of
// the four cells:
//
//                   |  secret axis                 |  identity axis
//   rule parity     |  har-scrub-gate-parity.test  |  har-scrub-gate-parity.test
//   reach parity    |  har-nested-reach.test       |  >>> THIS FILE <<<
//
// #454 gave the gate and the SECRET scrubber one traversal, one `MAX_DEPTH` and
// one byte-preservation rule, in `har-nested.js`. `pii.js` -- the identity axis
// -- was not part of it and kept a private descent that stops at the first
// parsed JSON document, plus a private cap (`FORM_DEPTH_LIMIT`) that did not
// even agree numerically with the shared one. Two engines, two caps, one name
// for the idea: exactly the hazard `har-nested.js`'s own header warns about.
//
// ## The oracle is the shared walk, so this file carries no depth literal
//
// A test that asserted "scrubbed at depth 2" would pin the symptom and go blind
// the moment the layering changed. Instead, each depth is asked of BOTH sides:
//
//   1. does `har-nested.js:transformNested` deliver the value as a WHOLE LEAF?
//      That is the only form in which a name-keyed rule could act on it -- an
//      outer layer whose text merely CONTAINS the value is not reach.
//   2. did the identity pass act on it?
//
// The invariant is that the second follows from the first:
//
//      wherever the shared walk delivers a value as a whole leaf,
//      the identity pass must act on it.
//
// So the cap is read from the module rather than written down here. If
// `MAX_DEPTH` moves, the oracle moves with it and the assertions stay true.
//
// ## The falsifier
//
// Revert `pii.js` to its private descent and section 2 fails, naming every
// depth at which the shared walk reached a value the identity pass did not.
// Measured before the fix: the walk delivers the leaf at hops 1-7 and the
// identity pass replaced only hops 1 -- six failing rows. Section 1 is the
// isolation that makes those six rows mean DEPTH and not RULE, and section 5
// is the self-check that stops section 2 passing because the oracle went blind.
//
// ## The already-fake trap, and why the premise is asserted
//
// `pii.js` declines to replace its own fake output, so a probe value inside any
// `isFake*` predicate is never a candidate and would make this whole file pass
// vacuously. It is a live trap, not a hypothetical: `+1555XXXXXXX` IS the phone
// faker's output, and a first measurement of #24 read a correctly-declined
// `+15551234567` as a reach failure. `pii-form-encoded-scrub.test.js` carries
// the same warning for `4242424242424242`. Section 0 therefore asserts the
// premise instead of trusting it.
//
// NOTHING here is real captured data. Every value is synthetic and defined in
// this file, and no assertion message prints a detected value -- a failure
// report that quotes the value merely relocates it into the log written to
// explain it. A depth, a count and a kind are the whole reportable fact.

'use strict';

const assert = require('assert');
const path = require('path');

const pii = require(path.join(__dirname, 'pii.js'));
const nested = require(path.join(__dirname, 'har-nested.js'));

let failures = 0;
function check(name, fn) {
    try {
        fn();
        console.log(`  ok - ${name}`);
    } catch (err) {
        failures++;
        console.error(`  FAIL - ${name}\n    ${err && err.message}`);
    }
}

const enc = encodeURIComponent;

// --- The two probe values, one per evidence kind. ---------------------------
//
// NAME-ONLY: a person's name has no shape any detector could claim, so the
// FIELD NAME is the entire evidence. This is the value #24 is about.
//
// THE EMBEDDED QUOTE IS LOAD-BEARING, and an escape-free name was this file's
// first real defect -- found in independent review by ABLATION, not by reading.
//
// With a plain name like `Jordan Rivenbark`, stubbing `replaceNestedLeaf` to a
// complete no-op still passed all ten sections. The reason is that once
// detection has enrolled the value, the PRE-EXISTING flat `replaceAll` pass
// scans whole leaf text for literal matches regardless of structure, and finds
// the name in the still-encoded outer blob with no help from the nested
// replacement at all. So the file proved detection reach and silently proved
// nothing about the replacement twin the commit calls load-bearing.
//
// A `"` inside the value changes that: nested in a JSON string the quote is
// stored ESCAPED (`\"`), so the flat pass -- searching for the unescaped
// spelling -- cannot match it, and only a descent that decodes the layer can
// replace it. Reviewer's measurement with the twin stubbed: the name survives
// VERBATIM while the shaped value is still replaced, which is exactly the
// asymmetry ("found in one shape, rewritten in another") this pairing exists
// to prevent. Section 7 pins that directly.
const NAME = 'Jo"an Rivenbark';
// SHAPE-ONLY: `contact_point` is in no phone name list the policy ships, so
// this is caught context-free by the E.164 pattern. `+1206…` is a real NANP
// area code and deliberately NOT `+1555…`, which is the faker's own output.
const PHONE = '+12063841297';

// A name a `piiFields` tail rule recognises, holding NAME; and a name no list
// recognises, holding PHONE. Keeping them in one fixture is what makes a
// failure attributable to one evidence kind rather than to the fixture.
function payload() {
    return { client_input_params: { first_name: NAME, contact_point: PHONE } };
}

/**
 * The #24 shape, at `hops` encoding layers.
 *
 * hops=1 is a form parameter whose value is percent-encoded JSON -- one decode
 * and one parse. Each further hop wraps the document in a JSON STRING, which is
 * the layering the observed provider traffic uses and the layer type the
 * private descent could not re-enter: a parsed document's string leaf was
 * handed to the flat detectors and never asked whether it was itself a payload.
 *
 * `av=1` is a benign neighbour, used by section 4.
 */
function bodyAt(hops) {
    let doc = payload();
    for (let i = 1; i < hops; i++) doc = { params: JSON.stringify(doc) };
    return 'params=' + enc(JSON.stringify(doc)) + '&av=1';
}

function harWith(body) {
    return {
        log: {
            entries: [{
                request: {
                    method: 'POST',
                    url: 'https://api.example.test/async/wbloks/fetch/',
                    headers: [{ name: 'content-type', value: 'application/x-www-form-urlencoded' }],
                    queryString: [],
                    postData: { mimeType: 'application/x-www-form-urlencoded', text: body },
                },
                response: {
                    status: 200, headers: [],
                    content: { mimeType: 'application/json', text: '{}' },
                },
            }],
        },
    };
}

function bodyOf(har) { return har.log.entries[0].request.postData.text; }

/**
 * Every spelling a value can wear in this fixture, across escape depths.
 *
 * ESCAPING COMPOUNDS, and assuming it did not was this checker's own bug. Each
 * JSON-string wrap escapes the previous level's backslashes again, so at two
 * hops `Jo"an` is stored as `Jo\\\"an`, not `Jo\"an`. A checker that enumerated
 * one escape level would report a surviving value as ABSENT at depth -- a false
 * negative on a leak check, which reads exactly like a pass and is the harder
 * direction to notice.
 *
 * So the levels are GENERATED by applying the same transform the fixture
 * applies, rather than written out.
 *
 * THE BOUND IS DERIVED, and a literal `4` here was this helper's own bug --
 * caught in re-review, by the one test that cannot be fooled: running
 * `survives()` against an UNSCRUBBED body, where the value is present by
 * construction. It answered `false` from hops 4 upward.
 *
 * The cause is that escaping compounds EXPONENTIALLY, not linearly: each wrap
 * escapes the previous level's backslashes too, so escape-depth `k` carries
 * `2 ** k - 1` backslashes -- 1, 3, 7, 15, 31, 63, 127, 255, 511, 1023 across
 * the depths this fixture builds. Four iterations therefore covered hops 1-3
 * only, which left section 1 vacuous for hops 4-7 and section 3.a -- pinned at
 * the deepest delivered hop -- vacuous outright. A checker that cannot see a
 * value reports every leak as absent, which is the same "reads exactly like a
 * pass" failure this file's probe exists to expose, one level removed.
 *
 * Bounding it by the shared cap keeps it correct if `MAX_DEPTH` moves, for the
 * same reason nothing else in this file names a depth.
 */
function spellings(value) {
    const out = new Set();
    let cur = value;
    for (let i = 0; i <= nested.MAX_DEPTH + 2; i++) {
        out.add(cur);
        out.add(enc(cur));
        out.add(enc(enc(cur)));
        cur = JSON.stringify(cur).slice(1, -1);
    }
    return [...out];
}

/**
 * Did the value survive in ANY spelling the artifact might carry it in?
 *
 * Checking one spelling is how a value left live one encoding layer down passes
 * unnoticed.
 */
function survives(text, value) {
    return spellings(value).some((s) => text.includes(s));
}

/**
 * Does the SHARED walk deliver `value` as a whole leaf of `text`?
 *
 * Read-only: `visit` returns its input, which is the mode the gate already
 * runs in. This is the oracle -- see the header.
 */
function sharedWalkDelivers(text, value) {
    let delivered = false;
    nested.transformNested(text, (node) => {
        if (node.value === value) delivered = true;
        return node.value;
    });
    return delivered;
}

/** Every depth this fixture can express, two past the shared cap. */
const DEPTHS = Array.from({ length: nested.MAX_DEPTH + 2 }, (_, i) => i + 1);

console.log('pii-nested-reach');

// --- 0. THE PREMISE. ------------------------------------------------------
// GUARDS, not falsifiers: they pass before and after the change. Without them
// every assertion below could pass for a reason that has nothing to do with
// reach.

check('0.a the probe values are not the faker\'s own output', () => {
    // Asked of the faker itself rather than of a copy of its predicates: a test
    // that re-stated `/^\+1555\d{7}$/` here would keep passing after the faker
    // changed shape, which is the drift this repo keeps paying for.
    assert.notStrictEqual(pii.fakeFor('phone', PHONE), PHONE,
        'the phone probe IS the faker\'s output for itself, so it would be declined as '
        + 'already-substituted and every reach assertion below would pass vacuously');
    assert.notStrictEqual(pii.fakeFor('person-name', NAME), NAME,
        'the name probe IS the faker\'s output for itself (see above)');
    assert.ok(!/^\+1555\d{7}$/.test(PHONE),
        'the phone probe is inside isFakePhone\'s exemption -- the exact trap that made '
        + 'a correctly-declined value read as a reach failure');
});

check('0.b the field names carry the evidence the sections attribute to them', () => {
    assert.strictEqual(pii.fieldTypeFor('first_name'), 'person-name',
        'the name-only probe\'s field is not recognised, so section 2 would be testing '
        + 'an absent RULE rather than absent REACH');
    assert.strictEqual(pii.fieldTypeFor('contact_point'), null,
        'the shape-only probe\'s field IS name-recognised, so section 3 would no longer '
        + 'be evidence about shape evidence');
});

check('0.d the survival checker can SEE the value at every depth it will judge', () => {
    // THE ONE CHECK THAT CANNOT BE FOOLED, and the reason it exists.
    //
    // Every "the value is gone" assertion in this file is `!survives(...)`, so
    // a checker that cannot SEE the value reports every leak as absent and the
    // whole file passes while testing nothing. That is not hypothetical: a
    // 4-iteration `spellings()` went blind from hops 4 upward, which left
    // section 1 vacuous across most of its range and section 3.a vacuous
    // outright. It was invisible to inspection and obvious the moment anyone
    // asked the checker about a body nothing had scrubbed.
    //
    // An UNSCRUBBED body is the oracle: the value is present by construction,
    // so `survives()` MUST say so. Anything else is a broken checker, never a
    // clean capture.
    const blind = DEPTHS.filter((hops) => !survives(bodyAt(hops), NAME));
    assert.deepStrictEqual(blind, [],
        `survives() cannot find the probe in an UNSCRUBBED body at depths ${blind.join(', ')}. `
        + 'The value is there by construction, so every "it was removed" assertion at those '
        + 'depths is vacuous -- a false negative on a leak check, which reads exactly like '
        + 'a pass');
    // The same question for the shaped probe, which section 2 judges at every depth.
    const blindShape = DEPTHS.filter((hops) => !survives(bodyAt(hops), PHONE));
    assert.deepStrictEqual(blindShape, [],
        `survives() cannot find the shaped probe in an UNSCRUBBED body at depths `
        + `${blindShape.join(', ')}, so section 2 is vacuous there`);
});

check('0.c one layer down already works', () => {
    const har = harWith(bodyAt(1));
    pii.scrubPii(har);
    const after = bodyOf(har);
    assert.ok(!survives(after, NAME),
        'the identity pass cannot reach a name-keyed value ONE layer down, so every '
        + 'assertion below would fail for a reason that is not about depth');
    assert.ok(!survives(after, PHONE), 'nor a shaped value one layer down');
});

// --- 1. THE INVARIANT. ----------------------------------------------------
// Wherever the shared walk delivers a value as a whole leaf, the identity pass
// must act on it. No depth is named here; the oracle supplies the boundary.

check('1. wherever the shared walk delivers a leaf, the identity pass acted on it', () => {
    const unreached = [];
    for (const hops of DEPTHS) {
        const body = bodyAt(hops);
        if (!sharedWalkDelivers(body, NAME)) continue;   // past the shared cap
        const har = harWith(body);
        pii.scrubPii(har);
        if (survives(bodyOf(har), NAME)) unreached.push(hops);
    }
    assert.deepStrictEqual(unreached, [],
        'the shared walk delivered a name-keyed value as a whole leaf at these depths and '
        + `the identity pass did not act on it: ${unreached.join(', ')}. The engines have `
        + 'unequal REACH -- the rule is present and fired one layer up (section 0.c), so '
        + 'this is #454\'s defect on the identity axis');
});

// --- 2. SHAPE EVIDENCE MUST NOT REGRESS. ----------------------------------
// This already holds: a shaped value is reached by the flat text pass whatever
// the nesting, which is why #24 is the INTERSECTION of name-only sensitivity
// and nesting rather than a general failure to descend. Pinned because a fix
// that routed everything through the structural walk could lose it.

check('2. a shaped value is still replaced at every depth', () => {
    const survivors = [];
    for (const hops of DEPTHS) {
        const har = harWith(bodyAt(hops));
        pii.scrubPii(har);
        if (survives(bodyOf(har), PHONE)) survivors.push(hops);
    }
    assert.deepStrictEqual(survivors, [],
        `a shaped value survived at depths ${survivors.join(', ')}. This worked before the `
        + 'reach change, so the change traded one axis of coverage for another');
});

// --- 3. THE CAP IS SHARED, AND HITTING IT IS REPORTED. --------------------
// A walk that stopped early has by definition not looked everywhere, and one
// that stops early while reporting nothing fails OPEN -- the D3 half of #454.
// The identity axis must report it for the same reason the gate does.

check('3.a the identity pass holds no cap of its own', () => {
    // Derived: the deepest depth the SHARED walk delivers must also be a depth
    // the identity pass reaches. A private `FORM_DEPTH_LIMIT` below `MAX_DEPTH`
    // fails here even when it is large enough for today's traffic.
    const deepest = DEPTHS.filter((h) => sharedWalkDelivers(bodyAt(h), NAME)).pop();
    assert.ok(deepest !== undefined,
        'the shared walk delivers nothing at any depth this fixture builds -- the oracle '
        + 'is blind and sections 1 and 3 cannot mean anything');
    const har = harWith(bodyAt(deepest));
    pii.scrubPii(har);
    assert.ok(!survives(bodyOf(har), NAME),
        `the shared walk reaches depth ${deepest} and the identity pass does not. Two caps `
        + 'wearing one name is how this defect comes back one layer further down');
});

check('3.b a payload nested past the shared cap is reported, not silently skipped', () => {
    const tooDeep = nested.MAX_DEPTH + 2;
    const har = harWith(bodyAt(tooDeep));
    const result = pii.scrubPii(har);
    assert.strictEqual(typeof result.depthLimited, 'number',
        'the identity pass does not say whether it stopped before the bottom. An '
        + 'unreported early stop is indistinguishable from a clean walk');
    assert.ok(result.depthLimited > 0,
        `a payload nested ${tooDeep} layers deep did not report hitting the shared cap, so `
        + 'a value below it is reported as absent rather than as unexamined');
});

// --- 4. BYTE PRESERVATION. ------------------------------------------------
// The committed reference is diffed against fresh captures, so re-spelling an
// untouched value churns every such diff for no gain. `transformNested`
// promises the original bytes for an unchanged subtree; routing the identity
// pass through it must keep that promise.

check('4. a parameter the pass did not touch is byte-identical', () => {
    const har = harWith(bodyAt(2));
    har.log.entries[0].request.postData.text += '&untouched=' + enc('a~b c/d');
    pii.scrubPii(har);
    const m = /(?:^|&)untouched=([^&]*)/.exec(bodyOf(har));
    assert.ok(m, 'the untouched parameter is missing from the scrubbed body');
    assert.strictEqual(m[1], enc('a~b c/d'),
        'a parameter with no detected value was round-tripped through a decoder rather '
        + 'than re-emitted as it arrived');
    assert.ok(/(?:^|&)av=1(?:&|$)/.test(bodyOf(har)),
        'a benign neighbour was removed, so sections 1-3 could pass for a pass that '
        + 'simply erases the body');
});

// --- 5. THE SELF-CHECK. ---------------------------------------------------
// Section 1 skips a depth the oracle does not deliver. If the oracle delivered
// nothing past one hop, section 1 would pass while testing nothing at all --
// the failure mode an oracle-driven test has and a hardcoded one does not.

check('5. the oracle really does deliver past one hop', () => {
    const delivered = DEPTHS.filter((h) => sharedWalkDelivers(bodyAt(h), NAME));
    assert.ok(delivered.length > 1,
        'the shared walk delivers the probe at no more than one depth, so section 1 is '
        + 'vacuous. Either the fixture stopped expressing nesting or MAX_DEPTH collapsed');
    assert.ok(delivered.includes(2),
        'the shared walk does not deliver at two hops -- the depth #24 was reported at -- '
        + 'so this file no longer covers the issue it exists for');
});

// --- 6. THE STREAMING PATH AGREES. ----------------------------------------
// Issue #10 split the scrub into detect-then-rewrite so a capture of any size
// can be processed. Two entry points into one pass is another pair that can
// drift, and the whole-document convenience is NOT the one the runner uses.

check('6. the streaming entry point reaches the same depth', () => {
    const har = harWith(bodyAt(2));
    const detections = pii.detectPii(har);
    const plan = pii.planPiiReplacements(detections);
    for (const entry of har.log.entries) {
        pii.applyReplacementsToEntry(entry, plan.replacements);
    }
    assert.ok(!survives(bodyOf(har), NAME),
        'the streaming path reaches less deeply than the whole-document path, so a large '
        + 'capture -- the only kind that takes the streaming path -- is scrubbed less');
});

// --- 7. THE REPLACEMENT TWIN IS LOAD-BEARING. -----------------------------
// Detection and replacement must descend identically. Sections 1-6 can all be
// satisfied by detection alone, because the flat `replaceAll` pass picks up any
// spelling it can match literally -- so without this section a regression that
// disabled the nested REPLACEMENT while leaving detection intact would ship
// green. Proven by ablation: with `replaceNestedLeaf` stubbed to a no-op, the
// file passed 10/10 before this section existed.
//
// The discriminator is the quote in NAME. Escaped inside a JSON string, it is
// unreachable by the flat pass, so a pass here means the descent genuinely
// rewrote a layer.

check('7. a value the flat pass CANNOT match is still replaced at depth', () => {
    // THE PREMISE, stated as the property rather than as a guessed spelling.
    //
    // What section 7 needs is "the flat pass cannot match this literally", and
    // that is exactly: the PLAIN spelling does not occur in the body. Asserting
    // it directly beats predicting the escaped form -- a first cut predicted
    // one escape level and the fixture carries two at this depth, so the
    // premise failed while the code was correct.
    const har = harWith(bodyAt(2));
    const before = bodyOf(har);
    assert.ok(!before.includes(NAME),
        'the PLAIN spelling occurs in the fixture, so the flat replacement pass can match '
        + 'it literally and this section proves nothing about the nested twin');
    assert.ok(sharedWalkDelivers(before, NAME),
        'the value is not reachable as a whole leaf in this fixture, so a pass below '
        + 'would mean the value was never there rather than that it was removed');

    pii.scrubPii(har);
    assert.ok(!survives(bodyOf(har), NAME),
        'a name-keyed value whose nested spelling is JSON-escaped survived. Detection '
        + 'reached it (section 1) and replacement did not, which is the asymmetry that '
        + 'makes the gate report a value the scrubber cannot remove');
});

if (failures > 0) {
    console.error(`pii-nested-reach: ${failures} failure(s)`);
    process.exit(1);
}
console.log('pii-nested-reach: all checks passed');
