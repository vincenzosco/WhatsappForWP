# Chat List: Group Pictures, Pin, Mute and Delete Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Group conversations show their picture in the chat list, and a row can be held (or the list's new three-dots menu used) to pin, mute or delete that conversation on this phone.

**Architecture:** Two independent faults and one new screen behaviour. The missing group picture is an adapter bug with a verified root cause: `chats.js` never asks for the picture of a group and `gowa-client.js` strips the JID down to its digits, but GOWA's `/user/avatar` takes any JID as `phone` (its `SanitizePhone` only rewrites a value that has no `@`, and `GetProfilePictureInfo` accepts a group JID), so both guards are removed. Pin, mute and delete are decisions of this phone - GOWA has no endpoint for any of them - so they live in a new `ChatPreferences` file in the app folder rather than in a frame: `DataService` applies them to the rows (pinned first, muted rows raise no toast, deleted rows stay hidden until a new message arrives), and `ChatsPage` grows a three-dots button at the top left plus a `Holding` menu on every row.

**Tech Stack:** Node.js adapter (CommonJS, zero runtime dependencies, `node:test`), WP8.1 C# 5 / XAML (`Windows.UI.Xaml`, `MenuFlyout`, `Holding`), `DataContractJsonSerializer`, the `tools/` static guards as the only test host for C#.

## Global Constraints

- **C# 5 only** in `WhatsappApp/`. No `$"..."`, no `?.`, no expression-bodied members, no auto-property initializers, no `out var`, no `is T x`, no `nameof`, no `_ =`, no string interpolation. `await` inside a `catch` block is refused by the compiler (CS1985): note the failure and await after the block. Gate: `node tools/check-csharp5.js`.
- **Icons are inline vector `Path` geometries**, each named by an `<!-- IconX -->` comment above its `Path.Data`; the same name must be the same geometry everywhere. No `Segoe MDL2 Assets`, no `Data="{StaticResource IconX}"`, no `Figures="M..."`. Gate: `node tools/check-icons.js`.
- **No hardcoded user-visible strings.** XAML uses `x:Uid`; C# uses `Loc.Get("Key", "fallback")`. A new key goes into **both** `Strings/en-US/Resources.resw` and `Strings/it-IT/Resources.resw`, and `--strict` fails on a key nobody reads. Gate: `node tools/check-resw.js --strict`.
- **Every survived failure goes through `Diag.Failed("<call site>", ex)`.** Never a silent `catch`.
- **A fire-and-forget call catches its own exception** (the caller has nobody to hand it to). `ChatPreferences.Save` and `MessageCache.DeleteAsync` catch everything internally.
- **A new `.cs` file must be listed in `WhatsappApp/WhatsappApp.csproj`** or it does not exist at build time.
- **A `Border` takes exactly one child**; wrap several in a `<Grid>` (WMC0035).
- **`ContentDialog` on WP8.1 has no `CloseButtonText`**: the cancel text is `SecondaryButtonText`, and the hardware Back button dismisses with `ContentDialogResult.None`.
- **The adapter keeps zero runtime dependencies** and its tests must stay green: `cd WhatsappBridge && npm test`.
- **There is no C# test host.** A C# change is verified by the guards, the WP8.1 build gate on the Parallels VM, and the checks in `test-the-app` on the device.
- **The fast gate, after every task:**

  ```bash
  node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"
  ```

  Counts today, to be compared against after each task: `35 C# file(s)`, `20 inline icon Path(s), 11 distinct icon(s)`, `110 key(s) in en-US and it-IT`, `2 doc pair(s) in step ... no emoji in 36 .md file(s)`, `19 button(s)`, `8*1024*1024` frame ceiling, all tool tests passing. Adapter: `pass 131`.
- **The WP8.1 build gate** (three separate calls; retry the identical MSBuild command if it answers `PrlJob_GetRetCode: Invalid argument` with exit 255):

  ```bash
  prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
  prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
  prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"
  ```

  Expected: `COPIA=0` and `Your package has been successfully created.`
- **Docs are written in pairs** (`README.md`/`README.it.md`, `WhatsappBridge/README.md`/`WhatsappBridge/README.it.md`): same headings, same order, same commit. `## Disclosure` stays the last section of the project READMEs. No emoji anywhere (U+26A0 is the only exception). `docs/superpowers/plans/*.md` stay English-only, outside `PAIRS`.
- **The plan is executed, not filed.** When this document is finished it is executed in the same session, task by task, until the last task is pushed.

---

## File Structure

| File | Responsibility | Change |
| --- | --- | --- |
| `WhatsappBridge/gowa-client.js` | HTTP to GOWA | Task 1: `avatar(jid)` asks for any JID, group or person |
| `WhatsappBridge/chats.js` | the `chat` rows | Task 1: ask for the picture of a group too |
| `WhatsappBridge/test/gowa-client.test.js` | gowa-client tests | Task 1: the group avatar test replaces the `null` test |
| `WhatsappBridge/test/chats.test.js` | chats tests | Task 1: every row carries its picture |
| `WhatsappBridge/README.md` / `.it.md` | adapter docs | Task 1: `CHATS_AVATARS` row |
| `WhatsappApp/Services/ChatPreferences.cs` | new | Task 2: what this phone decided about a chat, on disk |
| `WhatsappApp/WhatsappApp.csproj` | project file | Task 2: list `ChatPreferences.cs` |
| `WhatsappApp/Models/Contact.cs` | one row of the list | Task 2: `IsPinned`, `IsMuted` |
| `WhatsappApp/Services/DataService.cs` | state | Task 2: apply the preferences, order the pinned first, silence a muted chat |
| `WhatsappApp/Pages/ChatsPage.xaml` | the list | Task 3: pin and mute marks; Task 4: the three-dots button |
| `WhatsappApp/Pages/ChatsPage.xaml.cs` | the list | Task 4: the overflow menu and the pin picker; Task 5: the holding menu and delete |
| `WhatsappApp/Services/MessageCache.cs` | cached messages | Task 5: `DeleteAsync(chatId)` |
| `WhatsappApp/Strings/{en-US,it-IT}/Resources.resw` | strings | Task 4 and Task 5: 15 new keys |
| `tools/check-actions.js` | icon/handler guard | Task 4: `MoreButton` in `TITLE_BAR` |
| `README.md` / `README.it.md` | project docs | Task 6: the three new behaviours and their limits |
| `.agents/skills/maintain-the-app/SKILL.md` | constraints | Task 6: the group-picture claim, the new gotchas, the plan rule |
| `.agents/skills/README.md` | skills index | Task 6: the plan rule |
| `.agents/skills/test-the-app/SKILL.md` | device checks | Task 6: checks 47 to 50 |

---

### Task 1: A group's picture reaches the chat list

**Files:**
- Modify: `WhatsappBridge/gowa-client.js:226-241` (`avatar`)
- Modify: `WhatsappBridge/chats.js:99-105` (the avatar lookup)
- Test: `WhatsappBridge/test/gowa-client.test.js:158-175` and `WhatsappBridge/test/chats.test.js:89-116`
- Modify: `WhatsappBridge/README.md:114`, `WhatsappBridge/README.it.md:115`

**Interfaces:**
- Consumes: `GowaClient.request(method, path)` and `GowaClient.fetchBinary(url)`, both already in `gowa-client.js`.
- Produces: `gowa.avatar(jid) -> Promise<string|null>`, a base64 picture, for a person **and** for a group; `collectChats({ gowa, limit, avatars, groupNames, log })` rows whose `avatar` field is no longer forced to `null` for `@g.us`.

**Why the guard comes off.** `chats.js` skips the request for a group because `gowa-client.js` documents that "groups have no personal avatar". The GOWA side does not agree:

- `/user/avatar` binds its `phone` parameter and passes it through `utils.SanitizePhone`, which is `if phone != nil && len(*phone) > 0 && !strings.Contains(*phone, "@") { append a suffix }` (`src/pkg/utils/whatsapp.go`). A value that already contains `@` is left exactly as it is, so a full JID is a valid `phone`.
- The handler then runs `utils.ValidateJidWithLogin`, which for a non-`@s.whatsapp.net` JID returns `true` from `IsOnWhatsapp` without a lookup ("For non-user JIDs (groups, newsletters), skip validation"), and calls `client.GetProfilePictureInfo(ctx, dataWaRecipient, ...)` - whatsmeow's profile-picture call, which takes any `types.JID`, groups included.
- `GET /group/photo` is `POST` only (it *sets* a group picture), so there is no alternative door.

The two reasons the app used to show initials for every group are therefore both on this side: the request was never made, and when it is made the JID is cut down to its digits (`value.split('@')[0]`), which turns `123...@g.us` into a number that GOWA then appends `@s.whatsapp.net` to.

- [x] **Step 1: Change the two tests to state the new contract**

In `WhatsappBridge/test/gowa-client.test.js`, replace the whole `test('avatar() asks nothing about a group, and nothing when GOWA has no picture', ...)` block with these two tests:

```js
test('avatar() asks with the whole JID, so a group JID is a JID', async () => {
  const { client, seen } = clientWith([
    { status: 200, body: { results: { url: 'http://cdn.example/g.jpg' } } },
    { status: 200, body: 'BYTES', contentType: 'image/jpeg' }
  ]);

  const picture = await client.avatar('123456789012345678@g.us');

  // Il valore intero, non le sole cifre: GOWA aggiunge il suffisso solo a un
  // valore che non contiene '@', quindi un JID di gruppo passato intero resta
  // un JID di gruppo, e GetProfilePictureInfo accetta qualunque JID.
  assert.strictEqual(seen[0],
    'http://127.0.0.1:3000/user/avatar?phone=123456789012345678%40g.us&is_preview=true');
  assert.strictEqual(picture, 'BYkVRVM=');
});

test('avatar() drops a device suffix and answers null when there is no picture', async () => {
  const none = clientWith([{ status: 404, body: { message: 'no avatar found' } }]);
  const deviced = clientWith([
    { status: 200, body: { results: { url: 'http://cdn.example/p.jpg' } } },
    { status: 200, body: 'BYTES', contentType: 'image/jpeg' }
  ]);

  // Un JID con il suffisso del dispositivo (:12) non e' il JID che WhatsApp
  // riconosce in una richiesta di profilo.
  await deviced.client.avatar('393401234567:12@s.whatsapp.net');
  assert.strictEqual(deviced.seen[0],
    'http://127.0.0.1:3000/user/avatar?phone=393401234567%40s.whatsapp.net&is_preview=true');

  assert.strictEqual(await none.client.avatar('393401234567@s.whatsapp.net'), null);
});
```

Then, in the test `avatar() follows the picture URL GOWA returns, then downloads it`, update the two URL assertions to the full JID:

```js
  assert.strictEqual(seen[0],
    'http://127.0.0.1:3000/user/avatar?phone=393401234567%40s.whatsapp.net&is_preview=true');
```

If `clientWith` is not the helper name in that file, read the top of the file and use the helper already there (the existing tests build a client over a `nodes` array of `{ status, body, contentType }`); do not invent a second one.

- [x] **Step 2: Run them, and watch them fail on the group URL**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: FAIL - the group test receives `http://127.0.0.1:3000/user/avatar?phone=123456789012345678&is_preview=true` (the digits only) or `null`, and the device-suffix test receives a URL without the suffix stripped.

- [x] **Step 3: Make `avatar()` take any JID**

In `WhatsappBridge/gowa-client.js`, replace the body of `avatar` (keep the comment above it and extend it) with:

```js
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

    const target = value.split(':')[0];
    try {
      const r = await this.request('GET',
        `/user/avatar?phone=${encodeURIComponent(target)}&is_preview=true`);
      const url = (r.data && r.data.results && r.data.results.url) || '';
      if (!r.ok || !url) return null;

      const picture = await this.fetchBinary(url);
      return picture.buffer.length > 0 ? picture.buffer.toString('base64') : null;
    } catch (err) {
      return null;
    }
  }
```

- [x] **Step 4: Run the file's tests again**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: PASS, and one more test than before in that file.

- [x] **Step 5: Write the failing chats test**

In `WhatsappBridge/test/chats.test.js`, replace `test('collectChats asks for the avatar of people only, and survives a failure', ...)` with:

```js
test('collectChats asks for the avatar of every chat, groups included', async () => {
  const asked = [];
  const gowa = {
    chats: async () => ([
      { jid: 'a@s.whatsapp.net', name: 'A' },
      { jid: '123456789012345678@g.us', name: 'Amici' }
    ]),
    chatMessages: async () => ([]),
    avatar: async (jid) => {
      asked.push(jid);
      return jid.endsWith('@g.us') ? 'GRUPPO' : 'AAAA';
    }
  };

  const rows = await collectChats({ gowa, limit: 10, avatars: true, log: () => {} });

  assert.deepStrictEqual(asked.slice().sort(), ['123456789012345678@g.us', 'a@s.whatsapp.net']);
  assert.strictEqual(rows.find((r) => r.chatId === 'a@s.whatsapp.net').avatar, 'AAAA');
  // Un gruppo senza immagine non e' un gruppo senza riga: la riga c'e' e
  // l'immagine arriva quando c'e'.
  assert.strictEqual(rows.find((r) => r.chatId === '123456789012345678@g.us').avatar, 'GRUPPO');
});

test('collectChats survives an avatar that does not download', async () => {
  const failing = {
    chats: async () => ([{ jid: 'a@s.whatsapp.net', name: 'A' }]),
    chatMessages: async () => ([]),
    avatar: async () => { throw new Error('no picture'); }
  };

  // Un avatar che non si scarica e' un avatar in meno, non una chat in meno.
  const again = await collectChats({ gowa: failing, limit: 10, avatars: true, log: () => {} });
  assert.strictEqual(again.length, 1);
  assert.strictEqual(again[0].avatar, null);
});
```

- [x] **Step 6: Run it, and watch it fail**

Run: `cd WhatsappBridge && node --test test/chats.test.js`
Expected: FAIL - `asked` is `['a@s.whatsapp.net']`, the group's `avatar` is `null`.

- [x] **Step 7: Ask for a group's picture too**

In `WhatsappBridge/chats.js`, replace the avatar lookup and the comment above it:

```js
    // L'immagine del profilo si chiede per ogni conversazione: GOWA la sa dare
    // anche per un gruppo, perche' il suo parametro `phone` e' un JID e
    // whatsmeow accetta qualunque JID in una richiesta di profilo.
    let avatar = null;
    if (withAvatars) {
      try {
        avatar = await gowa.avatar(chat.jid);
      } catch (err) {
        log('DEBUG', `Chats: avatar of ${chat.jid} not readable (${err.message})`);
      }
    }
```

Also update the file's header comment, where it says the picture is fetched "per le persone": change that phrase to `e per ogni conversazione, gruppi compresi` and record the cost as one request per chat.

- [x] **Step 8: Run the whole adapter suite**

Run: `cd WhatsappBridge && npm test`
Expected: `pass 133`, `fail 0` (131 before, plus the two new tests).

- [x] **Step 9: Update the adapter README pair**

In `WhatsappBridge/README.md`, the environment table row:

```markdown
| `CHATS_AVATARS` | `on` | fetch profile pictures, one request per chat, groups included (`off` disables) |
```

In `WhatsappBridge/README.it.md`, the same row in the same position:

```markdown
| `CHATS_AVATARS` | `on` | scarica le immagini del profilo, una richiesta per chat, gruppi compresi (`off` le spegne) |
```

In both files, the `chat` frame row already says `AvatarData` (base64): leave it as it is.

- [x] **Step 10: Fix the claim in the skill**

In `.agents/skills/maintain-the-app/SKILL.md`, in *Showing only data that exists*, the paragraph that ends with "and only for people: groups have none." - replace its last two lines with:

```markdown
  `/chats` is full, which is exactly how the chat list came up blank. Profile
  pictures come from `GET /user/avatar?phone=<JID>&is_preview=true`, for people
  **and** groups: the parameter is a JID (GOWA appends a suffix only to a value
  with no `@` in it, `SanitizePhone`), and `GetProfilePictureInfo` takes any JID.
```

- [x] **Step 11: Run the fast gate**

Run the gate from the Global Constraints section.
Expected: the same counts as before except `2 doc pair(s) in step ... no emoji in 36 .md file(s)` (the plan file is not written yet when this task runs, so it is still 36), and the adapter output is not part of this command.

- [x] **Step 12: Commit and push**

```bash
git add WhatsappBridge .agents/skills/maintain-the-app/SKILL.md
git commit -m "$(cat <<'EOF'
fix: ask GOWA for a group's picture, not only a person's

The chat list showed initials for every group. The adapter never made the
request (chats.js skipped @g.us) and, when it did, it cut the JID down to its
digits - but /user/avatar takes a JID: GOWA appends a suffix only to a value
with no @ in it, and the profile-picture lookup accepts a group JID.
EOF
)"
git push
```

---

### Task 2: What this phone decided about a chat

**Files:**
- Create: `WhatsappApp/Services/ChatPreferences.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (the `Services\...` item group)
- Modify: `WhatsappApp/Models/Contact.cs`
- Modify: `WhatsappApp/Services/DataService.cs`

**Interfaces:**
- Consumes: `Diag.Failed(string, Exception)`, `Contact.Id`, `DataService._contacts`, `DataService._contactIndex`, `DataService._chatMessages`, `DataService._historyRequested`, `DataService.TotalUnread()`, `DataService.FindContact(string)`.
- Produces:
  - `ChatPreferences.LoadAsync() -> Task` (idempotent), `ChatPreferences.IsLoaded -> bool`
  - `ChatPreferences.IsPinned(string) -> bool`, `IsMuted(string) -> bool`, `IsHidden(string) -> bool`
  - `ChatPreferences.SetPinned(string, bool) -> void`, `SetMuted(string, bool) -> void`, `Hide(string) -> void`, `Reveal(string) -> void` (all save the file themselves)
  - `Contact.IsPinned -> bool` (notifying), `Contact.IsMuted -> bool` (notifying)
  - `DataService.SetPinned(string chatId, bool pinned) -> void`, `DataService.SetMuted(string chatId, bool muted) -> void`

**Why a file of its own.** `ChatCache` is a photograph of the server's list, replaced row by row on every sync: a pin kept there would be erased by the next `chats` reply, and a chat the adapter no longer lists (`CHATS_LIMIT`) would lose it. `ChatPreferences` is the other thing: what this phone decided, which nothing overwrites.

- [x] **Step 1: Write the store**

Create `WhatsappApp/Services/ChatPreferences.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>Quello che questo telefono ha deciso di una chat.</summary>
    [DataContract]
    internal class ChatPreference
    {
        [DataMember]
        public string ChatId { get; set; }

        [DataMember]
        public bool Pinned { get; set; }

        [DataMember]
        public bool Muted { get; set; }

        [DataMember]
        public bool Hidden { get; set; }
    }

    /// <summary>Il file su disco: un solo campo, l'elenco delle decisioni.</summary>
    [DataContract]
    internal class ChatPreferenceFile
    {
        [DataMember]
        public List<ChatPreference> Chats { get; set; }
    }

    /// <summary>
    /// Pinnare, silenziare ed eliminare una chat sono decisioni di questo
    /// telefono, non di WhatsApp: GOWA non ha nessun endpoint per nessuna delle
    /// tre, e l'elenco delle conversazioni che l'adapter manda e' di sola
    /// lettura.
    ///
    /// Perche' non nella cache dell'elenco (ChatCache): quella e' una fotografia
    /// che il server sostituisce riga per riga, e una chat che in
    /// quell'elenco non compare piu' (vecchia, o tagliata fuori da CHATS_LIMIT)
    /// perderebbe il suo pin. Qui invece sopravvive al server.
    /// </summary>
    public static class ChatPreferences
    {
        private const string FileName = "chat-preferences.json";

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(ChatPreferenceFile));

        // Snapshot in memoria: l'elenco chat chiede queste risposte per ogni
        // riga, e leggerle dal disco ogni volta sarebbe un file per messaggio.
        private static readonly Dictionary<string, ChatPreference> Known =
            new Dictionary<string, ChatPreference>();

        private static bool _loaded;

        /// <summary>Vero quando il file e' stato letto. Solo per la diagnosi.</summary>
        public static bool IsLoaded
        {
            get { return _loaded; }
        }

        /// <summary>Legge il file una volta sola. Mai un'eccezione: al primo avvio non c'e'.</summary>
        public static async Task LoadAsync()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                string json = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrEmpty(json)) return;

                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var known = Serializer.ReadObject(stream) as ChatPreferenceFile;
                    if (known == null || known.Chats == null) return;

                    for (int i = 0; i < known.Chats.Count; i++)
                    {
                        var entry = known.Chats[i];
                        if (entry == null || string.IsNullOrEmpty(entry.ChatId)) continue;
                        Known[entry.ChatId] = entry;
                    }
                }
            }
            catch (Exception ex)
            {
                // Primo avvio, o file scritto da una versione diversa.
                Diag.Failed("ChatPreferences.Load", ex);
            }
        }

        public static bool IsPinned(string chatId)
        {
            var entry = Find(chatId);
            return entry != null && entry.Pinned;
        }

        public static bool IsMuted(string chatId)
        {
            var entry = Find(chatId);
            return entry != null && entry.Muted;
        }

        public static bool IsHidden(string chatId)
        {
            var entry = Find(chatId);
            return entry != null && entry.Hidden;
        }

        public static void SetPinned(string chatId, bool pinned)
        {
            var entry = EntryFor(chatId);
            if (entry == null) return;
            entry.Pinned = pinned;
            Forget(chatId, entry);
            Save();
        }

        public static void SetMuted(string chatId, bool muted)
        {
            var entry = EntryFor(chatId);
            if (entry == null) return;
            entry.Muted = muted;
            Forget(chatId, entry);
            Save();
        }

        /// <summary>
        /// La chat e' stata eliminata da questo telefono: resta fuori
        /// dall'elenco anche quando il server la rimanda. Non e' la fine della
        /// conversazione - Reveal la fa tornare, e la chiama un messaggio
        /// nuovo, come fa WhatsApp.
        /// </summary>
        public static void Hide(string chatId)
        {
            var entry = EntryFor(chatId);
            if (entry == null) return;
            entry.Hidden = true;
            Save();
        }

        /// <summary>La chat torna nell'elenco. Pin e silenzio restano come erano.</summary>
        public static void Reveal(string chatId)
        {
            var entry = Find(chatId);
            if (entry == null || !entry.Hidden) return;
            entry.Hidden = false;
            Forget(chatId, entry);
            Save();
        }

        private static ChatPreference Find(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return null;

            ChatPreference entry;
            return Known.TryGetValue(chatId, out entry) ? entry : null;
        }

        private static ChatPreference EntryFor(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return null;

            var entry = Find(chatId);
            if (entry != null) return entry;

            entry = new ChatPreference { ChatId = chatId };
            Known[chatId] = entry;
            return entry;
        }

        /// <summary>
        /// Una voce senza niente da dire non si tiene: un file di righe tutte a
        /// false cresce con l'elenco delle conversazioni, che cambia da solo.
        /// </summary>
        private static void Forget(string chatId, ChatPreference entry)
        {
            if (entry.Pinned || entry.Muted || entry.Hidden) return;
            Known.Remove(chatId);
        }

        private static void Save()
        {
            var file = new ChatPreferenceFile { Chats = new List<ChatPreference>() };
            foreach (var entry in Known.Values) file.Chats.Add(entry);

#pragma warning disable 4014
            WriteAsync(file);
#pragma warning restore 4014
        }

        /// <summary>
        /// Scrive il file. Non aspetta nessuno - chi cambia un pin non ha niente
        /// da fare con l'esito - quindi cattura da sola: un deposito senza
        /// padrone non deve poter far cadere la pagina. Mai un'eccezione.
        /// </summary>
        private static async Task WriteAsync(ChatPreferenceFile file)
        {
            try
            {
                string json;
                using (var stream = new MemoryStream())
                {
                    Serializer.WriteObject(stream, file);
                    json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }

                StorageFile storage = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(storage, json);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPreferences.Save", ex);
            }
        }
    }
}
```

- [x] **Step 2: List it in the project file**

In `WhatsappApp/WhatsappApp.csproj`, next to the other `Services` entries, add:

```xml
    <Compile Include="Services\ChatPreferences.cs" />
```

- [x] **Step 3: Give a row the two marks**

In `WhatsappApp/Models/Contact.cs`, add the two fields next to `_unreadCount`:

```csharp
        private bool _isPinned;
        private bool _isMuted;
```

and the two properties after `UnreadCount`:

```csharp
        /// <summary>
        /// In cima all'elenco. E' una decisione di questo telefono
        /// (ChatPreferences): WhatsApp non sa niente di un pin fatto qui, e
        /// l'adapter non manda nessun campo per questo.
        /// </summary>
        public bool IsPinned
        {
            get { return _isPinned; }
            set { _isPinned = value; OnPropertyChanged(); }
        }

        /// <summary>I messaggi di questa chat non alzano un avviso. Il numero dei non letti resta.</summary>
        public bool IsMuted
        {
            get { return _isMuted; }
            set { _isMuted = value; OnPropertyChanged(); }
        }
```

- [x] **Step 4: Apply them in `DataService`**

In `WhatsappApp/Services/DataService.cs`:

1. In `LoadCachedChatsAsync`, the preferences are read before the first row is built:

```csharp
        private async System.Threading.Tasks.Task LoadCachedChatsAsync()
        {
            // Prima delle righe: ApplyChat chiede subito quali sono pinnate,
            // silenziate o eliminate, e un file non ancora letto risponderebbe
            // di no a tutte e tre.
            await ChatPreferences.LoadAsync();

            var cached = await ChatCache.LoadAsync();
            for (int i = 0; i < cached.Count; i++) ApplyChat(cached[i]);
            NotificationService.SetUnread(TotalUnread());
        }
```

2. In `ApplyChat`, right after the `if (string.IsNullOrEmpty(message.ChatId)) return;` line:

```csharp
            // Una chat eliminata da questo telefono non torna con l'elenco del
            // server: la decisione sta sul telefono (ChatPreferences) e la
            // annulla solo un messaggio nuovo.
            if (ChatPreferences.IsHidden(message.ChatId)) return;
```

3. In the same method, just before `RememberChatRow(message);`:

```csharp
            contact.IsPinned = ChatPreferences.IsPinned(message.ChatId);
            contact.IsMuted = ChatPreferences.IsMuted(message.ChatId);
            ResortContacts();

            RememberChatRow(message);
```

4. In `ApplyContact`, after the `if (string.IsNullOrEmpty(message.ChatId)) return;` line, add the same hidden check, and at the end of the method (after the `else` block) add:

```csharp
            var target = FindContact(message.ChatId);
            if (target != null)
            {
                target.IsPinned = ChatPreferences.IsPinned(message.ChatId);
                target.IsMuted = ChatPreferences.IsMuted(message.ChatId);
                ResortContacts();
            }
```

5. In `OnNetworkMessageReceived`, after the `if (message.IsHistory)` block, add:

```csharp
            // Un messaggio nuovo fa tornare una chat eliminata: e' quello che fa
            // WhatsApp. Senza questo, una chat cancellata per sbaglio non
            // tornerebbe mai piu'.
            if (ChatPreferences.IsHidden(message.ChatId)) ChatPreferences.Reveal(message.ChatId);
```

6. In the same method, the new-contact branch sets the two marks before the insert:

```csharp
                contact = new Contact
                {
                    Id = message.ChatId,
                    Name = name,
                    LastMessage = message.Text,
                    LastMessageTime = message.FormattedTime,
                    Initials = InitialsFor(name),
                    IsPinned = ChatPreferences.IsPinned(message.ChatId),
                    IsMuted = ChatPreferences.IsMuted(message.ChatId),
                    UnreadCount = message.IsIncoming ? 1 : 0
                };
```

7. In the same method, the `else` branch ends with the move; add the resort right after it:

```csharp
                var idx = _contacts.IndexOf(contact);
                if (idx > 0)
                    _contacts.Move(idx, 0);

                // Un messaggio in una chat non pinnata non deve scavalcare le
                // pinnate: si rimettono in cima, e questa resta subito sotto.
                ResortContacts();
```

8. The toast becomes silent for a muted chat:

```csharp
            // Un avviso solo per una chat che non stiamo guardando e che non e'
            // silenziata: il silenzio e' la sola cosa che "silenziare" fa - il
            // numero dei non letti resta, perche' il messaggio e' comunque non
            // letto.
            if (message.IsIncoming && message.ChatId != _activeChatId && !contact.IsMuted)
                NotificationService.ShowMessage(contact.Name, message.Text);
```

9. `AddMessage` (the app's own send path) ends with the move as well: add `ResortContacts();` after it.

10. The two setters, next to `ClearUnread`:

```csharp
        /// <summary>Mette (o toglie) una chat in cima all'elenco. Decide il telefono, non il server.</summary>
        public void SetPinned(string chatId, bool pinned)
        {
            var contact = FindContact(chatId);
            if (contact == null) return;

            contact.IsPinned = pinned;
            ChatPreferences.SetPinned(chatId, pinned);
            ResortContacts();
        }

        /// <summary>Silenzia la chat: i suoi messaggi non alzano un avviso.</summary>
        public void SetMuted(string chatId, bool muted)
        {
            var contact = FindContact(chatId);
            if (contact == null) return;

            contact.IsMuted = muted;
            ChatPreferences.SetMuted(chatId, muted);
        }
```

11. The ordering helper, next to `TotalUnread`:

```csharp
        /// <summary>
        /// Riporta in cima le chat pinnate lasciando le altre dove sono (la piu'
        /// recente per prima): ognuna viene spostata davanti alla prima non
        /// pinnata, quindi l'ordine relativo delle altre non cambia. Non e' un
        /// sort: una chiave di data servirebbe a rifare un ordine che la
        /// collezione ha gia'.
        /// </summary>
        private void ResortContacts()
        {
            int target = 0;
            for (int i = 0; i < _contacts.Count; i++)
            {
                if (_contacts[i] == null || !_contacts[i].IsPinned) continue;
                if (i != target) _contacts.Move(i, target);
                target++;
            }
        }
```

- [x] **Step 5: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-memory.js`
Expected: `OK: 36 C# file(s) are C# 5 compatible.` and `OK: every decoded bitmap asks for the width it is shown at.`

- [x] **Step 6: Check the project file lists the new source**

Run: `grep -n "ChatPreferences" WhatsappApp/WhatsappApp.csproj`
Expected: one `<Compile Include="Services\ChatPreferences.cs" />` line.

- [x] **Step 7: Build on the VM**

Run the three build commands from the Global Constraints.
Expected: `COPIA=0`, then `Your package has been successfully created.`

- [x] **Step 8: Commit and push**

```bash
git add WhatsappApp/Services/ChatPreferences.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/Models/Contact.cs WhatsappApp/Services/DataService.cs
git commit -m "$(cat <<'EOF'
feat: keep a chat's pin, silence and deletion on this phone

GOWA has no endpoint for pinning, muting or deleting a conversation, so the
three live in a file of the app instead of in a frame. ChatCache cannot hold
them: it is a photograph the server replaces row by row, so a pin kept there
would be gone at the next chat list.
EOF
)"
git push
```

---

### Task 3: The row shows what this phone decided

**Files:**
- Modify: `WhatsappApp/Pages/ChatsPage.xaml` (the row template, the time column)

**Interfaces:**
- Consumes: `Contact.IsPinned`, `Contact.IsMuted` (Task 2), the existing `BoolToVisibility` converter with `ConverterParameter=Invert`.
- Produces: nothing other tasks use; the row is the whole deliverable.

- [x] **Step 1: Add the two marks to the row**

In `WhatsappApp/Pages/ChatsPage.xaml`, the third column's `StackPanel` currently holds the time and the unread badge. Replace that whole `StackPanel` with this one (the badge markup is unchanged, it just moves inside):

```xml
                            <!-- Time, the marks of this phone, and unread badge -->
                            <StackPanel Grid.Column="2" VerticalAlignment="Center" Margin="0,0,12,0">
                                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                                    <!-- Il pin e il silenzio sono decisioni di
                                         questo telefono: la riga lo dice, o il
                                         menu sarebbe l'unico posto dove
                                         scoprirlo. -->
                                    <Path Width="12" Height="12" Margin="0,0,3,0"
                                          VerticalAlignment="Center"
                                          Stroke="#FF808080" StrokeThickness="1.5"
                                          StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                                          StrokeLineJoin="Round"
                                          Visibility="{Binding IsPinned, Converter={StaticResource BoolToVisibility}}">
                                        <!-- IconPin -->
                                        <Path.Data>
                                            <PathGeometry>
                                                <PathGeometry.Figures>
                                                    <PathFigure StartPoint="6,1.5" IsClosed="True">
                                                        <PathFigure.Segments>
                                                            <ArcSegment Size="3,3" RotationAngle="0" IsLargeArc="True"
                                                                        SweepDirection="Clockwise" Point="6,7.5"/>
                                                            <ArcSegment Size="3,3" RotationAngle="0" IsLargeArc="True"
                                                                        SweepDirection="Clockwise" Point="6,1.5"/>
                                                        </PathFigure.Segments>
                                                    </PathFigure>
                                                    <PathFigure StartPoint="6,7.5">
                                                        <PathFigure.Segments>
                                                            <LineSegment Point="6,11"/>
                                                        </PathFigure.Segments>
                                                    </PathFigure>
                                                </PathGeometry.Figures>
                                            </PathGeometry>
                                        </Path.Data>
                                    </Path>
                                    <Path Width="12" Height="12" Margin="0,0,3,0"
                                          VerticalAlignment="Center"
                                          Stroke="#FF808080" StrokeThickness="1.5"
                                          StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                                          StrokeLineJoin="Round"
                                          Visibility="{Binding IsMuted, Converter={StaticResource BoolToVisibility}}">
                                        <!-- IconMute -->
                                        <Path.Data>
                                            <PathGeometry>
                                                <PathGeometry.Figures>
                                                    <PathFigure StartPoint="1,4.5" IsClosed="True">
                                                        <PathFigure.Segments>
                                                            <PolyLineSegment Points="3.5,4.5 6,2 6,10 3.5,7.5 1,7.5"/>
                                                        </PathFigure.Segments>
                                                    </PathFigure>
                                                    <PathFigure StartPoint="7.5,3.5">
                                                        <PathFigure.Segments>
                                                            <LineSegment Point="11,8.5"/>
                                                        </PathFigure.Segments>
                                                    </PathFigure>
                                                </PathGeometry.Figures>
                                            </PathGeometry>
                                        </Path.Data>
                                    </Path>
                                    <TextBlock Text="{Binding LastMessageTime}" Foreground="#FF808080" FontSize="12"
                                               VerticalAlignment="Center"
                                               HorizontalAlignment="Right"/>
                                </StackPanel>
                                <Border MinWidth="20" Height="20"
                                        CornerRadius="10"
                                        Background="{StaticResource WhatsAppAccentBrush}"
                                        HorizontalAlignment="Right"
                                        Margin="0,4,0,0"
                                        Visibility="{Binding UnreadCount, Converter={StaticResource UnreadToVisibility}}">
                                    <TextBlock Text="{Binding UnreadCount}" Foreground="White" FontSize="11"
                                               FontWeight="Bold" HorizontalAlignment="Center"
                                               VerticalAlignment="Center"/>
                                </Border>
                            </StackPanel>
```

- [x] **Step 2: Check the icons**

Run: `node tools/check-icons.js`
Expected: `OK: 22 inline icon Path(s), 13 distinct icon(s).`

- [x] **Step 3: Render them and look at them**

Run: `node tools/check-icons.js --preview`
Expected: an `== IconPin ==` and an `== IconMute ==` ASCII block. The pin must read as a circle with a stem, the mute as a speaker with a slash. If a shape is unreadable at this size, change the coordinates in the markup above and run this command again - do not change the guard.

- [x] **Step 4: Build on the VM**

Run the three build commands from the Global Constraints.
Expected: `COPIA=0`, then `Your package has been successfully created.`

- [x] **Step 5: Commit and push**

```bash
git add WhatsappApp/Pages/ChatsPage.xaml
git commit -m "$(cat <<'EOF'
feat: show the pin and the silence on the chat row

A decision the row does not show is a decision the user cannot find again:
the two small marks sit next to the time, where WhatsApp puts them.
EOF
)"
git push
```

---

### Task 4: The three dots at the top left, and the pin picker

**Files:**
- Modify: `WhatsappApp/Pages/ChatsPage.xaml` (the title bar)
- Modify: `WhatsappApp/Pages/ChatsPage.xaml.cs`
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`
- Modify: `tools/check-actions.js` (`TITLE_BAR`)

**Interfaces:**
- Consumes: `DataService.SetPinned(string, bool)` (Task 2), `Contact.IsPinned` (Task 2), `Loc.Get`, `ToolTipService`, `AutomationProperties`, `XamlReader.Load` (all already used in this constructor).
- Produces: `MoreButton` in the title bar; nothing else depends on this task.

**Why a menu and not three buttons.** The title bar has room for one more icon-only button, and two icon-only buttons side by side are one edited line away from being swapped - which is exactly what `check-actions.js` exists to catch. `MoreButton` draws `IconOverflow` (three dots) and is declared in `TITLE_BAR` so the guard watches it like the other two.

- [x] **Step 1: Add the strings to both files**

In `WhatsappApp/Strings/en-US/Resources.resw`, before `</root>`:

```xml
  <data name="ChatsPage_MoreTooltip" xml:space="preserve">
    <value>More</value>
  </data>
  <data name="ChatsPage_PinChat" xml:space="preserve">
    <value>Pin a chat</value>
  </data>
  <data name="ChatsPage_UnpinAll" xml:space="preserve">
    <value>Unpin all</value>
  </data>
  <data name="ChatsPage_PinTitle" xml:space="preserve">
    <value>Pin chats</value>
  </data>
  <data name="ChatsPage_PinHint" xml:space="preserve">
    <value>Pinned chats stay at the top of the list, on this phone.</value>
  </data>
  <data name="ChatsPage_PinDone" xml:space="preserve">
    <value>Done</value>
  </data>
  <data name="ChatsPage_Cancel" xml:space="preserve">
    <value>Cancel</value>
  </data>
```

In `WhatsappApp/Strings/it-IT/Resources.resw`, the same seven keys in the same position, in Italian:

```xml
  <data name="ChatsPage_MoreTooltip" xml:space="preserve">
    <value>Altro</value>
  </data>
  <data name="ChatsPage_PinChat" xml:space="preserve">
    <value>Fissa una chat</value>
  </data>
  <data name="ChatsPage_UnpinAll" xml:space="preserve">
    <value>Togli tutte le chat fissate</value>
  </data>
  <data name="ChatsPage_PinTitle" xml:space="preserve">
    <value>Chat fissate</value>
  </data>
  <data name="ChatsPage_PinHint" xml:space="preserve">
    <value>Le chat fissate restano in cima all'elenco, su questo telefono.</value>
  </data>
  <data name="ChatsPage_PinDone" xml:space="preserve">
    <value>Fatto</value>
  </data>
  <data name="ChatsPage_Cancel" xml:space="preserve">
    <value>Annulla</value>
  </data>
```

- [x] **Step 2: Put the button in the title bar**

In `WhatsappApp/Pages/ChatsPage.xaml`, replace the whole title-bar `<Grid Grid.Row="0" ...>` block with:

```xml
        <!-- Title bar -->
        <Grid Grid.Row="0" Background="{StaticResource WhatsAppHeaderBrush}" Height="56">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>

            <!-- I tre puntini stanno a sinistra come chiesto: le azioni che
                 valgono per tutto l'elenco, e non per una riga. -->
            <Button x:Name="MoreButton" Grid.Column="0"
                    Background="Transparent" BorderThickness="0" Padding="0"
                    Width="48" Height="48" Margin="4,0,0,0" Click="MoreButton_Click">
                <Path Stroke="White" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24">
                    <!-- IconOverflow -->
                    <Path.Data>
                        <PathGeometry>
                            <PathGeometry.Figures>
                                <PathFigure StartPoint="6,10.5" IsClosed="True">
                                    <PathFigure.Segments>
                                        <ArcSegment Size="1.5,1.5" RotationAngle="0" IsLargeArc="True"
                                                    SweepDirection="Clockwise" Point="6,13.5"/>
                                        <ArcSegment Size="1.5,1.5" RotationAngle="0" IsLargeArc="True"
                                                    SweepDirection="Clockwise" Point="6,10.5"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                                <PathFigure StartPoint="12,10.5" IsClosed="True">
                                    <PathFigure.Segments>
                                        <ArcSegment Size="1.5,1.5" RotationAngle="0" IsLargeArc="True"
                                                    SweepDirection="Clockwise" Point="12,13.5"/>
                                        <ArcSegment Size="1.5,1.5" RotationAngle="0" IsLargeArc="True"
                                                    SweepDirection="Clockwise" Point="12,10.5"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                                <PathFigure StartPoint="18,10.5" IsClosed="True">
                                    <PathFigure.Segments>
                                        <ArcSegment Size="1.5,1.5" RotationAngle="0" IsLargeArc="True"
                                                    SweepDirection="Clockwise" Point="18,13.5"/>
                                        <ArcSegment Size="1.5,1.5" RotationAngle="0" IsLargeArc="True"
                                                    SweepDirection="Clockwise" Point="18,10.5"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                            </PathGeometry.Figures>
                        </PathGeometry>
                    </Path.Data>
                </Path>
            </Button>

            <TextBlock Grid.Column="1" Text="WhatsApp"
                       Foreground="White" FontSize="24" FontWeight="SemiBold"
                       VerticalAlignment="Center" Margin="4,0,0,4"/>

            <!-- I due pulsanti non si toccano: con il margine a 8 un dito
                 appoggiato sul bordo non cade sull'azione accanto. -->
            <Button x:Name="NewChatButton" Grid.Column="2"
                    Background="Transparent" BorderThickness="0" Padding="0"
                    Width="48" Height="48" Margin="0,0,8,0" Click="NewChatButton_Click">
                <Path Stroke="White" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24">
                    <!-- IconNewChat -->
                    <Path.Data>
                        <PathGeometry>
                            <PathGeometry.Figures>
                                <PathFigure StartPoint="4,5" IsClosed="True">
                                    <PathFigure.Segments>
                                        <PolyLineSegment Points="20,5 20,15.5 10.5,15.5 5.5,20 5.5,15.5 4,15.5"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                                <PathFigure StartPoint="12,7.6">
                                    <PathFigure.Segments>
                                        <LineSegment Point="12,13"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                                <PathFigure StartPoint="9.3,10.3">
                                    <PathFigure.Segments>
                                        <LineSegment Point="14.7,10.3"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                            </PathGeometry.Figures>
                        </PathGeometry>
                    </Path.Data>
                </Path>
            </Button>

            <Button x:Name="SettingsButton" Grid.Column="3"
                    Background="Transparent" BorderThickness="0" Padding="0"
                    Width="48" Height="48" Margin="0,0,4,0" Click="SettingsButton_Click">
                <Path Stroke="White" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24">
                    <!-- IconSettings -->
                    <Path.Data>
                        <PathGeometry>
                            <PathGeometry.Figures>
                                <PathFigure StartPoint="4,6.5">
                                    <PathFigure.Segments>
                                        <LineSegment Point="20,6.5"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                                <PathFigure StartPoint="4,12">
                                    <PathFigure.Segments>
                                        <LineSegment Point="20,12"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                                <PathFigure StartPoint="4,17.5">
                                    <PathFigure.Segments>
                                        <LineSegment Point="20,17.5"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                                <PathFigure StartPoint="9,4.5">
                                    <PathFigure.Segments>
                                        <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="True"
                                                    SweepDirection="Clockwise" Point="9,8.5"/>
                                        <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="True"
                                                    SweepDirection="Clockwise" Point="9,4.5"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                                <PathFigure StartPoint="15,10">
                                    <PathFigure.Segments>
                                        <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="True"
                                                    SweepDirection="Clockwise" Point="15,14"/>
                                        <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="True"
                                                    SweepDirection="Clockwise" Point="15,10"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                                <PathFigure StartPoint="9,15.5">
                                    <PathFigure.Segments>
                                        <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="True"
                                                    SweepDirection="Clockwise" Point="9,19.5"/>
                                        <ArcSegment Size="2,2" RotationAngle="0" IsLargeArc="True"
                                                    SweepDirection="Clockwise" Point="9,15.5"/>
                                    </PathFigure.Segments>
                                </PathFigure>
                            </PathGeometry.Figures>
                        </PathGeometry>
                    </Path.Data>
                </Path>
            </Button>
        </Grid>
```

- [x] **Step 3: Declare it in the actions guard**

In `tools/check-actions.js`, replace the `TITLE_BAR` constant with:

```js
const TITLE_BAR = {
  'Pages/ChatsPage.xaml': {
    MoreButton: 'IconOverflow',
    NewChatButton: 'IconNewChat',
    SettingsButton: 'IconSettings'
  }
};
```

- [x] **Step 4: Run the two guards**

Run: `node tools/check-actions.js && node tools/check-icons.js`
Expected: `OK: 20 button(s), name/handler/icon agree.` and `OK: 23 inline icon Path(s), 14 distinct icon(s).`

- [x] **Step 5: Name the button in the constructor**

In `WhatsappApp/Pages/ChatsPage.xaml.cs`, after the two existing `AutomationProperties.SetName` calls:

```csharp
            ToolTipService.SetToolTip(MoreButton, Loc.Get("ChatsPage_MoreTooltip", "More"));
            AutomationProperties.SetName(MoreButton, Loc.Get("ChatsPage_MoreTooltip", "More"));
```

- [x] **Step 6: Write the menu and the picker**

In `WhatsappApp/Pages/ChatsPage.xaml.cs`, add `using System.Collections.Generic;` and `using Windows.UI.Input;` to the imports, then add these members next to `SettingsButton_Click`:

```csharp
        /// <summary>
        /// Il menu dei tre puntini: quello che vale per l'elenco intero. Le
        /// azioni di una singola chat non stanno qui, perche' qui non c'e' una
        /// riga - stanno nella pressione prolungata (ChatRow_Holding).
        /// </summary>
        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            var pinItem = new MenuFlyoutItem { Text = Loc.Get("ChatsPage_PinChat", "Pin a chat") };
            pinItem.Click += PinChatMenuItem_Click;
            flyout.Items.Add(pinItem);

            var unpinItem = new MenuFlyoutItem { Text = Loc.Get("ChatsPage_UnpinAll", "Unpin all") };
            unpinItem.Click += UnpinAllMenuItem_Click;
            flyout.Items.Add(unpinItem);

            flyout.ShowAt(MoreButton);
        }

        private async void PinChatMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await ShowPinPickerAsync();
        }

        private void UnpinAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var rows = SnapshotContacts();
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].IsPinned) DataService.Instance.SetPinned(rows[i].Id, false);
            }
        }

        /// <summary>
        /// Le righe come sono adesso, in una lista nostra. Serve perche'
        /// SetPinned sposta le righe (le fissate tornano in cima): un ciclo che
        /// legge DataService.Contacts mentre quella stessa collezione si muove
        /// salterebbe delle righe.
        /// </summary>
        private static List<Contact> SnapshotContacts()
        {
            var rows = new List<Contact>();
            for (int i = 0; i < DataService.Instance.Contacts.Count; i++)
            {
                var contact = DataService.Instance.Contacts[i];
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;
                rows.Add(contact);
            }
            return rows;
        }

        /// <summary>
        /// Una o piu' chat da fissare, con le fissate gia' scelte: il menu dice
        /// cosa cambiare, non fa ricominciare da zero.
        /// </summary>
        private async Task ShowPinPickerAsync()
        {
            var rows = SnapshotContacts();

            var list = new ListView
            {
                ItemsSource = DataService.Instance.Contacts,
                SelectionMode = ListViewSelectionMode.Multiple,
                Height = 320
            };
            list.ItemTemplate = (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "<TextBlock Text=\"{Binding Name}\" Foreground=\"Black\" FontSize=\"16\" Margin=\"0,8,0,8\"/>" +
                "</DataTemplate>");

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].IsPinned) list.SelectedItems.Add(rows[i]);
            }

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = Loc.Get("ChatsPage_PinHint", "Pinned chats stay at the top of the list, on this phone."),
                Foreground = new SolidColorBrush(Colors.Gray),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            content.Children.Add(list);

            var dialog = new ContentDialog
            {
                Title = Loc.Get("ChatsPage_PinTitle", "Pin chats"),
                Content = content,
                PrimaryButtonText = Loc.Get("ChatsPage_PinDone", "Done"),
                SecondaryButtonText = Loc.Get("ChatsPage_Cancel", "Cancel")
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            for (int i = 0; i < rows.Count; i++)
            {
                DataService.Instance.SetPinned(rows[i].Id, list.SelectedItems.Contains(rows[i]));
            }
        }
```

- [x] **Step 7: Run the guards and the build**

Run: `node tools/check-csharp5.js && node tools/check-resw.js --strict && node tools/check-actions.js`
Expected: `OK: 36 C# file(s) are C# 5 compatible.`, `OK: 117 key(s) in en-US and it-IT, every x:Uid and Loc.Get lookup resolved.`, `OK: 20 button(s), name/handler/icon agree.`

Then run the three build commands from the Global Constraints.
Expected: `COPIA=0`, then `Your package has been successfully created.`

- [x] **Step 8: Commit and push**

```bash
git add WhatsappApp/Pages/ChatsPage.xaml WhatsappApp/Pages/ChatsPage.xaml.cs WhatsappApp/Strings tools/check-actions.js
git commit -m "$(cat <<'EOF'
feat: a three-dots menu at the top left, with pinning

Pinning is a property of a conversation but the choice is made for one or more
rows at a time, so the menu opens a multi-select picker with the pinned chats
already ticked. The button is declared in check-actions' title bar table like
the other two: it sits next to them and draws its own icon.
EOF
)"
git push
```

---

### Task 5: Holding a row: pin, silence, delete

**Files:**
- Modify: `WhatsappApp/Pages/ChatsPage.xaml` (the row template root)
- Modify: `WhatsappApp/Pages/ChatsPage.xaml.cs`
- Modify: `WhatsappApp/Services/DataService.cs` (`DeleteChat`)
- Modify: `WhatsappApp/Services/MessageCache.cs` (`DeleteAsync`)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes: `ChatPreferences.Hide(string)` (Task 2), `DataService.SetPinned` / `SetMuted` (Task 2), `DataService.TotalUnread()`, `FindContact`, `_contacts`, `_contactIndex`, `_chatMessages`, `_historyRequested`, `MessageCache.FileNameFor(string)`, `ChatMessage.Id`.
- Produces: `DataService.DeleteChat(string chatId) -> void`, `MessageCache.DeleteAsync(string chatId) -> Task`.

- [x] **Step 1: Add the eight strings**

In `WhatsappApp/Strings/en-US/Resources.resw`, before `</root>`:

```xml
  <data name="ChatsPage_Pin" xml:space="preserve">
    <value>Pin</value>
  </data>
  <data name="ChatsPage_Unpin" xml:space="preserve">
    <value>Unpin</value>
  </data>
  <data name="ChatsPage_Mute" xml:space="preserve">
    <value>Mute</value>
  </data>
  <data name="ChatsPage_Unmute" xml:space="preserve">
    <value>Unmute</value>
  </data>
  <data name="ChatsPage_Delete" xml:space="preserve">
    <value>Delete chat</value>
  </data>
  <data name="ChatsPage_DeleteTitle" xml:space="preserve">
    <value>Delete this chat?</value>
  </data>
  <data name="ChatsPage_DeleteBody" xml:space="preserve">
    <value>It disappears from this phone. Nothing is deleted from WhatsApp, and a new message brings it back.</value>
  </data>
  <data name="ChatsPage_DeleteConfirm" xml:space="preserve">
    <value>Delete</value>
  </data>
```

In `WhatsappApp/Strings/it-IT/Resources.resw`, the same eight keys in the same position:

```xml
  <data name="ChatsPage_Pin" xml:space="preserve">
    <value>Fissa</value>
  </data>
  <data name="ChatsPage_Unpin" xml:space="preserve">
    <value>Togli il fissaggio</value>
  </data>
  <data name="ChatsPage_Mute" xml:space="preserve">
    <value>Silenzia</value>
  </data>
  <data name="ChatsPage_Unmute" xml:space="preserve">
    <value>Togli il silenzio</value>
  </data>
  <data name="ChatsPage_Delete" xml:space="preserve">
    <value>Elimina chat</value>
  </data>
  <data name="ChatsPage_DeleteTitle" xml:space="preserve">
    <value>Eliminare questa chat?</value>
  </data>
  <data name="ChatsPage_DeleteBody" xml:space="preserve">
    <value>Sparisce da questo telefono. Da WhatsApp non viene eliminato niente, e un messaggio nuovo la fa tornare.</value>
  </data>
  <data name="ChatsPage_DeleteConfirm" xml:space="preserve">
    <value>Elimina</value>
  </data>
```

- [x] **Step 2: Let the row receive the hold**

In `WhatsappApp/Pages/ChatsPage.xaml`, the root of the row template is:

```xml
                        <Grid Height="72" Background="White">
```

Change it to:

```xml
                        <Grid Height="72" Background="White" Holding="ChatRow_Holding">
```

- [x] **Step 3: Delete the cached conversation**

In `WhatsappApp/Services/MessageCache.cs`, add this method after `SaveAsync`:

```csharp
        /// <summary>
        /// Butta la copia di una chat. La chiama l'eliminazione: senza questa, la
        /// conversazione cancellata tornerebbe a schermo al primo apri-e-chiudi,
        /// perche' DataService legge la cache quando la lista in memoria e'
        /// vuota. Mai un'eccezione: un file che non c'e' non e' un guasto.
        /// </summary>
        public static async Task DeleteAsync(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileNameFor(chatId));
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception ex)
            {
                Diag.Failed("MessageCache.Delete", ex);
            }
        }
```

- [x] **Step 4: Delete the chat from this phone**

In `WhatsappApp/Services/DataService.cs`, add this method next to `ClearUnread`:

```csharp
        /// <summary>
        /// Elimina una chat da questo telefono: la riga, i messaggi, la copia su
        /// disco e - per ultimo, perche' e' quello che dura - la decisione in
        /// ChatPreferences, che la tiene fuori dal prossimo elenco del server.
        ///
        /// Non si tocca niente su WhatsApp: non c'e' un endpoint, e un
        /// "elimina" che cancella la conversazione anche per l'altra parte
        /// sarebbe una cosa diversa da quella che chiede l'utente.
        ///
        /// Un messaggio nuovo la fa tornare (OnNetworkMessageReceived), come fa
        /// WhatsApp: una chat cancellata per sbaglio non resta persa.
        /// </summary>
        public void DeleteChat(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return;

            var contact = FindContact(chatId);
            if (contact != null)
            {
                _contacts.Remove(contact);
                _contactIndex.Remove(chatId);
            }

            _chatMessages.Remove(chatId);
            _historyRequested.Remove(chatId);

            ChatPreferences.Hide(chatId);
            NotificationService.SetUnread(TotalUnread());

#pragma warning disable 4014
            MessageCache.DeleteAsync(chatId);
#pragma warning restore 4014
        }
```

- [x] **Step 5: Write the menu and the confirmation**

In `WhatsappApp/Pages/ChatsPage.xaml.cs`, add these members after `ShowPinPickerAsync`:

```csharp
        /// <summary>
        /// Tenere premuta una riga apre le azioni di quella chat.
        ///
        /// Si guarda HoldingState: un tocco prolungato ne alza due, e senza
        /// questo controllo il menu si aprirebbe anche quando il dito si alza.
        /// </summary>
        private void ChatRow_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started) return;

            var row = sender as FrameworkElement;
            if (row == null) return;

            var contact = row.DataContext as Contact;
            if (contact == null || string.IsNullOrEmpty(contact.Id)) return;

            e.Handled = true;
            ShowChatMenu(row, contact);
        }

        /// <summary>
        /// Pin, silenzio ed eliminazione di una riga. L'id e lo stato si
        /// catturano adesso e non si rileggono nel gestore: quando si tocca la
        /// voce, la riga puo' essere gia' stata rimossa (eliminazione) e il suo
        /// DataContext non e' piu' quello che il menu mostra.
        /// </summary>
        private void ShowChatMenu(FrameworkElement row, Contact contact)
        {
            string id = contact.Id;
            bool pinned = contact.IsPinned;
            bool muted = contact.IsMuted;

            var flyout = new MenuFlyout();

            var pin = new MenuFlyoutItem
            {
                Text = pinned
                    ? Loc.Get("ChatsPage_Unpin", "Unpin")
                    : Loc.Get("ChatsPage_Pin", "Pin")
            };
            pin.Click += delegate { DataService.Instance.SetPinned(id, !pinned); };
            flyout.Items.Add(pin);

            var mute = new MenuFlyoutItem
            {
                Text = muted
                    ? Loc.Get("ChatsPage_Unmute", "Unmute")
                    : Loc.Get("ChatsPage_Mute", "Mute")
            };
            mute.Click += delegate { DataService.Instance.SetMuted(id, !muted); };
            flyout.Items.Add(mute);

            var remove = new MenuFlyoutItem { Text = Loc.Get("ChatsPage_Delete", "Delete chat") };
            remove.Click += delegate { ConfirmDeleteAsync(id); };
            flyout.Items.Add(remove);

            flyout.ShowAt(row);
        }

        /// <summary>
        /// Eliminare e' l'unica azione del menu che non si puo' disfare con un
        /// altro tocco, e un dito appoggiato a lungo e' anche un dito che
        /// scivolava: la domanda vale una dialog.
        /// </summary>
        private async Task ConfirmDeleteAsync(string chatId)
        {
            var dialog = new ContentDialog
            {
                Title = Loc.Get("ChatsPage_DeleteTitle", "Delete this chat?"),
                Content = new TextBlock
                {
                    Text = Loc.Get("ChatsPage_DeleteBody",
                        "It disappears from this phone. Nothing is deleted from WhatsApp, and a new message brings it back."),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = Loc.Get("ChatsPage_DeleteConfirm", "Delete"),
                SecondaryButtonText = Loc.Get("ChatsPage_Cancel", "Cancel")
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            DataService.Instance.DeleteChat(chatId);
        }
```

- [x] **Step 6: Run the guards**

Run the fast gate from the Global Constraints.
Expected: `OK: 36 C# file(s) are C# 5 compatible.`, `OK: 23 inline icon Path(s), 14 distinct icon(s).`, `OK: 125 key(s) in en-US and it-IT, every x:Uid and Loc.Get lookup resolved.`, `OK: 20 button(s), name/handler/icon agree.`, `OK: every decoded bitmap asks for the width it is shown at.`, all tool tests passing.

- [x] **Step 7: Build on the VM**

Run the three build commands from the Global Constraints.
Expected: `COPIA=0`, then `Your package has been successfully created.`

If the XAML compiler rejects `Holding` (it is a WP8.1 `UIElement` event, not a Windows 8.1 one), record the exact error text in this plan's `## What execution changed about this plan` section and replace the handler signature with `RightTapped` + `RightTappedRoutedEventArgs`, which WP8.1 also raises for a long press. Do not add a second gesture path.

- [x] **Step 8: Commit and push**

```bash
git add WhatsappApp/Pages/ChatsPage.xaml WhatsappApp/Pages/ChatsPage.xaml.cs WhatsappApp/Services/DataService.cs WhatsappApp/Services/MessageCache.cs WhatsappApp/Strings
git commit -m "$(cat <<'EOF'
feat: hold a chat to pin, mute or delete it

Deleting removes the row, the messages and the cached copy on this phone, and
remembers the chat as hidden so the next chat list does not bring it back. It
is not deleted on WhatsApp, and the confirmation says so; a new message makes
it reappear, which is what WhatsApp does.
EOF
)"
git push
```

---

### Task 6: The docs, the skills, and the rules the user asked for

**Files:**
- Modify: `README.md`, `README.it.md`
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Modify: `.agents/skills/README.md`
- Modify: `.agents/skills/test-the-app/SKILL.md`

**Interfaces:**
- Consumes: everything the earlier tasks produced, as text.
- Produces: no code.

- [x] **Step 1: The project README pair**

In `README.md`, in **Features**, the bullet that starts `- Lists the account's real **conversations** from \`GET /chats\`` - change `, each with its last message and, for people, the **profile picture**` into `, each with its last message and its **profile picture**`, and change the parenthesis `(two requests per person: the endpoint returns the picture's address, not the picture)` into `(two requests per chat, a group included: the endpoint returns the picture's address, not the picture)`.

Immediately after that bullet, insert:

```markdown
- Chats can be **pinned** (the three-dots menu at the top left, or a long press on a row), **muted** (no notification for that conversation; the unread number stays) and **deleted from this phone** (a long press, then Delete). The three are decisions of this phone: the server has no endpoint for any of them, so they are kept in the app's own folder and a chat pinned here is not pinned on your other devices
- A deleted chat comes back when a new message arrives in it, and nothing is deleted on WhatsApp: this app deletes the conversation from this phone, not from the account
```

In `README.it.md`, in **Funzionalita'**, the mirror bullet (it starts with the same words, `- Elenca le **conversazioni** vere dell'account da \`GET /chats\``): change `e, per le persone, l'**immagine del profilo**` into `e la sua **immagine del profilo**`, and the parenthesis `(due richieste per persona: l'endpoint restituisce l'indirizzo dell'immagine, non l'immagine)` into `(due richieste per chat, gruppi compresi: l'endpoint restituisce l'indirizzo dell'immagine, non l'immagine)`. Immediately after it insert:

```markdown
- Le chat si possono **fissare** (il menu dei tre puntini in alto a sinistra, o una pressione prolungata su una riga), **silenziare** (nessun avviso per quella conversazione; il numero dei non letti resta) ed **eliminare da questo telefono** (pressione prolungata, poi Elimina). Sono tre decisioni di questo telefono: il server non ha un endpoint per nessuna delle tre, quindi vivono nella cartella dell'app, e una chat fissata qui non e' fissata sugli altri dispositivi
- Una chat eliminata torna quando ci arriva un messaggio nuovo, e su WhatsApp non viene cancellato niente: questa app elimina la conversazione da questo telefono, non dall'account
```

- [x] **Step 2: The limits, in both languages**

In `README.md`, in **Limitations**, after the bullet about the unread number, insert:

```markdown
- Pinning, muting and deleting live on this phone only, in `chat-preferences.json` in the app's folder. Nothing about them is sent to WhatsApp or to the adapter, so another device does not see them, and they are lost when the app's data is cleared.
```

In `README.it.md`, in **Limiti**, after the bullet about the unread number ("Il numero dei non letti di una chat lo tiene l'adapter..."), insert the same statement:

```markdown
- Fissare, silenziare ed eliminare vivono solo su questo telefono, in `chat-preferences.json` nella cartella dell'app. Niente di tutto questo viene mandato a WhatsApp o all'adapter, quindi un altro dispositivo non lo vede, e si perde quando si cancellano i dati dell'app.
```

- [x] **Step 3: The skill gotchas**

In `.agents/skills/maintain-the-app/SKILL.md`, in **Known gotchas**, append:

```markdown
- **A pin, a silence and a deletion are this phone's, and the file that holds them
  is not the chat cache.** `ChatCache` is a photograph the server replaces row by
  row, so a decision kept there would be gone at the next `chats` reply and a chat
  that `CHATS_LIMIT` no longer lists would lose it. `ChatPreferences` is the other
  thing: `chat-preferences.json` in `LocalFolder`, read once at start-up (before
  the first row is built, in `DataService.LoadCachedChatsAsync`) and written by the
  caller that changes it. `Hidden` keeps a deleted chat out of `ApplyChat` and
  `ApplyContact`; a live incoming message calls `Reveal`, which is what makes a
  chat deleted by mistake come back - and is why nothing here is a one-way door.
  Muting suppresses the toast only: the unread count is still true.
- **A pinned chat is moved, not sorted.** `DataService.ResortContacts` walks the
  collection and moves each pinned row in front of the first row that is not
  pinned, so the rows keep the recency order the collection already had. Every
  path that moves a row to the top (`OnNetworkMessageReceived`, `AddMessage`) must
  call it afterwards, or an arriving message in an unpinned chat jumps over the
  pinned ones.
- **`Holding` fires twice.** `ChatRow_Holding` returns unless
  `e.HoldingState == HoldingState.Started`; without that test the menu is built
  again on the release, and a tap on a menu item that lands on the row re-opens it.
- **The row's menu captures the id and the state before it is shown.** By the time
  a menu item is tapped the row may already be gone (delete), so the handlers close
  over the values the menu was built from instead of reading `contact` again. The
  loops that apply a pin snapshot `DataService.Contacts` first
  (`SnapshotContacts`): `SetPinned` moves rows inside that same collection.
```

- [x] **Step 4: The rules the user asked for**

In `.agents/skills/maintain-the-app/SKILL.md`, in **Workflow for any change**, insert a new step 2 (and renumber the steps after it, so `git status --short` stays step 1 and **Push** becomes step 8):

```markdown
2. **A plan is written to be executed now.** When a change is multi-step enough to
   deserve one, write it to `docs/superpowers/plans/YYYY-MM-DD-<name>.md` and then
   execute it in the same session, task by task, until the last task is committed
   and pushed. The document is the record of the work, never the deliverable:
   stopping at the plan leaves the change undone and ships a description of code
   that does not exist. While executing, append a
   `## What execution changed about this plan` section at the end of the plan
   listing every divergence, and do not rewrite the tasks above it.
```

In `.agents/skills/README.md`, in the list under *Come mantenere, aggiornare e testare l'app, in breve*, after the **Aggiornare** bullet, insert:

```markdown
- **Pianificare**: un piano in `docs/superpowers/plans/` si scrive per eseguirlo
  subito, in questa stessa sessione e task per task, fino all'ultimo commit
  pushato. Il documento e' il resoconto del lavoro, non il risultato: fermarsi al
  piano vuol dire lasciare il lavoro non fatto e spedire la descrizione di un
  codice che non esiste.
```

**Added mid-session, at the user's request (see the execution section below): the
second rule.** A change to the adapter is not finished until the Docker repository
has it too. It is a new section in `maintain-the-app` (plus a row in *Where a
change belongs* and a sentence in step 8 of the workflow), and a sentence in the
**Aggiornare** bullet of `.agents/skills/README.md`:

```markdown
## The Docker repository

`WhatsappBridge/` is the source of truth for the adapter; the repository
`vincenzosco/docker-whatsappforwp` holds a **copy** of it in `server/`, plus the
`Dockerfile` that puts GOWA and the adapter in one image. That image is what most
people run, so an adapter change that stops at this repository is a change half
the users never get. **Every commit that touches `WhatsappBridge/` ends with the
same commit mirrored into the Docker repository.**

The mirror is not a patch. `tools/sync.js` **in the Docker repository** reads the
list of adapter files out of this checkout (its `adapterFiles`), so a new module
cannot be forgotten, and writes `server/SOURCE_COMMIT` with the commit the copy
came from.

```bash
# 1. here: the adapter change, its tests, the commit, the push
cd WhatsappBridge && npm test
git push

# 2. in a checkout of the Docker repository
cd /tmp/docker-whatsappforwp
git pull
node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP
node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP  # OK: server/ matches
(cd server && npm test)                                                    # same count as step 1
git add -A && git commit -m "fix: ..." && git push
```

What makes it *pass*: the `sync` job of `.github/workflows/image.yml` checks this
repository out at the commit `server/SOURCE_COMMIT` names, runs
`node tools/sync.js --check --from ../app`, and fails the build if the copy no
longer matches. A mirrored commit that missed a file therefore turns the image
build red instead of shipping an old adapter. After the push, the workflow builds
linux/amd64 and linux/arm64 and publishes
`ghcr.io/vincenzosco/docker-whatsappforwp:latest`.

Two things the copy cannot carry, because they belong to the container and not to
the adapter:

- **An external program.** The runtime stage of the `Dockerfile` installs
  `ffmpeg`, which the adapter calls for Ogg/Opus voice notes; the Node.js path in
  the Docker README tells the reader to install it by hand. Anything new the
  adapter starts executing needs the same treatment, or it is present when run
  from a checkout and missing in the image most people use.
- **A new environment variable**, in `.env.example` and in **both** Docker README
  tables, or it is an option nobody can set. Both compose files pass `.env`
  through with `env_file`, so there is nothing else to wire.
```

and, in the README's *Aggiornare* bullet:

```markdown
  finche' non e' su `origin/master`. E un commit che tocca `WhatsappBridge/` non
  e' finito nemmeno li': lo stesso commit va portato nel repository Docker
  `docker-whatsappforwp` con il suo `tools/sync.js`, perche' l'immagine e' quello
  che gira sulla maggior parte delle installazioni (vedi *The Docker repository*
  in `maintain-the-app`, che spiega anche come farlo passare in CI).
```

The mirror itself was executed in the same pass, in a checkout of the Docker
repository: `tools/sync.js` reads the file list from this repository instead of a
hand-kept one, `server/` is re-synced from `4f2cc0f`, `ffmpeg` is installed by the
image, the variables the adapter reads are in `.env.example` and both READMEs, and
the two commits are pushed (`2d4b61f`, `bc9a80e`).

- [x] **Step 5: The on-device checks**

In `.agents/skills/test-the-app/SKILL.md`, after check 46, append:

```markdown
47. Chat list: a group row shows the group's **picture**, not just its initials.
    If every group shows initials while people show pictures, the adapter is still
    skipping `@g.us` in `chats.js` or cutting the JID in `gowa-client.js.avatar`;
    on the host, `DEBUG` in `.tools/gowa/gowa.log` shows the `/user/avatar` call.
48. Hold a row for about a second: a menu with Pin, Mute and Delete chat appears
    next to that row, and it does not appear again when the finger is lifted. Pin
    it and leave the list: the row is at the top with the pin mark, and it is still
    there after killing and reopening the app. Mute it: a message that arrives in
    that chat raises no toast, and its unread number still goes up.
49. The three-dots button at the top left opens a menu with Pin a chat and Unpin
    all. Pin a chat opens a list where the pinned chats are already ticked;
    ticking two and pressing Done puts both at the top, and Unpin all drops every
    pin. A long press on the button reads More.
50. Delete a chat from the row menu: the confirmation says it disappears from this
    phone, the row goes, and the thread is gone when the chat list refreshes.
    Have someone write in that chat: it comes back with the new message. Kill and
    reopen the app before that message: the deleted row stays deleted.
```

- [x] **Step 6: Run the docs guard and the fast gate**

Run: `node tools/check-docs.js && node tools/check-resw.js --strict`
Expected: `OK: 2 doc pair(s) in step (1 closed by "Disclosure"), no emoji in 37 .md file(s).` and `OK: 125 key(s) in en-US and it-IT, every x:Uid and Loc.Get lookup resolved.`

Then run the whole fast gate from the Global Constraints.

- [x] **Step 7: Mark the plan and record what execution changed**

Tick every finished step in this file (`- [ ]` becomes `- [x]`) and append, at the end of this file:

```markdown
## What execution changed about this plan

- ...
```

listing every place the code, a guard or the WP8.1 build forced a divergence, plus the final guard counts.

- [x] **Step 8: Commit and push**

```bash
git add README.md README.it.md .agents/skills docs/superpowers/plans/2026-09-28-chat-list-pin-mute-delete-and-group-pictures.md
git commit -m "$(cat <<'EOF'
docs: group pictures, pin/mute/delete, and execute the plan you write

The README pair says what the three decisions do and that they live on this
phone; maintain-the-app says where they are kept and why that is not the chat
cache, and now states the rule that a plan is executed in the same session it
is written.
EOF
)"
git push
```

---

## Self-Review

**Spec coverage:**

| The request | Where |
| --- | --- |
| "le immagini dei gruppi non compaiono" | Task 1 (adapter asks for a group's picture, verified against GOWA's `SanitizePhone` and `GetProfilePictureInfo`) |
| "in alto a sinistra, ci siano 3 puntini" | Task 4 (a new first column in the title bar, `IconOverflow`, declared in `check-actions`' title bar table) |
| "in essi, la possibilita' di pinnare una/piu' chat" | Task 4 (`Pin a chat` opens a multi-select picker with the pinned rows pre-ticked; `Unpin all`) plus Task 2 (`SetPinned`, ordering, persistence) and Task 3 (the mark on the row) |
| "tenendo premuto si abbiano delle opzioni" | Task 5 (`Holding` on the row template, `MenuFlyout` anchored to the row) |
| "per eliminare chat" | Task 5 (`DeleteChat`: row, messages, cache and the hidden decision; confirmation; returns on a new message) |
| "silenziare messaggi" | Task 2 (`SetMuted`, the toast gate) and Task 5 (the menu item) |
| "metti nella skill, di eseguire sempre il piano dopo averlo fatto inline" | Task 6, step 4 (a new step 2 in `maintain-the-app`'s workflow, and the same rule in `.agents/skills/README.md`) |
| "ogni volta che si aggiorna il server, va anche fatto quello docker, e spiega come aggiornarlo per farlo passare" | Task 6, step 4 (second half: *The Docker repository* in `maintain-the-app`, a row in *Where a change belongs*, and the sentence in `.agents/skills/README.md`), plus the two pushed commits in the Docker repository itself |
| "risolvi tutti i warning di compilazione" | Task 5, step 7 (the CS4014 fix in `4f2cc0f`) and step 6 of this task: the WP8.1 build reports `Avvisi: 0, Errori: 0` at `/p:WarningLevel=4` in Debug **and** Release |

**Placeholder scan:** every step that touches code carries the whole code, every command carries its expected output, and the two tasks with a WP8.1 risk (`Holding`, the XAML edit) name the exact fallback and where to record it instead of saying "handle errors".

**Type consistency:** `ChatPreferences.SetPinned/SetMuted/Hide/Reveal/LoadAsync` (Task 2) are the names used by `DataService` (Tasks 2 and 5); `DataService.SetPinned/SetMuted` (Task 2) are the names used by `ChatsPage` (Tasks 4 and 5); `DataService.DeleteChat` and `MessageCache.DeleteAsync` are defined and called in Task 5; `Contact.IsPinned` / `IsMuted` are defined in Task 2, bound in Task 3 and read by the menus in Task 5; the 15 new `.resw` keys are each read exactly once by a `Loc.Get` in the task that adds them (7 in Task 4, 8 in Task 5), which is what `--strict` requires.

## What execution changed about this plan

- **The plan gained a task mid-session, at the user's request.** The two new rules
  (a plan is executed in the session that writes it, and an adapter change is
  mirrored into the Docker repository) were asked for after Tasks 1-5 were
  committed, so they landed in Task 6, step 4 instead of in a task of their own.
  The WP8.1 warning sweep was asked for in the same message and is step 6.
- **The Docker repository work was not in the plan at all.** It turned out to be
  more than a copy: `tools/sync.js` there kept a hand-written `FILES` list, and
  `calls.js`, `chats.js` and `ffmpeg.js` had never been copied while `server.js`,
  `config.js`, `gowa-client.js` and `message-format.js` were several commits
  behind. The list is now read from this repository (`adapterFiles`), `--check`
  reports drift in both directions, `server/` is re-synced from `4f2cc0f` and its
  suite went from `pass 83, fail 4` to `pass 133, fail 0`. `ffmpeg` was added to
  the runtime stage of the image and the `CHATS_*`, `MESSAGES_LIMIT`, `CALLS_*`
  and `FFMPEG_*` variables to `.env.example` and both Docker READMEs - none of
  them were documented. Two commits, pushed (`2d4b61f`, `bc9a80e`).
- **`Holding` is in `Windows.UI.Xaml.Input`**, not `Windows.UI.Input`: the
  namespace from the plan is a CS0246, and `HoldingRoutedEventArgs` and
  `HoldingState` are in the WP8.1 `Windows.winmd`, so no fallback was needed. The
  gotcha now says so.
- **`/p:WarningLevel=4` is the only way to see a warning.** The WP8.1 targets pass
  `/warn:0` to the first `Csc` pass, so the summary reads `Avvisi: 0` whatever the
  source says; with the property the second pass is `/warn:4` and the count is real.
  Debug and Release, both platforms, end at `Avvisi: 0` and `Errori: 0`. The one
  warning left silenced by hand is CS0618 on `Windows.ApplicationModel.Contacts`
  (the `ContactInformation` API is the only one WP8.1 has, and the comment above
  the `#pragma` says so); a `#pragma warning disable 4014` remains wherever a call
  is deliberately fire-and-forget.
- **The delete confirmation needed an extra commit.** The handler the plan wrote
  was a plain `async` lambda, which is CS4014 at `/warn:4`; `4f2cc0f` awaits it and
  splits the dialog into `AskToDeleteAsync` (returns `bool`, catches its own
  exception) for the reason it was added.
- **One chore the plan did not have:** `WhatsappApp.csproj` came back from an edit
  as CRLF with a BOM. Same content, re-normalised to LF in `6d5b62f`.
- **The counts in `test-the-app` were stale** (32 C# files, 104 keys, 12 icons, 98
  adapter tests, `pass 70`) and were refreshed to what the guards actually report.
- **Final counts, all green:** 36 C# files, 23 inline icon Paths (14 distinct), 125
  keys in each `.resw`, 20 buttons, 40 tests in `tools/test`, 133 adapter tests, 2
  doc pairs with no emoji in 37 Markdown files, one frame ceiling of 8 MiB. WP8.1
  build: `Your package has been successfully created.` with `Avvisi: 0`,
  `Errori: 0`.
