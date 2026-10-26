#!/usr/bin/env node
// Behavior tests for issue #67 -- a secret replaced in its percent-encoded
// spelling must not survive in its DECODED spelling elsewhere in the capture.
//
// Zero-dep, runs with `node subs-decoded-spelling.test.js`.
//
// The scrub replaces a known-secret field by NAME and records the original as
// it appeared on the wire. In a form body, a URL query or a parameter list
// that spelling is percent-encoded (`...%3A17%3A...`). The substitution sweep
// and its survivor guard (#475) look only for what was recorded, so the same
// value echoed decoded under a neutral key -- a response body field, say --
// was neither replaced nor reported, and the gate passed it: the echo carries
// no secret name and matches no shape.
//
// The fix records the decoded spelling beside the wire spelling, so the sweep
// replaces it with the same fake and the guard holds both.
//
// All values are synthetic.

'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const { makeTempRepo } = require(path.join(__dirname, 'har-test-repo.test-support.js'));

const SANITIZE = path.join(__dirname, 'sanitize-har.js');
const SUBS_SURVIVORS = path.join(__dirname, 'subs-survivors.js');
const { EXIT_SUBSTITUTION_SURVIVOR } = require(SANITIZE);

// A token whose wire spelling carries percent-escapes (the colons).
const TOKEN = 'QzSynthTok9WpT2vYb7Zc:42:1800000000';
const WIRE = encodeURIComponent(TOKEN);
// A token whose wire spelling is identical to its decoded spelling.
const PLAIN = 'PlainSynthTokenAbCdEf123456';

let passed = 0;
function test(name, fn) {
    try { fn(); passed++; console.log(`ok - ${name}`); } catch (e) {
        console.error(`not ok - ${name}\n${e.stack}`);
        process.exitCode = 1;
    }
}

function entry(url, { method = 'GET', queryString = [], postData, resText }) {
    return {
        startedDateTime: '2026-01-01T00:00:00.000Z', time: 1,
        request: { method, url, httpVersion: 'HTTP/1.1', headers: [], queryString, cookies: [],
            headersSize: -1, bodySize: 0, ...(postData ? { postData } : {}) },
        response: { status: 200, statusText: 'OK', httpVersion: 'HTTP/1.1', headers: [], cookies: [],
            content: { size: resText.length, mimeType: 'application/json', text: resText },
            redirectURL: '', headersSize: -1, bodySize: -1 },
        cache: {}, timings: { send: 0, wait: 1, receive: 0 },
    };
}

/**
 * Run sanitize-har.js over `entries`. `preload` is optional source for a
 * module loaded with `node -r` before the scrub runs.
 */
function scrub(entries, { preload } = {}) {
    const tmp = makeTempRepo('decoded-spelling-');
    try {
        const dir = path.join(tmp, '.har-captures', 'c');
        fs.mkdirSync(dir, { recursive: true });
        const profile = path.join(tmp, '.har-profile.json');
        fs.writeFileSync(profile, JSON.stringify({ salt: 'decoded-spelling-salt', literals: {} }));
        const raw = path.join(dir, 'raw.har');
        fs.writeFileSync(raw, JSON.stringify({ log: { version: '1.2', creator: { name: 't', version: '1' }, entries } }));
        const out = path.join(dir, 'scrubbed.har');
        const nodeArgs = [];
        if (preload) {
            const p = path.join(tmp, 'preload.js');
            fs.writeFileSync(p, preload);
            nodeArgs.push('-r', p);
        }
        const r = spawnSync(process.execPath, [...nodeArgs, SANITIZE, '--in', raw, '--out', out,
            '--profile', profile, '--fixed-time', '2026-01-01T00:00:00.000Z'], { encoding: 'utf8' });
        const text = fs.existsSync(out) ? fs.readFileSync(out, 'utf8') : null;
        return { code: r.status, stderr: r.stderr, text, entries: text ? JSON.parse(text).log.entries : null };
    } finally {
        fs.rmSync(tmp, { recursive: true, force: true });
    }
}

const echoEntry = (value) => entry('https://a.test/state', { resText: JSON.stringify({ echo: value, n: 1 }) });
const echoOf = (e) => JSON.parse(e.response.content.text).echo;
const formPost = (text, params) => entry('https://a.test/api/', {
    method: 'POST', resText: '{}',
    postData: { mimeType: 'application/x-www-form-urlencoded', text, ...(params ? { params } : {}) },
});
const formValue = (text, name) => text.split('&').find((p) => p.startsWith(`${name}=`)).slice(name.length + 1);

test('a form-body secret, percent-encoded on the wire, echoed DECODED: the echo gets the same fake', () => {
    const r = scrub([echoEntry(TOKEN), formPost(`fb_dtsg=${WIRE}&x=1`)]);
    assert.strictEqual(r.code, 0, r.stderr);
    assert.ok(!r.text.includes(TOKEN), 'the decoded spelling is gone');
    assert.ok(!r.text.includes(WIRE), 'the wire spelling is gone');
    const fake = formValue(r.entries[1].request.postData.text, 'fb_dtsg');
    assert.strictEqual(echoOf(r.entries[0]), fake, 'the echo carries the form field\'s fake');
    assert.doesNotMatch(r.stderr, /non-reversible substitution/,
        'two spellings of one datum sharing a fake is not a collision');
});

test('DOUBLE-encoded on the wire: the once-decoded and the fully decoded echoes both get the fake', () => {
    // A value re-embedded in an already-encoded parameter arrives encoded twice.
    const TWICE = encodeURIComponent(WIRE);
    const r = scrub([echoEntry(TOKEN), echoEntry(WIRE), formPost(`fb_dtsg=${TWICE}&x=1`)]);
    assert.strictEqual(r.code, 0, r.stderr);
    assert.ok(!r.text.includes(TOKEN), 'the fully decoded spelling is gone');
    assert.ok(!r.text.includes(WIRE), 'the once-decoded spelling is gone');
    const fake = formValue(r.entries[2].request.postData.text, 'fb_dtsg');
    assert.strictEqual(echoOf(r.entries[0]), fake);
    assert.strictEqual(echoOf(r.entries[1]), fake);
});

test('via postData.params[] only: the decoded echo is replaced by the params fake', () => {
    // `postData.text` carries no secret name, so only the parameter list does.
    const r = scrub([echoEntry(TOKEN), formPost('x=1', [{ name: 'fb_dtsg', value: WIRE }])]);
    assert.strictEqual(r.code, 0, r.stderr);
    assert.ok(!r.text.includes(TOKEN), 'the decoded spelling is gone');
    const fake = r.entries[1].request.postData.params[0].value;
    assert.notStrictEqual(fake, WIRE);
    assert.strictEqual(echoOf(r.entries[0]), fake);
});

test('via the URL query and queryString[]: the decoded echo is replaced by the same fake', () => {
    const q = entry(`https://a.test/api/?fb_dtsg=${WIRE}&x=1`, {
        queryString: [{ name: 'fb_dtsg', value: WIRE }, { name: 'x', value: '1' }], resText: '{}',
    });
    const r = scrub([echoEntry(TOKEN), q]);
    assert.strictEqual(r.code, 0, r.stderr);
    assert.ok(!r.text.includes(TOKEN), 'the decoded spelling is gone');
    assert.ok(!r.text.includes(WIRE), 'the wire spelling is gone');
    const fake = r.entries[1].request.queryString[0].value;
    assert.strictEqual(new URL(r.entries[1].request.url).searchParams.get('fb_dtsg'), fake);
    assert.strictEqual(echoOf(r.entries[0]), fake);
});

test('via queryString[] only (URL carries no secret name): the decoded echo is replaced', () => {
    const q = entry('https://a.test/api/?x=1', { queryString: [{ name: 'fb_dtsg', value: WIRE }], resText: '{}' });
    const r = scrub([echoEntry(TOKEN), q]);
    assert.strictEqual(r.code, 0, r.stderr);
    assert.ok(!r.text.includes(TOKEN), 'the decoded spelling is gone');
    assert.strictEqual(echoOf(r.entries[0]), r.entries[1].request.queryString[0].value);
});

test('regression guard: a secret whose wire spelling has NO escapes is swept exactly as before', () => {
    const r = scrub([echoEntry(PLAIN), formPost(`fb_dtsg=${PLAIN}&x=1`)]);
    assert.strictEqual(r.code, 0, r.stderr);
    assert.ok(!r.text.includes(PLAIN));
    assert.strictEqual(echoOf(r.entries[0]), formValue(r.entries[1].request.postData.text, 'fb_dtsg'));
});

test('the survivor guard holds the decoded spelling: with the sweep disabled, the echo refuses the scrub', () => {
    // The sweep leaves nothing for the guard to find on real input, so the
    // only way to see what the guard CHECKS is to switch the sweep off and
    // leave the guard on. Before #67 the guard did not know the decoded
    // spelling, so this exited 0 and wrote the echo out.
    const preload = `
const subsSurvivors = require(${JSON.stringify(SUBS_SURVIVORS)});
const real = subsSurvivors.compileSweeper;
subsSurvivors.compileSweeper = (entries, produced) => {
    const s = real(entries, produced);
    if (!s) return s;
    return Object.assign({}, s, { sweepEntry: () => {}, sweepEnvelope: () => {} });
};`;
    const r = scrub([echoEntry(TOKEN), formPost(`fb_dtsg=${WIRE}&x=1`)], { preload });
    assert.strictEqual(r.code, EXIT_SUBSTITUTION_SURVIVOR, `exit ${r.code}: ${r.stderr}`);
    assert.strictEqual(r.text, null, 'nothing is written');
    assert.match(r.stderr, /field:fb_dtsg \(\d+ chars\) x1 at entry 0 response\.content\.text/);
    assert.ok(!r.stderr.includes(TOKEN) && !r.stderr.includes(WIRE), 'the report never quotes a value');
});

if (process.exitCode) process.exit(process.exitCode);
console.log(`All subs-decoded-spelling tests passed (${passed})`);
