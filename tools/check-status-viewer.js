#!/usr/bin/env node
/**
 * tools/check-status-viewer.js
 *
 * Guard for the one thing the status tab could not do: show a status.
 *
 * Why it exists: the pane listed updates and the tap sent the `read` receipt, the
 * author's ring filled, and the screen stayed on the list - a table of contents
 * with no pages. The row, the page and the receipt are three files that compile
 * perfectly well on their own, so nothing in the build notices when the tap stops
 * opening the update, when the page stops asking for the bytes a history status
 * does not carry, or when the receipt goes back to being addressed to the author
 * instead of the broadcast list it lives in (which is what it did, silently, for
 * weeks: no ring ever filled and no line said so).
 *
 * Rules:
 *  1. `StatusPane.xaml.cs` opens the update with
 *     `Navigate(typeof(StatusViewerPage), row)`: a tap that only marks and asks is
 *     the bug this feature exists to close;
 *  2. `StatusViewerPage.xaml.cs` watches it (`MarkStatusViewed(_status)`) and asks
 *     for the bytes when the row has none (`RequestStatusMedia(_status)`): a status
 *     out of the history sync carries its keys and its address, not its file;
 *  3. `StatusFeed.cs` builds the receipt **to the broadcast list** and names the
 *     author as `participant`, and `IncomingMessage.Receipt` is the builder that
 *     can carry it: the server's own receipts for our statuses have that shape
 *     (`from="status@broadcast" ... participant="..." class="status"`), and a
 *     receipt addressed to the person names a message their chat does not have;
 *  4. the feed is kept for the day a status lives: `DataService` reads
 *     `StatusCache` at start and writes a snapshot when the feed changes, a pairing
 *     drops the copy (`StatusCache.DeleteAsync`), and `StatusCache` drops what is
 *     older than `LifetimeHours` on the way in and on the way out. Without this the
 *     status tab is empty at every start, because the sync that fills it comes once,
 *     at the pairing;
 *  5. the write is **coalesced**, not per row: the history burst inserts one status
 *     per frame, and a write per insert serialised the whole day of statuses once
 *     per status on the 512 MB phone the cache exists for. `SaveStatusSnapshot`
 *     only counts the change; one writer waits for the feed to stand still, writes
 *     it once, and writes again only if it moved while the file was being written;
 *  6. the row template binds only what a status row **has**: `StatusPane.xaml`
 *     asks for `{Binding X}` on a `ChatMessage`, and X must be a public member of
 *     it. The template asked for `Initials` before the model had one, and the run
 *     of 2026-10-22 answered with two binding errors per realised row
 *     (`'Initials' property not found on 'WhatsappApp.Models.ChatMessage'`) and an
 *     empty circle on the screen - no compile error, no DIAG line, nothing that
 *     failed.
 *
 * Usage:
 *   node tools/check-status-viewer.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { stripComments } = require('./csharp');

const ROOT = path.resolve(__dirname, '..');
const PANE = 'WhatsappApp/Controls/StatusPane.xaml.cs';
const PANE_XAML = 'WhatsappApp/Controls/StatusPane.xaml';
const ROW_MODEL = 'WhatsappApp/Models/ChatMessage.cs';
const VIEWER = 'WhatsappApp/Pages/StatusViewerPage.xaml.cs';
const FEED = 'WhatsappApp/Protocol/Client/StatusFeed.cs';
const MESSAGE = 'WhatsappApp/Protocol/Client/IncomingMessage.cs';
const DATA = 'WhatsappApp/Services/DataService.cs';
const CACHE = 'WhatsappApp/Services/StatusCache.cs';
const PAIRING = 'WhatsappApp/Pages/PairPage.xaml.cs';

// The receipt overload that can carry a `participant`, and the attribute it adds.
const CARRIES_PARTICIPANT =
  /Receipt\(string to, IList<string> ids, string type, uint timestamp, string participant\)[\s\S]{0,700}?Attrs\.Add\("participant", participant\)/;

/**
 * The body of a method: from its signature to the brace that closes it, with the
 * strings skipped so a `{` inside `string.Format`'s `"{0}"` is not the end of it.
 */
function methodBody(text, signature) {
  const at = text.indexOf(signature);
  if (at < 0) return null;

  let depth = 0;
  let started = false;
  for (let i = at + signature.length; i < text.length; i++) {
    const ch = text[i];
    if (ch === '"') {
      for (i++; i < text.length; i++) {
        if (text[i] === '\\') i++;
        else if (text[i] === '"') break;
      }
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

/** The status row's template: the block the list realises once per status. */
function rowTemplate(xaml) {
  const start = xaml.indexOf('<DataTemplate>');
  if (start < 0) return null;
  const end = xaml.indexOf('</DataTemplate>', start);
  if (end < 0) return null;
  return xaml.slice(start, end);
}

/** The property names a template's bindings ask for. */
function boundNames(template) {
  const names = new Set();
  const binding = /\{Binding\s+(?:Path\s*=\s*)?([A-Za-z_][A-Za-z0-9_]*)/g;
  for (const match of template.matchAll(binding)) names.add(match[1]);
  return names;
}

/** The public members (properties and fields) of the row model. */
function publicMembers(source) {
  const names = new Set();
  const declaration =
    /public\s+(?:static\s+)?([A-Za-z_][A-Za-z0-9_.<>\[\],? ]*?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:\r?\n\s*)?[{;=]/g;
  for (const match of stripComments(source).matchAll(declaration)) names.add(match[2]);
  return names;
}

/**
 * I problemi dei file, come frasi. Un array vuoto quando il percorso degli stati
 * e' intero.
 */
function statusViewerProblems(files) {
  const problems = [];
  const pane = String((files && files.pane) || '');
  const viewer = String((files && files.viewer) || '');
  const feed = String((files && files.feed) || '');
  const message = String((files && files.message) || '');
  const data = String((files && files.data) || '');
  const cache = String((files && files.cache) || '');
  const pairing = String((files && files.pairing) || '');
  const xaml = String((files && files.template) || '');
  const row = String((files && files.row) || '');

  // Rule 6: the row draws what the row has.
  const template = rowTemplate(xaml);
  if (template === null) {
    problems.push(PANE_XAML + ': the status row template is gone: this guard reads the ' +
      'bindings of the row from it, so a pane without one has no row to check');
  } else {
    const members = publicMembers(row);
    if (members.size < 10) {
      problems.push(ROW_MODEL + ': the guard found ' + members.size + ' public member(s) on a ' +
        'status row, which is too few to be the model: it can no longer tell a binding ' +
        'that resolves from one that never did');
    } else {
      for (const name of boundNames(template)) {
        if (members.has(name)) continue;
        problems.push(PANE_XAML + ': the status row binds ' + name + ', which ' + ROW_MODEL +
          ' does not have: the binding answers an error per realised row in the Output ' +
          'window and draws nothing on the screen, and nothing fails - the row keeps an ' +
          'empty circle where the author\'s initial belongs');
      }
    }
  }

  if (!pane.includes('Navigate(typeof(StatusViewerPage), row)')) {
    problems.push(PANE + ': a tap on a status must open the update ' +
      '(Navigate(typeof(StatusViewerPage), row)): marking it watched without showing it is ' +
      'the status listed and never seen, which is what the page exists to end');
  }

  if (!viewer.includes('MarkStatusViewed(_status)')) {
    problems.push(VIEWER + ': opening a status must watch it (MarkStatusViewed(_status)): the ' +
      'read receipt is what fills the author\'s ring, and it belongs to the page that shows ' +
      'the update, not to the list that only hands the row over');
  }
  if (!viewer.includes('RequestStatusMedia(_status)')) {
    problems.push(VIEWER + ': a status without its bytes must ask for them ' +
      '(RequestStatusMedia(_status)): a status out of the history sync arrives with its keys ' +
      'and its address and no file, so a page that only draws what it was given shows nothing');
  }

  if (!feed.includes('IncomingMessage.Receipt(BroadcastJid')) {
    problems.push(FEED + ': the view receipt must go to the broadcast list ' +
      '(IncomingMessage.Receipt(BroadcastJid, ...), not to the author): a status is not a ' +
      'chat, its message lives in status@broadcast, and a receipt addressed to the person ' +
      'names a message their one-to-one chat does not have - the ring never fills and nothing ' +
      'says so');
  }
  if (!/IncomingMessage\.Receipt\(BroadcastJid,[\s\S]{0,160}?author\)/.test(feed)) {
    problems.push(FEED + ': the view receipt must name the author as participant ' +
      '(the author is the last argument, after the timestamp): without it the list cannot ' +
      'say whose update was watched');
  }
  if (!CARRIES_PARTICIPANT.test(message)) {
    problems.push(MESSAGE + ': a receipt must be able to carry a participant ' +
      '(the Receipt overload with a participant argument adds Attrs.Add("participant", ' +
      'participant)): that attribute is how a broadcast list knows whose status a receipt is ' +
      'about');
  }

  if (!data.includes('StatusCache.LoadAsync()')) {
    problems.push(DATA + ': the status feed must be read back from the phone at start ' +
      '(StatusCache.LoadAsync()): the sync that fills it arrives once, at the pairing, so a ' +
      'feed that lives only in memory is empty at every start');
  }
  if (!data.includes('StatusCache.SaveAsync(')) {
    problems.push(DATA + ': the feed must be written to the phone when it changes ' +
      '(StatusCache.SaveAsync(...), from SaveStatusSnapshot): the next start draws it before ' +
      'any connection exists');
  }
  // The write is one per burst, not one per row (rule 5).
  const request = methodBody(data, 'private void SaveStatusSnapshot()');
  if (request == null) {
    problems.push(DATA + ': SaveStatusSnapshot is missing: nothing tells the phone that the ' +
      'feed changed');
  } else if (request.includes('StatusCache.SaveAsync(')) {
    problems.push(DATA + ': SaveStatusSnapshot writes the file itself: the history burst ' +
      'inserts a status per frame, so a write per insert rewrites the whole day once per ' +
      'status on a 512 MB phone - it must only count the change and let one writer coalesce it');
  }

  const writer = methodBody(data, 'private async Task WriteStatusSnapshotAsync()');
  if (writer == null) {
    problems.push(DATA + ': WriteStatusSnapshotAsync is missing: the snapshot has no writer');
  } else {
    if (!writer.includes('Task.Delay(StatusSnapshotQuietMilliseconds)')) {
      problems.push(DATA + ': the status snapshot writer does not wait for the feed to settle ' +
        '(Task.Delay(StatusSnapshotQuietMilliseconds)): one write per inserted status is the ' +
        'burst serialising the whole feed dozens of times');
    }

    // A window of zero is the burst again, with extra steps.
    const quiet = /private const int StatusSnapshotQuietMilliseconds = (\d+);/.exec(data);
    if (!quiet) {
      problems.push(DATA + ': StatusSnapshotQuietMilliseconds is missing: the writer needs a '
        + 'quiet window to wait for');
    } else if (Number(quiet[1]) < 200 || Number(quiet[1]) > 5000) {
      problems.push(DATA + ': StatusSnapshotQuietMilliseconds is ' + quiet[1] + ' ms: too short '
        + 'and the burst is a write per status again, too long and a feed the app has left has '
        + 'not reached the phone');
    }
    if (!/while \(version != _statusSnapshotRequested\)/.test(writer)) {
      problems.push(DATA + ': the status snapshot writer does not re-arm when a status arrives ' +
        'during its quiet window (while (version != _statusSnapshotRequested)): the row that ' +
        'made it wait would be the one left out of the file');
    }
    if (!writer.includes('StatusCache.SaveAsync(snapshot)')) {
      problems.push(DATA + ': the status snapshot writer never calls StatusCache.SaveAsync(' +
        'snapshot): the coalesced path must end in the same atomic write');
    }
  }

  // The feed is read newest first, and the history's chunk is a whole day at once.
  if (/_statuses\.Insert\(0/.test(data)) {
    problems.push(DATA + ': a status is inserted at the top of the feed instead of where its ' +
      'time says: the history re-sends a whole day in chunks, so yesterday\'s update would sit ' +
      'above the one posted a minute ago (InsertStatusInOrder)');
  }
  // The declaration alone is not a use: the rows must go through it.
  const orderedCalls = data.replace(/private void InsertStatusInOrder\(ChatMessage row\)/g, '');
  if (!orderedCalls.includes('InsertStatusInOrder(')) {
    problems.push(DATA + ': the status feed has no ordered insertion (InsertStatusInOrder): ' +
      'the order a person reads is then the order the frames happened to arrive in');
  }

  if (!pairing.includes('StatusCache.DeleteAsync()')) {
    problems.push(PAIRING + ': linking a different account must drop the old day of statuses ' +
      '(StatusCache.DeleteAsync()): they belong to the identity that was there before');
  }

  // The day a status lives: once on the way in, once on the way out.
  const cutoffs = (cache.match(/AddHours\(-LifetimeHours\)/g) || []).length;
  if (cutoffs < 2) {
    problems.push(CACHE + ': the copy must drop what is older than the day a status lives, ' +
      'both when it is read and when it is written (AddHours(-LifetimeHours) in LoadAsync and ' +
      'SaveAsync): a cache that keeps yesterday draws updates WhatsApp itself has forgotten');
  }

  return problems;
}

function main() {
  const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

  const problems = statusViewerProblems({
    pane: read(PANE),
    viewer: read(VIEWER),
    feed: read(FEED),
    message: read(MESSAGE),
    data: read(DATA),
    cache: read(CACHE),
    pairing: read(PAIRING),
    template: read(PANE_XAML),
    row: read(ROW_MODEL)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} status-viewer problem(s).`);
    process.exit(1);
  }

  console.log('OK: a status tap opens the update, the page watches it and asks for the bytes it '
    + 'does not have, the receipt goes to the broadcast list naming the author, the row binds '
    + 'only members its model has, and the feed survives a restart for the day a status lives '
    + '- written once per burst, not once per status.');
}

if (require.main === module) main();

module.exports = { statusViewerProblems, rowTemplate, boundNames, publicMembers };
