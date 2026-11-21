'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const home = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Pages', 'HomePage.xaml.cs'), 'utf8');
const call = fs.readFileSync(
  path.join(ROOT, 'WhatsappApp', 'Pages', 'CallPage.xaml.cs'), 'utf8');

function slice(text, startMarker, size) {
  const at = text.indexOf(startMarker);
  assert.notEqual(at, -1, 'the method ' + startMarker + ' is in the file');
  return text.slice(at, at + size);
}

test('the home banner says "no audio" only before somebody answers', () => {
  // The note answers a real question - why nothing is heard yet - and that question
  // exists only while the other phone has not answered. It was hanging on the
  // active call instead, where audio is exactly what is flowing, and with the
  // fallback sentence of a build that only signalled.
  const detail = slice(home, 'string detail = kind + " · " + state;', 700);
  assert.doesNotMatch(detail, /CallPhase\.Active\s*\)\s*\{[^}]*Call_NoAudio/s,
    'an active call carries audio and carries no apology');
  assert.match(detail, /CallPhase\.Ringing[\s\S]{0,160}Call_NoAudio|CallPhase\.Connecting[\s\S]{0,160}Call_NoAudio|Call_NoAudio[\s\S]{0,160}CallPhase\.Ringing|Call_NoAudio[\s\S]{0,160}CallPhase\.Connecting/,
    'the note belongs to the half of the call where the far end has not answered');
});

test('the two pages say the same sentence about the same wait', () => {
  const sentence = /Loc\.Get\("Call_NoAudio",\s*"([^"]+)"\)/;
  const homeNote = sentence.exec(home);
  const callNote = sentence.exec(call);
  assert.ok(homeNote, 'the home banner carries the note sentence');
  assert.ok(callNote, 'the call screen carries the note sentence');
  assert.equal(homeNote[1], callNote[1],
    'the fallback text is one sentence, not one per page');
});

test('the call screen still hides the note once the call is up', () => {
  const note = slice(call, 'NoteText.Text = Loc.Get("Call_NoAudio"', 300);
  assert.match(note, /CallPhase\.Active\s*\?\s*Visibility\.Collapsed/,
    'the note is the pre-answer note and leaves when the call goes up');
});