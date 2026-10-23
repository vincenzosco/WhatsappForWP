#!/usr/bin/env node
/**
 * tools/check-tile.js
 *
 * Guard for the live tile of the WP8.1 app.
 *
 * Perche' esiste: la tile non disegnava l'icona e non lo diceva a nessuno. Un
 * modello di tile NON prende l'icona dal manifest: vuole un <image src="..."> nel
 * payload, che punti a un'icona dedicata (Special tile templates, passo 3). Con
 * src vuoto la tile resta senza icona e non solleva nessuna eccezione: un guasto
 * che nessun log mostra.
 *
 * Regole:
 *  1. il codice della tile deve impostare src su ogni image che prende da un
 *     modello;
 *  2. l'immagine referenziata deve esistere in Assets/, essere un PNG dentro i
 *     limiti che il sistema accetta (<=1024 px per lato, <=200 KB) e avere
 *     almeno la misura del modello che la disegna: 200x200 per l'icona
 *     quadrata, 310x150 per la tile larga, che ha una proporzione sua;
 *  3. il modello TileWide310x150IconWithBadge non esiste su WP8.1: se compare,
 *     e' un errore di compilazione che vale la pena spiegare qui;
 *  4. la coda delle notifiche della tile deve restare accesa. E' quello che fa
 *     ruotare la tile fra i messaggi in attesa: con la coda spenta una sola
 *     notifica resta in vetrina e il messaggio cambia solo quando ne arriva un
 *     altro - il difetto visto sul telefono.
 *  5. i pixel che il decoder restituisce si MISURANO e si portano alla misura
 *     del disegno, non si prendono per quelli chiesti. Il 23 ottobre 2026 il log
 *     del telefono si e' fermato con `IndexOutOfRangeException` in
 *     `TileCanvas.Draw`: `Draw` legge `width` pixel per riga, e il chiamante gli
 *     passava i byte del decoder dichiarandoli della misura chiesta. Qui il
 *     decoder risponde al crop con la misura del crop (96x96) e non con quella
 *     chiesta (336x336), quindi la lettura usciva dal buffer.
 *
 * Usage:
 *   node tools/check-tile.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { stripComments } = require('./csharp');

const ROOT = path.resolve(__dirname, '..');
const SERVICE = path.join(ROOT, 'WhatsappApp', 'Services', 'NotificationService.cs');
const TILE_IMAGE = path.join(ROOT, 'WhatsappApp', 'Services', 'TileImage.cs');
const ASSETS_DIR = path.join(ROOT, 'WhatsappApp', 'Assets');

const IMAGE_PREFIX = 'ms-appx:///Assets/';
const MIN_SIDE = 200;
const WIDE_MIN_WIDTH = 310;
const WIDE_MIN_HEIGHT = 150;
const WIDE_RATIO = 310 / 150;
const WIDE_RATIO_SLACK = 0.25;
const MAX_SIDE = 1024;
const MAX_BYTES = 200 * 1024;
const ABSENT_ON_WP81 = 'TileWide310x150IconWithBadge';

/** Dimensioni dal chunk IHDR del PNG. null se i byte non sono un PNG. */
function pngSize(bytes) {
  if (!bytes || bytes.length < 24) return null;
  if (bytes[0] !== 0x89 || bytes[1] !== 0x50 || bytes[2] !== 0x4e || bytes[3] !== 0x47) return null;
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

/** Le immagini che la tile dichiara, come percorsi di pacchetto. */
function declaredImages(source) {
  const found = [];
  const pattern = new RegExp('"(' + IMAGE_PREFIX.replace(/[/.]/g, '\\$&') + '[^"]+)"', 'g');
  for (const m of source.matchAll(pattern)) found.push(m[1]);
  return found;
}

/**
 * Problemi del payload. Ogni image presa da un modello deve ricevere il suo
 * src: un'image con src vuoto e' una tile senza icona, e nessuno lo dice.
 */
function bindingProblems(source) {
  const problems = [];
  const code = stripComments(source);

  if (code.indexOf(ABSENT_ON_WP81) >= 0) {
    problems.push(`NotificationService.cs: ${ABSENT_ON_WP81} non esiste su WP8.1 ` +
      '(CS0117): la tile larga resta quella del manifest');
  }

  if (/EnableNotificationQueue\s*\(\s*false\s*\)/.test(code)) {
    problems.push('NotificationService.cs: EnableNotificationQueue(false) - a tile with no ' +
      'queue shows one conversation until another message arrives, it does not rotate');
  }

  const images = (code.match(/SelectSingleNode\([^;]*image"/g) || []).length;
  const srcs = (code.match(/SetAttribute\("src"/g) || []).length;

  if (srcs === 0) {
    problems.push('NotificationService.cs: no binding sets src: the iconic template does ' +
      'not take the icon from the manifest, so the tile renders without one and without ' +
      'an exception');
  } else if (images !== srcs) {
    problems.push(`NotificationService.cs: ${images} image(s) read from a template, ` +
      `${srcs} src set: an image that goes into the payload needs its own src`);
  }

  if (srcs > 0 && declaredImages(code).length === 0) {
    problems.push(`NotificationService.cs: the tile references no image under ${IMAGE_PREFIX}`);
  }

  return problems;
}

/**
 * Problemi della decodifica: la misura del buffer e' quella che conta.
 *
 * Perche' esiste: `TileCanvas.Draw` legge `width` pixel per riga, quindi il
 * buffer del disegno deve essere almeno `width * height * 4`. Il decoder invece
 * risponde alla richiesta di scala con il crop alla sua misura, e chi lo chiama
 * non puo' saperlo se non misura: e' il difetto del 23 ottobre 2026, un
 * `IndexOutOfRangeException` dentro `Draw` con un buffer da 96x96 disegnato come
 * 336x336. Le due regole qui sotto sono le due meta' della risposta: misurare
 * (`SideOf`) e ridimensionare (`ResizeSquare`).
 */
function decodeProblems(source) {
  const problems = [];
  const code = stripComments(source);

  if (!/DetachPixelData\s*\(/.test(code)) {
    problems.push('WhatsappApp/Services/TileImage.cs: no DetachPixelData - the card has no pixels ' +
      'to compose');
    return problems;
  }

  if (!/TileCanvas\.SideOf\s*\(/.test(code)) {
    problems.push('WhatsappApp/Services/TileImage.cs: the decoded buffer is never measured - ' +
      'the side it was asked for is not always the side it has (the IndexOutOfRangeException ' +
      'in TileCanvas.Draw)');
  }

  if (!/TileCanvas\.ResizeSquare\s*\(/.test(code)) {
    problems.push('WhatsappApp/Services/TileImage.cs: the decoded buffer is never scaled to the ' +
      'side the card is drawn at - TileCanvas.Draw then reads past the end of it');
  }

  return problems;
}

/** Problemi di un asset: misura minima, limiti del sistema, esistenza. */
function assetProblems(asset) {
  const problems = [];
  const where = 'WhatsappApp/' + asset.name;
  // Solo `false` significa mancante: un descrittore senza il campo descrive una
  // misura, e la misura e' quello che questa funzione controlla.
  if (asset.exists === false) {
    problems.push(`${where}: does not exist`);
    return problems;
  }
  if (asset.width == null) {
    problems.push(`${where}: not a PNG`);
    return problems;
  }
  // Una tile larga non e' un'icona quadrata: il modello 310x150 disegna
  // l'immagine cosi' com'e', e un'immagine quadrata viene scalata e ritagliata.
  if (asset.width > asset.height) {
    if (asset.width < WIDE_MIN_WIDTH || asset.height < WIDE_MIN_HEIGHT) {
      problems.push(`${where}: ${asset.width}x${asset.height}, the wide tile needs at ` +
        `least ${WIDE_MIN_WIDTH}x${WIDE_MIN_HEIGHT}`);
    }
    const ratio = asset.width / asset.height;
    if (Math.abs(ratio - WIDE_RATIO) > WIDE_RATIO_SLACK) {
      problems.push(`${where}: ${asset.width}x${asset.height}, the wide tile is ` +
        `${WIDE_MIN_WIDTH}x${WIDE_MIN_HEIGHT} (about ${WIDE_RATIO.toFixed(2)}:1) and ` +
        `this is ${ratio.toFixed(2)}:1`);
    }
  } else if (asset.width < MIN_SIDE || asset.height < MIN_SIDE) {
    problems.push(`${where}: ${asset.width}x${asset.height}, the iconic template needs at ` +
      `least ${MIN_SIDE}x${MIN_SIDE}`);
  }
  if (asset.width > MAX_SIDE || asset.height > MAX_SIDE) {
    problems.push(`${where}: ${asset.width}x${asset.height} is over the ${MAX_SIDE}x${MAX_SIDE} ` +
      'the system accepts for a tile image');
  }
  if (asset.bytes > MAX_BYTES) {
    problems.push(`${where}: ${Math.round(asset.bytes / 1024)} KB is over the 200 KB the ` +
      'system accepts for a tile image');
  }
  return problems;
}

/** Legge un asset dichiarato dalla tile e ne descrive la misura. */
function readAsset(packagePath) {
  const name = packagePath.slice(IMAGE_PREFIX.length).replace(/\//g, path.sep);
  const file = path.join(ASSETS_DIR, name);
  const exists = fs.existsSync(file);
  if (!exists) return { name, exists: false, width: null, height: null, bytes: 0 };
  const bytes = fs.readFileSync(file);
  const size = pngSize(bytes);
  return {
    name,
    exists: true,
    width: size ? size.width : null,
    height: size ? size.height : null,
    bytes: bytes.length
  };
}

function main() {
  const source = fs.readFileSync(SERVICE, 'utf8');
  const problems = bindingProblems(source);

  problems.push(...decodeProblems(fs.readFileSync(TILE_IMAGE, 'utf8')));

  const images = declaredImages(source);
  if (images.length === 0) {
    problems.push(`NotificationService.cs: the tile declares no image under ${IMAGE_PREFIX}`);
  }

  for (const image of images) {
    problems.push(...assetProblems(readAsset(image)));
    // La sorgente dichiara il percorso senza qualificatore ("Assets\TileIcon.png"):
    // MRT aggiunge .scale-240 quando il pacchetto viene risolto sul telefono,
    // quindi il file qualificato deve esserci, altrimenti l'immagine non si trova.
    const qualified = image.replace(/\.png$/, '.scale-240.png');
    if (qualified !== image) problems.push(...assetProblems(readAsset(qualified)));
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} tile problem(s).`);
    process.exit(1);
  }

  const first = readAsset(images[0]);
  console.log(`OK: ${images[0]} (${first.width}x${first.height}, ` +
    `${Math.round(first.bytes / 1024)} KB), src on every binding.`);
}

if (require.main === module) main();

module.exports = { pngSize, declaredImages, bindingProblems, decodeProblems, assetProblems, stripComments };
