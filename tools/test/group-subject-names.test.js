// A group row is called by its subject, and a row named `Gruppo 1203…` is not
// named at all - it is the placeholder this list writes when nobody has said what
// the group is called. Asking the server only for rows with no name meant asking
// for none of them, so the placeholder was kept for ever while the official app
// showed the subject (the account of 2026-11-23: `Gruppo 393270209946-1441923603`
// on every group row).
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..', '..');
const read = (p) => fs.readFileSync(path.join(root, p), 'utf8');

const native = read('WhatsappApp/Services/NativeConnection.cs');

function slice(text, marker, span) {
  const at = text.indexOf(marker);
  assert.ok(at >= 0, 'the code still has ' + marker);
  return text.slice(at, at + span);
}

test('the group names are asked for rows that are only called after their address', () => {
  const method = slice(native, 'private async Task NameGroupsAsync(', 2600);
  assert.match(method, /ChatListMerge\.LooksLikeFallback\(row\.Name, row\.Jid\)/,
    'a placeholder name counts as unnamed');
  assert.doesNotMatch(method, /if \(!string\.IsNullOrEmpty\(row\.Name\)\) continue;/,
    'and a row is not skipped just because something is written in its name');
});

test('the answer is what the row is renamed to', () => {
  const method = slice(native, 'private async Task NameGroupsAsync(', 5200);
  assert.match(method, /row\.Name = subject;/, 'the subject replaces the placeholder');
  assert.match(method, /NameBook\.Clean\(group\.Subject\)/, 'and is read from the group metadata');
});

test('the placeholder is what every language writes a nameless group with', () => {
  const merge = read('WhatsappApp/Services/ChatListMerge.cs');
  assert.match(merge, /name\.StartsWith\("Gruppo "/, 'the Italian placeholder is known');
  assert.match(merge, /name\.StartsWith\("Group "/, 'and the English one');
});