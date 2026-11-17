# 102 -- List recordings with their descriptions, and delete one on explicit request

**Issue:** [#102](https://github.com/IntelliTect-Samples/web-api-discovery/issues/102)
**Branch:** `feat/102-list-delete-recordings`

## Overview

Two operator-facing commands over the capture store, plus the one walk change
that makes a hand-imported phone capture read as a described capture instead of
an unrecognised directory.

## User Story

As an operator who has recorded a web or phone session into `.har-captures/`
I want to see what is in the store and to remove one recording on purpose
So that triage does not mean running an internal script and reading raw JSON,
and so that removing a recording is never something that happens to me.

## Approved Design (maintainer decisions, 2026-10-14)

- **Never delete automatically.** No age sweep, no count or size cap, no bulk
  delete. Every recording is kept until someone deletes *that one* on purpose.
- `Get-HarCapture` and `Remove-HarCapture` are both approved.

### `Get-HarCapture` -- the list

- A thin wrapper over the EXISTING inventory. `capture-store.js` stays the one
  enumeration; this does not grow a second walk. It reuses
  `Get-HarCaptureInventory` from `HarStoreBatch.psm1`, exactly as the batch
  drivers do.
- Emits `WebApiDiscovery.HarCapture.StoreEntry` objects so `ConvertTo-Json`,
  `Where-Object` and `Sort-Object` work on the same result. Console rendering
  comes from `HarCapture.Format.ps1xml`, never from hand-rolled text.
- Fields, one per item the issue lists: `Site`, `Stamp`, `StartedUtc`,
  `Description`, `Bytes`, `Ended`, `Scrubbed`, `Rejected`, `Digest`,
  `Catalogue`, `Errors`, plus `Class` and `Path` (`Path` is what
  `Remove-HarCapture` is given).
- **A missing description is flagged, not blank.** The object carries
  `Description = $null` -- the honest datum -- and the table renders
  `(no description)`, the same idiom the catalogue view already uses for an
  unmeasured count (`--`).
- Default target: the capture store `capture-har.js` itself resolves, so the
  command works with no argument from anywhere in the clone. Asked of
  `capture-har.js --capture-root`, not re-derived in PowerShell.

### `Remove-HarCapture` -- the single delete

- `SupportsShouldProcess` with `ConfirmImpact = 'High'`: confirmation by
  default, `-WhatIf` deletes nothing.
- Takes **exactly one** capture: a path, a `host/stamp` label, a bare `stamp`
  for a legacy capture, or one object from `Get-HarCapture`. A wildcard is
  refused. More than one capture is refused. No pipeline collection input --
  that is the back door to a bulk delete this issue forbids.
- A label that matches more than one capture is refused by naming the matches,
  never resolved to "the newest".
- Deletes the whole session directory, substitution tables included (they are
  keyed by the plaintext the scrub replaced, so leaving one behind would leave
  a reverse lookup of live credentials with no capture to justify it).

### The walk: a described import is not "foreign"

`capture-store.js` classifies a `raw.mitm` with no `session.json` as `foreign`
-- correct about provenance, but it also hides the description such a capture
carries in its hand-written `CAPTURE.md`. Measured 2026-10-14 in a consuming
repo: all 6 `foreign` entries of 122 were described phone-app captures.

- New class `imported`: no `session.json`, but a `CAPTURE.md` beside the raw.
  Somebody brought this in deliberately and wrote down what it is.
- `describe` is read from that `CAPTURE.md` -- first markdown heading, else
  first non-empty line, bounded read, single line, length-capped.
- **The scrub must still decline it**, and that is the risk in adding a class.
  So the decline test stops being `=== 'foreign'` and becomes the complement of
  "a recorder made this": the walk carries `recorderOutput`, and
  `Invoke-HarCaptureBatch` and `listSessionDirs` ask that field. A class added
  later can then never silently un-decline a capture with no session to
  attribute its traffic to.

### The "nothing deletes a raw" comments

`capture-har.js`'s header rule already says `Invoke-SanitizeHar -RemoveSource`
is the one way a raw is removed, and that there is "deliberately no age sweep
and no bulk delete command". `Remove-HarCapture` is a second way, so that
paragraph and `Invoke-SanitizeHar.ps1`'s `-RemoveSource` folder refusal are
updated to name it -- still no sweep, still no bulk delete.

## Evidence Plan

- **Change type**: CLI (PowerShell commands)
- **Artifact format**: markdown
- **Capture command**: `Publish-Evidence.ps1` with the CLI template, over a
  throwaway fixture store
- **Entry-point file**: `.evidence/<phase-id>/evidence.md`

## Acceptance Criteria

- [ ] `Get-HarCapture` over a fixture store lists sessions, legacy captures and
      phone-app captures, each with its description.
- [ ] A capture with no description is visibly flagged.
- [ ] `Remove-HarCapture` deletes exactly one named capture after confirmation.
- [ ] `-WhatIf` deletes nothing.
- [ ] A wildcard or multiple input is refused.
- [ ] A described phone-app capture is still declined by the scrub batch.
- [ ] Pester tests for both, written test-first.

## Implementation Checklist

- [ ] T1 RED: `capture-store.test.js` -- a `raw.mitm` + `CAPTURE.md` with no
      `session.json` classifies `imported`, carries its description, and
      reports `recorderOutput: false`; a bare `raw.mitm` stays `foreign`;
      a recorder session reports `recorderOutput: true`.
- [ ] T2 GREEN: `capture-store.js` -- `CLASS_IMPORTED`, `describeFromNotes()`,
      `recorderOutput`, `isRecorderCapture()`; `listSessionDirs` and
      `consider()` switch to the complement test.
- [ ] T3 RED/GREEN: `HarBatchStore.Tests.ps1` -- the batch declines an
      `imported` capture exactly as it declines a `foreign` one;
      `HarStoreBatch.psm1` asks `recorderOutput`.
- [ ] T4 RED: `tests/HarCaptureList.Tests.ps1` -- `Get-HarCapture` over a
      fixture store: every class listed, descriptions present, missing
      description flagged in the rendered table, objects on the pipeline.
- [ ] T5 GREEN: `scripts/capture/Get-HarCapture.ps1` + the `StoreEntry` views
      in `HarCapture.Format.ps1xml`.
- [ ] T6 RED: `tests/HarCaptureRemove.Tests.ps1` -- deletes one named capture;
      `-WhatIf` deletes nothing; wildcard refused; two captures refused;
      ambiguous label refused; a missing capture is an error.
- [ ] T7 GREEN: `scripts/capture/Remove-HarCapture.ps1`.
- [ ] T8: register both in `WebApiDiscovery.psm1` / `.psd1`.
- [ ] T9: update the "nothing deletes a raw" comments, `SKILL.md`, and the two
      `README.md` trees.
