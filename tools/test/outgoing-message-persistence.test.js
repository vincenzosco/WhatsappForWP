'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const dataService = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');

function methodBody(source, name) {
  const start = source.search(new RegExp(`(?:void|bool)\\s+${name}\\s*\\(`));
  assert.notEqual(start, -1, `method ${name} exists`);
  const open = source.indexOf('{', start);
  let depth = 0;
  for (let i = open; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}') {
      depth--;
      if (depth === 0) return source.slice(open, i + 1);
    }
  }
  assert.fail(`method ${name} has a readable body`);
}

test('a message this phone writes is armed for the disk like an arriving one', () => {
  // Only the arrival path armed the snapshot save, so a sent message lived in
  // memory alone: it was on screen while the app ran and gone at the next
  // start, which is why the chat showed only what other people had written.
  assert.match(methodBody(dataService, 'AddMessage'), /_saveSoon\.Arm\(\);/,
    'AddMessage must arm the save');
});

test('a row whose status changes here is armed for the disk too', () => {
  for (const name of ['SetMessageStatus', 'SetMessageStatusEverywhere']) {
    assert.match(methodBody(dataService, name), /_saveSoon\.Arm\(\);/,
      `${name} must arm the save`);
  }
  // A removal writes the tail itself instead of waiting for the snapshot.
  assert.match(methodBody(dataService, 'RemoveMessage'), /RewriteTailAsync|_saveSoon\.Arm\(\)/,
    'RemoveMessage must reach the disk');
});
