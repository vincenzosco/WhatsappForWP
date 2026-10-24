'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const fs = require('node:fs');

const guard = require('../check-one-row-per-chat');

const GOOD_DATA = [
  '        private void ApplyChat(ChatMessage message, bool remember)',
  '        {',
  '            message.ChatId = ChatIdFor(message.ChatId);',
  '',
  '            var contact = FindContact(message.ChatId);',
  '            if (contact == null)',
  '            {',
  '                contact = new Contact { Id = message.ChatId };',
  '                _contacts.Add(contact);',
  '                _contactIndex[contact.Id] = contact;',
  '            }',
  '        }',
  '',
  '        public Contact ContactFor(string chatId, string name)',
  '        {',
  '            chatId = ChatIdFor(chatId);',
  '',
  '            Contact contact = FindContact(chatId);',
  '            if (contact != null) return contact;',
  '',
  '            contact = new Contact { Id = chatId };',
  '            _contacts.Insert(0, contact);',
  '            _contactIndex[chatId] = contact;',
  '',
  '            return contact;',
  '        }',
  '',
  '        public Contact AddContact(Contact contact)',
  '        {',
  '            string chatId = ChatIdFor(contact.Id);',
  '            contact.Id = chatId;',
  '',
  '            Contact existing = FindContact(chatId);',
  '            if (existing != null) return existing;',
  '',
  '            _contacts.Insert(0, contact);',
  '            _contactIndex[contact.Id] = contact;',
  '            return contact;',
  '        }',
  '',
  '        public void RefreshChatList()',
  '        {',
  '            int folded = FoldDuplicateChats();',
  '            NameDuplicateRows();',
  '        }',
  '',
  '        private void NameDuplicateRows()',
  '        {',
  '            string canonical = ChatIdFor(row.Id);',
  '            Diag.Ok("chats: " + row.Id + " and " + first + " are the same chat (" + canonical + ")");',
  '        }',
  '',
  '        private void OnNativeStatusPost(object sender, StatusPost post)',
  '        {',
  '            ChatMessage row = FindStatus(post.Id);',
  '            if (row == null)',
  '            {',
  '                InsertStatusInOrder(row);',
  '            }',
  '        }',
  '',
  '        private ChatMessage FindStatus(string id)',
  '        {',
  '            return null;',
  '        }',
  '',
  '        private void InsertStatusInOrder(ChatMessage row)',
  '        {',
  '            _statuses.Insert(at, row);',
  '        }',
  '',
  '        private int FoldDuplicateChats()',
  '        {',
  '            string canonical = ChatIdFor(contact.Id);',
  '            OnChatMerged(this, new ChatMerge(from.Id, to.Id));',
  '            return 0;',
  '        }',
  '',
  '        public string ChatIdFor(string chatId)',
  '        {',
  '            string canonical = ChatListMerge.CanonicalId(chatId, NameCache.Book);',
  '            return canonical == null ? chatId : canonical;',
  '        }',
  ''
].join('\n');

const GOOD_MERGE = [
  '    public static class ChatListMerge',
  '    {',
  '        public static string CanonicalId(string jid, NameBook book)',
  '        {',
  '            return jid;',
  '        }',
  '    }',
  ''
].join('\n');

const GOOD_CHATS = [
  '        private HistoryConversation EnsureRow(string jid)',
  '        {',
  '            HistoryConversation row = Find(jid);',
  '            if (row != null) return row;',
  '',
  '            jid = _names.Canonical(jid);',
  '            if (jid == null) return null;',
  '',
  '            row = new HistoryConversation();',
  '            row.Jid = jid;',
  '            _rows.Add(row);',
  '',
  '            return row;',
  '        }',
  '',
  '        public HistoryConversation Find(string jid)',
  '        {',
  '            jid = _names.Canonical(jid);',
  '            if (jid == null) return null;',
  '',
  '            return null;',
  '        }',
  ''
].join('\n');

const GOOD_STATUS = [
  '        public static async Task<List<ChatMessage>> LoadAsync()',
  '        {',
  '            var rows = new List<ChatMessage>();',
  '            var seen = new HashSet<string>(StringComparer.Ordinal);',
  '',
  '            for (int i = 0; i < cache.Statuses.Count; i++)',
  '            {',
  '                StatusCacheRow entry = cache.Statuses[i];',
  '                if (!seen.Add(entry.Id)) continue;',
  '                rows.Add(ToMessage(entry));',
  '            }',
  '',
  '            return rows;',
  '        }',
  ''
].join('\n');

const GOOD = { data: GOOD_DATA, merge: GOOD_MERGE, chats: GOOD_CHATS, status: GOOD_STATUS };

const broken = (change) => guard.oneRowPerChatProblems(Object.assign({}, GOOD, change)).join('\n');

test('una riga per chat non e un problema', () => {
  assert.deepStrictEqual(guard.oneRowPerChatProblems(GOOD), []);
});

test('una riga creata con l id grezzo si segnala', () => {
  // Il `ContactFor` di prima: la ricerca canonicalizza, la riga no.
  const found = broken({
    data: GOOD_DATA.replace('            chatId = ChatIdFor(chatId);\n', '')
  });
  assert.match(found, /ContactFor\(string chatId, string name\) creates a chat row without canonicalising/);
});

test('una riga creata senza cercare quella che c e gia si segnala', () => {
  // L'`AddContact` di prima: nessuna ricerca, nessuna canonicalizzazione.
  const found = broken({
    data: GOOD_DATA.replace(
      '            string chatId = ChatIdFor(contact.Id);\n' +
      '            contact.Id = chatId;\n' +
      '\n' +
      '            Contact existing = FindContact(chatId);\n' +
      '            if (existing != null) return existing;\n',
      '            string chatId = ChatIdFor(contact.Id);\n' +
      '            contact.Id = chatId;\n')
  });
  assert.match(found, /AddContact\(Contact contact\) creates a chat row without looking for the row/);
});

test('una ricerca fatta prima di canonicalizzare si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace(
      '            chatId = ChatIdFor(chatId);\n' +
      '\n' +
      '            Contact contact = FindContact(chatId);',
      '            Contact contact = FindContact(chatId);\n' +
      '            chatId = ChatIdFor(chatId);')
  });
  assert.match(found, /ContactFor\(string chatId, string name\) creates a chat row without looking for the row/);
});

test('una riga che non entra nell indice si segnala', () => {
  // The four doors are checked one at a time: the site that lost its index line is the one
  // named in the problem.
  const found = broken({
    data: GOOD_DATA.replace(
      '            _contacts.Insert(0, contact);\n' +
      '            _contactIndex[chatId] = contact;',
      '            _contacts.Insert(0, contact);')
  });
  assert.match(found, /ContactFor\(string chatId, string name\) inserts a row and does not file it/);
});

test('una fusione decisa dalla forma della riga si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace('string canonical = ChatIdFor(contact.Id);', 'string canonical = contact.Id;')
  });
  assert.match(found, /does not key its map on ChatIdFor\(contact.Id\)/);
});

test('una fusione che perde la riga invece di unirla si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace('            OnChatMerged(this, new ChatMerge(from.Id, to.Id));\n', '')
  });
  assert.match(found, /never calls OnChatMerged/);
});

test('una regola copiata fuori da ChatListMerge si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace('ChatListMerge.CanonicalId(chatId, NameCache.Book)', 'chatId')
  });
  assert.match(found, /does not ask ChatListMerge.CanonicalId/);
});

test('un file che non crea righe si segnala', () => {
  const found = broken({ data: 'class Nothing { }' });
  assert.match(found, /no chat row is created/);
});

test('una riga della lista del client creata senza canonicalizzare si segnala', () => {
  // La riga di un gruppo o di una persona arriva con la forma che la rete le da'.
  const found = broken({
    chats: GOOD_CHATS.replace('            jid = _names.Canonical(jid);\n            if (jid == null) return null;\n\n            row = new HistoryConversation();',
      '            row = new HistoryConversation();')
  });
  assert.match(found, /creates the row under the address it was handed/);
});

test('una riga del client creata senza cercare quella che c e gia si segnala', () => {
  const found = broken({
    chats: GOOD_CHATS.replace('            HistoryConversation row = Find(jid);\n            if (row != null) return row;\n',
      '            HistoryConversation row = new HistoryConversation();')
  });
  assert.match(found, /EnsureRow creates a row without looking/);
});

test('una ricerca che confronta la forma e non l indirizzo si segnala', () => {
  const found = broken({
    chats: GOOD_CHATS.replace('            jid = _names.Canonical(jid);\n            if (jid == null) return null;\n\n            return null;',
      '            return null;')
  });
  assert.match(found, /Find compares the jid it was handed/);
});

test('un feed che rilegge un file con lo stesso id due volte si segnala', () => {
  const found = broken({
    status: GOOD_STATUS.replace('                if (!seen.Add(entry.Id)) continue;\n', '')
  });
  assert.match(found, /restores every entry of the file/);
});

test('uno stato inserito senza cercarne l id si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace(
      '            ChatMessage row = FindStatus(post.Id);\n            if (row == null)',
      '            ChatMessage row = null;\n            if (row == null)')
  });
  assert.match(found, /inserts a status without looking for the row/);
});

test('un doppione che nessuno nomina si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace('            NameDuplicateRows();\n', '')
  });
  assert.match(found, /does not call NameDuplicateRows/);
});

test('un doppione trovato e non scritto si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace('Diag.Ok("chats:', 'Nothing("chats:')
  });
  assert.match(found, /names the pair to nobody/);
});

test('una ricerca dei doppioni fatta sulla forma si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace('string canonical = ChatIdFor(row.Id);', 'string canonical = row.Id;')
  });
  assert.match(found, /compares the rows by the spelling they carry/);
});

test('i quattro sorgenti del progetto passano la guardia', () => {
  const read = (file) => fs.readFileSync(path.join(__dirname, '..', '..', file), 'utf8');
  assert.deepStrictEqual(guard.oneRowPerChatProblems({
    data: read(guard.DATA),
    merge: read(guard.MERGE),
    chats: read(guard.CHATS),
    status: read(guard.STATUS)
  }), []);
});
