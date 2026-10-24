#!/usr/bin/env node
/**
 * tools/check-no-exception-probes.js
 *
 * Guard for the padding probe that decided by throwing.
 *
 * Perche' esiste: `Message.DecodePadded` rispondeva a "questo e' il padding?"
 * provando a parsare dentro un `try`, un tentativo per ogni taglio possibile -
 * il pad dichiarato, poi 1..15 byte tolti, poi il plaintext intero. Ogni
 * tentativo che non reggeva era un `throw` preso dal `catch` due righe sotto:
 * fino a diciassette eccezioni first-chance per un messaggio che magari si
 * leggeva al primo colpo. Il debugger le stampa tutte, e la corsa del 24
 * ottobre 2026 fu una colonna di `Eccezione generata: ProtocolException` per
 * messaggi che poi arrivavano in chat.
 *
 * Adesso la forma si legge invece di indovinarla: `ProtoReader.Measure` dice
 * dove finisce il protobuf senza lanciare, e `DecodePadded` la interroga. La
 * sola eccezione che resta e' quella vera, una, con la frase dei byte.
 *
 * Regole:
 *  1. `ProtoReader.Measure` e `VarintAt` non lanciano niente: un troncamento
 *     e' una risposta, non un guasto;
 *  2. `Message.DecodePadded` non ha un `catch (ProtocolException)` nel corpo:
 *     se ricompare, la sonda a eccezioni e' tornata;
 *  3. `DecodePadded` chiede a `ProtoReader.Measure` dove finisce il protobuf,
 *     invece di provare i sedici tagli.
 *
 * Usage:
 *   node tools/check-no-exception-probes.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const PROTO = 'WhatsappApp/Protocol/WaProto/Proto.cs';
const MESSAGE = 'WhatsappApp/Protocol/WaProto/Message.cs';

/**
 * The body of `signature`'s method, braces matched. Null when it is not there.
 */
function bodyAt(source, signature) {
  const at = source.indexOf(signature);
  if (at < 0) return null;

  const open = source.indexOf('{', at + signature.length);
  if (open < 0) return null;

  let depth = 0;
  for (let i = open; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}') {
      depth--;
      if (depth === 0) return source.slice(open, i + 1);
    }
  }

  return null;
}

/**
 * The body without its comments: this guard reads code, and the prose beside the code
 * talks about the throws that were removed.
 */
function stripComments(text) {
  return text
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/\/\/[^\n]*/g, '');
}

function problemsFor(files) {
  const problems = [];

  const measure = stripComments(bodyAt(files.proto, 'public static int Measure(byte[] bytes, int limit)') || '');
  if (!measure) {
    problems.push(`${PROTO}: no Measure(byte[] bytes, int limit): the padding probe has to read ` +
      'the shape, and there is nothing that answers where a protobuf ends without throwing');
  } else if (measure.indexOf('throw new') >= 0) {
    problems.push(`${PROTO}: Measure throws: the whole point of it is that a truncated field is ` +
      'an answer - a probe that throws is the seventeen first-chance exceptions of 24 October 2026');
  }

  const varint = stripComments(bodyAt(files.proto, 'private static int VarintAt(') || '');
  if (!varint) {
    problems.push(`${PROTO}: no VarintAt: Measure reads its tags and lengths with it, and the ` +
      'throwing ReadVarint cannot stand in');
  } else if (varint.indexOf('throw new') >= 0) {
    problems.push(`${PROTO}: VarintAt throws: a number that runs past the buffer is where the ` +
      'walk stops, not a failure');
  }

  const padded = stripComments(bodyAt(files.message, 'public static Message DecodePadded(byte[] plaintext)') || '');
  if (!padded) {
    problems.push(`${MESSAGE}: no DecodePadded(byte[] plaintext): the reader every decrypted ` +
      'message goes through is gone');
  } else {
    if (padded.indexOf('catch (ProtocolException') >= 0) {
      problems.push(`${MESSAGE}: DecodePadded catches a ProtocolException again: that is the ` +
        'probe by exception, which costs one first-chance exception per padding guess in the ' +
        "debugger and reads as a crash in the run's output");
    }
    if (padded.indexOf('ProtoReader.Measure') < 0) {
      problems.push(`${MESSAGE}: DecodePadded does not ask ProtoReader.Measure where the ` +
        'protobuf ends: the padding is being guessed instead of read');
    }
    if (padded.indexOf('for (') >= 0) {
      problems.push(`${MESSAGE}: DecodePadded walks the sixteen trims in a loop again: the walk ` +
        'in ProtoReader.Measure answers once, and it does not throw on the way');
    }
  }

  return problems;
}

function main() {
  const read = (file) => fs.readFileSync(path.join(ROOT, file), 'utf8');
  const problems = problemsFor({ proto: read(PROTO), message: read(MESSAGE) });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} exception-probe problem(s).`);
    process.exit(1);
  }
  console.log('OK: the padding is read, not guessed - no exception per trim, and a damaged ' +
    'plaintext still names itself once.');
}

if (require.main === module) main();

module.exports = {
  problemsFor,
  bodyAt,
  stripComments,
  PROTO,
  MESSAGE
};
