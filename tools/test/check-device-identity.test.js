'use strict';
const test = require('node:test');
const assert = require('node:assert');

const identity = require('../check-device-identity');

const SERVICE = [
  '        private byte[] OurIdentity()',
  '        {',
  '            if (_account != null && _account.DeviceIdentity != null) return _account.DeviceIdentity;',
  '            return null;',
  '        }',
  '',
  '            await WriteAsync(session, id, OutgoingMessage.Node(target.To, id, message, OutgoingMessage.TextType, null,',
  '                target.Devices, timestamp, _random, privacyToken, OurJids(), OurIdentity()));',
  ''
].join('\n');

const GROUP_CALL = [
  '            await WriteAsync(session, id, OutgoingMessage.Group(jid, id, message, OutgoingMessage.TextType,',
  '                null, record, mode, distributions, timestamp, _random, OurIdentity()));',
  ''
].join('\n');

const BUILDER = [
  '            BinaryNode identity = preKey ? DeviceIdentityNode(deviceIdentity) : null;',
  '            if (identity != null) content.Add(identity);',
  '            BinaryNode identity2 = preKey ? DeviceIdentityNode(deviceIdentity) : null;',
  '            if (identity2 != null) content.Add(identity2);',
  ''
].join('\n');

const PAIRING = [
  '            _credentials.DeviceIdentity = identity.Encode();',
  ''
].join('\n');

function problems(overrides) {
  return identity.problemsFor({
    service: (overrides && overrides.service) || SERVICE,
    builder: (overrides && overrides.builder) || BUILDER,
    pairing: (overrides && overrides.pairing) || PAIRING
  });
}

test('un trio coerente passa', () => {
  assert.deepStrictEqual(problems(), []);
});

test('una chiamata di gruppo che chiude con la firma passa', () => {
  assert.deepStrictEqual(problems({ service: SERVICE + GROUP_CALL }), []);
});

test('un invio che non passa la firma si segnala, con la riga', () => {
  const service = SERVICE.replace(
    'target.Devices, timestamp, _random, privacyToken, OurJids(), OurIdentity()));',
    'target.Devices, timestamp, _random, privacyToken, OurJids(), OurJids()));');

  const found = problems({ service: service });
  assert.strictEqual(found.length, 1);
  assert.match(found[0], /NativeConnection\.cs:7:/);
  assert.match(found[0], /OurJids\(\)/);
  assert.match(found[0], /pkmsg/);
});

test('un invio che si dimentica proprio l\'ultimo argomento si segnala', () => {
  const service = SERVICE.replace(
    'privacyToken, OurJids(), OurIdentity()));',
    'privacyToken, OurJids()));');

  const found = problems({ service: service });
  assert.strictEqual(found.length, 1);
  assert.match(found[0], /OurJids\(\)/);
});

test('una chiamata commentata non conta', () => {
  const service = SERVICE + [
    '            // await WriteAsync(session, id, OutgoingMessage.Node(to, id, null, null, null,',
    '            //     devices, 0, _random, null, OurJids())',
    ''
  ].join('\n');

  assert.deepStrictEqual(problems({ service: service }), []);
});

test('un costruttore che attacca il nodo solo una volta si segnala', () => {
  const builder = [
    '            BinaryNode identity = preKey ? DeviceIdentityNode(deviceIdentity) : null;',
    '            if (identity != null) content.Add(identity);',
    ''
  ].join('\n');

  const found = problems({ builder: builder });
  assert.strictEqual(found.length, 1);
  assert.match(found[0], /OutgoingMessage\.cs/);
  assert.match(found[0], /only 1 of the two/);
});

test('un pairing che non tiene la firma si segnala', () => {
  const found = problems({ pairing: '            // the identity is gone\n' });
  assert.strictEqual(found.length, 1);
  assert.match(found[0], /PairingSession\.cs/);
});

test('un OurIdentity che non legge l\'account si segnala', () => {
  const service = [
    '        private byte[] OurIdentity()',
    '        {',
    '            return null;',
    '        }',
    SERVICE.split('\n').slice(5).join('\n')
  ].join('\n');

  const found = problems({ service: service });
  assert.strictEqual(found.length, 1);
  assert.match(found[0], /OurIdentity does not hand back/);
});
