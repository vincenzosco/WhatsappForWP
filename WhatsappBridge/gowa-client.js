'use strict';

const { createAvatarCache } = require('./avatar-cache');

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

// Il nome di un gruppo come lo restituisce GOWA.
//
// whatsmeow's types.GroupInfo non ha tag json e incorpora GroupName, e
// encoding/json promuove i campi di una struct incorporata: il nome arriva
// quindi come "Name" di primo livello. Si accettano anche le forme annidate
// perche' questo e' l'unico punto in cui il nome entra, e un cambio di forma a
// monte non deve svuotare i nomi dei gruppi.
function groupName(group) {
  if (!group) return '';
  const candidates = [group.Name, group.name];
  const nested = group.GroupName || group.group_name;
  if (nested) {
    candidates.push(nested.Name, nested.name);
  }
  for (const candidate of candidates) {
    if (typeof candidate === 'string' && candidate.trim()) return candidate.trim();
  }
  return '';
}

class GowaClient {
  constructor({ baseUrl, deviceId, user, pass, fetchImpl, avatarCache } = {}) {
    this.baseUrl = String(baseUrl || '').replace(/\/+$/, '');
    this.deviceId = deviceId || '';
    this.authHeader = buildAuthHeader(user, pass);
    this.fetch = fetchImpl || (typeof fetch !== 'undefined' ? fetch : null);
    if (!this.fetch) throw new Error('fetch is not available: Node 18.13+ is required');
    this.resolvedDeviceId = null;
    // Le foto gia' scaricate: l'elenco chat si richiede a ogni riconnessione,
    // e senza questa una foto per chat tornava da WhatsApp ogni volta.
    this.avatars = avatarCache || createAvatarCache();
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

  /**
   * Un file verso GOWA. Le tre rotte differiscono solo per il nome del campo
   * multipart e per il percorso: prima ce n'era una sola (sendImage), e un
   * video finiva spedito come immagine.
   */
  async postMedia(path, field, phone, caption, buffer, mimeType, fileName) {
    const form = new FormData();
    form.append('phone', phone);
    if (caption) form.append('caption', caption);
    form.append(field, new Blob([buffer], { type: mimeType }), fileName || field);

    const res = await this.fetch(`${this.baseUrl}${path}`, {
      method: 'POST', headers: this.headers(), body: form
    });
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
    if (!res.ok) throw new Error(errorMessage(data, `sending the ${field} failed`));
    return (data.results || {}).message_id || '';
  }

  async sendImage(phone, caption, buffer, mimeType, fileName) {
    return this.postMedia('/send/image', 'image', phone, caption, buffer,
      mimeType || 'image/jpeg', fileName || 'image.jpg');
  }

  async sendVideo(phone, caption, buffer, mimeType, fileName) {
    return this.postMedia('/send/video', 'video', phone, caption, buffer,
      mimeType || 'video/mp4', fileName || 'video.mp4');
  }

  async sendFile(phone, caption, buffer, mimeType, fileName) {
    return this.postMedia('/send/file', 'file', phone, caption, buffer,
      mimeType || 'application/octet-stream', fileName || 'file');
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

  // I gruppi a cui l'account partecipa, con il nome vero.
  //
  // Serve perche' l'elenco chat non e' una fonte affidabile per i nomi dei
  // gruppi: quando GOWA non ha un nome in storage risponde "Group <numero>"
  // (vedi chat_display_name.go nel sorgente di GOWA), che e' il numero e non il
  // nome. Una richiesta sola per tutti i gruppi, e 500 gruppi sono il tetto che
  // impone WhatsApp.
  async myGroups() {
    const names = new Map();
    try {
      const r = await this.request('GET', '/user/my/groups');
      const data = (r.data && r.data.results && r.data.results.data) || [];
      if (!r.ok || !Array.isArray(data)) return names;

      for (const group of data) {
        const jid = group && (group.JID || group.jid);
        const name = groupName(group);
        if (jid && name) names.set(String(jid), name);
      }
    } catch (err) {
      return names;
    }
    return names;
  }

  // Elenco delle chat presenti nella storage di GOWA (paginato lato server).
  async chats(limit) {
    const r = await this.request('GET', `/chats?limit=${encodeURIComponent(limit)}`);
    const res = (r.data && r.data.results) || {};
    return Array.isArray(res.data) ? res.data : [];
  }

  // Messaggi di una chat. La rotta di GOWA e' /chat/:chat_jid/messages.
  async chatMessages(jid, limit) {
    const r = await this.request('GET',
      `/chat/${encodeURIComponent(jid)}/messages?limit=${encodeURIComponent(limit)}`);
    const res = (r.data && r.data.results) || {};
    return Array.isArray(res.data) ? res.data : [];
  }

  // Immagine del profilo di una persona.
  //
  // Due richieste, non una: /user/avatar non restituisce l'immagine, restituisce
  // l'indirizzo dove sta (results.url, un URL del CDN di WhatsApp), quindi i byte
  // si scaricano dopo. Prima si prendeva il corpo di /user/avatar come se fosse
  // l'immagine: arrivavano i byte del JSON, che non sono una bitmap, e ogni
  // avatar veniva scartato in silenzio.
  //
  // GOWA risponde 404 quando l'immagine non c'e': per l'elenco chat e' "nessuna
  // immagine", non un errore da propagare.
  //
  // Si chiede per qualunque JID, gruppo compreso. Il parametro si chiama `phone`
  // ma e' un JID: dal lato GOWA `SanitizePhone` aggiunge un suffisso solo a un
  // valore che non contiene '@', e poi `client.GetProfilePictureInfo` riceve il
  // JID come e' - whatsmeow lo accetta per un gruppo come per una persona. Il
  // suffisso del dispositivo (:12) invece non e' un JID che WhatsApp riconosce
  // in una richiesta di profilo, quindi si toglie.
  async avatar(jid) {
    const value = String(jid || '');
    if (!value || value.indexOf('@') < 0) return null;

    // Il suffisso del dispositivo sta prima della chiocciola (utente:12@server):
    // si toglie da li', non tagliando la stringa sul primo ':'.
    const at = value.indexOf('@');
    const target = value.slice(0, at).split(':')[0] + value.slice(at);

    // undefined vuol dire "non si sa": null vuol dire "non ce l'ha", ed e' una
    // risposta che si tiene (per poco, vedi avatar-cache.js).
    const remembered = this.avatars.get(target);
    if (remembered !== undefined) return remembered;

    try {
      const r = await this.request('GET',
        `/user/avatar?phone=${encodeURIComponent(target)}&is_preview=true`);
      const url = (r.data && r.data.results && r.data.results.url) || '';
      if (!r.ok || !url) {
        this.avatars.put(target, null);
        return null;
      }

      const picture = await this.fetchBinary(url);
      const base64 = picture.buffer.length > 0 ? picture.buffer.toString('base64') : null;
      this.avatars.put(target, base64);
      return base64;
    } catch (err) {
      // Un guasto non si tiene: il prossimo elenco lo riprova.
      return null;
    }
  }

  // I byte del media di un messaggio.
  //
  // Due richieste, come per l'avatar: /message/:id/download non restituisce i
  // byte, restituisce l'indirizzo statico del file scaricato (results.file_url),
  // e i byte si prendono dopo. Un file_url vuoto significa che il file non e'
  // sotto statics: per l'app e' "non piu' disponibile", non un guasto.
  async downloadMedia(phone, messageId) {
    if (!phone || !messageId) return null;

    try {
      const r = await this.request('GET',
        `/message/${encodeURIComponent(messageId)}/download?phone=${encodeURIComponent(phone)}`);
      const res = (r.data && r.data.results) || {};
      const url = res.file_url || '';
      if (!r.ok || !url) return null;

      const media = await this.fetchBinary(url);
      if (!media.buffer || media.buffer.length === 0) return null;

      return {
        base64: media.buffer.toString('base64'),
        mimeType: media.contentType || res.media_type || '',
        fileName: res.filename || null
      };
    } catch (err) {
      return null;
    }
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
