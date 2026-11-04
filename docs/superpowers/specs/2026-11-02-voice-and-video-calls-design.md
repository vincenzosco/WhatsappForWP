# Le chiamate e le videochiamate, come le fa il client ufficiale

Data: 2 novembre 2026. Artefatto letto: `C:\Users\Vincenzo\Downloads\WhatsApp.apk`
(148,3 MB, `sha256` non ancora registrato qui), 13 file `classes*.dex` per circa
100 MB di codice e `lib/armeabi-v7a/libs.so` da 14,5 MB.

Perché questo documento esiste: `Protocol/Client/Calls.cs` sa leggere il registro
delle chiamate dallo `HistorySync` e sa **rifiutare** una chiamata che squilla, e
nient'altro. Realizzare una chiamata vera richiede due cose che il repository non
ha: la grammatica della segnalazione sul socket, e uno stack media (DTLS, SRTP,
RTP, jitter buffer). Questo è quello che l'APK ufficiale dice dell'una e
dell'altra, con la provenienza di ogni affermazione, e la parte che dall'APK **non
si può** ricavare.

## Come si è letto l'APK

`jadx` 1.5.1 è installato in `C:\Users\Vincenzo\Tools\jadx`, ma richiede un runtime
Java 11 e sulla macchina c'è solo Java 8 (`UnsupportedClassVersionError: class file
version 55.0`). Il provider Android di REA vuole un JAR `jadx-headless-mcp` che non
c'è. Quindi:

- i **DEX** sono stati letti direttamente (pool delle stringhe + tabella delle
  classi e dei metodi) con `.tools/apk/dex-scan.py`, che scrive
  `.tools/apk/dex-calls.txt`;
- il **nativo** è stato aperto con REA: `inspect_native_exports` risponde con 3
  export su `libs.so` intero, e nessuna stringa leggibile.

## Il motore nativo non è analizzabile staticamente

`libs.so` è **impacchettato**: 3 soli export nella tabella dinamica e zero stringhe
in chiaro, con `lib/armeabi-v7a/libsuperpack.so` accanto — il caricatore che
scompatta il codice a runtime. Non è un `.so` offuscato, è un contenitore: la parte
che conta (il motore `wavoip` con DTLS, SRTP, ICE, echo cancellation, H.264)
esiste solo dopo lo unpack in memoria. `inspect_binary_layout` di REA inoltre non
copre ancora ELF32 ARM (solo x64 Linux).

Conseguenza: **la segnalazione si ricostruisce, il media no.** Chi implementa il
media deve scriverlo, non ricavarlo da qui.

## L'architettura del client, per strati

I nomi delle classi sono in chiaro nel package nuovo: 300 classi sotto
`com/whatsapp/calling/`.

| strato | classi (nome reale, non offuscato) | cosa fa |
|---|---|---|
| interfaccia | `com.whatsapp.calling.*` (Compose), `CallsHistoryFragment` | schermate, registro |
| stato della chiamata | `infra/voipcalling/CallInfo`, `CallState`, `CallParticipant`, `CallGroupInfo`, `CallLinkInfo`, `CallLogInfo` | cosa è una chiamata mentre è in corso |
| segnalazione | `infra/voipcalling/CallOfferInfo`, `CallOfferAckError`, `SignalingXmppCallback`, `SignalingHttpCallback`, `SyncDevicesUserInfo` | offerta, ack, fanout sui device, inoltro HTTP |
| ponte nativo | `voipcalling/Voip`, `VoipNativeImpl`, `VoipEventCallback`, `JNIUtils` | JNI verso il motore |
| crittografia | `crypto/DefaultCryptoCallback.expandCallKey` | espansione della chiave di chiamata |
| media | `camera/VoipCamera`, `CaptureStream`, `voipcalling/EncodedVideoPassthrough`, `infra/voipcalling/GlVideoRenderer` | cattura, passthrough del video codificato, resa GL |

Il flusso che questi nomi raccontano: il layer Java **costruisce e interpreta gli
stanza**, il motore nativo fa il media e riporta gli eventi a `VoipEventCallback`;
`SignalingXmppCallback` è la direzione segnalazione→nativo e `SignalingHttpCallback`
il percorso di ripiego via HTTP (per i device che non hanno il socket aperto).

## La segnalazione: il vocabolario verificato

Queste stringhe sono nel pool dei DEX, quindi sono letterali che il client usa:

| stringa | dove sta | cosa dice |
|---|---|---|
| `call_offer` | telemetria | l'offerta è un evento con un nome, un ack e una scadenza |
| `call_offer_acked`, `call_offer_ack_timout`, `call_offer_fanout_count` | telemetria | l'offerta va a **più device** insieme e si considera persa se l'ack non arriva |
| `call_terminate` | telemetria | la chiusura è un messaggio a sé, non l'assenza di pacchetti |
| `preaccept` | stringa | esiste uno stato **prima** dell'accettazione: il telefono ha ricevuto l'offerta e sta preparando il media (squillo e ICE) |
| `enc_key` | stringa | la chiave di chiamata viaggia nella segnalazione |
| `candidates`, `relay`, `relaylatency` | stringhe | il trasporto è a **candidati** (p2p e relay), con latenze misurate verso i relay |
| `call_relay`, `call_relay_servers`, `call_relay_uuid`, `call_relay_bind_status` | telemetria | i relay sono allocati per chiamata e c'è un bind esplicito |
| `call_transport_p2p_to_relay_fallback_count`, `call_transport_relay_to_relay_fallback_count` | telemetria | il client **cambia trasporto durante la chiamata** |
| `call_relaying_help` | stringa di interfaccia | il relay è un percorso di ripiego mostrato all'utente |
| `voip_settings`, `process_voip_settings_update`, `update_voip_settings` | stringhe | c'è un blocco di impostazioni VoIP che il server aggiorna |
| `CallOfferInfo`, `CallOfferAckError` | classi | l'offerta ha una risposta tipizzata, e un rifiuto ha un motivo |

Le analytics `call_end_reconnecting_*` (`before_relay_failover`,
`soon_after_relay_failover`, `before_relay_reset`, `relay_pingable`) dicono che il
client, dopo un buco di rete, **prova a riconnettersi sullo stesso relay** prima di
chiudere: una chiamata non cade al primo pacchetto perso.

Cosa **non** è leggibile: i nomi degli attributi con il trattino (`call-id`,
`call-creator`), perché sono costruiti dai builder o stanno nel nativo. Il
`call` node che il repository già spedisce (`Calls.Reject`:
`<call to=… id=…><reject/></call>`) è coerente con questo quadro: stesso nodo, stesso
`id`, un figlio che nomina l'azione. `offer`, `preaccept`, `accept`, `terminate`
sono i fratelli di `reject` nello stesso nodo.

## Il media: cosa serve, per certo

Le stringhe di telemetria sono inequivocabili:

- `dtls_rx_bytes`, `dtls_tx_packets`, `dtlsConnectionSetupT`,
  `RP_AUDIO_PACKET_THRU_DTLS_TRANSPORT` → **DTLS** per stabilire le chiavi;
- `RP_AUDIO_PACKET_DROP_SRTP_DECRYPT`, `RP_AUDIO_PACKET_DROP_RTP_SRTP_SESSION`,
  `RP_ENCRYPTION_PACKET_DROP_SRTP_DECRYPT_V2` → **SRTP** per il media, con un
  contatore di pacchetti scartati in decifratura;
- `OpusHead`, `OpusDecoder`, `OpusLibrary`, `repackOpusFramesMaxDurationMs` →
  **Opus**, con un ripacchettizzatore che accorpa i frame;
- `H264_MIME_TYPE`, `H26xSupportResult`, `H264_BLACKLISTED_DEVICE_*` → **H.264**
  per il video, con blacklist per modello;
- `expandCallKey` in `crypto/DefaultCryptoCallback` → la chiave di chiamata che
  arriva in `enc_key` viene **espansa** (una KDF) nelle chiavi DTLS/SRTP.

Quindi una chiamata vocale è: segnalazione → scambio DTLS → SRTP → RTP con Opus a
20 ms. Niente di tutto questo esiste su Windows Phone 8.1: la piattaforma offre
RTP? no; SRTP? no; DTLS? no. Va scritto.

## Cosa manca all'app, in ordine di dipendenza

1. **La grammatica completa del nodo `call`.** Oggi c'è solo `reject`. Servono
   `offer`, `preaccept`, `accept`, `terminate` con i motivi, e il fanout sui device
   dell'altro (`SyncDevicesUserInfo` dice che il client ufficiale lo fa).
2. **Il ciclo di vita.** `CallState` e `CallInfo` sono lo stato che l'interfaccia
   disegna: squillo, in corso, in riconnessione, chiusa. Oggi l'app ha solo righe di
   registro lette dalla history.
3. **DTLS 1.2 con PSK/certificato e SRTP.** Nessuna delle due c'è nel profilo
   .NET di WP8.1: sono da scrivere in C# (o nel nativo `waopus.dll`, che però ora è
   senza CRT e senza math — aggiungerci DTLS è un lavoro a sé).
4. **RTP + jitter buffer + Opus.** L'Opus c'è già (`waopus.dll`, encoder e decoder);
   manca il resto del percorso.
5. **Relay.** Il client ufficiale non fa solo p2p: alloca relay, misura
   `relaylatency`, fa failover. Un client che non parla relay non chiama chi è
   dietro NAT simmetrico — cioè quasi tutti.
6. **Video.** H.264: WP8.1 ha un encoder/decoder H.264 in Media Foundation, quindi
   la strada esiste, ma la cattura camera in `MediaCapture` e il rendering in
   `MediaElement` non sono un percorso realtime. È la fase più grande.

## Giudizio sulla dimensione

Il vocabolario della segnalazione è ricostruibile e vale poche centinaia di righe
(`Protocol/Client/Calls.cs` più i test). Uno stack DTLS+SRTP+RTP corretto, con
relay e failover, è un progetto a sé: è la ragione per cui nessun client non
ufficiale di WhatsApp (Baileys, whatsmeow, e questo) fa le chiamate, mentre tutti
fanno i messaggi. Il repository lo dice già indirettamente: `Calls.cs` implementa
il registro e il rifiuto, cioè esattamente la parte che non richiede media.

La strada onesta, se si vuole una chiamata che funzioni: prima la segnalazione e
l'interfaccia (squillo, accetta, rifiuta, chiudi) con il media **dichiaratamente
assente** — l'app squilla e risponde, e la voce non passa — poi lo stack audio, poi
il video. Ogni fase è verificabile da sola, e la prima è già quasi tutta qui.

## Cosa è stato implementato il 4 novembre

La segnalazione c'era; il modello no. Questi tre pezzi sono ora nel codice, presi
dall'APK e non inventati:

- **I quindici stati.** `Services/CallSession.cs` porta i valori di
  `com.whatsapp.calling.infra.voipcalling.CallState` con i loro nomi
  (`None`, `PreCalling`, `Calling`, `ReceivedCall`, `PreAcceptReceived`,
  `AcceptSent`, `AcceptReceived`, `Link`, `Active`, `ConnectedLonely`,
  `Rejoining`, `Ending`, `ActiveElsewhere`, `Screening`, `BCallStarting`). Le
  cinque parole che l'app usava prima (`Idle`, `Ringing`, `Connecting`, `Active`,
  `Ended`) non sono sull'aria: sono ora `CallPhase`, derivata da `State` da
  `CallSession.PhaseOf`, e le pagine leggono quella. Il motivo per tenerle tutte
  e due è che gli stati non sono un anello lineare: `Link`, `ActiveElsewhere` e
  `Screening` dicono *cos'è* la chiamata, e una UI che ne conosce cinque non sa
  quale delle tre sta guardando.
- **Gli eventi.** `CallEvent` è il sottoinsieme di `VoipEventCallback` (119
  metodi) che un client di sola segnalazione ha qualcosa da fare: `CallStateChanged`,
  `CallOfferReceiptReceived`, `CallOfferAcked`, `CallOfferNacked`,
  `HandleOfferAckFailed`, `CallRejectReceived`, `CallTerminateReceived`,
  `CallEnding`, `CallMissed`, `CallWaitingStateChanged`, `CallLinkStateChanged`,
  `GroupInfoChanged`, `GroupParticipantLeft`, `DataChannelReady`,
  `DataChannelConnectionTimeout`, `HeartbeatNacked`, `BatteryLevelLow`,
  `HandleCallFatal`, `AutoVideoPauseStateChanged`. Cinque chiudono la chiamata
  (`reject`, `terminate`, `ending`, `missed`, `fatal`); gli altri aggiornano
  `LastEvent` e basta, perché appartengono al motore media che non c'è.
- **Il registro con lo schema ufficiale.** `Protocol/Client/CallLogTables.cs`
  riproduce `call_log` (con `call_log_participant_v2`) e `call_link` con gli
  indici del database ufficiale: per `timestamp` (la lista si legge dal più
  recente), per `call_id`, per `user_jid`, per `telecom_uuid`, per i partecipanti
  e `token` **unico** per i link. `Services/CallLogStore.cs` lo tiene in
  `call-log.json` dentro `LocalFolder`: WP8.1 non ha SQLite, e a questa
  dimensione gli indici valgono più del motore. `DataService.RememberCall` scrive
  una riga a ogni chiamata chiusa, con lo stesso id che la chiamata aveva
  sull'aria: è quello che rende la seconda copia di una chiamata una riga sola.

Quello che il modello sa e l'aria non gli ha ancora detto: `Link`,
`ConnectedLonely`, `Rejoining`, `ActiveElsewhere`, `Screening`, `BCallStarting`
sono valori che esistono e che nessun percorso di questo client produce ancora.
Sono dichiarati, non simulati.
