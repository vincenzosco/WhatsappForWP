'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { GowaClient, errorMessage } = require('../gowa-client');

test('chats() asks for a bounded list and reads results.data', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      return { ok: true, status: 200, text: async () => JSON.stringify({ results: { data: [{ jid: 'a@s.whatsapp.net' }] } }) };
    }
  });

  const chats = await client.chats(25);
  assert.strictEqual(seen[0], 'http://127.0.0.1:3000/chats?limit=25');
  assert.strictEqual(chats.length, 1);
  assert.strictEqual(chats[0].jid, 'a@s.whatsapp.net');
});

test('chatMessages() encodes the jid in the path', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      return { ok: true, status: 200, text: async () => JSON.stringify({ results: { data: [] } }) };
    }
  });

  await client.chatMessages('393401234567@s.whatsapp.net', 100);
  assert.strictEqual(seen[0], 'http://127.0.0.1:3000/chat/393401234567%40s.whatsapp.net/messages?limit=100');
});

// Le due rotte della presenza. GOWA le rifiuta senza l header X-Device-Id (e una
// prova dal vivo lo ha confermato), quindi il test pinna anche quello: senza,
// l account non va mai online e le notifiche di scrittura non arrivano.
test('sendPresence manda il tipo sulla rotta che lo vuole, con il device', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: {} }));
  const client = new GowaClient({ baseUrl: 'http://127.0.0.1:3000', deviceId: 'dev-1', fetchImpl });

  await client.sendPresence('available');

  assert.strictEqual(fetchImpl.calls[0].url, 'http://127.0.0.1:3000/send/presence');
  assert.strictEqual(fetchImpl.calls[0].options.method, 'POST');
  assert.strictEqual(fetchImpl.calls[0].options.headers['X-Device-Id'], 'dev-1');
  assert.strictEqual(fetchImpl.calls[0].options.body, JSON.stringify({ type: 'available' }));
});

test('sendChatPresence manda il jid e start/stop, e un errore diventa messaggio', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ code: 'INVALID_JID', message: 'non su whatsapp' }, 400));
  const client = new GowaClient({ baseUrl: 'http://127.0.0.1:3000', deviceId: 'dev-1', fetchImpl });

  await assert.rejects(() => client.sendChatPresence('39@s.whatsapp.net', 'start'),
    /non su whatsapp/);

  assert.strictEqual(fetchImpl.calls[0].url, 'http://127.0.0.1:3000/send/chat-presence');
  assert.strictEqual(fetchImpl.calls[0].options.body,
    JSON.stringify({ phone: '39@s.whatsapp.net', action: 'start' }));
});

function jsonResponse(body, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => 'application/json' },
    text: async () => JSON.stringify(body),
    arrayBuffer: async () => Buffer.from(JSON.stringify(body))
  };
}

function makeFetch(handler) {
  const calls = [];
  const impl = async (url, options) => {
    calls.push({ url, options });
    return handler(url, options);
  };
  impl.calls = calls;
  return impl;
}

test('errorMessage preferisce il campo message della risposta GOWA', () => {
  assert.strictEqual(errorMessage({ message: 'Boom' }, 'fallback'), 'Boom');
  assert.strictEqual(errorMessage(null, 'fallback'), 'fallback');
});

test('ensureDevice crea un device quando la lista è vuota', async () => {
  const fetchImpl = makeFetch(async (url, options) => {
    if (url.endsWith('/devices')) {
      if (options.method === 'POST') return jsonResponse({ status: 200, results: { id: 'org_1' } });
      return jsonResponse({ status: 200, results: [] });
    }
    return jsonResponse({});
  });
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const id = await client.ensureDevice();
  assert.strictEqual(id, 'org_1');
  assert.strictEqual(fetchImpl.calls[0].url, 'http://g/devices');
  assert.strictEqual(fetchImpl.calls[0].options.method, 'GET');
  assert.strictEqual(fetchImpl.calls[1].options.method, 'POST');
});

test('ensureDevice riusa il primo device esistente', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: [{ id: 'org_9' }] }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  assert.strictEqual(await client.ensureDevice(), 'org_9');
  assert.strictEqual(fetchImpl.calls.length, 1);
});

test('ensureDevice sceglie il device collegato, non il primo', async () => {
  // La lista e' in ordine di creazione: il primo e' il piu vecchio, e su un
  // server con piu device puo essere uno su cui nessuno ha mai fatto il login.
  const fetchImpl = makeFetch(async () => jsonResponse({
    status: 200,
    results: [
      { id: 'old-1', state: 'disconnected' },
      { id: 'old-2', state: 'disconnected' },
      { id: 'linked', state: 'logged_in' },
      { id: 'later', state: 'disconnected' }
    ]
  }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });

  assert.strictEqual(await client.ensureDevice(), 'linked');
  assert.strictEqual(client.resolvedDeviceId, 'linked');
  assert.strictEqual(fetchImpl.calls.length, 1, 'non deve creare un device');
});

test('senza nessun device collegato ensureDevice ripiega sul primo', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({
    status: 200,
    results: [{ id: 'old-1', state: 'disconnected' }, { id: 'old-2', state: 'disconnected' }]
  }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });

  assert.strictEqual(await client.ensureDevice(), 'old-1');
});

test('listDevices legge i device presenti sul server GOWA', async () => {
  const fetchImpl = makeFetch(async (url) => {
    assert.strictEqual(url, 'http://g/devices');
    return jsonResponse({ status: 200, results: [{ id: 'd1' }, { id: 'd2' }] });
  });
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const devices = await client.listDevices();
  assert.deepStrictEqual(devices.map((d) => d.id), ['d1', 'd2']);
});

test('createDevice manda il nome dell utente e pretende un id', async () => {
  const fetchImpl = makeFetch(async (url, options) => {
    assert.strictEqual(url, 'http://g/devices');
    assert.strictEqual(options.method, 'POST');
    assert.deepStrictEqual(JSON.parse(options.body), { name: 'wp8-anna' });
    return jsonResponse({ status: 200, results: { id: 'new-1' } });
  });
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  assert.strictEqual(await client.createDevice('wp8-anna'), 'new-1');
});

test('createDevice senza id e un guasto, non un device vuoto', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: {} }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  await assert.rejects(() => client.createDevice('x'), /device id/i);
});

test('withDevice ribalta lo stesso server su un altro device, con la stessa auth', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: { is_logged_in: true, jid: '39@x' } }));
  const base = new GowaClient({ baseUrl: 'http://g', user: 'admin', pass: 'secret', fetchImpl });

  const derived = base.withDevice('dev-7');
  await derived.status();

  const headers = fetchImpl.calls[0].options.headers;
  assert.strictEqual(headers['X-Device-Id'], 'dev-7');
  assert.strictEqual(headers.Authorization, 'Basic ' + Buffer.from('admin:secret').toString('base64'));
});

test('loginQr restituisce qr_link e qr_duration', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({
    status: 200, results: { device_id: 'd', qr_link: 'http://g/statics/qr.png', qr_duration: 30 }
  }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const qr = await client.loginQr();
  assert.strictEqual(qr.qrLink, 'http://g/statics/qr.png');
  assert.strictEqual(qr.duration, 30);
});

test('loginWithCode passa phone e legge pair_code', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: { pair_code: 'ABCD-EFGH' } }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const code = await client.loginWithCode('393401234567');
  assert.strictEqual(code, 'ABCD-EFGH');
  assert.match(fetchImpl.calls[0].url, /login-with-code\?phone=393401234567/);
});

test('sendText invia il body JSON e legge message_id', async () => {
  const fetchImpl = makeFetch(async (url, options) => {
    assert.strictEqual(url, 'http://g/send/message');
    assert.strictEqual(options.body, JSON.stringify({ phone: '39@s.whatsapp.net', message: 'ciao' }));
    return jsonResponse({ status: 200, results: { message_id: 'M1', status: 'PENDING' } });
  });
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  assert.strictEqual(await client.sendText('39@s.whatsapp.net', 'ciao'), 'M1');
});

test('status tollera gli errori HTTP', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 400, code: 'DEVICE_ID_REQUIRED' }, 400));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const s = await client.status();
  assert.deepStrictEqual(s, { isConnected: false, isLoggedIn: false, jid: '' });
});

test('aggiunge gli header di autenticazione e X-Device-Id', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: { is_logged_in: true, jid: '39@x' } }));
  const client = new GowaClient({ baseUrl: 'http://g', user: 'admin', pass: 'secret', deviceId: 'd1', fetchImpl });
  await client.status();
  const headers = fetchImpl.calls[0].options.headers;
  assert.strictEqual(headers.Authorization, 'Basic ' + Buffer.from('admin:secret').toString('base64'));
  assert.strictEqual(headers['X-Device-Id'], 'd1');
});

test('avatar() follows the picture URL GOWA returns, then downloads it', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      if (seen.length === 1) {
        // /user/avatar non restituisce l'immagine: restituisce l'indirizzo
        // dove sta. Il secondo giro scarica davvero i byte.
        return jsonResponse({
          status: 200,
          results: { url: 'https://pps.whatsapp.net/v/t1/abc.jpg', id: 'abc', type: 'image' }
        });
      }
      return {
        ok: true,
        status: 200,
        headers: { get: () => 'image/jpeg' },
        arrayBuffer: async () => new Uint8Array([1, 2, 3]).buffer
      };
    }
  });

  const picture = await client.avatar('393401234567@s.whatsapp.net');

  assert.strictEqual(seen[0], 'http://127.0.0.1:3000/user/avatar?phone=393401234567%40s.whatsapp.net&is_preview=true');
  assert.strictEqual(seen[1], 'https://pps.whatsapp.net/v/t1/abc.jpg');
  assert.strictEqual(picture, Buffer.from([1, 2, 3]).toString('base64'));
});

test('avatar() asks with the whole JID, so a group JID is a JID', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      if (seen.length === 1) {
        return jsonResponse({ status: 200, results: { url: 'https://pps.whatsapp.net/v/t1/g.jpg' } });
      }
      return {
        ok: true,
        status: 200,
        headers: { get: () => 'image/jpeg' },
        arrayBuffer: async () => new Uint8Array([7, 8, 9]).buffer
      };
    }
  });

  const picture = await client.avatar('123456789012345678@g.us');

  // Il valore intero, non le sole cifre: GOWA aggiunge un suffisso solo a un
  // valore che non contiene '@', quindi un JID di gruppo passato intero resta
  // un JID di gruppo, e GetProfilePictureInfo accetta qualunque JID.
  assert.strictEqual(seen[0],
    'http://127.0.0.1:3000/user/avatar?phone=123456789012345678%40g.us&is_preview=true');
  assert.strictEqual(picture, Buffer.from([7, 8, 9]).toString('base64'));
});

test('avatar() drops a device suffix, and answers null when there is no picture', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      return jsonResponse({}, 404);
    }
  });

  // Un JID con il suffisso del dispositivo (:12) non e' il JID che WhatsApp
  // riconosce in una richiesta di profilo.
  assert.strictEqual(await client.avatar('393401234567:12@s.whatsapp.net'), null);
  assert.strictEqual(seen[0],
    'http://127.0.0.1:3000/user/avatar?phone=393401234567%40s.whatsapp.net&is_preview=true');

  // Nessuna immagine: GOWA risponde con un errore e non c'e' niente da
  // scaricare, quindi una sola richiesta. La seconda chiamata e' lo stesso JID,
  // quindi risponde la cache e non si richiede niente: "non ce l'ha" e' una
  // risposta anche lei, e vale per un minuto (vedi avatar-cache.js).
  assert.strictEqual(await client.avatar('393401234567@s.whatsapp.net'), null);
  assert.strictEqual(seen.length, 1);
});

test('myGroups() maps every joined group to its real name', async () => {
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      assert.strictEqual(url, 'http://127.0.0.1:3000/user/my/groups');
      // whatsmeow's GroupInfo non ha tag json e incorpora GroupName: encoding/json
      // promuove i campi, quindi il nome arriva come "Name" di primo livello.
      return jsonResponse({
        results: {
          data: [
            { JID: '111@g.us', Name: 'Amici', GroupTopic: { Topic: 'x' } },
            { JID: '222@g.us', GroupName: { Name: 'Lavoro' } },
            { JID: '333@g.us', Name: '   ' }
          ]
        }
      });
    }
  });

  const names = await client.myGroups();

  assert.strictEqual(names.get('111@g.us'), 'Amici');
  assert.strictEqual(names.get('222@g.us'), 'Lavoro');
  assert.strictEqual(names.has('333@g.us'), false);
  assert.strictEqual(names.size, 2);
});

test('myGroups() is empty, not fatal, when GOWA cannot answer', async () => {
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async () => jsonResponse({}, 500)
  });

  const names = await client.myGroups();

  assert.strictEqual(names.size, 0);
});

test('avatar() returns null when the picture URL does not download', async () => {
  let calls = 0;
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async () => {
      calls++;
      if (calls === 1) {
        return jsonResponse({ status: 200, results: { url: 'https://pps.whatsapp.net/gone.jpg' } });
      }
      return { ok: false, status: 410, headers: { get: () => null } };
    }
  });

  assert.strictEqual(await client.avatar('393401234567@s.whatsapp.net'), null);
});

test('sendVideo posts the video field, and sendFile the file field', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://g',
    fetchImpl: async (url, options) => {
      seen.push({ url, field: [...options.body.keys()].join(',') });
      return jsonResponse({ status: 200, results: { message_id: 'V1' } });
    }
  });

  assert.strictEqual(await client.sendVideo('39@s.whatsapp.net', 'guarda', Buffer.from([1]), 'video/mp4', 'clip.mp4'), 'V1');
  assert.strictEqual(await client.sendFile('39@s.whatsapp.net', '', Buffer.from([2]), 'application/pdf', 'doc.pdf'), 'V1');

  assert.strictEqual(seen[0].url, 'http://g/send/video');
  assert.ok(seen[0].field.includes('video'));
  assert.ok(seen[0].field.includes('phone'));
  assert.ok(seen[0].field.includes('caption'));
  assert.strictEqual(seen[1].url, 'http://g/send/file');
  assert.ok(seen[1].field.includes('file'));
});

test('downloadMedia va sulla rotta del messaggio e segue il file_url', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      if (seen.length === 1) {
        return jsonResponse({
          status: 200,
          results: { message_id: 'M9', file_url: 'http://127.0.0.1:3000/statics/abc.jpg', filename: 'foto.jpg', media_type: 'image' }
        });
      }
      return {
        ok: true,
        status: 200,
        headers: { get: () => 'image/jpeg' },
        arrayBuffer: async () => new Uint8Array([1, 2, 3]).buffer
      };
    }
  });

  const media = await client.downloadMedia('393401234567@s.whatsapp.net', 'M9');

  assert.strictEqual(seen[0], 'http://127.0.0.1:3000/message/M9/download?phone=393401234567%40s.whatsapp.net');
  assert.strictEqual(seen[1], 'http://127.0.0.1:3000/statics/abc.jpg');
  assert.strictEqual(media.base64, Buffer.from([1, 2, 3]).toString('base64'));
  assert.strictEqual(media.mimeType, 'image/jpeg');
  assert.strictEqual(media.fileName, 'foto.jpg');
});

test('avatar() non richiede due volte la stessa immagine', async () => {
  const requests = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      requests.push(url);
      if (requests.length === 1) {
        return jsonResponse({ status: 200, results: { url: 'https://pps.whatsapp.net/v/t1/abc.jpg' } });
      }
      return {
        ok: true,
        status: 200,
        headers: { get: () => 'image/jpeg' },
        arrayBuffer: async () => new Uint8Array([1, 2, 3]).buffer
      };
    }
  });

  const first = await client.avatar('393401234567@s.whatsapp.net');
  const second = await client.avatar('393401234567:12@s.whatsapp.net');

  assert.strictEqual(first, Buffer.from([1, 2, 3]).toString('base64'));
  // Il suffisso del dispositivo si toglie prima di guardare nella cache,
  // quindi e' la stessa chiave: due richieste in tutto, nessuna la seconda volta.
  assert.strictEqual(second, first);
  assert.strictEqual(requests.length, 2);
});

test('downloadMedia non e fatale quando il file non c e piu', async () => {
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async () => jsonResponse({ status: 200, results: { message_id: 'M9' } })
  });

  assert.strictEqual(await client.downloadMedia('a@s.whatsapp.net', 'M9'), null);
});

test('userInfo legge nome, about e id immagine di un profilo', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({
    status: 200,
    results: { data: [{ name: 'Anna', verified_name: 'Anna B', status: 'in giro', picture_id: 'P1' }] }
  }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const info = await client.userInfo('393401234567:12@s.whatsapp.net');

  assert.strictEqual(fetchImpl.calls[0].url,
    'http://g/user/info?phone=393401234567%40s.whatsapp.net');
  assert.strictEqual(info.name, 'Anna');
  assert.strictEqual(info.verifiedName, 'Anna B');
  assert.strictEqual(info.status, 'in giro');
  assert.strictEqual(info.pictureId, 'P1');
});

test('userInfo risponde null quando GOWA non conosce il profilo', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: { data: [] } }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  assert.strictEqual(await client.userInfo('393401234567@s.whatsapp.net'), null);
  assert.strictEqual(await client.userInfo('non-un-jid'), null);
});

test('businessProfile legge email, indirizzo, categorie e orari', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({
    status: 200,
    results: {
      email: 'info@bar.it',
      address: 'Via Roma 1',
      categories: [{ id: '1', name: 'Bar' }, { id: '2', name: 'Caffe' }],
      business_hours_timezone: 'Europe/Rome',
      business_hours: [{ day_of_week: 1, mode: 'open', open_time: '09:00', close_time: '18:00' }]
    }
  }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const business = await client.businessProfile('393401234567@s.whatsapp.net');

  assert.strictEqual(fetchImpl.calls[0].url,
    'http://g/user/business-profile?phone=393401234567%40s.whatsapp.net');
  assert.strictEqual(business.email, 'info@bar.it');
  assert.deepStrictEqual(business.categories, ['Bar', 'Caffe']);
  assert.strictEqual(business.hours.length, 1);
});

test('businessProfile risponde null per un profilo che non e business', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 404, message: 'not a business account' }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  assert.strictEqual(await client.businessProfile('393401234567@s.whatsapp.net'), null);
});

test('groupParticipants legge i membri e i loro ruoli', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({
    status: 200,
    results: {
      group_id: '123@g.us',
      name: 'Famiglia',
      participants: [
        { jid: '1@s.whatsapp.net', phone_number: '39', display_name: 'Anna', is_admin: true, is_super_admin: false },
        { jid: '2@s.whatsapp.net', display_name: 'Bruno' }
      ]
    }
  }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const group = await client.groupParticipants('123@g.us');

  assert.strictEqual(fetchImpl.calls[0].url, 'http://g/group/participants?group_id=123%40g.us');
  assert.strictEqual(group.name, 'Famiglia');
  assert.strictEqual(group.participants.length, 2);
  assert.strictEqual(group.participants[0].isAdmin, true);
  assert.strictEqual(group.participants[1].isAdmin, false);
  assert.strictEqual(group.participants[1].displayName, 'Bruno');
});

test('groupInfo legge la descrizione, che GOWA non nomina sempre allo stesso modo', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: { Name: 'Famiglia', Topic: 'solo foto' } }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const info = await client.groupInfo('123@g.us');

  assert.strictEqual(fetchImpl.calls[0].url, 'http://g/group/info?group_id=123%40g.us');
  assert.strictEqual(info.name, 'Famiglia');
  assert.strictEqual(info.topic, 'solo foto');
});

test('sendAudio posts the audio field on /send/audio', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://g',
    fetchImpl: async (url, options) => {
      seen.push({ url, field: [...options.body.keys()].join(',') });
      return jsonResponse({ status: 200, results: { message_id: 'A1' } });
    }
  });

  assert.strictEqual(
    await client.sendAudio('39@s.whatsapp.net', '', Buffer.from([3]), 'audio/mp4', 'voce.m4a'),
    'A1');

  assert.strictEqual(seen[0].url, 'http://g/send/audio');
  assert.ok(seen[0].field.includes('audio'));
  assert.ok(seen[0].field.includes('phone'));
});
