#!/usr/bin/env node
// The pipeline hands the scrub's knowledge to the generator (issue #34).
//
// A query value the scrub replaced must never be baked into generated code as a
// default. Most of the scrubber's fakes can be recognised on sight; a typed
// fake -- a city, a name -- is an ordinary word, and only the scrub's
// substitution table says it was put there. run-agent.js writes that table
// beside the scrubbed capture, and the generator reads it from there. No option
// carries it: this test is what holds the two ends of that convention together.
//
// It runs the whole pipeline (sanitize -> verify -> detect-auth -> generate) on
// tests/fixtures/har/rest-query-typed-pii.har, where a city is personal data in
// the response and the same city is a query value.
//
// Zero-dep, runs with `node query-pipeline.test.js`.
'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const { scrubReasonFor } = require(path.join(__dirname, 'query-params.js'));

const RUN_AGENT = path.join(__dirname, 'run-agent.js');
const GENERATOR = path.join(__dirname, 'generate-wrapper.js');
const FIXTURE = path.resolve(__dirname, '..', '..', '..', '..', 'tests', 'fixtures', 'har', 'rest-query-typed-pii.har');
const CAPTURED_CITY = 'Seattle';

function filesUnder(dir) {
    const files = [];
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) files.push(...filesUnder(full));
        else files.push(full);
    }
    return files;
}

function nearbyMethod(outDir, projectName) {
    const client = fs.readFileSync(path.join(outDir, 'src', projectName, projectName + 'Client.Generated.cs'), 'utf8');
    const line = client.split('\n').find((l) => l.includes(' GetStoresNearbyAsync('));
    assert.ok(line, 'GetStoresNearbyAsync must be emitted');
    return line.trim();
}

const out = fs.mkdtempSync(path.join(os.tmpdir(), 'query-pipeline-'));
const bare = fs.mkdtempSync(path.join(os.tmpdir(), 'query-pipeline-bare-'));
try {
    // run-agent refuses to run without the operator's profile; a test declares its own.
    fs.writeFileSync(path.join(out, '.har-profile.json'), JSON.stringify({ salt: 'query-pipeline-test-salt', literals: {} }));
    const run = spawnSync(process.execPath, [
        RUN_AGENT,
        '--har', FIXTURE,
        '--out', out,
        '--project', 'StoresEx',
        '--namespace', 'StoresSite',
        '--base-url', 'https://api.example.com',
        '--fixed-time', '2026-01-01T00:00:00.000Z',
    ], { encoding: 'utf8' });
    assert.strictEqual(run.status, 0, 'run-agent failed:\n' + run.stdout + run.stderr);

    // ---- The precondition: the scrub replaced the city with a fake that carries no marker ----
    const scrubbed = JSON.parse(fs.readFileSync(path.join(out, '.run-agent', 'scrubbed.har'), 'utf8'));
    const fakeCity = new URL(scrubbed.log.entries[0].request.url).searchParams.get('city');
    assert.ok(fakeCity && fakeCity !== CAPTURED_CITY, 'the scrub must have replaced the city in the query; it is: ' + fakeCity);
    assert.strictEqual(scrubReasonFor('city', fakeCity), null,
        'this test needs a fake that cannot be recognised on sight, and ' + fakeCity + ' can');
    console.log('PASS: the scrub replaced the query\'s city with a fake that carries no marker.');

    // ---- The generator read the table run-agent wrote, and did not bake the fake in ----
    {
        assert.ok(/generate-wrapper: read the scrub's substitution table .*\.substitutions\.json/.test(run.stdout),
            'the generator says which tables it read:\n' + run.stdout);
        assert.strictEqual(nearbyMethod(out, 'StoresEx'),
            'public async Task<StoresNearbyResponse> GetStoresNearbyAsync(string? city = null, string? radius = "25", CancellationToken ct = default)',
            'the replaced city has no default; the radius, which the scrub left alone, keeps its own');

        const readme = fs.readFileSync(path.join(out, 'README.md'), 'utf8');
        assert.ok(readme.includes(
            '| `get_stores_nearby` | `city` | `city` | 2 of 2 | **left out, never baked in**: the scrub replaced the captured value |'),
            'the README says the city is never baked in');
        assert.ok(readme.includes('| `get_stores_nearby` | `radius` | `radius` | 2 of 2 | sends `"25"` (every captured call did) |'));

        // Neither the real city nor its fake is written as a parameter default,
        // a description, an example or a test expectation. (tests/fixtures/
        // holds the scrubbed RESPONSE bodies, where the fake belongs.)
        const generated = [path.join(out, 'README.md')]
            .concat(filesUnder(path.join(out, 'src')))
            .concat(filesUnder(path.join(out, 'tests', 'StoresEx.Tests')));
        for (const file of generated) {
            const text = fs.readFileSync(file, 'utf8');
            assert.ok(!text.includes(fakeCity), 'the fake city was written into ' + path.relative(out, file));
            assert.ok(!text.includes(CAPTURED_CITY), 'the captured city was written into ' + path.relative(out, file));
        }
        console.log('PASS: through the pipeline, a query value the scrub replaced is never a default, an example or a test value.');
    }

    // ---- The falsifier: the same scrubbed capture WITHOUT its tables ----
    {
        // Only the capture is copied. With no table beside it nothing says the
        // city is a fake, so it is the constant every call sent -- which is
        // what the hand-off above exists to prevent.
        const lone = path.join(bare, 'scrubbed.har');
        fs.copyFileSync(path.join(out, '.run-agent', 'scrubbed.har'), lone);
        const direct = spawnSync(process.execPath, [
            GENERATOR,
            '--har', lone,
            '--out', path.join(bare, 'out'),
            '--project-name', 'StoresEx',
            '--namespace', 'StoresSite',
            '--base-url', 'https://api.example.com',
        ], { encoding: 'utf8' });
        assert.strictEqual(direct.status, 0, direct.stdout + direct.stderr);
        assert.ok(!direct.stdout.includes('substitution table'), 'no table, nothing read');
        assert.ok(nearbyMethod(path.join(bare, 'out'), 'StoresEx').includes('string? city = "' + fakeCity + '"'),
            'without the table the fake is indistinguishable from a constant: ' + nearbyMethod(path.join(bare, 'out'), 'StoresEx'));
        console.log('PASS: it is the substitution table, found beside the capture, that keeps the fake out.');
    }
} finally {
    for (const dir of [out, bare]) fs.rmSync(dir, { recursive: true, force: true });
}

console.log('All query-pipeline tests passed.');
