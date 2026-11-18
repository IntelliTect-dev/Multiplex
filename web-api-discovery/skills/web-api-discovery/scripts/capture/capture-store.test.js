#!/usr/bin/env node
// Behavior tests for the one walk over a capture store (issues #386 / #387).
//
// WHAT THESE PIN, AND WHY EACH IS A SEPARATE ASSERTION.
//
// The walk that already existed -- `listSessionDirs`, the enumeration
// `resolveSession` uses -- understood exactly one shape: `<root>/<host>/<stamp>`
// holding a `session.json`. Measured against a real 88-capture store, that
// shape silently misses two whole populations, and "silently" is the defect:
// the operator asks for the store and gets a subset with nothing said.
//
//   LEGACY   six date-stamped session directories at the captures ROOT,
//            written before the host layer existed. A host-scoped walk never
//            descends to them.
//   FOREIGN  five mitmproxy dumps with a `raw.mitm` and no `session.json`.
//            They must be NAMED and DECLINED -- not fed to a HAR scrub that
//            cannot say whose traffic it is, and not passed over in silence
//            either.
//
// A COUNT WOULD NOT CATCH ANY OF THIS. "5 captures found" is equally true when
// the five are the wrong five, so every assertion below names the CLASS a
// specific directory landed in and fails by naming the class it should have
// had.
//
// The legacy/current distinction is tested from two directions on purpose:
// classifying by DEPTH FROM THE WALK'S START would call every capture "legacy"
// the moment an operator narrowed to one host folder -- which is the supported
// way to filter by host, so it would be wrong most of the time it mattered.
//
// Zero-dep, runs with `node capture-store.test.js`.

'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');

const store = require(path.join(__dirname, 'capture-store.js'));
const captureHar = require(path.join(__dirname, 'capture-har.js'));

const failures = [];
// Promises an async assertion returns, awaited before the verdict is printed.
const pending = [];
function section(name, fn) {
    try { fn(); } catch (e) {
        failures.push(`[block ${name}] ` + (e && e.message ? e.message : String(e)));
    }
}

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'capture-store-'));

function writeHar(p) {
    fs.writeFileSync(p, JSON.stringify({
        log: { version: '1.2', creator: { name: 'fixture', version: '1' }, entries: [] }
    }));
}

/** A capture directory. `opts.session` false makes it somebody else's capture. */
function makeCapture(dir, opts = {}) {
    fs.mkdirSync(dir, { recursive: true });
    if (opts.mitm) fs.writeFileSync(path.join(dir, 'raw.mitm'), 'not-a-har');
    if (opts.raw !== false) writeHar(path.join(dir, 'raw.har'));
    if (opts.session !== false) {
        fs.writeFileSync(path.join(dir, 'session.json'), JSON.stringify({
            uri: 'https://www.example.test/secret-magic-link-token',
            describe: 'fixture',
            sessionDir: dir,
            // A pid that is definitely alive, so `isDriverAlive` says yes. That
            // is the whole point of the never-ended fixtures below: a PID this
            // old is far likelier to have been REUSED than to still be the
            // recorder, and this proves what happens when the check believes it.
            pid: opts.pid,
            profileDir: opts.profileDir,
            endedUtc: opts.ended === false ? undefined : '2026-01-01T00:01:00.000Z'
        }));
    }
    if (opts.scrubbed) writeHar(path.join(dir, 'scrubbed.har'));
    if (opts.rejected) writeHar(path.join(dir, 'scrubbed.rejected.har'));
    if (opts.catalogue) fs.writeFileSync(path.join(dir, 'catalogue.json'), '[]');
    // Fixture content only -- never a real credential. This exercises EXISTENCE
    // reporting, so the file need not even be valid JSON.
    if (opts.legacySubs) fs.writeFileSync(path.join(dir, store.LEGACY_SUBS_FILENAME), '{}');
    if (opts.piiSubs) fs.writeFileSync(path.join(dir, store.PII_SUBS_FILENAME), '{}');
    return dir;
}

// The fixture store, shaped like the real one that was measured.
const root = path.join(tmp, '.har-captures');
const hostA = path.join(root, 'www.example.test');
const hostB = path.join(root, 'www.other.test');

const current = makeCapture(path.join(hostA, '2026-01-02-000001'));
const done = makeCapture(path.join(hostA, '2026-01-02-000002'), { scrubbed: true, catalogue: true });
const quarantined = makeCapture(path.join(hostA, '2026-01-02-000003'), { rejected: true });
// The credential file itself: a legacy-style substitution table beside the
// raw, with no other artifact present. Proves the field reports the table's
// existence on its own terms, not as a side effect of scrubbed/catalogue.
// Stamped BEFORE the other hostA captures -- section 6 pins `quarantined` as
// the newest recorder session by stamp, and these two must not steal that.
const subbed = makeCapture(path.join(hostA, '2026-01-01-500001'), { legacySubs: true });
// The typed-PII table is the OTHER of the two names -- exercised separately so
// neither name alone would make the assertion pass by accident.
const piiSubbed = makeCapture(path.join(hostA, '2026-01-01-500002'), { piiSubs: true });
const legacy = makeCapture(path.join(root, '2026-01-01-000001'));
const mitm = makeCapture(path.join(hostB, '2026-01-03-000001'), { session: false, mitm: true });
// A dump dropped straight under the captures root, with no host layer at all.
const rootMitm = makeCapture(path.join(root, '2020-01-01-mitmdump'), { session: false, mitm: true });
const orphanRaw = makeCapture(path.join(hostB, '2026-01-03-000002'), { session: false });
// A capture this recorder did not make, that ALSO has a substitution table
// beside it -- the shape an operator leaves by running sanitize-har.js by
// hand against an orphan raw.har. Existence is a fact about the directory,
// not about who produced the capture, so this must still report true.
const foreignSubbed = makeCapture(path.join(hostB, '2026-01-03-000003'),
    { session: false, legacySubs: true });

// Directories and files that are NOT captures, which is the population the
// counts depend on being excluded.
fs.mkdirSync(path.join(root, '_analysis'), { recursive: true });
fs.writeFileSync(path.join(root, 'CAPTURES-MANIFEST.md'), '# notes');
fs.mkdirSync(path.join(root, '_rescued'), { recursive: true });
fs.writeFileSync(path.join(root, '_rescued', 'catalogue.json'), '[]');

const byDir = (list) => new Map(list.map((e) => [e.dir, e]));

// ---------------------------------------------------------------------------
// 1 -- the three input classes, each named rather than counted
// ---------------------------------------------------------------------------
section('1', () => {
    const found = byDir(store.listCaptureDirs(root));

    assert.strictEqual(found.get(current) && found.get(current).captureClass, store.CLASS_SESSION,
        '1.a: a <host>/<stamp> capture is not classed as recorder output');

    assert.ok(found.has(legacy),
        '1.b: a date-stamped session directory AT THE CAPTURES ROOT was not found at all -- ' +
        'this is the population a host-scoped walk misses, six of them in the measured store');
    assert.strictEqual(found.get(legacy).captureClass, store.CLASS_LEGACY,
        '1.c: the legacy capture was found but not distinguished from the host layout');

    assert.ok(found.has(mitm),
        '1.d: a mitmproxy capture was skipped SILENTLY -- it must be named and declined');
    assert.strictEqual(found.get(mitm).captureClass, store.CLASS_FOREIGN,
        '1.e: a capture with no session.json was treated as recorder output');
    assert.ok(/raw\.mitm/.test(found.get(mitm).reason),
        '1.f: the declined capture does not say WHY it was declined: ' + found.get(mitm).reason);

    // A raw.har with no session beside it is the same problem wearing the
    // recorder's file name: nothing says whose traffic it is.
    assert.strictEqual(found.get(orphanRaw).captureClass, store.CLASS_FOREIGN,
        '1.g: a raw.har with no session.json was accepted as recorder output');
});

// ---------------------------------------------------------------------------
// 2 -- what is NOT a capture stays out of the counts
// ---------------------------------------------------------------------------
section('2', () => {
    const dirs = store.listCaptureDirs(root).map((e) => e.dir);
    for (const notACapture of ['_analysis', '_rescued']) {
        assert.ok(!dirs.includes(path.join(root, notACapture)),
            `2.a: ${notACapture} has neither a session nor a raw and is not a capture, but it was listed`);
    }
    assert.strictEqual(dirs.length, 10,
        '2.b: the store holds exactly ten captures; found ' + dirs.length + ': ' + dirs.join(', '));
});

// ---------------------------------------------------------------------------
// 3 -- legacy is a fact about the PATH, not about where the walk started
// ---------------------------------------------------------------------------
section('3', () => {
    // Narrowing to one host is how host filtering works -- there is no --host
    // option and there does not need to be. A depth-based classifier would
    // call every one of these "legacy" here.
    const fromHost = byDir(store.listCaptureDirs(hostA));
    assert.strictEqual(fromHost.get(current) && fromHost.get(current).captureClass, store.CLASS_SESSION,
        '3.a: narrowing the walk to a host folder re-classified its captures as legacy');
    assert.strictEqual(fromHost.size, 5,
        '3.b: pointing at one host must find that host\'s captures and no others, found ' + fromHost.size);

    // And the same directory, asked for on its own, is still what it was.
    const fromItself = store.listCaptureDirs(current);
    assert.strictEqual(fromItself.length, 1,
        '3.c: pointing at a single session directory should describe exactly it');
    assert.strictEqual(fromItself[0].captureClass, store.CLASS_SESSION,
        '3.d: a session directory asked for by itself changed class');
    assert.strictEqual(store.listCaptureDirs(legacy)[0].captureClass, store.CLASS_LEGACY,
        '3.e: a legacy capture asked for by itself changed class');
});

// ---------------------------------------------------------------------------
// 3b -- a directory that is DECLINED must not take its contents with it
// ---------------------------------------------------------------------------
section('3b', () => {
    // A stray `raw.har` sitting at HOST level is enough to make that directory
    // look like somebody else's capture. If being declined also stopped the
    // walk descending, one stray file would hide every capture under that host
    // -- silently, which is the failure mode this whole classification exists
    // to remove. Declining is a statement about the directory, never about the
    // tree below it.
    //
    // A recorder session is different and DOES stop the descent: its own
    // scrub output lives inside it, and a `<stamp>/` directory is a leaf by
    // construction.
    fs.writeFileSync(path.join(hostA, 'raw.har'), '{}');
    try {
        const found = byDir(store.listCaptureDirs(root));
        assert.strictEqual(found.get(hostA) && found.get(hostA).captureClass, store.CLASS_FOREIGN,
            '3b.a: a host directory holding a stray raw.har should be declined');
        assert.ok(found.has(current),
            '3b.b: declining the host directory hid every capture underneath it');
        assert.ok(found.has(done) && found.has(quarantined),
            '3b.c: the rest of the host went missing behind one stray file');
    } finally {
        fs.unlinkSync(path.join(hostA, 'raw.har'));
    }
});

// ---------------------------------------------------------------------------
// 4 -- the artifact facts the resume check is built on
// ---------------------------------------------------------------------------
section('4', () => {
    const found = byDir(store.listCaptureDirs(root));
    assert.strictEqual(found.get(done).scrubbed, true, '4.a: an existing scrubbed.har was not reported');
    assert.strictEqual(found.get(done).catalogue, true, '4.b: an existing catalogue.json was not reported');
    assert.strictEqual(found.get(current).scrubbed, false, '4.c: an unprocessed capture reported a scrub');

    // A QUARANTINED SCRUB IS NOT A PROCESSED CAPTURE. There is no verified
    // artifact and nothing was promoted, so `scrubbed` must stay false or the
    // batch would skip exactly the capture the leak gate refused.
    assert.strictEqual(found.get(quarantined).scrubbed, false,
        '4.d: a quarantined scrub was reported as a completed one -- resume would skip a REJECTED capture');
    assert.strictEqual(found.get(quarantined).rejected, true,
        '4.e: the quarantined scrub was not reported at all, so triage has nothing to go on');
});

// ---------------------------------------------------------------------------
// 4c -- the one fact #387 asked for that nothing else computed: a
// substitution table beside the raw
// ---------------------------------------------------------------------------
section('4c', () => {
    const found = byDir(store.listCaptureDirs(root));

    // The literal spellings, pinned. Both the fixtures above and
    // hasSubstitutionTable() read these same exported constants, so without
    // this a renamed constant would pass this whole section while silently
    // no longer recognising a table a real sanitize-har.js run wrote under
    // the ACTUAL name.
    assert.strictEqual(store.LEGACY_SUBS_FILENAME, '.har-substitutions.json',
        '4c.0a: the legacy substitution-table filename drifted from what sanitize-har.js writes');
    assert.strictEqual(store.PII_SUBS_FILENAME, '.substitutions.json',
        '4c.0b: the typed-PII substitution-table filename drifted from what sanitize-har.js writes');

    // Neither canonical name is present: no scrub has ever run here.
    assert.strictEqual(found.get(current).substitutions, false,
        '4c.a: an unprocessed capture with no substitution table reported one existing');

    // The two tables are two DIFFERENT filenames (legacy hash-keyed vs.
    // typed-PII), and each is checked on its own -- either one existing must
    // be enough to report `true`, not only their conjunction.
    assert.strictEqual(found.get(subbed).substitutions, true,
        '4c.b: an existing .har-substitutions.json table was not reported');
    assert.strictEqual(found.get(piiSubbed).substitutions, true,
        '4c.c: an existing .substitutions.json table was not reported');

    // Never the path, never the contents -- existence only. A path would be
    // a location the operator could carry into a shared summary without
    // thinking, and these tables are keyed by the plaintext values a scrub
    // replaced.
    assert.strictEqual(found.get(subbed).substitutionsPath, undefined,
        '4c.d: the inventory carries a substitution-table PATH, not just its existence');
    const serialised = JSON.stringify(found.get(subbed));
    assert.ok(!/\.har-substitutions\.json|\.substitutions\.json/.test(serialised),
        '4c.e: the inventory names the substitution-table FILENAME, which is one step from its path');

    // A DECLINED capture is not exempt. Existence of a substitution table is a
    // fact about the DIRECTORY, not about who made the capture in it -- an
    // operator who ran sanitize-har.js by hand against an orphan raw.har
    // leaves exactly this shape, and a blanket false here would tell them the
    // credential table is not there when it is.
    assert.strictEqual(found.get(foreignSubbed).captureClass, store.CLASS_FOREIGN,
        '4c.f: the foreign fixture with a substitution table was not classed as foreign -- this assertion checks nothing');
    assert.strictEqual(found.get(foreignSubbed).substitutions, true,
        '4c.g: a substitution table beside a DECLINED capture was reported as absent');
    assert.strictEqual(found.get(orphanRaw).substitutions, false,
        '4c.h: a declined capture with no substitution table reported one existing');
});

// ---------------------------------------------------------------------------
// 4b -- a capture at the captures ROOT has no host to name
// ---------------------------------------------------------------------------
section('4b', () => {
    const found = byDir(store.listCaptureDirs(root));
    assert.strictEqual(found.get(legacy).host, null,
        '4b.a: a capture directly under the captures root reported `.har-captures` as its ' +
        'host, which would print in the summary as though it were a site');
    assert.strictEqual(found.get(current).host, 'www.example.test',
        '4b.b: a host-layout capture must still report its host');
    // The question is the LAYOUT, not the class. A declined capture at the root
    // has no host either, and the label built from it must not be told
    // otherwise.
    assert.strictEqual(found.get(rootMitm).host, null,
        '4b.c: a DECLINED capture at the captures root reported a host it does not have');
    assert.strictEqual(found.get(rootMitm).captureClass, store.CLASS_FOREIGN,
        '4b.d: a root-level dump with no session.json must still be declined');
});

// ---------------------------------------------------------------------------
// 5 -- never a captured value
// ---------------------------------------------------------------------------
section('5', () => {
    // The fixture's session.json carries a start URI with a token in its path,
    // which is exactly the shape this pipeline must never echo. Host and stamp
    // are permitted; the URI is not, at any depth of the record.
    const serialised = JSON.stringify(store.listCaptureDirs(root));
    assert.ok(!/secret-magic-link-token/.test(serialised),
        '5.a: the inventory carries the capture start URI, which can hold a live token');
});

// ---------------------------------------------------------------------------
// 6 -- ONE walk: capture-har.js reads this same enumeration
// ---------------------------------------------------------------------------
section('6', () => {
    // The point of #387's narrowing. If these two ever disagree, the store has
    // two notions of what a capture is -- which is the defect the subsystem
    // exists to avoid, and no amount of testing the batch alone would show it.
    const sessions = store.listSessionDirs(root).map((e) => e.dir).sort();
    // ASKED AS THE PREDICATE, not as a list of classes to exclude. This
    // assertion is the guard against the two views disagreeing, and spelling it
    // `!== CLASS_FOREIGN` made the guard carry the very bug the walk was
    // changed to remove: a class added later would have been counted as
    // recorder output here and the disagreement would not have shown.
    const recorderSaw = store.listCaptureDirs(root)
        .filter((e) => store.isRecorderCapture(e.captureClass))
        .map((e) => e.dir).sort();
    assert.deepStrictEqual(sessions, recorderSaw,
        '6.a: the session view and the classified view disagree about the store');

    assert.ok(!sessions.includes(mitm),
        '6.b: a foreign capture reached the recorder\'s session list, where stop/status/catalogue would act on it');
    assert.ok(sessions.includes(legacy),
        '6.c: a legacy capture is still invisible to the recorder\'s own enumeration');

    // And `resolveSession` -- the function every entry point resolves through
    // -- answers over the same population. Newest by STAMP, so the legacy
    // capture (an older stamp) must not win.
    const resolved = captureHar.resolveSession({ dir: root });
    assert.ok(sessions.includes(resolved),
        '6.d: resolveSession answered with something the shared walk does not list: ' + resolved);
    // 2026-01-02-000003 is the newest RECORDER session; the two 2026-01-03
    // directories are foreign and must not be resolvable at all.
    assert.strictEqual(resolved, quarantined,
        '6.e: resolveSession no longer picks the newest recorder capture by stamp, got ' + resolved);
});

// ---------------------------------------------------------------------------
// 7 -- making legacy captures VISIBLE must not make them LIVE
// ---------------------------------------------------------------------------
//
// Before this walk moved, a session directory at the captures root was
// invisible to `resolveSession` and `findProfileConflict`. Making it visible is
// the fix; making it a candidate for "a recorder is running right now" is a
// side effect nobody asked for, and a dangerous one.
//
// `isDriverAlive` is a bare `process.kill(pid, 0)`. It cannot tell a running
// recorder from an unrelated process that inherited the pid, and a legacy
// capture is by definition old -- the host layer has existed for every capture
// written since. So a years-old interrupted legacy session can claim to be live
// on a reused pid, and would then shadow the recording the operator is actually
// sitting in front of, or refuse a new capture as a profile conflict.
//
// The layout itself settles it: a LIVE recorder writes `<host>/<stamp>`. A
// session at the root cannot be one.
section('7', () => {
    const live = fs.mkdtempSync(path.join(os.tmpdir(), 'capture-store-live-'));
    try {
        const liveRoot = path.join(live, '.har-captures');
        const profile = path.join(live, 'profile');
        // A legacy session that never ended, on a pid that is certainly alive.
        const zombie = makeCapture(path.join(liveRoot, '2020-01-01-000001'),
            { ended: false, pid: process.pid, profileDir: profile });
        // And a newer, properly ended capture in the current layout.
        const newer = makeCapture(path.join(liveRoot, 'www.example.test', '2026-01-02-000001'));

        assert.strictEqual(captureHar.resolveSession({ dir: liveRoot }), newer,
            '7.a: a legacy session claiming to be live shadowed the newest real capture -- ' +
            'stop/status/catalogue would act on the wrong one');

        pending.push(captureHar.findProfileConflict(profile, liveRoot).then((conflict) => {
            assert.strictEqual(conflict, null,
                '7.b: a legacy session on a reused pid was reported as a live profile conflict, ' +
                'which refuses a new capture the operator is entitled to start');
        }).catch((e) => {
            failures.push('[block 7] ' + (e && e.message ? e.message : String(e)));
        }).finally(() => {
            fs.rmSync(live, { recursive: true, force: true });
        }));
    } catch (e) {
        fs.rmSync(live, { recursive: true, force: true });
        throw e;
    }
});

// A post-processing failure the recorder wrote down must be READABLE from the
// inventory, not merely present in session.json (issue #450).
//
// The capture that motivated the issue recorded both signals on the day it was
// captured -- a summary the recorder could not parse back, and a sanitize-har
// error -- and was then described eight days later as the most evidence-dense
// unclaimed capture in the tree, because the one walk that looks at every
// capture read session.json and ignored both fields. A capture whose scrub
// never ran was listed exactly like one not scrubbed yet.
section('recorded post-processing failures are carried into the inventory', () => {
    const dir = path.join(hostB, '2026-08-28-032544');
    fs.mkdirSync(dir, { recursive: true });
    writeHar(path.join(dir, 'raw.har'));
    fs.writeFileSync(path.join(dir, 'session.json'), JSON.stringify({
        sessionDir: dir,
        endedUtc: '2026-08-28T03:50:00.000Z',
        summary: { bytes: 1714570316, parseError: 'Cannot create a string longer than 0x1fffffe8 characters' },
        postProcess: {
            errors: ['sanitize-har: cannot read raw.har: Cannot create a string longer than 0x1fffffe8 characters'],
            scrubbed: { path: null, verified: false }
        }
    }));

    const entry = store.describeCaptureDir(dir);
    assert.deepStrictEqual(entry.postProcessErrors, [
        'sanitize-har: cannot read raw.har: Cannot create a string longer than 0x1fffffe8 characters'
    ], 'the recorded stage failure is carried');
    assert.strictEqual(entry.summaryParseError,
        'Cannot create a string longer than 0x1fffffe8 characters',
        'so is the failure to read back what was just written');
    // The point of the issue: this is what made the failure invisible. The
    // capture looks unscrubbed either way, so `scrubbed` cannot carry it.
    assert.strictEqual(entry.scrubbed, false,
        'and it remains indistinguishable from not-yet-scrubbed by that field alone');

    // It must reach the inventory a batch driver and an agent actually read,
    // not only a direct call.
    const listed = store.listCaptureDirs(root).find((e) => e.dir === dir);
    assert.ok(listed, 'the failed capture is in the store listing');
    assert.strictEqual(listed.postProcessErrors.length, 1, 'with its error');
    assert.strictEqual(listed.summaryParseError,
        'Cannot create a string longer than 0x1fffffe8 characters');
});

section('a capture with nothing recorded reports nothing, not a false alarm', () => {
    // The ablation for the block above: if these fields were populated
    // unconditionally, the assertions there would pass on any capture and prove
    // nothing. A clean capture must be quiet.
    const clean = store.describeCaptureDir(done);
    assert.deepStrictEqual(clean.postProcessErrors, [], 'a clean capture carries no errors');
    assert.strictEqual(clean.summaryParseError, null, 'and no parse failure');

    // A capture somebody else made has no session.json to record anything in,
    // so the honest answer is empty rather than "no failures".
    const foreign = store.describeCaptureDir(mitm);
    assert.strictEqual(foreign.captureClass, store.CLASS_FOREIGN);
    assert.deepStrictEqual(foreign.postProcessErrors, []);
    assert.strictEqual(foreign.summaryParseError, null);
});

section('a malformed or older session.json does not crash the walk', () => {
    // This file is read from disk and may predate the fields, or have been
    // hand-edited. A walk that throws here takes the whole store inventory with
    // it -- which would be a worse failure than the one being fixed.
    const dir = path.join(hostB, '2026-08-29-000001');
    fs.mkdirSync(dir, { recursive: true });
    writeHar(path.join(dir, 'raw.har'));
    fs.writeFileSync(path.join(dir, 'session.json'), JSON.stringify({
        sessionDir: dir,
        endedUtc: '2026-08-29T00:00:00.000Z',
        summary: 'not an object',
        postProcess: { errors: 'not an array' }
    }));
    const entry = store.describeCaptureDir(dir);
    assert.deepStrictEqual(entry.postProcessErrors, []);
    assert.strictEqual(entry.summaryParseError, null);
});

section('a hand-imported capture is described, not merely unrecognised', () => {
    // THE FINDING (#102). Measured in a consuming repo on 2026-10-14: of 122
    // entries, 6 were `foreign` -- and all 6 were phone-app captures somebody
    // had imported on purpose, each with a hand-written `CAPTURE.md` saying
    // what it was. The walk read none of it, so an operator asking "what have
    // I recorded?" was told six directories were unrecognised while the answer
    // sat in a file beside each raw.
    //
    // `foreign` was never wrong about PROVENANCE. It was the only thing being
    // said, and provenance is not the question the list is asked.
    const imported = path.join(tmp, 'imported', '.har-captures', 'phone.example.test',
        '2026-02-01-000001');
    fs.mkdirSync(imported, { recursive: true });
    fs.writeFileSync(path.join(imported, 'raw.mitm'), 'not-a-har');
    fs.writeFileSync(path.join(imported, 'CAPTURE.md'),
        '# Grocery app checkout, iPhone, mitmproxy\n\nTapped through the basket.\n');

    const entry = store.describeCaptureDir(imported);
    assert.strictEqual(entry.captureClass, store.CLASS_IMPORTED,
        'a raw with a CAPTURE.md beside it was imported deliberately, and reads as `' +
        entry.captureClass + '`');
    assert.strictEqual(entry.describe, 'Grocery app checkout, iPhone, mitmproxy',
        'the description is the first heading of CAPTURE.md, got: ' + entry.describe);

    // AND IT IS STILL NOT RECORDER OUTPUT. This is the whole risk of adding a
    // class: there is no session.json, so nothing can say whose traffic this
    // is, and a HAR scrub that cannot attribute what it scrubbed must still
    // decline it. Being described changes what the LIST says, never what the
    // pipeline will touch.
    assert.strictEqual(entry.recorderOutput, false,
        'an imported capture has no session to attribute its traffic to and must not ' +
        'read as recorder output');
    assert.strictEqual(entry.scrubbed, false, 'and nothing claims this pipeline processed it');
    assert.strictEqual(entry.ended, false);
    assert.deepStrictEqual(entry.postProcessErrors, []);

    // AND IT HAS A SIZE. `rawBytes` is null for a capture with no session only
    // because `raw` is -- and `raw` is null on purpose, so nothing can hand a
    // scrub a path to a file it has no provenance for. The SIZE is a fact about
    // the directory rather than about who produced the capture in it, exactly
    // as the substitution-table flag is, and it is the number an operator reads
    // to decide what to keep. A multi-gigabyte mitm dump listed as "--" is the
    // failure mode #102 exists to remove.
    assert.ok(entry.rawBytes > 0,
        "an imported capture reports the size of its raw, got: " + entry.rawBytes);
    assert.strictEqual(entry.raw, null,
        "and still hands nobody a path to a capture it cannot attribute");

    // A bare raw with no notes beside it is unchanged: nobody said anything
    // about it, so there is nothing to report but that it is not ours.
    const bare = path.join(tmp, 'imported', '.har-captures', 'phone.example.test',
        '2026-02-01-000002');
    fs.mkdirSync(bare, { recursive: true });
    fs.writeFileSync(path.join(bare, 'raw.mitm'), 'not-a-har');
    const bareEntry = store.describeCaptureDir(bare);
    assert.strictEqual(bareEntry.captureClass, store.CLASS_FOREIGN,
        'a raw with nothing written beside it is still foreign');
    assert.strictEqual(bareEntry.describe, null);
    assert.strictEqual(bareEntry.recorderOutput, false);
    assert.ok(bareEntry.rawBytes > 0,
        "and a bare foreign capture reports its size too -- the question is the file on disk, " +
        "not whether anybody described it");

    // A recorder session is the other side of the predicate, and it has to be
    // asserted or `recorderOutput` could be hard-coded false and pass above.
    assert.strictEqual(store.describeCaptureDir(current).recorderOutput, true,
        'a <host>/<stamp> session IS recorder output');
    assert.strictEqual(store.describeCaptureDir(legacy).recorderOutput, true,
        'and so is a legacy capture at the captures root');

    // Both reach the listing a batch driver and an agent read, not only a
    // direct call.
    const listed = byDir(store.listCaptureDirs(path.dirname(imported)));
    assert.strictEqual(listed.get(imported).captureClass, store.CLASS_IMPORTED);
    assert.strictEqual(listed.get(bare).captureClass, store.CLASS_FOREIGN);
});

section('a description with no heading, and one nobody should have to read', () => {
    const base = path.join(tmp, 'notes', '.har-captures', 'phone.example.test');

    // No heading: the first line somebody actually wrote is the description.
    // A `CAPTURE.md` is hand-written, so requiring markdown of it would turn
    // "they wrote it down" into "they wrote it down correctly".
    const plain = path.join(base, '2026-03-01-000001');
    fs.mkdirSync(plain, { recursive: true });
    fs.writeFileSync(path.join(plain, 'raw.mitm'), 'not-a-har');
    fs.writeFileSync(path.join(plain, 'CAPTURE.md'), '\n\nBanking app, Android, login only\n');
    assert.strictEqual(store.describeCaptureDir(plain).describe, 'Banking app, Android, login only');

    // An EMPTY notes file is not a description. Classing this `imported` on the
    // file's existence alone would report a described capture whose description
    // is blank -- which is the state #102 exists to make visible, not to
    // manufacture.
    const empty = path.join(base, '2026-03-01-000002');
    fs.mkdirSync(empty, { recursive: true });
    fs.writeFileSync(path.join(empty, 'raw.mitm'), 'not-a-har');
    fs.writeFileSync(path.join(empty, 'CAPTURE.md'), '#\n\n   \n');
    const emptyEntry = store.describeCaptureDir(empty);
    assert.strictEqual(emptyEntry.describe, null, 'an empty CAPTURE.md says nothing');
    assert.strictEqual(emptyEntry.captureClass, store.CLASS_FOREIGN,
        'and a capture with nothing said about it is not a described import');

    // A long line is CAPPED and a multi-line one is FLATTENED. This string goes
    // into a console table and into an agent's context; a notes file with a
    // 40 KB first line would take the whole listing with it.
    const long = path.join(base, '2026-03-01-000003');
    fs.mkdirSync(long, { recursive: true });
    fs.writeFileSync(path.join(long, 'raw.mitm'), 'not-a-har');
    fs.writeFileSync(path.join(long, 'CAPTURE.md'), '# ' + 'x'.repeat(5000) + '\n');
    const longDescribe = store.describeCaptureDir(long).describe;
    assert.ok(longDescribe.length <= 200,
        'a description is capped for the table it is printed in, got ' + longDescribe.length);
    assert.ok(/x{50}/.test(longDescribe), 'and it is still the text that was written');

    // An unreadable notes file is not a crash. The walk over a whole store must
    // survive one bad directory -- a throw here loses the inventory, which is a
    // worse failure than the one being fixed.
    const unreadable = path.join(base, '2026-03-01-000004');
    fs.mkdirSync(unreadable, { recursive: true });
    fs.writeFileSync(path.join(unreadable, 'raw.mitm'), 'not-a-har');
    fs.mkdirSync(path.join(unreadable, 'CAPTURE.md'));   // a DIRECTORY by that name
    const odd = store.describeCaptureDir(unreadable);
    assert.strictEqual(odd.captureClass, store.CLASS_FOREIGN);
    assert.strictEqual(odd.describe, null);
});

section('the walk decides by what it IS, not by listing what it is not', () => {
    // `listSessionDirs` and the descent test used to name the classes they
    // excluded (`!== CLASS_FOREIGN`), so adding `imported` would have silently
    // promoted an un-attributable capture into the recorder's own enumeration
    // -- the one `stop` and `status` resolve "which capture" from. The test is
    // now the complement: a recorder capture, or not one.
    const storeRoot = path.join(tmp, 'descent', '.har-captures');
    const host = path.join(storeRoot, 'phone.example.test');
    fs.mkdirSync(host, { recursive: true });
    // A described import sitting at HOST level, with real captures under it.
    fs.writeFileSync(path.join(host, 'raw.mitm'), 'not-a-har');
    fs.writeFileSync(path.join(host, 'CAPTURE.md'), '# a stray import at host level\n');
    const under = makeCapture(path.join(host, '2026-04-01-000001'));

    const dirs = store.listSessionDirs(storeRoot).map((e) => e.dir);
    assert.ok(!dirs.includes(host),
        'a described import is not a recorder session and must stay out of the enumeration ' +
        '`stop` and `status` resolve from');
    assert.ok(dirs.includes(under),
        'and declining the directory above it must not hide the captures under it');
});

section('a notes file that is a symlink is not read', () => {
    // AN ARBITRARY-FILE-READ PRIMITIVE, caught in review. `CAPTURE.md` is the
    // one file this walk reports the CONTENT of, and the whole point of the
    // `imported` class is captures brought in from OUTSIDE -- a phone export, a
    // downloaded archive. An archive that recreates a symlink named CAPTURE.md
    // pointing at, say, a credentials file would have had its first line read
    // off disk and printed into the listing, an agent's context, and the delete
    // confirmation.
    const dir = path.join(tmp, 'symlink', '.har-captures', 'phone.example.test',
        '2026-05-01-000001');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'raw.mitm'), 'not-a-har');
    const elsewhere = path.join(tmp, 'symlink', 'not-a-capture-note.txt');
    fs.writeFileSync(elsewhere, 'SENTINEL-SHOULD-NEVER-BE-READ\n');

    let linked = true;
    try { fs.symlinkSync(elsewhere, path.join(dir, 'CAPTURE.md'), 'file'); }
    catch (e) {
        // Creating a symlink needs a privilege this machine may not grant.
        // SAID OUT LOUD rather than passed over: a guard that was never
        // exercised must not read as one that was.
        linked = false;
        process.stderr.write('capture-store.test: the symlink case was not exercised here (' +
            (e && e.code) + ')\n');
    }

    if (linked) {
        const entry = store.describeCaptureDir(dir);
        assert.strictEqual(entry.describe, null,
            'a symlinked CAPTURE.md must not be read, got: ' + entry.describe);
        assert.strictEqual(entry.captureClass, store.CLASS_FOREIGN,
            'and nothing it pointed at makes this a described import');
        assert.strictEqual(store.describeFromNotes(dir), null);
    }

    // THE ABLATION, and it runs everywhere. The same notes file written as a
    // real file in the same place is read -- without this, the assertions above
    // would pass against a reader that had simply stopped working.
    const real = path.join(tmp, 'symlink', '.har-captures', 'phone.example.test',
        '2026-05-01-000002');
    fs.mkdirSync(real, { recursive: true });
    fs.writeFileSync(path.join(real, 'raw.mitm'), 'not-a-har');
    fs.writeFileSync(path.join(real, 'CAPTURE.md'), '# a real notes file\n');
    assert.strictEqual(store.describeCaptureDir(real).describe, 'a real notes file');
});

section('a notes file that is a hard link is not read either', () => {
    // THE HALF THE SYMLINK FIX MISSED, caught by the reviewer re-reading it. A
    // hard link is not indirection the filesystem will own up to: `lstat`
    // cannot tell one from an ordinary file, because at that level it IS one.
    // So `isFile()` passed, the open followed the entry to somebody else's
    // bytes, and the first line came back as this capture's description.
    //
    // It is the EASIER attack of the two, not the harder one -- `link()` needs
    // no privilege on Windows where `symlink()` does, and `tar` carries
    // hard-link entries, so the archive that delivers an imported capture can
    // carry this.
    const dir = path.join(tmp, 'hardlink', '.har-captures', 'phone.example.test',
        '2026-07-01-000001');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'raw.mitm'), 'not-a-har');
    const elsewhere = path.join(tmp, 'hardlink', 'not-a-capture-note.txt');
    fs.writeFileSync(elsewhere, 'SENTINEL-SHOULD-NEVER-BE-READ\n');

    let linked = true;
    try { fs.linkSync(elsewhere, path.join(dir, 'CAPTURE.md')); }
    catch (e) {
        linked = false;
        process.stderr.write('capture-store.test: the hard-link case was not exercised here (' +
            (e && e.code) + ')\n');
    }

    if (linked) {
        const entry = store.describeCaptureDir(dir);
        assert.strictEqual(entry.describe, null,
            'a hard-linked CAPTURE.md must not be read, got: ' + entry.describe);
        assert.strictEqual(entry.captureClass, store.CLASS_FOREIGN,
            'and the content it published here does not make this a described import');
        assert.strictEqual(store.describeFromNotes(dir), null);

        // AND THE DIRECTORY IS STILL A CAPTURE. Refusing the notes file must
        // cost it its description and nothing else -- dropping the whole entry
        // would hide a capture, which is the failure this walk exists to
        // prevent.
        assert.strictEqual(entry.stamp, '2026-07-01-000001');
        assert.ok(entry.rawBytes > 0);
    }

    // The ablation, running everywhere: one directory entry, and it is read.
    const real = path.join(tmp, 'hardlink', '.har-captures', 'phone.example.test',
        '2026-07-01-000002');
    fs.mkdirSync(real, { recursive: true });
    fs.writeFileSync(path.join(real, 'raw.mitm'), 'not-a-har');
    fs.writeFileSync(path.join(real, 'CAPTURE.md'), '# a notes file nothing else references\n');
    assert.strictEqual(store.describeCaptureDir(real).describe,
        'a notes file nothing else references');
});

section('a description is cut between characters, not through one', () => {
    // A hand-written notes file with an emoji in it is the everyday case, and
    // `slice` counts UTF-16 code units: a cut landing between the halves of a
    // surrogate pair leaves a lone surrogate that renders as a replacement
    // glyph in the table. Raised in review.
    const dir = path.join(tmp, 'wide', '.har-captures', 'phone.example.test',
        '2026-06-01-000001');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'raw.mitm'), 'not-a-har');
    // Each of these is two code units, so a 200-unit cap lands mid-character on
    // every odd boundary -- the heading is built so that it does.
    fs.writeFileSync(path.join(dir, 'CAPTURE.md'), '# x' + '\u{1F600}'.repeat(300) + '\n');

    const describe = store.describeCaptureDir(dir).describe;
    assert.ok(describe.length <= 200, 'still capped, got ' + describe.length);
    assert.ok(!/[\uD800-\uDBFF]$/.test(describe),
        'a description must not end in half a character');
    // And it is still the text that was written, not an empty result.
    assert.ok(describe.startsWith('x\u{1F600}'), 'got: ' + JSON.stringify(describe.slice(0, 8)));
});

section('the walk carries which run made each capture, and says when nothing did', () => {
    // Issue #26. The store is shared and several sessions record into it at
    // once; the walk is the one thing that looks at every capture, so it is
    // where "which of these are mine?" has to be answerable. TWO RUNS,
    // INTERLEAVED BY STAMP on one host, with ONE description between them --
    // the measured case, where neither a time window nor the description could
    // tell them apart.
    const root = path.join(tmp, 'runs', '.har-captures');
    const hostDir = path.join(root, 'app.example.test');
    const record = (stamp, extra) => {
        const dir = path.join(hostDir, stamp);
        fs.mkdirSync(dir, { recursive: true });
        writeHar(path.join(dir, 'raw.har'));
        fs.writeFileSync(path.join(dir, 'session.json'), JSON.stringify(Object.assign({
            uri: 'https://app.example.test/', describe: 'create a step, then delete it', sessionDir: dir,
            endedUtc: '2026-01-01T00:01:00.000Z'
        }, extra)));
        return dir;
    };
    const runA = { id: 'aaaaaaaaaaaa', basis: 'process', lineage: ['111111111111', 'aaaaaaaaaaaa'] };
    const runB = { id: 'bbbbbbbbbbbb', basis: 'terminal', lineage: ['bbbbbbbbbbbb', '222222222222'] };
    const a1 = record('2026-02-01-000001', { captureId: 'capture-a1', run: runA });
    const b1 = record('2026-02-01-000002', { captureId: 'capture-b1', run: runB });
    const a2 = record('2026-02-01-000003', { captureId: 'capture-a2', run: runA });
    const b2 = record('2026-02-01-000004', { captureId: 'capture-b2', run: runB });
    // Recorded before any of this existed.
    const old = record('2026-02-01-000005', {});
    // Recorded since, on a machine where the ancestry could not be read.
    const unread = record('2026-02-01-000006', {
        captureId: 'capture-unread', run: { id: null, reason: 'process ancestry is not read on this platform' }
    });
    // A session.json somebody edited, or a different tool wrote.
    const mangled = record('2026-02-01-000007', { captureId: 42, run: 'aaaaaaaaaaaa' });
    const mangledId = record('2026-02-01-000008', { captureId: '', run: { id: ['aaaaaaaaaaaa'], basis: 7 } });
    // And one this recorder did not make.
    const foreign = path.join(root, 'phone.example.test', '2026-02-01-000009');
    fs.mkdirSync(foreign, { recursive: true });
    fs.writeFileSync(path.join(foreign, 'raw.mitm'), 'not-a-har');

    const found = store.listCaptureDirs(root);
    const at = (dir) => found.find((c) => c.dir === dir);

    assert.strictEqual(at(a1).captureId, 'capture-a1');
    assert.strictEqual(at(a1).runId, 'aaaaaaaaaaaa');
    assert.strictEqual(at(a1).runBasis, 'process');
    assert.strictEqual(at(b1).runId, 'bbbbbbbbbbbb');
    assert.strictEqual(at(b1).runBasis, 'terminal');

    // SELECTING BY ONE RUN NEVER RETURNS THE OTHER'S -- nor a capture with no
    // run at all, which a bare equality against a missing value would.
    const select = (id) => found.filter((c) => c.runId && c.runId === id).map((c) => c.dir).sort();
    assert.deepStrictEqual(select('aaaaaaaaaaaa'), [a1, a2].sort());
    assert.deepStrictEqual(select('bbbbbbbbbbbb'), [b1, b2].sort());
    // The stamps interleave, so no window over them is one run.
    const order = found.filter((c) => c.runId).sort((x, y) => (x.stamp < y.stamp ? -1 : 1)).map((c) => c.runId);
    assert.deepStrictEqual(order, ['aaaaaaaaaaaa', 'bbbbbbbbbbbb', 'aaaaaaaaaaaa', 'bbbbbbbbbbbb']);
    // And within a run each capture is still its own.
    assert.notStrictEqual(at(a1).captureId, at(a2).captureId);

    // BEFORE THIS CHANGE: listed, with nothing claimed about who made it.
    assert.ok(at(old), 'a capture with no run is still a capture');
    assert.strictEqual(at(old).captureId, null);
    assert.strictEqual(at(old).runId, null);
    assert.strictEqual(at(old).runBasis, null);

    // COULD NOT BE READ: it has its own id, and no run. The two are different
    // facts and a caller can tell them apart.
    assert.strictEqual(at(unread).captureId, 'capture-unread');
    assert.strictEqual(at(unread).runId, null);

    // A value of the wrong shape is not an identity. Null, never a throw and
    // never the wrong-shaped value passed through to be compared with.
    for (const dir of [mangled, mangledId]) {
        assert.strictEqual(at(dir).captureId, null, JSON.stringify(at(dir)));
        assert.strictEqual(at(dir).runId, null);
        assert.strictEqual(at(dir).runBasis, null);
    }

    // Not this recorder's: there is no session to have recorded a run in.
    assert.strictEqual(at(foreign).captureId, null);
    assert.strictEqual(at(foreign).runId, null);
    assert.strictEqual(at(foreign).runBasis, null);

    // THE LINEAGE STAYS IN session.json. A containment filter on a high
    // ancestor matches every session on the machine, so the walk -- which is
    // what the listing and every bulk driver read -- does not hand it out.
    for (const entry of found) {
        assert.ok(!('runLineage' in entry) && !('lineage' in entry) && !('run' in entry),
            'the walk carries the run id and its basis, not the ancestry');
    }
});

// `findProfileConflict` is async, and a promise nobody waits on is an assertion
// that cannot fail. Settled before the verdict is printed.
Promise.all(pending).then(() => {
    fs.rmSync(tmp, { recursive: true, force: true });

    if (failures.length > 0) {
        for (const f of failures) console.error('FAILED ' + f);
        console.error(`${failures.length} capture-store assertion(s) failed`);
        process.exit(1);
    }
    console.log('All capture-store tests passed');
});
