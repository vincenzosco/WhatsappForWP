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
    /// The information of a person or a group: the large picture, the name and the
    /// number, and - when something can fill them - the about, the business profile
    /// and a group's description and members.
    ///
    /// The name and the number are this phone's own: they came with the chat row, and
    /// the page shows them without asking anybody. The picture is asked of WhatsApp
    /// itself (`NativeConnection.RequestAvatarAsync` - the query answers a URL, the URL
    /// answers the JPEG). Everything the rest of the page draws used to arrive in one
    /// `contact.info` frame the adapter composed out of three server routes; there is no
    /// adapter any more and no native reader for those three yet, so the page states the
    /// limit instead of leaving a heading with nothing under it.
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
            EmptyText.Text = Loc.Get("ContactInfoPage_NoInfo",
                "The name, the number and the picture are what this phone knows. WhatsApp's about, business and group details are not read by this client yet.");

            MembersList.ItemsSource = _members;
            HoursList.ItemsSource = _hours;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _contact = e.Parameter as Contact;
            if (_contact == null) return;

            // The avatar and the initials come from the contact: the page does not
            // rebuild them.
            DataContext = _contact;
            NameText.Text = _contact.Name;

            RequestInfo();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            HideFullScreen();
        }

        /// <summary>
        /// Fills what this phone already knows, and asks WhatsApp for the picture.
        ///
        /// There is nothing to await for the first half: the row the chat list drew is
        /// the source, and this is the same contact object. The picture is a query with
        /// an answer, and it is guarded like every other network call.
        /// </summary>
        private void RequestInfo()
        {
            var known = new ContactInfo();
            known.Name = _contact.Name;
            known.Number = DisplayNumber(_contact.Id);
            Apply(known);

            if (NativeConnection.Instance.IsConnected)
            {
#pragma warning disable 4014
                Guarded.RunGuardedAsync("ContactInfoPage/picture", RequestPictureAsync());
#pragma warning restore 4014
            }
        }

        /// <summary>
        /// The full picture, from WhatsApp itself: the query answers a URL, the URL answers
        /// the JPEG, and the cache keeps the bytes because the URL expires.
        ///
        /// No picture is not a failure: the page then shows the initials the bubble already
        /// draws, and nothing is logged as broken.
        /// </summary>
        private async System.Threading.Tasks.Task RequestPictureAsync()
        {
            try
            {
                byte[] jpeg = await NativeConnection.Instance.RequestAvatarAsync(_contact.Id, false);
                if (jpeg == null || jpeg.Length == 0) return;

                string data = Convert.ToBase64String(jpeg);

                if (_contact.AvatarData != data)
                {
                    _contact.AvatarData = data;
                    await _contact.LoadAvatarAsync();
                }

                AvatarCache.Remember(_contact.Id, data);
            }
            catch (Exception ex)
            {
                Diag.Failed("ContactInfoPage/picture", ex);
            }
        }

        /// <summary>
        /// The number the way a person reads one: the user part of the JID, without the
        /// device and without the server. A group has no number at all - its JID is not
        /// a phone - so it shows none rather than a made-up one.
        /// </summary>
        private static string DisplayNumber(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return null;
            if (jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) return null;

            int at = jid.IndexOf('@');
            string user = at < 0 ? jid : jid.Substring(0, at);

            int device = user.IndexOf(':');
            if (device >= 0) user = user.Substring(0, device);

            if (user.Length == 0) return null;
            return "+" + user;
        }

        /// <summary>
        /// Fills the page from what there is. Everything here is optional except the
        /// name: a heading with nothing under it is worse than one heading fewer, which
        /// is why each block shows itself only when it has something to say.
        /// </summary>
        private void Apply(ContactInfo info)
        {
            if (info != null)
            {
                if (!string.IsNullOrEmpty(info.Name)) NameText.Text = info.Name;

                if (!string.IsNullOrEmpty(info.Number))
                {
                    NumberText.Text = info.Number;
                    NumberLabel.Visibility = Visibility.Visible;
                    NumberText.Visibility = Visibility.Visible;
                }
                else
                {
                    NumberLabel.Visibility = Visibility.Collapsed;
                    NumberText.Visibility = Visibility.Collapsed;
                }

                if (!string.IsNullOrEmpty(info.About))
                {
                    AboutText.Text = info.About;
                    AboutLabel.Visibility = Visibility.Visible;
                    AboutText.Visibility = Visibility.Visible;
                }

                ApplyBusiness(info.Business);
                ApplyGroup(info.Group);
            }

            // And the limit, always: this is the sentence that says why the page is
            // short, and it is true whether or not anything above was filled.
            EmptyText.Visibility = Visibility.Visible;
        }

        private void ApplyBusiness(ContactBusiness business)
        {
            if (business == null)
            {
                BusinessPanel.Visibility = Visibility.Collapsed;
                return;
            }

            BusinessPanel.Visibility = Visibility.Visible;

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
        /// A label and its value appear only when the value is there: a heading
        /// with nothing under it is worse than one heading fewer.
        /// </summary>
        private static void ShowIf(TextBlock label, TextBlock value, string text)
        {
            bool has = !string.IsNullOrEmpty(text);
            label.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            value.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            value.Text = text ?? "";
        }

        private void ApplyGroup(ContactGroup group)
        {
            if (group == null)
            {
                GroupPanel.Visibility = Visibility.Collapsed;
                return;
            }

            GroupPanel.Visibility = Visibility.Visible;

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
        /// The full-screen picture is decoded at the screen size: the large circle
        /// is 160 px and enlarging it would leave it blurry.
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
