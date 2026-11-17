#!/usr/bin/env node
'use strict';

/**
 * THE ONE WALK OVER A CAPTURE STORE.
 *
 * `capture-har.js` already had a walk -- `listSessionDirs`, the enumeration
 * `resolveSession` uses to answer "which capture" for `stop`, `status` and
 * `catalogue`. Issue #386 wanted to process a whole store, and issue #387's
 * narrowing comment made the binding call on how: REUSE that enumeration, do
 * not write a second one. Two notions of what a capture is, is the defect this
 * subsystem exists to avoid.
 *
 * So the walk MOVED here rather than being copied. `capture-har.js`'s
 * `listSessionDirs` now delegates to `listSessionDirs` below and keeps its
 * exact old contract (`{ dir, stamp }`, recorder sessions only), while the
 * batch drivers ask `listCaptureDirs` for the same walk with its
 * CLASSIFICATION intact. One traversal, two readings of it -- not two
 * traversals.
 *
 * WHY CLASSIFICATION IS PART OF THE WALK. Measured in a real 88-capture store,
 * a walk that understands only `<host>/<stamp>/session.json` silently misses
 * two whole populations:
 *
 *   LEGACY   date-stamped session directories sitting at the captures ROOT,
 *            written before the host layer existed. Six of them. A host-scoped
 *            walk never descends to them, and they carry a `session.json` and a
 *            raw like any other capture.
 *   FOREIGN  directories holding a capture that this recorder did not make --
 *            mitmproxy `raw.mitm` dumps, five of them, 1,739 entries. They have
 *            no `session.json`, so nothing here knows their provenance, their
 *            URI or their intent.
 *
 * Silently skipping either is the failure mode: the operator asked for the
 * store and got a subset, with nothing said. So a foreign directory is NAMED
 * and DECLINED -- reported by path and marker file, never fed to a HAR scrub
 * that has no session to attribute it to.
 *
 * A DECLINED DIRECTORY IS NOT ALWAYS AN UNKNOWN ONE (issue #102). Measured in
 * a consuming repo on 2026-10-14: of 122 entries, 6 were foreign -- and all six
 * were phone-app captures somebody had imported on purpose, each with a
 * hand-written `CAPTURE.md` saying what it was. `foreign` was never wrong about
 * their provenance; it was the only thing being said about them, and provenance
 * is not the question an operator asking "what have I recorded?" is asking. So
 * a raw with notes beside it is classed IMPORTED and carries its description,
 * while still not being recorder output -- see `recorderOutput`.
 *
 * IT NEVER READS A CAPTURE. Only directory entries and file existence, plus
 * `session.json` and `CAPTURE.md` -- one written by the recorder, one by the
 * operator, and neither holding captured traffic. Nothing here can echo a
 * captured value because nothing here opens a HAR.
 */

const fs = require('fs');
const path = require('path');
// The substitution-table filenames come from subs-destination.js, the one
// place every module that needs them reads them from -- this file, the
// scrubber that writes the tables, the scaffolded .gitignore that protects
// them, and the two gates that recognise them by name (issue #446).
// Re-spelling '.substitutions.json' / '.har-substitutions.json' here would be
// a second copy of a definition that now has exactly one.
const subsDestination = require(path.join(__dirname, '..', 'har', 'subs-destination.js'));
// Where a capture store IS, asked of the one function that decides it -- the
// same one `capture-har.js` asks, anchoring to the repository's MAIN working
// tree rather than to the working directory (#367). Required directly rather
// than through `capture-har.js`, which requires THIS file: a cycle whose
// partially-initialised exports would be a far subtler problem than the
// duplication it avoided.
const repoGuard = require(path.join(__dirname, '..', 'lib', 'repo-workflow-guard.js'));

const CAPTURES_DIR = '.har-captures';
const SESSION_FILE = 'session.json';
const RAW_HAR = 'raw.har';
const RAW_MITM = 'raw.mitm';
// What somebody writes beside a capture this recorder did not make, to say what
// it is. Hand-written, by convention and not by any tool here, which is why the
// reader below is forgiving about its shape.
const CAPTURE_NOTES = 'CAPTURE.md';
const SCRUBBED_HAR = 'scrubbed.har';
const DIGEST_FILE = 'digest.json';
const CATALOGUE_FILE = 'catalogue.json';
// The two substitution-table names, imported rather than re-spelled -- see
// the `subsDestination` require above.
const { LEGACY_SUBS_FILENAME, PII_SUBS_FILENAME } = subsDestination;
// Same stem `capture-har.js` quarantines onto. Matched as a PREFIX because a
// second rejection is given a distinguishing suffix rather than overwriting the
// first -- nothing under .har-captures/ is replaced.
const REJECTED_PREFIX = 'scrubbed.rejected';

/** A recorder capture under `<root>/<host>/<stamp>`. */
const CLASS_SESSION = 'session';
/** A recorder capture at `<root>/<stamp>`, predating the host layer. */
const CLASS_LEGACY = 'legacy';
/** A capture this recorder did not make. Named, and declined. */
const CLASS_FOREIGN = 'foreign';
/**
 * A capture this recorder did not make, that somebody imported ON PURPOSE and
 * described in a `CAPTURE.md` beside the raw.
 *
 * Declined exactly as `foreign` is -- there is still no `session.json`, so
 * nothing can say whose traffic it is. The difference is what the LISTING says:
 * "a phone capture of the grocery app checkout" rather than "unrecognised
 * directory". Use `recorderOutput`, never a class comparison, to decide what
 * the pipeline may touch.
 */
const CLASS_IMPORTED = 'imported';

/**
 * Did THIS recorder make the capture in that directory?
 *
 * The one question every caller deciding whether to PROCESS a capture is
 * actually asking, and it is asked as a positive rather than as a list of
 * classes to exclude. `!== CLASS_FOREIGN` read correctly while `foreign` was
 * the only non-recorder class; the moment `imported` was added it would have
 * silently promoted a capture with no session to attribute its traffic to into
 * the recorder's own enumeration -- the one `stop` and `status` resolve
 * "which capture" from, and the one the HAR scrub runs over. A class added
 * later defaults to "not ours", which is the only safe default.
 */
function isRecorderCapture(captureClass) {
    return captureClass === CLASS_SESSION || captureClass === CLASS_LEGACY;
}

/**
 * The longest description this will carry, and why there is a limit at all.
 *
 * `CAPTURE.md` is hand-written prose of unbounded length, and this string is
 * printed into a console table and read into an agent's context. One notes file
 * with a 40 KB first line would take the whole listing with it.
 */
const DESCRIBE_MAX = 200;
/**
 * How much of the notes file is read. The description is its first line, so
 * there is no reason to pull a megabyte off disk per capture while walking a
 * store -- and a bound is also what keeps a pathological file from being read
 * at all.
 */
const NOTES_READ_BYTES = 8192;

/**
 * Legacy or current, decided on the PARENT DIRECTORY and not on the walk's
 * starting point.
 *
 * The distinction has to survive being asked from anywhere: an operator may
 * point the batch at the store root, at one host folder, or at a single
 * capture, and the same directory must classify the same way in all three. A
 * depth counter measured from wherever the walk began would call every stamp
 * under a host folder "legacy" the moment the operator narrowed to that host --
 * which is the supported way to filter by host, so it would be wrong most of
 * the time it mattered.
 *
 * Sitting directly under `.har-captures` IS the legacy layout, and is the only
 * thing that means it.
 */
function classifySessionDir(dir) {
    return isAtCapturesRoot(dir) ? CLASS_LEGACY : CLASS_SESSION;
}

/** Does this directory sit DIRECTLY under `.har-captures`, with no host layer? */
function isAtCapturesRoot(dir) {
    return path.basename(path.dirname(dir)).toLowerCase() === CAPTURES_DIR;
}

/**
 * The site this capture belongs to, or null when there is no host layer to
 * read one from.
 *
 * Null rather than `.har-captures`, which is what the parent directory
 * literally is for a root-level capture -- printing that in a summary would
 * name the store as though it were a site. Applies to a DECLINED capture at the
 * root as much as to a legacy one: the question is the layout, not the class.
 */
function hostOf(dir) {
    return isAtCapturesRoot(dir) ? null : path.basename(path.dirname(dir));
}

function readJson(p) {
    try { return JSON.parse(fs.readFileSync(p, 'utf8')); } catch (e) { return null; }
}

function isDir(p) {
    try { return fs.statSync(p).isDirectory(); } catch (e) { return false; }
}

function exists(p) {
    try { fs.statSync(p); return true; } catch (e) { return false; }
}

function byteSize(p) {
    try { return fs.statSync(p).size; } catch (e) { return null; }
}

/**
 * Has a scrub already been quarantined here?
 *
 * A rejected scrub is NOT a processed capture -- there is no verified artifact
 * and nothing was promoted -- but it is worth reporting, because "this ran and
 * the gate refused it" and "nobody has touched this" are different states and
 * the operator triages them differently.
 */
function hasRejectedScrub(dir) {
    let names;
    try { names = fs.readdirSync(dir); } catch (e) { return false; }
    return names.some((n) => n.startsWith(REJECTED_PREFIX) && n.endsWith('.har'));
}

/**
 * Does a substitution table already sit beside this capture's raw?
 *
 * The only fact from #387's list this walk did not already compute. Existence
 * ONLY -- these tables are keyed by the plaintext values a scrub replaced, so
 * `.har-substitutions.json` in particular is a reverse lookup of live
 * credentials. Reporting THAT one exists is as far as this goes: never which
 * of the two, never a path handed to a caller that might print it into a
 * shared summary without thinking, and never, ever the contents.
 */
function hasSubstitutionTable(dir) {
    return exists(path.join(dir, LEGACY_SUBS_FILENAME)) || exists(path.join(dir, PII_SUBS_FILENAME));
}

/**
 * `DESCRIBE_MAX` units of a string, never half a character.
 *
 * A plain `slice` counts UTF-16 code units, so a cut landing between the two
 * halves of a surrogate pair -- an emoji in a hand-written notes file is the
 * everyday case -- yields a lone surrogate that renders as a replacement glyph.
 * Iterating by code point and rebuilding is the cheap correct answer at this
 * length.
 */
function capped(text) {
    if (text.length <= DESCRIBE_MAX) return text;
    let out = '';
    for (const ch of text) {
        if (out.length + ch.length > DESCRIBE_MAX) break;
        out += ch;
    }
    return out;
}

/**
 * The description somebody hand-wrote beside a capture, or null if they did not.
 *
 * FORGIVING ABOUT SHAPE ON PURPOSE. Nothing here writes `CAPTURE.md`; a person
 * does, in whatever markdown they felt like. So the first markdown heading is
 * preferred and the first non-empty line is accepted, because requiring a
 * heading would turn "they wrote it down" into "they wrote it down correctly"
 * and report a described capture as unrecognised.
 *
 * Whitespace-only, or a heading with nothing after the hashes, is NOT a
 * description. Reporting one on the file's existence alone would manufacture a
 * described capture whose description is blank -- the state #102 exists to make
 * visible, not to produce.
 *
 * A SYMLINK IS NOT READ, and this is the one file in this walk whose CONTENT
 * is reported. The `imported` class exists for captures brought in from
 * outside -- a phone export, a downloaded archive -- so an archive that
 * recreates a symlink named `CAPTURE.md` pointing at, say, a credentials file
 * would have had its first line read off disk and printed into the listing, an
 * agent's context and the delete confirmation. Found in review.
 *
 * `lstat`, not `stat`: stat resolves the link and answers about the target,
 * which is the question that was wrong. A link costs the directory its
 * description, exactly as any other unreadable shape does.
 *
 * AND NOT A HARD LINK EITHER, which is the half the first fix missed. A hard
 * link is not indirection the filesystem will own up to -- `lstat` cannot tell
 * one from an ordinary file, because at that level it IS one -- so the only
 * thing that distinguishes it is that something else references the same
 * bytes. A hand-written notes file has exactly one directory entry; more than
 * one means this content was published here from somewhere else, and `tar`
 * carries hard-link entries, so the archive that delivers an imported capture
 * can carry this. It costs nothing legitimate to refuse: nobody hard-links
 * their own capture notes.
 *
 * Every failure is null. The walk crosses a whole store, and a notes file that
 * is a directory, unreadable, or not text must cost that directory its
 * description and nothing more: a throw here loses the entire inventory, which
 * is a worse failure than the one being fixed.
 */
function describeFromNotes(dir) {
    const notes = path.join(dir, CAPTURE_NOTES);
    let text;
    try {
        const stat = fs.lstatSync(notes);
        if (!stat.isFile()) return null;
        // `> 1`, not `!== 1`: a filesystem that does not report a link count at
        // all answers 0, and refusing on that would cost every capture on it its
        // description to guard against something that cannot be expressed there.
        if (stat.nlink > 1) return null;
        const fd = fs.openSync(notes, 'r');
        try {
            const buf = Buffer.alloc(NOTES_READ_BYTES);
            const read = fs.readSync(fd, buf, 0, NOTES_READ_BYTES, 0);
            text = buf.slice(0, read).toString('utf8');
        } finally { fs.closeSync(fd); }
    } catch (e) { return null; }

    let fallback = null;
    for (const raw of text.split(/\r?\n/)) {
        const line = raw.trim();
        if (!line) continue;
        if (line.startsWith('#')) {
            const heading = line.replace(/^#+/, '').trim();
            if (heading) return capped(heading);
            continue;   // `#` with nothing after it says nothing
        }
        if (!fallback) fallback = capped(line);
    }
    return fallback;
}

/**
 * What one directory IS, or null if it is not a capture at all.
 *
 * The `session.json` test comes first and decides everything: with it, the
 * recorder made this and its provenance is readable; without it, a raw beside
 * no session is a capture somebody else made. A directory with neither is not a
 * capture -- an analysis folder, a manifest, a rescued output directory -- and
 * returning null for those is why the summary counts stay meaningful.
 */
function describeCaptureDir(dir) {
    const sessionPath = path.join(dir, SESSION_FILE);
    const stamp = path.basename(dir);

    if (exists(sessionPath)) {
        const session = readJson(sessionPath);
        const raw = path.join(dir, RAW_HAR);
        return {
            dir,
            stamp,
            host: hostOf(dir),
            captureClass: classifySessionDir(dir),
            // THIS recorder wrote the session.json, so the provenance is
            // readable and the pipeline may process it. Carried as a field
            // rather than re-derived by each caller from the class, so the
            // PowerShell side asks the same question the walk answered.
            recorderOutput: true,
            reason: null,
            startedUtc: (session && session.startedUtc) || null,
            raw: exists(raw) ? raw : null,
            rawBytes: byteSize(raw),
            // A session the recorder never finished closing. Reported, not
            // refused: `assembleFromLog` exists precisely so an interrupted
            // recording still yields a raw.
            ended: Boolean(session && session.endedUtc),
            describe: (session && session.describe) || null,
            scrubbed: exists(path.join(dir, SCRUBBED_HAR)),
            rejected: hasRejectedScrub(dir),
            digest: exists(path.join(dir, DIGEST_FILE)),
            catalogue: exists(path.join(dir, CATALOGUE_FILE)),
            substitutions: hasSubstitutionTable(dir),
            // WHAT THE RECORDER ALREADY KNEW AND NOBODY READ (issue #450).
            //
            // The recorder writes a post-processing failure into `session.json`
            // and, since f3d0360, prints it; since 9ff81ef it also exits 6. All
            // of that happens ONCE, in the run that failed. Afterwards the
            // capture sits in the store and this walk -- the one thing that
            // looks at every capture, and the input to every batch driver and
            // every agent triaging the store -- read `session.json` and ignored
            // both of these fields entirely.
            //
            // So a capture whose scrub never ran was listed identically to one
            // whose scrub succeeded: `scrubbed: false`, indistinguishable from
            // "not scrubbed yet". That is how a 1.7 GB capture that failed at
            // record time was described eight days later as the most
            // evidence-dense unclaimed capture in the tree and recommended for
            // keeping, while being unprocessable by every tool that would have
            // kept it. An errors array with an entry in it is not a report; it
            // is a place a report could have been read from. This reads it.
            //
            // Carried, not judged. This walk classifies; it does not decide
            // what a failure means. Callers render it.
            postProcessErrors: recordedErrors(session),
            summaryParseError: recordedParseError(session)
        };
    }

    // No session.json. A raw of either kind still means somebody captured
    // traffic here, and the operator pointed the batch at it, so it is named.
    const marker = [RAW_MITM, RAW_HAR].find((n) => exists(path.join(dir, n)));
    if (!marker) return null;
    // Did somebody say what this is? A description is the whole difference
    // between a directory nothing knows anything about and one an operator
    // imported deliberately -- and it changes the class so the listing can show
    // which it is. It does NOT change `recorderOutput`: being described is not
    // being attributable.
    const described = describeFromNotes(dir);
    return {
        dir,
        stamp,
        host: hostOf(dir),
        captureClass: described ? CLASS_IMPORTED : CLASS_FOREIGN,
        recorderOutput: false,
        reason: described
            ? `${marker} with no ${SESSION_FILE}, described in ${CAPTURE_NOTES}`
            : `${marker} with no ${SESSION_FILE}`,
        // NULL ON PURPOSE, and not for want of a path. Handing a caller the
        // raw of a capture with no session.json is handing it to a scrub that
        // could not say whose traffic it scrubbed.
        raw: null,
        // THE SIZE, THOUGH, IS REPORTED. It is a fact about the DIRECTORY
        // rather than about who produced the capture in it -- the same
        // distinction `substitutions` below is drawn on -- and it is the number
        // an operator reads to decide what to keep. A multi-gigabyte mitm dump
        // listed with no size at all is the failure #102 exists to remove, and
        // a size is not a path: nothing can be opened with it.
        rawBytes: byteSize(path.join(dir, marker)),
        ended: false,
        // The recorder's own `startedUtc`, which there is none of here. The
        // stamp is the only time this capture carries, and it is above.
        startedUtc: null,
        describe: described,
        scrubbed: false,
        rejected: false,
        digest: false,
        catalogue: false,
        // Unlike the fields above, this one is NOT hard-coded false. Every
        // other field here describes PROCESSING this walk knows this recorder
        // never did to a directory it does not own the provenance of, so
        // "false" is the honest answer regardless of what is on disk. A
        // substitution table is different: its existence is a fact about the
        // DIRECTORY, not about who produced the capture in it, and these
        // tables hold live credentials in the clear. An operator who ran
        // sanitize-har.js by hand against an orphan raw.har leaves exactly
        // this shape -- foreign, but with a table sitting right there -- and
        // a blanket false would tell them it is not, which is the one wrong
        // answer this field exists to prevent.
        substitutions: hasSubstitutionTable(dir),
        // No session.json, so there is nothing recorded to carry. Empty and
        // null are the honest answers, not "no failures".
        postProcessErrors: [],
        summaryParseError: null
    };
}

/**
 * The post-processing errors `session.json` recorded, as strings.
 *
 * Defensive about the shape because this file is read from disk and may have
 * been written by an older recorder, hand-edited, or truncated. A session whose
 * `postProcess` is missing is not a session that succeeded -- but it is also not
 * one this walk can claim failed, so it reports nothing rather than guessing.
 */
function recordedErrors(session) {
    const errors = session && session.postProcess && session.postProcess.errors;
    if (!Array.isArray(errors)) return [];
    return errors.map((e) => `${e}`).filter((e) => e.length > 0);
}

/**
 * The parse failure the recorder hit when it summarised its own output.
 *
 * Separate from the errors array because it means something different: the
 * errors say a post-processing STAGE failed, this says the recorder could not
 * read back the file it had just written. On the capture that motivated #450 it
 * was the first signal of the whole defect, and it was recorded and never read.
 */
function recordedParseError(session) {
    const e = session && session.summary && session.summary.parseError;
    return e ? `${e}` : null;
}

/**
 * Every root a capture might be FOUND under, when the caller named none.
 *
 * The same union `capture-har.js` reads for `stop`, `status` and `catalogue`:
 * the anchored root, plus the working-directory root a capture may have been
 * written under before #367 moved the anchor. Reading both is free; relocating
 * a raw is the one operation this pipeline will not perform.
 */
function defaultCaptureRoots(cwd) {
    const placement = repoGuard.resolveCaptureRoot(cwd || process.cwd(), CAPTURES_DIR);
    return placement.relocated ? [placement.root, placement.legacyRoot] : [placement.root];
}

/**
 * Every capture at or under `root`, classified.
 *
 * Two levels and no deeper, which is the whole shape of a store: a capture is
 * `<root>/<host>/<stamp>` or, in the legacy layout, `<root>/<stamp>`. Pointing
 * at one host folder finds that host's captures at the first level; pointing at
 * a single capture directory finds exactly it. Recursing further would start
 * calling scrub-output subdirectories captures.
 *
 * Accepts several roots for the reason `capture-har.js` already needed to
 * (#367): a capture written under an older working-directory root has to remain
 * findable, and a union is how it is found without anything being moved.
 */
function listCaptureDirs(root) {
    const roots = Array.isArray(root) ? root : [root];
    const seen = new Set();
    const found = [];

    // Records what `dir` is, and answers ONE question for the caller: is this a
    // leaf?
    //
    // A recorder session IS a leaf -- its own scrub output lives inside it, and
    // a `<stamp>/` directory has no captures under it by construction. So the
    // walk stops there and never calls `scrubbed.har`'s directory a capture.
    //
    // A DECLINED directory is NOT a leaf -- declined being every class that is
    // not recorder output, asked as `isRecorderCapture` rather than as a list
    // of classes to exclude. A described import at host level is declined for
    // exactly the reason an undescribed one is, and must not hide the captures
    // beneath it either. A
    // stray `raw.har` at host level is enough to make that directory look like
    // somebody else's capture; if declining also stopped the descent, one stray
    // file would hide every capture under that host, silently -- the exact
    // failure mode this classification exists to remove. Declining is a
    // statement about the directory, never about the tree below it.
    const consider = (dir) => {
        if (seen.has(dir)) return false;
        const entry = describeCaptureDir(dir);
        if (!entry) return false;
        seen.add(dir);
        found.push(entry);
        return isRecorderCapture(entry.captureClass);
    };

    for (const r of roots) {
        if (!r || !isDir(r)) continue;
        const resolved = path.resolve(r);
        // The root ITSELF may be a capture. An operator who points the batch at
        // one session directory is asking for a batch of one, and refusing that
        // would make "point it at a folder" mean "point it at the right level
        // of folder".
        if (consider(resolved)) continue;
        let children;
        try { children = fs.readdirSync(resolved); } catch (e) { continue; }
        for (const child of children) {
            const childDir = path.join(resolved, child);
            if (!isDir(childDir)) continue;   // a stray file, or anything non-traversable
            if (consider(childDir)) continue;
            let grandchildren;
            try { grandchildren = fs.readdirSync(childDir); } catch (e) { continue; }
            for (const grandchild of grandchildren) {
                const grandchildDir = path.join(childDir, grandchild);
                if (!isDir(grandchildDir)) continue;
                consider(grandchildDir);
            }
        }
    }

    return found;
}

/**
 * The old `capture-har.js` contract, unchanged: recorder sessions only, as
 * `{ dir, stamp }`.
 *
 * `resolveSession` sorts these by stamp and takes the newest, so returning the
 * stamp separately still matters -- the host sorts first in a joined path and
 * would otherwise decide which capture counts as most recent.
 *
 * Legacy captures are now IN this list where before they were invisible. That
 * is the fix, not a side effect: a date-stamped session directory at the
 * captures root is a capture by every test `stop` and `status` apply, and the
 * only reason they could not see it was that the walk started one level too
 * deep.
 *
 * THE CLASS COMES WITH THEM, and it is not decoration. Being findable and being
 * a candidate for "a recorder is running right now" are different questions,
 * and only the first one was being fixed -- see `isDriverAlive`'s callers.
 */
function listSessionDirs(root) {
    return listCaptureDirs(root)
        .filter((e) => isRecorderCapture(e.captureClass))
        .map(({ dir, stamp, captureClass }) => ({ dir, stamp, captureClass }));
}

module.exports = {
    listCaptureDirs,
    defaultCaptureRoots,
    isAtCapturesRoot,
    isRecorderCapture,
    describeFromNotes,
    listSessionDirs,
    describeCaptureDir,
    classifySessionDir,
    CLASS_SESSION,
    CLASS_LEGACY,
    CLASS_FOREIGN,
    CLASS_IMPORTED,
    CAPTURES_DIR,
    CAPTURE_NOTES,
    SCRUBBED_HAR,
    CATALOGUE_FILE,
    REJECTED_PREFIX,
    LEGACY_SUBS_FILENAME,
    PII_SUBS_FILENAME
};

// A JSON printer, and deliberately nothing more.
//
// This is the transport that lets the PowerShell batch drivers use THIS walk
// instead of growing one of their own in a language that cannot import it. It
// takes a path and prints the classified inventory; it has no options, no
// filters and no verbs, because every one of those would be a decision made
// twice. It is plumbing between two halves of one feature, not an operator
// surface. #387 asked for a read-only store REPORT on top of this; that
// report was deliberately never built -- an agent reads this JSON directly,
// and a formatted report with no caller was not worth a new command-line
// surface. #387's scope narrowed to the one field this walk was still
// missing (substitutions, above) instead.
if (require.main === module) {
    const target = process.argv[2];
    // NO PATH MEANS THE STORE THIS CLONE RECORDS INTO, resolved by the one
    // function that decides where that is. It used to be a usage error, which
    // made every caller that wanted the obvious default -- "list what I have
    // recorded" -- re-derive the anchoring rule for itself, in PowerShell,
    // where it would have been free to disagree with the recorder (#102).
    //
    // A store that does not exist yet is an EMPTY store, not an error: nothing
    // has been recorded here, and `[]` is the true answer to a question about
    // it. An explicitly named path that does not exist is still exit 3 below,
    // because there the operator named something and was wrong about it.
    const roots = target ? [target] : defaultCaptureRoots();
    if (target && !exists(target)) {
        process.stderr.write(`capture-store: ${target} does not exist\n`);
        process.exit(3);
    }
    process.stdout.write(JSON.stringify(listCaptureDirs(roots), null, 2) + '\n');
}
