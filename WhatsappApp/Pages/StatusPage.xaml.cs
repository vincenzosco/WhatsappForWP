using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// Sezione stato: la pagina esiste e si naviga, ma non ha dati finche'
    /// l'adapter non espone gli stati.
    /// </summary>
    public sealed partial class StatusPage : Page
    {
        public StatusPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Nav.Current = AppSection.Status;
        }
    }
}
