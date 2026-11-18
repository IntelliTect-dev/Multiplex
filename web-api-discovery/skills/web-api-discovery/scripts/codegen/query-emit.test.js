#!/usr/bin/env node
// Behavior tests for what the generator WRITES about query parameters
// (issue #34): the words a model reads, the C# the client and the tool class
// declare, the README, and the tests a wrapper carries.
//
// The second half runs the generator on tests/fixtures/har/rest-query.har and
// reads what it emitted. That the emitted C# compiles, that its own tests pass
// and that a running server really sends the query is
// tests/generated-query-parameters.Tests.ps1.
//
// Zero-dep, runs with `node query-emit.test.js`.
'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const { dedupePatterns } = require(path.join(__dirname, 'generate-wrapper.js'));
const { collectQueryParameters } = require(path.join(__dirname, 'query-params.js'));
const { describeQueryParameters, clientQueryLines, emitQueryTestsFile, queryReadmeSection } =
    require(path.join(__dirname, 'query-emit.js'));

const GENERATOR = path.join(__dirname, 'generate-wrapper.js');
const FIXTURES = path.resolve(__dirname, '..', '..', '..', '..', 'tests', 'fixtures', 'har');
const SITE = 'https://api.example.com/api/search';

function described(urls) {
    const patterns = dedupePatterns(urls.map((url) => ({ entry: { request: { method: 'GET', url } } })));
    assert.strictEqual(patterns.length, 1);
    return Object.fromEntries(describeQueryParameters(collectQueryParameters(patterns[0].samples)).map((q) => [q.wireName, q]));
}

// ---- Descriptions: built from what was observed, and examples are called examples ----
{
    const q = described([
        SITE + '?limit=11&prefix=w&type=A&type=B&filter=new&session-id=redacted-3f9a1c0b7e2d4a68&rsh=4821937465021',
        SITE + '?limit=11&prefix=wi&type=A&type=B&session-id=redacted-3f9a1c0b7e2d4a68&rsh=4821937466377',
        SITE + '?limit=11&prefix=wire+cutter&type=A&type=B&filter=new&session-id=redacted-3f9a1c0b7e2d4a68&rsh=4821937467910',
    ]);

    assert.strictEqual(q.limit.description,
        'The "limit" query parameter. All 3 captured calls sent "11", so that is what is sent when this is omitted. ' +
        'Pass another value to send that instead, or null to leave it out.');

    assert.strictEqual(q.prefix.description,
        'The "prefix" query parameter. It varied across the 3 captured calls. ' +
        'Examples from the capture, not the only values: "w", "wi", "wire cutter". ' +
        'Left out of the request unless you supply it.');

    assert.strictEqual(q.type.description,
        'The "type" query parameter, sent once per value. All 3 captured calls sent "A", "B", so that is what is sent ' +
        'when this is omitted. Pass a list to send those instead, or an empty list to send none.');

    assert.ok(q.filter.description.includes('It varied across the 3 captured calls (2 of them sent it).'), q.filter.description);

    // What must never be baked in is never quoted either.
    for (const name of ['session-id', 'rsh']) {
        assert.ok(q[name].description.startsWith('The "' + name + '" query parameter. '), q[name].description);
        assert.ok(q[name].description.includes('Left out of the request unless you supply'), q[name].description);
        assert.ok(!/redacted-|48219374/.test(q[name].description), 'no captured value in: ' + q[name].description);
    }
    assert.ok(q['session-id'].description.includes('not baked in'), q['session-id'].description);
    assert.ok(q['session-id'].description.includes('the scrub'), q['session-id'].description);
    assert.ok(q.rsh.description.includes('per-request timestamp or nonce'), q.rsh.description);

    const once = described([SITE + '?alias=aps']);
    assert.strictEqual(once.alias.description,
        'The "alias" query parameter. The one captured call sent "aps", so that is what is sent when this is omitted; ' +
        'one call is no evidence of what else the site accepts. Pass another value to send that instead, or null to leave it out.');

    const twice = described([SITE + '?wc=', SITE + '?wc=']);
    assert.ok(twice.wc.description.includes('Both captured calls sent "",'), 'an empty value is shown as one: ' + twice.wc.description);
    console.log('PASS: a description says what the calls sent, marks examples as examples, and never quotes a scrubbed value.');
}

// ---- Declarations: every parameter optional, a string, with the captured default ----
{
    const q = described([
        SITE + '?limit=11&prefix=w&type=A&type=B&wc=&class=x&tag=a&tag=b',
        SITE + '?limit=11&prefix=wi&type=A&type=B&wc=&class=x&tag=c',
    ]);
    assert.strictEqual(q.limit.declaration, 'string? limit = "11"');
    assert.strictEqual(q.prefix.declaration, 'string? prefix = null', 'a varying parameter has no default');
    assert.strictEqual(q.wc.declaration, 'string? wc = ""', 'an empty captured value is an empty default, not none');
    assert.strictEqual(q.class.declaration, 'string? @class = "x"', 'a keyword is escaped');
    // A C# default must be a constant, so a list defaults to null and the body supplies the values.
    assert.strictEqual(q.type.declaration, 'IReadOnlyList<string>? type = null');
    assert.strictEqual(q.tag.declaration, 'IReadOnlyList<string>? tag = null');
    assert.ok(q.limit.toolDeclaration.startsWith('[Description("The \\"limit\\" query parameter.'), q.limit.toolDeclaration);
    assert.ok(q.limit.toolDeclaration.endsWith('")] string? limit = "11"'), q.limit.toolDeclaration);

    assert.deepStrictEqual(clientQueryLines(Object.values(q)), [
        '        var wireQuery = new List<KeyValuePair<string, string?>>();',
        '        AddQuery(wireQuery, "limit", limit);',
        '        AddQuery(wireQuery, "prefix", prefix);',
        '        AddQuery(wireQuery, "type", type ?? new[] { "A", "B" });',
        '        AddQuery(wireQuery, "wc", wc);',
        '        AddQuery(wireQuery, "class", @class);',
        '        AddQuery(wireQuery, "tag", tag);',
    ], 'the wire name is sent exactly; a repeated constant falls back to its captured values');
    assert.deepStrictEqual(clientQueryLines([]), [], 'an endpoint with no query builds none');
    console.log('PASS: every query parameter is an optional string, defaulting to what the capture showed.');
}

// ---- Text from a capture cannot break out of a C# string ----
{
    const hostile = 'a"b\\c\nd';
    const url = SITE + '?v=' + encodeURIComponent(hostile) + '&' + encodeURIComponent('we"ird\nname') + '=1';
    const q = described([url, url]);
    assert.strictEqual(q.v.declaration, 'string? v = "a\\"b\\\\c\\u000ad"');
    for (const parameter of Object.values(q)) {
        assert.ok(/^\[Description\("(?:[^"\\]|\\.)*"\)\] string\? \w+ = "(?:[^"\\]|\\.)*"$/.test(parameter.toolDeclaration),
            'both literals are closed where they should be: ' + parameter.toolDeclaration);
    }
    const lines = clientQueryLines(Object.values(q));
    assert.ok(lines.includes('        AddQuery(wireQuery, "we\\"ird\\u000aname", weIrdName);'), lines.join('\n'));

    // C# ends a line -- and with it a string literal or a `///` comment -- at
    // U+0085, U+2028 and U+2029 too. None may reach the source raw: what
    // follows one would be compiled as code.
    const NEL = String.fromCharCode(0x85), LS = String.fromCharCode(0x2028), PS = String.fromCharCode(0x2029);
    const LINE_ENDS = new RegExp('[\\r\\n\\x85\\u2028\\u2029]');
    const broken = SITE + '?v=' + encodeURIComponent('a' + NEL + 'b' + LS + 'c' + PS + 'd') + '&' + encodeURIComponent('n' + NEL + 'm') + '=1';
    const b = described([broken, broken]);
    assert.strictEqual(b.v.declaration, 'string? v = "a\\u0085b\\u2028c\\u2029d"');
    for (const parameter of Object.values(b)) {
        assert.ok(!LINE_ENDS.test(parameter.description), 'no line end in a description: ' + JSON.stringify(parameter.description));
        assert.ok(!LINE_ENDS.test(parameter.toolDeclaration), 'nor in a declaration: ' + JSON.stringify(parameter.toolDeclaration));
    }
    for (const line of clientQueryLines(Object.values(b))) {
        assert.ok(!LINE_ENDS.test(line), 'nor in the method body: ' + JSON.stringify(line));
    }
    console.log('PASS: a captured name or value is escaped into every C# literal it reaches.');
}

// ---- Nothing to render when no endpoint takes a query ----
{
    const tools = [{ name: 'get_me', methodName: 'GetMeAsync', pattern: { method: 'GET', segments: [{ literal: 'me' }], queryParameters: [] } },
        { name: 'graphql', methodName: 'GraphQLAsync', pattern: null }];
    assert.strictEqual(emitQueryTestsFile({ opts: { projectName: 'P', namespace: 'N' }, tools }), null);
    assert.strictEqual(queryReadmeSection(tools, { projectName: 'P' }), '');
    console.log('PASS: a capture with no query parameters gets no query tests and no README section.');
}

// ---- The README gives the true reason a value was not baked in ----
{
    const url = SITE + '?fb_dtsg=NAdQx3kLr9WpT2vY&sid=redacted-3f9a1c0b7e2d4a68&v=7c9e6679-7425-40de-944b-e07fc1f90ae7&=x&a%7Cb=1';
    const parameters = Object.values(described([url, url]));
    const section = queryReadmeSection(
        [{ name: 'get_search', methodName: 'GetSearchAsync', pattern: { method: 'GET', segments: [{ literal: 'search' }], queryParameters: parameters } }],
        { projectName: 'P' });
    for (const row of [
        '| `get_search` | `fbDtsg` | `fb_dtsg` | 2 of 2 | **left out, never baked in**: its name is one the scrub policy treats as a secret |',
        '| `get_search` | `sid` | `sid` | 2 of 2 | **left out, never baked in**: the scrub replaced the captured value |',
        '| `get_search` | `v` | `v` | 2 of 2 | **left out, never baked in**: the captured value has a shape the scrub replaces (a token, an id, an email) |',
        '| `get_search` | `param` | (an empty name) | 2 of 2 | sends `"x"` (every captured call did) |',
        // A | in a name must not end the cell.
        '| `get_search` | `aB` | `a\\|b` | 2 of 2 | sends `"1"` (every captured call did) |',
    ]) {
        assert.ok(section.includes(row), 'README row missing: ' + row + '\n' + section);
    }
    assert.ok(!/NAdQx3kLr9WpT2vY|3f9a1c0b7e2d4a68|7c9e6679/.test(section), 'and never the value itself');
    assert.ok(section.includes('`tests/P.Tests/QueryStringTests.cs`'));
    console.log('PASS: the README says why each value was not baked in, and never shows it.');
}

// ======================================================================
// The generator, run on the fixture.
// ======================================================================

function generate(harName, outDir) {
    const result = spawnSync(process.execPath, [
        GENERATOR,
        '--har', path.join(FIXTURES, harName),
        '--out', outDir,
        '--project-name', 'QueryEx',
        '--namespace', 'QuerySite',
        '--base-url', 'https://api.example.com',
        '--auth-model', 'cookie',
    ], { encoding: 'utf8' });
    assert.strictEqual(result.status, 0, 'generate-wrapper failed:\n' + result.stdout + result.stderr);
    return result;
}

function allFilesUnder(dir) {
    const files = [];
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) files.push(...allFilesUnder(full));
        else files.push(full);
    }
    return files.sort();
}

/** The whole declaration of a method, from `public` to the line that opens its body or its arrow. */
function methodOf(source, methodName) {
    const start = source.indexOf(' ' + methodName + '(');
    assert.ok(start >= 0, methodName + ' must be emitted');
    const from = source.lastIndexOf('\n', source.lastIndexOf('public ', start)) + 1;
    const end = source.indexOf('\n    }', start);
    return source.slice(from, end >= 0 ? end : undefined);
}

const out = fs.mkdtempSync(path.join(os.tmpdir(), 'query-emit-'));
const outAgain = fs.mkdtempSync(path.join(os.tmpdir(), 'query-emit-'));
const outPlain = fs.mkdtempSync(path.join(os.tmpdir(), 'query-emit-'));
try {
    generate('rest-query.har', out);
    const client = fs.readFileSync(path.join(out, 'src', 'QueryEx', 'QueryExClient.Generated.cs'), 'utf8');
    const toolsCs = fs.readFileSync(path.join(out, 'src', 'QueryEx', 'QueryExTools.Generated.cs'), 'utf8');
    const readme = fs.readFileSync(path.join(out, 'README.md'), 'utf8');
    const testsDir = path.join(out, 'tests', 'QueryEx.Tests');

    // ---- The client method takes the query parameters, and sends them ----
    {
        const search = methodOf(client, 'GetSearchSuggestionsAsync');
        assert.ok(search.includes(
            'GetSearchSuggestionsAsync(string? limit = "11", string? prefix = null, IReadOnlyList<string>? suggestionType = null, ' +
            'string? pageType = "Gateway", string? page = null, string? wc = "", string? sessionId = null, ' +
            'string? underscore = null, string? lastPrefix = null, CancellationToken ct = default)'),
            'first-seen order; constants default to what was captured; scrubbed and nonce default to nothing:\n' + search);
        for (const line of [
            'AddQuery(wireQuery, "limit", limit);',
            'AddQuery(wireQuery, "prefix", prefix);',
            'AddQuery(wireQuery, "suggestion-type", suggestionType ?? new[] { "KEYWORD", "WIDGET" });',
            'AddQuery(wireQuery, "page-type", pageType);',
            'AddQuery(wireQuery, "wc", wc);',
            'AddQuery(wireQuery, "session-id", sessionId);',
            'AddQuery(wireQuery, "_", underscore);',
            'AddQuery(wireQuery, "last-prefix", lastPrefix);',
        ]) {
            assert.ok(search.includes(line), 'the wire name is sent exactly: ' + line + '\n' + search);
        }
        assert.ok(search.includes('jsonBody: null, query: wireQuery, ct: ct)'), 'the query reaches SendRawAsync:\n' + search);
        assert.ok(!search.includes('query: null'), search);
        console.log('PASS: a client method takes its endpoint\'s query parameters and passes them to SendRawAsync.');
    }

    // ---- Awkward and colliding names ----
    {
        const reviews = methodOf(client, 'GetItemsByIdReviewsAsync');
        assert.ok(reviews.includes(
            'GetItemsByIdReviewsAsync(int id, string? idQuery = "r-100", string? @class = "verified", string? q0 = null, ' +
            'string? filterName = "stars", string? clientQuery = "web", string? pageSize = "20", string? pageSize2 = "20", ' +
            'string? query = null, string? ctQuery = "1", CancellationToken ct = default)'), reviews);
        for (const line of [
            'AddQuery(wireQuery, "id", idQuery);',
            'AddQuery(wireQuery, "class", @class);',
            'AddQuery(wireQuery, "q[0]", q0);',
            'AddQuery(wireQuery, "filter.name", filterName);',
            'AddQuery(wireQuery, "client", clientQuery);',
            'AddQuery(wireQuery, "page_size", pageSize);',
            'AddQuery(wireQuery, "page-size", pageSize2);',
            'AddQuery(wireQuery, "ct", ctQuery);',
        ]) {
            assert.ok(reviews.includes(line), line + '\n' + reviews);
        }
        assert.ok(reviews.includes('$"/api/items/{id}/reviews"'), 'the path parameter is still the path parameter:\n' + reviews);
        console.log('PASS: a query name that is not a C# identifier, or that the method already uses, gets a valid one.');
    }

    // ---- One call, a body, and an endpoint with no query ----
    {
        const config = methodOf(client, 'GetCatalogConfigAsync');
        assert.ok(config.includes(
            'GetCatalogConfigAsync(string? alias = "aps", string? lop = "en_US", string? visitor = null, ' +
            'string? requestId = "K3QW9ZT5VB7XN2M4PD8R", string? underscore = null, CancellationToken ct = default)'),
            'captured once: every value is the default, except the UUID and the timestamp:\n' + config);

        const search = methodOf(client, 'CreateItemsSearchAsync');
        assert.ok(search.includes(
            'CreateItemsSearchAsync(string? body = null, string? dryRun = "true", string? sort = "price", CancellationToken ct = default)'),
            'the body keeps its place before the query parameters:\n' + search);
        assert.ok(search.includes('jsonBody: body, query: wireQuery, ct: ct)'), search);

        const me = methodOf(client, 'GetMeAsync');
        assert.ok(me.includes('GetMeAsync(CancellationToken ct = default)'), me);
        assert.ok(me.includes('jsonBody: null, query: null, ct: ct)'), 'an endpoint with no query is emitted as it always was:\n' + me);
        assert.ok(!me.includes('wireQuery'), me);
        console.log('PASS: a single call defaults to what it sent; a body and a query coexist; no query, no change.');
    }

    // ---- The tool takes the same parameters, described, and passes them on ----
    {
        const search = methodOf(toolsCs, 'GetSearchSuggestionsAsync');
        assert.ok(search.includes('[Description("The \\"limit\\" query parameter. All 5 captured calls sent \\"11\\", so that is what is sent when this is omitted. ' +
            'Pass another value to send that instead, or null to leave it out.")] string? limit = "11",'), search);
        assert.ok(search.includes('Examples from the capture, not the only values: \\"w\\", \\"wi\\", \\"wire cutter\\". ' +
            'Left out of the request unless you supply it.")] string? prefix = null,'), search);
        assert.ok(search.includes('")] IReadOnlyList<string>? suggestionType = null,'), search);
        assert.ok(search.includes('")] string? sessionId = null,'), search);
        assert.ok(search.includes(
            '=> _session.RunAsync(client => client.GetSearchSuggestionsAsync(limit, prefix, suggestionType, pageType, page, wc, sessionId, underscore, lastPrefix, ct));'),
            'the tool passes every argument on, in the client method\'s order:\n' + search);

        const reviews = methodOf(toolsCs, 'GetItemsByIdReviewsAsync');
        assert.ok(reviews.includes('[Description("The id in /api/items/{id}/reviews.")] int id,'), reviews);
        assert.ok(reviews.includes(
            '=> _session.RunAsync(client => client.GetItemsByIdReviewsAsync(id, idQuery, @class, q0, filterName, clientQuery, pageSize, pageSize2, query, ctQuery, ct));'),
            reviews);

        const create = methodOf(toolsCs, 'CreateItemsSearchAsync');
        assert.ok(create.includes('=> _session.RunAsync(client => client.CreateItemsSearchAsync(body, dryRun, sort, ct));'), create);

        const me = methodOf(toolsCs, 'GetMeAsync');
        assert.ok(me.includes('=> _session.RunAsync(client => client.GetMeAsync(ct));'), me);
        console.log('PASS: a tool declares the same query parameters as its client method, each described, and passes them on.');
    }

    // ---- The README lists them, and says what is never baked in ----
    {
        assert.ok(readme.includes('### Query parameters'), 'the README has the section');
        for (const row of [
            '| `get_search_suggestions` | `limit` | `limit` | 5 of 5 | sends `"11"` (every captured call did) |',
            '| `get_search_suggestions` | `prefix` | `prefix` | 5 of 5 | left out: it varied (for example `"w"`, `"wi"`, `"wire cutter"`) |',
            '| `get_search_suggestions` | `suggestionType` | `suggestion-type` (repeated) | 5 of 5 | sends `"KEYWORD"`, `"WIDGET"` (every captured call did) |',
            '| `get_search_suggestions` | `wc` | `wc` | 5 of 5 | sends `""` (every captured call did) |',
            '| `get_search_suggestions` | `sessionId` | `session-id` | 5 of 5 | **left out, never baked in**: the scrub replaced the captured value |',
            '| `get_search_suggestions` | `underscore` | `_` | 5 of 5 | **left out, never baked in**: a per-request timestamp or nonce |',
            '| `get_search_suggestions` | `lastPrefix` | `last-prefix` | 4 of 5 | left out: it varied (for example `"w"`, `"wi"`, `"wire"`) |',
            '| `get_items_by_id_reviews` | `class` | `class` | 2 of 2 | sends `"verified"` (every captured call did) |',
            '| `get_catalog_config` | `alias` | `alias` | 1 of 1 | sends `"aps"` (the one captured call did) |',
            // A UUID carries no scrub marker: the reason given is the true one.
            '| `get_catalog_config` | `visitor` | `visitor` | 1 of 1 | **left out, never baked in**: the captured value has a shape the scrub replaces (a token, an id, an email) |',
        ]) {
            assert.ok(readme.includes(row), 'README row missing: ' + row);
        }
        assert.ok(/A value the scrub replaced[^]*is never\s+written into the generated code/.test(readme), 'the note says so');
        const section = readme.slice(readme.indexOf('### Query parameters'));
        assert.ok(!section.includes('| `get_me` |'), 'a tool with no query parameters has no row');

        // A recipe shows the call a reader makes first: with the parameter that varied.
        assert.ok(readme.includes('var result = await client.GetSearchSuggestionsAsync(prefix: "w");'), 'the search recipe passes a search term');
        assert.ok(readme.includes('var result = await client.GetItemsByIdReviewsAsync(1, q0: "great");'), 'after the path parameter');
        assert.ok(readme.includes('var result = await client.GetMeAsync();'), 'and an endpoint with nothing to vary is called as before');
        console.log('PASS: the README lists every query parameter, what is sent when it is omitted, and what is never baked in.');
    }

    // ---- The wrapper's own tests assert the exact query on the wire ----
    {
        const tests = fs.readFileSync(path.join(testsDir, 'QueryStringTests.cs'), 'utf8');
        const probe = (n) => 'p' + n + '%20%26%3D%2F%2B%C3%A9';
        for (const expected of [
            // No query argument supplied: the captured defaults, in order, and nothing else.
            'Assert.Equal("/api/search/suggestions?limit=11&suggestion-type=KEYWORD&suggestion-type=WIDGET&page-type=Gateway&wc=", Assert.Single(sent).PathAndQuery);',
            'Assert.Equal("/api/items/1/reviews?id=r-100&class=verified&filter.name=stars&client=web&page_size=20&page-size=20&ct=1", Assert.Single(sent).PathAndQuery);',
            'Assert.Equal("/api/catalog/config?alias=aps&lop=en_US&request-id=K3QW9ZT5VB7XN2M4PD8R", Assert.Single(sent).PathAndQuery);',
            'Assert.Equal("/api/items/search?dryRun=true&sort=price", Assert.Single(sent).PathAndQuery);',
            // Every one supplied: each value distinct, and escaped.
            'Assert.Equal("/api/search/suggestions?limit=' + probe(1) + '&prefix=' + probe(2) +
                '&suggestion-type=' + probe(3) + '&suggestion-type=p3b&page-type=' + probe(4) + '&page=' + probe(5) +
                '&wc=' + probe(6) + '&session-id=' + probe(7) + '&_=' + probe(8) + '&last-prefix=' + probe(9) +
                '", Assert.Single(sent).PathAndQuery);',
        ]) {
            assert.ok(tests.includes(expected), 'QueryStringTests.cs must assert: ' + expected);
        }
        assert.ok(tests.includes('q%5B0%5D=' + probe(3)), 'a wire name is escaped too');
        assert.ok(tests.includes(
            'await client.GetItemsByIdReviewsAsync(1, idQuery: "p1 &=/+é", @class: "p2 &=/+é", q0: "p3 &=/+é",'), tests);
        assert.ok(tests.includes('suggestionType: new[] { "p3 &=/+é", "p3b" }'), 'a repeated parameter is given two values');
        const facts = tests.match(/^ +\[Fact\]$/gm) || [];
        assert.strictEqual(facts.length, 16, 'four facts for each of the four endpoints that take a query');
        assert.strictEqual((tests.match(/new QueryExTools\(session\)\./g) || []).length, 8, 'half of them go through the tool class');
        assert.ok(!tests.includes('GetMeAsync'), 'an endpoint with no query has no query facts');

        // The one-fact-per-endpoint and one-fact-per-tool files are left as they were.
        const clientFacts = fs.readdirSync(testsDir).filter((f) => /^ClientTests\..*\.cs$/.test(f))
            .map((f) => (fs.readFileSync(path.join(testsDir, f), 'utf8').match(/^ +\[Fact\]$/gm) || []).length)
            .reduce((a, b) => a + b, 0);
        assert.strictEqual(clientFacts, 5, 'ClientTests.* stays one fact per endpoint');
        assert.strictEqual((fs.readFileSync(path.join(testsDir, 'ToolsTests.cs'), 'utf8').match(/^ +\[Fact\]$/gm) || []).length, 5,
            'and ToolsTests.cs one per tool');

        const mock = fs.readFileSync(path.join(testsDir, 'MockHandler.cs'), 'utf8');
        assert.ok(/public static HttpClient FromJson\(string json, ICollection<Uri> sentTo/.test(mock), 'MockHandler records where requests went');
        console.log('PASS: the wrapper carries tests that assert the exact path and query each endpoint sends.');
    }

    // ---- The wrapper's smoke test expects the parameters in the tool's input schema ----
    {
        const smoke = fs.readFileSync(path.join(testsDir, 'pester', 'Mcp.Tests.ps1'), 'utf8');
        assert.ok(smoke.includes(
            '@{ Name = "get_search_suggestions"; Method = "GetSearchSuggestionsAsync"; ReadOnly = $true; Destructive = $false; Idempotent = $true; ' +
            'Query = @("limit", "prefix", "suggestionType", "pageType", "page", "wc", "sessionId", "underscore", "lastPrefix") }'), smoke);
        assert.ok(smoke.includes('Query = @("idQuery", "class", "q0", "filterName", "clientQuery", "pageSize", "pageSize2", "query", "ctQuery") }'),
            'the tool property name of a keyword is the keyword');
        assert.ok(smoke.includes(
            '@{ Name = "get_me"; Method = "GetMeAsync"; ReadOnly = $true; Destructive = $false; Idempotent = $true }'),
            'a tool with no query parameters keeps the line it always had');
        // That the suite then FINDS them in a running server's inputSchema, and
        // fails when one is missing, is tests/generated-query-parameters.Tests.ps1.
        console.log('PASS: the smoke test is given each tool\'s query parameters to look for in its inputSchema.');
    }

    // ---- What must not be baked in is nowhere in the output ----
    {
        const forbidden = {
            'the scrubbed session id': 'redacted-3f9a1c0b7e2d4a68',
            'the UUID': '7c9e6679-7425-40de-944b-e07fc1f90ae7',
            'a per-call timestamp': '17949869931',
            'the single call\'s timestamp': '1794987002162',
        };
        for (const file of allFilesUnder(out)) {
            const text = fs.readFileSync(file, 'utf8');
            for (const [what, value] of Object.entries(forbidden)) {
                assert.ok(!text.includes(value), what + ' was written into ' + path.relative(out, file));
            }
        }
        console.log('PASS: no scrubbed value and no timestamp from the capture is written anywhere in the wrapper.');
    }

    // ---- Deterministic ----
    {
        generate('rest-query.har', outAgain);
        const first = allFilesUnder(out).map((f) => path.relative(out, f));
        const second = allFilesUnder(outAgain).map((f) => path.relative(outAgain, f));
        assert.deepStrictEqual(second, first);
        for (const file of first) {
            assert.ok(fs.readFileSync(path.join(out, file)).equals(fs.readFileSync(path.join(outAgain, file))),
                file + ' differs between two runs');
        }
        console.log('PASS: two runs on the same capture give the same bytes.');
    }

    // ---- A capture with no query at all is emitted as before ----
    {
        generate('rest-crud.har', outPlain);
        const plainTests = path.join(outPlain, 'tests', 'QueryEx.Tests');
        assert.ok(!fs.existsSync(path.join(plainTests, 'QueryStringTests.cs')), 'no query, no query tests');
        const plainClient = fs.readFileSync(path.join(outPlain, 'src', 'QueryEx', 'QueryExClient.Generated.cs'), 'utf8');
        assert.ok(!plainClient.includes('AddQuery'), 'and no query helpers');
        assert.ok(!fs.readFileSync(path.join(outPlain, 'README.md'), 'utf8').includes('### Query parameters'));
        assert.ok(!fs.readFileSync(path.join(plainTests, 'pester', 'Mcp.Tests.ps1'), 'utf8').includes('Query = @('));

        // Generating again into the same folder, from a capture that now has no
        // query, must not leave the old query tests behind to break the build.
        generate('rest-crud.har', out);
        assert.ok(!fs.existsSync(path.join(testsDir, 'QueryStringTests.cs')), 'stale query tests are removed');
        console.log('PASS: a capture without query parameters produces no query code, tests or notes.');
    }
} finally {
    for (const dir of [out, outAgain, outPlain]) fs.rmSync(dir, { recursive: true, force: true });
}

console.log('All query-emit tests passed.');
