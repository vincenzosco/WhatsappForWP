'use strict';

const http = require('http');
const crypto = require('crypto');

// Verifica la firma HMAC-SHA256 inviata da GOWA nell'header
// X-Hub-Signature-256 ("sha256=<hex>"). Se non è configurato un secret,
// la verifica è disattivata.
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
    req.on('error', () => { /* la risposta arriverà comunque sotto */ });
    req.on('end', () => {
      const raw = Buffer.concat(chunks);
      if (!verifySignature(raw, req.headers['x-hub-signature-256'], secret)) {
        logger('WARN', 'Webhook con firma non valida, ignorato');
        res.writeHead(401, { 'Content-Type': 'text/plain' });
        res.end('Invalid signature');
        return;
      }

      let event = null;
      try { event = JSON.parse(raw.toString('utf8')); } catch (e) { event = null; }

      // Rispondi subito: GOWA ha un timeout breve sull'inoltro webhook.
      res.writeHead(200, { 'Content-Type': 'text/plain' });
      res.end('OK');

      if (event && typeof onEvent === 'function') {
        Promise.resolve(onEvent(event)).catch((err) =>
          logger('ERR', `Errore elaborazione webhook: ${err.message}`));
      }
    });
  });
}

module.exports = { createWebhookServer, verifySignature };
