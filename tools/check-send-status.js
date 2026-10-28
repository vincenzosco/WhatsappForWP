#!/usr/bin/env node
/**
 * tools/check-send-status.js
 *
 * Guard on the tick next to a message this phone sent.
 *
 * Why it exists: on 2026-10-22 an operator wrote a message, the bubble showed
 * the tick of a sent message, and the message never arrived. The page was
 * answering from its own state - `MessageStatus.Sent` written the moment the
 * bytes were handed to the socket - while the one thing that knows whether
 * WhatsApp has the message, the server's `<ack>`, arrived at
 * `NativeConnection` and was dropped because nothing was waiting for that id.
 *
 * The two halves that keep it honest:
 *  1. `ChatPage` never writes `Sent`. The bubble keeps its circle after the
 *     write, and waits (WaitForAck) for the answer; no answer in twenty
 *     seconds turns it into a failure.
 *  2. `NativeConnection` remembers the id it wrote and turns the server's ack
 *     into a status update, which `DataService` writes as Sent - or as Failed
 *     when the ack is a refusal.
 *
 * Usage:
 *   node tools/check-send-status.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const PAGE = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const CONNECTION = 'WhatsappApp/Services/NativeConnection.cs';
const DATA = 'WhatsappApp/Services/DataService.cs';

/** The body of a method, from its signature to its closing brace (or 1200 chars). */
function methodBody(text, signature) {
  const at = text.indexOf(signature);
  if (at < 0) return '';

  const rest = text.slice(at + signature.length);
  const end = rest.indexOf('\n        }');
  return end < 0 ? rest.slice(0, 1200) : rest.slice(0, end);
}

/**
 * Whether a body waits for the ack - directly, or through a helper of this same file.
 *
 * Why a helper counts: the attachment path moved its upload into `UploadAndSendAsync`
 * so that the retry of a failed bubble sends the same file again instead of a second
 * copy of the lines. The rule is about the send path, not about which method holds it;
 * a helper that does not wait, or one that is not in this file, still fails the check.
 */
function waitsForAck(page, body) {
  if (!body) return false;
  if (body.includes('WaitForAck(')) return true;

  for (const m of body.matchAll(/\b([A-Za-z_][A-Za-z0-9_]*)\s*\(/g)) {
    const helper = methodBody(page, 'private async System.Threading.Tasks.Task ' + m[1]);
    if (helper && helper.includes('WaitForAck(')) return true;
  }

  return false;
}

/** Rule 1: the page waits for the ack and never decides "sent" itself. */
function pageProblems(page) {
  const problems = [];

  if (/Status\s*=\s*MessageStatus\.Sent/.test(page)) {
    problems.push(`${PAGE}: the page writes MessageStatus.Sent itself - "sent" is the ` +
      "server's answer for the id, not what handing bytes to the socket means; leave the " +
      'bubble on Sending and let WaitForAck and the ack decide');
  }

  const text = methodBody(page, 'private async void AddAndSendMessage');
  if (!text.includes('WaitForAck(')) {
    problems.push(`${PAGE}: AddAndSendMessage sends a text and never waits for the ack - a ` +
      'message the server never took would keep its circle, or worse, read as sent');
  }

  const attachment = methodBody(page, 'private async System.Threading.Tasks.Task SendAttachmentAsync');
  if (!waitsForAck(page, attachment)) {
    problems.push(`${PAGE}: SendAttachmentAsync sends a file and never waits for the ack`);
  }

  // The retry a failed bubble offers is a send path like any other, and it is the one
  // that matters most: it runs when the cell is flaky, which is when an ack is late.
  const retry = methodBody(page, 'private async System.Threading.Tasks.Task RetryAsync');
  if (!retry) {
    problems.push(`${PAGE}: RetryAsync is missing: a failed bubble has no way to be sent again`);
  } else if (!waitsForAck(page, retry)) {
    problems.push(`${PAGE}: RetryAsync sends and never waits for the ack`);
  }

  const wait = methodBody(page, 'private static async System.Threading.Tasks.Task WaitForAckAsync');
  if (!wait) {
    problems.push(`${PAGE}: WaitForAckAsync is missing: nothing turns a message the server ` +
      'never acked into a failure');
  } else if (!/Status\s*=\s*MessageStatus\.Failed/.test(wait)) {
    problems.push(`${PAGE}: WaitForAckAsync never marks the bubble Failed: a message no ack ` +
      'came for would wait forever');
  }

  return problems;
}

/** Rule 2: the ack is recognised on the wire and written on the bubble. */
function connectionProblems(connection) {
  const problems = [];
  const code = connection;

  // The declarations do not count. `private void AnnounceAck(...)` and
  // `public bool IsRefused;` satisfied these checks on their own, so deleting every call
  // site - the exact regression this guard exists for - still printed OK.
  const calls = code
    .replace(/private\s+void\s+(AnnounceAck|AwaitAck)\s*\([^)]*\)/g, '')
    .replace(/public\s+bool\s+IsRefused\s*;/g, '');

  if (!calls.includes('AnnounceAck(')) {
    problems.push(`${CONNECTION}: there is no AnnounceAck for a sent message - the stanza ` +
      'arrives, matches no waiter and is dropped, which is the 2026-10-22 bubble');
  }

  if (!calls.includes('AwaitAck(')) {
    problems.push(`${CONNECTION}: a sent message's id is never remembered (AwaitAck), so an ` +
      'ack cannot be told apart from the answer to a query');
  }

  if (!/public bool IsAck;/.test(code)) {
    problems.push(`${CONNECTION}: MessageStatusUpdate carries no ack flag: DataService cannot ` +
      'tell an ack from a receipt');
  }

  if (!/\.IsRefused\b/.test(calls)) {
    problems.push(`${CONNECTION}: a refusal (an ack with type="error") is not carried as ` +
      'IsRefused: a message the server rejected would stay on Sending until the wait ends');
  }

  return problems;
}

/** And the service on the other end turns it into the bubble's status. */
function dataProblems(data) {
  const problems = [];
  const handler = methodBody(data, 'private void OnNativeStatus(object');

  if (!handler.includes('IsAck')) {
    problems.push(`${DATA}: OnNativeStatus ignores IsAck: the ack reaches the handler and ` +
      'still cannot say Sent');
  }
  if (!handler.includes('MessageStatus.Sent')) {
    problems.push(`${DATA}: OnNativeStatus never writes MessageStatus.Sent: no message this ` +
      'phone sends can be shown as sent');
  }
  if (!handler.includes('MessageStatus.Failed')) {
    problems.push(`${DATA}: OnNativeStatus never writes MessageStatus.Failed for a refusal`);
  }

  return problems;
}

/**
 * The problems of the three files.
 * Returns an array of sentences, empty when nothing is wrong.
 */
function sendStatusProblems(files) {
  const page = String((files && files.page) || '');
  const connection = String((files && files.connection) || '');
  const data = String((files && files.data) || '');

  return pageProblems(page)
    .concat(connectionProblems(connection))
    .concat(dataProblems(data));
}

function main() {
  const problems = sendStatusProblems({
    page: fs.readFileSync(path.join(ROOT, PAGE), 'utf8'),
    connection: fs.readFileSync(path.join(ROOT, CONNECTION), 'utf8'),
    data: fs.readFileSync(path.join(ROOT, DATA), 'utf8')
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} send status problem(s).`);
    process.exit(1);
  }

  console.log('OK: a message says "sent" only when the server acks it, and says "failed" when '
    + 'the ack never comes or is a refusal.');
}

if (require.main === module) main();

module.exports = { sendStatusProblems, methodBody };
