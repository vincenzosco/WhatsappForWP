// The chat page's native bars are declared inside <phone:PhoneApplicationPage.Resources>.
// Silverlight's InitializeComponent only assigns those x:Name fields through FindName,
// and FindName does not see items inside a ResourceDictionary: the fields stay null at
// runtime. Assigning `ApplicationBar = ChatAppBar` then installs no bar at all and the
// `CallHeaderButton.IsEnabled` line throws where OnNavigatedTo runs, which aborts the
// chat before its messages bind. HomePage already resolves its bars with
// `Resources["key"]`, the lookup that works, so the chat page must do the same and
// reach the icon buttons through the bar's own collections.
import test from 'node:test';
import assert from 'node:assert';
import { readFileSync } from 'node:fs';

const code = readFileSync('WhatsappApp/Pages/ChatPage.xaml.cs', 'utf8');

test('chat bars are resolved from the page resources, not from name fields', () => {
  assert.ok(code.includes('Resources["ChatApplicationBar"]'),
    'the chat bar is fetched by key, the way HomePage fetches its bars');
  assert.ok(code.includes('Resources["SelectedMessagesApplicationBar"]'),
    'the selection bar is fetched by key, the way HomePage fetches its bars');
});

test('no field of a resource-declared bar item is dereferenced', () => {
  const forbidden = [
    'ApplicationBar = ChatAppBar',
    'ApplicationBar = SelectedMessagesAppBar',
    'CallHeaderButton.',
    'ForwardMessagesAppBarButton.',
    'CopyMessagesAppBarButton.',
    'DeleteMessagesAppBarButton.'
  ];
  for (const needle of forbidden) {
    assert.ok(!code.includes(needle),
      `ChatPage.xaml.cs must not touch the generated field behind ${JSON.stringify(needle)}: it is null at runtime`);
  }
});

test('the bar buttons are read off the bar instance', () => {
  // Buttons[0..2] of the selection bar are Forward, Copy, Delete, the same order
  // the XAP reference builds them in (`TryInitSelectModeAppBar`).
  assert.ok(/\.Buttons\[0\]/.test(code),
    'Forward is reached through SelectedMessagesAppBar.Buttons');
  assert.ok(code.includes('.Buttons['),
    'icon buttons come from the bar collection');
});
