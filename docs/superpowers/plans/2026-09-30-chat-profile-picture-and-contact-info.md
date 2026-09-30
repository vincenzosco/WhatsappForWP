# Chat profile picture and contact info Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** in an open chat, the profile picture is tappable (it opens full screen) and tapping the name opens a WhatsApp-like contact-info view with the photo, the number, the "about" text, the business profile and, for a group, the description and the members.

**Architecture:** The chat header has no picture at all today: it is a back button and two lines of text. The header gains a round picture on the left of the name, drawn from the `Contact` the page already receives (`Avatar` / `Initials` / `HasAvatar`, decoded at 52 px for the row; the full-screen view decodes it again at `ViewerDecodePixels` and closes on tap, exactly like an image bubble). The name area becomes a tappable `Grid` that navigates to a new `ContactInfoPage`. The information itself does not exist on the phone and GOWA splits it over three routes (`/user/info`, `/user/business-profile`, `/group/participants`), so the adapter gets one new control command, `contact.info`: it fans out, composes a single JSON object and answers with one frame, so the page waits for one thing. The picture is carried inside that JSON (`AvatarData`, from the adapter's existing avatar cache) so the info page shows the big photo even for a chat whose row never had it.

**Tech Stack:** Windows Phone 8.1 WinRT/XAML, C# 5; Node.js 18+ (CommonJS, `node:test`) for the adapter; Node guard scripts in `tools/`.

## Global Constraints

- **C# 5 only** (`node tools/check-csharp5.js`): no `$"..."`, no `nameof`, no expression-bodied members, no `?.` on the left of an assignment.
- Colours, fonts: the WP8.1 theme only. No `Segoe MDL2 Assets`; an icon is an inline `Path` with an `<!-- IconX -->` comment.
- User-visible strings live in **both** `WhatsappApp/Strings/en-US/Resources.resw` and `WhatsappApp/Strings/it-IT/Resources.resw` (125 keys today, `--strict` fails on an unused one). A literal XAML `Text`/`Content`/`PlaceholderText`/`Header` needs `x:Uid` plus a `<name>.<attr>` key; a `Loc.Get("Key", ...)` key needs the plain key.
- Source comments are **Italian, no accented characters**; user strings carry accents normally.
- **Fast gate after every task:**
  `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"`, then `cd WhatsappBridge && npm test`.
- **Build gate after every task that touches the app**, on the Parallels VM `Windows 11` (retry the same MSBuild call once on exit 255 + `PrlJob_GetRetCode: Invalid argument`):
  1. `prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"`
  2. `prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"` → `COPIA=0`
  3. `prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m /p:WarningLevel=4"` → `Avvisi: 0`, `Errori: 0`, `Your package has been successfully created.`
- Every file is **LF, no BOM**. After any edit run:
  `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' <file>`
- **Every change under `WhatsappBridge/` also lands in the Docker repository** at `/tmp/docker-whatsappforwp`: `cd /tmp/docker-whatsappforwp && node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP`, then `node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP`, then `cd server && npm test`, then commit and push to `main`. `server/SOURCE_COMMIT` records the app commit that was mirrored.
- Commit messages contain **no apostrophe**.
- Plans and docs carry **no emoji**, except U+26A0.
- A `Border` takes exactly one child (WMC0035): wrap two in a `<Grid>`.
- A `<Button>` named `X` is wired only to `X_Click`; a button that declares `Width` also declares `MinWidth="0" MinHeight="0"` (WP8.1 theme minimums are 109 x 57.5).
- **Current expected counts** (the gate prints them; update them as the tasks land): 38 C# files, 125 keys per `.resw`, 23 inline `Path` (14 distinct icons), 20 buttons + 1 Button style, 58 tests in `tools/test`, 144 adapter tests.

---

## File Structure

| File | Create/Modify | Responsibility |
| --- | --- | --- |
| `WhatsappBridge/gowa-client.js` | Modify | Four read-only methods: `userInfo`, `businessProfile`, `groupParticipants`, `groupInfo`. |
| `WhatsappBridge/test/gowa-client.test.js` | Modify | The URL, the parsing and the "no answer means null" of each of them. |
| `WhatsappBridge/server.js` | Modify | The `contact.info` command: fan out, compose one JSON object, always answer. |
| `WhatsappBridge/test/server.test.js` | Modify | `contact.info` answers one frame with the composed JSON, for a person and for a group. |
| `WhatsappBridge/README.md` / `README.it.md` | Modify | The protocol table gains `contact.info`. |
| `WhatsappApp/Models/ContactInfo.cs` | **Create** | The `[DataContract]` shape the adapter sends, and `FromJson`. |
| `WhatsappApp/WhatsappApp.csproj` | Modify | Register the model and the page. |
| `WhatsappApp/Pages/ContactInfoPage.xaml` | **Create** | The view: big photo, name, number, about, business, group members, full-screen photo. |
| `WhatsappApp/Pages/ContactInfoPage.xaml.cs` | **Create** | Ask for the info, parse the frame, fill the view, open the photo full screen. |
| `WhatsappApp/Pages/ChatPage.xaml` | Modify | The header gains the round picture; the name area becomes tappable. |
| `WhatsappApp/Pages/ChatPage.xaml.cs` | Modify | Open the photo full screen; navigate to the info page. |
| `WhatsappApp/Strings/{en-US,it-IT}/Resources.resw` | Modify | The new strings, in both files. |
| `README.md` / `README.it.md` | Modify | The feature list says a picture and a name are tappable. |
| `.agents/skills/maintain-the-app/SKILL.md` | Modify | The gotcha: the info fan-out lives in the adapter. |
| `.agents/skills/test-the-app/SKILL.md` | Modify | The new counts and the on-device checks. |

---

### Task 1: The adapter knows a profile

**Files:**
- Modify: `WhatsappBridge/gowa-client.js`
- Test: `WhatsappBridge/test/gowa-client.test.js`

**Interfaces:**
- Consumes: the private `request(method, path)` of `GowaClient`; the module-level `groupName` helper.
- Produces:
  - `userInfo(jid)` → `{ name, verifiedName, status, pictureId }` or `null`
  - `businessProfile(jid)` → `{ email, address, categories: string[], timezone, hours: object[] }` or `null`
  - `groupParticipants(jid)` → `{ name, participants: [ { jid, phoneNumber, lid, displayName, isAdmin, isSuperAdmin } ] }` or `null`
  - `groupInfo(jid)` → `{ name, topic }` or `null`
  - module-level `normalizeJid(jid)` (device suffix stripped, `@` kept)

- [ ] **Step 1: Write the failing tests**

Append to `WhatsappBridge/test/gowa-client.test.js` (the helpers `jsonResponse` and `makeFetch` already exist in that file):

```javascript
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: FAIL with `client.userInfo is not a function` (and the same for the other three).

- [ ] **Step 3: Write the minimal implementation**

In `WhatsappBridge/gowa-client.js`, after the `groupName` function (module level), add:

```javascript
// Un JID senza il suffisso del dispositivo (utente:12@server -> utente@server).
// Il suffisso non e' un JID che WhatsApp riconosce in una richiesta di profilo.
function normalizeJid(jid) {
  const value = String(jid || '');
  const at = value.indexOf('@');
  if (at < 0) return value;
  return value.slice(0, at).split(':')[0] + value.slice(at);
}
```

Then, inside the class, after `avatar(jid)` and before `downloadMedia`, add:

```javascript
  // Nome, testo "about" (status) e id dell'immagine di una persona. GOWA
  // risponde con un array di un elemento: quello e' il profilo.
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

  // Il profilo aziendale: c'e' solo per un numero business, e per tutti gli
  // altri GOWA risponde con un errore. Per l'app e' "non c'e'", non un guasto.
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

  // I membri di un gruppo, con il ruolo. Senza membri non e' un gruppo: null.
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

  // La descrizione di un gruppo: GOWA la restituisce dentro un oggetto opaco,
  // quindi si leggono i nomi che whatsmeow usa per il testo e, se non ce n'e'
  // nessuno, si risponde vuoto invece di inventare un campo.
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: PASS, no failures.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/gowa-client.js WhatsappBridge/test/gowa-client.test.js
git commit -m "$(cat <<'EOF'
Read the profile information the adapter did not have

The info a chat shows lives on three GOWA routes, and none of them was
reachable from the bridge: a profile, a business profile, and the members of
a group. Each method answers null when GOWA has nothing, so the caller can
say "not available" without a failure path of its own.
EOF
)"
```

---

### Task 2: One frame carries the whole profile

**Files:**
- Modify: `WhatsappBridge/server.js` (the helpers near `syncContacts`, the `handleControl` switch)
- Test: `WhatsappBridge/test/server.test.js`
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md` (the protocol table)

**Interfaces:**
- Consumes: `GowaClient#userInfo`, `#businessProfile`, `#groupParticipants`, `#groupInfo`, `#avatar` from Task 1; the existing `sendControl(fields)` and `logger`.
- Produces: the `contact.info` control command. Request: `Command = "contact.info"`, the JID in `Text`. Reply: one frame with `Command = "contact.info"`, `ChatId = <jid>`, `Text = <JSON>` where the JSON is
  `{ "Name": string, "About": string, "Number": string, "AvatarData": string, "Business": { "Email": string, "Address": string, "Categories": string[], "Timezone": string, "Hours": [ { "Day": string, "Mode": string, "Open": string, "Close": string } ] } | null, "Group": { "Description": string, "Members": [ { "Jid": string, "Number": string, "Name": string, "IsAdmin": bool, "IsSuperAdmin": bool } ] } | null }`.
  The reply is always sent, even when WhatsApp is not connected.

- [ ] **Step 1: Write the failing tests**

Append to `WhatsappBridge/test/server.test.js`:

```javascript
test('contact.info composes the profile of a person into one frame', async () => {
  const sent = [];
  const gowa = {
    avatar: async (jid) => {
      assert.strictEqual(jid, 'a@s.whatsapp.net');
      return 'AAAA';
    },
    userInfo: async () => ({ name: 'Anna', verifiedName: '', status: 'in giro', pictureId: 'P1' }),
    businessProfile: async () => ({
      email: 'info@bar.it', address: 'Via Roma 1', categories: ['Bar'], timezone: 'Europe/Rome', hours: []
    })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'contact.info', Text: 'a@s.whatsapp.net' });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'contact.info');
  assert.strictEqual(sent[0].ChatId, 'a@s.whatsapp.net');
  assert.strictEqual(sent[0].Type, 3);
  const info = JSON.parse(sent[0].Text);
  assert.strictEqual(info.Name, 'Anna');
  assert.strictEqual(info.About, 'in giro');
  assert.strictEqual(info.Number, '+a');
  assert.strictEqual(info.AvatarData, 'AAAA');
  assert.strictEqual(info.Business.Email, 'info@bar.it');
  assert.strictEqual(info.Group, null);
});

test('contact.info reads the members and the description of a group', async () => {
  const sent = [];
  const gowa = {
    avatar: async () => null,
    groupParticipants: async () => ({
      name: 'Famiglia',
      participants: [
        { jid: '1@s.whatsapp.net', phoneNumber: '', displayName: 'Anna', isAdmin: true, isSuperAdmin: false },
        { jid: '2@s.whatsapp.net', phoneNumber: '', displayName: '', isAdmin: false, isSuperAdmin: false }
      ]
    }),
    groupInfo: async () => ({ name: 'Famiglia', topic: 'solo foto' })
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'contact.info', Text: '123@g.us' });

  const info = JSON.parse(sent[0].Text);
  assert.strictEqual(info.Name, 'Famiglia');
  assert.strictEqual(info.Group.Description, 'solo foto');
  assert.strictEqual(info.Group.Members.length, 2);
  assert.strictEqual(info.Group.Members[0].IsAdmin, true);
  assert.strictEqual(info.Group.Members[1].Name, '+2');
});

test('contact.info answers an empty profile instead of staying silent', async () => {
  const sent = [];
  const gowa = {
    avatar: async () => { throw new Error('senza rete'); },
    userInfo: async () => null,
    businessProfile: async () => null
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({ Type: 3, Command: 'contact.info', Text: 'a@s.whatsapp.net' });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'contact.info');
  assert.strictEqual(sent[0].ChatId, 'a@s.whatsapp.net');
  assert.deepStrictEqual(JSON.parse(sent[0].Text), {
    Name: '', About: '', Number: '', AvatarData: '', Business: null, Group: null
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL, the three tests receive no frame (the command is unknown).

- [ ] **Step 3: Write the minimal implementation**

In `WhatsappBridge/server.js`, right after `syncContacts`, add the helpers and the handler:

```javascript
  /// Il numero leggibile di un JID (es. +393401234567). Vuoto per un gruppo.
  function numberForJid(jid) {
    const user = String(jid || '').split('@')[0].split(':')[0];
    return /^\d+$/.test(user) ? '+' + user : '';
  }

  /// Il profilo aziendale nella forma che l'app si aspetta, o null.
  function businessFrom(profile) {
    if (!profile) return null;
    return {
      Email: profile.email || '',
      Address: profile.address || '',
      Categories: profile.categories || [],
      Timezone: profile.timezone || '',
      Hours: (profile.hours || []).map(function (h) {
        return {
          Day: h.day_of_week === undefined || h.day_of_week === null ? '' : String(h.day_of_week),
          Mode: h.mode || '',
          Open: h.open_time || '',
          Close: h.close_time || ''
        };
      })
    };
  }

  /// Un membro di un gruppo come lo mostra l'app: un nome c'e' sempre, anche
  /// quando WhatsApp ne manda uno solo per un numero.
  function groupMember(p) {
    return {
      Jid: p.jid || '',
      Number: p.phoneNumber || numberForJid(p.jid),
      Name: p.displayName || numberForJid(p.jid) || p.jid || '',
      IsAdmin: p.isAdmin === true,
      IsSuperAdmin: p.isSuperAdmin === true
    };
  }

  /**
   * Le informazioni di un profilo, in un solo frame di controllo.
   *
   * Tre richieste a monte (il profilo e, se e' un business, il profilo
   * aziendale; per un gruppo i membri e la descrizione) e una sola risposta:
   * l'app chiede una cosa e aspetta una cosa. L'immagine la porta `avatar`, che
   * ha gia' la sua cache, cosi' la pagina grande la ha anche per una chat il cui
   * elenco non l'aveva.
   *
   * Qualunque guasto diventa un profilo vuoto: la pagina deve smettere di
   * aspettare, non restare in caricamento per sempre.
   */
  async function sendContactInfo(jid) {
    if (!jid) return;

    const info = {
      Name: '', About: '', Number: '', AvatarData: '', Business: null, Group: null
    };

    try {
      info.AvatarData = (await gowa.avatar(jid)) || '';

      if (jid.endsWith('@g.us')) {
        const participants = await gowa.groupParticipants(jid);
        const description = await gowa.groupInfo(jid);
        info.Group = {
          Description: (description && description.topic) || '',
          Members: participants && participants.participants
            ? participants.participants.map(groupMember)
            : []
        };
        if (participants && participants.name) info.Name = participants.name;
      } else {
        const user = await gowa.userInfo(jid);
        if (user) {
          info.Name = user.name || user.verifiedName || '';
          info.About = user.status || '';
        }
        info.Number = numberForJid(jid);
        info.Business = businessFrom(await gowa.businessProfile(jid));
      }
    } catch (err) {
      logger('WARN', `contact info failed for ${jid}: ${err.message}`);
    }

    sendControl({ command: 'contact.info', chatId: jid, text: JSON.stringify(info) });
  }
```

In the `handleControl` switch, after `case 'messages':`, add:

```javascript
      case 'contact.info':
        // Il JID viaggia in `Text`, come per `messages`: e' il campo che il
        // protocollo di controllo usa per il dato di accompagnamento.
        await sendContactInfo((msg.Text || '').trim());
        break;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: PASS, no failures.

- [ ] **Step 5: Document the command in both adapter READMEs**

In `WhatsappBridge/README.md`, in the app-to-adapter protocol table, after the `messages` row, add a row:

```
| `contact.info` | `Text`: the JID of a chat. | One `contact.info` frame carrying the profile as a JSON `Text` (name, about, number, avatar, business profile, group members). |
```

In `WhatsappBridge/README.it.md`, after the `messages` row, add the same row in Italian:

```
| `contact.info` | `Text`: il JID di una chat. | Un frame `contact.info` con il profilo in `Text` come JSON (nome, about, numero, avatar, profilo aziendale, membri del gruppo). |
```

- [ ] **Step 6: Mirror the adapter into the Docker repository**

```bash
cd /tmp/docker-whatsappforwp
node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP
node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP
cd server && npm test
```

Expected: `OK: server/ matches the adapter (25 file(s))` (24 in Task 1's world is now 25 because `server.js` and both READMEs are not counted twice; whatever the tool prints) and the adapter suite passes with 0 failures.

- [ ] **Step 7: Commit**

```bash
git add WhatsappBridge/server.js WhatsappBridge/test/server.test.js WhatsappBridge/README.md WhatsappBridge/README.it.md
git commit -m "$(cat <<'EOF'
Answer the whole profile in a single frame

The info a chat needs comes from up to three GOWA routes, and the phone has
none of them. The adapter fans out and composes one JSON object, so the page
asks once and waits once; a failure becomes an empty profile, because a page
that waits forever is worse than a page that says nothing is available.
EOF
)"
cd /tmp/docker-whatsappforwp && git add -A && git commit -m "$(cat <<'EOF'
Mirror the contact.info command from the adapter
EOF
)" && git push origin main 2>&1 | tail -2
```

---

### Task 3: The app has a shape for the profile

**Files:**
- Create: `WhatsappApp/Models/ContactInfo.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (the `<Compile Include="Models\...">` list)

**Interfaces:**
- Consumes: `WhatsappApp/Services/Diag.cs` - `Diag.Failed(string, Exception)`; `WhatsappApp/Services/Loc.cs` - `Loc.Get(string, string)`.
- Produces:
  - `ContactInfo.FromJson(string json)` → `ContactInfo` (null if unparsable)
  - `ContactInfo.Name`, `.About`, `.Number`, `.AvatarData`, `.Business` (`ContactBusiness`), `.Group` (`ContactGroup`)
  - `ContactBusiness.Email`, `.Address`, `.Categories` (`List<string>`), `.Timezone`, `.Hours` (`List<ContactHours>`)
  - `ContactHours.Day`, `.Mode`, `.Open`, `.Close`, `.Display` (`string`)
  - `ContactGroup.Description`, `.Members` (`List<ContactMember>`)
  - `ContactMember.Jid`, `.Number`, `.Name`, `.IsAdmin`, `.IsSuperAdmin`, `.Display` (`string`), `.Role` (`string`), `.IsRoleVisible` (`bool`)

- [ ] **Step 1: Create the model**

Create `WhatsappApp/Models/ContactInfo.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using WhatsappApp.Services;

namespace WhatsappApp.Models
{
    /// <summary>
    /// Le informazioni di un profilo come le compone l'adapter (comando
    /// `contact.info`). I nomi dei campi sono quelli del filo: DataContractJson
    /// Serializer e' case-sensitive, e i due capi devono restare d'accordo.
    /// </summary>
    [DataContract]
    public class ContactInfo
    {
        [DataMember]
        public string Name { get; set; }

        /// <summary>Il testo "about" (lo status di WhatsApp).</summary>
        [DataMember]
        public string About { get; set; }

        /// <summary>Il numero leggibile, per una persona. Vuoto per un gruppo.</summary>
        [DataMember]
        public string Number { get; set; }

        /// <summary>L'immagine del profilo in base64, se l'adapter ce l'ha.</summary>
        [DataMember]
        public string AvatarData { get; set; }

        /// <summary>Il profilo aziendale. Null quando non e' un account business.</summary>
        [DataMember]
        public ContactBusiness Business { get; set; }

        /// <summary>Descrizione e membri. Null quando non e' un gruppo.</summary>
        [DataMember]
        public ContactGroup Group { get; set; }

        private static readonly DataContractJsonSerializer JsonSerializer =
            new DataContractJsonSerializer(typeof(ContactInfo));

        /// <summary>Un frame illeggibile non e' un guasto: e' "niente da mostrare".</summary>
        public static ContactInfo FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return (ContactInfo)JsonSerializer.ReadObject(ms);
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("ContactInfo.FromJson", ex);
                return null;
            }
        }
    }

    [DataContract]
    public class ContactBusiness
    {
        [DataMember]
        public string Email { get; set; }

        [DataMember]
        public string Address { get; set; }

        [DataMember]
        public List<string> Categories { get; set; }

        [DataMember]
        public string Timezone { get; set; }

        [DataMember]
        public List<ContactHours> Hours { get; set; }
    }

    [DataContract]
    public class ContactHours
    {
        [DataMember]
        public string Day { get; set; }

        [DataMember]
        public string Mode { get; set; }

        [DataMember]
        public string Open { get; set; }

        [DataMember]
        public string Close { get; set; }

        /// <summary>Una riga sola da mostrare: il giorno e la fascia, o solo il giorno.</summary>
        public string Display
        {
            get
            {
                if (string.IsNullOrEmpty(Open)) return Day ?? "";
                return (Day ?? "") + "  " + Open + " - " + Close;
            }
        }
    }

    [DataContract]
    public class ContactGroup
    {
        [DataMember]
        public string Description { get; set; }

        [DataMember]
        public List<ContactMember> Members { get; set; }
    }

    [DataContract]
    public class ContactMember
    {
        [DataMember]
        public string Jid { get; set; }

        [DataMember]
        public string Number { get; set; }

        [DataMember]
        public string Name { get; set; }

        [DataMember]
        public bool IsAdmin { get; set; }

        [DataMember]
        public bool IsSuperAdmin { get; set; }

        /// <summary>Il nome se c'e', altrimenti il numero: una riga vuota non serve a nessuno.</summary>
        public string Display
        {
            get
            {
                if (!string.IsNullOrEmpty(Name)) return Name;
                return Number ?? "";
            }
        }

        /// <summary>Il ruolo, vuoto per un membro normale.</summary>
        public string Role
        {
            get
            {
                if (IsSuperAdmin) return Loc.Get("ContactInfoPage_SuperAdmin", "Super admin");
                if (IsAdmin) return Loc.Get("ContactInfoPage_Admin", "Admin");
                return "";
            }
        }

        public bool IsRoleVisible
        {
            get { return IsAdmin || IsSuperAdmin; }
        }
    }
}
```

- [ ] **Step 2: Register the file in the csproj**

In `WhatsappApp/WhatsappApp.csproj`, in the `<Compile Include="Models\...">` group, after `<Compile Include="Models\Contact.cs" />`, add:

```xml
    <Compile Include="Models\ContactInfo.cs" />
```

- [ ] **Step 3: Run the fast gate**

Run: `node tools/check-csharp5.js && node tools/check-memory.js && node tools/check-actions.js`
Expected: all `OK` lines, no problems. (The new file has no `Loc.Get` key yet in the resw: `check-resw.js --strict` will fail until Task 4 adds the two keys. That is expected; run the full gate after Task 4.)

- [ ] **Step 4: Normalize the files and commit**

```bash
perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' WhatsappApp/Models/ContactInfo.cs WhatsappApp/WhatsappApp.csproj
git add WhatsappApp/Models/ContactInfo.cs WhatsappApp/WhatsappApp.csproj
git commit -m "$(cat <<'EOF'
Give the phone a shape for the profile information

The adapter now sends one JSON object; the app needed a contract to read it.
Every field is optional because the adapter composes the same object for a
person and for a group, and the absent half is exactly the signal.
EOF
)"
```

---

### Task 4: The contact-info page

**Files:**
- Create: `WhatsappApp/Pages/ContactInfoPage.xaml`
- Create: `WhatsappApp/Pages/ContactInfoPage.xaml.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (the `<Page>` and `<Compile>` groups)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes: `ContactInfo` (Task 3); `Contact` (`Avatar`, `Initials`, `HasAvatar`, `AvatarData`, `Id`); `CommunicationService.Instance.SendControlAsync(string, string)`, `.ControlMessageReceived`, `.IsConnected`; `ImageHelper.FromBase64Async(string, int)`; `AvatarCache.Remember(string, string)`; `Loc.Get`; `Diag.Failed`.
- Produces: `ContactInfoPage` (a `Page` navigated to with a `Contact` as parameter).

- [ ] **Step 1: Add the strings to both resource files**

In `WhatsappApp/Strings/en-US/Resources.resw`, before `</root>`, add:

```xml
  <data name="ContactInfoPage_Title" xml:space="preserve">
    <value>Contact info</value>
  </data>
  <data name="ContactInfoPage_BackTooltip" xml:space="preserve">
    <value>Back</value>
  </data>
  <data name="ContactInfoPage_Number" xml:space="preserve">
    <value>Phone number</value>
  </data>
  <data name="ContactInfoPage_About" xml:space="preserve">
    <value>About</value>
  </data>
  <data name="ContactInfoPage_Business" xml:space="preserve">
    <value>Business</value>
  </data>
  <data name="ContactInfoPage_Email" xml:space="preserve">
    <value>Email</value>
  </data>
  <data name="ContactInfoPage_Address" xml:space="preserve">
    <value>Address</value>
  </data>
  <data name="ContactInfoPage_Categories" xml:space="preserve">
    <value>Categories</value>
  </data>
  <data name="ContactInfoPage_Hours" xml:space="preserve">
    <value>Hours</value>
  </data>
  <data name="ContactInfoPage_GroupDescription" xml:space="preserve">
    <value>Group description</value>
  </data>
  <data name="ContactInfoPage_Members" xml:space="preserve">
    <value>Members</value>
  </data>
  <data name="ContactInfoPage_MembersCount" xml:space="preserve">
    <value>Members ({0})</value>
  </data>
  <data name="ContactInfoPage_Admin" xml:space="preserve">
    <value>Admin</value>
  </data>
  <data name="ContactInfoPage_SuperAdmin" xml:space="preserve">
    <value>Super admin</value>
  </data>
  <data name="ContactInfoPage_NoInfo" xml:space="preserve">
    <value>No information from the server.</value>
  </data>
```

In `WhatsappApp/Strings/it-IT/Resources.resw`, before `</root>`, add the same keys with:

```xml
  <data name="ContactInfoPage_Title" xml:space="preserve">
    <value>Info contatto</value>
  </data>
  <data name="ContactInfoPage_BackTooltip" xml:space="preserve">
    <value>Indietro</value>
  </data>
  <data name="ContactInfoPage_Number" xml:space="preserve">
    <value>Numero di telefono</value>
  </data>
  <data name="ContactInfoPage_About" xml:space="preserve">
    <value>Info</value>
  </data>
  <data name="ContactInfoPage_Business" xml:space="preserve">
    <value>Attivita</value>
  </data>
  <data name="ContactInfoPage_Email" xml:space="preserve">
    <value>Email</value>
  </data>
  <data name="ContactInfoPage_Address" xml:space="preserve">
    <value>Indirizzo</value>
  </data>
  <data name="ContactInfoPage_Categories" xml:space="preserve">
    <value>Categorie</value>
  </data>
  <data name="ContactInfoPage_Hours" xml:space="preserve">
    <value>Orari</value>
  </data>
  <data name="ContactInfoPage_GroupDescription" xml:space="preserve">
    <value>Descrizione del gruppo</value>
  </data>
  <data name="ContactInfoPage_Members" xml:space="preserve">
    <value>Membri</value>
  </data>
  <data name="ContactInfoPage_MembersCount" xml:space="preserve">
    <value>Membri ({0})</value>
  </data>
  <data name="ContactInfoPage_Admin" xml:space="preserve">
    <value>Amministratore</value>
  </data>
  <data name="ContactInfoPage_SuperAdmin" xml:space="preserve">
    <value>Amministratore principale</value>
  </data>
  <data name="ContactInfoPage_NoInfo" xml:space="preserve">
    <value>Nessuna informazione dal server.</value>
  </data>
```

- [ ] **Step 2: Create the page markup**

Create `WhatsappApp/Pages/ContactInfoPage.xaml`:

```xml
<Page
    x:Class="WhatsappApp.Pages.ContactInfoPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:conv="using:WhatsappApp.Converters"
    Background="White">

    <Page.Resources>
        <conv:BoolToVisibilityConverter x:Key="BoolToVisibility"/>
        <conv:InitialToColorConverter x:Key="InitialToColor"/>
        <SolidColorBrush x:Key="WhatsAppHeaderBrush" Color="#FF075E54"/>
    </Page.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Header: back + title. Come nelle altre pagine, la barra e' disegnata
             qui e il titolo lo scrive il gestore con Loc.Get. -->
        <Grid Grid.Row="0" Background="{StaticResource WhatsAppHeaderBrush}" Height="56">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <Button x:Name="BackButton" Grid.Column="0"
                    Background="Transparent"
                    MinWidth="0" MinHeight="0"
                    Width="48" Height="48" Margin="0,4,0,0" Padding="0"
                    Click="BackButton_Click"
                    BorderThickness="0">
                <Path Stroke="White" StrokeThickness="2.4"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24">
                    <!-- IconBack -->
                    <Path.Data>
                        <PathGeometry>
                            <PathGeometry.Figures>
                                <PathFigure StartPoint="15.5,4">
                                    <PathFigure.Segments>
                                        <PolyLineSegment Points="7.5,12 15.5,20"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                            </PathGeometry.Figures>
                        </PathGeometry>
                    </Path.Data>
                </Path>
            </Button>

            <TextBlock x:Name="TitleText" Grid.Column="1"
                       VerticalAlignment="Center" Margin="8,0,0,4"
                       Foreground="White" FontSize="18" FontWeight="SemiBold"
                       TextTrimming="WordEllipsis"/>
        </Grid>

        <ScrollViewer Grid.Row="1">
            <StackPanel Margin="0,0,0,24">

                <!-- La foto grande. E' un Border e non un'Image per lo stesso
                     motivo della riga dell'elenco: WP8.1 ritaglia un cerchio
                     solo con il CornerRadius di uno sfondo. -->
                <Border x:Name="BigAvatar" Width="160" Height="160" CornerRadius="80"
                        Margin="0,20,0,12" HorizontalAlignment="Center"
                        Tapped="BigAvatar_Tapped">
                    <Grid>
                        <Ellipse Width="160" Height="160"
                                 Fill="{Binding Initials, Converter={StaticResource InitialToColor}}"/>
                        <Border Width="160" Height="160" CornerRadius="80"
                                Visibility="{Binding HasAvatar, Converter={StaticResource BoolToVisibility}}">
                            <Border.Background>
                                <ImageBrush ImageSource="{Binding Avatar}" Stretch="UniformToFill"/>
                            </Border.Background>
                        </Border>
                        <TextBlock Text="{Binding Initials}"
                                   Foreground="White" FontSize="56" FontWeight="SemiBold"
                                   VerticalAlignment="Center" HorizontalAlignment="Center"
                                   Visibility="{Binding HasAvatar, Converter={StaticResource BoolToVisibility}, ConverterParameter=Invert}"/>
                    </Grid>
                </Border>

                <TextBlock x:Name="NameText" FontSize="24" FontWeight="SemiBold"
                           Foreground="#DE000000" HorizontalAlignment="Center"
                           TextTrimming="WordEllipsis" Margin="16,0,16,0"/>

                <TextBlock x:Name="NumberLabel" FontSize="13" Foreground="#8A000000"
                           HorizontalAlignment="Center" Margin="16,8,16,0"
                           Visibility="Collapsed"/>
                <TextBlock x:Name="NumberText" FontSize="18" Foreground="#DE000000"
                           HorizontalAlignment="Center" Margin="16,0,16,0"
                           Visibility="Collapsed"/>

                <TextBlock x:Name="AboutLabel" FontSize="13" Foreground="#8A000000"
                           Margin="16,20,16,0" Visibility="Collapsed"/>
                <TextBlock x:Name="AboutText" FontSize="17" Foreground="#DE000000"
                           TextWrapping="Wrap" Margin="16,2,16,0" Visibility="Collapsed"/>

                <StackPanel x:Name="BusinessPanel" Visibility="Collapsed" Margin="16,24,16,0">
                    <TextBlock x:Name="BusinessLabel" FontSize="18" FontWeight="SemiBold"
                               Foreground="#DE000000"/>
                    <TextBlock x:Name="BusinessEmailLabel" FontSize="13" Foreground="#8A000000"
                               Margin="0,10,0,0" Visibility="Collapsed"/>
                    <TextBlock x:Name="BusinessEmailText" FontSize="16" Foreground="#DE000000"
                               TextWrapping="Wrap" Visibility="Collapsed"/>
                    <TextBlock x:Name="BusinessAddressLabel" FontSize="13" Foreground="#8A000000"
                               Margin="0,10,0,0" Visibility="Collapsed"/>
                    <TextBlock x:Name="BusinessAddressText" FontSize="16" Foreground="#DE000000"
                               TextWrapping="Wrap" Visibility="Collapsed"/>
                    <TextBlock x:Name="BusinessCategoriesLabel" FontSize="13" Foreground="#8A000000"
                               Margin="0,10,0,0" Visibility="Collapsed"/>
                    <TextBlock x:Name="BusinessCategoriesText" FontSize="16" Foreground="#DE000000"
                               TextWrapping="Wrap" Visibility="Collapsed"/>
                    <TextBlock x:Name="BusinessHoursLabel" FontSize="13" Foreground="#8A000000"
                               Margin="0,10,0,0" Visibility="Collapsed"/>
                    <ListView x:Name="HoursList" SelectionMode="None" IsHitTestVisible="False">
                        <ListView.ItemTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding Display}" Foreground="#DE000000"
                                           FontSize="16" TextWrapping="Wrap" Margin="0,2,0,2"/>
                            </DataTemplate>
                        </ListView.ItemTemplate>
                    </ListView>
                </StackPanel>

                <StackPanel x:Name="GroupPanel" Visibility="Collapsed" Margin="16,24,16,0">
                    <TextBlock x:Name="GroupDescriptionLabel" FontSize="18" FontWeight="SemiBold"
                               Foreground="#DE000000" Visibility="Collapsed"/>
                    <TextBlock x:Name="GroupDescriptionText" FontSize="16" Foreground="#DE000000"
                               TextWrapping="Wrap" Margin="0,4,0,0" Visibility="Collapsed"/>
                    <TextBlock x:Name="MembersLabel" FontSize="18" FontWeight="SemiBold"
                               Foreground="#DE000000" Margin="0,18,0,0"/>
                    <ListView x:Name="MembersList" SelectionMode="None" IsHitTestVisible="False">
                        <ListView.ItemTemplate>
                            <DataTemplate>
                                <Grid Margin="0,6,0,6">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Grid.Column="0" Text="{Binding Display}"
                                               Foreground="#DE000000" FontSize="16"
                                               TextTrimming="WordEllipsis"/>
                                    <TextBlock Grid.Column="1" Text="{Binding Role}"
                                               Foreground="#8A000000" FontSize="13"
                                               Margin="8,0,0,0"
                                               Visibility="{Binding IsRoleVisible, Converter={StaticResource BoolToVisibility}}"/>
                                </Grid>
                            </DataTemplate>
                        </ListView.ItemTemplate>
                    </ListView>
                </StackPanel>

                <TextBlock x:Name="EmptyText" FontSize="15" Foreground="#8A000000"
                           TextWrapping="Wrap" Margin="16,24,16,0" Visibility="Collapsed"/>
            </StackPanel>
        </ScrollViewer>

        <!-- La foto a tutto schermo: si chiude toccandola, come per una bolla. -->
        <Grid x:Name="ImageViewer" Grid.RowSpan="2"
              Background="#F0000000"
              Visibility="Collapsed"
              Tapped="ImageViewer_Tapped">
            <Image x:Name="ImageViewerImage" Stretch="Uniform"/>
        </Grid>
    </Grid>
</Page>
```

- [ ] **Step 3: Create the page code**

Create `WhatsappApp/Pages/ContactInfoPage.xaml.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// Le informazioni di una persona o di un gruppo: la foto grande, il nome,
    /// il numero, l'about, il profilo aziendale e, per un gruppo, la descrizione
    /// e i membri. La pagina chiede una cosa sola (`contact.info`) e aspetta una
    /// cosa sola; l'adapter ha gia' messo insieme il resto.
    /// </summary>
    public sealed partial class ContactInfoPage : Page
    {
        private Contact _contact;

        private readonly ObservableCollection<ContactMember> _members =
            new ObservableCollection<ContactMember>();
        private readonly ObservableCollection<ContactHours> _hours =
            new ObservableCollection<ContactHours>();

        public ContactInfoPage()
        {
            this.InitializeComponent();

            ToolTipService.SetToolTip(BackButton, Loc.Get("ContactInfoPage_BackTooltip", "Back"));

            TitleText.Text = Loc.Get("ContactInfoPage_Title", "Contact info");
            NumberLabel.Text = Loc.Get("ContactInfoPage_Number", "Phone number");
            AboutLabel.Text = Loc.Get("ContactInfoPage_About", "About");
            BusinessLabel.Text = Loc.Get("ContactInfoPage_Business", "Business");
            BusinessEmailLabel.Text = Loc.Get("ContactInfoPage_Email", "Email");
            BusinessAddressLabel.Text = Loc.Get("ContactInfoPage_Address", "Address");
            BusinessCategoriesLabel.Text = Loc.Get("ContactInfoPage_Categories", "Categories");
            BusinessHoursLabel.Text = Loc.Get("ContactInfoPage_Hours", "Hours");
            GroupDescriptionLabel.Text = Loc.Get("ContactInfoPage_GroupDescription", "Group description");
            EmptyText.Text = Loc.Get("ContactInfoPage_NoInfo", "No information from the server.");

            MembersList.ItemsSource = _members;
            HoursList.ItemsSource = _hours;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _contact = e.Parameter as Contact;
            if (_contact == null) return;

            // L'avatar e le iniziali vengono dal contatto: la pagina non li
            // ricostruisce.
            DataContext = _contact;
            NameText.Text = _contact.Name;

            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;
            RequestInfo();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            CommunicationService.Instance.ControlMessageReceived -= OnControlMessageReceived;
            HideFullScreen();
        }

        /// <summary>
        /// Chiede il profilo. Non c'e' niente da attendere: la risposta e' un
        /// frame di controllo, e arriva all'evento.
        /// </summary>
        private void RequestInfo()
        {
            if (!CommunicationService.Instance.IsConnected) return;
#pragma warning disable 4014
            CommunicationService.Instance.SendControlAsync("contact.info", _contact.Id);
#pragma warning restore 4014
        }

        private void OnControlMessageReceived(object sender, ChatMessage message)
        {
            if (message == null || message.Command != "contact.info") return;
            if (message.ChatId != _contact.Id) return;

            Apply(ContactInfo.FromJson(message.Text));
        }

        /// <summary>
        /// Riempie la pagina. `any` dice se c'e' qualcosa da mostrare: quando non
        /// c'e' niente, la pagina lo dice invece di restare mezza vuota.
        /// </summary>
        private void Apply(ContactInfo info)
        {
            bool any = false;

            if (info != null)
            {
                if (!string.IsNullOrEmpty(info.Name)) NameText.Text = info.Name;

                // L'immagine che arriva adesso vale anche per l'elenco chat: si
                // tiene, cosi' la prossima apertura la ha senza chiederla.
                if (!string.IsNullOrEmpty(info.AvatarData))
                {
                    if (_contact.AvatarData != info.AvatarData)
                    {
                        _contact.AvatarData = info.AvatarData;
#pragma warning disable 4014
                        _contact.LoadAvatarAsync();
#pragma warning restore 4014
                    }
                    AvatarCache.Remember(_contact.Id, info.AvatarData);
                }

                if (!string.IsNullOrEmpty(info.Number))
                {
                    NumberText.Text = info.Number;
                    NumberLabel.Visibility = Visibility.Visible;
                    NumberText.Visibility = Visibility.Visible;
                    any = true;
                }

                if (!string.IsNullOrEmpty(info.About))
                {
                    AboutText.Text = info.About;
                    AboutLabel.Visibility = Visibility.Visible;
                    AboutText.Visibility = Visibility.Visible;
                    any = true;
                }

                ApplyBusiness(info.Business, ref any);
                ApplyGroup(info.Group, ref any);
            }

            EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyBusiness(ContactBusiness business, ref bool any)
        {
            if (business == null)
            {
                BusinessPanel.Visibility = Visibility.Collapsed;
                return;
            }

            BusinessPanel.Visibility = Visibility.Visible;
            any = true;

            ShowIf(BusinessEmailLabel, BusinessEmailText, business.Email);
            ShowIf(BusinessAddressLabel, BusinessAddressText, business.Address);

            string categories = "";
            if (business.Categories != null && business.Categories.Count > 0)
                categories = string.Join(", ", business.Categories);
            ShowIf(BusinessCategoriesLabel, BusinessCategoriesText, categories);

            _hours.Clear();
            if (business.Hours != null)
            {
                for (int i = 0; i < business.Hours.Count; i++) _hours.Add(business.Hours[i]);
            }
            BusinessHoursLabel.Visibility = _hours.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Un'etichetta e il suo valore compaiono solo se il valore c'e': un
        /// titolo con sotto niente e' peggio di un titolo in meno.
        /// </summary>
        private static void ShowIf(TextBlock label, TextBlock value, string text)
        {
            bool has = !string.IsNullOrEmpty(text);
            label.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            value.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            value.Text = text ?? "";
        }

        private void ApplyGroup(ContactGroup group, ref bool any)
        {
            if (group == null)
            {
                GroupPanel.Visibility = Visibility.Collapsed;
                return;
            }

            GroupPanel.Visibility = Visibility.Visible;
            any = true;

            bool hasDescription = !string.IsNullOrEmpty(group.Description);
            GroupDescriptionLabel.Visibility = hasDescription ? Visibility.Visible : Visibility.Collapsed;
            GroupDescriptionText.Visibility = hasDescription ? Visibility.Visible : Visibility.Collapsed;
            GroupDescriptionText.Text = group.Description ?? "";

            _members.Clear();
            if (group.Members != null)
            {
                for (int i = 0; i < group.Members.Count; i++) _members.Add(group.Members[i]);
            }

            MembersLabel.Text = string.Format(
                Loc.Get("ContactInfoPage_MembersCount", "Members ({0})"), _members.Count);
        }

        /// <summary>
        /// La foto a tutto schermo si decodifica alla misura dello schermo: il
        /// cerchio grande e' 160 px e ingrandirlo lo lascerebbe sfocato.
        /// </summary>
        private const int ViewerDecodePixels = 720;

        private async void BigAvatar_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (_contact == null || string.IsNullOrEmpty(_contact.AvatarData)) return;

            try
            {
                var bitmap = await ImageHelper.FromBase64Async(_contact.AvatarData, ViewerDecodePixels);
                if (bitmap == null) return;
                ImageViewerImage.Source = bitmap;
                ImageViewer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Diag.Failed("ContactInfoPage.BigAvatar_Tapped", ex);
                HideFullScreen();
            }
        }

        private void ImageViewer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            HideFullScreen();
            e.Handled = true;
        }

        private void HideFullScreen()
        {
            ImageViewer.Visibility = Visibility.Collapsed;
            ImageViewerImage.Source = null;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
```

- [ ] **Step 4: Register the page in the csproj**

In `WhatsappApp/WhatsappApp.csproj`, in the `<Compile Include="Pages\...">` group (after `Pages\CallsPage.xaml.cs`), add:

```xml
    <Compile Include="Pages\ContactInfoPage.xaml.cs">
      <DependentUpon>ContactInfoPage.xaml</DependentUpon>
    </Compile>
```

and in the `<Page Include="Pages\...">` group (after `Pages\ConnectionPage.xaml`), add:

```xml
    <Page Include="Pages\ContactInfoPage.xaml">
      <Generator>MSBuild:Compile</Generator>
      <SubType>Designer</SubType>
    </Page>
```

- [ ] **Step 5: Normalize and run the fast gate and the build gate**

```bash
perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' WhatsappApp/Pages/ContactInfoPage.xaml WhatsappApp/Pages/ContactInfoPage.xaml.cs WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw WhatsappApp/WhatsappApp.csproj
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"
```

Expected: `OK: 140 key(s) ...` (125 + 15), `OK: 21 button(s) and 1 Button style(s)`, 39 C# files, and the `tools/test` suite green.

Then run the three VM build commands from Global Constraints. Expected `COPIA=0`, `Avvisi: 0`, `Errori: 0`, `Your package has been successfully created.`

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Pages/ContactInfoPage.xaml WhatsappApp/Pages/ContactInfoPage.xaml.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
git commit -m "$(cat <<'EOF'
Show all the profile a chat can show

WhatsApp puts the photo, the number, the about text, the business details and
the members of a group on one page; the phone had none of it. The page asks
the adapter once and draws whatever came back, and says so when nothing did.
EOF
)"
```

---

### Task 5: The header picture and the tappable name

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml` (the header `Grid`, the `Page.Resources`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (the constructor, `OnNavigatedTo`, two handlers)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes: `Contact` (`Avatar`, `Initials`, `HasAvatar`); `ContactInfoPage` (Task 4); the existing `ImageViewer` / `ImageViewerImage` / `HideFullScreen` / `ViewerDecodePixels`.
- Produces: the header picture (`HeaderAvatar`) and the tappable name area (`ContactHeader`).

- [ ] **Step 1: Add the two tooltip strings**

In `WhatsappApp/Strings/en-US/Resources.resw`, before `</root>`, add:

```xml
  <data name="ChatPage_ProfilePhotoTooltip" xml:space="preserve">
    <value>Show the profile photo</value>
  </data>
  <data name="ChatPage_ContactInfoTooltip" xml:space="preserve">
    <value>Contact info</value>
  </data>
```

In `WhatsappApp/Strings/it-IT/Resources.resw`, before `</root>`, add:

```xml
  <data name="ChatPage_ProfilePhotoTooltip" xml:space="preserve">
    <value>Mostra la foto del profilo</value>
  </data>
  <data name="ChatPage_ContactInfoTooltip" xml:space="preserve">
    <value>Info contatto</value>
  </data>
```

- [ ] **Step 2: Add the converter resource**

In `WhatsappApp/Pages/ChatPage.xaml`, in `<Page.Resources>`, after `<conv:MessageTypeToImageVisibilityConverter x:Key="MsgTypeToImageVis"/>`, add:

```xml
        <conv:InitialToColorConverter x:Key="InitialToColor"/>
```

- [ ] **Step 3: Restructure the header**

In `WhatsappApp/Pages/ChatPage.xaml`, replace the header block (the `Grid Grid.Row="0"` with the two columns and the `StackPanel` with `ContactNameText` / `OnlineStatusText`) with:

```xml
        <!-- Header: back + picture + name/status. La foto e' una cosa a parte
             dalla riga del nome: toccandola si apre a tutto schermo, toccando il
             nome si aprono le informazioni. -->
        <Grid Grid.Row="0" Background="{StaticResource WhatsAppHeaderBrush}" Height="56">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <Button x:Name="BackButton" Grid.Column="0"
                    Background="Transparent"
                    MinWidth="0" MinHeight="0"
                    Width="48" Height="48" Margin="0,4,0,0" Padding="0"
                    Click="BackButton_Click"
                    BorderThickness="0">
                <Path Stroke="White" StrokeThickness="2.4"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24">
                    <!-- IconBack -->
                    <Path.Data>
                        <PathGeometry>
                            <PathGeometry.Figures>
                                <PathFigure StartPoint="15.5,4">
                                    <PathFigure.Segments>
                                        <PolyLineSegment Points="7.5,12 15.5,20"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                            </PathGeometry.Figures>
                        </PathGeometry>
                    </Path.Data>
                </Path>
            </Button>

            <Border x:Name="HeaderAvatar" Grid.Column="1"
                    Width="40" Height="40" CornerRadius="20"
                    Margin="8,0,0,0" VerticalAlignment="Center"
                    Tapped="HeaderAvatar_Tapped">
                <Grid>
                    <Ellipse Width="40" Height="40"
                             Fill="{Binding Initials, Converter={StaticResource InitialToColor}}"/>
                    <Border Width="40" Height="40" CornerRadius="20"
                            Visibility="{Binding HasAvatar, Converter={StaticResource BoolToVisibility}}">
                        <Border.Background>
                            <ImageBrush ImageSource="{Binding Avatar}" Stretch="UniformToFill"/>
                        </Border.Background>
                    </Border>
                    <TextBlock Text="{Binding Initials}"
                               Foreground="White" FontSize="15" FontWeight="SemiBold"
                               VerticalAlignment="Center" HorizontalAlignment="Center"
                               Visibility="{Binding HasAvatar, Converter={StaticResource BoolToVisibility}, ConverterParameter=Invert}"/>
                </Grid>
            </Border>

            <Grid x:Name="ContactHeader" Grid.Column="2"
                  Background="Transparent"
                  Margin="8,0,0,4"
                  Tapped="ContactHeader_Tapped">
                <StackPanel VerticalAlignment="Center">
                    <TextBlock x:Name="ContactNameText" Text=""
                               Foreground="White" FontSize="18" FontWeight="SemiBold"
                               TextTrimming="WordEllipsis"/>
                    <TextBlock x:Name="OnlineStatusText" Text=""
                               Foreground="#B0FFFFFF" FontSize="12" Margin="0,-2,0,0"/>
                </StackPanel>
            </Grid>
        </Grid>
```

- [ ] **Step 4: Set the data context, the tooltips and the two handlers**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, in the constructor after the `ClearImageButton` tooltip line, add:

```csharp
            // La foto e il nome sono due bersagli: il tooltip li distingue.
            ToolTipService.SetToolTip(HeaderAvatar, Loc.Get("ChatPage_ProfilePhotoTooltip", "Show the profile photo"));
            ToolTipService.SetToolTip(ContactHeader, Loc.Get("ChatPage_ContactInfoTooltip", "Contact info"));
```

In `OnNavigatedTo`, right after `_contact = contact;`, add:

```csharp
                // L'avatar e le iniziali vengono dal contatto: la pagina non li
                // ricostruisce.
                DataContext = contact;
```

Then, after `HeaderAvatar_Tapped` / below `ImageViewer_Tapped`, add:

```csharp
        /// <summary>
        /// La foto del profilo a tutto schermo: si decodifica alla misura dello
        /// schermo, come per una bolla, e si chiude toccandola (ImageViewer).
        /// Senza byte non si apre niente: non c'e' una richiesta da fare qui,
        /// l'elenco chat li ha gia' chiesti.
        /// </summary>
        private async void HeaderAvatar_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (_contact == null || string.IsNullOrEmpty(_contact.AvatarData)) return;

            try
            {
                var bitmap = await ImageHelper.FromBase64Async(_contact.AvatarData, ViewerDecodePixels);
                if (bitmap == null) return;
                ImageViewerImage.Source = bitmap;
                ImageViewer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.HeaderAvatar_Tapped", ex);
                HideFullScreen();
            }
        }

        /// <summary>Il nome apre le informazioni: la foto resta per la foto.</summary>
        private void ContactHeader_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_contact == null) return;
            e.Handled = true;
            Frame.Navigate(typeof(ContactInfoPage), _contact);
        }
```

- [ ] **Step 5: Normalize and run the fast gate and the build gate**

```bash
perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"
```

Expected: `OK: 142 key(s) ...`, `OK: 21 button(s) and 1 Button style(s)`, green tools tests.

Then run the three VM build commands. Expected `COPIA=0`, `Avvisi: 0`, `Errori: 0`, package created.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
git commit -m "$(cat <<'EOF'
Make the chat photo and name open what they promise

Until now the header was a back arrow and two lines of text: no picture to
touch and no way to the profile. The picture now opens full screen and the
name opens the information, which is the split the user asked for.
EOF
)"
```

---

### Task 6: Docs, counts and the final gate

**Files:**
- Modify: `README.md`, `README.it.md`
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Modify: `.agents/skills/test-the-app/SKILL.md`
- Modify: this plan (the execution section)

- [ ] **Step 1: Say the feature in both root READMEs**

In `README.md`, in the chat feature list, add:

```markdown
- A chat shows the profile picture, and both the picture and the name are tappable: the picture opens full screen, the name opens the contact info (number, about, business profile, and for a group the description and the members).
```

In `README.it.md`, in the same list, add:

```markdown
- Una chat mostra la foto del profilo, e sia la foto sia il nome si toccano: la foto si apre a tutto schermo, il nome apre le informazioni (numero, about, profilo aziendale e, per un gruppo, descrizione e membri).
```

- [ ] **Step 2: Add the maintain-the-app gotcha**

In `.agents/skills/maintain-the-app/SKILL.md`, after the avatar-cache gotcha, add:

```markdown
**The profile information is composed by the adapter, not on the phone.** The chat header and the contact-info page need a profile, a business profile and the members of a group, which live on three GOWA routes the phone cannot reach. The adapter answers one `contact.info` command with one JSON frame, and it always answers, even when WhatsApp is down: a page that waits forever is worse than a page that says nothing is available. The picture rides inside that JSON (`AvatarData`), from the adapter avatar cache, so the big photo exists even for a chat whose row never carried one. Adding a field means changing the adapter JSON, `WhatsappApp/Models/ContactInfo.cs` and the page in the same push, because the two ends are case-sensitive.
```

- [ ] **Step 3: Update the counts and the on-device checks**

In `.agents/skills/test-the-app/SKILL.md`, update the counts sentence to the numbers the gate prints (they are 40 C# files, 142 `.resw` keys, 23 inline `Path` / 14 distinct, 21 buttons + 1 Button style, and the tool/adapter test counts) and append these on-device checks after check 59:

```markdown
60. Open a chat whose contact has a picture: the header shows the round picture. Tap it: the picture fills the screen, and a tap closes it. Nothing else navigates.
61. Tap the name in the same chat: the contact-info page opens with the big picture, the name and, when the server knows them, the number and the about text.
62. Open a group chat and tap the name: the page shows the group description and the members, with the admin label on the ones that have it. The list scrolls, and a member without a name shows the number.
63. Open a chat with no picture at all: the header shows the initials, tapping them does nothing, and the name still opens the info page.
64. Open the info page on a phone with no connection: after a moment it says there is no information from the server, instead of staying in a loading state forever.
```

- [ ] **Step 4: Run the complete gate one last time**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"
cd WhatsappBridge && npm test
cd /tmp/docker-whatsappforwp && node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP && cd server && npm test
```

Expected: every line `OK`, 0 failures everywhere.

- [ ] **Step 5: Run the VM build one last time**

Run the three build commands from Global Constraints. Expected `COPIA=0`, `Avvisi: 0`, `Errori: 0`, `Your package has been successfully created.`

- [ ] **Step 6: Commit and push**

```bash
git add README.md README.it.md docs/superpowers/plans/2026-09-30-chat-profile-picture-and-contact-info.md .agents/skills/maintain-the-app/SKILL.md .agents/skills/test-the-app/SKILL.md
git commit -m "$(cat <<'EOF'
Record the tappable profile and the info page

The counts and the on-device checks have to say what the app can do now, and
the next person has to know the profile is composed by the adapter.
EOF
)"
git push origin master 2>&1 | tail -2
```

---

## Self-Review

**1. Spec coverage.**
- Picture clickable in a chat → Task 5 (`HeaderAvatar_Tapped`).
- Tapping the photo shows it full screen → Task 5 (reuses `ImageViewer`).
- Tapping the name opens WhatsApp-like info → Task 5 (`ContactHeader_Tapped` → `ContactInfoPage`) and Task 4 (the page).
- Photo, name, number, about, business profile → Task 1 (`userInfo`, `businessProfile`), Task 2 (composition), Task 3 (model), Task 4 (view).
- Group description and members with roles → Task 1 (`groupInfo`, `groupParticipants`), Task 2, Task 3, Task 4.
- Both languages, both `.resw` files → Task 4 Step 1 and Task 5 Step 1.

**2. Placeholder scan.** No `TBD`/`TODO`. Every code step carries the actual code. The only "whatever the gate prints" instruction is the counts update in Task 6, which is a measurement, not a placeholder.

**3. Type consistency.**
- Adapter JSON keys (`Name`, `About`, `Number`, `AvatarData`, `Business.Email`, `Business.Categories`, `Business.Hours[].Day/Mode/Open/Close`, `Group.Description`, `Group.Members[].Jid/Number/Name/IsAdmin/IsSuperAdmin`) match `WhatsappApp/Models/ContactInfo.cs` exactly, case included.
- `AvatarCache.Remember(string, string)`, `ImageHelper.FromBase64Async(string, int)`, `CommunicationService.SendControlAsync(string, string)`, `Loc.Get(string, string)` are used with their existing signatures.
- `ContactInfoPage` is created in Task 4 and navigated to in Task 5; Task 4 lands first, so the build is green at every task boundary.
- Both new `<Button>`s (`BackButton` in `ContactInfoPage`) declare `Width` with `MinWidth="0" MinHeight="0"` and are wired to `BackButton_Click`, so `check-actions.js` passes; the title-bar icon table only covers `ChatsPage`, so the new page needs no `IconX` declaration.

## What execution changed about this plan

- The plan declared 15 new `ContactInfoPage_*` keys, but the page code only ever uses
  `ContactInfoPage_MembersCount` for the heading. `ContactInfoPage_Members` was never
  read, and `check-resw.js --strict` fails on an unused key, so it was dropped from both
  `.resw` files. The final count is 141 keys, not 142.
- The two lists on the info page (`HoursList`, `MembersList`) got a `MaxHeight` (240 and
  320). A `ListView` inside a vertical `StackPanel` inside a `ScrollViewer` measures its
  items with unbounded height, so a 500-member group would have drawn every row at once;
  a bound keeps the list to its own scroll and caps what the page holds.
- Everything else landed as written: the four `GowaClient` methods with their tests, the
  `contact.info` command and its three server tests, `ContactInfo`, `ContactInfoPage`, the
  header picture and the tappable name, both README pairs, the Docker mirror (`server/`
  matches the adapter, 24 files), and the full gate plus the VM build (`Avvisi: 0`,
  `Errori: 0`, package created).
