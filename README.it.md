[English](README.md) | **Italiano**

<p align="center">
  <img src="WhatsappApp/Assets/WhatsAppLogo.png" width="128" alt="Icona dell'app WhatsApp per Windows Phone 8.1">
</p>

<h1 align="center">WhatsApp per Windows Phone 8.1</h1>

<p align="center">
  <strong>Un client WhatsApp che gira su un telefono del 2014, scritto partendo dal protocollo.</strong><br>
  Il telefono parla direttamente con WhatsApp: handshake Noise, nodi binari, ratchet di Signal, media.
  Non c'è nessun server da avviare e niente da configurare.
</p>

<p align="center">
  <a href="#compilarlo">Compilarlo</a> ·
  <a href="#usarlo">Usarlo</a> ·
  <a href="#come-funziona">Come funziona</a> ·
  <a href="#problemi-e-soluzioni">Problemi e soluzioni</a>
</p>

<p align="center">
  <a href="#compilarlo"><img src="https://img.shields.io/badge/platform-Windows%20Phone%208.1-2ea44f" alt="Windows Phone 8.1"></a>
  <a href="README.md"><img src="https://img.shields.io/badge/languages-English%20%7C%20Italiano-blue" alt="Inglese e italiano"></a>
</p>

Windows Phone 8.1 ha perso il suo client WhatsApp ufficiale anni fa. Questo è open source, vive in
una sola app sul telefono, e parla il protocollo WebSocket di WhatsApp invece di appoggiarsi a un
bridge sul PC. Gira su un WP8.1 vero o sull'emulatore x86. È gratis e non chiede nessun account
oltre al numero WhatsApp che colleghi.

Non c'è un installer e non ci sono release: si compila dal sorgente con Visual Studio 2013.

## Com'è fatto

Una app, un processo, nessun backend. `Protocol/` è il filo (handshake, nodi, sessioni Signal,
chiavi dei media), `Protocol/Client/` è la sessione (login, sync della history, invio, ricevute,
registro chiamate, feed degli stati), e `Services/NativeConnection.cs` possiede il socket e gli
eventi che le pagine ascoltano.

Le tre schede sono quelle dell'app ufficiale: chat, stati, chiamate, sotto una striscia di tab
fissa con la barra di WP8. Una conversazione è una pagina a parte.

| Pagina | Cos'è |
| --- | --- |
| `Pages/HomePage.xaml` | chat, stati e chiamate, con lo swipe sotto `Controls/TabHeader.xaml` |
| `Controls/StatusPane.xaml` | il feed degli stati |
| `Controls/CallsPane.xaml` | il registro delle chiamate |
| `Pages/ChatPage.xaml` | la conversazione |
| `Pages/SettingsPage.xaml` | il tema (lo sfondo delle chat) e la strada per l'account |
| `Pages/ConnectionPage.xaml` | il numero collegato e lo stato della connessione |
| `Pages/PairPage.xaml` | il QR che collega il telefono |

Cosa funziona oggi:

- messaggi di testo, immagini e vocali, con le spunte inviato/consegnato/letto su ogni fumetto
- l'elenco chat vero, dalla sync della history: avatar, non letti, gruppi, nomi salvati nella rubrica del telefono
- indicatore "sta scrivendo" nelle due direzioni
- il feed degli stati: quello che i contatti hanno pubblicato, il tocco apre la foto, il video o le parole, e la ricevuta di visione torna a `status@broadcast`. Il feed dell'ultimo giorno resta sul telefono, quindi c'è ancora dopo un riavvio, e uno stato la cui foto non è arrivata ha un pulsante **Riprova** invece di un vicolo cieco
- il registro delle chiamate, nelle due direzioni, con le perse scritte per esteso
- una tile viva che ruota fra le persone in attesa - nome e messaggio, e fra un messaggio e l'altro la sola foto di chi aspetta una risposta - più la tile di una chat fissata, il testo sulla schermata di blocco, e un controllo in background ogni 15 minuti circa
- selettore del tema con gli sfondi del pacchetto ufficiale, più il doodle e il bianco
- inglese e italiano, seguendo la lingua del telefono

## Va bene per, non va bene per

**Va bene per:** tenere un WP8.1 usabile come secondo dispositivo WhatsApp; leggere il protocollo in
C# 5; un telefono che non è il numero principale.

**Non va bene per:** sostituire WhatsApp sul telefono principale. È un client non ufficiale,
viola i Termini di servizio di WhatsApp, e il numero che colleghi può essere bannato. Usa un numero
di prova o secondario. Non è nemmeno un client completo: i vocali in Ogg/Opus non si sentono, la
history vecchia non viene chiesta su richiesta, le cancellazioni e le modifiche fatte da altri
dispositivi non vengono lette.

## Compilarlo

Serve **Visual Studio 2013 (v12.0) con l'SDK di Windows Phone 8.1**. Le versioni più nuove di VS non
hanno quel toolchain. Compila da un percorso su disco locale, non da una cartella condivisa o da una
unità di rete.

**Da riga di comando** (dalla radice del repository):

```bash
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

La build deve chiudere con `0 Error(s)` e lasciare
`WhatsappApp\AppPackages\WhatsApp_<version>_Debug_Test\WhatsApp_<version>_x86_Debug.appxbundle`.
Per il telefono compila `Release` su `ARM` e usa **Build > Deploy**.

**Da Visual Studio**: apri `WhatsappApp.sln`, scegli `Debug` / `x86` (emulatore) oppure `Release` /
`ARM` (telefono), poi F5.

<details>
<summary><strong>Cosa serve alla build e cosa rifiuta</strong> Il toolchain è vecchio di proposito.</summary>
<br>

- Il toolchain WP8.1 compila con il compilatore **C# 5**. Stringhe interpolate, `?.`, proprietà
  con corpo a espressione, inizializzatori di proprietà automatici, pattern matching e `out var`
  qui non compilano. `node tools/check-csharp5.js` elenca file, riga e costrutto da correggere, e
  intercetta anche i membri WinRT che esistono su Windows 10 ma non nella proiezione di WP8.1.
- Se compili dentro `C:\Mac\Home\...` (la cartella condivisa di Parallels) il secondo passaggio
  XAML fallisce sempre con `Xaml Internal Error error WMC9999: The given key was not present in
  the dictionary`. Copia l'albero su un disco locale (`robocopy <share> C:\wp81 /E`) e compila.
- L'emulatore WP8.1 non parte su un Mac con Apple Silicon: le sue immagini sono x86 e vogliono
  Hyper-V. Serve una macchina Windows x86/x64 oppure un telefono via USB.

</details>

## Usarlo

1. Apri l'app. La prima schermata è il QR, disegnato dall'app stessa (nessuna immagine arriva da
   fuori).
2. Sul telefono principale: WhatsApp > dispositivi collegati > collega un dispositivo, e inquadra.
3. L'app fa il login, legge la sync della history e riempie l'elenco chat.
4. Tocca una chat per scrivere. Invio manda. La graffetta allega una foto, il microfono registra un
   vocale.
5. Scorri fino alla scheda stati per vedere cosa hanno pubblicato i contatti. Tocca uno stato per
   aprirlo: la foto o il video, oppure le parole, con la didascalia sotto.
6. La scheda chiamate elenca il registro che WhatsApp ha mandato con la sync.

Consigli:

- Una chat si fissa alla schermata Start dal menu della pressione prolungata. La tile porta la
  foto della persona con il nome nella riga grande e il suo ultimo messaggio sotto, e il numero
  dei messaggi in attesa sul badge. Anche la tile dell'app si legge così, e fra un messaggio e
  l'altro mostra la sola foto di chi aspetta una risposta.
- Il controllo in background è attivo di default. Spegnilo nelle impostazioni se il telefono deve
  stare zitto.
- Se l'app apre un elenco vuoto e resta lì, apri la pagina Diagnostics dallo stato vuoto: mostra le
  ultime righe `DIAG`.

## Come funziona

Ogni passo è ancorato alle implementazioni di riferimento (`@whiskeysockets/baileys` e
`whatsmeow`), e i test che lo tengono onesto stanno in `ProtocolTests/`.

- **Trasporto**: un WebSocket verso `wss://web.whatsapp.com/ws/chat` su TLS. Tutto quello che ci
  passa è un nodo binario: un tag, una mappa di attributi, figli o byte (`Protocol/WABinary/`). La
  lunghezza di un nodo è controllata contro un tetto prima di leggerlo.
- **Handshake**: l'handshake Noise con la catena di certificati del server, verificata sul telefono
  (`Protocol/Noise/NoiseHandshake.cs`).
- **Accoppiamento**: il QR è codificato dall'app (`Protocol/Qr/QrCode.cs`), e il collegamento salva
  l'account nel credential store del protocollo, nella cartella dell'app.
- **Messaggi**: X3DH per una sessione nuova, il doppio ratchet per tutto quello che viene dopo, la
  sender key per un gruppo (`Protocol/Signal/`). Un messaggio con media nomina le chiavi e
  l'indirizzo del file; i byte vanno e vengono dall'host media di WhatsApp e sono cifrati e
  decifrati sul telefono (`Protocol/Media/`).
- **Sessione**: il login, il classificatore dei nodi, la sync della history, l'invio con le sue
  ricevute, la query della foto profilo, il registro chiamate e il feed degli stati
  (`Protocol/Client/`).
- **Una connessione, un padrone**: solo il tentativo più recente pubblica il suo socket e il suo
  ciclo di lettura, così un socket che fallisce non chiude quello che ha funzionato. Il client si
  riconnette da solo, e una sessione silenziosa viene chiusa e riaperta invece di restare un socket
  che sembra vivo.

Uno stato non è una chat: vive in `status@broadcast`, il suo autore è il `participant`, e guardarlo
è una ricevuta `read` a quella lista che porta l'id dello stato e l'autore. Il feed li tiene fuori
dall'elenco chat, ed è per questo che un telefono con una giornata di stati non mostra in cima una
riga che si chiama "status".

Le icone dell'interfaccia sono `Path` vettoriali con la geometria scritta dentro ogni `Path`: WP8.1
non ha `Segoe MDL2 Assets`, quindi un font di glifi si disegna vuoto. `node tools/check-icons.js`
impone questo, la forma a elementi della geometria, e che la stessa icona sia la stessa geometria
ovunque.

## Problemi e soluzioni

<details>
<summary><strong>La build fallisce con WMC9999 "The given key was not present in the dictionary"</strong></summary>
<br>

Il progetto è sulla cartella condivisa del Mac. Il secondo passaggio del compilatore XAML lì
fallisce sempre. Copia l'albero su un disco locale e ricompila; nel codice non va cambiato niente.
</details>

<details>
<summary><strong>L'app apre un elenco chat vuoto</strong></summary>
<br>

O il telefono non è ancora collegato (la prima schermata è il QR), oppure la connessione è caduta
prima della sync. Apri la pagina Diagnostics dallo stato vuoto: le righe `DIAG` nominano il passo
raggiunto dalla connessione. Le stesse righe sono in `diag.log` nella cartella dell'app, che
`ISETool.exe ts` copia dal telefono.
</details>

<details>
<summary><strong>I messaggi arrivano solo ogni 15 minuti con l'app chiusa</strong></summary>
<br>

È il controllo in background, non un difetto. WP8.1 chiude il socket quando sospende l'app e non
c'è nessun servizio cloud per il push, quindi `BackgroundSync` si sveglia con un `TimeTrigger`, si
connette, legge quello che aspetta, aggiorna tile e schermata di blocco, e chiude. 15 minuti è
l'intervallo più breve che un `TimeTrigger` accetta; il momento esatto lo decide il sistema e può
rimandarlo. Spegnilo nelle impostazioni se preferisci che in background non faccia niente.
</details>

<details>
<summary><strong>Un vocale di WhatsApp non si sente</strong></summary>
<br>

I vocali di WhatsApp sono Ogg/Opus, e WP8.1 non ha un decoder Opus. Un vocale registrato qui esce
in M4A/AAC, che questo telefono registra e riproduce. Il server che convertiva nei due sensi è
sparito con il resto del backend.
</details>

<details>
<summary><strong>L'elenco chat mostra numeri invece dei nomi</strong></summary>
<br>

I nomi arrivano dai push name della sync, dalla query dei metadati dei gruppi e dalla rubrica del
telefono, che l'app legge solo dopo che dai il permesso. Un contatto di cui nessuno ha condiviso il
nome e che non è in rubrica resta un numero.
</details>

<details>
<summary><strong>La foto in una chat o in uno stato mostra una parola finché non la tocchi</strong></summary>
<br>

Le righe della history portano le chiavi e l'indirizzo del file, non il file. L'app lo scarica la
prima volta che lo tocchi, poi lo tiene. Una foto di uno stato che non compare nemmeno dopo lo
scaricamento di solito è scaduta: gli stati vivono un giorno.
</details>

## Comandi

Da eseguire dalla radice del repository prima di una pull request. Le guardie sono il contratto:
ognuna intercetta un guasto che la build non può vedere.

```
node tools/check-csharp5.js         sintassi C# 6/7 e membri WinRT che WP8.1 non ha
node tools/check-icons.js           icone vettoriali inline, forma a elementi, stessa icona stessa geometria
node tools/check-resw.js --strict   x:Uid, Loc.Get, entrambi i .resw, nessuna chiave inutilizzata
node tools/check-docs.js            coppia di README, stesse intestazioni, disclosure in fondo, niente emoji
node tools/check-memory.js          larghezza di decodifica, tetti delle cache, tetto per chat
node tools/check-tile.js            payload della tile e misure degli asset
node tools/check-actions.js         i pulsanti della barra
node tools/check-fire-and-forget.js nessun task il cui guasto nessuno osserva
node tools/check-project-files.js   ogni file che i progetti compilano
node tools/check-xaml-names.js      un x:Name per namescope
node tools/check-client-state.js    le frasi passive/attive del login e il canale notifiche
node tools/check-background-task.js la classe attivabile del task e il suo budget di 30 s
node tools/check-diagnostics.js     il log che sopravvive a un crash
node tools/check-chat-list-source.js, check-history-sync.js, check-send-status.js,
     check-attachment-file.js, check-chat-scroll.js, check-no-server.js, check-status-viewer.js
```

L'intero cancello, in una riga:

```bash
for f in tools/check-*.js; do node "$f" || exit 1; done
node --test tools/test/*.test.js
node tools/test-protocol.js
```

`tools/test-protocol.js` compila ed esegue i 309 test del protocollo in `ProtocolTests/`. Sono C#
puro con un runner minimo, perché il toolchain WP8.1 non può ospitare un framework di test moderno.

## Da sapere

- I messaggi vecchi sono solo quelli che WhatsApp ha spinto dopo il login: questo client non chiede
  la history precedente di una conversazione su richiesta.
- Le cancellazioni e le modifiche fatte da un altro dispositivo non vengono ancora lette. Un
  messaggio revocato altrove resta a schermo fino a un nuovo login che risincronizza la
  conversazione.
- Fissare, silenziare e cancellare vivono solo su questo telefono, in `chat-preferences.json`.
  Niente di tutto questo va a WhatsApp, quindi un altro dispositivo non lo vede e cancellare i dati
  dell'app li perde.
- Il numero dei non letti di una chat arriva con la sync e si azzera quando apri la conversazione,
  che lo dice al server. I messaggi arrivati con l'app chiusa li conta la sync successiva.
- Un video ricevuto viene scritto nella cartella locale dell'app e non viene ripulito alla chiusura.
- Gli ultimi 60 messaggi di ogni conversazione vengono scritti sul telefono a fine sync, così una
  chat aperta dopo un riavvio li mostra subito.
- Su un telefono da 512 MB l'app ha circa 185 MB. Decodifica ogni immagine alla misura in cui la
  disegna, lascia andare gli avatar decodificati sotto pressione, e tiene al massimo 200 messaggi
  per chat in memoria. `node tools/check-memory.js` sorveglia tutte e tre.
- L'indicatore "sta scrivendo" ha bisogno che l'account sia presente, che è quello che i contatti
  vedono come online. Con l'app chiusa non si manda e non si riceve niente.
- La tile viva ha bisogno che l'app sia stata eseguita da quando il conteggio è cambiato; un
  messaggio arrivato con l'app sospesa lo conta il controllo in background.
- Un file condiviso dentro l'app da un'altra app viene letto nel momento in cui la condivisione
  viene consegnata, perché l'app può essere terminata mentre il selettore è aperto. Un video molto
  grande può non stare in memoria su un telefono da 512 MB.

## Contribuire

Issue e pull request sono benvenute. Leggi `.agents/skills/maintain-the-app/SKILL.md` prima di
toccare il codice: solo C# 5, icone vettoriali inline, nessuna stringa visibile scritta a mano, e
documenti sempre in coppia. L'indice delle skill del progetto è in `.agents/skills/README.md`.

1. Fork e ramo: `fix/...`, `feat/...`, `docs/...`.
2. Fai la modifica, poi esegui il cancello qui sopra. Deve essere verde.
3. Compila l'app su Windows e dillo nella pull request che riporta `0 Error(s)`. Non c'è un host di
   test C# per l'app, quindi la build è il suo cancello.
4. Scrivi l'oggetto del commit come `type: imperativo breve` in inglese e di' perché la modifica
   serve, non solo cosa fa. La history è il changelog.
5. Il testo visibile va in **entrambi** i `.resw`, e `README.md` e `README.it.md` cambiano nello
   stesso commit.

Un bug report utile ha le righe `DIAG` della finestra Output, la build che hai eseguito, e cosa ha
fatto il telefono.

I documenti che spiegano il progetto esistono in due lingue e vengono tenuti allineati. Aggiungere,
spostare o rinominare una sezione significa farlo in entrambi i README nello stesso commit, e la
sezione `## Disclosure` resta in fondo a entrambi. `node tools/check-docs.js` rifiuta una coppia con
intestazioni diverse, un documento senza la disclosure, e qualsiasi emoji tranne il segno di
pericolo.

## Disclaimer

- È un progetto non ufficiale, non affiliato a WhatsApp o Meta.
- Usa metodi non ufficiali per connettersi a WhatsApp, il che viola i Termini di servizio.
- Collegarlo può far bannare definitivamente il tuo numero di telefono.
- Usa solo un numero di prova o secondario.

## Licenza

MIT, mantenuto dalla community. Usalo a tuo rischio.

## Disclosure

**Questo progetto è open source e ha bisogno di manutentori.** Issue, traduzioni, revisioni,
documentazione e pull request sono tutte benvenute, e così chiunque voglia aiutarlo a crescere.

**L'autore non si assume la responsabilità per l'account WhatsApp usato per accedere.** Collegare
questo client significa connettere un client non ufficiale a WhatsApp, il che è contro i Termini di
servizio, e l'account può essere bannato definitivamente. Usa un numero di prova o secondario, e
solo se accetti tu quel rischio.
