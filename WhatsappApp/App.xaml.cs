using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;
using WhatsappApp.Pages;
using WhatsappApp.Services;

// Il modello di applicazione vuota è documentato all'indirizzo http://go.microsoft.com/fwlink/?LinkId=391641

namespace WhatsappApp
{
    /// <summary>
    ///Fornisce un comportamento specifico dell'applicazione in supplemento alla classe Application predefinita.
    /// </summary>
    public sealed partial class App : Application
    {
        private TransitionCollection transitions;

        // Navigate() fallisce restituendo false, non lanciando: il motivo vero
        // arriva qui. Senza questo, un errore XAML in una pagina si presenta
        // come "Failed to create initial page" e nient'altro.
        private Exception navigationFailure;

        /// <summary>
        /// Inizializza l'oggetto singleton Application. Si tratta della prima riga del codice creato
        /// eseguita e, come tale, corrisponde all'equivalente logico di main() o WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            this.Suspending += this.OnSuspending;
            this.Resuming += this.OnResuming;
        }

        /// <summary>
        /// Richiamato quando l'applicazione viene avviata normalmente dall'utente.  All'avvio dell'applicazione
        /// verranno utilizzati altri punti di ingresso per aprire un file specifico, per visualizzare
        /// risultati di ricerche e così via.
        /// </summary>
        /// <param name="e">Dettagli sulla richiesta e sul processo di avvio.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
#if DEBUG
            if (System.Diagnostics.Debugger.IsAttached)
            {
                this.DebugSettings.EnableFrameRateCounter = true;
            }
#endif

            // Il loader delle risorse non si puo' creare da un thread di
            // background: lo si crea qui, una volta, sul thread UI.
            Loc.Prewarm();

            // Stesso motivo del loader: il dispatcher si trova di sicuro solo
            // qui, sul thread UI. Risolverlo piu' tardi, da un thread di rete,
            // lasciava il servizio senza dispatcher per tutta la sessione.
            CommunicationService.Instance.Prewarm();

            // Il servizio dati si aggancia qui: prima si creava alla prima
            // pagina che lo toccava, e i messaggi arrivati nel frattempo (o i
            // contatti sincronizzati) non avevano nessun ascoltatore.
            DataService.Instance.Start();

#if DEBUG
            // Solo in debug: dice in tre righe cosa questo telefono sa fare
            // davvero, invece di lasciarlo scoprire da un catch silenzioso. In
            // rilascio non esiste, quindi non costa niente all'avvio.
            SelfCheck.RunAsync();
#endif

            // Riconnessione automatica: l'app non riprova da sola dopo un
            // riavvio, e senza questo l'elenco chat resta vuoto finche' l'utente
            // non apre le impostazioni.
            if (SettingsService.HasSavedSettings) StartAutoConnect();

            // E poi la tiene viva: WP8.1 chiude il socket sospendendo l'app, e
            // alla ripresa la connessione risulta attiva ma non passa piu'
            // niente (vedi ConnectionWatchdog).
            ConnectionWatchdog.Instance.Start();

            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                // Tre pagine di sezione (Chats/Status/Calls) con
                // NavigationCacheMode.Enabled: la cache le tiene in vita, cosi'
                // passare da una sezione all'altra non ricostruisce la pagina
                // (l'elenco chat conserva anche la posizione di scorrimento).
                rootFrame.CacheSize = 3;
                rootFrame.Language = Windows.Globalization.ApplicationLanguages.Languages[0];

                Window.Current.Content = rootFrame;
            }

            if (rootFrame.Content == null)
            {
                if (rootFrame.ContentTransitions != null)
                {
                    this.transitions = new TransitionCollection();
                    foreach (var c in rootFrame.ContentTransitions)
                    {
                        this.transitions.Add(c);
                    }
                }

                rootFrame.ContentTransitions = null;
                rootFrame.Navigated += this.RootFrame_FirstNavigated;
                rootFrame.NavigationFailed += this.RootFrame_NavigationFailed;

                // Dopo una terminazione (l'OS ha chiuso il processo mentre l'app era
                // sospesa) si riparte dalla sezione in cui l'utente si trovava,
                // invece che sempre dalle chat. Contatti e messaggi non si
                // ripristinano: l'adapter li rimanda alla connessione.
                Type startPage;
                if (!SettingsService.HasSavedSettings)
                {
                    startPage = typeof(ConnectionPage);
                }
                else if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    startPage = SectionNav.PageFor(SessionService.Section);
                }
                else
                {
                    startPage = typeof(ChatsPage);
                }
                if (!rootFrame.Navigate(startPage, e.Arguments))
                {
                    // Il nome della pagina e l'eccezione vera, non solo la
                    // frase del modello: senza di essi un XAML rotto e' un
                    // crash muto.
                    throw new Exception(
                        "Failed to create initial page: " + startPage.FullName,
                        this.navigationFailure);
                }
            }

            Window.Current.Activate();
        }

        /// <summary>
        /// Navigazione fallita: conserva l'eccezione per il messaggio di
        /// OnLaunched, che altrimenti riporterebbe solo il valore false.
        /// </summary>
        private void RootFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            this.navigationFailure = e.Exception;
        }

        /// <summary>
        /// Ripristina le transizioni del contenuto dopo l'avvio dell'applicazione.
        /// </summary>
        /// <param name="sender">Oggetto a cui è associato il gestore.</param>
        /// <param name="e">Dettagli sull'evento di navigazione.</param>
        private void RootFrame_FirstNavigated(object sender, NavigationEventArgs e)
        {
            var rootFrame = sender as Frame;
            rootFrame.ContentTransitions = this.transitions ?? new TransitionCollection() { new NavigationThemeTransition() };
            rootFrame.Navigated -= this.RootFrame_FirstNavigated;
        }

        /// <summary>
        /// Richiamato quando l'esecuzione dell'applicazione viene sospesa. Lo stato dell'applicazione viene salvato
        /// senza che sia noto se l'applicazione verrà terminata o ripresa con il contenuto
        /// della memoria ancora integro.
        /// </summary>
        /// <param name="sender">Origine della richiesta di sospensione.</param>
        /// <param name="e">Dettagli relativi alla richiesta di sospensione.</param>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();

            // Se l'OS termina il processo mentre l'app e' sospesa, OnLaunched
            // riparte da qui.
            SessionService.Section = CurrentSection();

            // Il socket non si chiude qui: l'OS lo chiude da solo mentre l'app
            // e' sospesa, e chiuderlo noi lascerebbe l'app segnata come
            // disconnessa senza che nessuno riprovi. Se ne occupa OnResuming,
            // che trova una connessione silenziosa e la rifa'.

            deferral.Complete();
        }

        /// <summary>
        /// L'app torna in primo piano. Il socket che aveva e' quasi sempre gia'
        /// morto - e' l'OS a chiuderlo sospendendo il processo - ma
        /// `CommunicationService.IsConnected` e' ancora true, perche' un socket
        /// chiuso dall'esterno non genera nessun evento finche' non lo si usa.
        ///
        /// Si controlla subito invece di aspettare il tick del watchdog: quei
        /// venti secondi sarebbero venti secondi di app che sembra collegata e
        /// non riceve niente.
        /// </summary>
        private void OnResuming(object sender, object e)
        {
            ConnectionWatchdog.Instance.CheckNow();
        }

        /// <summary>
        /// Prova a ricollegarsi in sottofondo. Volutamente muta: qualunque
        /// messaggio lo scrive la pagina delle impostazioni, che e' anche
        /// l'unico posto in cui la lingua e' gia' pronta.
        /// </summary>
        private async void StartAutoConnect()
        {
            await AutoConnector.Instance.TryConnectAsync(SettingsService.Username, 6);
        }

        /// <summary>Sezione della pagina in primo piano (la chat sta nelle chat).</summary>
        private static AppSection CurrentSection()
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null) return AppSection.Chats;
            if (frame.Content is StatusPage) return AppSection.Status;
            if (frame.Content is CallsPage) return AppSection.Calls;
            return AppSection.Chats;
        }
    }
}