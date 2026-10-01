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
 *     limiti che il sistema accetta (<=1024x1024 px, <=200 KB) e avere almeno
 *     la misura minima dell'icona (200x200);
 *  3. il modello TileWide310x150IconWithBadge non esiste su WP8.1: se compare,
 *     e' un errore di compilazione che vale la pena spiegare qui.
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
const ASSETS_DIR = path.join(ROOT, 'WhatsappApp', 'Assets');

const IMAGE_PREFIX = 'ms-appx:///Assets/';
const MIN_SIDE = 200;
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
  if (asset.width < MIN_SIDE || asset.height < MIN_SIDE) {
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

module.exports = { pngSize, declaredImages, bindingProblems, assetProblems, stripComments };
