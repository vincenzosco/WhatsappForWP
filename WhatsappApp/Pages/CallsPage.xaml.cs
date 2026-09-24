using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// Sezione chiamate: la pagina esiste e si naviga, ma non ha dati finche'
    /// l'adapter non espone le chiamate.
    /// </summary>
    public sealed partial class CallsPage : Page
    {
        public CallsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Nav.Current = AppSection.Calls;
        }
    }
}
