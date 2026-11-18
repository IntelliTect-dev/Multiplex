# The check-only start also verifies the browser driver and the operator profile

- Issue: https://github.com/IntelliTect-dev/Multiplex/issues/25
  (carried over from IntelliTect-Samples/web-api-discovery#105)
- PR:    (filled in once the PR exists)
- Slug:  25-check-only-verifies-dependencies

## Overview

`capture-har.js start --validate-only` is the preflight a caller runs before it
starts the recorder somewhere its errors cannot be seen (detached, a hidden
console, a schedule, an agent). By design it skipped the gates the real start
runs first, so a folder with no operator profile, or a machine with no
`playwright` module, validated with exit 0 and then failed the real run where
nobody was looking. This change makes the check-only start answer those gates
too, in its existing JSON, without launching, prompting, writing or installing
anything.

## Approved Design

### 1. No new option

The original issue offers `--check-dependencies` or a `doctor` command. Neither
is added: the existing `--validate-only` answers the question. `START_OPTIONS`
is unchanged, which `tests/capture-dependency-preflight.Tests.ps1` already pins
by equality.

### 2. The same three gates, asked instead of enforced

A real `start` can be refused, before any browser, by three gates. The
check-only start runs the SAME functions with the same arguments the real start
derives from its own command line, and collects their answers instead of
returning on the first refusal:

| check (`name`)     | the real start's gate                                             | applies |
|--------------------|-------------------------------------------------------------------|---------|
| `agent`            | `resolveAgent({ ai, claudeOnPath: claudeOnPath() })`              | only with `--ai` |
| `browser-driver`   | `preflightDependencies({ isTty: false, requireBrowser: ownsRecordHar })` | always |
| `operator-profile` | `preflightProfile(false)`                                         | always |

- `requireBrowser` is `ownsRecordHar`, exactly as the real start passes it: the
  default and `--isolated` modes need the module AND a Chromium build;
  `--browser native`, `--attach` and `--ai` need only the module.
- `isTty` is forced to `false`. That is what guarantees no prompt and no
  install offer, and it makes the verdict the one an UNATTENDED start gets --
  which is the caller this flag exists for. At a terminal the real start would
  offer to install or scaffold instead of refusing; the check still reports the
  fact.
- `--profile` names the BROWSER profile. The operator profile is discovered
  upward from the working directory by `loadProfile({})` and no option moves
  it, so `--profile` does not change the `operator-profile` answer -- as in the
  real start.
- One collector, `startGateVerdict`, sits beside `preflightProfile`. `start`
  calls it once, in its existing `--validate-only` return block. The real
  start's gates are not restructured.

### 3. The verdict, in the JSON `--validate-only` already prints

stdout stays one JSON document: the resolved session, plus ONE new top-level
key. It is built only in the check-only branch and is never part of
`session.json`.

```json
{
  "...": "every key the session had, unchanged",
  "validation": {
    "ok": false,
    "checks": [
      { "name": "browser-driver",   "ok": false, "reason": "the playwright module was not found. Searched, in order: ..." },
      { "name": "operator-profile", "ok": true,  "reason": null }
    ]
  }
}
```

- `checks` is in the order the real start asks, so the first failure is the
  message a real start would have ended on. All failures are reported in one
  run rather than one per run.
- `reason` is the real start's own message for that refusal (without the
  `capture-har: ` prefix), `null` on a pass.
- Prose stays on stderr: each failing reason exactly as the real start prints
  it, then one summary line. The passing line is unchanged
  (`--validate-only OK (no browser launched)`).
- The JSON is printed pass or fail. A usage error is still exit 2 with no JSON.

### 4. Exit code

`1` when any check fails, `0` when all pass. `1` is the code the real start
itself exits with for each of these three refusals, so the check and the start
agree; `2` (usage error) is unchanged. Documented in the header's exit-code
block. No new code.

### 5. Side effects

The check-only path must write nothing. Two existing behaviours had to change:

- `--isolated --validate-only` called `fs.mkdtempSync`, leaving a
  `har-capture-*` folder in the temp directory on every check. It now names the
  template (`<tmp>/har-capture-XXXXXX`) without creating it. `profileDir` stays
  a string.
- A missing module makes the resolver ask `npm root -g`. Measured (npm 11 on
  Node 26): that writes a debug log into npm's cache (`_logs`), creates the
  cache folder on a machine that has none, can consult the registry for an
  update notice, and leaves a compile cache in the temp directory. The check
  keeps the lookup (dropping it would fail a machine with a custom npm prefix
  that starts fine) but runs it with `npm_config_logs_max=0`,
  `npm_config_update_notifier=false`, `npm_config_cache` pointed at the
  already-existing temp directory and `NODE_DISABLE_COMPILE_CACHE=1`, through
  the resolver's existing `npmGlobalRoot` seam. These are npm's and Node's own
  settings, set on that one child process; the recorder reads none of them.
  The real start is unchanged.

The `claude --version` probe is reused as-is for `--ai`: it is the question the
launch asks (a PATH lookup says yes to a `claude.cmd` that cannot be spawned).

### 6. Deliberately not verified

Other reasons a real start can still refuse are out of scope and stay
unchecked: a capture store that is not gitignored, a capture profile that is
already recording, Chrome missing for `--browser native`, nothing answering at
an `--attach` endpoint. The docs say so.

### 7. `Invoke-HarCapture`

It exposes no check-only path today, so none is added (that would be a new
parameter).

### 8. Existing suites

Several suites use `--validate-only` as a dependency-free seam and assert exit
0 on machines with no driver, no agent and no operator profile. They keep their
assertions and get fixtures instead: one shared test-support module plants a
stand-in `playwright`, a Chromium directory and a spawnable `claude`, and each
fixture folder gets an operator profile.

## Evidence Plan

- **Change type**: CLI / bug fix
- **Artifact format**: Inline, ANSI-stripped captured output (markdown)
- **Capture command**: run `node capture-har.js start --uri https://example.com
  --describe "..." --port 0 --validate-only` in (a) an empty folder on a
  machine view with no driver, (b) the same folder after planting the profile
  and the driver; capture stdout, stderr and the exit code of each, plus the
  "before" from the base commit for (a)
- **Entry-point file**: `.evidence/<phase-id>/evidence.md`

## Acceptance Criteria

Each is pinned by a case in `scripts/capture/capture-check-only.test.js`
(named in brackets) unless it says otherwise.

- [x] With the `playwright` module missing, `start --validate-only` exits 1 and
      its JSON names `browser-driver` as failed with the reason; nothing is
      prompted for or installed. [C2, C13]
- [x] With no `.har-profile.json` at or above the working directory it exits 1
      and names `operator-profile`, with the real start's own reason. Same for
      a profile with no salt. [C3, C4]
- [x] With both present it exits 0 and `validation.ok` is `true`. [C1]
- [x] The answer follows the arguments: `--browser native`, `--attach` and
      `--ai` need only the module; the default and `--isolated` also need the
      Chromium build; `--ai` needs a `claude` that can be spawned. [C6, C7, C8, C9]
- [x] For every failing case the real start, given the same arguments
      unattended, exits 1 with the same reason. [C10; C11 for the passing side]
- [x] The check-only path writes nothing, launches nothing, makes no outbound
      connection and never prompts -- including at a terminal. [C12, C13]
- [x] A passing run's JSON has every key it had before, with the same values;
      `validation` is the only addition. [C1, and a run of the base commit's
      recorder beside this one on identical arguments, recorded in the
      evidence]
- [x] No new command-line option, switch, positional argument or environment
      variable. [`tests/capture-dependency-preflight.Tests.ps1` pins
      `START_OPTIONS` by equality, unchanged]
- [x] The usage header in `capture-har.js` and `SKILL.md` describe it.

## Implementation Checklist

- [x] **T1** `scripts/capture/capture-start-gates.test-support.js`: fixtures for
      a machine on which the gates pass or fail on purpose (`plantPlaywright`,
      `plantChromium`, `plantOperatorProfile`, `plantAgent`, `gateVars`,
      `childEnv`, `driverReach`).
- [x] **T2** `scripts/capture/capture-side-effects.test-support.js`: a `-r`
      preload that records every spawn, every filesystem mutation and every
      outbound connection the recorder makes, and can present stdin as a
      terminal.
- [x] **T3** (RED) `scripts/capture/capture-check-only.test.js`: the verdict
      cases, the mode matrix, parity with the real start, and the
      wrote-nothing / launched-nothing / asked-nothing cases; the one case in
      `capture-preflight.test.js` that pinned the old behaviour, inverted.
      Watched failing on the base behaviour: 13 of 16, and 1 of 29.
      Commit `test(web-api-discovery): pin what the check-only start must answer`.
- [x] **T4** (GREEN) `capture-har.js`: `quietNpmGlobalRoot`, `startGateVerdict`,
      the `--validate-only` return block, the `--isolated` template path, and
      the header (usage, `--validate-only`, exit codes).
- [x] **T5** Existing suites: give `--validate-only` callers passing gates
      (`capture-durability`, `capture-native-attach`, `capture-ai-drive`,
      `capture-har`, `har-recording.Tests.ps1`). In the same commit as T4,
      because that is the commit they are needed by.
      Commit `feat(web-api-discovery): the check-only start says whether a real start would be refused`.
- [x] **T6** Docs: `SKILL.md` and the stale line in the preflight wrapper's
      header.
      Commit `docs(web-api-discovery): check first, before a start nobody is watching`.
- [x] **T7** Refactor pass, suites on this machine AND with the machine's own
      driver and agent hidden (the CI shape), evidence capture, self review.
