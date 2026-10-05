'use strict';

const http = require('http');
const crypto = require('crypto');

// The largest webhook body buffered in memory. A real event is a few kilobytes;
// past this the request is refused instead of read into the heap.
const MAX_BODY_BYTES = 1024 * 1024;

// Verifies the HMAC-SHA256 signature GOWA sends in the X-Hub-Signature-256
// header ("sha256=<hex>"). It fails closed: with no secret there is nothing to
// verify with, so every request is refused rather than trusted. GOWA signs with
// "secret" by default (see the README and .env.example).
function verifySignature(rawBody, signatureHeader, secret) {
  if (!secret) return false;
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
    let bodyBytes = 0;
    let refused = false;
    req.on('data', (chunk) => {
      if (refused) return;
      bodyBytes += chunk.length;
      if (bodyBytes > MAX_BODY_BYTES) {
        refused = true;
        logger('WARN', `webhook body over ${MAX_BODY_BYTES} bytes, refused`);
        res.writeHead(413, { 'Content-Type': 'text/plain' });
        res.end('Payload too large');
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('error', () => { /* the response still arrives below */ });
    req.on('end', () => {
      if (refused) return;
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

module.exports = { createWebhookServer, verifySignature, MAX_BODY_BYTES };
