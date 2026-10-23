'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const fs = require('node:fs');

const guard = require('../check-lid-names');

const JID = 'WhatsappApp/Protocol/WABinary/JidUtils.cs';
const DATA = 'WhatsappApp/Services/DataService.cs';
const CHAT = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const CONTACT = 'WhatsappApp/Pages/ContactInfoPage.xaml.cs';
const CONNECTION = 'WhatsappApp/Pages/ConnectionPage.xaml.cs';

const RULE_LID = `
        public static bool IsLid(string jid)
        {
            DecodedJid decoded = Decode(jid);
            if (!decoded.IsJid) return false;
            return decoded.DomainType == DomainLid || decoded.DomainType == DomainHostedLid;
        }
`;

const RULE_BY_SUFFIX = `
        public static bool IsLid(string jid)
        {
            return jid != null && jid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase);
        }
`;

/** A `DisplayNameForJid` that asks the rule first, as the app does now. */
const NAMED = `
        public static string DisplayNameForJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return "?";
            jid = JidUtils.Normalize(jid);

            string user = jid.Split('@')[0];
            if (jid.EndsWith("@g.us")) return string.Format("Group {0}", user);
            if (JidUtils.IsLid(jid)) return user;
            if (user.Length >= 8 && user.All(char.IsDigit)) return "+" + user;

            return string.IsNullOrEmpty(user) ? "?" : user;
        }
`;

/** The defect of 2026-10-23: the digits test above the guard, so the guard is dead. */
const DIGITS_FIRST = `
        public static string DisplayNameForJid(string jid)
        {
            string user = jid.Split('@')[0];
            if (jid.EndsWith("@g.us")) return string.Format("Group {0}", user);
            if (user.Length >= 8 && user.All(char.IsDigit)) return "+" + user;
            if (JidUtils.IsLid(jid)) return user;

            return user;
        }
`;

const DISPLAY_NUMBER = `
        private static string DisplayNumber(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return "";
            if (jid.EndsWith("@g.us")) return "";
            if (JidUtils.IsLid(jid)) return "";

            string user = jid.Split('@')[0];
            return "+" + user;
        }
`;

const DISPLAY_NUMBER_WITHOUT_RULE = `
        private static string DisplayNumber(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return null;
            if (jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) return null;

            int at = jid.IndexOf('@');
            string user = at < 0 ? jid : jid.Substring(0, at);
            return "+" + user;
        }
`;

/** A tree where every site answers the rule. */
function goodTree(overrides) {
  const tree = {
    [JID]: RULE_LID,
    [DATA]: NAMED,
    [CHAT]: DISPLAY_NUMBER,
    [CONTACT]: DISPLAY_NUMBER
  };
  return Object.assign(tree, overrides || {});
}

test('il codice buono non ha problemi', () => {
  assert.deepStrictEqual(guard.lidNameProblems(goodTree()), []);
});

test('un controllo sui soli caratteri numerici prima del controllo LID e un problema', () => {
  const problems = guard.lidNameProblems(goodTree({ [DATA]: DIGITS_FIRST }));
  assert.ok(problems.some((p) => /tests for digits before it asks IsLid/.test(p)),
    problems.join('\n'));
  // And the same order writes the "+" before the rule: two sentences, one defect.
  assert.ok(problems.some((p) => /writes the "\+" before it asks IsLid/.test(p)),
    problems.join('\n'));
});

test('un DisplayNameForJid che non chiede IsLid e un problema', () => {
  const withoutRule = DIGITS_FIRST.replace(/\s*if \(JidUtils\.IsLid\(jid\)\) return user;/m, '');
  const problems = guard.lidNameProblems(goodTree({ [DATA]: withoutRule }));
  assert.ok(problems.some((p) => /DataService\.cs: public static string DisplayNameForJid never asks JidUtils\.IsLid/.test(p)),
    problems.join('\n'));
});

test('l intestazione della chat e la pagina contatto devono chiedere la regola', () => {
  const chat = guard.lidNameProblems(goodTree({ [CHAT]: DISPLAY_NUMBER_WITHOUT_RULE }));
  assert.ok(chat.some((p) => /ChatPage\.xaml\.cs: private static string DisplayNumber never asks/.test(p)),
    chat.join('\n'));

  const contact = guard.lidNameProblems(goodTree({ [CONTACT]: DISPLAY_NUMBER_WITHOUT_RULE }));
  assert.ok(contact.some((p) => /ContactInfoPage\.xaml\.cs: private static string DisplayNumber never asks/.test(p)),
    contact.join('\n'));
});

test('la regola deve decodificare il JID, non cercare un suffisso', () => {
  const problems = guard.lidNameProblems(goodTree({ [JID]: RULE_BY_SUFFIX }));
  assert.strictEqual(problems.length, 2);
  assert.ok(problems.some((p) => /does not ask the decoded domain/.test(p)));
  assert.ok(problems.some((p) => /matches a string suffix/.test(p)));
});

test('senza IsLid la regola manca', () => {
  const problems = guard.lidNameProblems(goodTree({ [JID]: '' }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /JidUtils\.IsLid is missing/);
});

test('un altro file che costruisce "+numero" da un JID e un problema', () => {
  const stray = `
        private static string Label(string jid)
        {
            string user = jid.Split('@')[0];
            return "+" + user;
        }
`;
  const problems = guard.lidNameProblems(goodTree({ 'WhatsappApp/Pages/SomewherePage.xaml.cs': stray }));
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /SomewherePage\.xaml\.cs/);
});

test('la pagina della connessione e esentata: stampa il numero di questo account', () => {
  const connection = `
        private static string DisplayNumber(string jid)
        {
            int at = jid.IndexOf('@');
            string user = at < 0 ? jid : jid.Substring(0, at);
            return "+" + user;
        }
`;
  assert.deepStrictEqual(guard.lidNameProblems(goodTree({ [CONNECTION]: connection })), []);
});

test('un commento non conta come chiamata alla regola', () => {
  const commented = `
        private static string DisplayNumber(string jid)
        {
            // TODO: JidUtils.IsLid(jid) qui?
            if (jid.EndsWith("@g.us")) return "";
            string user = jid.Split('@')[0];
            return "+" + user;
        }
`;
  const problems = guard.lidNameProblems(goodTree({ [CHAT]: commented }));
  assert.ok(problems.some((p) => /ChatPage\.xaml\.cs: private static string DisplayNumber never asks/.test(p)),
    problems.join('\n'));
});

test('il corpo di un metodo con "{0}" dentro una stringa si chiude dove deve', () => {
  const body = guard.methodBody(NAMED, 'public static string DisplayNameForJid');
  assert.ok(body.includes('return string.IsNullOrEmpty(user) ? "?" : user;'),
    'the body must reach the last statement, not stop at the "{0}" of string.Format');
  assert.ok(body.trimEnd().endsWith('}'));
});

test('i file del progetto non hanno problemi', () => {
  const root = path.join(__dirname, '..', '..');
  const files = {};
  const walk = (dir) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        walk(full);
        continue;
      }
      if (!entry.name.endsWith('.cs')) continue;
      const key = path.relative(root, full).split(path.sep).join('/');
      files[key] = fs.readFileSync(full, 'utf8');
    }
  };
  walk(path.join(root, 'WhatsappApp'));

  assert.deepStrictEqual(guard.lidNameProblems(files), []);
});
