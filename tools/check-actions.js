#!/usr/bin/env node
/**
 * tools/check-actions.js
 *
 * Guard for the buttons: nome, gestore, icona e misura dichiarata.
 *
 * Perche' esiste: due pulsanti con la sola icona, uno accanto all'altro, si
 * scambiano con una modifica di una riga - basta spostare un `Click="..."`, o il
 * commento che nomina l'icona - e il risultato e' che toccando un'icona se ne
 * apre un'altra. La regola non e' stilistica: e' il modo per rendere quello
 * scambio impossibile da spedire.
 *
 * Regole:
 *  1. un pulsante chiamato X e' cablato solo a X_Click;
 *  2. i pulsanti della barra del titolo disegnano l'icona dichiarata qui sotto;
 *  3. un pulsante a misura fissa - e uno stile che dimensiona pulsanti - azzera i
 *     minimi con cui il tema di WP8.1 scavalcherebbe la misura dichiarata.
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
    MoreButton: 'IconOverflow',
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

    // Il tema di WP8.1 impone a ogni Button un MinWidth di 109 e un MinHeight
    // di 57.5, e un minimo scavalca la misura dichiarata. Un pulsante a misura
    // fissa - uno che dichiara Width - deve quindi azzerare i minimi, o la sua
    // colonna si allarga oltre il disegnato e stringe quello che sta accanto:
    // era il titolo "WhatsApp" della lista chat, ed era la riga di scrittura di
    // una chat, larga 122 px in meno del previsto.
    const width = attribute(m[1], 'Width');
    if (width && (attribute(m[1], 'MinWidth') !== '0' || attribute(m[1], 'MinHeight') !== '0')) {
      problems.push(`${file}: ${name} declares Width="${width}" without MinWidth="0" and ` +
        'MinHeight="0": the WP8.1 theme minimums (109 x 57.5) override the declared size, ' +
        'the column grows and the text beside it is squeezed');
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

/**
 * Problemi degli stili che dimensionano pulsanti.
 *
 * Il minimo del tema vale anche per uno stile, e li' pesa di piu': un pulsante in
 * chiaro si vede nella sua colonna, mentre uno stile non sa in che colonna
 * finiranno i pulsanti che lo usano, quindi deve essere sicuro su entrambi gli
 * assi. Uno stile con BasedOn eredita i setter della base, che puo' stare in un
 * altro file: quello non si giudica da qui.
 */
function styleProblems(xaml, file) {
  const problems = [];
  for (const m of xaml.matchAll(/<Style\b([^>]*)>([\s\S]*?)<\/Style>/g)) {
    if (attribute(m[1], 'TargetType') !== 'Button') continue;
    if (attribute(m[1], 'BasedOn')) continue;

    const setters = {};
    for (const setter of m[2].matchAll(/<Setter\b([^>]*)\/>/g)) {
      setters[attribute(setter[1], 'Property')] = attribute(setter[1], 'Value');
    }

    const declared = ['Width', 'Height']
      .filter((axis) => setters[axis] !== undefined)
      .map((axis) => `${axis}="${setters[axis]}"`);
    if (!declared.length) continue;

    if (setters.MinWidth !== '0' || setters.MinHeight !== '0') {
      problems.push(`${file}: the ${attribute(m[1], 'x:Key') || 'implicit'} Button style ` +
        `declares ${declared.join(', ')} without MinWidth="0" and MinHeight="0": a WP8.1 ` +
        'theme minimum (109 x 57.5) overrides the declared size, and a style cannot know ' +
        'which column its buttons end up in');
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
  let styles = 0;

  for (const file of walk(APP, [])) {
    const rel = path.relative(APP, file).replace(/\\/g, '/');
    const xaml = fs.readFileSync(file, 'utf8');
    buttons += (xaml.match(/<Button\b[^>]*Click="/g) || []).length;
    styles += (xaml.match(/<Style\b[^>]*TargetType="Button"/g) || []).length;
    problems.push(...buttonProblems(xaml, rel));
    problems.push(...titleBarProblems(xaml, rel));
    problems.push(...styleProblems(xaml, rel));
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} action problem(s).`);
    process.exit(1);
  }
  console.log(`OK: ${buttons} button(s) and ${styles} Button style(s), ` +
    'name/handler/icon/size agree.');
}

if (require.main === module) main();

module.exports = { buttonProblems, titleBarProblems, styleProblems, TITLE_BAR };
