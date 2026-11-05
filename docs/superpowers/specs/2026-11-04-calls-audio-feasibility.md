# Se una chiamata puo portare la voce: il verdetto e cosa manca

Domanda: l'app puo fare una chiamata, farla rispondere e sentire la voce? La
risposta, verificata sul codice e sull'APK, e **no**: oggi la chiamata squilla e
viene risposta, la voce no. E non manca un file da scrivere - mancano tre strati
su quattro, e due di questi non sono ottenibili in questo ambiente.

Questo documento dice cosa c'e, cosa non c'e, e perche non e stato scritto il
resto. Le sezioni citano il codice con il file e la riga, e i numeri vengono da
un `grep` sul repository al 4 novembre 2026.

## Cosa funziona oggi

La **segnalazione** e completa e provata:

- `Protocol/Client/CallSignaling.cs` costruisce e legge il nodo `call`
  (`offer`, `preaccept`, `accept`, `reject`, `terminate`), e
  `ProtocolTests/CallSignalingTests.cs` la copre;
- `Services/CallSession.cs` e la macchina a stati: quindici stati di
  `com.whatsapp.calling.infra.voipcalling.CallState`, cinque `CallPhase` per le
  schermate, e i cinque eventi che chiudono una chiamata;
- `Services/DataService.cs` (`OnNativeCall`, `AcceptCallAsync`,
  `DeclineCallAsync`, `EndCallAsync`) e' il filo tra la rete e la schermata;
- `Pages/CallPage.xaml(.cs)` e' la schermata: risponde, rifiuta, chiude, e una
  chiamata che nessuno prende finisce da sola dopo 45 secondi.

La **parte deterministica del media** e scritta e provata senza un peer:

| file | righe | cosa e | prova |
|---|---|---|---|
| `Protocol/Media/Dtls.cs` | 714 | record, handshake, PRF di DTLS 1.2 | `DtlsTests.cs` (887 righe) |
| `Protocol/Media/DtlsClient.cs` | 730 | il ruolo client, con verifica della firma e del fingerprint | idem |
| `Protocol/Media/DtlsPsk.cs` | - | il premaster/master delle suite PSK (RFC 4279) | idem |
| `Protocol/Crypto/Srtp.cs` | 311 | SRTP AES-CM (RFC 3711/6188) | `SrtpTests.cs` |
| `Protocol/Media/RtpPacket.cs` | - | intestazione RTP, profilo Opus a 48 kHz | `CallMediaTests.cs` |
| `Protocol/Media/RtpJitterBuffer.cs` | - | ordine, duplicati, buchi | idem |
| `Protocol/Media/CallKeys.cs` | 185 | espansione di `enc_key` e split nelle due direzioni | `CallKeysTests.cs`, vettori da Node |
| `Protocol/Media/CallMedia.cs` | 199 | i quattro messi insieme: frame in SRTP, pacchetti in uscita dal jitter | `CallMediaTests.cs` (8 test) |

`node tools/test-protocol.js` passa 643 test, questi compresi.

## Cosa non c'e, e non e un dettaglio

### 1. Nessun motore e su un socket

Dal 5 novembre le classi sono istanziate: `CallMediaSocket` lega la
`DatagramSocket` che un candidato nomina e la legge, `CallTransport` la guida
(STUN, poi handshake, poi RTP), e `NativeConnection.StartCallMediaAsync` monta
il percorso su entrambi i lati: il risponditore quando manda il proprio
`accept`, con la call key aperta dall'`<enc>` dell'offerta
(`CallKeyExchange.UnwrapFrom`), e il chiamante in due tempi - una via senza
fingerprint quando scrive le offerte, che risponde ai controlli STUN e nomina
la coppia, e quella vera quando arriva l'`accept`, che prende il posto della
prima e fissa il certificato del peer. La via aperta presto non puo mandare
audio: `DtlsClient.IsTrusted` vuole il certificato che l'`accept` nomina. Un
datagramma che arriva alla porta prima che una via sia agganciata viene scritto
nel log con il suo indirizzo, invece di essere scartato in silenzio. Restano
fuori da questo percorso il relay (punto 2) e l'audio dal vivo (punto 4): i
pacchetti possono passare tra due candidati host, ma nessuno produce ancora i
frame da metterci dentro.

### 2. Il trasporto c'e a meta: ICE-lite e STUN ora ci sono, il relay no

Dal 4 novembre ci sono `Protocol/Media/Stun.cs` (il messaggio, i due hash, gli attributi) e
`Protocol/Media/IceLite.cs` (l'agente che risponde ai controlli di connettivita e nomina la
coppia), fissati ai vettori della RFC 5769. Non sono ancora su una socket e non c'e il relay.

```
grep -rni "turnclient|allocation" WhatsappApp WhatsappApp.Core -> nessun risultato
```

I candidati sono **solo host** (`CallCandidates.Host`, una voce per ogni IPv4 di
`NetworkInformation.GetHostNames`). L'APK dice che cosa usa il client ufficiale,
contato nelle sue 9.916 stringhe native: `ice` 660, `turn` 519, `relay` 651,
`stun` 107, `warp` 704, piu `relaylatency` tra le chiavi di segnalazione. Cioe:
il client ufficiale fa ICE con STUN e TURN, alloca relay e ripiega su WARP.

Perche questo basta a dire no: due telefoni sono quasi sempre dietro NAT, e la
tratta diretta tra due candidati host non si apre. Il relay e l'unica strada che
funziona sempre, e la sua allocazione passa da un dialogo con i server WhatsApp
(`relaylatency`) che nell'APK e solo un nome: la forma del nodo e i token non ci
sono, e inventarli e la cosa che questo repository non fa.

### 3. Manca la derivazione vera delle chiavi del media

`CallKeys.Expand` ricostruisce l'espansione dei 32 byte di `enc_key` nei 46 byte
del materiale media, e i suoi vettori vengono da un'implementazione Node: e una
prova di coerenza con se stessa, non con un peer. Nel motore ufficiale la stessa
cosa la fa `crypto/DefaultCryptoCallback.expandCallKey`, e il modo in cui i 46
byte si dividono tra le due direzioni lo decide il motore nativo.

Il lato di chi **risponde** c'e: `DtlsServer` chiude l'handshake che il
chiamante apre, e la call key sigillata nell'`<enc>` viene aperta con la
sessione del dispositivo che l'ha scritta (`CallKeyExchange.UnwrapFrom`, che
prova i dispositivi dell'account e tiene quello che apre). Quello che manca e
una prova contro un peer vero: la divisione dei 46 byte tra le due direzioni e
le etichette della derivazione ICE restano un'inferenza finche una chiamata
reale non le conferma.

### 4. Manca l'audio in tempo reale, e questo la piattaforma non lo da

Il WP8.1 che questa app usa non espone un percorso audio dal vivo:

- `MediaCapture` registra **su file** (`Services/AudioRecorder.cs` scrive un WAV,
  poi `VoiceNoteEncoder` lo codifica tutto insieme);
- la metadati della piattaforma non hanno nessun evento di frame audio:
  `AudioFrameAvailable` non compare in `Windows.winmd` ne in nessun altro file
  del Windows Phone 8.1 SDK; `AudioGraph` (la API di Windows 10 per il grafo
  audio) non c'e nemmeno;
- per la riproduzione l'app usa `MediaElement` su un file, che non e un percorso
  a 20 ms.

Un codec c'e: `waopus.dll` (encoder e decoder, `Services/VoiceNoteEncoder.cs` e
`VoiceNoteDecoder.cs`), con la nota in `tools/opus/waopus.c` sul caricamento
sulla telefono (`0x8007007E`) e `Services/AudioCodecCheck.cs` che la dice a ogni
avvio. Ma un codec senza frame da codificare non fa una chiamata.

## Perche il resto non e stato scritto

Ogni strato che manca richiederebbe di **indovinare** qualcosa che non e
osservabile qui:

- il relay e il suo dialogo: le stanze sono nell'APK (vedi la sezione sopra), ma
  il bind e il trasporto WARP no, e un client che non parla relay non chiama chi
  e dietro NAT simmetrico;
- il ruolo DTLS del risponditore e la divisione delle chiavi: sono dentro il
  motore nativo, e una prova contro un peer vero non e possibile da questa
  macchina (serve un telefono, e un'altra persona che risponda);
- l'audio dal vivo: non c'e una API, quindi non c'e codice da scrivere contro di
  essa.

Scrivere queste parti vorrebbe dire consegnare un motore che compila, non gira e
non puo essere provato - e la schermata della chiamata gia dice la verita:
`Call_NoAudio` ("No audio yet: this build only signals the call") compare
mentre la chiamata e su (`Pages/CallPage.xaml.cs`, riga 174).

## Il relay: cosa dice l'APK, e cosa serve una cattura

Ricerca del 5 novembre 2026 sui 13 DEX estratti (`.tools/apk/dex/classes*.dex`)
e sul dump delle stringhe native (`.tools/apk/calls/strings.txt`, circa 14.000
righe):

- le stanze del dialogo ci sono: `relaylatency` con il figlio `<te latency>`,
  `relay` come figlio dell'`offer`, e la controparte `relayelection`. Sono gia
  modellate in `CallSignaling`, che le ha prese dalla tabella dei figli
  dell'offer (il `relay` si registra dopo `voip_settings`);
- il lato nativo nomina il resto: `call_relay_servers`, `call_relay_server`,
  `call_relay_bind_status`, `call_relay_error_code`, `call_relays_received`,
  `relay_info_update`, `unbound_relay_list`, `relayElectionTimeInMsec`,
  `elected_relay_idx`, `server_recommended_relay_received_ms`,
  `num_relay_latencies_sent`, e 203 righe con `warp`, fra cui
  `WARP.RelayTransport` e `WARP.ACDCTransport`. Il relay e quindi un trasporto
  WARP con un bind per chiamata, e la riga `relayEnabled must be true when
  connectionPsm is RELAY` dice che viaggia su un PSM;
- il dump non contiene il vocabolario degli attributi TURN
  (`XOR-RELAYED-ADDRESS`, `CreatePermission`, `ChannelBind`), ma questo non prova
  niente: un client TURN usa costanti a 16 bit, non stringhe. La stessa ricerca
  sul `.so` non e possibile: nel repository e impacchettato e le stringhe sono
  compresse, e `grep` non trova nemmeno `relaylatency` che pure sappiamo esserci.

Verdetto, in due meta:

- **ricostruibile da una cattura**: il dialogo di segnalazione. Offerta con
  `<relay>`, misure `relaylatency`, elezione `relayelection` con il token. La
  cattura utile e quella della *nostra* sessione, non un MITM: il client siamo
  noi, quindi i nodi arrivano in chiaro al nostro parser. Se il server manda gli
  indirizzi dei relay, la cattura li mostra; la strada pratica e registrare i
  figli sconosciuti dell'offer e riprodurre una chiamata vera;
- **non ricostruibile da una cattura**: il piano media verso il relay. Le chiavi
  vengono dalla call key, quindi i datagrammi sono DTLS/SRTP e una cattura
  passiva da cifratura; il framing WARP e nel nativo, che qui si legge solo come
  stringhe.

Quindi TURN non e la domanda giusta: il relay di WhatsApp e WARP, e una cattura
serve per il dialogo (nodi, indirizzi e token), non per il trasporto.

## Cosa servirebbe, in ordine, se lo si volesse fare davvero

1. **ICE-lite e STUN** (RFC 5389/8445): rispondere ai binding request del peer e  usare l'indirizzo riflesso. Fatto: `Stun.cs` e `IceLite.cs`, con la RFC 5769
  come prova, e da novembre `CallMediaSocket` li mette su una socket UDP.
2. **Il relay**: le stanze sono nell'APK (sezione sopra), il trasporto no. Serve
   una cattura di una chiamata reale per la lista dei relay e i token, e poi il
   client WARP vero, che e il pezzo piu grande di tutti.
3. **Il ruolo DTLS server** e la derivazione delle chiavi verificata contro un
   peer.
4. **L'audio dal vivo**: su WP8.1 non esiste in managed; l'unica strada sarebbe
   un componente nativo con un sink Media Foundation
   (`StartRecordToCustomSinkAsync` esiste nei metadati) e una coda di
   riproduzione propria. E un progetto a se, dentro il repository o fuori.

Finche i punti 1-3 non ci sono, il media non puo passare; finche non c'e il 4,
non c'e voce da mandare anche se passasse. La strada onesta resta quella gia
scritta nella spec del 2 novembre: segnalazione completa (fatta), poi trasporto,
poi voce.

### 5. Perche il loader rifiuta `waopus.dll`

Il run del 5 novembre 2026 alle 14:30 scrive ancora `DllNotFoundException
0x80131524 Unable to load DLL 'waopus.dll' ... 0x8007007E` per un file che e nel
pacchetto, che e ARMNT e che importa un solo DLL. Confronto con le immagini
native che il telefono carica davvero - `WhatsAppNative.dll` e
`WhatsAppProxyStub.dll`, estratte dal pacchetto in `.tools/xap` - lette con lo
stesso parser PE (`header`, sezioni e tabella degli import):

| | `waopus.dll` | `WhatsAppNative.dll` | `WhatsAppProxyStub.dll` |
|---|---|---|---|
| macchina | 0x01c4 | 0x01c4 | 0x01c4 |
| subsystem / versione | 2 / 2.6 | 3 / 2.6 | 3 / 2.6 |
| `dllCharacteristics` | 0x0140 | 0x0140 | 0x0140 |
| `IMAGE_DLLCHARACTERISTICS_APPCONTAINER` | no | **no** | **no** |
| DLL importate | `api-ms-win-core-heap-l1-2-0`, `api-ms-win-core-errorhandling-l1-1-1` | api-ms-win-core-*, WinINet, bcrypt, WS2_32, MFPlat, ... | api-ms-win-core-*, RPCRT4 |
| import di console e argv | no | no | no |

Due differenze, e nessuna delle due e il bit che il 4 novembre si era scelto
come causa:

1. **La partenza della CRT** (osservato). Il link usa l'entry di default di
   `libcmt`, che porta dentro `_ioinit` e `__getmainargs`: da li arrivano
   `WriteConsoleW`, `GetConsoleCP`, `GetStdHandle`, `SetStdHandle`,
   `GetCommandLineA`, `GetEnvironmentStringsW`, `FindFirstFileExA`. Sul kernel
   WP quelle funzioni non ci sono - non c'e console e un'app non apre percorsi
   arbitrari - e un import che non si risolve e esattamente `0x8007007E`: il
   loader dice "modulo non trovato" anche quando il modulo c'e. Nessuna delle
   due immagini che il telefono carica importa una sola di quelle funzioni.
2. **Il bit app container** (smentito dalla ricostruzione). Le tre immagini
   accettate hanno `dllCharacteristics` 0x0140, cioe senza
   `IMAGE_DLLCHARACTERISTICS_APPCONTAINER` (`0x1000`). Il bit non decide il
   caricamento: il 5 novembre 2026 la DLL e stata ricostruita senza
   `/APPCONTAINER` e con un `/ENTRY` proprio, l'immagine e passata a
   `dllCharacteristics` 0x0140 - gli stessi flag dei moduli che si caricano - e
   la tabella degli import e rimasta identica, `KERNEL32.dll` compreso. Il bit e
   stato tolto perche non serve, non perche risolva.

La ricostruzione e stata fatta, con `/APPCONTAINER` via e con `/ENTRY:WaDllMain`
(`tools/opus/waentry.c`), e lo stesso lettore PE dice cosa ha cambiato: entry a
`0x2a101` in `.text`, `dllCharacteristics` 0x0140, sette export intatti, e la
tabella degli import **uguale** - `KERNEL32.dll` con le nove funzioni di console
e argv. Il `/ENTRY` non poteva bastare: l'entry di questa DLL richiama
`_DllMainCRTStartup`, che e cio che inizializza l'heap di cui il `malloc` di
libopus ha bisogno. Un entry che *non* richiama la partenza della CRT toglie le
nove funzioni e lascia `malloc` senza heap: la DLL si carica e risponde "no
encoder" a ogni vocale, che e peggio di un file che non si carica.

La superficie la decide il CRT che si linka, e su questa macchina non esiste un
CRT che nomini gli API set (in `Microsoft SDKs\Windows Phone\v8.1\Lib` non c e una
cartella ARM, e i Kits 10 per ARM hanno il solo
`api-ms-win-core-slapi-l1-1-0.lib`). La strada percorsa e quindi togliere il CRT
dal mezzo invece di ri-linkarlo:

- **libopus in virgola fissa** (`/DFIXED_POINT`, SILK da `silk/fixed`): e la
  configurazione che le build mobili di Opus usano, e porta via la libm;
- **allocatore proprio** (`tools/opus/waentry.c`): `HeapAlloc` sull'heap del
  processo, quindi nessuna inizializzazione CRT da chiamare;
- **`memcpy`, `memset`, `memmove`, `memcmp`, `strlen` e la piccola libm** che
  `analysis.c` e la KISS FFT usano ancora, scritti li accanto;
- **import library proprie** (`tools/opus/waheap.def`, `waerr.def`), costruite
  con `lib.exe`: `LIBRARY` nel `.def` e la stringa che il linker scrive nel
  descrittore di import, quindi e quella a scegliere il modulo;
- **`/NODEFAULTLIB:kernel32.lib`** nel link, cosi una funzione di kernel che
  ricomparisse e un errore di build invece di un rifiuto sul telefono.

L'immagine che ne esce e ARMNT, `dllCharacteristics` 0x0140, sette export, e
importa due moduli soli: `api-ms-win-core-heap-l1-2-0.dll` (`GetProcessHeap`,
`HeapAlloc`, `HeapFree`) e `api-ms-win-core-errorhandling-l1-1-1.dll`
(`RaiseException`). Ogni coppia modulo/funzione e una che `WhatsAppNative.dll`
importa dallo stesso modulo sullo stesso telefono: e il test
`ogni funzione importata e una che WhatsAppNative importa dallo stesso modulo` in
`tools/test/check-waopus.test.js`, e `node tools/check-waopus.js` ora e verde.

Cio che resta non e piu nel file: e il caricamento sul dispositivo, con la voce
che passa da M4A a Ogg/Opus nella prima riga di log di `AudioCodecCheck`.

### 6. Cosa e cambiato il 5 novembre, dopo il caricamento

**Le credenziali ICE erano al contrario, in due punti (difetto vero, corretto).**
`IceLiteAgent` confrontava lo username come `remoteUfrag:localUfrag` e verificava
`MESSAGE-INTEGRITY` con la password del *peer*. RFC 8445 (sezioni 7.2.2 e short-term
credentials) vuole l'opposto su entrambi: il mittente scrive l'ufrag del **ricevente**
per primo, e firma con la password del **ricevente**, che il ricevente verifica con la
propria. Con la convenzione sbagliata i due ruoli di questo repository andavano
d'accordo fra loro e nessuno dei due avrebbe accettato un peer corretto: il client
ufficiale che chiama avrebbe ricevuto `401` su una coppia offerta benissimo, e la
chiamata non avrebbe mai nominato niente. I test portavano la stessa inversione, per
questo erano verdi. Ora `IceLiteAgent.ExpectedUserName` e' `local:remote`,
`IceControllingAgent.UserName` e' `remote:local` (la stessa stringa), l'integrita' si
verifica con la password locale, e `IceLiteTests`, `CallTransportTests` e
`CallEndToEndTests` costruiscono i check come li costruisce un peer.

**La scoperta riflessa (srflx) c'era solo a meta': rispondere, mai chiedere.**
`Protocol/Media/StunDiscovery.cs` scrive la Binding request e legge la risposta
(`XOR-MAPPED-ADDRESS`, con il transaction id che distingue la risposta a *questa*
domanda da quella a una precedente) e `CallCandidates.ServerReflexive` la trasforma in
un candidato con `type` 1. Il numero del campo e' **inferito** (l'ordine consueto host,
srflx, prflx, relayed); il resto della struttura del messaggio e' quello gia' scritto.
Non e' attaccata al percorso vivo di proposito: l'indirizzo va chiesto ai server di
WhatsApp, che si conoscono solo da una cattura, e un servizio STUN pubblico
annuncerebbe la chiamata a un terzo.

**ICE completo da un lato.** `Protocol/Media/IceControlling.cs` e' l'agente che apre il
percorso: check con `PRIORITY`, `ICE-CONTROLLING` e `USE-CANDIDATE` (nomina
aggressiva), ritrasmissione a 0,5 s che raddoppia, `487` che scarta il candidato e
passa al successivo, e un fallimento che dice perche'. Non legge un orologio: `Tick`
prende il tempo del chiamante, quindi `IceControllingTests` attraversa un'intera
traversal contro `IceLiteAgent` in memoria e finisce con i due lati sulla stessa coppia.

**Cosa resta fermo, con la prova.**

- **L'audio dal vivo (voce 2):** la piattaforma non lo da'. Il profilo WP8.1 di
  `Microsoft.Xna.Framework.dll` non contiene il tipo `Microphone` (zero occorrenze
  nella reference assembly), quindi non esiste una callback PCM managed;
  `MediaCapture` registra su file, e `StartRecordToCustomSinkAsync` vuole un componente
  nativo e il toolset C++ di Windows Phone 8.1, che su questa macchina non c'e'.
- **La segnalazione (voce 3):** c'era gia' - l'offerta si ripete finche' non torna un
  nodo della chiamata, l'`ack` dell'offerta si legge come risposta del server, e il
  trasporto si attacca per chiamata. Il difetto corretto sopra era l'unico punto in cui
  il trasporto non avrebbe funzionato con un peer vero.
- **Relay WARP, video, gruppi, push (voci 5-8):** il relay vuole una cattura per nodi e
  token e poi il client WARP vero; il video vuole una pipeline H.264 realtime che
  WP8.1 non offre in managed; i gruppi vogliono il fanout della chiamata di gruppo; il
  push vuole un server. Nessuna di queste si ricava da questo repository.
