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
| `dllCharacteristics` | 0x1140 | 0x0140 | 0x0140 |
| `IMAGE_DLLCHARACTERISTICS_APPCONTAINER` | si | **no** | **no** |
| DLL importate | KERNEL32 | api-ms-win-core-*, WinINet, bcrypt, WS2_32, MFPlat, ... | api-ms-win-core-*, RPCRT4 |
| import di console e argv | `WriteConsoleW`, `GetConsoleCP`, `GetConsoleMode`, `GetStdHandle`, `SetStdHandle`, `GetCommandLineA/W`, `GetEnvironmentStringsW`, `FindFirstFileExA`, `CreateFileW` | no | no |

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
2. **Il bit app container** (inferenza). Le due immagini accettate hanno
   `dllCharacteristics` 0x0140, cioe senza `IMAGE_DLLCHARACTERISTICS_APPCONTAINER`
   (`0x1000`). La regola del loader e che un'immagine con quel bit si carica solo
   in un processo AppContainer; quindi, se il telefono carica immagini senza il
   bit, il processo dell'app WP 8.1 non e un AppContainer, e il `/APPCONTAINER`
   aggiunto il 4 novembre avrebbe aggiunto un rifiuto invece di toglierlo.

Il prossimo tentativo e una ricostruzione con `/APPCONTAINER` via e con un
`/ENTRY` proprio, cosi che la partenza della CRT non entri nel link: gli import
dovrebbero ridursi a heap, thread e sincronizzazione, che il telefono ha. La
verifica si fa su questa macchina, con lo stesso lettore PE, prima di installare:
nessun import di console, `dllCharacteristics` 0x0140, e le sette funzioni
esportate ancora li.
