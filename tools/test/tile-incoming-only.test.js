'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

test('a person\'s tile carries that person\'s words and never the reader\'s own', () => {
  // The tile body was `contact.LastMessage` - the list's preview, which is the
  // last line of the conversation whatever side wrote it. The phone's own words
  // were sitting on a person's tile. The tile asks for the last line that came
  // FROM them.
  const data = read('WhatsappApp/Services/DataService.cs');
  const publish = /private static void PublishUnread\(Contact contact, bool countKnown\)[\s\S]*?\n        \}/.exec(data);
  assert.ok(publish, 'the publish is there');
  assert.match(publish[0], /LastIncomingMessage/, 'the tile is fed the incoming line');

  const home = read('WhatsappApp/Pages/HomePage.xaml.cs');
  assert.match(home, /TogglePinnedTileAsync\(id, contact\.Name, contact\.LastIncomingMessage\)/,
    'and so is the toggle that writes the tile the first time');
});

test('the last incoming line is written only by a message from the other side', () => {
  const model = read('WhatsappApp/Models/Contact.cs');
  assert.match(model, /LastIncomingMessage/, 'the row keeps it');

  const data = read('WhatsappApp/Services/DataService.cs');
  // Every preview site writes it beside `LastMessage`, under the incoming rule.
  const sites = data.match(/LastIncomingMessage = [^;]+;/g) || [];
  assert.ok(sites.length >= 4, 'the preview sites all write it (' + sites.length + ' found)');
  assert.match(data, /if \(message\.IsIncoming\)\s*\r?\n\s*contact\.LastIncomingMessage = message\.Text;/,
    'and a sent message never replaces it');
});