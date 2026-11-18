#!/usr/bin/env node
// A stand-in for a website, on loopback, for tests that need to see what a
// generated client really puts on the wire (issue #34).
//
// It answers every request with one canned JSON body and writes the request
// line it received -- method and raw request target, exactly as sent, before
// any decoding -- to a log, one line per request. A test starts it, points a
// generated wrapper at it (the base URL is baked in at generation, so it must
// be listening first), makes a real tools/call, and reads the log.
//
// It takes no arguments and reads no environment variable. Its working
// directory is its configuration: it serves `response.json` from there and
// appends to `requests.log` there, so a test gives it a directory of its own.
//
// It listens on 127.0.0.1 only, on a port the system picks, and says which on
// the first line of stdout: `LISTENING <port>`. It stops when its standard
// input closes -- so it cannot outlive the test that holds that pipe -- or when
// the process is stopped. No real site is involved.
//
// Not a test itself, and deliberately not named *.test.js: the Node test runner
// would start it and wait for it to exit.
'use strict';

const fs = require('fs');
const http = require('http');

const RESPONSE_FILE = 'response.json';
const LOG_FILE = 'requests.log';

const body = fs.readFileSync(RESPONSE_FILE);

const server = http.createServer((request, response) => {
    // request.url is the request target as it arrived: the path and the query
    // string, still percent-encoded.
    fs.appendFileSync(LOG_FILE, request.method + ' ' + request.url + '\n');
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
