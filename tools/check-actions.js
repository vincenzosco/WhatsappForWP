#!/usr/bin/env node
/**
 * tools/check-actions.js
 *
 * Guard for the buttons: nome, gestore e icona devono raccontare la stessa
 * azione.
 *
 * Perche' esiste: due pulsanti con la sola icona, uno accanto all'altro, si
 * scambiano con una modifica di una riga - basta spostare un `Click="..."`, o il
 * commento che nomina l'icona - e il risultato e' che toccando un'icona se ne
 * apre un'altra. La regola non e' stilistica: e' il modo per rendere quello
 * scambio impossibile da spedire.
 *
 * Regole:
 *  1. un pulsante chiamato X e' cablato solo a X_Click;
 *  2. i pulsanti della barra del titolo disegnano l'icona dichiarata qui sotto.
 *
 * Usage:
 *   node tools/check-actions.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');

/**
 * I due pulsanti con la sola icona che stanno uno accanto all'altro nella barra
 * del titolo. Se qualcuno ne scambia i Click o i commenti delle icone, questa
 * tabella lo dice invece di lasciarlo arrivare sul telefono.
 */
const TITLE_BAR = {
  'Pages/ChatsPage.xaml': {
    NewChatButton: 'IconNewChat',
    SettingsButton: 'IconSettings'
  }
};

function attribute(tag, name) {
  const m = tag.match(new RegExp(`\\b${name}="([^"]*)"`));
  return m ? m[1] : null;
}

/** Problemi di tutti i pulsanti di un file XAML. */
function buttonProblems(xaml, file) {
  const problems = [];
  for (const m of xaml.matchAll(/<Button\b([^>]*)>([\s\S]*?)<\/Button>/g)) {
    const name = attribute(m[1], 'x:Name');
    const click = attribute(m[1], 'Click');
    if (!name || !click) continue;
    if (click !== name + '_Click') {
      problems.push(`${file}: ${name} is wired to ${click}, and it must be ${name}_Click: ` +
        'the handler of a button carries its name, so an icon cannot open another ' +
        "button's action");
    }
  }
  return problems;
}

/** Problemi della barra del titolo: l'icona dichiarata per ogni pulsante. */
function titleBarProblems(xaml, file) {
  const declared = TITLE_BAR[file];
  if (!declared) return [];

  const problems = [];
  for (const m of xaml.matchAll(/<Button\b([^>]*)>([\s\S]*?)<\/Button>/g)) {
    const name = attribute(m[1], 'x:Name');
    if (!name || !declared[name]) continue;
    const icon = (m[2].match(/<!--\s*(Icon[A-Za-z]+)\s*-->/) || [])[1];
    if (icon !== declared[name]) {
      problems.push(`${file}: ${name} draws ${icon || 'no icon'}, and it must be ` +
        `${declared[name]}: the two buttons of the title bar are one next to the other`);
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
  let buttons = 0;

  for (const file of walk(APP, [])) {
    const rel = path.relative(APP, file).replace(/\\/g, '/');
    const xaml = fs.readFileSync(file, 'utf8');
    buttons += (xaml.match(/<Button\b[^>]*Click="/g) || []).length;
    problems.push(...buttonProblems(xaml, rel));
    problems.push(...titleBarProblems(xaml, rel));
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} action problem(s).`);
    process.exit(1);
  }
  console.log(`OK: ${buttons} button(s), name/handler/icon agree.`);
}

if (require.main === module) main();

module.exports = { buttonProblems, titleBarProblems, TITLE_BAR };
