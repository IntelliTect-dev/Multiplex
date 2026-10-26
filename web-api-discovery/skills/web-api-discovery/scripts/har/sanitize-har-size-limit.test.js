#!/usr/bin/env node
// Behavior tests for what the scrub does with a capture past the size a single
// string can hold (issues #528, #10).
//
// Zero-dep, runs with `node sanitize-har-size-limit.test.js`. Exits non-zero
// when anything fails.
//
// THE HISTORY. An operator recorded an 816 MB capture of a live, fragile
// account, and the scrub said `Cannot create a string longer than 0x1fffffe8
// characters`. #528 turned that into a refusal that named the file, its size
// and the limit, decided on a `statSync` alone. #10 then removed the limit:
// the scrub now reads a capture entry by entry and never holds it as one
// string, so size is no longer a reason to refuse (sanitize-har-stream.test.js
// pins that it scrubs a capture larger than its heap).
//
// What survives from #528 is pinned here:
//
//  1. A capture past the old ceiling is NOT refused for its size. The sparse
//     file below is 576 MB of NUL bytes, so it is refused -- as not being a
//     HAR, which is the truth about it -- and never with the old size text.
//  2. A refused run writes nothing: no output, no substitution table, no
//     partial file.
//  3. The raw is left exactly as it was.
//
// This suite is slower than it was: the scrub now READS the sparse file to
// find out it is not a HAR, where it used to refuse on its size unread.

'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const { initProtectedRepo } = require(path.join(__dirname, 'har-test-repo.test-support.js'));
const captureSize = require(path.join(__dirname, '..', 'lib', 'capture-size.js'));

const sanitize = path.join(__dirname, 'sanitize-har.js');
const tmp = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'sanitize-size-')));

let failures = 0;
let ran = 0;

function test(name, fn) {
    ran++;
    try {
        fn();
    } catch (e) {
        failures++;
        process.stderr.write('FAIL: ' + name + '\n  ' + e.message + '\n');
        if (e.stack) process.stderr.write(e.stack.split('\n').slice(1, 4).join('\n') + '\n');
    }
}

function runScrub(args, cwd) {
    try {
        const out = execFileSync(process.execPath, [sanitize, ...args], {
            encoding: 'utf8', cwd, stdio: ['ignore', 'pipe', 'pipe'],
        });
        return { code: 0, stdout: out, stderr: '' };
    } catch (e) {
        return {
            code: e.status ?? 1,
            stdout: e.stdout?.toString() ?? '',
            stderr: e.stderr?.toString() ?? '',
        };
    }
}

/**
 * A file of the requested length whose contents were never written.
 *
 * Contents never written: all NUL bytes, which is not a HAR. Producing real
 * contents at this size would make the suite unrunnable.
 */
function sparseFile(filePath, bytes) {
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    const fd = fs.openSync(filePath, 'w');
    try { fs.ftruncateSync(fd, bytes); } finally { fs.closeSync(fd); }
    return filePath;
}

const OVERSIZE = captureSize.MAX_STRING_LENGTH + 64 * 1024 * 1024;

function project(name, { withProfile = true } = {}) {
    const dir = path.join(tmp, name);
    initProtectedRepo(dir);
    if (withProfile) {
        fs.writeFileSync(path.join(dir, '.har-profile.json'),
            JSON.stringify({ salt: 'test-salt', literals: {} }, null, 2));
    }
    return dir;
}

// --- 1. Size is not a reason to refuse. ---

test('a capture past the old ceiling is refused for what it is, never for its size', () => {
    const dir = project('named');
    const raw = sparseFile(path.join(dir, '.har-captures', 'session', 'raw.har'), OVERSIZE);

    const r = runScrub(['--in', raw], dir);

    assert.notStrictEqual(r.code, 0, 'a file that is not a HAR must not exit 0');
    assert.ok(/raw.har/.test(r.stderr), 'names the file: ' + r.stderr);
    assert.ok(/cannot be read as a HAR/.test(r.stderr), 'says it is not a HAR: ' + r.stderr);
    assert.ok(!/0x1fffffe8|single string|ceiling/.test(r.stderr),
        'and says nothing about a size limit: ' + r.stderr);
});

// --- 2. Nothing is written, and the raw survives untouched. ---

test('a refused run writes no output, no substitution table and no partial file', () => {
    const dir = project('nothing-written');
    const rawDir = path.join(dir, '.har-captures', 'session');
    const raw = sparseFile(path.join(rawDir, 'raw.har'), OVERSIZE);
    const outDir = path.join(dir, 'samples', 'har');
    const out = path.join(outDir, 'scrubbed.har');

    const r = runScrub(['--in', raw, '--out', out], dir);

    assert.notStrictEqual(r.code, 0);
    assert.ok(!fs.existsSync(out), 'no scrubbed output may be left behind');
    const strays = fs.readdirSync(rawDir).filter((f) => f !== 'raw.har');
    assert.deepStrictEqual(strays, [],
        'no substitution table beside the raw: ' + strays.join(', '));
    const partials = fs.existsSync(outDir) ? fs.readdirSync(outDir) : [];
    assert.deepStrictEqual(partials, [], 'no partial file: ' + partials.join(', '));
});

test('the raw capture is left exactly as it was', () => {
    // The raw is the only surviving copy of an unrepeatable session. A refusal
    // that rewrote, truncated or moved it would be a worse defect than the one
    // being fixed.
    const dir = project('raw-untouched');
    const raw = sparseFile(path.join(dir, '.har-captures', 'session', 'raw.har'), OVERSIZE);
    const before = fs.statSync(raw);

    runScrub(['--in', raw], dir);

    const after = fs.statSync(raw);
    assert.strictEqual(after.size, before.size, 'the raw kept its size');
    assert.strictEqual(after.mtimeMs, before.mtimeMs, 'the raw was not rewritten');
});

// --- 3. An ordinary capture is unaffected. ---

test('a capture below the ceiling is not refused', () => {
    const dir = project('ordinary');
    const raw = path.join(dir, '.har-captures', 'session', 'raw.har');
    fs.mkdirSync(path.dirname(raw), { recursive: true });
    fs.writeFileSync(raw, JSON.stringify({
        log: {
            version: '1.2',
            creator: { name: 'test', version: '1' },
            entries: [],
        },
    }, null, 2), 'utf8');

    const r = runScrub(['--in', raw, '--out', path.join(dir, 'samples', 'har', 'out.har')], dir);

    assert.strictEqual(r.code, 0, 'an ordinary capture still scrubs: ' + r.stderr);
    assert.ok(!/ceiling|single string/.test(r.stderr),
        'and says nothing about size: ' + r.stderr);
});

// ---------------------------------------------------------------------------

if (failures) {
    process.stderr.write('\n' + failures + ' of ' + ran + ' sanitize-har size-limit tests FAILED\n');
    process.exitCode = 1;
} else {
    process.stdout.write('All sanitize-har size-limit tests passed (' + ran + ')\n');
}

try { fs.rmSync(tmp, { recursive: true, force: true }); } catch (err) { void err; }
