#!/usr/bin/env node
// Behavior tests for capture-run.js -- which run made a capture (issue #26).
// Zero-dep; runs with `node capture-run.test.js`.
//
// THE DEFECT. The capture store is shared and several sessions record into it
// at once, and nothing in a capture said which of them made it. A time window
// cannot select one session's captures, because the sessions interleave; a
// `describe` substring over-matched on its first real use; and three attempts
// by one session with one `describe` could not be told apart at all.
//
// So a capture carries two identities, and these tests pin both:
//
//   captureId   one per recorder invocation
//   run         everything ONE invoking session started, read from the
//               recorder's own process ancestry with nothing supplied by the
//               caller
//
// WHY THE RULE IS TESTED OVER SHAPES THAT WERE MEASURED. The anchor cannot be a
// fixed depth: on the machine this was written on the chain is
// `recorder <- pwsh <- cmd <- claude (one per session) <- claude (shared) <- sihost`.
// The direct parent is new for every command, and the top is shared by every
// session. R1-R3 are that shape.
//
// FALSIFIERS (fail before this change, pass after):
//   R1  an agent session's captures share a run, through command shells that
//       are new for every command
//   R2  the same session reached through a different shell is the same run
//   R3  two sessions under one shared host are two runs
//   R4  at a terminal the run is the terminal's own shell, however many shells
//       sit between it and the recorder
//   R5  two terminals are two runs
//   R6  a process id that was reused is a different run
//   R7  a driving script is its own run, and the session above it is in the
//       lineage
//   R8  a shell is recognised however the operating system spells it
//   R9  the known cases where a run is too COARSE, pinned so they are known
//   R10 the known case where a run is too FINE
//   R11 a chain that ends in shells, and a chain with nothing in it
//   C1  the Windows reader drops a parent that started after its child, and
//       two readers of one start time agree
//   C2  the /proc reader survives a process name with spaces and brackets
//   C3  the Linux chain stops below process 1, and a reboot is a different run
//   C4  a lookup that fails, hangs or is not available says so and never throws
//   S1  nothing that was read is recorded: no name, no process id, no start time
//   I1  a capture id is a new one every time
//   P1  two real invoking processes, interleaved, are told apart -- and a
//       shell between one of them and its capture changes nothing
//   P2  what a real lookup records is tokens and nothing else

'use strict';

const assert = require('assert');
const os = require('os');
const path = require('path');
const readline = require('readline');
const { spawn } = require('child_process');

const captureRun = require(path.join(__dirname, 'capture-run.js'));
const SUPPORT = path.join(__dirname, 'capture-run.test-support.js');

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

const TOKEN = /^[0-9a-f]{12}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

/** One ancestor, as a reader hands it over. Start times are opaque strings. */
function proc(pid, name, start) {
    return { pid, name, start: String(start) };
}

// The measured shape. `SESSION_A` and `SESSION_B` are the per-session agent
// processes; `HOST` is the application both run under.
const HOST = proc(37016, 'claude', 1000);
const SIHOST = proc(5348, 'sihost', 900);
const SESSION_A = proc(63044, 'claude', 5000);
const SESSION_B = proc(47568, 'claude', 6000);

function agentChain(session, shells) {
    return shells.concat([session, HOST, SIHOST]);
}

const NOT_A_TERMINAL = { stdinIsTty: false };
const A_TERMINAL = { stdinIsTty: true };

// ---------------------------------------------------------------------------
// The rule
// ---------------------------------------------------------------------------

test('R1: an agent session\'s captures share a run, through shells that are new every time', () => {
    const first = captureRun.resolveRun(
        agentChain(SESSION_A, [proc(71016, 'pwsh', 9001), proc(47152, 'cmd', 9000)]), NOT_A_TERMINAL);
    const second = captureRun.resolveRun(
        agentChain(SESSION_A, [proc(2892, 'pwsh', 9501), proc(1111, 'cmd', 9500)]), NOT_A_TERMINAL);

    assert.match(first.id, TOKEN, `a run id is 12 hex characters, got ${JSON.stringify(first)}`);
    assert.strictEqual(first.id, second.id,
        'the shells differ for every command an agent runs; the session above them does not');
    assert.strictEqual(first.basis, 'process');
    assert.strictEqual(first.id, captureRun.tokenFor(SESSION_A),
        'the run is the session process, not a shell below it and not the host above it');
});

test('R2: the same session reached through a different shell is the same run', () => {
    // What the same agent session looks like from its other command tool:
    // three bash processes where there were a pwsh and a cmd.
    const viaPowerShell = captureRun.resolveRun(
        agentChain(SESSION_A, [proc(71016, 'pwsh', 9001), proc(47152, 'cmd', 9000)]), NOT_A_TERMINAL);
    const viaBash = captureRun.resolveRun(
        agentChain(SESSION_A, [proc(2892, 'pwsh', 9703), proc(53616, 'bash', 9702),
            proc(69916, 'bash', 9701), proc(49416, 'bash', 9700)]), NOT_A_TERMINAL);
    assert.match(viaBash.id, TOKEN, `no run was resolved: ${JSON.stringify(viaBash)}`);
    assert.strictEqual(viaBash.id, viaPowerShell.id);
});

test('R3: two sessions under one shared host are two runs', () => {
    const a = captureRun.resolveRun(agentChain(SESSION_A, [proc(71016, 'pwsh', 9001)]), NOT_A_TERMINAL);
    const b = captureRun.resolveRun(agentChain(SESSION_B, [proc(71999, 'pwsh', 9002)]), NOT_A_TERMINAL);
    assert.notStrictEqual(a.id, b.id, 'the host is shared; it must not be what names the run');
    // And the reason the TOP of the chain would have been the wrong anchor:
    // both sessions have it.
    assert.ok(a.lineage.includes(captureRun.tokenFor(HOST)));
    assert.ok(b.lineage.includes(captureRun.tokenFor(HOST)));
});

test('R4: at a terminal the run is the terminal\'s own shell', () => {
    const tabShell = proc(15632, 'pwsh', 2000);
    const terminal = proc(900, 'WindowsTerminal', 1500);
    const explorer = proc(800, 'explorer', 1000);

    const typed = captureRun.resolveRun([tabShell, terminal, explorer], A_TERMINAL);
    assert.strictEqual(typed.id, captureRun.tokenFor(tabShell));
    assert.strictEqual(typed.basis, 'terminal');

    // `pwsh -File Invoke-HarCapture.ps1`, or a shell started inside the shell:
    // more shells between the recorder and the tab, and the same tab.
    const nested = captureRun.resolveRun(
        [proc(3001, 'pwsh', 2600), proc(3000, 'cmd', 2500), tabShell, terminal, explorer], A_TERMINAL);
    assert.strictEqual(nested.id, typed.id);
});

test('R5: two terminals are two runs', () => {
    const terminal = proc(900, 'WindowsTerminal', 1500);
    const one = captureRun.resolveRun([proc(15632, 'pwsh', 2000), terminal], A_TERMINAL);
    const two = captureRun.resolveRun([proc(17088, 'pwsh', 2001), terminal], A_TERMINAL);
    assert.notStrictEqual(one.id, two.id,
        'the terminal application hosts every tab; the tab\'s shell is what differs');
});

test('R6: a process id that was reused is a different run', () => {
    const then = captureRun.resolveRun([proc(71016, 'pwsh', 9001), proc(63044, 'claude', 5000)], NOT_A_TERMINAL);
    const later = captureRun.resolveRun([proc(71016, 'pwsh', 9001), proc(63044, 'claude', 777000)], NOT_A_TERMINAL);
    assert.notStrictEqual(then.id, later.id,
        'same process id, different start time: the operating system gave the number to something else');
});

test('R7: a driving script is its own run, and the session above it is in the lineage', () => {
    const driver = proc(40000, 'node', 8000);
    const viaDriver = captureRun.resolveRun(
        [proc(40001, 'pwsh', 8500), driver, proc(71016, 'pwsh', 7001), proc(47152, 'cmd', 7000),
            SESSION_A, HOST, SIHOST], NOT_A_TERMINAL);
    assert.strictEqual(viaDriver.id, captureRun.tokenFor(driver),
        'the nearest program that is not a shell: one run of the script');
    assert.ok(viaDriver.lineage.includes(captureRun.tokenFor(SESSION_A)),
        'the session that started the script is still on record');
    assert.strictEqual(viaDriver.lineage[0], captureRun.tokenFor(proc(40001, 'pwsh', 8500)),
        'nearest first');
    assert.strictEqual(viaDriver.lineage.length, 7, 'every ancestor that was read');
    for (const token of viaDriver.lineage) assert.match(token, TOKEN);
});

test('R8: a shell is recognised however the operating system spells it', () => {
    for (const name of ['pwsh', 'PWSH.EXE', 'powershell.exe', 'cmd.exe', 'Cmd', 'bash', '-bash',
        'sh', '-zsh', 'zsh', 'dash', 'fish', 'C:\\Windows\\System32\\cmd.exe', '/usr/bin/zsh']) {
        assert.strictEqual(captureRun.isShell(name), true, `${name} is a command shell`);
    }
    for (const name of ['node', 'node.exe', 'claude', 'WindowsTerminal', 'explorer', 'bashful',
        'pwsh-preview-helper', 'Runner.Worker', 'sshd', '', undefined]) {
        assert.strictEqual(captureRun.isShell(name), false, `${name} is not a command shell`);
    }
});

test('R9: the known cases where a run is too coarse', () => {
    // Pinned as KNOWN, not as wanted. Both are documented in SKILL.md.
    //
    // A background job in an interactive terminal has no terminal on stdin, so
    // the run is the first program above the shells -- the terminal
    // application, which every tab shares.
    const terminal = proc(900, 'WindowsTerminal', 1500);
    const job = captureRun.resolveRun(
        [proc(3100, 'pwsh', 2700), proc(15632, 'pwsh', 2000), terminal], NOT_A_TERMINAL);
    assert.strictEqual(job.id, captureRun.tokenFor(terminal));
    assert.strictEqual(job.basis, 'process');

    // A scheduler that launches through a shell and nothing else.
    const scheduler = proc(2200, 'svchost', 100);
    const scheduled = captureRun.resolveRun([proc(3200, 'pwsh', 2800), scheduler], NOT_A_TERMINAL);
    assert.strictEqual(scheduled.id, captureRun.tokenFor(scheduler));
});

test('R10: the known case where a run is too fine', () => {
    // A harness that gives its command shells a terminal: every command's
    // shell reads as "the terminal's shell", so every capture is its own run.
    const first = captureRun.resolveRun([proc(5001, 'bash', 9001), SESSION_A, HOST], A_TERMINAL);
    const second = captureRun.resolveRun([proc(5002, 'bash', 9002), SESSION_A, HOST], A_TERMINAL);
    assert.notStrictEqual(first.id, second.id);
    assert.strictEqual(first.basis, 'terminal');
    // Too fine, and still recoverable: the session is in both lineages.
    assert.ok(first.lineage.includes(captureRun.tokenFor(SESSION_A)));
    assert.ok(second.lineage.includes(captureRun.tokenFor(SESSION_A)));
});

test('R11: a chain that ends in shells, and a chain with nothing in it', () => {
    // Nothing above the shells could be read. The outermost shell is the
    // finest honest answer, and it says which kind of answer it is.
    const outer = proc(47152, 'cmd', 9000);
    const orphaned = captureRun.resolveRun([proc(71016, 'pwsh', 9001), outer], NOT_A_TERMINAL);
    assert.strictEqual(orphaned.id, captureRun.tokenFor(outer));
    assert.strictEqual(orphaned.basis, 'shell');

    for (const chain of [[], null, undefined]) {
        const none = captureRun.resolveRun(chain, NOT_A_TERMINAL);
        assert.strictEqual(none.id, null, 'no ancestor, no run -- never a guess');
        assert.strictEqual(none.reason, captureRun.REASONS.NO_INVOKER);
        assert.deepStrictEqual(Object.keys(none).sort(), ['id', 'reason']);
    }
});

// ---------------------------------------------------------------------------
// The readers
// ---------------------------------------------------------------------------

test('C1: the Windows reader drops a parent that started after its child', () => {
    // `pid|ticks|name`, the process itself first. Windows keeps a dead
    // parent's id on the child, and the id can be given to something else --
    // which is then not an ancestor, and started LATER than the child did.
    const chain = captureRun.parseWindowsChain([
        '70392|639305963073186123|node',
        '71016|639305963050245803|pwsh',
        '47152|639305963047694346|cmd',
        '63044|639305963999999999|claude',      // started after cmd: a reused id
        '37016|639305453732308169|claude'
    ].join('\r\n'));
    assert.deepStrictEqual(chain.map((p) => p.pid), [71016, 47152],
        'the chain ends at the last parent that really is one, and excludes the process itself');
    assert.deepStrictEqual(chain.map((p) => p.name), ['pwsh', 'cmd']);

    // TWO READERS OF ONE START TIME AGREE. Measured: one Windows interface
    // reports 639305780637208890 and another 639305780637208891 for the same
    // process. A token over the raw value would give one run two ids.
    const [one] = captureRun.parseWindowsChain('1|639305963073186123|node\n63044|639305780637208890|claude');
    const [other] = captureRun.parseWindowsChain('1|639305963073186123|node\n63044|639305780637208891|claude');
    assert.strictEqual(captureRun.tokenFor(one), captureRun.tokenFor(other));

    // The system's own processes are nobody's session.
    const toSystem = captureRun.parseWindowsChain(
        '9|639305963073186123|node\n8|639305963073186000|services\n4|639305444613745770|System\n0|0|Idle');
    assert.deepStrictEqual(toSystem.map((p) => p.pid), [8]);

    // Anything that is not a row is not a row.
    assert.deepStrictEqual(captureRun.parseWindowsChain(''), []);
    assert.deepStrictEqual(captureRun.parseWindowsChain('Get-Process : Access is denied\nx|y|z'), []);
});

test('C2: the /proc reader survives a process name with spaces and brackets', () => {
    const stat = (comm) =>
        `4242 (${comm}) S 4000 4242 4000 34816 4242 4194304 100 0 0 0 1 1 0 0 20 0 1 0 987654 1000 100 ` +
        '18446744073709551615 0 0 0 0 0 0 0 0 0 0 0 0 17 3 0 0 0 0 0';
    assert.deepStrictEqual(captureRun.parseProcStat(stat('bash')),
        { ppid: 4000, startTicks: '987654', name: 'bash' });
    assert.deepStrictEqual(captureRun.parseProcStat(stat('tmux: server (1) x')),
        { ppid: 4000, startTicks: '987654', name: 'tmux: server (1) x' },
        'the name is everything between the FIRST "(" and the LAST ")"');
    assert.strictEqual(captureRun.parseProcStat('not a stat line'), null);
});

/** A /proc with exactly these processes in it: pid -> [ppid, startTicks, name]. */
function fakeProc(table, bootId) {
    return (file) => {
        if (file === '/proc/sys/kernel/random/boot_id') {
            if (bootId === null) throw new Error('ENOENT');
            return `${bootId}\n`;
        }
        const m = /^\/proc\/(\d+)\/stat$/.exec(file);
        const row = m && table[m[1]];
        if (!row) throw new Error(`ENOENT: ${file}`);
        return `${m[1]} (${row[2]}) S ${row[0]} 1 1 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 ${row[1]} 0 0`;
    };
}

test('C3: the Linux chain stops below process 1, and a reboot is a different run', () => {
    const table = {
        500: [400, 9000, 'node'],
        400: [300, 8000, 'bash'],
        300: [1, 7000, 'runner'],
        1: [0, 1, 'systemd']
    };
    const chain = captureRun.readLinuxChain(500, { readFile: fakeProc(table, 'boot-one') });
    assert.deepStrictEqual(chain.map((p) => [p.pid, p.name]), [[400, 'bash'], [300, 'runner']],
        'process 1 adopts everything that is orphaned; it is nobody\'s session');

    // Start time on Linux counts from boot, so the same pid and the same tick
    // count after a reboot is another process entirely.
    const afterReboot = captureRun.readLinuxChain(500, { readFile: fakeProc(table, 'boot-two') });
    assert.notStrictEqual(captureRun.tokenFor(chain[1]), captureRun.tokenFor(afterReboot[1]));

    // Without the boot id that distinction cannot be made, so there is no
    // answer rather than one that could be wrong.
    assert.throws(() => captureRun.readLinuxChain(500, { readFile: fakeProc(table, null) }));
});

test('C4: a lookup that fails, hangs or is not available says so and never throws', async () => {
    const unsupported = await captureRun.beginRunIdentity({ platform: 'sunos' });
    assert.deepStrictEqual(unsupported, { id: null, reason: captureRun.REASONS.UNSUPPORTED });

    const failed = await captureRun.beginRunIdentity({
        readChain: async () => { throw new Error('spawn C:\\Users\\somebody\\pwsh.exe ENOENT'); }
    });
    assert.deepStrictEqual(failed, { id: null, reason: captureRun.REASONS.FAILED },
        'the reason is one of a fixed few; an error message, which can carry a path, is not copied');

    const started = Date.now();
    const hung = await captureRun.beginRunIdentity({ readChain: () => new Promise(() => {}), timeoutMs: 50 });
    assert.deepStrictEqual(hung, { id: null, reason: captureRun.REASONS.TIMED_OUT });
    assert.ok(Date.now() - started < 5000, 'a capture is never held up waiting to learn its run');

    const found = await captureRun.beginRunIdentity({
        readChain: async () => [proc(71016, 'pwsh', 9001), SESSION_A, HOST], stdinIsTty: false
    });
    assert.strictEqual(found.id, captureRun.tokenFor(SESSION_A));
});

// ---------------------------------------------------------------------------
// What is recorded
// ---------------------------------------------------------------------------

test('S1: nothing that was read is recorded: no name, no process id, no start time', () => {
    const chain = [
        proc(424242, 'pwsh', 91919191),
        proc(535353, 'acme-secret-tool', 82828282),
        proc(646464, 'JaneDoeLauncher', 73737373)
    ];
    for (const options of [NOT_A_TERMINAL, A_TERMINAL]) {
        const run = captureRun.resolveRun(chain, options);
        const text = JSON.stringify(run);
        for (const read of ['acme-secret-tool', 'JaneDoeLauncher', 'pwsh', '424242', '535353', '646464',
            '91919191', '82828282', '73737373']) {
            assert.ok(!text.includes(read), `${read} was read to resolve the run and must not be in ${text}`);
        }
        assertRunShape(run);
    }
});

/** The ALLOW-LIST: a run record is these keys, holding these shapes, and nothing more. */
function assertRunShape(run) {
    assert.ok(run && typeof run === 'object', `a run record is an object, got ${JSON.stringify(run)}`);
    if (run.id === null) {
        assert.deepStrictEqual(Object.keys(run).sort(), ['id', 'reason']);
        assert.ok(Object.values(captureRun.REASONS).includes(run.reason),
            `the reason is one of the fixed few, got ${JSON.stringify(run.reason)}`);
        return;
    }
    assert.deepStrictEqual(Object.keys(run).sort(), ['basis', 'id', 'lineage']);
    assert.match(run.id, TOKEN);
    assert.ok(['terminal', 'process', 'shell'].includes(run.basis), `basis ${run.basis}`);
    assert.ok(Array.isArray(run.lineage) && run.lineage.length > 0);
    for (const token of run.lineage) assert.match(token, TOKEN);
    assert.ok(run.lineage.includes(run.id), 'the run is one of the ancestors that were read');
}

test('I1: a capture id is a new one every time', () => {
    const ids = new Set();
    for (let i = 0; i < 50; i++) {
        const id = captureRun.newCaptureId();
        assert.match(id, UUID, `a capture id is a random UUID, got ${JSON.stringify(id)}`);
        ids.add(id);
    }
    assert.strictEqual(ids.size, 50);
});

// ---------------------------------------------------------------------------
// Real processes
// ---------------------------------------------------------------------------

const READ_HERE = process.platform === 'linux' || process.platform === 'win32';

/** A long-lived process standing in for one session. */
function startDriver() {
    const child = spawn(process.execPath, [SUPPORT, 'driver'], {
        stdio: ['pipe', 'pipe', 'inherit'], windowsHide: true
    });
    const waiting = [];
    readline.createInterface({ input: child.stdout }).on('line', (line) => {
        const next = waiting.shift();
        if (next) next(line);
    });
    return {
        /** Start one capture-shaped leaf under this driver, and get what it resolved. */
        capture(how) {
            return new Promise((resolve, reject) => {
                const timer = setTimeout(() => reject(new Error(`the driver did not answer "${how}"`)), 90000);
                waiting.push((line) => { clearTimeout(timer); resolve(JSON.parse(line)); });
                child.stdin.write(how + '\n');
            });
        },
        quit() { try { child.stdin.write('quit\n'); } catch (e) { void e; } child.kill(); }
    };
}

test('P1: two real invoking processes, interleaved, are told apart', async () => {
    if (!READ_HERE) {
        const run = await captureRun.beginRunIdentity();
        assert.deepStrictEqual(run, { id: null, reason: captureRun.REASONS.UNSUPPORTED },
            `process ancestry is not read on ${process.platform}, and the record must say so`);
        return;
    }
    const a = startDriver();
    const b = startDriver();
    try {
        // INTERLEAVED, the way concurrent sessions record: A, B, A, B.
        const a1 = await a.capture('direct');
        const b1 = await b.capture('direct');
        const a2 = await a.capture('shell');
        const b2 = await b.capture('direct');
        const all = [a1, b1, a2, b2];
        for (const one of all) {
            assert.ok(!one.error, `a leaf failed: ${one.error}`);
            assert.ok(one.run && one.run.id, `no run was resolved: ${JSON.stringify(one)}`);
        }

        assert.strictEqual(a1.run.id, a2.run.id,
            'two captures started by one process are one run -- and the second went through a ' +
            `command shell, which must change nothing: ${JSON.stringify([a1.run, a2.run])}`);
        assert.strictEqual(b1.run.id, b2.run.id);
        assert.notStrictEqual(a1.run.id, b1.run.id,
            'two invoking processes are two runs, however their captures interleave');
        assert.strictEqual(a2.run.lineage.length, a1.run.lineage.length + 1,
            'the shell really was in the chain, or this proved nothing about skipping it');

        // SELECTING BY ONE RUN NEVER RETURNS THE OTHER'S.
        const selected = all.filter((c) => c.run.id === a1.run.id);
        assert.deepStrictEqual(selected, [a1, a2]);

        // One id per invocation: four captures, four capture ids.
        assert.strictEqual(new Set(all.map((c) => c.captureId)).size, 4);
        for (const one of all) assert.match(one.captureId, UUID);

        // And what both drivers have above them -- this test process -- is in
        // every lineage, which is exactly why the top of a chain cannot be the
        // anchor.
        const shared = a1.run.lineage.filter((t) => b1.run.lineage.includes(t));
        assert.ok(shared.length > 0, 'both drivers were started by this process');
        assert.ok(!shared.includes(a1.run.id) && !shared.includes(b1.run.id));
    } finally {
        a.quit();
        b.quit();
    }
});

test('P2: what a real lookup records is tokens and nothing else', async () => {
    const run = await captureRun.beginRunIdentity();
    assertRunShape(run);
    if (READ_HERE) assert.ok(run.id, `this platform is read, so there is a run: ${JSON.stringify(run)}`);

    const text = JSON.stringify(run).toLowerCase();
    const mustNotAppear = [os.userInfo().username, os.hostname(), path.basename(os.homedir())]
        .filter((s) => s && s.length >= 3)
        .map((s) => s.toLowerCase());
    for (const secret of mustNotAppear) {
        assert.ok(!text.includes(secret), `${secret} must not be in the run record`);
    }
});

// FAILED UNTIL PROVEN OTHERWISE. Several cases here wait on real processes, and
// a wait that nothing holds open ends the process quietly with status 0 -- which
// the runner, reading only the exit code, would report as a pass. Found while
// writing C4: an unref'd timer did exactly that, and nineteen tests "passed"
// in no time at all, having printed nothing.
process.exitCode = 1;

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
    if (failures.length) {
        process.stderr.write(`\n${failures.length} of ${queued.length} capture-run tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    process.exitCode = 0;
    console.log(`All capture-run tests passed (${passed})`);
})();
