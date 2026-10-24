'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const fs = require('node:fs');

const guard = require('../check-chat-list-refresh');

const GOOD_DATA = [
  '        public const int ListRefreshSeconds = 30;',
  '',
  '        public void RefreshChatList()',
  '        {',
  '            IList<HistoryConversation> rows = NativeConnection.Instance.ChatRows;',
  '            int folded = FoldDuplicateChats();',
  '        }',
  '',
  '        private void ApplyChat(ChatMessage message, bool remember)',
  '        {',
  '            message.ChatId = ChatIdFor(message.ChatId);',
  '        }',
  '',
  '        private bool RefreshChatRow(WhatsappApp.Protocol.WaProto.HistoryConversation row)',
  '        {',
  '            Contact contact = FindContact(row.Jid);',
  '            if (contact != null && !WouldChangeTheRow(contact, message)) return false;',
  '',
  '            ApplyChat(message, false);',
  '            return true;',
  '        }',
  '',
  '        private bool WouldChangeTheRow(Contact contact, ChatMessage message)',
  '        {',
  '            string name = NameFor(message);',
  '            if (contact.Name != name) return true;',
  '',
  '            return contact.LastMessage != message.Text;',
  '        }',
  '',
  '        public string ChatIdFor(string chatId)',
  '        {',
  '            return chatId;',
  '        }',
  '',
  '        private void AddMessage(string chatId) { chatId = ChatIdFor(chatId); }',
  '        private void GetMessages(string chatId) { chatId = ChatIdFor(chatId); }',
  '        private void FindContact(string chatId) { chatId = ChatIdFor(chatId); }',
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

const GOOD_HOME = [
  '        private void ActivateChats()',
  '        {',
  '            StartListRefresh();',
  '        }',
  '',
  '        private void DeactivateChats()',
  '        {',
  '            StopListRefresh();',
  '        }',
  '',
  '        private void StartListRefresh()',
  '        {',
  '            _listRefresh.Interval = TimeSpan.FromSeconds(ListRefreshSeconds);',
  '        }',
  '',
  '        private void OnListRefreshTick(object sender, object e)',
  '        {',
  '            DataService.Instance.RefreshChatList();',
  '        }',
  ''
].join('\n');

const GOOD_CHAT = [
  '        private void MarkRead()',
  '        {',
  '            DataService.Instance.ActiveChatId = DataService.Instance.ChatIdFor(_contact.Id);',
  '            ChatId = DataService.Instance.ChatIdFor(_contact.Id),',
  '            ChatId = DataService.Instance.ChatIdFor(_contact.Id),',
  '        }',
  ''
].join('\n');

const GOOD_CONNECTION = [
  '        private async Task ConnectAsync()',
  '        {',
  '            NameTheGroups();',
  '        }',
  '',
  '        private void OnSyncFinished()',
  '        {',
  '            NameTheGroups();',
  '        }',
  '',
  '        private void NameTheGroups()',
  '        {',
  '            NativeSession session = _session;',
  '        }',
  '',
  '        private async Task NameGroupsAsync(NativeSession session)',
  '        {',
  '            BinaryNode answer = await RequestAsync(session, GroupDirectory.Query(id), true);',
  '            MarkGroupAsked(wanted[i]);',
  '        }',
  '',
  '        private void MarkGroupAsked(string jid)',
  '        {',
  '            lock (_groupsAsked)',
  '            {',
  '                _groupsAsked.Add(jid);',
  '            }',
  '        }',
  ''
].join('\n');

const GOOD = {
  data: GOOD_DATA,
  merge: GOOD_MERGE,
  home: GOOD_HOME,
  chat: GOOD_CHAT,
  connection: GOOD_CONNECTION
};

const broken = (change) => guard.chatListRefreshProblems(Object.assign({}, GOOD, change)).join('\n');

test('una lista che si rilegge da sola non e un problema', () => {
  assert.deepStrictEqual(guard.chatListRefreshProblems(GOOD), []);
});

test('un intervallo oltre i 30 secondi si segnala', () => {
  const found = broken({ data: GOOD_DATA.replace('ListRefreshSeconds = 30;', 'ListRefreshSeconds = 60;') });
  assert.match(found, /at most 30/);
});

test('un timer che non parte si segnala', () => {
  const found = broken({ home: GOOD_HOME.replace('            StartListRefresh();\n', '') });
  assert.match(found, /does not start the refresh/);
});

test('un timer che non si ferma si segnala', () => {
  const found = broken({ home: GOOD_HOME.replace('            StopListRefresh();\n', '') });
  assert.match(found, /does not stop the refresh/);
});

test('una lista che non viene mai riletta si segnala', () => {
  const found = broken({ home: GOOD_HOME.replace('DataService.Instance.RefreshChatList();', 'Nothing();') });
  assert.match(found, /nothing calls/);
});

test('un refresh che interroga il server si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace('            int folded = FoldDuplicateChats();',
      '            int folded = FoldDuplicateChats();\n            var answer = await RequestAsync(session, query);')
  });
  assert.match(found, /must be local/);
});

test('un refresh che non fonde le righe doppie si segnala', () => {
  const found = broken({ data: GOOD_DATA.replace('FoldDuplicateChats();', 'Nothing();') });
  assert.match(found, /does not fold/);
});

test('un refresh che riapplica ogni riga si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace('            if (contact != null && !WouldChangeTheRow(contact, message)) return false;\n', '')
  });
  assert.match(found, /applies every row/);
});

test('una decisione presa dopo aver applicato la riga si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace(
      '            if (contact != null && !WouldChangeTheRow(contact, message)) return false;\n\n            ApplyChat(message, false);',
      '            ApplyChat(message, false);\n\n            if (contact != null && !WouldChangeTheRow(contact, message)) return false;')
  });
  assert.match(found, /asks the question after it applied/);
});

test('una decisione che non guarda il nome si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace('            if (contact.Name != name) return true;\n', '')
  });
  assert.match(found, /does not compare the name and the preview/);
});

test('un percorso che non canonicalizza l id si segnala', () => {
  const found = broken({
    data: GOOD_DATA.replace('            message.ChatId = ChatIdFor(message.ChatId);\n', '')
  });
  assert.match(found, /ApplyChat does not canonicalise/);
});

test('un invio con l id della riga si segnala', () => {
  const found = broken({ chat: GOOD_CHAT.replace(/ChatId = DataService\.Instance\.ChatIdFor\(_contact\.Id\),\n/g, '') });
  assert.match(found, /ChatIdFor is used/);
});

test('un nome dei gruppi chiesto solo dopo la sync si segnala', () => {
  const found = broken({
    connection: GOOD_CONNECTION.replace('            NameTheGroups();\n        }\n\n        private void OnSyncFinished', '        }\n\n        private void OnSyncFinished')
  });
  assert.match(found, /appears 1 time/);
});

test('una passata ancora legata alla sync si segnala', () => {
  const found = broken({
    connection: GOOD_CONNECTION.replace('NativeSession session = _session;',
      'if (!_chats.IsSyncComplete) return;')
  });
  assert.match(found, /IsSyncComplete/);
});

test('un gruppo segnato come chiesto prima della risposta si segnala', () => {
  const found = broken({
    connection: GOOD_CONNECTION.replace(
      '            BinaryNode answer = await RequestAsync(session, GroupDirectory.Query(id), true);\n            MarkGroupAsked(wanted[i]);',
      '            MarkGroupAsked(wanted[i]);\n            BinaryNode answer = await RequestAsync(session, GroupDirectory.Query(id), true);')
  });
  assert.match(found, /before the server answered/);
});

test('i cinque sorgenti del progetto passano la guardia', () => {
  const read = (file) => fs.readFileSync(path.join(__dirname, '..', '..', file), 'utf8');
  assert.deepStrictEqual(guard.chatListRefreshProblems({
    data: read(guard.DATA),
    merge: read(guard.MERGE),
    home: read(guard.HOME),
    chat: read(guard.CHAT),
    connection: read(guard.CONNECTION)
  }), []);
});
