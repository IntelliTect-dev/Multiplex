'use strict';

/**
 * capture-ai-e2e.test-support.js -- what an end-to-end -AI test needs: a fixture
 * repository the recorder will record in, a PATH with no real agent on it, the
 * recorder started under the stub agent (capture-ai-stub-agent.test-support.js)
 * and a headless browser, a local fixture site, and the waits and checks those
 * runs share. For tests only.
 *
 * The same shapes capture-ai-drive.test.js uses, held here so a second file of
 * end-to-end cases (capture-ai-pause.test.js) can use them without copying --
 * a file per group keeps each under the Node runner's per-file time limit.
 *
 * No test touches the public internet, a real agent, or a real tailnet: the
 * site is a local server, `claude` is pruned from PATH and answered by the
 * stub, and the recorder is pointed at a Tailscale CLI that is not there
 * unless a test fakes one onto loopback.
 */

const assert = require('assert');
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const { execFileSync, spawn, spawnSync } = require('child_process');

const captureJs = path.join(__dirname, 'capture-har.js');
const capture = require(captureJs);

const HEADLESS_PRELOAD = path.join(__dirname, 'capture-native-headless.test-support.js');
const STUB_AGENT_PRELOAD = path.join(__dirname, 'capture-ai-stub-agent.test-support.js');
const NO_TAILSCALE = path.join(os.tmpdir(), 'no-such-dir-for-tests', 'tailscale-not-here.exe');
const AGENT_EXECUTABLES = process.platform === 'win32'
    ? ['claude.exe', 'claude.cmd', 'claude.bat', 'claude']
    : ['claude'];

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function real(p) {
    try { return fs.realpathSync.native(p); } catch (e) { void e; return fs.realpathSync(p); }
}

function createE2eKit(prefix) {
    const tmpRoot = real(fs.mkdtempSync(path.join(os.tmpdir(), prefix)));
    let seq = 0;
    const skipped = [];

    function scratch(name) {
        const dir = path.join(tmpRoot, `${name}-${seq++}`);
        fs.mkdirSync(dir, { recursive: true });
        return real(dir);
    }

    function git(cwd, args) {
        return execFileSync('git', args, {
            cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true
        }).trim();
    }

    /** A repository whose capture store is gitignored, so the recorder records. */
    function makeRepo(name) {
        const dir = scratch(name);
        git(dir, ['init', '--quiet', '-b', 'main']);
        fs.writeFileSync(path.join(dir, '.gitignore'), '.har-captures/\n.har-profile.json\n', 'utf8');
        fs.writeFileSync(path.join(dir, 'README.md'), '# fixture\n', 'utf8');
        git(dir, ['add', '-A']);
        git(dir, ['-c', 'user.email=fixture@example.invalid', '-c', 'user.name=fixture',
            'commit', '--quiet', '-m', 'init']);
        // What scaffoldProfile writes, so the recorder's profile preflight passes.
        fs.writeFileSync(path.join(dir, '.har-profile.json'), JSON.stringify({
            salt: 'a'.repeat(48), literals: {}
        }, null, 2) + '\n', 'utf8');
        return dir;
    }

    /** This PATH with every directory holding a `claude` removed: the stub is the only agent. */
    function pathWithoutAgent() {
        return (process.env.PATH || '').split(path.delimiter).filter((dir) => dir && !AGENT_EXECUTABLES.some((name) => {
            try { return fs.existsSync(path.join(dir, name)); } catch (e) { void e; return false; }
        })).join(path.delimiter);
    }

    function agentOnPath(dirs) {
        return dirs.split(path.delimiter).some((dir) => dir && AGENT_EXECUTABLES.some((name) => {
            try { return fs.existsSync(path.join(dir, name)); } catch (e) { void e; return false; }
        }));
    }

    /** The child's environment: the driver reachable, no real tailnet, PATH replaced cleanly. */
    function driverEnv(over) {
        const replacingPath = !!(over && over.PATH !== undefined);
        const env = {};
        for (const [key, value] of Object.entries(process.env)) {
            if (replacingPath && key.toUpperCase() === 'PATH') continue;
            env[key] = value;
        }
        return Object.assign(env, {
            NODE_PATH: process.env.HAR_CAPTURE_TEST_NODE_PATH || process.env.NODE_PATH || '',
            HAR_CAPTURE_TAILSCALE: NO_TAILSCALE
        }, over || {});
    }

    function integrationBlocker(cwd) {
        const resolved = capture.resolvePlaywright({ cwd, env: driverEnv() });
        if (!resolved.found) return 'the playwright module is not installed';
        const chrome = capture.findChromeExecutable();
        if (!chrome.path) return 'Google Chrome is not installed';
        return null;
    }

    /** A skip on a laptop; a FAILURE in CI, as in capture-ai-drive.test.js. */
    function blockedOrSkipped(name, blocker) {
        if (!blocker) return false;
        const ci = process.env.CI;
        if (ci !== undefined && String(ci).trim() && String(ci).trim().toLowerCase() !== 'false') {
            throw new Error(`${name} cannot run: ${blocker} -- in CI this is a failure, not a skip.`);
        }
        process.stderr.write(`  SKIP ${name} -- ${blocker}\n`);
        skipped.push(`${name} (${blocker})`);
        return true;
    }

    function findSessions(root) {
        const out = [];
        if (!fs.existsSync(root)) return out;
        for (const host of fs.readdirSync(root)) {
            const hostDir = path.join(root, host);
            if (!fs.statSync(hostDir).isDirectory()) continue;
            for (const stampDir of fs.readdirSync(hostDir)) {
                const dir = path.join(hostDir, stampDir);
                if (fs.existsSync(path.join(dir, 'session.json'))) out.push(dir);
            }
        }
        return out.sort();
    }

    function readJson(file) {
        try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch (e) { void e; return null; }
    }

    async function waitFor(predicate, timeoutMs) {
        const deadline = Date.now() + timeoutMs;
        while (Date.now() < deadline) {
            if (await predicate()) return true;
            await sleep(200);
        }
        return false;
    }

    function pidAlive(pid) {
        try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; }
    }

    /** Test cleanup only: the browser this test's recorder launched. */
    function killTree(pid) {
        try {
            if (process.platform === 'win32') {
                spawnSync('taskkill', ['/PID', String(pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true });
            } else {
                process.kill(-pid, 'SIGKILL');
            }
        } catch (e) { void e; }
        try { process.kill(pid, 'SIGKILL'); } catch (e) { void e; }
    }

    async function cdpAnswers(endpoint) {
        try {
            const controller = new AbortController();
            const timer = setTimeout(() => controller.abort(), 2000);
            const res = await fetch(`${endpoint}/json/version`, { signal: controller.signal });
            clearTimeout(timer);
            return res.ok;
        } catch (e) { void e; return false; }
    }

    /**
     * The local site. `/api/limited` throttles its first request with 429 and
     * Retry-After: 3 and answers after; `/challenge` is a bot-check
     * interstitial. `limitedHits` is when each request reached the SERVER --
     * a click the browser held is not here until the hold lets it go.
     */
    async function fixtureServer() {
        const limitedHits = [];
        const server = http.createServer((req, res) => {
            if (req.url.startsWith('/api/search')) {
                res.writeHead(200, { 'content-type': 'application/json' });
                res.end(JSON.stringify({ results: [{ id: 1, name: 'fixture rate' }] }));
                return;
            }
            if (req.url.startsWith('/api/limited')) {
                limitedHits.push(Date.now());
                if (limitedHits.length === 1) {
                    res.writeHead(429, { 'content-type': 'application/json', 'retry-after': '3' });
                    res.end(JSON.stringify({ error: 'too many requests' }));
                    return;
                }
                res.writeHead(200, { 'content-type': 'application/json' });
                res.end(JSON.stringify({ ok: true }));
                return;
            }
            if (req.url.startsWith('/challenge')) {
                res.writeHead(200, { 'content-type': 'text/html' });
                res.end('<html><head><title>Just a moment...</title></head><body>Checking your browser</body></html>');
                return;
            }
            res.writeHead(200, { 'content-type': 'text/html' });
            res.end('<html><body><h1>ai drive fixture</h1>' +
                '<button id="search" onclick="fetch(\'/api/search?q=one-way\')">search</button>' +
                '<button id="limited" onclick="fetch(\'/api/limited?q=again\')">limited</button>' +
                '</body></html>');
        });
        await new Promise((r) => server.listen(0, '127.0.0.1', r));
        return { server, uri: `http://127.0.0.1:${server.address().port}/`, limitedHits };
    }

    /** The recorder, under -AI, with the stub agent and a headless browser; no TTY. */
    function startStubDrivenCapture(repo, uri, env) {
        return spawn(process.execPath, [
            '-r', HEADLESS_PRELOAD, '-r', STUB_AGENT_PRELOAD, captureJs, 'start',
            '--uri', uri,
            '--describe', 'ai drive: search once on the fixture page',
            '--ai',
            '--profile', path.join(repo, '.chrome-profile'),
            '--snapshot-seconds', '1',
            '--no-wait'
        ], { cwd: repo, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env });
    }

    async function waitForRecordingSession(repo, recorder, errText) {
        const deadline = Date.now() + 90000;
        while (Date.now() < deadline) {
            const found = findSessions(path.join(repo, '.har-captures'));
            if (found.length) {
                const doc = readJson(path.join(found[0], 'session.json'));
                if (doc && doc.startedUtc) return { sessionDir: found[0], session: doc };
            }
            assert.ok(recorder.exitCode === null,
                `the recorder exited (${recorder.exitCode}) before it recorded anything:\n${errText()}`);
            await sleep(250);
        }
        assert.fail(`no capture session appeared under ${repo}\n${errText()}`);
        return null;
    }

    /** GET or POST against the phone view, the way the phone's browser would. */
    function phoneRequest(url, opts = {}) {
        return new Promise((resolve, reject) => {
            const u = new URL(url);
            const req = http.request({ host: u.hostname, port: u.port, path: u.pathname, method: opts.method || 'GET',
                headers: opts.headers || {} }, (res) => {
                if (opts.stream) { resolve(res); return; }
                let body = '';
                res.setEncoding('utf8');
                res.on('data', (d) => { body += d; });
                res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body }));
            });
            req.on('error', reject);
            if (opts.body) req.write(opts.body);
            req.end();
        });
    }

    return {
        skipped, scratch, makeRepo, pathWithoutAgent, agentOnPath, driverEnv, integrationBlocker,
        blockedOrSkipped, readJson, waitFor, pidAlive, killTree, cdpAnswers, fixtureServer,
        startStubDrivenCapture, waitForRecordingSession, phoneRequest,
        cleanup() { try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch (e) { void e; } }
    };
}

module.exports = { createE2eKit, NO_TAILSCALE };
