#!/usr/bin/env node
// Behavior tests for issue #34 -- the leak gate reads a capture as it walks it.
//
// Zero-dep, runs with `node verify-scrub-stream.test.js`.
//
// Once the scrub streamed (#10), the gate became the next stage a large
// capture could not pass: it read the scrubbed file whole, and past the
// ~512 MB a string can hold it could not open it at all. It now walks the
// capture entry by entry. What is pinned:
//
//  1. SAME FINDINGS. For every committed HAR fixture -- and for the scrub
//     oracle's unscrubbed input, which carries a secret the gate must report --
//     the streamed collection returns exactly what the whole-document
//     collection returns, in the same order.
//  2. SAME LITERAL COUNTS. The file-chunked literal count equals the whole-text
//     count, for chunk sizes that split needles and multi-byte characters.
//  3. IT STREAMS. The gate command passes a capture several times larger than
//     the heap it is given.
//
// All values are synthetic.

'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const harGate = require(path.join(__dirname, 'har-gate.js'));
const harDocument = require(path.join(__dirname, 'har-document.js'));
const harPolicy = require(path.join(__dirname, 'har-policy.js'));
const harLiterals = require(path.join(__dirname, 'har-literals.js'));
const support = require(path.join(__dirname, 'sanitize-har-stream.test-support.js'));
const { makeTempRepo } = require(path.join(__dirname, 'har-test-repo.test-support.js'));

const VERIFY = path.join(__dirname, 'verify-scrub.js');
const FIXTURES = path.join(__dirname, '..', '..', '..', '..', 'tests', 'fixtures', 'har');
const POLICY = harPolicy.loadDefaultPolicy();

let passed = 0;
function test(name, fn) {
    try { fn(); passed++; console.log(`ok - ${name}`); } catch (e) {
        console.error(`not ok - ${name}\n${e.stack}`);
        process.exitCode = 1;
    }
}

function bothWays(file) {
    const whole = harGate.collectFindings(harDocument.readHarDocument(file).document, POLICY);
    const streamed = harGate.collectFindingsFromCapture(harDocument.openHarCapture(file), POLICY);
    return { whole, streamed };
}

test('the streamed gate finds exactly what the whole-document gate finds, on every fixture', () => {
    const files = fs.readdirSync(FIXTURES).filter((f) => f.endsWith('.har'));
    assert.ok(files.length >= 10, `expected the committed fixtures, found ${files.length}`);
    let findings = 0;
    for (const f of files) {
        const { whole, streamed } = bothWays(path.join(FIXTURES, f));
        assert.deepStrictEqual(streamed, whole, f);
        findings += whole.length;
    }
    assert.ok(findings > 0, 'the fixtures carry findings, or this comparison proves nothing');
});

test('an unscrubbed capture\'s known secrets are reported the same way, in the same order', () => {
    const tmp = makeTempRepo('gate-stream-');
    try {
        const file = path.join(tmp, 'raw.har');
        fs.writeFileSync(file, JSON.stringify(support.buildCapture(), null, 2));
        const { whole, streamed } = bothWays(file);
        assert.ok(whole.some((l) => l.kind === 'known-secret'), 'the raw oracle input carries a known secret');
        assert.deepStrictEqual(streamed, whole);
    } finally {
        fs.rmSync(tmp, { recursive: true, force: true });
    }
});

test('literal hits counted from the file in chunks equal the whole-text count', () => {
    const tmp = makeTempRepo('gate-literals-');
    try {
        const literals = [
            { literal: 'Zelda Quartermain-Operator', sentinel: '<OperatorName>' },
            { literal: '100000000001', sentinel: '<AccountId>' },
            { literal: 'Zoë Ångström', sentinel: '<OtherName>' },
        ];
        const parts = [];
        for (let i = 0; i < 4000; i++) {
            parts.push(i % 5 ? 'x'.repeat(i % 29) : 'Zelda Quartermain-Operator');
            if (i % 9 === 0) parts.push('100000000001');
            if (i % 13 === 0) parts.push(encodeURIComponent('Zoë Ångström'));
            if (i % 17 === 0) parts.push('Zoë Ångström');
        }
        const text = parts.join('é');
        const file = path.join(tmp, 'text.har');
        fs.writeFileSync(file, text);
        const expected = harLiterals.findLiteralHits(text, literals);
        assert.ok(expected.length === 3, 'every literal is present');
        for (const chunk of [5, 11, 64, 4096, 1 << 20]) {
            assert.deepStrictEqual(harLiterals.findLiteralHitsInFile(file, literals, chunk), expected, `chunk ${chunk}`);
        }
    } finally {
        fs.rmSync(tmp, { recursive: true, force: true });
    }
});

test('the gate passes a capture several times larger than its heap', () => {
    const tmp = makeTempRepo('gate-large-');
    try {
        const file = path.join(tmp, 'scrubbed.har');
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
        const fd = fs.openSync(file, 'w');
        fs.writeSync(fd, '{"log":{"version":"1.2","creator":{"name":"t","version":"1"},"entries":[');
        for (let i = 0; i < 1000; i++) fs.writeSync(fd, (i ? ',' : '') + one(i));
        fs.writeSync(fd, ']}}');
        fs.closeSync(fd);
        const HEAP_MB = 16;
        assert.ok(fs.statSync(file).size > 1.5 * HEAP_MB * 1024 * 1024, 'the capture must dwarf the heap');
        const r = spawnSync(process.execPath, [`--max-old-space-size=${HEAP_MB}`, VERIFY, '--in', file],
            { encoding: 'utf8' });
        assert.strictEqual(r.status, 0, `exit ${r.status}: ${(r.stderr || '').split('\n').slice(0, 3).join(' | ')}`);
        assert.match(r.stdout, /0 blocking leaks/);
    } finally {
        fs.rmSync(tmp, { recursive: true, force: true });
    }
});

test('a file too large to read whole that is not a HAR is refused, never passed', () => {
    // The fallback flat sweep needs the whole text, which a file this size
    // cannot be. So there are no findings -- and the gate must still refuse,
    // not report 0 blocking leaks. Sparse: 576 MB of NUL bytes, never written.
    const captureSize = require(path.join(__dirname, '..', 'lib', 'capture-size.js'));
    const tmp = makeTempRepo('gate-huge-');
    try {
        const file = path.join(tmp, 'scrubbed.har');
        const fd = fs.openSync(file, 'w');
        try { fs.ftruncateSync(fd, captureSize.MAX_STRING_LENGTH + 64 * 1024 * 1024); } finally { fs.closeSync(fd); }
        const r = spawnSync(process.execPath, [VERIFY, '--in', file], { encoding: 'utf8' });
        assert.strictEqual(r.status, 1, `exit ${r.status}: ${r.stdout}${r.stderr}`);
        assert.match(r.stderr, /REFUSING to report this file as scrubbed/);
        assert.doesNotMatch(r.stdout, /0 blocking leaks/);
    } finally {
        fs.rmSync(tmp, { recursive: true, force: true });
    }
});

if (process.exitCode) process.exit(process.exitCode);
console.log(`All verify-scrub-stream tests passed (${passed})`);
