#!/usr/bin/env node
/**
 * Translation kit: turns a plain text file written by somebody who does not
 * program into the embedded resource the app loads for that language.
 *
 * Usage:
 *   node tools/translation-kit.js export
 *   node tools/translation-kit.js build <file> <culture> [--root <dir>]
 *   node tools/translation-kit.js status [--root <dir>]
 *
 * The translator's file is one string per line: `Key = translation`. Lines
 * that are empty or start with # are notes and are ignored. Untranslated
 * keys stay English at runtime, because the lookup falls back to the
 * neutral catalog on its own.
 */
'use strict';

const fs = require('fs');
const path = require('path');

const DEFAULT_ROOT = path.resolve(__dirname, '..');

/**
 * Common culture names for the status line-up. A culture nobody named here
 * is shown under its own code.
 */
const LANGUAGE_LABELS = {
  'ar': 'العربية',
  'cs': 'Čeština',
  'da': 'Dansk',
  'de': 'Deutsch',
  'el': 'Ελληνικά',
  'en': 'English',
  'es': 'Español',
  'fi': 'Suomi',
  'fr': 'Français',
  'hu': 'Magyar',
  'it': 'Italiano',
  'ja': '日本語',
  'ko': '한국어',
  'nl': 'Nederlands',
  'no': 'Norsk',
  'pl': 'Polski',
  'pt': 'Português',
  'ro': 'Română',
  'ru': 'Русский',
  'sv': 'Svenska',
  'tr': 'Türkçe',
  'uk': 'Українська',
  'zh': '中文'
};

const NEUTRAL_FILE = 'Resources.resx';

function stringsDir(root) {
  return path.join(root, 'WhatsappApp.Core', 'Strings');
}

function coreProject(root) {
  return path.join(root, 'WhatsappApp.Core', 'WhatsappApp.Core.csproj');
}

/** Every `<data>` entry of a .resx, in file order. */
function parseCatalog(xml) {
  const entries = [];
  const pattern = /<data\s+name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g;
  let match;
  while ((match = pattern.exec(xml)) !== null) {
    entries.push({ key: match[1], value: unescapeXml(match[2].trim()) });
  }
  return entries;
}

function unescapeXml(text) {
  return text
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&');
}

function escapeXml(text) {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

/**
 * The translator's file, already checked for shape only (each line either a
 * note or `Key = translation`). Unknown keys and placeholder problems are
 * the checker's job, since they need the catalog beside them.
 */
function parseTranslationFile(text) {
  const entries = [];
  const errors = [];
  const lines = text.split(/\r\n|\r|\n/);

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (line.trim().length === 0 || line.trim().startsWith('#')) continue;

    const at = line.indexOf('=');
    if (at <= 0) {
      errors.push({ line: i + 1, message: 'line ' + (i + 1) + ': expected "Key = translation"' });
      continue;
    }

    const key = line.slice(0, at).trim();
    let value = line.slice(at + 1);
    if (value.charAt(0) === ' ') value = value.slice(1);

    if (key.length === 0) {
      errors.push({ line: i + 1, message: 'line ' + (i + 1) + ': there is no key before the equal sign' });
      continue;
    }

    entries.push({ key: key, value: value, line: i + 1 });
  }

  return { entries: entries, errors: errors };
}

/** The `{0}` slots of a value: the ones the runtime fills in. */
function placeholders(text) {
  const found = [];
  const pattern = /\{(\d+)\}/g;
  let match;
  while ((match = pattern.exec(text)) !== null) found.push(match[1]);
  return found.sort();
}

/**
 * What the catalog cannot answer from shape alone: keys that do not exist
 * and values that lose a format slot. Both corrupt a string in the app, so
 * both refuse the build.
 */
function checkEntries(entries, catalog) {
  const errors = [];
  const byKey = new Map();
  for (const entry of catalog) byKey.set(entry.key, entry);

  const declared = new Set();
  for (const entry of entries) {
    if (declared.has(entry.key)) {
      errors.push({ line: entry.line, message: 'line ' + entry.line + ': the key ' + entry.key + ' is written twice' });
      continue;
    }
    declared.add(entry.key);

    const source = byKey.get(entry.key);
    if (!source) {
      errors.push({ line: entry.line, message: 'line ' + entry.line + ': there is no key named ' + entry.key + ' in the app' });
      continue;
    }

    const wanted = placeholders(source.value).join(' ');
    const got = placeholders(entry.value).join(' ');
    if (wanted !== got) {
      errors.push({
        line: entry.line,
        message: 'line ' + entry.line + ': ' + entry.key + ' keeps the slots {' + wanted +
          '} but the translation has {' + got + '}'
      });
    }
  }

  return errors;
}

/** The template the translator copies: every key with its English value. */
function templateFile(catalog) {
  const lines = [
    '# WhatsApp for Windows Phone - translation template',
    '# Put your language where the English is, one line at a time.',
    '# A line left alone stays English in the app - translate what you can.',
    '# Do not change the part before the equal sign.',
    ''
  ];
  for (const entry of catalog) lines.push(entry.key + ' = ' + entry.value);
  return lines.join('\n') + '\n';
}

/** The header every generated .resx carries, copied from the it-IT one. */
function resxHeader(root) {
  const dir = stringsDir(root);
  const candidates = ['Resources.it-IT.resx', NEUTRAL_FILE];
  for (const name of candidates) {
    const file = path.join(dir, name);
    if (!fs.existsSync(file)) continue;
    const xml = fs.readFileSync(file, 'utf8');
    const at = xml.indexOf('  <data ');
    return at > 0 ? xml.slice(0, at) : '';
  }
  return '<?xml version="1.0" encoding="utf-8"?>\n<root>\n';
}

/** The .resx for one language: only the keys the translator wrote. */
function generateResx(entries, header) {
  const blocks = entries.map((entry) =>
    '  <data name="' + escapeXml(entry.key) + '" xml:space="preserve">\n'
    + '    <value>' + escapeXml(entry.value) + '</value>\n'
    + '  </data>');
  return header + blocks.join('\n') + '\n</root>\n';
}

/**
 * The csproj line that makes the resource a satellite the phone can load.
 * Idempotent: a language is registered once, however often the build runs.
 */
function applyRegistration(csproj, culture) {
  const include = '<EmbeddedResource Include="Strings\\Resources.' + culture + '.resx">';
  if (csproj.indexOf(include) >= 0) return { text: csproj, changed: false };

  const block = '    ' + include + '\n'
    + '      <Culture>' + culture + '</Culture>\n'
    + '      <WithCulture>true</WithCulture>\n'
    + '    </EmbeddedResource>';

  const it = csproj.indexOf('<EmbeddedResource Include="Strings\\Resources.it-IT.resx">');
  if (it < 0) {
    return { text: null, changed: false, error: 'the it-IT registration is missing from the project file' };
  }
  const close = csproj.indexOf('</EmbeddedResource>', it);
  const at = close + '</EmbeddedResource>'.length;
  return { text: csproj.slice(0, at) + '\n' + block + csproj.slice(at), changed: true };
}

/** A culture the phone can resolve: `fr`, `fr-FR`, `zh-Hans`. */
function cultureIsSane(culture) {
  return /^[a-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}$/.test(culture);
}

function round(part, whole) {
  return whole === 0 ? 0 : Math.round((part / whole) * 100);
}

/** One translation file to one resource: the whole build. */
function build(options) {
  const root = options.root || DEFAULT_ROOT;
  const culture = options.culture;

  if (!cultureIsSane(culture)) {
    return {
      written: false,
      errors: [{ line: 0, message: 'the language "' + culture + '" should read like "fr" or "fr-FR"' }],
      translated: 0,
      total: 0,
      coverage: 0
    };
  }

  const catalog = parseCatalog(fs.readFileSync(path.join(stringsDir(root), NEUTRAL_FILE), 'utf8'));
  const parsed = parseTranslationFile(options.text);
  const errors = parsed.errors.concat(checkEntries(parsed.entries, catalog));
  if (errors.length > 0) {
    return { written: false, errors: errors, translated: 0, total: catalog.length, coverage: 0 };
  }

  const kept = new Map();
  for (const entry of parsed.entries) kept.set(entry.key, entry.value);
  const ordered = catalog
    .filter((entry) => kept.has(entry.key))
    .map((entry) => ({ key: entry.key, value: kept.get(entry.key) }));

  fs.writeFileSync(
    path.join(stringsDir(root), 'Resources.' + culture + '.resx'),
    generateResx(ordered, resxHeader(root)));

  const registration = applyRegistration(fs.readFileSync(coreProject(root), 'utf8'), culture);
  if (registration.error) {
    return { written: false, errors: [{ line: 0, message: registration.error }], translated: 0, total: catalog.length, coverage: 0 };
  }
  if (registration.changed) fs.writeFileSync(coreProject(root), registration.text);

  return {
    written: true,
    errors: [],
    translated: ordered.length,
    total: catalog.length,
    coverage: round(ordered.length, catalog.length)
  };
}

function labelFor(culture) {
  const base = culture.split('-')[0];
  return LANGUAGE_LABELS[culture] || LANGUAGE_LABELS[base] || culture;
}

/** Every language of the app with what it has translated so far. */
function languageStatus(root) {
  const dir = stringsDir(root);
  const catalog = parseCatalog(fs.readFileSync(path.join(dir, NEUTRAL_FILE), 'utf8'));
  const keys = new Set(catalog.map((entry) => entry.key));

  const languages = [];
  const pattern = /^Resources\.([a-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,2})\.resx$/;
  for (const name of fs.readdirSync(dir).sort()) {
    const match = pattern.exec(name);
    if (!match) continue;

    const culture = match[1];
    const entries = parseCatalog(fs.readFileSync(path.join(dir, name), 'utf8'));
    const translated = entries.filter((entry) => keys.has(entry.key)).length;
    languages.push({
      culture: culture,
      label: labelFor(culture),
      translated: translated,
      total: catalog.length,
      coverage: round(translated, catalog.length)
    });
  }

  return { neutral: catalog.length, languages: languages };
}

/** The file the site reads for its language table. */
function writeStatus(root, status) {
  const site = path.join(root, 'site');
  if (!fs.existsSync(site)) fs.mkdirSync(site, { recursive: true });

  const out = path.join(site, 'status.json');
  fs.writeFileSync(out, JSON.stringify({
    neutral: status.neutral,
    languages: status.languages
  }, null, 2) + '\n');
  return out;
}

function readArguments(argv) {
  const options = { root: DEFAULT_ROOT, extra: [] };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--root' && i + 1 < argv.length) {
      options.root = path.resolve(argv[i + 1]);
      i++;
    } else {
      options.extra.push(argv[i]);
    }
  }
  return options;
}

function main(argv) {
  const args = readArguments(argv);
  const command = args.extra[0];

  if (command === 'export') {
    const root = args.root;
    const catalog = parseCatalog(fs.readFileSync(path.join(stringsDir(root), NEUTRAL_FILE), 'utf8'));
    const out = path.join(root, 'traduzioni', 'MODELLO.txt');
    fs.writeFileSync(out, templateFile(catalog));
    process.stdout.write('written ' + catalog.length + ' keys to ' + out + '\n');
    return 0;
  }

  if (command === 'build') {
    const file = args.extra[1];
    const culture = args.extra[2];
    if (!file || !culture) {
      process.stderr.write('use: build <file> <culture>\n');
      return 2;
    }

    const result = build({
      root: args.root,
      culture: culture,
      text: fs.readFileSync(path.resolve(file), 'utf8')
    });

    for (const error of result.errors) process.stderr.write(error.message + '\n');
    if (result.written) {
      process.stdout.write(culture + ': ' + result.translated + ' of ' + result.total +
        ' strings (' + result.coverage + '%)\n');
      return 0;
    }
    return 1;
  }

  if (command === 'status') {
    const status = languageStatus(args.root);
    process.stdout.write(neutralLine(status) + '\n');
    for (const language of status.languages) {
      process.stdout.write('  ' + language.culture + '  ' + language.label + '  ' +
        language.translated + '/' + language.total + '  ' + language.coverage + '%\n');
    }
    const out = writeStatus(args.root, status);
    process.stdout.write('written ' + out + '\n');
    return 0;
  }

  process.stderr.write('use: export | build <file> <culture> | status\n');
  return 2;
}

function neutralLine(status) {
  return status.neutral + ' strings in the app';
}

module.exports = {
  parseCatalog: parseCatalog,
  parseTranslationFile: parseTranslationFile,
  checkEntries: checkEntries,
  templateFile: templateFile,
  generateResx: generateResx,
  applyRegistration: applyRegistration,
  cultureIsSane: cultureIsSane,
  languageStatus: languageStatus,
  writeStatus: writeStatus,
  build: build,
  main: main
};

if (require.main === module) {
  process.exit(main(process.argv.slice(2)));
}