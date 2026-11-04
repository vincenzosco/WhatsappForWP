# Tutto quello che l'APK dice delle chiamate

Data: 4 novembre 2026. Artefatto: `C:\Users\Vincenzo\Downloads\WhatsApp.apk`,
155.531.089 byte, `sha256 887a6f94f6614791cfa845fe0b9990c064326d8b89cd71d81e817fc9fd530c2e`,
15.728 voci. Metodo: i 13 `classes*.dex` letti direttamente (pool delle stringhe,
tabella delle classi, `class_data_item`), il nativo letto dai 140 stream già
scompattati di `libs.so`, il `AndroidManifest.xml` letto come XML binario. Nessun
`jadx`: richiede Java 11 e sulla macchina c'è Java 8.

Perché esiste: la spec del 2 novembre (`2026-11-02-voice-and-video-calls-design.md`)
fissava l'architettura e il vocabolario della segnalazione, ma si fermava alle
stringhe che contengono la parola `call`. Questo documento prende **il resto** —
il vocabolario che di "call" non parla (`srtp`, `dtls`, `ssrc`, `relay`, `warp`) e
l'intera superficie di classi, metodi, campi, componenti e chiavi. Gli estratti
grezzi sono in `.tools/apk/calls/` (cartella ignorata da git).

## Cosa c'è dentro

| strato | cosa contiene | numeri |
|---|---|---|
| DEX | stringhe, classi, metodi, campi | 13.574 stringhe distinte, 901 classi |
| nativo (`libs.so`, stream scompattati) | motore `wavoip`, simboli `wa_call_*`/`wa_voip_*` | 9.916 stringhe, 496 simboli |
| manifest | permessi, servizi, activity, receiver | 62 stringhe, 42 componenti `com.whatsapp` |
| asset `strings_*.spo` | dizionario delle etichette UI per lingua | 1 pack per lingua, ~1 MB decompresso |
| `resources.arsc` | **assente** | — |

La tabella dei file dentro gli `.spo` dichiara un solo membro, `strings_it.pack`
(1.052.134 byte decompressi, `xxh64 0xacff11057a4c69e2`); il payload però non è
testo: 50,5% di byte non nulli e nessuna sequenza stampabile di 6 caratteri. È un
**dizionario indicizzato** (valori u16 piccoli, un prefisso per voce), quindi le
etichette UI non si estraggono senza decodificare anche quel formato. Le etichette
che servono all'app restano quelle dei `.resw`.

## Il livello Java: 901 classi, per package

| package | classi | cosa è |
|---|---|---|
| `com.whatsapp.calling.ui` | 135 | schermate: `VoipActivityV2`, registro, link, dialer, spam |
| `com.facebook.cameracore.mediapipeline` | 111 | effetti camera (AR) usati anche in chiamata |
| `com.meta.wearable.comms` | 90 | chiamate sugli occhiali/wearable |
| `com.whatsapp.mediacomposer.ui` | 53 | editor media (condivisione in chiamata) |
| `com.whatsapp.infra.media` | 52 | infrastruttura media |
| `com.whatsapp.calling.infra` | 36 | stato, callback, video port, crypto |
| `com.facebook.wearable.common` | 32 | trasporto wearable |
| `androidx.core.telecom.internal` | 16 | ponte Telecom (Android 14+) |
| `com.whatsapp.calling.camera` | 12 | `VoipCamera`, `VoipCameraManager`, `CaptureStream` |
| `com.whatsapp.calling.voipcalling` | 11 | `Voip`, `VoipNativeImpl`, `VoipEventCallback` (JNI) |
| `com.whatsapp.calling.service` | 9 | `VoiceFGService`, ringtone e vibrazione |

### Le classi che definiscono la chiamata

| classe | campi | metodi |
|---|---|---|
| `infra/voipcalling/CallInfo` | 60 | 155 |
| `infra/voipcalling/CallOfferInfo` | 15 | 5 |
| `infra/voipcalling/CallLinkInfo` | 9 | 11 |
| `voipcalling/Voip` | 152 | 187 |
| `voipcalling/VoipEventCallback` | — | 119 |
| `telecom/SelfManagedConnectionService` | 2 | 9 |

`CallInfo` è la fotografia di una chiamata in corso, e i suoi 60 campi si
raggruppano così: **identità** (`callId`, `groupJid`, `peerJidRaw`,
`creatorJidRaw`, `creatorDeviceJidRaw`, `phash`, `phashBasedCall`, `relayCallUuid`);
**stato** (`callState`, `callResult`, `callSetupErrorType`, `isCallEnding`,
`isEndedByMe`, `callActiveTime`, `audioDuration`, `videoDuration`);
**ruolo** (`isCaller`, `isGroupCall`, `isJoinableGroupCall`,
`isGroupCallCreatedOnServer`, `isBCall`, `isBCallBroadcaster`, `isBotCall`,
`isBotGroupCall`, `isNotE2ee`); **video** (`isVideoEnabled`,
`isVideoCaptureStarted`, `isVideoPreviewReady`, `unknownVideoPeer`,
`isDualStreamSsEnabled`, `isAvAutoAcceptEnabled`); **attesa e screening**
(`isInWaitingRoom`, `isWaitingRoomAdmin`, `isWaitingRoomEnabled`,
`isAutoScreened`, `isCallerScreenable`, `screenSharerInfo`); **link**
(`callLinkToken`, `callLinkState`, `callLinkCreatorJid`).

`CallState` ha **15 valori**, e sono la macchina a stati da rispettare:

```
NONE, PRECALLING, CALLING, RECEIVED_CALL, PRE_ACCEPT_RECEIVED,
ACCEPT_SENT, ACCEPT_RECEIVED, LINK, ACTIVE, CONNECTED_LONELY,
REJOINING, ENDING, ACTIVE_ELSEWHERE, SCREENING, BCALL_STARTING
```

Nota per l'app WP8: `PRE_ACCEPT_RECEIVED` conferma che il destinatario ha ricevuto
l'offerta e sta preparando il media **prima** di accettare, e `ACTIVE_ELSEWHERE` è
lo stato di una chiamata presa su un altro dispositivo — due stati che una UI a due
valori ("squilla"/"in corso") non può rappresentare.

`CallOfferInfo` porta ciò che serve a decidere se far squillare: `callId`,
`fromJid`, `groupJid`, `participantHash`, `callLinkToken`, `isVideoCall`,
`isAudioChat`, `isJoinableCall`, `isOfferExpired`, `silenceReason`,
`epochTimeMillis`, `callGroupInfo`, `uploadFieldStat`.

## Il contratto JNI: cosa il livello Java chiede al motore

`Voip` (187 metodi) è la superficie completa. I gruppi che contano:

- **ciclo di vita**: `invite`, `inviteToGroupCall`, `acceptCall`,
  `acceptCallWithVideoStopped`, `acceptMediaOnly`, `commitAccept`, `endCall`,
  `endCallAndAcceptPendingCall`, `cancelInviteToGroupCall`,
  `cancelVideoUpgrade`, `acceptVideoUpgrade`, `fastSwitch`, `extendVCTimeout`,
  `checkOngoingCalls`, `cleanupUnfinishedCallStats`;
- **link di chiamata**: `createCallLink`, `editCallLink`, `getCallLinkInfo`,
  `getCurrentCallLinkState`;
- **stato**: `getCallInfo`, `getCurrentCallId`, `getCurrentCallState`,
  `getCurrentCallStateEnum`, `getCallStateEnumFromInt`, `getPeerJid`,
  `getStreamStatistics`, `getStreamStatisticsShort`,
  `getCurrentRxNetworkConditionerParameters`,
  `getCurrentTxNetworkConditionerParameters`, `getUnfinishedCallEvent`,
  `getVoipCallEndReasonFromDetailedEndCallReason`;
- **parametri**: `getVoipParam`, `getVoipParamAsBool/Int/Str/IntArray`,
  `setVoipParam` equivalente tramite `clearVoipParam`, `getVoipParamForCall`;
- **audio**: `adjustAudioLevel`, `enableNoiseCancellation`,
  `hackBuiltInAec`, `hackBuiltInAgc`, `hackBuiltInNs`,
  `isBuildInAecAvailable`, `audioRouteToString`;
- **segnale in ingresso**: `handleIncomingSignalingHttp`,
  `handleIncomingTerminatePush`, `dataChannelSendMessage`;
- **UI**: `handleUIViewChange`, `dumpLastVideoFrame`,
  `allowUnknownPeerVideo`, `getScreeningRecordingInfo`,
  `callLogResultTypeToString`.

`VoipEventCallback` (119 metodi) è il ritorno, e i nomi sono l'elenco completo
degli eventi che una chiamata produce. Oltre ai prevedibili `callStateChanged`,
`callEnding`, `callMissed`, `callRejectReceived`, `callTerminateReceived`,
`callOfferAcked`, `callOfferNacked`, `handleOfferAckFailed`, `callWaitingStateChanged`,
ci sono: `groupInfoChanged`, `groupParticipantLeft`, `groupCallReminderReceived`,
`callLinkStateChanged`, `callLinkSelfStateChanged`, `dataChannelReady`,
`dataChannelConnectionTimeout`, `heartbeatNacked`, `batteryLevelLow`,
`highDataUsageDetected`, `interruptionStateChanged`, `autoVideoPauseStateChanged`,
`callGridRankingChanged`, `fieldstatsReady`, `eventNotHandled`, `handleCallFatal`,
`handleVoipAssert`, `handleFDLeakDetected`, `eagerCallDismiss`, `botPresenceChanged`,
`bCallCreated`, `bCallJoined`, `bCallEnded`, `bCallAudienceUpdated`.

La crittografia ha un callback suo: `crypto/DefaultCryptoCallback` e
`crypto/SignalIdentityCryptoCallback` implementano `infra/crypto/CryptoCallback` —
la chiave di chiamata si espande lì, non nella segnalazione.

## Il ponte con Telecom (Android 14+ e auto/wear)

`androidx.core.telecom.internal.CallSession` è la sessione: `onAnswer`,
`onDisconnect`, `onSetActive`, `onSetInactive`, `onMuteStateChanged`,
`onVideoStateChanged`, con i casi `enforceVideoCallSpeakerFallback`,
`maybeRerouteToEarpiece`, `switchToSpeakerForVideoCallIfNeeded`,
`avoidSpeakerOverrideOnCallStart`, `moveState`.
`com.whatsapp.calling.telecom.coretelecom.CoreTelecomRepository` fa il lavoro:
`addIncomingCall`, `addOutgoingCall`, `answerBeforeAccept`, `handleAudioRoutes`,
`handleVoipCallStateChanges`. `SelfManagedConnectionService` è il servizio
dichiarato nel manifest.

I permessi che il manifest chiede per le chiamate: `CALL_PHONE`, `READ_CALL_LOG`,
`WRITE_CALL_LOG`, `FOREGROUND_SERVICE_MICROPHONE`, `FOREGROUND_SERVICE_CAMERA`,
`FOREGROUND_SERVICE_PHONE_CALL`, `FOREGROUND_SERVICE_MEDIA_PROJECTION`,
`FOREGROUND_SERVICE_MEDIA_PLAYBACK`, `FOREGROUND_SERVICE_LOCATION`,
`FOREGROUND_SERVICE_DATA_SYNC`, `FOREGROUND_SERVICE_CONNECTED_DEVICE`,
`CHANGE_DEVICE_IDLE_TEMP_WHITELIST`, `UPDATE_DEVICE_STATS`.

Componenti dichiarati, per esteso: `VoipActivityV2`, `VoipAppUpdateActivity`,
`VoipNotAllowedActivity`, `VoipPermissionsActivity`, `DialerActivity`,
`CallSpamActivity`, `CallLogActivityV2`, `GroupCallLogActivity`,
`GroupCallParticipantPicker(Sheet)`, `FavoriteCallListActivity`,
`UpcomingCallListActivity`, `CallLinkActivity`, `CallRatingActivity`,
`GroupCallPsaActivity`, `PostCallUpsellActivity`,
`PostCallWearableUpsellActivity`, `WebActivationCallingUpsellActivity`,
`SystemDialerCallLandingActivity`, `SettingsCallingPrivacyActivity`,
`SelfManagedConnectionService`, `VoiceFGService`, `CallLinkShareReceiver`,
`DeclineIntentReceiver`, `MissedCallNotificationDismissedReceiver` (con
`DISMISS`), `MuteStateReceiver`, azioni `com.whatsapp.calling.hangup_call`,
`reject_call`, `end_bot_call`, `reject_group_reminder`, tipo di contatto
`vnd.android.cursor.item/vnd.com.whatsapp.voip.call`.

## Il nativo: 496 simboli, media compreso

`wa_call_*` e `wa_voip_*` sono i 496 simboli che il motore espone, e il suffisso
dice cosa fa: `wa_call_accept`, `wa_call_accept_media_only`,
`wa_call_commit_accept`, `wa_call_cancel_invite_to_group_call`,
`wa_call_convert_xmpp_msg_to_msg`, `wa_call_copy_call_info`,
`wa_call_copy_call_link_info`, `wa_call_data_channel_send_msg`,
`wa_call_add_or_update_relay_item`, `wa_call_clear_voip_params`,
`wa_call_close_external_alt_sock`, `wa_call_bwe_history`,
`wa_call_bot_verify_incoming_identity_key`.

Il media, che la spec precedente dava per non ricostruibile, adesso è **contato**:
su 9.916 stringhe native, `warp` 704, `ice` 660, `relay` 651, `rtcp` 545,
`turn` 519, `av1` 477, `sfu` 453, `p2p` 321, `dtls` 301, `srtp` 268, `opus` 252,
`ssrc` 239, `h264` 229, `stun` 107, `jitter` 104. Cioè: ICE con STUN e TURN,
DTLS per HBH, SRTP per i flussi, RTP/RTCP con SSRC, relay e SFU, codec opus e
H.264/AV1, WARP come trasporto proprietario, `jitter` buffer e BWE
(`BWE delay-based update options`, `final transport decision ... p2p_bps`,
`relay_bps`, `threshold_pct`).

Le chiavi di segnalazione che compaiono nei formati di log (quindi nei costruttori
di stanza): `call-id` 10, `call-creator` 18, `call_state` 34, `voip_settings` 13,
`joinable` 10, `preaccept` 26, `rekey` 12, `enc_key` 1, `relaylatency` 1,
`hbh` 194, `ssrc` 195.

C'è anche una **storia delle chiamate nel motore**, non solo nel DB Java:
123 simboli `wa_voip_history_*` / `wa_calling_history_*`, cioè
`wa_voip_history_add_call_transport_record`,
`wa_calling_history_generate_call_state_v1_record`,
`wa_calling_history_load_call_state_v1_record`,
`wa_calling_history_get_web_all_transport_info`,
`..._convert_storage_elem_to_call_transport_record`, con limiti espliciti
(`max_num_of_call_transport_record == 0, skip saving call record`).

## Il database e le chiavi

Tabelle e colonne, dalle query nel pool: `call_log` (`_id`, `timestamp`,
`call_result`, `from_me`, `call_id`, `user_jid`, `transaction_id`,
`telecom_uuid`, `video_call`, `row_id`), `call_log_participant_v2`,
`call_link` (`token`, `creator_jid`, `state`, `media_type`, `lobby_token`,
`waiting_room_enabled`, `last_used_timestamp`, `random_id`, `shared_app`),
`event(call_link_token)`, `joinable_call_log_store`. Indici:
`call_log_ts_index`, `call_link_token_index` (unico), `call_link_token_index` su
`event`, `call_log_telecom_uuid_index`, `call_log_participant_key_index`,
`call_logs_key_index`. L'accoppiata device/chiamata si interroga con
`account_lid = ? AND call_id = ?`.

Chiavi di preferenze e punti di traccia (formato `dominio/evento`):
`voip/service/start|stop|created|cmd/`, `voip/service/signal_thread/end_call/`,
`voip/ringtone/play|stop|setmode` e `voip/ringtone/premium/{play,stop,loop,fallback}`,
`voip/vibrate/start|stop`, `voip/audio_route/init|deinit`,
`voip/call/reject`, `voip/invite`, `voip/precall_connect_failed`,
`voip/orphannotification`, `call_link_store/insert|update_creator`,
`joinable_call_log_store/insert|update`, `missedcallnotification/dismiss`,
`device/messages/call_log`, `export/call_log/carried`.

## Cosa non c'è, e perché

- **`resources.arsc` non esiste** in questo APK: le `res/` hanno nomi offuscati
  (`res/-.xml`, `res/--.png`) e non c'è tabella risorse. Quindi non ci sono layout
  né stringhe di sistema da leggere: la UI va progettata, non copiata.
- **Gli `.spo` sono un dizionario indicizzato**, non testo: 6 stream xz per
  l'italiano che rendono 83.459 byte con zero run stampabili e metà byte nulli.
  Etichette come "Chiamata in arrivo" non sono estraibili senza decodificare quel
  formato, che è separato da sp2.
- **Il media resta da scrivere**: i simboli e le stringhe dicono *quale* stack
  serve e *come si chiama*, non come è implementato. Un `libs.so` scompattato è
  codice, non specifica.
- **La grammatica esatta dello `<call>`** (quali attributi, in che ordine, con
  quali valori) resta quella ricostruita dalla spec del 2 novembre: qui si
  aggiungono le chiavi e gli stati, non i costruttori.

## Cosa serve all'app, in ordine

1. gli **stati** di `CallState` e gli eventi di `VoipEventCallback` come macchina
   a stati della UI: sono 15 stati e 119 eventi, e l'app ne implementa oggi due;
2. i **campi di `CallInfo`** come modello: identità, ruolo, video, attesa, link;
3. le **chiavi di segnalazione** (`call-id`, `call-creator`, `call_state`,
   `enc_key`, `joinable`, `preaccept`, `rekey`) con i valori di stato in chiaro;
4. la **storia nel motore** come modello per il registro locale: trasporto per
   record, non solo esito finale.
