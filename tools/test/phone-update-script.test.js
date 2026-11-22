'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const update = fs.readFileSync(path.join(ROOT, 'tools', 'phone-update.ps1'), 'utf8');
const cmd = fs.readFileSync(path.join(ROOT, 'tools', 'Aggiorna-WhatsApp.cmd'), 'utf8');
const agents = fs.readFileSync(path.join(ROOT, 'AGENTS.md'), 'utf8');

test('the snapshot comes before anything that touches the phone', () => {
  // A plain install removes the app and its isolated storage with it - the account,
  // the chats and the logs. The snapshot is the only thing that survives one, so it
  // is taken before the first door is tried, not after it has been refused.
  const snapshot = update.indexOf('& $ise ts de');
  const updateDoor = update.indexOf('& $deploy /update');
  const install = update.indexOf('& $deploy /install');
  assert.notEqual(snapshot, -1, 'the script takes an ISETool snapshot');
  assert.notEqual(updateDoor, -1, 'the script tries the update door');
  assert.notEqual(install, -1, 'the script has the install fallback');
  assert.ok(snapshot < updateDoor, 'the snapshot precedes the update');
  assert.ok(snapshot < install, 'the snapshot precedes the install');
});

test('the update is tried before the install, and an install is always restored', () => {
  // `AppDeployCmd /update` is the one door that replaces the package and leaves the
  // storage alone. It is refused when the package does not carry a higher version,
  // which is the fallback's case: install, then put the snapshot back over the
  // emptied store. Every install in the script sits before that restore.
  const updateDoor = update.indexOf('& $deploy /update');
  const install = update.indexOf('& $deploy /install');
  const restore = update.indexOf('& $ise rs de');
  assert.ok(updateDoor < install, 'the update door comes first');
  assert.ok(install < restore, 'the restore follows the install');
  assert.equal(update.split('& $deploy /install').length - 1, 1,
    'the install appears once, in the fallback');
});

test('the steps judge the printed output, not the exit code', () => {
  // AppDeployCmd exits 0 even when it prints `Errore`, so a script that trusts
  // $LASTEXITCODE installs over a refused update without noticing.
  assert.doesNotMatch(update, /\$LASTEXITCODE/,
    'the exit code is never trusted');
  assert.match(update, /-notmatch "Errore\|Error"/,
    'the printed answer decides');
});

test('the read back looks for the session before it says the update kept it', () => {
  // "Updated" and "the account is still there" are two different claims. The claim
  // the person cares about is proved by reading the store back: the mirror file the
  // account lives in and the chat list beside it.
  const verify = update.slice(update.indexOf('4/4'));
  assert.match(verify, /session-settings\.txt/, 'the session file is looked for');
  assert.match(verify, /chats\.json/, 'the chat list is looked for');
  assert.match(verify, /-Filter "session-settings\.txt"/, 'it is read off the phone, not assumed');
});

test('the double-click wrapper runs the script', () => {
  assert.match(cmd, /phone-update\.ps1/, 'the wrapper calls the engine');
  assert.match(cmd, /Aggiorna-WhatsApp|powershell/i, 'and it is the thing a person double clicks');
});

test('AGENTS.md sends every update through the script', () => {
  // The rule that says "never install from here" existed because an install wipes
  // the phone. The rule now names the one door that does not, so the next delivery
  // does not quietly go back to `/installlaunch`.
  assert.match(agents, /Aggiorna-WhatsApp\.cmd/, 'the phone section names the script');
  assert.doesNotMatch(agents, /AppDeployCmd\.exe \/installlaunch/, 'the wiping command is gone from the rules');
});
test('a failure is read in either language the tool prints', () => {
  // AppDeployCmd prints `Errore` on an Italian toolchain and `Error` on an English
  // one. Judging only the Italian word lets a failed update - or a failed restore -
  // pass for success, which is the exact lie this script exists to prevent.
  const judges = update.match(/-(?:not)?match "[^"]+"/g) || [];
  const failureWords = judges.filter((j) => /Errore/.test(j));
  assert.ok(failureWords.length >= 3, 'the printed output is judged at each door');
  for (const judge of failureWords) {
    assert.match(judge, /Errore\|Error/,
      'and the judge catches both words: ' + judge);
  }
});

test('an empty answer is not a success', () => {
  // A tool that printed nothing proved nothing. Only a non-empty answer that carries
  // no failure word counts as the door having worked.
  assert.match(update, /-notmatch "Errore\|Error"[\s\S]{0,40}Trim\(\)|Trim\(\)[\s\S]{0,80}-notmatch "Errore\|Error"/,
    'the success decision requires output that is both present and clean');
});
