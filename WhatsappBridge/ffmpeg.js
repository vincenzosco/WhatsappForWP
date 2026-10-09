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
const fs = require('fs');
const os = require('os');
const path = require('path');

// The ceiling of bytes in memory. The same number as server.js
// (MAX_MEDIA_BYTES): past it there is no media any more, only a fault.
const MAX_MEDIA_BYTES = 64 * 1024 * 1024;

// Below this the conversion is not worth its cost: the phone has already made
// the video small, and the adapter would spend cpu to save a few kilobytes.
const VIDEO_COMPRESS_MIN_BYTES = 4 * 1024 * 1024;

// 480 lines at most, H.264 at crf 28: a video for a chat, not a copy of the
// camera file. `-2` keeps the width even and the aspect ratio; the comma inside
// min() is escaped with a backslash because there is no shell here to quote it.
const VIDEO_ARGS = [
  '-hide_banner',
  '-loglevel', 'error',
  '-i', 'pipe:0',
  '-vf', 'scale=min(854\\,iw):-2',
  '-c:v', 'libx264',
  '-preset', 'veryfast',
  '-crf', '28',
  '-pix_fmt', 'yuv420p',
  '-c:a', 'aac',
  '-b:a', '96k',
  '-movflags', 'frag_keyframe+empty_moov',
  '-f', 'mp4',
  'pipe:1'
];

/** Whether these bytes are a video, from the type or from the name. */
function isVideo(mimeType, fileName) {
  const mime = String(mimeType || '').toLowerCase();
  const name = String(fileName || '').toLowerCase();
  if (mime.indexOf('video/') === 0) return true;
  return /\.(mp4|mov|3gp|avi|mkv|webm)$/.test(name);
}

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

// Mono, 16 kHz, 32 kbit/s Opus in Ogg: the only form WhatsApp accepts as a voice
// note. The phone records AAC/M4A and has no Opus encoder (the same WP8.1 gap as
// the decoder), so the conversion has to happen here, on the way out.
const VOICE_ARGS = [
  '-hide_banner',
  '-loglevel', 'error',
  '-i', 'pipe:0',
  '-vn',
  '-ac', '1',
  '-ar', '16000',
  '-b:a', '32k',
  '-c:a', 'libopus',
  '-f', 'ogg',
  'pipe:1'
];

/** The type WP8.1 cannot read: Ogg, Opus or their container. */
function isOggOpus(mimeType, fileName) {
  const mime = String(mimeType || '').toLowerCase();
  const name = String(fileName || '').toLowerCase();
  if (mime === 'audio/ogg' || mime === 'audio/opus' || mime === 'audio/oga') return true;
  // `application/ogg` is the Ogg container's own MIME type (RFC 3533), and it
  // is what GOWA answers a downloaded voice note with - often with a file name
  // that has no extension. Without this the note is never converted and reaches
  // the phone as a codec it cannot read.
  if (mime === 'application/ogg' || mime === 'application/opus' || mime === 'application/oga') {
    return true;
  }
  return name.endsWith('.ogg') || name.endsWith('.opus') || name.endsWith('.oga');
}

/** The file name with another extension, or with the one it has if it has none. */
function replaceExtension(fileName, extension) {
  const name = String(fileName || 'audio');
  const dot = name.lastIndexOf('.');
  const base = dot > 0 ? name.slice(0, dot) : name;
  return `${base}${extension}`;
}

/**
 * The arguments with the input on a file instead of on `pipe:0`, and the folder
 * holding it.
 *
 * Why the input is not piped: ffmpeg cannot seek a pipe. The MP4/M4A the phone
 * records keeps its `moov` atom at the END of the file (the same file has it at
 * byte 77230 of 79299), so ffmpeg looking for it on a pipe reads the header,
 * finds no track, and writes a 322-byte Ogg with no audio in it - and exits 0,
 * so the empty note was accepted as a conversion and sent. On a file it seeks to
 * the end, reads the whole recording, and the conversion is real.
 */
function inputFileFor(args, input) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'whatsapp-bridge-'));
  const file = path.join(dir, 'input');
  fs.writeFileSync(file, input);
  return { dir, args: args.map((arg) => (arg === 'pipe:0' ? file : arg)) };
}

/** ffmpeg reads the input from a file and writes the output to stdout. */
function execFfmpeg(command, args, input) {
  return new Promise((resolve, reject) => {
    let written = null;
    let realArgs = args;
    if (input && input.length) {
      try {
        written = inputFileFor(args, input);
        realArgs = written.args;
      } catch (err) {
        reject(err);
        return;
      }
    }

    const done = () => {
      if (!written) return;
      try { fs.rmSync(written.dir, { recursive: true, force: true }); } catch (err) { /* nothing left to do */ }
      written = null;
    };

    const child = execFile(command, realArgs, { maxBuffer: MAX_MEDIA_BYTES, encoding: 'buffer' },
      (err, stdout) => {
        done();
        if (err) { reject(err); return; }
        resolve(stdout);
      });
    child.on('error', done);
    // Nothing goes on stdin: with the input on a file ffmpeg must not wait for
    // a pipe that will never be written to.
    child.stdin.end(written ? undefined : (input || undefined));
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
  // What the binary can actually do: a stripped build may have only one half.
  // `audio` is an MP3 for a received note, `opus` an Ogg/Opus for a sent one.
  let caps = { audio: false, opus: false, video: false };

  /** The text of one `ffmpeg -list` invocation, whatever the injected run returns. */
  async function listing(args) {
    const text = await run(args, null);
    return Buffer.isBuffer(text) ? text.toString('utf8') : String(text || '');
  }

  /** The first named piece the build lacks, or '' when it has them all. */
  function hasPieces(encoder, muxer, decoder, encoders, muxers, decoders) {
    if (encoders.indexOf(encoder) < 0) return encoder;
    if (muxers.indexOf(muxer) < 0) return muxer;
    if (decoder && decoders.indexOf(decoder) < 0) return decoder;
    return '';
  }

  return {
    isAvailable() { return available === true; },

    /** What the probed binary can do: an MP3 for voice notes, a smaller MP4 for video. */
    capabilities() { return caps; },

    /** Probed once, at startup, and the log says how it went. */
    async probe() {
      if (!enabled) {
        available = false;
        caps = { audio: false, opus: false, video: false };
        logger('INFO', 'ffmpeg disabled: Ogg/Opus voice notes will not be playable on WP8.1');
        return false;
      }
      try {
        await run(['-version'], null);
      } catch (err) {
        available = false;
        caps = { audio: false, opus: false, video: false };
        logger('WARN', `ffmpeg not found (${err.message}): Ogg/Opus voice notes will not be playable on WP8.1`);
        return false;
      }
      // A binary that runs is not a binary that can do this job: a stripped
      // build may lack libmp3lame, and then every conversion fails in silence.
      try {
        const encoders = await listing(['-hide_banner', '-encoders']);
        const muxers = await listing(['-hide_banner', '-muxers']);
        const decoders = await listing(['-hide_banner', '-decoders']);
        const missingAudio = hasPieces('libmp3lame', 'mp3', 'opus', encoders, muxers, decoders);
        const missingVoice = hasPieces('libopus', 'ogg', null, encoders, muxers, decoders);
        const missingVideo = hasPieces('libx264', 'mp4', null, encoders, muxers, decoders);
        caps = {
          audio: missingAudio === '',
          opus: missingVoice === '',
          video: missingVideo === ''
        };
        available = caps.audio;
        if (!caps.audio) {
          logger('WARN', `ffmpeg cannot make MP3 (${missingAudio} missing): received voice notes will not be playable on WP8.1`);
        } else {
          logger('OK', `ffmpeg found: audio yes, voice ${caps.opus ? 'yes' : 'no'}, video ${caps.video ? 'yes' : 'no'}`);
        }
        // A build without an Opus encoder still shows a received note, but a
        // voice note recorded on the phone cannot be sent: WhatsApp accepts a
        // voice note only as Ogg/Opus and the phone records M4A/AAC.
        if (!caps.opus) {
          logger('WARN', `ffmpeg cannot make Opus (${missingVoice} missing): a recorded voice note cannot be sent as a WhatsApp voice note`);
        }
      } catch (err) {
        available = false;
        caps = { audio: false, opus: false, video: false };
        logger('WARN', `ffmpeg could not be inspected (${err.message}): received voice notes will not be playable on WP8.1`);
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
    },

    /**
     * The bytes to send to WhatsApp as a voice note. WhatsApp accepts a voice
     * note only as Ogg/Opus, and the phone records M4A/AAC: without this the
     * send comes back refused ("your audio type is not allowed").
     *
     * Returns null when the payload is already Ogg/Opus (nothing to do), when
     * the build has no Opus encoder, when there is nothing to convert, or when
     * the conversion failed: the caller then sends the original, and WhatsApp
     * refuses it, which is what the bubble is told.
     */
    async toVoiceNote(buffer, mimeType, fileName) {
      if (!enabled || caps.opus !== true) return null;
      if (isOggOpus(mimeType, fileName)) return null;
      if (!buffer || buffer.length === 0) return null;

      try {
        const ogg = await run(VOICE_ARGS, buffer);
        if (!ogg || ogg.length === 0) return null;
        return {
          buffer: ogg,
          mimeType: 'audio/ogg',
          fileName: replaceExtension(fileName || 'voice.m4a', '.ogg')
        };
      } catch (err) {
        logger('WARN', `ffmpeg voice-note conversion failed: ${err.message}`);
        return null;
      }
    },

    /**
     * The same video, smaller, for the one that arrives large anyway: the phone
     * could not shrink it (no transcoder, or the transcode failed), and sending
     * the camera file to WhatsApp is what makes the send slow.
     *
     * Returns null when ffmpeg is absent, the file is not a video, it is already
     * small, or the conversion did not make it smaller: the caller sends the
     * original in every one of those cases.
     */
    async toSmallerVideo(buffer, mimeType, fileName) {
      if (!enabled || caps.video !== true) return null;
      if (!isVideo(mimeType, fileName)) return null;
      if (!buffer || buffer.length < VIDEO_COMPRESS_MIN_BYTES) return null;

      try {
        const smaller = await run(VIDEO_ARGS, buffer);
        if (!smaller || smaller.length === 0) return null;
        // A conversion that came out bigger is not a conversion.
        if (smaller.length >= buffer.length) return null;
        return {
          buffer: smaller,
          mimeType: 'video/mp4',
          fileName: replaceExtension(fileName || 'video.mp4', '.mp4')
        };
      } catch (err) {
        logger('WARN', `ffmpeg video transcode failed: ${err.message}`);
        return null;
      }
    }
  };
}

module.exports = {
  createTranscoder,
  inputFileFor,
  isOggOpus,
  isVideo,
  replaceExtension,
  TRANSCODE_ARGS,
  VOICE_ARGS,
  VIDEO_ARGS,
  VIDEO_COMPRESS_MIN_BYTES
};
