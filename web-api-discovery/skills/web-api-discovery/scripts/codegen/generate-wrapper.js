#!/usr/bin/env node
/**
 * generate-wrapper.js -- HAR -> C# wrapper project codegen pipeline.
 *
 * Pipeline:
 *   1. Load HAR(s) + auth-detection JSON + project metadata.
 *   2. Extract endpoints, detect GraphQL, dedup path patterns, and collect each
 *      pattern's query parameters across all its captured calls (query-params.js).
 *   3. Infer + merge JSON shapes -> typed models.
 *   4. Emit Client.Generated.cs + Models.Generated.cs (partial classes), and
 *      <Name>Tools.Generated.cs -- the MCP tool class, one tool per client method.
 *      Both take the endpoint's query parameters; what each defaults to comes
 *      from what the capture showed, and a scrubbed value is never a default.
 *   5. Substitute existing csharp/*.tmpl files (Client.cs, ISessionStore.cs, etc.).
 *   6. Emit .csproj with NuGet metadata.
 *   7. Emit README.md (recipes + polite-crawl note).
 *   8. Emit tests/fixtures/*.json (one per endpoint).
 *
 * Deterministic: same HAR + flags = byte-identical output. Stable sort, no timestamps in output.
 *
 * Usage:
 *   node generate-wrapper.js --har <p1[,p2,...]> --out <dir> \
 *     --project-name <Name> --namespace <Ns> --base-url <https://x> \
 *     [--auth-model cookie|cookie+csrf|bearer|...] [--auth-detection <path>] \
 *     [--authors <s>] [--description <s>] [--repository-url <s>] [--package-tags <s>]
 *
 * Zero npm deps. Pure Node stdlib.
 */

'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { emitTestScaffold } = require('./tests-emit.js');
const { listTools, listLeftOut, emitToolsFile } = require('./mcp-tools-emit.js');
const { collectQueryParameters, loadScrubTables } = require('./query-params.js');
const {
    describeQueryParameters, clientQueryLines, clientQueryHelperLines, queryReadmeSection, recipeQueryArguments, QUERY_LOCAL,
} = require('./query-emit.js');
const { emitSolution } = require('./sln-emit.js');
const { emitSecretGate, secretGateReadmeSection } = require('./secret-gate-emit.js');
const { detectAntiBotCookies } = require('../har/detect-auth.js');
// The canonical substitution-table filenames, imported rather than re-spelled
// (issue #446). subs-destination.js is dependency-free and safe to require.
const { LEGACY_SUBS_FILENAME, PII_SUBS_FILENAME } = require('../har/subs-destination.js');

// -------------------- CLI --------------------

function parseArgs(argv) {
    const out = {};
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a.startsWith('--')) {
            const key = a.slice(2);
            const next = argv[i + 1];
            if (next === undefined || next.startsWith('--')) { out[key] = true; }
            else { out[key] = next; i++; }
        }
    }
    return out;
}

function fail(msg) { console.error('generate-wrapper: ' + msg); process.exit(1); }

// -------------------- Consumer .gitignore maintenance --------------------

// Entries this skill owns. They live in the consumer's repo-root .gitignore
// (not upstream IntelliSDLC.ai's .gitignore -- moved here per issue #119) so
// only repos that actually scaffold a wrapper carry them.
//
// - Samples/HAR-Original/    real captures, always contain real PII/tokens.
// - Samples/MobileApp-Binaries/  downloaded apk/ipa binaries; documented as
//                            "always gitignored" in SKILL.md Phase 1.
// - .har-profile.json        the operator's own salt and identifier ->
//                            sentinel map; an operator secret, never project
//                            configuration, and the reason the scrub tooling
//                            refuses to run without it.
// - .har-substitutions.json  the legacy substitution map, keyed by
//                            `<kind>:<original>` -- the KEYS are the secrets.
//                            Spelled by subs-destination.js, not here: an
//                            entry that names a file the scrub no longer
//                            writes protects nothing (issue #446).
// - .substitutions.json      the typed-PII table, keyed the same way. Both
//                            default beside the raw capture in the gitignored
//                            captures tree now (issue #294), but an operator
//                            who ran an earlier scrub has a copy sitting in a
//                            committed directory, and only these two entries
//                            stop `git add -A` publishing what the scrub was
//                            run to remove.
// - .har-captures/           the recorder's own tree: the raw capture lives
//                            here and carries live session cookies. The
//                            scrub tooling's comments call this tree
//                            gitignored; without this entry that was true of
//                            this repo's hand-authored root .gitignore only,
//                            not of a scaffolded consumer.
const SCAFFOLD_GITIGNORE_ENTRIES = [
    'Samples/HAR-Original/',
    'Samples/MobileApp-Binaries/',
    '.har-profile.json',
    LEGACY_SUBS_FILENAME,
    PII_SUBS_FILENAME,
    '.har-captures/',
];

// What the appended block SAYS about itself. Parameterised because the header
// has to describe the entries actually being written: a caller appending only
// the capture-store rules (capture-har.js does, #377) would otherwise plant a
// comment about HAR-Original/ and MobileApp-Binaries/ in the operator's
// .gitignore above a block containing neither. A wrong provenance note is worse
// than none -- it sends the next reader looking for files that are not there.
const SCAFFOLD_GITIGNORE_HEADER = [
    '# Added by web-api-discovery (see skills/web-api-discovery/SKILL.md).',
    '# HAR-Original/ contains real PII; MobileApp-Binaries/ contains downloaded apk/ipa.',
];

function ensureRepoRootGitignoreHasScaffoldEntries(outDir, entries, header) {
    // Idempotent: appends only entries that are not already present (exact
    // line match, ignoring leading/trailing whitespace). Creates the file
    // when absent. Returns the array of entries actually added (empty when
    // already in sync).
    const list = entries || SCAFFOLD_GITIGNORE_ENTRIES;
    const heading = header || SCAFFOLD_GITIGNORE_HEADER;
    const target = path.join(outDir, '.gitignore');
    let existing = '';
    let hadFile = false;
    if (fs.existsSync(target)) {
        existing = fs.readFileSync(target, 'utf8');
        hadFile = true;
    }
    const present = new Set(
        existing.split(/\r?\n/).map((l) => l.trim()).filter(Boolean)
    );
    const missing = list.filter((e) => !present.has(e.trim()));
    if (missing.length === 0) return [];
    // Detect existing line ending. Default LF.
    const eol = /\r\n/.test(existing) ? '\r\n' : '\n';
    let block = '';
    if (hadFile && existing.length > 0 && !existing.endsWith('\n') && !existing.endsWith('\r')) {
        block += eol;
    }
    if (hadFile && existing.length > 0) { block += eol; }
    for (const line of heading) { block += line + eol; }
    block += missing.join(eol) + eol;
    fs.writeFileSync(target, existing + block);
    return missing;
}

// -------------------- HAR loading --------------------

function loadHar(filePath) {
    const text = fs.readFileSync(filePath, 'utf8');
    const sha = crypto.createHash('sha256').update(text).digest('hex');
    return { har: JSON.parse(text), sha, path: filePath };
}

function* iterEntries(harBundles) {
    for (const b of harBundles) {
        const entries = (b.har && b.har.log && b.har.log.entries) || [];
        for (const e of entries) { yield { entry: e, sourceSha: b.sha }; }
    }
}

// -------------------- Endpoint extraction + GraphQL detection --------------------

function isGraphQL(entry) {
    const req = entry.request || {};
    if (req.method !== 'POST') return false;
    const ct = headerValue(req.headers, 'content-type') || '';
    if (ct.toLowerCase().includes('application/graphql')) return true;
    const body = req.postData && req.postData.text;
    if (!body) return false;
    try {
        const parsed = JSON.parse(body);
        return typeof parsed === 'object' && parsed !== null && typeof parsed.query === 'string';
    } catch { return false; }
}

function headerValue(headers, name) {
    if (!Array.isArray(headers)) return null;
    const lname = name.toLowerCase();
    for (const h of headers) {
        if (h && typeof h.name === 'string' && h.name.toLowerCase() === lname) return h.value;
    }
    return null;
}

function parseUrl(rawUrl) {
    try {
        const u = new URL(rawUrl);
        // Split path into segments, drop empty leading segment.
        const segs = u.pathname.split('/').filter(Boolean);
        // The query's ordered [name, value] pairs, decoded the way a server
        // decodes them (percent-escapes, and `+` as a space). Each call of an
        // endpoint keeps its own, so query-params.js can tell what varied.
        return { host: u.host, segments: segs, pathname: u.pathname, query: Array.from(u.searchParams) };
    } catch { return null; }
}

// -------------------- Path-pattern dedup --------------------

// A segment is a likely path param if it matches one of these intrinsic patterns
// OR if it varies while neighboring segments are stable across captures.
const INTRINSIC_PARAM = [
    { re: /^\d+$/, type: 'int', name: 'id' },
    { re: /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/, type: 'string', name: 'id' },
    { re: /^[0-9a-fA-F]{24,}$/, type: 'string', name: 'id' }, // long hex (mongo ObjectId, etc.)
];

function classifySegment(seg) {
    for (const p of INTRINSIC_PARAM) { if (p.re.test(seg)) return p; }
    return null;
}

// "Opaque id-like" -- numeric, hex, UUID, or base64-ish noise lacking vowel runs.
// These are the values we are confident represent a path *parameter*, not a
// stable resource name. (See issue #62: TripIt endpoints like `appConfig`,
// `gtmDataAsJson`, `purchasedProductInfo` were being mis-classified as `{id}`.)
function isOpaqueIdLike(seg) {
    if (typeof seg !== 'string' || seg.length === 0) return false;
    if (/^\d+$/.test(seg)) return true;                                    // all-numeric
    if (/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(seg)) return true; // UUID
    if (/^[0-9a-fA-F]{24,}$/.test(seg)) return true;                       // long hex (ObjectId, sha, etc.)
    // base64-ish noise: >= 8 chars from the base64url alphabet with no vowel anywhere.
    if (/^[A-Za-z0-9_-]{8,}$/.test(seg) && !/[aeiouyAEIOUY]/.test(seg)) return true;
    return false;
}

// "Named segment" -- looks like a stable identifier word a developer would
// hand-write as a literal path segment (e.g. `appConfig`, `gtmDataAsJson`).
function isNamedSegment(seg) {
    if (typeof seg !== 'string') return false;
    if (!/^[A-Za-z][A-Za-z0-9]{2,}$/.test(seg)) return false;
    return !isOpaqueIdLike(seg);
}

/**
 * Group entries by (method, host, segCount) then within each group find segments
 * that should collapse to {param}. Returns array of pattern descriptors:
 *   { method, host, segments: [{ literal: 'users' } | { param: 'id', type: 'int' }], samples: [entry,...] }
 */
function dedupePatterns(restEntries) {
    const groups = new Map();
    for (const item of restEntries) {
        const u = parseUrl(item.entry.request.url);
        if (!u) continue;
        const key = item.entry.request.method + ' ' + u.host + ' /' + u.segments.length;
        let g = groups.get(key);
        if (!g) { g = { method: item.entry.request.method, host: u.host, len: u.segments.length, items: [] }; groups.set(key, g); }
        g.items.push({ ...item, url: u });
    }

    const patterns = [];
    // Sort groups for deterministic output: by method then host then path.
    const sortedKeys = Array.from(groups.keys()).sort();
    for (const key of sortedKeys) {
        const g = groups.get(key);
        for (const p of processGroup(g)) patterns.push(p);
    }

    // Now merge patterns that have identical (method, host, segments-shape).
    const merged = new Map();
    for (const p of patterns) {
        const sig = patternSignature(p);
        const ex = merged.get(sig);
        if (ex) { ex.samples.push(...p.samples); }
        else { merged.set(sig, p); }
    }
    return Array.from(merged.values()).sort((a, b) =>
        patternSignature(a).localeCompare(patternSignature(b)));
}

// Process one (method, host, segCount) group into one or more pattern descriptors.
// If a varying segment index contains named-looking values (and no opaque values),
// split the group by that segment's value and recurse so each named value becomes
// its own pattern with a literal segment at that index. (Issue #62.)
function processGroup(g) {
    for (let i = 0; i < g.len; i++) {
        const values = g.items.map((it) => it.url.segments[i]);
        const unique = Array.from(new Set(values));
        if (unique.length <= 1) continue;
        // Intrinsic param (all values match same intrinsic) -> param, no split.
        const intrinsics = values.map(classifySegment);
        if (intrinsics.every((x) => x) && allSameType(intrinsics)) continue;
        // Opaque varying (e.g. /trip/123 vs /trip/456) -> param, no split.
        if (values.every(isOpaqueIdLike)) continue;
        // Named varying with no opaque mixed in -> split this group by segment value
        // so each becomes a distinct pattern with a literal segment here.
        if (unique.every(isNamedSegment)) {
            const subgroups = new Map();
            for (const it of g.items) {
                const v = it.url.segments[i];
                if (!subgroups.has(v)) {
                    subgroups.set(v, { method: g.method, host: g.host, len: g.len, items: [] });
                }
                subgroups.get(v).items.push(it);
            }
            const result = [];
            // Deterministic order: sort by segment value.
            const sortedSubKeys = Array.from(subgroups.keys()).sort();
            for (const k of sortedSubKeys) {
                for (const p of processGroup(subgroups.get(k))) result.push(p);
            }
            return result;
        }
        // Mixed (some named, some opaque, or unclassifiable) -> fall through to
        // legacy behavior, which will emit {id} for this index.
    }

    // No named-split applied; build segDescs from this group as a single pattern.
    const segDescs = [];
    for (let i = 0; i < g.len; i++) {
        const values = g.items.map((it) => it.url.segments[i]);
        const unique = Array.from(new Set(values));
        const intrinsics = values.map(classifySegment);
        if (intrinsics.every((x) => x) && allSameType(intrinsics)) {
            segDescs.push({ param: intrinsics[0].name, type: intrinsics[0].type });
        } else if (unique.length === 1) {
            segDescs.push({ literal: unique[0] });
        } else if (unique.length > 1 && g.items.length >= 2) {
            // Varies across captures with same len -> path param. Guess type.
            const allInts = values.every((v) => /^\d+$/.test(v));
            segDescs.push({ param: 'id', type: allInts ? 'int' : 'string' });
        } else {
            segDescs.push({ literal: unique[0] });
        }
    }
    // Disambiguate duplicate param names (rare): id, id2, id3...
    let idCount = 0;
    for (const s of segDescs) {
        if (s.param) {
            if (idCount > 0) s.param = s.param + (idCount + 1);
            idCount++;
        }
    }
    return [{
        method: g.method,
        host: g.host,
        segments: segDescs,
        samples: g.items,
    }];
}

function allSameType(intrinsics) {
    const t = intrinsics[0].type;
    return intrinsics.every((x) => x.type === t);
}

function patternSignature(p) {
    const path = '/' + p.segments.map((s) => s.literal ? s.literal : '{' + s.param + '}').join('/');
    return p.method + ' ' + p.host + ' ' + path;
}

function patternPath(p) {
    return '/' + p.segments.map((s) => s.literal ? s.literal : '{' + s.param + '}').join('/');
}

// -------------------- JSON shape inference + merge --------------------

/**
 * Infer a shape descriptor from a JSON value:
 *   { kind: 'object', fields: { name: { type, nullable, optional } } }
 *   { kind: 'array', element: shape }
 *   { kind: 'primitive', type: 'string'|'int'|'long'|'double'|'bool', nullable }
 *   { kind: 'unknown' }
 */
function inferShape(val) {
    if (val === null) return { kind: 'primitive', type: 'object', nullable: true };
    if (Array.isArray(val)) {
        if (val.length === 0) return { kind: 'array', element: { kind: 'unknown' } };
        let acc = inferShape(val[0]);
        for (let i = 1; i < val.length; i++) acc = mergeShapes(acc, inferShape(val[i]));
        return { kind: 'array', element: acc };
    }
    const t = typeof val;
    if (t === 'string')  return { kind: 'primitive', type: 'string', nullable: false };
    if (t === 'boolean') return { kind: 'primitive', type: 'bool', nullable: false };
    if (t === 'number') {
        if (Number.isInteger(val)) {
            return { kind: 'primitive', type: (Math.abs(val) > 2147483647 ? 'long' : 'int'), nullable: false };
        }
        return { kind: 'primitive', type: 'double', nullable: false };
    }
    if (t === 'object') {
        const fields = {};
        const keys = Object.keys(val).sort();
        for (const k of keys) {
            const sub = inferShape(val[k]);
            // Field-level nullability: was THIS field observed as null in this sample?
            // (Distinct from primitive shape.nullable, which is the value-side flag and
            // gets lost when a null sample is merged with an object/array sample.)
            fields[k] = { shape: sub, optional: false, presentCount: 1, nullable: val[k] === null };
        }
        return { kind: 'object', fields, sampleCount: 1 };
    }
    return { kind: 'unknown' };
}

function isNullSentinel(s) {
    return !!s && s.kind === 'primitive' && s.type === 'object' && s.nullable === true;
}

function mergeShapes(a, b) {
    if (!a || a.kind === 'unknown') return b;
    if (!b || b.kind === 'unknown') return a;
    if (a.kind !== b.kind) {
        // A `null` JSON value infers as { kind:'primitive', type:'object', nullable:true }.
        // When merged against a real object/array shape from another sample, prefer the
        // real shape and let field-level nullability tracking record the null observation
        // (otherwise the shape collapses to JsonElement and the diff is lost).
        if (isNullSentinel(a)) return b;
        if (isNullSentinel(b)) return a;
        // genuinely mixed -> fall back to unknown (will emit as JsonElement)
        return { kind: 'unknown' };
    }
    if (a.kind === 'primitive') {
        const type = widenPrimitive(a.type, b.type);
        return { kind: 'primitive', type, nullable: a.nullable || b.nullable };
    }
    if (a.kind === 'array') {
        return { kind: 'array', element: mergeShapes(a.element, b.element) };
    }
    if (a.kind === 'object') {
        const allKeys = new Set([...Object.keys(a.fields), ...Object.keys(b.fields)]);
        const fields = {};
        const newCount = (a.sampleCount || 1) + (b.sampleCount || 1);
        for (const k of Array.from(allKeys).sort()) {
            const fa = a.fields[k];
            const fb = b.fields[k];
            if (fa && fb) {
                fields[k] = {
                    shape: mergeShapes(fa.shape, fb.shape),
                    optional: fa.optional || fb.optional,
                    presentCount: (fa.presentCount || 0) + (fb.presentCount || 0),
                    nullable: (fa.nullable || false) || (fb.nullable || false),
                };
            } else if (fa) {
                fields[k] = { shape: fa.shape, optional: true, presentCount: fa.presentCount || 0, nullable: fa.nullable || false };
            } else {
                fields[k] = { shape: fb.shape, optional: true, presentCount: fb.presentCount || 0, nullable: fb.nullable || false };
            }
        }
        return { kind: 'object', fields, sampleCount: newCount };
    }
    return a;
}

function widenPrimitive(a, b) {
    if (a === b) return a;
    const order = ['int', 'long', 'double', 'string', 'bool', 'object'];
    // Numeric widening
    if ((a === 'int' || a === 'long' || a === 'double') &&
        (b === 'int' || b === 'long' || b === 'double')) {
        if (a === 'double' || b === 'double') return 'double';
        if (a === 'long' || b === 'long') return 'long';
        return 'int';
    }
    if (a === 'object' || b === 'object') return a === 'object' ? b : a;
    return 'string';
}

// -------------------- Envelope detection (issue #64) --------------------

/**
 * Metadata-name regex: top-level field names that, when present alongside
 * exactly one substantial payload field, indicate a wrapper envelope (e.g.
 * { data, meta }, { items, count }, { result, errors }).
 *
 * Word-anchored, case-insensitive. Underscores are optional between word
 * fragments (so `has_more`, `hasmore`, `hasMore` all match).
 */
const METADATA_NAME_RE = /^(count|total|page|pages|cursor|next|prev|previous|has_?more|timestamp|status|offset|limit|size|per_?page|total_?pages|total_?count|page_?size|errors?|meta|metadata|pagination|links?|info|warnings?|debug)$/i;

/**
 * True when an object shape is "metadata-typed": every field is a primitive
 * scalar AND every field name matches METADATA_NAME_RE. An empty object
 * counts as metadata as well (carries no payload).
 *
 * The structural primitive-only check alone is too aggressive: a payload like
 * `{ id, name }` is also primitive-only, yet clearly the real payload. The
 * name check is what distinguishes wrapper boilerplate (`{ count, page }`)
 * from a small but meaningful payload (`{ id, name }`).
 */
function isMetadataObjectShape(shape) {
    if (!shape || shape.kind !== 'object') return false;
    const keys = Object.keys(shape.fields);
    if (keys.length === 0) return true;
    for (const k of keys) {
        const f = shape.fields[k];
        if (!f || !f.shape) return false;
        if (f.shape.kind !== 'primitive') return false;
        if (f.shape.type === 'object') return false; // JsonElement sentinel
        if (!METADATA_NAME_RE.test(k)) return false;
    }
    return true;
}

/**
 * Classify a top-level field as `substantial` (the real payload candidate)
 * or `metadata` (boilerplate envelope sibling). Returns 'substantial',
 * 'metadata', or 'ambiguous'.
 *
 * Rules:
 *   - primitive scalar -> metadata
 *   - key matches METADATA_NAME_RE AND shape is object-of-primitives or
 *     array-of-primitives or array-of-metadata-objects -> metadata
 *   - object with >= 1 non-primitive field -> substantial
 *   - object that is purely metadata-typed -> metadata
 *   - array of objects -> substantial (unless key matches metadata regex)
 *   - array of primitives -> metadata only if key matches regex; else
 *     substantial (e.g. a `tags: string[]` payload)
 *   - unknown / JsonElement -> ambiguous (treated as substantial so the
 *     heuristic abstains)
 */
function classifyEnvelopeField(key, shape) {
    if (!shape) return 'ambiguous';
    const keyIsMeta = METADATA_NAME_RE.test(key);
    if (shape.kind === 'primitive') {
        return 'metadata';
    }
    if (shape.kind === 'object') {
        if (isMetadataObjectShape(shape)) return 'metadata';
        if (keyIsMeta) return 'metadata';
        return 'substantial';
    }
    if (shape.kind === 'array') {
        const el = shape.element;
        if (!el || el.kind === 'unknown') return keyIsMeta ? 'metadata' : 'ambiguous';
        if (el.kind === 'primitive') {
            return keyIsMeta ? 'metadata' : 'substantial';
        }
        if (el.kind === 'object') {
            if (keyIsMeta && isMetadataObjectShape(el)) return 'metadata';
            return 'substantial';
        }
        return 'ambiguous';
    }
    return 'ambiguous';
}

/**
 * Detect whether a response shape is a single-payload envelope worth
 * unwrapping. Conservative: returns `{ envelope: false }` whenever the
 * heuristic is uncertain. See docs/designs/2026-05-15-envelope-unwrap-plan.md.
 *
 * @param {object} shape - shape descriptor from inferShape/mergeShapes
 * @param {object} [opts]
 * @param {number} [opts.maxFields=5] - maximum top-level field count
 * @returns {{envelope: false} | {envelope: true, payloadField: string, payloadShape: object}}
 */
function detectEnvelope(shape, opts) {
    const maxFields = (opts && opts.maxFields) || 5;
    if (!shape || shape.kind !== 'object') return { envelope: false };
    const fieldKeys = Object.keys(shape.fields);
    if (fieldKeys.length < 1 || fieldKeys.length > maxFields) return { envelope: false };

    const substantial = [];
    let hasAmbiguous = false;
    for (const k of fieldKeys) {
        const f = shape.fields[k];
        // Conservatism: the payload field must appear in every sample.
        // Optional fields (missing from some merged samples) cannot anchor
        // an unwrap -- callers would get null at runtime for the missing
        // case. Cf. issue #64 requirement 4 ("stable name across samples").
        if (f && f.optional) continue;
        const verdict = classifyEnvelopeField(k, f && f.shape);
        if (verdict === 'substantial') substantial.push(k);
        else if (verdict === 'ambiguous') hasAmbiguous = true;
    }

    // Exactly one substantial field, no ambiguous siblings.
    if (substantial.length !== 1) return { envelope: false };
    if (hasAmbiguous) return { envelope: false };

    const payloadField = substantial[0];

    // Field-name conflict: pascalCase of payload field must not collide with
    // pascalCase of any sibling top-level key.
    const payloadPascal = pascalCase(payloadField);
    for (const k of fieldKeys) {
        if (k === payloadField) continue;
        if (pascalCase(k) === payloadPascal) return { envelope: false };
    }

    return {
        envelope: true,
        payloadField,
        payloadShape: shape.fields[payloadField].shape,
    };
}

// -------------------- Name helpers --------------------

function pascalCase(s) {
    if (!s) return '';
    return s.replace(/[^A-Za-z0-9]+(.)/g, (_, c) => c.toUpperCase())
            .replace(/^(.)/, (_, c) => c.toUpperCase())
            .replace(/[^A-Za-z0-9]/g, '');
}

function singularize(s) {
    if (/ies$/i.test(s)) return s.slice(0, -3) + 'y';
    if (/ses$/i.test(s) || /xes$/i.test(s) || /zes$/i.test(s)) return s.slice(0, -2);
    if (/s$/i.test(s) && !/ss$/i.test(s)) return s.slice(0, -1);
    return s;
}

// URL path prefixes that carry no semantic meaning for a method name.
const IGNORED_PATH_PREFIXES = new Set(['api', 'v1', 'v2', 'v3', 'rest', 'graphql']);

function meaningfulSegments(segments) {
    let i = 0;
    while (i < segments.length && segments[i].literal && IGNORED_PATH_PREFIXES.has(segments[i].literal.toLowerCase())) {
        i++;
    }
    // Always keep at least one segment so the method name is non-empty.
    if (i >= segments.length) return segments.slice(-1);
    return segments.slice(i);
}

function methodNameFor(pattern) {
    const verbMap = { GET: 'Get', POST: 'Create', PUT: 'Update', PATCH: 'Update', DELETE: 'Delete' };
    const verb = verbMap[pattern.method] || pascalCase(pattern.method.toLowerCase());
    const segs = meaningfulSegments(pattern.segments);
    const parts = [];
    for (const s of segs) {
        if (s.literal) parts.push(pascalCase(s.literal));
        else parts.push('By' + pascalCase(s.param));
    }
    return verb + parts.join('') + 'Async';
}

function modelNameFor(pattern) {
    const segs = meaningfulSegments(pattern.segments).filter((s) => s.literal).map((s) => s.literal);
    const hasParam = pattern.segments.some((s) => s.param);
    let base = segs.map(pascalCase).join('') || 'Root';
    if (hasParam && segs.length > 0) {
        // Singularize the last literal segment for "by id" endpoints
        const last = segs[segs.length - 1];
        base = segs.slice(0, -1).map(pascalCase).join('') + pascalCase(singularize(last));
    }
    return base + 'Response';
}

function describeFor(pattern) {
    const verbWord = { GET: 'Gets', POST: 'Creates', PUT: 'Updates', PATCH: 'Updates', DELETE: 'Deletes' }[pattern.method] || pattern.method;
    const segs = meaningfulSegments(pattern.segments);
    const literals = segs.filter((s) => s.literal).map((s) => s.literal.replace(/[-_]/g, ' '));
    const hasParam = segs.some((s) => s.param);
    const noun = literals.length > 0 ? literals[literals.length - 1] : 'resource';
    // Keep the plural form for the noun; only suffix " by id" when a path param is present.
    const subject = hasParam ? noun + ' by id' : noun;
    return verbWord + ' ' + subject + '.';
}

// -------------------- Response shape extraction --------------------

function parseResponseJson(entry) {
    const resp = entry.response || {};
    const text = resp.content && resp.content.text;
    if (!text) return null;
    try { return JSON.parse(text); } catch { return null; }
}

// -------------------- C# emission --------------------

function csTypeFor(shape, modelMap, nameHint) {
    if (!shape || shape.kind === 'unknown') return 'JsonElement';
    if (shape.kind === 'primitive') {
        const base = { string: 'string', int: 'int', long: 'long', double: 'double', bool: 'bool', object: 'JsonElement' }[shape.type] || 'string';
        if (shape.nullable && shape.type !== 'object') return base + '?';
        return base;
    }
    if (shape.kind === 'array') {
        const inner = csTypeFor(shape.element, modelMap, singularize(nameHint || 'Item'));
        return 'IReadOnlyList<' + inner + '>';
    }
    if (shape.kind === 'object') {
        // Lazily register an inline record.
        const name = registerModel(modelMap, nameHint || 'Anon', shape);
        return name;
    }
    return 'JsonElement';
}

function registerModel(modelMap, hint, shape) {
    const baseName = pascalCase(hint) || 'Anon';
    // If an identical shape was already registered with this base, reuse it.
    const sig = shapeSignature(shape);
    for (const [n, info] of modelMap) {
        if (info.baseName === baseName && info.sig === sig) return n;
    }
    let name = baseName;
    let n = 2;
    while (modelMap.has(name)) { name = baseName + n; n++; }
    modelMap.set(name, { baseName, sig, shape });
    return name;
}

function shapeSignature(shape) {
    if (!shape) return 'null';
    if (shape.kind === 'primitive') return 'P:' + shape.type + (shape.nullable ? '?' : '');
    if (shape.kind === 'array') return 'A:' + shapeSignature(shape.element);
    if (shape.kind === 'object') {
        const parts = Object.keys(shape.fields).sort().map((k) => {
            const f = shape.fields[k];
            return k + (f.optional ? '?' : '') + (f.nullable ? 'n' : '') + ':' + shapeSignature(f.shape);
        });
        return 'O:{' + parts.join(',') + '}';
    }
    return '?';
}

function emitModels(modelMap) {
    // csTypeFor may register additional models (array elements) while we emit.
    // Iterate to fixed point so every reachable model gets a body.
    const emitted = new Map(); // name -> body
    let progressed = true;
    while (progressed) {
        progressed = false;
        const names = Array.from(modelMap.keys()).sort();
        for (const name of names) {
            if (emitted.has(name)) continue;
            const info = modelMap.get(name);
            if (!info || info.shape.kind !== 'object') { emitted.set(name, ''); continue; }
            const buf = [];
            buf.push('/// <summary>Auto-generated model for ' + name + '.</summary>');
            buf.push('public sealed partial class ' + name);
            buf.push('{');
            const keys = Object.keys(info.shape.fields).sort();
            for (const k of keys) {
                const f = info.shape.fields[k];
                const propName = pascalCase(k);
                let type = csTypeFor(f.shape, modelMap, propName);
                if ((f.optional || f.nullable) && !type.endsWith('?')) type = type + '?';
                buf.push('    [JsonPropertyName("' + escapeCs(k) + '")]');
                const init = initializerFor(type);
                buf.push('    public ' + type + ' ' + propName + ' { get; init; }' + (init ? init + ';' : ''));
            }
            buf.push('}');
            buf.push('');
            emitted.set(name, buf.join('\n'));
            progressed = true;
        }
    }
    const ordered = Array.from(emitted.keys()).sort();
    return ordered.map((n) => emitted.get(n)).filter(Boolean).join('\n');
}

/**
 * Returns a C# property initializer suffix (e.g., " = default!"). Used to
 * silence CS8618 ("non-nullable property must contain a non-null value") on
 * generated DTOs that are populated by JsonSerializer reflection.
 *
 * Value types (int, bool, double, long) and nullable types are zero-initialized
 * already; only non-nullable reference types need the explicit default.
 */
function initializerFor(type) {
    if (!type) return '';
    if (type.endsWith('?')) return '';
    const valueTypes = new Set(['int', 'long', 'double', 'bool']);
    if (valueTypes.has(type)) return '';
    return ' = default!';
}

function escapeCs(s) { return s.replace(/\\/g, '\\\\').replace(/"/g, '\\"'); }

function emitClient(opts, patterns, sourceShas, hasGraphQL, modelMap) {
    const header = autoGenHeader(sourceShas);
    const ns = opts.namespace;
    const projectName = opts.projectName;
    const lines = [];
    lines.push(header);
    lines.push('using System.ComponentModel;');
    lines.push('using System.Net.Http;');
    lines.push('using System.Text.Json;');
    lines.push('using System.Text.Json.Serialization;');
    lines.push('');
    lines.push('namespace ' + ns + ';');
    lines.push('');
    lines.push('public sealed partial class ' + projectName + 'Client');
    lines.push('{');

    if (hasGraphQL) {
        lines.push('    /// <summary>Issues a GraphQL operation against the target endpoint and deserializes the response.</summary>');
        lines.push('    [Description("Executes a GraphQL query or mutation and returns the typed response.")]');
        lines.push('    public async Task<T> GraphQLAsync<T>(string query, object? variables = null, CancellationToken ct = default)');
        lines.push('    {');
        lines.push('        var payload = JsonSerializer.Serialize(new { query, variables });');
        lines.push('        var raw = await SendRawJsonAsync("/graphql", payload, ct).ConfigureAwait(false);');
        lines.push('        return Deserialize<T>(raw);');
        lines.push('    }');
        lines.push('');
    }

    for (const p of patterns) {
        const methodName = methodNameFor(p);
        const modelName = p.responseModel || 'JsonElement';
        const pathArgs = p.segments
            .filter((s) => s.param)
            .map((s) => ({ name: s.param, type: s.type }));
        const httpVerb = (p.method || 'GET').toUpperCase();
        const verbExpr = 'HttpMethod.' + (httpVerb.charAt(0) + httpVerb.slice(1).toLowerCase());
        const acceptsBody = httpVerb === 'POST' || httpVerb === 'PUT' || httpVerb === 'PATCH';
        // What the capture showed in this endpoint's query string (issue #34).
        // Order: path parameters, the body, the query parameters, ct -- so a
        // call written before the endpoint had any still passes its body where
        // the body goes.
        const queryParams = p.queryParameters || [];
        const sigArgs = pathArgs.map((a) => a.type + ' ' + a.name);
        if (acceptsBody) sigArgs.push('string? body = null');
        for (const q of queryParams) sigArgs.push(q.declaration);
        sigArgs.push('CancellationToken ct = default');
        const sig = sigArgs.join(', ');
        const pathExpr = pathExpression(p);
        const bodyArg = acceptsBody ? 'body' : 'null';
        lines.push('    /// <summary>' + describeFor(p) + '</summary>');
        for (const l of queryParamDocLines(p, pathArgs, acceptsBody)) lines.push(l);
        lines.push('    [Description("' + escapeCs(describeFor(p)) + '")]');
        lines.push('    public async Task<' + modelName + '> ' + methodName + '(' + sig + ')');
        lines.push('    {');
        for (const l of clientQueryLines(queryParams)) lines.push(l);
        const queryArg = queryParams.length > 0 ? QUERY_LOCAL : 'null';
        lines.push('        var raw = await SendRawAsync(' + pathExpr + ', ' + verbExpr + ', jsonBody: ' + bodyArg + ', query: ' + queryArg + ', ct: ct).ConfigureAwait(false);');
        if (p.envelope && p.wrapperModel) {
            // Deserialize the wrapper record (kept available in Models.Generated.cs)
            // then return the inner payload property. Conservatism: heuristic
            // only fires for shapes with a single substantial field, so the
            // PascalCase property is guaranteed to exist on the wrapper.
            const propName = pascalCase(p.envelope.payloadField);
            lines.push('        var envelope = Deserialize<' + p.wrapperModel + '>(raw);');
            lines.push('        return envelope.' + propName + '!;');
        } else {
            lines.push('        return Deserialize<' + modelName + '>(raw);');
        }
        lines.push('    }');
        lines.push('');
    }

    // The helpers the query lines above call: once, and only in a client that
    // has a query to send.
    if (patterns.some((p) => (p.queryParameters || []).length > 0)) {
        for (const l of clientQueryHelperLines()) lines.push(l);
        lines.push('');
    }

    lines.push('    private async Task<string> SendRawJsonAsync(string path, string json, CancellationToken ct)');
    lines.push('    {');
    lines.push('        // POST helper used by GraphQLAsync; delegates to the hand-written Client.cs partial.');
    lines.push('        return await SendRawAsync(path, HttpMethod.Post, jsonBody: json, query: null, ct: ct).ConfigureAwait(false);');
    lines.push('    }');
    lines.push('}');
    lines.push('');
    return lines.join('\n');
}

/**
 * The `<param>` documentation of a client method that takes query parameters:
 * what each one is on the wire, what the capture showed, and what is sent when
 * it is omitted. Every parameter of the method is documented, or none is -- a
 * method documented in part is a compiler warning (CS1573) for a consumer who
 * turns the XML documentation file on. A method with no query parameters gets
 * none, as before.
 */
function queryParamDocLines(p, pathArgs, acceptsBody) {
    const queryParams = p.queryParameters || [];
    if (queryParams.length === 0) return [];
    const doc = (name, text) => '    /// <param name="' + name + '">' + xmlEscape(text) + '</param>';
    const lines = pathArgs.map((a) => doc(a.name, 'The ' + a.name + ' in ' + patternPath(p) + '.'));
    if (acceptsBody) lines.push(doc('body', 'The JSON request body, as text. Omit it to send no body.'));
    for (const q of queryParams) lines.push(doc(q.toolName, q.description));
    lines.push(doc('ct', 'Cancels the request.'));
    return lines;
}

function pathExpression(p) {
    // Build a C# interpolated string for the URL path.
    const segs = p.segments.map((s) => s.literal ? s.literal : '{' + s.param + '}').join('/');
    if (p.segments.every((s) => s.literal)) {
        return '"/' + segs + '"';
    }
    return '$"/' + segs + '"';
}

function autoGenHeader(sourceShas) {
    const lines = [];
    lines.push('// <auto-generated>');
    lines.push('//   This file was generated by web-api-discovery (generate-wrapper.js).');
    lines.push('//   Do not edit -- changes will be lost on regeneration.');
    for (const s of sourceShas) {
        lines.push('//   source HAR sha-256: ' + s);
    }
    lines.push('// </auto-generated>');
    lines.push('#nullable enable');
    return lines.join('\n');
}

function emitModelsFile(modelMap, sourceShas, opts) {
    const lines = [];
    lines.push(autoGenHeader(sourceShas));
    lines.push('using System.Text.Json;');
    lines.push('using System.Text.Json.Serialization;');
    lines.push('');
    lines.push('namespace ' + opts.namespace + ';');
    lines.push('');
    lines.push(emitModels(modelMap));
    return lines.join('\n');
}

// -------------------- Project metadata files --------------------

function emitCsproj(opts) {
    const tags = opts.packageTags || '';
    const repo = opts.repositoryUrl || '';
    return [
        '<Project Sdk="Microsoft.NET.Sdk">',
        '  <PropertyGroup>',
        '    <TargetFramework>net10.0</TargetFramework>',
        '    <OutputType>Exe</OutputType>',
        '    <Nullable>enable</Nullable>',
        '    <ImplicitUsings>enable</ImplicitUsings>',
        '    <PackageId>' + xmlEscape(opts.projectName) + '</PackageId>',
        '    <Description>' + xmlEscape(opts.description || '') + '</Description>',
        '    <Authors>' + xmlEscape(opts.authors || '') + '</Authors>',
        '    <RepositoryUrl>' + xmlEscape(repo) + '</RepositoryUrl>',
        '    <PackageTags>' + xmlEscape(tags) + '</PackageTags>',
        '  </PropertyGroup>',
        // ModelContextProtocol is the package a stdio server needs: it carries
        // AddMcpServer / WithStdioServerTransport / WithToolsFromAssembly. Its
        // version is the host's (src/Multiplex.Web uses the same line through
        // ModelContextProtocol.AspNetCore), and tests/codegen-pipeline.Tests.ps1
        // fails when the two drift. That SDK needs the Microsoft.Extensions
        // abstractions at 10.0.14 or later, so the two pins below move with it:
        // an older one fails restore with NU1605 (package downgrade).
        '  <ItemGroup>',
        '    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.14" />',
        '    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.14" />',
        '    <PackageReference Include="Microsoft.Playwright" Version="1.49.0" />',
        '    <PackageReference Include="ModelContextProtocol" Version="2.4.0" />',
        '    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="8.0.0" />',
        '  </ItemGroup>',
        '</Project>',
        ''
    ].join('\n');
}

function xmlEscape(s) {
    return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function emitReadme(opts, patterns, hasGraphQL) {
    const lines = [];
    lines.push('# ' + opts.projectName);
    lines.push('');
    lines.push(opts.description || ('Wrapper for ' + opts.baseUrl));
    lines.push('');
    lines.push('> Auto-generated by web-api-discovery. Re-run the generator to refresh; hand-written code goes in `Client.cs` (a `partial class` paired with `' + opts.projectName + 'Client.Generated.cs`).');
    lines.push('');
    lines.push('## Polite-crawl policy');
    lines.push('');
    lines.push('When re-capturing HAR fixtures, the recommended throttle is **1 request/second** with a descriptive `User-Agent`. Respect `robots.txt`. The bundled `capture-cdp.js` defaults to a ~1000 ms inter-request delay.');
    lines.push('');
    lines.push(pacingReadmeSection(opts));
    lines.push('');
    lines.push('## Recipes');
    lines.push('');
    const recipes = buildRecipes(opts, patterns, hasGraphQL);
    for (const r of recipes) {
        lines.push('### ' + r.title);
        lines.push('');
        lines.push('```csharp');
        lines.push(r.code);
        lines.push('```');
        lines.push('');
    }
    lines.push(mcpServerReadmeSection(opts, patterns, hasGraphQL));
    lines.push('');
    lines.push(secretGateReadmeSection());
    lines.push('');
    lines.push(mobileImportReadmeSection());
    if (Array.isArray(opts.antiBotCookies) && opts.antiBotCookies.length > 0) {
        lines.push('');
        lines.push(antiBotWarningSection(opts.antiBotCookies, opts.projectName));
    }
    return lines.join('\n');
}

/**
 * The "Pacing and throttling" README section: what the generated client's
 * PacingHandler (csharp/PacingHandler.cs.tmpl) does, what the client says it
 * is (PacingOptions.UserAgent), and how to tune both. The
 * environment-variable prefix is computed the way PacingOptions computes it --
 * the project name, upper-cased -- so the README names variables that exist.
 */
function pacingReadmeSection(opts) {
    const prefix = String(opts.projectName).toUpperCase() + '_PACING_';
    return [
        '### Pacing and throttling in this client',
        '',
        'Every request goes through `PacingHandler`: **one request in flight**, at least **1 s** between',
        'request starts plus up to 250 ms of jitter, and a site\'s throttling honoured -- a `429` or `503`',
        'waits out `Retry-After` (seconds or an HTTP-date; up to 60 s), or backs off exponentially with',
        'jitter when the site names no wait. Only idempotent methods are retried, and by default only',
        '`GET` and `HEAD`; `POST` and `PATCH` are never retried. When retries run out, or the site asks for',
        'a longer wait than 60 s, the call fails with `' + opts.projectName + 'ThrottledException` carrying what the site asked for.',
        'A block -- an HTML `403` from the site\'s CDN, a `403` carrying `x-datadome`, or a bot challenge,',
        'Cloudflare\'s "Just a moment..." served as a `503` included -- is never retried and nothing',
        'works around it: it fails with `' + opts.projectName + 'BlockedException`.',
        '',
        'The client also says what it is. Its `User-Agent` is `' + opts.projectName + '/<version> (generated by web-api-discovery; +https://github.com/IntelliTect-Samples/web-api-discovery)`;',
        'set `PacingOptions.UserAgent` (or the variable below) to name your deployment and how the site can',
        'reach you. It sends only what the API needs -- the session cookie, the CSRF token, `Accept` /',
        '`Content-Type`, and the `Origin` / `X-Requested-With` a site\'s CSRF checks look for -- and never a',
        'browser\'s `User-Agent` or the client-hint / fetch-metadata headers (`sec-ch-ua*`, `sec-fetch-*`) a browser adds.',
        '',
        'Tune it with environment variables (read by `PacingOptions.FromEnvironment()`, which the default',
        'constructor and the MCP server use), or pass a `PacingOptions` to the client\'s constructor:',
        '',
        '| Variable | Default |',
        '|---|---|',
        '| `' + prefix + 'MIN_SPACING_MS` | `1000` |',
        '| `' + prefix + 'JITTER_MS` | `250` |',
        '| `' + prefix + 'MAX_CONCURRENCY` | `1` |',
        '| `' + prefix + 'MAX_ATTEMPTS` | `3` (the first included) |',
        '| `' + prefix + 'MAX_RETRY_AFTER_SECONDS` | `60` |',
        '| `' + prefix + 'RETRY_METHODS` | `GET,HEAD` (idempotent methods only) |',
        '| `' + prefix + 'USER_AGENT` | `' + opts.projectName + '/<version> (generated by web-api-discovery; ...)` |',
    ].join('\n');
}

/**
 * The "MCP server" README section (issue #17): how to start the server, the
 * tools it lists with the hints each declares, and what happens when nobody
 * has signed in. The table is the same tool list the server registers
 * (mcp-tools-emit.js), set out so the hints can be reviewed in one place.
 */
function mcpServerReadmeSection(opts, patterns, hasGraphQL) {
    const name = opts.projectName;
    const yesNo = (b) => (b ? 'yes' : 'no');
    const lines = [
        '## MCP server',
        '',
        '`src/' + name + '` is also an MCP server over stdio. Build it, then give an MCP client the built',
        'assembly to start:',
        '',
        '```pwsh',
        'dotnet build src/' + name + ' -c Release',
        'dotnet src/' + name + '/bin/Release/net10.0/' + name + '.dll',
        '```',
        '',
        'Start the built assembly, not `dotnet run`: over stdio the server\'s standard output is the',
        'protocol stream, and `dotnet run` can write build messages into it. The server logs to standard',
        'error.',
        '',
        'It lists one tool for each captured endpoint (`' + name + 'Tools.Generated.cs`), less the',
        'endpoints left out below:',
        '',
        '| Tool | Request | Read-only | Destructive | Idempotent |',
        '|---|---|---|---|---|',
    ];
    const tools = listTools({ patterns, hasGraphQL, methodNameFor });
    for (const tool of tools) {
        const request = tool.pattern
            ? tool.pattern.method.toUpperCase() + ' ' + patternPath(tool.pattern)
            : 'POST /graphql (query or mutation)';
        lines.push('| `' + tool.name + '` | `' + request + '` | ' + yesNo(tool.hints.readOnly) +
            ' | ' + yesNo(tool.hints.destructive) + ' | ' + yesNo(tool.hints.idempotent) + ' |');
    }
    lines.push('');
    lines.push('The hints are what an MCP client reads to decide whether a call needs confirming. They are a');
    lines.push('first draft from each endpoint\'s HTTP method and path, and nothing here knows what the site');
    lines.push('really does with a call:');
    lines.push('');
    lines.push('- a `GET` is read-only;');
    lines.push('- `PUT` and `DELETE` are destructive and idempotent; `PATCH` is destructive;');
    lines.push('- a `POST` is destructive unless something vouches for it. It is called additive only when its');
    lines.push('  path has no id in it; names no change, action, money or commitment, opaque call or sign-in');
    lines.push('  (`delete`, `send`, `pay`, `order`, `rpc`, `login` and the like, or a word built on one of');
    lines.push('  the first three kinds: `deletion`, `withdrawal`, `payment`); and either says `create`,');
    lines.push('  `add` or `new` or is a collection: the capture also holds that same path with an id appended.');
    lines.push('');
    lines.push('So a `POST` the capture says little about asks for confirmation. Review the table before');
    lines.push('relying on a client that acts on hints without asking.');
    lines.push('');
    lines.push('The server starts without a session and never prompts for one. A tool called before anyone has');
    lines.push('signed in answers with an error that says so. Sign in with `' + name + 'Authenticator`');
    lines.push('(`BrowserLoginAsync`), save the session it returns, and call the tool again.');
    lines.push('');
    lines.push('When the server reads the saved session, exactly:');
    lines.push('');
    lines.push('- while it has none, at every tool call -- so a first sign-in needs no restart;');
    lines.push('- once it has one, not again while the site accepts it;');
    lines.push('- after the site refuses it -- a redirect (any 3xx), `401`, `403`, `419` or `440` -- at the');
    lines.push('  next tool call. So signing in again after a session expires needs no restart either.');
    lines.push('');
    lines.push('What does need a restart is changing to another account while the current session still works.');
    lines.push('');
    lines.push('`tests/' + name + '.Tests/pester/Mcp.Tests.ps1` starts the server and checks that it lists');
    lines.push('these tools, with these hints.');
    lines.push('');
    for (const line of leftOutReadmeLines(opts, patterns)) lines.push(line);
    // What each tool takes in its query string, what it sends when a parameter
    // is omitted, and what is never baked in (issue #34).
    const querySection = queryReadmeSection(tools, opts);
    if (querySection) {
        lines.push('');
        lines.push(querySection);
    }
    return lines.join('\n');
}

/**
 * The "Left out of the tools" part of the MCP server section: which endpoints
 * the client has a method for and the server offers no tool for, why, and how
 * to offer one deliberately. There is no switch for it on purpose -- a sign-in
 * call becomes model-callable when a person writes the tool, not by default.
 */
function leftOutReadmeLines(opts, patterns) {
    const name = opts.projectName;
    const leftOut = listLeftOut({ patterns, methodNameFor });
    const lines = [
        '### Left out of the tools',
        '',
        '`' + name + 'Client` has a method for every captured endpoint. The server offers a model no tool for:',
        '',
        '- an endpoint whose path has a segment about **signing in or credentials** -- `auth`, `login`,',
        '  `logout`, `signin`, `signup`, `register`, `token`, `session`, `oauth`, `saml`, `sso`, `csrf`,',
        '  `password`, `mfa`, `otp` and their other names, or a credential store such as `api-keys`,',
        '  `secrets` or `cookies` -- alone, in a compound such as `refresh-token`, or with a file',
        '  extension (`session.json`, `login.php`). Such a call returns tokens or takes a password,',
        '  and a model should be handed neither;',
        '- an **`OPTIONS` or `HEAD`** request, which asks about an endpoint and returns nothing to use',
        '  (an `OPTIONS` in a capture is a browser\'s preflight);',
        '- an endpoint the capture showed **no JSON response** from, because the client method has',
        '  nothing typed to return.',
        '',
    ];
    if (leftOut.length === 0) {
        lines.push('Nothing in this capture was left out.');
    } else {
        lines.push('In this capture that is:');
        lines.push('');
        lines.push('| Client method | Request | Why it has no tool |');
        lines.push('|---|---|---|');
        for (const o of leftOut) {
            lines.push('| `' + o.methodName + '` | `' + o.pattern.method.toUpperCase() + ' ' + patternPath(o.pattern) +
                '` | ' + o.reason + ' |');
        }
    }
    lines.push('');
    lines.push('To offer one of them anyway, write the tool yourself; no option turns them on. `' + name + 'Tools`');
    lines.push('is a partial class and the generator only ever writes `' + name + 'Tools.Generated.cs`, so a file');
    lines.push('of your own beside it is kept when the wrapper is generated again:');
    lines.push('');
    lines.push('```csharp');
    lines.push('// src/' + name + '/' + name + 'Tools.Mine.cs -- yours; the generator does not write this file.');
    lines.push('using System.ComponentModel;');
    lines.push('using System.Text.Json;');
    lines.push('using ModelContextProtocol.Server;');
    lines.push('');
    lines.push('namespace ' + opts.namespace + ';');
    lines.push('');
    lines.push('public sealed partial class ' + name + 'Tools');
    lines.push('{');
    lines.push('    [McpServerTool(Name = "my_tool", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]');
    lines.push('    [Description("What it does, in words a model can choose it by.")]');
    lines.push('    public Task<JsonElement> MyToolAsync(CancellationToken ct = default)');
    lines.push('        => _session.RunAsync(async client =>');
    lines.push('            JsonDocument.Parse(await client.SendRawAsync("/the/path", HttpMethod.Post, ct: ct)).RootElement);');
    lines.push('}');
    lines.push('```');
    lines.push('');
    lines.push('Inside `RunAsync`, call the client method named in the table, or `SendRawAsync` as here. Set the');
    lines.push('hints to what the call really does. The smoke test goes on checking the generated tools; a tool');
    lines.push('of your own is listed beside them and does not fail it. Give it a name of its own, though: the');
    lines.push('smoke test fails if a tool is listed under the name the generator would have given an endpoint');
    lines.push('it left out (`create_login`, say), which is how it catches a sign-in tool coming back by mistake.');
    return lines;
}

/**
 * Build the "Adding mobile-app coverage" README section (issue #90.a).
 *
 * A scaffolded project starts from web traffic only. The skill's Phase 1.5
 * lets the user opt into mobile-app HAR / endpoint-list capture, but
 * scaffolds driven by `run-agent.js` skip that prompt entirely. This
 * section is rendered into every generated README so consumers know how
 * to add mobile coverage retroactively without having to re-read SKILL.md.
 *
 * The section is intentionally generic (no project-specific paths): the
 * commands work identically against any wrapper that this generator emits.
 */
function mobileImportReadmeSection() {
    return [
        '## Adding mobile-app coverage',
        '',
        'A scaffold built from web HAR alone misses any endpoints that only',
        'appear in the target service\'s iOS or Android app. Mobile traffic',
        'frequently exposes richer data, internal APIs not visible on the web,',
        'and different auth shapes.',
        '',
        'To add mobile coverage to this project, run the importer from your',
        'web-api-discovery clone (https://github.com/IntelliTect-Samples/web-api-discovery)',
        'and feed the captured traffic back through this project\'s pipeline:',
        '',
        '```pwsh',
        '# 1. Print the platform-specific capture instructions and create the',
        '#    target paths under Samples/HAR-Original/ (proxy mode) or',
        '#    Samples/MobileApp-Discovered/ (decompile mode).',
        'node <web-api-discovery>/skills/web-api-discovery/scripts/capture/import-mobile-app.js `',
        '  --platform=<ios|android|both> `',
        '  --mode=<proxy|decompile|both>',
        '',
        '# 2. Follow the on-screen instructions to:',
        '#    - PROXY mode: install a TLS-intercepting proxy (mitmproxy, Charles,',
        '#      Proxyman, Fiddler), trust its CA on the device, exercise the app,',
        '#      then export the captured session as HAR.',
        '#    - DECOMPILE mode: use a static-analysis tool (jadx for Android,',
        '#      class-dump for iOS) to dump candidate endpoint URLs to a sorted',
        '#      unique list at Samples/MobileApp-Discovered/<platform>-endpoints.txt.',
        '',
        '# 3. Re-run the wrapper pipeline against the new mobile HAR. Each',
        '#    captured mobile HAR is processed exactly like a web HAR.',
        'node <web-api-discovery>/skills/web-api-discovery/scripts/codegen/run-agent.js `',
        '  --har Samples/HAR-Original/mobile-android-<timestamp>.har `',
        '  --out . --project <Name> --namespace <Namespace>',
        '```',
        '',
        '### Legal note on decompilation',
        '',
        'Only decompile apps you are **legally permitted to inspect** -- apps',
        'tied to your own account, or apps whose Terms of Service explicitly',
        'allow security research. The importer surfaces this warning before it',
        'prints decompile-mode instructions and will not proceed without your',
        'explicit acknowledgement.',
        '',
        '### What gets merged',
        '',
        'Re-running the pipeline merges the new mobile endpoints into the same',
        '`(method, path-template)` catalog used for the web HARs. Generated',
        'files (`*.g.cs`, fixtures) are rewritten in place; any hand-written',
        'partials and user-edited `Client.cs` content are preserved.',
    ].join('\n');
}

/**
 * Build the "Anti-bot challenge warning" README section (issue #66).
 *
 * Rendered only when the captured HAR contained one or more Akamai
 * bot-management cookies (`_abck`, `bm_sz`, `bm_sv`, `ak_bmsc`). It says what
 * is true and stops there: the site runs bot management, so it may refuse this
 * client; a refusal fails with `<Name>BlockedException`; and the remedy is a
 * person -- a fresh session from a real, signed-in browser. It never advises a
 * way past the refusal (SKILL.md, "Pacing, throttling, and where the line is").
 */
function antiBotWarningSection(cookies, projectName) {
    const list = cookies.map((c) => '`' + c + '`').join(', ');
    return [
        '## Anti-bot challenge warning',
        '',
        'The captured HAR contains Akamai bot-management cookies (' + list + '), so this site ' +
        'almost certainly runs Akamai Bot Manager. Bot management decides whether a caller is a ' +
        'person in a browser. This client is not one and does not pretend to be, so **the site may ' +
        'refuse it**.',
        '',
        'When it does, the call fails with `' + projectName + 'BlockedException` (an HTML `403` from ' +
        "the site's edge, or a challenge). The client does not send that call again and does not try " +
        'to get past the refusal. That is deliberate.',
        '',
        '### When a call is refused',
        '',
        '- **Get a fresh session from a real, signed-in browser.** Run `' + projectName + 'Authenticator` ' +
        '(`BrowserLoginAsync`), sign in yourself in the browser window it opens, and build the client ' +
        'with the session it returns.',
        '- **Or have a person do the task** on the site itself.',
        '',
        'Do not add browser headers or a browser `User-Agent`, make extra requests to collect the bot ' +
        "manager's cookies, or send a refused call again until it goes through. Each of those works " +
        "around the site's decision, and this project does not do that.",
        '',
        'Detected cookies: ' + list + '.',
    ].join('\n');
}

function buildRecipes(opts, patterns, hasGraphQL) {
    const out = [];
    const client = opts.projectName + 'Client';
    // Recipe 0: construction
    out.push({
        title: 'Construct the client',
        code: [
            'var session = await new ' + opts.projectName + 'Authenticator(store).AuthenticateAsync();',
            'using var client = new ' + client + '(session);',
        ].join('\n'),
    });
    if (hasGraphQL) {
        out.push({
            title: 'Run a GraphQL query',
            code: [
                'var result = await client.GraphQLAsync<MyResponse>(',
                '    "query { me { id name } }");',
            ].join('\n'),
        });
    }
    for (const p of patterns.slice(0, 4)) {
        const m = methodNameFor(p);
        // A search term, a page, a filter: whatever the capture showed varying
        // is what a caller will want to pass first (issue #34).
        const paramArgs = p.segments.filter((s) => s.param).map((s) => s.type === 'int' ? '1' : '"abc"')
            .concat(recipeQueryArguments(p)).join(', ');
        out.push({
            title: p.method + ' ' + patternPath(p),
            code: 'var result = await client.' + m + '(' + paramArgs + ');',
        });
    }
    return out;
}

function emitTestFixtures(outDir, patterns, hasGraphQL, graphqlEntries) {
    const fixDir = path.join(outDir, 'tests', 'fixtures');
    fs.mkdirSync(fixDir, { recursive: true });
    for (const p of patterns) {
        const sample = p.samples[0];
        const json = parseResponseJson(sample.entry);
        if (json === null) continue;
        const fname = (p.method + '_' + patternPath(p).replace(/[^A-Za-z0-9]+/g, '_').replace(/^_|_$/g, '')) + '.json';
        const content = JSON.stringify(json, null, 2) + '\n';
        fs.writeFileSync(path.join(fixDir, fname), content);
    }
    if (hasGraphQL && graphqlEntries.length > 0) {
        const json = parseResponseJson(graphqlEntries[0].entry);
        if (json !== null) {
            fs.writeFileSync(path.join(fixDir, 'graphql.json'), JSON.stringify(json, null, 2) + '\n');
        }
    }
}

// -------------------- Template substitution --------------------

function substituteTemplates(opts, outDir) {
    const templatesDir = path.resolve(__dirname, '..', '..', 'csharp');
    if (!fs.existsSync(templatesDir)) return;
    const manifestPath = path.join(templatesDir, 'manifest.json');
    const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
    const tokens = buildTokenMap(opts);
    const projDir = path.join(outDir, 'src', opts.projectName);
    fs.mkdirSync(projDir, { recursive: true });

    // Only substitute templates whose required tokens are all available.
    for (const t of manifest.templates) {
        const required = t.requiredTokens || [];
        if (required.some((tok) => tokens[tok] === undefined)) continue;
        const src = path.join(templatesDir, t.file);
        if (!fs.existsSync(src)) continue;
        const body = fs.readFileSync(src, 'utf8');
        const subbed = body.replace(/\{\{(\w+)\}\}/g, (m, k) => tokens[k] !== undefined ? tokens[k] : m);
        // Strip ".tmpl" suffix; ".gitignore.tmpl" -> ".gitignore"
        const outName = t.file.replace(/\.tmpl$/, '');
        // Friendly remappings:
        //   Client.cs -> src/<Project>/Client.cs (hand-written partial)
        //   manifest.json -> not emitted
        if (outName === 'manifest.json') continue;
        fs.writeFileSync(path.join(projDir, outName), subbed);
    }
}

function buildTokenMap(opts) {
    return {
        ProjectName: opts.projectName,
        Namespace: opts.namespace,
        BaseUrl: opts.baseUrl,
        AuthModel: opts.authModel || 'cookie',
        IdpName: opts.idpName || 'Google',
        IdpAuthorizeUrl: opts.idpAuthorizeUrl || 'https://accounts.google.com/o/oauth2/v2/auth',
        IdpTokenUrl: opts.idpTokenUrl || 'https://oauth2.googleapis.com/token',
        IdpClientId: opts.idpClientId || 'REPLACE_WITH_CLIENT_ID',
        IdpScopes: opts.idpScopes || 'openid email profile',
        HasMobileCoverage: opts.hasMobileCoverage || 'false',
        MobileHarPaths: opts.mobileHarPaths || '',
    };
}

// -------------------- Pipeline orchestration --------------------

function run(args) {
    if (!args.har) fail('missing --har <path[,path,...]>');
    if (!args.out) fail('missing --out <dir>');
    if (!args['project-name']) fail('missing --project-name <Name>');
    if (!args.namespace) fail('missing --namespace <Ns>');
    if (!args['base-url']) fail('missing --base-url <url>');

    const opts = {
        projectName: args['project-name'],
        namespace: args.namespace,
        baseUrl: args['base-url'],
        authModel: args['auth-model'] || 'cookie',
        authors: args.authors || '',
        description: args.description || '',
        repositoryUrl: args['repository-url'] || '',
        packageTags: args['package-tags'] || '',
    };

    const harPaths = String(args.har).split(',').map((s) => s.trim()).filter(Boolean);
    const bundles = harPaths.map(loadHar);
    const sourceShas = bundles.map((b) => b.sha).sort();

    // Detect Akamai bot-management cookies across all input HARs (issue #66).
    // Union the per-HAR detections so a multi-HAR run still surfaces every
    // cookie the consumer should be warned about.
    const antiBotSet = new Set();
    for (const b of bundles) {
        for (const name of detectAntiBotCookies(b.har)) antiBotSet.add(name);
    }
    if (antiBotSet.size > 0) {
        // Preserve canonical detector ordering for deterministic README output.
        const canonical = ['_abck', 'bm_sz', 'bm_sv', 'ak_bmsc'];
        opts.antiBotCookies = canonical.filter((n) => antiBotSet.has(n));
    }

    // Partition entries into GraphQL vs REST.
    const gql = [], rest = [];
    for (const item of iterEntries(bundles)) {
        if (isGraphQL(item.entry)) gql.push(item);
        else rest.push(item);
    }

    // Dedup REST patterns.
    const patterns = dedupePatterns(rest);

    // Infer response shapes per pattern.
    const modelMap = new Map();
    for (const p of patterns) {
        let merged = null;
        for (const s of p.samples) {
            const json = parseResponseJson(s.entry);
            if (json === null || json === undefined) continue;
            const inferred = inferShape(json);
            merged = merged ? mergeShapes(merged, inferred) : inferred;
        }
        // Whether any captured response of this endpoint was JSON. One that
        // never was gets a client method and no MCP tool (mcp-tools-emit.js).
        p.hasJsonResponse = merged !== null;
        if (merged && merged.kind === 'object') {
            // Always register the wrapper model so consumers retain access to
            // the raw envelope via a strongly-typed record (issue #64 req. 3).
            const wrapperModel = registerModel(modelMap, modelNameFor(p), merged);
            p.wrapperModel = wrapperModel;

            const env = detectEnvelope(merged);
            if (env.envelope) {
                // Hint must match what emitModels uses for the wrapper's
                // property of the same name, so the inner model is registered
                // once (not duplicated under a different baseName). emitModels
                // calls csTypeFor with hint = pascalCase(fieldKey); for arrays
                // csTypeFor singularizes that for the element type. Mirroring
                // that here guarantees a single shared model.
                const hint = pascalCase(env.payloadField);
                p.responseModel = csTypeFor(env.payloadShape, modelMap, hint);
                p.envelope = { payloadField: env.payloadField };
                p.returnShape = env.payloadShape;
            } else {
                p.responseModel = wrapperModel;
                p.returnShape = merged;
            }
        } else if (merged && merged.kind === 'array') {
            p.returnShape = merged;
            // Array response: emit a list type directly; introduce a wrapper model.
            const itemModel = registerModel(modelMap, singularize(modelNameFor(p).replace(/Response$/, '')) + 'Item', merged.element.kind === 'object' ? merged.element : { kind: 'object', fields: {} });
            p.responseModel = 'IReadOnlyList<' + itemModel + '>';
        } else {
            p.responseModel = 'JsonElement';
        }
    }

    // What each endpoint's captured calls carried in their query strings, and
    // so what a generated call sends by default (issue #34). The scrub's
    // substitution tables are looked for beside each capture -- which is where
    // run-agent.js puts them -- so a replaced value that carries no marker is
    // still never baked in. Only paths are reported; a table's contents never are.
    const scrub = loadScrubTables(harPaths);
    for (const source of scrub.sources) {
        console.log('generate-wrapper: read the scrub\'s substitution table ' + source +
            ' (a query value it replaced is not baked in)');
    }
    for (const problem of scrub.problems) {
        console.error('generate-wrapper: NOTE -- ' + problem +
            '; scrubbed query values are recognised by their markers only.');
    }
    for (const p of patterns) {
        p.queryParameters = describeQueryParameters(collectQueryParameters(p.samples, {
            reserved: p.segments.filter((s) => s.param).map((s) => s.param),
            scrub,
        }));
    }

    // Out dirs.
    const outDir = path.resolve(args.out);
    const projDir = path.join(outDir, 'src', opts.projectName);
    fs.mkdirSync(projDir, { recursive: true });

    // Substitute templates first (writes Client.cs, ISessionStore.cs, etc).
    substituteTemplates(opts, outDir);

    // Emit generated C# files.
    const clientCs = emitClient(opts, patterns, sourceShas, gql.length > 0, modelMap);
    const modelsCs = emitModelsFile(modelMap, sourceShas, opts);
    fs.writeFileSync(path.join(projDir, opts.projectName + 'Client.Generated.cs'), clientCs);
    fs.writeFileSync(path.join(projDir, 'Models.Generated.cs'), modelsCs);

    // The MCP tool class. McpProgram.cs registers tools with
    // WithToolsFromAssembly(), which finds nothing unless a class is marked
    // [McpServerToolType] -- and this file is the only one that is (issue #17).
    const tools = listTools({ patterns, hasGraphQL: gql.length > 0, methodNameFor });
    fs.writeFileSync(path.join(projDir, opts.projectName + 'Tools.Generated.cs'), emitToolsFile({
        header: autoGenHeader(sourceShas),
        opts,
        tools,
        describeFor,
        patternPath,
    }));

    // .csproj
    fs.writeFileSync(path.join(projDir, opts.projectName + '.csproj'), emitCsproj(opts));

    // README + recipes + polite-crawl note
    fs.writeFileSync(path.join(outDir, 'README.md'), emitReadme(opts, patterns, gql.length > 0));

    // Test fixtures
    emitTestFixtures(outDir, patterns, gql.length > 0, gql);

    // xUnit + Pester test-project scaffold (one [Fact] per detected endpoint).
    emitTestScaffold({
        outDir,
        opts,
        patterns,
        hasGraphQL: gql.length > 0,
        methodNameFor,
        patternPath,
    });

    // Secret-gate (pre-commit hook + .gitleaks.toml + secret-scan/ci workflows).
    emitSecretGate({ outDir, opts });

    // Top-level .slnx referencing every emitted csproj so `dotnet build` from
    // the wrapper root "just works" without -p arguments. Runs LAST so all
    // csprojs (client + tests) are already on disk for discovery.
    emitSolution({ outDir, projectName: opts.projectName });

    // Ensure consumer repo-root .gitignore carries the entries this skill
    // owns (Samples/HAR-Original/ and Samples/MobileApp-Binaries/). Idempotent
    // on re-run. See issue #119 -- these moved out of upstream IntelliSDLC.ai's
    // .gitignore so only repos that actually scaffold a wrapper carry them.
    const addedIgnores = ensureRepoRootGitignoreHasScaffoldEntries(outDir);
    if (addedIgnores.length > 0) {
        console.log('generate-wrapper: added to .gitignore: ' + addedIgnores.join(', '));
    }

    console.log('generate-wrapper: wrote ' + patterns.length + ' REST pattern(s)' +
        (gql.length ? ' + GraphQL' : '') + ' to ' + outDir);
    // An endpoint with a client method and no MCP tool is said here as well as
    // in the README: whoever finds a tool missing looks at this output first.
    const leftOut = listLeftOut({ patterns, methodNameFor });
    if (leftOut.length > 0) {
        console.log('generate-wrapper: ' + leftOut.length + ' endpoint(s) left out of the MCP tools ' +
            '(sign-in or credentials, OPTIONS/HEAD, or no JSON response); see "Left out of the tools" in ' +
            path.join(outDir, 'README.md'));
    }
    console.log('generate-wrapper: next step -- activate the pre-commit hook with:');
    console.log('                  git -C ' + outDir + ' config core.hooksPath .githooks');
}

if (require.main === module) {
    try { run(parseArgs(process.argv.slice(2))); }
    catch (e) { fail(e && e.stack ? e.stack : String(e)); }
}

module.exports = {
    parseArgs,
    isGraphQL,
    inferShape,
    mergeShapes,
    registerModel,
    emitModels,
    detectEnvelope,
    classifyEnvelopeField,
    dedupePatterns,
    methodNameFor,
    pascalCase,
    singularize,
    isOpaqueIdLike,
    isNamedSegment,
    emitReadme,
    antiBotWarningSection,
    ensureRepoRootGitignoreHasScaffoldEntries,
    SCAFFOLD_GITIGNORE_ENTRIES,
    SCAFFOLD_GITIGNORE_HEADER,
};
