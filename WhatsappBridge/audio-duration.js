'use strict';

// How long an audio is, read out of its own bytes.
//
// Why it exists: the chat-list preview of a voice note said only "Audio", and
// GOWA has no duration to give - /chat/:jid/messages has media_type, filename,
// url and file_length but no length in time, and the webhook payload carries the
// audio as a path and nothing else. The bytes, however, do say it: every
// container keeps the length, and three of them cover what WhatsApp sends.
//
// It is hand-written on purpose: the adapter has zero runtime dependencies, and
// pulling in a media library for one number would be the whole dependency.
//
// Three containers, one question:
//   Ogg (Opus, Vorbis) - the granule position of the last page, in the units of
//                        the codec's sample rate;
//   MP4 / M4A          - the mvhd box of the moov, duration over timescale;
//   MP3                - the sum of the frames, each with a fixed sample count.
//
// Anything it cannot read returns null: the caller keeps the word it had.

const OGG_MAGIC = 'OggS';
const OPUS_MAGIC = 'OpusHead';
const VORBIS_MAGIC = '\u0001vorbis';

// Opus granules are always counted at 48 kHz, whatever the original rate was.
const OPUS_RATE = 48000;

// A granule of -1 means "no packet ends on this page": it is not a time.
const NO_GRANULE = -1;

/** The 64-bit unsigned little-endian value at `offset`, as a number. */
function readUInt64LE(buffer, offset) {
  const low = buffer.readUInt32LE(offset);
  const high = buffer.readUInt32LE(offset + 4);
  // Above 2^53 the value is not exact; a media that long is not a media.
  return high * 0x100000000 + low;
}

/** The granule of an Ogg page at `offset`, or null when it is not one. */
function oggPageAt(buffer, offset) {
  if (offset + 27 > buffer.length) return null;
  if (buffer.toString('ascii', offset, offset + 4) !== OGG_MAGIC) return null;

  const granule = readUInt64LE(buffer, offset + 6);
  const segments = buffer.readUInt8(offset + 26);
  const headerLength = 27 + segments;
  if (offset + headerLength > buffer.length) return null;

  let bodyLength = 0;
  for (let i = 0; i < segments; i++) bodyLength += buffer.readUInt8(offset + 27 + i);

  const end = offset + headerLength + bodyLength;
  if (end > buffer.length) return null;

  return {
    granule: granule >= 0xffffffffffffffff ? NO_GRANULE : granule,
    // The first packet of the first page: the identification header.
    payload: buffer.slice(offset + headerLength, end),
    end
  };
}

/** The sample rate of the codec identified in an Ogg stream. */
function oggSampleRate(buffer) {
  let offset = 0;
  while (offset + 27 <= buffer.length) {
    const page = oggPageAt(buffer, offset);
    if (page === null) break;

    const payload = page.payload;
    if (payload.length >= 19 && payload.toString('ascii', 0, 8) === OPUS_MAGIC) {
      // The header's rate is the original one, but the granule is always 48 kHz.
      return OPUS_RATE;
    }
    if (payload.length >= 16 && payload.toString('ascii', 0, 7) === VORBIS_MAGIC) {
      const rate = payload.readUInt32LE(12);
      if (rate > 0) return rate;
    }
    offset = page.end;
  }
  return 0;
}

/** Seconds in an Ogg stream, from the granule of its last complete page. */
function oggDuration(buffer) {
  let offset = 0;
  let lastGranule = NO_GRANULE;

  while (offset + 27 <= buffer.length) {
    const page = oggPageAt(buffer, offset);
    if (page === null) break;
    if (page.granule !== NO_GRANULE) lastGranule = page.granule;
    offset = page.end;
  }

  if (lastGranule === NO_GRANULE) return null;

  const rate = oggSampleRate(buffer);
  if (rate <= 0) return null;
  return lastGranule / rate;
}

/** The boxes of an ISO base media file, as { type, start, end } in order. */
function mp4Boxes(buffer, from, to) {
  const boxes = [];
  let offset = from;
  while (offset + 8 <= to) {
    let size = buffer.readUInt32BE(offset);
    const type = buffer.toString('ascii', offset + 4, offset + 8);
    let headerLength = 8;

    if (size === 1) {
      // 64-bit size: the box carries its own length after the type.
      if (offset + 16 > to) break;
      size = readUInt64BE(buffer, offset + 8);
      headerLength = 16;
    } else if (size === 0) {
      // To the end of the enclosing box.
      size = to - offset;
    }

    if (size < headerLength || offset + size > to) break;
    boxes.push({ type, start: offset + headerLength, end: offset + size });
    offset += size;
  }
  return boxes;
}

/** The 64-bit unsigned big-endian value at `offset`, as a number. */
function readUInt64BE(buffer, offset) {
  const high = buffer.readUInt32BE(offset);
  const low = buffer.readUInt32BE(offset + 4);
  return high * 0x100000000 + low;
}

/** Seconds in an MP4/M4A, from the mvhd of its moov. */
function mp4Duration(buffer) {
  const top = mp4Boxes(buffer, 0, buffer.length);
  const moov = top.find((box) => box.type === 'moov');
  if (!moov) return null;

  const inside = mp4Boxes(buffer, moov.start, moov.end);
  const mvhd = inside.find((box) => box.type === 'mvhd');
  if (!mvhd || mvhd.start + 4 > mvhd.end) return null;

  const version = buffer.readUInt8(mvhd.start);
  let timescale;
  let duration;

  if (version === 1) {
    // version(1) flags(3) created(8) modified(8) timescale(4) duration(8)
    if (mvhd.start + 32 > mvhd.end) return null;
    timescale = buffer.readUInt32BE(mvhd.start + 20);
    duration = readUInt64BE(buffer, mvhd.start + 24);
  } else {
    // version(1) flags(3) created(4) modified(4) timescale(4) duration(4)
    if (mvhd.start + 20 > mvhd.end) return null;
    timescale = buffer.readUInt32BE(mvhd.start + 12);
    duration = buffer.readUInt32BE(mvhd.start + 16);
  }

  if (timescale <= 0) return null;
  return duration / timescale;
}

// The MP3 tables. Only what a frame header needs: version, layer, bitrate and
// rate. A frame with a bitrate or a rate the table does not know stops the walk.
const MPEG_BITRATES = {
  // version, layer -> 16 values, index by the header's bitrate field
  '1-3': [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0],
  '1-2': [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0],
  '1-1': [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0],
  '2-3': [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0],
  '2-2': [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0],
  '2-1': [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0]
};

const MPEG_RATES = {
  1: [44100, 48000, 32000, 0],
  2: [22050, 24000, 16000, 0],
  // MPEG 2.5 keeps its own table; the version field calls it 0.
  0: [11025, 12000, 8000, 0]
};

/** Where the audio starts: past an ID3v2 tag, if there is one. */
function pastId3(buffer) {
  if (buffer.length < 10) return 0;
  if (buffer.toString('ascii', 0, 3) !== 'ID3') return 0;

  // A synchsafe integer: seven bits per byte.
  const size = (buffer.readUInt8(6) << 21) | (buffer.readUInt8(7) << 14)
    | (buffer.readUInt8(8) << 7) | buffer.readUInt8(9);
  return Math.min(10 + size, buffer.length);
}

/** Seconds in an MP3, from the frames it is made of. */
function mp3Duration(buffer) {
  let offset = pastId3(buffer);
  let seconds = 0;
  let frames = 0;

  while (offset + 4 <= buffer.length) {
    // Frame sync: eleven bits set.
    if (buffer.readUInt8(offset) !== 0xff || (buffer.readUInt8(offset + 1) & 0xe0) !== 0xe0) break;

    const versionBits = (buffer.readUInt8(offset + 1) >> 3) & 0x03;
    const layerBits = (buffer.readUInt8(offset + 1) >> 1) & 0x03;
    const bitrateIndex = (buffer.readUInt8(offset + 2) >> 4) & 0x0f;
    const rateIndex = (buffer.readUInt8(offset + 2) >> 2) & 0x03;

    // The version field: 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5, 1 = reserved.
    if (versionBits === 1 || layerBits === 0) break;

    const version = versionBits === 3 ? 1 : (versionBits === 2 ? 2 : 0);
    const layer = 4 - layerBits;
    const key = `${version === 1 ? 1 : 2}-${layer}`;
    const bitrate = (MPEG_BITRATES[key] || [])[bitrateIndex] || 0;
    const rate = (MPEG_RATES[version] || [])[rateIndex] || 0;
    if (bitrate <= 0 || rate <= 0) break;

    // Layer I reads 384 samples per frame, Layer II and III 1152 on MPEG1 and
    // 576 below it.
    const samples = layer === 1 ? 384 : (version === 1 ? 1152 : 576);
    // Layer I pads to four bytes, the others to one.
    const padding = (buffer.readUInt8(offset + 2) >> 1) & 0x01;
    const frameLength = layer === 1
      ? Math.floor((12 * bitrate * 1000 / rate + padding) * 4)
      : Math.floor((samples / 8) * bitrate * 1000 / rate + padding);

    if (frameLength <= 4 || offset + frameLength > buffer.length) break;

    seconds += samples / rate;
    frames++;
    offset += frameLength;
  }

  // One frame is a fragment, not an audio: the answer would be a wrong number
  // presented as a true one.
  if (frames < 2) return null;
  return seconds;
}

/** Whether these bytes look like the container the type or the name claims. */
function isOgg(buffer, mimeType, fileName) {
  const mime = String(mimeType || '').toLowerCase();
  const name = String(fileName || '').toLowerCase();
  if (buffer.length >= 4 && buffer.toString('ascii', 0, 4) === OGG_MAGIC) return true;
  return mime.indexOf('ogg') >= 0 || mime.indexOf('opus') >= 0
    || name.endsWith('.ogg') || name.endsWith('.opus') || name.endsWith('.oga');
}

function isMp4(buffer, mimeType, fileName) {
  const mime = String(mimeType || '').toLowerCase();
  const name = String(fileName || '').toLowerCase();
  if (buffer.length >= 8 && buffer.toString('ascii', 4, 8) === 'ftyp') return true;
  return mime.indexOf('mp4') >= 0 || mime.indexOf('m4a') >= 0 || mime.indexOf('aac') >= 0
    || name.endsWith('.m4a') || name.endsWith('.mp4') || name.endsWith('.aac');
}

/**
 * The length of these bytes in whole seconds, or null.
 *
 * `mimeType` and `fileName` are hints only: the container is decided by the
 * bytes first, because a name is what the sender chose and not what the file is.
 */
function durationSecondsOf(buffer, mimeType, fileName) {
  if (!buffer || buffer.length < 4) return null;

  try {
    let seconds = null;

    if (isOgg(buffer, mimeType, fileName)) seconds = oggDuration(buffer);
    else if (isMp4(buffer, mimeType, fileName)) seconds = mp4Duration(buffer);
    else seconds = mp3Duration(buffer);

    if (seconds === null || !isFinite(seconds) || seconds < 0) return null;
    // A duration past a day is not a voice note: it is a parse that went wrong.
    if (seconds > 24 * 3600) return null;
    return Math.round(seconds);
  } catch (err) {
    return null;
  }
}

/** Whole seconds as the phone reads them: 10 -> "0:10", 65 -> "1:05". */
function formatDuration(seconds) {
  const total = Math.max(0, Math.round(Number(seconds) || 0));
  const minutes = Math.floor(total / 60);
  const rest = total % 60;
  return `${minutes}:${rest < 10 ? '0' : ''}${rest}`;
}

module.exports = { durationSecondsOf, formatDuration };
