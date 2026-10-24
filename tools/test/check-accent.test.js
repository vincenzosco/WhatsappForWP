'use strict';
const test = require('node:test');
const assert = require('node:assert');

const guard = require('../check-accent');

/** The three methods the guard reads. Each fixture changes one point. */
function sources(options) {
  const opts = options || {};

  const apply = '        private static Color Lookup()\n'
    + '        {\n'
    + '        }\n\n'
    + '        public static void Apply()\n'
    + '        {\n'
    + (opts.applyKeyed ? '            if (resources.ContainsKey(keys[i]))\n'
      + '            {\n'
      + '                _color = (Color)resources[keys[i]];\n'
      + '            }\n'
      : '')
    + (opts.applyTry ? '            try { _color = (Color)resources[keys[i]]; }\n'
      + '            catch (Exception ex) { Diag.Ok(ex.Message); }\n' : '')
    + (opts.applyKeyed || opts.applyTry ? '' : '            _color = (Color)resources[keys[i]];\n')
    + '            Diag.OkNow("accent: " + _color);\n'
    + '        }\n';

  const brush = '        private static void SetBrush(string key, Color color)\n'
    + '        {\n'
    + (opts.brushKeyed ? '            Application app = Application.Current;\n'
      + '            if (app == null || !app.Resources.ContainsKey(key)) return;\n'
      : '')
    + (opts.brushTry ? '            try { }\n' : '')
    + '            var brush = app.Resources[key] as SolidColorBrush;\n'
    + '            if (brush != null) brush.Color = color;\n'
    + '        }\n';

  return apply + '\n' + brush;
}

test('la forma vera non si segnala', () => {
  assert.deepStrictEqual(guard.problemsFor(sources({ applyKeyed: true, brushKeyed: true })), []);
});

test('una Apply che legge senza ContainsKey si segnala, ed e il difetto del 24 ottobre', () => {
  const problems = guard.problemsFor(sources({ brushKeyed: true }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /Apply reads a resource without asking ContainsKey/);
  assert.match(problems[0], /24 October 2026/);
});

test('una Apply con un try si segnala anche quando ContainsKey c e', () => {
  const problems = guard.problemsFor(sources({ applyKeyed: true, applyTry: true, brushKeyed: true }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /Apply has a try again/);
});

test('una SetBrush senza ContainsKey si segnala (un tentativo per pennello)', () => {
  const problems = guard.problemsFor(sources({ applyKeyed: true }));
  assert.strictEqual(problems.length, 2);
  assert.match(problems[0], /SetBrush reads the resource dictionary without ContainsKey/);
  assert.match(problems[1], /process with no application/);
});

test('un file senza Apply si segnala', () => {
  const problems = guard.problemsFor('');
  assert.ok(problems.some((p) => /no Apply\(\)/.test(p)), problems.join('\n'));
});

test('una SetBrush con un try si segnala', () => {
  const problems = guard.problemsFor(sources({ applyKeyed: true, brushKeyed: true, brushTry: true }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /SetBrush has a try again/);
});
