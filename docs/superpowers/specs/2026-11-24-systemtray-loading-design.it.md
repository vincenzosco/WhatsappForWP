# Indicatore di caricamento nel SystemTray

Data: 24 novembre 2026

## Obiettivo

Mostrare `SystemTray.ProgressIndicator` di Windows Phone ogni volta che l’app in primo piano carica dati o contenuti, compresi i caricamenti automatici e la configurazione delle chiamate VoIP. Un caricamento avviato su una pagina deve mantenere visibile l’indicatore anche dopo la navigazione; le operazioni sovrapposte non devono spegnersi a vicenda.

Questa modifica riguarda solo la UI e il tracciamento delle attività. Non cambia i payload del protocollo WhatsApp, il comportamento delle chiamate, i formati di cache o i contenuti multimediali.

## Stato attuale

L’app ha indicatori locali: `BusyBar` per l’associazione, una barra percentuale di sincronizzazione su `HomePage`, indicatori per i media nei messaggi e nel visualizzatore degli stati. `ChatPage` carica i messaggi locali e può attendere la cronologia prima del binding. Altri servizi caricano chat e stati in cache, preferenze, nomi, avatar e registro chiamate. `DataService` avvia anche scansioni di chat/stati/chiamate e recupera media di messaggi e stati e avatar. Queste operazioni indipendenti non condividono un conteggio delle attività in primo piano.

`SystemTray.ProgressIndicator` è una proprietà associata alla pagina in Windows Phone 8.1 Silverlight. La pagina visibile cambia durante il lavoro, quindi impostarla una sola volta su una pagina non copre le navigazioni successive. `CallSession` distingue squillo, connessione, chiamata attiva e conclusa; il trasporto media distingue inoltre attesa, handshake, pronto e fallito.

## Design

### Coordinatore condiviso delle attività

Aggiungere un piccolo coordinatore compatibile con C# 5. I chiamanti acquisiscono un lease idempotente e disposable prima di un caricamento effettivo e lo rilasciano in `finally` (o con uno scope `using`). Il coordinatore conta i lease concorrenti invece di mantenere un singolo booleano, così il completamento di un’operazione non nasconde l’indicatore mentre un’altra è ancora in corso. Stato e contratto degli eventi devono essere testabili indipendentemente dai tipi UI di Windows Phone.

L’adapter UI possiede un unico `Microsoft.Phone.Shell.ProgressIndicator` indeterminato con testo generico localizzato. Si iscrive al coordinatore, instrada gli aggiornamenti tramite il dispatcher UI e collega l’indicatore alla `PhoneApplicationPage` corrente dopo ogni navigazione del frame principale. Ogni nuova pagina visibile riflette subito il conteggio delle attività. Senza finestra o dispatcher in primo piano, gli aggiornamenti non fanno nulla: un’attività in background non può mostrare un indicatore nel tray.

Non tracciare salvataggi, invii/upload in uscita, digitazione, riproduzione audio o keep-alive ordinari del socket. Tracciare le letture e i recuperi di dati/contenuti: ripristino cache; cronologia chat/messaggi e sincronizzazione; scansioni di stati e registro chiamate; aggiornamento della lista chat; recupero e decodifica di contatti, nomi e avatar; download di media di messaggi e stati e relativa decodifica; visualizzazione di stati; e gli altri recuperi di dati dell’app avviati da pagine o servizi condivisi. Le operazioni automatiche usano gli stessi lease di quelle avviate dall’utente. Una sincronizzazione continua della cronologia resta attiva dall’inizio esplicito fino al segnale di completamento/errore, non durante l’inattività successiva.

### Apertura della chat

L’apertura di una conversazione acquisisce un’attività prima della lettura dei messaggi in cache. Resta attiva fino al termine della lettura e del binding/scroll iniziale. Se la pagina attende il burst di cronologia corrispondente, anche tale attesa è un caricamento: l’attività resta attiva fino al burst, al timeout limitato o al completamento della richiesta di cronologia on-demand. Tutte le uscite, inclusi navigazione altrove ed errori, rilasciano i lease; un completamento tardivo non deve aggiornare una pagina che non è più visibile.

### VoIP

Tracciare l’invio dell’offerta in uscita e l’accettazione/recupero chiave/configurazione media in ingresso come attività asincrone finite. Lo squillo ordinario in attesa della risposta dell’altra persona non è un caricamento. La transizione di pre-accettazione/configurazione è un caricamento fino all’attivazione o alla conclusione della chiamata. Tracciare un trasporto media collegato solo durante l’handshake DTLS effettivo; attendere la nomination del peer non è un caricamento attivo, mentre stato pronto, fallimento, distacco o conclusione rilasciano l’attività. L’attività appartiene al coordinatore condiviso, non a `CallPage`, così navigare alla chat durante la chiamata non può lasciarla bloccata o nasconderla. Non mostrare l’indicatore per tutta la conversazione stabilita.

### Indicatori locali esistenti

Mantenere gli indicatori locali che comunicano informazioni assenti nel tray, inclusi la percentuale della cronologia e lo spinner del media/stato corrente. Il SystemTray è il segnale complessivo di attività dell’app, non un sostituto di quei dettagli. La barra locale di associazione può rimanere come feedback specifico della pagina mentre le sue fasi di rete/cache sono tracciate anche dal coordinatore.

### Localizzazione e ciclo di vita

Aggiungere il testo generico di caricamento in entrambe le risorse `.resw` inglese e italiana con chiavi corrispondenti. L’adapter si collega alla pagina attiva dopo la navigazione, si ricollega alla riattivazione dell’app se necessario e non conserva riferimenti a pagine lasciate. Al rilascio dell’ultimo lease l’indicatore si nasconde anche se un’operazione è fallita; gli errori restano registrati tramite i percorsi `Diag` esistenti.

## Verifica

- Test unitari delle transizioni del conteggio lease, dei lease sovrapposti, del dispose idempotente e degli eventi di stato.
- Aggiungere controlli statici/regressione mirati per collegamento della pagina dopo la navigazione, durata del lease di apertura chat, punti di ingresso dei caricamenti dati, limiti tra setup/squillo/chiamata attiva VoIP e rilascio bilanciato in caso di errore.
- Eseguire la suite protocollo, i test JavaScript, i gate C# 5/XAML/localizzazione/inclusione file e gli altri controlli statici, quindi la build Release ARM.
- Preparare lo XAP versionato e lo script di aggiornamento; non installarlo sul telefono. La verifica sul dispositivo richiede che l’operatore applichi l’aggiornamento e fornisca il log del telefono.

## Rischi e limiti

- Un’acquisizione mancante lascia un caricamento senza indicatore; un lease non rilasciato lo lascia acceso. Scope espliciti e test di regressione sul codice riducono entrambi i rischi.
- La proprietà del tray Windows Phone è associata alla pagina. Serve ricollegarla dopo la navigazione e provarla sul dispositivo; i test offline del sorgente non possono garantire il rendering del sistema.
- La sincronizzazione in background gira in un processo senza finestra e non può mostrare questa UI. Il coordinatore viene usato solo nel codice condiviso col processo in primo piano; non si promette un indicatore quando il processo UI è assente.
- L’indicatore del SystemTray è indeterminato e generico; le percentuali precise restano nell’interfaccia locale di sincronizzazione esistente.
