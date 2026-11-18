# Stopping a large capture waits for post-processing and says what state it is in

- Issue: https://github.com/IntelliTect-dev/Multiplex/issues/27
  (carried over from IntelliTect-Samples/web-api-discovery#39)
- PR:    (filled in once the PR exists)
- Slug:  27-stop-reports-post-processing

## Overview

`capture-har.js stop` (and `Stop-HarRecording.ps1`) waited a fixed 60 seconds
for the recording process to finish scrub, verify, digest and catalogue. On a
22 MB capture it returned exit 6, "post-processing is still running", while the
recording process finished normally a little later. The caller could not tell
*progressing* from *hung* from *the stop never registered*, and a caller going
by the exit code treated a good capture as failed.

## Measured, before designing

A synthetic 22.9 MB, 434-entry capture on the development machine:

| Phase  | Wall time | Observable from outside while it runs |
|--------|-----------|----------------------------------------|
| scrub  | 114.6 s   | nothing for the first 53.5 s (the scrub reads the capture twice; the first pass writes nothing), then `.scrubbed.har.partial-<pid>` grows |
| verify | 11.6 s    | nothing (it only reads) |

So a healthy capture is silent for stretches that grow with its size. Any fixed
window near 60 seconds is the same defect renamed.

## Approved Design

### 1. The progress signal

Two parts, both written only by the code doing the work:

1. **The ending record in `session.json`.** The recording process writes
   `ending: { by, registeredUtc, phase, phaseStartedUtc }` the moment its
   ending race settles, and again as each phase STARTS: `finalizing`, `scrub`,
   `verify`, `publish`, `digest`, `catalogue`, `ai-catalogue`, `complete`.
   Post-processing is synchronous, so a phase is entered only when the one
   before it returned: the record cannot advance while the work is hung.
2. **The artifacts post-processing itself writes**, by size and modification
   time: `raw.har`, `raw.har.annotating`, `.scrubbed.har.partial-*`,
   `scrubbed.har`, `scrub-findings*.json`, `scrubbed.rejected*.har` in the
   session directory, and `.publishing-*`, `scrubbed.har`, `scrub-findings.json`,
   `digest.json`, `catalogue.json` in the output path.

Deliberately NOT a signal: `raw.ndjson` (grows while the browser still makes
requests), the agent's logs (grow while an agent outlives the recording), the
heartbeat of `run-with-heartbeat.js` (a timer), CPU time, or the recorder
merely being alive. Each of those advances while the work is hung.

### 2. One evaluation, used by `stop` and by `status`

`capture-ending.js` holds a pure `evaluate(evidence, nowMs)`. It is derived
from persisted timestamps (the ending record, artifact modification times, the
STOP file's time) so the single-shot `status` gives the same answer `stop`
does, and a fixture with old timestamps exercises every state through the real
command line with no test-only switch.

| State            | Meaning | `stop` |
|------------------|---------|--------|
| `recording`      | nothing has asked this capture to end | (cannot occur: `stop` asks) |
| `stop-pending`   | a stop was asked for under a minute ago, not yet acknowledged | keeps waiting |
| `not-registered` | the recorder is running and has not acknowledged the stop for 60 s | returns, exit 1 |
| `progressing`    | the ending registered; a sign of progress within the allowance | keeps waiting |
| `stalled`        | the ending registered; no sign of progress for longer than the allowance | returns, exit 1 |
| `complete`       | post-processing finished | returns, exit 0 / 5 / 6 / 7 as before |
| `not-processed`  | the recording was cancelled; the raw is kept | returns, exit 0 |
| `recorder-gone`  | the recording process exited without finishing | runs the phases itself, as before |

**The allowance** (the "stated time") is `max(60 s, 15 s per MB of capture)`,
and 30 minutes during the AI catalogue pass, which prints nothing until it
ends. It is printed in the message and carried in the JSON.

**Running `stop` again does not start the clock again.** Because the state is
read from what is on disk, a second `stop` waits only if something has moved
since, and otherwise gives the same answer at once.

**Two things only a watcher knows**, in `stop`'s wait and not in a single
`status`, and they are kept apart because only the first is evidence:

- A file that grew between two polls where its modification time did not move
  is a sign of progress like any other.
- That the waiting process itself was not running (two polls more than 10
  seconds apart: a laptop asleep, and its recorder with it) restarts the
  ALLOWANCE from the first poll after the gap -- and nothing more. It is not a
  sign of progress: `lastProgressUtc` does not move, `waitResumedUtc` says the
  wait was interrupted, and the message says so too. The same goes for a clock
  set back so that no time on disk is usable: the allowance is counted from
  when the wait began, and `lastProgressUtc` is null.

### 3. Exit codes

- `stop` no longer returns while post-processing is progressing, so a healthy
  capture is never reported as anything but its real result.
- Exit 6 goes back to meaning only what its table entry says: scrub or
  catalogue FAILED. It is no longer returned for "still running".
- `stalled` and `not-registered` return 1. Neither concludes anything about the
  capture: the recording process is still running. 6 would tell a caller
  post-processing had ended in failure, and 0 would send it to read artifacts
  that are not there. No new exit code is added.

### 4. Machine-readable output

- `status` JSON gains `ending`, the evaluation above.
- `stop` prints one JSON line on stdout once it has found the recording --
  every exit except 3 (no capture session found):
  `{ "stop": <state>, "sessionDir", "exitCode", "ending": {...} }`.
- Every write the recorder makes to session.json from the ending onwards goes
  beside it and is renamed over it, so a `stop` or `status` reading while it
  works never finds the file truncated. (The writes made while the recording
  is still running are unchanged: nothing polls for those.)

### 5. Three defects closed on the way

- After its first wait expired with the recorder still alive, `stop` went on to
  assemble `raw.har` from the log itself, racing the recorder. It now returns
  before touching anything.
- A recorder that died mid-post-processing was waited on for the full minute.
  The wait now checks it every poll and drops into the existing run-it-here
  branch.
- `stop` on a cancelled capture crashed (exit 1, a TypeError) after printing
  its line. It now exits 0.

## Acceptance Criteria

- [ ] `stop` keeps waiting while there is a sign of progress; a capture that
      takes more than 60 seconds is reported as success.
- [ ] `stalled` and `not-registered` each have their own message, JSON state
      and are never exit 6 or 0.
- [ ] `status` reports the same state.
- [ ] `Stop-HarRecording.ps1` surfaces the same states.
- [ ] No new command-line option, switch, positional argument, environment
      variable or exit code.
- [ ] Tests with an injected clock for each state; each fails for a behavioural
      reason when the change is reverted.

## Implementation checklist

- [x] `capture/capture-ending.js`: `notePhase`, `readEvidence`, `evaluate`,
      `waitForEnding`, the message lines. Tests in `capture-ending.test.js`.
- [x] `capture-har.js` recorder side: acknowledge the ending after the race,
      `onPhase` through `postProcess` / `catalogueScrubbed` / `runCatalogue`
      (injected only at the recorder's call site), `complete` at the end.
- [x] `capture-har.js` `stop`: replace both deadline loops with the wait,
      return before assembling when the recorder is alive, print the JSON line.
- [x] `capture-har.js` `status`: `ending`.
- [x] Usage header and exit-code table; `SKILL.md`; `Stop-HarRecording.ps1`.
- [x] Pester: `Stop-HarRecording.ps1` against fixtures in each state.
- [x] Evidence under `.evidence/` (not committed: the directory is ignored).

## Left for the owner

- **An exit code of its own for "returned before post-processing finished".**
  `stalled` and `not-registered` share exit 1 with runtime errors, because the
  brief allowed changing which EXISTING code is returned and nothing more. The
  JSON tells them apart; a dedicated code would let a caller branch without
  parsing it.
- **Should `stop` post-process a capture that ended `sign-in-needed` (exit 8)?**
  It does today, before and after this change, once the recorder has exited --
  while the recorder itself deliberately does not. Unchanged here.
- **Should `stop` post-process a CANCELLED capture?** The recorder's cancel
  message says "Re-run post-processing by stopping this session again"; `stop`
  has never done that. This change only stops it crashing.
