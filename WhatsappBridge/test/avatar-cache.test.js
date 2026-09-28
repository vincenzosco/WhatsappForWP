'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { createAvatarCache, DEFAULT_TTL_MS } = require('../avatar-cache');

test('una immagine chiesta due volte si scarica una volta', () => {
  const cache = createAvatarCache();
  assert.strictEqual(cache.get('a@s.whatsapp.net'), undefined);

  cache.put('a@s.whatsapp.net', 'AAAA');
  assert.strictEqual(cache.get('a@s.whatsapp.net'), 'AAAA');
});

test('una voce scaduta non risponde piu', () => {
  let clock = 1000;
  const cache = createAvatarCache({ ttlMs: 60000, now: () => clock });

  cache.put('a@s.whatsapp.net', 'AAAA');
  clock += 60000;
  assert.strictEqual(cache.get('a@s.whatsapp.net'), 'AAAA');

  clock += 1;
  assert.strictEqual(cache.get('a@s.whatsapp.net'), undefined);
});

test('i byte di una immagine si tengono cinque minuti', () => {
  assert.strictEqual(DEFAULT_TTL_MS, 5 * 60 * 1000);
});

test('una chat senza immagine si ricontrolla prima', () => {
  let clock = 0;
  const cache = createAvatarCache({ missingTtlMs: 1000, now: () => clock });

  // Un JID senza foto puo' aggiungerla: la risposta "non ce l'ha" non vale
  // quanto i byte di un'immagine.
  cache.put('b@s.whatsapp.net', null);
  assert.strictEqual(cache.get('b@s.whatsapp.net'), null);

  clock += 1001;
  assert.strictEqual(cache.get('b@s.whatsapp.net'), undefined);
});

test('il tetto butta fuori quello che e entrato per primo', () => {
  const cache = createAvatarCache({ maxEntries: 2, now: () => 0 });

  cache.put('a', 'A');
  cache.put('b', 'B');
  cache.put('c', 'C');

  assert.strictEqual(cache.size(), 2);
  assert.strictEqual(cache.get('a'), undefined);
  assert.strictEqual(cache.get('c'), 'C');
});

test('riscrivere una voce la tiene', () => {
  const cache = createAvatarCache({ maxEntries: 2, now: () => 0 });

  cache.put('a', 'A');
  cache.put('b', 'B');
  cache.put('a', 'A2');
  cache.put('c', 'C');

  assert.strictEqual(cache.get('a'), 'A2');
  assert.strictEqual(cache.get('b'), undefined);
});

test('una chiave vuota non entra nella cache', () => {
  const cache = createAvatarCache();
  cache.put('', 'AAAA');
  cache.put(null, 'AAAA');
  assert.strictEqual(cache.size(), 0);
});
