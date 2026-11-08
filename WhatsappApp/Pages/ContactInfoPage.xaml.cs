using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// The information of a person or a group: the large picture, the name and the
    /// number, and the details this client can currently read.
    /// </summary>
    public sealed partial class ContactInfoPage : PhoneApplicationPage
    {
        private const string NavigationParameterKey = "WhatsappApp.NavigationParameter";
        private Contact _contact;

        private readonly ObservableCollection<ContactMember> _members =
            new ObservableCollection<ContactMember>();
        private readonly ObservableCollection<ContactHours> _hours =
            new ObservableCollection<ContactHours>();

        public ContactInfoPage()
        {
            InitializeComponent();

            AutomationProperties.SetName(BackButton, Loc.Get("ContactInfoPage_BackTooltip", "Back"));
            AutomationProperties.SetName(BigAvatar, Loc.Get("ChatPage_ProfilePhotoTooltip", "Show the profile photo"));
            TitleText.Text = Loc.Get("ContactInfoPage_Title", "Contact info").ToUpper();
            CallButton.Content = Loc.Get("Call_Start", "Call");
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

            ApplyTheme();

            object parameter;
            if (PhoneApplicationService.Current.State.TryGetValue(NavigationParameterKey, out parameter))
            {
                PhoneApplicationService.Current.State.Remove(NavigationParameterKey);
                _contact = parameter as Contact;
            }
            if (_contact == null) return;

            DataContext = _contact;
            NameText.Text = _contact.Name;
            ApplyAvatar();

            CallButton.Visibility = IsGroup(_contact.Id) ? Visibility.Collapsed : Visibility.Visible;
            RequestInfo();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            HideFullScreen();
        }

        private void CallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_contact == null) return;

            Guarded.RunGuardedAsync("ContactInfoPage/start call", StartCallAndOpenAsync(_contact.Id));
        }

        private async Task StartCallAndOpenAsync(string jid)
        {
            await DataService.Instance.StartCallAsync(jid, false);

            CallSession call = DataService.Instance.Call;
            if (NavigationService == null) return;

            if (call != null && call.Occupied)
                NavigationService.Navigate(new Uri("/Pages/CallPage.xaml", UriKind.Relative));
            else if (NavigationService.CanGoBack)
                NavigationService.GoBack();
        }

        private void ApplyTheme()
        {
            ImageSource image = ThemeService.ImageFor(ThemeService.Current);
            PageWallpaper.Source = image;
            PageWallpaper.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyAvatar()
        {
            BigAvatarInitials.Text = _contact.Initials;
            BigAvatarCircle.Fill = InitialBrush(_contact.Initials);

            BitmapImage avatar = _contact.Avatar;
            if (avatar == null)
            {
                BigAvatarPicture.Visibility = Visibility.Collapsed;
                BigAvatarInitials.Visibility = Visibility.Visible;
            }
            else
            {
                BigAvatarPicture.Background = new ImageBrush
                {
                    ImageSource = avatar,
                    Stretch = Stretch.UniformToFill
                };
                BigAvatarPicture.Visibility = Visibility.Visible;
                BigAvatarInitials.Visibility = Visibility.Collapsed;
            }
        }

        private static Brush InitialBrush(string initials)
        {
            Color[] colors = new Color[]
            {
                Color.FromArgb(255, 82, 93, 138), Color.FromArgb(255, 30, 142, 62),
                Color.FromArgb(255, 227, 116, 0), Color.FromArgb(255, 229, 37, 44),
                Color.FromArgb(255, 13, 107, 153), Color.FromArgb(255, 123, 31, 162),
                Color.FromArgb(255, 194, 24, 91), Color.FromArgb(255, 56, 142, 60),
                Color.FromArgb(255, 25, 118, 210), Color.FromArgb(255, 245, 124, 0),
                Color.FromArgb(255, 69, 90, 100), Color.FromArgb(255, 175, 180, 35),
                Color.FromArgb(255, 0, 105, 92), Color.FromArgb(255, 93, 64, 55),
                Color.FromArgb(255, 81, 45, 168), Color.FromArgb(255, 48, 63, 159)
            };

            string value = initials ?? "?";
            int index = (value.GetHashCode() & 0x7FFFFFFF) % colors.Length;
            return new SolidColorBrush(colors[index]);
        }

        private static bool IsGroup(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return false;
            return jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
        }

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

        private async Task RequestPictureAsync()
        {
            try
            {
                AvatarAnswer picture = await NativeConnection.Instance.RequestAvatarAsync(_contact.Id, false);

                byte[] jpeg = picture == null ? null : picture.Jpeg;
                if (jpeg == null || jpeg.Length == 0) return;

                string data = Convert.ToBase64String(jpeg);
                if (_contact.AvatarData != data)
                {
                    _contact.AvatarData = data;
                    await _contact.LoadAvatarAsync();
                    ApplyAvatar();
                }

                AvatarCache.Remember(_contact.Id, data);
            }
            catch (Exception ex)
            {
                Diag.Failed("ContactInfoPage/picture", ex);
            }
        }

        private static string DisplayNumber(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return null;
            if (jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) return null;
            if (WhatsappApp.Protocol.WABinary.JidUtils.IsLid(jid)) return null;

            int at = jid.IndexOf('@');
            string user = at < 0 ? jid : jid.Substring(0, at);
            int device = user.IndexOf(':');
            if (device >= 0) user = user.Substring(0, device);
            if (user.Length == 0) return null;

            return "+" + user;
        }

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

        private const int ViewerDecodePixels = 720;

        private async void BigAvatar_Tapped(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_contact == null || string.IsNullOrEmpty(_contact.AvatarData)) return;

            try
            {
                BitmapImage bitmap = await ImageHelper.FromBase64Async(_contact.AvatarData, ViewerDecodePixels);
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

        private void ImageViewer_Tapped(object sender, MouseButtonEventArgs e)
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
            if (NavigationService != null && NavigationService.CanGoBack)
                NavigationService.GoBack();
        }
    }
}
