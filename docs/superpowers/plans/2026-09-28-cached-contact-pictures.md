# Cached contact pictures Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** keep the contact pictures of the chat list on the phone and in the adapter, so the list shows faces instead of initials after a restart or after a memory spike.

**Architecture:** The app already has the bytes when it draws the picture, and throws them away twice. `ChatCache.Slim` leaves `AvatarData` out of `chats.json` on purpose (that file is read before the connection exists and must stay small), and `MemoryWatcher` drops the decoded `BitmapImage` of every row under pressure. So the same picture has to be fetched again - one HTTP request for the CDN address plus one for the bytes, per chat - and the row shows initials until it arrives. The fix is a third small file next to `chats.json` and `chat-preferences.json`: `avatar-cache.json`, written through the same `SerialQueue` that `ChatPreferences` uses and read once at startup, before the cached rows are applied. The adapter half is a 5-minute in-memory cache in `gowa-client.avatar`, because the chat list is re-read on every reconnection and after the adapter's own minute of cache.

**Tech Stack:** Windows Phone 8.1 WinRT/XAML, C# 5; Node.js 18+ (CommonJS, `node:test`) for the adapter; Node guard scripts in `tools/`.

## Global Constraints

- **C# 5 only** (`node tools/check-csharp5.js`): no `$"..."`, no `nameof`, no expression-bodied members, no `?.` on the left of an assignment.
- Colours, fonts: the WP8.1 theme only. No `Segoe MDL2 Assets`; an icon is an inline `Path` with an `<!-- IconX -->` comment.
- User-visible strings live in **both** `WhatsappApp/Strings/en-US/Resources.resw` and `WhatsappApp/Strings/it-IT/Resources.resw` (125 keys today, `--strict` fails on an unused one).
- Source comments are **Italian, no accented characters** (the phone's toolchain is not guaranteed to read UTF-8 in a comment); user strings carry accents normally.
- **Fast gate after every task:**
  `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"`, then `cd WhatsappBridge && npm test`.
- **Build gate after every task that touches the app**, on the Parallels VM `Windows 11` (retry the same MSBuild call once on exit 255 + `PrlJob_GetRetCode: Invalid argument`):
  1. `prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"`
  2. `prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"` → `COPIA=0`
  3. `prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m /p:WarningLevel=4"` → `Avvisi: 0`, `Errori: 0`, `Your package has been successfully created.`
- Every file is **LF, no BOM**. After any edit run:
  `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' <file>`
- **Every change under `WhatsappBridge/` also lands in the Docker repository** at `/tmp/docker-whatsappforwp`: `cd /tmp/docker-whatsappforwp && node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP`, then `node tools/sync.js --check`, then `cd server && npm test`, then commit and push to `main`. `server/SOURCE_COMMIT` records the app commit that was mirrored.
- Commit messages contain **no apostrophe**.
- Plans and docs carry **no emoji**, except U+26A0.
- A `Border` takes exactly one child (WMC0035): wrap two in a `<Grid>`.
- Current expected counts (the gate prints them): 37 C# files, 125 keys per `.resw`, 23 inline `Path` (14 distinct icons), 20 buttons + 1 Button style, 47 tests in `tools/test`, 136 adapter tests.

---

## File Structure

| File | Create/Modify | Responsibility |
| --- | --- | --- |
| `WhatsappApp/Services/AvatarCache.cs` | **Create** | The bytes of the chat-list pictures, on disk (`avatar-cache.json`): read once, bounded, written through a `SerialQueue`. |
| `WhatsappApp/WhatsappApp.csproj` | Modify | Register the new file as `<Compile>`. |
| `WhatsappApp/Services/DataService.cs` | Modify | Fill a row's picture from the cache when the adapter does not bring one; remember the ones it does; rebuild the decoded bitmaps on demand. |
| `WhatsappApp/Pages/ChatsPage.xaml.cs` | Modify | Ask for the pictures again when the chat list comes back to the front. |
| `tools/check-memory.js` | Modify | Three new rules: the row cache stays slim, the avatar cache is bounded and written through the queue, and the avatar cache is read before the cached rows are applied. |
| `tools/test/check-memory.test.js` | Modify | The tests of those three rules. |
| `WhatsappBridge/avatar-cache.js` | **Create** | A small TTL map: the pictures the adapter has already downloaded. |
| `WhatsappBridge/gowa-client.js` | Modify | `avatar(jid)` answers from that map before it asks GOWA. |
| `WhatsappBridge/test/avatar-cache.test.js` | **Create** | The map's tests: hit, expiry, a missing picture, the cap. |
| `WhatsappBridge/test/gowa-client.test.js` | Modify | The second `avatar()` of the same JID makes no request. |
| `WhatsappBridge/README.md` / `README.it.md` | Modify | Say that the adapter keeps a picture for a few minutes. |
| `README.md` / `README.it.md` | Modify | Say that the phone keeps the pictures, and what its limits are now. |
| `.agents/skills/maintain-the-app/SKILL.md` | Modify | The gotcha: the bytes of the pictures live in a cache of their own, and it is read before the rows. |
| `.agents/skills/test-the-app/SKILL.md` | Modify | The new counts, the `check-memory.js` row, and the on-device checks. |

---

### Task 1: The app keeps the contact pictures

**Files:**
- Create: `WhatsappApp/Services/AvatarCache.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (the `<Compile Include="Services\...">` list at lines 102-122)
- Modify: `WhatsappApp/Services/DataService.cs` (`LoadCachedChatsAsync`, `ApplyChat`)
- Modify: `tools/check-memory.js`
- Test: `tools/test/check-memory.test.js`

**Interfaces:**
- Consumes: `WhatsappApp/Services/SerialQueue.cs` - `Task RunAsync(Func<Task> work)`; `WhatsappApp/Services/Diag.cs` - `Diag.Failed(string where, Exception ex)`, `Diag.Ok(string message)`.
- Produces: `AvatarCache.LoadAsync()` → `Task`; `AvatarCache.Get(string chatId)` → `string` (null when nothing is held); `AvatarCache.Remember(string chatId, string base64)` → `void`; `AvatarCache.Count` → `int`; constants `MaxChats`, `MaxEntryChars`, `MaxTotalChars`.

- [x] **Step 1: Write the failing tests**

Add to the end of `tools/test/check-memory.test.js`:

```javascript
const CHAT_CACHE = 'WhatsappApp/Services/ChatCache.cs';
const AVATAR_CACHE = 'WhatsappApp/Services/AvatarCache.cs';
const DATA_SERVICE = 'WhatsappApp/Services/DataService.cs';

test('la copia dell elenco chat non si porta dietro i byte dell immagine', () => {
  const bad = 'return new ChatMessage {\n  ChatId = row.ChatId,\n  AvatarData = row.AvatarData\n};';
  const problems = memory.avatarCacheProblems(bad, CHAT_CACHE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /chats\.json/);
});

test('la copia dell elenco chat senza l immagine passa', () => {
  const good = 'return new ChatMessage {\n  ChatId = row.ChatId,\n  SenderName = row.SenderName\n};';
  assert.deepStrictEqual(memory.avatarCacheProblems(good, CHAT_CACHE), []);
});

test('un altra cache di immagini non entra in questa regola', () => {
  const source = 'return new ChatMessage { AvatarData = row.AvatarData };';
  assert.deepStrictEqual(memory.avatarCacheProblems(source, 'WhatsappApp/Services/MessageCache.cs'), []);
});

test('la cache degli avatar senza tetti si segnala', () => {
  const source = 'private static readonly Dictionary<string, string> Known =\n' +
    '    new Dictionary<string, string>();\n' +
    'private static void Save() {\n' +
    '  Writes.RunAsync(delegate { return WriteIfChangedAsync(); });\n' +
    '}';
  const problems = memory.avatarCacheProblems(source, AVATAR_CACHE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /MaxChats/);
});

test('la cache degli avatar che scrive senza la coda si segnala', () => {
  const source = 'private const int MaxChats = 40;\n' +
    'private const int MaxTotalChars = 1500000;\n' +
    'StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(\n' +
    '  FileName, CreationCollisionOption.ReplaceExisting);';
  const problems = memory.avatarCacheProblems(source, AVATAR_CACHE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /serial queue/);
});

test('una cache degli avatar con i tetti e la coda passa', () => {
  const source = 'private const int MaxChats = 40;\n' +
    'private const int MaxEntryChars = 150000;\n' +
    'private const int MaxTotalChars = 1500000;\n' +
    'Writes.RunAsync(delegate { return WriteIfChangedAsync(); });';
  assert.deepStrictEqual(memory.avatarCacheProblems(source, AVATAR_CACHE), []);
});

test('la coda non basta se la coda si e chiamata diversamente', () => {
  const source = 'private const int MaxChats = 40;\n' +
    'private const int MaxTotalChars = 1500000;\n' +
    'private static void Save() {\n' +
    '  FireAndForget(WriteFileAsync(Serialize()));\n' +
    '}';
  const problems = memory.avatarCacheProblems(source, AVATAR_CACHE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /serial queue/);
});

test('una cache locale letta dopo le righe si segnala', () => {
  const bad = 'private async Task LoadCachedChatsAsync()\n{\n' +
    '    await ChatPreferences.LoadAsync();\n' +
    '    var cached = await ChatCache.LoadAsync();\n' +
    '    for (int i = 0; i < cached.Count; i++) ApplyChat(cached[i]);\n' +
    '    await AvatarCache.LoadAsync();\n}';
  const problems = memory.cachedRowsProblems(bad, DATA_SERVICE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /after the first ApplyChat/);
});

test('una cache locale mai letta si segnala', () => {
  const bad = 'private async Task LoadCachedChatsAsync()\n{\n' +
    '    var cached = await ChatCache.LoadAsync();\n' +
    '    for (int i = 0; i < cached.Count; i++) ApplyChat(cached[i]);\n}';
  const problems = memory.cachedRowsProblems(bad, DATA_SERVICE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /does not await AvatarCache\.LoadAsync/);
});

test('una cache locale letta prima delle righe passa', () => {
  const good = 'private async Task LoadCachedChatsAsync()\n{\n' +
    '    await ChatPreferences.LoadAsync();\n' +
    '    await AvatarCache.LoadAsync();\n' +
    '    var cached = await ChatCache.LoadAsync();\n' +
    '    for (int i = 0; i < cached.Count; i++) ApplyChat(cached[i]);\n}';
  assert.deepStrictEqual(memory.cachedRowsProblems(good, DATA_SERVICE), []);
});

test('le regole delle cache non guardano altri file', () => {
  assert.deepStrictEqual(memory.cachedRowsProblems('anything at all', 'WhatsappApp/Services/ChatPage.xaml.cs'), []);
});
```

- [x] **Step 2: Run the tests to make sure they fail**

Run: `node --test tools/test/check-memory.test.js 2>&1 | tail -20`
Expected: FAIL - `memory.avatarCacheProblems is not a function` (and the same for `cachedRowsProblems`).

- [x] **Step 3: Add the three rules to the guard**

In `tools/check-memory.js`, add the two functions just after `attachmentProblems` and before `function walk`:

```javascript
const CHAT_CACHE = 'WhatsappApp/Services/ChatCache.cs';
const AVATAR_CACHE = 'WhatsappApp/Services/AvatarCache.cs';
const DATA_SERVICE = 'WhatsappApp/Services/DataService.cs';

/**
 * Problemi delle due cache dell'elenco chat.
 *
 * Perche' esiste: le due cache si dividono lo stesso lavoro e si difendono a
 * vicenda. ChatCache e' il file che l'app legge prima che la connessione
 * esista, e deve restare piccolo: i byte di un'immagine non sono suoi, e da
 * quando c'e' AvatarCache non c'e' piu' nessuna ragione per rimetterli li'.
 * I byte stanno in AvatarCache, che per questo ha bisogno di tetti (una cache
 * senza tetto e' una crescita lenta che nessuno vede) e della stessa coda di
 * scrittura delle preferenze (due scritture sullo stesso file, lanciate senza
 * aspettarsi, finiscono fuori ordine - e' il difetto che ChatPreferences ha
 * gia' avuto).
 */
function avatarCacheProblems(source, file) {
  const problems = [];
  const code = stripComments(source);

  if (file === CHAT_CACHE) {
    if (/AvatarData\s*=/.test(code)) {
      problems.push(`${file}: the row cache saves AvatarData into chats.json: that file ` +
        'is read before the connection exists, holds one row per chat, and the pictures ' +
        'have a bounded cache of their own (AvatarCache, avatar-cache.json)');
    }
    return problems;
  }

  if (file !== AVATAR_CACHE) return problems;

  if (!/MaxChats\s*=\s*\d+/.test(code) || !/MaxTotalChars\s*=\s*\d+/.test(code)) {
    problems.push(`${file}: no MaxChats/MaxTotalChars cap: the avatar cache would grow ` +
      'with the account, one picture per conversation, and nothing would ever drop one');
  }
  if (!/Writes\.RunAsync\s*\(/.test(code)) {
    problems.push(`${file}: writes the file without the serial queue: two pictures ` +
      'arriving together start two writes on the same file, and the older snapshot can ' +
      'win (this is the bug ChatPreferences had)');
  }
  return problems;
}

/**
 * Problemi dell'ordine con cui si carica la copia locale.
 *
 * Perche' esiste: la copia locale non ha i byte delle immagini (ChatCache li
 * lascia fuori), quindi ApplyChat li chiede ad AvatarCache. Se la cache non e'
 * ancora stata letta risponde di no a tutte, e un riavvio mostra le iniziali
 * finche' l'adapter non rimanda ogni riga - che e' esattamente il difetto che
 * questa cache esiste per togliere. Il file viene letto dal disco una volta
 * sola, e ci vuole un await: l'ordine e' il contenuto di questa regola.
 */
function cachedRowsProblems(source, file) {
  const problems = [];
  if (file !== DATA_SERVICE) return problems;

  const code = stripComments(source);
  const start = code.indexOf('LoadCachedChatsAsync');
  if (start < 0) {
    problems.push(`${file}: LoadCachedChatsAsync is gone (did the cached rows move? ` +
      'then this guard must move too)');
    return problems;
  }

  const body = code.slice(start, start + 1500);
  const load = body.indexOf('AvatarCache.LoadAsync');
  const apply = body.indexOf('ApplyChat(');

  if (load < 0) {
    problems.push(`${file}: LoadCachedChatsAsync does not await AvatarCache.LoadAsync: ` +
      'the cached rows are applied with the pictures still unread, so a restart shows ' +
      'initials until the adapter answers');
    return problems;
  }
  if (apply >= 0 && load > apply) {
    problems.push(`${file}: AvatarCache.LoadAsync comes after the first ApplyChat: the ` +
      'rows are applied with the pictures still unread');
  }
  return problems;
}
```

In the same file, inside `main()`, add the two calls to the loop:

```javascript
  for (const file of walk(APP, [])) {
    const rel = path.relative(ROOT, file).replace(/\\/g, '/');
    const source = fs.readFileSync(file, 'utf8');
    problems.push(...decodeProblems(source, rel));
    problems.push(...attachmentProblems(source, rel));
    problems.push(...avatarCacheProblems(source, rel));
    problems.push(...cachedRowsProblems(source, rel));
    if (rel === HELPER) problems.push(...sourceShapeProblems(source, rel));
  }
```

Replace the success line with:

```javascript
  console.log('OK: every decoded bitmap asks for the width it is shown at, the row cache ' +
    'stays slim, and the pictures have a bounded cache read before the rows.');
```

And export the two new functions:

```javascript
module.exports = {
  decodeProblems,
  attachmentProblems,
  avatarCacheProblems,
  cachedRowsProblems,
  sourceShapeProblems,
  splitArguments,
  MAX_DECODE
};
```

Finally, extend the header comment of the file with the two new reasons, after rule 3:

```
 *  4. la copia dell'elenco chat (ChatCache) non tiene i byte di un'immagine:
 *     quelli stanno nella cache degli avatar, che ha dei tetti ed e' letta
 *     prima che le righe salvate vengano applicate.
```

- [x] **Step 4: Run the tests to make sure they pass**

Run: `node --test tools/test/check-memory.test.js 2>&1 | tail -12`
Expected: PASS, 21 tests.

- [x] **Step 5: Create the cache**

Create `WhatsappApp/Services/AvatarCache.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>Il file su disco: un'immagine per chat.</summary>
    [DataContract]
    internal class AvatarCacheEntry
    {
        [DataMember]
        public string ChatId { get; set; }

        [DataMember]
        public string Data { get; set; }
    }

    /// <summary>Il file intero: l'elenco delle immagini.</summary>
    [DataContract]
    internal class AvatarCacheFile
    {
        [DataMember]
        public List<AvatarCacheEntry> Chats { get; set; }
    }

    /// <summary>
    /// Le immagini del profilo che l'adapter ha mandato, tenute sul telefono.
    ///
    /// Perche' esiste: l'elenco chat arriva con l'immagine di ogni
    /// conversazione, e l'app la decodifica e la disegna. Ma i byte non
    /// sopravvivevano a niente - la copia dell'elenco (ChatCache) li lascia
    /// fuori di proposito, perche' e' il file che si legge prima che la
    /// connessione esista e deve restare piccolo, e la copia decodificata la
    /// butta via MemoryWatcher sotto pressione. Risultato: dopo un riavvio, o
    /// dopo un picco di memoria, la lista mostra le iniziali finche' l'adapter
    /// non rimanda ogni riga, cioe' due richieste HTTP per chat (l'indirizzo
    /// dell'immagine, e poi i byte dal CDN).
    ///
    /// Qui i byte si tengono. Non e' una verita': e' una cache, e la riga che
    /// arriva dal server la sostituisce appena arriva (vedi ApplyChat).
    ///
    /// Tetti, perche' quello che non ha un tetto cresce: MaxChats chat (una in
    /// piu' di CHATS_LIMIT non verrebbe mai disegnata), MaxEntryChars per una
    /// singola immagine e MaxTotalChars per il file. Chi non entra non si
    /// tiene, e la riga torna alle iniziali - che e' quello che faceva prima.
    /// </summary>
    public static class AvatarCache
    {
        private const string FileName = "avatar-cache.json";

        /// <summary>Quante chat si tengono.</summary>
        public const int MaxChats = 40;

        /// <summary>Il tetto di una singola immagine, in caratteri base64 (~110 KB).</summary>
        public const int MaxEntryChars = 150000;

        /// <summary>Il tetto di tutto il file, in caratteri base64 (~1,1 MB).</summary>
        public const int MaxTotalChars = 1500000;

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(AvatarCacheFile));

        /// <summary>ChatId -> immagine in base64.</summary>
        private static readonly Dictionary<string, string> Known =
            new Dictionary<string, string>();

        /// <summary>L'ordine in cui le chat sono entrate: da qui esce chi e' di troppo.</summary>
        private static readonly List<string> Order = new List<string>();

        /// <summary>Le chat la cui immagine e' troppo grande: si dice una volta sola.</summary>
        private static readonly HashSet<string> TooBig = new HashSet<string>();

        /// <summary>I caratteri che Known occupa adesso: sommarli a ogni controllo costerebbe.</summary>
        private static long _chars;

        /// <summary>
        /// Il lucchetto di Known, Order, TooBig e _chars. Serve perche' Remember
        /// lo chiama il thread UI (ApplyChat) e la scrittura la esegue la coda.
        /// </summary>
        private static readonly object Gate = new object();

        /// <summary>Le scritture del file, una alla volta e in ordine.</summary>
        private static readonly SerialQueue Writes = new SerialQueue();

        /// <summary>Cresce a ogni cambiamento: dice se c'e' qualcosa da scrivere.</summary>
        private static int _version;

        /// <summary>L'ultima versione arrivata sul disco.</summary>
        private static int _written;

        private static bool _loaded;

        /// <summary>Quante immagini si tengono adesso. Solo per la diagnosi.</summary>
        public static int Count
        {
            get { lock (Gate) { return Known.Count; } }
        }

        /// <summary>
        /// Legge il file una volta sola, e va aspettata prima di applicare le
        /// righe salvate: senza, quelle righe non trovano nessuna immagine.
        /// Mai un'eccezione: al primo avvio il file non c'e'.
        /// </summary>
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
                    var cache = Serializer.ReadObject(stream) as AvatarCacheFile;
                    if (cache == null || cache.Chats == null) return;

                    lock (Gate)
                    {
                        for (int i = 0; i < cache.Chats.Count; i++)
                        {
                            var entry = cache.Chats[i];
                            if (entry == null) continue;
                            if (string.IsNullOrEmpty(entry.ChatId)) continue;
                            if (string.IsNullOrEmpty(entry.Data)) continue;
                            if (Known.ContainsKey(entry.ChatId)) continue;

                            Known[entry.ChatId] = entry.Data;
                            Order.Add(entry.ChatId);
                            _chars += entry.Data.Length;
                        }

                        Evict();

                        // Quello che si e' letto e' gia' sul disco: non c'e'
                        // niente da riscrivere.
                        _written = _version;
                    }
                }
            }
            catch (Exception ex)
            {
                // Primo avvio, o file scritto da una versione diversa.
                Diag.Failed("AvatarCache.Load", ex);
            }
        }

        /// <summary>L'immagine tenuta per questa chat, o null. Dopo LoadAsync.</summary>
        public static string Get(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return null;

            lock (Gate)
            {
                string data;
                return Known.TryGetValue(chatId, out data) ? data : null;
            }
        }

        /// <summary>
        /// Tiene l'immagine di una chat. La chiama ApplyChat a ogni riga che ne
        /// porta una: se sono gli stessi byte di prima non si fa niente, e il
        /// server le rimanda uguali a ogni elenco, quindi un aggiornamento non
        /// riscrive il file.
        /// </summary>
        public static void Remember(string chatId, string base64)
        {
            if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(base64)) return;

            bool changed = false;
            lock (Gate)
            {
                if (base64.Length > MaxEntryChars)
                {
                    if (TooBig.Add(chatId))
                    {
                        Diag.Ok("avatar-cache: image of " + chatId + " is " + base64.Length +
                            " chars, over the " + MaxEntryChars + " limit, not kept");
                    }
                    return;
                }

                string current;
                if (Known.TryGetValue(chatId, out current) && current == base64) return;

                if (current != null)
                {
                    _chars -= current.Length;
                    Order.Remove(chatId);
                }

                Known[chatId] = base64;
                Order.Add(chatId);
                _chars += base64.Length;
                Evict();

                _version++;
                changed = true;
            }

            if (changed) Save();
        }

        /// <summary>
        /// Tiene il file dentro i tetti: esce la chat entrata per prima. Va
        /// chiamata sotto Gate, perche' tocca Known, Order e _chars insieme.
        /// </summary>
        private static void Evict()
        {
            while (Order.Count > MaxChats || _chars > MaxTotalChars)
            {
                if (Order.Count == 0) return;

                string oldest = Order[0];
                Order.RemoveAt(0);

                string data;
                if (Known.TryGetValue(oldest, out data)) _chars -= data.Length;
                Known.Remove(oldest);
            }
        }

        /// <summary>
        /// Mette una scrittura in coda. Lo scatto si prende dentro la coda,
        /// non qui: venti righe che arrivano insieme - un elenco chat - con
        /// questa forma scrivono il file una volta sola, e serializzare sta
        /// fuori dal lucchetto, perche' chi chiama Remember e' il thread UI e
        /// un megabyte di JSON non e' roba da tenergli in mano.
        /// </summary>
        private static void Save()
        {
#pragma warning disable 4014
            Writes.RunAsync(delegate { return WriteIfChangedAsync(); });
#pragma warning restore 4014
        }

        /// <summary>Scrive se qualcosa e' cambiato da quando e' stato scritto l'ultima volta.</summary>
        private static async Task WriteIfChangedAsync()
        {
            int version;
            List<AvatarCacheEntry> snapshot;

            lock (Gate)
            {
                if (_version == _written) return;

                version = _version;
                snapshot = new List<AvatarCacheEntry>();
                for (int i = 0; i < Order.Count; i++)
                {
                    snapshot.Add(new AvatarCacheEntry
                    {
                        ChatId = Order[i],
                        Data = Known[Order[i]]
                    });
                }
            }

            if (!await WriteFileAsync(Serialize(snapshot))) return;

            lock (Gate)
            {
                if (_written < version) _written = version;
            }
        }

        /// <summary>Le voci in JSON.</summary>
        private static string Serialize(List<AvatarCacheEntry> entries)
        {
            using (var stream = new MemoryStream())
            {
                Serializer.WriteObject(stream, new AvatarCacheFile { Chats = entries });
                return Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
            }
        }

        /// <summary>
        /// Scrive il file. Non aspetta nessuno - chi guarda l'elenco chat non
        /// ha niente a che fare con l'esito - quindi cattura da sola: un
        /// deposito senza padrone non deve poter far cadere la pagina. Dice se
        /// e' andata bene, perche' solo allora la versione e' sul disco.
        /// </summary>
        private static async Task<bool> WriteFileAsync(string json)
        {
            try
            {
                StorageFile storage = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(storage, json);
                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("AvatarCache.Save", ex);
                return false;
            }
        }
    }
}
```

Note: `using System.Linq;` is not needed by this file. Leave it out - the compiler warns on an unused using only with a diagnostic that WP8.1's MSBuild does not raise, but the repo's files do not carry unused usings.

- [x] **Step 6: Register the file in the project**

In `WhatsappApp/WhatsappApp.csproj`, after the `ChatCache.cs` line:

```xml
    <Compile Include="Services\AvatarCache.cs" />
    <Compile Include="Services\ChatCache.cs" />
    <Compile Include="Services\ChatPreferences.cs" />
```

- [x] **Step 7: Read the cache before the rows, and use it in ApplyChat**

In `WhatsappApp/Services/DataService.cs`, in `LoadCachedChatsAsync`, replace:

```csharp
            await ChatPreferences.LoadAsync();

            var cached = await ChatCache.LoadAsync();
```

with:

```csharp
            await ChatPreferences.LoadAsync();

            // E anche prima delle righe, per lo stesso motivo: la copia locale
            // non ha i byte delle immagini (ChatCache li lascia fuori), e
            // ApplyChat li chiede qui.
            await AvatarCache.LoadAsync();

            var cached = await ChatCache.LoadAsync();
```

and in `ApplyChat` replace:

```csharp
            if (!string.IsNullOrEmpty(message.AvatarData) && contact.AvatarData != message.AvatarData)
            {
                contact.AvatarData = message.AvatarData;
#pragma warning disable 4014
                contact.LoadAvatarAsync();
#pragma warning restore 4014
            }
```

with:

```csharp
            // L'immagine del profilo. Quando la riga la porta, i byte si tengono
            // anche sul telefono: alla prossima apertura l'elenco ha una faccia
            // prima che l'adapter risponda. Quando non la porta - una riga della
            // copia locale - si usa quella tenuta.
            string avatar = message.AvatarData;
            if (string.IsNullOrEmpty(avatar)) avatar = AvatarCache.Get(message.ChatId);

            if (!string.IsNullOrEmpty(avatar) && contact.AvatarData != avatar)
            {
                contact.AvatarData = avatar;
#pragma warning disable 4014
                contact.LoadAvatarAsync();
#pragma warning restore 4014
            }

            // Solo i byte arrivati dal server si tengono: una riga letta dal
            // disco non e' una notizia, e' quello che c'e' gia' scritto.
            if (!string.IsNullOrEmpty(message.AvatarData))
            {
                AvatarCache.Remember(message.ChatId, message.AvatarData);
            }
```

- [x] **Step 8: Repair the line endings and run the gate**

Run:
```bash
perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' WhatsappApp/Services/AvatarCache.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/Services/DataService.cs tools/check-memory.js tools/test/check-memory.test.js
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"
```
Expected: `OK: 38 C# file(s) are C# 5 compatible.` (was 37), `125 key(s)`, `OK: every decoded bitmap asks for the width it is shown at, the row cache stays slim...`, and `# pass 57` in `tools/test` (47 + 10).

Then: `cd WhatsappBridge && npm test` → 136 passing (nothing changed here).

- [x] **Step 9: Build on the VM**

Run the three commands of the Build gate (Global Constraints).
Expected: `COPIA=0`, `Avvisi: 0`, `Errori: 0`, `Your package has been successfully created.`

- [x] **Step 10: Commit**

```bash
git add WhatsappApp/Services/AvatarCache.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/Services/DataService.cs tools/check-memory.js tools/test/check-memory.test.js
git commit -m "$(cat <<'EOF'
feat: keep the contact pictures on the phone

The chat list got its pictures from the adapter every time, because the row
cache leaves the bytes out on purpose and the decoded copy is dropped under
memory pressure: a restart, or a memory spike, left the list on initials
until one HTTP request per chat went out again. AvatarCache holds those bytes
in a file of its own, bounded and written through the serial queue, and it is
read before the cached rows are applied.
EOF
)"
```

---

### Task 2: The pictures come back after memory pressure

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs` (`TrimForMemory` comment, new `RestoreAvatars`)
- Modify: `WhatsappApp/Pages/ChatsPage.xaml.cs` (`OnNavigatedTo`)

**Interfaces:**
- Consumes: `AvatarCache.Get(string chatId)` → `string`; `MemoryWatcher.Instance.IsUnderPressure` → `bool`; `Contact.LoadAvatarAsync()` → `Task`; `DataService.Instance` → `DataService`.
- Produces: `DataService.RestoreAvatars()` → `void` (safe to call on the UI thread, does nothing under memory pressure).

- [x] **Step 1: Note what TrimForMemory keeps**

In `WhatsappApp/Services/DataService.cs`, in the doc comment of `TrimForMemory`, replace:

```csharp
        /// La collezione di una chat si svuota invece di essere buttata via: la
```

with:

```csharp
        /// I byte di un'immagine non si buttano via: si butta via la copia
        /// decodificata, che e' quella che pesa, e si rifa' quando l'elenco
        /// torna davanti (vedi RestoreAvatars).
        ///
        /// La collezione di una chat si svuota invece di essere buttata via: la
```

- [x] **Step 2: Add RestoreAvatars**

In the same file, right after `TrimForMemory` (after its closing brace), add:

```csharp
        /// <summary>
        /// Ridisegna gli avatar di cui ci sono ancora i byte. Sotto pressione di
        /// memoria TrimForMemory butta via la copia decodificata e la riga resta
        /// con le iniziali: il server la rimanda solo al prossimo elenco, e
        /// intanto la lista sembra vuota di facce. I byte invece ci sono ancora
        /// - in memoria, o nella cache sul telefono - quindi si rifa' qui,
        /// quando l'elenco torna davanti.
        ///
        /// Va chiamata sul thread UI: BitmapImage non e' agnostica rispetto
        /// alla view (vedi ImageHelper).
        /// </summary>
        public void RestoreAvatars()
        {
            if (MemoryWatcher.Instance.IsUnderPressure) return;

            for (int i = 0; i < _contacts.Count; i++)
            {
                var contact = _contacts[i];
                if (contact == null || contact.Avatar != null) continue;

                string data = contact.AvatarData;
                if (string.IsNullOrEmpty(data)) data = AvatarCache.Get(contact.Id);
                if (string.IsNullOrEmpty(data)) continue;

                contact.AvatarData = data;
#pragma warning disable 4014
                contact.LoadAvatarAsync();
#pragma warning restore 4014
            }
        }
```

- [x] **Step 3: Call it when the list comes back**

In `WhatsappApp/Pages/ChatsPage.xaml.cs`, in `OnNavigatedTo`, after:

```csharp
            UpdateEmptyState();
```

add:

```csharp
            // Le immagini che ci sono ancora in byte ma non piu' decodificate:
            // MemoryWatcher le ha buttate via, e senza questo l'elenco resta con
            // le iniziali finche' il server non rimanda le righe.
            DataService.Instance.RestoreAvatars();
```

- [x] **Step 4: Repair the line endings and run the gate**

Run:
```bash
perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' WhatsappApp/Services/DataService.cs WhatsappApp/Pages/ChatsPage.xaml.cs
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"
```
Expected: 38 C# files, every guard OK, `# pass 57`.

- [x] **Step 5: Build on the VM**

Run the three commands of the Build gate.
Expected: `COPIA=0`, `Avvisi: 0`, `Errori: 0`, package created.

- [x] **Step 6: Commit**

```bash
git add WhatsappApp/Services/DataService.cs WhatsappApp/Pages/ChatsPage.xaml.cs
git commit -m "$(cat <<'EOF'
fix: redraw the contact pictures when the list comes back

MemoryWatcher drops the decoded bitmaps under pressure and nothing brought
them back: the adapter only resends a row with its next chat list, so the
list sat on initials. RestoreAvatars rebuilds every picture whose bytes are
still there, in memory or in the phone cache, and the chats page asks for it
whenever the list is shown.
EOF
)"
```

---

### Task 3: The adapter keeps the pictures it downloaded

**Files:**
- Create: `WhatsappBridge/avatar-cache.js`
- Modify: `WhatsappBridge/gowa-client.js` (constructor, `avatar(jid)`)
- Create: `WhatsappBridge/test/avatar-cache.test.js`
- Modify: `WhatsappBridge/test/gowa-client.test.js`
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`

**Interfaces:**
- Consumes: nothing from the earlier tasks (the adapter and the app are separate programs).
- Produces: `createAvatarCache({ ttlMs, missingTtlMs, maxEntries, now })` → `{ get(key), put(key, value), size() }`, where `get` returns `undefined` when there is nothing to answer with and `null` when the answer is "this JID has no picture"; `DEFAULT_TTL_MS`, `DEFAULT_MISSING_TTL_MS`, `DEFAULT_MAX_ENTRIES` → `number`. `new GowaClient({ ..., avatarCache })` accepts an injected cache (same seam as `fetchImpl`).

- [x] **Step 1: Write the failing tests for the map**

Create `WhatsappBridge/test/avatar-cache.test.js`:

```javascript
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { createAvatarCache, DEFAULT_TTL_MS } = require('../avatar-cache');

test('una immagine chiesta due volte si scarica una volta', () => {
  const cache = createAvatarCache();
  assert.strictEqual(cache.get('a@s.whatsapp.net'), undefined);

  cache.put('a@s.whatsapp.net', 'AAAA');
  assert.strictEqual(cache.get('a@s.whatsapp.net'), 'AAAA');
});

test('una voce scaduta non risponde piu', () => {
  let clock = 1000;
  const cache = createAvatarCache({ ttlMs: 60000, now: () => clock });

  cache.put('a@s.whatsapp.net', 'AAAA');
  clock += DEFAULT_TTL_MS;
  assert.strictEqual(cache.get('a@s.whatsapp.net'), 'AAAA');

  clock += 60001;
  assert.strictEqual(cache.get('a@s.whatsapp.net'), undefined);
});

test('una chat senza immagine si ricontrolla prima', () => {
  let clock = 0;
  const cache = createAvatarCache({ missingTtlMs: 1000, now: () => clock });

  // Un JID senza foto puo' aggiungerla: la risposta "non ce l'ha" non vale
  // quanto i byte di un'immagine.
  cache.put('b@s.whatsapp.net', null);
  assert.strictEqual(cache.get('b@s.whatsapp.net'), null);

  clock += 1001;
  assert.strictEqual(cache.get('b@s.whatsapp.net'), undefined);
});

test('il tetto butta fuori quello che e entrato per primo', () => {
  const cache = createAvatarCache({ maxEntries: 2, now: () => 0 });

  cache.put('a', 'A');
  cache.put('b', 'B');
  cache.put('c', 'C');

  assert.strictEqual(cache.size(), 2);
  assert.strictEqual(cache.get('a'), undefined);
  assert.strictEqual(cache.get('c'), 'C');
});

test('riscrivere una voce la tiene', () => {
  const cache = createAvatarCache({ maxEntries: 2, now: () => 0 });

  cache.put('a', 'A');
  cache.put('b', 'B');
  cache.put('a', 'A2');
  cache.put('c', 'C');

  assert.strictEqual(cache.get('a'), 'A2');
  assert.strictEqual(cache.get('b'), undefined);
});

test('una chiave vuota non entra nella cache', () => {
  const cache = createAvatarCache();
  cache.put('', 'AAAA');
  cache.put(null, 'AAAA');
  assert.strictEqual(cache.size(), 0);
});
```

- [x] **Step 2: Run the tests to make sure they fail**

Run: `cd WhatsappBridge && node --test test/avatar-cache.test.js 2>&1 | tail -8`
Expected: FAIL - `Cannot find module '../avatar-cache'`.

- [x] **Step 3: Write the map**

Create `WhatsappBridge/avatar-cache.js`:

```javascript
'use strict';

// Le immagini del profilo gia' scaricate, tenute in memoria per un po'.
//
// Perche' esiste: l'elenco chat costa una richiesta HTTP per conversazione per
// l'ultimo messaggio e due per la sua immagine (una per l'indirizzo, una per i
// byte dal CDN di WhatsApp), e l'app lo richiede a ogni riconnessione e a ogni
// cambio di sezione una volta scaduto il minuto di cache di server.js. Gli
// stessi venti avatar si riscaricavano quindi piu' volte al giorno: lenti, e
// sono le richieste che WhatsApp guarda quando decide di limitare un account.
//
// I byte di una foto non cambiano sotto i piedi, quindi valgono qualche
// minuto. Un JID che una foto non ce l'ha vale meno, perche' quella si puo'
// aggiungere: la sua risposta scade prima.
//
// Limiti: mai piu' di maxEntries voci - un telefono con cinquanta
// conversazioni ne disegna comunque CHATS_LIMIT, quindi il resto e' memoria
// buttata. Una voce scaduta si butta al primo accesso: non c'e' nessun timer
// che gira, e la memoria si libera quando qualcuno guarda.

const DEFAULT_TTL_MS = 5 * 60 * 1000;
const DEFAULT_MISSING_TTL_MS = 60 * 1000;
const DEFAULT_MAX_ENTRIES = 60;

function createAvatarCache(options) {
  const opts = options || {};
  const ttlMs = typeof opts.ttlMs === 'number' ? opts.ttlMs : DEFAULT_TTL_MS;
  const missingTtlMs = typeof opts.missingTtlMs === 'number'
    ? opts.missingTtlMs
    : DEFAULT_MISSING_TTL_MS;
  const maxEntries = typeof opts.maxEntries === 'number' ? opts.maxEntries : DEFAULT_MAX_ENTRIES;
  const now = typeof opts.now === 'function' ? opts.now : () => Date.now();

  // Map conserva l'ordine di inserimento: la prima chiave e' la piu' vecchia,
  // ed e' quella che esce quando si e' sopra il tetto.
  const entries = new Map();

  return {
    /** L'immagine tenuta, null se la risposta era "non ce l'ha", undefined se non si sa. */
    get(key) {
      const entry = entries.get(key);
      if (!entry) return undefined;
      if (now() - entry.at > entry.ttl) {
        entries.delete(key);
        return undefined;
      }
      return entry.value;
    },

    /** Tiene una risposta. null e' una risposta: "questo JID non ha una foto". */
    put(key, value) {
      if (typeof key !== 'string' || key === '') return;

      entries.delete(key);
      entries.set(key, {
        value,
        at: now(),
        ttl: value === null || value === undefined ? missingTtlMs : ttlMs
      });

      while (entries.size > maxEntries) {
        const oldest = entries.keys().next().value;
        entries.delete(oldest);
      }
    },

    /** Quante voci si tengono adesso. Per i test e per la diagnosi. */
    size() {
      return entries.size;
    }
  };
}

module.exports = { createAvatarCache, DEFAULT_TTL_MS, DEFAULT_MISSING_TTL_MS, DEFAULT_MAX_ENTRIES };
```

- [x] **Step 4: Run the tests to make sure they pass**

Run: `cd WhatsappBridge && node --test test/avatar-cache.test.js 2>&1 | tail -8`
Expected: PASS, 6 tests.

- [x] **Step 5: Use it from the client**

In `WhatsappBridge/gowa-client.js`, add the require under `'use strict';`:

```javascript
const { createAvatarCache } = require('./avatar-cache');
```

change the constructor signature and body:

```javascript
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
```

and in `avatar(jid)`, replace:

```javascript
    const at = value.indexOf('@');
    const target = value.slice(0, at).split(':')[0] + value.slice(at);
    try {
```

with:

```javascript
    const at = value.indexOf('@');
    const target = value.slice(0, at).split(':')[0] + value.slice(at);

    // undefined vuol dire "non si sa": null vuol dire "non ce l'ha", ed e' una
    // risposta che si tiene (per poco, vedi avatar-cache.js).
    const remembered = this.avatars.get(target);
    if (remembered !== undefined) return remembered;

    try {
```

and before each `return` of that method's body, remember the answer. Replace:

```javascript
      const url = (r.data && r.data.results && r.data.results.url) || '';
      if (!r.ok || !url) return null;

      const picture = await this.fetchBinary(url);
      return picture.buffer.length > 0 ? picture.buffer.toString('base64') : null;
    } catch (err) {
      return null;
    }
  }
```

with:

```javascript
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
```

- [x] **Step 6: Write the wiring test**

Add to the end of `WhatsappBridge/test/gowa-client.test.js`:

```javascript
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
```

- [x] **Step 7: Run the adapter suite**

Run: `cd WhatsappBridge && npm test 2>&1 | grep -E "^(# (pass|fail)|not ok)"`
Expected: `# pass 143` (136 + 6 + 1), `# fail 0`.

- [x] **Step 8: Say it in the adapter READMEs**

In `WhatsappBridge/README.md`, in the env table row of `CHATS_AVATARS`, replace:

```
| `CHATS_AVATARS` | `on` | fetch profile pictures, one request per chat, groups included (`off` disables) |
```

with:

```
| `CHATS_AVATARS` | `on` | fetch profile pictures, one request per chat, groups included (`off` disables). A picture that was downloaded is kept for five minutes, a JID with no picture for one, so a chat list that is read again does not go back to WhatsApp (see `avatar-cache.js`) |
```

In `WhatsappBridge/README.it.md`, the same row:

```
| `CHATS_AVATARS` | `on` | scarica le immagini del profilo, una richiesta per chat, gruppi compresi (`off` le spegne). Un'immagine gia' scaricata si tiene per cinque minuti, un JID senza immagine per uno, cosi' un elenco chat riletto non torna da WhatsApp (vedi `avatar-cache.js`) |
```

- [x] **Step 9: Run the gate, then mirror into the Docker repository**

Run: `node tools/check-docs.js && node tools/check-csharp5.js && cd WhatsappBridge && npm test 2>&1 | grep -E "^# (pass|fail)"`
Expected: docs OK, `# pass 143`, `# fail 0`.

Then:
```bash
cd /tmp/docker-whatsappforwp && node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP && node tools/sync.js --check && cd server && npm test 2>&1 | grep -E "^# (pass|fail)"
```
Expected: the sync reports the changed files and `OK: server/ matches the adapter`, the suite passes as it does in the source.

- [x] **Step 10: Commit and push both repositories**

```bash
git add WhatsappBridge/avatar-cache.js WhatsappBridge/gowa-client.js WhatsappBridge/test/avatar-cache.test.js WhatsappBridge/test/gowa-client.test.js WhatsappBridge/README.md WhatsappBridge/README.it.md
git commit -m "$(cat <<'EOF'
feat: keep a downloaded profile picture in the adapter

The chat list is re-read on every reconnection and after the adapter's own
minute of cache, and each read cost two HTTP requests per chat for a picture
that had not changed. The adapter now answers from a small map instead:
five minutes for a picture, one for a JID that has none, so a photo added
later is not hidden for long.
EOF
)"
git push origin master 2>&1 | tail -3
```

```bash
cd /tmp/docker-whatsappforwp && git add -A && git commit -m "chore: mirror the adapter avatar cache" && git push origin main 2>&1 | tail -3
```

`server/SOURCE_COMMIT` is updated by `sync.js` itself; verify with `cat server/SOURCE_COMMIT` that it names the commit just made in the app repository.

---

### Task 4: The docs, the counts, and the on-device checks

**Files:**
- Modify: `README.md`, `README.it.md` (the memory section and the limits list)
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Modify: `.agents/skills/test-the-app/SKILL.md`
- Modify: `docs/superpowers/plans/2026-09-28-cached-contact-pictures.md` (tick the steps, append what execution changed)

**Interfaces:**
- Consumes: the counts printed by the gate at the end of Task 3.
- Produces: nothing importable; the documentation the next session reads.

- [x] **Step 1: The two user READMEs**

In `README.md`, in the "Memory on a 512 MB device" section, after the `let go when asked` bullet, add:

```
- **and ask again from the phone**: the bytes of a picture are kept in
  `avatar-cache.json` (at most 40 conversations, about 1 MB), so a restart shows
  the faces before the adapter answers, and a row whose decoded bitmap was
  dropped is redrawn when the list comes back to the front. The pictures are the
  app's copy of what the adapter sent: the adapter keeps them for five minutes
  as well, so a chat list read twice does not go back to WhatsApp;
```

In `README.it.md`, in the same section, the mirrored line:

```
- **e si richiedono dal telefono**: i byte di un'immagine si tengono in
  `avatar-cache.json` (al massimo 40 conversazioni, circa 1 MB), cosi' un
  riavvio mostra le facce prima che l'adapter risponda, e una riga la cui
  bitmap decodificata e' stata buttata via si ridisegna quando l'elenco torna
  davanti. Le immagini sono la copia di quello che l'adapter ha mandato: anche
  l'adapter le tiene per cinque minuti, cosi' un elenco chat letto due volte
  non torna da WhatsApp;
```

In `README.md`, in the limits list, replace:

```
- Under memory pressure the app drops the decoded avatars and asks for them again later: on a phone that stays under pressure, the chat list shows initials for a while.
```

with:

```
- Under memory pressure the app drops the decoded avatars; the bytes stay, and the pictures are redrawn when the chat list comes back. While the pressure lasts nothing new is decoded, so on a phone that stays under pressure the list shows initials for a while.
```

In `README.it.md`, the mirrored line:

```
- Sotto pressione di memoria l'app butta via gli avatar decodificati; i byte restano, e le immagini si ridisegnano quando l'elenco chat torna davanti. Finche' la pressione dura non si decodifica niente di nuovo, quindi su un telefono che resta sotto pressione l'elenco mostra le iniziali per un po'.
```

- [x] **Step 2: The two user READMEs mention the new file in the build section**

In `README.md`, replace:

```
`tools/check-memory.js` fails the build if a call site forgets the decode width, if
one asks for more pixels than the screen can show, or if `ImageHelper` sets
`DecodePixelWidth` after the decode.
```

with:

```
`tools/check-memory.js` fails the build if a call site forgets the decode width, if
one asks for more pixels than the screen can show, if `ImageHelper` sets
`DecodePixelWidth` after the decode, if the row cache starts carrying picture bytes,
if the avatar cache loses its caps or its serial queue, or if the avatar cache is not
read before the cached rows are applied.
```

In `README.it.md`, the mirrored sentence:

```
`tools/check-memory.js` fa fallire la build se un punto di chiamata dimentica la
misura di decodifica, se chiede piu' pixel di quanti lo schermo ne mostri, se
`ImageHelper` imposta `DecodePixelWidth` dopo la decodifica, se la copia dell'elenco
comincia a portarsi dietro i byte delle immagini, se la cache degli avatar perde i
suoi tetti o la coda di scrittura, o se la cache degli avatar non viene letta prima
delle righe salvate.
```

(the second sentence of each paragraph is already there: only the list grows, and the following lines are unchanged.)

- [x] **Step 3: The gotcha in maintain-the-app**

In `.agents/skills/maintain-the-app/SKILL.md`, in the gotchas list, add an entry after the one about the row cache (`chats.json`):

```
- **The pictures have a cache of their own, and it is read before the rows.** The row
  cache (`ChatCache`, `chats.json`) leaves `AvatarData` out on purpose: it is the file the
  app reads before the connection exists, and one picture per chat would multiply it.
  The bytes live in `AvatarCache` (`avatar-cache.json`), capped at 40 chats, 150000
  characters per picture and 1500000 for the file, written through the same
  `SerialQueue` as `ChatPreferences` (two writes on the same file, launched without
  waiting, can land out of order - that bug was already fixed once in
  `ChatPreferences`). `DataService.LoadCachedChatsAsync` awaits `AvatarCache.LoadAsync()`
  before the first `ApplyChat`, because `ApplyChat` asks the cache for the picture of a
  row that carries none. `tools/check-memory.js` fails on all three of these.
```

- [x] **Step 4: Counts and checks in test-the-app**

In `.agents/skills/test-the-app/SKILL.md`:

- the guard line becomes
  `node tools/check-memory.js      # no bitmap decoded bigger than it is drawn, and the picture caches stay bounded`;
- the `check-memory.js` row of the guard table gains: "the row cache carrying picture bytes, the avatar cache without its caps or without the serial queue, and the avatar cache read after the cached rows - a restart then shows initials";
- the counts become `38 C# files`, `23 inline icon Paths (14 distinct icons)`, `20 buttons`, `57 tests in tools/test`, `143 adapter tests`, `125 keys in each .resw`;
- append the on-device checks:

```
53. Restart: close the app from the phone (long-press the back arrow), then open it
    again. The chat list shows the pictures of the conversations you have seen
    before, not the initials, and they are there before the adapter answers (the
    `DIAG ok: connected` line arrives after them).
54. The cache file: `DIAG ok` lists no error for `AvatarCache`; a second launch does
    not rewrite `avatar-cache.json` (its timestamp does not change when nothing new
    arrived).
55. Under pressure: on a 512 MB phone, open and close a few chats until
    `DIAG ok: memory under pressure: releasing decoded images` appears. Go back to
    the chat list: the pictures are drawn again (the bytes were kept), and no new
    `AvatarCache` write happened.
56. A picture the adapter already sent: with the adapter running, switch between
    Chats and Calls a few times. The adapter log shows the avatar requests of the
    first read only - the second chat list is answered from its own cache.
```

- [x] **Step 5: Tick the plan and record what execution changed**

In `docs/superpowers/plans/2026-09-28-cached-contact-pictures.md`, change every completed `- [ ]` to `- [x]`, and append a closing section with what was different from what this plan predicted - at least the printed counts, any function or constant renamed during execution, and anything the device check could not verify (there is no WP8.1 renderer on this machine).

- [x] **Step 6: The whole gate, the build, and the push**

Run:
```bash
perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' README.md README.it.md .agents/skills/maintain-the-app/SKILL.md .agents/skills/test-the-app/SKILL.md docs/superpowers/plans/2026-09-28-cached-contact-pictures.md
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js" && cd WhatsappBridge && npm test 2>&1 | grep -E "^# (pass|fail)"
```
Expected: every guard OK, `# pass 57`, `# pass 143`, `# fail 0`.

Then the three VM build commands (Global Constraints) → `Avvisi: 0`, `Errori: 0`, package created.

- [x] **Step 7: Commit and push**

```bash
git add README.md README.it.md .agents/skills/maintain-the-app/SKILL.md .agents/skills/test-the-app/SKILL.md docs/superpowers/plans/2026-09-28-cached-contact-pictures.md
git commit -m "$(cat <<'EOF'
docs: the pictures have a cache of their own

The limits list said the app asks for the avatars again later, which is only
half true: nothing asked again until the adapter sent the rows. The READMEs,
the maintain-the-app gotcha and the on-device checks now say where the bytes
are kept, how big the cache is allowed to grow, and what a restart and a
memory spike are supposed to look like.
EOF
)"
git push origin master 2>&1 | tail -3
```

---

## Self-Review

**Spec coverage** - the request is "make the contact pictures be cached too":
- the app keeps them across a restart: Task 1 (`AvatarCache` + `ApplyChat` + the load order);
- they come back after a memory spike: Task 2 (`RestoreAvatars` + the page hook);
- the adapter does not re-download them: Task 3 (`avatar-cache.js` + `gowa-client`);
- nothing grows without a cap: Task 1 (the three constants, `Evict`)
- the guards keep it that way: Task 1 (three rules + 10 tests)
- the adapter half reaches the Docker mirror: Task 3, Step 9-10;
- the docs and the counts: Task 4.

**Placeholder scan**: every step carries the code or the exact command, and no step says "add error handling" or "similar to Task N". The one value not written down is a printed count in Task 4, which is read from the command output in the same step.

**Type consistency**: `AvatarCache.Get/Remember/LoadAsync/Count` and `MaxChats/MaxEntryChars/MaxTotalChars` are spelled the same in Task 1's code, in the guard rules and in Task 4's docs. `DataService.RestoreAvatars()` is defined in Task 2 and called only in Task 2. Adapter side: `createAvatarCache({ttlMs, missingTtlMs, maxEntries, now})` with `get/put/size`, and `client.avatars` is the only place the client touches it.

---

## What execution changed about this plan

**The counts.** The plan predicted 10 new tests in `tools/test` and 57 in total; the tests it
spelled out are 11, because the two `cachedRowsProblems` fixtures and the "not another file"
case are three tests and not two. The real numbers are **58 tests in `tools/test`** (was 47)
and **144 adapter tests** (was 136: 7 in `avatar-cache.test.js` plus 1 in `gowa-client.test.js`),
with **38 C# files**. `test-the-app` carries the numbers the gate printed, not the ones this
plan guessed.

**The adapter suite refused one of the numbers.** `avatar-cache.test.js` first failed on its
own arithmetic - the expiry test declared `ttlMs: 60000` and then advanced the clock by
`DEFAULT_TTL_MS` (five minutes), so the entry was long expired. The test was wrong, not the
module, and the fix also pins the documented five minutes with a test of its own.

**An existing test had to change its contract, and that is a real behaviour change.**
`avatar() drops a device suffix, and answers null when there is no picture` asserted that two
calls with the same normalised JID make **two** requests. With the cache the second is a miss
by design, so the assertion became `seen.length === 1` and the comment says why: a "no picture"
answer is an answer too, and it is held for a minute. Anyone who reads the cache as an
optimisation that changes nothing should read that test.

**A guard rule found the real file before any code was written.** `cachedRowsProblems` was
added while `DataService.LoadCachedChatsAsync` still did not await `AvatarCache.LoadAsync()`,
and it named the file and the method in its message - the rule is not only a formula that the
fixtures satisfy.

**Three rules went into `check-memory.js` instead of a new script.** The plan put them there
because the picture caches exist for the memory budget, but the file now carries two rules
that are about disk and write order rather than decoded pixels: its success line and its
header say so.

**Not verified here.** There is no WP8.1 renderer on this machine, so nothing in this plan
says a picture was *seen*. The build proves the XAML and the C# compile, the guards prove the
invariants, and the four on-device checks (56-59 in `test-the-app`) are the only evidence for
the part that matters to the user: whether a face appears.

**One thing was left out on purpose.** `ChatsPage.ShowPinPickerAsync` builds a `ListView` of
names only, with no picture, so nothing there needs the cache. If that picker ever grows an
avatar, it goes through `Contact.Avatar`, which `RestoreAvatars` also feeds.
