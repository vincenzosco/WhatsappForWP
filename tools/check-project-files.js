#!/usr/bin/env node
/**
 * tools/check-project-files.js
 *
 * Guard for a file the solution cannot see.
 *
 * Perche' esiste: un file .cs che non compare in WhatsappApp.csproj e'
 * invisibile a MSBuild, e nient'altro se ne accorge. La guardia C# 5 lo legge
 * (gira sui file, non sul progetto), i test dei tool passano, e il guasto esce
 * solo sulla build della VM - o, peggio, viene committato e la build successiva
 * riporta un tipo che "non esiste" da un file che c'e'. E' successo davvero a
 * Services/RecordingSession.cs e Services/ConversationView.cs: le loro voci
 * sono state rimosse da un `git checkout` del .csproj dopo una build.
 *
 * Regole:
 *  1. ogni .cs sotto WhatsappApp (obj/ e bin/ a parte) e' un <Compile Include>;
 *  2. ogni .xaml e' un <Page> o <ApplicationDefinition> Include.
 *
 * Usage:
 *   node tools/check-project-files.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP = path.join(ROOT, 'WhatsappApp');
const PROJECT = path.join(APP, 'WhatsappApp.csproj');

/** Ogni file dell'app, senza obj/ e bin/. */
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

/** Il percorso relativo a WhatsappApp, con gli slash in avanti. */
function relative(file) {
  return path.relative(APP, file).replace(/\\/g, '/');
}

function main() {
  const app = includes(fs.readFileSync(PROJECT, 'utf8'));
  const problems = [];

  for (const file of walk(APP, [])) {
    const rel = relative(file);
    if (file.endsWith('.cs') && !app.compile.has(rel)) {
      problems.push(`WhatsappApp/${rel}: not in WhatsappApp.csproj as a <Compile Include>, ` +
        'so MSBuild cannot see it and the type it holds does not exist');
    }
    if (file.endsWith('.xaml') && !app.pages.has(rel)) {
      problems.push(`WhatsappApp/${rel}: not in WhatsappApp.csproj as a <Page> or ` +
        '<ApplicationDefinition>, so it is never compiled');
    }
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} file(s) the project does not list.`);
    process.exit(1);
  }
  console.log('OK: every .cs and .xaml under WhatsappApp is listed in the project file.');
}

if (require.main === module) main();

module.exports = { walk, includes, normalize, relative };
