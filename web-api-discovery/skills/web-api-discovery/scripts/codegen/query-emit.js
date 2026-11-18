// scripts/codegen/query-emit.js -- how a wrapper's query parameters are written down.
//
// query-params.js decides what an endpoint's query parameters are and what each
// defaults to. This module renders that decision everywhere it has to appear,
// from one description of it, so the client method, the MCP tool, the README
// and the tests cannot disagree:
//
//   describeQueryParameters()  the words a model reads, and the C# declarations
//   clientQueryLines()         the client method's body: the pairs it sends
//   queryReadmeSection()       the README table, and the note on what is never baked in
//   emitQueryTestsFile()       xUnit facts asserting the exact query on the wire
//   expectedQueryNames()       what the smoke test expects in a tool's inputSchema
//
// Zero npm deps. Deterministic: same parameters in, same bytes out.

'use strict';

const { csString } = require('./mcp-tools-emit.js');
const { wireQuery } = require('./query-params.js');

const MAX_SHOWN_VALUE_LENGTH = 60;

function shorten(value) {
    const text = String(value);
    return text.length > MAX_SHOWN_VALUE_LENGTH ? text.slice(0, MAX_SHOWN_VALUE_LENGTH) + '...' : text;
}

// C# ends a line at U+0085, U+2028 and U+2029 as well as CR and LF, and a
// capture is text somebody else wrote. JSON.stringify escapes the C0 controls
// and nothing above them, so these are spelled out too: a description is also
// written into a `///` comment, where a raw line end would hand the rest of it
// to the compiler as code. Built from character codes so that this file never
// contains one of them itself.
const LINE_SEPARATOR = String.fromCharCode(0x2028);
const PARAGRAPH_SEPARATOR = String.fromCharCode(0x2029);
const UNPRINTABLE_RE = new RegExp('[\\x7f-\\x9f' + LINE_SEPARATOR + PARAGRAPH_SEPARATOR + ']', 'g');

function spelledOut(c) {
    return '\\u' + c.charCodeAt(0).toString(16).padStart(4, '0');
}

/** A captured name or value in quotes, with nothing in it that is not printable: `"wire cutter"`, `""`. */
function quoted(value) {
    return JSON.stringify(shorten(value)).replace(UNPRINTABLE_RE, spelledOut);
}

/** `"11"`, `"KEYWORD", "WIDGET"`, `""` -- quoted so an empty value and a space are visible. */
function quotedList(values) {
    return values.map(quoted).join(', ');
}

function capturedCalls(count) {
    return count === 2 ? 'Both captured calls' : 'All ' + count + ' captured calls';
}

const NEVER_BAKED_IN = {
    scrubbed: 'The captured value is not baked in: the scrub replaced it, or it is the kind of value the scrub ' +
        'replaces (a session id, a token, personal data). Left out of the request unless you supply a current one.',
    nonce: 'In the capture it looked like a per-request timestamp or nonce, so no value is baked in. ' +
        'Left out of the request unless you supply one.',
    // A different long number or token on every call is not baked in either --
    // but it may just as well be the id or the cursor the caller has to
    // choose. So this says what was seen, and does not tell a model the
    // parameter is noise it should leave alone.
    differsEveryCall: 'Every captured call that sent it sent a different long number or token -- an id, a cursor, ' +
        'a timestamp or a nonce -- so no value is baked in. Left out of the request unless you supply the one you mean.',
};

function neverBakedIn(q) {
    if (q.kind === 'nonce' && q.reason === 'differs-every-call') return NEVER_BAKED_IN.differsEveryCall;
    return NEVER_BAKED_IN[q.kind];
}

/**
 * What a caller -- a model reading the tool's input schema, or a developer
 * reading the client method -- is told about one query parameter: its name on
 * the wire, what the captured calls sent, and what happens when it is omitted.
 * Observed values are quoted as examples and called examples. A scrubbed or
 * nonce value is never quoted at all.
 */
function describe(q) {
    const lead = 'The ' + quoted(q.wireName) + ' query parameter' + (q.repeated ? ', sent once per value' : '') + '.';
    if (q.kind === 'scrubbed' || q.kind === 'nonce') return lead + ' ' + neverBakedIn(q);

    if (q.kind === 'varying') {
        const sentIn = q.sentIn < q.calls ? ' (' + q.sentIn + ' of them sent it)' : '';
        const examples = q.examples.length
            ? ' Examples from the capture, not the only values: ' + quotedList(q.examples) + '.'
            : '';
        return lead + ' It varied across the ' + q.calls + ' captured calls' + sentIn + '.' + examples +
            ' Left out of the request unless you supply it.';
    }

    const override = q.repeated
        ? ' Pass a list to send those instead, or an empty list to send none.'
        : ' Pass another value to send that instead, or null to leave it out.';
    const observed = q.kind === 'single'
        ? ' The one captured call sent ' + quotedList(q.defaults) + ', so that is what is sent when this is omitted; ' +
            'one call is no evidence of what else the site accepts.'
        : ' ' + capturedCalls(q.calls) + ' sent ' + quotedList(q.defaults) + ', so that is what is sent when this is omitted.';
    return lead + observed + override;
}

/**
 * A C# string literal for a captured name or value. csString() escapes quotes,
 * backslashes, the C0 controls and U+2028 / U+2029; U+0085 (NEL) is a C# line
 * end too, and the rest of the C1 controls have no business in source either.
 */
function csLiteral(value) {
    return '"' + csString(value).replace(UNPRINTABLE_RE, spelledOut) + '"';
}

/**
 * The parameters of collectQueryParameters(), with what the emitters write:
 *
 *   description       the words above
 *   csType            `string?`, or `IReadOnlyList<string>?` when a call repeated it
 *   csDefault         the C# default: a string literal, or `null`
 *   declaration       `string? limit = "11"` -- the client method's parameter
 *   toolDeclaration   the same behind its [Description] -- the tool method's parameter
 *
 * Every query parameter is a string: the wire is text, and a typed parameter
 * would re-spell it (`True` for `true`, `1` for `01`).
 *
 * A repeated parameter cannot carry its values as a C# default (a default must
 * be a constant), so it defaults to null and the client method sends the
 * captured values for null -- see clientQueryLines().
 */
function describeQueryParameters(parameters) {
    return (parameters || []).map((q) => {
        const csType = q.repeated ? 'IReadOnlyList<string>?' : 'string?';
        const csDefault = (!q.repeated && q.defaults) ? csLiteral(q.defaults[0]) : 'null';
        const description = describe(q);
        const declaration = csType + ' ' + q.csName + ' = ' + csDefault;
        return Object.assign({}, q, {
            description,
            csType,
            csDefault,
            declaration,
            toolDeclaration: '[Description("' + csString(description) + '")] ' + declaration,
        });
    });
}

// -------------------- The client method --------------------

/** The local the client method collects its query pairs in. Reserved in query-params.js. */
const QUERY_LOCAL = 'wireQuery';

/**
 * The lines of a client method that build its query, in wire order. Empty when
 * the endpoint has no query parameters: such a method is emitted as it always
 * was, with `query: null`.
 */
function clientQueryLines(parameters) {
    if (!parameters || parameters.length === 0) return [];
    const lines = ['        var ' + QUERY_LOCAL + ' = new List<KeyValuePair<string, string?>>();'];
    for (const q of parameters) {
        const captured = (q.repeated && q.defaults)
            ? ' ?? new[] { ' + q.defaults.map(csLiteral).join(', ') + ' }'
            : '';
        lines.push('        AddQuery(' + QUERY_LOCAL + ', ' + csLiteral(q.wireName) + ', ' + q.csName + captured + ');');
    }
    return lines;
}

/** The two helpers those lines call. Emitted once, and only into a client that has a query to send. */
function clientQueryHelperLines() {
    return [
        '    /// <summary>Adds one query parameter. A null value is left out; an empty one is sent as <c>name=</c>.</summary>',
        '    private static void AddQuery(List<KeyValuePair<string, string?>> query, string name, string? value)',
        '    {',
        '        if (value is not null) query.Add(new(name, value));',
        '    }',
        '',
        '    /// <summary>Adds a repeated query parameter: once per value, in order. Null adds nothing.</summary>',
        '    private static void AddQuery(List<KeyValuePair<string, string?>> query, string name, IEnumerable<string?>? values)',
        '    {',
        '        if (values is null) return;',
        '        foreach (var value in values) AddQuery(query, name, value);',
        '    }',
    ];
}

// -------------------- What reaches the wire --------------------

/** The pairs a call sends when the caller supplies no query argument: the captured defaults. */
function defaultPairs(parameters) {
    const pairs = [];
    for (const q of parameters) {
        for (const value of q.defaults || []) pairs.push([q.wireName, value]);
    }
    return pairs;
}

// A value that needs every kind of escaping a query value can need: a space,
// the separators, a slash, a plus and a non-ASCII letter (e-acute, U+00E9,
// built from its code so this file stays ASCII).
const PROBE_SUFFIX = ' &=/+' + String.fromCharCode(0xe9);

/**
 * A distinct, awkward value for every parameter, to call a generated method
 * with: `p1 &=/+` and an e-acute for the first, and two values for a repeated
 * one. Distinct, so two arguments passed in the wrong order cannot produce the
 * right query.
 */
function probeArguments(parameters) {
    return parameters.map((q, i) => {
        const probe = 'p' + (i + 1) + PROBE_SUFFIX;
        return { parameter: q, values: q.repeated ? [probe, 'p' + (i + 1) + 'b'] : [probe] };
    });
}

function probePairs(probes) {
    const pairs = [];
    for (const { parameter, values } of probes) {
        for (const value of values) pairs.push([parameter.wireName, value]);
    }
    return pairs;
}

function probeCallArguments(probes) {
    return probes.map(({ parameter, values }) => parameter.csName + ': ' +
        (parameter.repeated ? 'new[] { ' + values.map(csLiteral).join(', ') + ' }' : csLiteral(values[0])));
}

// -------------------- Generated tests --------------------

function placeholderFor(type) {
    return type === 'int' || type === 'long' ? '1' : '"abc"';
}

/** A body the method's response type can be read from, whatever the capture's fixtures hold. */
function cannedResponseFor(pattern) {
    const isList = !pattern.envelope && /^IReadOnlyList</.test(pattern.responseModel || '');
    return isList ? '[]' : '{}';
}

/**
 * The endpoints that take query parameters, in the order the client declares
 * them, each with its client method's name and its tool -- or null when it has
 * none. Every endpoint has a client method; not every one has a tool
 * (mcp-tools-emit.js leaves out sign-in paths, OPTIONS/HEAD and endpoints the
 * capture showed no JSON response from). The query parameters belong to the
 * endpoint, so they are on the client method either way, and whatever is
 * written about them must not assume a tool.
 */
function endpointsWithQuery(args) {
    const { patterns, tools, methodNameFor } = args;
    return (patterns || [])
        .filter((p) => (p.queryParameters || []).length > 0)
        .map((p) => ({
            pattern: p,
            methodName: methodNameFor(p),
            tool: (tools || []).find((t) => t.pattern === p) || null,
        }));
}

function queryFactsFor(endpoint, names) {
    const p = endpoint.pattern;
    const methodName = endpoint.methodName;
    const parameters = p.queryParameters;
    const pathArgs = p.segments.filter((s) => s.param).map((s) => placeholderFor(s.type));
    const probes = probeArguments(parameters);
    const supplied = pathArgs.concat(probeCallArguments(probes)).join(', ');
    const byDefault = csLiteral(wireQuery(defaultPairs(parameters)));
    const whenSupplied = csLiteral(wireQuery(probePairs(probes)));
    const canned = csLiteral(cannedResponseFor(p));

    // Uri.Query, not PathAndQuery: the query is escaped exactly as the client
    // built it, while .NET rewrites a PATH it considers equivalent (`%7E` to
    // `~`, `%c3` to `%C3`), which a captured path may well contain.
    const fact = (suffix, target, args, expected) => [
        '    [Fact]',
        '    public async Task ' + methodName + '_' + suffix + '()',
        '    {',
        '        var sent = new List<Uri>();',
        '        using var http = MockHandler.FromJson(' + canned + ', sent);',
        ...target.setup,
        '        await ' + target.call + '.' + methodName + '(' + args + ');',
        '        Assert.Equal(' + expected + ', Assert.Single(sent).Query);',
        '    }',
    ].join('\n');

    const client = {
        setup: ['        using var client = new ' + names.client + '(SignedIn, http);'],
        call: 'client',
    };
    const viaTool = {
        setup: ['        using var session = new ' + names.toolSession + '(new SavedSession(), credentials => new ' + names.client + '(credentials, http));'],
        call: 'new ' + names.tools + '(session)',
    };
    const facts = [
        fact('SendsTheCapturedQueryWhenNoneIsSupplied', client, pathArgs.join(', '), byDefault),
        fact('SendsEveryQueryParameterTheCallerSupplies', client, supplied, whenSupplied),
    ];
    // Only an endpoint that has a tool has a tool method to call.
    if (endpoint.tool) {
        facts.push(fact('SendsTheCapturedQueryThroughTheToolWhenNoneIsSupplied', viaTool, pathArgs.join(', '), byDefault));
        facts.push(fact('SendsEveryQueryParameterThroughTheTool', viaTool, supplied, whenSupplied));
    }
    return facts;
}

function hasQuery(tool) {
    return !!(tool.pattern && tool.pattern.queryParameters && tool.pattern.queryParameters.length > 0);
}

/**
 * Render QueryStringTests.cs: for every endpoint that takes query parameters,
 * the exact query string that reaches the HTTP handler -- with no query
 * argument supplied (the captured defaults) and with every one supplied, from
 * the client, and through the MCP tool class when the endpoint has a tool.
 *
 * The expected strings are computed here from the wire names and values, not
 * read back from the emitted client: a client that stops sending its query
 * still has these to fail against.
 *
 * @param {object}   args
 * @param {object}   args.opts           projectName, namespace.
 * @param {object[]} args.patterns       The endpoints, with queryParameters described.
 * @param {object[]} args.tools          From listTools(): the endpoints that have a tool.
 * @param {function} args.methodNameFor  generate-wrapper.js's client method namer.
 * @returns {string|null} The file, or null when no endpoint takes a query parameter.
 */
function emitQueryTestsFile(args) {
    const { opts } = args;
    const withQuery = endpointsWithQuery(args);
    if (withQuery.length === 0) return null;
    const name = opts.projectName;
    const names = { client: name + 'Client', tools: name + 'Tools', toolSession: name + 'ToolSession' };
    const facts = [];
    for (const endpoint of withQuery) facts.push(...queryFactsFor(endpoint, names));
    return [
        '// <auto-generated>',
        '//   Emitted by web-api-discovery (scripts/codegen/query-emit.js).',
        '//   The query string each endpoint puts on the wire. For every endpoint that',
        '//   takes query parameters: what is sent when the caller supplies none (the',
        '//   captured defaults) and when the caller supplies every one -- from the',
        '//   client, and again through the MCP tool class when the endpoint has a tool.',
        '//   (An endpoint the README lists as left out of the tools has a client method',
        '//   and no tool, so it has the client\'s two facts only.) Each asserts the exact',
        '//   query string the HTTP handler received, so the names, the order, the',
        '//   encoding, repeated parameters and empty values are all pinned.',
        '//',
        '//   The expected strings come from the captured endpoints, not from the client',
        '//   that is being tested: re-run generate-wrapper.js to refresh them.',
        '// </auto-generated>',
        '#nullable enable',
        '',
        'using System.Net.Http;',
        'using Xunit;',
        'using ' + opts.namespace + ';',
        'using ' + opts.namespace + '.Auth;',
        '',
        'namespace ' + opts.namespace + '.Tests;',
        '',
        'public class ' + name + 'QueryStringTests',
        '{',
        '    private static readonly SessionCredentials SignedIn = new("test=1", string.Empty);',
        '',
        '    /// <summary>A session store that already holds a session, for calls made through the tool class.</summary>',
        '    private sealed class SavedSession : ISessionStore',
        '    {',
        '        public string Description => "a saved session, in memory";',
        '',
        '        public bool TryLoad(out SessionCredentials credentials)',
        '        {',
        '            credentials = SignedIn;',
        '            return true;',
        '        }',
        '',
        '        public void Save(SessionCredentials credentials) { }',
        '',
        '        public void Clear() { }',
        '    }',
        '',
        facts.join('\n\n'),
        '}',
        '',
    ].join('\n');
}

/**
 * The query argument a README recipe shows for an endpoint: its first varying
 * parameter, set to the first value the capture showed for it -- `prefix: "w"`.
 * Empty when nothing varied; the call then reads as it always did.
 */
function recipeQueryArguments(pattern) {
    const varying = (pattern.queryParameters || []).find((q) => q.kind === 'varying' && q.examples.length > 0);
    if (!varying) return [];
    return [varying.csName + ': ' + (varying.repeated ? 'new[] { ' + csLiteral(varying.examples[0]) + ' }' : csLiteral(varying.examples[0]))];
}

/** The tool property names the smoke test expects in a tool's inputSchema, in order. */
function expectedQueryNames(tool) {
    return hasQuery(tool) ? tool.pattern.queryParameters.map((q) => q.toolName) : [];
}

// -------------------- README --------------------

function mdCodeSpan(text) {
    const cell = text.replace(/\|/g, '\\|');
    return cell.includes('`') ? '`` ' + cell + ' ``' : '`' + cell + '`';
}

/** A captured value as a Markdown code span inside a table cell: `"11"`, `""`. */
function mdCode(value) {
    return mdCodeSpan(quoted(value));
}

/** A name as a code span. An ordinary name is shown as it is; anything in it that could break the row is spelled out. */
function mdName(name) {
    if (name === '') return '(an empty name)';
    return mdCodeSpan(quoted(name).slice(1, -1));
}

// Why a captured value was not baked in, for the person reviewing the README.
// The reasons are scrubReasonFor()'s (query-params.js).
const SCRUB_REASON_TEXT = {
    'scrub-marker': 'the scrub replaced the captured value',
    'scrub-table': 'the scrub replaced the captured value',
    'secret-name': 'its name is one the scrub policy treats as a secret',
    'secret-shape': 'the captured value has a shape the scrub replaces (a token, an id, an email)',
};

function whenOmitted(q) {
    if (q.kind === 'scrubbed') {
        return '**left out, never baked in**: ' + (SCRUB_REASON_TEXT[q.reason] || SCRUB_REASON_TEXT['scrub-marker']);
    }
    if (q.kind === 'nonce') {
        return '**left out, never baked in**: ' + (q.reason === 'differs-every-call'
            ? 'a different long number or token on every call (an id, a cursor, a timestamp or a nonce)'
            : 'a per-request timestamp or nonce');
    }
    if (q.kind === 'varying') {
        const examples = q.examples.length ? ' (for example ' + q.examples.map(mdCode).join(', ') + ')' : '';
        return 'left out: it varied' + examples;
    }
    const why = q.kind === 'single' ? 'the one captured call did' : 'every captured call did';
    return 'sends ' + q.defaults.map(mdCode).join(', ') + ' (' + why + ')';
}

/**
 * The README's "Query parameters" section: every endpoint's query parameters,
 * what each is called on the wire, and what is sent when the caller omits it --
 * with the parameters that are never baked in marked, and the reason given. An
 * endpoint is listed by its tool, or by its client method when it has no tool.
 *
 * @param {object}   args
 * @param {object}   args.opts           projectName.
 * @param {object[]} args.patterns       The endpoints, with queryParameters described.
 * @param {object[]} args.tools          From listTools(): the endpoints that have a tool.
 * @param {function} args.methodNameFor  generate-wrapper.js's client method namer.
 * @returns {string} The section, or '' when no endpoint takes a query parameter.
 */
function queryReadmeSection(args) {
    const { opts } = args;
    const withQuery = endpointsWithQuery(args);
    if (withQuery.length === 0) return '';
    const lines = [
        '### Query parameters',
        '',
        'Each client method takes the query parameters the capture showed for its endpoint, and so does',
        'its tool. An endpoint that is left out of the tools keeps them on its client method, and is listed',
        'here by that method. Every parameter is optional. What is sent when a caller supplies nothing comes',
        'from what the captured calls did:',
        '',
        '| Tool | Parameter | On the wire | Sent in | When omitted |',
        '|---|---|---|---|---|',
    ];
    for (const endpoint of withQuery) {
        const listedAs = endpoint.tool ? '`' + endpoint.tool.name + '`' : 'no tool (`' + endpoint.methodName + '`)';
        for (const q of endpoint.pattern.queryParameters) {
            lines.push('| ' + listedAs + ' | ' + mdName(q.toolName) + ' | ' + mdName(q.wireName) + (q.repeated ? ' (repeated)' : '') +
                ' | ' + q.sentIn + ' of ' + q.calls + ' | ' + whenOmitted(q) + ' |');
        }
    }
    lines.push('');
    lines.push('- **Sent as captured.** A value every captured call sent is the default, and so is every value of');
    lines.push('  an endpoint that was captured only once: one call is no evidence of what varies. A default is');
    lines.push('  what the capture showed, not what the site requires. Pass another value to override it, or');
    lines.push('  `null` to leave the parameter out.');
    lines.push('- **Left out.** A parameter whose value varied from call to call -- a search term, a page number,');
    lines.push('  a filter -- has no default. The examples are values the capture happened to contain.');
    lines.push('- **Never baked in.** A value the scrub replaced (a session id, a token, personal data) is never');
    lines.push('  written into the generated code, and neither is one that looked like a per-request timestamp or');
    lines.push('  nonce (a cache-buster such as `_=1700000000000`), or that was a different long number or token on');
    lines.push('  every call -- which may be a nonce, or the id or cursor a caller has to choose. Those parameters');
    lines.push('  are left out unless the caller supplies a value. If the site refuses a call without one, pass a');
    lines.push('  current value at run time; do not paste one into the source.');
    lines.push('');
    lines.push('A parameter keeps its exact name on the wire. Its C# and tool name is that name in camelCase');
    lines.push('(`page-size` is `pageSize`), and values are percent-encoded when the request is built.');
    lines.push('`tests/' + opts.projectName + '.Tests/QueryStringTests.cs` asserts the exact query string each endpoint sends, and');
    lines.push('`pester/Mcp.Tests.ps1` that the running server lists a tool\'s parameters in that tool\'s input schema.');
    return lines.join('\n');
}

module.exports = {
    describeQueryParameters,
    clientQueryLines,
    clientQueryHelperLines,
    emitQueryTestsFile,
    expectedQueryNames,
    recipeQueryArguments,
    queryReadmeSection,
    defaultPairs,
    QUERY_LOCAL,
};
