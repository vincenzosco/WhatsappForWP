'use strict';

const http = require('http');
const crypto = require('crypto');

// Verifies the HMAC-SHA256 signature GOWA sends in the X-Hub-Signature-256
// header ("sha256=<hex>"). If no secret is configured, verification is off.
function verifySignature(rawBody, signatureHeader, secret) {
  if (!secret) return true;
  if (!signatureHeader) return false;
  const received = String(signatureHeader).replace(/^sha256=/, '');
  const expected = crypto.createHmac('sha256', secret).update(rawBody).digest('hex');
  if (received.length !== expected.length) return false;
  try {
    return crypto.timingSafeEqual(Buffer.from(expected, 'hex'), Buffer.from(received, 'hex'));
  } catch (e) {
    return false;
  }
}

function createWebhookServer({ path, secret, onEvent, log }) {
  const logger = typeof log === 'function' ? log : () => {};

  return http.createServer((req, res) => {
    const requestPath = String(req.url || '').split('?')[0];

    if (req.method !== 'POST' || requestPath !== path) {
      res.writeHead(404, { 'Content-Type': 'text/plain' });
      res.end('Not found');
      return;
    }

    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('error', () => { /* the response still arrives below */ });
    req.on('end', () => {
      const raw = Buffer.concat(chunks);
      if (!verifySignature(raw, req.headers['x-hub-signature-256'], secret)) {
        logger('WARN', 'webhook with an invalid signature, ignored');
        res.writeHead(401, { 'Content-Type': 'text/plain' });
        res.end('Invalid signature');
        return;
      }

      let event = null;
      try { event = JSON.parse(raw.toString('utf8')); } catch (e) { event = null; }

      // Answer at once: GOWA has a short timeout on the webhook forward.
      res.writeHead(200, { 'Content-Type': 'text/plain' });
      res.end('OK');

      if (event && typeof onEvent === 'function') {
        Promise.resolve(onEvent(event)).catch((err) =>
          logger('ERR', `webhook handling failed: ${err.message}`));
      }
    });
  });
}

module.exports = { createWebhookServer, verifySignature };
