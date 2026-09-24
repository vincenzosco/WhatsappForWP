# WP8.1 C# 5 Port Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `WhatsappApp.sln` actually compile on the Windows Phone 8.1 toolchain by removing every C# 6/7 construct from the app sources and replacing it with the C# 5 equivalent, without changing any runtime behaviour.

**Architecture:** The WP8.1 project (`WhatsappApp`) is compiled by the legacy C# 5 compiler bundled with the Windows Phone 8.1 toolchain, while `WhatsappServer` (a plain .NET Framework 4.5.1 console app) is compiled by the modern compiler. The 83 build errors are therefore not logic bugs but syntax: expression-bodied members, auto-property initializers, null-conditional operators, string interpolation, pattern matching and inline `out` declarations, plus one genuinely missing closing brace in `Converters/Converters.cs`. This plan ports the syntax mechanically (same fields, same event semantics, same output strings) and adds a Node guard script that fails the build-check in this repo whenever a C# 6/7 construct is reintroduced.

**Tech Stack:** C# 5 (`.cs` sources compiled by the WP8.1 toolchain), Node.js 18+ (guard script, adapter test suite), MSBuild/VS2015 on Windows (authoritative build gate).

## Global Constraints

- Target `Microsoft Windows Phone 8.1` + VS2015 project type `{76F1466A-8B6D-4E39-A767-685A06062A39}`; the app is compiled with the **legacy C# 5 compiler**. Valid C# 5 features (keep using them): `var`, lambdas, object initializers, `??`, `as`, `using`, `async`/`await`, `[CallerMemberName]`, auto-properties **without** initializers, `event EventHandler<T>`.
- Forbidden (must be zero occurrences in `WhatsappApp/**/*.cs` and `WhatsappServer/**/*.cs`): `$"..."` / `$@"..."`, `?.`, `get =>` / `set =>` / `Member =>`, auto-property initializers, `is Type name`, `out int x` (inline declaration), `nameof(...)`, `_ = Something(...)` (discard assignment), `using static`.
- No behaviour change: identical event-raise semantics, identical user-visible strings (concatenation instead of interpolation produces the exact same text), identical default values.
- Do not touch `WhatsappBridge/**` (29/29 `node --test` tests pass — must stay green) and do not change the AES passphrase or the wire protocol.
- Do not add new files to `WhatsappApp/WhatsappApp.csproj`: the project lists its `<Compile>` items explicitly, and no new `.cs` file is created by this plan. The guard script lives outside the solution (`tools/check-csharp5.js`).
- UI strings and code comments stay Italian.
- Authoritative gate: `msbuild WhatsappApp.sln /p:Configuration=Debug /p:Platform=AnyCPU` on Windows. Everything runnable on macOS is a proxy for it.

## File Structure

| File | Responsibility | Change |
| --- | --- | --- |
| `tools/check-csharp5.js` | **Create.** Static guard: scans every `.cs` file of the solution for C# 6/7 syntax, exits `1` with a report. | new |
| `WhatsappApp/Models/ServerConfig.cs` | Connection defaults model. | auto-property initializers → constructor |
| `WhatsappApp/Models/Contact.cs` | Contact view model (`INotifyPropertyChanged`). | `get =>` ×10, `PropertyChanged?.Invoke` |
| `WhatsappApp/Models/ChatMessage.cs` | Message + control-frame DTO (`DataContract`). | `get =>` ×19, `IsOutgoing`/`IsMedia`, `PropertyChanged?.Invoke` |
| `WhatsappApp/Converters/Converters.cs` | XAML value converters. | **missing closing `}` of the namespace** |
| `WhatsappApp/Services/CommunicationService.cs` | Encrypted TCP transport + control frames. | `Instance =>`, 5 `=>` props, 2 auto-prop initializers, 9 interpolations, `?.` ×14, `_ =` ×1 |
| `WhatsappApp/Services/DataService.cs` | Contacts/messages store. | `Instance =>`, `Contacts =>`, 3 `get =>`, `PropertyChanged?.Invoke` |
| `WhatsappApp/Pages/ConnectionPage.xaml.cs` | Server address + WhatsApp login UI. | `?.` ×2, 5 interpolations, `_ =` ×1 |
| `WhatsappApp/Pages/ChatPage.xaml.cs` | Chat UI. | `e.Parameter is Contact`, `?.` ×3, 1 interpolation |
| `WhatsappApp/MainPage.xaml.cs` | Contact list + new-chat dialog. | `is Contact`, `_ =` ×1 |
| `WhatsappServer/Program.cs` | Legacy desktop relay (same solution). | 10 interpolations, `?.` ×3, `out int`, `_ =` ×1 |

Task order is safe: each task only edits its own file, and the guard script (Task 1) gives an independent per-file verdict.

---

### Task 1: Static C# 5 guard script

**Files:**
- Create: `tools/check-csharp5.js`

**Interfaces:**
- Consumes: nothing.
- Produces: `node tools/check-csharp5.js` → exit `0` ("OK: N C# file(s) are C# 5 compatible.") or exit `1` with `<file>:<line>: <rule>  ->  <source line>` for every violation. Every later task uses this command as its test.

- [ ] **Step 1: Write the guard script**

```js
#!/usr/bin/env node
/**
 * tools/check-csharp5.js
 *
 * Guard for the Windows Phone 8.1 app: the WP8.1 toolchain compiles the app
 * with the legacy C# 5 compiler, so any C# 6/7 syntax in WhatsappApp/**/*.cs
 * (or WhatsappServer/**/*.cs) breaks the build with errors such as
 * "Invalid token '=' in class, struct, or interface member declaration"
 * and "Unexpected character '$'".
 *
 * Usage:  node tools/check-csharp5.js
 * Exit code 0 = every file is C# 5 compatible, 1 = violations found.
 */
'use strict';

const fs = require('fs');
const path = require('path');

const PROJECT_ROOT = path.resolve(__dirname, '..');
const SCAN_ROOTS = ['WhatsappApp', 'WhatsappServer'];
const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git', 'AppPackages']);

const RULES = [
  { name: 'interpolated string', re: /\$@?"/ },
  { name: 'null-conditional operator ?.', re: /\?\./ },
  { name: 'expression-bodied property', re: /^\s*(public|private|protected|internal)[^;{}()]*=>/ },
  { name: 'expression-bodied method', re: /^\s*(public|private|protected|internal)[^;{}]*\)\s*=>/ },
  { name: 'expression-bodied accessor', re: /^\s*(get|set)\s*=>/ },
  { name: 'auto-property initializer', re: /\{[^{}]*\bget;[^{}]*\bset;[^{}]*\}\s*=/ },
  { name: 'inline out variable declaration', re: /\bout\s+(var|int|string|bool|long|byte|double|float)\s+\w+\s*[,)]/ },
  { name: 'pattern matching (is Type name)', re: /\bis\s+[A-Z]\w*\s+[a-z]\w*\s*[,)]/ },
  { name: 'nameof(...)', re: /\bnameof\s*\(/ },
  { name: 'discard assignment (_ = ...)', re: /^\s*_+\s*=[^=]/ },
  { name: 'using static', re: /^\s*using\s+static\s/ }
];

function walk(dir, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name)) continue;
      walk(path.join(dir, entry.name), out);
    } else if (entry.name.endsWith('.cs')) {
      out.push(path.join(dir, entry.name));
    }
  }
  return out;
}

const files = [];
for (const root of SCAN_ROOTS) {
  const abs = path.join(PROJECT_ROOT, root);
  if (fs.existsSync(abs)) walk(abs, files);
}

let violations = 0;
for (const file of files) {
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  const rel = path.relative(PROJECT_ROOT, file);
  for (let i = 0; i < lines.length; i++) {
    for (const rule of RULES) {
      if (rule.re.test(lines[i])) {
        violations++;
        console.log(rel + ':' + (i + 1) + ': ' + rule.name + '  ->  ' + lines[i].trim());
      }
    }
  }
}

if (violations > 0) {
  console.log('\n' + violations + ' C# 6/7 construct(s) found in ' + files.length + ' file(s).');
  console.log('The WP8.1 toolchain needs C# 5 syntax (see docs/superpowers/plans/2026-09-24-wp81-csharp5-port.md).');
  process.exit(1);
}

console.log('OK: ' + files.length + ' C# file(s) are C# 5 compatible.');
```

- [ ] **Step 2: Run the guard to verify it fails**

Run: `node tools/check-csharp5.js`
Expected: FAIL (exit 1) with a long report; must include at least
`WhatsappApp/Models/ServerConfig.cs:11: auto-property initializer`, `WhatsappApp/Models/Contact.cs:21: expression-bodied accessor`, `WhatsappApp/Services/CommunicationService.cs:114: interpolated string`, `WhatsappApp/MainPage.xaml.cs:50: pattern matching (is Type name)`.

- [ ] **Step 3: Commit**

```bash
git add tools/check-csharp5.js
git commit -m "test: add C# 5 syntax guard for the WP8.1 app"
```

---

### Task 2: `Models/ServerConfig.cs`

**Files:**
- Modify: `WhatsappApp/Models/ServerConfig.cs:11-15`

**Interfaces:**
- Consumes: nothing.
- Produces: `ServerConfig` with the same five auto-properties (`Mode`, `ServerAddress`, `ServerPort`, `Username`, `DeviceName`) and the same default values, now applied by a parameterless constructor. No other file references `ServerConfig`, so no consumer code changes.

- [ ] **Step 1: Replace the five auto-property initializers**

Old (lines 11-15):

```csharp
        public ConnectionMode Mode { get; set; } = ConnectionMode.ClientMode;
        public string ServerAddress { get; set; } = "192.168.1.100";
        public int ServerPort { get; set; } = 8585;
        public string Username { get; set; } = "";
        public string DeviceName { get; set; } = "Windows Phone";
```

New:

```csharp
        public ConnectionMode Mode { get; set; }
        public string ServerAddress { get; set; }
        public int ServerPort { get; set; }
        public string Username { get; set; }
        public string DeviceName { get; set; }

        public ServerConfig()
        {
            Mode = ConnectionMode.ClientMode;
            ServerAddress = "192.168.1.100";
            ServerPort = 8585;
            Username = "";
            DeviceName = "Windows Phone";
        }
```

- [ ] **Step 2: Run the guard and check the file is clean**

Run: `node tools/check-csharp5.js | grep ServerConfig`
Expected: no output (exit code of the pipe may be 0 or 1 — what matters is that no `ServerConfig.cs` line is printed).

- [ ] **Step 3: Commit**

```bash
git add WhatsappApp/Models/ServerConfig.cs
git commit -m "refactor: replace ServerConfig auto-property initializers with a constructor"
```

---

### Task 3: `Models/Contact.cs`

**Files:**
- Modify: `WhatsappApp/Models/Contact.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: unchanged public surface — `Id`, `Name`, `Status`, `LastMessage`, `LastMessageTime`, `Initials`, `AvatarColor`, `IsOnline`, `UnreadCount`, `AvatarUri`, `event PropertyChangedEventHandler PropertyChanged`. Only the accessor bodies change.

- [ ] **Step 1: Convert the ten expression-bodied getters**

Apply these ten exact one-line replacements (each `get => _x;` becomes `get { return _x; }`, indentation 12 spaces):

| Old | New |
| --- | --- |
| `            get => _id;` | `            get { return _id; }` |
| `            get => _name;` | `            get { return _name; }` |
| `            get => _status;` | `            get { return _status; }` |
| `            get => _lastMessage;` | `            get { return _lastMessage; }` |
| `            get => _lastMessageTime;` | `            get { return _lastMessageTime; }` |
| `            get => _initials;` | `            get { return _initials; }` |
| `            get => _avatarColor;` | `            get { return _avatarColor; }` |
| `            get => _isOnline;` | `            get { return _isOnline; }` |
| `            get => _unreadCount;` | `            get { return _unreadCount; }` |
| `            get => _avatarUri;` | `            get { return _avatarUri; }` |

- [ ] **Step 2: Replace the null-conditional event raise**

Old:

```csharp
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
```

New:

```csharp
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
```

- [ ] **Step 3: Run the guard and check the file is clean**

Run: `node tools/check-csharp5.js | grep Contact.cs`
Expected: no output.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Models/Contact.cs
git commit -m "refactor: port Contact.cs to C# 5 syntax"
```

---

### Task 4: `Models/ChatMessage.cs`

**Files:**
- Modify: `WhatsappApp/Models/ChatMessage.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: unchanged public surface, including the control-frame properties added for the GOWA login flow (`Command`, `State`, `PairCode`, `QrImageData`, `QrDuration`, `AccountJid`) and the computed `IsOutgoing` / `IsMedia` used by XAML bindings. `ToJson()`/`FromJson()` are untouched.

- [ ] **Step 1: Convert the nineteen expression-bodied getters**

Each line below becomes `get { return _x; }` (indentation 12 spaces):

`get => _id;` · `get => _text;` · `get => _senderId;` · `get => _senderName;` · `get => _chatId;` · `get => _timestamp;` · `get => _status;` · `get => _type;` · `get => _isIncoming;` · `get => _mediaData;` · `get => _mediaMimeType;` · `get => _mediaFileName;` · `get => _command;` · `get => _state;` · `get => _pairCode;` · `get => _qrImageData;` · `get => _qrDuration;` · `get => _accountJid;` · `get => _formattedTime;`

Example (the `Id` property, before and after):

```csharp
        [DataMember]
        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }
```

```csharp
        [DataMember]
        public string Id
        {
            get { return _id; }
            set { _id = value; OnPropertyChanged(); }
        }
```

- [ ] **Step 2: Convert `IsOutgoing`**

Old:

```csharp
        // For XAML binding to determine bubble alignment
        public bool IsOutgoing => !IsIncoming;
```

New:

```csharp
        // For XAML binding to determine bubble alignment
        public bool IsOutgoing
        {
            get { return !IsIncoming; }
        }
```

- [ ] **Step 3: Convert `IsMedia`**

Old:

```csharp
        // Convenience property: Is this message a media type (image/audio)?
        public bool IsMedia => Type == MessageType.Image || Type == MessageType.Audio;
```

New:

```csharp
        // Convenience property: Is this message a media type (image/audio)?
        public bool IsMedia
        {
            get { return Type == MessageType.Image || Type == MessageType.Audio; }
        }
```

- [ ] **Step 4: Replace the null-conditional event raise**

Old:

```csharp
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
```

New:

```csharp
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
```

- [ ] **Step 5: Run the guard and check the file is clean**

Run: `node tools/check-csharp5.js | grep ChatMessage.cs`
Expected: no output.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Models/ChatMessage.cs
git commit -m "refactor: port ChatMessage.cs to C# 5 syntax"
```

---

### Task 5: `Converters/Converters.cs` (missing namespace brace)

**Files:**
- Modify: `WhatsappApp/Converters/Converters.cs` (append at end)

**Interfaces:**
- Consumes: nothing.
- Produces: a file whose braces balance, so `namespace WhatsappApp.Converters` actually closes. This is the only *real* defect behind error 80 (`} expected ... line 239`). The converter class names stay exactly as the XAML files reference them (`BoolToVisibilityConverter`, `MessageStatusToStringConverter`, `MessageStatusToColorConverter`, `InitialToColorConverter`, `UnreadCountToVisibilityConverter`, `OnlineStatusConverter`, `OnlineToDotColorConverter`, `Base64ToImageSourceConverter`, `MessageTypeToImageVisibilityConverter`, `MessageTypeToTextVisibilityConverter`).

- [ ] **Step 1: Append the missing brace**

The file currently ends with the closing brace of `MessageTypeToTextVisibilityConverter` followed by blank lines. Append a single line containing the namespace's closing brace so the tail becomes:

```csharp
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
```

(one `}` for the class, one for the namespace — the class brace already exists, the namespace brace is the new line).

- [ ] **Step 2: Verify the braces balance**

Run:

```bash
awk '{o+=gsub(/\{/,"{"); c+=gsub(/\}/,"}")} END{print o" open / "c" close"}' WhatsappApp/Converters/Converters.cs
```

Expected: `29 open / 29 close` (equal counts; the exact number depends on the file, the point is `open == close`).

- [ ] **Step 3: Run the guard and check the file is clean**

Run: `node tools/check-csharp5.js | grep Converters.cs`
Expected: no output.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Converters/Converters.cs
git commit -m "fix: close the Converters namespace brace"
```

---

### Task 6: `Services/CommunicationService.cs`

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs`

**Interfaces:**
- Consumes: `ChatMessage`, `MessageType`, `CryptoHelper`.
- Produces: unchanged public surface — `static CommunicationService Instance`, `IsConnected`, `IsServerMode`, `MyUserId`, `MyUsername`, `ServerAddress`, `WhatsAppState`, `AccountJid`, events `MessageReceived`, `ControlMessageReceived`, `ConnectionStatusChanged`, `ErrorOccurred`, and the methods the pages call: `ConnectToServerAsync(string, int, string)`, `SendMessageAsync(ChatMessage)`, `SendControlAsync(string, string = null)`, `Disconnect()`. New **private** helpers `RaiseConnectionStatusChanged`, `RaiseErrorOccurred`, `RaiseMessageReceived`, `RaiseControlMessageReceived` replace every `Event?.Invoke(...)`.

- [ ] **Step 1: Replace the expression-bodied singleton**

Old:

```csharp
        private static CommunicationService _instance;
        public static CommunicationService Instance => _instance ?? (_instance = new CommunicationService());
```

New:

```csharp
        private static CommunicationService _instance;
        public static CommunicationService Instance
        {
            get
            {
                if (_instance == null) _instance = new CommunicationService();
                return _instance;
            }
        }
```

- [ ] **Step 2: Replace the five expression-bodied properties**

Old:

```csharp
        public bool IsConnected => _isConnected;
        public bool IsServerMode => _isServerMode;
        public string MyUserId => _myUserId;
        public string MyUsername => _myUsername;
        public string ServerAddress => _serverAddress;
```

New:

```csharp
        public bool IsConnected
        {
            get { return _isConnected; }
        }
        public bool IsServerMode
        {
            get { return _isServerMode; }
        }
        public string MyUserId
        {
            get { return _myUserId; }
        }
        public string MyUsername
        {
            get { return _myUsername; }
        }
        public string ServerAddress
        {
            get { return _serverAddress; }
        }
```

- [ ] **Step 3: Move the two auto-property initializers into the constructor**

Old:

```csharp
        /// <summary>Stato della connessione WhatsApp: "disconnected", "waiting" o "connected".</summary>
        public string WhatsAppState { get; private set; } = "disconnected";

        /// <summary>JID dell'account WhatsApp collegato (vuoto se non connesso).</summary>
        public string AccountJid { get; private set; } = "";

        private CommunicationService() { }
```

New:

```csharp
        /// <summary>Stato della connessione WhatsApp: "disconnected", "waiting" o "connected".</summary>
        public string WhatsAppState { get; private set; }

        /// <summary>JID dell'account WhatsApp collegato (vuoto se non connesso).</summary>
        public string AccountJid { get; private set; }

        private CommunicationService()
        {
            WhatsAppState = "disconnected";
            AccountJid = "";
        }
```

- [ ] **Step 4: Add the four event-raising helpers**

Insert this block immediately after the `private CommunicationService()` constructor:

```csharp
        private void RaiseConnectionStatusChanged(string status)
        {
            var handler = ConnectionStatusChanged;
            if (handler != null) handler(this, status);
        }

        private void RaiseErrorOccurred(string error)
        {
            var handler = ErrorOccurred;
            if (handler != null) handler(this, error);
        }

        private void RaiseMessageReceived(ChatMessage message)
        {
            var handler = MessageReceived;
            if (handler != null) handler(this, message);
        }

        private void RaiseControlMessageReceived(ChatMessage message)
        {
            var handler = ControlMessageReceived;
            if (handler != null) handler(this, message);
        }
```

- [ ] **Step 5: Replace every event raise and interpolation**

Apply these exact replacements (each removes both a `?.` and an interpolated string):

| Old | New |
| --- | --- |
| `ConnectionStatusChanged?.Invoke(this, $"Server avviato sulla porta {port}")` | `RaiseConnectionStatusChanged("Server avviato sulla porta " + port)` |
| `ErrorOccurred?.Invoke(this, $"Errore avvio server: {ex.Message}")` | `RaiseErrorOccurred("Errore avvio server: " + ex.Message)` |
| `ConnectionStatusChanged?.Invoke(this, $"Nuovo client connesso ({_serverClients.Count} connessi)")` | `RaiseConnectionStatusChanged("Nuovo client connesso (" + _serverClients.Count + " connessi)")` |
| `ErrorOccurred?.Invoke(this, $"Client disconnesso: {ex.Message}")` | `RaiseErrorOccurred("Client disconnesso: " + ex.Message)` |
| `ConnectionStatusChanged?.Invoke(this, $"Client rimosso ({_serverClients.Count} connessi)")` | `RaiseConnectionStatusChanged("Client rimosso (" + _serverClients.Count + " connessi)")` |
| `ConnectionStatusChanged?.Invoke(this, "Connessione in corso...")` | `RaiseConnectionStatusChanged("Connessione in corso...")` |
| `ConnectionStatusChanged?.Invoke(this, "Connesso al server")` | `RaiseConnectionStatusChanged("Connesso al server")` |
| `ErrorOccurred?.Invoke(this, $"Errore connessione: {ex.Message}")` | `RaiseErrorOccurred("Errore connessione: " + ex.Message)` |
| `ErrorOccurred?.Invoke(this, $"Connessione persa: {ex.Message}")` | `RaiseErrorOccurred("Connessione persa: " + ex.Message)` |
| `ConnectionStatusChanged?.Invoke(this, "Disconnesso")` (in `ListenForMessagesAsync`) | `RaiseConnectionStatusChanged("Disconnesso")` |
| `DispatchOnUiThread(() => ErrorOccurred?.Invoke(this, "Non connesso"));` | `DispatchOnUiThread(() => RaiseErrorOccurred("Non connesso"));` |
| `ErrorOccurred?.Invoke(this, $"Errore invio: {ex.Message}")` | `RaiseErrorOccurred("Errore invio: " + ex.Message)` |
| `DispatchOnUiThread(() => ControlMessageReceived?.Invoke(this, message));` | `DispatchOnUiThread(() => RaiseControlMessageReceived(message));` |
| `DispatchOnUiThread(() => MessageReceived?.Invoke(this, message));` | `DispatchOnUiThread(() => RaiseMessageReceived(message));` |
| `ErrorOccurred?.Invoke(this, $"Errore decifratura messaggio: {ex.Message}")` | `RaiseErrorOccurred("Errore decifratura messaggio: " + ex.Message)` |
| `DispatchOnUiThread(() => ConnectionStatusChanged?.Invoke(this, "Disconnesso"));` (in `Disconnect`) | `DispatchOnUiThread(() => RaiseConnectionStatusChanged("Disconnesso"));` |

- [ ] **Step 6: Replace the discard assignment in `ConnectToServerAsync`**

Old:

```csharp
                // Start listening for incoming messages on a background thread
                _ = Task.Run(() => ListenForMessagesAsync());
```

New:

```csharp
                // Start listening for incoming messages on a background thread
#pragma warning disable 4014
                Task.Run(() => ListenForMessagesAsync());
#pragma warning restore 4014
```

- [ ] **Step 7: Replace the null-conditional disposals in `Disconnect`**

Old:

```csharp
            try
            {
                _writer?.Dispose();
                _reader?.Dispose();
                _clientSocket?.Dispose();
                _serverListener?.Dispose();
            }
            catch { }
```

New:

```csharp
            try
            {
                if (_writer != null) _writer.Dispose();
                if (_reader != null) _reader.Dispose();
                if (_clientSocket != null) _clientSocket.Dispose();
                if (_serverListener != null) _serverListener.Dispose();
            }
            catch { }
```

- [ ] **Step 8: Run the guard and check the file is clean**

Run: `node tools/check-csharp5.js | grep CommunicationService.cs`
Expected: no output.

- [ ] **Step 9: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs
git commit -m "refactor: port CommunicationService.cs to C# 5 syntax"
```

---

### Task 7: `Services/DataService.cs`

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs`

**Interfaces:**
- Consumes: `Contact`, `ChatMessage`, `CommunicationService.Instance.MessageReceived`, `CommunicationService.Instance.ControlMessageReceived`.
- Produces: unchanged public surface — `static DataService Instance`, `ObservableCollection<Contact> Contacts`, `SelectedContact`, `ConnectionStatus`, `IsServerRunning`, `GetMessages(string)`, `AddMessage(string, ChatMessage)`, `ClearUnread(string)`, `AddContact(Contact)`, `static string DisplayNameForJid(string)`.

- [ ] **Step 1: Replace the expression-bodied singleton**

Old:

```csharp
        private static DataService _instance;
        public static DataService Instance => _instance ?? (_instance = new DataService());
```

New:

```csharp
        private static DataService _instance;
        public static DataService Instance
        {
            get
            {
                if (_instance == null) _instance = new DataService();
                return _instance;
            }
        }
```

- [ ] **Step 2: Replace `Contacts` and the three `get =>` accessors**

Old:

```csharp
        public ObservableCollection<Contact> Contacts => _contacts;
        public Contact SelectedContact
        {
            get => _selectedContact;
            set { _selectedContact = value; OnPropertyChanged(); }
        }
        public string ConnectionStatus
        {
            get => _connectionStatus;
            set { _connectionStatus = value; OnPropertyChanged(); }
        }
        public bool IsServerRunning
        {
            get => _isServerRunning;
            set { _isServerRunning = value; OnPropertyChanged(); }
        }
```

New:

```csharp
        public ObservableCollection<Contact> Contacts
        {
            get { return _contacts; }
        }
        public Contact SelectedContact
        {
            get { return _selectedContact; }
            set { _selectedContact = value; OnPropertyChanged(); }
        }
        public string ConnectionStatus
        {
            get { return _connectionStatus; }
            set { _connectionStatus = value; OnPropertyChanged(); }
        }
        public bool IsServerRunning
        {
            get { return _isServerRunning; }
            set { _isServerRunning = value; OnPropertyChanged(); }
        }
```

- [ ] **Step 3: Replace the null-conditional event raise**

Old:

```csharp
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
```

New:

```csharp
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
```

- [ ] **Step 4: Run the guard and check the file is clean**

Run: `node tools/check-csharp5.js | grep DataService.cs`
Expected: no output.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Services/DataService.cs
git commit -m "refactor: port DataService.cs to C# 5 syntax"
```

---

### Task 8: `Pages/ConnectionPage.xaml.cs`

**Files:**
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml.cs`

**Interfaces:**
- Consumes: `SettingsService`, `CommunicationService.Instance` (`IsConnected`, `WhatsAppState`, `AccountJid`, `ConnectToServerAsync`, `SendControlAsync`, events), `ChatMessage` (`Command`, `State`, `AccountJid`, `QrImageData`, `QrDuration`, `PairCode`, `Text`).
- Produces: unchanged XAML contract — every `x:Name` the XAML exposes (`ServerAddressBox`, `ServerPortBox`, `UsernameBox`, `PageTitleText`, `StatusPanel`, `StatusText`, `ActionButton`, `DisconnectButton`, `WhatsAppPanel`, `WhatsAppStateText`, `LoginQrButton`, `LoginCodeButton`, `PhoneBox`, `PairCodeText`, `QrImage`, `QrInfoText`, `ContinueButton`) is still driven by the same handlers: `ActionButton_Click`, `LoginQrButton_Click`, `LoginCodeButton_Click`, `ContinueButton_Click`, `DisconnectButton_Click`, `BackButton_Click`.

- [ ] **Step 1: Replace the two null-conditional text reads**

Old (in `ActionButton_Click`):

```csharp
            string username = UsernameBox.Text?.Trim();
```

New:

```csharp
            string username = (UsernameBox.Text ?? "").Trim();
```

Old:

```csharp
            string address = ServerAddressBox.Text?.Trim();
```

New:

```csharp
            string address = (ServerAddressBox.Text ?? "").Trim();
```

- [ ] **Step 2: Replace the first interpolated string**

Old:

```csharp
            StatusText.Text = $"Connessione a {address}:{port}...";
```

New:

```csharp
            StatusText.Text = "Connessione a " + address + ":" + port + "...";
```

- [ ] **Step 3: Replace the login-state interpolated string**

Old:

```csharp
                    WhatsAppStateText.Text = string.IsNullOrEmpty(accountJid)
                        ? "WhatsApp connesso!"
                        : $"Connesso come {accountJid.Split('@')[0]}";
```

New:

```csharp
                    WhatsAppStateText.Text = string.IsNullOrEmpty(accountJid)
                        ? "WhatsApp connesso!"
                        : "Connesso come " + accountJid.Split('@')[0];
```

- [ ] **Step 4: Replace the pairing-code interpolated string**

Old:

```csharp
                    PairCodeText.Text = $"Codice: {message.PairCode}";
```

New:

```csharp
                    PairCodeText.Text = "Codice: " + message.PairCode;
```

- [ ] **Step 5: Replace the QR hint interpolated string**

Old:

```csharp
                QrInfoText.Text = duration > 0
                    ? $"Apri WhatsApp > Dispositivi collegati > Collega un dispositivo e inquadra il codice (valido ~{duration}s)."
                    : "Apri WhatsApp > Dispositivi collegati > Collega un dispositivo e inquadra il codice.";
```

New:

```csharp
                QrInfoText.Text = duration > 0
                    ? "Apri WhatsApp > Dispositivi collegati > Collega un dispositivo e inquadra il codice (valido ~" + duration + "s)."
                    : "Apri WhatsApp > Dispositivi collegati > Collega un dispositivo e inquadra il codice.";
```

- [ ] **Step 6: Replace the QR error interpolated string**

Old:

```csharp
                QrInfoText.Text = $"Impossibile mostrare il QR code: {ex.Message}";
```

New:

```csharp
                QrInfoText.Text = "Impossibile mostrare il QR code: " + ex.Message;
```

- [ ] **Step 7: Replace the discard assignment in `OnNavigatedTo`**

Old:

```csharp
                _ = CommunicationService.Instance.SendControlAsync("status");
```

New:

```csharp
                // OnNavigatedTo is not async: fire the status request and ignore the task
                CommunicationService.Instance.SendControlAsync("status");
```

- [ ] **Step 8: Run the guard and check the file is clean**

Run: `node tools/check-csharp5.js | grep ConnectionPage`
Expected: no output.

- [ ] **Step 9: Commit**

```bash
git add WhatsappApp/Pages/ConnectionPage.xaml.cs
git commit -m "refactor: port ConnectionPage to C# 5 syntax"
```

---

### Task 9: `Pages/ChatPage.xaml.cs`

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`

**Interfaces:**
- Consumes: `Contact`, `ChatMessage`, `MessageType`, `MessageStatus`, `DataService.Instance`, `CommunicationService.Instance`.
- Produces: unchanged handlers (`SendButton_Click`, `MessageTextBox_KeyDown`, `BackButton_Click`, `AttachButton_Click`, `ClearImageButton_Click`), same navigation contract (`e.Parameter` is a `Contact`).

- [ ] **Step 1: Replace the pattern match in `OnNavigatedTo`**

Old:

```csharp
            if (e.Parameter is Contact contact)
            {
                _contact = contact;
```

New:

```csharp
            var contact = e.Parameter as Contact;
            if (contact != null)
            {
                _contact = contact;
```

- [ ] **Step 2: Replace the null-conditional text read**

Old:

```csharp
            string text = MessageTextBox.Text?.Trim();
```

New:

```csharp
            string text = (MessageTextBox.Text ?? "").Trim();
```

- [ ] **Step 3: Replace the null-conditional file-type chain**

Old:

```csharp
            string extension = _selectedImageFile?.FileType?.ToLower();
```

New:

```csharp
            string extension = _selectedImageFile == null ? null : _selectedImageFile.FileType.ToLower();
```

- [ ] **Step 4: Replace the null-conditional file name**

Old:

```csharp
                MediaFileName = _selectedImageFile?.Name
```

New:

```csharp
                MediaFileName = _selectedImageFile == null ? null : _selectedImageFile.Name
```

- [ ] **Step 5: Replace the interpolated debug line**

Old:

```csharp
                System.Diagnostics.Debug.WriteLine($"Errore selezione immagine: {ex.Message}");
```

New:

```csharp
                System.Diagnostics.Debug.WriteLine("Errore selezione immagine: " + ex.Message);
```

- [ ] **Step 6: Run the guard and check the file is clean**

Run: `node tools/check-csharp5.js | grep ChatPage`
Expected: no output.

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml.cs
git commit -m "refactor: port ChatPage to C# 5 syntax"
```

---

### Task 10: `MainPage.xaml.cs`

**Files:**
- Modify: `WhatsappApp/MainPage.xaml.cs`

**Interfaces:**
- Consumes: `DataService.Instance.Contacts`, `DataService.Instance.AddContact`, `DataService.Instance.DisplayNameForJid` (not used here), `CommunicationService.Instance.IsConnected` / `SendControlAsync`, `ChatPage`.
- Produces: unchanged behaviour — selecting a contact navigates to `ChatPage` with the `Contact` as parameter; "Nuova chat" normalises the typed number to a JID (`...@s.whatsapp.net`) and reuses an existing contact when present.

- [ ] **Step 1: Replace the discard assignment in `OnNavigatedTo`**

Old:

```csharp
            if (CommunicationService.Instance.IsConnected)
                _ = CommunicationService.Instance.SendControlAsync("contacts");
```

New:

```csharp
            // OnNavigatedTo is not async: fire the contacts request and ignore the task
            if (CommunicationService.Instance.IsConnected)
                CommunicationService.Instance.SendControlAsync("contacts");
```

- [ ] **Step 2: Replace the pattern match in `ChatListView_SelectionChanged`**

Old:

```csharp
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is Contact contact)
            {
                Frame.Navigate(typeof(ChatPage), contact);
                ChatListView.SelectedItem = null; // Reset selection
            }
```

New:

```csharp
            if (e.AddedItems.Count > 0)
            {
                var contact = e.AddedItems[0] as Contact;
                if (contact != null)
                {
                    Frame.Navigate(typeof(ChatPage), contact);
                    ChatListView.SelectedItem = null; // Reset selection
                }
            }
```

- [ ] **Step 3: Run the guard and check the file is clean**

Run: `node tools/check-csharp5.js | grep MainPage`
Expected: no output.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/MainPage.xaml.cs
git commit -m "refactor: port MainPage to C# 5 syntax"
```

---

### Task 11: `WhatsappServer/Program.cs`

**Files:**
- Modify: `WhatsappServer/Program.cs`

**Interfaces:**
- Consumes: `System.Net.Sockets` (`TcpListener`, `TcpClient`, `IPEndPoint`, `NetworkStream`).
- Produces: unchanged behaviour of the legacy desktop relay (same console output text, same relay logic). New private helper `static string Describe(IPEndPoint endpoint)` returning `"?"` for `null` and `"<address>:<port>"` otherwise. This keeps the whole solution on one language level even if a future build invokes the legacy toolchain for it.

- [ ] **Step 1: Replace the inline `out` declaration in `Main`**

Old:

```csharp
            int port = 8585;
            if (args.Length > 0 && int.TryParse(args[0], out int customPort))
            {
                port = customPort;
            }
```

New:

```csharp
            int port = 8585;
            int customPort;
            if (args.Length > 0 && int.TryParse(args[0], out customPort))
            {
                port = customPort;
            }
```

- [ ] **Step 2: Replace the startup interpolated strings**

Old:

```csharp
            Console.WriteLine($"Avvio server sulla porta {port}...");
            Console.WriteLine($"In attesa di connessioni...\n");
```

New:

```csharp
            Console.WriteLine("Avvio server sulla porta " + port + "...");
            Console.WriteLine("In attesa di connessioni...\n");
```

Old:

```csharp
                Console.WriteLine($"Server avviato! IP locale: {GetLocalIPAddress()}");
                Console.WriteLine($"I client possono connettersi con: {GetLocalIPAddress()}:{port}\n");
```

New:

```csharp
                Console.WriteLine("Server avviato! IP locale: " + GetLocalIPAddress());
                Console.WriteLine("I client possono connettersi con: " + GetLocalIPAddress() + ":" + port + "\n");
```

- [ ] **Step 3: Add the `Describe` helper**

Insert immediately above `private static async Task HandleClientAsync(...)`:

```csharp
        private static string Describe(IPEndPoint endpoint)
        {
            if (endpoint == null) return "?";
            return endpoint.Address + ":" + endpoint.Port;
        }
```

- [ ] **Step 4: Replace the accept-loop interpolations and discard**

Old:

```csharp
                    var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                    Console.WriteLine($"Nuovo client connesso: {endpoint?.Address}:{endpoint?.Port}");

                    // Handle each client in a separate task
                    var clientId = Guid.NewGuid().ToString("N").Substring(0, 6);
                    _ = Task.Run(() => HandleClientAsync(client, clientId));
```

New:

```csharp
                    var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                    Console.WriteLine("Nuovo client connesso: " + Describe(endpoint));

                    // Handle each client in a separate task
                    var clientId = Guid.NewGuid().ToString("N").Substring(0, 6);
#pragma warning disable 4014
                    Task.Run(() => HandleClientAsync(client, clientId));
#pragma warning restore 4014
```

- [ ] **Step 5: Replace the error and shutdown interpolations**

Old:

```csharp
                Console.WriteLine($"Errore: {ex.Message}");
```

New:

```csharp
                Console.WriteLine("Errore: " + ex.Message);
```

Old:

```csharp
                _server?.Stop();
```

New:

```csharp
                if (_server != null) _server.Stop();
```

- [ ] **Step 6: Replace the per-message log interpolations**

Old:

```csharp
                    Console.WriteLine($"[{timestamp}] Messaggio ricevuto ({json.Length} byte)");
                    Console.WriteLine($"   {json.Substring(0, Math.Min(json.Length, 150))}\n");
```

New:

```csharp
                    Console.WriteLine("[" + timestamp + "] Messaggio ricevuto (" + json.Length + " byte)");
                    Console.WriteLine("   " + json.Substring(0, Math.Min(json.Length, 150)) + "\n");
```

- [ ] **Step 7: Replace the disconnect/finally interpolations**

Old:

```csharp
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                Console.WriteLine($"Client disconnesso: {endpoint?.Address}:{endpoint?.Port} ({ex.Message})");
```

New:

```csharp
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                Console.WriteLine("Client disconnesso: " + Describe(endpoint) + " (" + ex.Message + ")");
```

Old:

```csharp
                Console.WriteLine($"Client rimosso. Connessioni attive: {_clients.Count}\n");
```

New:

```csharp
                Console.WriteLine("Client rimosso. Connessioni attive: " + _clients.Count + "\n");
```

- [ ] **Step 8: Run the guard and check the file is clean**

Run: `node tools/check-csharp5.js | grep WhatsappServer`
Expected: no output.

- [ ] **Step 9: Commit**

```bash
git add WhatsappServer/Program.cs
git commit -m "refactor: port WhatsappServer to C# 5 syntax"
```

---

### Task 12: Whole-solution verification

**Files:**
- Modify: none (verification only; `README.md` gets one line about the guard script).

**Interfaces:**
- Consumes: all previous tasks.
- Produces: a repo state where the guard script passes, the adapter suite still passes, and the Windows build is ready to be re-run by the user.

- [ ] **Step 1: Run the C# 5 guard on the whole solution**

Run: `node tools/check-csharp5.js`
Expected: `OK: <N> C# file(s) are C# 5 compatible.` (exit 0)

- [ ] **Step 2: Re-verify the JSON payload of every ported file no longer contains a `$"`**

Run:

```bash
grep -rn '\$"' --include=*.cs WhatsappApp WhatsappServer | grep -v obj/ || echo "no interpolated strings left"
```

Expected: `no interpolated strings left`

- [ ] **Step 3: Run the adapter regression suite**

Run: `cd WhatsappBridge && npm test; cd ..`
Expected: `# pass 29` / `# fail 0`.

- [ ] **Step 4: Check all touched C# files for balanced braces**

Run:

```bash
for f in WhatsappApp/Models/ServerConfig.cs WhatsappApp/Models/Contact.cs WhatsappApp/Models/ChatMessage.cs WhatsappApp/Converters/Converters.cs WhatsappApp/Services/CommunicationService.cs WhatsappApp/Services/DataService.cs WhatsappApp/Pages/ConnectionPage.xaml.cs WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/MainPage.xaml.cs WhatsappServer/Program.cs; do awk -v f="$f" '{o+=gsub(/\{/,"{"); c+=gsub(/\}/,"}")} END{print f": "o" open / "c" close "(o==c?" OK":" MISMATCH")}' "$f"; done
```

Expected: every line ends with `OK`.

- [ ] **Step 5: Document the guard in the README**

Add to `README.md`, after the build instructions section, this paragraph:

```markdown
## Verifica della compatibilità C# 5 (Windows Phone 8.1)

Il toolchain di Windows Phone 8.1 compila l'app con il compilatore **C# 5**:
la sintassi C# 6/7 (stringhe interpolate, `?.`, proprietà con corpo `=>`,
inizializzatori di proprietà automatiche, pattern matching, `out var`) non
compila. Prima di ogni build eseguire:

```bash
node tools/check-csharp5.js
```

Esce con codice 0 quando tutti i file `.cs` della soluzione sono compatibili
con C# 5, altrimenti elenca file, riga e costrutto da correggere.
```

- [ ] **Step 6: Commit**

```bash
git add README.md
git commit -m "docs: document the C# 5 compatibility guard"
```

- [ ] **Step 7: Hand off the authoritative build**

On Windows (`C:\Mac\Home\Documents\WhatsappForWP`):

```bash
git pull
msbuild WhatsappApp.sln /p:Configuration=Debug /p:Platform=AnyCPU
```

Expected: `0 Error(s)`. If any error remains, run `node tools/check-csharp5.js`
on the same sources first — every remaining C# 5 violation is reported there
with file and line.

---

## Self-Review

**Spec coverage:** every one of the 83 reported errors maps to a task —
`ServerConfig.cs` (errors 1-6 → Task 2), `CommunicationService.cs` (7-10, 24-37,
51, 54, 57, 62, 65, 79, 81, 82, 83 → Task 6), `DataService.cs` (11-14, 17-19,
21, 23 → Task 7), `Contact.cs` (15, 16, 20, 22, 32, 36, 39, 41, 43, 44 → Task 3),
`ChatMessage.cs` (38, 40, 42, 45-50, 52, 55, 56, 58-61, 64, 66, 67, 69, 71-77 →
Task 4), `ConnectionPage.xaml.cs` (47, 53, 63, 68, 70 → Task 8),
`ChatPage.xaml.cs` (78 → Task 9), `Converters.cs` (80 → Task 5). The
C# 6/7 constructs that the reported error list did not reach — `is Contact`
pattern matches (Tasks 9-10), `_ =` discards (Tasks 8, 10, 6, 11) and every
`WhatsappServer/Program.cs` construct (Task 11) — are covered by the guard
script from Task 1 and would otherwise resurface as fresh errors after the
first successful compile.

**Placeholder scan:** no `TBD`/`TODO`/"handle edge cases" steps; every code
change shows both the old and the new text verbatim; the only file created by
the plan (`tools/check-csharp5.js`) is given in full.

**Type consistency:** every signature quoted in an `Interfaces` block matches
the source file it is taken from (`CommunicationService.SendControlAsync(string,
string = null)`, `DataService.AddContact(Contact)`, `Describe(IPEndPoint)`,
`RaiseControlMessageReceived(ChatMessage)`, …). No task introduces a rename.
The helper names are used consistently: `RaiseConnectionStatusChanged`,
`RaiseErrorOccurred`, `RaiseMessageReceived`, `RaiseControlMessageReceived`.
