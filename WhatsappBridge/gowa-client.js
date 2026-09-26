'use strict';

// Unico modulo che parla HTTP con il server GOWA.
// Usa fetch/FormData/Blob globali di Node 18.13+.

function errorMessage(data, fallback) {
  if (data && typeof data.message === 'string' && data.message.trim()) return data.message;
  if (data && typeof data.code === 'string' && data.code.trim()) return data.code;
  return fallback;
}

function buildAuthHeader(user, pass) {
  if (!user) return null;
  return 'Basic ' + Buffer.from(`${user}:${pass || ''}`, 'utf8').toString('base64');
}

class GowaClient {
  constructor({ baseUrl, deviceId, user, pass, fetchImpl } = {}) {
    this.baseUrl = String(baseUrl || '').replace(/\/+$/, '');
    this.deviceId = deviceId || '';
    this.authHeader = buildAuthHeader(user, pass);
    this.fetch = fetchImpl || (typeof fetch !== 'undefined' ? fetch : null);
    if (!this.fetch) throw new Error('fetch is not available: Node 18.13+ is required');
    this.resolvedDeviceId = null;
  }

  headers(extra) {
    const h = Object.assign({}, extra || {});
    if (this.authHeader) h.Authorization = this.authHeader;
    const id = this.deviceId || this.resolvedDeviceId;
    if (id) h['X-Device-Id'] = id;
    return h;
  }

  async request(method, path, { json } = {}) {
    const headers = this.headers();
    let body;
    if (json !== undefined) {
      headers['Content-Type'] = 'application/json';
      body = JSON.stringify(json);
    }
    const res = await this.fetch(`${this.baseUrl}${path}`, { method, headers, body });
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
    return { ok: res.ok, status: res.status, data };
  }

  async ensureDevice() {
    const list = await this.request('GET', '/devices');
    const devices = (list.data && list.data.results) || [];
    if (Array.isArray(devices) && devices.length > 0) {
      this.resolvedDeviceId = devices[0].id || null;
      return this.resolvedDeviceId;
    }
    const created = await this.request('POST', '/devices', { json: {} });
    const id = created.data && created.data.results && created.data.results.id;
    this.resolvedDeviceId = id || null;
    return this.resolvedDeviceId;
  }

  async status() {
    try {
      const r = await this.request('GET', '/app/status');
      const res = (r.data && r.data.results) || {};
      return {
        isConnected: !!res.is_connected,
        isLoggedIn: !!res.is_logged_in,
        jid: res.jid || ''
      };
    } catch (e) {
      return { isConnected: false, isLoggedIn: false, jid: '' };
    }
  }

  async loginQr() {
    const r = await this.request('GET', '/app/login');
    if (!r.ok) throw new Error(errorMessage(r.data, 'QR login failed'));
    const res = r.data.results || {};
    if (!res.qr_link) throw new Error('GOWA did not return a QR code');
    return { qrLink: res.qr_link, duration: res.qr_duration || 60 };
  }

  async loginWithCode(phone) {
    const r = await this.request('GET', `/app/login-with-code?phone=${encodeURIComponent(phone)}`);
    if (!r.ok) throw new Error(errorMessage(r.data, 'code login failed'));
    const code = (r.data.results || {}).pair_code;
    if (!code) throw new Error('GOWA did not return a pairing code');
    return code;
  }

  async logout() {
    await this.request('GET', '/app/logout');
  }

  async sendText(phone, message) {
    const r = await this.request('POST', '/send/message', { json: { phone, message } });
    if (!r.ok) throw new Error(errorMessage(r.data, 'sending the message failed'));
    return (r.data.results || {}).message_id || '';
  }

  async sendImage(phone, caption, buffer, mimeType, fileName) {
    const form = new FormData();
    form.append('phone', phone);
    if (caption) form.append('caption', caption);
    form.append('image', new Blob([buffer], { type: mimeType || 'image/jpeg' }), fileName || 'image.jpg');

    const res = await this.fetch(`${this.baseUrl}/send/image`, {
      method: 'POST', headers: this.headers(), body: form
    });
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
    if (!res.ok) throw new Error(errorMessage(data, 'sending the image failed'));
    return (data.results || {}).message_id || '';
  }

  async fetchBinary(urlOrPath) {
    const value = String(urlOrPath || '');
    const absolute = /^https?:\/\//i.test(value) ? value : `${this.baseUrl}/${value.replace(/^\/+/, '')}`;
    const res = await this.fetch(absolute, { headers: this.headers() });
    if (!res.ok) throw new Error(`download failed (${res.status})`);
    return {
      buffer: Buffer.from(await res.arrayBuffer()),
      contentType: (res.headers && res.headers.get && res.headers.get('content-type')) || 'application/octet-stream'
    };
  }

  async contacts() {
    const r = await this.request('GET', '/user/my/contacts');
    const data = (r.data && r.data.results && r.data.results.data) || [];
    return data.map((c) => ({ jid: c.jid, name: c.name || '' }));
  }

  async setDeviceWebhook(url) {
    const id = this.deviceId || this.resolvedDeviceId;
    if (!id) return false;
    const r = await this.request('PATCH', `/devices/${encodeURIComponent(id)}/webhook`, {
      json: { webhook_url: url }
    });
    return r.ok;
  }
}

module.exports = { GowaClient, errorMessage, buildAuthHeader };
