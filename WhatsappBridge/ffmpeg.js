'use strict';

// Transcodes the audio Windows Phone 8.1 cannot decode.
//
// Why it exists: WhatsApp voice notes are Ogg with the Opus codec, and WP8.1
// has no Opus decoder (it only arrived with Windows 10). Without this
// conversion a voice note stays a word that cannot be played. The adapter calls
// ffmpeg, if present, and sends the app an MP3, which the phone reads.
//
// ffmpeg is an external program, not an npm dependency: the adapter must work
// without it too. The call is injectable, so the tests need no ffmpeg installed.

const { execFile } = require('child_process');

// The ceiling of bytes in memory. The same number as server.js
// (MAX_MEDIA_BYTES): past it there is no media any more, only a fault.
const MAX_MEDIA_BYTES = 64 * 1024 * 1024;

// Mono, 16 kHz, 32 kbit/s: a WhatsApp voice note is speech, and this is the
// smallest form that stays intelligible. The file is downloaded from the phone.
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

/** The type WP8.1 cannot read: Ogg, Opus or their container. */
function isOggOpus(mimeType, fileName) {
  const mime = String(mimeType || '').toLowerCase();
  const name = String(fileName || '').toLowerCase();
  if (mime === 'audio/ogg' || mime === 'audio/opus' || mime === 'audio/oga') return true;
  return name.endsWith('.ogg') || name.endsWith('.opus') || name.endsWith('.oga');
}

/** The file name with another extension, or with the one it has if it has none. */
function replaceExtension(fileName, extension) {
  const name = String(fileName || 'audio');
  const dot = name.lastIndexOf('.');
  const base = dot > 0 ? name.slice(0, dot) : name;
  return `${base}${extension}`;
}

/** ffmpeg reads the input from stdin and writes the output to stdout: no file in between. */
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
 * An ffmpeg transcoder. `run` is injectable: the tests do not have ffmpeg
 * installed and must not need it.
 */
function createTranscoder(options) {
  const o = options || {};
  const command = o.path || 'ffmpeg';
  const logger = typeof o.log === 'function' ? o.log : function () {};
  const enabled = o.enabled !== false;
  const run = typeof o.run === 'function' ? o.run : (args, input) => execFfmpeg(command, args, input);

  // null until it has been probed: a voice note must not start ffmpeg once per
  // message just to find out it is not there.
  let available = null;

  return {
    isAvailable() { return available === true; },

    /** Probed once, at startup, and the log says how it went. */
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
     * The bytes to send to the app. For an audio WP8.1 cannot read it returns
     * the MP3 and its identity; for everything else, or when ffmpeg is absent
     * or fails, it returns null and the adapter sends the original.
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
