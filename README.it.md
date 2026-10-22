# WhatsApp per Windows Phone 8.1

[English](README.md) | **Italiano**

Un client WhatsApp per Windows Phone 8.1 (Universal Windows Platform) mantenuto
dalla community. Il telefono **e'** il client: parla il protocollo di WhatsApp in
prima persona - l'handshake Noise, il formato binario dei nodi, il doppio ratchet di
Signal, il caricamento e lo scaricamento dei media - quindi non c'e' nessun server da
avviare e niente da configurare.

## Progetti

### WhatsappApp (app Windows Phone 8.1)

L'app principale, con un'interfaccia utente autentica di WhatsApp.

**Funzioni:**
- tema WhatsApp in tutta l'app (accento su intestazione, tab, barra e fumetti)
- selettore del tema nelle impostazioni: gli sfondi del pacchetto ufficiale (piu' il doodle e il bianco) dipingono la conversazione e l'elenco della home
- elenco chat con avatar e contatori dei non letti
- tile live con quanti messaggi aspettano, la foto di chi ha scritto per ultimo e il suo nome
- una chat si puo' fissare alla schermata iniziale dal menu che si apre tenendo premuto: quella tile porta la foto, il nome e quanti messaggi di quella persona aspettano in ogni dimensione (71x71 con il numero sul badge), e un tocco apre la chat
- controllo in background ogni 15 minuti circa (si puo' spegnere): i messaggi che arrivano con l'app chiusa aggiornano tile, badge e schermo di blocco, e la tile ruota fra le conversazioni in attesa
- stato nello schermo di blocco: dettagliato (mittente e testo del messaggio) o rapido (il numero dei messaggi), scelto nelle impostazioni di Windows
- fumetti con orario e stato inviato/consegnato/letto
- messaggi di testo con Invio per inviare
- allegati immagine: si sceglie una foto dalla galleria e si invia allo spazio media di WhatsApp
- anteprima dell'immagine nel fumetto
- il primo avvio collega il telefono da solo: il codice QR lo disegna l'app (i suoi moduli sono calcolati sul telefono, nessuna immagine arriva da fuori) e inquadrarlo lega questo dispositivo all'account
- indicatore di scrittura: tre puntini animati nella conversazione mentre l'altra persona scrive, e lo stesso stato viene mandato mentre scrivi tu
- interfaccia nella lingua del dispositivo: inglese e italiano

**Architettura:**

L'app apre il WebSocket di WhatsApp (`wss://web.whatsapp.com/ws/chat`) su TLS e su
quello parla il protocollo. `WhatsappApp/Protocol/` e' il filo: l'handshake Noise con
il controllo della catena di certificati, il lettore e lo scrittore dei nodi binari,
le sessioni Signal (X3DH, il doppio ratchet, la chiave del mittente di un gruppo), le
chiavi dei media e i messaggi protobuf. `WhatsappApp/Protocol/Client/` e' la
sessione: il login, il classificatore dei nodi, la sincronizzazione della cronologia,
l'invio, le ricevute, il registro delle chiamate e il feed degli stati.
`WhatsappApp/Services/NativeConnection.cs` tiene il socket, i tentativi e gli eventi
che le pagine ascoltano.

#### Chiamate

La scheda Chiamate elenca il registro delle chiamate dell'account, come lo porta la
sincronizzazione della cronologia di WhatsApp (`HistorySync.callLogRecords`), in
entrambe le direzioni: una chiamata fatta da questo telefono e una ricevuta ci sono
tutte e due, e una mai risposta dice `missed`. Si aggiorna all'apertura della scheda.

#### Chat e nuove chat

L'elenco chat e' la lista vera delle conversazioni dell'account, come la manda la
sincronizzazione della cronologia dopo un login: ogni riga porta la conversazione,
l'ultimo messaggio, il numero dei non letti e il nome. Il nome di un gruppo arriva
dalla richiesta dei metadati a WhatsApp, perche' la riga sincronizzata non ne ha uno
utilizzabile. L'immagine del profilo di una chat viene chiesta a WhatsApp quando la
riga viene disegnata (la richiesta risponde un indirizzo e l'indirizzo risponde il
JPEG), quindi un gruppo ce l'ha come chiunque altro, e i byte restano sul telefono
perche' un riavvio mostri le facce prima che la connessione ci sia.

Una nuova chat si apre in tre modi: digitando un numero con prefisso, scegliendo
un contatto con il selettore del sistema (e' il consenso dell'utente, quindi l'app
non legge mai la rubrica per conto suo), oppure toccando una conversazione che il
server conosce gia'.

Mentre qualcuno scrive, nella conversazione compare un fumetto con tre puntini, uno
che sale dopo l'altro, e sparisce quando smette. E' l'unica cosa che questa app mostra
che non sia un messaggio, e arriva da WhatsApp (`chatstate` e il nodo `chat_presence`),
non da un'ipotesi. Nell'altra direzione funziona allo stesso modo: mentre scrivi tu,
questo telefono manda lo stesso nodo e il contatto vede gli stessi puntini. Le due
direzioni hanno bisogno che l'account sia presente, quindi il client si mette
`available` mentre l'app gira e `unavailable` quando il sistema la sospende.

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
di anteprima lo mostra e Invia lo carica sullo spazio media di WhatsApp come vocale.

La parte di questo che va detta e' nei **Limiti**: i vocali di WhatsApp sono in
Ogg/Opus e WP8.1 non ha ne' un encoder ne' un decoder Opus, quindi un vocale
registrato qui parte come M4A in un fumetto da vocale, e un vocale Ogg/Opus che
arriva non si riesce a riprodurre su questo telefono. Il server che convertiva nei
due sensi (con `ffmpeg`) se n'e' andato con tutto il resto.

## Struttura dell'app

La home e' una sola superficie con le tre sezioni sotto una striscia di tab
fissa e la barra dell'app WP8, piu' una pagina per conversazione:

| Pagina | Che cos'e' |
| --- | --- |
| `Pages/HomePage.xaml` | chat, stati e chiamate, sfogliati sotto `Controls/TabHeader.xaml` |
| `Controls/StatusPane.xaml` | il feed degli stati |
| `Controls/CallsPane.xaml` | il registro delle chiamate |
| `Pages/ChatPage.xaml` | conversazione |
| `Pages/SettingsPage.xaml` | tema (lo sfondo delle chat) e accesso all'account |
| `Pages/ConnectionPage.xaml` | il numero collegato e lo stato della connessione |
| `Pages/PairPage.xaml` | il codice QR che collega un dispositivo |

La home e' in cache: andare in una chat e tornare non la ricostruisce, e l'elenco
conserva la posizione di scorrimento. Il tema si sceglie nelle impostazioni tra
gli sfondi che il pacchetto ufficiale porta in `Images/Wallpapers` (piu' il
doodle e il bianco), e' salvato in `LocalSettings` e dipinge sia la conversazione
sia l'elenco della home.

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
     && node tools/check-history-sync.js \
     && node tools/check-client-state.js \
     && node tools/check-diagnostics.js \
     && node tools/check-handshake-answer.js \
     && node tools/check-xaml-names.js \
     && node tools/check-attachment-file.js \
     && node --test "tools/test/**/*.test.js"
   ```

4. Compila l'app su Windows
   (`msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`)
   e scrivi nella pull request che riporta `0 Error(s)`. Non esiste un host di test
   C#, quindi per l'app il gate e' la build.
5. Scrivi l'oggetto del commit come `tipo: imperativo breve` in inglese, e di'
   *perche'* serve la modifica, non solo cosa fa: la cronologia e' il changelog.
6. Testi visibili: aggiungi la chiave in **entrambi** i file `.resw`, e aggiorna
   sia `README.md` sia `README.it.md` nello stesso commit.

Una segnalazione utile contiene le righe `DIAG` della finestra Output, quale build
hai usato e cosa ha fatto il telefono. Le stesse righe sono nel `diag.log` dell'app,
che la pagina Diagnostica mostra e che `ISETool.exe ts` copia dal telefono.

## Protocollo

Il client parla il protocollo di WhatsApp, e niente qui dentro e' inventato: ogni
passo e' fissato contro le implementazioni di riferimento (`@whiskeysockets/baileys`
e `whatsmeow`), e i test che lo tengono onesto stanno in `ProtocolTests/`.

- **Trasporto**: un WebSocket verso `wss://web.whatsapp.com/ws/chat` su TLS, e su di
esso tutto e' un **nodo binario** - un tag, una mappa di attributi, una lista di
figli o dei byte (`Protocol/WABinary/`). `NodeReader` e `NodeWriter` sono le due
meta' di quel formato, e la lunghezza di un nodo viene controllata contro un tetto
prima di leggerlo.
- **Handshake**: l'handshake del protocollo Noise con la catena di certificati del
server, verificata sul telefono (`Protocol/Noise/NoiseHandshake.cs`). L'abbinamento
che segue e' un QR i cui moduli li calcola questa app (`Protocol/Qr/QrCode.cs`).
- **Messaggi**: il protocollo Signal - X3DH per una sessione nuova, il doppio ratchet
per ogni messaggio dopo, la chiave del mittente per un gruppo (`Protocol/Signal/`).
Un messaggio con dei media nomina le chiavi e l'indirizzo del file, e i byte vengono
caricati e scaricati dallo spazio media di WhatsApp, cifrati e decifrati sul telefono
(`Protocol/Media/`).
- **La sessione**: il classificatore dei nodi, la sincronizzazione della cronologia,
l'invio con le sue ricevute, la richiesta delle immagini del profilo, il registro
delle chiamate e il feed degli stati (`Protocol/Client/`). L'account e' lo store di
credenziali del protocollo, nella cartella dell'app
(`Protocol/Client/CredentialStore.cs`).

Tutto cio' che l'app stampa a runtime e' in inglese: le righe `DIAG` e i messaggi
delle eccezioni che ci finiscono. Anche i commenti nel sorgente sono in inglese: il
codice si spiega in una lingua sola. Restano fuori i nomi dei test e le diagnostiche
degli script di guardia in `tools/` - strumenti per sviluppatori che nessuno esegue -
e le stringhe dell'interfaccia in `Strings/it-IT`, che sono traduzioni, non diagnostica.

Una connessione e' di chi l'ha aperta: solo il tentativo piu' recente pubblica il suo
socket e il suo ciclo di lettura, quindi un socket fallito non puo' chiudere quello
che invece e' riuscito. Il client si riconnette da solo, con un'attesa limitata tra i
tentativi, e una sessione silenziosa da un po' viene chiusa e riaperta invece di
restare un socket che sembra vivo e non riceve niente.

## Compilazione

### App WP8

Toolchain verificato: **Visual Studio 2013 (v12.0) + Windows Phone 8.1 SDK**. Il
gate e'

```bash
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

che deve chiudere con `0 Error(s)` e produrre
`WhatsappApp\AppPackages\WhatsappApp_<versione>_Debug_Test\WhatsappApp_<versione>_x86_Debug.appxbundle`.

Ogni riga `DIAG` che l'app scrive viene anche spinta al debugger, quindi **Visual
Studio 2013** con la soluzione aperta, la configurazione su **Debug**, la
piattaforma su **ARM** e il telefono come destinazione le mostra in **Debug →
Windows → Output** quando l'app parte con **F5** (deploy e debug). Per un'app gia'
installata si usa **Debug → Attach to Process** scegliendola. Filtra la finestra su
`DIAG`. Senza un debugger collegato li' non appare niente; `diag.log` (esportalo
con `ISETool.exe ts`) e la pagina Diagnostics restano i canali sul telefono.

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

### Icone, tile e splash screen

Le tile, l'icona di avvio e lo splash screen sono la grafica **ufficiale** di
WhatsApp per WP8.1, non un ridisegno: `tools/brand/official/` tiene i file del
pacchetto rilasciato (`Icon1.png`, `Images/Tile*.png`, `Images/logo.png`, lo
sfondo delle chat `Images/Background.jpg` e lo splash 480×800), e
`tools/make-brand-assets.js` li copia in `WhatsappApp/Assets/` alle misure che il
manifest chiede al 240% (150→360, 71→170, 44→106, 310×150→744×360). Solo l'icona
della tile live (200 e 480 px), lo `StoreLogo` (120 px) e lo splash 1152×1920
sono ricavati, da `Icon1.png` e dallo splash del pacchetto; anche loro sono
committati, quindi lo script gira solo se cambia la grafica. E' Node puro, senza
ImageMagick: il PNG viene letto, ridimensionato con un kernel triangolare e
scritto con `zlib`.

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

La tile live ha asset suoi, ricavati dall'icona ufficiale verde dell'app dallo
stesso `make-brand-assets.js`. `Assets/TileIcon.png` piu' la sua versione al 240%,
`TileIcon.scale-240.png` (480×480), sono la tile quadrata; `TileIconWide.png`
piu' `TileIconWide.scale-240.png` (744×360) sono quella larga. Non e' un doppione
di `Logo.png`: il modello della tile disegna l'immagine cosi' com'e', a piena
superficie. Per questo il marchio sta **dentro** il suo asset con un margine -
78% del lato, 80% dell'altezza sulla larga - ed e' quel respiro a non farlo
sembrare zoommato. La tile larga ha un file suo per la stessa ragione:
un'immagine quadrata viene scalata per riempire 310×150 e il sistema ne taglia
sopra e sotto, cioe' taglia il marchio.

Il manifest imposta `BackgroundColor="#45D82C"` insieme a
`ForegroundText="light"`. Le tile quadrate e quella larga ufficiali sono la
cornetta bianca su fondo trasparente, quindi il verde traspare; `light` e'
l'altra meta' della coppia, perche' testo scuro su quel verde non si legge.

`tools/check-tile.js` la custodisce, perche' il guasto che intercetta e' invisibile.
Un modello di tile **non** prende l'icona dal manifest: la vuole nel payload, in un
`<image src="..."/>`, e con `src` vuoto la tile si disegna senza icona e non solleva
nessuna eccezione. La guardia controlla anche la misura di ogni asset contro il
modello che lo disegna: almeno 200×200 per l'icona quadrata, 310×150 con la
proporzione della tile per quella larga.

### Memoria su un telefono da 512 MB

Un telefono da 512 MB da' all'app un tetto di memoria rigido (piu' o meno 185 MB; un
dispositivo da 1 GB concede circa il doppio) e la sospende o la chiude se continua a
crescere. WP8.1 **non ha una dichiarazione di manifest per questo** -
`AppxManifestSchema2010_v2.xsd` e i suoi fratelli non hanno nessun elemento di
memoria - quindi il lavoro e' a runtime, in due meta':

- **decodifica alla misura che disegni**: `ImageHelper` riceve la larghezza a cui
  l'immagine viene mostrata e imposta `DecodePixelWidth` prima di `SetSourceAsync`.
  Un'immagine del profilo da 640×640 disegnata in un cerchio da 56 px costa qualche
  decina di KB invece di quasi 2 MB, moltiplicato per una conversazione;
- **molla la presa quando te lo chiede**: `MemoryWatcher` ascolta
  `MemoryManager.AppMemoryUsageIncreased` e, al livello `High`, butta le bitmap
  degli avatar decodificate e svuota la cronologia di ogni chat che non e' aperta
  (quella aperta non si tocca, e' quella che si sta leggendo). Finche' la pressione
  dura non si decodifica niente di nuovo. Il limite del telefono viene scritto una
  volta, come `DIAG ok: memory budget N MB`;
- **e si richiedono dal telefono**: i byte di un'immagine si tengono in
  `avatar-cache.json` (al massimo 40 conversazioni, circa 1 MB), cosi' un riavvio
  mostra le facce prima che la connessione ci sia, e una riga la cui bitmap decodificata
  e' stata buttata via si ridisegna quando l'elenco torna davanti. Le immagini sono
  la copia di quello che WhatsApp ha risposto: si chiede una volta, e la cache e' cio'
  che impedisce a un elenco letto due volte di chiedere di nuovo;
- **limita la conversazione**: una chat tenuta in memoria e' limitata a 200
  messaggi (`MaxMessagesPerChat` in `DataService`); i piu' vecchi escono e tornano
  dal server quando la chat viene riaperta. Era l'ultima allocazione che una
  conversazione occupata poteva far crescere senza un limite, ed e' quella che serve
  di piu' a un telefono da 512 MB;

`tools/check-memory.js` fa fallire la build se un punto di chiamata dimentica la
misura di decodifica, se ne chiede piu' pixel di quanti lo schermo sappia mostrare, se
`ImageHelper` imposta `DecodePixelWidth` dopo la decodifica, se la copia dell'elenco
comincia a portarsi dietro i byte delle immagini, se la cache degli avatar perde i
suoi tetti o la coda di scrittura, se la cache degli avatar non viene letta prima
delle righe salvate, o se la cronologia in memoria di una chat non ha un tetto
`MaxMessagesPerChat` o non lo applica con un `while`.

### Lingua dell'app

L'app segue automaticamente la lingua del dispositivo tramite risorse `.resw`:

| Lingua | File | Note |
| --- | --- | --- |
| Inglese | `WhatsappApp/Strings/en-US/Resources.resw` | `<DefaultLanguage>`: fallback per ogni altra lingua |
| Italiano | `WhatsappApp/Strings/it-IT/Resources.resw` | |

L'interfaccia dell'app e' l'**unica superficie localizzata**: tutto cio' che l'app e
gli script stampano (le righe `DIAG`, le diagnostiche delle guardie, gli strumenti in
`tools/`) e' solo in inglese, cosi' leggere un log non richiede una seconda lingua.
Anche i commenti nel sorgente sono in inglese, quindi l'italiano resta solo nei nomi
dei test e nelle diagnostiche degli script di guardia, che nessuno esegue a runtime.

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

Aggiungere, spostare o rinominare una sezione significa farlo in entrambi i file,
nello stesso commit, e lo stesso vale per la sezione `## Disclosure` in fondo a
ogni README. Un nuovo documento che spiega il progetto nasce gia' in coppia. Il
guard rifiuta una coppia con heading diversi, un documento senza la disclosure e
qualunque emoji diversa dal segno di pericolo:

```bash
node tools/check-docs.js
```

## Limiti

- Un vocale che arriva in Ogg/Opus non si riesce a riprodurre qui: i vocali di WhatsApp sono in Ogg/Opus e WP8.1 non ha un decoder Opus, e il server che li convertiva in MP3 (con `ffmpeg`) non c'e' piu'. Un vocale **registrato** da questo telefono parte invece come M4A/AAC, quindi chi lo riceve puo' vederlo come file audio invece che come vocale.
- Gli indicatori di scrittura dipendono dal fatto che l'account sia presente, ed e' la stessa cosa che vedono i contatti: WhatsApp manda quegli eventi solo a un client marcato online, l'app mette l'account `available` mentre gira e `unavailable` quando il sistema la sospende. Con l'app chiusa non si manda e non si riceve nessuna presenza, quindi in quel momento non si vede niente.
- I messaggi piu' vecchi sono solo quelli che WhatsApp ha spinto dopo il login: questo client non chiede su richiesta la cronologia passata di una conversazione, quindi una chat mostra cio' che l'ultima sincronizzazione ha portato e cio' che e' arrivato dopo.
- Eliminazioni e modifiche fatte da un altro dispositivo non vengono ancora lette: un messaggio revocato altrove resta a schermo finche' la conversazione non viene ripresa a un login nuovo.
- Un messaggio che arriva con l'app chiusa viene annunciato dal controllo in background, non in tempo reale: WP8.1 chiude la socket quando sospende l'app e questo progetto non ha un servizio push. `BackgroundSync` si sveglia ogni 15 minuti - il minimo che un `TimeTrigger` accetta su WP8.1: un numero piu' basso non registra niente - si connette, legge cio' che aspetta, aggiorna tile, badge e schermo di blocco e si chiude. La tile ruota fra le conversazioni in attesa, un turno per conversazione: non resta sull'ultimo che ha scritto. L'intervallo e' una richiesta - il momento esatto lo decide il sistema e puo' rimandarlo - e il controllo si puo' spegnere dalle impostazioni.
- Nello schermo di blocco Windows mostra cio' che l'utente sceglie nelle impostazioni: con l'app come **stato dettagliato** compare il tile largo (mittente e testo del messaggio), come **stato rapido** compare il badge con il numero dei messaggi. L'app tiene aggiornati entrambi; quale dei due si vede lo decide il sistema.
- Le notifiche vengono alzate mentre l'app gira: WP8.1 la sospende in background, il che chiude il socket, e questo progetto non ha un servizio cloud da cui fare push. Un messaggio arrivato con l'app sospesa viene consegnato alla ripresa, quando l'app si ricollega da sola: non viene annunciato nel momento in cui arriva.
- Fissare, silenziare ed eliminare vivono solo su questo telefono, in `chat-preferences.json` nella cartella dell'app. Niente di tutto questo viene mandato a WhatsApp, quindi un altro dispositivo non lo vede, e si perde quando si cancellano i dati dell'app.
- Il numero dei non letti di una chat arriva da WhatsApp con la sincronizzazione della cronologia e si azzera quando la conversazione viene aperta nell'app: il conteggio viene detto al server, e i messaggi arrivati mentre l'app era chiusa vengono contati alla sincronizzazione successiva.
- Un file condiviso da un'altra app viene letto nel momento in cui la condivisione viene consegnata, perche' l'app puo' essere terminata mentre il selettore o l'app che condivide sono aperti. I file molto grandi vengono comunque tenuti in memoria per essere spediti, quindi un video di lunghezza intera puo' non starci su un telefono da 512 MB.
- Una foto o un video di una vecchia conversazione mostrano una parola (`[Image]`, `[Video]`) finche' non vengono toccati, e allora l'app li scarica da WhatsApp e li riproduce o li disegna. I byte di un video ricevuto restano nella cartella locale dell'app e non vengono ripuliti alla chiusura. Gli ultimi 60 messaggi di una conversazione sono in una cache sul telefono, quindi la prima vista di una chat e' una fotografia che la connessione poi sostituisce.
- La tile live dice quanti messaggi aspettano, chi ha scritto per ultimo e la sua foto. La scrive l'app, quindi ha bisogno che l'app sia girata dopo che il conteggio e' cambiato: un messaggio arrivato mentre l'app e' sospesa viene contato quando riprende. Con il conteggio a zero la tile torna a quella del manifest.
- Sotto pressione di memoria l'app butta le bitmap degli avatar decodificate; i byte restano, e le immagini si ridisegnano quando l'elenco chat torna davanti. Finche' la pressione dura non si decodifica niente di nuovo, quindi su un telefono che resta sotto pressione l'elenco mostra le iniziali per un po'.

## Disclaimer

- Questo e' un progetto non ufficiale, non affiliato a WhatsApp o a Meta.
- Questo client usa metodi non ufficiali per collegarsi a WhatsApp, il che viola i
  Termini di servizio di WhatsApp.
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

**L'autore non si assume la responsabilita' dell'account WhatsApp con cui si
accede.** Collegare questo client significa connettere un client non ufficiale a
WhatsApp, contro i Termini di servizio di WhatsApp, e l'account puo' essere bannato
in modo permanente. Usare un numero di prova o secondario, e solo se si accetta
quel rischio per conto proprio.
