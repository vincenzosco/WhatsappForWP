'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');

const guard = require('../check-no-exception-probes');

const ROOT = path.resolve(__dirname, '..', '..');

// La forma dei tre corpi che il guard legge. Ogni fixture cambia un punto solo.
function sources(options) {
  const opts = options || {};

  const measure = opts.noMeasure ? '' : '        public static int Measure(byte[] bytes, int limit)\n'
    + '        {\n'
    + '            int index = 0;\n'
    + (opts.measureThrows ? '            if (index > limit) throw new ProtocolException("nope");\n' : '')
    + '            return index;\n'
    + '        }\n';

  const endOf = opts.noEndOf ? '' : '        public static int EndOfPadded(byte[] bytes, int declared)\n'
    + '        {\n'
    + (opts.endOfThrows ? '            if (declared > bytes.Length) throw new ProtocolException("nope");\n' : '')
    + '            return Measure(bytes, bytes.Length);\n'
    + '        }\n';

  const varint = opts.noVarint ? '' : '        private static int VarintAt(byte[] bytes, int index, int limit, out ulong value)\n'
    + '        {\n'
    + (opts.varintThrows ? '            throw new ProtocolException("a varint runs past the buffer");\n' : '')
    + '            return -1;\n'
    + '        }\n';

  const padded = opts.noPadded ? '' : '        public static Message DecodePadded(byte[] plaintext)\n'
    + '        {\n'
    + (opts.loop ? '            for (int trimmed = 1; trimmed <= MaxPadding; trimmed++)\n'
      + '            {\n'
      + '                return Decode(plaintext);\n'
      + '            }\n' : '')
    + (opts.measureCall === false ? '' : '            int whole = ProtoReader.Measure(plaintext);\n')
    + (opts.endCall === false ? '' : '            int ended = ProtoReader.EndOfPadded(plaintext, 6);\n')
    + (opts.catchProbe ? '            try { return Decode(plaintext); }\n'
      + '            catch (ProtocolException) { }\n' : '')
    + '            return Decode(plaintext);\n'
    + '        }\n';

  return { proto: measure + endOf + varint, message: padded };
}

test('la forma vera non si segnala', () => {
  assert.deepStrictEqual(guard.problemsFor(sources()), []);
});

test('un Measure che lancia si segnala, ed e il difetto del 24 ottobre', () => {
  const problems = guard.problemsFor(sources({ measureThrows: true }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /Measure throws/);
  assert.match(problems[0], /seventeen first-chance exceptions/);
});

test('un Measure assente si segnala', () => {
  const problems = guard.problemsFor(sources({ noMeasure: true }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /no Measure/);
});

test('un VarintAt che lancia si segnala', () => {
  const problems = guard.problemsFor(sources({ varintThrows: true }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /VarintAt throws/);
});

test('un VarintAt assente si segnala', () => {
  const problems = guard.problemsFor(sources({ noVarint: true }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /no VarintAt/);
});

test('un DecodePadded che cattura di nuovo si segnala', () => {
  const problems = guard.problemsFor(sources({ catchProbe: true }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /catches a ProtocolException again/);
});

test('un DecodePadded che non chiede a Measure si segnala', () => {
  const problems = guard.problemsFor(sources({ measureCall: false }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /does not ask ProtoReader.Measure/);
});

test('un giro dei sedici tagli si segnala', () => {
  const problems = guard.problemsFor(sources({ loop: true }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /sixteen trims/);
});

test('un EndOfPadded che lancia si segnala', () => {
  const problems = guard.problemsFor(sources({ endOfThrows: true }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /EndOfPadded throws/);
});

test('un EndOfPadded assente si segnala', () => {
  const problems = guard.problemsFor(sources({ noEndOf: true }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /no EndOfPadded/);
});

test('un DecodePadded che si fida del conto si segnala', () => {
  const problems = guard.problemsFor(sources({ endCall: false }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /trusts the count in the last byte/);
});

test('un DecodePadded assente si segnala', () => {
  const problems = guard.problemsFor(sources({ noPadded: true }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /no DecodePadded/);
});

test('il codice vero legge il padding invece di indovinarlo', () => {
  const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');
  assert.deepStrictEqual(
    guard.problemsFor({ proto: read(guard.PROTO), message: read(guard.MESSAGE) }), []);
});

test('bodyAt legge il corpo giusto, con le graffe annidate', () => {
  const source = 'class A\n{\n  int F()\n  {\n    if (x) { y(); }\n    return 1;\n  }\n  int G() { return 2; }\n}\n';
  const body = guard.bodyAt(source, 'int F()');
  assert.match(body, /return 1;/);
  assert.doesNotMatch(body, /return 2;/);
});
