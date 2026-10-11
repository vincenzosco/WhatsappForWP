'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const os = require('os');
const path = require('path');

const guard = require('../check-no-server');

/** Un albero di prova: mappa percorso relativo -> contenuto. */
function tree(files) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'no-server-'));
  for (const rel of Object.keys(files)) {
    const file = path.join(root, rel);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, files[rel]);
  }
  return root;
}

test("l'app con il protocollo e nient'altro non si segnala", () => {
  const root = tree({
    'WhatsappApp/Protocol/Client/WhatsAppSocket.cs': 'namespace WhatsappApp.Protocol.Client { }',
    'WhatsappApp/Services/Diag.cs': 'namespace WhatsappApp.Services { }',
    'WhatsappApp/Strings/en-US/Resources.resw': '<root><data name="Chats_Title"><value>Chats</value></data></root>',
    'README.md': '# WhatsApp for Windows Phone 8.1\n\nIt speaks the protocol from the phone.\n',
    'README.it.md': '# WhatsApp per Windows Phone 8.1\n\nParla il protocollo dal telefono.\n',
    '.agents/skills/maintain-the-app/SKILL.md': '---\nname: maintain-the-app\n---\n\n# Maintaining the app\n',
  });
  assert.deepStrictEqual(guard.findProblems(root), []);
});

test('un WhatsappBridge rimasto indietro si segnala', () => {
  const root = tree({ 'WhatsappBridge/server.js': "'use strict';\n" });
  const problems = guard.findProblems(root);
  assert.strictEqual(problems.some((p) => p.includes('WhatsappBridge')), true);
});

test('un WhatsappServer e una cartella registry si segnalano', () => {
  const root = tree({
    'WhatsappServer/Program.cs': 'class Program { }',
    'registry/index.json': '{}',
  });
  const problems = guard.findProblems(root);
  assert.strictEqual(problems.some((p) => p.includes('WhatsappServer')), true);
  assert.strictEqual(problems.some((p) => p.includes('registry')), true);
});

test('un file del servizio fra i dieci si segnala, con il suo percorso', () => {
  const root = tree({
    'WhatsappApp/Services/CommunicationService.cs': 'namespace WhatsappApp.Services { }',
    'WhatsappApp/Services/FrameCodec.cs': 'namespace WhatsappApp.Services { }',
  });
  const problems = guard.findProblems(root);
  assert.strictEqual(problems.some((p) => p.includes('CommunicationService.cs')), true);
  assert.strictEqual(problems.some((p) => p.includes('FrameCodec.cs')), true);
});

test('una chiave del server rimasta nelle impostazioni si segnala', () => {
  const root = tree({
    'WhatsappApp/Services/SettingsService.cs':
      'private const string KeyServerAddress = "ServerAddress";\n',
  });
  const problems = guard.findProblems(root);
  assert.strictEqual(problems.some((p) => p.includes('ServerAddress')), true);
});

test("una stringa dell'interfaccia che nomina il server si segnala", () => {
  const root = tree({
    'WhatsappApp/Strings/en-US/Resources.resw':
      '<root><data name="Connection_Address"><value>Server address</value></data></root>',
  });
  // Il valore e' testo d'interfaccia: e' il nome della chiave a contare, quindi
  // questo albero non ha niente da segnalare.
  assert.deepStrictEqual(guard.findProblems(root), []);
});

test("l'adapter nominato in una skill si segnala", () => {
  const root = tree({
    '.agents/skills/test-the-app/SKILL.md':
      'Expected counts. Then:\n\ncd WhatsappBridge && npm test\n',
  });
  const problems = guard.findProblems(root);
  assert.strictEqual(problems.some((p) => p.includes('test-the-app')), true);
});

test("l'adattatore nominato in README.it.md si segnala", () => {
  const root = tree({ 'README.it.md': "L'app parla con un adattatore Node.js.\n" });
  const problems = guard.findProblems(root);
  assert.strictEqual(problems.some((p) => p.includes('README.it.md')), true);
});

test('la skill del server di login, se resta, si segnala', () => {
  const root = tree({ '.agents/skills/run-the-login-server/SKILL.md': '# Login\n' });
  const problems = guard.findProblems(root);
  assert.strictEqual(problems.some((p) => p.includes('run-the-login-server')), true);
});

test('ogni problema dice dove, e un albero pulito non ne ha', () => {
  const root = tree({ 'WhatsappBridge/server.js': "'use strict';\n" });
  const problems = guard.findProblems(root);
  assert.strictEqual(problems.length > 0, true);
  for (const p of problems) assert.strictEqual(typeof p, 'string');
});
