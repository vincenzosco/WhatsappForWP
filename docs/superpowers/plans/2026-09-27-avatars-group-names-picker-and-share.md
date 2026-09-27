# Avatars, Group Names, Image Picker and Share Target Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the chat list show real profile pictures and real group subjects, and let an image reach WhatsApp on a WP8.1 phone from the attach button and from the system share sheet.

**Architecture:** Two bugs are in the Node adapter and are fixed there, with tests: `/user/avatar` returns the picture's *address*, not the picture, and GOWA's chat list has no usable name for groups (`/user/my/groups` does). One bug is the app using a picker API Windows Phone does not implement: `PickSingleFileAsync` must become `PickSingleFileAndContinue`, whose result arrives after the app is deactivated and reactivated. The fourth is a missing manifest contract: a `windows.shareTarget` extension plus `App.OnShareTargetActivated`. Because both of the last two deliver a file to a process that may have been restarted, they share one place that holds those bytes, `AttachmentInbox`.

**Tech Stack:** Node 18.13+ (adapter, `node:test` for tests), C# 5 on WP8.1 WinRT (app), MSBuild 12.0 in the Parallels VM.

## Global Constraints

- Adapter code is CommonJS, `'use strict';`, `node:test` + `node:assert` tests. Run with `cd WhatsappBridge && npm test`.
- The app is C# 5 only: no interpolated strings, no `?.`, no expression-bodied members, no auto-property initializers, no `nameof`, no `using static`. `node tools/check-csharp5.js` enforces this and also rejects Windows-only members.
- All runtime text (logs, diagnostics, resource strings) is English. Comments may be Italian, matching the surrounding code.
- The adapter is the only place that knows GOWA's HTTP shapes; the app only ever sees adapter frames.
- The app never reads the address book by itself. `ContactPicker` and the file picker are the user's consent.
- Documentation comes in pairs that must stay mirror images: `README.md` + `README.it.md`, `WhatsappBridge/README.md` + `WhatsappBridge/README.it.md`. Same heading count, order and level. Root pair keeps `## Disclosure` last. No emoji anywhere in `.md`.
- Every `.resw` key added to `WhatsappApp/Strings/en-US/Resources.resw` needs the same key in `it-IT`, and the XAML must keep a literal fallback next to `x:Uid` (`node tools/check-resw.js --strict` reads it).
- Gates after every task: `node tools/check-csharp5.js`, `node tools/check-icons.js`, `node tools/check-resw.js --strict`, `node tools/check-docs.js`, `node tools/check-framing.js`, `node tools/qr-term.js --self-test`, `cd WhatsappBridge && npm test`, `node --test "tools/test/**/*.test.js"`.
- Build gate (VM, Windows 11 via Parallels): `prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"`, then `robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%` (expect `COPIA=0`), then `MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86` (expect 0 errors). Before this change the only warning was CS0618 on the file picker; after Task 3 the only CS0618 must be on `PickSingleFileAndContinue`, suppressed with a `#pragma` and a comment.
- Commit messages are English, `type: short imperative`. One commit per task, then push.

---

### Task 1: The avatar arrives whole

`GowaClient#avatar()` fetches `user/avatar?...` and base64-encodes the response body. GOWA's `serviceUser.Avatar` returns JSON, not an image: `AvatarResponse{URL string \`json:"url"\`; ID; Type}` marshalled into `{"results":{"url":"https://pps.whatsapp.net/...","id":"...","type":"..."}}`. So the app receives base64 of a JSON document, `BitmapImage.SetSourceAsync` fails, and `Contact.HasAvatar` stays false for everybody: no profile pictures, ever. `fetchBinary(urlOrPath)` already accepts an absolute URL, so this is one extra hop.

**Files:**
- Modify: `WhatsappBridge/gowa-client.js` (the `avatar` method, around line 152)
- Test: `WhatsappBridge/test/gowa-client.test.js`
- Modify: `README.md`, `README.it.md`

**Interfaces:**
- Consumes: `GowaClient#request(method, path)` (returns `{ ok, status, data }`) and `GowaClient#fetchBinary(urlOrPath)` (returns `{ buffer, contentType }`, accepts an absolute URL).
- Produces: `GowaClient#avatar(jid): Promise<string|null>` - base64 of the picture bytes, or `null`. Signature unchanged; only the number of HTTP requests changes (two for a person with a picture, one otherwise).

- [ ] **Step 1: Replace the two avatar tests with three that describe the two hops**

In `WhatsappBridge/test/gowa-client.test.js`, delete the whole test starting `test('avatar() asks GOWA for the person, never for a group, and returns base64'` and the whole test starting `test('avatar() returns null when GOWA has no picture'`. Put these three in their place:

```js
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

  assert.strictEqual(seen[0], 'http://127.0.0.1:3000/user/avatar?phone=393401234567&is_preview=true');
  assert.strictEqual(seen[1], 'https://pps.whatsapp.net/v/t1/abc.jpg');
  assert.strictEqual(picture, Buffer.from([1, 2, 3]).toString('base64'));
});

test('avatar() asks nothing about a group, and nothing when GOWA has no picture', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      return jsonResponse({ status: 404, results: {} });
    }
  });

  // I gruppi non hanno un avatar personale: nessuna richiesta.
  assert.strictEqual(await client.avatar('123456789012345678@g.us'), null);
  assert.strictEqual(seen.length, 0);

  // Nessuna immagine: GOWA risponde con un errore e non c'e' niente da
  // scaricare, quindi una sola richiesta.
  assert.strictEqual(await client.avatar('393401234567@s.whatsapp.net'), null);
  assert.strictEqual(seen.length, 1);
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: the first test FAILS with `seen[1]` undefined (`AssertionError`), the second FAILS with `seen.length` 1 instead of 0, the third FAILS with a non-null base64 string. The other tests in the file still pass.

- [ ] **Step 3: Implement the second hop**

In `WhatsappBridge/gowa-client.js`, replace the whole `avatar(jid)` method with:

```js
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
  // I gruppi non hanno un avatar personale, quindi non si chiede.
  async avatar(jid) {
    const value = String(jid || '');
    if (!value || value.endsWith('@g.us')) return null;

    const phone = value.split('@')[0];
    try {
      const r = await this.request('GET',
        `/user/avatar?phone=${encodeURIComponent(phone)}&is_preview=true`);
      const url = (r.data && r.data.results && r.data.results.url) || '';
      if (!r.ok || !url) return null;

      const picture = await this.fetchBinary(url);
      return picture.buffer.length > 0 ? picture.buffer.toString('base64') : null;
    } catch (err) {
      return null;
    }
  }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 84 tests. (83 before: two avatar tests removed, three added, so the file grows by one.)

- [ ] **Step 5: Say in the docs that an avatar costs two requests**

In `README.md`, replace:

```
Each row carries the last message and, for people, the profile picture
(`GET /user/avatar`, disabled with `CHATS_AVATARS=off`).
```

with:

```
Each row carries the last message and, for people, the profile picture
(`GET /user/avatar` and then the CDN address it returns, so two requests per
person; disabled with `CHATS_AVATARS=off`).
```

In `README.it.md`, replace:

```
l'immagine del profilo (`GET /user/avatar`, si spegne con `CHATS_AVATARS=off`).
```

with:

```
l'immagine del profilo (`GET /user/avatar` e poi l'indirizzo CDN che restituisce,
quindi due richieste per persona; si spegne con `CHATS_AVATARS=off`).
```

Then in both files replace the feature bullet. `README.md`:

```
- Lists the account's real **conversations** from `GET /chats` (the address book is empty on a freshly linked device), each with its last message and, for people, the **profile picture** from `GET /user/avatar` (`CHATS_LIMIT`, `CHATS_AVATARS`)
```

becomes:

```
- Lists the account's real **conversations** from `GET /chats` (the address book is empty on a freshly linked device), each with its last message and, for people, the **profile picture** from `GET /user/avatar` (two requests per person: the endpoint returns the picture's address, not the picture) (`CHATS_LIMIT`, `CHATS_AVATARS`)
```

`README.it.md`:

```
- elenca le **conversazioni** vere dell'account da `GET /chats` (la rubrica e' vuota su un dispositivo appena collegato), ognuna con l'ultimo messaggio e, per le persone, l'**immagine del profilo** da `GET /user/avatar` (`CHATS_LIMIT`, `CHATS_AVATARS`)
```

becomes:

```
- elenca le **conversazioni** vere dell'account da `GET /chats` (la rubrica e' vuota su un dispositivo appena collegato), ognuna con l'ultimo messaggio e, per le persone, l'**immagine del profilo** da `GET /user/avatar` (due richieste per persona: l'endpoint restituisce l'indirizzo dell'immagine, non l'immagine) (`CHATS_LIMIT`, `CHATS_AVATARS`)
```

- [ ] **Step 6: Run the docs guard and commit**

```bash
node tools/check-docs.js
git add WhatsappBridge/gowa-client.js WhatsappBridge/test/gowa-client.test.js README.md README.it.md
git commit -m "fix: download the avatar GOWA points at instead of its JSON"
```

---

### Task 2: Group rows carry the real subject

GOWA's `ChatDisplayNameResolver.Resolve` keeps a stored chat name only when it is meaningful; for a group with no stored name it returns `"Group " + jid.User`. That is the string the user sees (`Group 123456789012345678`), and our own `displayNameForJid` produces the same shape, so nothing in the chat list has a subject. `GET /user/my/groups` returns every joined group with its subject, in one request.

**Files:**
- Modify: `WhatsappBridge/gowa-client.js` (add `myGroups`)
- Modify: `WhatsappBridge/chats.js` (accept and use a name map)
- Modify: `WhatsappBridge/server.js` (`sendChats` fetches the map once per scan)
- Test: `WhatsappBridge/test/gowa-client.test.js`, `WhatsappBridge/test/chats.test.js`
- Modify: `README.md`, `README.it.md`

**Interfaces:**
- Consumes: `GowaClient#request`.
- Produces: `GowaClient#myGroups(): Promise<Map<string, string>>` mapping a group JID to its subject, empty when GOWA cannot answer; `collectChats({ gowa, limit, avatars, groupNames, log })` where `groupNames` is that `Map` (optional, defaults to an empty one).

- [ ] **Step 1: Write the failing tests**

Append to `WhatsappBridge/test/gowa-client.test.js`:

```js
test('myGroups() maps every joined group to its real name', async () => {
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      assert.strictEqual(url, 'http://127.0.0.1:3000/user/my/groups');
      // whatsmeow's GroupInfo non ha tag json e incorpora GroupName: encoding/json
      // promuove i campi, quindi il nome arriva come "Name" di primo livello.
      return jsonResponse({
        status: 200,
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
    fetchImpl: async () => jsonResponse({ status: 500, results: {} })
  });

  const names = await client.myGroups();

  assert.strictEqual(names.size, 0);
});
```

Append to `WhatsappBridge/test/chats.test.js`:

```js
test('collectChats names a group from the group map, not from GOWA placeholder', async () => {
  const gowa = fakeGowa({
    chats: [{ jid: '123456789012345678@g.us', name: 'Group 123456789012345678' }],
    messagesByJid: { '123456789012345678@g.us': [{ content: 'ciao', timestamp: '2026-09-26T09:00:00Z' }] }
  });

  const rows = await collectChats({
    gowa,
    limit: 10,
    avatars: false,
    groupNames: new Map([['123456789012345678@g.us', 'Amici']]),
    log: () => {}
  });

  assert.strictEqual(rows[0].name, 'Amici');
  assert.strictEqual(rows[0].isGroup, true);
});

test('collectChats keeps what GOWA said when the group map has no entry', async () => {
  const gowa = fakeGowa({
    chats: [{ jid: '123456789012345678@g.us', name: 'Amici veri' }],
    messagesByJid: {}
  });

  const rows = await collectChats({ gowa, limit: 10, avatars: false, log: () => {} });

  assert.strictEqual(rows[0].name, 'Amici veri');
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js test/chats.test.js`
Expected: FAIL - `client.myGroups is not a function` (twice), `rows[0].name` is `Group 123456789012345678` instead of `Amici`. The second `collectChats` test already passes.

- [ ] **Step 3: Implement `myGroups()` and the tolerant name reader**

In `WhatsappBridge/gowa-client.js`, add this function above `class GowaClient` (next to `buildAuthHeader`):

```js
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
```

Then add this method to `GowaClient`, right after `contacts()`:

```js
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
```

Export the helper so the test can reach it only if needed - it is not needed; leave `module.exports` unchanged.

- [ ] **Step 4: Use the map in `chats.js`**

In `WhatsappBridge/chats.js`, add the option next to the others inside `collectChats`:

```js
  const groupNames = opts.groupNames instanceof Map ? opts.groupNames : new Map();
```

and replace:

```js
    const isGroup = isGroupJid(chat.jid);
    const name = chat.name || displayNameForJid(chat.jid);
```

with:

```js
    const isGroup = isGroupJid(chat.jid);
    // Per un gruppo si preferisce il nome vero da /user/my/groups: quello che
    // arriva con l'elenco delle conversazioni puo' essere il segnaposto
    // "Group <numero>" di GOWA, o il numero nudo.
    const name = (isGroup && groupNames.get(chat.jid)) || chat.name || displayNameForJid(chat.jid);
```

- [ ] **Step 5: Fetch the map once per scan in `server.js`**

In `WhatsappBridge/server.js`, add this module-level function next to the other helpers above the server factory:

```js
// I nomi dei gruppi in una richiesta. Un nome che manca costa un nome, non
// l'elenco: se GOWA non risponde si torna una mappa vuota e le righe dei gruppi
// restano con quello che l'elenco delle conversazioni diceva.
async function groupNamesOrEmpty(client, logger) {
  try {
    return await client.myGroups();
  } catch (err) {
    logger('DEBUG', `Chats: group names not readable (${err.message})`);
    return new Map();
  }
}
```

Then in `sendChats`, inside the `if (fresh)` block, replace:

```js
        logger('INFO', `reading up to ${limits.limit || 25} conversation(s)...`);
        const rows = await collectChats({
          gowa,
          limit: limits.limit,
          avatars: limits.avatars,
          log: logger
        });
```

with:

```js
        logger('INFO', `reading up to ${limits.limit || 25} conversation(s)...`);
        const groupNames = await groupNamesOrEmpty(gowa, logger);
        const rows = await collectChats({
          gowa,
          limit: limits.limit,
          avatars: limits.avatars,
          groupNames,
          log: logger
        });
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 88 tests.

- [ ] **Step 7: Document where a group name comes from**

In `README.md`, replace:

```
Each row carries the last message and, for people, the profile picture
(`GET /user/avatar` and then the CDN address it returns, so two requests per
person; disabled with `CHATS_AVATARS=off`).
```

with:

```
Each row carries the last message and, for people, the profile picture
(`GET /user/avatar` and then the CDN address it returns, so two requests per
person; disabled with `CHATS_AVATARS=off`). A group row is named from
`GET /user/my/groups`, one request for all of them, because the chat list has no
usable name for a group and falls back to `Group <number>`.
```

In `README.it.md`, replace:

```
l'immagine del profilo (`GET /user/avatar` e poi l'indirizzo CDN che restituisce,
quindi due richieste per persona; si spegne con `CHATS_AVATARS=off`).
```

with:

```
l'immagine del profilo (`GET /user/avatar` e poi l'indirizzo CDN che restituisce,
quindi due richieste per persona; si spegne con `CHATS_AVATARS=off`). Il nome di
un gruppo arriva da `GET /user/my/groups`, una richiesta per tutti, perche'
l'elenco delle conversazioni non ha un nome utilizzabile per un gruppo e ripiega
su `Group <numero>`.
```

- [ ] **Step 8: Run the docs guard and commit**

```bash
node tools/check-docs.js
git add WhatsappBridge/gowa-client.js WhatsappBridge/chats.js WhatsappBridge/server.js WhatsappBridge/test/gowa-client.test.js WhatsappBridge/test/chats.test.js README.md README.it.md
git commit -m "fix: name a group from the group list, not from its placeholder"
```

---

### Task 3: The attach button opens the picker

`FileOpenPicker.PickSingleFileAsync` is documented as unsupported on Windows Phone, for both Windows Runtime and Silverlight; the WP-supported call is `PickSingleFileAndContinue`, which deactivates the app and delivers the file to `App.OnActivated` as `ActivationKind.PickFileContinuation` with a `FileOpenPickerContinuationEventArgs`. Today `PickSingleFileAsync` throws on the phone, the catch logs it, and the button appears dead.

Because the app can be terminated while the picker is open, the chosen file cannot be handed to a page instance: the bytes are read at reactivation, into `AttachmentInbox`, and whichever page is showing an attachment picks them up. The page also stops holding a `StorageFile` - it holds the bytes, the name and the type - so the same preview bar serves both the picker and (in Task 4) a share.

**Files:**
- Create: `WhatsappApp/Services/AttachmentInbox.cs`
- Create: `WhatsappApp/Services/ImagePickerService.cs`
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (add the two files to the compile item group)
- Modify: `WhatsappApp/App.xaml.cs`

**Interfaces:**
- Consumes: `ImageHelper.FromBase64Async(string)`.
- Produces: `AttachmentInbox.HasAttachment` (`bool`), `AttachmentInbox.Base64`, `AttachmentInbox.FileName`, `AttachmentInbox.MimeType`, `AttachmentInbox.Note` (all `string`), `AttachmentInbox.Ready` (`event Action`), `AttachmentInbox.Clear()`, `AttachmentInbox.PutAsync(StorageFile, string)` (`Task`), `AttachmentInbox.PutBytes(byte[], string, string, string)`; `ImagePickerService.RequestImage()`.

- [ ] **Step 1: Write the inbox**

Create `WhatsappApp/Services/AttachmentInbox.cs`:

```csharp
using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Un file che entra nell'app da fuori: il selettore immagini o la
    /// condivisione di un'altra applicazione.
    ///
    /// In tutti e due i casi la pagina che lo riceverebbe puo' non esistere piu'
    /// nel momento in cui il file arriva: WP8.1 sospende l'app mentre il
    /// selettore e' aperto, e puo' terminarla. Non si puo' quindi tenere un
    /// riferimento al file aspettando una pagina: i byte si leggono subito, e la
    /// pagina che sta davanti li ritira quando puo'.
    ///
    /// Chi riceve un allegato: ChatPage, che lo mostra nella barra di anteprima;
    /// ChatsPage, che dice che c'e' qualcosa da inviare. Entrambe si iscrivono a
    /// Ready, perche' dopo il selettore la pagina e' ancora quella davanti senza
    /// che OnNavigatedTo venga chiamato di nuovo.
    /// </summary>
    public static class AttachmentInbox
    {
        private static string _base64;
        private static string _fileName;
        private static string _mimeType;
        private static string _note;

        /// <summary>Un allegato che nessuno ha ancora ritirato.</summary>
        public static event Action Ready;

        public static bool HasAttachment
        {
            get { return !string.IsNullOrEmpty(_base64); }
        }

        public static string Base64
        {
            get { return _base64; }
        }

        public static string FileName
        {
            get { return _fileName; }
        }

        public static string MimeType
        {
            get { return _mimeType; }
        }

        /// <summary>Testo che accompagnava la condivisione, se ce n'era uno.</summary>
        public static string Note
        {
            get { return _note; }
        }

        /// <summary>
        /// Deposita un file scelto o condiviso. Si legge adesso, non quando
        /// servira': dopo una riattivazione il riferimento al file puo' non
        /// essere piu' valido.
        /// </summary>
        public static async Task PutAsync(StorageFile file, string note)
        {
            if (file == null) return;

            byte[] buffer;
            using (var stream = await file.OpenReadAsync())
            {
                using (var reader = new DataReader(stream))
                {
                    uint size = (uint)stream.Size;
                    await reader.LoadAsync(size);
                    buffer = new byte[size];
                    reader.ReadBytes(buffer);
                }
            }

            PutBytes(buffer, file.Name, MimeFor(file.FileType), note);
        }

        /// <summary>Deposita i byte gia' letti (una bitmap condivisa, per esempio).</summary>
        public static void PutBytes(byte[] buffer, string fileName, string mimeType, string note)
        {
            if (buffer == null || buffer.Length == 0) return;

            _base64 = Convert.ToBase64String(buffer);
            _fileName = fileName;
            _mimeType = string.IsNullOrEmpty(mimeType) ? "image/jpeg" : mimeType;
            _note = note;

            var handler = Ready;
            if (handler != null) handler();
        }

        /// <summary>Ritira l'allegato: chi lo mostra lo fa una volta sola.</summary>
        public static void Clear()
        {
            _base64 = null;
            _fileName = null;
            _mimeType = null;
            _note = null;
        }

        /// <summary>Il tipo MIME di un'estensione, come la manda WhatsApp.</summary>
        private static string MimeFor(string extension)
        {
            string value = (extension ?? "").ToLower();
            if (value == ".png") return "image/png";
            if (value == ".gif") return "image/gif";
            if (value == ".bmp") return "image/bmp";
            return "image/jpeg";
        }
    }
}
```

- [ ] **Step 2: Write the picker service**

Create `WhatsappApp/Services/ImagePickerService.cs`:

```csharp
using System;
using Windows.Storage.Pickers;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Apre il selettore di file di WP8.1.
    ///
    /// Si usa PickSingleFileAndContinue e NON PickSingleFileAsync: la
    /// documentazione Microsoft dice che PickSingleFileAsync non e' supportato
    /// su Windows Phone (ne' per Windows Runtime ne' per Silverlight) e indica
    /// PickSingleFileAndContinue. Sul telefono la prima falliva, e il pulsante
    /// allegato sembrava morto.
    ///
    /// La differenza che conta: PickSingleFileAndContinue non restituisce
    /// niente. Deattiva l'app, e il file scelto torna ad App.OnActivated come
    /// PickFileContinuation. Il risultato non passa quindi da qui: lo deposita
    /// App in AttachmentInbox.
    /// </summary>
    public static class ImagePickerService
    {
        public static void RequestImage()
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".bmp");

            // CS0618: deprecata da Windows 10, ma e' l'unica che Windows Phone
            // 8.1 implementa. Non e' un warning da sistemare, e' la piattaforma.
#pragma warning disable 618
            picker.PickSingleFileAndContinue();
#pragma warning restore 618
        }
    }
}
```

- [ ] **Step 3: Add both files to the project**

In `WhatsappApp/WhatsappApp.csproj`, find the line that compiles `Services\AttachmentInbox`'s neighbours - the `<Compile Include="Services\SettingsService.cs" />` entry - and add next to it, keeping the existing alphabetical order of the `Services` entries:

```xml
    <Compile Include="Services\AttachmentInbox.cs" />
    <Compile Include="Services\ImagePickerService.cs" />
```

- [ ] **Step 4: Make the page hold bytes instead of a file**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, replace the field declarations:

```csharp
        private StorageFile _selectedImageFile;
        private string _selectedImageBase64;
```

with:

```csharp
        // L'immagine scelta: i byte, piu' cio' che serve per inviarla. Non il
        // StorageFile: dopo il selettore il file puo' appartenere a un processo
        // che non c'e' piu' (vedi AttachmentInbox).
        private string _selectedImageBase64;
        private string _selectedImageFileName;
        private string _selectedImageMimeType;
```

Remove the now-unused usings `using Windows.Storage;`, `using Windows.Storage.Pickers;` and `using Windows.Storage.Streams;` from the top of the file.

- [ ] **Step 5: Replace the picker handler with the continuation request, and pick the attachment up**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, replace the whole `AttachButton_Click` method with:

```csharp
        /// <summary>
        /// Pulsante allegato: chiede il selettore di sistema. La risposta non
        /// arriva qui - arriva ad App.OnActivated dopo che l'app e' stata
        /// riattivata - quindi non c'e' niente da attendere.
        /// </summary>
        private void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ImagePickerService.RequestImage();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage/image", ex);
                Debug.WriteLine(string.Format(
                    Loc.Get("ChatPage_ImageError", "Could not open the image: {0}"), ex.Message));
            }
        }

        /// <summary>
        /// Un allegato e' arrivato mentre questa chat era aperta: e' il caso
        /// normale, perche' il selettore si apre da qui e l'app torna qui.
        /// </summary>
        private void OnAttachmentReady()
        {
            ShowPendingAttachment();
        }

        /// <summary>
        /// Mostra l'allegato in attesa, se c'e'. Chiamato sia navigando qui sia
        /// all'arrivo: dopo il selettore la pagina e' ancora quella davanti e
        /// OnNavigatedTo non viene richiamato.
        /// </summary>
        private void ShowPendingAttachment()
        {
            if (!AttachmentInbox.HasAttachment) return;

            string note = AttachmentInbox.Note;
            _selectedImageBase64 = AttachmentInbox.Base64;
            _selectedImageFileName = AttachmentInbox.FileName;
            _selectedImageMimeType = AttachmentInbox.MimeType;
            AttachmentInbox.Clear();

            if (!string.IsNullOrEmpty(note) && string.IsNullOrEmpty(MessageTextBox.Text))
            {
                MessageTextBox.Text = note;
            }

            ImagePreviewBar.Visibility = Visibility.Visible;
#pragma warning disable 4014
            ShowLocalPreviewAsync(_selectedImageBase64);
#pragma warning restore 4014
        }

        /// <summary>Anteprima locale: il mittente vede la propria immagine.</summary>
        private async System.Threading.Tasks.Task ShowLocalPreviewAsync(string base64)
        {
            SelectedImagePreview.Source = await ImageHelper.FromBase64Async(base64);
        }
```

- [ ] **Step 6: Follow the attachment in navigation and drop the file from the send path**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, in `OnNavigatedTo`, right after the line `CommunicationService.Instance.MessageReceived += OnMessageReceived;`, add:

```csharp
                // Un'immagine arrivata da fuori puo' essere arrivata mentre
                // questa pagina non c'era (processo riavviato): si ritira qui, e
                // da qui in poi anche all'arrivo.
                AttachmentInbox.Ready += OnAttachmentReady;
                ShowPendingAttachment();
```

in `OnNavigatedFrom`, right after `CommunicationService.Instance.MessageReceived -= OnMessageReceived;`, add:

```csharp
            AttachmentInbox.Ready -= OnAttachmentReady;
```

replace the guard in `SendMessage`:

```csharp
            if (_selectedImageFile != null && _selectedImageBase64 != null)
```

with:

```csharp
            if (_selectedImageBase64 != null)
```

and in `SendImageMessage` replace:

```csharp
            string mimeType = "image/jpeg";
            string extension = _selectedImageFile == null ? null : _selectedImageFile.FileType.ToLower();
            if (extension == ".png") mimeType = "image/png";
            else if (extension == ".gif") mimeType = "image/gif";
            else if (extension == ".bmp") mimeType = "image/bmp";
```

with:

```csharp
            // Il tipo lo decide chi ha consegnato l'immagine: AttachmentInbox lo
            // ricava dall'estensione una volta sola.
            string mimeType = _selectedImageMimeType ?? "image/jpeg";
```

and then replace:

```csharp
                MediaFileName = _selectedImageFile == null ? null : _selectedImageFile.Name
```

with:

```csharp
                MediaFileName = _selectedImageFileName
```

and replace `ClearSelectedImage` with:

```csharp
        private void ClearSelectedImage()
        {
            _selectedImageBase64 = null;
            _selectedImageFileName = null;
            _selectedImageMimeType = null;
            SelectedImagePreview.Source = null;
            ImagePreviewBar.Visibility = Visibility.Collapsed;
        }
```

- [ ] **Step 7: Hand the continuation to the inbox in `App.xaml.cs`**

In `WhatsappApp/App.xaml.cs`, add `using Windows.Storage;` next to the other `using` lines, add this field next to `navigationFailure`:

```csharp
        // Servizi e watchdog esistono una volta per processo: una condivisione
        // puo' riattivare un'app gia' avviata, e non si devono raddoppiare.
        private bool servicesStarted;
```

replace the block in `OnLaunched` from `Loc.Prewarm();` down to `ConnectionWatchdog.Instance.Start();` with:

```csharp
            StartServicesOnce();
```

replace:

```csharp
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                // Tre pagine di sezione (Chats/Status/Calls) con
                // NavigationCacheMode.Enabled: la cache le tiene in vita, cosi'
                // passare da una sezione all'altra non ricostruisce la pagina
                // (l'elenco chat conserva anche la posizione di scorrimento).
                rootFrame.CacheSize = 3;
                rootFrame.Language = Windows.Globalization.ApplicationLanguages.Languages[0];

                Window.Current.Content = rootFrame;
            }
```

with:

```csharp
            Frame rootFrame = EnsureFrame();
```

and add these three methods at the end of the class:

```csharp
        /// <summary>
        /// Tutto cio' che deve esistere una volta sola per processo, prima della
        /// prima pagina. Lo chiamano sia OnLaunched sia una condivisione: un'app
        /// avviata dalla condivisione non passa da OnLaunched.
        /// </summary>
        private void StartServicesOnce()
        {
            if (servicesStarted) return;
            servicesStarted = true;

            // Il loader delle risorse non si puo' creare da un thread di
            // background: lo si crea qui, una volta, sul thread UI.
            Loc.Prewarm();

            // Stesso motivo del loader: il dispatcher si trova di sicuro solo
            // qui, sul thread UI. Risolverlo piu' tardi, da un thread di rete,
            // lasciava il servizio senza dispatcher per tutta la sessione.
            CommunicationService.Instance.Prewarm();

            // Il servizio dati si aggancia qui: prima si creava alla prima
            // pagina che lo toccava, e i messaggi arrivati nel frattempo (o i
            // contatti sincronizzati) non avevano nessun ascoltatore.
            DataService.Instance.Start();

#if DEBUG
            // Solo in debug: dice in tre righe cosa questo telefono sa fare
            // davvero, invece di lasciarlo scoprire da un catch silenzioso. In
            // rilascio non esiste, quindi non costa niente all'avvio.
            SelfCheck.RunAsync();
#endif

            // Riconnessione automatica: l'app non riprova da sola dopo un
            // riavvio, e senza questo l'elenco chat resta vuoto finche' l'utente
            // non apre le impostazioni.
            if (SettingsService.HasSavedSettings) StartAutoConnect();

            // E poi la tiene viva: WP8.1 chiude il socket sospendendo l'app, e
            // alla ripresa la connessione risulta attiva ma non passa piu'
            // niente (vedi ConnectionWatchdog).
            ConnectionWatchdog.Instance.Start();
        }

        /// <summary>
        /// Il frame radice, creato se non c'e'. Tre pagine di sezione
        /// (Chats/Status/Calls) con NavigationCacheMode.Enabled: la cache le
        /// tiene in vita, cosi' passare da una sezione all'altra non ricostruisce
        /// la pagina (l'elenco chat conserva anche la posizione di scorrimento).
        /// </summary>
        private static Frame EnsureFrame()
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame != null) return rootFrame;

            rootFrame = new Frame();
            rootFrame.CacheSize = 3;
            rootFrame.Language = Windows.Globalization.ApplicationLanguages.Languages[0];
            Window.Current.Content = rootFrame;
            return rootFrame;
        }

        /// <summary>
        /// Riattivazione: non e' un avvio. L'unico caso che questo punto di
        /// ingresso deve gestire e' il selettore di file, che non ha un
        /// risultato di ritorno: il file scelto arriva qui.
        /// </summary>
        protected override void OnActivated(IActivatedEventArgs e)
        {
            base.OnActivated(e);

            if (e == null || e.Kind != ActivationKind.PickFileContinuation) return;

            var continuation = e as FileOpenPickerContinuationEventArgs;
            if (continuation == null || continuation.Files == null || continuation.Files.Count == 0)
            {
                return;
            }

            // OnActivated non e' async: il file si deposita e basta, e la pagina
            // che e' davanti lo ritira con l'evento di AttachmentInbox.
#pragma warning disable 4014
            AttachmentInbox.PutAsync(continuation.Files[0], null);
#pragma warning restore 4014
        }
```

- [ ] **Step 8: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && cd WhatsappBridge && npm test`
Expected: `OK: 31 C# file(s) are C# 5 compatible.`, icons OK, 103 keys, docs OK, 88 adapter tests.

- [ ] **Step 9: Build in the VM**

```bash
prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"
```

Expected: `COPIA=0`, `0 Error(s)`, `Your package has been successfully created`. If `PickSingleFileAndContinue` or `FileOpenPickerContinuationEventArgs` is reported as missing (CS1061/CS0117), stop and report: it means the WP8.1 projection is narrower than the documentation says, and the picker needs the `Windows.Phone.ApplicationModel.ContinuationManager` route instead. Do not paper over it by going back to `PickSingleFileAsync`.

- [ ] **Step 10: Document the picker constraint**

In `README.md`, in the `#### Chats and new chats` section, after the paragraph about the three ways to start a chat, add:

```
Attaching an image uses `PickSingleFileAndContinue`. `PickSingleFileAsync` is
documented as unsupported on Windows Phone, and on the phone it failed silently:
the app is deactivated while the picker is open, and the chosen file arrives at
`App.OnActivated` as a `PickFileContinuation`.
```

In `README.it.md`, in the same position, add:

```
Allegare un'immagine usa `PickSingleFileAndContinue`. `PickSingleFileAsync` e'
documentata come non supportata su Windows Phone, e sul telefono falliva in
silenzio: l'app viene deattivata mentre il selettore e' aperto, e il file scelto
arriva ad `App.OnActivated` come `PickFileContinuation`.
```

- [ ] **Step 11: Commit**

```bash
node tools/check-docs.js
git add WhatsappApp README.md README.it.md
git commit -m "fix: open the image picker with the call Windows Phone implements"
```

---

### Task 4: WhatsApp appears in the share sheet

WP8.1 has no share-target declaration in the manifest and no `OnShareTargetActivated` override, so no app can hand this app a picture: tapping Share in Photos lists no WhatsApp. This adds the contract and the receiver, reusing `AttachmentInbox` from Task 3, and tells the user on the chat list that an image is waiting.

**Files:**
- Modify: `WhatsappApp/Package.appxmanifest`
- Modify: `WhatsappApp/App.xaml.cs`
- Modify: `WhatsappApp/Pages/ChatsPage.xaml`, `WhatsappApp/Pages/ChatsPage.xaml.cs`
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`
- Modify: `README.md`, `README.it.md`

**Interfaces:**
- Consumes: `AttachmentInbox.HasAttachment`, `AttachmentInbox.PutAsync`, `AttachmentInbox.PutBytes`, `AttachmentInbox.Ready`, `App.EnsureFrame()`, `App.StartServicesOnce()` (all from Task 3).
- Produces: nothing new for other tasks.

- [ ] **Step 1: Declare the share target**

In `WhatsappApp/Package.appxmanifest`, inside the `<Application>` element, after the closing `</m3:VisualElements>` line and before `</Application>`, add:

```xml
        <Extensions>
            <!-- Riceve immagini condivise da un'altra app (Galleria, Foto,
                 browser). Va dichiarato qui: senza questa estensione WP8.1 non
                 elenca l'app nel pannello di condivisione. -->
            <m3:Extension Category="windows.shareTarget"
                          Executable="$targetnametoken$.exe"
                          EntryPoint="WhatsappApp.App">
                <m3:ShareTarget>
                    <m3:SupportedFileTypes>
                        <m3:FileType>.jpg</m3:FileType>
                        <m3:FileType>.jpeg</m3:FileType>
                        <m3:FileType>.png</m3:FileType>
                        <m3:FileType>.gif</m3:FileType>
                        <m3:FileType>.bmp</m3:FileType>
                    </m3:SupportedFileTypes>
                    <m3:DataFormat>Bitmap</m3:DataFormat>
                    <m3:DataFormat>StorageItems</m3:DataFormat>
                </m3:ShareTarget>
            </m3:Extension>
        </Extensions>
```

- [ ] **Step 2: Build to prove the manifest schema accepts it**

```bash
prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"
```

Expected: `COPIA=0`, `0 Error(s)`. If the manifest validation rejects `m3:ShareTarget` (`error APPX...` / `The 'ShareTarget' element is not declared`), the extension belongs to the 2013 namespace in this schema: change the prefix on `Extension`, `ShareTarget`, `SupportedFileTypes`, `FileType` and `DataFormat` from `m3:` to `m2:`, rebuild, and leave the prefix that passes in place.

- [ ] **Step 3: Receive the share**

In `WhatsappApp/App.xaml.cs`, add these methods after `OnActivated`:

```csharp
        /// <summary>
        /// Un'altra applicazione sta condividendo qualcosa con questa (Galleria,
        /// Foto, browser). L'immagine si deposita e si portano davanti le chat:
        /// il passo successivo e' scegliere a chi mandarla.
        /// </summary>
        protected override void OnShareTargetActivated(ShareTargetActivatedEventArgs e)
        {
            base.OnShareTargetActivated(e);

            if (e == null || e.ShareOperation == null) return;

            // Questa attivazione puo' essere l'avvio del processo: i servizi e il
            // frame non ci sono ancora.
            StartServicesOnce();
            var rootFrame = EnsureFrame();
            if (!(rootFrame.Content is ChatsPage))
            {
                rootFrame.Navigate(typeof(ChatsPage));
            }
            Window.Current.Activate();

#pragma warning disable 4014
            AcceptShareAsync(e.ShareOperation);
#pragma warning restore 4014
        }

        /// <summary>
        /// Legge cio' che e' stato condiviso, se e' un'immagine.
        ///
        /// Due forme possibili: un elenco di file (quasi tutte le app) o una
        /// bitmap sola. Un testo non si usa: l'utente ha chiesto di condividere
        /// un'immagine, e l'app si limita a non fare niente se non c'e'.
        /// </summary>
        private async System.Threading.Tasks.Task AcceptShareAsync(ShareOperation operation)
        {
            try
            {
                var data = operation.Data;
                if (data == null) return;

                if (data.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await data.GetStorageItemsAsync();
                    for (int i = 0; i < items.Count; i++)
                    {
                        var file = items[i] as StorageFile;
                        if (file == null) continue;

                        await AttachmentInbox.PutAsync(file, null);
                        break;
                    }
                }
                else if (data.Contains(StandardDataFormats.Bitmap))
                {
                    var reference = await data.GetBitmapAsync();
                    using (var stream = await reference.OpenReadAsync())
                    {
                        using (var reader = new DataReader(stream))
                        {
                            uint size = (uint)stream.Size;
                            await reader.LoadAsync(size);
                            var buffer = new byte[size];
                            reader.ReadBytes(buffer);
                            AttachmentInbox.PutBytes(buffer, "shared.png", "image/png", null);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("App/share", ex);
            }
            finally
            {
                // Va sempre detto che la condivisione e' finita: un'operazione
                // non riportata lascia l'app chiamante a girare a vuoto.
                try
                {
                    operation.ReportCompleted();
                }
                catch (Exception ex)
                {
                    Diag.Failed("App/share-report", ex);
                }
            }
        }
```

Add `using Windows.ApplicationModel.DataTransfer;` and `using Windows.Storage.Streams;` to the `using` block at the top of `App.xaml.cs`.

- [ ] **Step 4: Make room for the notice on the chat list**

In `WhatsappApp/Pages/ChatsPage.xaml`, replace the outer grid's row definitions:

```xml
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
```

with:

```xml
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
```

then add, right after the closing `</Grid>` of the title bar (the element that holds `NewChatButton`), this notice:

```xml
        <!-- Un'immagine scelta col selettore o condivisa da un'altra app aspetta
             una chat. Senza questa riga l'app si aprirebbe su un elenco e
             sembrerebbe che non sia successo niente. -->
        <Border x:Name="PendingAttachmentBar" Grid.Row="1"
                Background="#FFFFF3CD" Padding="12,8" Visibility="Collapsed">
            <TextBlock x:Uid="ChatsPage_PendingImage"
                       Text="An image is ready: open a chat to send it"
                       Foreground="#FF7A5B00" FontSize="13" TextWrapping="Wrap"/>
        </Border>
```

and change the list container and the nav bar to make room:

```xml
        <Grid Grid.Row="1" Background="{StaticResource WhatsAppChatBgBrush}">
```

becomes

```xml
        <Grid Grid.Row="2" Background="{StaticResource WhatsAppChatBgBrush}">
```

```xml
        <controls:SectionNav x:Name="Nav" Grid.Row="2"/>
```

becomes

```xml
        <controls:SectionNav x:Name="Nav" Grid.Row="3"/>
```

- [ ] **Step 5: Show and hide the notice**

In `WhatsappApp/Pages/ChatsPage.xaml.cs`, in `OnNavigatedTo`, after `UpdateEmptyState();`, add:

```csharp
            // Un'allegato puo' arrivare mentre questa pagina e' davanti (l'app
            // torna qui dopo il selettore) oppure prima che esista (processo
            // avviato da una condivisione): si guarda in tutti e due i casi.
            AttachmentInbox.Ready += OnAttachmentReady;
            UpdatePendingAttachment();
```

add this override if the file does not already have one, otherwise add its body to the existing one:

```csharp
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            AttachmentInbox.Ready -= OnAttachmentReady;
        }
```

and add these two methods before the closing brace of the class:

```csharp
        private void OnAttachmentReady()
        {
            UpdatePendingAttachment();
        }

        /// <summary>La riga in cima dice che c'e' un'immagine da mandare.</summary>
        private void UpdatePendingAttachment()
        {
            PendingAttachmentBar.Visibility = AttachmentInbox.HasAttachment
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
```

- [ ] **Step 6: Add the string in both languages**

In `WhatsappApp/Strings/en-US/Resources.resw`, before the closing `</root>`, add:

```xml
  <data name="ChatsPage_PendingImage" xml:space="preserve">
    <value>An image is ready: open a chat to send it</value>
  </data>
```

In `WhatsappApp/Strings/it-IT/Resources.resw`, before the closing `</root>`, add:

```xml
  <data name="ChatsPage_PendingImage" xml:space="preserve">
    <value>Un'immagine e' pronta: apri una chat per inviarla</value>
  </data>
```

- [ ] **Step 7: Run the guards and build**

Run: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js`
Expected: `OK: 31 C# file(s)`, icons OK, `OK: 104 keys`, docs OK.

Then rerun the three VM commands from Step 2.
Expected: `COPIA=0`, `0 Error(s)`, `Your package has been successfully created`.

- [ ] **Step 8: Document sharing**

In `README.md`, in the `#### Chats and new chats` section, after the paragraph about the picker, add:

```
Sharing an image into the app works from any app that offers Share (Photos,
Gallery, a browser): the manifest declares a `windows.shareTarget` extension for
`Bitmap` and `StorageItems`, and `App.OnShareTargetActivated` puts the image in
the same waiting slot the picker uses. The app opens on the chat list, because the
next step is choosing who to send it to.
```

In `README.it.md`, in the same position, add:

```
Condividere un'immagine dentro l'app funziona da qualsiasi app che offra
Condividi (Foto, Galleria, un browser): il manifest dichiara un'estensione
`windows.shareTarget` per `Bitmap` e `StorageItems`, e `App.OnShareTargetActivated`
mette l'immagine nello stesso posto in attesa che usa il selettore. L'app si apre
sull'elenco chat, perche' il passo successivo e' scegliere a chi mandarla.
```

- [ ] **Step 9: Commit**

```bash
node tools/check-docs.js
git add WhatsappApp README.md README.it.md
git commit -m "feat: receive a shared image and add the app to the share sheet"
```

---

### Task 5: Write down what the phone taught us, and record the run

The two platform facts in this plan are the kind that get re-broken by a future edit: a picker call that looks right and is unsupported, and a GOWA endpoint whose name is not where you would look for it. Both belong in the skill, and the on-device checklist needs the four checks that only a phone can do.

**Files:**
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Modify: `.agents/skills/test-the-app/SKILL.md`
- Modify: `docs/superpowers/plans/2026-09-27-avatars-group-names-picker-and-share.md`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Add the two gotchas to the maintain skill**

In `.agents/skills/maintain-the-app/SKILL.md`, in the gotchas list, add these two entries in the existing style (a bold claim, then why, then what to do):

```markdown
- **On WP8.1 a file picker is not awaited, it is continued.** `FileOpenPicker.PickSingleFileAsync` is documented as unsupported on Windows Phone for both Windows Runtime and Silverlight; on the phone it throws and the button looks dead. The supported call is `PickSingleFileAndContinue()`, which deactivates the app and delivers the file to `App.OnActivated` as `ActivationKind.PickFileContinuation` with a `FileOpenPickerContinuationEventArgs`. Because the process can be terminated while the picker is open, the result never goes to a page instance: `AttachmentInbox` reads the bytes at reactivation and whichever page is showing an attachment takes them, through its `Ready` event and its `HasAttachment` check on navigation.
- **GOWA's `/user/avatar` returns an address, not an image.** `AvatarResponse` is `{url, id, type}`, so the picture is a second request to the CDN URL. Encoding the first response as if it were a bitmap produces base64 that no decoder accepts, and the failure is silent: every row falls back to initials. Same shape of trap in `/user/my/groups`: whatsmeow's `types.GroupInfo` has no json tags and embeds `GroupName`, so `encoding/json` promotes the fields and the subject arrives as a top-level `Name`.
```

- [ ] **Step 2: Add the on-device checks to the test skill**

In `.agents/skills/test-the-app/SKILL.md`, in the on-device checklist, add these items with the next numbers in the existing sequence:

```markdown
- Chat list: people show their profile picture, and the initials only appear when GOWA has no picture for them. If every row shows initials, the avatar is being fetched as JSON instead of downloaded from the address it points at.
- Chat list: a group row shows the group's subject, not `Group` followed by its number. If it shows the number, `/user/my/groups` did not answer and the row fell back to the chat list's name.
- Chat: the attach button opens the system picker. If nothing happens, check the diagnostics for `ChatPage/image` - the picker call is the one WP8.1 implements, and the file arrives only after the app is reactivated.
- Chat: after choosing an image in the picker the app comes back with the image in the preview bar, and Send sends it as an image. The app must come back by itself: it was deactivated, not closed.
- Share: in Photos, tapping Share lists WhatsApp; choosing it opens the app on the chat list with the image waiting, and opening a chat shows it in the preview bar.
```

- [ ] **Step 3: Record what execution changed**

Run the full fast gate one last time:

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/qr-term.js --self-test
cd WhatsappBridge && npm test
cd .. && node --test "tools/test/**/*.test.js"
```

Then append to this plan document a `## What execution changed about this plan` section that lists, honestly, every place the plan turned out to be wrong or incomplete while it was executed - the exact error, and the exact fix. At minimum it must cover: the manifest namespace that Task 4 Step 2 actually required, any C# API the WP8.1 compiler rejected in Task 3, and the final adapter test count. Do not write "no changes" unless the gate output in this session proves it.

- [ ] **Step 4: Commit and push**

```bash
git add .agents docs
git commit -m "docs: record the WP8.1 picker and GOWA shape traps"

git status --short
git push origin master
```

---

## What only the phone can prove

No WP8.1 emulator exists in this environment (XDE is x86 and needs Hyper-V, absent on the ARM64 Windows guest), and the adapter's GOWA server is running on the user's account, not here. So the following stay the user's verification, with the checklist item that covers each:

1. Profile pictures on the chat rows, with initials only as the fallback (Task 1).
2. Group rows named by their subject (Task 2).
3. The attach button opening the system picker, and the image surviving the app's deactivation (Task 3).
4. WhatsApp appearing in the share sheet from Photos, and the image waiting on the chat list (Task 4).

## Deliberate non-goals

- **Group avatars.** A group's photo needs `is_community=true` (GOWA retries with it off and a 5 second timeout), and it is another two requests per group. The group name is the actual complaint; initials on a group row are correct, not a fallback that looks broken.
- **Shared text and links.** Only images are read out of `ShareOperation.Data`. Sharing a URL opens the app on the chat list with nothing waiting, which is a no-op rather than a wrong action.
- **Notifications, `IsConnected`, unread counts.** Untouched by this plan.
