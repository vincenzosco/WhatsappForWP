'use strict';
const test = require('node:test');
const assert = require('node:assert');

const ff = require('../check-fire-and-forget');

const FILE = 'WhatsappApp/Pages/ChatPage.xaml.cs';

/** Una regione come la guardia la legge, con il corpo del metodo chiamato. */
const region = (text) => ({ line: 1, text });
const withMethods = (source) => ff.methods(source);

test('una chiamata a un metodo che cattura passa', () => {
  const known = withMethods('private async Task SaveAsync()\n{\n  try { await WriteAsync(); }\n' +
    '  catch (Exception ex) { Diag.Failed("x", ex); }\n}');
  assert.deepStrictEqual(ff.regionProblems(region(' SaveAsync(); '), FILE, known), []);
});

test('una chiamata a un metodo che non cattura si segnala', () => {
  const known = withMethods('private async Task SaveAsync()\n{\n  await WriteAsync();\n}');
  const problems = ff.regionProblems(region(' SaveAsync(); '), FILE, known);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /catches nothing/);
});

test('e il caso che ha aperto questa guardia', () => {
  const known = withMethods('private async Task StartRecordingAsync()\n{\n' +
    '  bool started = await AudioRecorder.StartAsync();\n}');
  const problems = ff.regionProblems(region(' StartRecordingAsync(); '), FILE, known);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /StartRecordingAsync\(\)/);
});

test('RunGuardedAsync osserva la chiamata', () => {
  const known = withMethods('private async Task SaveAsync()\n{\n  await WriteAsync();\n}');
  const source = ' Guarded.RunGuardedAsync("where", SaveAsync()); ';
  assert.deepStrictEqual(ff.regionProblems(region(source), FILE, known), []);
});

test('un metodo che l app non definisce si segnala', () => {
  const problems = ff.regionProblems(region(' Framework.MagicAsync(); '), FILE, new Map());
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /does not define it/);
});

test('Task.Run risolve la chiamata dentro il delegato', () => {
  const known = withMethods('private async Task ListenAsync()\n{\n' +
    '  try { await ReadAsync(); } catch (Exception ex) { Diag.Failed("x", ex); }\n}');
  const source = ' Task.Run(() => ListenAsync()); ';
  assert.deepStrictEqual(ff.regionProblems(region(source), FILE, known), []);
});

test('Task.Run su un metodo che non cattura si segnala', () => {
  const known = withMethods('private async Task ListenAsync()\n{\n  await ReadAsync();\n}');
  const problems = ff.regionProblems(region(' Task.Run(() => ListenAsync()); '), FILE, known);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /Task.Run fires ListenAsync\(\)/);
});

test('la lambda del dispatcher senza catch si segnala', () => {
  const source = ' Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>\n{\n' +
    '  Items.UpdateLayout();\n}); ';
  const problems = ff.regionProblems(region(source), FILE, new Map());
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /dispatcher lambda/);
});

test('la lambda del dispatcher con il catch passa', () => {
  const source = ' Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>\n{\n' +
    '  try { Items.UpdateLayout(); }\n  catch (Exception ex) { Diag.Failed("x", ex); }\n}); ';
  assert.deepStrictEqual(ff.regionProblems(region(source), FILE, new Map()), []);
});

test('due istruzioni in una sola regione si segnalano', () => {
  const problems = ff.regionProblems(region(' A(); B(); '), FILE, new Map());
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /more than one statement/);
});

test('il delegato con un punto e virgola dentro resta una chiamata sola', () => {
  const known = withMethods('public Task RunAsync(Func<Task> work)\n{\n' +
    '  return RunAsync<bool>(work);\n}');
  const source = ' Writes.RunAsync(delegate { return WriteAsync(); }); ';
  // RunAsync non cattura: la regione si segnala per quello, non per il conteggio.
  const problems = ff.regionProblems(region(source), FILE, known);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /RunAsync\(\) is fired without await/);
});

test('il conteggio dei punti e virgola ignora lambda e parentesi', () => {
  assert.strictEqual(ff.topLevelSemicolons(' A(delegate { return B(); }); '), 1);
  assert.strictEqual(ff.topLevelSemicolons(' A(); B(); '), 2);
  assert.strictEqual(ff.topLevelSemicolons(' A();\n B();\n '), 2);
  // Il `;` dentro il blocco `if` non e' un'istruzione di primo livello.
  assert.strictEqual(ff.topLevelSemicolons(' A();\n if (x) { B(); }\n '), 1);
});

test('una regione vuota si segnala', () => {
  const problems = ff.regionProblems(region('   '), FILE, new Map());
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /guards nothing/);
});

test('regions legge il testo fra le due direttive con la sua riga', () => {
  const source = 'class A\n{\n#pragma warning disable 4014\n  SaveAsync();\n' +
    '#pragma warning restore 4014\n}';
  const found = ff.regions(source);
  assert.strictEqual(found.length, 1);
  assert.strictEqual(found[0].line, 3);
  assert.match(found[0].text, /SaveAsync\(\);/);
});

test('una direttiva commentata non e una regione', () => {
  const source = '// #pragma warning disable 4014\n  SaveAsync();';
  assert.deepStrictEqual(ff.regions(source), []);
});

test('methods non scambia il pragma per una dichiarazione', () => {
  const source = 'private void Save()\n{\n#pragma warning disable 4014\n' +
    '  ShowLocalPreviewAsync(name);\n#pragma warning restore 4014\n}';
  const known = ff.methods(source);
  assert.strictEqual(known.has('ShowLocalPreviewAsync'), false);
  assert.strictEqual(known.has('Save'), true);
});

test('methods legge il corpo di un metodo con modificatori', () => {
  const known = ff.methods('public static async Task<bool> StartAsync()\n{\n' +
    '  try { return true; }\n  catch (Exception ex) { return false; }\n}');
  assert.strictEqual(known.has('StartAsync'), true);
  assert.strictEqual(ff.handlesItsOwnFault(known.get('StartAsync')), true);
});

test("l'answer di un allegato senza SetMessageStatus si segnala", () => {
  const source = 'case "attachment.sent":\n    break;';
  const found = ff.statusAnswerProblems(source, 'WhatsappApp/Services/DataService.cs').join('\n');
  assert.match(found, /SetMessageStatus/);
});

test('un case error che non segna Failed si segnala', () => {
  const source = 'case "attachment.sent":\n  SetMessageStatus(a, b, MessageStatus.Sent);\n' +
    'case "error":\n  RaiseAdapterError(x);';
  const found = ff.statusAnswerProblems(source, 'WhatsappApp/Services/DataService.cs').join('\n');
  assert.match(found, /Failed/);
});
