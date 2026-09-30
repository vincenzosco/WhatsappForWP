'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { createTranscoder, isOggOpus, isVideo, replaceExtension, VIDEO_COMPRESS_MIN_BYTES } =
  require('../ffmpeg');

const silent = () => {};

test('Ogg e Opus si riconoscono dal tipo o dal nome', () => {
  assert.ok(isOggOpus('audio/ogg', 'audio.ogg'));
  assert.ok(isOggOpus('audio/opus', null));
  assert.ok(isOggOpus(null, 'voce.opus'));
  assert.ok(!isOggOpus('audio/mpeg', 'canzone.mp3'));
  assert.ok(!isOggOpus('audio/mp4', 'voce.m4a'));
});

test('replaceExtension sostituisce solo l ultima estensione', () => {
  assert.strictEqual(replaceExtension('voce.ogg', '.mp3'), 'voce.mp3');
  assert.strictEqual(replaceExtension('audio', '.mp3'), 'audio.mp3');
  assert.strictEqual(replaceExtension('a.b.oga', '.mp3'), 'a.b.mp3');
});

test('un vocale Ogg diventa un MP3', async () => {
  const calls = [];
  const transcoder = createTranscoder({
    log: silent,
    run: async (args, input) => { calls.push({ args, input }); return Buffer.from('mp3-finto'); }
  });
  await transcoder.probe();
  const out = await transcoder.toPlayable(Buffer.from('ogg-finto'), 'audio/ogg', 'voce.ogg');

  assert.strictEqual(out.mimeType, 'audio/mpeg');
  assert.strictEqual(out.fileName, 'voce.mp3');
  assert.strictEqual(out.buffer.toString(), 'mp3-finto');
  assert.strictEqual(calls.length, 2);              // la prova e la conversione
  assert.ok(calls[1].args.includes('pipe:1'));
});

test('un audio che il telefono legge non si tocca', async () => {
  let ran = 0;
  const transcoder = createTranscoder({
    log: silent,
    run: async () => { ran++; return Buffer.alloc(0); }
  });
  await transcoder.probe();
  const before = ran;
  assert.strictEqual(await transcoder.toPlayable(Buffer.from('x'), 'audio/mp4', 'voce.m4a'), null);
  assert.strictEqual(ran, before);                  // nessuna chiamata in piu
});

test('senza ffmpeg si resta muti e non si prova per ogni vocale', async () => {
  let ran = 0;
  const transcoder = createTranscoder({
    log: silent,
    run: async () => { ran++; throw new Error('not found'); }
  });
  assert.strictEqual(await transcoder.probe(), false);
  assert.strictEqual(transcoder.isAvailable(), false);
  assert.strictEqual(await transcoder.toPlayable(Buffer.from('x'), 'audio/ogg', 'voce.ogg'), null);
  assert.strictEqual(ran, 1);                       // solo la prova
});

test('se ffmpeg fallisce si manda l originale', async () => {
  const transcoder = createTranscoder({
    log: silent,
    run: async (args) => {
      if (args[0] === '-version') return Buffer.alloc(0);
      throw new Error('boom');
    }
  });
  await transcoder.probe();
  assert.strictEqual(await transcoder.toPlayable(Buffer.from('x'), 'audio/ogg', 'voce.ogg'), null);
});

test('disabilitato non si prova nemmeno', async () => {
  let ran = 0;
  const transcoder = createTranscoder({
    enabled: false,
    log: silent,
    run: async () => { ran++; return Buffer.alloc(0); }
  });
  assert.strictEqual(await transcoder.probe(), false);
  assert.strictEqual(ran, 0);
});

test('isVideo riconosce il tipo e il nome, e non scambia un audio per un video', () => {
  assert.ok(isVideo('video/mp4', 'clip.mp4'));
  assert.ok(isVideo('video/quicktime', null));
  assert.ok(isVideo(null, 'clip.MOV'));
  assert.ok(!isVideo('audio/ogg', 'voce.ogg'));
  assert.ok(!isVideo('image/jpeg', 'foto.jpg'));
});

test('un video grande si rimpicciolisce', async () => {
  const calls = [];
  const transcoder = createTranscoder({
    log: silent,
    run: async (args, input) => {
      calls.push({ args, input });
      return args[0] === '-version' ? Buffer.alloc(0) : Buffer.alloc(1000);
    }
  });
  await transcoder.probe();

  const big = Buffer.alloc(VIDEO_COMPRESS_MIN_BYTES + 1);
  const out = await transcoder.toSmallerVideo(big, 'video/mp4', 'clip.mp4');

  assert.ok(out, 'il video viene convertito');
  assert.strictEqual(out.mimeType, 'video/mp4');
  assert.strictEqual(out.fileName, 'clip.mp4');
  assert.ok(out.buffer.length < big.length);
  assert.ok(calls.some((c) => c.args.includes('pipe:1')), 'ffmpeg legge e scrive dalle pipe');
});

test('un video gia piccolo non si tocca', async () => {
  let ran = 0;
  const transcoder = createTranscoder({
    log: silent,
    run: async (args) => { ran++; return Buffer.alloc(0); }
  });
  await transcoder.probe();
  const before = ran;

  const small = Buffer.alloc(VIDEO_COMPRESS_MIN_BYTES - 1);
  assert.strictEqual(await transcoder.toSmallerVideo(small, 'video/mp4', 'clip.mp4'), null);
  assert.strictEqual(ran, before, 'nessuna chiamata in piu');
});

test('una conversione che non riduce si scarta', async () => {
  const transcoder = createTranscoder({
    log: silent,
    run: async (args, input) => {
      if (args[0] === '-version') return Buffer.alloc(0);
      return Buffer.alloc(input.length + 1);
    }
  });
  await transcoder.probe();

  const big = Buffer.alloc(VIDEO_COMPRESS_MIN_BYTES + 1);
  assert.strictEqual(await transcoder.toSmallerVideo(big, 'video/mp4', 'clip.mp4'), null);
});

test('se ffmpeg fallisce su un video si manda l originale', async () => {
  const transcoder = createTranscoder({
    log: silent,
    run: async (args) => {
      if (args[0] === '-version') return Buffer.alloc(0);
      throw new Error('boom');
    }
  });
  await transcoder.probe();

  const big = Buffer.alloc(VIDEO_COMPRESS_MIN_BYTES + 1);
  assert.strictEqual(await transcoder.toSmallerVideo(big, 'video/mp4', 'clip.mp4'), null);
});

test('senza ffmpeg un video grande resta com e', async () => {
  const transcoder = createTranscoder({
    log: silent,
    run: async () => { throw new Error('not found'); }
  });
  await transcoder.probe();

  const big = Buffer.alloc(VIDEO_COMPRESS_MIN_BYTES + 1);
  assert.strictEqual(await transcoder.toSmallerVideo(big, 'video/mp4', 'clip.mp4'), null);
});
