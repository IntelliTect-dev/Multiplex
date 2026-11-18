// scripts/codegen/mcp-tools-emit.js -- the MCP tool class of a generated wrapper.
//
// McpProgram.cs registers tools with WithToolsFromAssembly(), which finds
// classes marked [McpServerToolType] and their [McpServerTool] methods. Until
// issue #17 the generator emitted neither, so a generated server started and
// listed no tools. This module decides what the tools are -- name, hints,
// description, parameters -- and renders <Name>Tools.Generated.cs.
//
// listTools() is the single description of the tool surface. The emitter
// renders it, and tests-emit.js bakes the same list into the wrapper's smoke
// test as what a RUNNING server must answer to tools/list. That list comes from
// the endpoint list, never from the rendered C#: remove the rendering and the
// expectation is still there to fail.
//
// Attribute property names (Name, ReadOnly, Destructive, Idempotent, OpenWorld)
// are those of ModelContextProtocol 2.4.0's McpServerToolAttribute, read from
// the package's XML documentation rather than recalled. Its defaults are
// Destructive = true, ReadOnly = false, Idempotent = false, OpenWorld = true;
// every tool sets all four, so no hint on the wire is an accident of a default.
//
// Zero npm deps. Deterministic: same endpoints in, same bytes out.

'use strict';

const crypto = require('crypto');

const GRAPHQL_TOOL_NAME = 'graphql';
const GRAPHQL_METHOD_NAME = 'GraphQLAsync';

// HTTP's safe methods: a request made with one asks for nothing to change.
const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

// A POST is taken to CHANGE OR REMOVE something unless there is positive
// evidence that it only adds (addsOnly, below). Erring this way round is
// deliberate: a tool wrongly called destructive costs a confirmation, one
// wrongly called additive can skip it -- and a list of the words that make a
// POST dangerous can never be complete.
//
// These word lists are therefore vetoes, not the rule: a path that carries one
// of them is not additive whatever else speaks for it. They are matched against
// whole words of the literal path segments, a plural "s" aside.

// Changes or removes what is already there: POST /items/42/delete.
const CHANGING_WORDS = new Set([
    // removes
    'delete', 'remove', 'destroy', 'cancel', 'revoke', 'purge', 'clear',
    'reset', 'erase', 'archive', 'deactivate', 'disable', 'unsubscribe',
    // overwrites
    'update', 'edit', 'replace', 'overwrite', 'rename', 'move', 'set', 'patch',
]);

// Does something that is not "one more record": it sends, pays for, closes or
// releases. POST /payments/send, POST /orders/checkout.
const ACTING_WORDS = new Set([
    'send', 'pay', 'checkout', 'purchase', 'buy', 'charge', 'transfer', 'refund',
    'withdraw', 'close', 'terminate', 'void', 'trash', 'submit', 'publish',
    'approve', 'reject', 'merge', 'unfriend', 'unfollow', 'block',
]);

// Moves money, or commits the user to something. The record such a POST adds
// is the point of it and not a small thing: "one more order" is an order
// placed. So a collection of these is not vouched for by being a collection.
const COMMITTING_WORDS = new Set([
    'order', 'invoice', 'fund', 'donation', 'booking', 'reservation', 'subscription', 'bid', 'trade',
]);

// Carries its command in the body, so the path cannot say what it does:
// POST /rpc, POST /batch.
const OPAQUE_CALL_WORDS = new Set([
    'rpc', 'jsonrpc', 'batch', 'bulk', 'execute', 'exec', 'graphql', 'gql',
    'query', 'command', 'invoke', 'call', 'run', 'action',
]);

// Signs someone in or out, or handles what signing in produces. An endpoint
// with a path segment about one of these gets no tool at all (reasonLeftOut).
const SIGN_IN_WORDS = new Set([
    'auth', 'login', 'logout', 'signin', 'signout', 'token', 'session',
    'oauth', 'sso', 'csrf', 'password', 'mfa', 'otp',
    // the same things under their other names
    'authenticate', 'authentication', 'authorize', 'authorization', 'xsrf', 'totp', 'credential',
    'authn', 'authz', 'signup', 'register', 'saml', 'oidc', 'openid', '2fa',
    'pwd', 'passwd', 'passcode',
    // and what a credential STORE is called by: GET /api/secrets hands its contents over
    'secret', 'apikey', 'accesskey', 'jwt', 'jwks', 'cookie',
]);

// Path segments that are about signing in without containing any of the words.
const SIGN_IN_SEGMENTS = new Set(['j_security_check']);

// The words a sign-in word keeps company with inside one path segment:
// refresh-token, reset_password, userSession, auth-callback. A segment made of
// sign-in words and these is about signing in. Any other word makes it a name
// that merely contains one -- sessions-of-a-conference, token-usage.
const SIGN_IN_COMPANIONS = new Set([
    'access', 'refresh', 'id', 'bearer', 'api', 'user', 'account', 'current', 'my', 'me',
    'key', 'personal', 'client',
    // a file extension says nothing about what the segment is: session.json, login.php
    'json', 'php', 'aspx', 'asp', 'jsp', 'html', 'htm', 'do', 'action', 'xml', 'cgi',
    'new', 'reset', 'change', 'forgot', 'forgotten', 'update', 'verify', 'validate', 'check',
    'renew', 'revoke', 'exchange', 'callback', 'redirect', 'start', 'begin', 'end', 'complete',
    'challenge', 'code', 'status', 'info', 'state', 'keepalive', 'extend', 'create', 'delete',
    'get', 'set', 'generate', 'request', 'confirm', 'send', 'resend', 'device', 'sms', 'email',
    'two', 'factor', 'external', 'social', 'provider',
]);

// What a path has to say for a POST to count as additive.
const ADDING_WORDS = new Set(['create', 'add', 'new']);

const MAX_FIELDS_IN_DESCRIPTION = 10;

// Many MCP clients refuse, or cut, a tool name longer than this, and a deep
// path makes a longer one: /a/{id}/b/{id2}/c/{id3}/d is already a mouthful.
const MAX_TOOL_NAME_LENGTH = 64;
const NAME_HASH_LENGTH = 8;

/** Words of a path segment: `cancelOrder`, `cancel-order`, `cancel_order` -> cancel, order. */
function wordsOf(segment) {
    return String(segment)
        .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
        .split(/[^A-Za-z0-9]+/)
        .filter(Boolean)
        .map((w) => w.toLowerCase());
}

// A removal or a payment is as often named by a noun or a participle as by the
// verb: withdrawal, cancellation, deletion, removal, archived, payout. So the
// words that change, act or commit also veto any word that BEGINS with their
// stem -- the word without a final "e" (delet-, remov-, archiv-). A stem
// shorter than four letters is not used: `set`, `buy`, `bid` and `mov` would
// take settings, buyers, bidders and movies with them. `pay` is the exception,
// because payment, payout and payee are what it is there for.
const MIN_STEM_LENGTH = 4;
const VETO_STEMS = [CHANGING_WORDS, ACTING_WORDS, COMMITTING_WORDS]
    .flatMap((words) => Array.from(words))
    .map((word) => (word.endsWith('e') ? word.slice(0, -1) : word))
    .filter((stem) => stem.length >= MIN_STEM_LENGTH)
    .concat(['pay']);

/**
 * Whether a word vetoes "additive": it, or its singular, is in one of the
 * lists, or it begins with the stem of a word that changes, acts or commits.
 */
function vetoesAdditive(word) {
    const singular = word.endsWith('s') ? word.slice(0, -1) : word;
    return [CHANGING_WORDS, ACTING_WORDS, COMMITTING_WORDS, OPAQUE_CALL_WORDS, SIGN_IN_WORDS]
        .some((words) => words.has(word) || words.has(singular)) ||
        VETO_STEMS.some((stem) => word.startsWith(stem));
}

/**
 * Whether there is positive evidence that a POST only adds. All of:
 *
 *   - no path parameter: a POST addressed to something that exists
 *     (/items/{id}, /items/{id}/anything) changes that thing or acts on it;
 *   - no word of the path is a veto (vetoesAdditive);
 *   - and either the path says so -- a whole word create, add or new -- or the
 *     capture does: evidence.isCollection, the same path with an id appended
 *     was captured too, so a POST here makes one more of something.
 */
function addsOnly(segments, evidence) {
    const all = segments || [];
    if (all.some((s) => !s.literal)) return false;
    const words = all.flatMap((s) => wordsOf(s.literal));
    if (words.some(vetoesAdditive)) return false;
    return words.some((w) => ADDING_WORDS.has(w)) || !!(evidence && evidence.isCollection);
}

/**
 * The capture shows things addressed by id under this path: /items beside
 * /items/{id}, on the same host, with any method.
 */
function isCollection(pattern, patterns) {
    const n = pattern.segments.length;
    return patterns.some((q) => q.host === pattern.host && q.segments.length === n + 1 &&
        !!q.segments[n].param &&
        pattern.segments.every((s, i) => !!s.literal && s.literal === q.segments[i].literal));
}

/**
 * `sign-in`, `signIn`, `log_out`, `sign-up`, `api-key` and `accessKeys` are one
 * word each: signin, signin, logout, signup, apikey, accesskeys.
 */
function signInWordsOf(segment) {
    const words = wordsOf(segment);
    const joined = [];
    for (let i = 0; i < words.length; i++) {
        const next = words[i + 1];
        const spelledInTwo =
            ((words[i] === 'sign' || words[i] === 'log') && (next === 'in' || next === 'out' || next === 'up')) ||
            ((words[i] === 'api' || words[i] === 'access') && (next === 'key' || next === 'keys'));
        joined.push(spelledInTwo ? words[i] + words[++i] : words[i]);
    }
    return joined;
}

/** token, tokens, oauth2: a sign-in word, a plural "s" or a version digit aside. */
function isSignInWord(word) {
    const bare = word.replace(/\d+$/, '');
    return SIGN_IN_WORDS.has(bare) || (bare.endsWith('s') && SIGN_IN_WORDS.has(bare.slice(0, -1)));
}

/**
 * Whether a path segment is about signing in. It is when, split into words
 * (camelCase, `-`, `_` and `.` are boundaries), it has a sign-in word and every
 * other word is one of its usual companions or a number:
 *
 *   auth, login, sessions, oauth2, sign-in         the word itself
 *   refresh-token, reset_password, userSession     with its company
 *   api-keys, client-secret, session.json          a credential store; an extension
 *
 * and is not when any other word says what the segment is about:
 *
 *   sessions-of-a-conference, token-usage          a name containing the word
 *   cookie-recipes, secret-santa                   likewise
 *   authors, tokenizer, keyboard-layouts           not the word at all
 *   keys, key-results                              `key` alone is only a companion
 */
function isSignInSegment(segment) {
    if (SIGN_IN_SEGMENTS.has(String(segment).toLowerCase())) return true;
    const words = signInWordsOf(segment);
    return words.some(isSignInWord) &&
        words.every((w) => isSignInWord(w) || SIGN_IN_COMPANIONS.has(w) || /^\d+$/.test(w));
}

/**
 * Every path segment an endpoint was captured with: its literal segments, and
 * the values its path parameters took. The second half matters because the path
 * matcher can fold a name into a parameter -- /api/auth/session captured beside
 * /api/some-thing/7 becomes a value of /api/{id}/{id2} -- and a segment that is
 * no longer in the pattern would otherwise not be seen.
 */
function capturedSegments(pattern) {
    const literals = (pattern.segments || []).filter((s) => s.literal).map((s) => s.literal);
    const captured = (pattern.samples || []).flatMap((sample) => (sample.url && sample.url.segments) || []);
    return literals.concat(captured);
}

/**
 * Why an endpoint gets no tool, or null when it gets one. The client keeps a
 * method for every endpoint either way; this is only about what a model is
 * offered.
 *
 *   OPTIONS, HEAD       ask about an endpoint and return nothing to use; an
 *                       OPTIONS in a capture is a browser's preflight.
 *   a sign-in segment   the call signs someone in or out, or returns what
 *                       signing in produces -- tokens, a session, a CSRF value.
 *                       A model should neither be handed those nor asked to
 *                       type a password into one.
 *   no JSON response    hasJsonResponse === false: generate-wrapper.js saw no
 *                       captured response of it parse as JSON, so the client
 *                       method has nothing typed to return.
 */
function reasonLeftOut(pattern) {
    const method = String(pattern.method || '').toUpperCase();
    if (method === 'OPTIONS' || method === 'HEAD') {
        return method + ' asks about an endpoint and returns nothing for a model to use';
    }
    const signIn = capturedSegments(pattern).find(isSignInSegment);
    if (signIn) return 'its path names sign-in or credentials (' + signIn + ')';
    if (pattern.hasJsonResponse === false) return 'the capture showed no JSON response from it';
    return null;
}

/**
 * The hints a client reads to decide whether a call needs confirming, from the
 * endpoint's HTTP method and path, and for a POST what the capture shows.
 *
 *   GET / HEAD / OPTIONS   read-only (and so idempotent, not destructive)
 *   POST                   destructive, not idempotent -- additive only on
 *                          positive evidence (addsOnly)
 *   PUT                    replaces what is there: destructive, idempotent
 *   DELETE                 destructive, idempotent
 *   PATCH, anything else   destructive, not idempotent -- the cautious answer
 *                          when the method does not say
 */
function toolHintsFor(method, segments, evidence) {
    const m = String(method || '').toUpperCase();
    if (SAFE_METHODS.has(m)) return { readOnly: true, destructive: false, idempotent: true };
    if (m === 'POST') return { readOnly: false, destructive: !addsOnly(segments, evidence), idempotent: false };
    if (m === 'PUT' || m === 'DELETE') return { readOnly: false, destructive: true, idempotent: true };
    return { readOnly: false, destructive: true, idempotent: false };
}

/**
 * One GraphQL endpoint carries queries and mutations alike, and which one a
 * call is cannot be known when the tool is listed. So: not read-only, possibly
 * destructive, not idempotent.
 */
function graphQLHints() {
    return { readOnly: false, destructive: true, idempotent: false };
}

/**
 * A name that is too long, cut to fit with a short hash of the WHOLE name in
 * place of what was cut -- so two names that differ only past the cut stay
 * different, and the same endpoint gets the same name on every run.
 */
function capToolName(name) {
    if (name.length <= MAX_TOOL_NAME_LENGTH) return name;
    const hash = crypto.createHash('sha256').update(name).digest('hex').slice(0, NAME_HASH_LENGTH);
    const kept = name.slice(0, MAX_TOOL_NAME_LENGTH - NAME_HASH_LENGTH - 1).replace(/_+$/, '');
    return kept + '_' + hash;
}

/** GetUsersByIdTripsAsync -> get_users_by_id_trips, capped at MAX_TOOL_NAME_LENGTH. */
function toolNameFor(methodName) {
    return capToolName(String(methodName)
        .replace(/Async$/, '')
        .replace(/([a-z0-9])([A-Z])/g, '$1_$2')
        .replace(/([A-Z]+)([A-Z][a-z])/g, '$1_$2')
        .toLowerCase());
}

/**
 * The tool surface of a wrapper: one tool per generated client method that is
 * not left out (reasonLeftOut), in the order the client declares them (GraphQL
 * first, then the REST endpoints).
 *
 * @param {object}   args
 * @param {object[]} args.patterns        REST endpoint patterns from dedupePatterns().
 * @param {boolean}  args.hasGraphQL      The capture had GraphQL traffic.
 * @param {function} args.methodNameFor   generate-wrapper.js's client method namer.
 * @returns {{name: string, methodName: string, hints: object, pattern: (object|null)}[]}
 */
function listTools(args) {
    const { patterns, hasGraphQL, methodNameFor } = args;
    const tools = [];
    if (hasGraphQL) {
        tools.push({ name: GRAPHQL_TOOL_NAME, methodName: GRAPHQL_METHOD_NAME, hints: graphQLHints(), pattern: null });
    }
    for (const p of patterns) {
        if (reasonLeftOut(p)) continue;
        const methodName = methodNameFor(p);
        const hints = toolHintsFor(p.method, p.segments, { isCollection: isCollection(p, patterns) });
        tools.push({ name: toolNameFor(methodName), methodName, hints, pattern: p });
    }
    // A tool name must be unique on the wire. Two client methods that differ
    // only in casing (GetHTMLAsync, GetHtmlAsync) would otherwise share one.
    // The number that tells them apart has to fit inside the cap as well.
    const taken = new Set();
    for (const t of tools) {
        let name = t.name;
        for (let n = 2; taken.has(name); n++) {
            const suffix = '_' + n;
            name = t.name.slice(0, MAX_TOOL_NAME_LENGTH - suffix.length) + suffix;
        }
        taken.add(name);
        t.name = name;
    }
    return tools;
}

/**
 * The endpoints listTools() gives no tool, each with the reason, in the order
 * the client declares them. Every endpoint is in one list or the other.
 *
 * @param {object}   args
 * @param {object[]} args.patterns        REST endpoint patterns from dedupePatterns().
 * @param {function} args.methodNameFor   generate-wrapper.js's client method namer.
 * @returns {{methodName: string, pattern: object, reason: string}[]}
 */
function listLeftOut(args) {
    const { patterns, methodNameFor } = args;
    const leftOut = [];
    for (const p of patterns) {
        const reason = reasonLeftOut(p);
        if (reason) leftOut.push({ methodName: methodNameFor(p), pattern: p, reason });
    }
    return leftOut;
}

// -------------------- Descriptions --------------------

// What a response key has to look like to be put in front of a model as a
// field name. A description is text the model reads, and a key is whatever the
// site sent: an object keyed by DATA (e-mail addresses, titles, free text) would
// otherwise put that data, verbatim, into the description.
const FIELD_NAME = /^[A-Za-z_][A-Za-z0-9_]{0,40}$/;
const MAX_FIELDS_OF_A_RECORD = 40;

function fieldList(shape) {
    const keys = Object.keys(shape.fields);
    const names = keys.filter((k) => FIELD_NAME.test(k)).sort();
    // A map, not a record: more keys than a record has, or keys that are mostly
    // not field names. Nothing is said about its fields, including the few keys
    // that happen to read as names.
    if (keys.length > MAX_FIELDS_OF_A_RECORD || names.length * 2 < keys.length) return '';
    if (names.length === 0) return '';
    const shown = names.slice(0, MAX_FIELDS_IN_DESCRIPTION).join(', ');
    const rest = names.length - MAX_FIELDS_IN_DESCRIPTION;
    return rest > 0 ? shown + ', and ' + rest + ' more' : shown;
}

/** " Returns an object with: id, name." -- or nothing, when the capture did not show a shape. */
function returnsSentence(shape) {
    if (!shape) return '';
    if (shape.kind === 'object') {
        const fields = fieldList(shape);
        return fields ? ' Returns an object with: ' + fields + '.' : '';
    }
    if (shape.kind === 'array') {
        const el = shape.element;
        if (el && el.kind === 'object') {
            const fields = fieldList(el);
            return fields ? ' Returns a list of objects with: ' + fields + '.' : ' Returns a list.';
        }
        return ' Returns a list.';
    }
    return '';
}

/** What calling a tool that is not read-only does to the site, in the words of its hints. */
function effectSentence(hints) {
    if (hints.readOnly) return '';
    if (!hints.destructive) return ' Adds to the data on the site; calling it again adds again.';
    return hints.idempotent
        ? ' Changes or removes data on the site; repeating the same call changes nothing more.'
        : ' May change or remove data on the site.';
}

/**
 * What the model reads to choose a tool: what it does, the request it sends,
 * what comes back, and what it does to the site.
 */
function toolDescriptionFor(tool, ctx) {
    if (!tool.pattern) {
        return 'Runs a GraphQL query or mutation against ' + ctx.baseUrl + ' and returns the response. ' +
            'A query reads; a mutation can change or remove data on the site.';
    }
    const p = tool.pattern;
    // The client summarizes every POST as "Creates ...". That is true only of
    // one that adds; for the rest the request line below says what is known.
    const summary = String(p.method).toUpperCase() === 'POST' && tool.hints.destructive ? '' : ctx.describeFor(p) + ' ';
    return summary +
        'Sends ' + String(p.method).toUpperCase() + ' ' + ctx.patternPath(p) + ' to ' + ctx.baseUrl + '.' +
        returnsSentence(p.returnShape) +
        effectSentence(tool.hints);
}

// -------------------- C# rendering --------------------

/** A C# string literal's contents: quotes, backslashes and control characters escaped. */
function csString(s) {
    let out = '';
    for (const c of String(s)) {
        const code = c.codePointAt(0);
        if (c === '\\') out += '\\\\';
        else if (c === '"') out += '\\"';
        // Control characters, and the two Unicode line separators a C# string
        // literal may not contain either.
        else if (code < 0x20 || code === 0x7f || code === 0x2028 || code === 0x2029) {
            out += '\\u' + code.toString(16).padStart(4, '0');
        }
        else out += c;
    }
    return out;
}

function boolCs(b) { return b ? 'true' : 'false'; }

function toolAttribute(tool) {
    const h = tool.hints;
    return '[McpServerTool(Name = "' + csString(tool.name) + '"' +
        ', ReadOnly = ' + boolCs(h.readOnly) +
        ', Destructive = ' + boolCs(h.destructive) +
        ', Idempotent = ' + boolCs(h.idempotent) +
        ', OpenWorld = true)]';
}

function renderGraphQLTool(tool, ctx) {
    return [
        '    ' + toolAttribute(tool),
        '    [Description("' + csString(toolDescriptionFor(tool, ctx)) + '")]',
        '    public Task<JsonElement> ' + tool.methodName + '(',
        '        [Description("The GraphQL document to run: a query or a mutation.")] string query,',
        '        [Description("The document\'s variables, as a JSON object. Omit it when the document takes none.")] JsonElement? variables = null,',
        '        CancellationToken ct = default)',
        '        => _session.RunAsync(client => client.' + tool.methodName + '<JsonElement>(query, variables, ct));',
    ];
}

function renderRestTool(tool, ctx) {
    const p = tool.pattern;
    const verb = String(p.method || 'GET').toUpperCase();
    const acceptsBody = verb === 'POST' || verb === 'PUT' || verb === 'PATCH';
    const pathParams = p.segments.filter((s) => s.param);
    const route = ctx.patternPath(p);

    const parameters = pathParams.map((s) =>
        '[Description("The ' + csString(s.param) + ' in ' + csString(route) + '.")] ' + s.type + ' ' + s.param);
    if (acceptsBody) {
        parameters.push('[Description("The JSON request body, as text. Omit it to send no body.")] string? body = null');
    }
    // The endpoint's query parameters (issue #34), described and declared by
    // query-emit.js, in the order the client method declares them.
    for (const q of p.queryParameters || []) parameters.push(q.toolDeclaration);
    parameters.push('CancellationToken ct = default');

    const callArgs = pathParams.map((s) => s.param);
    if (acceptsBody) callArgs.push('body');
    for (const q of p.queryParameters || []) callArgs.push(q.csName);
    callArgs.push('ct');

    const lines = [
        '    ' + toolAttribute(tool),
        '    [Description("' + csString(toolDescriptionFor(tool, ctx)) + '")]',
        '    public Task<' + (p.responseModel || 'JsonElement') + '> ' + tool.methodName + '(',
    ];
    parameters.forEach((param, i) => {
        lines.push('        ' + param + (i < parameters.length - 1 ? ',' : ')'));
    });
    lines.push('        => _session.RunAsync(client => client.' + tool.methodName + '(' + callArgs.join(', ') + '));');
    return lines;
}

/**
 * Render <Name>Tools.Generated.cs.
 *
 * @param {object}   args
 * @param {string}   args.header       The auto-generated banner.
 * @param {object}   args.opts         projectName, namespace, baseUrl.
 * @param {object[]} args.tools        From listTools().
 * @param {function} args.describeFor  generate-wrapper.js's one-line summary of an endpoint.
 * @param {function} args.patternPath  generate-wrapper.js's path template of an endpoint.
 */
function emitToolsFile(args) {
    const { header, opts, tools, describeFor, patternPath } = args;
    const name = opts.projectName;
    const ctx = { baseUrl: opts.baseUrl, describeFor, patternPath };
    const lines = [];
    lines.push(header);
    lines.push('using System.ComponentModel;');
    lines.push('using System.Text.Json;');
    lines.push('using ModelContextProtocol.Server;');
    lines.push('');
    lines.push('namespace ' + opts.namespace + ';');
    lines.push('');
    lines.push('/// <summary>');
    lines.push('/// The MCP tools of the ' + name + ' server: one for each method the capture gave');
    lines.push('/// <see cref="' + name + 'Client"/>, less the sign-in, OPTIONS/HEAD and non-JSON endpoints');
    lines.push('/// the README lists as left out. McpProgram.cs registers them with');
    lines.push('/// <c>WithToolsFromAssembly()</c>, which looks for exactly these two attributes --');
    lines.push('/// <c>[McpServerToolType]</c> on the class and <c>[McpServerTool]</c> on each method.');
    lines.push('/// Without them the server starts and lists no tools.');
    lines.push('/// </summary>');
    lines.push('/// <remarks>');
    lines.push('/// Each tool\'s read-only / destructive / idempotent hints are a first draft from');
    lines.push('/// the endpoint\'s HTTP method and path, and its description from the request');
    lines.push('/// and the captured response. Neither knows what the site actually does, so');
    lines.push('/// review them before relying on a client that acts on hints without asking.');
    lines.push('/// A tool of your own goes in another file: this class is partial, and the');
    lines.push('/// generator writes only this one.');
    lines.push('/// </remarks>');
    lines.push('[McpServerToolType]');
    lines.push('public sealed partial class ' + name + 'Tools');
    lines.push('{');
    lines.push('    private readonly ' + name + 'ToolSession _session;');
    lines.push('');
    lines.push('    public ' + name + 'Tools(' + name + 'ToolSession session)');
    lines.push('    {');
    lines.push('        _session = session ?? throw new ArgumentNullException(nameof(session));');
    lines.push('    }');
    for (const tool of tools) {
        lines.push('');
        const rendered = tool.pattern ? renderRestTool(tool, ctx) : renderGraphQLTool(tool, ctx);
        for (const l of rendered) lines.push(l);
    }
    lines.push('}');
    lines.push('');
    return lines.join('\n');
}

module.exports = {
    listTools,
    listLeftOut,
    isSignInSegment,
    wordsOf,
    toolNameFor,
    toolHintsFor,
    graphQLHints,
    toolDescriptionFor,
    emitToolsFile,
    csString,
};
