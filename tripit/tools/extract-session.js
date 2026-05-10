// Extract a fresh TripIt session (Cookie + X-CSRF-Token-WA) for the .NET MCP server.
//
// Strategy:
//   1. Spawn / attach to a Chrome instance using ./.chrome-cdp-profile (the same profile
//      created by capture-cdp.js), so the user is already logged in.
//   2. Open https://www.tripit.com/app/trips and intercept the SPA's own /api/v2/* XHR.
//   3. Save its Cookie + X-CSRF-Token-WA headers to ~/.tripit-mcp/session.json.
//
// Re-run this any time the .NET server reports a 302/401 (session expired).

const { chromium } = require('playwright');
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const http = require('http');
const os = require('os');

const ROOT         = path.resolve(__dirname, '..');
const CHROME_EXE   = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const PROFILE_DIR  = path.join(ROOT, '.chrome-cdp-profile');
const DEBUG_PORT   = 9222;
const OUT_FILE     = path.join(os.homedir(), '.tripit-mcp', 'session.json');

function isCdpUp(port) {
  return new Promise(resolve => {
    const req = http.get({ host: '127.0.0.1', port, path: '/json/version', timeout: 1000 },
      r => { r.resume(); resolve(true); });
    req.on('error', () => resolve(false));
    req.on('timeout', () => { req.destroy(); resolve(false); });
  });
}
function waitForCdp(port, timeout = 30000) {
  const deadline = Date.now() + timeout;
  return new Promise((resolve, reject) => {
    (function tick() {
      isCdpUp(port).then(up => {
        if (up) return resolve();
        if (Date.now() > deadline) return reject(new Error('CDP not up'));
        setTimeout(tick, 500);
      });
    })();
  });
}

(async () => {
  fs.mkdirSync(PROFILE_DIR, { recursive: true });

  const running = await isCdpUp(DEBUG_PORT);
  if (!running) {
    console.log('Spawning Chrome with --remote-debugging-port=' + DEBUG_PORT);
    const child = spawn(CHROME_EXE, [
      '--remote-debugging-port=' + DEBUG_PORT,
      '--user-data-dir=' + PROFILE_DIR,
      '--no-first-run', '--no-default-browser-check',
      'https://www.tripit.com/app/trips',
    ], { detached: true, stdio: 'ignore' });
    child.unref();
    await waitForCdp(DEBUG_PORT);
  }

  const browser = await chromium.connectOverCDP('http://127.0.0.1:' + DEBUG_PORT);
  const ctx = browser.contexts()[0];
  let page = ctx.pages().find(p => /tripit\.com/.test(p.url())) || await ctx.newPage();

  if (!/tripit\.com\/app/.test(page.url())) {
    await page.goto('https://www.tripit.com/app/trips', { waitUntil: 'domcontentloaded' });
  }

  console.log('Waiting for /api/v2/* request to capture headers...');
  let captured = null;
  const onReq = req => {
    const url = req.url();
    if (!/tripit\.com\/api\/v2\//.test(url)) return;
    const h = req.headers();
    if (!h['cookie'] || !h['x-csrf-token-wa']) return;
    captured = { cookie: h['cookie'], csrf_token: h['x-csrf-token-wa'], sample_url: url };
  };
  page.on('request', onReq);

  try { await page.goto('https://www.tripit.com/app/dashboard', { waitUntil: 'domcontentloaded' }); } catch {}
  try { await page.goto('https://www.tripit.com/app/trips', { waitUntil: 'domcontentloaded' }); } catch {}

  const deadline = Date.now() + 20000;
  while (!captured && Date.now() < deadline) {
    await new Promise(r => setTimeout(r, 250));
  }

  page.off('request', onReq);
  if (!captured) {
    console.error('Could not capture /api/v2/* headers. Make sure you are logged in.');
    process.exit(1);
  }

  fs.mkdirSync(path.dirname(OUT_FILE), { recursive: true });
  fs.writeFileSync(OUT_FILE, JSON.stringify({
    cookie: captured.cookie,
    csrf_token: captured.csrf_token,
    captured_from: captured.sample_url,
    captured_at: new Date().toISOString(),
  }, null, 2));

  console.log('Wrote', OUT_FILE);
  console.log('  cookie length :', captured.cookie.length);
  console.log('  csrf token    :', captured.csrf_token.slice(0, 8) + '…');

  browser.close().catch(() => {});
})().catch(err => { console.error(err); process.exit(1); });
