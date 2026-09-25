# WhatsApp for Windows Phone 8.1

A community-maintained WhatsApp client for Windows Phone 8.1 (Universal Windows Platform). This project includes a full WhatsApp-like UI and a bridge server to connect to real WhatsApp servers.

## Projects

### WhatsappApp (Windows Phone 8.1 App)

The main client app with an authentic WhatsApp user interface.

**Features:**
- WhatsApp green theme (header #075E54, accent #25D366, chat bubbles)
- Chat list with avatars, unread badges, online indicators
- Message bubbles with timestamps and sent/delivered/read status
- Text messaging with Enter-to-send
- Image attachment: pick photos from gallery and send them via the bridge
- Image preview in chat bubbles (base64 over TCP)
- Connection settings: point the app at the GOWA adapter, then log in with a QR code or a phone pairing code
- Italian language UI

**Architecture:**

The app uses a TCP connection with length-prefixed JSON messages:

```
[4 bytes: UInt32 LE message length] [N bytes: UTF-8 JSON ChatMessage]
```

The `ChatMessage` model uses `DataContractJsonSerializer` for serialization (DateTime in `\/Date()\/` format).

### WhatsappServer (.NET Console App)

A simple TCP relay server that broadcasts messages between connected clients.

- Listens on port 8585 (default)
- Relays length-prefixed JSON messages between clients
- Displays connection logs and message previews
- Written in .NET Framework 4.5.1

> **Note:** this relay is superseded by `WhatsappBridge/server.js`, which now
> bridges the WP8 app to a self-hosted **GOWA** server instead of relaying to
> other phones. The .NET project is kept for reference / standalone use.

### GOWA Adapter (Node.js)

A thin adapter that connects the Windows Phone 8.1 app to a self-hosted
[GOWA](https://github.com/vincenzosco/go-whatsapp-web-multidevice) server
(`go-whatsapp-web-multidevice`). It does **not** implement its own WhatsApp
client any more: it uses GOWA's REST API and webhooks.

**Features**

- Login via **QR code** or via **phone number pairing code**, both shown in the app
- Keeps the encrypted (AES-256-GCM) TCP channel between app and adapter
- Sends text and images through `POST /send/message` and `POST /send/image`
- Receives incoming messages through a GOWA webhook (HMAC-verified)
- Syncs contacts from `GET /user/my/contacts`

**Setup**

1. Start GOWA:

```bash
git clone https://github.com/vincenzosco/go-whatsapp-web-multidevice
cd go-whatsapp-web-multidevice/src
go run . rest --basic-auth=admin:admin --port=3000
```

2. Start the adapter:

```bash
cd WhatsappBridge
cp .env.example .env   # optional, or export the variables
npm install
npm start
```

The adapter registers its webhook on GOWA automatically. If that fails, start
GOWA with `--webhook=http://<adapter-host>:8586/webhook`.

3. In the app, set the adapter address/port and tap **Connetti al server**, then
   log in with the QR code or with your phone number.

**Requirements:** Node.js 18.13+ and a reachable GOWA instance.

Environment variables are documented in `WhatsappBridge/.env.example`.

## Struttura dell'app

L'app e' divisa in una pagina per sezione, con una barra di navigazione
condivisa (`WhatsappApp/Controls/SectionNav.xaml`):

| Pagina | Sezione |
| --- | --- |
| `Pages/ChatsPage.xaml` | elenco chat, nuova chat, accesso alle impostazioni |
| `Pages/StatusPage.xaml` | stati |
| `Pages/CallsPage.xaml` | chiamate |
| `Pages/ChatPage.xaml` | conversazione |
| `Pages/ConnectionPage.xaml` | configurazione server e accesso WhatsApp |

Il cambio di sezione naviga sul `Frame` radice e rimuove dallo stack la sezione
lasciata, quindi il tasto **Indietro** esce dall'app da qualunque sezione invece
di ripassare tra quelle viste. Le tre pagine di sezione sono in cache
(`Frame.CacheSize = 3`): passare da una all'altra non ricostruisce la pagina e
l'elenco chat conserva la posizione di scorrimento.

## Skill del progetto

In `.agents/skills/` (indice in `.agents/skills/README.md`) ci sono le istruzioni
per mantenere, aggiornare, testare e rilasciare l'app: vincoli del toolchain,
ricette di modifica, la matrice di verifica e la checklist di deploy. Chi mette
mano al codice dovrebbe leggerle prima: sono la memoria lunga del progetto.

## Protocol

The TCP protocol uses length-prefixed JSON messages, compatible with Windows `DataWriter`/`DataReader`:

- 4 bytes: message length (UInt32, Little Endian)
- N bytes: encrypted payload

The payload is encrypted with **AES-256-GCM** using a pre-shared key (SHA-256 of a passphrase):
12-byte random IV, ciphertext, 16-byte auth tag. The app and the server must use the
same passphrase (`BRIDGE_KEY` env var on the server, constant in `CryptoHelper.cs` in the app).

After decryption, the JSON body follows the `ChatMessage` schema:

```json
{
  "Id": "msg_123",
  "Text": "Hello!",
  "SenderId": "393401234567@s.whatsapp.net",
  "SenderName": "Mario",
  "ChatId": "393401234567@s.whatsapp.net",
  "Timestamp": "\/Date(1750000000000)\/",
  "Status": 1,
  "Type": 0,
  "IsIncoming": true,
  "MediaData": "/9j/4AAQ...base64...",
  "MediaMimeType": "image/jpeg",
  "MediaFileName": "photo.jpg"
}
```

Types: 0=Text, 1=Image, 2=Audio, 3=System
Statuses: 0=Sending, 1=Sent, 2=Delivered, 3=Read, 4=Failed

`Type = 3` frames are **control frames** (`ChatId = "system"`) used for the
WhatsApp login flow: the app sends `login.qr` / `login.code` and the adapter
answers with `qr` / `paircode` / `state` / `contact` / `error` frames. See
`WhatsappBridge/README.md` for the full command table.

## Building

### WP8 App

Toolchain verificato: **Visual Studio 2013 (v12.0) + Windows Phone 8.1 SDK**. Il
gate è

```bash
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

che deve chiudere con `0 Error(s)` e produrre
`WhatsappApp\AppPackages\WhatsappApp_<versione>_Debug_Test\WhatsappApp_<versione>_x86_Debug.appxbundle`.

**Compilare da un percorso su disco locale, non dalla cartella condivisa.** Se il
progetto sta nella condivisione Mac (`C:\Mac\Home\...`), il pass 2 del compilatore
XAML fallisce *sempre* con

```
Microsoft.Windows.UI.Xaml.Common.targets(327,9): Xaml Internal Error error WMC9999:
La chiave specificata non era presente nel dizionario.
```

anche su un albero appena pulito e qualunque cosa contengano le pagine: è la
condivisione, non il codice. Copiare il progetto su un disco della macchina
Windows e compilare lì (`robocopy <condivisione> C:\wp81 /E`): stessi file,
`0 Error(s)`.

**L'emulatore WP8.1 non parte su un Mac Apple Silicon.** Le immagini XDE sono x86
e girano su Hyper-V: su un ospite Windows ARM64 non esistono né Hyper-V x86 né
quelle immagini. Per eseguire l'app servono una macchina Windows x86/x64 (fisica
o VM Intel) oppure un telefono WP8.1 collegato in USB.

Il toolchain di Windows Phone 8.1 compila l'app con il compilatore **C# 5**: la
sintassi C# 6/7 (stringhe interpolate, `?.`, proprietà con corpo `=>`,
inizializzatori di proprietà automatiche, pattern matching, `out var`) non
compila. Prima di ogni build eseguire:

```bash
node tools/check-csharp5.js
```

Esce con codice 0 quando tutti i file `.cs` della soluzione sono compatibili con
C# 5, altrimenti elenca file, riga e costrutto da correggere. Lo stesso script
controlla anche i membri **assenti dalla proiezione WinRT di Windows Phone 8.1**
(es. `CryptographicBuffer.CreateFromByteArray` a 3 argomenti,
`ContentDialog.CloseButtonText`): compilano su Windows 8.1/10 ma non su WP8.1.

### GOWA Adapter

```bash
cd WhatsappBridge
npm install
npm test     # test unitari e di integrazione
npm start
```

### Icone, tile e splash screen

Il logo WhatsApp (bolla bianca con la cornetta ritagliata) è disegnato via
geometria vettoriale da `tools/make-brand-assets.js`, che richiede ImageMagick 7
(`magick`) e riscrive i PNG in `WhatsappApp/Assets/` — già committati, quindi lo
script serve solo se cambia la grafica:

```bash
node tools/make-brand-assets.js            # riscrive i PNG
node tools/make-brand-assets.js --preview  # + anteprima ASCII per controllare il logo
```

Le icone dell'interfaccia (ricerca, impostazioni, tab, allegati, invio…)
**non** usano un font di icone: Windows Phone 8.1 non ha `Segoe MDL2 Assets`
(è arrivato con Windows 10), quindi i pulsanti restavano vuoti. Sono `Path`
vettoriali con la geometria **in linea su ogni `Path`**, preceduta da un
commento che dà un nome all'icona:

```xml
<Path Stroke="White" StrokeThickness="2" Width="24" Height="24">
    <!-- IconChats -->
    <Path.Data>
        <PathGeometry>
            <PathGeometry.Figures>
                <PathFigure StartPoint="4,5" IsClosed="True">
                    <PathFigure.Segments>
                        <PolyLineSegment Points="20,5 20,15.5 10.5,15.5 5.5,20 5.5,15.5 4,15.5"/>
                    </PathFigure.Segments>
                </PathFigure>
            </PathGeometry.Figures>
        </PathGeometry>
    </Path.Data>
</Path>
```

Due regole non sono preferenze di stile ma requisiti del toolchain:

- la geometria **non** può stare in `App.xaml` e arrivare qui con
  `Data="{StaticResource Icon…}"`: compila, poi a runtime lancia
  `XamlParseException: Failed to assign to property
  'Windows.UI.Xaml.Shapes.Path.Data'.` — in WinRT una `Geometry` non è
  condivisibile attraverso una `StaticResource`
  ([microsoft-ui-xaml#1909](https://github.com/microsoft/microsoft-ui-xaml/issues/1909),
  [#5780](https://github.com/microsoft/microsoft-ui-xaml/issues/5780));
- la geometria va scritta in forma di elementi (`PathFigure` + `LineSegment` /
  `PolyLineSegment` / `ArcSegment`): su WP8.1 il convertitore di
  `PathFigureCollection` non accetta la stringa, quindi `Figures="M…"` **non
  compila** (`The TypeConverter for "PathFigureCollection" does not support
  converting from a string.`).

Il guard verifica entrambe (più "stessa icona, stessa geometria"):

```bash
node tools/check-icons.js            # regole + coerenza + font vietati
node tools/check-icons.js --preview  # + anteprima ASCII (richiede ImageMagick)
```

### Lingua dell'app

L'app segue automaticamente la lingua del dispositivo tramite risorse `.resw`:

| Lingua | File | Note |
| --- | --- | --- |
| Inglese | `WhatsappApp/Strings/en-US/Resources.resw` | `<DefaultLanguage>`: fallback per ogni altra lingua |
| Italiano | `WhatsappApp/Strings/it-IT/Resources.resw` | |

- I testi dichiarati in XAML usano `x:Uid`, e la proprieta' deve corrispondere al
  tipo dell'elemento: `TextBlock` -> `.Text`, `Button` -> `.Content`,
  `TextBox` -> `.PlaceholderText`. Un abbinamento sbagliato e' un errore a
  run time.
- I testi costruiti in C# passano da `Loc.Get("Chiave", "fallback")`
  (`WhatsappApp/Services/Loc.cs`), che non lancia mai eccezioni: se la risorsa
  manca usa il fallback. `Loc.Prewarm()` viene chiamato all'avvio sul thread UI
  perche' `ResourceLoader.GetForCurrentView()` non si puo' creare da un thread
  di background (i messaggi arrivano dal socket su un thread di background).
- I pulsanti con la sola icona non usano `x:Uid` (sovrascriverebbe il `Path`):
  l'etichetta e' un tooltip impostato da `Loc.Get` nel costruttore della pagina.
- Prima di ogni build, o dopo aver toccato una stringa:

```bash
node tools/check-resw.js            # chiavi, x:Uid, Loc.Get, PRIResource, lingua di default
node tools/check-resw.js --strict   # + fallisce sulle chiavi inutilizzate
```

Lo script fallisce se una `x:Uid` o una `Loc.Get` non ha la voce in **entrambi**
i file, se i due file non hanno le stesse chiavi, se un `.resw` non e' registrato
come `PRIResource` nel `.csproj` (in quel caso non verrebbe mai incluso nel
pacchetto) o se `<DefaultLanguage>` non e' una delle lingue supportate. Senza
questo controllo un errore nelle risorse **non** fa fallire la build: il testo
resta semplicemente quello scritto nel markup.

Per verificare le traduzioni sul dispositivo basta cambiare la lingua di sistema
(Impostazioni > Data/ora e lingua): Windows riavvia l'app e le stringhe cambiano
di conseguenza. Se l'app resta nella lingua precedente, chiuderla e riaprirla.

## Disclaimer

- This is an unofficial project not affiliated with WhatsApp or Meta.
- GOWA (and therefore this adapter) uses unofficial methods to connect to WhatsApp, which violates WhatsApp's Terms of Service.
- Using this bridge may result in a permanent ban of your phone number.
- Only use with test/secondary phone numbers.
- For production use, refer to the official WhatsApp Business API.

## License

MIT - Community maintained project. Use at your own risk.
