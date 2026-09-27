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
