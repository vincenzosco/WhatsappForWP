'use strict';

const { createAvatarCache } = require('./avatar-cache');

// The only module that speaks HTTP with the GOWA server.
// It uses the global fetch/FormData/Blob of Node 18.13+.

function errorMessage(data, fallback) {
  if (data && typeof data.message === 'string' && data.message.trim()) return data.message;
  if (data && typeof data.code === 'string' && data.code.trim()) return data.code;
  return fallback;
}

function buildAuthHeader(user, pass) {
  if (!user) return null;
  return 'Basic ' + Buffer.from(`${user}:${pass || ''}`, 'utf8').toString('base64');
}

// The name of a group as GOWA returns it.
//
// whatsmeow's types.GroupInfo has no json tags and embeds GroupName, and
// encoding/json promotes the fields of an embedded struct: the name therefore
// arrives as a top-level "Name". Nested forms are accepted too, because this is
// the only place the name enters, and a change of shape upstream must not empty
// the group names.
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

// A JID without the device suffix (user:12@server -> user@server).
// The suffix is not a JID WhatsApp recognizes in a profile request.
function normalizeJid(jid) {
  const value = String(jid || '');
  const at = value.indexOf('@');
  if (at < 0) return value;
  return value.slice(0, at).split(':')[0] + value.slice(at);
}

class GowaClient {
  constructor({ baseUrl, deviceId, user, pass, authHeader, fetchImpl, avatarCache } = {}) {
    this.baseUrl = String(baseUrl || '').replace(/\/+$/, '');
    this.deviceId = deviceId || '';
    // A ready `authHeader` serves `withDevice`: a derived client does not start
    // from a user and password, which it does not have.
    this.authHeader = authHeader || buildAuthHeader(user, pass);
    this.fetch = fetchImpl || (typeof fetch !== 'undefined' ? fetch : null);
    if (!this.fetch) throw new Error('fetch is not available: Node 18.13+ is required');
    this.resolvedDeviceId = null;
    // The pictures already downloaded: the chat list is requested on every
    // reconnection, and without this one picture per chat came back from
    // WhatsApp every time.
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

  /**
   * The same GOWA server, but bound to one specific device.
   *
   * On the shared service every user has their own WhatsApp session, and the
   * session is the device: a different `X-Device-Id` per user is what keeps them
   * apart. The starting client stays without a device (or with the configured
   * one), because it is the one that creates devices.
   */
  withDevice(deviceId) {
    return new GowaClient({
      baseUrl: this.baseUrl,
      deviceId: deviceId || '',
      authHeader: this.authHeader,
      fetchImpl: this.fetch,
      avatarCache: this.avatars
    });
  }

  /** The devices already present on the GOWA server. */
  async listDevices() {
    const r = await this.request('GET', '/devices');
    const devices = (r.data && r.data.results) || [];
    return Array.isArray(devices) ? devices : [];
  }

  /**
   * A new device, with a readable label (the user name), and its id. GOWA
   * answers with `results.id`: without that id there is no session to open, so a
   * missing id is a fault and not an empty value.
   */
  async createDevice(label) {
    const created = await this.request('POST', '/devices', {
      json: { name: String(label || '') }
    });
    const id = created.data && created.data.results && created.data.results.id;
    if (!id) throw new Error('GOWA did not return a device id');
    return id;
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
   * A file toward GOWA. The three routes differ only in the multipart field name
   * and the path: there used to be a single one (sendImage), and a video ended up
   * sent as an image.
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

  /**
   * Our own presence on WhatsApp: "available" while the app is connected,
   * "unavailable" when nobody is watching.
   *
   * It is not cosmetic. WhatsApp sends typing notifications only to a client
   * that is online, and GOWA connects as "unavailable": without this the
   * chat_presence events would arrive only in the five minutes of GOWA's daily
   * presence pulse.
   */
  async sendPresence(type) {
    const r = await this.request('POST', '/send/presence', { json: { type } });
    if (!r.ok) throw new Error(errorMessage(r.data, 'sending the presence failed'));
    return true;
  }

  /**
   * Our own typing state in one chat, so the contact sees "typing...".
   *
   * GOWA names the two values differently from the webhook that carries the
   * same notion: here it is "start"/"stop", there it is "composing"/"paused".
   * The two lists are not interchangeable, so the translation lives in the
   * caller.
   */
  async sendChatPresence(jid, action) {
    const r = await this.request('POST', '/send/chat-presence', {
      json: { phone: jid, action }
    });
    if (!r.ok) throw new Error(errorMessage(r.data, 'sending the chat presence failed'));
    return true;
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

  // The groups the account takes part in, with the real name.
  //
  // It is needed because the chat list is not a reliable source for group names:
  // when GOWA has no name in storage it answers "Group <number>" (see
  // chat_display_name.go in the GOWA source), which is the number and not the
  // name. One request for all the groups, and 500 groups is the ceiling WhatsApp
  // imposes.
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

  // List of the chats in the GOWA storage (paginated server-side).
  async chats(limit) {
    const r = await this.request('GET', `/chats?limit=${encodeURIComponent(limit)}`);
    const res = (r.data && r.data.results) || {};
    return Array.isArray(res.data) ? res.data : [];
  }

  // Messages of a chat. The GOWA route is /chat/:chat_jid/messages.
  async chatMessages(jid, limit) {
    const r = await this.request('GET',
      `/chat/${encodeURIComponent(jid)}/messages?limit=${encodeURIComponent(limit)}`);
    const res = (r.data && r.data.results) || {};
    return Array.isArray(res.data) ? res.data : [];
  }

  // Profile picture of a person.
  //
  // Two requests, not one: /user/avatar does not return the picture, it returns
  // the address where it is (results.url, a WhatsApp CDN URL), so the bytes are
  // downloaded afterwards. It used to take the body of /user/avatar as if it were
  // the picture: the JSON bytes arrived, which are not a bitmap, and every avatar
  // was silently discarded.
  //
  // GOWA answers 404 when the picture is not there: for the chat list that is
  // "no picture", not an error to propagate.
  //
  // It is asked for any JID, a group included. The parameter is named `phone` but
  // it is a JID: on the GOWA side `SanitizePhone` adds a suffix only to a value
  // that does not contain '@', and then `client.GetProfilePictureInfo` receives
  // the JID as it is - whatsmeow accepts it for a group as for a person. The
  // device suffix (:12), on the other hand, is not a JID WhatsApp recognizes in a
  // profile request, so it is dropped.
  async avatar(jid) {
    const value = String(jid || '');
    if (!value || value.indexOf('@') < 0) return null;

    // The device suffix is before the at sign (user:12@server): it is removed
    // from there, not by cutting the string at the first ':'.
    const at = value.indexOf('@');
    const target = value.slice(0, at).split(':')[0] + value.slice(at);

    // undefined means "not known": null means "it has none", and that is an
    // answer that is kept (briefly, see avatar-cache.js).
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
      // A failure is not kept: the next list tries again.
      return null;
    }
  }

  // Name, "about" text (status) and picture id of a person. GOWA answers with
  // an array of one element: that one is the profile.
  async userInfo(jid) {
    const target = normalizeJid(jid);
    if (!target || target.indexOf('@') < 0) return null;

    try {
      const r = await this.request('GET', `/user/info?phone=${encodeURIComponent(target)}`);
      const data = (r.data && r.data.results && r.data.results.data) || [];
      const info = Array.isArray(data) ? data[0] : null;
      if (!r.ok || !info) return null;

      return {
        name: info.name || info.verified_name || '',
        verifiedName: info.verified_name || '',
        status: info.status || '',
        pictureId: info.picture_id || ''
      };
    } catch (err) {
      return null;
    }
  }

  // The business profile: it exists only for a business number, and for all the
  // others GOWA answers with an error. For the app that is "there is none", not
  // a failure.
  async businessProfile(jid) {
    const target = normalizeJid(jid);
    if (!target || target.indexOf('@') < 0) return null;

    try {
      const r = await this.request('GET',
        `/user/business-profile?phone=${encodeURIComponent(target)}`);
      const res = (r.data && r.data.results) || null;
      if (!r.ok || !res) return null;

      return {
        email: res.email || '',
        address: res.address || '',
        categories: Array.isArray(res.categories)
          ? res.categories.map((c) => (c && c.name) || '').filter(Boolean)
          : [],
        timezone: res.business_hours_timezone || '',
        hours: Array.isArray(res.business_hours) ? res.business_hours : []
      };
    } catch (err) {
      return null;
    }
  }

  // The members of a group, with the role. Without members it is not a group: null.
  async groupParticipants(jid) {
    const target = normalizeJid(jid);
    if (!target || target.indexOf('@') < 0) return null;

    try {
      const r = await this.request('GET',
        `/group/participants?group_id=${encodeURIComponent(target)}`);
      const res = (r.data && r.data.results) || null;
      if (!r.ok || !res || !Array.isArray(res.participants)) return null;

      return {
        name: res.name || '',
        participants: res.participants.map((p) => ({
          jid: p.jid || '',
          phoneNumber: p.phone_number || '',
          lid: p.lid || '',
          displayName: p.display_name || '',
          isAdmin: p.is_admin === true,
          isSuperAdmin: p.is_super_admin === true
        }))
      };
    } catch (err) {
      return null;
    }
  }

  // The description of a group: GOWA returns it inside an opaque object, so the
  // names whatsmeow uses for the text are read and, if there is none, an empty
  // answer goes back instead of inventing a field.
  async groupInfo(jid) {
    const target = normalizeJid(jid);
    if (!target || target.indexOf('@') < 0) return null;

    try {
      const r = await this.request('GET', `/group/info?group_id=${encodeURIComponent(target)}`);
      const res = (r.data && r.data.results) || null;
      if (!r.ok || !res) return null;

      return {
        name: res.Name || res.name || '',
        topic: res.Topic || res.topic || res.Description || res.description || ''
      };
    } catch (err) {
      return null;
    }
  }

  // The bytes of the media of a message.
  //
  // Two requests, as for the avatar: /message/:id/download does not return the
  // bytes, it returns the static address of the downloaded file
  // (results.file_url), and the bytes are taken afterwards. An empty file_url
  // means the file is not under statics: for the app that is "no longer
  // available", not a failure.
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
