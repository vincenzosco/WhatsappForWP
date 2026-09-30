'use strict';

// Il token viaggia dentro il frame `hello`, cifrato come tutto il resto: questo
// protocollo non ha header HTTP, quindi non c'e' un posto fuori dal payload dove
// metterlo. Un campo dedicato e' piu' chiaro del riuso di `Text`, che per
// `hello` porta gia' il nome utente.

const TOKEN_FIELD = 'Token';

function parseToken(frame) {
  const value = frame && frame[TOKEN_FIELD];
  return typeof value === 'string' ? value.trim() : '';
}

/**
 * Decide se questo handshake puo' passare.
 *
 * - `authRequired` falso: un'istanza privata, come prima, e nessun token serve.
 * - token che verifica: si restituisce l'utente.
 * - token assente o sbagliato: si restituisce null, e chi chiama chiude.
 */
function authenticate({ frame, users, authRequired }) {
  if (!authRequired) return { ok: true, user: null };
  if (!users) return { ok: false, user: null, reason: 'no user store' };

  const token = parseToken(frame);
  if (!token) return { ok: false, user: null, reason: 'missing token' };

  const user = users.verify(token);
  if (!user) return { ok: false, user: null, reason: 'unknown token' };

  return { ok: true, user };
}

module.exports = { parseToken, authenticate, TOKEN_FIELD };
