// Quick smoke test: speak MCP protocol over stdio and list tools.
const { spawn } = require('child_process');
const path = require('path');

const proj = path.join(__dirname, '..', 'src', 'TripItMcp', 'TripItMcp.csproj');
const child = spawn('dotnet', ['run', '--project', proj, '-c', 'Release', '--no-build'], {
  stdio: ['pipe', 'pipe', 'inherit'],
});

let buf = '';
const pending = new Map();
let nextId = 1;

child.stdout.on('data', chunk => {
  buf += chunk.toString('utf8');
  let nl;
  while ((nl = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, nl).trim();
    buf = buf.slice(nl + 1);
    if (!line) continue;
    let msg;
    try { msg = JSON.parse(line); } catch { continue; }
    if (msg.id != null && pending.has(msg.id)) {
      pending.get(msg.id)(msg);
      pending.delete(msg.id);
    }
  }
});

function send(method, params) {
  const id = nextId++;
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  return new Promise(res => pending.set(id, res));
}
function notify(method, params) {
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');
}

(async () => {
  const init = await send('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'smoke', version: '1' },
  });
  console.log('initialize:', JSON.stringify(init.result?.serverInfo));
  notify('notifications/initialized');

  const list = await send('tools/list', {});
  const tools = list.result?.tools || [];
  console.log('tool count:', tools.length);
  for (const t of tools) console.log(' -', t.name);

  const status = await send('tools/call', {
    name: 'tripit_session_status', arguments: {},
  });
  const text = status.result?.content?.[0]?.text;
  console.log('session_status ->', text);

  child.stdin.end();
  setTimeout(() => process.exit(0), 500);
})().catch(e => { console.error(e); process.exit(1); });
