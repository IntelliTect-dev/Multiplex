#!/usr/bin/env node
// Behavior tests for issue #10 -- the scrub streams.
//
// Zero-dep, runs with `node sanitize-har-stream.test.js`.
//
// The scrub used to read a capture whole, and a capture past the ~512 MB a
// single string can hold could not be scrubbed at all -- a fixed engine
// ceiling that no amount of memory moves. It now reads entry by entry, in two
// walks. What is pinned here is the whole claim of that change:
//
//  1. NOTHING OBSERVABLE CHANGED on a capture that was already scrubbable.
//     The whole-document scrub's output for a capture built to tell one pass
//     from two is committed under tests/fixtures/har/stream-scrub/, and the
//     streaming scrub must reproduce it byte for byte -- the scrubbed file and
//     both substitution tables.
//  2. IT STREAMS. A capture several times larger than the heap the scrub is
//     given is scrubbed; the whole-document scrub dies on the same input.
//  3. It leaves nothing behind but its outputs: no partial file.
//
// All values are synthetic.

'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const support = require(path.join(__dirname, 'sanitize-har-stream.test-support.js'));
const { makeTempRepo } = require(path.join(__dirname, 'har-test-repo.test-support.js'));

const SANITIZE = path.join(__dirname, 'sanitize-har.js');

let passed = 0;
function test(name, fn) {
    try { fn(); passed++; console.log(`ok - ${name}`); } catch (e) {
        console.error(`not ok - ${name}\n${e.stack}`);
        process.exitCode = 1;
    }
}

const golden = (name) => fs.readFileSync(path.join(support.GOLDEN_DIR, name), 'utf8');

test('the streamed scrub writes the whole-document scrub\'s bytes, and its tables', () => {
    const r = support.scrubFixture(SANITIZE);
    try {
        assert.strictEqual(r.code, 0, r.stderr);
        // Compared as text, then pinned exactly: a mismatch names where it is.
        const expected = golden('expected.scrubbed.har');
        if (r.scrubbed !== expected) {
            let at = 0;
            while (at < expected.length && r.scrubbed[at] === expected[at]) at++;
            assert.fail(`scrubbed output differs from the oracle at byte ${at}`);
        }
        assert.strictEqual(r.subs, golden('expected.subs.json'), 'legacy substitution table');
        assert.strictEqual(r.piiSubs, golden('expected.pii-subs.json'), 'typed-PII substitution table');
    } finally {
        fs.rmSync(r.dir, { recursive: true, force: true });
    }
});

test('the oracle exercises what one pass would get wrong', () => {
    // Guards the guard: if the fixture stopped carrying these cases, the byte
    // comparison above would still pass and prove much less.
    const out = JSON.parse(golden('expected.scrubbed.har'));
    const [e0, , e2] = out.log.entries;
    const body0 = JSON.parse(e0.response.content.text);
    const body2 = JSON.parse(e2.response.content.text);
    assert.strictEqual(body0.order_code, body2.media_id,
        'the card-shaped value is declined in entry 0 because of its identifier-field twin in entry 2');
    assert.notStrictEqual(body0.again, 'AbCdEf123456GhIjKlMn',
        'the secret named in entry 1 was swept from entry 0');
    assert.ok(out.log._blunted && out.log._blunted.values === 1, 'a blunt record after the entries');
    assert.deepStrictEqual(Object.keys(out.log), ['version', 'creator', 'pages', 'entries', 'comment', '_blunted']);
    assert.strictEqual(out.log.comment, 'recorded by <OperatorName>', 'the literal pass reached the envelope');
});

test('a capture several times larger than the heap is scrubbed', () => {
    const tmp = makeTempRepo('stream-scrub-large-');
    const dir = path.join(tmp, '.har-captures', 'large');
    fs.mkdirSync(dir, { recursive: true });
    const profile = path.join(tmp, '.har-profile.json');
    fs.writeFileSync(profile, JSON.stringify(support.PROFILE));
    const raw = path.join(dir, 'raw.har');
    // ~32 MB of entries, written in pieces so this test does not itself need
    // the memory it is proving the scrub does not.
    const body = JSON.stringify({ rows: Array.from({ length: 300 }, (_, i) => ({ id: i, note: 'x'.repeat(80) })) });
    const one = (i) => JSON.stringify({
        startedDateTime: '2026-01-01T00:00:00.000Z', time: 1,
        request: { method: 'GET', url: `https://api.example.test/rows/${i}`, httpVersion: 'HTTP/1.1',
            headers: [], queryString: [], cookies: [], headersSize: -1, bodySize: 0 },
        response: { status: 200, statusText: 'OK', httpVersion: 'HTTP/1.1', headers: [], cookies: [],
            content: { size: body.length, mimeType: 'application/json', text: body },
            redirectURL: '', headersSize: -1, bodySize: body.length },
        cache: {}, timings: { send: 0, wait: 1, receive: 0 },
    });
    const fd = fs.openSync(raw, 'w');
    fs.writeSync(fd, '{"log":{"version":"1.2","creator":{"name":"t","version":"1"},"entries":[');
    const COUNT = 1000;
    for (let i = 0; i < COUNT; i++) fs.writeSync(fd, (i ? ',' : '') + one(i));
    fs.writeSync(fd, ']}}');
    fs.closeSync(fd);
    const bytes = fs.statSync(raw).size;
    const HEAP_MB = 16;
    assert.ok(bytes > 1.5 * HEAP_MB * 1024 * 1024, `the capture (${bytes} bytes) must dwarf the heap`);

    const out = path.join(dir, 'scrubbed.har');
    const r = spawnSync(process.execPath, [`--max-old-space-size=${HEAP_MB}`, SANITIZE,
        '--in', raw, '--out', out, '--profile', profile, '--fixed-time', support.FIXED_TIME],
        { encoding: 'utf8' });
    try {
        assert.strictEqual(r.status, 0, `exit ${r.status}: ${(r.stderr || '').split('\n').slice(0, 3).join(' | ')}`);
        const harDocument = require(path.join(__dirname, 'har-document.js'));
        let n = 0;
        for (const e of harDocument.iterateHarEntries(out)) { if (e) n++; }
        assert.strictEqual(n, COUNT, 'every entry was written');
        assert.deepStrictEqual(fs.readdirSync(dir).filter((f) => f.includes('.partial-')), [],
            'no partial file is left beside the output');
    } finally {
        fs.rmSync(tmp, { recursive: true, force: true });
    }
});

test('a survivor refusal exits 5 and leaves no output, table or partial file', () => {
    const tmp = makeTempRepo('stream-scrub-survivor-');
    try {
        const dir = path.join(tmp, '.har-captures', 'fixture');
        fs.mkdirSync(dir, { recursive: true });
        const profile = path.join(tmp, '.har-profile.json');
        fs.writeFileSync(profile, JSON.stringify(support.PROFILE));
        const raw = path.join(dir, 'raw.har');
        fs.writeFileSync(raw, JSON.stringify(support.buildCapture(), null, 2));
        const outDir = path.join(tmp, 'out');
        const r = spawnSync(process.execPath, ['-r', path.join(__dirname, 'sanitize-har-stream-survivor.test-support.js'),
            SANITIZE, '--in', raw, '--out', path.join(outDir, 'scrubbed.har'), '--profile', profile],
            { encoding: 'utf8' });
        assert.strictEqual(r.status, 5, r.stderr);
        assert.match(r.stderr, /REFUSING to write/);
        assert.deepStrictEqual(fs.existsSync(outDir) ? fs.readdirSync(outDir) : [], [], 'nothing in the output folder');
        assert.deepStrictEqual(fs.readdirSync(dir), ['raw.har'], 'no substitution table beside the raw');
    } finally {
        fs.rmSync(tmp, { recursive: true, force: true });
    }
});

if (process.exitCode) process.exit(process.exitCode);
console.log(`All sanitize-har-stream tests passed (${passed})`);
