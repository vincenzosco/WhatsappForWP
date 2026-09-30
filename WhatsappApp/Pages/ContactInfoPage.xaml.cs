using System;
using System.Collections.ObjectModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// Le informazioni di una persona o di un gruppo: la foto grande, il nome,
    /// il numero, l'about, il profilo aziendale e, per un gruppo, la descrizione
    /// e i membri. La pagina chiede una cosa sola (`contact.info`) e aspetta una
    /// cosa sola; l'adapter ha gia' messo insieme il resto.
    /// </summary>
    public sealed partial class ContactInfoPage : Page
    {
        private Contact _contact;

        private readonly ObservableCollection<ContactMember> _members =
            new ObservableCollection<ContactMember>();
        private readonly ObservableCollection<ContactHours> _hours =
            new ObservableCollection<ContactHours>();

        public ContactInfoPage()
        {
            this.InitializeComponent();

            ToolTipService.SetToolTip(BackButton, Loc.Get("ContactInfoPage_BackTooltip", "Back"));

            TitleText.Text = Loc.Get("ContactInfoPage_Title", "Contact info");
            NumberLabel.Text = Loc.Get("ContactInfoPage_Number", "Phone number");
            AboutLabel.Text = Loc.Get("ContactInfoPage_About", "About");
            BusinessLabel.Text = Loc.Get("ContactInfoPage_Business", "Business");
            BusinessEmailLabel.Text = Loc.Get("ContactInfoPage_Email", "Email");
            BusinessAddressLabel.Text = Loc.Get("ContactInfoPage_Address", "Address");
            BusinessCategoriesLabel.Text = Loc.Get("ContactInfoPage_Categories", "Categories");
            BusinessHoursLabel.Text = Loc.Get("ContactInfoPage_Hours", "Hours");
            GroupDescriptionLabel.Text = Loc.Get("ContactInfoPage_GroupDescription", "Group description");
            EmptyText.Text = Loc.Get("ContactInfoPage_NoInfo", "No information from the server.");

            MembersList.ItemsSource = _members;
            HoursList.ItemsSource = _hours;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _contact = e.Parameter as Contact;
            if (_contact == null) return;

            // L'avatar e le iniziali vengono dal contatto: la pagina non li
            // ricostruisce.
            DataContext = _contact;
            NameText.Text = _contact.Name;

            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;
            RequestInfo();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            CommunicationService.Instance.ControlMessageReceived -= OnControlMessageReceived;
            HideFullScreen();
        }

        /// <summary>
        /// Chiede il profilo. Non c'e' niente da attendere: la risposta e' un
        /// frame di controllo, e arriva all'evento.
        /// </summary>
        private void RequestInfo()
        {
            if (!CommunicationService.Instance.IsConnected) return;
#pragma warning disable 4014
            CommunicationService.Instance.SendControlAsync("contact.info", _contact.Id);
#pragma warning restore 4014
        }

        private void OnControlMessageReceived(object sender, ChatMessage message)
        {
            if (message == null || message.Command != "contact.info") return;
            if (message.ChatId != _contact.Id) return;

            Apply(ContactInfo.FromJson(message.Text));
        }

        /// <summary>
        /// Riempie la pagina. `any` dice se c'e' qualcosa da mostrare: quando non
        /// c'e' niente, la pagina lo dice invece di restare mezza vuota.
        /// </summary>
        private void Apply(ContactInfo info)
        {
            bool any = false;

            if (info != null)
            {
                if (!string.IsNullOrEmpty(info.Name)) NameText.Text = info.Name;

                // L'immagine che arriva adesso vale anche per l'elenco chat: si
                // tiene, cosi' la prossima apertura la ha senza chiederla.
                if (!string.IsNullOrEmpty(info.AvatarData))
                {
                    if (_contact.AvatarData != info.AvatarData)
                    {
                        _contact.AvatarData = info.AvatarData;
#pragma warning disable 4014
                        _contact.LoadAvatarAsync();
#pragma warning restore 4014
                    }
                    AvatarCache.Remember(_contact.Id, info.AvatarData);
                }

                if (!string.IsNullOrEmpty(info.Number))
                {
                    NumberText.Text = info.Number;
                    NumberLabel.Visibility = Visibility.Visible;
                    NumberText.Visibility = Visibility.Visible;
                    any = true;
                }

                if (!string.IsNullOrEmpty(info.About))
                {
                    AboutText.Text = info.About;
                    AboutLabel.Visibility = Visibility.Visible;
                    AboutText.Visibility = Visibility.Visible;
                    any = true;
                }

                ApplyBusiness(info.Business, ref any);
                ApplyGroup(info.Group, ref any);
            }

            EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyBusiness(ContactBusiness business, ref bool any)
        {
            if (business == null)
            {
                BusinessPanel.Visibility = Visibility.Collapsed;
                return;
            }

            BusinessPanel.Visibility = Visibility.Visible;
            any = true;

            ShowIf(BusinessEmailLabel, BusinessEmailText, business.Email);
            ShowIf(BusinessAddressLabel, BusinessAddressText, business.Address);

            string categories = "";
            if (business.Categories != null && business.Categories.Count > 0)
                categories = string.Join(", ", business.Categories);
            ShowIf(BusinessCategoriesLabel, BusinessCategoriesText, categories);

            _hours.Clear();
            if (business.Hours != null)
            {
                for (int i = 0; i < business.Hours.Count; i++) _hours.Add(business.Hours[i]);
            }
            BusinessHoursLabel.Visibility = _hours.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Un'etichetta e il suo valore compaiono solo se il valore c'e': un
        /// titolo con sotto niente e' peggio di un titolo in meno.
        /// </summary>
        private static void ShowIf(TextBlock label, TextBlock value, string text)
        {
            bool has = !string.IsNullOrEmpty(text);
            label.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            value.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            value.Text = text ?? "";
        }

        private void ApplyGroup(ContactGroup group, ref bool any)
        {
            if (group == null)
            {
                GroupPanel.Visibility = Visibility.Collapsed;
                return;
            }

            GroupPanel.Visibility = Visibility.Visible;
            any = true;

            bool hasDescription = !string.IsNullOrEmpty(group.Description);
            GroupDescriptionLabel.Visibility = hasDescription ? Visibility.Visible : Visibility.Collapsed;
            GroupDescriptionText.Visibility = hasDescription ? Visibility.Visible : Visibility.Collapsed;
            GroupDescriptionText.Text = group.Description ?? "";

            _members.Clear();
            if (group.Members != null)
            {
                for (int i = 0; i < group.Members.Count; i++) _members.Add(group.Members[i]);
            }

            MembersLabel.Text = string.Format(
                Loc.Get("ContactInfoPage_MembersCount", "Members ({0})"), _members.Count);
        }

        /// <summary>
        /// La foto a tutto schermo si decodifica alla misura dello schermo: il
        /// cerchio grande e' 160 px e ingrandirlo lo lascerebbe sfocato.
        /// </summary>
        private const int ViewerDecodePixels = 720;

        private async void BigAvatar_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (_contact == null || string.IsNullOrEmpty(_contact.AvatarData)) return;

            try
            {
                var bitmap = await ImageHelper.FromBase64Async(_contact.AvatarData, ViewerDecodePixels);
                if (bitmap == null) return;
                ImageViewerImage.Source = bitmap;
                ImageViewer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Diag.Failed("ContactInfoPage.BigAvatar_Tapped", ex);
                HideFullScreen();
            }
        }

        private void ImageViewer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            HideFullScreen();
            e.Handled = true;
        }

        private void HideFullScreen()
        {
            ImageViewer.Visibility = Visibility.Collapsed;
            ImageViewerImage.Source = null;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
