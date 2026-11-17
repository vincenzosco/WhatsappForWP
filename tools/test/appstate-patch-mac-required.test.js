'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const source = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp/Protocol/Client/AppStateSync.cs'), 'utf8');

// The body of Apply, from its signature to the next member: the checks live in an
// order that decides what a patch is allowed to change, and only a whole method is
// enough to talk about that order.
const apply = source.slice(source.indexOf('public static AppStateResult Apply('));
const body = apply.slice(0, apply.indexOf('private static byte[] ReadRecord('));

test('a patch without its patch MAC is refused before anything is applied', () => {
  // The vendored reference rejects an absent patchMac outright: "MAC omitted" is an
  // invalid patch, not a protocol option. Verifying only when the server supplies
  // the MAC hands a tampering server exactly the case it must not get - genuine
  // value MACs under a spliced or replayed mutation set. The refusal has to land
  // before ReadRecord runs: one record applied is already a mutation of the
  // LT-Hash and of the contact list, and nothing may advance on a refused patch.
  const refusal = body.search(/patch\.PatchMac == null[\s\S]{0,300}?throw new ProtocolException/);
  assert.ok(refusal >= 0,
    'a missing patch MAC is refused with a ProtocolException, not skipped');

  const applied = body.indexOf('ReadRecord(mutation.Record');
  assert.ok(applied > refusal,
    'the refusal comes before any mutation of the patch touches the LT-Hash');

  const version = body.indexOf('result.Version = patch.Version');
  assert.ok(version > refusal,
    'the version a refused patch claims is never recorded');
});

test('the patch MAC is verified whether or not the server supplies one', () => {
  // The check itself binds the mutation set to the version: with a null guard
  // around it, an omitted MAC meant an unchecked patch and the check was theatre.
  assert.ok(!/if \(patch\.PatchMac != null\)/.test(body),
    'no null guard stands between a patch and its MAC check');
  assert.match(body, /if \(!Hmac\.FixedTimeEquals\(expectedPatchMac, patch\.PatchMac\)\)/,
    'the patch MAC is compared unconditionally');
});

test('the patch LT-Hash check binds every patch too', () => {
  // A patch's snapshot MAC is the same binding a snapshot gets, one version at a
  // time. Testing it only when the field arrived lets a server roll a collection's
  // version back by dropping the field from a genuine patch.
  assert.ok(!/patch\.SnapshotMac != null &&/.test(body),
    'the LT-Hash check is not conditional on the field arriving');
  assert.match(body, /if \(!Hmac\.FixedTimeEquals\(computed, patch\.SnapshotMac\)\)/,
    'the computed LT-Hash is compared against the patch snapshot MAC unconditionally');
});

test('a patch without its snapshot MAC is refused the same way', () => {
  const refusal = body.search(/patch\.SnapshotMac == null[\s\S]{0,300}?throw new ProtocolException/);
  assert.ok(refusal >= 0,
    'a patch that carries no LT-Hash MAC is refused, not trusted');

  const version = body.indexOf('result.Version = patch.Version');
  assert.ok(version > refusal,
    'no version is recorded for a patch whose LT-Hash is unbound');
});
