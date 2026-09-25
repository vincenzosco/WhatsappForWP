# Device Auto-Discovery and Device-Side Login Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the WP8 app find the adapter on the local network by itself and let the whole WhatsApp login happen on the phone (QR and pairing code shown by the app), with the terminal QR left as an optional fallback that always shows something scannable.

**Architecture:** The adapter broadcasts a small UDP beacon (port 8587) containing its TCP port, its hostname and the current WhatsApp state; the app binds that UDP port and uses the *sender* address of the beacon as the server address (the computer has several interfaces, only the sender is guaranteed reachable). Once found, the app connects and shows the login QR full-screen on the device, which is what the user scans with the WhatsApp phone; the terminal drawing becomes a fallback for people who prefer the computer.

**Tech Stack:** Node.js 18.13+ (`dgram`, zero runtime dependencies), C# 5 / XAML for Windows Phone 8.1 (`Windows.Networking.Sockets.DatagramSocket`, `Windows.System.Display.DisplayRequest` — both verified present in `Windows Phone Kits\8.1\References\CommonConfiguration\Neutral\Windows.winmd`), ImageMagick 7 for the terminal QR, Node's `node:test` for the adapter.

## Global Constraints

- **C# 5 only** in `WhatsappApp/`. No `$"..."`, `?.`, expression-bodied members, auto-property initializers, `out int x`, `is Type name`, `nameof`, `_ = ...`, `static async Task Main`. Gate: `node tools/check-csharp5.js`.
- **No icon font.** WP8.1 has no `Segoe MDL2 Assets`; every icon is a `Path` with its geometry inlined as `<Path.Data><PathGeometry>…</PathGeometry></Path.Data>` plus an `<!-- IconX -->` comment. Gate: `node tools/check-icons.js`.
- **No hardcoded user-visible strings.** XAML literals need `x:Uid="Key"` plus `Key.Text` / `Key.Content` / `Key.PlaceholderText` in **both** `.resw` files; C# uses `Loc.Get("Key", "fallback")`. Gate: `node tools/check-resw.js --strict` (also fails on unused keys).
- **Every page and every `.cs`/`.resw` file must be registered in `WhatsappApp/WhatsappApp.csproj`** (a file that is not listed does not exist at build time).
- **`WhatsappBridge/` keeps zero runtime dependencies**; `package.json` `dependencies` stays `{}`; tests are `node:test` via `npm test`.
- **Ports:** adapter TCP `8585` (`BRIDGE_PORT`), adapter webhook `8586` (`WEBHOOK_PORT`), GOWA `3000` (`GOWA_URL`), discovery UDP `8587` (`DISCOVERY_PORT`, new). The app's `DiscoveryService.Port` constant must equal `DISCOVERY_PORT`.
- **Beacon contract, version 1** (exact keys, `DataContractJsonSerializer` is case-sensitive):
  `{"service":"whatsapp-wp8-adapter","version":1,"name":"<hostname>","port":8585,"state":"disconnected|waiting|connected","account":"<jid or empty>"}`
- **Build gate runs only from a local disk on the Windows VM**, never from the Parallels shared folder (that fails with `WMC9999`): `robocopy` to `C:\Temp\wp81`, then `MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`. Expected `0 Error(s)`.
- **The app must not gain NuGet/package references.** The single manifest change allowed is adding the `privateNetworkClientServer` capability (needed for local-network UDP).
- **`.tools/` stays git ignored** (it holds the GOWA binary and the live WhatsApp session).
- Comments and UI strings are Italian in `WhatsappApp/` and `WhatsappBridge/`, English in the guard scripts' docs.

## File Structure

**Adapter (`WhatsappBridge/`)**

- `discovery.js` (create) — pure helpers (`ipv4Broadcast`, `broadcastTargets`, `buildPayload`) plus `createDiscoveryBeacon`: binds a UDP socket, sets broadcast, and emits the beacon on every interface every 2 s. No WhatsApp knowledge: the payload comes from a `getPayload` callback.
- `server.js` (modify) — start the beacon next to the TCP/webhook servers, stop it on shutdown.
- `config.js` (modify) — `discovery: { enabled, port, name }`.
- `test/discovery.test.js` (create), `test/config.test.js` (modify).

**App (`WhatsappApp/`)**

- `Models/DiscoveredServer.cs` (create) — one adapter seen on the network, with the display strings the `ListView` binds to.
- `Models/BeaconPayload.cs` (create) — the DTO for the beacon JSON.
- `Services/DiscoveryService.cs` (create) — UDP listener, TTL cache, `Snapshot()`, `WaitForSingleAsync()`.
- `Services/AutoConnector.cs` (create) — "connect to the saved server, else to the only discovered one"; used by both `App` and `ConnectionPage`.
- `Pages/ConnectionPage.xaml` + `.xaml.cs` (modify) — discovered servers list, manual entry behind a toggle, connection extracted into `ConnectAsync`, full-screen QR overlay, automatic QR request/refresh, screen kept awake.
- `App.xaml.cs` (modify) — reconnect automatically at launch.
- `Package.appxmanifest` (modify) — add `privateNetworkClientServer`.

**Tools / docs**

- `tools/start-login.js` (modify) — `--no-qr` (device-first), and never print a truncated code: write the PNG to a stable path and offer it.
- `.agents/skills/run-the-login-server/SKILL.md`, `README.md`, `WhatsappBridge/README.md`, `.agents/skills/{maintain-the-app,test-the-app}/SKILL.md` (modify).

---

### Task 1: Adapter discovery beacon

**Files:**
- Create: `WhatsappBridge/discovery.js`
- Create: `WhatsappBridge/test/discovery.test.js`
- Modify: `WhatsappBridge/config.js` (add `DISCOVERY_*` defaults and the `discovery` block)
- Modify: `WhatsappBridge/test/config.test.js` (append one test)
- Modify: `WhatsappBridge/server.js:1-40` (imports) and `:250-330` (wiring + shutdown)
- Modify: `WhatsappBridge/.env.example`, `WhatsappBridge/README.md`

**Interfaces:**
- Consumes: `bridge.getState()` from `createBridge` in `server.js`, which returns `{ status: 'disconnected'|'waiting'|'connected', jid: string }`; `log(level, message)`.
- Produces:
  - `ipv4Broadcast(address: string, netmask: string) -> string`
  - `broadcastTargets(interfaces: object) -> string[]`
  - `buildPayload(fields: { name, port, state, account }) -> object`
  - `createDiscoveryBeacon({ port, intervalMs, getPayload, interfaces, socketFactory, log }) -> { socket, sendOnce(), stop() }`
  - config: `loadConfig(env).discovery = { enabled: boolean, port: number, name: string }`

- [x] **Step 1: Write the failing tests**

Create `WhatsappBridge/test/discovery.test.js`:

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const dgram = require('node:dgram');
const { ipv4Broadcast, broadcastTargets, createDiscoveryBeacon } = require('../discovery');

test('ipv4Broadcast calcola l indirizzo di broadcast dalla netmask', () => {
  assert.strictEqual(ipv4Broadcast('192.168.0.86', '255.255.255.0'), '192.168.0.255');
  assert.strictEqual(ipv4Broadcast('10.211.55.2', '255.255.0.0'), '10.211.255.255');
  assert.strictEqual(ipv4Broadcast('127.0.0.1', '255.255.255.255'), '127.0.0.1');
  assert.strictEqual(ipv4Broadcast('192.168.1.5', undefined), '192.168.1.255');
});

test('broadcastTargets salta le interfacce virtuali e aggiunge il broadcast globale', () => {
  const targets = broadcastTargets({
    en0: [{ family: 'IPv4', internal: false, address: '192.168.0.86', netmask: '255.255.255.0' }],
    utun3: [{ family: 'IPv4', internal: false, address: '10.8.0.2', netmask: '255.255.255.255' }],
    lo0: [{ family: 'IPv4', internal: true, address: '127.0.0.1', netmask: '255.0.0.0' }],
  });
  assert.deepStrictEqual(targets, ['192.168.0.255', '255.255.255.255']);
});

test('il beacon invia il payload su ogni interfaccia e si chiude', () => {
  const sent = [];
  const fakeSocket = {
    on() {},
    bind(callback) { callback(); },
    setBroadcast() {},
    send(payload, offset, length, port, target, callback) {
      sent.push({ json: JSON.parse(payload.toString('utf8')), port, target });
      callback(null);
    },
    close() { sent.push({ closed: true }); },
  };

  const beacon = createDiscoveryBeacon({
    port: 8587,
    intervalMs: 1000000,
    getPayload: () => ({
      service: 'whatsapp-wp8-adapter', version: 1, name: 'mac-di-vincenzo',
      port: 8585, state: 'disconnected', account: '',
    }),
    interfaces: { en0: [{ family: 'IPv4', internal: false, address: '192.168.0.86', netmask: '255.255.255.0' }] },
    socketFactory: () => fakeSocket,
    log: () => {},
  });

  assert.strictEqual(sent.length, 2, 'un invio per target');
  assert.strictEqual(sent[0].target, '192.168.0.255');
  assert.strictEqual(sent[0].port, 8587);
  assert.strictEqual(sent[0].json.service, 'whatsapp-wp8-adapter');
  assert.strictEqual(sent[0].json.port, 8585);
  assert.strictEqual(sent[0].json.version, 1);

  beacon.stop();
  assert.strictEqual(sent[sent.length - 1].closed, true);
});

test('il beacon arriva davvero su un socket UDP in ascolto', async () => {
  const received = [];
  const listener = dgram.createSocket('udp4');
  await new Promise((resolve) => listener.bind(0, '127.0.0.1', resolve));
  listener.on('message', (buffer) => received.push(JSON.parse(buffer.toString('utf8'))));

  const beacon = createDiscoveryBeacon({
    port: listener.address().port,
    intervalMs: 50,
    getPayload: () => ({
      service: 'whatsapp-wp8-adapter', version: 1, name: 'test-mac',
      port: 8585, state: 'connected', account: '39@s.whatsapp.net',
    }),
    // una /32 su 127.0.0.1: il broadcast di quella rete e' l'indirizzo stesso,
    // quindi il test e' un giro UDP vero senza uscire dalla loopback
    interfaces: { lo0: [{ family: 'IPv4', internal: false, address: '127.0.0.1', netmask: '255.255.255.255' }] },
    log: () => {},
  });

  await new Promise((resolve) => setTimeout(resolve, 300));
  beacon.stop();
  listener.close();

  assert.ok(received.length >= 1, 'nessun beacon ricevuto');
  assert.strictEqual(received[0].service, 'whatsapp-wp8-adapter');
  assert.strictEqual(received[0].name, 'test-mac');
  assert.strictEqual(received[0].account, '39@s.whatsapp.net');
});
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/discovery.test.js`
Expected: FAIL with `Cannot find module '../discovery'`.

- [x] **Step 3: Write `WhatsappBridge/discovery.js`**

```js
'use strict';

/**
 * Annuncio UDP dell'adapter.
 *
 * L'app WP8 non ha modo di sapere su quale indirizzo sta il computer, e
 * scriverlo a mano e' l'unico passo manuale rimasto. Qui l'adapter si annuncia
 * su ogni interfaccia fisica ogni due secondi; l'app ascolta e usa l'indirizzo
 * del *mittente* come indirizzo del server: il computer puo' avere piu'
 * interfacce (Wi-Fi, Ethernet, Parallels), e solo quella da cui e' arrivato il
 * pacchetto e' per definizione raggiungibile dal telefono.
 *
 * Il payload e' volutamente minimo e senza segreti: hostname, porta e stato.
 *
 * Nota: questo modulo non sa nulla di WhatsApp e non importa `server.js`; il
 * contenuto del beacon arriva da `getPayload`, cosi' e' testabile da solo.
 */

const dgram = require('dgram');
const os = require('os');

const SERVICE_ID = 'whatsapp-wp8-adapter';
const SERVICE_VERSION = 1;
const DEFAULT_INTERVAL_MS = 2000;
const DEFAULT_NETMASK = '255.255.255.0';
const GLOBAL_BROADCAST = '255.255.255.255';

// Interfacce che non portano da nessuna parte per un telefono della stessa rete:
// VPN, AirDrop, hotspot, bridge di macchine virtuali.
const VIRTUAL = /^(utun|awdl|llw|bridge|ap\d|gif|stf|xhci|anpi|vmenet|docker)/;

function ipv4ToInt(ip) {
  const parts = String(ip).split('.');
  if (parts.length !== 4) return null;
  let value = 0;
  for (const part of parts) {
    const octet = Number(part);
    if (!Number.isInteger(octet) || octet < 0 || octet > 255) return null;
    value = (value << 8) + octet;
  }
  return value >>> 0;
}

function intToIpv4(value) {
  return [24, 16, 8, 0].map((shift) => (value >>> shift) & 0xff).join('.');
}

/** Indirizzo di broadcast (a tutti gli host) della rete di `address`. */
function ipv4Broadcast(address, netmask) {
  const host = ipv4ToInt(address);
  const mask = ipv4ToInt(netmask || DEFAULT_NETMASK);
  if (host === null || mask === null) return null;
  return intToIpv4(((host & mask) | (~mask >>> 0)) >>> 0);
}

/** Indirizzi su cui mandare il beacon, uno per interfaccia fisica. */
function broadcastTargets(interfaces) {
  const targets = [];
  let physical = false;
  for (const name of Object.keys(interfaces || {})) {
    if (VIRTUAL.test(name)) continue;
    for (const info of interfaces[name] || []) {
      if (info.family !== 'IPv4' || info.internal) continue;
      physical = true;
      const address = ipv4Broadcast(info.address, info.netmask);
      if (address && targets.indexOf(address) === -1) targets.push(address);
    }
  }
  // Ultima risorsa: alcune reti filtrano il broadcast locale ma accettano questo.
  if (physical && targets.indexOf(GLOBAL_BROADCAST) === -1) targets.push(GLOBAL_BROADCAST);
  return targets;
}

/** Corpo del beacon: le chiavi devono combaciare con BeaconPayload.cs. */
function buildPayload(fields) {
  const source = fields || {};
  return {
    service: SERVICE_ID,
    version: SERVICE_VERSION,
    name: source.name || '',
    port: source.port,
    state: source.state || 'disconnected',
    account: source.account || '',
  };
}

function createDiscoveryBeacon(options) {
  const opts = options || {};
  const log = typeof opts.log === 'function' ? opts.log : () => {};
  const socket = typeof opts.socketFactory === 'function'
    ? opts.socketFactory()
    : dgram.createSocket({ type: 'udp4', reuseAddr: true });

  socket.on('error', (err) => log('WARN', `Discovery: ${err.message}`));

  function sendOnce() {
    const payload = Buffer.from(JSON.stringify(opts.getPayload() || {}));
    const interfaces = opts.interfaces || os.networkInterfaces();
    for (const target of broadcastTargets(interfaces)) {
      socket.send(payload, 0, payload.length, opts.port, target, (err) => {
        if (err) log('DEBUG', `Discovery: invio a ${target} fallito (${err.message})`);
      });
    }
  }

  // Il primo invio aspetta il bind: prima del bind il socket non ha ancora
  // SO_BROADCAST e il pacchetto verrebbe rifiutato.
  socket.bind(() => {
    try {
      socket.setBroadcast(true);
    } catch (err) {
      log('WARN', `Discovery: broadcast non attivabile (${err.message})`);
    }
    sendOnce();
  });

  const timer = setInterval(sendOnce, opts.intervalMs || DEFAULT_INTERVAL_MS);

  return {
    socket,
    sendOnce,
    stop() {
      clearInterval(timer);
      try {
        socket.close();
      } catch (err) {
        // gia' chiuso
      }
    },
  };
}

module.exports = {
  SERVICE_ID,
  SERVICE_VERSION,
  VIRTUAL,
  ipv4Broadcast,
  broadcastTargets,
  buildPayload,
  createDiscoveryBeacon,
};
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `cd WhatsappBridge && node --test test/discovery.test.js`
Expected: PASS, `# pass 4`.

- [x] **Step 5: Add the failing configuration test**

Append to `WhatsappBridge/test/config.test.js`:

```js
test('loadConfig espone la configurazione di discovery', () => {
  const defaults = loadConfig({});
  assert.strictEqual(defaults.discovery.enabled, true);
  assert.strictEqual(defaults.discovery.port, 8587);
  assert.ok(defaults.discovery.name.length > 0, 'nome di default = hostname');

  const custom = loadConfig({
    DISCOVERY_ENABLED: 'off',
    DISCOVERY_PORT: '9000',
    DISCOVERY_NAME: 'studio',
  });
  assert.strictEqual(custom.discovery.enabled, false);
  assert.strictEqual(custom.discovery.port, 9000);
  assert.strictEqual(custom.discovery.name, 'studio');
});
```

- [x] **Step 6: Run it to verify it fails**

Run: `cd WhatsappBridge && node --test test/config.test.js`
Expected: FAIL — `Cannot read properties of undefined (reading 'enabled')`.

- [x] **Step 7: Implement the configuration**

In `WhatsappBridge/config.js`, add `require('os')` next to the existing requires, add to `DEFAULTS`:

```js
  DISCOVERY_PORT: '8587',
  DISCOVERY_ENABLED: 'on',
  DISCOVERY_NAME: '',
```

and add to the object returned by `loadConfig` (after `pollIntervalMs`):

```js
    discovery: {
      enabled: pick(env, 'DISCOVERY_ENABLED').toLowerCase() !== 'off',
      port: parseInt(pick(env, 'DISCOVERY_PORT'), 10),
      // Nome che l'app mostra nella lista dei server trovati.
      name: pick(env, 'DISCOVERY_NAME') || os.hostname(),
    },
```

- [x] **Step 8: Run both test files**

Run: `cd WhatsappBridge && node --test test/config.test.js test/discovery.test.js`
Expected: PASS, `# pass 7`.

- [x] **Step 9: Wire the beacon into the adapter**

In `WhatsappBridge/server.js`, add to the imports:

```js
const { createDiscoveryBeacon, buildPayload } = require('./discovery');
```

Inside `main()`, after the `webhookServer.listen(...)` block, add:

```js
  // Il corpo del beacon si costruisce con l'unico builder del modulo di
  // discovery: sei chiavi, le stesse che l'app legge in BeaconPayload.cs.
  let beacon = null;
  if (config.discovery.enabled) {
    beacon = createDiscoveryBeacon({
      port: config.discovery.port,
      getPayload: () => {
        const current = bridge.getState();
        return buildPayload({
          name: config.discovery.name,
          port: config.bridge.port,
          state: current.status,
          account: current.jid,
        });
      },
      log,
    });
    log('OK', `Discovery attivo sulla porta UDP ${config.discovery.port} (nome: ${config.discovery.name})`);
  }
```

and in `shutdown()`, before `bridge.stop()`:

```js
    if (beacon) beacon.stop();
```

No new export is needed: `buildPayload` already lives in `discovery.js`.

- [x] **Step 10: Run the whole adapter suite**

Run: `cd WhatsappBridge && npm test`
Expected: `pass 34`, `fail 0`.

- [x] **Step 11: Prove the beacon leaves the machine**

Run (one command, the adapter is killed at the end):

```bash
cd WhatsappBridge && (npm start > /tmp/adapter.log 2>&1 &) && sleep 3 && \
  node -e "const d=require('dgram').createSocket('udp4');d.bind(8587,()=>{setTimeout(()=>{console.log('nessun beacon');process.exit(1)},6000)});d.on('message',(b,r)=>{console.log('beacon da',r.address,':',b.toString());d.close();process.exit(0)})"
```

Expected output: `beacon da 192.168.0.86 : {"service":"whatsapp-wp8-adapter","version":1,"name":"<hostname>","port":8585,"state":"disconnected","account":""}` and a log line `[OK] Discovery attivo sulla porta UDP 8587`. Then `pkill -f "node server.js"` and remove `/tmp/adapter.log`.

- [x] **Step 12: Document the beacon**

In `WhatsappBridge/.env.example`, under `# ── Adapter verso l'app Windows Phone ──`:

```
# Annuncio UDP con cui l'app trova l'adapter da sola (porta e nome visibile).
DISCOVERY_PORT=8587
DISCOVERY_ENABLED=on
DISCOVERY_NAME=
```

In `WhatsappBridge/README.md`, add after the `Avvio` section (quattro backtick
perche' il blocco contiene a sua volta un blocco `json`):

````markdown
## Scoperta automatica

L'adapter annuncia la sua presenza ogni 2 secondi in UDP sulla porta 8587
(`DISCOVERY_PORT`): l'app WP8 ascolta quella porta e usa l'indirizzo *del
mittente* per connettersi, quindi non serve più digitare IP e porta.

Il beacon non contiene segreti: solo hostname, porta TCP e stato WhatsApp.

```json
{"service":"whatsapp-wp8-adapter","version":1,"name":"mac-di-vincenzo","port":8585,"state":"disconnected","account":""}
```

Metti `DISCOVERY_ENABLED=off` per spegnerlo (l'inserimento manuale nell'app
continua a funzionare).
````

- [x] **Step 13: Commit**

```bash
git add WhatsappBridge/discovery.js WhatsappBridge/test/discovery.test.js \
        WhatsappBridge/config.js WhatsappBridge/test/config.test.js \
        WhatsappBridge/server.js WhatsappBridge/.env.example WhatsappBridge/README.md
git commit -m "feat(bridge): announce the adapter on the LAN so the app can find it"
```

---

### Task 2: The app finds the adapter and connects by itself

**Files:**
- Create: `WhatsappApp/Models/DiscoveredServer.cs`
- Create: `WhatsappApp/Models/BeaconPayload.cs`
- Create: `WhatsappApp/Services/DiscoveryService.cs`
- Create: `WhatsappApp/Services/AutoConnector.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (`<Compile Include=...>` block)
- Modify: `WhatsappApp/Package.appxmanifest` (`<Capabilities>`)
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml` (server block) and `.xaml.cs`
- Modify: `WhatsappApp/App.xaml.cs` (`OnLaunched`)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes: the beacon contract from Task 1; `CommunicationService.Instance` (`IsConnected`, `ConnectToServerAsync(string, int, string)`, `ConnectionEstablished`, `WhatsAppState`); `SettingsService` (`ServerAddress`, `ServerPort`, `Username`, `Save`).
- Produces:
  - `DiscoveredServer { Address, Port, Name, State, AccountJid, LastSeen, DisplayName, Endpoint }`
  - `DiscoveryService.Port` (const int 8587), `DiscoveryService.Instance`, `Task StartAsync()`, `void Stop()`, `List<DiscoveredServer> Snapshot()`, `Task<DiscoveredServer> WaitForSingleAsync(int seconds)`, `event EventHandler ServersChanged`
  - `AutoConnector.Instance.TryConnectAsync(string username, int discoverySeconds) -> Task<bool>`
  - resw keys: `ConnectionPage_Discovering.Text`, `ConnectionPage_DiscoveredLabel.Text`, `ConnectionPage_ManualToggle.Content`, `ConnectionPage_NoServerFound.Text`

- [x] **Step 1: Create the model**

`WhatsappApp/Models/DiscoveredServer.cs`:

```csharp
using System;

namespace WhatsappApp.Models
{
    /// <summary>
    /// Un adapter visto sulla rete. L'indirizzo e' quello del mittente del
    /// beacon, non quello scritto dentro il beacon: il computer ha piu'
    /// interfacce e solo il mittente e' raggiungibile dal telefono.
    /// </summary>
    public class DiscoveredServer
    {
        public string Address { get; set; }
        public int Port { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
        public string AccountJid { get; set; }
        public DateTime LastSeen { get; set; }

        public DiscoveredServer()
        {
            Address = "";
            Port = 0;
            Name = "";
            State = "";
            AccountJid = "";
            LastSeen = DateTime.Now;
        }

        /// <summary>Nome leggibile: l'hostname del computer, o l'indirizzo.</summary>
        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Name) ? Address : Name; }
        }

        /// <summary>Indirizzo e porta, mostrati sotto il nome.</summary>
        public string Endpoint
        {
            get { return Address + ":" + Port; }
        }

        /// <summary>True quando questo adapter ha gia' l'account collegato.</summary>
        public bool IsWhatsAppConnected
        {
            get { return State == "connected"; }
        }
    }
}
```

- [x] **Step 2: Create the beacon DTO**

`WhatsappApp/Models/BeaconPayload.cs`:

```csharp
using System.Runtime.Serialization;

namespace WhatsappApp.Models
{
    /// <summary>
    /// Beacon UDP dell'adapter (WhatsappBridge/discovery.js).
    /// I [DataMember] devono restare identici alle chiavi del JSON:
    /// DataContractJsonSerializer e' case-sensitive e un campo che non combacia
    /// resta al valore di default senza nessun errore.
    /// </summary>
    [DataContract]
    public class BeaconPayload
    {
        [DataMember(Name = "service")]
        public string Service { get; set; }

        [DataMember(Name = "version")]
        public int Version { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "port")]
        public int Port { get; set; }

        [DataMember(Name = "state")]
        public string State { get; set; }

        [DataMember(Name = "account")]
        public string Account { get; set; }
    }
}
```

- [x] **Step 3: Create the discovery listener**

`WhatsappApp/Services/DiscoveryService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Ascolta i beacon dell'adapter e tiene la lista di quelli visti negli
    /// ultimi secondi. Non mostra niente e non lancia: chi la usa decide cosa
    /// fare dei server trovati.
    /// </summary>
    public sealed class DiscoveryService
    {
        /// <summary>Porta UDP annunciata dall'adapter (DISCOVERY_PORT).</summary>
        public const int Port = 8587;

        /// <summary>Secondi dopo i quali un adapter che non si annuncia piu' sparisce.</summary>
        private const int TtlSeconds = 6;

        private const int PollIntervalMs = 250;

        private static DiscoveryService _instance;

        public static DiscoveryService Instance
        {
            get
            {
                if (_instance == null) _instance = new DiscoveryService();
                return _instance;
            }
        }

        private readonly List<DiscoveredServer> _servers = new List<DiscoveredServer>();
        private readonly object _gate = new object();
        private DatagramSocket _socket;
        private bool _starting;

        /// <summary>Sollevato su un thread di rete quando la lista cambia.</summary>
        public event EventHandler ServersChanged;

        private DiscoveryService()
        {
        }

        public bool IsListening
        {
            get { return _socket != null; }
        }

        /// <summary>Gli adapter noti adesso, dal piu' recente.</summary>
        public List<DiscoveredServer> Snapshot()
        {
            lock (_gate)
            {
                Prune();
                var copy = new List<DiscoveredServer>(_servers);
                copy.Sort(delegate(DiscoveredServer a, DiscoveredServer b)
                {
                    return b.LastSeen.CompareTo(a.LastSeen);
                });
                return copy;
            }
        }

        /// <summary>
        /// Apre la porta UDP. Se la rete o la porta non lo permettono resta
        /// spento: l'inserimento manuale dell'indirizzo continua a funzionare.
        /// </summary>
        public async Task StartAsync()
        {
            if (_socket != null || _starting) return;
            _starting = true;
            try
            {
                var socket = new DatagramSocket();
                socket.MessageReceived += OnMessageReceived;
                await socket.BindServiceNameAsync(Port.ToString());
                _socket = socket;
            }
            catch (Exception)
            {
                _socket = null;
            }
            finally
            {
                _starting = false;
            }
        }

        public void Stop()
        {
            DatagramSocket socket = _socket;
            _socket = null;
            if (socket == null) return;
            try { socket.MessageReceived -= OnMessageReceived; } catch { }
            try { socket.Dispose(); } catch { }
        }

        /// <summary>
        /// Aspetta fino a <paramref name="seconds"/> secondi che si veda un solo
        /// adapter. Con zero o piu' di uno restituisce null: con piu' di uno la
        /// scelta tocca all'utente.
        /// </summary>
        public async Task<DiscoveredServer> WaitForSingleAsync(int seconds)
        {
            int attempts = seconds * (1000 / PollIntervalMs);
            for (int i = 0; i < attempts; i++)
            {
                List<DiscoveredServer> found = Snapshot();
                if (found.Count == 1) return found[0];
                if (found.Count > 1) return null;
                await Task.Delay(PollIntervalMs);
            }
            return null;
        }

        private void Prune()
        {
            DateTime limit = DateTime.Now.AddSeconds(-TtlSeconds);
            for (int i = _servers.Count - 1; i >= 0; i--)
            {
                if (_servers[i].LastSeen < limit) _servers.RemoveAt(i);
            }
        }

        private async void OnMessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                DataReader reader = args.GetDataReader();
                uint size = reader.UnconsumedBufferLength;
                if (size == 0) return;
                await reader.LoadAsync(size);
                string json = reader.ReadString(size);

                BeaconPayload beacon = Parse(json);
                if (beacon == null) return;
                if (beacon.Service != "whatsapp-wp8-adapter") return;
                if (beacon.Port <= 0) return;

                string address = args.RemoteAddress == null ? "" : args.RemoteAddress.RawName;
                if (string.IsNullOrEmpty(address)) return;

                if (AddOrUpdate(address, beacon)) RaiseServersChanged();
            }
            catch (Exception)
            {
                // Un datagramma malformato non deve fermare l'ascolto.
            }
        }

        private bool AddOrUpdate(string address, BeaconPayload beacon)
        {
            string name = beacon.Name == null ? "" : beacon.Name;
            string state = beacon.State == null ? "" : beacon.State;
            string account = beacon.Account == null ? "" : beacon.Account;

            lock (_gate)
            {
                Prune();
                for (int i = 0; i < _servers.Count; i++)
                {
                    DiscoveredServer known = _servers[i];
                    if (known.Address != address) continue;

                    bool changed = known.Port != beacon.Port
                        || known.Name != name
                        || known.State != state
                        || known.AccountJid != account;

                    known.Port = beacon.Port;
                    known.Name = name;
                    known.State = state;
                    known.AccountJid = account;
                    known.LastSeen = DateTime.Now;
                    return changed;
                }

                var found = new DiscoveredServer();
                found.Address = address;
                found.Port = beacon.Port;
                found.Name = name;
                found.State = state;
                found.AccountJid = account;
                found.LastSeen = DateTime.Now;
                _servers.Add(found);
                return true;
            }
        }

        private void RaiseServersChanged()
        {
            EventHandler handler = ServersChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static BeaconPayload Parse(string json)
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(BeaconPayload));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return serializer.ReadObject(stream) as BeaconPayload;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
```

- [x] **Step 4: Create the auto-connector**

`WhatsappApp/Services/AutoConnector.cs`:

```csharp
using System.Threading.Tasks;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Decide a quale adapter connettersi senza che l'utente scriva nulla:
    /// l'indirizzo salvato se c'e', altrimenti l'unico adapter annunciato sulla
    /// rete. La usano sia l'avvio dell'app sia la pagina delle impostazioni, che
    /// e' l'unico posto in cui la logica puo' stare (DRY).
    ///
    /// Non mostra nulla e non lancia: il chiamante sa dove scrivere.
    /// </summary>
    public sealed class AutoConnector
    {
        private static AutoConnector _instance;

        public static AutoConnector Instance
        {
            get
            {
                if (_instance == null) _instance = new AutoConnector();
                return _instance;
            }
        }

        private bool _running;

        private AutoConnector()
        {
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>
        /// True quando alla fine la connessione e' attiva.
        /// </summary>
        public async Task<bool> TryConnectAsync(string username, int discoverySeconds)
        {
            if (CommunicationService.Instance.IsConnected) return true;
            if (_running) return false;

            _running = true;
            try
            {
                string address = SettingsService.ServerAddress;
                int port = SettingsService.ServerPort;

                if (string.IsNullOrEmpty(address))
                {
                    await DiscoveryService.Instance.StartAsync();
                    DiscoveredServer server = await DiscoveryService.Instance.WaitForSingleAsync(discoverySeconds);
                    if (server == null) return false;
                    address = server.Address;
                    port = server.Port;
                }

                bool connected = await CommunicationService.Instance.ConnectToServerAsync(address, port, username);
                if (connected) SettingsService.Save(address, port, username);
                return connected;
            }
            finally
            {
                _running = false;
            }
        }
    }
}
```

- [x] **Step 5: Register the four new files in the project**

In `WhatsappApp/WhatsappApp.csproj`, inside the `<Compile Include="Models\ServerConfig.cs" />` … region, add:

```xml
    <Compile Include="Models\BeaconPayload.cs" />
    <Compile Include="Models\DiscoveredServer.cs" />
```

and next to the other services:

```xml
    <Compile Include="Services\AutoConnector.cs" />
    <Compile Include="Services\DiscoveryService.cs" />
```

- [x] **Step 6: Add the discovery UI to the settings page**

In `WhatsappApp/Pages/ConnectionPage.xaml`, replace the block that starts with `<!-- Server (adapter) -->` and ends with the port `TextBox` (the `<TextBlock x:Uid="ConnectionPage_PortLabel">` … `</TextBox>` pair) with:

```xml
                <!-- Server trovati sulla rete -->
                <TextBlock x:Uid="ConnectionPage_DiscoveredLabel" Text="Servers found"
                           Foreground="#FF075E54" FontSize="14" FontWeight="SemiBold"/>
                <TextBlock x:Name="DiscoveryStatusText" x:Uid="ConnectionPage_Discovering"
                           Text="Looking for the server on the network..."
                           Foreground="#FF808080" FontSize="12" TextWrapping="Wrap" Margin="0,4,0,0"/>
                <ListView x:Name="ServersList" Margin="0,4,0,0" Background="White"
                          SelectionMode="None" IsItemClickEnabled="True"
                          Visibility="Collapsed" ItemClick="ServersList_ItemClick">
                    <ListView.ItemTemplate>
                        <DataTemplate>
                            <StackPanel Margin="0,8,0,8">
                                <TextBlock Text="{Binding DisplayName}" FontSize="16"
                                           Foreground="#FF075E54" TextWrapping="Wrap"/>
                                <TextBlock Text="{Binding Endpoint}" FontSize="12"
                                           Foreground="#FF808080"/>
                            </StackPanel>
                        </DataTemplate>
                    </ListView.ItemTemplate>
                </ListView>
                <TextBlock x:Name="NoServerText" x:Uid="ConnectionPage_NoServerFound"
                           Text="No server found. Start the adapter on the computer, or enter the address by hand."
                           Foreground="#FF606060" FontSize="12" TextWrapping="Wrap"
                           Visibility="Collapsed" Margin="0,4,0,0"/>

                <!-- Inserimento manuale: resta la via di riserva quando la
                     scoperta automatica non vede nulla (reti che filtrano UDP). -->
                <Button x:Name="ManualToggleButton" x:Uid="ConnectionPage_ManualToggle"
                        Content="Enter the address by hand"
                        Background="#FFE0E0E0" Foreground="#FF075E54" FontSize="14"
                        Height="40" BorderThickness="0" Margin="0,12,0,0"
                        Click="ManualToggleButton_Click"/>

                <StackPanel x:Name="ManualPanel" Visibility="Collapsed">
                    <TextBlock x:Uid="ConnectionPage_ServerLabel" Text="WhatsApp server (adapter)"
                               Foreground="#FF075E54" FontSize="14" FontWeight="SemiBold" Margin="0,12,0,0"/>
                    <TextBox x:Name="ServerAddressBox" x:Uid="ConnectionPage_ServerPlaceholder"
                             Text="192.168.1.100"
                             PlaceholderText="e.g. 192.168.1.100"
                             Background="White" FontSize="16" Margin="0,4,0,0"/>

                    <TextBlock x:Uid="ConnectionPage_PortLabel" Text="TCP port"
                               Foreground="#FF075E54" FontSize="14" FontWeight="SemiBold" Margin="0,12,0,0"/>
                    <TextBox x:Name="ServerPortBox" Text="8585"
                             PlaceholderText="8585"
                             Background="White" FontSize="16" Margin="0,4,0,0"/>
                </StackPanel>
```

- [x] **Step 7: Run the localization guard to watch it fail**

Run: `node tools/check-resw.js --strict`
Expected: FAIL, four lines such as `WhatsappApp/Pages/ConnectionPage.xaml: x:Uid="ConnectionPage_Discovering" has no "ConnectionPage_Discovering.Text" entry`.

- [x] **Step 8: Add the four keys to both languages**

In `WhatsappApp/Strings/en-US/Resources.resw`, before `</root>`:

```xml
  <data name="ConnectionPage_Discovering.Text" xml:space="preserve">
    <value>Looking for the server on the network...</value>
  </data>
  <data name="ConnectionPage_DiscoveredLabel.Text" xml:space="preserve">
    <value>Servers found</value>
  </data>
  <data name="ConnectionPage_ManualToggle.Content" xml:space="preserve">
    <value>Enter the address by hand</value>
  </data>
  <data name="ConnectionPage_NoServerFound.Text" xml:space="preserve">
    <value>No server found. Start the adapter on the computer, or enter the address by hand.</value>
  </data>
```

In `WhatsappApp/Strings/it-IT/Resources.resw`, before `</root>`:

```xml
  <data name="ConnectionPage_Discovering.Text" xml:space="preserve">
    <value>Cerco il server sulla rete...</value>
  </data>
  <data name="ConnectionPage_DiscoveredLabel.Text" xml:space="preserve">
    <value>Server trovati</value>
  </data>
  <data name="ConnectionPage_ManualToggle.Content" xml:space="preserve">
    <value>Inserisci l'indirizzo a mano</value>
  </data>
  <data name="ConnectionPage_NoServerFound.Text" xml:space="preserve">
    <value>Nessun server trovato. Avvia l'adapter sul computer, oppure inserisci l'indirizzo a mano.</value>
  </data>
```

- [x] **Step 9: Run the guard again**

Run: `node tools/check-resw.js --strict`
Expected: `OK: 86 key(s) in en-US and it-IT, every x:Uid and Loc.Get lookup resolved.`

- [x] **Step 10: Wire the page**

In `WhatsappApp/Pages/ConnectionPage.xaml.cs`:

1. Add the field and the collections near the other fields:

```csharp
        private readonly System.Collections.ObjectModel.ObservableCollection<DiscoveredServer> _servers =
            new System.Collections.ObjectModel.ObservableCollection<DiscoveredServer>();

        private bool _autoConnectTried;
        private DateTime _discoveryStartedAt;
```

2. In `OnNavigatedTo`, after the existing `UsernameBox.Text = savedUsername;` line:

```csharp
            ServersList.ItemsSource = _servers;
            _discoveryStartedAt = DateTime.Now;
            DiscoveryService.Instance.ServersChanged += OnServersChanged;
            StartDiscovery();
```

3. In `OnNavigatedFrom`, alongside the other unsubscriptions:

```csharp
            DiscoveryService.Instance.ServersChanged -= OnServersChanged;
```

4. Replace `ActionButton_Click` with the shared connection path plus the new handlers:

```csharp
        private async void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            string address = (ServerAddressBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(address)) address = SettingsService.DefaultAddress;

            int port = 8585;
            int boxPort;
            if (!string.IsNullOrEmpty(ServerPortBox.Text) &&
                int.TryParse(ServerPortBox.Text.Trim(), out boxPort))
            {
                port = boxPort;
            }

            await ConnectAsync(address, port);
        }

        /// <summary>
        /// Unico punto in cui si apre la connessione: lo usano il pulsante, la
        /// lista dei server trovati e la riconnessione automatica.
        /// </summary>
        private async Task ConnectAsync(string address, int port)
        {
            string username = (UsernameBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(username))
            {
                username = Loc.Get("ConnectionPage_DefaultUsername", "User");
                UsernameBox.Text = username;
            }

            StatusPanel.Visibility = Visibility.Visible;
            ActionButton.IsEnabled = false;
            StatusText.Text = string.Format(
                Loc.Get("ConnectionPage_Connecting", "Connecting to {0}:{1}..."), address, port);

            bool connected = await CommunicationService.Instance.ConnectToServerAsync(address, port, username);
            if (connected)
            {
                SettingsService.Save(address, port, username);
                StatusText.Text = Loc.Get("ConnectionPage_Connected", "Connected!");
                ShowConnectedState();
                await CommunicationService.Instance.SendControlAsync("status");
            }
            else
            {
                StatusText.Text = Loc.Get("ConnectionPage_ConnectFailed", "Connection failed");
                ActionButton.IsEnabled = true;
            }
        }

        private async void StartDiscovery()
        {
            await DiscoveryService.Instance.StartAsync();
            RefreshServers();

            if (_autoConnectTried || CommunicationService.Instance.IsConnected) return;
            _autoConnectTried = true;
            if (!await AutoConnector.Instance.TryConnectAsync(UsernameBox.Text ?? "", 6))
            {
                RefreshServers();
            }
        }

        private async void OnServersChanged(object sender, EventArgs e)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, RefreshServers);
        }

        private void RefreshServers()
        {
            System.Collections.Generic.List<DiscoveredServer> found = DiscoveryService.Instance.Snapshot();

            // Prima "sto cercando", poi "non ho trovato niente": due stati
            // diversi, perche' l'utente deve sapere quando smettere di aspettare.
            bool searching = found.Count == 0
                && DiscoveryService.Instance.IsListening
                && (DateTime.Now - _discoveryStartedAt).TotalSeconds < 8;
            DiscoveryStatusText.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
            NoServerText.Visibility = found.Count == 0 && !searching
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (!SameServers(found))
            {
                _servers.Clear();
                foreach (DiscoveredServer server in found) _servers.Add(server);
            }
            ServersList.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Un solo server in vista e nessuna connessione: ci si va da soli.
            if (found.Count == 1 && !_autoConnectTried && !CommunicationService.Instance.IsConnected)
            {
                _autoConnectTried = true;
#pragma warning disable 4014
                ConnectAsync(found[0].Address, found[0].Port);
#pragma warning restore 4014
            }
        }

        private bool SameServers(System.Collections.Generic.List<DiscoveredServer> found)
        {
            if (found.Count != _servers.Count) return false;
            for (int i = 0; i < found.Count; i++)
            {
                if (found[i].Endpoint != _servers[i].Endpoint) return false;
                if (found[i].DisplayName != _servers[i].DisplayName) return false;
                if (found[i].State != _servers[i].State) return false;
            }
            return true;
        }

        private async void ServersList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var server = e.ClickedItem as DiscoveredServer;
            if (server == null) return;

            ServerAddressBox.Text = server.Address;
            ServerPortBox.Text = server.Port.ToString();
            await ConnectAsync(server.Address, server.Port);
        }

        private void ManualToggleButton_Click(object sender, RoutedEventArgs e)
        {
            ManualPanel.Visibility = ManualPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
```

5. In `DisconnectButton_Click`, add `_autoConnectTried = true;` as the first line so a manual disconnect does not immediately reconnect.

- [x] **Step 11: Reconnect at launch**

In `WhatsappApp/App.xaml.cs`, in `OnLaunched`, after `Loc.Prewarm();`:

```csharp
            // Riconnessione automatica: l'app non riprova da sola dopo un
            // riavvio, e senza questo l'elenco chat resta vuoto finche' l'utente
            // non apre le impostazioni.
            if (SettingsService.HasSavedSettings) StartAutoConnect();
```

and at the end of the class:

```csharp
        /// <summary>
        /// Prova a ricollegarsi in sottofondo. Volutamente muta: qualunque
        /// messaggio lo scrive la pagina delle impostazioni, che e' anche
        /// l'unico posto in cui la lingua e' gia' pronta.
        /// </summary>
        private async void StartAutoConnect()
        {
            await AutoConnector.Instance.TryConnectAsync(SettingsService.Username, 6);
        }
```

- [x] **Step 12: Allow local-network traffic**

In `WhatsappApp/Package.appxmanifest`, inside `<Capabilities>`:

```xml
    <Capability Name="internetClientServer" />
    <!-- Serve per ricevere i beacon UDP dell'adapter sulla rete locale. -->
    <Capability Name="privateNetworkClientServer" />
```

- [x] **Step 13: Run the static gates**

Run:

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict
```

Expected: three `OK:` lines (the resw one now mentioning 86 keys).

- [x] **Step 14: Build on the Windows machine**

Run:

```bash
prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS"
prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"
```

Expected: `0 Error(s)`, and the two known warnings only (`CS0618` `FileOpenPicker.PickSingleFileAsync`, `CS4014` in `ConnectionPage`).

- [x] **Step 15: Commit**

```bash
git add WhatsappApp/WhatsappApp.csproj WhatsappApp/Package.appxmanifest \
        WhatsappApp/Models/BeaconPayload.cs WhatsappApp/Models/DiscoveredServer.cs \
        WhatsappApp/Services/DiscoveryService.cs WhatsappApp/Services/AutoConnector.cs \
        WhatsappApp/Pages/ConnectionPage.xaml WhatsappApp/Pages/ConnectionPage.xaml.cs \
        WhatsappApp/App.xaml.cs WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
git commit -m "feat: let the app find the adapter on the LAN and connect by itself"
```

---

### Task 3: The login happens on the device

**Files:**
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml` (QR overlay)
- Modify: `WhatsappApp/Pages/ConnectionPage.xaml.cs` (auto request, refresh timer, screen lock)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes: `CommunicationService.Instance` (`ConnectionEstablished`, `ControlMessageReceived`, `WhatsAppState`, `SendControlAsync("login.qr")`, `SendControlAsync("login.code", phone)`); `ChatMessage.QrImageData` (base64 PNG), `QrDuration` (seconds), `PairCode`; the existing `BitmapFromBase64Async`.
- Produces: resw keys `ConnectionPage_QrOverlayTitle.Text`, `ConnectionPage_QrOverlayHint.Text`, `ConnectionPage_QrOverlayClose.Content`, `ConnectionPage_QrOverlayRefreshing.Text`; overlay names `QrOverlay`, `QrOverlayImage`, `QrOverlayCodeText`, `QrOverlayHintText`, `QrOverlayCloseButton`.

- [x] **Step 1: Add the overlay markup**

In `WhatsappApp/Pages/ConnectionPage.xaml`, as the last child of the root `<Grid>` (after the `</ScrollViewer>`), add:

```xml
        <!-- QR a tutto schermo: il codice lo inquadra il telefono con WhatsApp,
             quindi serve grande, su fondo bianco e con lo schermo accceso. -->
        <Grid x:Name="QrOverlay" Grid.RowSpan="2" Background="White" Visibility="Collapsed">
            <StackPanel VerticalAlignment="Center" Margin="16,16,16,16">
                <TextBlock x:Uid="ConnectionPage_QrOverlayTitle"
                           Text="Scan this code with WhatsApp"
                           Foreground="#FF075E54" FontSize="20" FontWeight="SemiBold"
                           TextAlignment="Center" TextWrapping="Wrap"/>
                <Image x:Name="QrOverlayImage" Width="340" Height="340" Margin="0,12,0,0"
                       Stretch="Uniform" HorizontalAlignment="Center"/>
                <TextBlock x:Name="QrOverlayCodeText" Text="" FontSize="30" FontWeight="Bold"
                           Foreground="#FF075E54" TextAlignment="Center"
                           TextWrapping="Wrap" Margin="0,12,0,0"/>
                <TextBlock x:Name="QrOverlayHintText" x:Uid="ConnectionPage_QrOverlayHint"
                           Text="WhatsApp &gt; Linked devices &gt; Link a device, then scan. The screen stays on while this page is open."
                           Foreground="#FF606060" FontSize="13" TextAlignment="Center"
                           TextWrapping="Wrap" Margin="0,12,0,0"/>
                <Button x:Name="QrOverlayCloseButton" x:Uid="ConnectionPage_QrOverlayClose"
                        Content="Close"
                        Background="#FFE0E0E0" Foreground="#FF075E54" FontSize="16"
                        Height="44" BorderThickness="0" Margin="0,16,0,0"
                        Click="QrOverlayCloseButton_Click"/>
            </StackPanel>
        </Grid>
```

- [x] **Step 2: Run the localization guard to watch it fail**

Run: `node tools/check-resw.js --strict`
Expected: FAIL on `ConnectionPage_QrOverlayTitle`, `ConnectionPage_QrOverlayHint`, `ConnectionPage_QrOverlayClose`.

- [x] **Step 3: Add the three keys to both languages**

`WhatsappApp/Strings/en-US/Resources.resw`:

```xml
  <data name="ConnectionPage_QrOverlayTitle.Text" xml:space="preserve">
    <value>Scan this code with WhatsApp</value>
  </data>
  <data name="ConnectionPage_QrOverlayHint.Text" xml:space="preserve">
    <value>WhatsApp &gt; Linked devices &gt; Link a device, then scan. The screen stays on while this page is open.</value>
  </data>
  <data name="ConnectionPage_QrOverlayClose.Content" xml:space="preserve">
    <value>Close</value>
  </data>
```

`WhatsappApp/Strings/it-IT/Resources.resw`:

```xml
  <data name="ConnectionPage_QrOverlayTitle.Text" xml:space="preserve">
    <value>Inquadra questo codice con WhatsApp</value>
  </data>
  <data name="ConnectionPage_QrOverlayHint.Text" xml:space="preserve">
    <value>WhatsApp &gt; Dispositivi collegati &gt; Collega un dispositivo, poi inquadra. Lo schermo resta acceso finché resti qui.</value>
  </data>
  <data name="ConnectionPage_QrOverlayClose.Content" xml:space="preserve">
    <value>Chiudi</value>
  </data>
```

- [x] **Step 4: Run the guard again**

Run: `node tools/check-resw.js --strict`
Expected: `OK: 89 key(s) ...`.

- [x] **Step 5: Automatically show and refresh the code**

In `WhatsappApp/Pages/ConnectionPage.xaml.cs`:

1. Add the fields:

```csharp
        private Windows.System.Display.DisplayRequest _displayRequest;
        private bool _displayRequestActive;
        private Windows.UI.Xaml.DispatcherTimer _qrTimer;
```

2. In `OnConnectionEstablished`, request the code as soon as the socket is up:

```csharp
        private void OnConnectionEstablished(object sender, EventArgs e)
        {
            ShowConnectedState();

            // Il login si fa dal telefono: il codice si chiede subito, senza
            // che l'utente debba cercare il pulsante.
            if (CommunicationService.Instance.WhatsAppState != "connected")
            {
#pragma warning disable 4014
                CommunicationService.Instance.SendControlAsync("login.qr");
#pragma warning restore 4014
            }
        }
```

3. Replace `ShowQrCode` with the overlay version:

```csharp
        private async void ShowQrCode(string base64, int duration)
        {
            if (string.IsNullOrEmpty(base64))
            {
                QrInfoText.Text = Loc.Get("ConnectionPage_QrUnavailable", "QR code not available.");
                return;
            }

            try
            {
                var bitmap = await BitmapFromBase64Async(base64);
                QrImage.Source = bitmap;
                QrOverlayImage.Source = bitmap;
                QrImage.Visibility = Visibility.Visible;

                PairCodeText.Text = "";
                QrOverlayCodeText.Text = "";
                QrInfoText.Text = duration > 0
                    ? string.Format(Loc.Get("ConnectionPage_QrHintDuration",
                        "Open WhatsApp, open Linked devices and tap Link a device, then scan the code (valid for about {0} seconds)."),
                        duration)
                    : Loc.Get("ConnectionPage_QrHint",
                        "Open WhatsApp, open Linked devices and tap Link a device, then scan the code.");

                OpenQrOverlay();
                ScheduleQrRefresh(duration);
            }
            catch (Exception ex)
            {
                QrInfoText.Text = string.Format(
                    Loc.Get("ConnectionPage_QrError", "Could not show the QR code: {0}"), ex.Message);
            }
        }
```

4. Replace the `paircode` branch of `OnControlMessageReceived` with:

```csharp
                case "paircode":
                    PairCodeText.Text = string.Format(Loc.Get("ConnectionPage_PairCode", "Code: {0}"),
                        message.PairCode);
                    WhatsAppStateText.Text = Loc.Get("ConnectionPage_PairCodeHint",
                        "Enter this code in WhatsApp: Linked devices, Link a device, Link with phone number instead.");
                    QrImage.Visibility = Visibility.Collapsed;
                    QrOverlayImage.Visibility = Visibility.Collapsed;
                    QrOverlayCodeText.Text = message.PairCode;
                    OpenQrOverlay();
                    break;
```

5. In `UpdateLoginUi`, in the `case "connected":` branch add (before `break;`):

```csharp
                    CloseQrOverlay();
                    StopQrTimer();
```

and in the `default:` branch add `StopQrTimer();`.

6. Add the overlay plumbing:

```csharp
        /// <summary>
        /// Mostra il codice grande. Lo schermo resta acceso: se si spegne o si
        /// abbassa la luminosita' mentre si inquadra, il codice diventa
        /// illeggibile e la scansione fallisce senza un messaggio d'errore.
        /// </summary>
        private void OpenQrOverlay()
        {
            QrOverlayImage.Visibility = string.IsNullOrEmpty(QrOverlayCodeText.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            QrOverlay.Visibility = Visibility.Visible;
            KeepScreenOn();
        }

        private void CloseQrOverlay()
        {
            QrOverlay.Visibility = Visibility.Collapsed;
            ReleaseScreenOn();
        }

        private void KeepScreenOn()
        {
            if (_displayRequest == null) _displayRequest = new Windows.System.Display.DisplayRequest();
            if (_displayRequestActive) return;
            try
            {
                _displayRequest.RequestActive();
                _displayRequestActive = true;
            }
            catch (Exception)
            {
                // Limite di richieste attive raggiunto: si prosegue lo stesso.
            }
        }

        private void ReleaseScreenOn()
        {
            if (!_displayRequestActive || _displayRequest == null) return;
            try { _displayRequest.RequestRelease(); } catch (Exception) { }
            _displayRequestActive = false;
        }

        /// <summary>Richiede un codice nuovo poco prima che scada.</summary>
        private void ScheduleQrRefresh(int duration)
        {
            StopQrTimer();

            int seconds = duration > 10 ? duration - 5 : 10;
            _qrTimer = new Windows.UI.Xaml.DispatcherTimer();
            _qrTimer.Interval = TimeSpan.FromSeconds(seconds);
            _qrTimer.Tick += OnQrTimerTick;
            _qrTimer.Start();
        }

        private void StopQrTimer()
        {
            if (_qrTimer == null) return;
            _qrTimer.Stop();
            _qrTimer.Tick -= OnQrTimerTick;
            _qrTimer = null;
        }

        private async void OnQrTimerTick(object sender, object e)
        {
            StopQrTimer();
            if (!CommunicationService.Instance.IsConnected) return;
            if (CommunicationService.Instance.WhatsAppState == "connected") return;
            QrOverlayHintText.Text = Loc.Get("ConnectionPage_QrOverlayRefreshing", "Refreshing the code...");
            await CommunicationService.Instance.SendControlAsync("login.qr");
        }

        private void QrOverlayCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseQrOverlay();
        }
```

7. In `OnNavigatedFrom`, add `StopQrTimer(); CloseQrOverlay();`.

- [x] **Step 6: Declare the refresh string**

Add to both `.resw` files (en-US value `Refreshing the code...`, it-IT value `Aggiorno il codice...`) with names `ConnectionPage_QrOverlayRefreshing.Text`, so the key count becomes **90**.

- [x] **Step 7: Run the static gates**

Run: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict`
Expected: three `OK:` lines. The C# 5 guard is the one that matters here: `DispatcherTimer.Tick += OnQrTimerTick;` with an `async void` handler and `Windows.System.Display.DisplayRequest` must not be on its "missing on WP8.1" list (both exist in `Windows.winmd`, verified).

- [x] **Step 8: Build on the Windows machine**

Run the two `prlctl` commands from Task 2, Step 14.
Expected: `0 Error(s)`.

- [ ] **Step 9: On the device** — NOT RUN: the WP8.1 emulator images are x86 and
  need Hyper-V, which the ARM64 Windows guest does not have; this needs an x86/x64
  Windows machine or a real WP8.1 phone. The feature is only build- and
  guard-verified so far.

1. Launch the app with the adapter running: the settings page must show the computer by name and connect without typing anything.
2. The QR must appear **full screen** without tapping anything, and a new one must appear about every 20–30 s.
3. Scan it with WhatsApp (Linked devices): the overlay must close by itself and the chat list must fill with contacts.
4. Put the phone down for a minute with the overlay open: the screen must not dim.
5. Lock and unlock the phone, then reopen the app: it must reconnect without help.

- [x] **Step 10: Commit**

```bash
git add WhatsappApp/Pages/ConnectionPage.xaml WhatsappApp/Pages/ConnectionPage.xaml.cs \
        WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
git commit -m "feat: show and refresh the login QR on the device, full screen and awake"
```

---

### Task 4: The terminal login becomes optional and always usable

**Files:**
- Modify: `tools/start-login.js`
- Modify: `.agents/skills/run-the-login-server/SKILL.md`, `README.md`

**Interfaces:**
- Consumes: `qrTerm.qrFromPng(pngPath, { quietZone, plain }) -> { lines, count, error }` (from `tools/qr-term.js`).
- Produces: `--no-qr` (start the stack and wait for the phone to log in), `--open-qr` (open the current PNG with `open`), a stable PNG at `.tools/gowa/login-qr.png`, and no truncated drawing ever.

- [x] **Step 1: Add the two flags**

In `parseArgs`, next to `--once`:

```js
      case '--no-qr': options.noQr = true; break;
      case '--open-qr': options.openQr = true; break;
```

and in the defaults object: `noQr: false, openQr: false,`.

Document both in `HELP`:

```
  --no-qr               avvia lo stack senza disegnare il QR: il login si fa
                        dal telefono, nell'app (consigliato)
  --open-qr             apre il PNG del codice con Anteprima (si aggiorna da solo)
```

- [x] **Step 2: Keep the current PNG where it can be reopened**

In `showQr`, after `const qr = qrTerm.qrFromPng(...)`, add:

```js
  // Il codice ruota ogni ~20s: il PNG si tiene in un percorso fisso, cosi'
  // chi lo apre con Anteprima lo vede aggiornarsi invece di invecchiare.
  const stable = path.join(GOWA_DIR, 'login-qr.png');
  try {
    fs.copyFileSync(pngPath, stable);
  } catch (err) {
    // il PNG di GOWA puo' sparire a meta' copia quando scade: non e' fatale
  }
```

- [x] **Step 3: Never draw a code that does not fit**

Replace the whole `makePrinter` function with these two functions:

```js
/**
 * Il codice e' piu' grande della finestra (o l'output non e' un terminale):
 * si spiega e si indica il PNG, mai un disegno incompleto: un QR tagliato non
 * si puo' inquadrare, e sembra solo un errore di visualizzazione.
 */
function printTooSmall(printer, lines, caption, hint) {
  if (printer.warned) return;
  printer.warned = true;

  const columns = process.stdout.columns || 0;
  const rows = process.stdout.rows || 0;
  console.log(`\n  ${caption}`);
  if (process.stdout.isTTY && columns && rows) {
    console.log(`     Il codice occupa ${lines.length} righe e ${lines[0].length} colonne;`);
    console.log(`     questa finestra ne ha ${rows} x ${columns}.`);
    console.log('     Ridimensionala, o premi Cmd - per rimpicciolire il testo: il prossimo');
    console.log('     codice verra\' disegnato qui.');
  }
  if (hint) console.log(`     Oppure: ${hint}`);
  console.log('     Oppure: --no-qr, e fai il login dal telefono nell\'app.');
}

function makePrinter(options) {
  const printer = {
    warned: false,
    drawnLines: 0,

    /**
     * Disegna il codice, riscrivendo sopra il precedente solo se ci sta tutto
     * nella finestra: una riga che va a capo, o un disegno piu' alto dello
     * schermo, sposterebbe il cursore e lascerebbe residui in giro.
     */
    render(lines, caption, hint) {
      const columns = process.stdout.columns || 0;
      const rows = process.stdout.rows || 0;
      const canRedraw = !options.plain && !!process.stdout.isTTY
        && (columns === 0 || columns >= lines[0].length + 2)
        && (rows === 0 || rows >= lines.length + 2);

      if (!canRedraw) {
        if (printer.warned) {
          // Senza questa riga, dopo il primo avviso il log resta muto per
          // minuti e sembra che lo script si sia piantato.
          console.log(`  · nuovo codice alle ${stamp()} (non disegnato: la finestra e' troppo piccola)`);
          printer.drawnLines = 0;
          return;
        }
        printTooSmall(printer, lines, caption, hint);
        printer.drawnLines = 0;
        return;
      }

      if (printer.drawnLines > 0) process.stdout.write(`\x1b[${printer.drawnLines}A`);
      const output = [`\x1b[2K  ${caption}\n`];
      for (const line of lines) output.push(`\x1b[2K${line}\n`);
      process.stdout.write(output.join(''));
      printer.drawnLines = lines.length + 1;
    },

    done() {
      printer.drawnLines = 0;
    },
  };
  return printer;
}
```

Note: `--plain` (and a non-TTY) now goes through `printTooSmall` too, so a piped log never contains a half QR. `printed`/`drawnLines` are gone; `reportLoginError` calls `printer.done()`, which still exists.

- [x] **Step 4: Point at the PNG instead of the GOWA URL**

In `showQr`, pass the stable file as the hint:

```js
  printer.render(qr.lines, `QR aggiornato alle ${stamp()} — ${qr.count} moduli, ` +
    `ricostruzione ${(qr.error * 100).toFixed(2)}%`,
    `apri ${path.relative(ROOT, stable)} con Anteprima (si aggiorna da solo), o ${imageUrl}`);
```

- [x] **Step 5: Implement `--no-qr`**

In `waitForLogin`, when `options.noQr` is set, skip both the drawing and the login requests, and report only state changes:

```js
    if (options.noQr) {
      const status = await statusOf(baseUrl, deviceId, options).catch(() => null);
      if (status && status.isLoggedIn) return status;
      if (!state.reportedWaiting) {
        state.reportedWaiting = true;
        console.log(`\n  Il login si fa dal telefono: apri l'app e inquadra il codice che mostra.` +
          `\n  In attesa del collegamento...`);
      }
      await sleep(2000);
      continue;
    }
```

- [x] **Step 6: Report it in the banner**

In `main()`, in the else branch of the "already connected" check, add:

```js
    if (options.noQr) {
      console.log('\n  Nessun QR qui: loggati dal telefono, nell\'app (Ctrl-C per fermare).');
    }
```

- [x] **Step 7: Verify by hand**

```bash
node tools/start-login.js --no-qr --no-bridge
```

Expected: the banner, then `Il login si fa dal telefono: … In attesa del collegamento...`, and no QR drawing. Ctrl-C stops it.

```bash
script -q /dev/null bash -c 'stty rows 24 cols 80; node tools/start-login.js --no-bridge'
```

Expected: the explanation about the size, the path `.tools/gowa/login-qr.png`, the `--no-qr` hint, and **no** partial QR.

```bash
ls -la .tools/gowa/login-qr.png
```

Expected: the file exists and its mtime changes every time a new code arrives.

- [x] **Step 8: Update the docs and the skill**

- `README.md`, in the option table: add the `--no-qr` and `--open-qr` rows, and a sentence: "Il login si fa dal telefono: il QR dell'app è a tutto schermo, e il codice resta leggibile perché lo schermo non si spegne."
- `.agents/skills/run-the-login-server/SKILL.md`: add to the options table, and replace the OCR paragraph about the drawing with the rule "un codice che non ci sta non si disegna: si scrive il PNG in `.tools/gowa/login-qr.png`" and the `--no-qr` first choice; add the discovery port to the ports list.

- [x] **Step 9: Run the whole gate**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict \
  && node tools/qr-term.js --self-test && (cd WhatsappBridge && npm test)
```

Expected: three `OK:` lines, `Tutti i controlli sono passati.`, `pass 34`, `fail 0`.

- [x] **Step 10: Commit and push**

```bash
git add tools/start-login.js README.md .agents/skills/run-the-login-server/SKILL.md
git commit -m "feat(tools): make the terminal QR optional and never truncate it"
git push origin master
```

---

## Self-Review

**1. Spec coverage**

| Request | Task |
| --- | --- |
| "il dispositivo autodiscovera il server" | Task 1 (beacon) + Task 2 (listener, auto-connect on launch and on first run) |
| "si possa scansionare il qr code direttamente dal dispositivo e non dal computer" | Task 3 (full-screen QR on the phone, automatic request and refresh, screen kept awake, pairing code path) + Task 4 (`--no-qr` so the login is not the computer's job at all) |
| "il qr code sulla finestra non si vede bene per essere scansionato" | Task 4 (window-aware printing, no truncated drawing, stable PNG that Preview reloads, `Cmd -` hint) |
| "pusha tutte le modifiche" | Commit step in every task; Task 4 pushes after the last change |

No gaps found. mDNS/SSDP, a manual "pair with a QR shown by the computer" flow and a background discovery service were all considered and dropped as YAGNI: the beacon plus the manual fallback covers the same ground with less code.

**2. Placeholder scan**

No "TBD/TODO/handle edge cases/see Task N" for code: every step carries the code or the exact command. Two deliberate cross-task references exist and both repeat the literal command instead of pointing at it (`prlctl` build commands in Task 3, Step 8; the resw key counts).

**3. Type consistency**

- `createDiscoveryBeacon` returns `{ socket, sendOnce, stop }`; `server.js` uses `beacon.stop()` only.
- The beacon body has exactly one builder: `buildPayload` in `discovery.js`. `server.js` imports it and passes only the four live fields (`name`, `port`, `state`, `account`), so there is no second copy of the six keys to drift — `service` and `version` are set inside `buildPayload`, which the fake-socket test asserts. `BeaconPayload.cs` in Task 2 uses the same six names.
- `makePrinter` (Task 4) is referenced by `waitForLogin`, `showQr`, `showPairCode` and `reportLoginError`; the replacement keeps the same three members (`render`, `done`, plus the new `warned`) and drops the inner closure variable, so no caller changes.
- `DiscoveryService.Port` (8587) equals `DISCOVERY_PORT`'s default (8587).
- `AutoConnector.TryConnectAsync(string, int)` is called identically from `App.StartAutoConnect` and `ConnectionPage.StartDiscovery`.
- `DiscoveredServer` members used by XAML bindings (`DisplayName`, `Endpoint`) and by code (`Address`, `Port`, `State`) all exist in the file created in Task 2, Step 1.
- `ConnectionPage` handlers referenced from XAML (`ServersList_ItemClick`, `ManualToggleButton_Click`, `QrOverlayCloseButton_Click`) are all defined in Tasks 2 and 3.
- resw key counts quoted per step (82 → 86 in Task 2, → 89 in Task 3, → 90 after Step 6 of Task 3); adjust the expected string if another change lands first.
