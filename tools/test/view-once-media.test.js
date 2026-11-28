'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const message = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Models/ChatMessage.cs'), 'utf8');
const cache = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/MessageCache.cs'), 'utf8');
const data = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/DataService.cs'), 'utf8');
const native = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Services/NativeConnection.cs'), 'utf8');
const history = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Protocol/WaProto/HistorySync.cs'), 'utf8');
const page = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Pages/ChatPage.xaml'), 'utf8');
const english = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Strings/en-US/Resources.resw'), 'utf8');
const italian = fs.readFileSync(path.join(ROOT, 'WhatsappApp/Strings/it-IT/Resources.resw'), 'utf8');

test('view-once wrappers retain a serializable flag and draw an explicit localized badge', () => {
  assert.match(message, /\[DataMember\][\s\S]{0,180}public bool IsViewOnce\b/,
    'the cacheable message model carries the flag');
  assert.match(cache, /IsViewOnce\s*=\s*message\.IsViewOnce/,
    'slimming a cached row keeps the flag');
  const viewOnceBindings = page.match(/Visibility="\{Binding IsViewOnce, Converter=\{StaticResource BoolToVisibility\}\}"/g) || [];
  assert.ok(viewOnceBindings.length >= 2,
    'both received and sent media bubbles show the badge');
  assert.match(page, /x:Uid="ChatPage_ViewOnce"/,
    'the badge text comes from the UI resources');
  assert.match(english, /<data name="ChatPage_ViewOnce\.Text"/,
    'English has the visible label');
  assert.match(italian, /<data name="ChatPage_ViewOnce\.Text"/,
    'Italian has the visible label');
  assert.match(history, /public bool IsViewOnce;/,
    'history rows can carry wrapper metadata that is not in the normalized encoding');
  assert.match(history, /target\.IsViewOnce\s*=\s*decoded\.IsViewOnce/,
    'the history decoder captures wrapper identity before normalizing the message');
  assert.match(native, /message\.IsViewOnce\s*=\s*incoming\.Content\.IsViewOnce/,
    'the live media reader saves the flag before encoding normalized content');
  assert.match(data, /message\.IsViewOnce\s*=\s*incoming\.IsViewOnce/,
    'the live row adapter carries the saved flag to the model');
  assert.match(data, /message\.IsViewOnce\s*=\s*message\.IsViewOnce\s*\|\|\s*decoded\.IsViewOnce/,
    'decoding normalized content does not erase its preserved wrapper flag');
});
