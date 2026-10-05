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
- elenco chat con avatar e contatori dei non letti
- tile live con quanti messaggi aspettano, la foto di chi ha scritto per ultimo e il suo nome
- fumetti con orario e stato inviato/consegnato/letto
- messaggi di testo con Invio per inviare
- allegati immagine: si sceglie una foto dalla galleria e si invia attraverso il ponte
- anteprima dell'immagine nel fumetto (base64 sul canale TCP)
- l'app trova l'adapter sulla rete locale da sola, quindi non c'e' nessun indirizzo da digitare
- login dal telefono: il QR o il codice di abbinamento compare a tutto schermo nell'app
- indicatore di scrittura: tre puntini animati nella conversazione mentre l'altra persona scrive, e lo stesso stato viene mandato mentre scrivi tu
- interfaccia nella lingua del dispositivo: inglese e italiano

**Architettura:**

L'app usa una connessione TCP con messaggi JSON preceduti dalla lunghezza:

```
[4 byte: UInt32 LE lunghezza] [N byte: JSON UTF-8 del ChatMessage]
```

Il modello `ChatMessage` viene serializzato con `DataContractJsonSerializer`; il
suo campo `Timestamp` e' il semplice valore `/Date(<ms>)/`, senza backslash.

#### Chiamate

La scheda Chiamate elenca **solo** le chiamate in entrata, prese dalle chat che
l'adapter ha scansionato (`CALLS_CHAT_LIMIT`, default 25), aggiornate all'apertura
della scheda o con il pulsante Aggiorna. GOWA non conserva le chiamate in uscita,
quindi non c'e' altro da mostrare.

#### Chat e nuove chat

L'elenco chat e' la lista vera delle conversazioni dell'account (`GET /chats`,
limitata da `CHATS_LIMIT`), non la rubrica di WhatsApp, che su un dispositivo
appena collegato e' vuota. Ogni riga porta l'ultimo messaggio e la sua immagine
del profilo (`GET /user/avatar` e poi l'indirizzo CDN che restituisce, quindi due
richieste per chat, gruppi compresi; si spegne con `CHATS_AVATARS=off`). Il nome di
un gruppo arriva da `GET /user/my/groups`, una richiesta per tutti, perche'
l'elenco delle conversazioni non ha un nome utilizzabile per un gruppo e ripiega
su `Group <numero>`.

Una nuova chat si apre in tre modi: digitando un numero con prefisso, scegliendo
un contatto con il selettore del sistema (e' il consenso dell'utente, quindi l'app
non legge mai la rubrica per conto suo), oppure toccando una conversazione che il
server conosce gia'.

Mentre qualcuno scrive, nella conversazione compare un fumetto con tre puntini, uno
che sale dopo l'altro, e sparisce quando smette. E' l'unica cosa che questa app mostra
che non sia un messaggio, e arriva da WhatsApp (l'evento `chat_presence`, che GOWA 9.5
inoltra), non da un'ipotesi. Nell'altra direzione funziona allo stesso modo: mentre
scrivi tu, l'adapter lo dice a WhatsApp e il contatto vede gli stessi puntini. Le due
direzioni hanno bisogno che l'account sia online, quindi l'adapter lo mette `available`
mentre l'app e' collegata e `unavailable` quando non lo e'.

Allegare un'immagine usa `PickSingleFileAndContinue`. `PickSingleFileAsync` e'
documentata come non supportata su Windows Phone, e sul telefono falliva in
silenzio: l'app viene deattivata mentre il selettore e' aperto, e il file scelto
arriva ad `App.OnActivated` come `PickFileContinuation`.

Condividere un'immagine dentro l'app funziona da qualsiasi app che offra
Condividi (Foto, Galleria, un browser): il manifest dichiara un'estensione
`windows.shareTarget` per `Bitmap` e `StorageItems`, e `App.OnShareTargetActivated`
mette l'immagine nello stesso posto in attesa che usa il selettore. L'app si apre
sull'elenco chat, perche' il passo successivo e' scegliere a chi mandarla.

Un vocale si registra nell'app: il pulsante del microfono avvia
`Windows.Media.Capture.MediaCapture` (solo audio, niente fotocamera) e scrive
AAC in un file M4A nella cartella dell'app, che e' quello che questo telefono
registra e riproduce senza transcodifica. Il pulsante di stop chiude la
registrazione, e il file aspetta nello stesso posto di una foto scelta: la barra
di anteprima lo mostra e Invia lo spedisce. Sul lato adapter un vocale registrato
va su `POST /send/audio`, che e' cio' che fa disegnare a WhatsApp un vocale con
la forma d'onda invece di un allegato audio.

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
- il telefono puo' generare da se' la chiave del canale e il proprio token e consegnarli al server una volta sola, provati da un codice che il server stampa all'avvio, quindi un server raggiungibile non ha bisogno di nessun segreto digitato sopra (vedi il README dell'adapter, *Accoppiamento*)
- invia testi, foto, video e file (`POST /send/message`, `/send/image`, `/send/video`, `/send/file`); un allegato piu' grande di un frame viaggia a pezzi (`media.begin` / `media.chunk` / `media.end`), e un media che arriva da WhatsApp torna allo stesso modo in frame `media` che portano l'indice del pezzo
- riceve i messaggi in arrivo da un webhook di GOWA (con verifica HMAC)
- tiene viva la connessione da sola: un watchdog chiede lo stato ogni 20 s, e una connessione silenziosa da 60 s viene chiusa e riaperta, quindi l'app si riprende da sola quando WP8.1 le chiude il socket mentre e' sospesa
- carica i messaggi gia' in memoria aprendo una chat (`messages`, fino a `MESSAGES_LIMIT`), con frame marcati `IsHistory`: vengono inseriti in ordine di data e restano fuori dal conteggio dei non letti e dagli avvisi
- la foto del profilo di una chat e il suo nome sono due bersagli: toccando la foto si apre a tutto schermo, toccando il nome si aprono le informazioni, con il numero, l'about, il profilo aziendale e, per un gruppo, la descrizione e i membri con il loro ruolo (`contact.info`). Tutto quello che il server non ha viene lasciato fuori, e una pagina senza niente da mostrare lo dice
- un video ricevuto si riproduce a tutto schermo su un `MediaElement` con i controlli di sistema, e si chiude con la X nell'angolo. I suoi byte arrivano a pezzi e vengono scritti su un file mentre arrivano, quindi un video di lunghezza intera non sta mai tutto in memoria; toccare la casella con il triangolo prima che i byte ci siano li scarica prima, come per un'immagine
- un vocale o un audio ricevuto mostra una barra con il triangolo e si riproduce nello stesso lettore. WhatsApp manda i vocali come Ogg/Opus e WP8.1 non ha un decoder Opus, quindi l'adapter li converte prima in un piccolo MP3 mono, usando `ffmpeg` quando e' installato (`FFMPEG_ENABLED`, `FFMPEG_PATH`); senza `ffmpeg` il vocale arriva lo stesso e dice che non si puo' riprodurre
- un documento ricevuto mostra il nome del file e una barra con un foglio, e toccarla apre il file con l'app del telefono. I suoi byte arrivano su un file come quelli di un video, e un documento di una vecchia conversazione viene scaricato al tocco
- un file condiviso o scelto dalla Galleria viene copiato nella cartella dell'app invece che letto in memoria, quindi un video lungo si spedisce a pezzi invece di chiudere l'app
- una chat si disegna dai messaggi in cache prima che la connessione ci sia, e gli ultimi messaggi restano sul telefono per conversazione
- elenca le **conversazioni** vere dell'account da `GET /chats` (la rubrica e' vuota su un dispositivo appena collegato), ognuna con l'ultimo messaggio e la sua **immagine del profilo** da `GET /user/avatar` (due richieste per chat, gruppi compresi: l'endpoint restituisce l'indirizzo dell'immagine, non l'immagine) (`CHATS_LIMIT`, `CHATS_AVATARS`)
- Le chat si possono **fissare** (il menu dei tre puntini in alto a sinistra, o una pressione prolungata su una riga), **silenziare** (nessun avviso per quella conversazione; il numero dei non letti resta) ed **eliminare da questo telefono** (pressione prolungata, poi Elimina). Sono tre decisioni di questo telefono: il server non ha un endpoint per nessuna delle tre, quindi vivono nella cartella dell'app, e una chat fissata qui non e' fissata sugli altri dispositivi
- Una chat eliminata torna quando ci arriva un messaggio nuovo, e su WhatsApp non viene cancellato niente: questa app elimina la conversazione da questo telefono, non dall'account
- tiene sul telefono l'ultimo elenco delle conversazioni: l'elenco e' a schermo mentre la connessione sta ancora arrivando, e si aggiorna appena WhatsApp si dichiara collegato

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
I vocali hanno bisogno anche di `ffmpeg` sulla macchina che esegue l'adapter.
Le variabili d'ambiente dell'adapter sono documentate in
`WhatsappBridge/.env.example` (il file viene letto all'avvio; le variabili gia'
esportate hanno la precedenza).

**A mano**, se si preferisce avviare i pezzi da soli: avviare un GOWA che parli
l'API REST v9 (`whatsapp rest --port=3000 --host=127.0.0.1`), poi
`cd WhatsappBridge && cp .env.example .env && npm start`.

Per mettere in piedi l'adapter (con GOWA) su un NAS o su un PC sempre acceso c'e'
un repository di deployment separato:
[vincenzosco/docker-whatsappforwp](https://github.com/vincenzosco/docker-whatsappforwp).
Pubblica un'immagine a container unico con GOWA e questo adapter, e lo stesso
server si avvia ancora con `node server.js`.

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

## Contribuire

Segnalazioni e pull request sono benvenute. Il progetto e' piccolo di proposito, e
i guard in `tools/` sono il contratto: una modifica che li passa in locale e'
quasi sempre pronta da integrare.

1. Fai il fork del repository e lavora su un ramo (`fix/...`, `feat/...`, `docs/...`).
2. Leggi `.agents/skills/maintain-the-app/SKILL.md` prima di toccare il codice:
   solo C# 5, icone vettoriali inline, nessuna stringa visibile hardcoded, documenti
   sempre in coppia.
3. Fai la modifica, poi esegui il gate veloce:

   ```bash
   node tools/check-csharp5.js && node tools/check-icons.js \
     && node tools/check-resw.js --strict && node tools/check-docs.js \
     && node tools/check-framing.js && node tools/check-tile.js \
     && node tools/check-memory.js && node tools/check-actions.js \
     && node tools/check-fire-and-forget.js \
     && node tools/check-project-files.js \
     && node tools/check-chat-list-source.js \
     && node --test "tools/test/**/*.test.js"
   cd WhatsappBridge && npm test
   ```

4. Se tocchi `WhatsappApp/` o `WhatsappServer/`, compilalo su Windows
   (`msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`)
   e scrivi nella pull request che riporta `0 Error(s)`. Non esiste un host di test
   C#, quindi per l'app il gate e' la build.
5. Scrivi l'oggetto del commit come `tipo: imperativo breve` in inglese, e di'
   *perche'* serve la modifica, non solo cosa fa: la cronologia e' il changelog.
6. Testi visibili: aggiungi la chiave in **entrambi** i file `.resw`, e aggiorna
   sia `README.md` sia `README.it.md` nello stesso commit.

Una segnalazione utile contiene le righe `DIAG` della finestra Output, quale build
hai usato e cosa ha fatto il telefono. Se puoi, lancia
`node tools/start-login.js --no-qr` e allega il log dell'adapter.

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
rifiuta qualunque valore fuori da `1..8 MiB` (`FrameCodec.MaxFrameLength`), e l'adapter
chiude il client che annuncia piu' di `MAX_FRAME_LENGTH` (gli stessi 8 MiB)
invece di accumularlo.

`Timestamp` e' l'unico campo di cui vale la pena dire il *tipo* sul filo: porta
`/Date(<millisecondi dal 1970, UTC>)/`, e nessun backslash - il `\/` che si vede nel testo JSON e'
l'escape del lettore, non parte del valore. Scrivere il valore con i backslash (un difetto
dell'adapter, corretto in questi commit) faceva buttare via l'intero frame al telefono con
`SerializationException 0x8013150C`, "String was not recognized as a valid DateTime". L'app legge
quel campo come stringa e lo interpreta con tolleranza, quindi un timestamp che non riesce a
leggere costa il timestamp, non il messaggio.

Il byte order e' detto per esteso da entrambe le parti: l'adapter scrive la
lunghezza con `writeUInt32LE` e l'app costruisce lettori e scrittori con i
`CreateFrameReader`/`CreateFrameWriter` di `WhatsappApp/Services/FrameCodec.cs`,
che impostano `ByteOrder = ByteOrder.LittleEndian`. Il valore predefinito di WinRT non e'
little-endian, e un lettore che non concorda non fallisce in modo evidente:
legge una lunghezza invertita (`0x00000121` tornava come `0x21010000`, 553713664)
e scarta un frame che era perfettamente valido. `tools/check-framing.js` fa
fallire il gate veloce se nella parte socket un `DataReader`/`DataWriter` viene
creato in un altro modo.

Tutto cio' che il server e l'app stampano a runtime e' in inglese: il log e i
messaggi di errore dell'adapter, e le righe `DIAG` dell'app con i messaggi delle
eccezioni che ci finiscono. Anche i commenti nel sorgente sono in inglese: il
codice si spiega in una lingua sola. Restano fuori i nomi dei test e le
diagnostiche degli script di guardia in `tools/` - strumenti per sviluppatori che
nessun operatore esegue - e le stringhe localizzate in `Strings/it-IT`, che sono
traduzioni, non diagnostica. Anche i testi di errore che l'adapter manda all'app
per essere mostrati sono contenuto UI, e restano in italiano in attesa della
localizzazione dell'app.

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

Il logo WhatsApp (bolla verde con la cornetta ritagliata) parte dall'immagine di
riferimento `tools/brand/logo-source.png` (il marchio su fondo bianco).
`tools/make-brand-assets.js` toglie quel fondo bianco e riscrive i PNG in
`WhatsappApp/Assets/` — gia' committati, quindi lo script serve solo se cambia la
grafica. E' Node puro, senza ImageMagick: il PNG viene letto e scritto con `zlib`.

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

La tile live ha un asset suo, `Assets/TileIcon.png` piu' la sua versione al 240%,
`TileIcon.scale-240.png` (480×480): un PNG trasparente scritto dallo stesso
`make-brand-assets.js`, con il marchio disegnato dentro la tile nella stessa
proporzione interna dei logo del manifest. Non e' un doppione di `Logo.png`: il
modello della tile iconica disegna l'immagine cosi' com'e', quindi un marchio che
riempie tutto l'asset su una tile da 150 px sembra ingrandito e tagliato.

Il manifest imposta `BackgroundColor="#FFFFFF"` insieme a
`ForegroundText="dark"`. Le icone e la tile sono bianche e il marchio e' verde,
quindi si distinguono; `dark` e' l'altra meta' della coppia, perche' testo chiaro
su una tile bianca non si vede.

`tools/check-tile.js` la custodisce, perche' il guasto che intercetta e' invisibile.
Un modello di tile **non** prende l'icona dal manifest: la vuole nel payload, in un
`<image src="..."/>`, e con `src` vuoto la tile si disegna senza icona e non solleva
nessuna eccezione.

### Memoria su un telefono da 512 MB

Un telefono da 512 MB da' all'app un tetto di memoria rigido (piu' o meno 185 MB; un
dispositivo da 1 GB concede circa il doppio) e la sospende o la chiude se continua a
crescere. WP8.1 **non ha una dichiarazione di manifest per questo** -
`AppxManifestSchema2010_v2.xsd` e i suoi fratelli non hanno nessun elemento di
memoria - quindi il lavoro e' a runtime, in due meta':

- **decodifica alla misura che disegni**: `ImageHelper` riceve la larghezza a cui
  l'immagine viene mostrata e imposta `DecodePixelWidth` prima di `SetSourceAsync`.
  Un'immagine del profilo da 640×640 disegnata in un cerchio da 52 px costa qualche
  decina di KB invece di quasi 2 MB, moltiplicato per una conversazione;
- **molla la presa quando te lo chiede**: `MemoryWatcher` ascolta
  `MemoryManager.AppMemoryUsageIncreased` e, al livello `High`, butta le bitmap
  degli avatar decodificate e svuota la cronologia di ogni chat che non e' aperta
  (quella aperta non si tocca, e' quella che si sta leggendo). Finche' la pressione
  dura non si decodifica niente di nuovo. Il limite del telefono viene scritto una
  volta, come `DIAG ok: memory budget N MB`;
- **e si richiedono dal telefono**: i byte di un'immagine si tengono in
  `avatar-cache.json` (al massimo 40 conversazioni, circa 1 MB), cosi' un riavvio
  mostra le facce prima che l'adapter risponda, e una riga la cui bitmap decodificata
  e' stata buttata via si ridisegna quando l'elenco torna davanti. Le immagini sono
  la copia di quello che l'adapter ha mandato: anche l'adapter le tiene per cinque
  minuti, cosi' un elenco chat letto due volte non torna da WhatsApp.

`tools/check-memory.js` fa fallire la build se un punto di chiamata dimentica la
misura di decodifica, se ne chiede piu' pixel di quanti lo schermo sappia mostrare, se
`ImageHelper` imposta `DecodePixelWidth` dopo la decodifica, se la copia dell'elenco
comincia a portarsi dietro i byte delle immagini, se la cache degli avatar perde i
suoi tetti o la coda di scrittura, o se la cache degli avatar non viene letta prima
delle righe salvate.
`WhatsappBridge/test/config.test.js` tiene l'altra meta' dello stesso budget
(`CHATS_LIMIT` ≤ 30, `MESSAGES_LIMIT` ≤ 60).

### Lingua dell'app

L'app segue automaticamente la lingua del dispositivo tramite risorse `.resw`:

| Lingua | File | Note |
| --- | --- | --- |
| Inglese | `WhatsappApp/Strings/en-US/Resources.resw` | `<DefaultLanguage>`: fallback per ogni altra lingua |
| Italiano | `WhatsappApp/Strings/it-IT/Resources.resw` | |

L'interfaccia dell'app e' l'**unica superficie localizzata**: tutto cio' che gli
script e il server stampano (il banner del launcher, il suo `--help`, il log
dell'adapter, il relay legacy) e' solo in inglese, cosi' leggere un log non
richiede una seconda lingua. Anche i commenti nel sorgente sono in inglese, quindi
l'italiano resta solo nei nomi dei test e nelle diagnostiche degli script di
guardia, che nessuno esegue a runtime.

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

## Server condiviso ed endpoint pubblico

L'adapter puo' ospitare piu' di un account. Ogni utente ha un device GOWA suo,
creato al primo handshake, e il token in `hello` decide quale e' il suo; con
`AUTH_REQUIRED=on`, un messaggio instradato dal suo `device_id` non arriva mai al
socket di un altro utente. Con l'interruttore spento non cambia niente e
l'istanza resta privata, senza token.

Un telefono che arriva senza token ne riceve uno alla prima connessione
(`AUTH_REGISTER=on`, il valore predefinito): l'adapter crea il device, risponde
con un frame `registered` e l'app conserva il token, quindi il servizio condiviso
non chiede altro che l'interruttore. Il token identifica il dispositivo, non e'
una password da digitare; con `AUTH_REGISTER=off` si torna a consegnare i token a
mano. Il token e' derivato dall'id di dispositivo che l'app presenta, e quell'id e'
il token hardware specifico del pacchetto, quindi reinstallare l'app non crea un
dispositivo nuovo: lo stesso telefono conserva lo stesso account, e l'accesso a
WhatsApp non viene richiesto di nuovo. Con `AUTH_STRICT_DEVICE=on` questa
comodita' si spegne: un dispositivo che il servizio conosce gia' deve presentare
il suo token, quindi conoscere un device id non basta per raggiungere un account.

La chiave del cifrario dei frame non deve essere scelta a mano. Con `PAIRING=on`
e nessuna chiave sua, un server stampa all'avvio un codice monouso; la pagina di
connessione dell'app prende quel codice e invia una chiave che il telefono ha
generato, sigillata con esso, e il server la adotta (`BRIDGE_KEY_FILE` la
conserva tra i riavvii). Il telefono puo' generare allo stesso modo anche il
proprio token, quindi la credenziale non esiste sul server: ne resta solo l'hash.
La pagina delle impostazioni ha ancora un campo *Chiave del server*: un server
avviato con un `BRIDGE_KEY` suo e `BRIDGE_REQUIRE_KEY=on` (che rifiuta il default
pubblico) si raggiunge digitando lo stesso valore li'.

Il servizio pubblico non e' un indirizzo compilato nell'app: `EndpointService`
legge `endpoint.json` da
[whatsappforwp-endpoint](https://github.com/vincenzosco/whatsappforwp-endpoint),
perche' il tunnel bore.pub che espone il server prende una porta nuova a ogni
riavvio. Quel file contiene un indirizzo e nient'altro. L'interruttore nella
pagina di connessione sceglie tra il servizio pubblico e un server proprio.

Quello che resta vero, e vale la pena dire chiaramente:

- Il trasporto e' cifrato dal cifrario dell'app (AES-256-CBC + HMAC-SHA256) con
  la passphrase che il telefono ha (quella compilata, una digitata nelle
  impostazioni, o una che il telefono ha generato e accoppiato). Sotto non c'e'
  TLS: uno `StreamSocket` di WP8.1 non sa fissare un certificato, quindi uno
  autofirmato non e' una strada.
- Chi gestisce un server condiviso puo' tecnicamente arrivare alle sessioni
  sulla macchina. Il token separa gli utenti tra loro, non dall'operatore.
- I token sono salvati solo come hash scrypt, e la sessione WhatsApp e'
  protetta cifrando il volume sull'host.

## Limiti

- Gli indicatori di scrittura dipendono dal fatto che l'account sia online, ed e' la stessa cosa che vedono i contatti: WhatsApp manda quegli eventi solo a un client marcato online, l'adapter mette l'account `available` mentre un client dell'app e' collegato e `unavailable` quando esce l'ultimo. Con l'app chiusa non si manda e non si riceve nessuna presenza, quindi in quel momento non si vede niente.
- Gli aggiornamenti non sono disponibili: il server GOWA con cui parla questa app non ha un endpoint per gli stati, quindi la sezione Stato resta vuota per scelta.
- Il registro chiamate elenca solo le chiamate in entrata, prese dalle chat più recenti che il server ha scansionato. I limiti esatti sono nella sezione Chiamate qui sotto.
- Eliminazioni e modifiche fatte dal telefono arrivano all'app solo mentre è collegata: non vengono riprodotte dopo un riavvio. Il confronto usa l'id del messaggio di WhatsApp, quindi i messaggi inviati dall'app non vengono riconosciuti.
- Le notifiche vengono alzate mentre l'app gira: WP8.1 la sospende in background, il che chiude il socket, e questo progetto non ha un servizio cloud da cui fare push. Un messaggio arrivato con l'app sospesa viene consegnato alla ripresa, quando l'app si ricollega da sola: non viene annunciato nel momento in cui arriva.
- Fissare, silenziare ed eliminare vivono solo su questo telefono, in `chat-preferences.json` nella cartella dell'app. Niente di tutto questo viene mandato a WhatsApp o all'adapter, quindi un altro dispositivo non lo vede, e si perde quando si cancellano i dati dell'app.
- Il numero dei non letti di una chat lo tiene l'adapter, in memoria, e si azzera quando la conversazione viene aperta nell'app. Riavviare l'adapter fa ripartire il conteggio da zero, e i messaggi arrivati mentre non gira ne' l'app ne' l'adapter non vengono contati.
- Un file condiviso da un'altra app viene letto nel momento in cui la condivisione viene consegnata, perche' l'app puo' essere terminata mentre il selettore o l'app che condivide sono aperti. I file molto grandi vengono comunque tenuti in memoria per essere spediti, quindi un video di lunghezza intera puo' non starci su un telefono da 512 MB.
- Aprendo una chat si vedono i messaggi recenti che il server ha gia'. I piu' vecchi non vengono richiesti al telefono. Una foto o un video di quella cronologia mostrano una parola (`[Image]`, `[Video]`) finche' non vengono toccati, e allora l'adapter li scarica dal server e l'app li riproduce o li disegna. I byte di un video ricevuto restano nella cartella locale dell'app finche' dura la sessione, e non vengono ripuliti alla chiusura. Gli ultimi 60 messaggi di una conversazione sono in una cache sul telefono, quindi la prima vista di una chat e' una fotografia che il server sostituisce.
- La tile live dice quanti messaggi aspettano, chi ha scritto per ultimo e la sua foto. La scrive l'app, quindi ha bisogno che l'app sia girata dopo che il conteggio e' cambiato: un messaggio arrivato mentre l'app e' sospesa viene contato quando riprende. Con il conteggio a zero la tile torna a quella del manifest.
- Sotto pressione di memoria l'app butta le bitmap degli avatar decodificate; i byte restano, e le immagini si ridisegnano quando l'elenco chat torna davanti. Finche' la pressione dura non si decodifica niente di nuovo, quindi su un telefono che resta sotto pressione l'elenco mostra le iniziali per un po'.

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
