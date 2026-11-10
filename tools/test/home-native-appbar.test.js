'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const xaml = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/HomePage.xaml'), 'utf8');
const code = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/HomePage.xaml.cs'), 'utf8');
const project = fs.readFileSync(path.join(ROOT, 'WhatsappApp/WhatsApp.csproj'), 'utf8');

test('home uses native app bars for each section and keeps only working commands', () => {
  assert.ok(xaml.includes('xmlns:shell="clr-namespace:Microsoft.Phone.Shell;assembly=Microsoft.Phone"'));
  assert.match(xaml, /<shell:ApplicationBar x:Key="ChatsApplicationBar"/);
  assert.match(xaml, /x:Uid="Home_SearchAppBarButton"/);
  assert.match(xaml, /x:Uid="Home_MarkStatusesAppBarMenuItem"/);
  assert.match(xaml, /x:Uid="Home_NewCallAppBarButton"/);
  assert.match(xaml, /<shell:ApplicationBar x:Key="StatusApplicationBar"/);
  assert.match(xaml, /<shell:ApplicationBar x:Key="CallsApplicationBar"/);
  assert.match(xaml, /IconUri="\/Assets\/Reference\/search\.png"/);
  assert.match(xaml, /IconUri="\/Assets\/Reference\/add\.png"/);
  assert.match(xaml, /IconUri="\/Assets\/Reference\/refresh\.png"/);
  assert.match(xaml, /ApplicationBarMenuItem[^>]*Click="ReadAllAppBarMenuItem_Click"/);
  assert.match(xaml, /ApplicationBarMenuItem[^>]*Click="PinChatsAppBarMenuItem_Click"/);
  assert.match(xaml, /ApplicationBarMenuItem[^>]*Click="UnpinAllAppBarMenuItem_Click"/);
  assert.match(xaml, /ApplicationBarMenuItem[^>]*Click="MarkAllStatusesAppBarMenuItem_Click"/);
  assert.match(xaml, /ApplicationBarMenuItem[^>]*Click="MarkCallsAppBarMenuItem_Click"/);
  assert.match(xaml, /ApplicationBarMenuItem[^>]*Click="SettingsAppBarMenuItem_Click"/);
  assert.match(xaml, /ApplicationBarIconButton[^>]*Click="NewCallButton_Click"/);
  const appBarHandlers = [...xaml.matchAll(/<shell:ApplicationBar(?:IconButton|MenuItem)\b([^>]*)Click="([A-Za-z_][A-Za-z0-9_]*)"/g)]
    .map((match) => match[2]);
  for (const handler of appBarHandlers) {
    assert.match(code, new RegExp(`private (?:async )?void ${handler}\\(object sender, EventArgs e\\)`),
      `${handler} uses the native ApplicationBar EventArgs signature`);
  }
  for (const [handler, argsType] of [
    ['ReadAllAppBarMenuItem_Click', 'EventArgs'],
    ['PinChatsAppBarMenuItem_Click', 'EventArgs'],
    ['UnpinAllAppBarMenuItem_Click', 'EventArgs'],
    ['MarkAllStatusesAppBarMenuItem_Click', 'EventArgs'],
    ['MarkCallsAppBarMenuItem_Click', 'EventArgs'],
    ['SettingsAppBarMenuItem_Click', 'EventArgs'],
    ['RefreshCallsAppBarMenuItem_Click', 'EventArgs']
  ]) {
    const handlerBody = new RegExp(`(?:private|public)\\s+(?:async\\s+)?void\\s+${handler}\\s*\\(object sender, ${argsType} e\\)`);
    assert.match(code, handlerBody, `${handler} uses ${argsType}, as required by its XAML event`);
  }
  assert.match(code, /ApplicationBar = index == 0 \? _chatsApplicationBar[\s\S]*?index == 1 \? _statusApplicationBar[\s\S]*?: _callsApplicationBar/);
  assert.match(code, /private void SearchApplicationBarButton_Click/);
  assert.match(code, /private void NewChatApplicationBarButton_Click/);
  assert.match(code, /private void NewCallButton_Click/);
  assert.match(code, /private void PinChatsAppBarMenuItem_Click/);
  assert.ok(project.includes('Assets\\Reference\\search.png'));
  assert.ok(project.includes('Assets\\Reference\\add.png'));
  assert.ok(project.includes('Assets\\Reference\\refresh.png'));
});

test('home resolves native application bars and menu items from the resource dictionary', () => {
  for (const key of ['ChatsApplicationBar', 'StatusApplicationBar', 'CallsApplicationBar']) {
    const bar = new RegExp(`<shell:ApplicationBar\\b(?=[^>]*x:Key="${key}")[^>]*>`).exec(xaml);
    assert.ok(bar, `${key} is declared as a resource`);
    assert.match(bar[0], /IsVisible="True"/, `${key} is visible when assigned to the page`);
    assert.doesNotMatch(bar[0], /x:Name=/, `${key} is resolved by resource key, not FindName`);
  }
  assert.doesNotMatch(xaml, /<shell:ApplicationBarMenuItem[^>]*x:Name=/,
    'menu items are resolved through their ApplicationBar collection');
  assert.match(code, /_chatsApplicationBar = Resources\["ChatsApplicationBar"\] as ApplicationBar/);
  assert.match(code, /_statusApplicationBar = Resources\["StatusApplicationBar"\] as ApplicationBar/);
  assert.match(code, /_callsApplicationBar = Resources\["CallsApplicationBar"\] as ApplicationBar/);
  assert.match(code, /ApplicationBar = index == 0 \? _chatsApplicationBar[\s\S]*?index == 1 \? _statusApplicationBar[\s\S]*?: _callsApplicationBar/);
  assert.match(code, /private void UpdateChatsMenuItems\(\)[\s\S]*?SetChatMenuItemEnabled\(0, hasChats\)[\s\S]*?SetChatMenuItemEnabled\(1, hasChats\)[\s\S]*?SetChatMenuItemEnabled\(2, hasChats\)/);
  assert.match(code, /private void SetChatMenuItemEnabled\(int index, bool enabled\)[\s\S]*?_chatsApplicationBar\.MenuItems\.Count <= index\) return[\s\S]*?item\.IsEnabled = enabled/);
});

test('a new pairing clears account-scoped memory before the next login', () => {
  const data = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');
  const cache = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/AvatarCache.cs'), 'utf8');
  const prefs = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/ChatPreferences.cs'), 'utf8');
  const native = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/NativeConnection.cs'), 'utf8');
  const chats = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Protocol/Client/NativeChats.cs'), 'utf8');
  const pair = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/PairPage.xaml.cs'), 'utf8');
  assert.match(data, /ResetForNewAccountAsync\(\)[\s\S]*?_contacts\.Clear\(\)[\s\S]*?_chatMessages\.Clear\(\)[\s\S]*?_statuses\.Clear\(\)/);
  assert.match(data, /NameCache\.Clear\(\)[\s\S]*?AvatarCache\.Clear\(\)[\s\S]*?ChatPreferences\.Clear\(\)/);
  assert.match(cache, /Known\.Clear\(\)[\s\S]*?Order\.Clear\(\)/);
  assert.match(prefs, /public static void Clear\(\)[\s\S]*?Known\.Clear\(\)/);
  assert.match(native, /_seen\.Clear\(\)[\s\S]*?_chats\.Reset\(\)[\s\S]*?_appStateKeys\.Clear\(\)/);
  assert.match(chats, /public void Reset\(\)[\s\S]*?_rows\.Clear\(\)[\s\S]*?_seen\.Clear\(\)/);
  const paired = pair.slice(pair.indexOf('Credentials paired ='), pair.indexOf('Deployment.Current.Dispatcher.BeginInvoke(NavigateHomeAfterPairing)'));
  assert.ok(paired.indexOf('ResetForNewAccountAsync') < paired.indexOf('NativeConnection.Instance.RestartAsync()'));
});

test('chat sync order remains based on valid recent activity, not type or arrival order', () => {
  const sync = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Protocol/Client/NativeChats.cs'), 'utf8');
  const service = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');
  assert.match(sync, /if \(message\.Timestamp > row\.ConversationTimestamp\) row\.ConversationTimestamp = message\.Timestamp/);
  assert.match(sync, /while \(at >= 0 && _rows\[at\]\.ConversationTimestamp < row\.ConversationTimestamp\)/);
  assert.match(service, /long at = Math\.Max\(row\.LastMessageTimestamp, row\.ConversationTimestamp\)/);
  assert.match(service, /private static bool ComesBefore\(Contact left, Contact right\)[\s\S]*return left\.LastMessageAt > right\.LastMessageAt/);
  assert.match(service, /private void RememberChatList\(\)[\s\S]*SortContacts\(\)/);
});
