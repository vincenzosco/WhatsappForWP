#!/usr/bin/env node
'use strict';

const { createUserStore } = require('./users');

// Registers a user on the shared service and prints the token. The token comes
// out once: it is not stored, only its hash is. Whoever loses it creates
// another one, does not recover it.
//
// Usage:
//   node create-user.js <name> [--file /data/users.json]
//
// Where it is used: in the Docker image the file lives in /data (the volume), so
// the users survive a restart like the WhatsApp session does.
//   docker exec whatsapp-for-wp8 node /opt/adapter/create-user.js vincenzo

function parseArgs(argv) {
  const out = { name: '', file: '' };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--file') out.file = argv[++i] || '';
    else if (!out.name) out.name = argv[i];
  }
  return out;
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (!args.name) {
    console.error('usage: node create-user.js <name> [--file users.json]');
    process.exit(2);
  }

  const users = createUserStore({ file: args.file || undefined });
  const { token, user } = users.register(args.name);

  console.log('user id:  ' + user.id);
  console.log('name:     ' + user.name);
  console.log('token:    ' + token);
  console.log('');
  console.log('Put the token in the app\'s settings. It is not stored, and cannot');
  console.log('be shown again.');
}

if (require.main === module) main();

module.exports = { parseArgs };
