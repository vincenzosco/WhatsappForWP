# WhatsApp per Windows Phone 8.1

[English](README.md) | **Italiano**

Un client WhatsApp per Windows Phone 8.1 (Universal Windows Platform) mantenuto
dalla community. Il progetto comprende un'interfaccia completa in stile WhatsApp e
un server ponte per collegarsi ai server WhatsApp veri.

## Progetti

### WhatsappApp (app Windows Phone 8.1)

L'app principale, con un'interfaccia utente autentica di WhatsApp.

**Funzioni:**
- tema verde WhatsApp (intestazione #075E54, accento #25D366, fumetti dei messaggi)
- elenco chat con avatar, contatori dei non letti, indicatori di presenza
- fumetti con orario e stato inviato/consegnato/letto
- messaggi di testo con Invio per inviare
- allegati immagine: si sceglie una foto dalla galleria e si invia attraverso il ponte
- anteprima dell'immagine nel fumetto (base64 sul canale TCP)
- l'app trova l'adapter sulla rete locale da sola, quindi non c'e' nessun indirizzo da digitare
- login dal telefono: il QR o il codice di abbinamento compare a tutto schermo nell'app
- interfaccia nella lingua del dispositivo: inglese e italiano

**Architettura:**

L'app usa una connessione TCP con messaggi JSON preceduti dalla lunghezza:

```
[4 byte: UInt32 LE lunghezza] [N byte: JSON UTF-8 del ChatMessage]
```

Il modello `ChatMessage` viene serializzato con `DataContractJsonSerializer`
(DateTime nel formato `\/Date()\/`).

### WhatsappServer (app console .NET)

Un semplice server TCP di inoltro, che distribuisce i messaggi tra i client
collegati.

- ascolta sulla porta 8585 (default)
- inoltra tra i client messaggi JSON preceduti dalla lunghezza
- mostra a video i collegamenti e un'anteprima dei messaggi
- scritto in .NET Framework 4.5.1

> **Nota:** questo relay e' stato sostituito da `WhatsappBridge/server.js`, che ora
> fa da ponte tra l'app WP8 e un server **GOWA** self-hosted invece di inoltrare i
> messaggi ad altri telefoni. Il progetto .NET resta per riferimento o per un uso
> autonomo.

### Adapter GOWA (Node.js)

Un adapter sottile che collega l'app per Windows Phone 8.1 a un server
[GOWA](https://github.com/vincenzosco/go-whatsapp-web-multidevice) self-hosted
(`go-whatsapp-web-multidevice`). **Non** implementa piu' un client WhatsApp
proprio: usa l'API REST e i webhook di GOWA.

**Funzioni**

- login con **QR code** o con **codice di abbinamento** del numero, entrambi mostrati nell'app
- si annuncia sulla rete locale in UDP, quindi l'app lo trova senza essere configurata
- mantiene il canale TCP cifrato (AES-256-GCM) tra app e adapter
- invia testi e immagini con `POST /send/message` e `POST /send/image`
- riceve i messaggi in arrivo da un webhook di GOWA (con verifica HMAC)
- sincronizza i contatti da `GET /user/my/contacts`

**Avvio (un solo comando)**

```bash
node tools/start-login.js --download   # --download solo la prima volta
```

Lo script scarica in `.tools/gowa` il binario ufficiale di GOWA per **il sistema e
la CPU su cui stai girando** (macOS Intel/ARM, Linux x64/arm64/armv7/386, Windows
x64/386), verifica il SHA-256 pubblicato e lo scompatta con `node:zlib`: non
servono `unzip` ne' `tar`. Poi avvia `whatsapp rest`, avvia
`WhatsappBridge/server.js`, annuncia l'adapter sulla rete locale in UDP (cosi'
l'app lo trova **da sola**) e stampa indirizzi e porte. Il login si fa normalmente
**dal telefono**: l'app mostra il QR a tutto schermo, quindi non c'e' niente da
inquadrare dal computer. Il QR nel terminale resta disponibile e viene disegnato
come un codice davvero scansionabile, rinnovato finche' serve; quando non entra
nella finestra lo script lo dice e scrive il PNG in `.tools/gowa/login-qr.png`
invece di disegnare qualcosa di tagliato. L'adapter registra da solo il suo webhook
su GOWA.

Quali servizi partono e' un elenco dichiarativo (`tools/services.js`), non due
figli scritti a mano: metti un `WhatsappCallServer/server.js` nel repo e il
launcher lo avvia anche lui, gli da' la sua porta, lo mostra nel riepilogo e lo
ferma con `Ctrl-C` / `--stop` come gli altri (`--no-calls` lo spegne,
`--list-services` mostra l'elenco risolto).

| Opzione | Effetto |
| --- | --- |
| `--code 393401234567` | collega con il codice di abbinamento invece del QR |
| `--no-bridge` | solo GOWA e il QR |
| `--once` | disegna un solo QR ed esce |
| `--no-qr` | non disegna niente: il login si fa dal telefono, nell'app (consigliato) |
| `--open-qr` | apre il PNG del codice in Anteprima, dove si ricarica a ogni rotazione |
| `--url http://host:3000` | usa un GOWA gia' avviato |
| `--ui` | serve anche la dashboard web di GOWA |
| `--stop` | ferma lo stack avviato prima |

La sessione WhatsApp sta in `.tools/gowa/storages/whatsapp.db` (ignorata da git),
quindi dai lanci successivi ci si ricollega da soli senza un nuovo QR. `Ctrl-C`
ferma GOWA e l'adapter.

Poi, nell'app: trova l'adapter sulla rete e si collega da sola (c'e' comunque
**Inserisci l'indirizzo a mano** per un server che non si riesce a scoprire). Il
login si fa dal telefono — l'app mostra il suo QR a tutto schermo e tiene lo schermo
acceso finche' resta visibile — oppure si riusa la sessione gia' collegata.

**Requisiti:** Node.js 18.13+ e, per il QR nel terminale, ImageMagick 7 (`magick`).
Le variabili d'ambiente dell'adapter sono documentate in
`WhatsappBridge/.env.example` (il file viene letto all'avvio; le variabili gia'
esportate hanno la precedenza).

**A mano**, se si preferisce avviare i pezzi da soli: avviare un GOWA che parli
l'API REST v9 (`whatsapp rest --port=3000 --host=127.0.0.1`), poi
`cd WhatsappBridge && cp .env.example .env && npm start`.

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

## Protocollo

Il protocollo TCP usa messaggi JSON preceduti dalla lunghezza, compatibili con
`DataWriter`/`DataReader` di Windows:

- 4 byte: lunghezza del messaggio (UInt32, Little Endian)
- 1 byte: tag cifrario (`1` = AES-256-GCM, `2` = AES-256-CBC + HMAC-SHA256)
- N byte: payload cifrato

Il payload e' cifrato con **AES-256-CBC e autenticato con HMAC-SHA256** con una chiave
condivisa (lo `SHA-256` di una passphrase, da cui entrambe le parti derivano due chiavi
con `HMAC-SHA256`): IV casuale di 16 byte, testo cifrato, HMAC di 32 byte su IV e cifrato.
Il tag `1` resta accettato e porta IV di 12 byte, cifrato e tag GCM di 16 byte, ma
**l'app scrive sempre il tag `2`**: Windows Phone 8.1 risponde a AES-GCM con
`NotImplementedException 0x80004001`. L'adapter risponde a ciascun client con il cifrario
che quel client ha usato. App e server devono usare la stessa passphrase (variabile
`BRIDGE_KEY` sul server, costante in `CryptoHelper.cs` nell'app).

Dopo la decifratura, il corpo JSON segue lo schema `ChatMessage`:

```json
{
  "Id": "msg_123",
  "Text": "Ciao!",
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

Tipi: 0=Testo, 1=Immagine, 2=Audio, 3=Sistema
Stati: 0=In invio, 1=Inviato, 2=Consegnato, 3=Letto, 4=Fallito

I frame con `Type = 3` sono **frame di controllo** (`ChatId = "system"`) usati per
il flusso di login WhatsApp: l'app manda `login.qr` / `login.code` e l'adapter
risponde con i frame `qr` / `paircode` / `state` / `contact` / `error`. La tabella
completa dei comandi e' in `WhatsappBridge/README.it.md`.

Una lunghezza di frame non viene mai creduta sulla parola: l'app riempie per
intero il prefisso di 4 byte (`InputStreamOptions.Partial` puo' spezzarlo) e
rifiuta qualunque valore fuori da `1..8 MiB` (`MaxFrameLength`), e l'adapter
chiude il client che annuncia piu' di `MAX_FRAME_LENGTH` (gli stessi 8 MiB)
invece di accumularlo.

Il byte order e' detto per esteso da entrambe le parti: l'adapter scrive la
lunghezza con `writeUInt32LE` e l'app costruisce lettori e scrittori con
`CreateFrameReader`/`CreateFrameWriter`, che impostano
`ByteOrder = ByteOrder.LittleEndian`. Il valore predefinito di WinRT non e'
little-endian, e un lettore che non concorda non fallisce in modo evidente:
legge una lunghezza invertita (`0x00000121` tornava come `0x21010000`, 553713664)
e scarta un frame che era perfettamente valido. `tools/check-framing.js` fa
fallire il gate veloce se nella parte socket un `DataReader`/`DataWriter` viene
creato in un altro modo.

Tutto cio' che il server e l'app stampano a runtime e' in inglese: il log e i
messaggi di errore dell'adapter, e le righe `DIAG` dell'app con i messaggi delle
eccezioni che ci finiscono. I commenti nel sorgente e i nomi dei test
dell'adapter non rientrano nella regola, e nemmeno le stringhe localizzate in
`Strings/it-IT`, che sono traduzioni e non diagnostica. Anche i testi di errore
che l'adapter manda all'app per essere mostrati sono contenuto UI, e restano in
italiano in attesa della localizzazione dell'app.

Un tentativo di connessione possiede il suo socket, il suo `DataReader` e il suo
ciclo di lettura: solo il tentativo piu' recente li pubblica e solo il suo ciclo
li legge, quindi un tentativo fallito (per esempio su un indirizzo salvato che
non risponde piu') non puo' chiudere la connessione che invece e' riuscita. La
connessione ha una scadenza di 6 secondi; `0x8007274C` significa che e' scaduta,
e l'app dimentica l'indirizzo salvato e ripiega sulla scoperta.

## Compilazione

### App WP8

Toolchain verificato: **Visual Studio 2013 (v12.0) + Windows Phone 8.1 SDK**. Il
gate e'

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

anche su un albero appena pulito e qualunque cosa contengano le pagine: e' la
condivisione, non il codice. Copiare il progetto su un disco della macchina
Windows e compilare li' (`robocopy <condivisione> C:\wp81 /E`): stessi file,
`0 Error(s)`.

**L'emulatore WP8.1 non parte su un Mac Apple Silicon.** Le immagini XDE sono x86
e girano su Hyper-V: su un ospite Windows ARM64 non esistono ne' Hyper-V x86 ne'
quelle immagini. Per eseguire l'app servono una macchina Windows x86/x64 (fisica
o VM Intel) oppure un telefono WP8.1 collegato in USB.

Il toolchain di Windows Phone 8.1 compila l'app con il compilatore **C# 5**: la
sintassi C# 6/7 (stringhe interpolate, `?.`, proprieta' con corpo `=>`,
inizializzatori di proprieta' automatiche, pattern matching, `out var`) non
compila. Prima di ogni build eseguire:

```bash
node tools/check-csharp5.js
```

Esce con codice 0 quando tutti i file `.cs` della soluzione sono compatibili con
C# 5, altrimenti elenca file, riga e costrutto da correggere. Lo stesso script
controlla anche i membri **assenti dalla proiezione WinRT di Windows Phone 8.1**
(es. `CryptographicBuffer.CreateFromByteArray` a 3 argomenti,
`ContentDialog.CloseButtonText`): compilano su Windows 8.1/10 ma non su WP8.1.

### Adapter GOWA

```bash
cd WhatsappBridge
npm install
npm test     # test unitari e di integrazione
npm start
```

### Icone, tile e splash screen

Il logo WhatsApp (bolla bianca con la cornetta ritagliata) e' disegnato via
geometria vettoriale da `tools/make-brand-assets.js`, che richiede ImageMagick 7
(`magick`) e riscrive i PNG in `WhatsappApp/Assets/` — gia' committati, quindi lo
script serve solo se cambia la grafica:

```bash
node tools/make-brand-assets.js            # riscrive i PNG
node tools/make-brand-assets.js --preview  # + anteprima ASCII per controllare il logo
```

Le icone dell'interfaccia (ricerca, impostazioni, tab, allegati, invio…)
**non** usano un font di icone: Windows Phone 8.1 non ha `Segoe MDL2 Assets`
(e' arrivato con Windows 10), quindi i pulsanti restavano vuoti. Sono `Path`
vettoriali con la geometria **in linea su ogni `Path`**, preceduta da un
commento che da' un nome all'icona:

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

- la geometria **non** puo' stare in `App.xaml` e arrivare qui con
  `Data="{StaticResource Icon…}"`: compila, poi a runtime lancia
  `XamlParseException: Failed to assign to property
  'Windows.UI.Xaml.Shapes.Path.Data'.` — in WinRT una `Geometry` non e'
  condivisibile attraverso una `StaticResource`
  ([microsoft-ui-xaml#1909](https://github.com/microsoft/microsoft-ui-xaml/issues/1909),
  [#5780](https://github.com/microsoft/microsoft-ui-xaml/issues/5780));
- la geometria va scritta in forma di elementi (`PathFigure` + `LineSegment` /
  `PolyLineSegment` / `ArcSegment`): su WP8.1 il convertitore di
  `PathFigureCollection` non accetta la stringa, quindi `Figures="M…"` **non
  compila** (`The TypeConverter for "PathFigureCollection" does not support
  converting from a string.`).

Il guard verifica entrambe (piu' "stessa icona, stessa geometria"):

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

### Documentazione

I documenti che spiegano il progetto esistono in due lingue, inglese e italiano, e
vanno tenuti allineati:

| Documento | Inglese | Italiano |
| --- | --- | --- |
| README del progetto | `README.md` | `README.it.md` |
| README dell'adapter | `WhatsappBridge/README.md` | `WhatsappBridge/README.it.md` |

Aggiungere, spostare o rinominare una sezione significa farlo in entrambi i file,
nello stesso commit, e lo stesso vale per la sezione `## Disclosure` in fondo a
ogni README. Un nuovo documento che spiega il progetto nasce gia' in coppia. Il
guard rifiuta una coppia con heading diversi, un documento senza la disclosure e
qualunque emoji diversa dal segno di pericolo:

```bash
node tools/check-docs.js
```

## Disclaimer

- Questo e' un progetto non ufficiale, non affiliato a WhatsApp o a Meta.
- GOWA (e quindi questo adapter) usa metodi non ufficiali per collegarsi a
  WhatsApp, il che viola i Termini di servizio di WhatsApp.
- Usare questo ponte puo' causare il ban permanente del proprio numero.
- Usarlo solo con numeri di prova o secondari.
- Per un uso in produzione fare riferimento alla WhatsApp Business API ufficiale.

## Licenza

MIT - progetto mantenuto dalla community. Usalo a tuo rischio.

## Disclosure

**Questo progetto e' open source e ha bisogno di maintainer.** Issue, traduzioni,
revisioni, documentazione e pull request sono tutte benvenute, come lo e' chiunque
voglia aiutarlo a crescere: piu' mani e' l'unica cosa che lo fa andare avanti piu'
in fretta.

**L'app e' stata scritta al 100% da un agente AI**, guidato e rivisto da una
persona. Leggere il codice con il sospetto che merita: eseguire i guard in
`tools/`, eseguire i test dell'adapter e controllare qualunque cosa tocchi il
proprio account prima di fidarsene.

**L'autore non si assume la responsabilita' dell'account WhatsApp con cui si
accede.** Collegare questo client significa connettere un client non ufficiale a
WhatsApp, contro i Termini di servizio di WhatsApp, e l'account puo' essere bannato
in modo permanente. Usare un numero di prova o secondario, e solo se si accetta
quel rischio per conto proprio.
