#!/usr/bin/env node
/**
 * Guard for the Silverlight scheduled agent that checks for messages while the app is closed.
 *
 * WP8.1 Silverlight uses ScheduledTaskAgent and PeriodicTask, not WinRT IBackgroundTask,
 * BackgroundExecutionManager or activatable in-process servers. The agent class is named by
 * WMAppManifest.xml and runs in its own process. Scheduled agents cannot run an OnCancel
 * callback, so the task records a running marker and the next invocation reports the previous
 * LastExitReason.
 *
 * The guard keeps the app manifest, the scheduler, the agent assembly, and its entry point in
 * agreement. It also ensures revoked registrations are noticed through ScheduledAction.IsScheduled
 * and that a periodic registration uses the platform's 30-minute cadence.
 *
 * Usage: node tools/check-background-task.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const MANIFEST = 'WhatsappApp/Properties/WMAppManifest.xml';
const TASK = 'WhatsappApp.Tasks/BackgroundSyncTask.cs';
const TASK_PROJECT = 'WhatsappApp.Tasks/WhatsappApp.Tasks.csproj';
const SYNC = 'WhatsappApp/Services/BackgroundSync.cs';
const SETTINGS = 'WhatsappApp/Pages/SettingsPage.xaml.cs';
const SETTINGS_XAML = 'WhatsappApp/Pages/SettingsPage.xaml';

function constString(source, name) {
  const match = new RegExp('const\\s+string\\s+' + name + '\\s*=\\s*"([^"]+)"').exec(source);
  return match ? match[1] : null;
}

function taskType(source) {
  const namespace = /namespace\s+([\w.]+)/.exec(source);
  const type = /class\s+(\w+)\s*:\s*ScheduledTaskAgent/.exec(source);
  if (!namespace || !type) return null;
  return namespace[1] + '.' + type[1];
}

function backgroundTaskProblems(files) {
  const problems = [];
  const manifest = files.manifest;
  const agent = /<BackgroundServiceAgent\b[^>]*\/>/.exec(manifest);
  if (!agent) {
    problems.push('WMAppManifest.xml: no BackgroundServiceAgent - the phone has no Silverlight scheduled task to wake');
  } else {
    const type = /\bType="([^"]+)"/.exec(agent[0]);
    const name = /\bName="([^"]+)"/.exec(agent[0]);
    if (!type) problems.push('WMAppManifest.xml: BackgroundServiceAgent has no Type');
    if (!name) problems.push('WMAppManifest.xml: BackgroundServiceAgent has no Name');
    const compiledType = taskType(files.task);
    if (!compiledType) {
      problems.push('WhatsappApp.Tasks/BackgroundSyncTask.cs: no class derives from ScheduledTaskAgent');
    } else if (type && compiledType !== type[1]) {
      problems.push('WhatsappApp.Tasks/BackgroundSyncTask.cs: the class is "' + compiledType +
        '" but WMAppManifest.xml names "' + type[1] + '"');
    }
    if (type && files.sync && constString(files.sync, 'EntryPoint') !== type[1]) {
      problems.push('WhatsappApp/Services/BackgroundSync.cs: EntryPoint does not match the manifest agent type');
    }
    if (name && files.sync && constString(files.sync, 'TaskName') !== name[1]) {
      problems.push('WhatsappApp/Services/BackgroundSync.cs: TaskName does not match the manifest agent name');
    }
  }

  if (!/BackgroundAgentType>AgentLibrary</.test(files.taskProject)) {
    problems.push('WhatsappApp.Tasks/WhatsappApp.Tasks.csproj: BackgroundAgentType must be AgentLibrary');
  }
  if (!/<Compile\s+Include="[^\"]*BackgroundSyncTask\.cs"/.test(files.taskProject)) {
    problems.push('WhatsappApp.Tasks/WhatsappApp.Tasks.csproj: BackgroundSyncTask.cs is not compiled into the agent library');
  }
  if (!/class\s+BackgroundSyncTask\s*:\s*ScheduledTaskAgent/.test(files.task)) {
    problems.push('WhatsappApp.Tasks/BackgroundSyncTask.cs: the task must derive from ScheduledTaskAgent');
  }
  if (!/protected\s+override\s+void\s+OnInvoke\s*\(\s*ScheduledTask\s+task\s*\)/.test(files.task)) {
    problems.push('WhatsappApp.Tasks/BackgroundSyncTask.cs: the scheduled agent has no OnInvoke(ScheduledTask)');
  }
  if (!/NotifyComplete\s*\(\s*\)/.test(files.task)) {
    problems.push('WhatsappApp.Tasks/BackgroundSyncTask.cs: OnInvoke does not call NotifyComplete()');
  }
  if (!/new\s+PeriodicTask\s*\(\s*TaskName\s*\)/.test(files.sync)) {
    problems.push('WhatsappApp/Services/BackgroundSync.cs: the scheduler does not register a PeriodicTask');
  }
  if (!/IntervalMinutes\s*=\s*30/.test(files.sync)) {
    problems.push('WhatsappApp/Services/BackgroundSync.cs: Silverlight PeriodicTask cadence must be 30 minutes');
  }
  if (!/ScheduledActionService\.Find\s*\(\s*TaskName\s*\)/.test(files.sync)
      || !/action\.IsScheduled/.test(files.sync)) {
    problems.push('WhatsappApp/Services/BackgroundSync.cs: registration availability must use ScheduledAction.IsScheduled');
  }
  if (!/RegisteredEntryPoint\(\)\s*==\s*EntryPoint\s*&&\s*IsPermitted\(\)/.test(files.sync)) {
    problems.push('WhatsappApp/Services/BackgroundSync.cs: an existing registration is accepted without rechecking IsPermitted()');
  }
  if (!/BackgroundSync\.IsPermitted\s*\(\s*\)/.test(files.settings)) {
    problems.push('WhatsappApp/Pages/SettingsPage.xaml.cs: the settings page never asks BackgroundSync.IsPermitted()');
  }
  if (!/x:Name="BackgroundState"/.test(files.settingsXaml)) {
    problems.push('WhatsappApp/Pages/SettingsPage.xaml: no BackgroundState element explains a refused task');
  }

  return problems;
}

function main() {
  const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
  const problems = backgroundTaskProblems({
    manifest: read(MANIFEST),
    task: read(TASK),
    taskProject: read(TASK_PROJECT),
    sync: read(SYNC),
    settings: read(SETTINGS),
    settingsXaml: read(SETTINGS_XAML)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log('\n' + problems.length + ' problem(s) with the scheduled agent.');
    process.exit(1);
  }
  console.log('OK: the Silverlight manifest, scheduled agent, and periodic registration agree.');
}

if (require.main === module) main();
module.exports = { backgroundTaskProblems, taskType, constString };
