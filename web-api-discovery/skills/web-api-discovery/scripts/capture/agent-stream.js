'use strict';

/**
 * agent-stream.js -- talking to the driving agent, and keeping a readable
 * record of what it did.
 *
 * The -AI agent runs as `claude -p --input-format stream-json --output-format
 * stream-json --verbose --replay-user-messages ...`. Its stdin stays open for
 * the whole drive, which is what lets the recorder TELL it things instead of
 * ending it: the site asked for a pause, the wait is over, a person is taking
 * over, the person is done, the capture has ended. Each is one user message,
 * one JSON line. Measured against the CLI (2.1.325), not assumed:
 *
 *   - `--verbose` is REQUIRED with stream-json output in print mode.
 *   - A message written while the agent is mid-turn is injected at its next
 *     tool boundary -- after the tool call it is making returns -- and the
 *     turn goes on with it. There is no separate `result` for it.
 *   - A message written while the agent is idle (its turn ended) starts a new
 *     turn, which ends in its own `result`.
 *   - `--replay-user-messages` echoes each message back (`isReplay: true`)
 *     when the agent actually takes it in: that is the acknowledgement.
 *   - Closing stdin ends the session after the current turn; the CLI exits 0.
 *
 * So the agent is known to be idle only once a `result` arrives, and the
 * browser hold (capture-pause.js) is what makes "don't use the browser" true
 * in the seconds before the message lands.
 *
 * The output is JSON, and the step log has to stay READABLE: the cataloguing
 * step reads it to segment the session into actions. So every event is
 * rendered -- the agent's own words as they are, a tool call as one short
 * line, a failed tool as one line, a turn ending as one line -- and the raw
 * JSON goes to a side file, with screenshots' base64 dropped.
 *
 * Zero dependencies.
 */

const pacing = require('./pacing.js');

/** One stream-json user message, as the CLI reads it from stdin: one line. */
function userMessage(text) {
    return JSON.stringify({ type: 'user', message: { role: 'user', content: [{ type: 'text', text }] } }) + '\n';
}

function seconds(ms) {
    return `${Math.max(0, Math.round(ms / 1000))} s`;
}

/**
 * What the recorder tells the agent. Plain sentences: the agent reads them as
 * the only instructions besides its prompt (drive-prompt.md says so).
 */
const agentMessages = {
    /** `until` is the local clock time the pause ends at, as the operator reads it. */
    pause({ status, waitMs, until, named }) {
        const why = named
            ? `The site asked us to slow down (HTTP ${status}, wait ${seconds(waitMs)}).`
            : `The site is throttling us (HTTP ${status}) and named no wait, so the capture backs off for ${seconds(waitMs)}.`;
        return `${why} The capture is paused until ${until} local. Don't use the browser; end your turn and wait; ` +
            'I\'ll tell you when to continue.';
    },
    extend({ until }) {
        return `The site asked for a longer wait: the capture is now paused until ${until} local. ` +
            'Keep waiting; don\'t use the browser. I\'ll tell you when to continue.';
    },
    resume({ pausedMs }) {
        return `The wait is over (paused ${pacing.describeDuration(pausedMs)}). Re-check the page, then continue ` +
            'where you left off.';
    },
    handOver({ reason }) {
        return `The site showed ${reason}. A person is taking over the browser. Don't use the browser; end your ` +
            'turn and wait. I\'ll tell you when they are done.';
    },
    handBack() {
        return 'The person has finished. Re-check the page and continue where you left off.';
    },
    ended() {
        return 'The capture has ended; stop. Don\'t use the browser again, and end your turn.';
    }
};

/** What the site did, as a phrase the agent can act on. */
function handOverPhrase(decision) {
    if (decision.why === 'challenge') return 'a "prove you\'re human" check';
    if (decision.why === 'blocked') return 'a block page (it refused this visitor)';
    if (decision.why === 'persistent') return 'nothing but "slow down" for an hour';
    if (decision.why === 'retry-after-too-long') return 'a request to wait more than a day';
    return 'a block or a check';
}

// ---------------------------------------------------------------------------
// Rendering the stream into the step log
// ---------------------------------------------------------------------------

/** `mcp__claude-in-chrome__navigate` -> `navigate`. */
function toolName(name) {
    const parts = String(name || 'tool').split('__');
    return parts[parts.length - 1];
}

/**
 * A tool call in one line, WITHOUT what it typed: a form value or a typed
 * string can be a search term somebody would rather not see in a log, and
 * the agent may not type a credential anyway. URLs lose their query for the
 * same reason pacing.whereOf drops it.
 */
function toolBrief(input) {
    if (!input || typeof input !== 'object') return '';
    const out = [];
    if (input.action) out.push(String(input.action));
    if (input.url) out.push(pacing.whereOf(input.url));
    if (Array.isArray(input.coordinate)) out.push(`at ${input.coordinate.join(',')}`);
    if (input.ref) out.push(`ref=${input.ref}`);
    if (input.element) out.push(`"${String(input.element).slice(0, 60)}"`);
    for (const key of ['text', 'value']) {
        if (typeof input[key] === 'string' && input.action !== 'key') out.push(`(${input[key].length} chars typed)`);
    }
    if (input.action === 'key' && typeof input.text === 'string') out.push(input.text.slice(0, 30));
    return out.join(' ');
}

function firstText(content) {
    if (typeof content === 'string') return content;
    if (Array.isArray(content)) {
        const t = content.find((c) => c && c.type === 'text');
        return t ? String(t.text) : '';
    }
    return '';
}

/**
 * One event as step-log text, or null for an event the log does not need
 * (thinking, rate limits, hooks, the agent's own bookkeeping).
 */
function renderEvent(ev) {
    if (!ev || typeof ev !== 'object') return null;
    if (ev.type === 'system' && ev.subtype === 'init') {
        return `[agent] session started${ev.model ? ` (${ev.model})` : ''}\n`;
    }
    if (ev.type === 'assistant' && ev.message && Array.isArray(ev.message.content)) {
        let out = '';
        for (const c of ev.message.content) {
            if (!c) continue;
            if (c.type === 'text' && c.text) out += c.text.endsWith('\n') ? c.text : `${c.text}\n`;
            if (c.type === 'tool_use') out += `  > ${toolName(c.name)} ${toolBrief(c.input)}`.trimEnd() + '\n';
        }
        return out || null;
    }
    if (ev.type === 'user' && ev.message && Array.isArray(ev.message.content) && !ev.isReplay) {
        let out = '';
        for (const c of ev.message.content) {
            if (c && c.type === 'tool_result' && c.is_error) {
                const line = firstText(c.content).split('\n')[0].slice(0, 200);
                out += `  ! the tool failed: ${line}\n`;
            }
        }
        return out || null;
    }
    if (ev.type === 'result') {
        return `[agent] turn ended (${ev.subtype || 'done'}${ev.is_error ? ', error' : ''})\n`;
    }
    return null;
}

/** JSON for the raw side file: a screenshot's base64 is not a record of anything. */
function rawLine(line) {
    try {
        return JSON.stringify(JSON.parse(line), (key, value) =>
            key === 'data' && typeof value === 'string' && value.length > 1024
                ? `[${value.length} characters omitted]` : value) + '\n';
    } catch (e) {
        void e;
        return null;
    }
}

/**
 * Line-buffers the agent's stdout and turns it into step-log text.
 *
 *   write(text)   where the readable text goes (the step log and stderr)
 *   raw(line)     where the raw JSON goes (optional)
 *   onEvent(ev)   every parsed event, for the drive's own bookkeeping
 *
 * A line that is not JSON is passed through as it is: the agent is not
 * always the CLI (a test's stub, an older CLI) and nothing it says is lost.
 */
function createStreamRenderer(opts = {}) {
    const write = opts.write || (() => {});
    const raw = opts.raw || null;
    const onEvent = opts.onEvent || (() => {});
    let buf = '';

    function line(text) {
        const trimmed = text.replace(/\r$/, '');
        if (!trimmed) return;
        let ev = null;
        if (trimmed.startsWith('{')) {
            try { ev = JSON.parse(trimmed); } catch (e) { void e; ev = null; }
        }
        if (!ev) { write(`${trimmed}\n`); return; }
        if (raw) { const r = rawLine(trimmed); if (r) raw(r); }
        const rendered = renderEvent(ev);
        if (rendered) write(rendered);
        onEvent(ev);
    }

    return {
        push(chunk) {
            buf += String(chunk);
            let i;
            while ((i = buf.indexOf('\n')) >= 0) {
                const next = buf.slice(0, i);
                buf = buf.slice(i + 1);
                line(next);
            }
        },
        /** The last line, when the stream ended without a newline. */
        flush() {
            if (buf) { const rest = buf; buf = ''; line(rest); }
        }
    };
}

module.exports = {
    userMessage,
    agentMessages,
    handOverPhrase,
    renderEvent,
    toolBrief,
    createStreamRenderer
};
