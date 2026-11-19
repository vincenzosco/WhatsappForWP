'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

const data = () => read('WhatsappApp/Services/DataService.cs');
const method = (name, size) => {
  const src = data();
  const at = src.indexOf(name);
  return src.slice(at, at + size);
};

test('the repair drops the rows nothing names, before it writes the list', () => {
  // A row no message names is not a chat this phone has ever had a message of:
  // no tail on the disk and no conversation in memory leaves a row the list
  // invented. Writing the same rows back after deleting the file would be the
  // photograph again - the repair has to drop them between the rebuild and the save.
  const resync = method('public async Task ResyncChatsAsync', 1800);
  const restore = resync.indexOf('RestoreRowsFromTheirTailsAsync');
  const drop = resync.indexOf('DropRowsNothingNamesAsync');
  const save = resync.indexOf('SaveSnapshotAsync');
  assert.ok(restore >= 0 && drop > restore, 'the rows come back first');
  assert.ok(save > drop, 'and the unnamed ones leave before the list is written');
});

test('only a tail or a live conversation names a row', () => {
  const drop = method('private async Task DropRowsNothingNamesAsync', 2600);
  assert.match(drop, /TailsAsync/, 'the tails on disk name their rows');
  assert.match(drop, /_chatMessages/, 'and a conversation still in memory is a chat');
  assert.match(drop, /_contacts\.RemoveAt|_contacts\.Remove/,
    'the drop takes the row out of the list');
  assert.match(drop, /_contactIndex\.Remove/,
    'and out of the index, the way DeleteChat does it');
  assert.doesNotMatch(drop, /MessageCache\.(Delete|DeleteAll)/,
    'dropping a row the repair never named must not drop a message');
});