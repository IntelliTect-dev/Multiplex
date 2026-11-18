#!/usr/bin/env node
/**
 * capture-cdp.js -- Playwright CDP-attach HAR capture baseline.
 *
 * Launches Chromium via Playwright. When --storage-state is provided,
 * loads the storage state and skips interactive login. Captures all
 * network traffic to a HAR file. Polite-crawl defaults: descriptive
 * User-Agent and ~1 req/sec throttle on automated traversal.
 *
 * Its navigation is paced and its responses are read for throttling, by the
 * same pacing.js the recorder uses: a 429 or a 503 with Retry-After pauses; a
 * block, a challenge or throttling that persists stops automated browsing and
 * says so -- the browser is left to the person (headed), or the run ends with
 * exit 3 (headless, where there is nobody to hand it to). Nothing here works
 * around a block.
 *
 * Usage:
 *   node capture-cdp.js --url <baseUrl> --out <out.har> [--storage-state <path>] [--headless]
 *                       [--throttle-ms 1000] [--pace off|human|<spec>]
 *
 *   --throttle-ms  a random delay of up to this many ms before every request;
 *                  0 turns it off (and with it the request interception).
 *   --pace         as capture-har.js: the wait between navigations; `off`
 *                  for tests.
 *
 * Notes:
 *   - This is a template consumed by the web-api-discovery agent. The
 *     agent substitutes default values (project name in UA, output path)
 *     at scaffold time; the script itself reads everything from CLI args
 *     so it is testable with `node --check` and runnable standalone.
 *   - Requires `playwright` to be installed at runtime in the consumer
 *     project (not in this repo).
 *
 * Exit codes:
 *   0 -- HAR written successfully
 *   1 -- argument or runtime error
 *   3 -- headless, and the site throttled or blocked the session; the HAR
 *        holds what was recorded, refusals included
 */

'use strict';

const path = require('path');
const pacing = require(path.join(__dirname, 'pacing.js'));

function parseArgs(argv) {
    const out = { headless: false, 'throttle-ms': 1000 };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--headless') { out.headless = true; continue; }
        if (a === '--validate-only') { out['validate-only'] = true; continue; }
        if (a.startsWith('--')) {
            out[a.slice(2)] = argv[i + 1];
            i++;
        }
    }
    return out;
}

/**
 * Whether a response's body is read for the throttle check: where the body is
 * what tells a block or a challenge apart. An HTML main-frame document (a page
 * that answered 200 can be an interstitial), and a refusal from the site --
 * a 403 or 503 -- WHATEVER its content type, because an edge refusing an XHR
 * answers it in JSON as often as in HTML.
 */
function readsBodyFor({ status, contentType, isDocument, onSite }) {
    if (isDocument && /html/i.test(contentType || '')) return true;
    return (isDocument || onSite) && (status === 403 || status === 503);
}

async function main() {
    const args = parseArgs(process.argv.slice(2));
    if (!args.url || !args.out) {
        console.error('usage: node capture-cdp.js --url <baseUrl> --out <out.har> [--storage-state <path>] [--validate-only] [--headless] [--throttle-ms 1000] [--pace off|human|<spec>]');
        process.exit(1);
    }
    // ZERO MEANS ZERO. This was `Number(...) || 1000`, which read an explicit
    // `--throttle-ms 0` as "use the default" -- there was no way to turn it off.
    const throttleMs = Number(args['throttle-ms']);
    if (!Number.isFinite(throttleMs) || throttleMs < 0) {
        console.error(`capture-cdp: --throttle-ms must be a number of milliseconds, 0 or more (got '${args['throttle-ms']}')`);
        process.exit(1);
    }
    const paced = pacing.parsePace(args.pace);
    if (paced.error) {
        console.error('capture-cdp: ' + paced.error);
        process.exit(1);
    }
    for (const note of paced.notes || []) console.error('capture-cdp: ' + note);

    // --validate-only: argument-validation mode. Does not launch the browser.
    // Verifies that the named --storage-state file exists (if provided) and
    // that the CLI argument shape is sane. Exits 0 on pass, non-zero with a
    // clear error on failure. Used by tests and CI smoke checks.
    if (args['validate-only']) {
        if (args['storage-state']) {
            const ssPath = path.resolve(args['storage-state']);
            if (!require('fs').existsSync(ssPath)) {
                console.error('capture-cdp: --storage-state file not found: ' + ssPath);
                process.exit(2);
            }
        }
        console.log('capture-cdp: --validate-only OK');
        process.exit(0);
    }

    // The same four-location search the recorder uses (#512), and for the same
    // reason: a bare `require` here resolved upward from THIS file, so an
    // install in the folder the operator was standing in was invisible, and
    // the advice to make one there could not work.
    const nodeDep = require(path.join(__dirname, '..', 'lib', 'node-dependency.js'));
    const resolved = nodeDep.resolvePlaywright({ toolDir: __dirname });
    if (!resolved.found) {
        console.error('capture-cdp: ' + nodeDep.describeMissing(nodeDep.PLAYWRIGHT, resolved,
            { browser: nodeDep.chromiumPresent({}) }));
        process.exit(1);
    }
    // The browser binary is its own fact here too. Without this the run reaches
    // launchPersistentContext and fails with Playwright's raw error, which is
    // the pre-fix experience the recorder no longer has -- and the two entry
    // points refusing on different terms is what #512 set out to end.
    if (!nodeDep.chromiumPresent({})) {
        console.error('capture-cdp: the playwright module is installed, but its Chromium ' +
            'build is not.\n  ' + nodeDep.BROWSER_INSTALL_COMMAND);
        process.exit(1);
    }
    const playwright = require(resolved.path);

    const projectUA = process.env.CAPTURE_USER_AGENT
        || 'web-api-discovery/0.1 (+https://github.com/IntelliTect-Samples/web-api-discovery)';

    const browser = await playwright.chromium.launch({ headless: !!args.headless });
    const contextOptions = {
        userAgent: projectUA,
        recordHar: { path: path.resolve(args.out), mode: 'full', content: 'embed' }
    };
    if (args['storage-state']) {
        contextOptions.storageState = args['storage-state'];
    }

    const context = await browser.newContext(contextOptions);
    const page = await context.newPage();

    // Polite crawl: throttle requests by inserting a small delay.
    if (throttleMs > 0) {
        await context.route('**/*', async (route) => {
            await new Promise((r) => setTimeout(r, Math.floor(Math.random() * throttleMs)));
            return route.continue();
        });
    }

    // PACED, AND WATCHING FOR THE SITE SAYING NO (pacing.js). Only the site's
    // own responses count -- a main-frame document, or the target's host.
    const pacer = pacing.createPacer(paced.pace);
    const monitor = pacing.createThrottleMonitor();
    const site = new URL(args.url).hostname.toLowerCase().replace(/^www\./, '');
    let handedOver = null;
    context.on('response', (response) => {
        void (async () => {
            try {
                const request = response.request();
                const frame = request.frame();
                const isDocument = request.isNavigationRequest() && !!frame && frame.parentFrame() === null;
                const host = new URL(response.url()).hostname.toLowerCase();
                const headers = await response.allHeaders().catch(() => ({}));
                const status = response.status();
                const onSite = host === site || host.endsWith('.' + site);
                const body = readsBodyFor({ status, contentType: headers['content-type'], isDocument, onSite })
                    ? await response.body().catch(() => null) : null;
                const decision = monitor.observe(pacing.classifyResponse({
                    url: response.url(), status, headers, mimeType: headers['content-type'], body, isDocument, onSite
                }));
                if (!decision || decision.action === 'none') return;
                if (decision.action === 'pause') pacer.pauseFor(decision.waitMs);
                if (decision.action === 'hand-over') handedOver = decision;
                const say = args.headless
                    ? { tool: 'capture-cdp', noHuman: 'Nobody can take over a headless browser, so this run ends (exit 3).' }
                    : { tool: 'capture-cdp', finish: 'close the window to finish' };
                for (const [, line] of pacing.throttleLines(decision, 'script', say)) {
                    console.error(line);
                }
            } catch (e) {
                // Reading a response for throttling must never cost the capture.
                void e;
            }
        })();
    });

    console.log(`capture-cdp: navigating to ${args.url}`);
    await pacer.navigate(() => page.goto(args.url, { waitUntil: 'networkidle' }));

    if (!args.headless && !args['storage-state']) {
        console.log('capture-cdp: interact with the browser to drive the capture. Press Ctrl+C when done.');
        // Keep the process alive until the user closes the browser window.
        await new Promise((resolve) => {
            page.on('close', resolve);
            context.on('close', resolve);
            browser.on('disconnected', resolve);
        });
    }

    await context.close();
    await browser.close();
    console.log(`capture-cdp: HAR written to ${args.out}`);
    process.exit(handedOver && args.headless ? 3 : 0);
}

// A CLI, and a module (issue #456). The rejection handler stays with the call
// it handles, inside the guard: `main` is async, so without it the promise --
// and the browser launch behind it -- started during the require.
if (require.main === module) {
    main().catch((err) => {
        console.error('capture-cdp: ' + (err && err.stack ? err.stack : err));
        process.exit(1);
    });
}

module.exports = { main, parseArgs, readsBodyFor };
