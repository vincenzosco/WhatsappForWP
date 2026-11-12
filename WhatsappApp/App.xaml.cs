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
    /// The application object: the piece that exists before any page and outlives all of them.
    ///
    /// App.xaml.cs is the code half of App.xaml (the `x:Class` there points here). It does
    /// four jobs:
    ///
    /// - owns the page frame (`PhoneApplicationFrame`), which is the thing that shows pages
    ///   and keeps the Back stack;
    /// - decides which page the app opens on, through a URI mapper that can rewrite a route
    ///   before the frame sees it;
    /// - listens to the phone's lifecycle events (launch, activate, deactivate, close) and
    ///   saves or resumes the right work;
    /// - starts the process-wide services once, and reports unhandled failures to the log.
    /// </summary>
    public sealed partial class App : Application
    {
        // The two routes this class can choose between at startup.
        private const string HomePageUri = "/Pages/HomePage.xaml";
        private const string PairPageUri = "/Pages/PairPage.xaml";

        // The frame that shows pages. Created in InitializePhoneApplication.
        private PhoneApplicationFrame _rootFrame;

        // Guards that setup runs once, even if the lifecycle calls us twice.
        private bool _phoneApplicationInitialized;

        // True until the first page has landed: the URI mapper only redirects a launch, never
        // a navigation the user started later.
        private bool _initialNavigationPending = true;

        // Set at launch when the app may restore the section the user was reading.
        private bool _allowLaunchRoute;

        // Set when a share or a file picker brought us back: those continuations must open on
        // the home page, not on whatever page was in front.
        private bool _forceHomeForContinuation;

        // Services start once per process, not once per activation.
        private bool _servicesStarted;

        // Whether the app is currently in front, and whether the leave work already started.
        private bool _isForeground;
        private bool _leavingForeground;

        // A counter that tells a slow "leaving" task whether it is still the current one.
        private int _lifecycleVersion;

        // A one second timer that proves the UI thread is still able to run code.
        private DispatcherTimer _uiHeartbeat;

        // Work handed to us by the phone and finished on the pages: a share, a file picker.
        private ShareOperation _shareOperation;
        private FileOpenPickerContinuationEventArgs _pickerContinuation;

        /// <summary>
        /// Builds the app object: loads App.xaml, creates the frame, and subscribes to the
        /// phone's lifecycle and failure events.
        /// </summary>
        public App()
        {
            // Loads App.xaml, including the resource dictionary the pages read.
            InitializeComponent();

            // Creates the frame before the lifecycle can ask for a page.
            InitializePhoneApplication();

            // These events are how the phone tells the app what is happening to it.
            PhoneApplicationService service = PhoneApplicationService.Current;
            service.Launching += OnLaunching;
            service.Activated += OnActivated;
            service.Deactivated += OnDeactivated;
            service.Closing += OnClosing;
            service.ContractActivated += OnContractActivated;

            // Last chance to log a failure that nothing else caught.
            UnhandledException += OnUnhandled;
            TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        }

        /// <summary>
        /// Creates the frame once and wires its events. Safe to call twice.
        /// </summary>
        private void InitializePhoneApplication()
        {
            if (_phoneApplicationInitialized) return;

            _rootFrame = new PhoneApplicationFrame();

            // The URI mapper sees every route before the frame navigates to it. MapInitialUri
            // is where "open on the pair page or on the chats" is decided.
            _rootFrame.UriMapper = new AppUriMapper(this);

            // NavigationFailed fires when the frame cannot open a route: log it rather than
            // letting the shell kill the app.
            _rootFrame.NavigationFailed += RootFrame_NavigationFailed;

            // Navigated fires after a page has landed. Two handlers: one that finishes
            // startup and removes itself, and one that runs on every navigation.
            _rootFrame.Navigated += CompleteInitializePhoneApplication;
            _rootFrame.Navigated += RootFrame_Navigated;
            _phoneApplicationInitialized = true;
        }

        /// <summary>
        /// The first navigation has completed: the frame becomes the root visual, which is the
        /// element the phone draws, and this handler unhooks itself because its job is done.
        /// </summary>
        private void CompleteInitializePhoneApplication(object sender, NavigationEventArgs e)
        {
            if (RootVisual != _rootFrame) RootVisual = _rootFrame;
            _rootFrame.Navigated -= CompleteInitializePhoneApplication;
        }

        /// <summary>
        /// Runs every time a page lands. Clears the launch flag, logs the route, and delivers
        /// work that was waiting for a page to exist.
        /// </summary>
        private void RootFrame_Navigated(object sender, NavigationEventArgs e)
        {
            // The launch is over: from here on the mapper must not redirect anything. The flag
            // is cleared here and not at the first mapping, because a launch can be mapped more
            // than once.
            _initialNavigationPending = false;
            string route = e.Uri == null ? "unknown" : e.Uri.ToString();
            Diag.Ok("App/navigation landed on " + route);
            OpenPendingChat();
            DepositPickerContinuation();
        }

        /// <summary>
        /// If a pinned tile asked for a conversation, the home page opens it now that the
        /// home page exists.
        /// </summary>
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

        /// <summary>
        /// Rewrites a route before the frame opens it. This is the startup decision point.
        ///
        /// The frame asks this method for every navigation. The cases, in order:
        ///
        /// - `paired=1`: the pair page has just linked the account and asks for the chats;
        /// - a pinned tile: the chat named in the tile is remembered for the home page;
        /// - a share or a picker continuation: the home page, whatever was in front;
        /// - otherwise, at launch only: pair page when no account is stored, chats when one is.
        /// </summary>
        private Uri MapInitialUri(Uri uri)
        {
            if (uri == null) return null;

            // The pairing page gives its explicit handoff a marker so it cannot be mistaken
            // for the shell's initial route after the first frame landing.
            string paired = QueryValue(uri, "paired");
            if (string.Equals(paired, "1", StringComparison.Ordinal))
            {
                Diag.Ok("pair: the explicit HomePage handoff reached the URI mapper");
                return new Uri(HomePageUri, UriKind.Relative);
            }

            // A tile passes its chat in one of three parameter names, depending on how the
            // tile was created.
            string tileArguments = QueryValue(uri, "tile");
            if (tileArguments == null) tileArguments = QueryValue(uri, "arguments");
            if (tileArguments == null) tileArguments = QueryValue(uri, "launchArgs");

            string tileChat = PinnedTile.ChatFromArguments(tileArguments);
            bool hasTileRoute = !string.IsNullOrEmpty(tileChat);
            if (hasTileRoute)
            {
                // Hand the chat to the home page, which consumes it in OnNavigatedTo.
                SessionService.PendingChatId = tileChat;
                SessionService.PendingChatName = PinnedTile.NameFromArguments(tileArguments);
                Diag.Ok("pinned tile: launching into " + tileChat);

                // Not a launch: the app is already showing something, so only the home page
                // matters here.
                if (!_initialNavigationPending)
                {
                    return new Uri(HomePageUri, UriKind.Relative);
                }
            }

            // Not a launch: leave the route the user asked for untouched.
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

            // The one question of a cold start: is there an account on this phone? No account
            // means the pair page; an account, or an expired session, means the chats.
            StartTarget target = StartDecision.Decide(
                NativeCredentialStorage.Session().HasSession,
                SessionState.Expired);

            Diag.Ok("start: " + (target == StartTarget.Pair
                ? "no account stored, the pair page"
                : "the account is stored, the chats"));

            return new Uri(target == StartTarget.Pair ? PairPageUri : HomePageUri, UriKind.Relative);
        }

        /// <summary>
        /// Reads one value out of a URI's query string, for example `1` out of
        /// `/Pages/HomePage.xaml?paired=1`.
        ///
        /// It is written by hand because the phone's launch URIs are not always complete URLs:
        /// the string is split on `?`, then on `&amp;`, then on `=`, and each piece is decoded.
        /// A `+` means a space in a query string, so it becomes `%20` before decoding.
        /// </summary>
        private string QueryValue(Uri uri, string expectedKey)
        {
            if (uri == null || string.IsNullOrEmpty(uri.OriginalString)) return null;

            try
            {
                string source = uri.OriginalString;
                int queryStart = source.IndexOf('?');
                if (queryStart < 0) return null;

                // Everything after the `?`, minus anything after a `#` fragment.
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

        /// <summary>
        /// The mapper the frame calls. It is a tiny adapter: the frame only knows
        /// `UriMapperBase`, so this class forwards to the app's own method.
        /// </summary>
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
        /// The app has just started. Picks up a share if the user launched by sharing
        /// something into WhatsApp, starts the services, and makes sure a page is on its way.
        /// </summary>
        private void OnLaunching(object sender, LaunchingEventArgs e)
        {
            // A share launch arrives as a different kind of event argument, so it is checked
            // with `as`: null means "an ordinary launch".
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

            // A share must open on the home page, so the saved launch route is not restored.
            _allowLaunchRoute = _shareOperation == null;
            StartServicesOnce();
            _isForeground = true;
            NotificationService.SetForeground(true);

            if (_shareOperation != null)
            {
                _forceHomeForContinuation = true;

                // The share operation is a phone object and is read asynchronously. Nobody can
                // await it here (this method is an event handler), so Guarded starts it and
                // keeps any failure in the log. The pragma only silences the compiler warning
                // about the missing await.
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

            // The page is not in the tree yet at this point, so the files are handed over on
            // the UI thread, once the frame has something in it.
            Deployment.Current.Dispatcher.BeginInvoke(delegate
            {
                DepositPickerContinuation();
            });
        }

        /// <summary>
        /// The common part of "we are in front again": start services if this is the first
        /// time, tell notifications, and reconnect the account if the app really was asleep.
        /// </summary>
        private void EnterForegroundFromActivation()
        {
            StartServicesOnce();
            NotificationService.SetForeground(true);

            // Already in front: a fast switch between apps, nothing to resume.
            if (_isForeground) return;

            _isForeground = true;
            _leavingForeground = false;

            // Bumping the counter cancels the slower "leaving" work still running.
            _lifecycleVersion++;
            Diag.MarkAlive();
            BackgroundSync.Refresh();

#pragma warning disable 4014
            Guarded.RunGuardedAsync("App/OnActivated", ResumeAsync());
#pragma warning restore 4014
        }

        /// <summary>
        /// A safety net for the cold start: if nothing has been navigated to yet, open the
        /// home page, whose URI mapper sends the app to the chat list or to the pair page.
        /// </summary>
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

        /// <summary>
        /// The app is going away. Remembers which tab the user was reading, then saves the
        /// state in the background so the phone does not wait for it.
        /// </summary>
        private void LeaveForeground()
        {
            _isForeground = false;
            NotificationService.SetForeground(false);

            // Deactivated and Closing can both arrive; the work must start only once.
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
        /// deactivating. Unlike the WinRT suspension event, this lifecycle has no deferral:
        /// the phone will not wait for this work, which is why it is bounded and guarded.
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
                // Tell the server this phone is away, but never wait more than two seconds for
                // the network: the phone is about to suspend us.
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
                // Only the newest leave work closes the diagnostic run: an older one that
                // finished late must not end a run that is still going.
                if (version == _lifecycleVersion) Diag.EndRun();
            }
        }

        /// <summary>
        /// The app is in front again: check the connection and say we are available. Guarded
        /// owns any failure, so this method only describes the happy path.
        /// </summary>
        private static async Task ResumeAsync()
        {
            if (!await NativeConnection.Instance.CheckAsync()) return;
            await NativeConnection.Instance.SendPresenceAsync("available");
        }

        /// <summary>Which home tab is showing, for restoring it at the next start.</summary>
        private static AppSection CurrentSection()
        {
            Application app = Application.Current;
            PhoneApplicationFrame frame = app == null ? null : app.RootVisual as PhoneApplicationFrame;
            if (frame == null) return AppSection.Chats;

            HomePage home = frame.Content as HomePage;
            return home == null ? AppSection.Chats : home.CurrentSection;
        }

        /// <summary>
        /// Starts the services that live for the whole process, once, before the first page is
        /// shown. On a phone every start is expensive, so this is the single place where the
        /// order is written down.
        /// </summary>
        private void StartServicesOnce()
        {
            if (_servicesStarted) return;
            _servicesStarted = true;

            // Text and colours first: every page below reads them.
            Loc.Prewarm();
            Accent.Apply();
            ApplyShellPalette();

#pragma warning disable 4014
            Guarded.RunGuardedAsync("App/tile theme", TileTheme.RefreshAsync());
#pragma warning restore 4014

            // The diagnostics watchdog and the heartbeat that proves the UI thread still runs.
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

            // Unsubscribe first: a second Start would otherwise run this handler twice.
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

        /// <summary>
        /// Copies the two colours that can change at runtime into their brushes, so the shell
        /// chrome follows the phone's accent.
        /// </summary>
        private static void ApplyShellPalette()
        {
            Application app = Application.Current;
            if (app == null) return;

            SetBrushColor(app, "WhatsAppHeaderBrush", "WhatsAppHeaderColor");
            SetBrushColor(app, "WhatsAppAppBarBrush", "WhatsAppAppBarColor");
        }

        /// <summary>
        /// Finds a brush and a colour in App.xaml by key and copies the colour into the brush.
        ///
        /// `Resources.Contains` asks whether the key exists; `is Color` checks the type and
        /// casts in one step, answering false instead of throwing when the value is something
        /// else.
        /// </summary>
        private static void SetBrushColor(Application app, string brushKey, string colorKey)
        {
            if (!app.Resources.Contains(brushKey) || !app.Resources.Contains(colorKey)) return;

            object colorValue = app.Resources[colorKey];
            if (!(colorValue is Color)) return;

            SolidColorBrush brush = app.Resources[brushKey] as SolidColorBrush;
            if (brush != null) brush.Color = (Color)colorValue;
        }

        /// <summary>
        /// A call has changed state. A ringing call takes over the screen, so that one case is
        /// handled here; everything else is drawn by the page that is already showing.
        /// </summary>
        private void OnCallChanged(object sender, EventArgs e)
        {
            CallSession call = DataService.Instance.Call;
            if (call == null || call.Phase != CallPhase.Ringing) return;

            try
            {
                // This event can arrive on a background thread, and pages may only be opened
                // from the UI thread.
                Deployment.Current.Dispatcher.BeginInvoke(new Action(NavigateToCallPage));
            }
            catch (Exception ex)
            {
                Diag.Failed("App/call-dispatch", ex);
            }
        }

        /// <summary>Opens the call page for a ringing call, unless it is already open.</summary>
        private void NavigateToCallPage()
        {
            // Checked again: the call may have been answered or cancelled while the dispatch
            // was waiting in the queue.
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

        /// <summary>Starts the diagnostic run file, then the crash report writer that reads it.</summary>
        private static async Task StartDiagnosticsAsync()
        {
            await Diag.StartRunAsync();
            CrashReport.Start();
        }

        /// <summary>
        /// Hands the files chosen in the phone's file picker to the attachment inbox, once.
        /// The continuation is cleared first so the same files cannot be added twice.
        /// </summary>
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

        /// <summary>
        /// Adds each picked file to the inbox. One file that fails must not lose the others,
        /// so the try is inside the loop and the failure is logged per file.
        /// </summary>
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

        /// <summary>
        /// Reads a Windows Phone Silverlight share launch into the attachment inbox.
        ///
        /// The share contract expects a small conversation: started, data retrieved,
        /// completed, or an error message. Each `Report` call tells the phone how far we got,
        /// which is what the other app shows to the user.
        /// </summary>
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
                        // Files: the first one is taken, because this app shares one
                        // attachment at a time.
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
                        // A bitmap comes as a stream; the two `using` blocks close the reader
                        // and the stream even if reading throws.
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
                    // Reporting the error can fail too, and that failure is logged rather
                    // than thrown out of a background task.
                    Diag.Failed("App/share-report", reportEx);
                }
            }
        }

        /// <summary>
        /// A failure nobody caught. It is written to the log and flushed immediately, because
        /// the app may not survive to flush later.
        /// </summary>
        private void OnUnhandled(object sender, ApplicationUnhandledExceptionEventArgs e)
        {
            Diag.Failed("App/unhandled", e.ExceptionObject);
            Diag.Stack(e.ExceptionObject);
            Diag.Flush(true);
#if DEBUG
            // A debug build keeps running so the failure can be investigated.
            e.Handled = true;
#endif
        }

        /// <summary>
        /// A task failed and nothing read its exception. It is logged and then marked as
        /// observed, so the runtime does not end the process over it.
        /// </summary>
        private void OnUnobservedTask(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Diag.Failed("App/unobserved-task", e.Exception);
            Diag.Stack(e.Exception);
            Diag.Flush(true);
            e.SetObserved();
        }

    }
}
