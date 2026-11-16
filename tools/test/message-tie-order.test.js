'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');

/** The body of a method, from its declaration to the closing brace at eight spaces. */
function methodBody(source, name) {
  const at = source.search(new RegExp('(?:private|public|internal)[^;{]*\\b' + name + '\\s*\\('));
  assert.ok(at >= 0, name + ' esiste');
  const rest = source.slice(at);
  const end = rest.indexOf('\n        }');
  return end < 0 ? rest : rest.slice(0, end);
}

// The history keeps whole seconds, so two rows of one chat often carry the same
// moment. List<T>.Sort does not promise anything about rows that compare equal:
// a merge sorted that way rewrites the order they arrived in, and the tail cache
// writes the shuffled order to the phone (OrderConversation can keep a tie order
// but never recover one). The rule is the one OrderConversation already follows:
// MessageOrder.StableOrder decides, and no timestamp sort is left.

test('il merge dei messaggi non rimescola le righe dello stesso secondo', () => {
  const service = read('WhatsappApp/Services/DataService.cs');
  const body = methodBody(service, 'MergeMessages');

  assert.doesNotMatch(body, /\.Sort\s*\(\s*delegate/,
    'MergeMessages ordina con List.Sort: le righe dello stesso secondo possono scambiarsi');
  assert.match(body, /MessageOrder\.StableOrder/);
});

test('la coda della conversazione non rimescola le righe dello stesso secondo', () => {
  const cache = read('WhatsappApp/Services/MessageCache.cs');
  const body = methodBody(cache, 'AppendCoreAsync');

  assert.doesNotMatch(body, /\.Sort\s*\(\s*delegate/,
    'AppendCoreAsync ordina con List.Sort e scrive sul telefono l\'ordine rimescolato');
  assert.match(body, /MessageOrder\.StableOrder/);
});
