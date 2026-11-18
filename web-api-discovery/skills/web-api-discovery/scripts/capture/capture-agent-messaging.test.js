#!/usr/bin/env node
// Behavior tests for the conversation with the -AI agent: the recorder TELLS
// it about a pause, the end of a pause, a hand-over and the end of the capture
// -- and never ends it. Zero-dep; runs with `node capture-agent-messaging.test.js`.
//
// Every case drives capture-har.js's own driveWithAgent against a FAKE agent
// (the injected `spawn`), speaking the stream-json the CLI speaks -- measured
// against claude 2.1.325, see agent-stream.js.
//
// FALSIFIERS (fail before this change, pass after):
//   G1  the prompt goes in as the first stream-json message on stdin, and
//       stdin stays open
//   G2  a pause: the agent is told, its turn ending meanwhile is a YIELD (the
//       drive goes on), the end of the pause is told, and only a turn that
//       ends with nothing waiting and every message acknowledged is the agent
//       finishing -- then stdin is closed and it exits by itself
//   G3  a hand-over and a hand-back are told; the end of the capture is told
//       and stdin closed
//   G4  NOTHING IS EVER KILLED: no kill() on the child in any path, and no
//       terminating call anywhere in capture-har.js
//   G5  the step log stays READABLE: the agent's words, one line per tool call
//       (never what it typed, never a query string), one per turn, the
//       recorder's messages -- no JSON; the raw events go to a side file with
//       screenshots dropped
//   G6  a turn that ends while a message is still unacknowledged is not the
//       end -- and an acknowledgement that never comes is waited for only so long
//   G7  a child gone from under its stdin (EPIPE) does not crash the recorder
//   G8  the messages say what the owner asked them to say
//   G9  once the message the grace was waiting for is acknowledged, the agent
//       is working: a quiet stretch longer than the grace does not end it
//   G10 who is driving: without -AI it is a person, terminal or not (advice,
//       never a hold); under -AI an agent whose input is closed is not told,
//       and one that has exited is not driving

'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { EventEmitter } = require('events');
const { Writable } = require('stream');

const capture = require(path.join(__dirname, 'capture-har.js'));
const agentStream = require(path.join(__dirname, 'agent-stream.js'));

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'capture-agent-msg-'));
let seq = 0;
function sessionFor(name) {
    const dir = path.join(tmpRoot, `${name}-${seq++}`);
    fs.mkdirSync(dir, { recursive: true });
    return {
        agent: 'claude', sessionDir: dir,
        agentLogPath: path.join(dir, 'agent-log.txt'),
        agentStreamPath: path.join(dir, 'agent-stream.jsonl'),
        agentPromptPath: path.join(dir, 'agent-prompt.md')
    };
}

const tick = (ms = 10) => new Promise((r) => setTimeout(r, ms));

/**
 * A fake `claude -p --input-format stream-json ...`: it records what arrives
 * on stdin, replays each message the way --replay-user-messages does, and
 * exits 0 when stdin closes -- unless killed, which it counts.
 */
function fakeAgent() {
    const proc = new EventEmitter();
    proc.pid = 5150;
    proc.stdout = new EventEmitter();
    proc.stderr = new EventEmitter();
    const received = [];
    const state = { kills: 0, stdinEnded: false, args: null, options: null };
    let buf = '';
    proc.stdin = new Writable({
        write(chunk, enc, cb) {
            buf += chunk.toString();
            let i;
            while ((i = buf.indexOf('\n')) >= 0) {
                const line = buf.slice(0, i);
                buf = buf.slice(i + 1);
                const ev = JSON.parse(line);
                received.push(ev);
                // The acknowledgement, on the agent's own schedule.
                setImmediate(() => out(Object.assign({}, ev, { isReplay: true })));
            }
            cb();
        },
        final(cb) {
            state.stdinEnded = true;
            setTimeout(() => { proc.emit('exit', 0, null); proc.emit('close', 0, null); }, 5);
            cb();
        }
    });
    proc.kill = () => { state.kills++; return true; };
    const out = (ev) => proc.stdout.emit('data', JSON.stringify(ev) + '\n');
    const text = (t) => out({ type: 'assistant', message: { content: [{ type: 'text', text: t }] } });
    const result = (over) => out(Object.assign({ type: 'result', subtype: 'success', is_error: false }, over));
    const spawn = (command, args, options) => { state.args = args; state.options = options; return proc; };
    const texts = () => received.map((ev) => ev.message.content[0].text);
    return { proc, spawn, received, texts, state, out, text, result };
}

function drive(name, fake, opts = {}) {
    const session = sessionFor(name);
    const control = {};
    const done = capture.driveWithAgent(session, 'THE PROMPT', Object.assign(
        { spawn: fake.spawn, drainMs: 50, ackGraceMs: 200, control }, opts));
    return { session, control, done, log: () => fs.readFileSync(session.agentLogPath, 'utf8') };
}

// ---------------------------------------------------------------------------

test('G1: the prompt is the first stream-json message on stdin, and stdin stays open', async () => {
    const fake = fakeAgent();
    const { control, done } = drive('g1', fake);
    await tick();
    assert.deepStrictEqual(fake.state.options.stdio, ['pipe', 'pipe', 'pipe'], 'stdin is a pipe now, not ignored');
    assert.ok(!fake.state.options.shell, 'still no shell');
    assert.deepStrictEqual(fake.received[0], { type: 'user', message: { role: 'user', content: [{ type: 'text', text: 'THE PROMPT' }] } });
    assert.ok(fake.state.args.includes('--input-format') && fake.state.args.includes('stream-json'));
    assert.strictEqual(control.messaging, true);
    assert.strictEqual(fake.state.stdinEnded, false, 'stdin stays open for the drive');
    control.end('bye');
    await done;
});

test('G2: a pause is told, a turn ending meanwhile is a yield, and the agent finishing closes its session', async () => {
    const fake = fakeAgent();
    const { control, done, log } = drive('g2', fake);
    let resolved = false;
    done.then(() => { resolved = true; });
    await tick();
    fake.text('STEP: searched DCA to IAD');
    // The site said 429: the recorder tells the agent to wait...
    const pause = agentStream.agentMessages.pause({ status: 429, waitMs: 300000, until: '10:42', named: true });
    assert.strictEqual(control.wait(pause), true);
    await tick();
    assert.strictEqual(fake.texts()[1], pause);
    // ...and the agent ends its turn, as told. That is a yield, not the end.
    fake.result();
    await tick(50);
    assert.strictEqual(fake.state.stdinEnded, false, 'a turn that ends during a pause is the agent waiting');
    assert.strictEqual(resolved, false);
    // The pause runs out: the recorder tells it to carry on.
    const resume = agentStream.agentMessages.resume({ pausedMs: 300000 });
    control.carryOn(resume);
    await tick();
    assert.strictEqual(fake.texts()[2], resume);
    fake.text('STEP: re-checked the page; continuing');
    fake.text('SUMMARY: done');
    fake.result();
    const result = await done;
    assert.strictEqual(fake.state.stdinEnded, true, 'finished: stdin closed, so the session ends by itself');
    assert.strictEqual(result.status, 0);
    assert.strictEqual(result.finishedWork, true);
    assert.strictEqual(capture.agentExitEnding(result, null).reason, 'agent-finished');
    assert.strictEqual(fake.state.kills, 0, 'never killed');
    assert.match(log(), /capture-har: the agent has finished; closing its session\./);
});

test('G3: a hand-over and a hand-back are told; the end of the capture is told and stdin closed', async () => {
    const fake = fakeAgent();
    const { control, done } = drive('g3', fake);
    await tick();
    const handOver = agentStream.agentMessages.handOver({ reason: agentStream.handOverPhrase({ why: 'challenge' }) });
    control.wait(handOver);
    fake.result();
    await tick(50);
    control.carryOn(agentStream.agentMessages.handBack());
    await tick();
    fake.text('STEP: the check is gone; continuing');
    control.end(agentStream.agentMessages.ended());
    const result = await done;
    assert.deepStrictEqual(fake.texts().slice(1), [
        'The site showed a "prove you\'re human" check. A person is taking over the browser. Don\'t use the ' +
            'browser; end your turn and wait. I\'ll tell you when they are done.',
        'The person has finished. Re-check the page and continue where you left off.',
        'The capture has ended; stop. Don\'t use the browser again, and end your turn.'
    ]);
    assert.strictEqual(fake.state.stdinEnded, true);
    assert.strictEqual(result.status, 0, 'it ended by itself');
    assert.strictEqual(fake.state.kills, 0, 'never killed');
    assert.strictEqual(control.carryOn('too late'), false, 'nothing can be sent once stdin is closed');
});

test('G4: nothing is ever killed -- not the child, and nothing in capture-har.js terminates a process', async () => {
    // Every path through the drive, by a fake that counts kills.
    const fake = fakeAgent();
    const { control, done } = drive('g4', fake);
    await tick();
    control.wait('pause');
    control.wait('hand-over');
    control.end('end');
    await done;
    assert.strictEqual(fake.state.kills, 0);
    // And the source: the one kill() that used to be here is gone.
    const source = fs.readFileSync(path.join(__dirname, 'capture-har.js'), 'utf8');
    assert.ok(!/proc\.kill\(|\.kill\(\)/.test(source), 'no child.kill() anywhere in the recorder');
    for (const m of source.matchAll(/process\.kill\(([^)]*)\)/g)) {
        assert.match(m[1], /,\s*0\s*$/, `process.kill(${m[1]}) must be a liveness probe (signal 0)`);
    }
    assert.ok(!/SIGKILL|SIGTERM|taskkill/.test(source));
});

test('G5: the step log is readable text; the raw events go beside it', async () => {
    const fake = fakeAgent();
    const { session, control, done, log } = drive('g5', fake);
    await tick();
    fake.out({ type: 'system', subtype: 'init', model: 'claude-test' });
    fake.out({ type: 'system', subtype: 'hook_started' });
    fake.text('I will search for one-way rentals.');
    fake.out({ type: 'assistant', message: { content: [
        { type: 'thinking', thinking: 'secret reasoning' },
        { type: 'tool_use', name: 'mcp__claude-in-chrome__navigate',
            input: { url: 'https://www.example.com/search?email=someone%40example.com&q=DCA' } },
        { type: 'tool_use', name: 'mcp__claude-in-chrome__computer', input: { action: 'type', text: 'Washington Dulles' } },
        { type: 'tool_use', name: 'mcp__claude-in-chrome__computer', input: { action: 'left_click', coordinate: [120, 300] } }
    ] } });
    fake.out({ type: 'user', message: { content: [{ type: 'tool_result', is_error: true,
        content: [{ type: 'text', text: 'Navigation timed out\nstack...' }] }] } });
    fake.out({ type: 'user', message: { content: [{ type: 'tool_result', content: [
        { type: 'image', source: { type: 'base64', data: 'A'.repeat(5000) } }] }] } });
    fake.text('SUMMARY: searched once.');
    fake.result();
    await done;
    const text = log();
    assert.match(text, /\[agent\] session started \(claude-test\)/);
    assert.match(text, /I will search for one-way rentals\./);
    assert.match(text, /  > navigate https:\/\/www\.example\.com\/search\n/, `the tool line: \n${text}`);
    assert.match(text, /  > computer type \(17 chars typed\)/);
    assert.match(text, /  > computer left_click at 120,300/);
    assert.match(text, /  ! the tool failed: Navigation timed out\n/);
    assert.match(text, /SUMMARY: searched once\./);
    assert.match(text, /\[agent\] turn ended \(success\)/);
    assert.match(text, /capture-har -> agent: \(the prompt in agent-prompt\.md\)/, 'the prompt is named, not pasted');
    assert.doesNotMatch(text, /Washington Dulles|email|q=DCA|secret reasoning|hook_started/,
        'never what it typed, never a query string, never its thinking or bookkeeping');
    assert.ok(!text.split('\n').some((l) => l.trimStart().startsWith('{')), `no JSON in the step log:\n${text}`);
    const raw = fs.readFileSync(session.agentStreamPath, 'utf8');
    assert.ok(raw.split('\n').filter(Boolean).every((l) => JSON.parse(l)), 'the side file is JSON lines');
    assert.match(raw, /\[5000 characters omitted\]/, 'a screenshot is not kept');
    assert.strictEqual(control.inputOpen(), false, 'and after the end, nothing more can be sent');
});

test('G6: a turn ending with a message unacknowledged is not the end; a missing ack is waited for only so long', async () => {
    const fake = fakeAgent();
    // An agent that does NOT acknowledge the resume (as if it arrived as the turn ended).
    const realOut = fake.out;
    let swallow = false;
    fake.proc.stdout.emit = ((orig) => function (event, data) {
        if (swallow && event === 'data' && /"isReplay":true/.test(String(data))) return true;
        return orig.call(this, event, data);
    })(fake.proc.stdout.emit);
    const { control, done } = drive('g6', fake, { ackGraceMs: 300 });
    let resolved = false;
    done.then(() => { resolved = true; });
    await tick();
    swallow = true;
    control.carryOn('The wait is over.');
    await tick();
    fake.result();
    await tick(100);
    assert.strictEqual(fake.state.stdinEnded, false, 'a message is still on its way: another turn is coming');
    await tick(400);
    assert.strictEqual(fake.state.stdinEnded, true, 'but not for ever');
    await done;
    assert.ok(resolved);
    void realOut;
});

test('G9: once the awaited message is acknowledged the agent is working, however long it then stays quiet', async () => {
    // The grace for an unacknowledged message is armed when a turn ends with
    // one outstanding. When that acknowledgement ARRIVES, the agent has taken
    // the message in and a turn is starting on it -- and a turn may be quiet
    // for longer than the grace (a long first thinking block). It must not be
    // declared finished, and its stdin closed, in the middle of that turn.
    const fake = fakeAgent();
    const held = [];
    let holdAcks = false;
    fake.proc.stdout.emit = ((orig) => function (event, data) {
        if (holdAcks && event === 'data' && /"isReplay":true/.test(String(data))) { held.push(data); return true; }
        return orig.call(this, event, data);
    })(fake.proc.stdout.emit);
    const { control, done, log } = drive('g9', fake, { ackGraceMs: 200 });
    await tick();
    holdAcks = true;
    control.carryOn('The wait is over.');   // sent as the turn was ending
    await tick();
    fake.result();                          // the turn ends with it unacknowledged: the grace starts
    await tick(50);
    holdAcks = false;
    for (const d of held.splice(0)) fake.proc.stdout.emit('data', d);   // ...and now it is acknowledged
    await tick(450);                        // then nothing at all, for twice the grace
    assert.strictEqual(fake.state.stdinEnded, false, 'mid-turn: its stdin must not be closed');
    assert.doesNotMatch(log(), /the agent has finished/);
    fake.text('STEP: re-checked the page; continuing');
    fake.result();
    await done;
    assert.strictEqual(fake.state.stdinEnded, true, 'the turn that ends with everything acknowledged is the end');
    assert.match(log(), /the agent has finished/);
});

test('G10: who is driving -- without -AI it is a person, terminal or not; an agent that cannot be told is not told', () => {
    const d = (over) => capture.captureDriver(Object.assign(
        { ai: false, agentLaunched: false, agentRunning: false, canMessage: false }, over));
    // WITHOUT -AI IT IS A PERSON'S CAPTURE. No TTY is not evidence of
    // automation: an AI session or a scheduled task that starts the recorder
    // for somebody to browse by hand has none either. Advice, never a hold.
    assert.strictEqual(d({}), 'you');
    assert.strictEqual(capture.captureDriver({ ai: false, humanAtTerminal: false }), 'you', 'whatever else is said');
    // Under -AI: not launched yet, running and reachable, running and not.
    assert.strictEqual(d({ ai: true }), 'agent-unstarted');
    assert.strictEqual(d({ ai: true, agentLaunched: true, agentRunning: true, canMessage: true }), 'agent');
    assert.strictEqual(d({ ai: true, agentLaunched: true, agentRunning: true, canMessage: false }), 'agent-unmessaged',
        'an agent whose input has closed is not "told to wait"');
    assert.strictEqual(d({ ai: true, agentLaunched: true, agentRunning: false }), 'agent-gone',
        'an agent that has exited is not driving');
});

test('G7: a child gone from under its stdin does not take the recorder down', async () => {
    const fake = fakeAgent();
    const { control, done, log } = drive('g7', fake);
    await tick();
    fake.proc.stdin.emit('error', Object.assign(new Error('write EPIPE'), { code: 'EPIPE' }));
    assert.strictEqual(control.wait('pause'), false, 'nothing more is sent');
    assert.strictEqual(control.messaging, true, 'messaging was on; the input is what closed');
    fake.proc.emit('exit', 1, null);
    fake.proc.emit('close', 1, null);
    const result = await done;
    assert.strictEqual(result.status, 1);
    assert.match(log(), /the agent's input closed \(EPIPE\)/);
});

test('G8: the messages say what the owner asked them to say', () => {
    const m = agentStream.agentMessages;
    assert.strictEqual(m.pause({ status: 429, waitMs: 300000, until: '10:42', named: true }),
        'The site asked us to slow down (HTTP 429, wait 300 s). The capture is paused until 10:42 local. ' +
        'Don\'t use the browser; end your turn and wait; I\'ll tell you when to continue.');
    assert.strictEqual(m.resume({ pausedMs: 300000 }),
        'The wait is over (paused 5 min). Re-check the page, then continue where you left off.');
    assert.match(m.pause({ status: 429, waitMs: 60000, until: '10:38', named: false }),
        /named no wait, so the capture backs off for 60 s\. The capture is paused until 10:38 local/);
    assert.match(m.handOver({ reason: agentStream.handOverPhrase({ why: 'blocked' }) }),
        /^The site showed a block page .* A person is taking over the browser\. Don't use the browser; end your turn and wait\./);
    assert.strictEqual(m.handBack(), 'The person has finished. Re-check the page and continue where you left off.');
    assert.match(m.ended(), /^The capture has ended; stop\./);
    // Every reason a hand-over can have is said as itself, not as the catch-all.
    for (const why of ['challenge', 'blocked', 'persistent', 'zero-wait', 'retry-after-too-long']) {
        assert.notStrictEqual(agentStream.handOverPhrase({ why }), agentStream.handOverPhrase({ why: 'unknown' }),
            `'${why}' has its own phrase`);
    }
});

(async () => {
    let passed = 0;
    const failures = [];
    for (const { name, fn } of queued) {
        try { await fn(); passed++; } catch (e) {
            failures.push(name);
            process.stderr.write(`FAIL: ${name}\n  ${e.message}\n`);
            if (e.stack) process.stderr.write(e.stack.split('\n').slice(1, 4).join('\n') + '\n');
        }
    }
    try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch (e) { void e; }
    if (failures.length) {
        process.stderr.write(`\n${failures.length} of ${queued.length} capture-agent-messaging tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    console.log(`All capture-agent-messaging tests passed (${passed})`);
})();
