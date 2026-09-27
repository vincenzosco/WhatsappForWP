#!/usr/bin/env node
/**
 * tools/csharp.js
 *
 * Legge il C# come lo legge un compilatore e non come lo legge una regex: i
 * commenti non sono codice.
 *
 * Perche' esiste: due guardie hanno bisogno della stessa cosa, e sbagliarla in
 * due modi diversi costa due falsi positivi. I due casi che l'hanno resa
 * necessaria:
 *
 *  - un commento che cita il modello `TileWide310x150IconWithBadge` veniva letto
 *    come l'uso del modello;
 *  - un commento che cita `SetSourceAsync` sembrava una chiamata fatta prima di
 *    `DecodePixelWidth`.
 *
 * E il motivo per cui non basta cercare `//`: "ms-appx:///Assets/TileIcon.png"
 * ha due barre dentro, e cancellare da li' in poi cancellerebbe proprio il
 * percorso che si sta cercando. Quindi si guarda dentro le stringhe.
 */
'use strict';

/**
 * Toglie i commenti di riga, senza toccare le stringhe. I commenti di blocco non
 * servono qui: questo C# usa quelli di riga.
 */
function stripComments(source) {
  let out = '';
  let inString = false;
  for (let i = 0; i < source.length; i++) {
    const ch = source[i];
    if (inString) {
      out += ch;
      if (ch === '\\' && i + 1 < source.length) {
        out += source[++i];
        continue;
      }
      if (ch === '"') inString = false;
      continue;
    }
    if (ch === '"') {
      inString = true;
      out += ch;
      continue;
    }
    if (ch === '/' && source[i + 1] === '/') {
      while (i < source.length && source[i] !== '\n') i++;
      out += '\n';
      continue;
    }
    out += ch;
  }
  return out;
}

module.exports = { stripComments };
