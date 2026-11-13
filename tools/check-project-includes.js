#!/usr/bin/env node
/**
 * tools/check-project-includes.js
 *
 * Guard for a reference the project makes to a file that is not there.
 *
 * Why it exists: a `<Content Include>` that names a file which no longer exists
 * is invisible to every test and to the C# compiler, and only surfaces as an
 * APPX0702 payload error deep inside the packaging step of the build - or, for
 * a `<Compile Include>`, as a type that "does not exist" from a file that was
 * renamed or removed. It happened for real: a commit converted
 * `Assets/SplashScreen.scale-240.png` to `.jpg` and left the `.png` reference
 * behind, so the project packaged a payload that was not on disk and the x86
 * build failed only at the Appx packaging target.
 *
 * `check-project-files.js` covers the other direction (a file on disk that no
 * project lists). This covers the direction that broke the build: an include
 * the project lists that resolves to nothing.
 *
 * Rules:
 * 1. every `Include` attribute in a project file names a path that exists,
 *    resolved relative to the project's own directory;
 * 2. wildcard includes (`*` and `**`) are a folder contract, not a single file,
 *    and are left to MSBuild to expand - this guard does not second-guess them;
 * 3. a linked file whose `<Link>` says where it lands still has to exist at the
 *    `Include` path, which is what MSBuild reads.
 *
 * Usage:
 * node tools/check-project-includes.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const PROJECTS = [
  path.join(ROOT, 'WhatsappApp', 'WhatsApp.csproj'),
  path.join(ROOT, 'WhatsappApp.Core', 'WhatsappApp.Core.csproj'),
  path.join(ROOT, 'WhatsappApp.Tasks', 'WhatsappApp.Tasks.csproj'),
];

/** The item types that name a file path on disk rather than an assembly identity. */
const FILE_ITEMS = ['Content', 'Compile', 'Page', 'ApplicationDefinition', 'None', 'Resource', 'EmbeddedResource'];

/**
 * Every `Include="..."` path a project file names on a file-based item, without
 * duplicates. A `<Reference Include="Zlib.Portable, Version=...">` names an
 * assembly identity, not a file, so it is not one of these.
 */
function includes(projectText) {
  const out = new Set();
  const tagPattern = new RegExp(`<(${FILE_ITEMS.join('|')})\\b([^>]*)>`, 'g');
  for (const tag of projectText.matchAll(tagPattern)) {
    const include = /\sInclude="([^"]+)"/.exec(tag[2]);
    if (include) out.add(include[1]);
  }
  return [...out];
}

/** A wildcard is a folder contract for MSBuild, not one file to look up. */
function isWildcard(include) {
  return include.indexOf('*') >= 0;
}

/** The include with the Windows separators the project uses turned into slashes. */
function normalize(include) {
  return include.replace(/\\/g, '/');
}

function main() {
  const problems = [];

  for (const project of PROJECTS) {
    if (!fs.existsSync(project)) continue;
    const name = path.relative(ROOT, project).replace(/\\/g, '/');
    for (const include of missingIncludes(fs.readFileSync(project, 'utf8'), path.dirname(project))) {
      problems.push(`${name}: Include="${include}" names no file at ` +
        `${path.relative(ROOT, path.join(path.dirname(project), normalize(include))).replace(/\\/g, '/')}`);
    }
  }

  if (problems.length) {
    console.log(problems.join('\n'));
    console.log(`\n${problems.length} include(s) the projects list but the tree does not hold.`);
    process.exit(1);
  }
  console.log('OK: every file a project lists exists at the path it names.');
}

if (require.main === module) main();

/** The includes a project lists that resolve to no file under `dir`. */
function missingIncludes(projectText, dir) {
  return includes(projectText).filter((include) => {
    if (isWildcard(include)) return false;
    return !fs.existsSync(path.join(dir, normalize(include)));
  });
}

module.exports = { includes, isWildcard, normalize, missingIncludes };
