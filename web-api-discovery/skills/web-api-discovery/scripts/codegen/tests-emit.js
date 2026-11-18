// scripts/codegen/tests-emit.js -- emits the xUnit + Pester test scaffold for a
// generated wrapper. Invoked by generate-wrapper.js after the wrapper csproj
// and fixtures are on disk. Zero npm deps, deterministic output.
//
// Layout produced under <outDir>:
//   tests/
//     <TestProjectName>/
//       <TestProjectName>.csproj
//       FixtureLoader.cs
//       MockHandler.cs
//       PacingHandlerTests.cs     (the client's pacing + throttling handler)
//       ToolSessionTests.cs       (the MCP server's lazily read session)
//       ToolsTests.cs             (one [Fact] per MCP tool, through the tool class)
//       ClientTests.<Group>.cs    (one per resource group; [Fact] per endpoint)
//       pester/
//         Mcp.Tests.ps1           (starts the server and asks it for its tools)
//         McpStdio.ps1            (the MCP stdio client that suite uses)
//         run-pester.ps1

'use strict';

const fs = require('fs');
const path = require('path');

const { pascalCase } = require('./generate-wrapper-helpers.js');
const { listTools } = require('./mcp-tools-emit.js');

const IGNORED_PATH_PREFIXES = new Set(['api', 'v1', 'v2', 'v3', 'rest', 'graphql']);

function meaningfulSegments(segments) {
    let i = 0;
    while (i < segments.length && segments[i].literal &&
           IGNORED_PATH_PREFIXES.has(segments[i].literal.toLowerCase())) {
        i++;
    }
    if (i >= segments.length) return segments.slice(-1);
    return segments.slice(i);
}

function groupNameFor(pattern) {
    const segs = meaningfulSegments(pattern.segments);
    for (const s of segs) {
        if (s.literal) return pascalCase(s.literal);
    }
    return 'Root';
}

function fixtureFileNameFor(pattern, patternPath) {
    return (pattern.method + '_' + patternPath.replace(/[^A-Za-z0-9]+/g, '_').replace(/^_|_$/g, '')) + '.json';
}

function placeholderArg(type) {
    if (type === 'int' || type === 'long') return '1';
    if (type === 'double') return '1.0';
    if (type === 'bool') return 'true';
    return '"abc"';
}

function buildFactBody(pattern, methodName, patternPath) {
    const fixture = fixtureFileNameFor(pattern, patternPath);
    const args = pattern.segments.filter((s) => s.param).map((s) => placeholderArg(s.type)).join(', ');
    const lines = [];
    lines.push('    [Fact]');
    lines.push('    public async Task ' + methodName + '_ReturnsFixture()');
    lines.push('    {');
    lines.push('        using var http = MockHandler.FromFixture("' + fixture + '");');
    lines.push('        var creds = new SessionCredentials("test=1", string.Empty);');
    lines.push('        using var client = new __CLIENT__(creds, http);');
    lines.push('        var result = await client.' + methodName + '(' + args + ');');
    lines.push('        Assert.NotNull((object?)result);');
    lines.push('    }');
    return lines.join('\n');
}

function buildGraphQlFact() {
    const lines = [];
    lines.push('    [Fact]');
    lines.push('    public async Task GraphQLAsync_ReturnsFixture()');
    lines.push('    {');
    lines.push('        using var http = MockHandler.FromFixture("graphql.json");');
    lines.push('        var creds = new SessionCredentials("test=1", string.Empty);');
    lines.push('        using var client = new __CLIENT__(creds, http);');
    lines.push('        var result = await client.GraphQLAsync<JsonElement>("query { me { id } }");');
    lines.push('        Assert.NotNull((object?)result);');
    lines.push('    }');
    return lines.join('\n');
}

// One fact per MCP tool. The call goes tool -> tool session -> client, so a tool
// that passes the wrong arguments, or does not reach the client at all, fails
// here and not in front of a model.
function buildToolFact(tool, patternPath) {
    const p = tool.pattern;
    const fixture = p ? fixtureFileNameFor(p, patternPath(p)) : 'graphql.json';
    const args = p
        ? p.segments.filter((s) => s.param).map((s) => placeholderArg(s.type)).join(', ')
        : '"query { me { id } }"';
    const lines = [];
    lines.push('    [Fact]');
    lines.push('    public async Task ' + tool.methodName + '_ReturnsFixtureThroughTheTool()');
    lines.push('    {');
    lines.push('        using var http = MockHandler.FromFixture("' + fixture + '");');
    lines.push('        using var session = SignedInOver(http);');
    lines.push('        var result = await new __TOOLS__(session).' + tool.methodName + '(' + args + ');');
    lines.push('        Assert.NotNull((object?)result);');
    lines.push('    }');
    return lines.join('\n');
}

function psBool(b) { return b ? '$true' : '$false'; }

// What a RUNNING server must answer to tools/list, as a PowerShell hashtable
// per tool. Built from the endpoint list -- not read back from the emitted tool
// class -- so a wrapper whose tool class is missing still expects its tools.
function expectedToolLine(tool) {
    return '        @{ Name = "' + tool.name + '"; Method = "' + tool.methodName + '"' +
        '; ReadOnly = ' + psBool(tool.hints.readOnly) +
        '; Destructive = ' + psBool(tool.hints.destructive) +
        '; Idempotent = ' + psBool(tool.hints.idempotent) + ' }';
}

function substitute(body, tokens) {
    return body.replace(/\{\{(\w+)\}\}/g, (m, k) => tokens[k] !== undefined ? tokens[k] : m);
}

function emitTestScaffold(args) {
    const { outDir, opts, patterns, hasGraphQL, methodNameFor, patternPath } = args;
    const projectName = opts.projectName;
    const testProjectName = opts.testProjectName || (projectName + '.Tests');
    const namespace = opts.namespace;

    const tmplDir = path.resolve(__dirname, '..', '..', 'csharp', 'tests');
    if (!fs.existsSync(tmplDir)) return;

    const testProjDir = path.join(outDir, 'tests', testProjectName);
    const pesterDir = path.join(testProjDir, 'pester');
    fs.mkdirSync(pesterDir, { recursive: true });

    // Group patterns by resource group; emit ClientTests.<Group>.cs per group.
    // Stable sort so output is deterministic.
    const groups = new Map();
    for (const p of patterns) {
        const g = groupNameFor(p);
        if (!groups.has(g)) groups.set(g, []);
        groups.get(g).push(p);
    }
    if (hasGraphQL) {
        if (!groups.has('Graphql')) groups.set('Graphql', []);
        groups.get('Graphql').push({ __graphql: true });
    }

    const groupNames = Array.from(groups.keys()).sort();

    const baseTokens = {
        ProjectName: projectName,
        TestProjectName: testProjectName,
        Namespace: namespace,
    };

    // Tests.csproj
    const csprojTmpl = fs.readFileSync(path.join(tmplDir, 'Tests.csproj.tmpl'), 'utf8');
    fs.writeFileSync(path.join(testProjDir, testProjectName + '.csproj'),
        substitute(csprojTmpl, baseTokens));

    // FixtureLoader.cs + MockHandler.cs
    const flTmpl = fs.readFileSync(path.join(tmplDir, 'FixtureLoader.cs.tmpl'), 'utf8');
    fs.writeFileSync(path.join(testProjDir, 'FixtureLoader.cs'), substitute(flTmpl, baseTokens));
    const mhTmpl = fs.readFileSync(path.join(tmplDir, 'MockHandler.cs.tmpl'), 'utf8');
    fs.writeFileSync(path.join(testProjDir, 'MockHandler.cs'), substitute(mhTmpl, baseTokens));

    // PacingHandlerTests.cs -- the generated client's pacing + throttling handler,
    // tested in every wrapper's own build. Deliberately NOT named ClientTests.*:
    // that glob is one [Fact] per endpoint, and these are not endpoints.
    const phTmpl = fs.readFileSync(path.join(tmplDir, 'PacingHandlerTests.cs.tmpl'), 'utf8');
    fs.writeFileSync(path.join(testProjDir, 'PacingHandlerTests.cs'), substitute(phTmpl, baseTokens));

    // ToolSessionTests.cs -- the MCP server's tool session: the saved session is
    // read at the first tool call, and a call with none is told so in words.
    const tsTmpl = fs.readFileSync(path.join(tmplDir, 'ToolSessionTests.cs.tmpl'), 'utf8');
    fs.writeFileSync(path.join(testProjDir, 'ToolSessionTests.cs'), substitute(tsTmpl, baseTokens));

    // ClientTests.<Group>.cs (per group)
    const clientName = projectName + 'Client';
    const ctTmpl = fs.readFileSync(path.join(tmplDir, 'ClientTests.cs.tmpl'), 'utf8');
    for (const g of groupNames) {
        const facts = [];
        const groupPatterns = groups.get(g).slice().sort((a, b) => {
            const ka = a.__graphql ? 'zzz' : (methodNameFor(a) || '');
            const kb = b.__graphql ? 'zzz' : (methodNameFor(b) || '');
            return ka.localeCompare(kb);
        });
        for (const p of groupPatterns) {
            if (p.__graphql) {
                facts.push(buildGraphQlFact());
            } else {
                facts.push(buildFactBody(p, methodNameFor(p), patternPath(p)));
            }
        }
        // Replace __CLIENT__ placeholder used inside fact bodies. The placeholder
        // avoids interfering with the {{...}} substitution pass.
        const factBlock = facts.join('\n\n').replace(/__CLIENT__/g, clientName);

        // Each group's file is a `partial class` body; using the same class
        // name across groups keeps every generated [Fact] in one logical type.
        const tokens = Object.assign({}, baseTokens, { Facts: factBlock });
        const body = substitute(ctTmpl, tokens);
        fs.writeFileSync(path.join(testProjDir, 'ClientTests.' + g + '.cs'), body);
    }

    // The tool surface, from the endpoint list (mcp-tools-emit.js). Sorted by
    // name so the output is deterministic whatever order the capture was in.
    const tools = listTools({ patterns, hasGraphQL, methodNameFor })
        .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));

    // ToolsTests.cs -- one [Fact] per tool, through the tool class.
    const toolFacts = tools.map((t) => buildToolFact(t, patternPath)).join('\n\n')
        .replace(/__TOOLS__/g, projectName + 'Tools');
    const ttTmpl = fs.readFileSync(path.join(tmplDir, 'ToolsTests.cs.tmpl'), 'utf8');
    fs.writeFileSync(path.join(testProjDir, 'ToolsTests.cs'),
        substitute(ttTmpl, Object.assign({}, baseTokens, { Facts: toolFacts })));

    // Pester -- bake the expected tools into Mcp.Tests.ps1, which starts the
    // server and compares them with what it answers to tools/list.
    const expectedTools = tools.map(expectedToolLine).join('\n');

    const mcpTokens = Object.assign({}, baseTokens, { ExpectedTools: expectedTools });
    const mcpTmpl = fs.readFileSync(path.join(tmplDir, 'pester', 'Mcp.Tests.ps1.tmpl'), 'utf8');
    fs.writeFileSync(path.join(pesterDir, 'Mcp.Tests.ps1'), substitute(mcpTmpl, mcpTokens));
    // The MCP stdio client Mcp.Tests.ps1 starts the server with. It sits beside
    // the suite so the smoke test needs nothing but PowerShell and the SDK.
    const stdioTmpl = fs.readFileSync(path.join(tmplDir, 'pester', 'McpStdio.ps1.tmpl'), 'utf8');
    fs.writeFileSync(path.join(pesterDir, 'McpStdio.ps1'), substitute(stdioTmpl, baseTokens));
    const runTmpl = fs.readFileSync(path.join(tmplDir, 'pester', 'run-pester.ps1.tmpl'), 'utf8');
    fs.writeFileSync(path.join(pesterDir, 'run-pester.ps1'), substitute(runTmpl, baseTokens));
}

module.exports = { emitTestScaffold, groupNameFor, fixtureFileNameFor };