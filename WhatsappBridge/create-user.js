#!/usr/bin/env node
'use strict';

const { createUserStore } = require('./users');

// Registra un utente sul servizio condiviso e stampa il suo token. Il token
// esce una volta sola: non e' salvato, si salva il suo hash. Chi lo perde ne
// crea un altro, non lo recupera.
//
// Usage:
//   node create-user.js <nome> [--file /data/users.json]
//
// Dive lo si usa: nell'immagine Docker il file sta in /data (il volume), cosi'
// gli utenti sopravvivono a un riavvio come la sessione di WhatsApp.
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
