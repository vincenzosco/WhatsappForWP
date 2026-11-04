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

Le classi qui sopra non vengono **mai** istanziate dal codice dell'app:

```
grep -rn "new CallMediaSession|new DtlsClient|new SrtpSession|new IceLiteAgent" WhatsappApp
    -> nessun risultato fuori da Protocol/Media/CallMedia.cs, che e il
       costruttore di se stessa
```

`NativeConnection.CallMediaPortAsync` (riga 3800) lega una `DatagramSocket` su
una porta e la tiene, e `CallCandidatesAsync` (riga 3830) offre quell'indirizzo
nell'offerta e nell'accept. Ma nessuno legge o scrive su quella socket: la porta
esiste perche un candidato deve nominarla, non perche ci passi l'audio.

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

C'e anche un pezzo mancante per il lato di chi **risponde**: il repository ha
solo il ruolo client di DTLS (`DtlsClient`). Senza il ruolo server, la parte che
riceve la chiamata non puo chiudere un handshake DTLS con un client che lo
inizia, qualunque chiave abbia.

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

- il relay e il suo dialogo: la forma del nodo non e nell'APK, e un client che
  non parla relay non chiama chi e dietro NAT simmetrico;
- il ruolo DTLS del risponditore e la divisione delle chiavi: sono dentro il
  motore nativo, e una prova contro un peer vero non e possibile da questa
  macchina (serve un telefono, e un'altra persona che risponda);
- l'audio dal vivo: non c'e una API, quindi non c'e codice da scrivere contro di
  essa.

Scrivere queste parti vorrebbe dire consegnare un motore che compila, non gira e
non puo essere provato - e la schermata della chiamata gia dice la verita:
`Call_NoAudio` ("No audio yet: this build only signals the call") compare
mentre la chiamata e su (`Pages/CallPage.xaml.cs`, riga 174).

## Cosa servirebbe, in ordine, se lo si volesse fare davvero

1. **ICE-lite e STUN** (RFC 5389/8445): rispondere ai binding request del peer e
   usare l'indirizzo riflesso. Fatto: `Stun.cs` e `IceLite.cs`, con la RFC 5769
   come prova. Manca loro la socket, che e il punto 1 di sopra.
2. **Il relay**: il dialogo con i server WhatsApp e la sua crittografia. Qui
   l'APK non basta: serve una cattura di una chiamata reale.
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
