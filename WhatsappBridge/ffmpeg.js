'use strict';

// Trascodifica l'audio che Windows Phone 8.1 non sa decodificare.
//
// Perche' esiste: i messaggi vocali di WhatsApp sono Ogg con codec Opus, e
// WP8.1 non ha un decoder Opus (arriva solo da Windows 10). Senza questa
// conversione un vocale resta una parola che non si puo' toccare. L'adapter
// chiama ffmpeg, se c'e', e manda all'app un MP3, che il telefono legge.
//
// ffmpeg e' un programma esterno, non una dipendenza npm: l'adapter deve
// funzionare anche senza. La chiamata e' iniettabile, cosi' i test non hanno
// bisogno di ffmpeg installato.

const { execFile } = require('child_process');

// Il tetto dei byte in memoria. Lo stesso numero di server.js (MAX_MEDIA_BYTES):
// oltre non c'e' piu' un media ma un guasto.
const MAX_MEDIA_BYTES = 64 * 1024 * 1024;

// Mono, 16 kHz, 32 kbit/s: un vocale WhatsApp e' parlato, e questa e' la forma
// piu' piccola che resta intelligibile. Il file si scarica dal telefono.
const TRANSCODE_ARGS = [
  '-hide_banner',
  '-loglevel', 'error',
  '-i', 'pipe:0',
  '-vn',
  '-ac', '1',
  '-ar', '16000',
  '-b:a', '32k',
  '-f', 'mp3',
  'pipe:1'
];

/** Il tipo che WP8.1 non sa leggere: Ogg, Opus o il loro contenitore. */
function isOggOpus(mimeType, fileName) {
  const mime = String(mimeType || '').toLowerCase();
  const name = String(fileName || '').toLowerCase();
  if (mime === 'audio/ogg' || mime === 'audio/opus' || mime === 'audio/oga') return true;
  return name.endsWith('.ogg') || name.endsWith('.opus') || name.endsWith('.oga');
}

/** Il nome del file con un'altra estensione, o con quella se non ne ha. */
function replaceExtension(fileName, extension) {
  const name = String(fileName || 'audio');
  const dot = name.lastIndexOf('.');
  const base = dot > 0 ? name.slice(0, dot) : name;
  return `${base}${extension}`;
}

/** ffmpeg legge l'input da stdin e scrive l'output su stdout: nessun file di mezzo. */
function execFfmpeg(command, args, input) {
  return new Promise((resolve, reject) => {
    const child = execFile(command, args, { maxBuffer: MAX_MEDIA_BYTES, encoding: 'buffer' },
      (err, stdout) => {
        if (err) { reject(err); return; }
        resolve(stdout);
      });
    child.stdin.end(input || undefined);
  });
}

/**
 * Un transcodificatore ffmpeg. `run` e' iniettabile: i test non hanno ffmpeg
 * installato e non devono averlo.
 */
function createTranscoder(options) {
  const o = options || {};
  const command = o.path || 'ffmpeg';
  const logger = typeof o.log === 'function' ? o.log : function () {};
  const enabled = o.enabled !== false;
  const run = typeof o.run === 'function' ? o.run : (args, input) => execFfmpeg(command, args, input);

  // null finche' non si e' provato: un vocale non deve far partire ffmpeg una
  // volta per messaggio solo per scoprire che non c'e'.
  let available = null;

  return {
    isAvailable() { return available === true; },

    /** Si prova una volta, all'avvio, e si dice nel log com'e' andata. */
    async probe() {
      if (!enabled) {
        available = false;
        logger('INFO', 'ffmpeg disabled: Ogg/Opus voice notes will not be playable on WP8.1');
        return false;
      }
      try {
        await run(['-version'], null);
        available = true;
        logger('OK', 'ffmpeg found: voice notes will be transcoded to MP3 for WP8.1');
      } catch (err) {
        available = false;
        logger('WARN', `ffmpeg not found (${err.message}): Ogg/Opus voice notes will not be playable on WP8.1`);
      }
      return available;
    },

    /**
     * I byte da mandare all'app. Per un audio che WP8.1 non legge restituisce
     * l'MP3 e la sua identita'; per tutto il resto, o quando ffmpeg non c'e'
     * o fallisce, restituisce null e l'adapter manda l'originale.
     */
    async toPlayable(buffer, mimeType, fileName) {
      if (!enabled || available !== true) return null;
      if (!isOggOpus(mimeType, fileName)) return null;
      if (!buffer || buffer.length === 0) return null;

      try {
        const mp3 = await run(TRANSCODE_ARGS, buffer);
        if (!mp3 || mp3.length === 0) return null;
        return {
          buffer: mp3,
          mimeType: 'audio/mpeg',
          fileName: replaceExtension(fileName || 'audio.ogg', '.mp3')
        };
      } catch (err) {
        logger('WARN', `ffmpeg transcode failed: ${err.message}`);
        return null;
      }
    }
  };
}

module.exports = { createTranscoder, isOggOpus, replaceExtension, TRANSCODE_ARGS };
