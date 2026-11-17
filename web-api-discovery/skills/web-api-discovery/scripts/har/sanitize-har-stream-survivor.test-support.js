'use strict';

// Preloaded (`node -r`) by sanitize-har-stream.test.js to make the survivor
// guard fire. The sweep leaves nothing for the guard to find on any real
// input -- which is the point of the guard -- so the only way to prove the
// refusal path removes its partial file is to make the guard report one.
const path = require('path');
const subsSurvivors = require(path.join(__dirname, 'subs-survivors.js'));
const real = subsSurvivors.compileSweeper;
subsSurvivors.compileSweeper = (entries, produced) => {
    const sweeper = real(entries, produced);
    if (!sweeper) return sweeper;
    return Object.assign({}, sweeper, {
        survivorsInEntry: (entry, i, findings) => {
            sweeper.survivorsInEntry(entry, i, findings);
            findings.push({ kind: 'substitution-survivor', substitutionKind: 'field',
                namespace: 'field:injected', entryIndex: i, keyPath: 'injected', length: 1, count: 1 });
        },
    });
};
