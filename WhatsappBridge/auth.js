'use strict';

// The token travels inside the `hello` frame, encrypted like everything else:
// this protocol has no HTTP headers, so there is no place outside the payload to
// put it. A dedicated field is clearer than reusing `Text`, which for `hello`
// already carries the user name.

const TOKEN_FIELD = 'Token';

function parseToken(frame) {
  const value = frame && frame[TOKEN_FIELD];
  return typeof value === 'string' ? value.trim() : '';
}

/**
 * Decides whether this handshake may pass.
 *
 * - `authRequired` false: a private instance, as before, and no token is needed.
 * - token that verifies: the user is returned.
 * - missing or wrong token: null is returned, and the caller closes.
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
