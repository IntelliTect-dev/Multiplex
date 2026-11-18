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
const { LEAK_PATTERNS, findLeaks } = require('../har/har-shapes.js');
const harPolicy = require('../har/har-policy.js');
const { findUpward } = require('../har/har-profile.js');
const { PATTERNS: SCRUBBED_SHAPES, CAPTURES_DIR, deriveSubsDir } = require('../har/sanitize-har.js');
const { LEGACY_SUBS_FILENAME, PII_SUBS_FILENAME } = require('../har/subs-destination.js');
// How the tool emitter splits a path segment into words. A parameter's name is
// split the same way, so the two cannot disagree about what a word is.
const { wordsOf } = require('./mcp-tools-emit.js');

// -------------------- Recognising a value the scrub replaced --------------------

// A scrubber marker INSIDE a larger value (`/users/<AccountId>/orders`).
// `isRedacted` answers for a value that is nothing but a marker -- any
// `<...>` at all. Inside a larger value that would take every HTML-like value
// (`<b>bold</b>`, `<summary>`) for a scrub replacement, so only the spellings
// the scrubber really writes are looked for:
//   <AccountId>                 a .har-profile.json literal's sentinel, as the
//                               profile's own documentation spells them:
//                               PascalCase, so a capital first and a small
//                               letter somewhere (`<b>` and `<BR>` are neither)
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
    '<[A-Z][A-Za-z0-9]*[a-z][A-Za-z0-9]*>',
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
    return { values: new Set(), namedValues: new Set(), sources: [], problems: [], searched: [], policies: [] };
}

function namedValueKey(name, value) {
    return name + '\u0000' + value;
}

function isPlainObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function entriesIgnored(count) {
    return count + (count === 1 ? ' entry' : ' entries') + ' did not have the shape of a substitution and ' +
        (count === 1 ? 'was' : 'were') + ' ignored';
}

/**
 * Read the legacy table: `{ "<kind>:<plaintext original>": "<fake>" }`. Only the
 * VALUES are read: the keys are what the scrub removed.
 *
 * @returns {string|null} What is wrong with the file's SHAPE, or null. Never
 *          anything of its content: a problem is reported to a log.
 */
function readLegacyTable(table, scrub) {
    if (!isPlainObject(table)) return 'is not an object of replacement strings';
    let ignored = 0;
    for (const fake of Object.values(table)) {
        if (typeof fake !== 'string') { ignored++; continue; }
        if (fake === '') continue;
        scrub.values.add(fake);
        // A bearer token's stand-in travels without its scheme too.
        const bare = /^Bearer\s+(\S+)$/i.exec(fake);
        if (bare) scrub.values.add(bare[1]);
    }
    return ignored > 0 ? entriesIgnored(ignored) : null;
}

/**
 * Read the typed table: `{ substitutions: [{ type, originalHash, replacement,
 * locations }] }`. A replacement long enough to be distinctive is matched
 * anywhere; a short one only under the parameter name a location gives.
 *
 * @returns {string|null} As readLegacyTable().
 */
function readPiiTable(table, scrub) {
    if (!isPlainObject(table) || !Array.isArray(table.substitutions)) {
        return 'is not an object with a "substitutions" list';
    }
    const QUERY_PATH = 'request.url.query.';
    let ignored = 0;
    for (const substitution of table.substitutions) {
        const replacement = isPlainObject(substitution) ? substitution.replacement : undefined;
        if (typeof replacement !== 'string' && typeof replacement !== 'number') { ignored++; continue; }
        const fake = String(replacement);
        if (fake.length >= MIN_DISTINCTIVE_FAKE_LENGTH) scrub.values.add(fake);
        // The replacement is what matters. Locations only sharpen a short one,
        // so locations of the wrong shape cost that and nothing else.
        const locations = Array.isArray(substitution.locations) ? substitution.locations : [];
        for (const location of locations) {
            if (!isPlainObject(location)) continue;
            if (typeof location.queryParam === 'string') {
                scrub.namedValues.add(namedValueKey(location.queryParam, fake));
            }
            if (typeof location.jsonPath === 'string' && location.jsonPath.startsWith(QUERY_PATH)) {
                scrub.namedValues.add(namedValueKey(location.jsonPath.slice(QUERY_PATH.length), fake));
            }
        }
    }
    return ignored > 0 ? entriesIgnored(ignored) : null;
}

// The scrub's conventional layout: it reads `<x>/har-original/<file>` and
// writes `<x>/har/<file>` (sanitize-har.js, deriveOutPath). These are that
// function's two folder names, not a second convention.
const SCRUB_INPUT_FOLDER = 'har-original';
const SCRUB_OUTPUT_FOLDER = 'har';

/**
 * Where the scrub would have put its tables for a capture the generator is
 * given, in the order they are searched:
 *
 *   <dir>/.har-captures/   run-agent.js writes `.run-agent/scrubbed.har` and puts
 *                          both tables here
 *   <dir>/                 a capture inside the gitignored store, where the
 *                          raw capture, the scrubbed one and the tables share
 *                          a session folder
 *   the scrub's own default for its documented standalone layout: when the
 *   capture sits in a `har` folder, the scrub's input was the same file under
 *   the sibling `har-original`, and it puts the tables beside its INPUT
 *   (sanitize-har.js, deriveSubsDir) -- which is asked, not re-derived.
 */
function tableDirectoriesFor(harPath) {
    const resolved = path.resolve(harPath);
    const dir = path.dirname(resolved);
    const directories = [path.join(dir, CAPTURES_DIR), dir];
    const segments = dir.split(path.sep);
    const outputFolder = segments.lastIndexOf(SCRUB_OUTPUT_FOLDER);
    if (outputFolder >= 0) {
        segments[outputFolder] = SCRUB_INPUT_FOLDER;
        const scrubInput = path.join(segments.join(path.sep), path.basename(resolved));
        directories.push(deriveSubsDir(scrubInput));
    }
    return Array.from(new Set(directories));
}

/**
 * The merged scrub policy that governs a capture, found the way the scrub and
 * the leak gate find it: `.har-policy.project.json`, walking up from the
 * capture's own folder, merged over the shipped default. A project that added
 * a secret name, or vetoed a shipped one, is honoured here as it is there.
 *
 * A project policy that will not load is reported -- by its path and the kind
 * of failure, never its content -- and the shipped default stands in for it.
 */
function policyFor(harPath, scrub) {
    const startDir = path.dirname(path.resolve(harPath));
    try {
        return harPolicy.loadPolicy({ startDir });
    } catch (e) {
        // Named by the same upward walk the loader made. Not e.message: a
        // policy error quotes the entry it objects to.
        const projectPath = findUpward(harPolicy.POLICY_FILENAME, startDir) || harPolicy.POLICY_FILENAME;
        scrub.problems.push(projectPath + ' could not be loaded as a scrub policy (' +
            (e && e.name ? e.name : 'error') + '); the shipped secret names are used instead');
        return harPolicy.loadDefaultPolicy();
    }
}

/**
 * The scrub's substitution tables for these captures, found by convention.
 *
 * Most of the scrubber's fakes are recognisable on sight. Some are not: a typed
 * fake for a city or a name is an ordinary word. The tables list every
 * replacement the scrub made, so they settle it.
 *
 * No option names them: where they are IS how the pipeline hands them over.
 * They are looked for where the scrub puts them (tableDirectoriesFor), and
 * every directory searched is reported back, found or not -- a caller that
 * found none can say so, because a fake with no marker is then a constant as
 * far as anyone can tell.
 *
 * Only replacement values are kept. The legacy table's keys are the plaintext
 * originals; they are parsed with the file and never looked at.
 *
 * The scrub policy that governs each capture is loaded with them (policyFor):
 * which names are secrets is the other half of what the scrub knew.
 *
 * @param {string[]} harPaths The captures given to the generator.
 * @returns {object}
 *   values, namedValues  the replacements, for scrubReasonFor()
 *   policies             the merged scrub policy of each capture
 *   sources              the table files read
 *   searched             per capture: `{ har, directories, found }` -- where
 *                        its tables were looked for, and which were there
 *   problems             files that could not be used -- a path and the shape
 *                        of the problem, never any content
 */
function loadScrubTables(harPaths) {
    const scrub = emptyScrubTables();
    const read = new Set();
    for (const harPath of harPaths || []) {
        const record = { har: path.resolve(harPath), directories: tableDirectoriesFor(harPath), found: [] };
        scrub.searched.push(record);
        scrub.policies.push(policyFor(harPath, scrub));
        for (const tableDir of record.directories) {
            for (const [fileName, readTable] of [[LEGACY_SUBS_FILENAME, readLegacyTable], [PII_SUBS_FILENAME, readPiiTable]]) {
                const file = path.join(tableDir, fileName);
                if (!fs.existsSync(file)) continue;
                record.found.push(file);
                if (read.has(file)) continue;
                read.add(file);
                let table;
                try {
                    table = JSON.parse(fs.readFileSync(file, 'utf8'));
                } catch (e) {
                    // The file name and the kind of failure. Not e.message: a
                    // JSON parse error quotes the text it choked on.
                    scrub.problems.push(file + ' could not be read as JSON (' + (e && e.name ? e.name : 'error') + ')');
                    continue;
                }
                const wrongShape = readTable(table, scrub);
                if (wrongShape) scrub.problems.push(file + ' ' + wrongShape);
                // A file that is not a table at all taught nothing: not a source.
                if (!wrongShape || !/^is not /.test(wrongShape)) scrub.sources.push(file);
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

/** The policies to ask: each capture's merged one, or the shipped default when none was loaded. */
function policiesOf(scrub) {
    return (scrub && Array.isArray(scrub.policies) && scrub.policies.length > 0) ? scrub.policies : [undefined];
}

/**
 * Whether the leak gate itself would report something in this value: its own
 * predicate (har-shapes.js, findLeaks) -- each pattern WITH its validator, and
 * its own fakes excepted. The raw patterns are not enough: the MAC pattern
 * matches `de-DE` and the card pattern any 13-digit timestamp, and the
 * validators (six pairs; an assigned issuer and a checksum) are what make
 * either a finding. A phone number, a social security number, a card, an IBAN,
 * a MAC address and a bearer token are caught here; a UUID is not the gate's
 * business, and is caught by carriesScrubbedShape().
 */
function carriesLeakShape(value, scrub) {
    return policiesOf(scrub).some((policy) => findLeaks(value, policy).length > 0);
}

function isPolicySecretName(name, scrub) {
    return policiesOf(scrub).some((policy) => isKnownSecretField(name, policy) || isKnownSecretHeader(name, policy));
}

// Words that say a parameter carries a credential: a signed URL's signature,
// an API key, a session id, a token. The scrub replaces what it recognises by
// shape and by the names its policy lists; an opaque 20-character key under a
// name nobody listed is neither, and would otherwise become a default.
//
// The signed-URL family needs no words of its own: X-Amz-Credential,
// X-Amz-Signature, X-Amz-Security-Token, X-Goog-Signature, Key-Pair-Id and an
// Azure `sig` each contain one of these as a whole word.
const CREDENTIAL_NAME_WORDS = new Set([
    'token', 'sid', 'session', 'sig', 'signature', 'secret', 'auth', 'credential', 'key', 'apikey', 'password',
]);

/**
 * Whether a parameter's NAME says it carries a credential: one of its words --
 * split as the tool emitter splits a path segment, at camelCase boundaries and
 * at anything that is not a letter or a digit, lower-cased -- IS one of
 * CREDENTIAL_NAME_WORDS. A whole word, with no stemming and no plural: `key`,
 * `apiKey`, `X-Amz-Signature` and `session-id` are; `keyword`, `sidebar`,
 * `author`, `tokenizer` and `sessions-per-page` are not.
 */
function looksLikeCredentialName(name) {
    return wordsOf(name).some((word) => CREDENTIAL_NAME_WORDS.has(word));
}

/**
 * Why a captured string -- a parameter's value, or its name -- must not be
 * written into generated code, or null. See scrubReasonFor().
 *
 * @param {string} value   The captured string, decoded.
 * @param {object} [scrub] From loadScrubTables().
 * @param {string} [name]  The parameter it is a value of, for a table entry
 *                         that is matched only under its own name.
 */
function capturedTextReasonFor(value, scrub, name) {
    if (typeof value !== 'string' || value === '') return null;
    if (scrub && scrub.values && scrub.values.has(value)) return 'scrub-table';
    if (scrub && scrub.namedValues && name !== undefined && scrub.namedValues.has(namedValueKey(name, value))) return 'scrub-table';
    if (isRedacted(value) || EMBEDDED_MARKER_RE.test(value) || carriesScrubberFake(value)) return 'scrub-marker';
    if (carriesScrubbedShape(value) || carriesLeakShape(value, scrub)) return 'secret-shape';
    return null;
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
 *                  UUID, a 32/64-hex run, an email), or one the leak gate
 *                  would report (a phone number, a card, an IBAN, a bearer
 *                  token, ...). In a scrubbed capture it IS a fake; in an
 *                  unscrubbed one it is the real thing. Either way it does not
 *                  belong in source.
 *   credential-name  nothing about the value says so, but the parameter's NAME
 *                  does: a token, a key, a session id, a signature. An opaque
 *                  credential has no shape for the scrub to recognise.
 *
 * In that order: what is known about the value is a better reason than a guess
 * from the name, and the policy's word is better than either.
 *
 * @param {string} name   The parameter's name on the wire.
 * @param {string} value  One captured value, decoded.
 * @param {object} [scrub] From loadScrubTables().
 */
function scrubReasonFor(name, value, scrub) {
    if (isPolicySecretName(name, scrub)) return 'secret-name';
    return capturedTextReasonFor(value, scrub, name) || (looksLikeCredentialName(name) ? 'credential-name' : null);
}

/**
 * Why a captured NAME is not a parameter at all, or null when it is one.
 *
 * A name is text from the capture like a value is. `url + '?' + Date.now()`
 * puts a timestamp where a name goes -- a different "parameter" on every call,
 * each re-sent stale for ever -- and the scrub rewrites an email used as a
 * name to its fake as readily as one used as a value. Emitting either would
 * write a scrub fake or a stale cache-buster into the client, the tool schema
 * and the README, and send it.
 *
 *   scrub-table, scrub-marker, secret-shape   as for a value (capturedTextReasonFor)
 *   nonce-name    a timestamp of the call it was sent on, or a long number
 *
 * @param {string}   name
 * @param {object[]} sent   The calls that carried it, each with `startedMs`.
 * @param {object}   [scrub] From loadScrubTables().
 */
function droppedNameReasonFor(name, sent, scrub) {
    const captured = capturedTextReasonFor(name, scrub);
    if (captured) return captured;
    const isTimestamp = sent.length > 0 && sent.every((call) => isTimestampOfItsCall(name, call.startedMs));
    return (isTimestamp || isLongNumber(name)) ? 'nonce-name' : null;
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
    // An empty value says nothing either way: a call that sent `_=` does not
    // make the timestamps of the other calls any less stale. So the empties
    // are set aside, not taken as proof that the parameter is an ordinary one.
    const filled = sent
        .map((call) => ({ values: call.values.filter((v) => v !== ''), startedMs: call.startedMs }))
        .filter((call) => call.values.length > 0);
    const values = filled.flatMap((call) => call.values);
    if (values.length === 0) return null;

    // The time of the call itself. This needs no second call to compare with,
    // which is what catches `_=1794986993187` on an endpoint captured once.
    if (filled.every((call) => call.values.every((v) => isTimestampOfItsCall(v, call.startedMs)))) return 'timestamp';

    if (CACHE_BUSTER_NAME_RE.test(name) && values.every(isLongNumber)) return 'cache-buster';

    // "Differs on every call" needs calls to differ between.
    if (filled.length >= 2) {
        const signatures = new Set(filled.map((call) => JSON.stringify(call.values)));
        const differsEveryCall = signatures.size === filled.length;
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

function scrubbed(reason) {
    return { kind: 'scrubbed', reason, defaults: null, examples: [] };
}

function classify(wireName, perCall, sent, scrub) {
    const values = sent.flatMap((call) => call.values);
    // The reasons of scrubReasonFor(), over every captured value: the policy's
    // word on the name first, whatever the values are (even empty); then what
    // any one value shows -- one replaced value is enough, since what the
    // scrub touched once it would touch again; then what the name suggests.
    if (isPolicySecretName(wireName, scrub)) return scrubbed('secret-name');
    for (const value of values) {
        const reason = capturedTextReasonFor(value, scrub, wireName);
        if (reason) return scrubbed(reason);
    }
    if (looksLikeCredentialName(wireName)) return scrubbed('credential-name');

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
function callsOf(samples) {
    return (samples || []).map((sample) => ({
        pairs: (sample && sample.url && Array.isArray(sample.url.query)) ? sample.url.query : [],
        startedMs: Date.parse(sample && sample.entry && sample.entry.startedDateTime),
    }));
}

/**
 * Every captured name of an endpoint, in first-seen order -- the first call's
 * own order, then whatever a later call adds -- each with why it is not a
 * parameter (droppedNameReasonFor), or null when it is one.
 */
function screenNames(calls, scrub) {
    const wireNames = distinctInOrder(calls.flatMap((call) => call.pairs.map(([name]) => name)));
    return wireNames.map((name) => {
        const sent = calls.filter((call) => call.pairs.some(([n]) => n === name));
        return { name, sentIn: sent.length, dropped: droppedNameReasonFor(name, sent, scrub) };
    });
}

/**
 * The captured names of an endpoint that are NOT emitted as parameters,
 * counted by reason and never named: a name that is a scrub fake or a secret
 * shape must not be printed, and one that is a timestamp is noise.
 *
 * @param {object[]} samples  As for collectQueryParameters().
 * @param {object}   [options] `scrub`, from loadScrubTables().
 * @returns {{reason: string, names: number, calls: number}[]} In first-seen
 *          order of the reasons: how many distinct names were dropped for it,
 *          and in how many calls of the endpoint at least one of them was sent.
 */
function collectDroppedQueryNames(samples, options) {
    const calls = callsOf(samples);
    const screened = screenNames(calls, (options || {}).scrub).filter((s) => s.dropped);
    return distinctInOrder(screened.map((s) => s.dropped)).map((reason) => {
        const names = new Set(screened.filter((s) => s.dropped === reason).map((s) => s.name));
        return {
            reason,
            names: names.size,
            calls: calls.filter((call) => call.pairs.some(([name]) => names.has(name))).length,
        };
    });
}

function collectQueryParameters(samples, options) {
    const opts = options || {};
    const calls = callsOf(samples);

    // A captured name that is not a parameter at all is not emitted: see
    // droppedNameReasonFor(), and collectDroppedQueryNames() for the account.
    const wireNames = screenNames(calls, opts.scrub).filter((s) => !s.dropped).map((s) => s.name);
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
    collectDroppedQueryNames,
    loadScrubTables,
    scrubReasonFor,
    looksLikeCredentialName,
    nonceReasonFor,
    assignIdentifiers,
    escapeDataString,
    wireQuery,
};
