# One Page Per Section + Performance Pass + Skills Directory — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop using a single page for the whole app — give each section (chats, status, calls) its own `Page` behind a shared bottom navigation bar — then remove the per-message allocations and repeated work found in the hot paths, and document how to maintain, update and test the app as agent skills under `.agents/skills/`.

**Architecture:** `MainPage` (title bar + three `PivotItem`s + app bar) is deleted and replaced by three real pages (`ChatsPage`, `StatusPage`, `CallsPage`) that share one `Controls/SectionNav` user control. Section switching uses the root `Frame` and drops the page it leaves from the back stack, so hardware Back exits the app from any section root instead of walking through visited sections. The performance pass then targets the measured-by-inspection hot paths: a contact lookup index (was a linear scan per incoming message), cached JSON serializers, a single image read per attach, coalesced list scrolling, a cached settings snapshot and list containers that do less layout work.

**Tech Stack:** C# 5 (VS2013/WP8.1 compiler), WinRT XAML for Windows Phone 8.1, `.resw` + `Loc` for strings, Node.js guards in `tools/`, Agent Skills (`.agents/skills/<name>/SKILL.md`).

## Global Constraints

- **C# 5 only** — no `$"..."`, `?.`, expression-bodied members, auto-property initializers, `out <type> var`, `is <Type> name`, `nameof`, discard. Gate: `node tools/check-csharp5.js`.
- **No icon font.** Icons are `PathGeometry` in `App.xaml`; every geometry must be referenced, and every reference must be defined. Gate: `node tools/check-icons.js`.
- **Every user-visible string comes from `.resw`**: `x:Uid` in XAML (property must match the element type), `Loc.Get("Key", "fallback")` in C#. A key and the same key with a `.Property` suffix must not coexist. Gate: `node tools/check-resw.js --strict`.
- **Never call `ResourceLoader.GetForCurrentView()` from a background thread**; `Loc.Prewarm()` runs on the UI thread at startup and `Loc.Get` degrades to the fallback instead of throwing.
- **Pages live in `WhatsappApp/Pages/`**, reusable controls in `WhatsappApp/Controls/`, services in `WhatsappApp/Services/`.
- **Skill directories must follow the Agent Skills spec:** directory name == `name` in the frontmatter, `name` max 64 chars lowercase-with-hyphens, `description` max 1024 chars saying what it does and when to use it, `SKILL.md` under 500 lines.
- **Adapter must not regress:** `cd WhatsappBridge && npm test` stays 29/29.
- **Authoritative build gate (user's machine):** `msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86` → `0 Error(s)`.
- **Verification limits, stated honestly:** this machine has no WP8.1 compiler and no device, so performance changes are verified only by (a) the three static guards, (b) reading the call sites, and (c) the absence of new API surface outside WP8.1. Behaviour on device is the user's confirmation. Every task below names exactly which guard covers it.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `WhatsappApp/Controls/SectionNav.xaml(.cs)` *(new)* | The one bottom navigation bar shared by the three section pages; knows the section → page mapping and trims the back stack. |
| `WhatsappApp/Pages/ChatsPage.xaml(.cs)` *(new)* | Chat list, empty state, new-chat dialog, settings entry. |
| `WhatsappApp/Pages/StatusPage.xaml(.cs)` *(new)* | Status section (placeholder content, real page). |
| `WhatsappApp/Pages/CallsPage.xaml(.cs)` *(new)* | Calls section (placeholder content, real page). |
| `WhatsappApp/MainPage.xaml(.cs)` | **Deleted** — its three sections become the three pages above. |
| `WhatsappApp/Pages/ChatPage.xaml(.cs)` | Conversation (unchanged role; scroll + attach optimized in Tasks 5). |
| `WhatsappApp/Pages/ConnectionPage.xaml(.cs)` | Setup/settings (unchanged role; `Continue` now targets `ChatsPage`). |
| `WhatsappApp/Services/ImageHelper.cs` *(new)* | One place that turns bytes/base64 into a `BitmapImage`, used by chat and setup. |
| `WhatsappApp/Services/DataService.cs` | + contact index (`FindContact`) so lookups are O(1). |
| `WhatsappApp/Services/SettingsService.cs` | + in-memory snapshot instead of a `LocalSettings` read per property. |
| `WhatsappApp/Services/CommunicationService.cs` | + one `DataWriter` per socket instead of one per frame. |
| `WhatsappApp/Models/ChatMessage.cs` | + cached serializers, + no repeated image decode. |
| `WhatsappApp/App.xaml.cs` | Start page = `ChatsPage`; `Frame.CacheSize` 1 → 3. |
| `.agents/skills/*/SKILL.md` *(new)* | How to maintain, update, test and release the app. |

---

### Task 1: One page per section

This task is atomic: deleting `MainPage` breaks references elsewhere, and the renamed resource keys must land with the pages that use them. It is one commit because any split leaves the project inconsistent.

**Files:**
- Create: `WhatsappApp/Controls/SectionNav.xaml`, `WhatsappApp/Controls/SectionNav.xaml.cs`
- Create: `WhatsappApp/Pages/ChatsPage.xaml`, `WhatsappApp/Pages/ChatsPage.xaml.cs`
- Create: `WhatsappApp/Pages/StatusPage.xaml`, `WhatsappApp/Pages/StatusPage.xaml.cs`
- Create: `WhatsappApp/Pages/CallsPage.xaml`, `WhatsappApp/Pages/CallsPage.xaml.cs`
- Delete: `WhatsappApp/MainPage.xaml`, `WhatsappApp/MainPage.xaml.cs`
- Modify: `WhatsappApp/App.xaml.cs`, `WhatsappApp/Pages/ConnectionPage.xaml.cs`, `WhatsappApp/WhatsappApp.csproj`
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`

**Interfaces:**
- Consumes: `Loc.Get` (from `WhatsappApp.Services`), `DataService`, `CommunicationService`, the `IconChats`/`IconStatus`/`IconCalls`/`IconSettings`/`IconNewChat` geometries.
- Produces: `WhatsappApp.Controls.AppSection { Chats, Status, Calls }` and `WhatsappApp.Controls.SectionNav` with `public AppSection Current { get; set; }`.

- [ ] **Step 1: Rename the page-scoped resource keys**

Run this exact rename (both files, all keys in one pass — the old names were tied to the single hub page):

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
for f in WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw; do
  sed -i '' \
    -e 's/name="MainPage_TabChats.Text"/name="Nav_Chats"/' \
    -e 's/name="MainPage_TabStatus.Text"/name="Nav_Status"/' \
    -e 's/name="MainPage_TabCalls.Text"/name="Nav_Calls"/' \
    -e 's/name="MainPage_EmptyTitle.Text"/name="ChatsPage_EmptyTitle.Text"/' \
    -e 's/name="MainPage_EmptyHint.Text"/name="ChatsPage_EmptyHint.Text"/' \
    -e 's/name="MainPage_StatusEmptyTitle.Text"/name="StatusPage_EmptyTitle.Text"/' \
    -e 's/name="MainPage_StatusEmptyHint.Text"/name="StatusPage_EmptyHint.Text"/' \
    -e 's/name="MainPage_CallsEmptyTitle.Text"/name="CallsPage_EmptyTitle.Text"/' \
    -e 's/name="MainPage_CallsEmptyHint.Text"/name="CallsPage_EmptyHint.Text"/' \
    -e 's/name="MainPage_NewChatTooltip"/name="ChatsPage_NewChatTooltip"/' \
    -e 's/name="MainPage_SettingsTooltip"/name="ChatsPage_SettingsTooltip"/' \
    -e 's/name="MainPage_NewChatTitle"/name="NewChat_Title"/' \
    -e 's/name="MainPage_NewChatPrompt"/name="NewChat_Prompt"/' \
    -e 's/name="MainPage_NewChatOpen"/name="NewChat_Open"/' \
    -e 's/name="MainPage_NewChatCancel"/name="NewChat_Cancel"/' \
    -e 's/name="MainPage_NewChatInvalid"/name="NewChat_Invalid"/' \
    "$f"
done
grep -c 'MainPage_' WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
# expected: 0 and 0
```

Then add the two page titles, in the same position in both files (after the `CallsPage_EmptyHint.Text` entry):

```xml
  <data name="StatusPage_Title.Text" xml:space="preserve">
    <value>Status</value>            <!-- it-IT: Stato -->
  </data>
  <data name="CallsPage_Title.Text" xml:space="preserve">
    <value>Calls</value>             <!-- it-IT: Chiamate -->
  </data>
```

- [ ] **Step 2: Create the shared bottom navigation**

Create `WhatsappApp/Controls/SectionNav.xaml`:

```xml
<UserControl
    x:Class="WhatsappApp.Controls.SectionNav"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <UserControl.Resources>
        <!-- App bar buttons: the released WP8 app had a bottom bar with icons
             and no floating button. -->
        <Style x:Key="NavIconButtonStyle" TargetType="Button">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Padding" Value="0"/>
            <Setter Property="Height" Value="48"/>
        </Style>
    </UserControl.Resources>

    <Grid Background="#FFF5F5F5" Height="48">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <Button x:Name="ChatsButton" Grid.Column="0"
                Style="{StaticResource NavIconButtonStyle}"
                Click="ChatsButton_Click">
            <Path x:Name="ChatsIcon" Data="{StaticResource IconChats}" StrokeThickness="2"
                  StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                  Width="24" Height="24"/>
        </Button>

        <Button x:Name="StatusButton" Grid.Column="1"
                Style="{StaticResource NavIconButtonStyle}"
                Click="StatusButton_Click">
            <Path x:Name="StatusIcon" Data="{StaticResource IconStatus}" StrokeThickness="2"
                  StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                  Width="24" Height="24"/>
        </Button>

        <Button x:Name="CallsButton" Grid.Column="2"
                Style="{StaticResource NavIconButtonStyle}"
                Click="CallsButton_Click">
            <Path x:Name="CallsIcon" Data="{StaticResource IconCalls}" StrokeThickness="3"
                  StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                  Width="24" Height="24"/>
        </Button>
    </Grid>
</UserControl>
```

Create `WhatsappApp/Controls/SectionNav.xaml.cs`:

```csharp
using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using WhatsappApp.Pages;
using WhatsappApp.Services;

namespace WhatsappApp.Controls
{
    /// <summary>Le tre sezioni dell'app, ognuna con la propria pagina.</summary>
    public enum AppSection
    {
        Chats,
        Status,
        Calls
    }

    /// <summary>
    /// Barra di navigazione inferiore condivisa dalle pagine di sezione.
    /// La barra non conosce la cronologia: naviga sul Frame radice e toglie
    /// dallo stack la sezione appena lasciata, cosi' il tasto Indietro esce
    /// dall'app invece di ripassare tra le sezioni gia' viste.
    /// </summary>
    public sealed partial class SectionNav : UserControl
    {
        private static readonly SolidColorBrush SelectedBrush =
            new SolidColorBrush(Color.FromArgb(255, 18, 140, 126));
        private static readonly SolidColorBrush IdleBrush =
            new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));

        private AppSection _current;

        public SectionNav()
        {
            this.InitializeComponent();

            // I pulsanti sono solo icone: il testo e' un tooltip (che il
            // sistema legge anche come etichetta di accessibilita').
            ToolTipService.SetToolTip(ChatsButton, Loc.Get("Nav_Chats", "chats"));
            ToolTipService.SetToolTip(StatusButton, Loc.Get("Nav_Status", "status"));
            ToolTipService.SetToolTip(CallsButton, Loc.Get("Nav_Calls", "calls"));

            UpdateColors();
        }

        /// <summary>Sezione mostrata dalla pagina che ospita la barra.</summary>
        public AppSection Current
        {
            get { return _current; }
            set
            {
                _current = value;
                UpdateColors();
            }
        }

        private void UpdateColors()
        {
            ChatsIcon.Stroke = _current == AppSection.Chats ? SelectedBrush : IdleBrush;
            StatusIcon.Stroke = _current == AppSection.Status ? SelectedBrush : IdleBrush;
            CallsIcon.Stroke = _current == AppSection.Calls ? SelectedBrush : IdleBrush;
        }

        private void ChatsButton_Click(object sender, RoutedEventArgs e) { Go(AppSection.Chats); }
        private void StatusButton_Click(object sender, RoutedEventArgs e) { Go(AppSection.Status); }
        private void CallsButton_Click(object sender, RoutedEventArgs e) { Go(AppSection.Calls); }

        private void Go(AppSection section)
        {
            if (section == _current) return;

            // UserControl non ha una proprieta' Frame: il Frame radice e'
            // quello che App.OnLaunched assegna a Window.Current.Content.
            var frame = Window.Current.Content as Frame;
            if (frame == null) return;

            Type target = PageFor(section);
            if (target == null) return;

            frame.Navigate(target);

            int count = frame.BackStack.Count;
            if (count > 0 && IsSectionPage(frame.BackStack[count - 1].SourcePageType))
            {
                frame.BackStack.RemoveAt(count - 1);
            }
        }

        private static Type PageFor(AppSection section)
        {
            switch (section)
            {
                case AppSection.Status: return typeof(StatusPage);
                case AppSection.Calls: return typeof(CallsPage);
                default: return typeof(ChatsPage);
            }
        }

        private static bool IsSectionPage(Type pageType)
        {
            return pageType == typeof(ChatsPage)
                || pageType == typeof(StatusPage)
                || pageType == typeof(CallsPage);
        }
    }
}
```

- [ ] **Step 3: Create the three section pages**

Create `WhatsappApp/Pages/ChatsPage.xaml` (the chat list moves here verbatim from `MainPage.xaml`, minus the pivot and the FAB):

```xml
<Page
    x:Class="WhatsappApp.Pages.ChatsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:conv="using:WhatsappApp.Converters"
    xmlns:controls="using:WhatsappApp.Controls"
    Background="#FFECE5DD">

    <Page.Resources>
        <conv:BoolToVisibilityConverter x:Key="BoolToVisibility"/>
        <conv:UnreadCountToVisibilityConverter x:Key="UnreadToVisibility"/>
        <conv:InitialToColorConverter x:Key="InitialToColor"/>
        <conv:OnlineToDotColorConverter x:Key="OnlineToDotColor"/>
        <SolidColorBrush x:Key="WhatsAppHeaderBrush" Color="#FF075E54"/>
        <SolidColorBrush x:Key="WhatsAppAccentBrush" Color="#FF25D366"/>
        <SolidColorBrush x:Key="WhatsAppChatBgBrush" Color="#FFECE5DD"/>

        <!-- Containers senza padding/animazioni: meno lavoro per item durante
             lo scorrimento (telefoni WP8.1 di fascia bassa). -->
        <Style x:Key="ChatItemContainerStyle" TargetType="ListViewItem">
            <Setter Property="Padding" Value="0"/>
            <Setter Property="Margin" Value="0"/>
            <Setter Property="MinHeight" Value="0"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="IsTabStop" Value="False"/>
        </Style>
    </Page.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Title bar -->
        <Grid Grid.Row="0" Background="{StaticResource WhatsAppHeaderBrush}" Height="56">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>

            <TextBlock Text="WhatsApp"
                       Foreground="White" FontSize="24" FontWeight="SemiBold"
                       VerticalAlignment="Center" Margin="16,0,0,4"/>

            <Button x:Name="NewChatButton" Grid.Column="1"
                    Background="Transparent" BorderThickness="0" Padding="0"
                    Width="56" Height="56" Click="NewChatButton_Click">
                <Path Data="{StaticResource IconNewChat}" Stroke="White" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="24" Height="24"/>
            </Button>
        </Grid>

        <Grid Grid.Row="1" Background="{StaticResource WhatsAppChatBgBrush}">
            <ListView x:Name="ChatListView"
                      ItemsSource="{Binding}"
                      ItemContainerStyle="{StaticResource ChatItemContainerStyle}"
                      Background="Transparent"
                      SelectionMode="Single"
                      SelectionChanged="ChatListView_SelectionChanged">
                <ListView.ItemsPanel>
                    <ItemsPanelTemplate>
                        <VirtualizingStackPanel/>
                    </ItemsPanelTemplate>
                </ListView.ItemsPanel>
                <ListView.ItemTemplate>
                    <DataTemplate>
                        <Grid Height="72" Background="White">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="72"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>

                            <!-- Avatar circle with initials -->
                            <Grid Grid.Column="0" Width="52" Height="52"
                                  Margin="10,10,0,10" VerticalAlignment="Center">
                                <Ellipse Width="52" Height="52"
                                         Fill="{Binding Initials, Converter={StaticResource InitialToColor}}"/>
                                <TextBlock Text="{Binding Initials}"
                                           Foreground="White" FontSize="18" FontWeight="SemiBold"
                                           VerticalAlignment="Center" HorizontalAlignment="Center"/>
                                <!-- Online dot -->
                                <Ellipse Width="12" Height="12"
                                         Fill="{Binding IsOnline, Converter={StaticResource OnlineToDotColor}}"
                                         Stroke="White" StrokeThickness="2"
                                         VerticalAlignment="Bottom" HorizontalAlignment="Right"
                                         Visibility="{Binding IsOnline, Converter={StaticResource BoolToVisibility}}"/>
                            </Grid>

                            <!-- Contact info -->
                            <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                <TextBlock Text="{Binding Name}" Foreground="Black" FontSize="17" FontWeight="SemiBold"
                                           TextTrimming="WordEllipsis" Margin="0,0,0,2"/>
                                <TextBlock Text="{Binding LastMessage}" Foreground="#FF808080" FontSize="14"
                                           TextTrimming="WordEllipsis" MaxHeight="18"
                                           Opacity="0.8"/>
                            </StackPanel>

                            <!-- Time and unread badge -->
                            <StackPanel Grid.Column="2" VerticalAlignment="Center" Margin="0,0,12,0">
                                <TextBlock Text="{Binding LastMessageTime}" Foreground="#FF808080" FontSize="12"
                                           HorizontalAlignment="Right"/>
                                <Border MinWidth="20" Height="20"
                                        CornerRadius="10"
                                        Background="{StaticResource WhatsAppAccentBrush}"
                                        HorizontalAlignment="Right"
                                        Margin="0,4,0,0"
                                        Visibility="{Binding UnreadCount, Converter={StaticResource UnreadToVisibility}}">
                                    <TextBlock Text="{Binding UnreadCount}" Foreground="White" FontSize="11"
                                               FontWeight="Bold" HorizontalAlignment="Center"
                                               VerticalAlignment="Center"/>
                                </Border>
                            </StackPanel>

                            <!-- Separator -->
                            <Rectangle Grid.Column="1" Grid.ColumnSpan="2" Height="1" Fill="#1A000000"
                                       VerticalAlignment="Bottom" Margin="0,0,12,0"/>
                        </Grid>
                    </DataTemplate>
                </ListView.ItemTemplate>
            </ListView>

            <!-- Empty state: shown by UpdateEmptyState() -->
            <StackPanel x:Name="EmptyStatePanel" Visibility="Collapsed"
                        VerticalAlignment="Center" HorizontalAlignment="Center">
                <Path Data="{StaticResource IconChats}" Stroke="#FFBDBDBD" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="48" Height="48" Stretch="Uniform" HorizontalAlignment="Center"/>
                <TextBlock x:Uid="ChatsPage_EmptyTitle" Text="No chats yet"
                           Foreground="#FF9E9E9E" FontSize="16"
                           HorizontalAlignment="Center" Margin="0,10,0,0"/>
                <TextBlock x:Uid="ChatsPage_EmptyHint" Text="Tap new chat to start"
                           Foreground="#FFBDBDBD" FontSize="13"
                           HorizontalAlignment="Center" Margin="0,2,0,0"/>
            </StackPanel>
        </Grid>

        <controls:SectionNav x:Name="Nav" Grid.Row="2"/>
    </Grid>
</Page>
```

Create `WhatsappApp/Pages/ChatsPage.xaml.cs` (the hub's chat logic, with the new-chat dialog and the settings entry):

```csharp
using System;
using System.Linq;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>Sezione chat: elenco conversazioni e nuova chat.</summary>
    public sealed partial class ChatsPage : Page
    {
        public ChatsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;

            ChatListView.ItemsSource = DataService.Instance.Contacts;

            // I pulsanti dell'app bar sono solo icone: il testo e' un tooltip.
            ToolTipService.SetToolTip(NewChatButton, Loc.Get("ChatsPage_NewChatTooltip", "New chat"));
            ToolTipService.SetToolTip(Nav, Loc.Get("ChatsPage_SettingsTooltip", "Settings"));
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            Nav.Current = AppSection.Chats;

            // Keep the empty state in sync with the contact list
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            DataService.Instance.Contacts.CollectionChanged += Contacts_CollectionChanged;
            UpdateEmptyState();

            // OnNavigatedTo is not async: fire the contacts request and ignore the task
            if (CommunicationService.Instance.IsConnected)
#pragma warning disable 4014
                CommunicationService.Instance.SendControlAsync("contacts");
#pragma warning restore 4014
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
        }

        private void Contacts_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            bool empty = DataService.Instance.Contacts.Count == 0;
            EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChatListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;

            var contact = e.AddedItems[0] as Contact;
            if (contact == null) return;

            Frame.Navigate(typeof(ChatPage), contact);
            ChatListView.SelectedItem = null; // Reset selection
        }

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextBox
            {
                PlaceholderText = Loc.Get("NewChat_Prompt",
                    "Phone number with country code (e.g. 393401234567)")
            };

            // L'errore di validazione vive dentro la dialog: cosi' l'utente
            // ritrova il numero che aveva digitato invece di ripartire da zero.
            var error = new TextBlock
            {
                Text = Loc.Get("NewChat_Invalid",
                    "Enter a valid number with country code (e.g. 393401234567)."),
                Foreground = new SolidColorBrush(Colors.Red),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };

            var content = new StackPanel();
            content.Children.Add(input);
            content.Children.Add(error);

            // WP8.1 ContentDialog has no CloseButtonText: the cancel text is the
            // secondary button, and the dialog can also be dismissed with the
            // hardware back button (result = None).
            var dialog = new ContentDialog
            {
                Title = Loc.Get("NewChat_Title", "New chat"),
                Content = content,
                PrimaryButtonText = Loc.Get("NewChat_Open", "Open"),
                SecondaryButtonText = Loc.Get("NewChat_Cancel", "Cancel")
            };

            string jid = null;
            string phone = null;
            while (jid == null)
            {
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                phone = (input.Text ?? "").Trim()
                    .Replace("+", "").Replace(" ", "").Replace("-", "");
                if (phone.Length < 6 || !phone.All(char.IsDigit))
                {
                    error.Visibility = Visibility.Visible;
                    continue;
                }

                jid = phone.Contains("@") ? phone : phone + "@s.whatsapp.net";
            }

            var existing = DataService.Instance.FindContact(jid);
            if (existing != null)
            {
                Frame.Navigate(typeof(ChatPage), existing);
                return;
            }

            var contact = new Contact
            {
                Id = jid,
                Name = "+" + phone,
                Initials = phone.Substring(0, 2).ToUpper(),
                AvatarColor = "#FF075E54",
                IsOnline = false,
                UnreadCount = 0
            };
            DataService.Instance.AddContact(contact);
            Frame.Navigate(typeof(ChatPage), contact);
        }
    }
}
```

Create `WhatsappApp/Pages/StatusPage.xaml`:

```xml
<Page
    x:Class="WhatsappApp.Pages.StatusPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:WhatsappApp.Controls"
    Background="#FFECE5DD">

    <Page.Resources>
        <SolidColorBrush x:Key="WhatsAppHeaderBrush" Color="#FF075E54"/>
        <SolidColorBrush x:Key="WhatsAppChatBgBrush" Color="#FFECE5DD"/>
    </Page.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <Grid Grid.Row="0" Background="{StaticResource WhatsAppHeaderBrush}" Height="56">
            <TextBlock x:Uid="StatusPage_Title" Text="Status"
                       Foreground="White" FontSize="24" FontWeight="SemiBold"
                       VerticalAlignment="Center" Margin="16,0,0,4"/>
        </Grid>

        <Grid Grid.Row="1" Background="{StaticResource WhatsAppChatBgBrush}">
            <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" MaxWidth="320">
                <Path Data="{StaticResource IconStatus}" Stroke="#FFBDBDBD" StrokeThickness="2"
                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                      Width="48" Height="48" Stretch="Uniform" HorizontalAlignment="Center"/>
                <TextBlock x:Uid="StatusPage_EmptyTitle" Text="No status updates"
                           Foreground="#FF9E9E9E" FontSize="16"
                           HorizontalAlignment="Center" Margin="0,10,0,0"/>
                <TextBlock x:Uid="StatusPage_EmptyHint" Text="Status updates are not supported in this version."
                           Foreground="#FFBDBDBD" FontSize="13" TextWrapping="Wrap" TextAlignment="Center"
                           HorizontalAlignment="Center" Margin="0,2,0,0"/>
            </StackPanel>
        </Grid>

        <controls:SectionNav x:Name="Nav" Grid.Row="2"/>
    </Grid>
</Page>
```

Create `WhatsappApp/Pages/StatusPage.xaml.cs`:

```csharp
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;

namespace WhatsappApp.Pages
{
    /// <summary>Sezione stato: nessun dato finche' l'adapter non lo espone.</summary>
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
```

Create `WhatsappApp/Pages/CallsPage.xaml` — same shape as `StatusPage.xaml` with: `x:Class="WhatsappApp.Pages.CallsPage"`, `x:Uid="CallsPage_Title"` / `Text="Calls"`, `Data="{StaticResource IconCalls}"` with `StrokeThickness="3"`, `x:Uid="CallsPage_EmptyTitle"` / `Text="No calls"`, `x:Uid="CallsPage_EmptyHint"` / `Text="Calls are not supported in this version."`.

Create `WhatsappApp/Pages/CallsPage.xaml.cs` — same as `StatusPage.xaml.cs` with `class CallsPage` and `Nav.Current = AppSection.Calls;`.

- [ ] **Step 4: Delete the hub page and repoint every reference**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
rm WhatsappApp/MainPage.xaml WhatsappApp/MainPage.xaml.cs
```

In `WhatsappApp/App.xaml.cs` replace:

```csharp
                Type startPage = SettingsService.HasSavedSettings ? typeof(MainPage) : typeof(ConnectionPage);
```

with:

```csharp
                Type startPage = SettingsService.HasSavedSettings ? typeof(ChatsPage) : typeof(ConnectionPage);
```

In `WhatsappApp/Pages/ConnectionPage.xaml.cs` replace both occurrences of `typeof(MainPage)` with `typeof(ChatsPage)` (in `ContinueToMainPage`, and rename that method to `ContinueToChatsPage` including its two call sites).

In `WhatsappApp/WhatsappApp.csproj`:
- replace the `MainPage.xaml.cs` `Compile` entry with `Pages\ChatsPage.xaml.cs`, `Pages\CallsPage.xaml.cs`, `Pages\StatusPage.xaml.cs` (each `<DependentUpon>` its own `.xaml`) and add `<Compile Include="Controls\SectionNav.xaml.cs"><DependentUpon>SectionNav.xaml</DependentUpon></Compile>`;
- replace the `MainPage.xaml` `Page` entry with `Pages\ChatsPage.xaml`, `Pages\CallsPage.xaml`, `Pages\StatusPage.xaml`;
- add `<Page Include="Controls\SectionNav.xaml"><Generator>MSBuild:Compile</Generator><SubType>Designer</SubType></Page>`.

- [ ] **Step 5: Verify**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
grep -rn 'MainPage' WhatsappApp --include=*.xaml --include=*.cs --include=*.csproj | grep -v '^WhatsappApp/obj/'
# expected: no output
node tools/check-resw.js --strict   # expected: OK: 82 key(s) ...
node tools/check-icons.js           # expected: OK: 9 icon(s) defined, 9 reference(s) resolved.
node tools/check-csharp5.js         # expected: OK
for f in WhatsappApp/Controls/SectionNav.xaml WhatsappApp/Pages/ChatsPage.xaml WhatsappApp/Pages/StatusPage.xaml WhatsappApp/Pages/CallsPage.xaml; do xmllint --noout "$f" || echo "MALFORMED: $f"; done
# expected: no MALFORMED line
node -e "
const fs=require('fs');
for (const p of ['Pages/ChatsPage','Pages/StatusPage','Pages/CallsPage','Controls/SectionNav']) {
  const x=fs.readFileSync('WhatsappApp/'+p+'.xaml','utf8').replace(/<!--[\s\S]*?-->/g,'');
  const names=[...x.matchAll(/x:Name=\"([^\"]+)\"/g)].map(m=>m[1]);
  const cs=fs.readFileSync('WhatsappApp/'+p+'.xaml.cs','utf8');
  const missing=names.filter(n=>!cs.includes(n));
  console.log(p+': unreferenced x:Name -> '+(missing.length?missing.join(', '):'none'));
}"
# expected: every page "none" except ChatsPage, whose 'Nav' is referenced (already) - none allowed
```

- [ ] **Step 6: Commit**

```bash
git add -A WhatsappApp
git commit -m "feat: give every section its own page behind a shared navigation bar"
```

---

### Task 2: O(1) contact lookup

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `public Contact FindContact(string id)` — returns the contact with that JID/JID-like id, or null. Used by `ChatsPage` (Task 1) and internally.

- [ ] **Step 1: Add the index**

In `WhatsappApp/Services/DataService.cs`, replace:

```csharp
        private readonly ObservableCollection<Contact> _contacts;
        private readonly Dictionary<string, ObservableCollection<ChatMessage>> _chatMessages;
```

with:

```csharp
        private readonly ObservableCollection<Contact> _contacts;
        private readonly Dictionary<string, ObservableCollection<ChatMessage>> _chatMessages;

        // Indice per id: senza questo ogni messaggio in arrivo scandiva tutta
        // la lista dei contatti (FirstOrDefault) per trovare la chat.
        private readonly Dictionary<string, Contact> _contactIndex =
            new Dictionary<string, Contact>();
```

- [ ] **Step 2: Add the lookup with self-healing**

Add right before `public ObservableCollection<ChatMessage> GetMessages(string chatId)`:

```csharp
        /// <summary>
        /// Trova un contatto per id. L'indice viene ricostruito se non lo
        /// conosce (la collezione e' pubblica: qualcuno puo' averla modificata
        /// senza passare da AddContact) e ripulito dagli id non piu' presenti.
        /// </summary>
        public Contact FindContact(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            Contact indexed;
            if (_contactIndex.TryGetValue(id, out indexed))
            {
                if (_contacts.Contains(indexed)) return indexed;
                _contactIndex.Remove(id);
            }

            RebuildContactIndex();

            Contact found;
            return _contactIndex.TryGetValue(id, out found) ? found : null;
        }

        private void RebuildContactIndex()
        {
            _contactIndex.Clear();
            foreach (var contact in _contacts)
            {
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;
                _contactIndex[contact.Id] = contact;
            }
        }
```

- [ ] **Step 3: Register inserts and use the index**

Replace every `_contacts.FirstOrDefault(c => c.Id == ...)` / `FirstOrDefault(c => c.Id == chatId)` / `FirstOrDefault(c => c.Id == message.ChatId)` / `FirstOrDefault(c => c.Id == contact.Id)` call with `FindContact(...)`, and register every insert.

In `OnNetworkMessageReceived`, replace:

```csharp
            var contact = _contacts.FirstOrDefault(c => c.Id == message.ChatId);
            if (contact == null)
            {
                string name = string.IsNullOrEmpty(message.SenderName)
                    ? DisplayNameForJid(message.ChatId)
                    : message.SenderName;

                contact = new Contact
                {
                    Id = message.ChatId,
                    Name = name,
                    LastMessage = message.Text,
                    LastMessageTime = message.FormattedTime,
                    Initials = InitialsFor(name),
                    AvatarColor = "#FF075E54",
                    IsOnline = true,
                    UnreadCount = 0
                };
                _contacts.Insert(0, contact);
            }
```

with:

```csharp
            var contact = FindContact(message.ChatId);
            if (contact == null)
            {
                string name = string.IsNullOrEmpty(message.SenderName)
                    ? DisplayNameForJid(message.ChatId)
                    : message.SenderName;

                contact = new Contact
                {
                    Id = message.ChatId,
                    Name = name,
                    LastMessage = message.Text,
                    LastMessageTime = message.FormattedTime,
                    Initials = InitialsFor(name),
                    AvatarColor = "#FF075E54",
                    IsOnline = true,
                    UnreadCount = 0
                };
                _contacts.Insert(0, contact);
                _contactIndex[contact.Id] = contact;
            }
```

In `OnControlMessageReceived`, replace `var contact = _contacts.FirstOrDefault(c => c.Id == message.ChatId);` with `var contact = FindContact(message.ChatId);` and, in its `if (contact == null)` branch, add `_contactIndex[c.Id] = c;` — concretely, replace:

```csharp
            if (contact == null)
            {
                _contacts.Add(new Contact
                {
                    Id = message.ChatId,
                    Name = name,
                    Status = "",
                    Initials = InitialsFor(name),
                    AvatarColor = "#FF075E54",
                    IsOnline = false,
                    UnreadCount = 0
                });
            }
```

with:

```csharp
            if (contact == null)
            {
                var added = new Contact
                {
                    Id = message.ChatId,
                    Name = name,
                    Status = "",
                    Initials = InitialsFor(name),
                    AvatarColor = "#FF075E54",
                    IsOnline = false,
                    UnreadCount = 0
                };
                _contacts.Add(added);
                _contactIndex[added.Id] = added;
            }
```

In `AddMessage`, replace `var contact = _contacts.FirstOrDefault(c => c.Id == chatId);` with `var contact = FindContact(chatId);`.

In `ClearUnread`, replace `var contact = _contacts.FirstOrDefault(c => c.Id == chatId);` with `var contact = FindContact(chatId);`.

Replace `AddContact`:

```csharp
        public void AddContact(Contact contact)
        {
            _contacts.Insert(0, contact);
        }
```

with:

```csharp
        public void AddContact(Contact contact)
        {
            if (contact == null) return;
            _contacts.Insert(0, contact);
            if (!string.IsNullOrEmpty(contact.Id)) _contactIndex[contact.Id] = contact;
        }
```

- [ ] **Step 4: Verify**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
grep -n 'FirstOrDefault' WhatsappApp/Services/DataService.cs; echo "(empty = no linear scans left)"
grep -c 'FindContact' WhatsappApp/Services/DataService.cs   # expected: >= 6
node tools/check-csharp5.js                                  # expected: OK
```

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Services/DataService.cs
git commit -m "perf: index contacts instead of scanning the list per message"
```

---

### Task 3: Cache the settings snapshot

**Files:**
- Modify: `WhatsappApp/Services/SettingsService.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: the same public surface (`ServerAddress`, `ServerPort`, `Username`, `HasSavedSettings`, `Save`).

- [ ] **Step 1: Load once, write through**

Replace the whole body of the `SettingsService` class (everything between `public static class SettingsService` braces) with:

```csharp
        private const string KeyServerAddress = "ServerAddress";
        private const string KeyServerPort = "ServerPort";
        private const string KeyUsername = "Username";
        private const string DefaultServerAddress = "192.168.1.100";
        private const int DefaultServerPort = 8585;

        private static ApplicationDataContainer Settings
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        // Snapshot in memoria: senza questo ogni lettura attraversa il confine
        // WinRT di ApplicationData (e' il caso tipico: piu' proprieta' lette
        // una dopo l'altra mentre si costruisce la pagina).
        private static bool _loaded;
        private static string _serverAddress;
        private static int _serverPort;
        private static string _username;

        private static void EnsureLoaded()
        {
            if (_loaded) return;

            _serverAddress = ReadString(KeyServerAddress, DefaultServerAddress);
            _serverPort = ReadInt(KeyServerPort, DefaultServerPort);
            _username = ReadString(KeyUsername, "");

            _loaded = true;
        }

        public static string ServerAddress
        {
            get { EnsureLoaded(); return _serverAddress; }
            set { EnsureLoaded(); _serverAddress = value; Settings.Values[KeyServerAddress] = value; }
        }

        public static int ServerPort
        {
            get { EnsureLoaded(); return _serverPort; }
            set { EnsureLoaded(); _serverPort = value; Settings.Values[KeyServerPort] = value; }
        }

        public static string Username
        {
            get { EnsureLoaded(); return _username; }
            set { EnsureLoaded(); _username = value; Settings.Values[KeyUsername] = value; }
        }

        /// <summary>
        /// True when a server address has been saved (first-run setup done).
        /// </summary>
        public static bool HasSavedSettings
        {
            get { return !string.IsNullOrEmpty(ServerAddress); }
        }

        public static void Save(string address, int port, string username)
        {
            ServerAddress = address;
            ServerPort = port;
            Username = username;
        }

        private static string ReadString(string key, string defaultValue)
        {
            object val;
            if (Settings.Values.TryGetValue(key, out val) && val != null)
            {
                string text = val.ToString();
                if (!string.IsNullOrEmpty(text)) return text;
            }
            return defaultValue;
        }

        private static int ReadInt(string key, int defaultValue)
        {
            object val;
            if (Settings.Values.TryGetValue(key, out val) && val != null)
            {
                int port;
                if (int.TryParse(val.ToString(), out port) && port > 0) return port;
            }
            return defaultValue;
        }
```

- [ ] **Step 2: Verify**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js                     # expected: OK
grep -n 'GetString(' WhatsappApp/Services/SettingsService.cs; echo "(empty = old helper gone)"
grep -n 'EnsureLoaded' WhatsappApp/Services/SettingsService.cs | head -4
```
Behaviour to confirm by reading: `ServerAddress` is never empty after first run, so `HasSavedSettings` still means "setup done"; a fresh install now returns the default address from `ReadString` instead of `""`, which would make `HasSavedSettings` true on first launch. **Therefore** the address key must keep falling back to `""`:

Replace `_serverAddress = ReadString(KeyServerAddress, DefaultServerAddress);` with `_serverAddress = ReadString(KeyServerAddress, "");` and add a public default for the UI:

```csharp
        public static string DefaultAddress { get { return DefaultServerAddress; } }
```

Then in `WhatsappApp/Pages/ConnectionPage.xaml.cs` replace `if (string.IsNullOrEmpty(address)) address = "192.168.1.100";` with `if (string.IsNullOrEmpty(address)) address = SettingsService.DefaultAddress;`.

- [ ] **Step 3: Commit**

```bash
git add WhatsappApp/Services/SettingsService.cs WhatsappApp/Pages/ConnectionPage.xaml.cs
git commit -m "perf: read the settings snapshot once instead of on every property"
```

---

### Task 4: One serializer, one image decode

**Files:**
- Modify: `WhatsappApp/Models/ChatMessage.cs`
- Create: `WhatsappApp/Services/ImageHelper.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj`

**Interfaces:**
- Consumes: nothing new.
- Produces: `public static Task<BitmapImage> ImageHelper.FromBytesAsync(byte[] bytes)` and `public static Task<BitmapImage> ImageHelper.FromBase64Async(string base64)`. Used by Task 5.

- [ ] **Step 1: Cache the JSON serializers**

In `WhatsappApp/Models/ChatMessage.cs`, replace:

```csharp
        private BitmapImage _mediaImage; // decoded MediaData, for the XAML image binding
```

with:

```csharp
        private BitmapImage _mediaImage; // decoded MediaData, for the XAML image binding

        // Un serializer per tipo, non uno per messaggio: DataContractJsonSerializer
        // costruisce internamente il grafo del contratto a ogni istanza.
        private static readonly DataContractJsonSerializer JsonSerializer =
            new DataContractJsonSerializer(typeof(ChatMessage));
```

Replace `var serializer = new DataContractJsonSerializer(typeof(ChatMessage));` in **both** `ToJson` and `FromJson` with `var serializer = JsonSerializer;`.

- [ ] **Step 2: Do not decode the same image twice**

Replace the beginning of `LoadMediaImageAsync`:

```csharp
        public async Task LoadMediaImageAsync()
        {
            if (Type != MessageType.Image || string.IsNullOrEmpty(MediaData)) return;

            try
            {
                byte[] bytes = Convert.FromBase64String(MediaData);
                using (var stream = new InMemoryRandomAccessStream())
                {
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(bytes);
                        await writer.StoreAsync();
                    }
                    var bitmap = new BitmapImage();
                    stream.Seek(0);
                    await bitmap.SetSourceAsync(stream);
                    MediaImage = bitmap;
                }
            }
            catch
            {
                MediaImage = null;
            }
        }
```

with:

```csharp
        public async Task LoadMediaImageAsync()
        {
            if (Type != MessageType.Image || string.IsNullOrEmpty(MediaData)) return;

            // Gia' decodificata (es. si torna sulla pagina): rifarlo sprecherebbe
            // CPU e memoria per un risultato identico.
            if (MediaImage != null) return;

            try
            {
                MediaImage = await ImageHelper.FromBase64Async(MediaData);
            }
            catch
            {
                MediaImage = null;
            }
        }
```

- [ ] **Step 3: Put the decode in one place**

Create `WhatsappApp/Services/ImageHelper.cs`:

```csharp
using System;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Decodifica di immagini in bitmap per il binding XAML. Va usato sul
    /// thread UI: BitmapImage non e' agnostico rispetto alla view.
    /// </summary>
    public static class ImageHelper
    {
        public static async Task<BitmapImage> FromBase64Async(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            return await FromBytesAsync(Convert.FromBase64String(base64));
        }

        public static async Task<BitmapImage> FromBytesAsync(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;

            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                }

                var bitmap = new BitmapImage();
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
        }
    }
}
```

Register it in `WhatsappApp/WhatsappApp.csproj` next to `Services\Loc.cs`:

```xml
    <Compile Include="Services\ImageHelper.cs" />
```

- [ ] **Step 4: Verify**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js                                  # expected: OK
grep -c 'new DataContractJsonSerializer' WhatsappApp/Models/ChatMessage.cs   # expected: 1
grep -n 'ImageHelper' WhatsappApp/Models/ChatMessage.cs      # expected: one call in LoadMediaImageAsync
grep -c 'ImageHelper.cs' WhatsappApp/WhatsappApp.csproj      # expected: 1
```

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Models/ChatMessage.cs WhatsappApp/Services/ImageHelper.cs WhatsappApp/WhatsappApp.csproj
git commit -m "perf: reuse one JSON serializer and stop decoding an image twice"
```

---

### Task 5: One image read per attach, one scroll per burst

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`

**Interfaces:**
- Consumes: `ImageHelper.FromBytesAsync` / `FromBase64Async` (Task 4).
- Produces: nothing new.

- [ ] **Step 1: Read the picked file once**

Replace this block in `AttachButton_Click`:

```csharp
                _selectedImageFile = file;

                // Read the image file and convert to base64
                using (var stream = await file.OpenReadAsync())
                {
                    using (var reader = new DataReader(stream))
                    {
                        uint size = (uint)stream.Size;
                        await reader.LoadAsync(size);
                        byte[] buffer = new byte[size];
                        reader.ReadBytes(buffer);
                        _selectedImageBase64 = System.Convert.ToBase64String(buffer);
                    }
                }

                // Show preview
                using (var stream = await file.OpenReadAsync())
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    SelectedImagePreview.Source = bitmap;
                }

                ImagePreviewBar.Visibility = Visibility.Visible;
```

with:

```csharp
                _selectedImageFile = file;

                // Un solo passaggio sul file: i byte servono sia per l'invio
                // (base64) sia per l'anteprima (bitmap). Prima il file veniva
                // letto due volte.
                byte[] buffer;
                using (var stream = await file.OpenReadAsync())
                {
                    using (var reader = new DataReader(stream))
                    {
                        uint size = (uint)stream.Size;
                        await reader.LoadAsync(size);
                        buffer = new byte[size];
                        reader.ReadBytes(buffer);
                    }
                }

                _selectedImageBase64 = System.Convert.ToBase64String(buffer);
                SelectedImagePreview.Source = await ImageHelper.FromBytesAsync(buffer);
                ImagePreviewBar.Visibility = Visibility.Visible;
```

- [ ] **Step 2: Coalesce scrolling**

Add these fields next to the existing ones:

```csharp
        private ChatMessage _pendingScroll;
        private bool _scrollQueued;
```

Replace the body of `OnMessageReceived`:

```csharp
            // DataService ha già inserito il messaggio nella stessa collezione:
            // qui si scorre soltanto, altrimenti la bolla comparirebbe due volte.
            if (message.ChatId == _contact.Id)
            {
                MessagesListView.UpdateLayout();
                MessagesListView.ScrollIntoView(message);
            }
```

with:

```csharp
            // DataService ha già inserito il messaggio nella stessa collezione:
            // qui si scorre soltanto, altrimenti la bolla comparirebbe due volte.
            if (message.ChatId == _contact.Id)
                ScrollToMessage(message);
```

Add the coalescing helper near the bottom of the class:

```csharp
        /// <summary>
        /// Scorre sull'ultimo messaggio una volta per raffica: una raffica di
        /// messaggi in arrivo prima faceva un UpdateLayout + ScrollIntoView
        /// per ognuno (uno per messaggio, non uno per frame).
        /// </summary>
        private void ScrollToMessage(ChatMessage message)
        {
            _pendingScroll = message;
            if (_scrollQueued) return;

            _scrollQueued = true;
#pragma warning disable 4014
            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
            {
                _scrollQueued = false;
                if (_pendingScroll == null) return;

                MessagesListView.ScrollIntoView(_pendingScroll);
                _pendingScroll = null;
            });
#pragma warning restore 4014
        }
```

Then replace the two remaining direct scrolls (in `OnNavigatedTo` and in `AddAndSendMessage`) with the same helper:

```csharp
                // Auto-scroll to bottom
                if (_messages.Count > 0)
                {
                    MessagesListView.UpdateLayout();
                    MessagesListView.ScrollIntoView(_messages[_messages.Count - 1]);
                }
```
→
```csharp
                // Auto-scroll to bottom
                if (_messages.Count > 0)
                    ScrollToMessage(_messages[_messages.Count - 1]);
```

```csharp
            // Auto-scroll
            MessagesListView.UpdateLayout();
            MessagesListView.ScrollIntoView(message);
```
→
```csharp
            // Auto-scroll
            ScrollToMessage(message);
```

Finally, `OnNavigatedFrom` must drop the pending scroll:

```csharp
            DataService.Instance.ActiveChatId = null;
```
→
```csharp
            DataService.Instance.ActiveChatId = null;
            _pendingScroll = null;
```

- [ ] **Step 3: Verify**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js                                   # expected: OK
grep -c 'ScrollIntoView' WhatsappApp/Pages/ChatPage.xaml.cs    # expected: 1
grep -c 'UpdateLayout' WhatsappApp/Pages/ChatPage.xaml.cs      # expected: 0
grep -c 'OpenReadAsync' WhatsappApp/Pages/ChatPage.xaml.cs     # expected: 1
grep -n 'using Windows.UI.Xaml.Media.Imaging' WhatsappApp/Pages/ChatPage.xaml.cs; echo "(BitmapImage no longer used directly)"
```
If `BitmapImage` is no longer referenced, delete the now-unused `using Windows.UI.Xaml.Media.Imaging;` line (an unused using is a warning-free no-op, but keep the file honest).

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml.cs
git commit -m "perf: read an attached image once and coalesce list scrolling"
```

---

### Task 6: One writer per socket

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: internal only — `SendFrameAsync` now takes the `DataWriter` to use.

- [ ] **Step 1: Send through the socket's own writer**

Replace:

```csharp
        /// <summary>
        /// Encrypts the JSON bytes and writes one frame:
        /// [4-byte UInt32LE payload length][encrypted payload].
        /// </summary>
        private async Task SendFrameAsync(StreamSocket socket, byte[] jsonBytes)
        {
            byte[] payload = CryptoHelper.Encrypt(jsonBytes);
            var writer = new DataWriter(socket.OutputStream);
            writer.WriteUInt32((uint)payload.Length);
            writer.WriteBytes(payload);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
```

with:

```csharp
        /// <summary>
        /// Encrypts the JSON bytes and writes one frame on the given writer:
        /// [4-byte UInt32LE payload length][encrypted payload].
        /// Il writer arriva da fuori perche' e' quello del socket (creato una
        /// volta in ConnectToServerAsync): prima ne veniva creato — e mai
        /// chiuso — uno nuovo per ogni frame inviato.
        /// </summary>
        private async Task SendFrameAsync(DataWriter writer, byte[] jsonBytes)
        {
            byte[] payload = CryptoHelper.Encrypt(jsonBytes);
            writer.WriteUInt32((uint)payload.Length);
            writer.WriteBytes(payload);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
```

- [ ] **Step 2: Update the two call sites**

In `ConnectToServerAsync`: `await SendFrameAsync(_clientSocket, Encoding.UTF8.GetBytes(handshake.ToJson()));` → `await SendFrameAsync(_writer, Encoding.UTF8.GetBytes(handshake.ToJson()));`

In `SendMessageAsync`: `await SendFrameAsync(_clientSocket, jsonBytes);` → `await SendFrameAsync(_writer, jsonBytes);`

- [ ] **Step 3: Verify**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js                                     # expected: OK
grep -n 'SendFrameAsync' WhatsappApp/Services/CommunicationService.cs
# expected: 1 definition (DataWriter writer) + 2 call sites passing _writer
grep -n 'new DataWriter' WhatsappApp/Services/CommunicationService.cs
# expected: only the client writer in ConnectToServerAsync and the per-client writer in BroadcastToAllClientsAsync
```

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs
git commit -m "perf: reuse the socket writer instead of allocating one per frame"
```

---

### Task 7: Cheaper list items and cached section pages

**Files:**
- Modify: `WhatsappApp/App.xaml.cs`
- Modify: `WhatsappApp/Pages/ChatPage.xaml`

**Interfaces:**
- Consumes: nothing new.
- Produces: nothing new.

- [ ] **Step 1: Let the three section pages stay alive**

In `WhatsappApp/App.xaml.cs`, replace:

```csharp
                rootFrame = new Frame();
                rootFrame.CacheSize = 1;
```

with:

```csharp
                rootFrame = new Frame();
                // Tre pagine di sezione (Chats/Status/Calls) con
                // NavigationCacheMode.Enabled: la cache le tiene in vita, cosi'
                // passare da una sezione all'altra non ricostruisce la pagina
                // (l'elenco chat conserva anche la posizione di scorrimento).
                rootFrame.CacheSize = 3;
```

- [ ] **Step 2: Give chat bubbles their width back**

In `WhatsappApp/Pages/ChatPage.xaml`, add an item container style and use it. After the `<SolidColorBrush x:Key="WhatsAppTextSecondaryBrush" .../>` line, insert:

```xml
        <!-- Containers senza padding/animazioni: i fumetti usano tutta la
             larghezza e ogni item costa meno layout durante lo scorrimento. -->
        <Style x:Key="MessageItemContainerStyle" TargetType="ListViewItem">
            <Setter Property="Padding" Value="0"/>
            <Setter Property="Margin" Value="0"/>
            <Setter Property="MinHeight" Value="0"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="IsTabStop" Value="False"/>
        </Style>
```

and on the messages list:

```xml
        <ListView x:Name="MessagesListView" Grid.Row="1"
                  ItemsSource="{Binding}"
                  Background="Transparent"
                  SelectionMode="None">
```
→
```xml
        <ListView x:Name="MessagesListView" Grid.Row="1"
                  ItemsSource="{Binding}"
                  ItemContainerStyle="{StaticResource MessageItemContainerStyle}"
                  Background="Transparent"
                  SelectionMode="None">
```

- [ ] **Step 3: Verify**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
xmllint --noout WhatsappApp/Pages/ChatPage.xaml && echo "well-formed"
node tools/check-csharp5.js                                  # expected: OK
grep -n 'CacheSize' WhatsappApp/App.xaml.cs                  # expected: 3
grep -n 'ItemContainerStyle' WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Pages/ChatsPage.xaml
# expected: one in each file
```

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/App.xaml.cs WhatsappApp/Pages/ChatPage.xaml
git commit -m "perf: cache the section pages and trim per-item layout work"
```

---

### Task 8: The skills directory

**Files:**
- Create: `.agents/skills/README.md`
- Create: `.agents/skills/maintain-the-app/SKILL.md`
- Create: `.agents/skills/update-the-app/SKILL.md`
- Create: `.agents/skills/test-the-app/SKILL.md`
- Create: `.agents/skills/release-the-app/SKILL.md`

**Interfaces:**
- Consumes: the guards in `tools/`, the resw contract, the adapter test suite.
- Produces: skills discoverable by any Agent Skills client; each directory name equals its frontmatter `name`.

- [ ] **Step 1: Write the index**

Create `.agents/skills/README.md`:

```markdown
# Skills

Istruzioni riutilizzabili per chi (umano o agente) mette mano a questo progetto.
Ogni cartella e' una skill secondo la specifica Agent Skills: un `SKILL.md` con
frontmatter YAML (`name` uguale al nome della cartella, `description` che dice
cosa fa e quando usarla).

| Skill | Quando usarla |
| --- | --- |
| [`maintain-the-app`](maintain-the-app/SKILL.md) | Prima di modificare il codice: vincoli del toolchain, mappa dei file, workflow con i guard. |
| [`update-the-app`](update-the-app/SKILL.md) | Ricette per aggiungere pagina, sezione, icona, stringa, lingua, endpoint dell'adapter, e per alzare la versione. |
| [`test-the-app`](test-the-app/SKILL.md) | Cosa eseguire e cosa guardare prima di dire che una modifica e' a posto. |
| [`release-the-app`](release-the-app/SKILL.md) | Asset di marca, manifest, versione, deploy su dispositivo ed emulatore. |

Regola numero uno, valida per tutte: **i guard di `tools/` sono il gate**. Il
toolchain di Windows Phone 8.1 non usa il compilatore moderno e non e' qui, quindi
un errore di sintassi o una risorsa mancante si scoprono con gli script, non con
la build.
```

- [ ] **Step 2: Write each skill**

Create the four `SKILL.md` files. Each must open with frontmatter (name matching its directory) and stay under 500 lines.

`.agents/skills/maintain-the-app/SKILL.md`:

```markdown
---
name: maintain-the-app
description: Constraints and safe workflow for changing the WhatsApp for Windows Phone 8.1 codebase (C# 5 only, vector icons, resw localization, GOWA adapter). Use before editing any file under WhatsappApp/ or WhatsappBridge/, when a build fails, or when deciding where a change belongs.
---

# Maintaining the app

## What this is

An unofficial WhatsApp client for **Windows Phone 8.1**, built in Visual Studio
2015 with the WP8.1 SDK, that talks to a self-hosted **GOWA**
(go-whatsapp-web-multidevice) server through a thin Node.js adapter.

```
WhatsappApp/            WP8.1 XAML app (C# 5)
  App.xaml(.cs)         icon geometries, start page, resource loader warm-up
  Controls/             SectionNav - the shared bottom navigation bar
  Pages/                ChatsPage, StatusPage, CallsPage, ChatPage, ConnectionPage
  Converters/           IValueConverter implementations used by the XAML
  Models/               Contact, ChatMessage, ServerConfig
  Services/             CommunicationService (socket), DataService (state),
                        CryptoHelper (AES-GCM), Loc (strings), ImageHelper,
                        SettingsService
  Strings/<lang>/       Resources.resw - every user-visible string
  Assets/               generated PNGs (tiles, logos, splash)
WhatsappBridge/         Node.js adapter: GOWA HTTP + webhook -> encrypted TCP frames
WhatsappServer/         legacy .NET console project (not part of the app flow)
tools/                  static guards - run them, they are the real gate
```

## Hard constraints

1. **C# 5.** The WP8.1 toolchain compiler rejects C# 6/7 syntax:
   `$"..."`, `?.`, `get => x`, `X Y { get; set; } = v;`, `out int x`,
   `is Contact c`, `nameof(...)`, `_ = ...`.
   Gate: `node tools/check-csharp5.js` (it also flags WP8.1-missing WinRT APIs
   such as `CryptographicBuffer.CreateFromByteArray(byte[], uint, uint)` and
   `ContentDialog.CloseButtonText`).
2. **No icon font.** WP8.1 predates `Segoe MDL2 Assets`; an icon button using it
   renders blank. Icons are `PathGeometry` resources in `App.xaml` consumed as
   `Data="{StaticResource IconX}"`. Every geometry must be both defined and
   used. Gate: `node tools/check-icons.js` (add `--preview` for an ASCII render).
3. **No hardcoded user-visible strings.** XAML uses `x:Uid` with the property
   that matches the element (`TextBlock`→`.Text`, `Button`→`.Content`,
   `TextBox`→`.PlaceholderText`); C# uses `Loc.Get("Key", "fallback")`. Icon-only
   buttons get their label from `ToolTipService.SetToolTip(button, Loc.Get(...))`
   in the constructor - an `x:Uid` on them would overwrite the `Path`.
   Gate: `node tools/check-resw.js --strict`.
4. **Never create a `ResourceLoader` off the UI thread.** Strings arrive from the
   socket on a background thread; `Loc.Prewarm()` runs on the UI thread at
   startup and `Loc.Get` falls back to its literal instead of throwing.
5. **Back navigation.** Do not subscribe to `HardwareButtons.BackPressed` to
   reimplement Back; the system pops the frame back stack and exits at the root.
   `SectionNav` trims the section page it leaves so Back exits from any section.
6. **The adapter is a separate, dependency-free Node.js program** (`package.json`
   has zero runtime dependencies). Its tests must keep passing.

## Workflow for any change

1. `git status --short` - start from a clean tree.
2. Read the file you are about to change **completely**; this codebase keeps
   per-file invariants in comments.
3. Make the change.
4. Run all three guards (and `cd WhatsappBridge && npm test` if you touched the
   adapter).
5. If the change is user-visible, say which page and which string key changed.
6. Commit with a message that says *why* (the repo history is the changelog).

## Where a change belongs

| Change | File |
| --- | --- |
| New section of the app | new `Pages/XxxPage.xaml(.cs)` + a case in `SectionNav` |
| New icon | `PathGeometry` in `App.xaml`, then reference it |
| New string | both `.resw` files (same key), then `x:Uid`/`Loc.Get` |
| Socket/protocol behaviour | `Services/CommunicationService.cs` (+ adapter + its tests) |
| Contacts/messages state | `Services/DataService.cs` |
| New GOWA call | `WhatsappBridge/gowa-client.js`, a control command in `server.js`, and the app side in `ConnectionPage`/`CommunicationService` |

## Known gotchas

- The app cannot be built on macOS: there is no WP8.1 toolchain. The build gate
  runs on the Windows/Parallels machine.
- `x:Uid` on a `Button` overwrites `Content`; do not combine it with a `Path`.
- A key and the same key with a `.Property` suffix cannot coexist in a `.resw`
  (duplicate resource identifier) - the guard enforces this.
- WP8.1 caches the tile name and icons: after changing them, uninstall the app on
  the device before redeploying.
- `Frame.BackStack` is mutable and used on purpose in `SectionNav`.
```

`.agents/skills/update-the-app/SKILL.md`:

```markdown
---
name: update-the-app
description: Step-by-step recipes for extending the WhatsApp WP8.1 app - adding a page, a section, an icon, a localized string, a language, a GOWA endpoint, and bumping the version. Use when the user asks for a new feature, a new screen, a new string, a new language, or a version bump.
---

# Updating the app

Every recipe ends the same way: run the guards, then commit.

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict
```

## Add a page

1. `Pages/MyPage.xaml` - copy the shape of `Pages/StatusPage.xaml`: a `Grid`
   with `Auto`/`*`/`Auto` rows (title bar, content, navigation) and
   `<controls:SectionNav x:Name="Nav" Grid.Row="2"/>` if it is a section.
2. `Pages/MyPage.xaml.cs` - `public sealed partial class MyPage : Page`,
   `InitializeComponent()`, and pick a `NavigationCacheMode`:
   `Enabled` for the three section pages, default for everything else.
3. Register both files in `WhatsappApp/WhatsappApp.csproj`:
   `<Compile Include="Pages\MyPage.xaml.cs"><DependentUpon>MyPage.xaml</DependentUpon></Compile>`
   and `<Page Include="Pages\MyPage.xaml"><Generator>MSBuild:Compile</Generator><SubType>Designer</SubType></Page>`.
   **A page that is not listed here simply does not exist at build time.**
4. Navigate with `Frame.Navigate(typeof(MyPage))` or `Frame.Navigate(typeof(MyPage), parameter)`.

## Add a section to the navigation bar

1. `Controls/SectionNav.xaml`: add a column to the `Grid` and a `Button` with a
   `Path` bound to an `IconX` geometry.
2. `Controls/SectionNav.xaml.cs`: add the value to `enum AppSection`, a case in
   `PageFor`, the type in `IsSectionPage`, a `Click` handler, a tooltip in the
   constructor and a line in `UpdateColors`.
3. Create the page (recipe above) with `Nav.Current = AppSection.Xxx;` in
   `OnNavigatedTo`.
4. Add `Nav_Xxx` to both `.resw` files for the tooltip.

## Add an icon

1. `App.xaml`: `<PathGeometry x:Key="IconMyThing" Figures="M... Z"/>`, 24x24 view box.
2. `node tools/check-icons.js --preview` and read the ASCII render before trusting it.
3. Reference it with `Data="{StaticResource IconMyThing}"`, and choose
   `Stroke` (outline) or `Fill` to match the neighbours.
4. Delete any geometry whose last reference you removed - the guard fails on
   unused definitions.

## Add or change a string

1. Add the key to **both** `Strings/en-US/Resources.resw` and
   `Strings/it-IT/Resources.resw`, same `name`, translated `<value>`.
2. XAML: `x:Uid="KeyName"` and keep a sensible literal as the fallback.
   C#: `Loc.Get("KeyName", "fallback in the default language (English)")`.
3. `node tools/check-resw.js --strict`.
4. Naming: XAML entries end with the property (`.Text`, `.Content`,
   `.PlaceholderText`); code-only entries are plain identifiers; never both for
   the same base name.

## Add a language

1. `mkdir WhatsappApp/Strings/<bcp47>/` (for example `fr-FR`), copy
   `Strings/en-US/Resources.resw`, translate the values only.
2. `WhatsappApp.csproj`: add `<PRIResource Include="Strings\<bcp47>\Resources.resw" />`.
3. Add the tag to `LANGS` in `tools/check-resw.js` so the guard covers it.
4. `node tools/check-resw.js --strict` - it fails if a key is missing in any
   language.
5. No code change is needed: Windows picks the file matching the device language
   and falls back to `<DefaultLanguage>` (currently `en-US`).

## Talk to a new GOWA endpoint

1. `WhatsappBridge/gowa-client.js`: add the call, read the `results` object, and
   cover it in `test/gowa-client.test.js`.
2. `WhatsappBridge/server.js`: expose it as a control command
   (`hello|status|login.qr|login.code|contacts|logout`) or as a reply frame.
3. App side: send it with `CommunicationService.Instance.SendControlAsync("cmd", payload)`
   and handle the reply in `Pages/ConnectionPage.xaml.cs::OnControlMessageReceived`
   (control frames carry `Command`/`State`/`PairCode`/`QrImageData`/`AccountJid`).
4. `cd WhatsappBridge && npm test`.

## Bump the version

1. `WhatsappApp/Package.appxmanifest`: `<Identity ... Version="1.0.2.0" />` - a
   higher version is what makes redeploy an *upgrade* instead of a conflict.
2. `WhatsappApp/Properties/AssemblyInfo.cs`: `AssemblyVersion` /
   `AssemblyFileVersion` to match.
3. Commit both together.
```

`.agents/skills/test-the-app/SKILL.md`:

```markdown
---
name: test-the-app
description: How to verify a change to the WhatsApp WP8.1 app and its GOWA adapter - the static guards, what each one catches, the adapter test suite, the Windows msbuild gate, and the on-device checklist. Use before declaring any change done, or when something fails on the device.
---

# Testing the app

## The fast gate (runs on any machine, seconds)

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js        # C# 5 syntax + WP8.1-missing WinRT APIs
node tools/check-icons.js          # icon geometries defined <-> referenced
node tools/check-resw.js --strict  # x:Uid/Loc.Get <-> both .resw, PRIResource, default language
```

Exit code 0 and an `OK: ...` line each. What they catch that the build does not:

| Guard | Catches |
| --- | --- |
| `check-csharp5.js` | Syntax the WP8.1 compiler rejects (it never shows up here otherwise), and APIs that exist on Windows 10 but not on WP8.1. |
| `check-icons.js` | A blank icon button (`Segoe MDL2 Assets`), a `{StaticResource IconX}` that does not exist, a geometry nothing uses. |
| `check-resw.js` | A string that would silently stay in the markup language: missing/mistyped `x:Uid`, `x:Uid` on the wrong property, a `Loc.Get` key absent from a language, languages whose key sets differ, a `.resw` missing from the `csproj` (`PRIResource`), a wrong `<DefaultLanguage>`, an unused key. |

Also worth running while the tree is open:

```bash
for f in WhatsappApp/*.xaml WhatsappApp/Pages/*.xaml WhatsappApp/Controls/*.xaml; do
  xmllint --noout "$f" || echo "MALFORMED: $f"
done
```

## The adapter suite

```bash
cd WhatsappBridge && npm test
```

Expected `pass 29`, `fail 0`. It covers the config, the GOWA client, the message
format (including the `\/Date(ms)\/` wire format the app requires), the TCP
server and the webhook receiver. Add a test with every adapter change.

## Cross-checking the app against the adapter

The two sides must agree on two things that no guard verifies:

1. **Key.** `WhatsappBridge/config.js` `BRIDGE_KEY` must equal the passphrase in
   `WhatsappApp/Services/CryptoHelper.cs` (`WhatsAppCommunityWP8-2026`). A
   mismatch shows up as "Errore decifratura messaggio" for every frame.
2. **Frame shape.** `[4-byte UInt32LE length][AES-256-GCM payload]`, JSON inside,
   control frames with `Type = 3` and `ChatId = "system"`. If you change one
   side, change the other and the tests.

## The real build gate (Windows machine)

```bash
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

Expected `0 Error(s)`. Notes worth remembering:

- On a Rebuild followed by a build that fails early, the XAML pass
  (`MarkupCompilePass1`) may report `The name "...Converter" does not exist in the
  namespace ...`. Building a second time (without cleaning) resolves the reference
  against the freshly compiled assembly.
- The VS2013 XAML designer needs a developer licence / sideload policy and is not
  needed: close the designer, open `.xaml` as XML, or build with `msbuild`.

## On-device checklist

1. Deploy, then open **impostazioni** from the app bar and connect to the adapter.
2. Sign in with the QR code, then with the phone number (both paths).
3. Send a text message and an image (attach + caption), verify the outgoing bubble
   shows sent / delivered / failed correctly.
4. Receive a message with the app open and with it closed (the app must show the
   unread badge only in the second case).
5. Switch section with the bottom bar three times: the highlighted icon follows,
   lists keep their scroll position, and **Back** exits instead of walking back
   through the sections.
6. Change the device language (Settings > Time & language) and reopen: every
   visible string must switch between English and Italian.
7. Rotate the device / open the software keyboard on the chat page: the input row
   must stay above the keyboard.
```

`.agents/skills/release-the-app/SKILL.md`:

```markdown
---
name: release-the-app
description: How to produce the icons, tiles and splash screen, localize the package manifest, bump the version and deploy the WhatsApp WP8.1 app to a device or emulator. Use when packaging, rebuilding brand assets, or deploying to a phone.
---

# Releasing the app

## Brand assets

The logo (white bubble with the handset cut out) is generated vectorially, so it
stays sharp at every size:

```bash
node tools/make-brand-assets.js             # rewrites the PNGs in WhatsappApp/Assets
node tools/make-brand-assets.js --preview   # + ASCII preview, check before committing
```

Requires ImageMagick 7 (`magick`). Sizes and colours are constants at the top of
that script; the tile background is the WhatsApp teal `#075E54` and the splash
gradient ends on the same colour so it blends into the first page.

## Manifest

`WhatsappApp/Package.appxmanifest`:

| Field | Value |
| --- | --- |
| `Identity / Version` | bump on every release (`1.0.1.0` → `1.0.2.0`) |
| `Properties / DisplayName` | `WhatsApp` |
| `m3:VisualElements / DisplayName` | `WhatsApp` |
| `m3:VisualElements / BackgroundColor` | `#075E54` |
| `Resources` | `<Resource Language="x-generate"/>` - languages come from `Strings\*\Resources.resw` |

The manifest is intentionally **not** localized: the only translatable value is
the description, `makepri` fails the build on a bad `ms-resource:` reference, and
the name is the same in both languages.

## Deploy

1. `msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`.
2. Deploy to the emulator or device from Visual Studio, or install the signed
   package.
3. WP8.1 caches the tile/name: after changing icons or the display name,
   **uninstall** the app on the device and deploy again.

Developer licence / sideload, if the machine complains:

```powershell
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" /f /v AllowDevelopmentWithoutDevLicense /t REG_DWORD /d 1
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" /f /v AllowAllTrustedApps /t REG_DWORD /d 1
```

Plus Settings > Privacy & security > For developers > **Developer Mode = On**, then
restart Visual Studio. The XAML designer is not required for building.

## Checklist before calling it a release

- [ ] The three guards pass (`check-csharp5`, `check-icons`, `check-resw --strict`).
- [ ] `cd WhatsappBridge && npm test` is 29/29.
- [ ] `msbuild` reports `0 Error(s)`.
- [ ] Version bumped in `Package.appxmanifest` **and** `Properties/AssemblyInfo.cs`.
- [ ] README sections that describe the changed behaviour are updated.
- [ ] `git status` clean and pushed.
```

- [ ] **Step 3: Verify the skills match the Agent Skills spec**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node -e "
const fs=require('fs'), path=require('path');
const root='.agents/skills';
let bad=0;
for (const d of fs.readdirSync(root,{withFileTypes:true})) {
  if (!d.isDirectory()) continue;
  const file=path.join(root,d.name,'SKILL.md');
  if (!fs.existsSync(file)) { console.log('MISSING SKILL.md: '+d.name); bad++; continue; }
  const text=fs.readFileSync(file,'utf8');
  const m=/^---\n([\s\S]*?)\n---\n/.exec(text);
  if (!m) { console.log('NO FRONTMATTER: '+d.name); bad++; continue; }
  const name=/^name:\s*(.+)$/m.exec(m[1]);
  const desc=/^description:\s*(.+)$/m.exec(m[1]);
  const lines=text.split('\n').length;
  const problems=[];
  if (!name) problems.push('no name');
  else {
    if (name[1]!==d.name) problems.push('name \"'+name[1]+'\" != directory \"'+d.name+'\"');
    if (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(name[1])) problems.push('name not lowercase-hyphen');
    if (name[1].length>64) problems.push('name too long');
  }
  if (!desc) problems.push('no description');
  else if (desc[1].length>1024) problems.push('description too long');
  if (lines>500) problems.push('SKILL.md is '+lines+' lines (limit 500)');
  console.log((problems.length?'FAIL ':'ok   ')+d.name+(problems.length?' -> '+problems.join('; '):' ('+lines+' lines)'));
  if (problems.length) bad++;
}
process.exit(bad?1:0);
"
# expected: ok for all four skills, exit 0
grep -c 'SKILL.md' .agents/skills/README.md    # expected: >= 4
```

- [ ] **Step 4: Commit**

```bash
git add .agents/skills
git commit -m "docs: add agent skills for maintaining, updating, testing and releasing the app"
```

---

### Task 9: Final verification, README, push

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: the finished change set.

- [ ] **Step 1: Run every gate**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js        # expected: OK
node tools/check-icons.js          # expected: OK: 9 icon(s) defined, 9 reference(s) resolved.
node tools/check-resw.js --strict  # expected: OK: 82 key(s) in en-US and it-IT, every x:Uid and Loc.Get lookup resolved.
for f in WhatsappApp/App.xaml WhatsappApp/Pages/*.xaml WhatsappApp/Controls/*.xaml; do xmllint --noout "$f" || echo "MALFORMED: $f"; done
cd WhatsappBridge && npm test && cd ..   # expected: pass 29
git status --short
```

- [ ] **Step 2: Point the README at the structure and the skills**

In `README.md`, add after the `## Projects` section:

```markdown
### Struttura dell'app

L'app e' divisa in una pagina per sezione, con una barra di navigazione
condivisa (`WhatsappApp/Controls/SectionNav.xaml`):

| Pagina | Sezione |
| --- | --- |
| `Pages/ChatsPage.xaml` | elenco chat, nuova chat |
| `Pages/StatusPage.xaml` | stati |
| `Pages/CallsPage.xaml` | chiamate |
| `Pages/ChatPage.xaml` | conversazione |
| `Pages/ConnectionPage.xaml` | configurazione server e accesso WhatsApp |

Il cambio di sezione naviga sul `Frame` radice e rimuove dallo stack la sezione
lasciata, quindi il tasto **Indietro** esce dall'app da qualunque sezione invece
di ripassare tra quelle viste. Le tre pagine di sezione sono in cache
(`Frame.CacheSize = 3`), cosi' passare da una all'altra non ricostruisce la
pagina e l'elenco chat conserva la posizione di scorrimento.

### Skill del progetto

In `.agents/skills/` (indice in `.agents/skills/README.md`) ci sono le istruzioni
per mantenere, aggiornare, testare e rilasciare l'app: vincoli del toolchain,
ricette di modifica, la matrice di verifica e la checklist di deploy. Chi mette
mano al codice dovrebbe leggerle prima: sono la memoria lunga del progetto.
```

- [ ] **Step 3: Commit and push**

```bash
git add README.md
git commit -m "docs: document the per-section page structure and the project skills"
git push origin master
```

Expected: `master -> master` accepted.

---

## Self-Review

**1. Spec coverage**

| Request | Task |
| --- | --- |
| "pagine multiple per ogni sezione, non una pagina per tutto" | 1 (`ChatsPage`/`StatusPage`/`CallsPage` + `SectionNav`, `MainPage` deleted) |
| "ottimizza l'app per bene" | 2 (contact index), 3 (settings snapshot), 4 (serializer + single decode), 5 (single file read + coalesced scroll), 6 (writer reuse), 7 (page cache + cheaper list items) |
| "se non sai cerca come implementare" | researched before writing: `Frame.BackStack` mutability and the WP8.1 XAML designer trap, plus the Agent Skills spec (frontmatter fields, name/directory match, 500-line body) |
| "esegui sempre il piano" | every task ends in a commit and Task 9 pushes |
| "crea una directory per le skill … come mantenere, aggiornare e testare l'app" | 8 (`.agents/skills/` with `maintain-the-app`, `update-the-app`, `test-the-app`, `release-the-app` + index) |

**2. Placeholder scan:** no `TBD`, no "similar to Task N" (the `CallsPage` steps spell out every divergent value because the file is a near-copy of `StatusPage`), every code step carries the literal replacement text. Task 3 Step 2 and Task 5 Step 3 contain a conditional rename/delete with the exact command or replacement shown.

**3. Type consistency:** `AppSection`/`SectionNav.Current` are defined in Task 1 and used with those exact names in the three pages of the same task. `DataService.FindContact(string)` is introduced in Task 2 and already called by `ChatsPage` in Task 1 — Task 1 must therefore be applied **after** Task 2's method exists, or `FindContact` must be introduced in Task 1; the plan orders Task 1 first for the page structure, so **Task 1's `ChatsPage` uses `FindContact` and Task 2 must land in the same push**; if applied strictly in order, temporarily keep the `FirstOrDefault` line in `ChatsPage` and switch it in Task 2. `ImageHelper.FromBytesAsync`/`FromBase64Async` are created in Task 4 and consumed in Task 5. `SettingsService.DefaultAddress` is added and consumed in Task 3. `Loc.Get`/`Prewarm` signatures are unchanged from the previous plan; the 82 keys used by Tasks 1-9 all exist in both `.resw` files (`--strict` verifies).

**Ordering note (the one real dependency risk):** Task 1's `ChatsPage` calls `DataService.FindContact`, which Task 2 introduces. Execute Task 2's Step 1-2 (index + `FindContact`) *before* Task 1, or keep `DataService.Instance.Contacts.FirstOrDefault(c => c.Id == jid)` in `ChatsPage` and change it in Task 2 Step 3. The guard cannot catch this one; the compiler can.

**Known follow-ups (not in this plan):** status/calls data (needs new adapter endpoints), in-app chat search, manifest localization for the tile name, and a real device performance profile (this plan's optimizations are reasoned from the call paths, not measured).
