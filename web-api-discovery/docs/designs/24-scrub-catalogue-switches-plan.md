# 24 -- Run only the scrub or the catalogue step on an existing capture

- Issue: https://github.com/IntelliTect-dev/Multiplex/issues/24
  (carried over from IntelliTect-Samples/web-api-discovery#48, where the owner
  chose the shape)
- PR:    (filled in once the PR exists)
- Slug:  24-scrub-catalogue-switches
- Branch: `feat/24-scrub-catalogue-switches`

## Overview

`Invoke-HarCapture` always runs record -> scrub -> catalogue. Two switches,
`-Scrub` and `-Catalogue`, re-run one phase (or both, in that order) on a
capture that already exists, and record nothing.

## User Story

As an operator (or an agent) with a capture already in the store
I want to re-run only its scrub or only its catalogue step from the one front door
So that an improved scrubber, a waived finding or a failed AI pass does not
mean re-recording a browser session somebody drove by hand.

## Approved Design

Owner decisions, from #48: the switches are `-Scrub` and `-Catalogue`; each
"implies skip capture because it is already done"; they name what WILL run, so
no combination means "record nothing and do nothing". No other new option.

### The switches

| Typed | Runs |
|---|---|
| neither | record -> scrub -> catalogue, exactly as today |
| `-Scrub` | scrub + leak gate on the existing capture |
| `-Catalogue` | digest + catalogue on the existing, already scrubbed capture |
| both | scrub, then catalogue -- and the catalogue only when the scrub exited 0 |

Read by VALUE: `-Scrub:$false` records as today.

### Re-entry points are reused, never rebuilt

- **Scrub** -> `har/Invoke-SanitizeHar.ps1 -InputHar <capture directory> -Force
  -WhatIf:$false -Confirm:$false`. The FOLDER form, on purpose. #48 rejected composing the
  FILE form (`-InputHar raw.har -OutputHar ...`) because it writes an unjudged
  file straight to its destination. The folder form (since #386) scrubs under
  a temporary name, calls it `scrubbed.har` only after the gate passes, and
  quarantines a refusal as `scrubbed.rejected.har` -- so the front door
  restates none of promotion, quarantine or the verdict. `-Force` because the
  operator asked for this capture to be scrubbed again; without it the wrapper
  answers "already scrubbed -- use -Force", a switch this door does not have.
  `-WhatIf:$false` because that wrapper supports ShouldProcess and an ambient
  `$WhatIfPreference` would otherwise turn the phase into a silent no-op;
  `-Confirm:$false` because a lowered `$ConfirmPreference` would have it prompt
  per capture, which an unattended run cannot answer.
- **Catalogue** -> `capture-har.js catalogue <capture directory> --output-path
  <capture directory>`. `--output-path` is pinned to the capture's own
  directory so the catalogue is derived from the `scrubbed.har` a `-Scrub`
  just wrote there, not from a copy published elsewhere by an earlier
  `-OutputPath` run. The recorder asks the leak gate again, runs the digest,
  asks `decideCatalogueRunner`, and refuses to replace a catalogue that
  carries described actions -- all unchanged.
- Nothing is added to `capture-har.js`, `capture-store.js` or either wrapper.
- **Both calls are made from the capture's own store root** -- the folder that
  holds its `.har-captures` -- and the caller's directory is restored
  afterwards, even on failure. The scrub and the leak gate find the operator
  profile (`.har-profile.json`: the operator's own identifiers and the salt
  every pseudonym is derived from) by walking up from the working directory.
  A capture named by its directory from another repository was therefore
  scrubbed AND gated with that other repository's profile: the operator's
  identifiers went back into `scrubbed.har`, the gate passed it, and the run
  reported "clean". (Independent review, Critical.) Run from the store root,
  the profile beside the capture is the one found; if none is found from
  there, the scrub's existing hard failure applies.

  WHAT THAT RULE DOES AND DOES NOT GUARANTEE. A recording finds its profile by
  walking up from the directory IT was run in (`harProfile.loadProfile({})`,
  and it scaffolds a missing one in that same directory), while the store is
  anchored to the repository's main working tree. Run from the store's own
  folder -- or from below it with no nearer profile, which is what a linked
  worktree under the main tree is -- those are the same file, and a re-scrub
  uses the profile the recording used. Run from a subfolder that has its own
  profile, the recording used THAT one, and the phases will not find it: they
  use the one at the store root (a different salt, so different pseudonyms) or
  fail for want of any. The rule makes a re-scrub independent of where it is
  typed; it does not make it use the recording's profile in that case. Nothing
  records which profile a recording used.

### FOR THE OWNER: what changed meaning beyond the two switches

The two switches are the approved surface. To make them usable without a third
parameter, three existing things mean something new WHEN A PHASE SWITCH IS
TYPED. Each is an option-level change and none has been approved; the pull
request is a draft until they are.

| What | Before | With `-Scrub` / `-Catalogue` |
|---|---|---|
| The positional first argument (`-Uri`) | the site to open; must be a URL | which existing capture: the site's URL, **or the capture's directory, or its `host/stamp` label** |
| `-Describe` | required; what the new recording is for | **optional**; when typed, narrows the selection to the capture recorded with that description |
| Exit codes at this door | 2 only for the `-Describe` refusal and what the recorder refuses | **2** also for an ambiguous selection, a wildcard, or a recording parameter with a phase switch; **3** (new here) for no capture, a capture not in the state the phase needs, or one outside a capture store; **7** from `-Scrub` alone on an advisory verdict |

Also new, and in the same spirit: the nine recording parameters are REFUSED
with a phase switch rather than ignored.

**The alternative, if overloading is not wanted** -- neither part was built,
because each is new command-line surface:

1. **A separate capture-naming parameter**, shaped like `Remove-HarCapture`'s
   `-Capture`: one capture by directory, by `host/stamp` label, or as an object
   from `Get-HarCapture`. The positional argument would go back to meaning
   only a URL (the site), `-Describe` would go back to meaning only "what this
   recording is for" and would simply be refused with a phase switch, and the
   selection rules below would move behind the new parameter unchanged.
2. **A `scrub` command on `capture-har.js`**, exposing `postProcess`'s phase A
   as its `catalogue` command exposes phase B. It would let the scrub phase
   answer the recorder's own 0 / 6 / 7 instead of this door reading a findings
   report, publish to the output path a capture was recorded with, place the
   substitution tables for a capture recorded outside a repository, and keep
   the sign-in rule in the one place that owns it. #48 already names it as
   the honest shape.

### Which capture (existing parameters only)

The positional first argument names it. With a phase switch it is one of:

1. a **URL** -- the captures recorded for that site (the host folder, derived
   exactly as a recording derives it);
2. a **directory** -- the capture's own directory, or a folder holding one;
3. a **`host/stamp` label** -- as the refusal list and `Remove-HarCapture` use.

`-Describe`, when typed, narrows to captures recorded with that description
(the whole of it; letter case and surrounding spaces aside), so the command
that made a capture plus `-Catalogue` names it.

The candidates come from `Get-HarCaptureInventory` (`capture-store.js`, the one
walk), filtered to recorder output. Exactly one -> selected, and said: label,
description, directory. Several -> refused, listed newest first by label. The
newest is never picked silently.

### Refusals (all non-zero, lines on stderr, nothing written)

| Case | Exit |
|---|---|
| a recording-only parameter with a phase switch (`-OutputPath`, `-Profile`, `-Isolated`, `-Port`, `-Browser`, `-Attach`, `-AI`, `-AITools`, `-Pace`) | 2 |
| several captures match | 2 |
| the first argument is a wildcard (and not a URL with a scheme) | 2 |
| no capture matches / `-Describe` matches none | 3 |
| the only match is not recorder output (foreign / imported) | 3 |
| the capture is not inside a capture store (no `.har-captures` above it) | 3 |
| either phase, while the capture's recorder is still working on it (see below) | 3 |
| `-Scrub` and the capture has no `raw.har` | 3 |
| `-Scrub` and the capture signed in (or its `session.json` is unreadable) AND already has a `scrubbed.har` or a quarantined rejected scrub (see below) | 3 |
| `-Catalogue` alone and the capture has no `scrubbed.har` (never scrubbed, or its only scrub was REJECTED) | 3 |

A first argument that is not a URL with a scheme, a directory or a label that
matches is refused without being echoed: a URL pasted without `https://`
lands there and may carry a token.

2 is the recorder's usage-error code; 3 is its "no capture / nothing to
catalogue" code. The catalogue's exit code is passed through whole.

### What `-Scrub` alone answers

The scrub wrapper's folder form answers 0 for an artifact it KEPT, whatever
the gate said of it, and says the verdict only in a summary on the information
stream. So `-Scrub -InformationAction SilentlyContinue` replaced a clean
`scrubbed.har` with an advisory one, exited 0 and said nothing. (Independent
review.) Now:

| Verdict of the scrub that just ran | Exit | Said |
|---|---|---|
| clean | 0 | the wrapper's summary (information stream) |
| ADVISORY (artifact kept) | **7** | a warning, on the warning stream |
| values BLUNTED (gate passed) | 0 | a warning, on the warning stream |
| rejected, or the scrub failed | the wrapper's code (1) | a warning |

**How the verdict is read, without a new option and without parsing wording:**

- ADVISORY from the gate's own findings report. `verify-scrub.js` writes
  `scrub-findings.json` beside the file it judged whenever a run is not clean,
  with the verdict in it as data (`"verdict": "advisory"`), before it prints
  anything. The report's last-write time is noted before the run, so one this
  run wrote is told from one an earlier run left -- the gate writes none on a
  clean run and nothing removes an old one.
- "Anything else that is not clean" by COMPARISON. The wrapper's status lines
  are collected (its `6>&1`), then re-emitted under the caller's preference.
  `Get-HarScrubVerdictLine` -- the module function that writes the wrapper's
  verdict line -- is asked what it says for one clean capture; if the wrapper
  did not say exactly that, the scrub did not come back clean, and what it did
  say is passed on in the warning. No format is restated here, and a missing
  line fails closed.

7 is the recorder's existing code for "produced, with advisory findings", and
what `-Scrub -Catalogue` already answers because the recorder asks the gate
again. BLUNTED stays 0: the gate passed that artifact, and the recorder's own
`catalogue` answers 0 for it too.

### A capture its recorder still has

A recorder writes `endedUtc` when the recording stops and `postProcess` only
when its own scrub and catalogue -- the AI pass included -- have finished. In
between, for minutes, it is writing the files a phase run would write.

Both phases are refused (3) when `session.json` has no `postProcess` AND the
recorder's process (`pid`) is still running. The second half is what makes it
usable: a recorder KILLED during its AI pass leaves "ended, no postProcess"
for good, and that capture is the one `-Catalogue` exists for. A flag-only
rule would refuse it, and every cancelled capture, forever.

The recorder's own liveness test is a bare "does that process id exist", and
says of itself that it cannot tell a recorder from an unrelated process that
inherited the id. Here that would refuse a capture for as long as a stranger
ran, so one more thing is asked: a recorder is older than the session it
opened, and a process that started after `startedUtc` is not it. When the ages
cannot be compared, the process is taken to be the recorder.

### Output

- Status lines on the information stream (selection, phase start), warnings on
  the warning stream, refusals on stderr.
- `-Catalogue` (alone or after `-Scrub`) emits the catalogue rows as
  `WebApiDiscovery.HarCapture.CatalogueEntry`, like a recording does.
- `-Scrub` alone emits nothing on the success stream.

### What the front door reads from session.json, and why

Two facts the shared walk does not carry.

**The recorder signed in** (`signIn.source` is anything but `none`). It handed
the values it filled to the scrub and the gate over stdin; they exist nowhere
else, so a later scrub removes them by field name only and cannot prove them
gone. `postProcess` already warns about exactly this when it scrubs for a
recorder that has exited.

- Neither a `scrubbed.har` nor a quarantined rejected scrub: `-Scrub` warns
  and runs. There is nothing to lose.
- A `scrubbed.har` already there: `-Scrub` **refuses** (3). That file was
  checked against the values and its replacement cannot be, and `-Force` would
  overwrite it with nothing able to put it back. The way through is to move
  the file aside -- the same deliberate act the recorder asks for before it
  replaces a catalogue somebody worked on. (Review finding; the first cut
  warned in both cases.)
- A quarantined REJECTED scrub (`scrubbed.rejected*.har`): `-Scrub` **refuses**
  (3) as well. The gate that rejected it had the sign-in values and may have
  rejected it because one survived; a scrub run now is gated without them, so
  it could pass -- and publish as `scrubbed.har`, and let a catalogue be
  derived from -- the very value that was refused. Moving the quarantined file
  aside is the way through. (Independent review.)

Asked as "not `none`" rather than as the recorder's list of sources, so a
source added later fails closed.

**A `session.json` that cannot be read is treated as a sign-in**, on the same
two terms. It is the only record of the sign-in, so a torn or hand-edited one
has no answer -- and "did not sign in" is the one answer damage must not be
able to produce, since it would switch off the refusal above. (Second review
finding: the first fix read an unreadable file as "no".)

**It was recorded outside a git repository** (`unprotectedTables`). The
recorder placed that capture's substitution tables itself; the scrub re-entry
point writes them only where git confirms they are ignored, and refuses. Its
stated remedies (run inside the owning repository, or pass `--subs`) cannot be
followed through this door, so when the scrub fails on such a capture the run
adds what can be: `git init` the folder that holds `.har-captures`. It is said
AFTER the failure rather than refused up front, because the flag records where
the capture was made, not where it is now -- a folder that has since become a
repository scrubs fine. For the same reason the hint is only given while git
says the capture's folder is still outside a work tree: once it is a
repository, a failure is about something else, and pointing at `git init`
would send the operator away from the cause.

## Evidence Plan

- **Change type**: CLI / PowerShell.
- **Artifact format**: Inline markdown transcript (`cli-evidence.md.tmpl`).
- **Capture command**: `Invoke-HarCapture <fixture> -Scrub`, `-Catalogue`, and
  one refusal, against a temp fixture store.
- **Entry-point file**: `.evidence/<phase-id>/evidence.md`

## Acceptance Criteria

- [x] `-Scrub` runs the scrub and its verification on an existing capture and records nothing.
- [x] `-Catalogue` runs the catalogue step on an existing, already scrubbed capture and records nothing.
- [x] Both together run scrub then catalogue. Neither: today's behaviour, unchanged.
- [x] The selected capture is named; an ambiguous selection and a capture not in the needed state are refused, non-zero, with a clear message.
- [x] Tests for each combination and each refusal.
- [x] The only new parameters anywhere are `-Scrub` and `-Catalogue`.

## Implementation Checklist

- [x] RED: `tests/HarCapturePhases.Tests.ps1` -- each combination, selection by
      URL / directory / label / `-Describe`, every refusal, gate REJECTED and
      ADVISORY through a stubbed-gate copy of the scripts tree, "records
      nothing" (store directories and raw hash unchanged), ambient
      `$WhatIfPreference`.
      `pwsh -NoProfile -Command "Invoke-Pester web-api-discovery/tests/HarCapturePhases.Tests.ps1"`
- [x] RED: `tests/har-recording.Tests.ps1` -- the parameter list becomes
      thirteen with the argument written down; `-Scrub:$false -Catalogue:$false`
      still forwards `start`.
- [x] GREEN: `scripts/capture/Invoke-HarCapture.ps1` -- the two switches, the
      phase block (selection, refusals, the two re-entry calls), help text.
- [x] `tests/har-recatalogue.Tests.ps1` -- the `-WhatIf:$false` tripwire is no
      longer vacuous; say so.
- [x] Refactor: small functions, no duplicated messages.
- [x] Docs: `SKILL.md` parameter table and capture-only section.
- [x] Evidence under `.evidence/`, self review, suites re-run.

## Known gaps (reported, not built -- each needs an owner decision)

- What `-Scrub` still does not answer non-zero for:
  - a BLUNTED scrub, which is warned about and exits 0 -- from `-Scrub` alone
    and from `-Scrub -Catalogue` (where the code is the recorder's). In a full
    recording the same scrub ends 7. Raising it here would be this door adding
    to the recorder's exit mapping;
  - an ADVISORY verdict whose findings report the gate could not write (the
    report is best effort). The wrapper's verdict line then differs from the
    clean one, so it is still WARNED about, but nothing says "advisory" as
    data and the exit stays 0.
- The honest long-term shape for the scrub phase is a `scrub` command on
  `capture-har.js` exposing `postProcess`'s phase A (publish to the session's
  output path, 0/6/7, the sign-in-literals notice in one place). That is new
  command-line surface and was not added. See "For the owner", above.
- Because the phases run from the store root, a place the recorder SUGGESTS
  for a committed reference is in that folder's work tree, not in a linked
  worktree the operator may be standing in. Nothing is written there; it is a
  printed suggestion.
- A capture recorded with `-OutputPath` keeps the artifacts it published
  there; the phases work in the capture's own directory and do not republish.
- The sign-in rule is applied by `-Scrub` only. `Invoke-SanitizeHar <folder>
  -Force` re-scrubs the same captures, replaces the same checked artifacts, and
  says nothing.
- A capture recorded outside a git repository cannot be re-scrubbed until its
  folder is one (above). Making the folder form place the tables as the
  recorder does would change the scrub wrapper for every store run.
- "Its recorder still has it" is told from the recorder's process. Not
  covered: `Stop-HarRecording` post-processing a capture whose recorder
  crashed runs in ANOTHER process, which `session.json` does not name, so a
  phase run at that moment is not refused. Both wrappers, run directly, check
  nothing at all.
- `Find-HarCaptureCandidate` and `Remove-HarCapture` still resolve "which
  capture" separately. They now share the store guard
  (`Get-HarCaptureStoreRoot` / `Test-HarCaptureStorePath` in
  `HarStoreBatch.psm1`) and the wildcard rule; one resolver for both is
  deferred.
- `session.json`'s `postProcess` record is not refreshed by either phase, so a
  failure recorded at capture time is still listed after a successful re-run.
- `-WhatIf` is not offered on `Invoke-HarCapture` (existing guard); #48's
  `-WhatIf` constraint therefore has nothing to apply to yet.
