#!/usr/bin/env node
// Behavior tests for gate/scrubber PARITY (issue #42).
//
// Zero-dep, runs with `node har-scrub-gate-parity.test.js`. Exits non-zero on
// first failure.
//
// ## What this guards, and why a guard rather than a fix
//
// The gate and the scrubber are two engines that must agree about what a secret
// is. Issue #59 removed the CAUSE of their disagreeing -- one merged policy now
// sources the gate and both verifiers. It did not add anything that FAILS when
// they stop agreeing, and #42 was opened about that absence, after the two
// engines were caught disagreeing in both directions within a few hours:
//
//   - fails OPEN   (#378): the gate could not decode what the scrubber had
//                          already decoded, and blessed a live login capture.
//   - fails CLOSED (#395): the gate detected a shape the scrubber had no rule
//                          for, so the capture was refused with no remedy.
//
// A guard that only prevents "fails open" would have caught #378 and missed
// #395, so all four directions below are asserted, each DERIVED from the live
// source rather than from a list this file keeps:
//
//   1. every kind the gate can report has a scrub path      (anti-fails-closed)
//   2. every kind the scrubber rewrites is policy-declared  (governability)
//   3. every kind the scrubber rewrites is gate-detected    (anti-fails-open)
//
// ## The falsifier
//
// Add a kind to one side and to no other file, and a test here must fail AND
// NAME THE KIND. Ablate by removing a kind from the scrubber's table and confirm
// the message says which one. Both are exercised below as live self-checks
// (section 4) rather than described, so this file cannot rot into a test that
// passes because it stopped looking.
//
// ## Why an exception table, and why it is not a suppression list
//
// Some asymmetries are correct. An identity kind matched only by field name has
// no shape for the gate to detect; a kind whose shape rule would drown the
// report in server addresses is deliberately left to the name path. Those must
// be DECLARED AND NAMED in one place, because #395's cost was not the gap -- it
// was that the gap was indistinguishable from an oversight.
//
// The table is only ever consulted to EXCUSE a gap, never as a source of truth,
// and section 3b asserts every entry is still LIVE: an exception for a gap that
// no longer exists fails, naming the kind. So the table cannot accumulate rows
// that quietly stop meaning anything, which is the one failure mode #395's own
// pinned test could not see past its single shape.

'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const sanitize = require(path.join(__dirname, 'sanitize-har.js'));
const pii = require(path.join(__dirname, 'pii.js'));
const shapes = require(path.join(__dirname, 'har-shapes.js'));
const secrets = require(path.join(__dirname, 'har-secrets.js'));
const policyModule = require(path.join(__dirname, 'har-policy.js'));
const exceptions = require(path.join(__dirname, 'har-parity-exceptions.js'));

// --- The three live sources. Nothing below hard-codes a kind list. ---

// What the scrubber rewrites, across BOTH of its paths. Counting only one is how
// a reviewer concludes five gated shapes are never scrubbed: the shape secrets
// are rewritten by `PATTERNS` in sanitize-har.js, and the name/PII kinds by
// pii.js. A parity check that reads one path reports the other as a hole.
const scrubShapeKinds = new Set(sanitize.PATTERNS.map((p) => p.kind));
const scrubPiiKinds = new Set(Array.from(pii.PII_TYPES));
const scrubbed = new Set([...scrubShapeKinds, ...scrubPiiKinds]);

// What the gate can report by SHAPE.
const gated = new Set(shapes.LEAK_PATTERNS.map((p) => p.name));

// --- The THIRD engine, and why it cannot be compared by kind ---
//
// There are three engines, not two. `har-secrets.js` matches by FIELD NAME,
// driven by the policy's own `secretFields` / `secretHeaders`, and it is what
// both the gate and the scrubber use for the `named-credential` class -- session
// cookies, `password`, `enc_password`. That is the engine behind #378, the
// fails-open incident this guard's own commit message cites.
//
// A first version of this file compared only the shape tables and the PII kind
// list. `named-credential` is declared in the policy and appears in NEITHER of
// those, so it could not surface as a gap in any direction, and the engine tied
// to the cited regression was the one engine the guard did not watch. Caught in
// independent review, and the reason section 4b exists.
//
// It cannot be checked the same way: its vocabulary is field NAMES, not kinds, so
// there is no kind list to intersect. So it is probed BEHAVIORALLY instead --
// both sides are asked to act on a policy-declared secret name, and the class
// counts as implemented only if both do.
// The FOURTH engine is deliberately not checked here. `har-literals.js` rewrites
// the operator's own literal values from the gitignored `.har-profile.json`,
// matching encoded, percent and hex spellings of a specific secret VALUE. It has
// no kind and no policy class -- its input is arbitrary operator-supplied
// literals -- so there is nothing in `policy.classes` for direction 4 to compare
// it against, and no vocabulary for directions 1-3 to intersect. Excluded because
// the policy does not govern it, not because it was overlooked.
const NAME_BASED_CLASS = 'named-credential';

// A value that is not a redaction sentinel, so the walk must treat it as live.
const PROBE_VALUE = 'parity-probe-live-value';

// A field name no shipped policy declares, and the value under it. Used to prove
// that the policy this file wrote is the policy the scrub subprocess actually
// read -- see the canary comment in the scrub half below.
const CANARY_FIELD = 'zz_parity_canary_secret';
const CANARY_VALUE = 'parity-canary-live-value';

// The keys a `.har-policy.project.json` is allowed to declare. A loaded policy
// object also carries derived keys, and the loader refuses a project file that
// names one, so the merged policy cannot simply be written back out whole.
const DECLARABLE_POLICY_KEYS = [
    'schemaVersion', 'classes', 'identifierFields', 'secretFields', 'secretHeaders',
    'notSecretFields', 'piiFields', 'cardIssuers', 'waivers',
];

function nameEngineLive() {
    const policyNames = Array.isArray(policy.secretFields) ? policy.secretFields : [];
    // A literal name, not a pattern: `secretFields` may carry regex spellings,
    // and a probe must exercise the predicate, not the policy's syntax.
    const probeName = policyNames.find((n) => typeof n === 'string' && /^[a-z_][a-z0-9_]*$/i.test(n));
    if (!probeName) return { scrub: false, gate: false, probeName: null, failure: 'no literal secret field name in the policy to probe with' };

    // --- Gate half: does the walk REPORT an unredacted value under that name? ---
    //
    // The options bag matters, and getting it wrong was a real defect here. The
    // walk reads `options.policy`, and an absent policy means the module's
    // HARDCODED defaults -- so passing `policy` directly, as an earlier draft
    // did, made the probe report success from the shipped list while never
    // consulting the merged policy at all. This must be spelled exactly as the
    // production caller in har-gate.js spells it, or the probe verifies a
    // different code path than the one that runs.
    let gate = false;
    const entry = { request: { postData: { text: JSON.stringify({ [probeName]: PROBE_VALUE }) } } };
    try {
        secrets.walkForUnredactedSecrets(entry, () => { gate = true; }, { policy });
    } catch {
        gate = false;
    }

    // --- Scrub half: does a real document come back REWRITTEN? ---
    //
    // Not `isKnownSecretField`. That is a decision predicate, and it is not the
    // path that rewrites a JSON body: the body scrub compiles its own field
    // regex from `policy.secretFields` by alternation, a separate mechanism. So
    // a bug in that compilation would ship the value unredacted while the
    // predicate still answered "yes, secret" -- the guard would report the
    // engine live while captures leaked. The only oracle that cannot be fooled
    // that way is the scrubbed document itself.
    let scrub = false;
    let failure = '';
    const work = fs.mkdtempSync(path.join(os.tmpdir(), 'har-parity-'));
    try {
        const har = {
            log: {
                version: '1.2',
                entries: [{
                    request: {
                        method: 'POST',
                        url: 'https://example.invalid/probe',
                        headers: [{ name: 'content-type', value: 'application/json' }],
                        postData: {
                            mimeType: 'application/json',
                            text: JSON.stringify({ [probeName]: PROBE_VALUE, [CANARY_FIELD]: CANARY_VALUE }),
                        },
                    },
                    response: { status: 200, headers: [], content: { mimeType: 'application/json', text: '{}' } },
                }],
            },
        };
        const inPath = path.join(work, 'in.har');
        const outPath = path.join(work, 'out.har');
        const profilePath = path.join(work, '.har-profile.json');
        fs.writeFileSync(inPath, JSON.stringify(har));
        fs.writeFileSync(profilePath, JSON.stringify({ salt: 'parity-probe-salt', literals: {} }));

        // --- Hand the subprocess THIS policy, and prove that it arrived ---
        //
        // `sanitize-har.js` has no policy flag: it rediscovers policy by walking
        // upward from the INPUT FILE's own directory. A capture written to a temp
        // directory therefore sees only the shipped defaults, so without this the
        // probe could never check that a consuming project's own `secretFields`
        // reach the scrub -- which is the entire promise of the merged policy, and
        // the half of it this guard is supposed to be watching.
        //
        // So the merged policy is written beside the capture as a project file,
        // restricted to the keys a project file may declare (the loaded object
        // also carries derived keys, and the loader rejects those by name).
        //
        // The CANARY is what makes this verifiable rather than hopeful: a field
        // name that appears in NO shipped default, present only in the policy
        // written here. If the subprocess redacts it, the policy provably arrived.
        // If policy discovery ever stops reaching the capture, the canary survives
        // and this check fails -- rather than passing because the defaults happened
        // to cover the other probe name.
        const projectPolicy = {};
        for (const key of DECLARABLE_POLICY_KEYS) {
            if (policy[key] !== undefined) projectPolicy[key] = policy[key];
        }
        projectPolicy.secretFields = [...(projectPolicy.secretFields || []), CANARY_FIELD];
        fs.writeFileSync(path.join(work, '.har-policy.project.json'), JSON.stringify(projectPolicy));
        execFileSync(process.execPath, [
            path.join(__dirname, 'sanitize-har.js'),
            '--in', inPath, '--out', outPath,
            '--subs', path.join(work, 'subs.json'),
            // Both tables must be named explicitly. The scrub REFUSES to write a
            // substitution table to a default path outside a git work tree --
            // its keys are the plaintext values it replaced, so an uncommittable
            // location is not optional. A probe that omits this gets a non-zero
            // exit and would read as "the scrubber is leaking", which is exactly
            // the false alarm this comment exists to prevent.
            '--pii-subs', path.join(work, 'pii-subs.json'),
            '--profile', profilePath,
            '--fixed-time', '2026-01-01T00:00:00.000Z',
        ], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
        const scrubbedText = fs.readFileSync(outPath, 'utf8');
        // The body must still BE there. A pipeline that dropped request bodies
        // would leave no value to find, and "the value is absent" would then be
        // true for a reason that proves nothing about the scrub -- a vacuous pass
        // dressed as a clean capture.
        const body = JSON.parse(scrubbedText).log.entries[0].request.postData;
        if (!body || typeof body.text !== 'string' || !body.text.includes(probeName)) {
            failure = 'the scrubbed capture no longer carries the probe field at all, so this ' +
                'check cannot tell a redacted value from a discarded body';
        } else if (body.text.includes(CANARY_VALUE)) {
            // Checked FIRST: if the policy never arrived, the other result below
            // says nothing about the merged policy, only about the defaults.
            failure = 'the value under the canary field survived, so the policy written beside the ' +
                'capture never reached the scrub -- policy discovery walks up from the input ' +
                'file\'s directory, and this check can no longer see a consuming project\'s ' +
                'own secretFields at all';
        } else if (body.text.includes(PROBE_VALUE)) {
            failure = 'the scrubbed capture still carries the probe value under that field';
        } else {
            scrub = true;
        }
    } catch (err) {
        // A crashed scrub and a leaked value are different emergencies, and an
        // earlier draft reported both as "the scrubber is leaking". Kept apart so
        // the message names what actually happened.
        failure = `the scrub did not complete: ${err && err.message ? String(err.message).split('\n')[0] : 'unknown error'}`;
    } finally {
        fs.rmSync(work, { recursive: true, force: true });
    }

    return { scrub, gate, probeName, failure };
}

// What the merged policy declares, so a consumer can tune it and a waiver can
// cover it.
const policy = policyModule.loadPolicy();
const declared = new Set();
for (const className of Object.keys(policy.classes || {})) {
    for (const kind of Object.keys(policy.classes[className])) declared.add(kind);
}

assert.ok(
    scrubShapeKinds.size > 0 && scrubPiiKinds.size > 0 && gated.size > 0 && declared.size > 0,
    '0.a: a source list came back empty, so every parity assertion below would pass vacuously -- ' +
        'that is the broken-oracle failure this check exists to prevent',
);

// Everything any engine actually acts on. The name-based class joins this set
// only when BOTH of its halves are live -- if either side stopped acting, the
// class is no longer implemented, and direction 4 says so.
const nameEngine = nameEngineLive();
const implemented = new Set([...gated, ...scrubbed]);
if (nameEngine.scrub && nameEngine.gate) implemented.add(NAME_BASED_CLASS);

// --- 0b. The name-based engine: both halves live, named separately. ---
// Runs BEFORE the direction loop on purpose. Direction 4 also fails when this class
// stops being implemented, but it cannot
// say WHICH side dropped, and "the gate stopped verifying" and "the scrubber
// stopped rewriting" are opposite emergencies: one fails open, the other fails
// closed. Asserted individually so the message tells the operator which.
{
    assert.ok(
        nameEngine.probeName,
        '0b.a: the policy declares no literal secret field name to probe with, so the name-based ' +
            'engine cannot be checked at all -- either the policy lost its secretFields or they ' +
            'are all patterns, and this guard is blind to the engine behind #378',
    );
    assert.ok(
        nameEngine.scrub,
        `0b.b: the scrubber did not produce a clean capture for '${nameEngine.probeName}', a name ` +
            `the merged policy declares as a secret field -- ${nameEngine.failure}. Asserted ` +
            `against the scrubbed DOCUMENT, not against the decision predicate, because the ` +
            `JSON-body scrub compiles its own field regex and can break while the predicate still ` +
            `answers "secret"`,
    );
    assert.ok(
        nameEngine.gate,
        `0b.c: the gate's walk does not report an unredacted value under '${nameEngine.probeName}', ` +
            `a name the merged policy declares as a secret field -- nothing verifies that the ` +
            `name-based scrub happened, so a scrub that silently stops yields a capture carrying a ` +
            `live credential that passes as verified. This is #378, exactly`,
    );
}

// The ONE spelling of every gap predicate. Both the real DIRECTIONS below and
// the ablations in section 4 call this, so the falsifier cannot end up proving a
// parallel copy of the logic instead of the logic that runs.
function gapsFor(gatedSet, scrubbedSet, declaredSet, implementedSet) {
    return {
        one: [...gatedSet].filter((k) => !scrubbedSet.has(k)),
        two: [...scrubbedSet].filter((k) => !declaredSet.has(k)),
        three: [...scrubbedSet].filter((k) => !gatedSet.has(k)),
        four: [...declaredSet].filter((k) => !implementedSet.has(k)),
    };
}

const GAPS = gapsFor(gated, scrubbed, declared, implemented);

// Each direction names the gap key that may excuse it, so a row filed against
// the wrong direction cannot silently excuse a different gap.
const DIRECTIONS = [
    {
        id: '1',
        gap: 'gated-not-scrubbed',
        kinds: GAPS.one,
        why:
            'the gate can report this kind and no scrub path rewrites it, so a capture carrying ' +
            'one is REFUSED with no automatic remedy -- the fails-closed defect of #395',
    },
    {
        id: '2',
        gap: 'scrubbed-not-declared',
        kinds: GAPS.two,
        why:
            'the scrubber rewrites this kind but no policy class names it, so no consuming ' +
            'project can tune it and no waiver can cover it -- the scrub is ungovernable',
    },
    {
        id: '3',
        gap: 'scrubbed-not-gated',
        kinds: GAPS.three,
        why:
            'the scrubber rewrites this kind but the gate cannot detect it, so nothing verifies ' +
            'the rewrite happened -- a rule that silently stops firing fails OPEN',
    },
    {
        // The completeness direction, and the one that does not need to know how
        // many engines there are. The policy is the single definition #59
        // established, so anything it declares must be implemented SOMEWHERE. A
        // class no engine implements is a rule the operator can read, tune and
        // waive that does nothing at all -- and it is how `named-credential`
        // escaped the first three directions entirely.
        id: '4',
        gap: 'declared-not-implemented',
        kinds: GAPS.four,
        why:
            'the policy declares this class and no engine implements it, so a consumer can read ' +
            'and tune a rule that does nothing -- and no other direction can see it, because the ' +
            'kind is absent from every table the others compare',
    },
];

// --- 1-4. Every gap is either absent or declared, with a reason. ---
{
    for (const direction of DIRECTIONS) {
        for (const kind of direction.kinds) {
            const entry = exceptions.find(direction.gap, kind);
            assert.ok(
                entry,
                `${direction.id}.a: '${kind}' is an undeclared ${direction.gap} gap -- ` +
                    `${direction.why}. Either close the gap, or declare it in ` +
                    `har-parity-exceptions.js with a reason (by-design) or an issue (tracked-by). ` +
                    `An undeclared gap is indistinguishable from an oversight, which is the whole ` +
                    `cost this issue was opened about`,
            );
            assert.ok(
                typeof entry.reason === 'string' && entry.reason.trim().length > 0,
                `${direction.id}.b: the '${kind}' exception carries no reason -- an exception ` +
                    `without one is a suppression, and a reviewer cannot tell it from a mistake`,
            );
            assert.ok(
                entry.disposition === 'by-design' || /^#\d+$/.test(String(entry.trackedBy || '')),
                `${direction.id}.c: the '${kind}' exception is neither 'by-design' nor tracked by ` +
                    `an issue -- a gap nobody owns is how this issue became necessary`,
            );
        }
    }
}

// --- 3b. No exception outlives its gap. ---
// The failure mode a hand-maintained waiver list always reaches: the gap is
// fixed, the row stays, and the next real instance of the same gap is excused by
// it. Asserting liveness makes the table self-pruning -- when a tracked issue
// lands, CI goes red until its row is deleted.
{
    const live = new Map(DIRECTIONS.map((d) => [d.gap, new Set(d.kinds)]));
    for (const entry of exceptions.all()) {
        assert.ok(
            live.has(entry.gap),
            `3b.a: the exception for '${entry.kindName}' names gap '${entry.gap}', which is not a ` +
                `direction this test measures -- a typo here excuses nothing and hides a real gap`,
        );
        assert.ok(
            live.get(entry.gap).has(entry.kindName),
            `3b.b: '${entry.kindName}' is declared as a '${entry.gap}' exception but is no longer ` +
                `a gap -- delete the row. A stale exception will excuse the next real instance`,
        );
    }
}

// --- 4. The falsifier, exercised rather than described. ---
// Both ablations the issue asks for, run against the REAL predicate with
// synthetic sources, so this file cannot pass by having stopped looking.
//
// `gapsFor` is the one spelling of the gap predicate; DIRECTIONS above is built
// from it too. An earlier draft re-spelled the filters here, which meant this
// section verified a parallel copy that could drift from the assertions it is
// supposed to be proving -- flagged in review, and collapsed to one definition.
{
    // Add a kind to the gate and to no other file: direction 1 must see it.
    const added = gapsFor(new Set([...gated, 'zz-new-shape']), scrubbed, declared, implemented);
    assert.ok(
        added.one.includes('zz-new-shape'),
        '4.a: a kind added to the gate alone was not reported as a gated-not-scrubbed gap -- the ' +
            'anti-fails-closed direction is not actually measuring anything',
    );

    // Remove a kind from the scrubber: direction 1 must name that kind.
    const bothSides = [...scrubbed].find((k) => gated.has(k));
    assert.ok(
        bothSides,
        '4.b: no kind is both scrubbed and gated, so the ablation below cannot run -- the sources ' +
            'are not what this test thinks they are',
    );
    const ablated = gapsFor(gated, new Set([...scrubbed].filter((k) => k !== bothSides)), declared, implemented);
    assert.ok(
        ablated.one.includes(bothSides),
        `4.c: removing '${bothSides}' from the scrubber did not make it a gated-not-scrubbed gap ` +
            `-- the ablation the issue asks for does not fail`,
    );

    // A scrub kind with no policy class: direction 2 must see it.
    const ungoverned = gapsFor(gated, new Set([...scrubbed, 'zz-ungoverned']), declared, implemented);
    assert.ok(
        ungoverned.two.includes('zz-ungoverned'),
        '4.d: a scrubbed kind with no policy class was not reported as ungovernable',
    );

    // A policy class no engine implements: direction 4 must see it. This is the
    // one that catches an engine whose vocabulary is not kinds at all, so it is
    // the ablation that would have caught this file's own first draft.
    const unimplemented = gapsFor(gated, scrubbed, new Set([...declared, 'zz-declared-only']), implemented);
    assert.ok(
        unimplemented.four.includes('zz-declared-only'),
        '4.e: a policy class no engine implements was not reported -- direction 4 is the only ' +
            'check that can see an engine matching by field name rather than by kind, and it is ' +
            'not measuring anything',
    );

    // And the real class, not a synthetic name: the name-based class must be in
    // the implemented set, which is only true because BOTH of its halves are
    // live. If this ever fails while 0b passed, the two disagree about what live
    // means, and the guard is lying somewhere.
    assert.ok(
        implemented.has(NAME_BASED_CLASS),
        `4.f: '${NAME_BASED_CLASS}' is declared by the policy and absent from the implemented ` +
            `set, so direction 4 is carrying a real gap that section 0b should already have ` +
            `failed on -- the two checks disagree about whether the engine is live`,
    );
}

// --- 5. The report never carries a value. ---
// This file reads pattern tables and kind names, never captures, so the only way
// a value could reach the output is via a sample someone adds later. Pinned so
// that stops being possible by accident.
{
    for (const direction of DIRECTIONS) {
        for (const kind of direction.kinds) {
            assert.ok(
                /^[a-z0-9-]+$/.test(kind),
                `5.a: '${kind}' is not a bare kind name -- this report must carry kinds and class ` +
                    `names only, never a detected value`,
            );
        }
    }
}

console.log('All har-scrub-gate-parity tests passed');
