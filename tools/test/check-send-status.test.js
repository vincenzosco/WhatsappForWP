'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { sendStatusProblems } = require('../check-send-status');

const ROOT = path.join(__dirname, '..', '..');
const PAGE = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const CONNECTION = 'WhatsappApp/Services/NativeConnection.cs';
const DATA = 'WhatsappApp/Services/DataService.cs';
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
const files = () => ({
  page: read(PAGE),
  connection: read(CONNECTION),
  data: read(DATA)
});

test('la spunta viene dall ack del server, in tutti e tre i file', () => {
  assert.deepStrictEqual(sendStatusProblems(files()), []);
});

test('una pagina che decide da sola di aver inviato e un problema', () => {
  const broken = files();
  broken.page = broken.page.replace(
    'WaitForAck(message);',
    'message.Status = MessageStatus.Sent;'
  );
  assert.match(sendStatusProblems(broken).join('\n'), /MessageStatus\.Sent/);
});

test('un invio senza attesa dell ack non diventa mai un fallimento', () => {
  const broken = files();
  broken.page = broken.page.replace(/WaitForAck\(message\);/g, '');
  assert.match(sendStatusProblems(broken).join('\n'), /never waits for the ack/);
});

test('un aiuto che invia senza attendere l ack non conta come attesa', () => {
  const broken = files();
  // The upload lives in UploadAndSendAsync so that the retry of a failed bubble can
  // send the same file again: with the wait taken out of the helper, the attachment
  // path has none, and the helper being in the page does not excuse it.
  broken.page = broken.page.replace(
    /private async System\.Threading\.Tasks\.Task UploadAndSendAsync[\s\S]*?\n        \}/,
    (body) => body.replace('WaitForAck(message);', '')
  );
  assert.match(sendStatusProblems(broken).join('\n'),
    /SendAttachmentAsync sends a file and never waits for the ack/);
});

test('un connessione che non riconosce l ack lo lascia cadere', () => {
  const broken = files();
  broken.connection = broken.connection.replace(/AnnounceAck\(/g, 'AnnounceSomethingElse(');
  assert.match(sendStatusProblems(broken).join('\n'), /AnnounceAck/);
});

test('un servizio che ignora il flag dell ack non puo dire inviato', () => {
  const broken = files();
  broken.data = broken.data.replace('if (update.IsAck)', 'if (false)');
  assert.match(sendStatusProblems(broken).join('\n'), /IsAck/);
});
