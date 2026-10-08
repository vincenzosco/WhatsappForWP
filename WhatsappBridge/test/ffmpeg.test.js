'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { createTranscoder, isOggOpus, isVideo, replaceExtension, VIDEO_COMPRESS_MIN_BYTES } =
  require('../ffmpeg');

const silent = () => {};

// What a full ffmpeg prints for the three listings the probe reads. The words
// are on their own lines, exactly as ffmpeg writes them.
const LISTINGS = {
  '-encoders': 'Encoders:\n A..... libmp3lame          libmp3lame\n A..... libopus             libopus\n A..... libx264             libx264',
  '-muxers': 'Muxers:\n E  mp3           MP3\n E  ogg           Ogg\n E  mp4           MP4',
  '-decoders': 'Decoders:\n A....D opus                Opus'
};

/**
 * A run() that answers -version and the three listings like a full ffmpeg, and
 * hands every other call (the conversion) to `then`. The tests that need a
 * working transcoder use this; the ones about a broken or stripped binary
 * spell their own run out.
 */
function probeRun(then) {
  return async (args, input) => {
    const key = args[args.length - 1];
    if (key === '-version') return Buffer.alloc(0);
    if (LISTINGS[key] !== undefined) return Buffer.from(LISTINGS[key]);
    return then(args, input);
  };
}

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

test('un ffmpeg completo dichiara audio e video', async () => {
  const transcoder = createTranscoder({ log: silent, run: probeRun(async () => Buffer.alloc(0)) });
  assert.strictEqual(await transcoder.probe(), true);
  assert.deepStrictEqual(transcoder.capabilities(), { audio: true, opus: true, video: true });
});

test('un ffmpeg senza libmp3lame non e disponibile, e lo dice', async () => {
  const logs = [];
  const transcoder = createTranscoder({
    log: (level, message) => logs.push(level + ' ' + message),
    run: async (args) => {
      // Solo -version risponde: il binario parte ma non ha nessun pezzo utile.
      return Buffer.alloc(0);
    }
  });
  assert.strictEqual(await transcoder.probe(), false);
  assert.deepStrictEqual(transcoder.capabilities(), { audio: false, opus: false, video: false });
  assert.ok(logs.some((line) => /WARN/.test(line) && /libmp3lame/.test(line)),
    'il log deve dire cosa manca al binario');
});

test('un ffmpeg con audio ma senza video serve solo i vocali', async () => {
  const logs = [];
  const transcoder = createTranscoder({
    log: (level, message) => logs.push(level + ' ' + message),
    run: async (args) => {
      const key = args[args.length - 1];
      if (key === '-version') return Buffer.alloc(0);
      if (key === '-encoders') return Buffer.from('Encoders:\n A..... libmp3lame          libmp3lame\n A..... libopus             libopus');
      if (key === '-muxers') return Buffer.from('Muxers:\n E  mp3           MP3\n E  ogg           Ogg');
      if (key === '-decoders') return Buffer.from('Decoders:\n A....D opus                Opus');
      return Buffer.alloc(0);
    }
  });
  assert.strictEqual(await transcoder.probe(), true);
  assert.deepStrictEqual(transcoder.capabilities(), { audio: true, opus: true, video: false });
  assert.ok(logs.some((line) => /OK/.test(line) && /audio yes, voice yes, video no/.test(line)));
});

test('un vocale Ogg diventa un MP3', async () => {
  const calls = [];
  const transcoder = createTranscoder({
    log: silent,
    run: probeRun(async (args, input) => {
      calls.push({ args, input });
      return Buffer.from('mp3-finto');
    })
  });
  await transcoder.probe();
  const out = await transcoder.toPlayable(Buffer.from('ogg-finto'), 'audio/ogg', 'voce.ogg');

  assert.strictEqual(out.mimeType, 'audio/mpeg');
  assert.strictEqual(out.fileName, 'voce.mp3');
  assert.strictEqual(out.buffer.toString(), 'mp3-finto');
  const transcodes = calls.filter((c) => c.args.includes('pipe:1'));
  assert.strictEqual(transcodes.length, 1);          // la prova non e' una conversione
  assert.ok(transcodes[0].args.includes('pipe:1'));
});

test('un audio che il telefono legge non si tocca', async () => {
  let ran = 0;
  const transcoder = createTranscoder({
    log: silent,
    run: probeRun(async () => { ran++; return Buffer.alloc(0); })
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
  assert.deepStrictEqual(transcoder.capabilities(), { audio: false, opus: false, video: false });
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
    run: probeRun(async (args) => {
      calls.push({ args });
      return Buffer.alloc(1000);
    })
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

test('un ffmpeg solo audio non prova a rimpicciolire un video', async () => {
  let transcodes = 0;
  const transcoder = createTranscoder({
    log: silent,
    run: async (args) => {
      const key = args[args.length - 1];
      if (key === '-version') return Buffer.alloc(0);
      if (key === '-encoders') return Buffer.from('Encoders:\n A..... libmp3lame          libmp3lame');
      if (key === '-muxers') return Buffer.from('Muxers:\n E  mp3           MP3');
      if (key === '-decoders') return Buffer.from('Decoders:\n A....D opus                Opus');
      transcodes++;
      return Buffer.alloc(1000);
    }
  });
  await transcoder.probe();

  const big = Buffer.alloc(VIDEO_COMPRESS_MIN_BYTES + 1);
  assert.strictEqual(await transcoder.toSmallerVideo(big, 'video/mp4', 'clip.mp4'), null);
  assert.strictEqual(transcodes, 0, 'senza libx264 non si tenta la conversione');
});

test('un video gia piccolo non si tocca', async () => {
  let ran = 0;
  const transcoder = createTranscoder({
    log: silent,
    run: probeRun(async () => { ran++; return Buffer.alloc(0); })
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
    run: probeRun(async (args, input) => Buffer.alloc(input.length + 1))
  });
  await transcoder.probe();

  const big = Buffer.alloc(VIDEO_COMPRESS_MIN_BYTES + 1);
  assert.strictEqual(await transcoder.toSmallerVideo(big, 'video/mp4', 'clip.mp4'), null);
});

test('se ffmpeg fallisce su un video si manda l originale', async () => {
  const transcoder = createTranscoder({
    log: silent,
    run: probeRun(async () => { throw new Error('boom'); })
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

test('un vocale registrato dal telefono diventa un Ogg/Opus', async () => {
  const calls = [];
  const transcoder = createTranscoder({
    log: silent,
    run: probeRun(async (args, input) => {
      calls.push({ args, input });
      return Buffer.from('ogg-finto');
    })
  });
  await transcoder.probe();
  const out = await transcoder.toVoiceNote(Buffer.from('m4a-finto'), 'audio/mp4', 'voce.m4a');

  assert.ok(out, 'un M4A registrato va convertito: WhatsApp non accetta altro');
  assert.strictEqual(out.mimeType, 'audio/ogg');
  assert.strictEqual(out.fileName, 'voce.ogg');
  assert.strictEqual(out.buffer.toString(), 'ogg-finto');
  const transcodes = calls.filter((c) => c.args.includes('pipe:1'));
  assert.strictEqual(transcodes.length, 1);
  assert.ok(transcodes[0].args.includes('libopus'), 'la conversione usa l encoder Opus');
});

test('un vocale gia Ogg/Opus non si riconverte', async () => {
  let ran = 0;
  const transcoder = createTranscoder({
    log: silent,
    run: probeRun(async () => { ran++; return Buffer.alloc(0); })
  });
  await transcoder.probe();
  const before = ran;
  assert.strictEqual(await transcoder.toVoiceNote(Buffer.from('x'), 'audio/ogg', 'voce.ogg'), null);
  assert.strictEqual(ran, before, 'un vocale gia pronto non passa da ffmpeg');
});

test('un ffmpeg senza libopus non prova a fare un vocale', async () => {
  let transcodes = 0;
  const transcoder = createTranscoder({
    log: silent,
    run: async (args) => {
      const key = args[args.length - 1];
      if (key === '-version') return Buffer.alloc(0);
      if (key === '-encoders') return Buffer.from('Encoders:\n A..... libmp3lame          libmp3lame');
      if (key === '-muxers') return Buffer.from('Muxers:\n E  mp3           MP3');
      if (key === '-decoders') return Buffer.from('Decoders:\n A....D opus                Opus');
      transcodes++;
      return Buffer.alloc(1000);
    }
  });
  await transcoder.probe();
  assert.strictEqual(transcoder.capabilities().opus, false);
  assert.strictEqual(await transcoder.toVoiceNote(Buffer.from('x'), 'audio/mp4', 'voce.m4a'), null);
  assert.strictEqual(transcodes, 0, 'senza libopus non si tenta la conversione');
});

test('se ffmpeg fallisce su un vocale in uscita si manda l originale', async () => {
  const transcoder = createTranscoder({
    log: silent,
    run: probeRun(async () => { throw new Error('boom'); })
  });
  await transcoder.probe();
  assert.strictEqual(await transcoder.toVoiceNote(Buffer.from('x'), 'audio/mp4', 'voce.m4a'), null);
});
