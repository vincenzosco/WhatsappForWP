'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const read = (relative) => fs.readFileSync(path.join(ROOT, relative), 'utf8');
const source = (relative) => {
  const file = path.join(ROOT, relative);
  assert.ok(fs.existsSync(file), relative + ' exists');
  return fs.readFileSync(file, 'utf8');
};

test('the shared activity counter exposes the lease contract', () => {
  const counter = source('WhatsappApp/Services/LoadingActivity.cs');
  assert.match(counter, /public static IDisposable Begin\(string name\)/);
  assert.match(counter, /public static bool IsActive/);
  assert.match(counter, /public static event EventHandler Changed/);
});

test('the counter releases each lease exactly once and notifies on state transitions', () => {
  const counter = source('WhatsappApp/Services/LoadingActivity.cs');
  assert.match(counter, /Interlocked\.Exchange/);
  assert.match(counter, /EventHandler handler/);
  assert.match(counter, /handler\(null, EventArgs\.Empty\)/);
});

test('the tray adapter follows activity and navigation without creating per-page indicators', () => {
  const adapter = source('WhatsappApp/Services/LoadingIndicator.cs');
  assert.match(adapter, /SystemTray\.SetProgressIndicator\(page, indicator\)/);
  assert.match(adapter, /LoadingActivity\.Changed/);
  assert.match(adapter, /frame\.Navigated/);
  assert.match(adapter, /IsIndeterminate = true/);
  assert.match(adapter, /LoadingIndicator_Loading/);
});

test('the tray adapter retries dispatcher lookup after the first navigation', () => {
  const adapter = source('WhatsappApp/Services/LoadingIndicator.cs');
  const refresh = adapter.slice(adapter.indexOf('public static void Refresh()'), adapter.indexOf('private static void OnNavigated'));
  assert.match(refresh, /if \(dispatcher == null\)[\s\S]*?UiThread\.Dispatcher\(\)/,
    'startup can precede RootVisual, so navigation must be able to provide the dispatcher later');
});

test('forgetting an account releases an unfinished history-sync activity', () => {
  const data = source('WhatsappApp/Services/DataService.cs');
  const forget = data.slice(data.indexOf('public void ForgetAccount()'), data.indexOf('public int UnreadChats()'));
  assert.match(forget, /SetHistorySyncAllowed\(false\)/);
  assert.match(forget, /SetHistorySyncAllowed\(false\)[\s\S]*?NativeConnection\.Instance\.Stop\(\)/);
  assert.match(data, /private void ReleaseHistorySyncActivity\(\)[\s\S]*?_historySyncActivity\.Dispose\(\)[\s\S]*?_historySyncActivity = null/);
});

test('history from a replaced connection cannot be applied to the new account', () => {
  const data = source('WhatsappApp/Services/DataService.cs');
  const progress = data.slice(data.indexOf('private void OnNativeSyncProgress'), data.indexOf('private void OnNativeCalls'));
  const forget = data.slice(data.indexOf('public void ForgetAccount()'), data.indexOf('public int UnreadChats()'));
  const begin = data.slice(data.indexOf('public void BeginHistory()'), data.indexOf('public async Task ResetForNewAccountAsync()'));
  const reset = data.slice(data.indexOf('public async Task ResetForNewAccountAsync()'), data.indexOf('public bool MarkHistoryRequested'));
  const native = source('WhatsappApp/Services/NativeConnection.cs');
  const readLoop = native.slice(native.indexOf('private async Task ReadAsync'), native.indexOf('private async Task ReadNotificationAsync'));
  const messageReader = native.slice(native.indexOf('private async Task ReadMessageAsync'), native.indexOf('private async Task RetryHeldAsync'));
  const history = native.slice(native.indexOf('private async Task<bool> ApplyHistoryAsync'), native.indexOf('private void ApplySync'));
  assert.match(progress, /_historyActivityGate[\s\S]*?_historySyncAllowed/);
  assert.match(forget, /SetHistorySyncAllowed\(false\)[\s\S]*?NativeConnection\.Instance\.Stop\(\)/);
  assert.match(begin, /_historySyncAllowed = true[\s\S]*?LoadingActivity\.Begin\("history sync"\)/);
  assert.match(reset, /SetHistorySyncAllowed\(false\)/);
  assert.match(data, /private void SetHistorySyncAllowed\(bool allowed\)[\s\S]*?_historyActivityGate[\s\S]*?ReleaseHistorySyncActivity\(\)/);
  assert.match(native, /await ReadAsync\(session, account, generation\)/);
  assert.match(readLoop, /ReadAsync\(NativeSession session, Credentials account, int generation\)/);
  assert.match(readLoop, /await ReadMessageAsync\(session, account, node, generation\)/);
  assert.match(messageReader, /ReadMessageAsync\(NativeSession session, Credentials account, BinaryNode node, int generation\)/);
  assert.match(messageReader, /ApplyHistoryAsync\(history, generation\)/);
  assert.match(history, /ApplyHistoryAsync\(HistorySyncNotification notification, int generation\)/);
  assert.match(history, /generation != _generation[\s\S]*?return false;[\s\S]*?ApplySync\(sync\)/);
  assert.doesNotMatch(history, /int generation = _generation/);
});

test('application startup starts the tray adapter once with its root frame', () => {
  const app = read('WhatsappApp/App.xaml.cs');
  assert.match(app, /LoadingIndicator\.Start\(_rootFrame\)/);
});

test('the app-only tray adapter is not compiled into the shared core', () => {
  const core = source('WhatsappApp.Core/WhatsappApp.Core.csproj');
  assert.match(core, /Compile Include="[^\"]*Services\\\*\*\\\*\.cs" Exclude="[^\"]*Services\\LoadingIndicator\.cs"/);
});

test('chat opening and awaited data retrieval hold shared activity leases', () => {
  const chat = source('WhatsappApp/Pages/ChatPage.xaml.cs');
  const data = source('WhatsappApp/Services/DataService.cs');
  const avatar = source('WhatsappApp/Services/AvatarEnricher.cs');
  const statusViewer = source('WhatsappApp/Pages/StatusViewerPage.xaml.cs');
  const contactPage = source('WhatsappApp/Pages/ContactInfoPage.xaml.cs');
  const callPage = source('WhatsappApp/Pages/CallPage.xaml.cs');
  const bindStart = chat.indexOf('private async System.Threading.Tasks.Task BindAfterCacheAsync');
  const bind = chat.slice(bindStart, bindStart + 9000);
  assert.match(bind, /using \(LoadingActivity\.Begin\("chat opening"\)\)/);
  assert.match(bind, /await DataService\.Instance\.LoadCachedMessagesAsync\(chatId\)/);
  assert.match(bind, /await WaitForHistoryAsync\(chatId\)/);
  assert.match(data, /private async System\.Threading\.Tasks\.Task LoadCachedStatusesAsync[\s\S]*?LoadingActivity\.Begin\("cached statuses/);
  assert.match(data, /private async System\.Threading\.Tasks\.Task LoadCachedChatsAsync[\s\S]*?LoadingActivity\.Begin\("cached chats/);
  assert.match(data, /public void RefreshChatList\(\)[\s\S]*?LoadingActivity\.Begin\("chat list refresh/);
  assert.match(data, /private async Task RunScanCallsAsync\(\)[\s\S]*?LoadingActivity\.Begin\("call log scan/);
  assert.match(data, /private async Task RunScanStatusesAsync\(\)[\s\S]*?LoadingActivity\.Begin\("status scan/);
  assert.match(data, /public async Task ResyncChatsAsync\(\)[\s\S]*?LoadingActivity\.Begin\("resync chats/);
  assert.match(data, /private void OnNativeSyncProgress[\s\S]*?LoadingActivity\.Begin\("history sync/);
  assert.match(data, /private void OnNativeHistoryFinished[\s\S]*?ReleaseHistorySyncActivity\(\)/);
  assert.match(data, /private async Task RequestNativeMediaAsync[\s\S]*?LoadingActivity\.Begin\("message media/);
  assert.match(data, /private async Task RequestStatusMediaAsync[\s\S]*?LoadingActivity\.Begin\("status media/);
  assert.match(data, /private async Task ApplyNativeMediaAsync[\s\S]*?LoadingActivity\.Begin\("incoming media/);
  assert.match(data, /private async Task ApplyStatusBytesAsync[\s\S]*?LoadingActivity\.Begin\("incoming status media/);
  assert.match(statusViewer, /private async void ShowPicture\(\)[\s\S]*?LoadingActivity\.Begin\("status picture/);
  assert.match(contactPage, /private async Task RequestPictureAsync\(\)[\s\S]*?LoadingActivity\.Begin\("contact picture/);
  assert.match(callPage, /private async Task DecodeAvatarAsync\([\s\S]*?LoadingActivity\.Begin\("call avatar/);
  assert.match(avatar, /RequestAvatarAsync\(id, true\)/);
});

test('VoIP loading covers setup and DTLS handshake, not ringing or active calls', () => {
  const data = source('WhatsappApp/Services/DataService.cs');
  const native = source('WhatsappApp/Services/NativeConnection.cs');
  const socket = source('WhatsappApp/Protocol/Transport/CallMediaSocket.cs');
  assert.match(data, /public async Task StartCallAsync[\s\S]*?LoadingActivity\.Begin\("start call/);
  assert.match(data, /public async Task AcceptCallAsync[\s\S]*?LoadingActivity\.Begin\("accept call/);
  assert.match(data, /private void OnCallStateChanged[\s\S]*?LoadingActivity\.Begin\("incoming call setup/);
  assert.match(data, /CallKind\.Accept[\s\S]*?ReleaseCallSetupActivity\(\)/);
  assert.match(native, /private async Task ReceiveCallKeyAsync[\s\S]*?LoadingActivity\.Begin\("incoming call key/);
  assert.match(native, /public async Task<string> OfferCallAsync\(string jid, bool video\)[\s\S]*?LoadingActivity\.Begin\("prepare call offer/);
  assert.match(socket, /private void UpdateHandshakeActivity[\s\S]*?state == CallTransportState\.Handshaking[\s\S]*?LoadingActivity\.Begin\("call media handshake/);
  assert.match(socket, /transport\.StateChanged \+= OnTransportStateChanged/);
  assert.match(socket, /transport\.StateChanged -= OnTransportStateChanged/);
  assert.match(socket, /private void OnTransportStateChanged[\s\S]*?UpdateHandshakeActivity\(transport\)/);
  assert.match(socket, /if \(!ReferenceEquals\(_transport, transport\)\) return;/,
    'a delayed notification from a replaced transport cannot release the current handshake lease');
  assert.doesNotMatch(socket, /state == CallTransportState\.Waiting[\s\S]*?LoadingActivity\.Begin/);
});

test('the generic loading label is localized in both embedded languages', () => {
  const english = read('WhatsappApp.Core/Strings/Resources.resx');
  const italian = read('WhatsappApp.Core/Strings/Resources.it-IT.resx');
  for (const resources of [english, italian]) {
    assert.ok(resources.includes('name="LoadingIndicator_Loading"'));
  }
});
