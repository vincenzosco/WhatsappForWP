#!/usr/bin/env node
/**
 * tools/check-save-soon.js
 *
 * Guard on when the chat list and a conversation are written to the phone.
 *
 * Why it exists: the only writer of the list and of the tails was `SaveSnapshotAsync`, called
 * at the end of a history sync, by `BackgroundSync`, and by `App.OnSuspending`. On Windows
 * Phone a suspension is often not a suspension: the process is terminated for memory or
 * closed from the task switcher, `OnSuspending` never runs, and every message the run had read
 * died with it - while WhatsApp had counted them as delivered and never sent them again. The
 * phone showed the conversations stopping at the last clean close, in the
 * list and inside the chat.
 *
 * The rule: a live message arms a debounced snapshot, and that debounce restarts its clock
 * instead of beating. Losing the guard means going back to "the write happens at suspension",
 * which is the bug.
 *
 * Usage:
 * node tools/check-save-soon.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const SERVICE = 'WhatsappApp/Services/DataService.cs';
const HELPER = 'WhatsappApp/Services/SaveSoon.cs';
const APP = 'WhatsappApp/App.xaml.cs';

/** The body of a method, from its signature to the first closing brace line. */
function methodBody(text, signature, limit) {
  const at = text.indexOf(signature);
  if (at < 0) return '';

  const rest = text.slice(at + signature.length);
  const end = rest.indexOf('\n        }');
  const body = end < 0 ? rest : rest.slice(0, end);

  return limit && body.length > limit ? body.slice(0, limit) : body;
}

/**
 * The problems of the three files, as sentences. Empty when nothing is wrong.
 */
function saveSoonProblems(files) {
  const problems = [];

  const helper = String((files && files.helper) || '').replace(/\r\n/g, '\n');
  const service = String((files && files.service) || '').replace(/\r\n/g, '\n');
  const app = String((files && files.app) || '').replace(/\r\n/g, '\n');

  // 1. The debounce restarts its clock. A timer that is not stopped before it starts is a
  // beat: one snapshot every interval for as long as the app is open.
  const arm = methodBody(helper, 'public void Arm()');
  if (!arm) {
    problems.push(`${HELPER}: there is no Arm() - nothing would schedule the write`);
  } else if (!/_timer\.Stop\(\)/.test(arm) || !/_timer\.Start\(\)/.test(arm)) {
    problems.push(`${HELPER}: Arm() must stop the timer before starting it, or a burst of ` +
      'messages writes the snapshot on the first one\'s deadline');
  }

  // 2. The timer is stopped when it fires: the same rule from the other side.
  const tick = methodBody(helper, 'private void OnTick(');
  if (!tick) {
    problems.push(`${HELPER}: there is no OnTick() - nothing fires`);
  } else if (!/_timer\.Stop\(\)/.test(tick)) {
    problems.push(`${HELPER}: OnTick() must stop the timer: a tick that leaves it running ` +
      'writes the snapshot forever');
  } else if (!/Guarded\.RunGuardedAsync/.test(tick)) {
    problems.push(`${HELPER}: OnTick() runs the write unguarded - a throw there takes the ` +
      'thread the connection is reading on');
  }

  // 3. The service builds it over the one writer it already had.
  if (!/_saveSoon\s*=\s*new SaveSoon\([^;]*SaveSnapshotAsync/.test(service)) {
    problems.push(`${SERVICE}: _saveSoon is not built over SaveSnapshotAsync - the debounced ` +
      'path and the suspension would write two different things');
  }

  // 4. A live message arms it. The history branch returns before this point on purpose: a sync
  // already ends with a snapshot of its own.
  const received = methodBody(service, 'private async void OnNetworkMessageReceived(');
  if (!received) {
    problems.push(`${SERVICE}: OnNetworkMessageReceived is missing - the message path this ` +
      'guard knows has gone');
  } else if (!/_saveSoon\.Arm\(\)/.test(received)) {
    problems.push(`${SERVICE}: OnNetworkMessageReceived never arms the snapshot - a message ` +
      'that arrived in this run is on the phone only if a suspension happens to run');
  } else {
    const inserted = received.indexOf('TrimToCeiling(conversation)');
    const armed = received.indexOf('_saveSoon.Arm()');
    if (inserted < 0 || armed < inserted) {
      problems.push(`${SERVICE}: the arm sits before the message is in the conversation - ` +
        'the snapshot would be written for a list that does not have it yet');
    }
  }

  // 5. The suspension still flushes, and immediately: the debounce is the safety net, not a
  // replacement for saying the list before the process is frozen.
  if (!/SaveSnapshotAsync\(\)/.test(app)) {
    problems.push(`${APP}: the suspension no longer writes the snapshot - the debounce would ` +
      'be the only writer, and it is the one that may never fire');
  }

  return problems;
}

function main() {
  const read = (name) => {
    try {
      return fs.readFileSync(path.join(ROOT, name), 'utf8');
    } catch (ex) {
      return '';
    }
  };

  const problems = saveSoonProblems({
    helper: read(HELPER),
    service: read(SERVICE),
    app: read(APP)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} save problem(s).`);
    process.exit(1);
  }

  console.log('OK: a message arms a debounced snapshot, and the suspension still flushes.');
}

if (require.main === module) main();

module.exports = { saveSoonProblems, methodBody };
