#!/usr/bin/env node
/**
 * tools/check-project-files.js
 *
 * Guard for a file the solution cannot see.
 *
 * Perche' esiste: un file .cs che non compare in nessun progetto e'
 * invisibile a MSBuild, e nient'altro se ne accorge. La guardia C# 5 lo legge
 * (gira sui file, non sul progetto), i test dei tool passano, e il guasto esce
 * solo sulla build della VM - o, peggio, viene committato e la build successiva
 * riporta un tipo che "non esiste" da un file che c'e'. E' successo davvero a
 * Services/RecordingSession.cs e Services/ConversationView.cs: le loro voci
 * sono state rimosse da un `git checkout` del .csproj dopo una build.
 *
 * Regole:
 *  1. ogni .cs sotto WhatsappApp (obj/ e bin/ a parte) e' un <Compile Include>
 *     di WhatsApp.csproj oppure e' coperto da WhatsappApp.Core.csproj, che
 *     compila protocollo, servizi e modelli per tutti e due i processi (l'app e
 *     il task di sfondo) - vedi il commento in quel progetto;
 *  2. ogni .xaml e' un <Page> o <ApplicationDefinition> Include di WhatsApp.csproj;
 *  3. ogni .cs sotto WhatsappApp.Tasks e' un <Compile Include> del suo progetto.
 *
 * Usage:
 *   node tools/check-project-files.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');
const PROJECT = path.join(APP, 'WhatsApp.csproj');
const CORE_PROJECT = path.join(ROOT, 'WhatsappApp.Core', 'WhatsappApp.Core.csproj');
const TASKS = path.join(ROOT, 'WhatsappApp.Tasks');
const TASKS_PROJECT = path.join(TASKS, 'WhatsappApp.Tasks.csproj');

/** Ogni file di un albero, senza obj/ e bin/. */
function walk(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'obj' || entry.name === 'bin') continue;
      walk(path.join(dir, entry.name), out);
    } else {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

/** Il percorso di un Include, con gli slash in avanti come i file su disco. */
function normalize(include) {
  return include.replace(/\\/g, '/');
}

/** I due insiemi che il progetto dichiara. */
function includes(projectText) {
  const compile = new Set();
  const pages = new Set();
  for (const match of projectText.matchAll(/<Compile Include="([^"]+)"/g)) {
    compile.add(normalize(match[1]));
  }
  for (const match of projectText.matchAll(/<(?:Page|ApplicationDefinition) Include="([^"]+)"/g)) {
    pages.add(normalize(match[1]));
  }
  return { compile, pages };
}

/**
 * I file dell'app che un altro progetto compila, come percorsi relativi a
 * WhatsappApp: `..\WhatsappApp\Services\**\*.cs` copre tutta la cartella,
 * `..\WhatsappApp\Controls\AppSection.cs` un file solo.
 *
 * Il perche': WhatsappApp.Core non ha una copia di questi sorgenti, li compila
 * da qui (vedi il commento in WhatsappApp.Core.csproj). Per questa guardia
 * contano lo stesso: un file che un progetto compila non e' un file perduto.
 */
function coveredElsewhere(projectText) {
  const dirs = [];
  const files = new Set();
  for (const match of projectText.matchAll(/<Compile Include="([^"]+)"/g)) {
    const include = normalize(match[1]);
    const marker = '..\\WhatsappApp\\'.replace(/\\/g, '/');
    if (include.indexOf(marker) !== 0) continue;
    const rest = include.slice(marker.length);
    const wildcard = rest.indexOf('/**/');
    if (wildcard >= 0) dirs.push(rest.slice(0, wildcard));
    else files.add(rest);
  }
  return { dirs, files };
}

/** Il percorso relativo a WhatsappApp, con gli slash in avanti. */
function relative(file) {
  return path.relative(APP, file).replace(/\\/g, '/');
}

function main() {
  const app = includes(fs.readFileSync(PROJECT, 'utf8'));
  const core = coveredElsewhere(fs.readFileSync(CORE_PROJECT, 'utf8'));
  const tasks = includes(fs.readFileSync(TASKS_PROJECT, 'utf8'));
  const problems = [];

  for (const file of walk(APP, [])) {
    const rel = relative(file);
    if (file.endsWith('.cs') && !app.compile.has(rel)) {
      const inCore = core.files.has(rel) || core.dirs.some((d) => rel.indexOf(d + '/') === 0);
      if (!inCore) {
        problems.push(`WhatsappApp/${rel}: not in WhatsApp.csproj as a <Compile Include> ` +
          'and not compiled by WhatsappApp.Core.csproj either, so MSBuild cannot see it ' +
          'and the type it holds does not exist');
      }
    }
    if (file.endsWith('.xaml') && !app.pages.has(rel)) {
      problems.push(`WhatsappApp/${rel}: not in WhatsApp.csproj as a <Page> or ` +
        '<ApplicationDefinition>, so it is never compiled');
    }
  }

  for (const file of walk(TASKS, [])) {
    const rel = path.relative(TASKS, file).replace(/\\/g, '/');
    if (file.endsWith('.cs') && !tasks.compile.has(rel)) {
      problems.push(`WhatsappApp.Tasks/${rel}: not in WhatsappApp.Tasks.csproj as a ` +
        '<Compile Include>, so the background task assembly does not contain it');
    }
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} file(s) the projects do not list.`);
    process.exit(1);
  }
  console.log('OK: every .cs and .xaml is listed in the project that compiles it.');
}

if (require.main === module) main();

module.exports = { walk, includes, normalize, relative, coveredElsewhere };
