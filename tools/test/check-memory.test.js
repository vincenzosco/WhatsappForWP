'use strict';
const test = require('node:test');
const assert = require('node:assert');

const memory = require('../check-memory');

const FILE = 'WhatsappApp/Models/Contact.cs';

test('una chiamata senza misura di decodifica si segnala', () => {
  const problems = memory.decodeProblems(
    'Avatar = await ImageHelper.FromBase64Async(_avatarData);', FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /two arguments/);
});

test('una chiamata con la sua misura passa', () => {
  const problems = memory.decodeProblems(
    'Avatar = await ImageHelper.FromBase64Async(_avatarData, AvatarDecodePixels);', FILE);
  assert.deepStrictEqual(problems, []);
});

test('una misura oltre lo schermo si segnala', () => {
  const problems = memory.decodeProblems(
    'var b = await ImageHelper.FromBase64Async(x, 4096);', FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /wider than 720/);
});

const INBOX = 'WhatsappApp/Services/AttachmentInbox.cs';

test('un allegato letto tutto in memoria si segnala', () => {
  const source = 'await file.CopyAsync(folder, name, NameCollisionOption.ReplaceExisting);\n' +
    'byte[] b = new byte[(uint)stream.Size];';
  const problems = memory.attachmentProblems(source, INBOX);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /byte array/);
});

test('un allegato mai copiato si segnala', () => {
  const source = 'byte[] b = await ReadAllAsync(file);';
  const problems = memory.attachmentProblems(source, INBOX);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /CopyAsync/);
});

test('un allegato copiato passa', () => {
  const source = 'await file.CopyAsync(ApplicationData.Current.LocalFolder, name, ' +
    'NameCollisionOption.ReplaceExisting);';
  assert.deepStrictEqual(memory.attachmentProblems(source, INBOX), []);
});

test('ImageHelper.FromFileAsync chiede anche lui la misura', () => {
  const problems = memory.decodeProblems(
    'MediaImage = await ImageHelper.FromFileAsync(MediaFilePath);', FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /two arguments/);
});

test('ImageHelper.FromStreamAsync chiede anche lui la misura', () => {
  const problems = memory.decodeProblems(
    'Cover = await ImageHelper.FromStreamAsync(stream);', FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /two arguments/);
});

test('FromStreamAsync con la sua misura passa', () => {
  const problems = memory.decodeProblems(
    'Cover = await ImageHelper.FromStreamAsync(thumb, 480);', FILE);
  assert.deepStrictEqual(problems, []);
});

const CHAT_CACHE = 'WhatsappApp/Services/ChatCache.cs';
const AVATAR_CACHE = 'WhatsappApp/Services/AvatarCache.cs';
const DATA_SERVICE = 'WhatsappApp/Services/DataService.cs';

test('la copia dell elenco chat non si porta dietro i byte dell immagine', () => {
  const bad = 'return new ChatMessage {\n  ChatId = row.ChatId,\n  AvatarData = row.AvatarData\n};';
  const problems = memory.avatarCacheProblems(bad, CHAT_CACHE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /chats\.json/);
});

test('la copia dell elenco chat senza l immagine passa', () => {
  const good = 'return new ChatMessage {\n  ChatId = row.ChatId,\n  SenderName = row.SenderName\n};';
  assert.deepStrictEqual(memory.avatarCacheProblems(good, CHAT_CACHE), []);
});

test('un altra cache di immagini non entra in questa regola', () => {
  const source = 'return new ChatMessage { AvatarData = row.AvatarData };';
  assert.deepStrictEqual(memory.avatarCacheProblems(source, 'WhatsappApp/Services/MessageCache.cs'), []);
});

test('la cache degli avatar senza tetti si segnala', () => {
  const source = 'private static readonly Dictionary<string, string> Known =\n' +
    '    new Dictionary<string, string>();\n' +
    'private static void Save() {\n' +
    '  Writes.RunAsync(delegate { return WriteIfChangedAsync(); });\n' +
    '}';
  const problems = memory.avatarCacheProblems(source, AVATAR_CACHE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /MaxChats/);
});

test('la cache degli avatar che scrive senza la coda si segnala', () => {
  const source = 'private const int MaxChats = 40;\n' +
    'private const int MaxTotalChars = 1500000;\n' +
    'StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(\n' +
    '  FileName, CreationCollisionOption.ReplaceExisting);';
  const problems = memory.avatarCacheProblems(source, AVATAR_CACHE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /serial queue/);
});

test('una cache degli avatar con i tetti e la coda passa', () => {
  const source = 'private const int MaxChats = 40;\n' +
    'private const int MaxEntryChars = 150000;\n' +
    'private const int MaxTotalChars = 1500000;\n' +
    'Writes.RunAsync(delegate { return WriteIfChangedAsync(); });';
  assert.deepStrictEqual(memory.avatarCacheProblems(source, AVATAR_CACHE), []);
});

test('la coda non basta se la coda si e chiamata diversamente', () => {
  const source = 'private const int MaxChats = 40;\n' +
    'private const int MaxTotalChars = 1500000;\n' +
    'private static void Save() {\n' +
    '  FireAndForget(WriteFileAsync(Serialize()));\n' +
    '}';
  const problems = memory.avatarCacheProblems(source, AVATAR_CACHE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /serial queue/);
});

test('una cache locale letta dopo le righe si segnala', () => {
  const bad = 'private async Task LoadCachedChatsAsync()\n{\n' +
    '    await ChatPreferences.LoadAsync();\n' +
    '    var cached = await ChatCache.LoadAsync();\n' +
    '    for (int i = 0; i < cached.Count; i++) ApplyChat(cached[i]);\n' +
    '    await AvatarCache.LoadAsync();\n}';
  const problems = memory.cachedRowsProblems(bad, DATA_SERVICE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /after the first ApplyChat/);
});

test('una cache locale mai letta si segnala', () => {
  const bad = 'private async Task LoadCachedChatsAsync()\n{\n' +
    '    var cached = await ChatCache.LoadAsync();\n' +
    '    for (int i = 0; i < cached.Count; i++) ApplyChat(cached[i]);\n}';
  const problems = memory.cachedRowsProblems(bad, DATA_SERVICE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /does not await AvatarCache\.LoadAsync/);
});

test('una cache locale letta prima delle righe passa', () => {
  const good = 'private async Task LoadCachedChatsAsync()\n{\n' +
    '    await ChatPreferences.LoadAsync();\n' +
    '    await AvatarCache.LoadAsync();\n' +
    '    var cached = await ChatCache.LoadAsync();\n' +
    '    for (int i = 0; i < cached.Count; i++) ApplyChat(cached[i]);\n}';
  assert.deepStrictEqual(memory.cachedRowsProblems(good, DATA_SERVICE), []);
});

test('le regole delle cache non guardano altri file', () => {
  assert.deepStrictEqual(memory.cachedRowsProblems('anything at all', 'WhatsappApp/Services/ChatPage.xaml.cs'), []);
});

test('una chiamata su piu righe si legge lo stesso', () => {
  const source = 'var b = await ImageHelper.FromBytesAsync(\n    bytes,\n    320);';
  assert.deepStrictEqual(memory.decodeProblems(source, FILE), []);
});

test('un commento che cita SetSourceAsync non e una chiamata', () => {
  const source = '// Va impostato PRIMA di SetSourceAsync: dopo non ha effetto.\n' +
    'var bitmap = new BitmapImage();\n' +
    'if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;\n' +
    'await bitmap.SetSourceAsync(stream);';
  assert.deepStrictEqual(memory.sourceShapeProblems(source, 'WhatsappApp/Services/ImageHelper.cs'), []);
});

test('una chiamata commentata non conta', () => {
  const source = '// MediaImage = await ImageHelper.FromBase64Async(MediaData);\n' +
    'MediaImage = await ImageHelper.FromBase64Async(MediaData, 320);';
  assert.deepStrictEqual(memory.decodeProblems(source, FILE), []);
});

test('DecodePixelWidth viene assegnato prima del SetSource', () => {
  const good = 'var bitmap = new BitmapImage();\n' +
    'if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;\n' +
    'await bitmap.SetSourceAsync(stream);';
  assert.deepStrictEqual(memory.sourceShapeProblems(good, 'WhatsappApp/Services/ImageHelper.cs'), []);

  const bad = 'var bitmap = new BitmapImage();\nawait bitmap.SetSourceAsync(stream);';
  const problems = memory.sourceShapeProblems(bad, 'WhatsappApp/Services/ImageHelper.cs');
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /DecodePixelWidth/);
});

test('una chat senza tetto in memoria si segnala', () => {
  const found = memory.historyCeilingProblems('public void AddMessage() {}', DATA_SERVICE).join('\n');
  assert.match(found, /MaxMessagesPerChat/);
});

test('un tetto applicato con while passa', () => {
  const source = 'private const int MaxMessagesPerChat = 200;\n' +
    'while (list.Count > MaxMessagesPerChat) list.RemoveAt(0);';
  assert.deepStrictEqual(memory.historyCeilingProblems(source, DATA_SERVICE), []);
});

test('un tetto a zero si segnala', () => {
  const source = 'private const int MaxMessagesPerChat = 0;\n' +
    'while (list.Count > MaxMessagesPerChat) list.RemoveAt(0);';
  const found = memory.historyCeilingProblems(source, DATA_SERVICE).join('\n');
  assert.match(found, /MaxMessagesPerChat/);
});

test('un tetto applicato con un if si segnala', () => {
  const source = 'private const int MaxMessagesPerChat = 200;\n' +
    'if (list.Count > MaxMessagesPerChat) list.RemoveAt(0);';
  const found = memory.historyCeilingProblems(source, DATA_SERVICE).join('\n');
  assert.match(found, /while/);
});
