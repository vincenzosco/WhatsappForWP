// The People card behind a chat. The entry was missing for every person because
// the list addresses them by `@lid` and `PhoneNumbers.UserDigits` refuses a lid,
// and a group was left out altogether - but a group is a chat too, and its card
// is the subject the list shows. So: a person whose number this phone knows the
// pair of, and any group.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..', '..');
const read = (p) => fs.readFileSync(path.join(root, p), 'utf8');

const hub = read('WhatsappApp/Services/PeopleHub.cs');
const home = read('WhatsappApp/Pages/HomePage.xaml.cs');

function slice(text, marker, span) {
  const at = text.indexOf(marker);
  assert.ok(at >= 0, 'the code still has ' + marker);
  return text.slice(at, at + span);
}

test('a person is offered whatever spelling the list has them under', () => {
  const canShow = slice(hub, 'public static bool CanShow(', 600);
  assert.match(canShow, /NumberFor\(jid\) != null \|\| GroupDirectory\.IsGroup\(jid\)/,
    'a person with a number and a group are both offered');

  const numberFor = slice(hub, 'private static string NumberFor(', 900);
  assert.match(numberFor, /PhoneNumberFor\(/,
    'the pair this phone knows turns a lid back into a number');
});

test('the card of a person carries its number and the name the list shows', () => {
  const show = slice(hub, 'public static void Show(', 3000);
  assert.match(show, /contact\.Phones\.Add/, 'the number is on the card');
  assert.match(show, /"\+" \+ digits/, 'with the plus the phone dials');
});

test('the card of a group is its subject', () => {
  const show = slice(hub, 'public static void Show(', 3000);
  assert.match(show, /IsGroup\(/, 'a group is recognized');
  assert.match(show, /ContactManager\.ShowContactCard\(/, 'and shown on a card like any other');
});

test('the menu asks before it draws the entry', () => {
  assert.match(home, /PeopleHub\.CanShow\(id\)/, 'HomePage asks first');
  assert.match(home, /ChatsPage_ShowInPeople/, 'and the entry is the one the strings carry');
});