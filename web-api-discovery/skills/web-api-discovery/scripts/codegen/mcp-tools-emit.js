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

const GRAPHQL_TOOL_NAME = 'graphql';
const GRAPHQL_METHOD_NAME = 'GraphQLAsync';

// HTTP's safe methods: a request made with one asks for nothing to change.
const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

// A POST is taken to ADD something unless its path says it changes or removes
// what is already there -- POST /items/42/delete is not an additive call. The
// words are matched against whole words of the literal path segments. Erring
// this way round is deliberate: a tool wrongly called destructive costs a
// confirmation, one wrongly called additive can skip it.
const CHANGING_WORDS = new Set([
    // removes
    'delete', 'remove', 'destroy', 'cancel', 'revoke', 'purge', 'clear',
    'reset', 'erase', 'archive', 'deactivate', 'disable', 'unsubscribe',
    // overwrites
    'update', 'edit', 'replace', 'overwrite', 'rename', 'move', 'set', 'patch',
]);

const MAX_FIELDS_IN_DESCRIPTION = 10;

/** Words of a path segment: `cancelOrder`, `cancel-order`, `cancel_order` -> cancel, order. */
function wordsOf(segment) {
    return String(segment)
        .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
        .split(/[^A-Za-z0-9]+/)
        .filter(Boolean)
        .map((w) => w.toLowerCase());
}

function namesAChange(segments) {
    for (const s of segments || []) {
        if (!s.literal) continue;
        if (wordsOf(s.literal).some((w) => CHANGING_WORDS.has(w))) return true;
    }
    return false;
}

/**
 * The hints a client reads to decide whether a call needs confirming, from the
 * endpoint's HTTP method and path.
 *
 *   GET / HEAD / OPTIONS   read-only (and so idempotent, not destructive)
 *   POST                   additive, not idempotent -- destructive when the
 *                          path names a change or removal
 *   PUT                    replaces what is there: destructive, idempotent
 *   DELETE                 destructive, idempotent
 *   PATCH, anything else   destructive, not idempotent -- the cautious answer
 *                          when the method does not say
 */
function toolHintsFor(method, segments) {
    const m = String(method || '').toUpperCase();
    if (SAFE_METHODS.has(m)) return { readOnly: true, destructive: false, idempotent: true };
    if (m === 'POST') return { readOnly: false, destructive: namesAChange(segments), idempotent: false };
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

/** GetUsersByIdTripsAsync -> get_users_by_id_trips. */
function toolNameFor(methodName) {
    return String(methodName)
        .replace(/Async$/, '')
        .replace(/([a-z0-9])([A-Z])/g, '$1_$2')
        .replace(/([A-Z]+)([A-Z][a-z])/g, '$1_$2')
        .toLowerCase();
}

/**
 * The tool surface of a wrapper: one tool per generated client method, in the
 * order the client declares them (GraphQL first, then the REST endpoints).
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
        const methodName = methodNameFor(p);
        tools.push({ name: toolNameFor(methodName), methodName, hints: toolHintsFor(p.method, p.segments), pattern: p });
    }
    // A tool name must be unique on the wire. Two client methods that differ
    // only in casing (GetHTMLAsync, GetHtmlAsync) would otherwise share one.
    const seen = new Map();
    for (const t of tools) {
        const count = (seen.get(t.name) || 0) + 1;
        seen.set(t.name, count);
        if (count > 1) t.name = t.name + '_' + count;
    }
    return tools;
}

// -------------------- Descriptions --------------------

function fieldList(shape) {
    const names = Object.keys(shape.fields).sort();
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
        : ' Changes or removes data on the site.';
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
    return ctx.describeFor(p) +
        ' Sends ' + String(p.method).toUpperCase() + ' ' + ctx.patternPath(p) + ' to ' + ctx.baseUrl + '.' +
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
    parameters.push('CancellationToken ct = default');

    const callArgs = pathParams.map((s) => s.param);
    if (acceptsBody) callArgs.push('body');
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
    lines.push('/// <see cref="' + name + 'Client"/>. McpProgram.cs registers them with');
    lines.push('/// <c>WithToolsFromAssembly()</c>, which looks for exactly these two attributes --');
    lines.push('/// <c>[McpServerToolType]</c> on the class and <c>[McpServerTool]</c> on each method.');
    lines.push('/// Without them the server starts and lists no tools.');
    lines.push('/// </summary>');
    lines.push('/// <remarks>');
    lines.push('/// Each tool\'s read-only / destructive / idempotent hints are a first draft from');
    lines.push('/// the endpoint\'s HTTP method and path, and its description from the request');
    lines.push('/// and the captured response. Neither knows what the site actually does, so');
    lines.push('/// review them before relying on a client that acts on hints without asking.');
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
    toolNameFor,
    toolHintsFor,
    graphQLHints,
    toolDescriptionFor,
    emitToolsFile,
    csString,
};
