#!/usr/bin/env node
/**
 * tools/check-background-task.js
 *
 * Guard per il task che il telefono deve poter istanziare.
 *
 * Perche' esiste: su WP8.1 un task di sfondo non puo' stare nell'assembly
 * dell'app. Il sistema lo istanzia in un processo suo, a partire da una classe
 * che la manifest dichiara come attivabile; se la classe e' in WhatsappApp.exe
 * non c'e' niente da istanziare, e il fallimento e' **muto**: la registrazione
 * riesce, il log scrive "the check runs every 15 minute(s)", e poi nessun
 * "background: woke" compare mai. E' successo per due build, il 22 ottobre 2026,
 * con l'app ferma sulle 18:45 e il log identico per due ore.
 *
 * Tre nomi devono essere lo stesso, e nessuno dei tre si lamenta da solo:
 *
 *  1. `EntryPoint` di `<Extension Category="windows.backgroundTasks">`;
 *  2. `ActivatableClassId` del server in-process (`CLRHost.dll`: la classe e'
 *     gestita, la carica l'host del CLR dalla winmd del componente);
 *  3. il nome completo del tipo che implementa `IBackgroundTask`, namespace
 *     compreso, nel progetto del componente.
 *
 * E, perche' la registrazione chieda davvero quella classe, anche
 * `BackgroundSync.EntryPoint` deve dire lo stesso: e' il valore che finisce in
 * `BackgroundTaskBuilder.TaskEntryPoint`.
 *
 * Regole:
 *  1. la manifest dichiara il task `timer` e la classe attivabile con Path
 *     `CLRHost.dll`;
 *  2. l'`EntryPoint` del task, l'`ActivatableClassId` e il tipo compilato nel
 *     componente sono la stessa stringa;
 *  3. `BackgroundSync.EntryPoint` e' quella stessa stringa;
 *  4. il file del task e' un `<Compile Include>` del progetto del componente.
 *
 * Usage:
 *   node tools/check-background-task.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const MANIFEST = 'WhatsappApp/Package.appxmanifest';
const TASK = 'WhatsappApp.Tasks/BackgroundSyncTask.cs';
const TASK_PROJECT = 'WhatsappApp.Tasks/WhatsappApp.Tasks.csproj';
const SYNC = 'WhatsappApp/Services/BackgroundSync.cs';

/** La stringa cercata in un attributo, o null. */
function attribute(text, name) {
  const match = new RegExp(name + '="([^"]+)"').exec(text);
  return match ? match[1] : null;
}

/** Il valore letto da una `const string` del sorgente, o null. */
function constString(source, name) {
  const match = new RegExp('const\\s+string\\s+' + name + '\\s*=\\s*"([^"]+)"').exec(source);
  return match ? match[1] : null;
}

/** Il nome completo del tipo che implementa IBackgroundTask, o null. */
function taskType(source) {
  const namespace = /namespace\s+([\w.]+)/.exec(source);
  const type = /class\s+(\w+)\s*:\s*IBackgroundTask/.exec(source);
  if (!namespace || !type) return null;
  return namespace[1] + '.' + type[1];
}

/**
 * I problemi, dati i testi dei file. Puro: i test gli passano copie manomesse.
 */
function backgroundTaskProblems(files) {
  const problems = [];
  const manifest = files.manifest;

  // L'estensione del task, con il tipo che il sistema deve istanziare.
  const extensions = manifest.match(/<Extension\s+Category="windows\.backgroundTasks"[\s\S]*?<\/Extension>/);
  if (!extensions) {
    problems.push('Package.appxmanifest: no <Extension Category="windows.backgroundTasks"> - ' +
      'the system has no task of ours to wake');
    return problems;
  }

  const entryPoint = attribute(extensions[0], 'EntryPoint');
  if (!entryPoint) {
    problems.push('Package.appxmanifest: the backgroundTasks extension has no EntryPoint');
  }
  if (!/<Task\s+Type="timer"/.test(extensions[0])) {
    problems.push('Package.appxmanifest: the backgroundTasks extension is not <Task Type="timer"> - ' +
      'a TimeTrigger registration has nothing it may run');
  }

  // La dichiarazione che rende la classe istanziabile.
  const server = manifest.match(/<Extension\s+Category="windows\.activatableClass\.inProcessServer"[\s\S]*?<\/Extension>/);
  if (!server) {
    problems.push('Package.appxmanifest: no <Extension Category="windows.activatableClass.inProcessServer"> - ' +
      'the class exists, but nothing tells the system how to create it, so the wake never happens ' +
      '(this is the failure of 22 October 2026)');
  } else {
    const serverPath = /<Path>\s*([^<\s]+)\s*<\/Path>/.exec(server[0]);
    if (!serverPath) {
      problems.push('Package.appxmanifest: the in-process server has no <Path>');
    } else if (serverPath[1].toLowerCase() !== 'clrhost.dll') {
      problems.push('Package.appxmanifest: the in-process server points at "' + serverPath[1] +
        '" instead of CLRHost.dll - a managed WinRT class is loaded by the CLR host, and an ' +
        'executable here means the class can never be created');
    }

    const activatable = attribute(server[0], 'ActivatableClassId');
    if (!activatable) {
      problems.push('Package.appxmanifest: the in-process server has no ActivatableClassId');
    } else if (entryPoint && activatable !== entryPoint) {
      problems.push('Package.appxmanifest: the task EntryPoint is "' + entryPoint +
        '" but the activatable class is "' + activatable + '" - the system registers one name ' +
        'and can only create the other');
    }
  }

  // Il tipo compilato nel componente.
  const type = taskType(files.task);
  if (!type) {
    problems.push('WhatsappApp.Tasks/BackgroundSyncTask.cs: no class implementing IBackgroundTask ' +
      '(or no namespace) - there is nothing for the manifest to name');
  } else if (entryPoint && type !== entryPoint) {
    problems.push('WhatsappApp.Tasks/BackgroundSyncTask.cs: the class is "' + type +
      '" but the manifest names "' + entryPoint + '"');
  }

  // Il tipo deve essere compilato nel componente, non solo scritto.
  if (!/<Compile\s+Include="[^"]*BackgroundSyncTask\.cs"/.test(files.taskProject)) {
    problems.push('WhatsappApp.Tasks/WhatsappApp.Tasks.csproj: BackgroundSyncTask.cs is not a ' +
      '<Compile Include> - the class is not in the component the manifest points at');
  }

  // E la registrazione deve chiedere quella classe.
  const registered = constString(files.sync, 'EntryPoint');
  if (!registered) {
    problems.push('WhatsappApp/Services/BackgroundSync.cs: no EntryPoint const - ' +
      'the registration has no name to ask for');
  } else if (entryPoint && registered !== entryPoint) {
    problems.push('WhatsappApp/Services/BackgroundSync.cs: EntryPoint is "' + registered +
      '" but the manifest names "' + entryPoint + '"');
  }

  return problems;
}

function main() {
  const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
  const problems = backgroundTaskProblems({
    manifest: read(MANIFEST),
    task: read(TASK),
    taskProject: read(TASK_PROJECT),
    sync: read(SYNC)
  });

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} problem(s) with the background task.`);
    process.exit(1);
  }
  console.log('OK: the manifest, the component and the registration name the same task.');
}

if (require.main === module) main();

module.exports = { backgroundTaskProblems, taskType, constString, attribute };
