#!/usr/bin/env node
// Behavior tests for issue #21 -- the pipeline runner over pinned captures.
//
// Zero-dep, runs with `node pipeline-check.test.js`.
//
// The runner is run as a command against synthetic captures in a protected
// temp repo, the way it runs against a real store. What is pinned:
//
//  1. A clean capture passes all five checks, and a record is written.
//  2. A capture whose bytes no longer match the pinned SHA-256 fails.
//  3. No captured value reaches the console or the record.
//  4. Check 3 finds a body the scrub broke; check 4 finds entries the
//     catalogue lost; check 5 finds a request URL that no longer parses (#66).
//  5. The ratchet: pass -> fail is a regression, and so is a rise in broken
//     bodies or URLs; a changed salt is refused.
//  6. No profile is a hard failure, not an unsalted run.
//
// All values here are synthetic.

'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { execFileSync } = require('child_process');

const check = require(path.join(__dirname, 'pipeline-check.js'));
const { makeTempRepo } = require(path.join(__dirname, 'har-test-repo.test-support.js'));

const RUNNER = path.join(__dirname, 'pipeline-check.js');
const SECRET = 'Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0LXVzZXItOTkifQ.c2lnbmF0dXJlLXZhbHVlLWhlcmU';
const EMAIL = 'pipeline.runner.person@example.org';

let passed = 0;
function test(name, fn) {
    try { fn(); passed++; console.log(`ok - ${name}`); } catch (e) {
        console.error(`not ok - ${name}\n${e.stack}`);
        process.exitCode = 1;
    }
}

function entry(url, { reqText, resText, headers = [] } = {}) {
    return {
        startedDateTime: '2026-01-01T00:00:00.000Z', time: 1,
        request: {
            method: reqText ? 'POST' : 'GET', url, httpVersion: 'HTTP/1.1', headers,
            queryString: [], cookies: [], headersSize: -1, bodySize: reqText ? reqText.length : 0,
            ...(reqText ? { postData: { mimeType: 'application/json', text: reqText } } : {}),
        },
        response: {
            status: 200, statusText: 'OK', httpVersion: 'HTTP/1.1', headers: [], cookies: [],
            content: { size: (resText || '').length, mimeType: 'application/json', text: resText || '{}' },
            redirectURL: '', headersSize: -1, bodySize: -1,
        },
        cache: {}, timings: { send: 0, wait: 1, receive: 0 },
    };
}

function har(entries) {
    return { log: { version: '1.2', creator: { name: 'test', version: '1' }, entries } };
}

function sha(file) { return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex'); }

// A store laid out like a real one: a profile at the root, captures below it,
// and a manifest pinning them.
function store(captures, { profile = true } = {}) {
    const root = makeTempRepo('pipeline-check-');
    const dir = path.join(root, '.har-captures');
    fs.mkdirSync(dir, { recursive: true });
    if (profile) {
        fs.writeFileSync(path.join(root, '.har-profile.json'),
            JSON.stringify({ salt: 'pipeline-check-test-salt', literals: {} }));
    }
    const pinned = [];
    for (const [label, doc] of Object.entries(captures)) {
        const file = path.join(dir, label, 'raw.har');
        fs.mkdirSync(path.dirname(file), { recursive: true });
        fs.writeFileSync(file, JSON.stringify(doc));
        pinned.push({ label, path: `${label}/raw.har`, sha256: sha(file) });
    }
    const manifest = path.join(dir, 'pinned.json');
    fs.writeFileSync(manifest, JSON.stringify({ captures: pinned }, null, 2));
    return { root, dir, manifest };
}

function run(manifest, extra = []) {
    try {
        const stdout = execFileSync(process.execPath, [RUNNER, '--manifest', manifest, '--heap-mb', '512', ...extra],
            { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
        return { code: 0, stdout, stderr: '' };
    } catch (e) {
        return { code: e.status, stdout: String(e.stdout || ''), stderr: String(e.stderr || '') };
    }
}

function latestRecord(dir) {
    const runs = path.join(dir, 'runs');
    const f = fs.readdirSync(runs).filter((x) => x.startsWith('run-')).sort().pop();
    return { file: path.join(runs, f), doc: JSON.parse(fs.readFileSync(path.join(runs, f), 'utf8')) };
}

const clean = har([
    entry('https://api.example.test/v1/items/123', { resText: '{"items":[{"id":1,"name":"widget"}]}' }),
    entry('https://api.example.test/v1/items', {
        reqText: `{"name":"widget","contact":"${EMAIL}"}`,
        resText: '{"ok":true}',
        headers: [{ name: 'Authorization', value: SECRET }],
    }),
    // `widget` is enrolled as a person's name above (a `name` field), and is
    // also a hostname label here -- the shape that broke 160 URLs in #66.
    entry('https://cdn.widget.test/app.js'),
]);

test('a clean capture passes all five checks and a record is written', () => {
    const s = store({ clean });
    const r = run(s.manifest);
    assert.strictEqual(r.code, 0, `exit ${r.code}\n${r.stdout}${r.stderr}`);
    const { doc } = latestRecord(s.dir);
    const c = doc.captures[0];
    assert.strictEqual(c.status, 'pass', JSON.stringify(c));
    for (const k of ['trim', 'survivors', 'payloads', 'urls', 'catalogue']) assert.strictEqual(c.checks[k], 'pass', k);
    assert.ok(['pass', 'advisory'].includes(c.checks.gate), c.checks.gate);
    assert.strictEqual(c.counts.payloads.broken, 0);
    assert.ok(c.counts.payloads.parsedBefore >= 3, 'the JSON bodies were actually examined');
    assert.strictEqual(c.counts.urls.broken, 0);
    assert.strictEqual(c.counts.urls.parsedBefore, 3, 'every request URL was actually examined');
    assert.strictEqual(c.counts.catalogue.rowEntries, c.counts.catalogue.entries);
    assert.match(doc.saltId, /^[0-9a-f]{16}$/);
    // A passing capture leaves no scrubbed copy or substitution table behind.
    assert.ok(!fs.readdirSync(path.join(s.dir, 'runs')).some((f) => f.startsWith('work-')),
        'the work directory of a passing run is removed');
});

test('no captured value reaches the console or the record', () => {
    const s = store({ clean });
    const r = run(s.manifest, ['--keep-work']);
    const { file } = latestRecord(s.dir);
    const record = fs.readFileSync(file, 'utf8');
    for (const value of [SECRET, SECRET.slice(7), EMAIL, 'pipeline-check-test-salt']) {
        assert.ok(!r.stdout.includes(value) && !r.stderr.includes(value), 'console carries a value');
        assert.ok(!record.includes(value), 'record carries a value');
    }
});

test('a capture that no longer matches its pinned SHA-256 fails', () => {
    const s = store({ clean });
    fs.appendFileSync(path.join(s.dir, 'clean', 'raw.har'), '\n');
    const r = run(s.manifest);
    assert.strictEqual(r.code, check.EXIT.failed, r.stdout);
    const c = latestRecord(s.dir).doc.captures[0];
    assert.strictEqual(c.status, 'fail');
    assert.match(c.reason, /SHA-256/);
});

test('no profile is a hard failure, not an unsalted run', () => {
    const s = store({ clean }, { profile: false });
    const r = run(s.manifest);
    assert.strictEqual(r.code, check.EXIT.error, r.stdout + r.stderr);
    assert.ok(!fs.existsSync(path.join(s.dir, 'runs')), 'nothing was recorded');
});

test('check 3 finds a JSON body the scrub broke, by entry and side only', () => {
    const tmp = makeTempRepo('pipeline-check-payloads-');
    const before = path.join(tmp, 'before.har');
    const after = path.join(tmp, 'after.har');
    fs.writeFileSync(before, JSON.stringify(har([
        entry('https://a.test/x', { reqText: '{"n":12345}', resText: '{"ok":true}' }),
        entry('https://a.test/y', { resText: 'not json at all' }),
    ])));
    fs.writeFileSync(after, JSON.stringify(har([
        entry('https://a.test/x', { reqText: '{"n":[REDACTED]}', resText: '{"ok":true}' }),
        entry('https://a.test/y', { resText: 'still not json' }),
    ])));
    const r = check.comparePayloads(before, after);
    assert.strictEqual(r.parsedBefore, 2, 'a body that never parsed is not held to parsing');
    assert.strictEqual(r.broken, 1);
    assert.deepStrictEqual(r.locations, [{ entry: 0, where: 'request:body' }]);
    assert.strictEqual(r.entriesBefore, 2);
    assert.strictEqual(r.entriesAfter, 2);
});

test('check 3 finds the #25 damage: JSON inside a form-encoded field', () => {
    const tmp = makeTempRepo('pipeline-check-form-');
    const before = path.join(tmp, 'before.har');
    const after = path.join(tmp, 'after.har');
    const form = (vars) => {
        const e = entry('https://a.test/api/graphql/', { reqText: 'x' });
        e.request.postData = { mimeType: 'application/x-www-form-urlencoded',
            text: `av=1&variables=${encodeURIComponent(vars)}`,
            params: [{ name: 'av', value: '1' }, { name: 'variables', value: encodeURIComponent(vars) }] };
        return e;
    };
    // The response carries the anti-hijacking prefix, and must still be read.
    const prefixed = entry('https://a.test/ajax', { resText: 'for (;;);{"ok":1}' });
    fs.writeFileSync(before, JSON.stringify(har([form('{"id":"123456789"}'), prefixed])));
    fs.writeFileSync(after, JSON.stringify(har([form('{"id":B422112590"}'), prefixed])));
    const r = check.comparePayloads(before, after);
    assert.deepStrictEqual(r.locations, [{ entry: 0, where: 'request:field variables#1' }]);
    assert.strictEqual(r.broken, 1);
    assert.strictEqual(r.parsedBefore, 3, 'the form field, the prefixed response, and the plain {} response');
});

test('check 3 counts both sides when the scrub changed the entry count', () => {
    const tmp = makeTempRepo('pipeline-check-count-');
    const before = path.join(tmp, 'before.har');
    const after = path.join(tmp, 'after.har');
    fs.writeFileSync(before, JSON.stringify(har([entry('https://a.test/1'), entry('https://a.test/2'), entry('https://a.test/3')])));
    fs.writeFileSync(after, JSON.stringify(har([entry('https://a.test/1')])));
    const r = check.comparePayloads(before, after);
    assert.strictEqual(r.entriesBefore, 3);
    assert.strictEqual(r.entriesAfter, 1);
});

test('check 5 finds a request URL the scrub broke, by entry only', () => {
    const tmp = makeTempRepo('pipeline-check-urls-');
    const before = path.join(tmp, 'before.har');
    const after = path.join(tmp, 'after.har');
    fs.writeFileSync(before, JSON.stringify(har([
        entry('https://api.example.test/x'),
        entry('https://static01.cdnword.test/a.js'),
        entry('not a url'),
    ])));
    // #66's shape: a fake person's name written over a hostname label. A space
    // is a forbidden host code point, so the URL no longer parses.
    fs.writeFileSync(after, JSON.stringify(har([
        entry('https://api.example.test/x'),
        entry('https://static01.Avery Brooks.test/a.js'),
        entry('not a url'),
    ])));
    const r = check.compareUrls(before, after);
    assert.strictEqual(r.parsedBefore, 2, 'a URL that never parsed is not held to parsing');
    assert.strictEqual(r.broken, 1);
    assert.deepStrictEqual(r.locations, [{ entry: 1 }], 'a location is an entry index, never a URL');
    assert.strictEqual(r.entriesBefore, 3);
    assert.strictEqual(r.entriesAfter, 3);
});

test('the ratchet: more broken request URLs is a regression', () => {
    const rec = (broken) => ({
        saltId: 's1', checksVersion: 3, file: '/runs/run-earlier.json',
        captures: [{ label: 'a', sha256: 'h', status: 'fail', counts: { urls: { broken } } }],
    });
    assert.match(check.compare(rec(1), rec(4)).regressions[0].what, /broken URLs 1 -> 4/);
    assert.strictEqual(check.compare(rec(4), rec(0)).regressions.length, 0, 'improvement is not a regression');
});

test('check 4 fails when catalogue rows do not account for every entry', () => {
    const tmp = makeTempRepo('pipeline-check-catalogue-');
    const file = path.join(tmp, 'scrubbed.har');
    // An entry whose URL does not parse is dropped by the digest, so the rows
    // account for one entry of two.
    fs.writeFileSync(file, JSON.stringify(har([entry('https://a.test/1'), entry('not a url')])));
    const r = check.reconcileCatalogue(file);
    assert.strictEqual(r.entries, 2);
    assert.strictEqual(r.rowEntries, 1);
});

test('the ratchet: pass then fail is a regression; more broken bodies is a regression', () => {
    const rec = (status, broken, salt = 's1', checksVersion = 2) => ({
        saltId: salt, checksVersion, file: '/runs/run-earlier.json',
        captures: [{ label: 'a', sha256: 'h', status, counts: { payloads: { broken }, gate: { gating: 0 } } }],
    });
    assert.deepStrictEqual(check.compare(rec('pass', 0), rec('fail', 1)).regressions.map((r) => r.what),
        ['passed before, fails now']);
    assert.match(check.compare(rec('fail', 1), rec('fail', 3)).regressions[0].what, /broken payloads 1 -> 3/);
    assert.strictEqual(check.compare(rec('fail', 3), rec('fail', 1)).regressions.length, 0, 'improvement is not a regression');
    const salted = check.compare(rec('pass', 0), rec('fail', 1, 's2'));
    assert.strictEqual(salted.comparable, false);
    assert.match(salted.reason, /salt/);
    // A capture already failing that starts failing ANOTHER check has regressed.
    const withChecks = (checks) => ({ saltId: 's1', checksVersion: 2, file: '/runs/run-earlier.json',
        captures: [{ label: 'a', sha256: 'h', status: 'fail', checks, counts: {} }] });
    const worse = check.compare(
        withChecks({ survivors: 'pass', gate: 'pass', catalogue: 'fail' }),
        withChecks({ survivors: 'fail', gate: 'pass', catalogue: 'fail' }));
    assert.deepStrictEqual(worse.regressions.map((r) => r.what), ['check survivors: pass -> fail']);
    // A stricter check failing what a weaker one passed is not a regression.
    const stricter = check.compare(rec('pass', 0, 's1', 1), rec('fail', 1, 's1', 2));
    assert.strictEqual(stricter.comparable, false);
    assert.match(stricter.reason, /checks changed/);
});

test('a second run compares against the first, and removes the work of the first', () => {
    const s = store({ clean });
    run(s.manifest, ['--keep-work']);
    assert.ok(fs.readdirSync(path.join(s.dir, 'runs')).some((f) => f.startsWith('work-')));
    const r = run(s.manifest);
    assert.strictEqual(r.code, 0, r.stdout);
    assert.match(r.stdout, /no regressions against run-/);
    assert.ok(!fs.readdirSync(path.join(s.dir, 'runs')).some((f) => f.startsWith('work-')),
        'the scrubbed copies and tables of the earlier run are gone');
});

test('a stage that runs out of memory is an error, never a failure or a regression', () => {
    // One large body rather than many small ones: the scrub then fails at
    // once under a small heap instead of thrashing towards the limit.
    const body = JSON.stringify({ blob: 'q'.repeat(24 * 1024 * 1024) });
    const big = har([entry('https://api.example.test/v1/blob', { resText: body })]);
    const s = store({ big });
    // First a pass with room, so the out-of-memory run has a pass to "regress" from.
    assert.strictEqual(run(s.manifest).code, 0);
    const r = run(s.manifest, ['--heap-mb', '64']);
    const c = latestRecord(s.dir).doc.captures[0];
    assert.strictEqual(c.status, 'error', JSON.stringify(c));
    assert.match(c.reason, /did not finish/);
    assert.strictEqual(r.code, check.EXIT.failed, 'reported as not passing');
    assert.doesNotMatch(r.stdout, /REGRESSION/);
});

if (process.exitCode) process.exit(process.exitCode);
console.log(`All pipeline-check tests passed (${passed})`);
