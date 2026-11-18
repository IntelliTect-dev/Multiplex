# 26 -- Every capture records which run made it

- Issue: https://github.com/IntelliTect-dev/Multiplex/issues/26
- PR:    (filled in once the PR exists)
- Slug:  26-capture-run-identity
- Carried over from: https://github.com/IntelliTect-Samples/web-api-discovery/issues/53

## Overview

The capture store is shared and append-only, several sessions record into it at
once, and nothing in a capture says which run produced it. Directory names are
start times and `session.json`'s modified time is when post-processing finished,
so no time window selects one run's captures. This change records two
identities in every capture, shows them where captures are listed, and lets the
existing list and remove commands select by them with no new option.

## Approved Design

### Two identities, at two grains

The original issue measured two separate failures, and one identifier cannot
answer both:

| Field | Grain | Answers |
|---|---|---|
| `captureId` | one recorder invocation | "which attempt is this?" -- three captures with one `describe`, made by one session, are three ids |
| `run.id` | everything one invoking session started | "which captures are mine?" -- two sessions recording the same site in the same hour are two ids |

**Naming.** The original issue calls one recorder invocation a "run". Here that
is `captureId`; `run` is the group. `run.id` GROUPS and `captureId` IDENTIFIES:
anything that acts on a capture acts on one capture at a time.

### What a "run" is

Every capture the same invoking process started. No caller supplies anything:
the recorder reads its own process ancestry when recording starts and picks one
ancestor as the anchor.

The anchor cannot be a fixed depth. Measured on the owner's machine:

```
node (recorder) <- pwsh (new per command) <- cmd (new per command)
                <- claude (one per agent session) <- claude (app host, shared) <- sihost
```

The direct parent is a new process for every command an agent runs, and the top
is shared by every session. So the rule is structural:

1. Walk up from the recorder through the unbroken chain of command shells
   (`pwsh`, `powershell`, `cmd`, `bash`, `sh`, `zsh`, ...) to the first process
   that is not a shell.
2. If the recorder's stdin is a terminal and there was at least one shell, the
   run is the OUTERMOST of those shells -- the terminal's own shell
   (`basis: "terminal"`).
3. Otherwise the run is that first non-shell process -- the agent session, a
   driving script, a CI job worker (`basis: "process"`).
4. If nothing above the shells can be read, the run is the outermost shell
   (`basis: "shell"`). If no ancestor can be read at all, there is no run and
   the record says why.

No timing is involved, so the same session always resolves to the same anchor.
The token is the first 12 hex characters of SHA-256 over the anchor's process
id and operating-system start time (plus the boot id on Linux, where start time
counts from boot), so a reused process id is a different run.

**Which way it errs.** Rule 2 is never coarser than rule 3, and an unknown
wrapper between the recorder and its session becomes its own run. Finer is the
safe direction: a run that is too small selects too little. Known cases where
it is too coarse, pinned in tests as known behaviour and documented:

- a launch with no terminal on stdin from inside an interactive terminal (a
  background job, redirected stdin) anchors on the terminal application, which
  every tab shares;
- a scheduler or service that launches through shells only anchors on the
  scheduler;
- a recorder whose launcher has already exited is adopted by the system and
  anchors on whatever adopted it.

Known case where it is too fine: an agent harness that gives its command shells
a terminal makes every capture its own run.

### What is recorded

In `session.json`, written when recording starts, beside `pid` and `startedUtc`:

```json
"captureId": "0f6b3a52-9a0e-4c1c-8d64-0c1d2f4a7b11",
"run": { "id": "3f9c2a71d0b4", "basis": "process", "lineage": ["a81e...", "3f9c2a71d0b4", "..."] }
```

or, when the ancestry could not be read: `"run": { "id": null, "reason": "..." }`
with a reason from a fixed vocabulary.

`lineage` is the token of every readable ancestor, nearest first. It is recorded
only in `session.json`; the walk and the listing do not carry it, because a
containment filter on a high ancestor matches every session on the machine.

Nothing else is recorded: no process name, command line, user name, host name or
environment content. Names are read to tell a shell from a program and are not
stored.

### Where it is surfaced

- `session.json` (above).
- `capture-har.js status` JSON: `captureId`, `runId` (null for a capture recorded
  before this change).
- The recorder's one machine-readable line (`capture: complete`): `captureId`,
  `runId`.
- `Invoke-HarCapture` prints one information line naming both, read from that
  line, so a session learns its own ids from its own capture.
- `capture-store.js` (the walk): `captureId`, `runId`, `runBasis`.
- `Get-HarCapture`: `CaptureId`, `RunId`, `RunBasis` properties; a `Run` column
  in the table; all three in the list view. A capture recorded before this
  change prints `(none recorded)`; one whose ancestry could not be read prints
  `(unidentified)`; a capture this recorder did not make prints `--`.
- `Remove-HarCapture`: the confirmation and the closing line name the run.

### Selection, with no new option

```powershell
Get-HarCapture | Where-Object { $_.RunId -and $_.RunId -eq $run }
Get-HarCapture | Where-Object { $_.RunId -and $_.RunId -eq $run } |
    ForEach-Object { Remove-HarCapture -Capture $_ }
```

The `$_.RunId -and` guard matters: `RunId -eq $run` with an empty `$run` is true
for every capture that has no run.

### Cost

Linux reads `/proc` and costs nothing. Windows has no way to read another
process's start time from Node, so the recorder starts one `pwsh` (which the
front doors already require) as soon as `start` is past its checks, and
collects the answer when it writes `session.json` -- by then a browser has been
launched. `--validate-only` reads nothing. Other platforms record
`run: { id: null, reason }`.

**A slow lookup does not hold a recording up, and is not thrown away.** The
first implementation awaited the lookup for ten seconds and then recorded
`run: { id: null, reason }`. Recording for real on a busy machine (four
recordings by two sessions while a test suite ran) left two of the four with no
run. So recording now waits five seconds at most, writes
`run: { id: null, reason: "...had not been read when this was written" }`, and
the run joins the session when the lookup answers; the lookup itself is given a
minute. It is written with the session's next write -- the ending at the
latest -- and not by a write of its own: `session.json` is not written
atomically, the driving agent reads it as it starts, and a write at a moment
nothing chose is one more chance of it reading half a file. Two more things
came out of the same finding:

- the lookup starts in the call that asks for it, not a microtask later;
- a time limit is not final until the event loop has turned a few times after
  it expires, because a process coming back from synchronous work serves an
  expired timer before it notices a child that exited seconds earlier.

### Deliberately not done (owner decisions)

- No `-RunId` parameter on `Get-HarCapture`, and no "captures of the session
  asking" selector. Both are proposed in the pull request.
- `captureId` is not carried into `digest.json` / `catalogue.json` provenance.

## Evidence Plan

- **Change type**: CLI / PowerShell
- **Artifact format**: markdown
- **Capture command**: `Get-HarCapture -Path <fixture store> | Format-Table`
  over a store holding two interleaved runs and one capture recorded before
  this change, plus one real recording's `session.json` identity fields
- **Entry-point file**: `.evidence/<phase-id>/evidence.md`

## Acceptance Criteria

- [x] Every capture gets `captureId` and `run` in `session.json` when recording starts
- [x] `status` JSON and the `capture: complete` line carry `captureId` and `runId`
- [x] Two captures started by one invoking process share `run.id`; two invoking processes get different ones, interleaved or not
- [x] `Get-HarCapture` shows the run in objects, the table and the list view
- [x] Selecting by one run never returns another run's captures, nor a capture with no run
- [x] A capture recorded before this change is listed and labelled as having no run
- [x] Nothing sensitive is recorded
- [x] No new command-line option, switch, positional argument or environment variable
- [x] A slow lookup neither holds a recording up nor costs it its run

## Implementation Checklist

- [x] `scripts/capture/capture-run.js` -- the rule (`resolveRun`), the readers
      (Linux `/proc`, Windows `pwsh`), `beginRunIdentity`, `settleRunIdentity`,
      `newCaptureId`
- [x] `scripts/capture/capture-run.test.js` -- the rule over injected chains,
      the readers' parsing, the limits under a busy event loop, and two real
      interleaved invoking processes (`capture-run.test-support.js`)
- [x] `capture-har.js` -- one require, one kickoff, the at-start write,
      `status()`, the complete line
- [x] `scripts/capture/capture-run-identity.test.js` -- real recordings
      against a local page: `session.json`, `status`, the complete line, the
      walk, and one whose lookup is withheld
      (`capture-run-slow-lookup.test-support.js`)
- [x] `capture-store.js` + `capture-store.test.js` -- carry the identity
- [x] `Get-HarCapture.ps1`, `HarCapture.Format.ps1xml`, `tests/HarCaptureList.Tests.ps1`
- [x] `Remove-HarCapture.ps1`, `tests/HarCaptureRemove.Tests.ps1`
- [x] `Invoke-HarCapture.ps1`, `tests/har-recording.Tests.ps1`
- [x] `SKILL.md`, both READMEs

## Not done here

- macOS and other platforms record `run: { id: null, reason }`; a `ps`-based
  reader is not written, because it could not be run here.
- Windows without PowerShell 7 records no run. The Windows PowerShell 5.1 route
  to the same answer goes through WMI and took 5-15 s where it was measured.
- The Linux reader has been run only against a constructed `/proc` on the
  machine this was written on; its real-process cases run first in CI.

## Test commands

```powershell
node skills/web-api-discovery/scripts/capture/capture-run.test.js
node skills/web-api-discovery/scripts/capture/capture-run-identity.test.js
node skills/web-api-discovery/scripts/capture/capture-store.test.js
./.github/ci/Invoke-PesterSuite.ps1 -Path ./tests/HarCaptureList.Tests.ps1, ./tests/HarCaptureRemove.Tests.ps1, ./tests/har-recording.Tests.ps1, ./tests/HarBatchStore.Tests.ps1
./scripts/Invoke-NodeTestSuite.ps1 -Filter '*capture*'
```
