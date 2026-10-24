'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const fs = require('node:fs');

const guard = require('../check-app-state');

const GOOD_CONNECTION = [
  '        private async Task ReadMessageAsync(NativeSession session, Credentials account, BinaryNode node)',
  '        {',
  '            ' + guard.LEARN_CALL,
  '            ' + guard.ECHO_RETURN,
  '        }',
  '',
  '        private async Task ConnectAsync(NativeSession session, Credentials account)',
  '        {',
  '            ' + guard.STORED_CALL,
  '            ' + guard.SYNC_CALL,
  '            ' + guard.SYNC_CALL,
  '        }',
  '',
  '        private bool LearnAppStateKeys(NativeSession session, IncomingMessage incoming, Credentials account)',
  '        {',
  '            _store.Save(account);',
  '            return true;',
  '        }',
  '',
  '        private async Task SyncAppStateAsync(NativeSession session)',
  '        {',
  '            BinaryNode answer = await RequestAsync(session, AppStateSync.Query(OutgoingMessage.Id(_random)), true);',
  '            try',
  '            {',
  '                applied = AppStateSync.Apply(collection, _appStateKeys);',
  '            }',
  '            catch (ProtocolException error)',
  '            {',
  '                continue;',
  '            }',
  '            if (RememberSavedContact(applied.Contacts[c])) names++;',
  '            AnnounceRows();',
  '        }',
  '',
  '        private static bool RememberSavedContact(SavedContact contact)',
  '        {',
  '            NameCache.RememberMapping(contact.LidJid, contact.PnJid);',
  '            NameCache.RememberSavedName(contact.Address, contact.Name);',
  '            NameCache.RememberSavedName(contact.PnJid, contact.Name);',
  '            NameCache.RememberSavedName(contact.LidJid, contact.Name);',
  '            return true;',
  '        }',
  ''
].join('\n');

const GOOD_APP_STATE = [
  '        private static byte[] ReadRecord(SyncdRecord record, int operation, AppStateKeyset keys)',
  '        {',
  '            ' + guard.SNAPSHOT_MAC,
  '            ' + guard.PATCH_MAC,
  '            ' + guard.CONTACT_ONLY,
  '        }',
  ''
].join('\n');

const GOOD = { connection: GOOD_CONNECTION, appState: GOOD_APP_STATE };

const broken = (change) => guard.appStateProblems(Object.assign({}, GOOD, change)).join('\n');

test('the share of the keys, the sync and the names are not a problem', () => {
  assert.deepStrictEqual(guard.appStateProblems(GOOD), []);
});

test('a key share nobody reads is reported', () => {
  const found = broken({ connection: GOOD_CONNECTION.replace(guard.LEARN_CALL, '') });
  assert.match(found, /nothing calls/);
});

test('a share read after the echo rule is reported', () => {
  const found = broken({
    connection: GOOD_CONNECTION
      .replace('            ' + guard.LEARN_CALL + '\n            ' + guard.ECHO_RETURN, '')
      .replace('        private bool LearnAppStateKeys', '            ' + guard.ECHO_RETURN + '\n' +
        '            ' + guard.LEARN_CALL + '\n        }\n\n' +
        '        private bool LearnAppStateKeys')
  });
  assert.match(found, /read after/);
});

test('a learned key that is not written with the account is reported', () => {
  const found = broken({ connection: GOOD_CONNECTION.replace('_store.Save(account);', '') });
  assert.match(found, /_store\.Save/);
});

test('a login that never reads the keys back is reported', () => {
  const found = broken({ connection: GOOD_CONNECTION.replace(guard.STORED_CALL, '') });
  assert.match(found, /RememberStoredAppStateKeys/);
});

test('a sync asked for only once is reported', () => {
  const found = broken({
    connection: GOOD_CONNECTION.replace('            ' + guard.SYNC_CALL + '\n            ' + guard.SYNC_CALL,
      '            ' + guard.SYNC_CALL)
  });
  assert.match(found, /must appear twice/);
});

test('a sync that asks for nothing is reported', () => {
  const found = broken({ connection: GOOD_CONNECTION.replace('AppStateSync.Query(', 'Nothing.Query(') });
  assert.match(found, /asks for nothing/);
});

test('a collection that does not verify and takes the others with it is reported', () => {
  const found = broken({ connection: GOOD_CONNECTION.replace('                continue;', '                restart();') });
  assert.match(found, /not skipped/);
});

test('a sync whose names go nowhere is reported', () => {
  const found = broken({ connection: GOOD_CONNECTION.replace('RememberSavedContact(applied.Contacts[c])', 'count(applied)') });
  assert.match(found, /go nowhere/);
});

test('a name written under one address is reported', () => {
  const found = broken({
    connection: GOOD_CONNECTION.replace('            NameCache.RememberSavedName(contact.LidJid, contact.Name);\n', '')
      .replace('            NameCache.RememberSavedName(contact.PnJid, contact.Name);\n', '')
  });
  assert.match(found, /1 address/);
});

test('a saved contact whose LID pair is forgotten is reported', () => {
  const found = broken({ connection: GOOD_CONNECTION.replace(/\s*NameCache\.RememberMapping\([^\n]*\n/, '\n') });
  assert.match(found, /LID\/phone pair/);
});

test('a name that never reaches the rows is reported', () => {
  const found = broken({ connection: GOOD_CONNECTION.replace('            AnnounceRows();\n', '') });
  assert.match(found, /rows are not told/);
});

test('a snapshot whose LT-Hash is not checked is reported', () => {
  const found = broken({ appState: GOOD_APP_STATE.replace(guard.SNAPSHOT_MAC, '') });
  assert.match(found, /LT-Hash is not checked/);
});

test('a patch whose MAC is not verified is reported', () => {
  const found = broken({ appState: GOOD_APP_STATE.replace(guard.PATCH_MAC, '') });
  assert.match(found, /patch's MAC is not verified/);
});

test('a reader that applies more than the saved names is reported', () => {
  const found = broken({ appState: GOOD_APP_STATE.replace(guard.CONTACT_ONLY, 'ReadAction(action, result);') });
  assert.match(found, /more than the saved names/);
});

test('the two sources of the project pass the guard', () => {
  const read = (file) => fs.readFileSync(path.join(__dirname, '..', '..', file), 'utf8');
  assert.deepStrictEqual(guard.appStateProblems({
    connection: read(guard.CONNECTION),
    appState: read(guard.APP_STATE)
  }), []);
});
