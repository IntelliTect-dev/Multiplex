#!/usr/bin/env node
// Behavior tests for the generated MCP tool class (issue #17): which tools a
// wrapper gets, what each is called, the hints it declares, the description the
// model reads, and the C# that carries them.
//
// These pin the DECISIONS. That a running server actually lists the tools is
// tests/generated-mcp-server.Tests.ps1, which builds a wrapper and asks it.
//
// Zero-dep, runs with `node mcp-tools-emit.test.js`.
'use strict';

const assert = require('assert');
const path = require('path');
const { dedupePatterns, methodNameFor } = require(path.join(__dirname, 'generate-wrapper.js'));
const {
    listTools, toolNameFor, toolHintsFor, graphQLHints, toolDescriptionFor, emitToolsFile, csString,
} = require(path.join(__dirname, 'mcp-tools-emit.js'));

function har(method, url) {
    return { entry: { request: { method, url } } };
}

function literal(...names) {
    return names.map((n) => ({ literal: n }));
}

function toolsFor(entries, hasGraphQL) {
    return listTools({ patterns: dedupePatterns(entries), hasGraphQL: !!hasGraphQL, methodNameFor });
}

// What generate-wrapper.js passes in. Stand-ins here: the wording of an
// endpoint's one-line summary is that file's business, not this module's.
const ctx = {
    baseUrl: 'https://api.example.com',
    describeFor: (p) => 'Summary of ' + p.method + '.',
    patternPath: (p) => '/' + p.segments.map((s) => s.literal ? s.literal : '{' + s.param + '}').join('/'),
};

// ---- Names: the client method, without Async, in snake_case ----
{
    assert.strictEqual(toolNameFor('GetMeAsync'), 'get_me');
    assert.strictEqual(toolNameFor('GetUsersByIdTripsAsync'), 'get_users_by_id_trips');
    assert.strictEqual(toolNameFor('GetGtmDataAsJsonAsync'), 'get_gtm_data_as_json');
    assert.strictEqual(toolNameFor('GetV2ItemsAsync'), 'get_v2_items');
    assert.strictEqual(toolNameFor('GetHTMLPageAsync'), 'get_html_page', 'an acronym is one word');
    assert.strictEqual(toolNameFor('DeleteItemsByIdAsync'), 'delete_items_by_id');
    console.log('PASS: a tool is named after its client method, in snake_case.');
}

// ---- Hints: read-only for the safe methods ----
{
    for (const method of ['GET', 'HEAD', 'OPTIONS', 'get']) {
        assert.deepStrictEqual(toolHintsFor(method, literal('api', 'items')),
            { readOnly: true, destructive: false, idempotent: true }, method + ' is read-only');
    }
    // A GET stays read-only whatever its path is called: the method is what the
    // issue derives it from, and a POST is where the path is consulted.
    assert.strictEqual(toolHintsFor('GET', literal('items', 'delete')).readOnly, true);
    console.log('PASS: GET, HEAD and OPTIONS are read-only, idempotent and not destructive.');
}

// ---- Hints: POST adds, and is never idempotent ----
{
    assert.deepStrictEqual(toolHintsFor('POST', literal('api', 'items')),
        { readOnly: false, destructive: false, idempotent: false });
    console.log('PASS: a POST is additive and not idempotent.');
}

// ---- Hints: a POST whose path names a change or removal is destructive ----
{
    const destructive = [
        literal('api', 'items', 'delete'),
        literal('api', 'orders', 'cancelOrder'),
        literal('api', 'cart', 'remove-item'),
        literal('api', 'profile', 'update_email'),
        literal('api', 'tokens', 'revoke'),
    ];
    for (const segments of destructive) {
        const hints = toolHintsFor('POST', segments);
        const where = segments.map((s) => s.literal).join('/');
        assert.strictEqual(hints.destructive, true, 'POST ' + where + ' is destructive');
        assert.strictEqual(hints.readOnly, false);
        assert.strictEqual(hints.idempotent, false, 'and still not idempotent');
    }
    // Whole words only: a resource whose NAME merely contains one is not a verb.
    for (const segments of [literal('api', 'settings'), literal('api', 'resets'), literal('api', 'movements')]) {
        const where = segments.map((s) => s.literal).join('/');
        assert.strictEqual(toolHintsFor('POST', segments).destructive, false, 'POST ' + where + ' stays additive');
    }
    // A path PARAMETER is a value, not a word of the operation.
    assert.strictEqual(
        toolHintsFor('POST', [{ literal: 'items' }, { param: 'delete', type: 'string' }]).destructive, false);
    console.log('PASS: a POST is destructive when its path names a change or removal, by whole word.');
}

// ---- Hints: PUT, DELETE, PATCH, and the unknown ----
{
    assert.deepStrictEqual(toolHintsFor('PUT', literal('items')),
        { readOnly: false, destructive: true, idempotent: true }, 'PUT replaces, idempotently');
    assert.deepStrictEqual(toolHintsFor('DELETE', literal('items')),
        { readOnly: false, destructive: true, idempotent: true }, 'DELETE removes, idempotently');
    assert.deepStrictEqual(toolHintsFor('PATCH', literal('items')),
        { readOnly: false, destructive: true, idempotent: false }, 'PATCH changes, not idempotently');
    for (const method of ['TRACE', 'PROPFIND', '', undefined]) {
        assert.deepStrictEqual(toolHintsFor(method, literal('items')),
            { readOnly: false, destructive: true, idempotent: false },
            'a method that says nothing gets the cautious answer: ' + method);
    }
    assert.deepStrictEqual(graphQLHints(), { readOnly: false, destructive: true, idempotent: false },
        'a GraphQL endpoint may be asked to mutate');
    console.log('PASS: PUT and DELETE are destructive and idempotent; PATCH, GraphQL and the unknown are cautious.');
}

// ---- The tool surface: one tool per client method ----
{
    const tools = toolsFor([
        har('GET', 'https://api.example.com/api/items'),
        har('GET', 'https://api.example.com/api/items/41'),
        har('GET', 'https://api.example.com/api/items/42'),
        har('POST', 'https://api.example.com/api/items'),
        har('PUT', 'https://api.example.com/api/items/42'),
        har('DELETE', 'https://api.example.com/api/items/42'),
    ]);
    assert.deepStrictEqual(tools.map((t) => t.name).sort(),
        ['create_items', 'delete_items_by_id', 'get_items', 'get_items_by_id', 'update_items_by_id']);
    const byName = Object.fromEntries(tools.map((t) => [t.name, t]));
    assert.strictEqual(byName.get_items_by_id.methodName, 'GetItemsByIdAsync');
    assert.strictEqual(byName.get_items.hints.readOnly, true);
    assert.strictEqual(byName.create_items.hints.destructive, false);
    assert.strictEqual(byName.update_items_by_id.hints.idempotent, true);
    assert.strictEqual(byName.delete_items_by_id.hints.destructive, true);
    console.log('PASS: every endpoint of the capture is one tool, with the hints of its method.');
}

// ---- The tool surface: GraphQL, and a capture with nothing in it ----
{
    const gql = toolsFor([], true);
    assert.deepStrictEqual(gql.map((t) => t.name), ['graphql']);
    assert.strictEqual(gql[0].methodName, 'GraphQLAsync');
    assert.strictEqual(gql[0].pattern, null);
    assert.deepStrictEqual(toolsFor([], false), []);
    console.log('PASS: GraphQL traffic gives one graphql tool; no endpoints give no tools.');
}

// ---- Names are unique on the wire ----
{
    const tools = listTools({
        patterns: [
            { method: 'GET', segments: literal('one') },
            { method: 'GET', segments: literal('two') },
        ],
        hasGraphQL: false,
        // Two client methods that differ only in casing.
        methodNameFor: (p) => p.segments[0].literal === 'one' ? 'GetHTMLAsync' : 'GetHtmlAsync',
    });
    assert.deepStrictEqual(tools.map((t) => t.name), ['get_html', 'get_html_2']);
    console.log('PASS: two client methods never share one tool name.');
}

// ---- The description is derived from the operation ----
{
    const getOne = {
        name: 'get_items_by_id',
        hints: toolHintsFor('GET'),
        pattern: {
            method: 'GET',
            segments: [{ literal: 'api' }, { literal: 'items' }, { param: 'id', type: 'int' }],
            returnShape: { kind: 'object', fields: { name: {}, id: {}, price: {} } },
        },
    };
    const text = toolDescriptionFor(getOne, ctx);
    assert.ok(text.startsWith('Summary of GET.'), 'it opens with the endpoint summary: ' + text);
    assert.ok(text.includes('Sends GET /api/items/{id} to https://api.example.com.'), 'the request it sends: ' + text);
    assert.ok(text.includes('Returns an object with: id, name, price.'), 'the response fields, sorted: ' + text);
    assert.ok(!/Changes|Adds/.test(text), 'a read says nothing about changing the site: ' + text);

    const list = { ...getOne, pattern: { ...getOne.pattern, returnShape: { kind: 'array', element: { kind: 'object', fields: { id: {} } } } } };
    assert.ok(toolDescriptionFor(list, ctx).includes('Returns a list of objects with: id.'));

    const unknown = { ...getOne, pattern: { ...getOne.pattern, returnShape: null } };
    assert.ok(!toolDescriptionFor(unknown, ctx).includes('Returns'), 'no shape captured, no claim about one');

    const many = {};
    for (let i = 0; i < 14; i++) many['f' + String(i).padStart(2, '0')] = {};
    const wide = { ...getOne, pattern: { ...getOne.pattern, returnShape: { kind: 'object', fields: many } } };
    assert.ok(toolDescriptionFor(wide, ctx).includes('f09, and 4 more.'), 'a wide response is summarized: ' + toolDescriptionFor(wide, ctx));
    console.log('PASS: a description names the request, and the fields the capture showed coming back.');
}

// ---- A write says what it does to the site, in the words of its hints ----
{
    const write = (method, segments) => toolDescriptionFor(
        { name: 'x', hints: toolHintsFor(method, segments), pattern: { method, segments, returnShape: null } }, ctx);
    assert.ok(write('POST', literal('items')).endsWith('Adds to the data on the site; calling it again adds again.'));
    assert.ok(write('POST', literal('items', 'delete')).endsWith('Changes or removes data on the site.'));
    assert.ok(write('PATCH', literal('items')).endsWith('Changes or removes data on the site.'));
    for (const method of ['PUT', 'DELETE']) {
        assert.ok(write(method, literal('items')).endsWith(
            'Changes or removes data on the site; repeating the same call changes nothing more.'), method);
    }
    const gql = toolDescriptionFor({ name: 'graphql', hints: graphQLHints(), pattern: null }, ctx);
    assert.ok(gql.includes('https://api.example.com') && gql.includes('mutation can change or remove'), gql);
    console.log('PASS: a write tells the model what calling it does.');
}

// ---- The emitted C# carries the attributes the SDK looks for ----
{
    const patterns = dedupePatterns([
        har('GET', 'https://api.example.com/api/items/41'),
        har('GET', 'https://api.example.com/api/items/42'),
        har('POST', 'https://api.example.com/api/items'),
        har('DELETE', 'https://api.example.com/api/items/42'),
    ]);
    for (const p of patterns) p.responseModel = 'ItemResponse';
    const tools = listTools({ patterns, hasGraphQL: true, methodNameFor });
    const code = emitToolsFile({
        header: '// <auto-generated/>',
        opts: { projectName: 'ExampleEx', namespace: 'Example', baseUrl: ctx.baseUrl },
        tools,
        describeFor: ctx.describeFor,
        patternPath: ctx.patternPath,
    });

    assert.ok(/\[McpServerToolType\]\npublic sealed partial class ExampleExTools\n/.test(code), 'the class is a tool type');
    assert.ok(code.includes('using ModelContextProtocol.Server;'));
    assert.ok(code.includes('namespace Example;'));
    assert.ok(code.includes('public ExampleExTools(ExampleExToolSession session)'), 'it goes through the tool session');

    const attributes = code.match(/\[McpServerTool\(/g) || [];
    assert.strictEqual(attributes.length, tools.length, 'one [McpServerTool] per tool');
    const descriptions = code.match(/^    \[Description\("[^"]+"\)\]$/gm) || [];
    assert.strictEqual(descriptions.length, tools.length, 'and a [Description] on each');

    assert.ok(code.includes(
        '[McpServerTool(Name = "get_items_by_id", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]'));
    assert.ok(code.includes(
        '[McpServerTool(Name = "create_items", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]'));
    assert.ok(code.includes(
        '[McpServerTool(Name = "delete_items_by_id", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]'));
    assert.ok(code.includes(
        '[McpServerTool(Name = "graphql", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]'));

    // Each tool calls the client method it is named after, with its arguments.
    assert.ok(code.includes('[Description("The id in /api/items/{id}.")] int id,'), 'a path parameter is described');
    assert.ok(code.includes('=> _session.RunAsync(client => client.GetItemsByIdAsync(id, ct));'));
    assert.ok(code.includes('string? body = null,'), 'a POST takes a body');
    assert.ok(code.includes('=> _session.RunAsync(client => client.CreateItemsAsync(body, ct));'));
    assert.ok(code.includes('=> _session.RunAsync(client => client.DeleteItemsByIdAsync(id, ct));'), 'a DELETE takes none');
    assert.ok(code.includes('=> _session.RunAsync(client => client.GraphQLAsync<JsonElement>(query, variables, ct));'));
    assert.ok(code.includes('public Task<ItemResponse> GetItemsByIdAsync('), 'a tool returns what the client returns');

    assert.strictEqual(emitToolsFile({
        header: '// <auto-generated/>',
        opts: { projectName: 'ExampleEx', namespace: 'Example', baseUrl: ctx.baseUrl },
        tools,
        describeFor: ctx.describeFor,
        patternPath: ctx.patternPath,
    }), code, 'the same endpoints give the same bytes');
    console.log('PASS: the emitted class is a tool type with one attributed, described tool per endpoint.');
}

// ---- Text from a capture cannot break out of a C# string ----
{
    assert.strictEqual(csString('a"b\\c'), 'a\\"b\\\\c');
    assert.strictEqual(csString('line\nbreak\ttab'), 'line\\u000abreak\\u0009tab');
    const tool = {
        name: 'get_x',
        methodName: 'GetXAsync',
        hints: toolHintsFor('GET'),
        pattern: {
            method: 'GET',
            segments: literal('x'),
            responseModel: 'JsonElement',
            returnShape: { kind: 'object', fields: { 'we"ird\nfield': {} } },
        },
    };
    const code = emitToolsFile({
        header: '//',
        opts: { projectName: 'P', namespace: 'N', baseUrl: 'https://x.invalid' },
        tools: [tool],
        describeFor: ctx.describeFor,
        patternPath: ctx.patternPath,
    });
    const line = code.split('\n').find((l) => l.includes('[Description("Summary'));
    assert.ok(line.includes('we\\"ird\\u000afield'), 'the field name is escaped: ' + line);
    assert.ok(/^    \[Description\("(?:[^"\\]|\\.)*"\)\]$/.test(line), 'and the literal is closed where it should be: ' + line);
    console.log('PASS: a field name from the capture is escaped into the description.');
}

console.log('All mcp-tools-emit tests passed.');
