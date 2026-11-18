#!/usr/bin/env node
// A stand-in for a website, on loopback, for tests that need to see what a
// generated client really puts on the wire (issue #34).
//
// It answers every request with one canned JSON body and writes the request
// line it received -- method and raw request target, exactly as sent, before
// any decoding -- to a log file, one line per request. A test starts it, points
// a generated wrapper at it (the base URL is baked in at generation, so it must
// be listening first), makes a real tools/call, and reads the log.
//
// It listens on 127.0.0.1 only, on a port the system picks, and stops when its
// standard input closes or the process is stopped. No real site is involved.
//
// Usage: node stand-in-site.js <log file> <response body file>
//   stdout, first line:  LISTENING <port>
//
// Not a test itself, and deliberately not named *.test.js: the Node test runner
// would start it and wait for it to exit.
'use strict';

const fs = require('fs');
const http = require('http');

const [logFile, bodyFile] = process.argv.slice(2);
if (!logFile || !bodyFile) {
    process.stderr.write('usage: node stand-in-site.js <log file> <response body file>\n');
    process.exit(2);
}
const body = fs.readFileSync(bodyFile);

const server = http.createServer((request, response) => {
    // request.url is the request target as it arrived: the path and the query
    // string, still percent-encoded.
    fs.appendFileSync(logFile, request.method + ' ' + request.url + '\n');
    request.resume();
    request.on('end', () => {
        response.writeHead(200, { 'Content-Type': 'application/json', 'Content-Length': body.length });
        response.end(body);
    });
});

server.listen(0, '127.0.0.1', () => {
    process.stdout.write('LISTENING ' + server.address().port + '\n');
});

// The test that started this owns its lifetime; closing stdin is "goodbye".
process.stdin.on('end', () => server.close(() => process.exit(0)));
process.stdin.resume();
