// scripts/codegen/query-params.js -- the query parameters of a generated wrapper.
//
// Until issue #34 every generated method sent `query: null`: 17 captured calls
// to GET /suggestions?prefix=<typed text>&limit=11 became a method, and an MCP
// tool, that took nothing and asked the site for /suggestions. This module
// reads what the capture showed for an endpoint and decides, per parameter,
// what a generated call sends when the caller says nothing:
//
//   scrubbed   the captured value was put there by the scrub     left out
//   nonce      it looked like a per-request timestamp or nonce   left out
//   single     the endpoint was captured once                    the captured value
//   constant   every captured call sent the same value(s)        the captured value(s)
//   varying    anything else                                     left out
//
// in that order: a scrubbed value is never a default however constant it was.
// Every parameter is optional; the caller can always supply its own.
//
// It decides and names. query-emit.js renders the C#, the README and the tests.
//
// Zero npm deps. Deterministic: same samples in, same parameters out.

'use strict';

const fs = require('fs');
const path = require('path');
// The scrubber's own definitions, imported rather than re-spelled: what its
// redaction markers look like, which fakes the leak gate exempts as its own,
// which shapes it always replaces, which names it treats as secrets, and where
// it writes its tables.
const { isRedacted, isKnownSecretField, isKnownSecretHeader } = require('../har/har-secrets.js');
const { LEAK_PATTERNS } = require('../har/har-shapes.js');
const { PATTERNS: SCRUBBED_SHAPES, CAPTURES_DIR } = require('../har/sanitize-har.js');
const { LEGACY_SUBS_FILENAME, PII_SUBS_FILENAME } = require('../har/subs-destination.js');

// -------------------- Recognising a value the scrub replaced --------------------

// A scrubber marker INSIDE a larger value (`/users/<AccountId>/orders`).
// `isRedacted` answers for a value that is nothing but a marker; these are the
// same spellings, unanchored:
//   <AccountId>                 a .har-profile.json literal's sentinel (har-profile.js)
//   <REDACTED-1a2b3c4d>         sanitize-har.js / pii.js, a kind with no format of its own
//   <BLUNTED:jwt:ABCD>          har-blunt.js
//   redacted-3f9a1c0b7e2d4a68   sanitize-har.js, a cookie or a named field
//   192.0.2.7                   pii.js, an IP address (RFC 5737 TEST-NET-1, never routable)
//   eyJ<18 hex>.<40 hex>.<hex>  sanitize-har.js, a JWT. Its last run is 6
//                               characters, not the 43 the fake is written to
//                               have (the digest it is cut from is 64 long), so
//                               neither the JWT shape nor har-shapes.js's
//                               `isFake` for it matches what is really emitted.
const EMBEDDED_MARKER_RE = new RegExp([
    '<[A-Za-z][A-Za-z0-9]*>',
    '<REDACTED-[0-9a-f]{8}>',
    '<BLUNTED:[^<>]*>',
    '\\bredacted-[0-9a-f]{8,}',
    '\\b192\\.0\\.2\\.\\d{1,3}\\b',
    '\\beyJ[0-9a-f]{18}\\.[0-9a-f]{40}\\.[0-9a-f]+',
].join('|'));

// A typed-PII fake shorter than this is matched only where its table says it
// was put. `0` (a coordinate) and `ZZ` (a country) are fakes, and are also what
// `page=0` and `lang=ZZ` look like.
const MIN_DISTINCTIVE_FAKE_LENGTH = 6;

function emptyScrubTables() {
    return { values: new Set(), namedValues: new Set(), sources: [], problems: [] };
}

function namedValueKey(name, value) {
    return name + '\u0000' + value;
}

function readLegacyTable(table, scrub) {
    // { "<kind>:<plaintext original>": "<fake>" }. Only the VALUES are read:
    // the keys are what the scrub removed.
    for (const fake of Object.values(table)) {
        if (typeof fake !== 'string' || fake === '') continue;
        scrub.values.add(fake);
        // A bearer token's stand-in travels without its scheme too.
        const bare = /^Bearer\s+(\S+)$/i.exec(fake);
        if (bare) scrub.values.add(bare[1]);
    }
}

function readPiiTable(table, scrub) {
    // { substitutions: [{ type, originalHash, replacement, locations }] }.
    const QUERY_PATH = 'request.url.query.';
    for (const substitution of (table && table.substitutions) || []) {
        if (!substitution || substitution.replacement === undefined || substitution.replacement === null) continue;
        const fake = String(substitution.replacement);
        if (fake.length >= MIN_DISTINCTIVE_FAKE_LENGTH) scrub.values.add(fake);
        for (const location of substitution.locations || []) {
            if (!location) continue;
            if (typeof location.queryParam === 'string') {
                scrub.namedValues.add(namedValueKey(location.queryParam, fake));
            }
            if (typeof location.jsonPath === 'string' && location.jsonPath.startsWith(QUERY_PATH)) {
                scrub.namedValues.add(namedValueKey(location.jsonPath.slice(QUERY_PATH.length), fake));
            }
        }
    }
}

/**
 * The scrub's substitution tables for these captures, found by convention.
 *
 * Most of the scrubber's fakes are recognisable on sight. Some are not: a typed
 * fake for a city or a name is an ordinary word. The tables list every
 * replacement the scrub made, so they settle it.
 *
 * `run-agent.js` writes the scrubbed capture to `.run-agent/scrubbed.har` and
 * both tables to `.run-agent/.har-captures/`. So the tables are looked for in
 * `.har-captures/` beside each capture, and beside the capture itself. No
 * option names them: where they are IS how the pipeline hands them over.
 *
 * Only replacement values are kept. The legacy table's keys are the plaintext
 * originals; they are parsed with the file and never looked at.
 *
 * @param {string[]} harPaths The captures given to the generator.
 * @returns {{values: Set<string>, namedValues: Set<string>, sources: string[], problems: string[]}}
 *          `sources` are the table files read, `problems` the ones that could
 *          not be -- paths and reasons only, never contents.
 */
function loadScrubTables(harPaths) {
    const scrub = emptyScrubTables();
    const seen = new Set();
    for (const harPath of harPaths || []) {
        const dir = path.dirname(path.resolve(harPath));
        for (const tableDir of [path.join(dir, CAPTURES_DIR), dir]) {
            for (const [fileName, read] of [[LEGACY_SUBS_FILENAME, readLegacyTable], [PII_SUBS_FILENAME, readPiiTable]]) {
                const file = path.join(tableDir, fileName);
                if (seen.has(file) || !fs.existsSync(file)) continue;
                seen.add(file);
                let table;
                try {
                    table = JSON.parse(fs.readFileSync(file, 'utf8'));
                } catch (e) {
                    // The file name and the kind of failure. Not e.message: a
                    // JSON parse error quotes the text it choked on.
                    scrub.problems.push(file + ' could not be read as JSON (' + (e && e.name ? e.name : 'error') + ')');
                    continue;
                }
                if (table && typeof table === 'object') read(table, scrub);
                scrub.sources.push(file);
            }
        }
    }
    return scrub;
}

function carriesScrubberFake(value) {
    for (const pattern of LEAK_PATTERNS) {
        for (const match of value.match(pattern.re) || []) {
            if (pattern.isFake(match)) return true;
        }
    }
    return false;
}

function carriesScrubbedShape(value) {
    // String.prototype.match resets a global pattern's lastIndex, so these
    // shared, stateful patterns are safe to reuse here.
    return SCRUBBED_SHAPES.some((shape) => (value.match(shape.re) || []).length > 0);
}

/**
 * Why a captured query value must not be written into generated code, or null
 * when nothing says so.
 *
 *   secret-name    the parameter's name is one the scrub policy treats as a
 *                  secret -- whatever the value looks like, and whether or not
 *                  this capture was ever scrubbed
 *   scrub-table    the value is a replacement listed in a substitution table
 *   scrub-marker   the value is, or carries, one of the scrubber's own markers
 *                  or recognisable fakes
 *   secret-shape   the value has a shape the scrub always replaces (a JWT, a
 *                  UUID, a 32/64-hex run, an email). In a scrubbed capture it
 *                  IS a fake; in an unscrubbed one it is the real thing. Either
 *                  way it does not belong in source.
 *
 * @param {string} name   The parameter's name on the wire.
 * @param {string} value  One captured value, decoded.
 * @param {object} [scrub] From loadScrubTables().
 */
function scrubReasonFor(name, value, scrub) {
    if (isKnownSecretField(name) || isKnownSecretHeader(name)) return 'secret-name';
    if (typeof value !== 'string' || value === '') return null;
    if (scrub) {
        if (scrub.values.has(value) || scrub.namedValues.has(namedValueKey(name, value))) return 'scrub-table';
    }
    if (isRedacted(value) || EMBEDDED_MARKER_RE.test(value) || carriesScrubberFake(value)) return 'scrub-marker';
    if (carriesScrubbedShape(value)) return 'secret-shape';
    return null;
}

// -------------------- Recognising a per-request timestamp or nonce --------------------

const ONE_DAY_MS = 24 * 60 * 60 * 1000;
const MIN_NONCE_DIGITS = 10;
const MIN_OPAQUE_TOKEN_LENGTH = 16;

// Names a client library gives the parameter it adds to defeat a cache.
const CACHE_BUSTER_NAME_RE = /^(?:_|_?t|_?ts|timestamp|no[-_]?cache|cache[-_]?buster|cb|rnd|rand|random|nonce)$/i;

/** A 10-digit (seconds) or 13-digit (milliseconds) number within a day of the call it was sent on. */
function isTimestampOfItsCall(value, startedMs) {
    if (!Number.isFinite(startedMs) || !/^(?:\d{10}|\d{13})$/.test(value)) return false;
    const ms = value.length === 10 ? Number(value) * 1000 : Number(value);
    return Math.abs(ms - startedMs) <= ONE_DAY_MS;
}

function isLongDigitRun(value) {
    return /^\d+$/.test(value) && value.length >= MIN_NONCE_DIGITS;
}

/** `1794986993187`, or Math.random()'s `0.4821937465021`. */
function isLongNumber(value) {
    return /^\d+(?:\.\d+)?$/.test(value) && value.replace('.', '').length >= MIN_NONCE_DIGITS;
}

/** Long, unbroken, and letters and digits mixed: not a word, a phrase or a small number. */
function isOpaqueToken(value) {
    return value.length >= MIN_OPAQUE_TOKEN_LENGTH &&
        /^[A-Za-z0-9_\-+/=.~:%]+$/.test(value) &&
        /[0-9]/.test(value) && /[A-Za-z]/.test(value);
}

/**
 * Why a parameter looks like a per-request timestamp or nonce, or null.
 *
 * @param {string} name
 * @param {{values: string[], startedMs: number}[]} sent One entry per call that carried the parameter.
 */
function nonceReasonFor(name, sent) {
    const values = sent.flatMap((call) => call.values);
    if (values.length === 0 || values.some((v) => v === '')) return null;

    // The time of the call itself. This needs no second call to compare with,
    // which is what catches `_=1794986993187` on an endpoint captured once.
    if (sent.every((call) => call.values.every((v) => isTimestampOfItsCall(v, call.startedMs)))) return 'timestamp';

    if (CACHE_BUSTER_NAME_RE.test(name) && values.every(isLongNumber)) return 'cache-buster';

    // "Differs on every call" needs calls to differ between.
    if (sent.length >= 2) {
        const signatures = new Set(sent.map((call) => JSON.stringify(call.values)));
        const differsEveryCall = signatures.size === sent.length;
        if (differsEveryCall && values.every((v) => isLongDigitRun(v) || isOpaqueToken(v))) return 'differs-every-call';
    }
    return null;
}

// -------------------- C# and tool parameter names --------------------

// C#'s reserved keywords, and `await`, which an async method reserves too.
const CSHARP_KEYWORDS = new Set([
    'abstract', 'as', 'base', 'bool', 'break', 'byte', 'case', 'catch', 'char', 'checked', 'class', 'const',
    'continue', 'decimal', 'default', 'delegate', 'do', 'double', 'else', 'enum', 'event', 'explicit', 'extern',
    'false', 'finally', 'fixed', 'float', 'for', 'foreach', 'goto', 'if', 'implicit', 'in', 'int', 'interface',
    'internal', 'is', 'lock', 'long', 'namespace', 'new', 'null', 'object', 'operator', 'out', 'override',
    'params', 'private', 'protected', 'public', 'readonly', 'ref', 'return', 'sbyte', 'sealed', 'short',
    'sizeof', 'stackalloc', 'static', 'string', 'struct', 'switch', 'this', 'throw', 'true', 'try', 'typeof',
    'uint', 'ulong', 'unchecked', 'unsafe', 'ushort', 'using', 'virtual', 'void', 'volatile', 'while',
    'await',
]);

// Identifiers a generated method already uses: its own parameters (`body`,
// `ct`), the locals of the client method (`raw`, `envelope`, `wireQuery`) and
// the lambda parameter of the tool method (`client`). A query parameter that
// took one of these would not compile. Path parameters are added per endpoint.
const GENERATED_IDENTIFIERS = ['body', 'ct', 'raw', 'envelope', 'wireQuery', 'client'];
const COLLISION_SUFFIX = 'Query';

// Well inside the 64 characters an MCP client allows a property name, with
// room for the collision suffix and a number.
const MAX_IDENTIFIER_LENGTH = 48;

/** `page-size` -> pageSize, `q[0]` -> q0, `URLPath` -> urlPath, `_` -> underscore. */
function baseIdentifierFor(wireName) {
    const words = String(wireName).split(/[^A-Za-z0-9]+/).filter(Boolean);
    if (words.length === 0) return wireName === '_' ? 'underscore' : 'param';
    const first = words[0].replace(/^[A-Z]+(?=[A-Z][a-z])|^[A-Z]+$|^[A-Z]/, (run) => run.toLowerCase());
    const rest = words.slice(1).map((w) => w.charAt(0).toUpperCase() + w.slice(1));
    let identifier = first + rest.join('');
    if (/^[0-9]/.test(identifier)) identifier = 'p' + identifier;
    return identifier.slice(0, MAX_IDENTIFIER_LENGTH);
}

/**
 * A valid, unique C# parameter name for each wire name, in the order given.
 *
 * The MCP SDK names a tool's property after the C# parameter, so the same name
 * (without the `@` of an escaped keyword) is what a model sees. It is ASCII
 * letters and digits only and compared without case: a client may refuse
 * anything else, and PowerShell reads `pageSize` and `PageSize` as one.
 *
 * @param {string[]} wireNames
 * @param {string[]} reserved  Names the method already declares, e.g. its path parameters.
 * @returns {{wireName: string, csName: string, toolName: string}[]}
 */
function assignIdentifiers(wireNames, reserved) {
    const methodsOwn = new Set([...GENERATED_IDENTIFIERS, ...(reserved || [])].map((n) => n.toLowerCase()));
    const taken = new Set(methodsOwn);
    return wireNames.map((wireName) => {
        let base = baseIdentifierFor(wireName);
        // What the method already has keeps its name; the query parameter says
        // what it is. Two query parameters that collide are numbered instead.
        if (methodsOwn.has(base.toLowerCase())) base += COLLISION_SUFFIX;
        let name = base;
        for (let n = 2; taken.has(name.toLowerCase()); n++) name = base + n;
        taken.add(name.toLowerCase());
        return { wireName, csName: CSHARP_KEYWORDS.has(name) ? '@' + name : name, toolName: name };
    });
}

// -------------------- Collecting and classifying --------------------

const MAX_EXAMPLES = 3;

function distinctInOrder(values) {
    return Array.from(new Set(values));
}

function classify(wireName, perCall, sent, scrub) {
    const values = sent.flatMap((call) => call.values);
    // One replaced value is enough: what the scrub touched once it would touch
    // again. (A secret NAME answers here too, whatever the value, even empty.)
    for (const value of values) {
        const reason = scrubReasonFor(wireName, value, scrub);
        if (reason) return { kind: 'scrubbed', reason, defaults: null, examples: [] };
    }

    const nonce = nonceReasonFor(wireName, sent);
    if (nonce) return { kind: 'nonce', reason: nonce, defaults: null, examples: [] };

    if (perCall.length === 1) return { kind: 'single', reason: null, defaults: perCall[0].slice(), examples: [] };

    const signatures = new Set(perCall.map((callValues) => JSON.stringify(callValues)));
    if (sent.length === perCall.length && signatures.size === 1) {
        return { kind: 'constant', reason: null, defaults: perCall[0].slice(), examples: [] };
    }
    const examples = distinctInOrder(values.filter((v) => v !== '')).slice(0, MAX_EXAMPLES);
    return { kind: 'varying', reason: null, defaults: null, examples };
}

/**
 * The query parameters of one endpoint, from every captured call of it.
 *
 * @param {object[]} samples  A pattern's samples from dedupePatterns(): one per
 *        captured call, each with `url.query` -- the call's ordered, decoded
 *        `[name, value]` pairs -- and the HAR `entry` it came from.
 * @param {object}   [options]
 * @param {string[]} [options.reserved] C# names the method already declares (its path parameters).
 * @param {object}   [options.scrub]    From loadScrubTables().
 * @returns {object[]} One per parameter name, in first-seen order:
 *   wireName   the name on the wire, exactly
 *   csName     the C# parameter name (`@class` for a keyword)
 *   toolName   the MCP tool property name (`class`)
 *   kind       scrubbed | nonce | single | constant | varying
 *   reason     why it is scrubbed or a nonce; null otherwise
 *   repeated   some call sent it more than once (`a=1&a=2`)
 *   defaults   the values sent when the caller supplies nothing; null = left out
 *   examples   a few observed values of a varying parameter
 *   calls      how many calls of the endpoint were captured
 *   sentIn     how many of them carried the parameter
 */
function collectQueryParameters(samples, options) {
    const opts = options || {};
    const calls = (samples || []).map((sample) => ({
        pairs: (sample && sample.url && Array.isArray(sample.url.query)) ? sample.url.query : [],
        startedMs: Date.parse(sample && sample.entry && sample.entry.startedDateTime),
    }));

    // First-seen order: the first call's own order, then whatever a later call adds.
    const wireNames = distinctInOrder(calls.flatMap((call) => call.pairs.map(([name]) => name)));
    if (wireNames.length === 0) return [];

    const identifiers = assignIdentifiers(wireNames, opts.reserved);
    return identifiers.map((identifier) => {
        const perCall = calls.map((call) => call.pairs.filter(([name]) => name === identifier.wireName).map(([, value]) => value));
        const sent = calls
            .map((call, i) => ({ values: perCall[i], startedMs: call.startedMs }))
            .filter((call) => call.values.length > 0);
        const verdict = classify(identifier.wireName, perCall, sent, opts.scrub);
        return {
            wireName: identifier.wireName,
            csName: identifier.csName,
            toolName: identifier.toolName,
            kind: verdict.kind,
            reason: verdict.reason,
            repeated: perCall.some((callValues) => callValues.length > 1),
            defaults: verdict.defaults,
            examples: verdict.examples,
            calls: calls.length,
            sentIn: sent.length,
        };
    });
}

// -------------------- The wire --------------------

/**
 * .NET's Uri.EscapeDataString, which is what the generated client's BuildQuery
 * escapes names and values with: everything but RFC 3986's unreserved
 * characters. encodeURIComponent also leaves ! * ' ( ) alone; .NET does not.
 */
function escapeDataString(value) {
    return encodeURIComponent(String(value))
        .replace(/[!*'()]/g, (c) => '%' + c.charCodeAt(0).toString(16).toUpperCase());
}

/**
 * The query string the generated client puts on the wire for these pairs, in
 * order: `?a=1&b=` -- or nothing at all when every value is null.
 *
 * @param {Array<[string, (string|null)]>} pairs A null value is left out; an empty one is sent.
 */
function wireQuery(pairs) {
    const sent = pairs.filter(([, value]) => value !== null && value !== undefined);
    if (sent.length === 0) return '';
    return '?' + sent.map(([name, value]) => escapeDataString(name) + '=' + escapeDataString(value)).join('&');
}

module.exports = {
    collectQueryParameters,
    loadScrubTables,
    scrubReasonFor,
    nonceReasonFor,
    assignIdentifiers,
    escapeDataString,
    wireQuery,
};
