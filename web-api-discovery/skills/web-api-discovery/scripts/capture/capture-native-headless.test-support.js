'use strict';

/**
 * capture-native-headless.test-support.js -- make the RECORDER's Chrome
 * headless, for tests only (#88).
 *
 * Preloaded (`node -r`) into the recorder process by capture-native-attach's
 * I1, which drives `capture-har.js start --browser native` end to end. That
 * launch is the product's, and it carries no headless flag on purpose -- a
 * protected site detects one (#73), and N5 pins the argument list. So the flag
 * is not added to the product; it is added HERE, around `child_process.spawn`,
 * before `capture-har.js` loads and takes its reference to it.
 *
 * Narrow by construction: only a spawn that carries `--remote-debugging-port=`
 * and no `--headless` is touched, which is the native Chrome launch and nothing
 * else the recorder starts. `about:blank` stays the last argument, as the
 * launch puts it.
 */

const childProcess = require('child_process');

const realSpawn = childProcess.spawn;

function headlessArgs(argv) {
    if (!Array.isArray(argv)) return argv;
    if (!argv.some((a) => String(a).startsWith('--remote-debugging-port='))) return argv;
    if (argv.some((a) => String(a).startsWith('--headless'))) return argv;
    const rest = argv.filter((a) => a !== 'about:blank');
    return rest.concat(['--headless=new'], argv.includes('about:blank') ? ['about:blank'] : []);
}

childProcess.spawn = function spawn(command, args, options) {
    return realSpawn.call(this, command, headlessArgs(args), options);
};

module.exports = { headlessArgs };
