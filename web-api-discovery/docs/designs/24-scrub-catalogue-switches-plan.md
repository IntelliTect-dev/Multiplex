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
  -WhatIf:$false`. The FOLDER form, on purpose. #48 rejected composing the
  FILE form (`-InputHar raw.har -OutputHar ...`) because it writes an unjudged
  file straight to its destination. The folder form (since #386) scrubs under
  a temporary name, calls it `scrubbed.har` only after the gate passes, and
  quarantines a refusal as `scrubbed.rejected.har` -- so the front door
  restates none of promotion, quarantine or the verdict. `-Force` because the
  operator asked for this capture to be scrubbed again; without it the wrapper
  answers "already scrubbed -- use -Force", a switch this door does not have.
  `-WhatIf:$false` because that wrapper supports ShouldProcess and an ambient
  `$WhatIfPreference` would otherwise turn the phase into a silent no-op.
- **Catalogue** -> `capture-har.js catalogue <capture directory> --output-path
  <capture directory>`. `--output-path` is pinned to the capture's own
  directory so the catalogue is derived from the `scrubbed.har` a `-Scrub`
  just wrote there, not from a copy published elsewhere by an earlier
  `-OutputPath` run. The recorder asks the leak gate again, runs the digest,
  asks `decideCatalogueRunner`, and refuses to replace a catalogue that
  carries described actions -- all unchanged.
- Nothing is added to `capture-har.js`, `capture-store.js` or either wrapper.

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
| no capture matches / `-Describe` matches none | 3 |
| the only match is not recorder output (foreign / imported) | 3 |
| `-Scrub` and the capture has no `raw.har` | 3 |
| `-Scrub` and the capture signed in AND already has a `scrubbed.har` (see below) | 3 |
| `-Catalogue` alone and the capture has no `scrubbed.har` (never scrubbed, or its only scrub was REJECTED) | 3 |

A first argument that is not a URL with a scheme, a directory or a label that
matches is refused (3) without being echoed: a URL pasted without `https://`
lands there and may carry a token.

2 is the recorder's usage-error code; 3 is its "no capture / nothing to
catalogue" code. A phase's own exit code is passed through whole.

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

- No `scrubbed.har` yet: `-Scrub` warns and runs. There is nothing to lose.
- A `scrubbed.har` already there: `-Scrub` **refuses** (3). That file was
  checked against the values and its replacement cannot be, and `-Force` would
  overwrite it with nothing able to put it back. The way through is to move
  the file aside -- the same deliberate act the recorder asks for before it
  replaces a catalogue somebody worked on. (Review finding; the first cut
  warned in both cases.)

Asked as "not `none`" rather than as the recorder's list of sources, so a
source added later fails closed.

**It was recorded outside a git repository** (`unprotectedTables`). The
recorder placed that capture's substitution tables itself; the scrub re-entry
point writes them only where git confirms they are ignored, and refuses. Its
stated remedies (run inside the owning repository, or pass `--subs`) cannot be
followed through this door, so when the scrub fails on such a capture the run
adds what can be: `git init` the folder that holds `.har-captures`. It is said
AFTER the failure rather than refused up front, because the flag records where
the capture was made, not where it is now -- a folder that has since become a
repository scrubs fine.

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

- `-Scrub` alone exits 0 on an ADVISORY verdict: the folder form of the scrub
  wrapper answers 0/1 and says the verdict in its summary. Mapping 4 -> 7 here
  would restate the recorder's exit mapping, which #48 rules out. `-Scrub
  -Catalogue` does answer 7, because the recorder asks the gate again.
- The honest long-term shape for the scrub phase is a `scrub` command on
  `capture-har.js` exposing `postProcess`'s phase A (publish to the session's
  output path, 0/6/7, the sign-in-literals notice in one place). That is new
  command-line surface and was not added.
- A capture recorded with `-OutputPath` keeps the artifacts it published
  there; the phases work in the capture's own directory and do not republish.
- The sign-in rule is applied by `-Scrub` only. `Invoke-SanitizeHar <folder>
  -Force` re-scrubs the same captures, replaces the same checked artifacts, and
  says nothing.
- A capture recorded outside a git repository cannot be re-scrubbed until its
  folder is one (above). Making the folder form place the tables as the
  recorder does would change the scrub wrapper for every store run.
- A capture whose recorder is still post-processing it is not detected:
  only the recorder can say whether its driver is alive. The same race exists
  for both wrappers run directly.
- `session.json`'s `postProcess` record is not refreshed by either phase, so a
  failure recorded at capture time is still listed after a successful re-run.
- `-WhatIf` is not offered on `Invoke-HarCapture` (existing guard); #48's
  `-WhatIf` constraint therefore has nothing to apply to yet.
