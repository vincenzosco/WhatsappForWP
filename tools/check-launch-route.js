#!/usr/bin/env node
/**
 * tools/check-launch-route.js
 *
 * Guard for the road from a tap on a tile to the section that has something to
 * show.
 *
 * Why it exists: on WP8.1 a tap on the app's tile - and on the lock screen,
 * which shows the same tile - arrives as a plain launch, with the empty
 * arguments the app list sends too. Nothing in the launch says where the tap
 * was: the only thing the app knows is what the phone was showing, the route
 * the notification layer wrote when it drew the badge. Every piece of that
 * chain is mute when it is missing:
 *
 * - without `RefreshLaunchRoute` the route is never written;
 * - without `TakeRoute` in `App.MapInitialUri` the route stays on the phone
 *   forever;
 * - without `MarkOpened` the section does not know it was looked at, and the
 *   same route stays true forever: that is how a shortcut becomes a hijack.
 *
 * Rules:
 * 1. `App.MapInitialUri` takes the route once and **names it in the log**;
 *    `OnLaunching` starts the Silverlight navigation;
 * 2. a launch lands on the chats: `HomePage.OnNavigatedTo` shows index 0, and
 *    nobody can move it to another tab - `SessionService.PendingSection` is
 *    gone (28 October 2026: every launch opened the Status tab, and the ask
 *    was "start on the status tab and not on chats");
 * 3. `HomePage` marks the section it shows as opened;
 * 4. `DataService` writes the route when it publishes the badge, and rewrites
 *    it when the states change and when the call scan ends;
 * 5. `LaunchRoute.cs` does not name `Windows.` - it is the file the runner
 *    compiles, and it is the rule the tests check
 *    (`ProtocolTests/LaunchRouteTests.cs`);
 * 6. a tap on a pinned chat tile opens that chat **directly**: the mapper
 *    routes the frame to the chat page, the chat serves the request once
 *    (a fresh arrival opens it, a replay drops it), and the way back leads
 *    to the chats with the tile route out of the journal.
 *
 * Usage:
 * node tools/check-launch-route.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = 'WhatsappApp/App.xaml.cs';
const HOME = 'WhatsappApp/Pages/HomePage.xaml.cs';
const CHAT = 'WhatsappApp/Pages/ChatPage.xaml.cs';
const DATA = 'WhatsappApp/Services/DataService.cs';
const ROUTE = 'WhatsappApp/Services/LaunchRoute.cs';
const SESSION = 'WhatsappApp/Services/SessionService.cs';

/** The body of a method, from its signature to the brace that closes it. */
function methodBody(text, signature) {
  const start = text.indexOf(signature);
  if (start < 0) return null;

  const open = text.indexOf('{', start);
  if (open < 0) return null;

  let depth = 0;
  for (let i = open; i < text.length; i++) {
    if (text[i] === '{') depth++;
    else if (text[i] === '}') {
      depth--;
      if (depth === 0) return text.slice(open, i + 1);
    }
  }
  return null;
}

/**
 * The problems, given the file texts. Pure: the tests pass it tampered copies.
 */
function launchRouteProblems(files) {
  const problems = [];

  // 1. Silverlight launches through PhoneApplicationService.Launching and AppUriMapper.
  const launching = methodBody(files.app, 'private void OnLaunching(') || '';
  const mapped = methodBody(files.app, 'private Uri MapInitialUri(') || '';
  if (!launching || !/EnsureStartupPage\s*\(\s*\)/.test(launching)) {
    problems.push('WhatsappApp/App.xaml.cs: OnLaunching does not start the Silverlight frame navigation');
  }
  if (!mapped) {
    problems.push('WhatsappApp/App.xaml.cs: no MapInitialUri - the Silverlight launch has no route decision');
  } else {
    if (!/SessionService\.TakeRoute\s*\(/.test(mapped)) {
      problems.push('WhatsappApp/App.xaml.cs: MapInitialUri never calls SessionService.TakeRoute() - ' +
        'a tile tap arrives as a plain launch and the route it left is the only thing that says where it was');
    }
    if (!/Diag\.Ok\s*\([^)]*route/.test(mapped)) {
      problems.push('WhatsappApp/App.xaml.cs: MapInitialUri takes the route and never names it - ' +
        'a tap that lands somewhere unexpected has to be readable in diag.log');
    }
    if (/SessionService\.PendingSection/.test(mapped)) {
      problems.push('WhatsappApp/App.xaml.cs: MapInitialUri turns the route into a pending section - ' +
        'a start is the chats (28 October 2026: every launch opened the Status tab)');
    }
    if (!/StartDecision\.Decide\s*\(/.test(mapped)
        || !/StartTarget\.Pair\s*\?\s*PairPageUri\s*:\s*HomePageUri/.test(mapped)) {
      problems.push('WhatsappApp/App.xaml.cs: a session-less Silverlight launch does not map to PairPage');
    }
  }

  // 2. A launch is the chat list, and there is no pending section left to overrule it.
  const navigatedTo = methodBody(files.home, 'protected override void OnNavigatedTo(') || '';
  if (!/Show\s*\(\s*0\s*,\s*true\s*\)/.test(navigatedTo)) {
    problems.push('WhatsappApp/Pages/HomePage.xaml.cs: OnNavigatedTo does not show the chats (Show(0, true)) - ' +
      'a start has to land on the conversations, whatever tab the app was left on');
  }
  if (!/SessionService\.MarkOpened\s*\(/.test(files.home)) {
    problems.push('WhatsappApp/Pages/HomePage.xaml.cs: the section shown is never passed to ' +
      'SessionService.MarkOpened() - "what arrived since the last look" never moves forward, and the ' +
      'tile keeps advertising what was already read');
  }
  if (/PendingSection/.test(files.home) || /PendingSection/.test(files.session)) {
    problems.push('WhatsappApp/Pages/HomePage.xaml.cs: PendingSection is back - ' +
      'a section in waiting is a start that does not open on the chats');
  }

  // 3. The route is written with the badge, and rewritten when something without a badge changes.
  const published = methodBody(files.data, 'private void PublishBadge(');
  if (!published) {
    problems.push('WhatsappApp/Services/DataService.cs: no PublishBadge() - ' +
      'the count and the route of a tap are published in one place, and that place is gone');
  } else if (!/RefreshLaunchRoute\s*\(\s*\)/.test(published)) {
    problems.push('WhatsappApp/Services/DataService.cs: PublishBadge() does not call RefreshLaunchRoute() - ' +
      'the badge changes and the phone still says the previous tap destination');
  }
  const badges = files.data.match(/NotificationService\.SetUnread\s*\(\s*TotalUnread\s*\(\s*\)\s*\)/g);
  if (badges && badges.length > 1) {
    problems.push('WhatsappApp/Services/DataService.cs: ' + badges.length + ' raw ' +
      'NotificationService.SetUnread(TotalUnread()) calls - the count and the route are the same fact ' +
      'and are published together by PublishBadge()');
  }
  if (!/RefreshLaunchRoute\s*\(\s*\)/.test(methodBody(files.data, 'private void RaiseStatusesChanged(') || '')) {
    problems.push('WhatsappApp/Services/DataService.cs: RaiseStatusesChanged does not refresh the route - ' +
      'a status that arrives has no badge, so the tile would never point at the updates');
  }
  if (!/RefreshLaunchRoute\s*\(\s*\)/.test(methodBody(files.data, 'private void RaiseCallsScanCompleted(') || '')) {
    problems.push('WhatsappApp/Services/DataService.cs: RaiseCallsScanCompleted does not refresh the route - ' +
      'a missed call arrives with the log and nothing else');
  }

  // 4. The rule stays compilable by the desktop runner.
  if (!/LaunchRoute/.test(files.route)) {
    problems.push('WhatsappApp/Services/LaunchRoute.cs: gone - the rule has no home');
  } else {
    const winrt = /^\s*using\s+Windows\./m.exec(files.route);
    if (winrt) {
      problems.push('WhatsappApp/Services/LaunchRoute.cs: uses ' + winrt[0].trim() + ' - ' +
        'ProtocolTests compiles this file for the desktop and the rule would stop being testable');
    }
  }

  // 5. A tile that names a chat opens that chat, straight and served once.
  if (!/return new Uri\(ChatPageUri/.test(mapped)) {
    problems.push('WhatsappApp/App.xaml.cs: MapInitialUri never routes a tile tap to the chat page - ' +
      'the tap names the conversation and a stop at the chat list first flashes a page the user never asked for');
  }
  const chatLanded = methodBody(files.chat, 'protected override void OnNavigatedTo(') || '';
  if (!/SessionService\.PendingChatId/.test(chatLanded)
      || !/NavigationMode\.New/.test(chatLanded)
      || !/ClearPendingChat\(\)/.test(chatLanded)) {
    problems.push('WhatsappApp/Pages/ChatPage.xaml.cs: OnNavigatedTo does not serve the tile\'s request ' +
      'once (PendingChatId, only on a new arrival, cleared) - the frame replays the tile route on the way back ' +
      'and the back key would reopen the chat it just left');
  }
  const chatBack = methodBody(files.chat, 'protected override void OnBackKeyPress(') || '';
  if (!/RemoveBackEntry\(\)/.test(chatBack) || !/HomePage\.xaml/.test(chatBack)) {
    problems.push('WhatsappApp/Pages/ChatPage.xaml.cs: OnBackKeyPress does not lead a tile-opened chat back ' +
      'to the chats with RemoveBackEntry() - the tile route would sit in the journal and replay into a reopen');
  }
  const served = methodBody(files.app, 'private void OpenPendingChat(') || '';
  if (!/NavigateToPage\("ChatPage"/.test(served)) {
    problems.push('WhatsappApp/App.xaml.cs: OpenPendingChat does not open the chat directly - ' +
      'the activation case has no landing event and this is what serves the tap');
  }

  return problems;
}

function main() {
  const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
  const problems = launchRouteProblems({
    app: read(APP),
    home: read(HOME),
    chat: read(CHAT),
    data: read(DATA),
    route: read(ROUTE),
    session: read(SESSION)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} problem(s) with the route a tap on the tile takes.`);
    process.exit(1);
  }
  console.log('OK: the badge records what the tile is showing, the route is named once at the launch, '
    + 'and every start lands on the chats.');
}

if (require.main === module) main();

module.exports = { launchRouteProblems, methodBody };
