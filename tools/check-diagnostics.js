#!/usr/bin/env node
/**
 * tools/check-diagnostics.js
 *
 * Guard for the app's ability to be diagnosed on the phone.
 *
 * Why it exists: every crash investigated in this repo arrived as a log that
 * stopped at the assembly list with no DIAG line at all, and the reason was
 * never the crash - it was the log. `Diag` kept its history in a 200-line list
 * in memory, so the process that died took the evidence with it, and the run
 * that had to be explained was the one run whose log nobody could read. A phone
 * has no debugger attached, and the diagnostics page needs the app to still be
 * alive to show what it knows.
 *
 * So the run is recorded as it goes: the lines are appended to a capped file
 * through the same SerialQueue the other files use, a marker file says the run
 * is alive, and a marker file that is still there at the next startup is what a
 * crash looks like. The marker has to be a file of its own and not a line in the
 * log: OnSuspending is the normal end of a run on this platform, so "the log has
 * no end marker" is the normal case too, and a crash after a resume would be
 * missed by anything that only looked at the log.
 *
 * Rule D is here for the same reason - it guards the one constant that decides
 * whether opening a chat can go wrong at all. The list waited two seconds for
 * the adapter's `history.done` and the adapter's first read of the account was
 * measured at about fifteen, so the list was bound before its burst and the
 * fifty frames then inserted into a collection the ListView was already
 * watching: the exact failure `ConversationView.Bind` exists to prevent. The
 * floor below is that measurement, not a preference.
 *
 * Rules:
 *   A. `Diag.cs` writes through a `SerialQueue`, has a file name, a marker file
 *      name and a byte ceiling for the log;
 *   B. `App.xaml.cs` flushes the log inside the unhandled-exception handler,
 *      after the line is written and before the process may die;
 *   C. `Diag.cs` writes both run markers and deletes the marker file when the
 *      run ends on purpose;
 *   D. `ChatPage.xaml.cs` waits for the history at least as long as the
 *      adapter's cold read (20000 ms, measured at about 15 s).
 *
 * Usage: node tools/check-diagnostics.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const DIAG_REL = 'WhatsappApp/Services/Diag.cs';
const APP_REL = 'WhatsappApp/App.xaml.cs';
const CHAT_PAGE_REL = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const CRASH_REPORT_REL = 'WhatsappApp/Services/CrashReport.cs';

/**
 * The floor of rule D. It is the adapter's cold read of the account, measured on
 * the device run of 2026-10-07: the list must not be bound before the burst it
 * is waiting for, and a shorter wait is what binds it.
 */
const HISTORY_WAIT_FLOOR = 20000;

/**
 * The problems of a set of sources: empty when the app can still be diagnosed.
 * `input` holds the text of the three files.
 */
function problemsFor(input) {
  const problems = [];
  const diag = input.diag || '';
  const app = input.app || '';
  const chatPage = input.chatPage || '';

  // -------------------------------------------------------------------------
  // A. the log has a file, a ceiling and one writer
  // -------------------------------------------------------------------------
  if (!/new SerialQueue\(\)/.test(diag)) {
    problems.push(DIAG_REL + ': the log file is not written through a SerialQueue' +
      ' - two writes on one file overlap and the older snapshot can win, which is' +
      ' the defect ChatPreferences already had');
  }
  if (!/FileName\s*=\s*"diag\.log"/.test(diag)) {
    problems.push(DIAG_REL + ': no "diag.log" file name - the diagnostics are not' +
      ' written to disk, so a crash leaves nothing to read');
  }
  if (!/MaxBytes\s*=\s*\d+/.test(diag)) {
    problems.push(DIAG_REL + ': no MaxBytes ceiling - a log with no cap is a slow' +
      ' growth nobody sees, on a phone whose storage and memory are both small');
  }
  if (!/MarkerName\s*=\s*"[^"]+"/.test(diag)) {
    problems.push(DIAG_REL + ': no marker file name - without it a run that died' +
      ' cannot be told from a run that was suspended, which is the normal end of a' +
      ' run on this platform');
  }

  // -------------------------------------------------------------------------
  // B. the log is on disk before the process can die
  // -------------------------------------------------------------------------
  // The flush has to be in the unhandled handler's own body: an index search
  // from the line to the end of the file would accept the flush of the next
  // handler and let this one go without one.
  const unhandled = app.indexOf('Diag.Failed("App/unhandled"');
  const nextMember = unhandled < 0 ? -1 : app.indexOf('private ', unhandled + 1);
  const body = unhandled < 0 ? ''
    : app.slice(unhandled, nextMember < 0 ? app.length : nextMember);
  if (!/Diag\.Flush\(true\)/.test(body)) {
    problems.push(APP_REL + ': the App/unhandled handler does not flush the log' +
      ' after writing the line - the crash that has to be named is the one that' +
      ' dies before the in-memory history is ever read');
  }

  // -------------------------------------------------------------------------
  // C. the run markers, and the marker file the end of a run removes
  // -------------------------------------------------------------------------
  if (diag.indexOf('"=== run started "') < 0 || diag.indexOf('"=== run ended "') < 0) {
    problems.push(DIAG_REL + ': the run markers are gone - the file no longer says' +
      ' where one run of the app starts and ends');
  }
  if (diag.indexOf('DeleteAsync') < 0) {
    problems.push(DIAG_REL + ': the marker file is never deleted (no' +
      ' DeleteAsync on it) - every launch would report the previous run as a crash' +
      ' and the container log becomes noise');
  }

  // -------------------------------------------------------------------------
  // D. the history wait is not shorter than the read it waits for
  // -------------------------------------------------------------------------
  const wait = /HistoryWaitMaxMilliseconds\s*=\s*(\d+)/.exec(chatPage);
  if (!wait) {
    problems.push(CHAT_PAGE_REL + ': HistoryWaitMaxMilliseconds is gone (did the' +
      ' wait move? then this guard must move too)');
  } else if (Number(wait[1]) < HISTORY_WAIT_FLOOR) {
    problems.push(CHAT_PAGE_REL + ': HistoryWaitMaxMilliseconds = ' + wait[1] +
      ', below the ' + HISTORY_WAIT_FLOOR + ' ms floor: the adapter takes about' +
      ' 15 s to read the account the first time, so this wait expires first and' +
      ' binds the list before the history burst it is waiting for - the fifty' +
      ' frames then insert into a collection the ListView is already watching');
  }

  // -------------------------------------------------------------------------
  // E. the crash goes to the adapter by itself
  // -------------------------------------------------------------------------
  const crashReport = input.crashReport;
  if (typeof crashReport === 'string') {
    if (crashReport === '') {
      problems.push(CRASH_REPORT_REL + ': not found - the crash of the previous run' +
        ' is sent nowhere, so it is only ever visible on the phone screen the user' +
        ' is holding');
    } else {
      if (!/SendControlAsync\("diag"/.test(crashReport)) {
        problems.push(CRASH_REPORT_REL + ': the previous run is not sent to the' +
          ' adapter (no SendControlAsync("diag")) - the tail it left is the only' +
          ' copy of what it was doing when it died');
      }
      if (!/Diag\.PendingCrashTail/.test(crashReport)) {
        problems.push(CRASH_REPORT_REL + ': CrashReport does not read' +
          ' Diag.PendingCrashTail - the service would send nothing, which looks' +
          ' exactly like a run that ended properly');
      }
    }
  }

  return { problems };
}

function main() {
  const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
  const input = {
    diag: read(DIAG_REL),
    app: read(APP_REL),
    chatPage: read(CHAT_PAGE_REL),
    // Reported as a problem rather than read blindly: a missing service is the
    // finding rule E exists for, not a crash of the guard.
    crashReport: fs.existsSync(path.join(ROOT, CRASH_REPORT_REL))
      ? read(CRASH_REPORT_REL) : ''
  };
  const { problems } = problemsFor(input);

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log('\n' + problems.length + ' problem(s).');
    process.exit(1);
  }
  console.log('OK: the diagnostics reach the disk through one writer with a ' +
    'ceiling, the unhandled handler flushes them, the run keeps its marker until ' +
    'it ends on purpose, and the history wait is at least ' + HISTORY_WAIT_FLOOR +
    ' ms.');
}

if (require.main === module) main();

module.exports = {
  problemsFor, HISTORY_WAIT_FLOOR, DIAG_REL, APP_REL, CHAT_PAGE_REL, CRASH_REPORT_REL
};
