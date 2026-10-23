#!/usr/bin/env node
/**
 * tools/check-lid-names.js
 *
 * Guard on the names this app makes out of a JID.
 *
 * Why it exists: a LID is the identifier WhatsApp uses for an account whose
 * phone number this phone does not know yet, and its user part is a long run of
 * digits (150375613116620). Anything that asks "is this a number?" before it asks
 * "is this a LID?" prints `+150375613116620` - a phone number that belongs to
 * nobody - in the chat list, in the conversation's header and on the contact
 * page.
 *
 * That is not a hypothetical: `DataService.DisplayNameForJid` carried the right
 * rule and the wrong order. The `@lid` guard was written under the
 * `user.Length >= 8 && user.All(char.IsDigit)` branch, which every LID satisfies,
 * so the guard could not fire; and the two `DisplayNumber` helpers only excluded
 * groups. The three sites are checked here, and so is the rule they ask.
 *
 * Usage:
 *   node tools/check-lid-names.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { stripComments } = require('./csharp');

const ROOT = path.resolve(__dirname, '..');

const JID = 'WhatsappApp/Protocol/WABinary/JidUtils.cs';
const DATA = 'WhatsappApp/Services/DataService.cs';
const CHAT = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const CONTACT = 'WhatsappApp/Pages/ContactInfoPage.xaml.cs';
const CONNECTION = 'WhatsappApp/Pages/ConnectionPage.xaml.cs';

/**
 * The places that turn a JID into something a person reads. Every one of them
 * asks `JidUtils.IsLid`; the order matters in the first, where the digits test
 * sits right below.
 */
const SITES = [
  {
    file: DATA,
    method: 'public static string DisplayNameForJid',
    digits: 'All(char.IsDigit)',
    why: 'the chat list and every preview'
  },
  {
    file: CHAT,
    method: 'private static string DisplayNumber',
    digits: 'char.IsDigit',
    why: "the conversation's header line"
  },
  {
    file: CONTACT,
    method: 'private static string DisplayNumber',
    digits: 'char.IsDigit',
    why: 'the contact page'
  }
];

/**
 * The body of a method: from its signature to the brace that closes it, with the
 * strings skipped so a `{` inside `string.Format`'s `"{0}"` cannot be mistaken
 * for the end of the body.
 */
function methodBody(text, signature) {
  const at = text.indexOf(signature);
  if (at < 0) return null;

  let depth = 0;
  let started = false;
  for (let i = at + signature.length; i < text.length; i++) {
    const ch = text[i];
    if (ch === '"') {
      i = endOfString(text, i);
      continue;
    }
    if (ch === '{') {
      depth++;
      started = true;
      continue;
    }
    if (ch === '}') {
      depth--;
      if (started && depth === 0) return text.slice(at, i + 1);
    }
  }

  return text.slice(at);
}

/** The index of the quote that closes the string opened at `open`. */
function endOfString(text, open) {
  for (let i = open + 1; i < text.length; i++) {
    if (text[i] === '\\') {
      i++;
      continue;
    }
    if (text[i] === '"') return i;
  }
  return text.length - 1;
}

/**
 * The rule itself: it must answer from the decoded domain, not from a suffix on
 * the raw string. `x:5@lid` is a LID with a device, and a suffix test on the raw
 * address is how the `@hosted.lid` shape gets through.
 */
function ruleProblems(jid) {
  const problems = [];
  const body = methodBody(jid, 'public static bool IsLid');

  if (body == null) {
    problems.push(`${JID}: JidUtils.IsLid is missing - every name this app makes out of a `
      + 'JID needs one answer to "is this an identifier and not a number?"');
    return problems;
  }

  if (!/DomainLid/.test(body)) {
    problems.push(`${JID}: JidUtils.IsLid does not ask the decoded domain (DomainLid): a `
      + 'suffix test on the raw address misses `x:5@lid` and lets `x@hosted.lid` through');
  }

  if (/EndsWith\(/.test(body)) {
    problems.push(`${JID}: JidUtils.IsLid matches a string suffix instead of decoding the `
      + 'JID: the device sits inside the address (`x:5@lid`), which is why the rule lives here');
  }

  return problems;
}

/** A display site: it asks the rule, and it asks before it prints digits. */
function siteProblems(source, site) {
  const problems = [];
  const body = methodBody(source, site.method);

  if (body == null) {
    problems.push(`${site.file}: ${site.method} is missing - ${site.why} has no rule for a LID`);
    return problems;
  }

  const lid = body.indexOf('IsLid(');
  if (lid < 0) {
    problems.push(`${site.file}: ${site.method} never asks JidUtils.IsLid, so a LID is printed `
      + `as a phone number in ${site.why}: its user part is a long run of digits, and `
      + '`+<those digits>` is a number nobody has');
    return problems;
  }

  // The guard must come *first*: a digits test above it is the defect this exists for.
  const digits = body.indexOf(site.digits);
  if (digits >= 0 && digits < lid) {
    problems.push(`${site.file}: ${site.method} tests for digits before it asks IsLid - the `
      + 'LID branch under it can never run, because a LID satisfies the digits test');
  }

  const plus = body.indexOf('"+"');
  if (plus >= 0 && plus < lid) {
    problems.push(`${site.file}: ${site.method} writes the "+" before it asks IsLid`);
  }

  return problems;
}

/**
 * Any other file that turns a JID's user part into text must ask the rule too.
 * `ConnectionPage` is the one exception, and it is named rather than pattern-matched:
 * it prints this account's own address, which is a phone number by construction.
 */
function strayProblems(files) {
  const problems = [];

  for (const file of Object.keys(files).sort()) {
    if (file === CONNECTION) continue;
    if (file === JID) continue;

    const text = files[file];
    const buildsText = text.includes("Split('@')") && text.includes('"+"');
    if (!buildsText) continue;
    if (text.includes('IsLid(')) continue;

    problems.push(`${file}: builds a "+number" out of a JID and never asks JidUtils.IsLid: `
      + 'a LID would be printed as a phone number that belongs to nobody');
  }

  return problems;
}

/** Every `.cs` under WhatsappApp, keyed by its path relative to the project. */
function appSources(dir, out) {
  out = out || {};
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      appSources(full, out);
      continue;
    }
    if (!entry.name.endsWith('.cs')) continue;

    out[path.relative(ROOT, full).split(path.sep).join('/')] = fs.readFileSync(full, 'utf8');
  }
  return out;
}

/**
 * The problems of the sources handed in: empty when a LID can never be read as a
 * phone number.
 */
function lidNameProblems(files) {
  files = files || {};

  // Comments are not code: a `// TODO: JidUtils.IsLid(jid)` is not the rule being
  // asked, and this file's own header names the methods it guards.
  const code = {};
  for (const file of Object.keys(files)) code[file] = stripComments(String(files[file] || ''));

  const problems = ruleProblems(code[JID] || '');

  const bySite = {};
  for (const site of SITES) {
    if (bySite[site.file] == null) bySite[site.file] = code[site.file] || '';
    problems.push(...siteProblems(bySite[site.file], site));
  }

  problems.push(...strayProblems(code));

  return problems;
}

function main() {
  const problems = lidNameProblems(appSources(path.join(ROOT, 'WhatsappApp')));

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} LID name problem(s).`);
    process.exit(1);
  }

  console.log('OK: a LID is never read as a phone number - the rule lives in JidUtils.IsLid and '
    + 'the three names built from a JID ask it before they print digits.');
}

if (require.main === module) main();

module.exports = { lidNameProblems, methodBody };
