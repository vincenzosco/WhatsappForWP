#!/usr/bin/env node
/**
 * tools/test-protocol.js
 *
 * Builds and runs the desktop host of the WhatsApp protocol core
 * (`ProtocolTests/ProtocolTests.csproj`) and forwards its exit code.
 *
 * The core under `WhatsappApp/Protocol/**` is compiled by the WP8.1 toolchain
 * inside the app, where there is no test runner: every iteration would need a
 * phone. This project compiles the same sources against plain .NET 4.5 with the
 * same C# 5 compiler, so the codec and the crypto are checked in seconds.
 *
 * It is not in WhatsappApp.sln on purpose: that solution builds only for the
 * WP8.1 platforms, and a net45 console project would break its gate.
 *
 * Usage:  node tools/test-protocol.js
 * Exit code: 0 = every protocol test passed, 1 = build failed or a test failed.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const ROOT = path.resolve(__dirname, '..');
const PROJECT = path.join(ROOT, 'ProtocolTests', 'ProtocolTests.csproj');
const EXE = path.join(ROOT, 'ProtocolTests', 'bin', 'Debug', 'ProtocolTests.exe');

// MSBuild 12 is the Visual Studio 2013 one, the same toolchain the WP8.1 app
// builds with. The later ones are accepted so the script still works on a machine
// where only a newer Visual Studio is installed.
const MSBUILD_CANDIDATES = [
  'C:\\Program Files (x86)\\MSBuild\\12.0\\bin\\MSBuild.exe',
  'C:\\Program Files\\MSBuild\\12.0\\bin\\MSBuild.exe',
  'C:\\Program Files (x86)\\MSBuild\\14.0\\Bin\\MSBuild.exe',
  'C:\\Program Files (x86)\\MSBuild\\15.0\\Bin\\MSBuild.exe',
  'C:\\Program Files\\Microsoft Visual Studio\\2022\\Enterprise\\MSBuild\\Current\\Bin\\MSBuild.exe',
  'C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild\\Current\\Bin\\MSBuild.exe'
];

function findMsbuild() {
  for (const candidate of MSBUILD_CANDIDATES) {
    if (fs.existsSync(candidate)) return candidate;
  }
  return null;
}

function main() {
  if (!fs.existsSync(PROJECT)) {
    console.error('Cannot find ' + PROJECT);
    return 1;
  }

  const msbuild = findMsbuild();
  if (!msbuild) {
    console.error('Cannot find MSBuild.exe. Install Visual Studio 2013 (Windows Phone 8.1 SDK)');
    console.error('or edit MSBUILD_CANDIDATES in tools/test-protocol.js.');
    return 1;
  }

  console.log('building with ' + msbuild);

  const build = spawnSync(
    msbuild,
    [
      PROJECT,
      '/t:Rebuild',
      '/p:Configuration=Debug',
      '/p:Platform=AnyCPU',
      '/v:minimal',
      '/nologo'
    ],
    { stdio: 'inherit' }
  );

  if (build.error) {
    console.error('MSBuild could not start: ' + build.error.message);
    return 1;
  }

  if (build.status !== 0) {
    console.error('\nThe protocol core did not compile: MSBuild exited ' + build.status + '.');
    return 1;
  }

  if (!fs.existsSync(EXE)) {
    console.error('\nMSBuild reported success but ' + EXE + ' was not produced.');
    return 1;
  }

  const run = spawnSync(EXE, [], { stdio: 'inherit' });
  if (run.error) {
    console.error('Could not run the protocol tests: ' + run.error.message);
    return 1;
  }

  return run.status === null ? 1 : run.status;
}

process.exit(main());
