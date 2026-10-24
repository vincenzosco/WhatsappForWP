#!/usr/bin/env node
/**
 * tools/emit-appstate-fixture.js
 *
 * Writes `ProtocolTests/Vectors/AppStateFixture.cs`: one app-state snapshot and one patch,
 * encrypted and signed exactly as the reference does, so the C# reader can be checked
 * against bytes it did not produce.
 *
 * Why the vectors are generated here and not on the phone: the app-state crypto is a chain
 * of four things - HKDF to five keys, AES-256-CBC over the action, an HMAC index and a MAC
 * over an LT-Hash - and every one of them has to agree with WhatsApp for a saved contact
 * name to appear. A fixture built by Node's own `crypto` (a different implementation of the
 * same standards) is what makes the C# side prove the layout rather than agree with itself.
 *
 * The rules are transcribed from `@whiskeysockets/baileys` 6.7.18:
 *   - `Utils/chat-utils.js`: `mutationKeys` (HKDF `WhatsApp Mutation Keys`, 160 bytes),
 *     `generateMac` (operation byte, key id, data, length of the key id; HMAC-SHA512 cut to
 *     32), `generateSnapshotMac` (sum, version as eight big-endian bytes, name; HMAC-SHA256),
 *     `generatePatchMac` (snapshot MAC, value MACs, version, name; HMAC-SHA256);
 *   - `Utils/lt-hash.js`: `WhatsApp Patch Integrity`, HKDF to 128 bytes, 16-bit little-endian
 *     words added or subtracted with wrap;
 *   - `WAProto`: the field numbers used below.
 *
 * Usage:  node tools/emit-appstate-fixture.js
 */
'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const TARGET = path.join(ROOT, 'ProtocolTests', 'Vectors', 'AppStateFixture.cs');

// ---------------------------------------------------------------- protobuf writing

function varint(value) {
  const bytes = [];
  let rest = value;
  while (rest >= 0x80) {
    bytes.push((rest & 0x7f) | 0x80);
    rest = Math.floor(rest / 128);
  }
  bytes.push(rest);
  return Buffer.from(bytes);
}

function tag(field, wireType) {
  return varint((field << 3) | wireType);
}

function bytesField(field, value) {
  if (value == null) return Buffer.alloc(0);
  return Buffer.concat([tag(field, 2), varint(value.length), value]);
}

function varintField(field, value) {
  return Buffer.concat([tag(field, 0), varint(value)]);
}

function textField(field, value) {
  return bytesField(field, Buffer.from(value, 'utf8'));
}

function concat(parts) {
  return Buffer.concat(parts.filter((part) => part && part.length));
}

// ---------------------------------------------------------------- the crypto

const hmac = (key, data, hash) => crypto.createHmac(hash, key).update(data).digest();

function mutationKeys(keyData) {
  const expanded = Buffer.from(crypto.hkdfSync('sha256', keyData, Buffer.alloc(0),
    Buffer.from('WhatsApp Mutation Keys', 'utf8'), 160));

  return {
    indexKey: expanded.subarray(0, 32),
    valueEncryptionKey: expanded.subarray(32, 64),
    valueMacKey: expanded.subarray(64, 96),
    snapshotMacKey: expanded.subarray(96, 128),
    patchMacKey: expanded.subarray(128, 160)
  };
}

function generateMac(operationByte, data, keyId, key) {
  const head = Buffer.concat([Buffer.from([operationByte]), keyId]);
  const length = Buffer.alloc(8);
  length.writeUInt32BE(head.length, 4);
  return hmac(key, Buffer.concat([head, data, length]), 'sha512').subarray(0, 32);
}

const bigEndian64 = (value) => {
  const bytes = Buffer.alloc(8);
  bytes.writeUInt32BE(value, 4);
  return bytes;
};

function generateSnapshotMac(sum, version, name, key) {
  return hmac(key, Buffer.concat([sum, bigEndian64(version), Buffer.from(name, 'utf8')]), 'sha256');
}

function generatePatchMac(snapshotMac, valueMacs, version, name, key) {
  return hmac(key, Buffer.concat([snapshotMac, ...valueMacs, bigEndian64(version), Buffer.from(name, 'utf8')]),
    'sha256');
}

function ltAdd(sum, valueMac) {
  const expanded = Buffer.from(crypto.hkdfSync('sha256', valueMac, Buffer.alloc(0),
    Buffer.from('WhatsApp Patch Integrity', 'utf8'), 128));

  const out = Buffer.alloc(128);
  for (let i = 0; i < 128; i += 2) {
    const word = sum.readUInt16LE(i);
    const add = expanded.readUInt16LE(i);
    out.writeUInt16LE((word + add) & 0xffff, i);
  }
  return out;
}

// ---------------------------------------------------------------- the records

const KEY_ID = Buffer.from('0a1b2c3d4e5f6071', 'hex');
const KEY_DATA = Buffer.from('00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff', 'hex');
const keys = mutationKeys(KEY_DATA);

/** One SET mutation with its encrypted value, its MACs and the action inside it. */
function setRecord(index, contactAction, iv) {
  const indexBuffer = Buffer.from(JSON.stringify(index), 'utf8');

  const action = concat([
    textField(1, contactAction.fullName),
    contactAction.firstName ? textField(2, contactAction.firstName) : Buffer.alloc(0),
    contactAction.lidJid ? textField(3, contactAction.lidJid) : Buffer.alloc(0),
    varintField(4, 1), // saveOnPrimaryAddressbook: a varint, which the reader has to skip
    contactAction.pnJid ? textField(5, contactAction.pnJid) : Buffer.alloc(0)
  ]);

  const syncActionValue = bytesField(3, action);
  const syncActionData = concat([bytesField(1, indexBuffer), bytesField(2, syncActionValue)]);

  const cipher = crypto.createCipheriv('aes-256-cbc', keys.valueEncryptionKey, iv);
  const encrypted = Buffer.concat([iv, cipher.update(syncActionData), cipher.final()]);
  const valueMac = generateMac(1, encrypted, KEY_ID, keys.valueMacKey);
  const indexMac = hmac(keys.indexKey, indexBuffer, 'sha256');

  return {
    indexBuffer,
    indexMac,
    valueMac,
    // SyncdRecord: index 1, value 2, keyId 3 - the server names the key on every record,
    // and a reader that expects the key's id to arrive only at the snapshot's top level
    // ends up with no key to read the record with.
    record: concat([
      bytesField(1, bytesField(1, indexMac)),
      bytesField(2, bytesField(1, Buffer.concat([encrypted, valueMac]))),
      bytesField(3, bytesField(1, KEY_ID))
    ]),
    mutation: concat([varintField(1, 0), bytesField(2, concat([
      bytesField(1, bytesField(1, indexMac)),
      bytesField(2, bytesField(1, Buffer.concat([encrypted, valueMac]))),
      bytesField(3, bytesField(1, KEY_ID))
    ]))])
  };
}

const MAMMA = {
  index: ['contact', '393331234567@s.whatsapp.net'],
  contact: { fullName: 'Mamma', pnJid: '393331234567@s.whatsapp.net' }
};

const PAPA = {
  index: ['contact', '150375613116620@lid'],
  contact: { fullName: 'Papà', lidJid: '150375613116620@lid', pnJid: '393892672185@s.whatsapp.net' }
};

const NONNA = {
  index: ['contact', '393401112223@s.whatsapp.net'],
  contact: { fullName: 'Nonna', firstName: 'Nonna', pnJid: '393401112223@s.whatsapp.net' }
};

const first = setRecord(MAMMA.index, MAMMA.contact, Buffer.from('000102030405060708090a0b0c0d0e0f', 'hex'));
const second = setRecord(PAPA.index, PAPA.contact, Buffer.from('101112131415161718191a1b1c1d1e1f', 'hex'));
const third = setRecord(NONNA.index, NONNA.contact, Buffer.from('202122232425262728292a2b2c2d2e2f', 'hex'));

let sum = Buffer.alloc(128);
sum = ltAdd(sum, first.valueMac);
sum = ltAdd(sum, second.valueMac);

const SNAPSHOT_VERSION = 7;
const snapshotMac = generateSnapshotMac(sum, SNAPSHOT_VERSION, 'regular', keys.snapshotMacKey);

const snapshot = concat([
  bytesField(1, varintField(1, SNAPSHOT_VERSION)),
  bytesField(2, first.record),
  bytesField(2, second.record),
  bytesField(3, snapshotMac),
  bytesField(4, bytesField(1, KEY_ID))
]);

// A patch that saves one more name, on top of the snapshot.
let patchSum = ltAdd(sum, third.valueMac);

const PATCH_VERSION = 8;
const patchSnapshotMac = generateSnapshotMac(patchSum, PATCH_VERSION, 'regular', keys.snapshotMacKey);
const patchMac = generatePatchMac(patchSnapshotMac, [third.valueMac], PATCH_VERSION, 'regular', keys.patchMacKey);

const patch = concat([
  bytesField(1, varintField(1, PATCH_VERSION)),
  bytesField(2, third.mutation),
  bytesField(4, patchSnapshotMac),
  bytesField(5, patchMac),
  bytesField(6, bytesField(1, KEY_ID))
]);

// ---------------------------------------------------------------- the C# file

const base64 = (buffer) => buffer.toString('base64');

const file = `namespace ProtocolTests.Vectors
{
    /// <summary>
    /// One app-state snapshot and one patch, built by tools/emit-appstate-fixture.js with
    /// Node's own crypto from the reference's rules - not by the code under test.
    ///
    /// What it holds: a \`regular\` collection at version ${SNAPSHOT_VERSION} with two saved
    /// contacts (Mamma, and Papà who is known by a LID and a phone number), and the patch to
    /// version ${PATCH_VERSION} that saves a third (Nonna). The MACs are real: a reader that
    /// gets the LT-Hash, the key derivation or the field numbers wrong fails on them.
    /// </summary>
    internal static class AppStateFixture
    {
        /// <summary>The key id the records name, base64 as the phone shares it.</summary>
        public const string KeyId = "${base64(KEY_ID)}";

        /// <summary>The 32 bytes every mutation key is derived from, base64.</summary>
        public const string KeyData = "${base64(KEY_DATA)}";

        /// <summary>The encoded \`SyncdSnapshot\`, base64.</summary>
        public const string Snapshot = "${base64(snapshot)}";

        /// <summary>The encoded \`SyncdPatch\`, base64.</summary>
        public const string Patch = "${base64(patch)}";

        public const int SnapshotVersion = ${SNAPSHOT_VERSION};
        public const int PatchVersion = ${PATCH_VERSION};
    }
}
`;

fs.writeFileSync(TARGET, file);
console.log('wrote ' + TARGET);
console.log('  snapshot ' + snapshot.length + ' bytes, patch ' + patch.length + ' bytes');
