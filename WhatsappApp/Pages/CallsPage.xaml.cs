using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// Registro chiamate. I dati non sono locali: l'adapter li ricava dalla
    /// history di GOWA (solo chiamate in entrata, dalle chat piu' recenti) e li
    /// manda quando riceve il comando "calls".
    /// </summary>
    public sealed partial class CallsPage : Page
    {
        public CallsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;

            CallsListView.ItemsSource = DataService.Instance.Calls;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Nav.Current = AppSection.Calls;

            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
            DataService.Instance.CallsScanCompleted += OnCallsScanCompleted;

            RefreshCalls();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshCalls();
        }

        private void RefreshCalls()
        {
            if (!CommunicationService.Instance.IsConnected)
            {
                ShowMessage(Loc.Get("CallsPage_NotConnected",
                    "Not connected to the server: call records are unavailable."));
                return;
            }

            DataService.Instance.ClearCalls();
            ShowMessage(Loc.Get("CallsPage_Scanning", "Looking for calls..."));

            // OnNavigatedTo non e' async: si manda la richiesta e si aspetta il
            // frame "calls.done" per sapere che l'adapter ha finito.
#pragma warning disable 4014
            CommunicationService.Instance.SendControlAsync("calls");
#pragma warning restore 4014
        }

        private void OnCallsScanCompleted(object sender, EventArgs e)
        {
            if (DataService.Instance.Calls.Count == 0)
            {
                ShowMessage(Loc.Get("CallsPage_EmptyHint",
                    "Incoming calls found in the most recent chats. Nothing to show yet."));
                return;
            }

            StatusText.Visibility = Visibility.Collapsed;
        }

        /// <summary>Mostra una riga di stato al posto (o sopra) della lista vuota.</summary>
        private void ShowMessage(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
