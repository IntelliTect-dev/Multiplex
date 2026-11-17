#!/usr/bin/env node
/**
 * pipeline-check.js -- "the pipeline worked", as four mechanical checks over a
 * pinned set of raw captures (issue #21).
 *
 * Every verification of the post-capture pipeline used to be a person running
 * stages by hand against one or two captures and reading the output. That is
 * how a fix that passed eleven unit tests and left the real capture untouched
 * got as far as a pull request. This runs the real stages, as subprocesses,
 * over captures named by SHA-256, and answers four questions per capture:
 *
 *   1. gate        verify-scrub exits 0 (or 4: advisory findings only).
 *   2. survivors   no value this run's own substitution table says to replace
 *                  survives. sanitize-har.js enforces this itself and refuses
 *                  with exit 5; the runner reads that verdict rather than
 *                  re-deriving it, because the originals exist only inside the
 *                  scrub's process.
 *   3. payloads    every JSON body that parsed before the scrub still parses
 *                  after it (#25). The gate does not ask this, and it has
 *                  already passed captures whose bodies the scrub broke.
 *   4. catalogue   the catalogue scaffold's rows account for every scrubbed
 *                  entry -- a row per group, and the groups' counts summing to
 *                  the entry count.
 *
 * NEVER PRINTS A VALUE. Output and run records carry counts, kinds, entry
 * indexes and exit codes. The children's own stderr can name fingerprints and
 * sentinels, so it is written to a log inside the (gitignored) work directory
 * and never echoed.
 *
 * A RATCHET, NOT A SNAPSHOT. Each run is recorded, and compared with the last
 * record for the same manifest. A capture that passed before and fails now is
 * a regression and exits 4 -- the one outcome a fix loop must never accept.
 * Comparison is refused when the salt changed: every fake would differ, and a
 * 100% diff answers nothing.
 *
 * Usage:
 *   node pipeline-check.js --manifest <pinned.json> [--out <dir>]
 *                          [--profile <.har-profile.json>] [--only <label>]
 *                          [--heap-mb <n>] [--keep-work]
 *
 * The manifest:
 *   { "captures": [ { "label": "fb-login", "path": "<relative to the manifest>",
 *                     "sha256": "<hex>", "why": "<why it is pinned>" } ] }
 *
 * Exit codes:
 *   0 -- every capture passed (skips are reported, and are not passes)
 *   1 -- I/O, manifest or profile error
 *   2 -- usage error
 *   3 -- at least one capture failed a check; nothing regressed
 *   4 -- REGRESSION: a capture that passed in the previous run fails now
 */

'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { spawnSync } = require('child_process');

const harDocument = require('./har-document.js');
const harProfile = require('./har-profile.js');

const HAR_DIR = __dirname;
const EXIT = { ok: 0, error: 1, usage: 2, failed: 3, regressed: 4 };
const SCRUB_SURVIVOR_EXIT = 5;
const GATE_ADVISORY_EXIT = 4;
const RECORD_SCHEMA = 1;
// What the four checks ASK. Bumped whenever a check starts asking something
// new: a capture that passed an older, weaker check and fails a stricter one
// has not regressed, and comparing the two runs would say it had.
const CHECKS_VERSION = 2;
// Bodies a check lists by location. Counts are always complete; the list is
// capped so one badly broken capture cannot bury the rest of the report.
const MAX_LOCATIONS = 20;

function usage(msg) {
    if (msg) console.error(`pipeline-check: ${msg}`);
    console.error('usage: node pipeline-check.js --manifest <pinned.json> [--out <dir>] ' +
        '[--profile <path>] [--only <label>] [--heap-mb <n>] [--keep-work]');
    process.exit(EXIT.usage);
}

function parseArgs(argv) {
    const out = {};
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--keep-work') { out['keep-work'] = true; continue; }
        if (a.startsWith('--')) { out[a.slice(2)] = argv[++i]; continue; }
        usage(`unexpected argument ${a}`);
    }
    return out;
}

/** Read and check the manifest. Paths resolve against the manifest's folder. */
function loadManifest(manifestPath) {
    const doc = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
    if (!doc || !Array.isArray(doc.captures) || doc.captures.length === 0) {
        throw new Error(`${manifestPath}: no "captures" array`);
    }
    const base = path.dirname(path.resolve(manifestPath));
    const labels = new Set();
    return doc.captures.map((c, i) => {
        for (const field of ['label', 'path', 'sha256']) {
            if (typeof c[field] !== 'string' || !c[field]) {
                throw new Error(`${manifestPath}: captures[${i}] has no "${field}"`);
            }
        }
        if (!/^[a-z0-9][a-z0-9._-]*$/i.test(c.label)) {
            throw new Error(`${manifestPath}: captures[${i}].label must be a plain name`);
        }
        if (labels.has(c.label)) throw new Error(`${manifestPath}: label "${c.label}" is repeated`);
        labels.add(c.label);
        return { label: c.label, file: path.resolve(base, c.path), sha256: c.sha256.toLowerCase() };
    });
}

function sha256File(file) {
    const hash = crypto.createHash('sha256');
    const fd = fs.openSync(file, 'r');
    const buf = Buffer.alloc(1 << 20);
    try {
        let n;
        while ((n = fs.readSync(fd, buf, 0, buf.length, null)) > 0) hash.update(buf.subarray(0, n));
    } finally {
        fs.closeSync(fd);
    }
    return hash.digest('hex');
}

/**
 * A non-reversible name for the salt. Two records with different salt ids
 * cannot be compared: every substituted value differs between them.
 */
function saltId(salt) {
    return crypto.createHash('sha256').update(`pipeline-check salt id\0${salt}`).digest('hex').slice(0, 16);
}

function pipelineCommit() {
    const git = (args) => spawnSync('git', args, { cwd: HAR_DIR, encoding: 'utf8', windowsHide: true });
    const head = git(['rev-parse', 'HEAD']);
    if (head.status !== 0) return { commit: null, dirty: null };
    const status = git(['status', '--porcelain', '--', path.join(HAR_DIR, '..')]);
    return { commit: head.stdout.trim(), dirty: status.stdout.trim().length > 0 };
}

/** Run one stage. Its stderr goes to the work log, never to the console. */
function runStage(name, script, args, ctx) {
    const started = Date.now();
    const r = spawnSync(process.execPath,
        [`--max-old-space-size=${ctx.heapMb}`, path.join(HAR_DIR, script), ...args],
        { encoding: 'utf8', windowsHide: true, maxBuffer: 64 * 1024 * 1024 });
    fs.appendFileSync(ctx.log,
        `== ${name}: exit ${r.status}${r.signal ? ` signal ${r.signal}` : ''}\n` +
        `${r.stdout || ''}${r.stderr || ''}${r.error ? `${r.error.message}\n` : ''}`, 'utf8');
    const outOfMemory = /heap out of memory|Allocation failed/i.test(r.stderr || '');
    return { status: r.status, signal: r.signal || (outOfMemory ? 'out of memory' : null),
        stdout: r.stdout || '', ms: Date.now() - started };
}

/**
 * A stage that never reached its own exit code -- killed for memory, or never
 * started -- says nothing about the pipeline. It is an ERROR, kept out of the
 * ratchet: counting it as a failure turns a machine running out of memory
 * into a "regression" the next run then has to explain away.
 */
// Every stage's own exit codes are small. 128 and above is the process being
// ended from outside -- on Windows an out-of-memory abort arrives as 134 with no
// signal at all.
const ABNORMAL_EXIT = 128;

function died(stage) {
    return stage.status === null || stage.status === undefined
        || stage.status >= ABNORMAL_EXIT || stage.signal === 'out of memory';
}

/** JSON-looking text: the only pieces check 3 holds the scrub to. */
function jsonCandidate(text) {
    if (typeof text !== 'string') return false;
    const t = text.trimStart();
    return t.startsWith('{') || t.startsWith('[');
}

function parses(text) {
    try { JSON.parse(text); return true; } catch { return false; }
}

// The anti-hijacking prefix some providers put in front of a JSON response.
const XSSI_PREFIX = /^\s*for\s*\(;;\);/;

// A field name is printed as a location, so only a plain one is: anything else
// could be data wearing a name's position.
function safeName(name) {
    return /^[A-Za-z0-9_.[\]-]{1,40}$/.test(name) ? name : '?';
}

function formPairs(text) {
    if (typeof text !== 'string' || !text.includes('=') || /\s/.test(text.trim())) return null;
    const out = [];
    for (const part of text.split('&')) {
        const eq = part.indexOf('=');
        if (eq <= 0) return null;
        let value;
        try { value = decodeURIComponent(part.slice(eq + 1).replace(/\+/g, ' ')); } catch { value = null; }
        out.push({ name: part.slice(0, eq), value });
    }
    return out;
}

/**
 * The JSON a body carries, as named pieces: the whole body, each line of a
 * line-delimited body, the body behind a `for (;;);` prefix, and each
 * form-encoded field whose value is JSON (`variables=` is where #25's damage
 * was, and a whole-body check reads right past it). Pieces are keyed by
 * where they sit, so the scrub's output is compared piece for piece.
 *
 * A piece's KEY is a location -- a side, a field name, a line number -- and
 * never a value.
 */
function jsonPieces(side, text, params) {
    const out = new Map();
    const add = (where, piece) => { if (jsonCandidate(piece)) out.set(`${side}:${where}`, piece); };
    if (typeof text === 'string') {
        const body = text.replace(XSSI_PREFIX, '');
        if (jsonCandidate(body) && parses(body)) add('body', body);
        else if (body.includes('\n')) {
            body.split('\n').forEach((line, i) => { if (line.trim()) add(`line ${i + 1}`, line); });
        } else add('body', body);
        const pairs = formPairs(text);
        if (pairs) {
            pairs.forEach((p, i) => { if (p.value !== null) add(`field ${safeName(p.name)}#${i}`, p.value); });
        }
    }
    if (Array.isArray(params)) {
        params.forEach((p, i) => {
            if (p && typeof p.value === 'string') add(`param ${safeName(String(p.name))}#${i}`, p.value);
        });
    }
    return out;
}

function piecesOf(entry) {
    const req = entry && entry.request && entry.request.postData;
    const res = entry && entry.response && entry.response.content;
    const out = new Map();
    if (req) for (const [k, v] of jsonPieces('request', req.text, req.params)) out.set(k, v);
    if (res && res.encoding !== 'base64') for (const [k, v] of jsonPieces('response', res.text)) out.set(k, v);
    return out;
}

/**
 * Check 3. Walks the scrub's input and output in lockstep, one entry of each
 * at a time, so it holds two entries whatever the size of the capture.
 *
 * Entries are paired by POSITION. The scrub neither drops, adds nor reorders
 * entries, and a changed count fails the check outright; a scrub that dropped
 * one entry and duplicated another would pair unrelated entries, and this
 * check would not notice.
 */
function comparePayloads(beforePath, afterPath) {
    const before = harDocument.iterateHarEntries(beforePath);
    const after = harDocument.iterateHarEntries(afterPath);
    const result = { entriesBefore: 0, entriesAfter: 0, parsedBefore: 0, broken: 0, locations: [] };
    for (let index = 0; ; index++) {
        const b = before.next();
        const a = after.next();
        if (!b.done) result.entriesBefore++;
        if (!a.done) result.entriesAfter++;
        if (b.done && a.done) break;
        // One side ran out: keep walking the other so both counts are true.
        if (b.done || a.done) continue;
        const was = piecesOf(b.value);
        const now = piecesOf(a.value);
        for (const [where, text] of was) {
            if (!parses(text)) continue;
            result.parsedBefore++;
            const twin = now.get(where);
            if (twin !== undefined && parses(twin)) continue;
            result.broken++;
            if (result.locations.length < MAX_LOCATIONS) result.locations.push({ entry: index, where });
        }
    }
    return result;
}

/** Check 4. The scaffold the capture pipeline would write, reconciled. */
function reconcileCatalogue(scrubbedPath) {
    // Loaded here, not at the top: capture-har.js is large and only this
    // check needs it.
    const captureHar = require('../capture/capture-har.js');
    // Each entry is read as it streams past and only what the digest GROUPS
    // on is kept -- URL, method, status, content type, timing. The bodies it
    // would read for shapes decide no row and no count, and holding them is
    // what would make the largest capture exhaust memory.
    const entries = [];
    for (const e of harDocument.iterateHarEntries(scrubbedPath)) {
        const req = (e && e.request) || {};
        const res = (e && e.response) || {};
        entries.push({
            startedDateTime: e && e.startedDateTime,
            time: e && e.time,
            request: { url: req.url, method: req.method },
            response: { status: res.status, content: { mimeType: res.content && res.content.mimeType } },
        });
    }
    const digest = captureHar.buildDigest({ log: { entries } }, { capturedUtc: '1970-01-01T00:00:00.000Z' });
    const rows = captureHar.buildCatalogueScaffold(digest, {});
    const rowEntries = rows.reduce((n, r) => n + (r.EntryCount || 0), 0);
    return { entries: entries.length, rows: rows.length, rowEntries };
}

/** The numbers sanitize-har reports on its stable stdout lines. Counts only. */
function scrubCounts(stdout) {
    const counts = {};
    const wrote = /\((\d+) legacy \+ (\d+) typed-PII substitutions;.*substitution sweep: (\d+) occurrence/.exec(stdout);
    if (wrote) {
        counts.legacySubstitutions = Number(wrote[1]);
        counts.typedPiiSubstitutions = Number(wrote[2]);
        counts.sweptOccurrences = Number(wrote[3]);
    }
    const blunted = /^sanitize-har: blunted: (.+)$/m.exec(stdout);
    if (blunted) {
        const numbers = blunted[1].match(/\d+/g);
        counts.bluntedValues = numbers ? Number(numbers[0]) : null;
    }
    return counts;
}

/** Gate findings by class and kind, from the report verify-scrub writes. */
function gateCounts(scrubbedPath) {
    const report = path.join(path.dirname(scrubbedPath), 'scrub-findings.json');
    if (!fs.existsSync(report)) return { gating: 0, advisory: 0, reported: 0, byKind: {} };
    const doc = JSON.parse(fs.readFileSync(report, 'utf8'));
    const byKind = {};
    for (const f of doc.findings || []) {
        const key = `${f.class || 'unknown'}:${f.kind || 'unknown'}`;
        byKind[key] = (byKind[key] || 0) + 1;
    }
    return Object.assign({}, doc.counts || {}, { byKind });
}

/** Everything about one capture. Never throws: a failure is a result. */
function checkCapture(capture, ctx) {
    const result = { label: capture.label, sha256: capture.sha256, status: 'fail', checks: {}, counts: {} };
    const work = path.join(ctx.runDir, capture.label);
    fs.mkdirSync(work, { recursive: true });
    const stageCtx = Object.assign({}, ctx, { log: path.join(work, 'stages.log') });
    const fail = (reason) => { result.reason = reason; return result; };
    const infra = (name, stage) => {
        result.status = 'error';
        result.reason = `the ${name} stage did not finish (${stage.signal || 'no exit code'}) -- not a pipeline verdict`;
        return result;
    };

    if (!fs.existsSync(capture.file)) return fail('the pinned capture is missing');
    result.bytes = fs.statSync(capture.file).size;
    if (sha256File(capture.file) !== capture.sha256) {
        return fail('the file at the pinned path no longer has the pinned SHA-256');
    }

    const trimmed = path.join(work, 'trimmed.har');
    const trim = runStage('trim', 'trim-har-capture.js', ['--in', capture.file, '--out', trimmed], stageCtx);
    if (died(trim)) return infra('trim', trim);
    result.checks.trim = trim.status === 0 ? 'pass' : `exit ${trim.status}`;
    if (trim.status !== 0) return fail('the trim failed');
    result.counts.trimmedBytes = fs.statSync(trimmed).size;

    const scrubbed = path.join(work, 'scrubbed.har');
    const scrub = runStage('scrub', 'sanitize-har.js', [
        '--in', trimmed, '--out', scrubbed,
        '--subs', path.join(work, 'subs.legacy.json'),
        '--pii-subs', path.join(work, 'subs.pii.json'),
        '--profile', ctx.profilePath,
        '--fixed-time', '1970-01-01T00:00:00.000Z',
    ], stageCtx);
    if (died(scrub)) return infra('scrub', scrub);
    result.checks.survivors = scrub.status === SCRUB_SURVIVOR_EXIT ? 'fail'
        : scrub.status === 0 ? 'pass' : 'not reached';
    if (scrub.status !== 0) {
        return fail(scrub.status === SCRUB_SURVIVOR_EXIT
            ? 'values the scrub itself said to replace survived it'
            : `the scrub failed (exit ${scrub.status})`);
    }
    Object.assign(result.counts, scrubCounts(scrub.stdout));

    const gate = runStage('gate', 'verify-scrub.js', ['--in', scrubbed, '--profile', ctx.profilePath], stageCtx);
    if (died(gate)) return infra('gate', gate);
    result.checks.gate = gate.status === 0 ? 'pass'
        : gate.status === GATE_ADVISORY_EXIT ? 'advisory' : `exit ${gate.status}`;
    result.counts.gate = gateCounts(scrubbed);

    try {
        const payloads = comparePayloads(trimmed, scrubbed);
        result.checks.payloads = payloads.broken === 0 && payloads.entriesBefore === payloads.entriesAfter
            ? 'pass' : 'fail';
        result.counts.payloads = payloads;
    } catch (e) {
        result.checks.payloads = 'error';
        result.counts.payloads = { error: e.code || e.name };
    }

    try {
        const cat = reconcileCatalogue(scrubbed);
        result.checks.catalogue = cat.rowEntries === cat.entries && (cat.entries === 0 || cat.rows > 0)
            ? 'pass' : 'fail';
        result.counts.catalogue = cat;
    } catch (e) {
        result.checks.catalogue = 'error';
        result.counts.catalogue = { error: e.code || e.name };
    }

    const failed = Object.entries(result.checks)
        .filter(([, v]) => v !== 'pass' && v !== 'advisory').map(([k]) => k);
    if (failed.length) return fail(`failed: ${failed.join(', ')}`);
    result.status = 'pass';
    return result;
}

/** The newest earlier record for the same manifest, or null. */
function previousRecord(outDir, manifestId, excludeFile) {
    if (!fs.existsSync(outDir)) return null;
    const files = fs.readdirSync(outDir).filter((f) => /^run-.*\.json$/.test(f)).sort().reverse();
    for (const f of files) {
        const full = path.join(outDir, f);
        if (full === excludeFile) continue;
        try {
            const doc = JSON.parse(fs.readFileSync(full, 'utf8'));
            if (doc.schema === RECORD_SCHEMA && doc.manifestId === manifestId) return Object.assign(doc, { file: full });
        } catch { /* not a record */ }
    }
    return null;
}

/**
 * The ratchet. A capture that passed and now fails is a regression; so is one
 * whose broken-payload or gating count went up, even while it was already
 * failing -- a fix loop that trades one defect for two has not converged.
 */
function compare(previous, current) {
    if (!previous) return { comparable: false, reason: 'no earlier run for this manifest', regressions: [] };
    if (previous.saltId !== current.saltId) {
        return { comparable: false, reason: 'the salt changed since the earlier run', regressions: [] };
    }
    if (previous.checksVersion !== current.checksVersion) {
        return { comparable: false, reason: 'the checks changed since the earlier run', regressions: [] };
    }
    const regressions = [];
    const before = new Map(previous.captures.map((c) => [c.label, c]));
    for (const now of current.captures) {
        const was = before.get(now.label);
        if (!was || was.sha256 !== now.sha256) continue;
        if (was.status === 'pass' && now.status === 'fail') {
            regressions.push({ label: now.label, what: 'passed before, fails now' });
            continue;
        }
        // Per check, too: a capture already failing on one check must still
        // be caught failing on ANOTHER. The survivor guard going red inside a
        // capture that was already red for its catalogue is exactly the leak
        // this runner exists to see. A run that did not finish says nothing.
        if (now.status !== 'error' && now.status !== 'skipped' && was.status !== 'error' && was.status !== 'skipped') {
            const ok = (v) => v === 'pass' || v === 'advisory';
            for (const [name, before] of Object.entries(was.checks || {})) {
                const after = (now.checks || {})[name];
                if (ok(before) && !ok(after)) {
                    regressions.push({ label: now.label, what: `check ${name}: ${before} -> ${after || 'not reached'}` });
                }
            }
        }
        const worse = (get, what) => {
            const a = get(was); const b = get(now);
            if (Number.isFinite(a) && Number.isFinite(b) && b > a) regressions.push({ label: now.label, what: `${what} ${a} -> ${b}` });
        };
        worse((c) => c.counts && c.counts.payloads && c.counts.payloads.broken, 'broken payloads');
        worse((c) => c.counts && c.counts.gate && c.counts.gate.gating, 'gating findings');
    }
    return { comparable: true, against: path.basename(previous.file), regressions };
}

function summaryLines(record, comparison) {
    const lines = [];
    const cols = ['trim', 'survivors', 'gate', 'payloads', 'catalogue'];
    lines.push(['capture'.padEnd(28), 'status'.padEnd(8), ...cols.map((c) => c.padEnd(10))].join(' '));
    for (const c of record.captures) {
        lines.push([c.label.padEnd(28), c.status.padEnd(8),
            ...cols.map((k) => String(c.checks[k] || '-').padEnd(10))].join(' ').trimEnd());
        if (c.reason) lines.push(`    ${c.reason}`);
        const p = c.counts.payloads;
        if (p && p.broken) {
            lines.push(`    ${p.broken} of ${p.parsedBefore} JSON bodies no longer parse, e.g. ` +
                p.locations.slice(0, 5).map((l) => `entry ${l.entry} ${l.where}`).join(', '));
        }
        const g = c.counts.gate;
        if (g && (g.gating || g.advisory)) {
            lines.push(`    gate: ${g.gating || 0} gating, ${g.advisory || 0} advisory -- ` +
                Object.entries(g.byKind).map(([k, n]) => `${k} x${n}`).join(', '));
        }
    }
    const tally = (s) => record.captures.filter((c) => c.status === s).length;
    lines.push(`pipeline-check: ${tally('pass')} passed, ${tally('fail')} failed, ${tally('skipped')} skipped, ` +
        `${tally('error')} did not finish ` +
        `(pipeline ${record.pipeline.commit ? record.pipeline.commit.slice(0, 7) : 'unknown'}` +
        `${record.pipeline.dirty ? '+dirty' : ''}, salt id ${record.saltId})`);
    if (!comparison.comparable) lines.push(`pipeline-check: not compared -- ${comparison.reason}`);
    else if (!comparison.regressions.length) lines.push(`pipeline-check: no regressions against ${comparison.against}`);
    else {
        lines.push(`pipeline-check: REGRESSION against ${comparison.against}:`);
        for (const r of comparison.regressions) lines.push(`    ${r.label}: ${r.what}`);
    }
    return lines;
}

function main() {
    const args = parseArgs(process.argv.slice(2));
    if (!args.manifest) usage('--manifest is required');
    const heapMb = args['heap-mb'] ? Number(args['heap-mb']) : 4096;
    if (!Number.isInteger(heapMb) || heapMb < 64) usage('--heap-mb must be a whole number of megabytes, at least 64');

    let captures; let profile; let profilePath;
    try {
        captures = loadManifest(args.manifest);
        if (args.only) {
            captures = captures.filter((c) => c.label === args.only);
            if (!captures.length) throw new Error(`no capture labelled "${args.only}" in the manifest`);
        }
        // An absent profile is a hard failure: without the salt nothing is
        // deterministic, and a run nobody can compare is not a measurement.
        profilePath = args.profile ? path.resolve(args.profile)
            : harProfile.findProfilePath(path.dirname(path.resolve(args.manifest)));
        if (!profilePath) throw new Error('no .har-profile.json above the manifest; pass --profile');
        profile = harProfile.loadProfile({ profilePath });
    } catch (e) {
        console.error(`pipeline-check: ${e.message}`);
        return EXIT.error;
    }

    const outDir = path.resolve(args.out || path.join(path.dirname(path.resolve(args.manifest)), 'runs'));
    // An earlier run's work directories hold scrubbed copies and substitution
    // tables -- a reverse lookup of live credentials -- kept only so a failure
    // could be looked into. They are not kept past the next run.
    if (fs.existsSync(outDir)) {
        for (const d of fs.readdirSync(outDir).filter((x) => x.startsWith('work-'))) {
            fs.rmSync(path.join(outDir, d), { recursive: true, force: true });
        }
    }
    const runId = new Date().toISOString().replace(/[:.]/g, '-');
    const runDir = path.join(outDir, `work-${runId}`);
    fs.mkdirSync(runDir, { recursive: true });
    const manifestId = crypto.createHash('sha256')
        .update(captures.map((c) => `${c.label}\0${c.sha256}`).join('\n')).digest('hex').slice(0, 16);

    const record = {
        schema: RECORD_SCHEMA,
        checksVersion: CHECKS_VERSION,
        runId,
        manifestId,
        saltId: saltId(profile.salt),
        pipeline: pipelineCommit(),
        node: process.version,
        captures: [],
    };
    const ctx = { runDir, heapMb, profilePath };
    for (const capture of captures) {
        console.log(`pipeline-check: ${capture.label} ...`);
        const started = Date.now();
        const result = checkCapture(capture, ctx);
        result.ms = Date.now() - started;
        record.captures.push(result);
        // A passing capture's work directory holds a scrubbed copy and two
        // substitution tables -- a reverse lookup of live credentials. Nothing
        // needs them once the capture passed.
        if (result.status === 'pass' && !args['keep-work']) {
            fs.rmSync(path.join(runDir, capture.label), { recursive: true, force: true });
        }
    }
    if (!args['keep-work'] && fs.readdirSync(runDir).length === 0) fs.rmdirSync(runDir);

    const recordPath = path.join(outDir, `run-${runId}.json`);
    const comparison = compare(previousRecord(outDir, manifestId, recordPath), record);
    record.comparison = comparison;
    fs.writeFileSync(recordPath, `${JSON.stringify(record, null, 2)}\n`, 'utf8');
    for (const line of summaryLines(record, comparison)) console.log(line);
    console.log(`pipeline-check: record written to ${recordPath}`);

    if (comparison.regressions.length) return EXIT.regressed;
    return record.captures.some((c) => c.status === 'fail' || c.status === 'error') ? EXIT.failed : EXIT.ok;
}

if (require.main === module) process.exit(main());

module.exports = { loadManifest, comparePayloads, reconcileCatalogue, compare, saltId, scrubCounts, EXIT };
