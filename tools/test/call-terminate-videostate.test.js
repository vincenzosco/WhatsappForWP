'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const source = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Protocol/Client/CallSignaling.cs'), 'utf8');

test('a plain terminate carries no videostate attribute', () => {
  // The plain overloads forward -1 as "no video state" and the writers used to
  // guard with `!= 0`, so every ordinary hang-up and reject-timeout went out as
  // videostate="-1". The reader in this same file treats -1 as absent; a peer
  // with a strict parser treats it as a video state that does not exist. The
  // wire never carries the sentinel: the guard is the one the duration attribute
  // already has, `>= 0`.
  assert.ok(source.indexOf('if (videoState != 0)') < 0,
    'the sentinel is not written by a "!= 0" guard');

  const guards = source.match(/if \(videoState >= 0\)/g) || [];
  assert.ok(guards.length >= 2,
    'every terminate writer omits videostate when videoState is the sentinel');
});

test('each writer checks before it writes the attribute', () => {
  // A guard after the write guards nothing, and the two long overloads are two
  // writers: both have to carry the check.
  const writers = source.split('attrs.Add(Key("videostate"');
  assert.equal(writers.length - 1, 2, 'there are two terminate writers');

  for (let i = 0; i < writers.length - 1; i++) {
    const before = writers[i];
    assert.ok(before.lastIndexOf('videoState >= 0') >= 0,
      'the sentinel guard precedes the videostate write in every writer');
  }
});

test('a real video state still goes out', () => {
  // The attribute is not dropped, only the sentinel: a call that ended with the
  // video on says so, and 0 is a state a peer can be told about like any other.
  assert.match(source, /if \(videoState >= 0\) attrs\.Add\(Key\("videostate", Number\(videoState\)\)\)/,
    'the attribute is written for every real state');
});
