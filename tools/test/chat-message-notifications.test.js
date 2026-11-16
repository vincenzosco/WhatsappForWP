'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const MODEL = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Models/ChatMessage.cs'), 'utf8');

// The bubble draws computed getters (ShowsText, DocumentTitle, CanRetry, ...) and
// those are built from other fields. A setter that changes an input without
// raising the computed name leaves the binding on the old answer while the
// getter already says the new one. The case that shows it on the phone: a
// document row the history brought has no file name yet, and when the download
// completes `DataService.ApplyMedia` writes MediaFileName onto the row that is
// already on screen - the caption under the card has to appear then, and it
// never did because ShowsText was not raised again.

/** The body of a property's setter, braces included. */
function setterBody(source, property) {
  const decl = source.search(
    new RegExp('public\\s+[\\w<>,.\\[\\]]+\\s+' + property + '\\s*\\r?\\n\\s*\\{'));
  assert.ok(decl >= 0, 'property ' + property + ' exists');
  const rest = source.slice(decl);
  const setAt = rest.search(/\r?\n\s+set\b/);
  assert.ok(setAt >= 0, property + ' has a setter');
  const open = rest.indexOf('{', setAt);
  let depth = 0;
  for (let i = open; i < rest.length; i++) {
    if (rest[i] === '{') depth++;
    else if (rest[i] === '}') {
      depth--;
      if (depth === 0) return rest.slice(open, i + 1);
    }
  }
  assert.fail(property + ' has a readable setter body');
}

/** The explicit OnPropertyChanged("...") names a setter raises. */
function raisedNames(body) {
  return [...body.matchAll(/OnPropertyChanged\("([A-Za-z0-9_]+)"\)/g)].map((m) => m[1]);
}

test('the file name arriving on a document makes the caption appear', () => {
  const names = raisedNames(setterBody(MODEL, 'MediaFileName'));
  assert.ok(names.includes('ShowsText'),
    'MediaFileName does not raise ShowsText: after the download the caption never '
    + 'appears under the card and the bubble stays on its old answer');
});

test('every field a bubble computation reads raises that computation', () => {
  const required = {
    Text: ['ShowsText', 'ShownText', 'DocumentTitle', 'DocumentBadge', 'CanRetry'],
    SenderName: ['Initials'],
    Timestamp: ['FormattedTime'],
    TimestampWire: ['FormattedTime'],
    Status: ['CanRetry'],
    Type: ['IsVideo', 'IsAudio', 'ShowsText', 'CanRetry'],
    IsIncoming: ['IsOutgoing', 'CanRetry'],
    MediaData: ['MediaImage'],
    MediaFileName: ['DocumentTitle', 'DocumentBadge', 'ShowsText'],
    MediaType: ['IsVideo', 'IsAudio', 'IsDocument', 'ShowsText', 'DocumentTitle',
      'DocumentBadge', 'CanRetry'],
    MediaFilePath: ['CanRetry'],
    MediaSizeBytes: ['MediaSizeText'],
    IsPlaying: ['IsAudioNotPlaying']
  };

  for (const property of Object.keys(required)) {
    const names = raisedNames(setterBody(MODEL, property));
    for (const name of required[property]) {
      assert.ok(names.includes(name),
        property + ' does not raise ' + name
        + ': a binding on that computation stays on its old answer');
    }
  }
});
