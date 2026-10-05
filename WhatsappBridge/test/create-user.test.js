'use strict';

// The admin tool that hands a token out by hand. It used to pass the name as
// the device id, which left the user anonymous in the store and keyed the token
// on a value no phone ever sends; this pins the name and the token it prints.
const test = require('node:test');
const assert = require('node:assert');
const { spawnSync } = require('node:child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const { createUserStore } = require('../users');

test('create-user.js names the user and prints a working token', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'wp8-create-user-'));
  const file = path.join(dir, 'users.json');
  try {
    const run = spawnSync(process.execPath, [path.join(__dirname, '..', 'create-user.js'), 'vincenzo', '--file', file], {
      encoding: 'utf8'
    });
    assert.strictEqual(run.status, 0, run.stderr);
    assert.match(run.stdout, /name:\s+vincenzo/);

    const tokenMatch = run.stdout.match(/token:\s+(\S+)/);
    assert.ok(tokenMatch, 'the token is printed');

    const store = createUserStore({ file });
    assert.strictEqual(store.count(), 1);
    const user = store.verify(tokenMatch[1]);
    assert.ok(user, 'the printed token verifies');
    assert.strictEqual(user.name, 'vincenzo', 'the name is stored, not left empty');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
