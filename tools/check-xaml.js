#!/usr/bin/env node
/**
 * tools/check-xaml.js
 *
 * Guard for well-formed XAML: every element that opens is closed, in order,
 * every attribute value is quoted, and nothing in the text or in a value is a
 * bare `&` or a `<` that starts markup nobody finished.
 *
 * Why it exists: a styles block moved into `App.xaml` lost its outer `</Grid>`.
 * MSBuild would have refused the file (XMC0410, and the app does not start at
 * all), but MSBuild runs on the Windows machine: the fast gate ran, printed OK,
 * and a file that cannot be parsed nearly reached the repository. The gate has to
 * answer that question here, on any machine, in the same second.
 *
 * The parser is deliberately small: it walks the document once, keeps a stack of
 * open elements and reports the first structural error of each kind. It is not a
 * XAML validator - it does not know the controls, the namespaces or the markup
 * extensions - and it is not meant to be one.
 *
 * Usage:
 * node tools/check-xaml.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');

/** The first offset of every line, so a character offset becomes a line number. */
function lineStarts(text) {
  const starts = [0];
  for (let i = 0; i < text.length; i++) {
    if (text[i] === '\n') starts.push(i + 1);
  }
  return starts;
}

function lineAt(starts, offset) {
  let low = 0;
  let high = starts.length - 1;
  while (low < high) {
    const mid = (low + high + 1) >> 1;
    if (starts[mid] <= offset) low = mid;
    else high = mid - 1;
  }
  return low + 1;
}

/** `&` is only legal as one of the five entities or as a numeric reference. */
const BARE_AMPERSAND = /&(?![A-Za-z][A-Za-z0-9]*;|#\d+;|#x[0-9A-Fa-f]+;)/g;

function textProblems(text, from, to, where, starts, problems) {
  const slice = text.slice(from, to);
  BARE_AMPERSAND.lastIndex = 0;
  let match;
  while ((match = BARE_AMPERSAND.exec(slice)) !== null) {
    problems.push(`${where(from + match.index)}: a bare & in the text, write &amp;`);
  }
}

function xamlProblems(text, file) {
  const problems = [];
  const starts = lineStarts(text);
  const where = (offset) => `${file}:${lineAt(starts, offset)}`;
  const stack = [];
  const nameToken = /[A-Za-z_:][-A-Za-z0-9_:.]*/y;

  let i = 0;
  while (i < text.length) {
    const lt = text.indexOf('<', i);

    if (lt < 0) {
      textProblems(text, i, text.length, where, starts, problems);
      break;
    }
    textProblems(text, i, lt, where, starts, problems);
    i = lt;

    if (text.startsWith('<!--', i)) {
      const end = text.indexOf('-->', i + 4);
      if (end < 0) {
        problems.push(`${where(i)}: a comment opens and never closes`);
        return problems;
      }
      i = end + 3;
      continue;
    }

    if (text.startsWith('<![CDATA[', i)) {
      const end = text.indexOf(']]>', i + 9);
      if (end < 0) {
        problems.push(`${where(i)}: CDATA opens and never closes`);
        return problems;
      }
      i = end + 3;
      continue;
    }

    if (text.startsWith('<?', i)) {
      const end = text.indexOf('?>', i + 2);
      if (end < 0) {
        problems.push(`${where(i)}: a processing instruction opens and never closes`);
        return problems;
      }
      i = end + 2;
      continue;
    }

    if (text.startsWith('<!', i)) {
      const end = text.indexOf('>', i + 2);
      if (end < 0) {
        problems.push(`${where(i)}: a declaration opens and never closes`);
        return problems;
      }
      i = end + 1;
      continue;
    }

    const closing = text[i + 1] === '/';
    let at = i + (closing ? 2 : 1);
    nameToken.lastIndex = at;
    const nameMatch = nameToken.exec(text);
    if (nameMatch === null) {
      problems.push(`${where(i)}: '<' is not followed by an element name`);
      i = at;
      continue;
    }

    const name = nameMatch[0];
    at = nameToken.lastIndex;

    if (closing) {
      while (at < text.length && /\s/.test(text[at])) at++;
      if (text[at] !== '>') {
        problems.push(`${where(i)}: </${name}> is not closed with >`);
        i = at;
        continue;
      }

      if (stack.length === 0) {
        problems.push(`${where(i)}: </${name}> closes nothing`);
      } else if (stack[stack.length - 1].name !== name) {
        const open = stack[stack.length - 1];
        problems.push(`${where(i)}: </${name}> closes <${open.name}>, opened at line `
          + `${lineAt(starts, open.offset)}`);
        const matching = stack.map((entry) => entry.name).lastIndexOf(name);
        if (matching >= 0) stack.length = matching;
      } else {
        stack.pop();
      }

      i = at + 1;
      continue;
    }

    let selfClosing = false;
    let attribute = /[A-Za-z_:][-A-Za-z0-9_:.]*/y;
    for (;;) {
      while (at < text.length && /\s/.test(text[at])) at++;

      if (at >= text.length) {
        problems.push(`${where(i)}: <${name}> never ends`);
        return problems;
      }
      if (text[at] === '>') {
        at++;
        break;
      }
      if (text[at] === '/' && text[at + 1] === '>') {
        selfClosing = true;
        at += 2;
        break;
      }

      attribute.lastIndex = at;
      const attributeMatch = attribute.exec(text);
      if (attributeMatch === null) {
        problems.push(`${where(at)}: <${name}> carries something that is not an attribute`);
        return problems;
      }
      const attributeName = attributeMatch[0];
      at = attribute.lastIndex;

      let cursor = at;
      while (cursor < text.length && /\s/.test(text[cursor])) cursor++;
      if (text[cursor] !== '=') continue;

      cursor++;
      while (cursor < text.length && /\s/.test(text[cursor])) cursor++;
      const quote = text[cursor];
      if (quote !== '"' && quote !== '\'') {
        problems.push(`${where(cursor)}: ${attributeName} on <${name}> has no quotes`);
        return problems;
      }

      const end = text.indexOf(quote, cursor + 1);
      if (end < 0) {
        problems.push(`${where(cursor)}: the value of ${attributeName} on <${name}> never closes`);
        return problems;
      }

      const value = text.slice(cursor + 1, end);
      BARE_AMPERSAND.lastIndex = 0;
      const ampersand = BARE_AMPERSAND.exec(value);
      if (ampersand !== null) {
        problems.push(`${where(cursor + 1 + ampersand.index)}: ${attributeName} on <${name}> `
          + 'carries a bare &, write &amp;');
      }
      const angle = value.indexOf('<');
      if (angle >= 0) {
        problems.push(`${where(cursor + 1 + angle)}: ${attributeName} on <${name}> carries a <`);
      }

      at = end + 1;
    }

    if (!selfClosing) stack.push({ name, offset: i });
    i = at;
  }

  for (const open of stack) {
    problems.push(`${where(open.offset)}: <${open.name}> opens and is never closed`);
  }

  return problems;
}

/** Every `x:Key` of a document, in the order it appears. */
function keysOf(text) {
  const found = [];
  const token = /x:Key="([^"]+)"/g;
  let match;
  while ((match = token.exec(text)) !== null) found.push(match[1]);
  return found;
}

/**
 * The resource keys the pages must not hide: a page's own dictionary wins over
 * the application's, so the same `x:Key` in a page replaces the shared entry in
 * silence - the page draws its copy and nobody sees the difference until the
 * shared one is changed and this screen ignores it.
 */
function resourceKeyProblems(applicationText, documents) {
  const problems = [];
  const application = keysOf(applicationText);
  const known = new Set();

  for (const key of application) {
    if (known.has(key)) {
      problems.push(`App.xaml: x:Key="${key}" is declared twice in the application dictionary`);
    }
    known.add(key);
  }

  for (const document of documents) {
    for (const key of keysOf(document.text)) {
      if (known.has(key)) {
        problems.push(`${document.file}: x:Key="${key}" shadows the key of the same name in `
          + 'App.xaml: a page resource wins over the application one, so the shared copy '
          + 'is never used');
      }
    }
  }

  return problems;
}

function walk(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin') continue;
      walk(path.join(dir, entry.name), out);
    } else if (entry.name.endsWith('.xaml')) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

function main() {
  const problems = [];
  const files = walk(APP, []);
  const application = path.resolve(path.join(APP, 'App.xaml'));
  const applicationText = fs.existsSync(application) ? fs.readFileSync(application, 'utf8') : '';
  const documents = [];

  for (const file of files) {
    const rel = path.relative(ROOT, file).replace(/\\/g, '/');
    const xaml = fs.readFileSync(file, 'utf8');
    problems.push(...xamlProblems(xaml, rel));
    if (path.resolve(file) !== application) documents.push({ file: rel, text: xaml });
  }

  problems.push(...resourceKeyProblems(applicationText, documents));

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} XAML problem(s).`);
    process.exit(1);
  }
  console.log(`OK: ${files.length} XAML file(s) are well formed, and no page hides a key of App.xaml.`);
}

if (require.main === module) main();

module.exports = { xamlProblems, resourceKeyProblems };
