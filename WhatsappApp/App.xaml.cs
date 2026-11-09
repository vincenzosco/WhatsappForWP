using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;
using Windows.Storage.Streams;
using WhatsappApp.Controls;
using WhatsappApp.Pages;
using WhatsappApp.Services;

namespace WhatsappApp
{
    /// <summary>
    /// Owns the Windows Phone Silverlight application lifecycle and navigation frame.
    /// </summary>
    public sealed partial class App : Application
    {
        private const string HomePageUri = "/Pages/HomePage.xaml";
        private const string PairPageUri = "/Pages/PairPage.xaml";

        private PhoneApplicationFrame _rootFrame;
        private bool _phoneApplicationInitialized;
        private bool _initialNavigationPending = true;
        private bool _allowLaunchRoute;
        private bool _forceHomeForContinuation;
        private bool _servicesStarted;
        private bool _isForeground;
        private bool _leavingForeground;
        private int _lifecycleVersion;
        private DispatcherTimer _uiHeartbeat;
        private ShareOperation _shareOperation;
        private FileOpenPickerContinuationEventArgs _pickerContinuation;

        /// <summary>Initializes the Silverlight app, frame, and phone lifecycle events.</summary>
        public App()
        {
            InitializeComponent();
            InitializePhoneApplication();

            PhoneApplicationService service = PhoneApplicationService.Current;
            service.Launching += OnLaunching;
            service.Activated += OnActivated;
            service.Deactivated += OnDeactivated;
            service.Closing += OnClosing;
            service.ContractActivated += OnContractActivated;

            UnhandledException += OnUnhandled;
            TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        }

        private void InitializePhoneApplication()
        {
            if (_phoneApplicationInitialized) return;

            _rootFrame = new PhoneApplicationFrame();
            _rootFrame.UriMapper = new AppUriMapper(this);
            _rootFrame.NavigationFailed += RootFrame_NavigationFailed;
            _rootFrame.Navigated += CompleteInitializePhoneApplication;
            _rootFrame.Navigated += RootFrame_Navigated;
            _phoneApplicationInitialized = true;
        }

        private void CompleteInitializePhoneApplication(object sender, NavigationEventArgs e)
        {
            if (RootVisual != _rootFrame) RootVisual = _rootFrame;
            _rootFrame.Navigated -= CompleteInitializePhoneApplication;
        }

        private void RootFrame_Navigated(object sender, NavigationEventArgs e)
        {
            // A page has landed: the launch is over and the mappings stop asking
            // where it should have gone. The flag is cleared here and not at the
            // first mapping, because a launch can map more than once (the safety
            // net in `EnsureStartupPage` and the manifest's own navigation), and
            // the second raw one used to land the home page over the pair page.
            _initialNavigationPending = false;
            string route = e.Uri == null ? "unknown" : e.Uri.ToString();
            Diag.Ok("App/navigation landed on " + route);
            OpenPendingChat();
            DepositPickerContinuation();
        }

        private void OpenPendingChat()
        {
            HomePage home = _rootFrame.Content as HomePage;
            if (home != null) home.OpenPinnedChat();
        }

        /// <summary>Captures failures from native Silverlight navigation before they reach the shell.</summary>
        private void RootFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            string route = e.Uri == null ? "unknown route" : e.Uri.ToString();
            Diag.Failed("App/navigation " + route, e.Exception);
        }

        private Uri MapInitialUri(Uri uri)
        {
            if (uri == null) return null;

            // The pairing page gives its explicit handoff a marker so it cannot be
            // mistaken for the shell's initial route after the first frame landing.
            string paired = QueryValue(uri, "paired");
            if (string.Equals(paired, "1", StringComparison.Ordinal))
            {
                Diag.Ok("pair: the explicit HomePage handoff reached the URI mapper");
                return new Uri(HomePageUri, UriKind.Relative);
            }

            string tileArguments = QueryValue(uri, "tile");
            if (tileArguments == null) tileArguments = QueryValue(uri, "arguments");
            if (tileArguments == null) tileArguments = QueryValue(uri, "launchArgs");

            string tileChat = PinnedTile.ChatFromArguments(tileArguments);
            bool hasTileRoute = !string.IsNullOrEmpty(tileChat);
            if (hasTileRoute)
            {
                SessionService.PendingChatId = tileChat;
                SessionService.PendingChatName = PinnedTile.NameFromArguments(tileArguments);
                Diag.Ok("pinned tile: launching into " + tileChat);

                if (!_initialNavigationPending)
                {
                    return new Uri(HomePageUri, UriKind.Relative);
                }
            }

            if (!_initialNavigationPending) return uri;

            if (_shareOperation != null || _forceHomeForContinuation)
            {
                _forceHomeForContinuation = false;
                return new Uri(HomePageUri, UriKind.Relative);
            }

            if (!hasTileRoute && _allowLaunchRoute)
            {
                string route = SessionService.TakeRoute(DateTime.UtcNow);
                if (!string.IsNullOrEmpty(route))
                {
                    Diag.Ok("launch: the tile was showing " + route + ", the chats open");
                }
            }

            _allowLaunchRoute = false;

            StartTarget target = StartDecision.Decide(
                NativeCredentialStorage.Session().HasSession,
                SessionState.Expired);

            Diag.Ok("start: " + (target == StartTarget.Pair
                ? "no account stored, the pair page"
                : "the account is stored, the chats"));

            return new Uri(target == StartTarget.Pair ? PairPageUri : HomePageUri, UriKind.Relative);
        }

        private string QueryValue(Uri uri, string expectedKey)
        {
            if (uri == null || string.IsNullOrEmpty(uri.OriginalString)) return null;

            try
            {
                string source = uri.OriginalString;
                int queryStart = source.IndexOf('?');
                if (queryStart < 0) return null;

                string query = source.Substring(queryStart + 1);
                int fragmentStart = query.IndexOf('#');
                if (fragmentStart >= 0) query = query.Substring(0, fragmentStart);

                string[] fields = query.Split('&');
                for (int i = 0; i < fields.Length; i++)
                {
                    int separator = fields[i].IndexOf('=');
                    if (separator < 0) continue;

                    string key = Uri.UnescapeDataString(fields[i].Substring(0, separator));
                    if (!string.Equals(key, expectedKey, StringComparison.OrdinalIgnoreCase)) continue;

                    string value = fields[i].Substring(separator + 1).Replace("+", "%20");
                    return Uri.UnescapeDataString(value);
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("App/launch-query " + expectedKey, ex);
            }

            return null;
        }

        /// <summary>Maps the shell's initial URI to the pair, home, or shared-content route.</summary>
        private sealed class AppUriMapper : UriMapperBase
        {
            private readonly App _app;

            public AppUriMapper(App app)
            {
                _app = app;
            }

            public override Uri MapUri(Uri uri)
            {
                return _app.MapInitialUri(uri);
            }
        }

        /// <summary>
        /// Silverlight share activation arrives here as ShareLaunchingEventArgs. The phone's
        /// share contract also requires the initial URI to pass through the frame's URI mapper.
        /// </summary>
        private void OnLaunching(object sender, LaunchingEventArgs e)
        {
            ShareLaunchingEventArgs share = e as ShareLaunchingEventArgs;
            if (share != null)
            {
                if (share.ShareTargetActivatedEventArgs == null
                    || share.ShareTargetActivatedEventArgs.ShareOperation == null)
                {
                    Diag.Failed("App/share-launch", new InvalidOperationException("The share contract supplied no operation."));
                }
                else
                {
                    _shareOperation = share.ShareTargetActivatedEventArgs.ShareOperation;
                }
            }

            _allowLaunchRoute = _shareOperation == null;
            StartServicesOnce();
            _isForeground = true;
            NotificationService.SetForeground(true);

            if (_shareOperation != null)
            {
                _forceHomeForContinuation = true;
#pragma warning disable 4014
                Guarded.RunGuardedAsync("App/share", AcceptShareAsync(_shareOperation));
#pragma warning restore 4014
            }

            EnsureStartupPage();
        }

        /// <summary>Restores foreground state after the phone reactivates this app.</summary>
        private void OnActivated(object sender, ActivatedEventArgs e)
        {
            EnterForegroundFromActivation();
            EnsureStartupPage();
            OpenPendingChat();
            DepositPickerContinuation();
        }

        /// <summary>Captures a Windows Phone picker continuation from the Silverlight contract event.</summary>
        private void OnContractActivated(object sender, IActivatedEventArgs e)
        {
            FileOpenPickerContinuationEventArgs continuation = e as FileOpenPickerContinuationEventArgs;
            if (continuation == null) return;

            _pickerContinuation = continuation;
            _forceHomeForContinuation = true;
            EnterForegroundFromActivation();
            EnsureStartupPage();

            Deployment.Current.Dispatcher.BeginInvoke(delegate
            {
                DepositPickerContinuation();
            });
        }

        private void EnterForegroundFromActivation()
        {
            StartServicesOnce();
            NotificationService.SetForeground(true);

            if (_isForeground) return;

            _isForeground = true;
            _leavingForeground = false;
            _lifecycleVersion++;
            Diag.MarkAlive();
            BackgroundSync.Refresh();

#pragma warning disable 4014
            Guarded.RunGuardedAsync("App/OnActivated", ResumeAsync());
#pragma warning restore 4014
        }

        private void EnsureStartupPage()
        {
            if (_rootFrame.Content != null || _rootFrame.Source != null) return;
            _rootFrame.Navigate(new Uri(HomePageUri, UriKind.Relative));
        }

        /// <summary>Marks the app backgrounded and begins the bounded Silverlight deactivation work.</summary>
        private void OnDeactivated(object sender, DeactivatedEventArgs e)
        {
            LeaveForeground();
        }

        private void OnClosing(object sender, ClosingEventArgs e)
        {
            LeaveForeground();
        }

        private void LeaveForeground()
        {
            _isForeground = false;
            NotificationService.SetForeground(false);
            if (_leavingForeground) return;

            _leavingForeground = true;
            try
            {
                SessionService.Section = CurrentSection();
            }
            catch (Exception ex)
            {
                Diag.Failed("App/deactivate-section", ex);
            }

            int version = ++_lifecycleVersion;
#pragma warning disable 4014
            Guarded.RunGuardedAsync("App/deactivate", LeaveWatchingAsync(version));
#pragma warning restore 4014
        }

        /// <summary>
        /// Saves local state and announces unavailability while the Silverlight host is
        /// deactivating. Unlike the WinRT suspension event, this lifecycle has no deferral.
        /// </summary>
        private async Task LeaveWatchingAsync(int version)
        {
            try
            {
                await DataService.Instance.SaveSnapshotAsync();
            }
            catch (Exception ex)
            {
                Diag.Failed("App/deactivate-snapshot", ex);
            }

            try
            {
                if (NativeConnection.Instance.IsConnected)
                {
                    Task sending = NativeConnection.Instance.SendPresenceAsync("unavailable");
                    await Task.WhenAny(sending, Task.Delay(2000));
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("App/deactivate-presence", ex);
            }
            finally
            {
                if (version == _lifecycleVersion) Diag.EndRun();
            }
        }

        private static async Task ResumeAsync()
        {
            if (!await NativeConnection.Instance.CheckAsync()) return;
            await NativeConnection.Instance.SendPresenceAsync("available");
        }

        private static AppSection CurrentSection()
        {
            Application app = Application.Current;
            PhoneApplicationFrame frame = app == null ? null : app.RootVisual as PhoneApplicationFrame;
            if (frame == null) return AppSection.Chats;

            HomePage home = frame.Content as HomePage;
            return home == null ? AppSection.Chats : home.CurrentSection;
        }

        /// <summary>Starts process-wide services once, before the first page is shown.</summary>
        private void StartServicesOnce()
        {
            if (_servicesStarted) return;
            _servicesStarted = true;

            Loc.Prewarm();
            Accent.Apply();
            ApplyShellPalette();

#pragma warning disable 4014
            Guarded.RunGuardedAsync("App/tile theme", TileTheme.RefreshAsync());
#pragma warning restore 4014

            Diag.StartWatchdog();
            _uiHeartbeat = new DispatcherTimer();
            _uiHeartbeat.Interval = TimeSpan.FromSeconds(1);
            _uiHeartbeat.Tick += OnUiHeartbeat;
            _uiHeartbeat.Start();

#pragma warning disable 4014
            Guarded.RunGuardedAsync("Diag/StartRun", StartDiagnosticsAsync());
#pragma warning restore 4014

            BackNavigator.Start();
            DataService.Instance.Start();

            DataService.Instance.CallChanged -= OnCallChanged;
            DataService.Instance.CallChanged += OnCallChanged;

            MemoryWatcher.Instance.Start();

#pragma warning disable 4014
            Guarded.RunGuardedAsync("App/audio codec", AudioCodecCheck.RunAsync());
#pragma warning restore 4014

            CallAudioProbe.Run();
            CallAudioPipe.Start();
            NativeConnection.Instance.Start();
            BackgroundSync.Start();
        }

        private static void ApplyShellPalette()
        {
            Application app = Application.Current;
            if (app == null) return;

            SetBrushColor(app, "WhatsAppHeaderBrush", "WhatsAppHeaderColor");
            SetBrushColor(app, "WhatsAppAppBarBrush", "WhatsAppAppBarColor");
            SetBrushColor(app, "WhatsAppSentBubbleBrush", "WhatsAppSentBubbleColor");
        }

        private static void SetBrushColor(Application app, string brushKey, string colorKey)
        {
            if (!app.Resources.Contains(brushKey) || !app.Resources.Contains(colorKey)) return;

            object colorValue = app.Resources[colorKey];
            if (!(colorValue is Color)) return;

            SolidColorBrush brush = app.Resources[brushKey] as SolidColorBrush;
            if (brush != null) brush.Color = (Color)colorValue;
        }

        private void OnCallChanged(object sender, EventArgs e)
        {
            CallSession call = DataService.Instance.Call;
            if (call == null || call.Phase != CallPhase.Ringing) return;

            try
            {
                Deployment.Current.Dispatcher.BeginInvoke(new Action(NavigateToCallPage));
            }
            catch (Exception ex)
            {
                Diag.Failed("App/call-dispatch", ex);
            }
        }

        private void NavigateToCallPage()
        {
            CallSession call = DataService.Instance.Call;
            if (call == null || call.Phase != CallPhase.Ringing) return;

            PhoneApplicationFrame frame = _rootFrame;
            if (frame == null) frame = EnsurePhoneApplicationFrame();
            if (frame.Content is CallPage) return;

            if (!frame.Navigate(new Uri("/Pages/CallPage.xaml", UriKind.Relative)))
            {
                Diag.Failed("App/call-navigation", new InvalidOperationException("The call page route was refused."));
            }
        }

        private PhoneApplicationFrame EnsurePhoneApplicationFrame()
        {
            InitializePhoneApplication();
            return _rootFrame;
        }

        private void OnUiHeartbeat(object sender, EventArgs e)
        {
            Diag.UiTick();
        }

        private static async Task StartDiagnosticsAsync()
        {
            await Diag.StartRunAsync();
            CrashReport.Start();
        }

        private void DepositPickerContinuation()
        {
            FileOpenPickerContinuationEventArgs continuation = _pickerContinuation;
            if (continuation == null) return;

            _pickerContinuation = null;
            if (continuation.Files == null || continuation.Files.Count == 0) return;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("App/picker", DepositPickedFilesAsync(continuation.Files));
#pragma warning restore 4014
        }

        private async Task DepositPickedFilesAsync(IReadOnlyList<StorageFile> files)
        {
            for (int i = 0; i < files.Count; i++)
            {
                try
                {
                    await AttachmentInbox.PutAsync(files[i], null);
                }
                catch (Exception ex)
                {
                    Diag.Failed("App/picker", ex);
                }
            }
        }

        /// <summary>Reads a Windows Phone Silverlight share launch into the attachment inbox.</summary>
        private async Task AcceptShareAsync(ShareOperation operation)
        {
            try
            {
                operation.ReportStarted();

                DataPackageView data = operation.Data;
                if (data != null)
                {
                    if (data.Contains(StandardDataFormats.StorageItems))
                    {
                        IReadOnlyList<IStorageItem> items = await data.GetStorageItemsAsync();
                        for (int i = 0; i < items.Count; i++)
                        {
                            StorageFile file = items[i] as StorageFile;
                            if (file == null) continue;

                            await AttachmentInbox.PutAsync(file, null);
                            break;
                        }
                    }
                    else if (data.Contains(StandardDataFormats.Bitmap))
                    {
                        RandomAccessStreamReference reference = await data.GetBitmapAsync();
                        using (IRandomAccessStream stream = await reference.OpenReadAsync())
                        using (DataReader reader = new DataReader(stream))
                        {
                            uint size = (uint)stream.Size;
                            uint loaded = await reader.LoadAsync(size);
                            byte[] buffer = new byte[loaded];
                            reader.ReadBytes(buffer);
                            await AttachmentInbox.PutBytesAsync(buffer, "shared.png", "image/png", null);
                        }
                    }
                }

                operation.ReportDataRetrieved();
                operation.ReportCompleted();
            }
            catch (Exception ex)
            {
                Diag.Failed("App/share", ex);
                try
                {
                    operation.ReportError(Loc.Get("App_ShareFailed", "The shared file could not be read."));
                }
                catch (Exception reportEx)
                {
                    Diag.Failed("App/share-report", reportEx);
                }
            }
        }

        private void OnUnhandled(object sender, ApplicationUnhandledExceptionEventArgs e)
        {
            Diag.Failed("App/unhandled", e.ExceptionObject);
            Diag.Stack(e.ExceptionObject);
            Diag.Flush(true);
#if DEBUG
            e.Handled = true;
#endif
        }

        private void OnUnobservedTask(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Diag.Failed("App/unobserved-task", e.Exception);
            Diag.Stack(e.Exception);
            Diag.Flush(true);
            e.SetObserved();
        }

    }
}
