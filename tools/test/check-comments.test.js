'use strict';
const test = require('node:test');
const assert = require('node:assert');

const comments = require('../check-comments');

/** Italian sample words, built so this file holds no Italian comment of its own. */
const italian = (words) => '// ' + words.join(' ');

test('the shipped code passes its own comment rules', () => {
  const strict = comments.lint().filter((problem) => problem.strict);

  assert.deepStrictEqual(strict, []);
});

test('shipped code is enforced, tooling scripts are counted as debt', () => {
  assert.strictEqual(comments.isStrict('WhatsappApp/Services/CallRinger.cs'), true);
  assert.strictEqual(comments.isStrict('WhatsappApp.Tasks/BackgroundSyncTask.cs'), true);
  assert.strictEqual(comments.isStrict('ProtocolTests/TileCanvasTests.cs'), true);
  assert.strictEqual(comments.isStrict('tools/check-tile.js'), false);
});

test('a comment written in Italian is reported', () => {
  const found = comments.lintText(italian(['la', 'coda', 'della', 'corsa']), 'x.cs');

  assert.strictEqual(found.length, 1);
  assert.strictEqual(found[0].kind, 'italian');
  assert.strictEqual(found[0].line, 1);
});

test('one Italian-looking word inside an English comment is not a sentence', () => {
  const found = comments.lintText('// the non-zero count, and the non-pinned rows', 'x.cs');

  assert.deepStrictEqual(found, []);
});

test('Italian inside a quote is a quoted UI string, not the comment', () => {
  const found = comments.lintText('// it turns "' + ['gli', 'stati', 'non', 'compaiono'].join(' ') + '" into a fact', 'x.cs');

  assert.deepStrictEqual(found, []);
});

test('Italian inside backticks is a quoted identifier', () => {
  const found = comments.lintText('// it reports `' + ['Impossibile', 'collegare', 'il', 'dispositivo'].join(' ') + '`', 'x.cs');

  assert.deepStrictEqual(found, []);
});

test('a date in a comment is reported', () => {
  const found = comments.lintText('// on 4 November 2026 the phone answered', 'x.cs');

  assert.strictEqual(found.length, 1);
  assert.strictEqual(found[0].kind, 'date');
});

test('an emoji in a comment is reported', () => {
  const found = comments.lintText('// this works \u{1F680} now', 'x.cs');

  assert.strictEqual(found.length, 1);
  assert.strictEqual(found[0].kind, 'emoji');
});

test('a template opener is reported, in both of its forms', () => {
  assert.strictEqual(comments.lintText('/// Why it exists: the tap did nothing', 'x.cs')[0].kind, 'rhetoric');
  assert.strictEqual(comments.lintText('// What it is not: a ringtone', 'x.cs')[0].kind, 'rhetoric');
});

test('a block comment is read line by line', () => {
  const text = ['/*', ' * ' + ['il', 'cavo', 'della', 'rete'].join(' '), ' */'].join('\n');
  const found = comments.lintText(text, 'x.c');

  assert.strictEqual(found.length, 1);
  assert.strictEqual(found[0].line, 2);
});

test('code that is not a comment is never judged', () => {
  const found = comments.lintText('string text = "' + ['la', 'casa', 'della', 'nonna'].join(' ') + '";', 'x.cs');

  assert.deepStrictEqual(found, []);
});

test('a batch file is read too', () => {
  const found = comments.lintText('rem ' + ['questo', 'viene', 'prima', 'anche'].join(' '), 'x.bat');

  assert.strictEqual(found.length, 1);
});
