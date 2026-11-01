'use strict';
const test = require('node:test');
const assert = require('node:assert');

const guard = require('../check-emoji');

const RULE = 'WhatsappApp/Protocol/Client/EmojiText.cs';
const MESSAGE = 'WhatsappApp/Models/ChatMessage.cs';
const CHAT_XAML = 'WhatsappApp/Pages/ChatPage.xaml';
const CHAT_CODE = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const VIEWER = 'WhatsappApp/Pages/StatusViewerPage.xaml.cs';
const GROUP = 'WhatsappApp/Models/StatusGroup.cs';
const DATA = 'WhatsappApp/Services/DataService.cs';

const GOOD = {
  [RULE]: `
    public static class EmojiText
    {
        private static readonly Dictionary<int, string> Older = new Dictionary<int, string>
        {
            { 0x1FA75, "\\U0001F499" }
        };

        public static string Compatible(string text) { return text; }

        public static string Emoticons(string text) { return text; }
    }
`,
  [MESSAGE]: `
    public class ChatMessage
    {
        public string Text
        {
            get { return _text; }
            set { _text = value; }
        }

        public string ShownText
        {
            get { return EmojiText.Compatible(_text); }
        }
    }
`,
  [CHAT_XAML]: `
    <TextBlock Text="{Binding ShownText}" TextWrapping="Wrap"/>
    <TextBox x:Name="MessageTextBox">
      <TextBox.InputScope>
        <InputScope>
          <InputScopeName NameValue="Chat"/>
        </InputScope>
      </TextBox.InputScope>
    </TextBox>
`,
  [CHAT_CODE]: `
    string text = EmojiText.Emoticons((MessageTextBox.Text ?? "").Trim());
    var message = new ChatMessage { Text = text };
`,
  [VIEWER]: 'StatusText.Text = _status.ShownText ?? "";',
  [GROUP]: 'Detail = Latest == null ? "" : Latest.ShownText;',
  [DATA]: 'NotificationService.ShowMessage(contact.Name, message.ShownText);'
};

function files(overrides) {
  return Object.assign({}, GOOD, overrides || {});
}

test('il codice buono non ha problemi', () => {
  assert.deepStrictEqual(guard.emojiProblems(files()), []);
});

test('una bolla che lega Text torna a disegnare il quadratino', () => {
  const problems = guard.emojiProblems(files({
    [CHAT_XAML]: GOOD[CHAT_XAML].replace('{Binding ShownText}', '{Binding Text}')
  }));
  assert.match(problems.join('\n'), /a bubble binds Text/);
});

test('un messaggio senza ShownText non ha niente da legare', () => {
  const broken = GOOD[MESSAGE].replace('public string ShownText', 'public string NeverCalled');
  const problems = guard.emojiProblems(files({ [MESSAGE]: broken }));
  assert.match(problems.join('\n'), /no ShownText/);
});

test('sostituire l emoji dentro Text manda un altro messaggio', () => {
  const broken = GOOD[MESSAGE].replace('set { _text = value; }',
    'set { _text = EmojiText.Compatible(value); }');
  const problems = guard.emojiProblems(files({ [MESSAGE]: broken }));
  assert.match(problems.join('\n'), /the Text setter substitutes the emoji/);
});

test('una regola senza tabella non sostituisce niente', () => {
  const broken = GOOD[RULE].replace(/private static readonly Dictionary<int, string>[\s\S]*?\};?\n/m, '');
  const problems = guard.emojiProblems(files({ [RULE]: broken }));
  assert.match(problems.join('\n'), /no table of replacements/);
});

test('senza la regola non c e niente da chiedere', () => {
  const problems = guard.emojiProblems(files({ [RULE]: '' }));
  assert.match(problems.join('\n'), /the emoji rule is missing/);
});

test('una didascalia di stato grezza e la stessa emoji in grande', () => {
  const problems = guard.emojiProblems(files({ [VIEWER]: 'StatusText.Text = _status.Text ?? "";' }));
  assert.match(problems.join('\n'), /the status viewer reads _status\.Text/);
});

test('la riga della tab Stato che non chiede la regola', () => {
  const broken = GOOD[GROUP].replace('Latest.ShownText', 'Latest.Text');
  const problems = guard.emojiProblems(files({ [GROUP]: broken }));
  assert.match(problems.join('\n'), /does not read Latest\.ShownText/);
});

test('il corpo della notifica grezzo porta il quadratino sulla lock screen', () => {
  const problems = guard.emojiProblems(files({
    [DATA]: 'NotificationService.ShowMessage(contact.Name, message.Text);'
  }));
  assert.match(problems.join('\n'), /the toast body is message\.Text/);
});

test('senza InputScope Chat la tastiera non offre ne emoji ne :)', () => {
  const broken = GOOD[CHAT_XAML].replace(/<TextBox\.InputScope>[\s\S]*?<\/TextBox\.InputScope>/, '');
  const problems = guard.emojiProblems(files({ [CHAT_XAML]: broken }));
  assert.match(problems.join('\n'), /no InputScope Chat/);
});

test('senza la conversione un :) digitato resta due punti e parentesi', () => {
  const broken = GOOD[RULE].replace(/public static string Emoticons\(string text\) \{ return text; \}/, '');
  const problems = guard.emojiProblems(files({ [RULE]: broken }));
  assert.match(problems.join('\n'), /no Emoticons\(string\)/);
});

test('la conversione che nessuno chiama non converte niente', () => {
  const broken = GOOD[CHAT_CODE].replace('EmojiText.Emoticons(', 'EmojiText.Compatible(');
  const problems = guard.emojiProblems(files({ [CHAT_CODE]: broken }));
  assert.match(problems.join('\n'), /the composer sends the text it was given/);
});

test('lo scope sbagliato non basta: la faccina e del solo Chat', () => {
  const broken = GOOD[CHAT_XAML].replace('NameValue="Chat"', 'NameValue="Text"');
  const problems = guard.emojiProblems(files({ [CHAT_XAML]: broken }));
  assert.match(problems.join('\n'), /no InputScope Chat/);
});
