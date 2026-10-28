using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Streams;
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

// The Blank Application template is documented at http://go.microsoft.com/fwlink/?LinkId=391641

namespace WhatsappApp
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public sealed partial class App : Application
    {
        private TransitionCollection transitions;

        // Navigate() fails by returning false, not by throwing: the real reason
        // arrives here. Without this, a XAML error in a page shows up as
        // "Failed to create initial page" and nothing else.
        private Exception navigationFailure;

        // Services and watchdogs exist once per process: a share can reactivate
        // an already-started app, and they must not be duplicated.
        private bool servicesStarted;

        // The heartbeat the diagnostics watchdog reads: while this timer ticks, the
        // ui thread is answering, and a run that stops ticking is written down with
        // the step the connection loop had reached (see `Diag.StartWatchdog`).
        private DispatcherTimer _uiHeartbeat;

        /// <summary>
        /// Initializes the singleton application object. This is the first line of
        /// authored code executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            this.Suspending += this.OnSuspending;
            this.Resuming += this.OnResuming;

            // A fatal exception on the UI thread used to kill the process before
            // any Diag line was written: the log stopped at the assembly list and
            // the crash had no name. These handlers put the type, the HRESULT, the
            // message and the frames in the log before that happens.
            this.UnhandledException += this.OnUnhandled;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += this.OnUnobservedTask;

            // Only the UI thread is covered, and on this platform it cannot be
            // otherwise: Windows Phone 8.1 declares no `AppDomain` at all (the ARM
            // build answered `error CS0103: the name 'AppDomain' does not exist`),
            // so an exception raised on a thread that is not the UI thread has no
            // hook here and takes the process down without a line. What a background
            // failure can do about it is not be raised: every worker goes through
            // `Guarded`, which records the failure and keeps the app alive.
        }

        /// <summary>
        /// An exception nobody caught. It is written down first; a debug build
        /// then survives it, because the run that is being diagnosed is worth more
        /// alive than dead. A release build still goes down: an app that lost its
        /// UI is worse than an app that closes.
        /// </summary>
        private void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            Diag.Failed("App/unhandled", e.Exception);

            // The frames too: the type and the HRESULT name the failure, and only
            // the stack says which call raised it.
            Diag.Stack(e.Exception);

            // Forced: the lines above are only in memory until they are written,
            // and this is the one moment where the process may not survive to
            // write them later. A crash with no line is the failure nobody can fix.
            Diag.Flush(true);
#if DEBUG
            e.Handled = true;
#endif
        }


        /// <summary>
        /// A Task whose fault nobody read. The phone used to die with no line at
        /// all, which is the same blind spot one task lower.
        /// </summary>
        private void OnUnobservedTask(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            Diag.Failed("App/unobserved-task", e.Exception);
            Diag.Stack(e.Exception);
            Diag.Flush(true);
            e.SetObserved();
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user. Other
        /// entry points will be used to open a specific file, to display search
        /// results, and so on.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
#if DEBUG
            if (System.Diagnostics.Debugger.IsAttached)
            {
                this.DebugSettings.EnableFrameRateCounter = true;
            }
#endif

            StartServicesOnce();

            // A tile on the Start screen names the chat it opens, and it arrives here as the
            // launch arguments: the same launch entry point, with a reason attached. The page
            // that can act on it is the home page, which is built below on a cold start and
            // already alive on a launch from a running app.
            string tileChat = PinnedTile.ChatFromArguments(e.Arguments);
            if (!string.IsNullOrEmpty(tileChat))
            {
                SessionService.PendingChatId = tileChat;
                SessionService.PendingChatName = PinnedTile.NameFromArguments(e.Arguments);
                Diag.Ok("pinned tile: launching into " + tileChat);
            }
            else if (e.PreviousExecutionState != ApplicationExecutionState.Terminated)
            {
                // The app's own tile - and the lock screen, which shows the same tile -
                // arrives as a plain launch: WP8.1 hands it the same empty arguments as the
                // app list does, so nothing in the launch says where the tap was. What is
                // left is the record the notification layer wrote of what the phone was
                // showing while it was closed (`LaunchRoute`), taken once here. A resume is
                // not this: it has a section of its own and the user left it on purpose.
                string route = SessionService.TakeRoute(DateTime.UtcNow);
                if (route == LaunchRoute.Status)
                {
                    SessionService.PendingSection = AppSection.Status;
                    Diag.Ok("launch: the tile was showing an update, the Status tab opens");
                }
                else if (route == LaunchRoute.Calls)
                {
                    SessionService.PendingSection = AppSection.Calls;
                    Diag.Ok("launch: the tile was showing a missed call, the Calls tab opens");
                }
            }

            Frame rootFrame = EnsureFrame();

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

                // Where this start goes. The decision is a rule and it lives in
                // StartDecision, where it is a test (ProtocolTests/StartupTests.cs):
                // no account stored - or an account WhatsApp has already refused -
                // is the QR, an app the OS terminated comes back to its section, and
                // a cold start with an account is the chats. Nothing about the old
                // server is an input, which is what keeps an install upgraded from
                // the server build off its connection page.
                //
                // After a termination contacts and messages are not restored: they
                // come from WhatsApp again on connection.
                bool resumed = e.PreviousExecutionState == ApplicationExecutionState.Terminated;

                StartTarget target = StartDecision.Decide(
                    NativeCredentialStorage.Session().HasSession,
                    SessionState.Expired,
                    resumed,
                    SessionService.Section == AppSection.Chats);

                // One page hosts all three sections now (HomePage draws the tab
                // strip and the panes); which tab it opens is read from
                // SessionService in its OnNavigatedTo, so "the last section" and
                // "the chats" are the same page with a different starting tab.
                Type startPage = target == StartTarget.Pair
                    ? typeof(PairPage)
                    : typeof(HomePage);
                if (!rootFrame.Navigate(startPage, e.Arguments))
                {
                    // The page name and the real exception, not just the template
                    // sentence: without them a broken XAML is a silent crash.
                    throw new Exception(
                        "Failed to create initial page: " + startPage.FullName,
                        this.navigationFailure);
                }
            }

            // The page is there now: a cold start has already consumed the request in its own
            // OnNavigatedTo, and this is the launch that found the app already running.
            // A tap on the tile of a suspended app is a resume: the page is already there,
            // nothing is navigated, and the section the route asked for has to be shown on
            // it. On a cold start the page consumed the request in its own OnNavigatedTo
            // and this finds nothing left.
            var home = rootFrame.Content as HomePage;
            if (home != null) home.ShowPendingSection();
            if (home != null) home.OpenPinnedChat();

            Window.Current.Activate();
        }

        /// <summary>
        /// Navigation failed: keeps the exception for the OnLaunched message,
        /// which would otherwise report only the false value.
        /// </summary>
        private void RootFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            this.navigationFailure = e.Exception;
        }

        /// <summary>
        /// Restores the content transitions after the application launches.
        /// </summary>
        /// <param name="sender">The object where the handler is attached.</param>
        /// <param name="e">Details about the navigation event.</param>
        private void RootFrame_FirstNavigated(object sender, NavigationEventArgs e)
        {
            var rootFrame = sender as Frame;
            rootFrame.ContentTransitions = this.transitions ?? new TransitionCollection() { new NavigationThemeTransition() };
            rootFrame.Navigated -= this.RootFrame_FirstNavigated;
        }

        /// <summary>
        /// Invoked when application execution is being suspended. Application state
        /// is saved without knowing whether the application will be terminated or
        /// resumed with the contents of memory still intact.
        /// </summary>
        /// <param name="sender">The source of the suspend request.</param>
        /// <param name="e">Details about the suspend request.</param>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();

            // The deferral is completed inside LeaveWatchingAsync, and only there: if any of
            // this throws on the way to it the OS waits out the suspension timeout and then
            // kills the process, with the section unsaved and the run marker still saying a
            // live run. So the two writes are guarded and the deferral is released here when
            // the work that owns it never started.
            bool leaving = false;
            try
            {
                // If the OS terminates the process while the app is suspended,
                // OnLaunched restarts from here.
                SessionService.Section = CurrentSection();

                // This is how a run is supposed to end, and it is the only place that
                // says so: the marker file the run left is removed here, so the next
                // start reads a run that never reached this line as one that died. A
                // suspension is not an exit - OnResuming puts the marker back.
                Diag.EndRun();

                leaving = true;
            }
            catch (Exception ex)
            {
                Diag.Failed("App/suspend", ex);
            }

            if (!leaving)
            {
                deferral.Complete();
                return;
            }

            // The socket is not closed here: the OS closes it on its own while the
            // app is suspended, and WhatsApp's own client is what reconnects when
            // the process comes back (see NativeConnection). The one thing that has
            // to be sent within the deferral is the presence change, so the account
            // stops being shown as online while this phone is frozen - the OS freezes
            // the process without closing the socket, so nothing else would.
#pragma warning disable 4014
            LeaveWatchingAsync(deferral);
#pragma warning restore 4014
        }

        /// <summary>
        /// Says "this phone is going away" and then completes the suspension.
        ///
        /// The wait is bounded on purpose: a suspended app is not given unlimited
        /// time, and a socket that does not answer must not hold the app in the
        /// foreground. The deferral is completed in a finally, so a failure releases
        /// the app too. A phone with no session at all has nothing to say and leaves
        /// at once.
        /// </summary>
        private async void LeaveWatchingAsync(SuspendingDeferral deferral)
        {
            // The list and the conversations as they are now, before the socket is told
            // anything: a suspension is allowed to be the end of this run, and a row that
            // arrived while the app was up would otherwise be gone from the next start's
            // list. Awaited and not raced against a timeout like the presence below: a
            // write cut in half is a `chats.json` the next start cannot read, which is an
            // empty list - worse than the row it was saving.
            try
            {
                await DataService.Instance.SaveSnapshotAsync();
            }
            catch (Exception ex)
            {
                Diag.Failed("App/suspend-snapshot", ex);
            }

            try
            {
                if (NativeConnection.Instance.IsConnected)
                {
                    System.Threading.Tasks.Task sending =
                        NativeConnection.Instance.SendPresenceAsync("unavailable");
                    await System.Threading.Tasks.Task.WhenAny(sending,
                        System.Threading.Tasks.Task.Delay(2000));
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("App/suspend-presence", ex);
            }
            finally
            {
                deferral.Complete();
            }
        }

        /// <summary>
        /// The app comes back to the foreground. The socket it had is usually already
        /// dead - the OS closes it when suspending the process - and nothing observes
        /// a socket closed from the outside, so the client is told to look rather than
        /// waiting for its own next keep-alive (see `NativeConnection`).
        /// </summary>
        private void OnResuming(object sender, object e)
        {
            // The run is alive again: the marker its suspension removed is put back,
            // or a crash after this point would look like a run that ended cleanly.
            Diag.MarkAlive();

            // The other half of the suspension frame: the socket is asked whether it is
            // still there **before** the phone says it is here. WP8.1 freezes the process
            // and the connection is usually gone by the time it resumes, so a presence sent
            // without looking first is a sentence about a socket nobody checked - and the app
            // went on showing the list it had while nothing new arrived (see
            // `NativeConnection.CheckAsync`).
#pragma warning disable 4014
            Guarded.RunGuardedAsync("App/OnResuming", ResumeAsync());
#pragma warning restore 4014
        }

        /// <summary>
        /// The app is in front again: the connection is checked, and the account goes back
        /// online only when the check found one to say it on. A check that fails is not a
        /// failure here: the client dials again on its own, and this run has nothing left to
        /// say.
        /// </summary>
        private static async System.Threading.Tasks.Task ResumeAsync()
        {
            if (!await NativeConnection.Instance.CheckAsync()) return;

            await NativeConnection.Instance.SendPresenceAsync("available");
        }

        /// <summary>Section of the foreground page (a chat lives inside the chats).</summary>
        private static AppSection CurrentSection()
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null) return AppSection.Chats;

            var home = frame.Content as HomePage;
            if (home != null) return home.CurrentSection;

            return AppSection.Chats;
        }

        /// <summary>
        /// Everything that must exist once per process, before the first page.
        /// Both OnLaunched and a share call it: an app started by a share does not
        /// go through OnLaunched.
        /// </summary>
        private void StartServicesOnce()
        {
            if (servicesStarted) return;
            servicesStarted = true;

            // The resource loader cannot be created from a background thread:
            // it is created here, once, on the UI thread.
            Loc.Prewarm();

            // The phone's accent, before the first page resolves the brushes:
            // app bar, selected tab, badges and sent bubble all use it, which is
            // what the official package does through PhoneAccentBrush.
            Accent.Apply();

            // The ui thread's own heartbeat, before anything that could stop it: a
            // freeze is named by the thread that is still running, not by the one
            // that stopped (see `Diag.StartWatchdog`).
            Diag.StartWatchdog();
            _uiHeartbeat = new DispatcherTimer();
            _uiHeartbeat.Interval = TimeSpan.FromSeconds(1);
            _uiHeartbeat.Tick += this.OnUiHeartbeat;
            _uiHeartbeat.Start();

            // The run is recorded on disk from here on, and the previous one is read
            // in the same piece of work: the marker file it left is what says whether
            // it ended on purpose. Reading and writing go through one writer in this
            // order, so this run cannot find its own marker (see Diag).
            //
            // The crash report is the second half and it waits for the first: the tail
            // it writes is what `Diag.StartRunAsync` has just read off the marker, and a
            // report asked for before that read would find nothing to report and look
            // like a run that ended properly.
#pragma warning disable 4014
            Guarded.RunGuardedAsync("Diag/StartRun", StartDiagnosticsAsync());
#pragma warning restore 4014

            // The hardware Back button is not wired to the frame by the platform
            // on a Runtime app: without this the press leaves the app from the
            // first page (see BackNavigator).
            BackNavigator.Start();

            // The data service is hooked up here: it used to be created at the
            // first page that touched it, and the messages that arrived
            // meanwhile (or the synced contacts) had no listener.
            DataService.Instance.Start();

            // The phone memory budget is watched from here on: it is the only
            // way to know the app is about to be closed because of memory, and to
            // free early what can be rebuilt.
            MemoryWatcher.Instance.Start();

            // The account this phone is linked as, brought online. This is the only
            // connection there is: the login WhatsApp needs after a pairing, the
            // client that keeps it, and the one that brings it back after a suspend
            // (see NativeConnection). A start with no account stored does nothing
            // here, and the QR of PairPage is the page.
            NativeConnection.Instance.Start();

            // The periodic check while the app is suspended. It is the only way a
            // message that arrives with the app closed reaches the tile, the badge
            // and the lock screen: the socket does not survive a suspension, and
            // there is no push service behind this client (see BackgroundSync).
            BackgroundSync.Start();
        }

        /// <summary>
        /// The ui thread answered: the watchdog's silence counter starts again.
        /// </summary>
        private void OnUiHeartbeat(object sender, object e)
        {
            Diag.UiTick();
        }

        /// <summary>
        /// The log the run leaves behind, begun, and the crash of the previous run put
        /// into it. One method because the order of the two is the whole point.
        /// </summary>
        private static async System.Threading.Tasks.Task StartDiagnosticsAsync()
        {
            await Diag.StartRunAsync();
            CrashReport.Start();
        }

        /// <summary>
        /// The root frame, created if missing. Three section pages
        /// (Chats/Status/Calls) with NavigationCacheMode.Enabled: the cache keeps
        /// them alive, so moving from one section to another does not rebuild the
        /// page (the chat list also keeps its scroll position).
        /// </summary>
        private static Frame EnsureFrame()
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame != null) return rootFrame;

            rootFrame = new Frame();
            rootFrame.CacheSize = 3;
            rootFrame.Language = Windows.Globalization.ApplicationLanguages.Languages[0];
            Window.Current.Content = rootFrame;
            return rootFrame;
        }

        /// <summary>
        /// Reactivation: this is not a launch. The only case this entry point must
        /// handle is the file picker, which has no return value: the chosen file
        /// arrives here.
        /// </summary>
        protected override void OnActivated(IActivatedEventArgs e)
        {
            base.OnActivated(e);

            if (e == null || e.Kind != ActivationKind.PickFileContinuation) return;

            // This activation can be the process start: the OS may terminate a suspended app
            // while the picker is in front, and a continuation goes through OnActivated and
            // not OnLaunched. Without the services and the frame the file is deposited with
            // nobody subscribed to AttachmentInbox.Ready, and the user comes back to a blank
            // window - including the case where they cancelled the picker.
            if (Window.Current.Content == null)
            {
                StartServicesOnce();
                var frame = EnsureFrame();
                if (!(frame.Content is HomePage)) frame.Navigate(typeof(HomePage));
                Window.Current.Activate();
            }

            var continuation = e as FileOpenPickerContinuationEventArgs;
            if (continuation == null || continuation.Files == null || continuation.Files.Count == 0)
            {
                return;
            }

            // OnActivated is not async: the file is just deposited, and the page
            // in front picks it up with the AttachmentInbox event.
#pragma warning disable 4014
            DepositPickedFileAsync(continuation.Files[0]);
#pragma warning restore 4014
        }

        /// <summary>
        /// A file chosen from the picker. A failure here has nobody to collect it
        /// - OnActivated is not async and nobody awaits this Task - so everything
        /// is caught: an unobserved exception closes the app.
        /// </summary>
        private async void DepositPickedFileAsync(StorageFile file)
        {
            try
            {
                await AttachmentInbox.PutAsync(file, null);
            }
            catch (Exception ex)
            {
                Diag.Failed("App/picker", ex);
            }
        }

        /// <summary>
        /// Another application is sharing something with this one (Gallery, Photos,
        /// browser). The image is deposited and the chats are brought to the
        /// front: the next step is choosing who to send it to.
        /// </summary>
        protected override void OnShareTargetActivated(ShareTargetActivatedEventArgs e)
        {
            base.OnShareTargetActivated(e);

            if (e == null || e.ShareOperation == null) return;

            // This activation can be the process start: the services and the frame
            // do not exist yet.
            StartServicesOnce();
            var rootFrame = EnsureFrame();
            if (!(rootFrame.Content is HomePage))
            {
                rootFrame.Navigate(typeof(HomePage));
            }
            Window.Current.Activate();

#pragma warning disable 4014
            AcceptShareAsync(e.ShareOperation);
#pragma warning restore 4014
        }

        /// <summary>
        /// Reads what was shared, if it is an image.
        ///
        /// Two possible shapes: a list of files (almost every app) or a single
        /// bitmap. Text is not used: the user asked to share an image, and the app
        /// simply does nothing if there is none.
        /// </summary>
        private async System.Threading.Tasks.Task AcceptShareAsync(ShareOperation operation)
        {
            // The share state is reported in the order WP8.1 expects: started,
            // then (when the bytes are there) data retrieved, then completed. A
            // ReportCompleted without ReportStarted leaves the calling app waiting
            // and brings down the process, and that is what happened when sharing
            // a photo.
            //
            // No deferral: on WP8.1 ShareOperation has no GetDeferral (the Deferral
            // type of Windows.Foundation does not even exist in this projection),
            // and none is needed: the app receiving the share is in the foreground,
            // and the operation stays valid while it is in front. The contract is
            // the order of the three calls, not a deferral.
            try
            {
                operation.ReportStarted();

                var data = operation.Data;
                if (data != null)
                {
                    if (data.Contains(StandardDataFormats.StorageItems))
                    {
                        var items = await data.GetStorageItemsAsync();
                        for (int i = 0; i < items.Count; i++)
                        {
                            var file = items[i] as StorageFile;
                            if (file == null) continue;

                            await AttachmentInbox.PutAsync(file, null);
                            break;
                        }
                    }
                    else if (data.Contains(StandardDataFormats.Bitmap))
                    {
                        var reference = await data.GetBitmapAsync();
                        using (var stream = await reference.OpenReadAsync())
                        {
                            using (var reader = new DataReader(stream))
                            {
                                uint size = (uint)stream.Size;
                                uint loaded = await reader.LoadAsync(size);

                                // The stream can answer fewer bytes than it said it had (a
                                // provider that is not a file): reading `size` anyway
                                // zero-fills the tail, and the share lands as a corrupt
                                // picture reported as sent.
                                var buffer = new byte[loaded];
                                reader.ReadBytes(buffer);
                                await AttachmentInbox.PutBytesAsync(buffer, "shared.png", "image/png", null);
                            }
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
                    // A failed share must be reported: without this the calling app
                    // spins forever.
                    operation.ReportError(Loc.Get("App_ShareFailed",
                        "The shared file could not be read."));
                }
                catch (Exception reportEx)
                {
                    Diag.Failed("App/share-report", reportEx);
                }
            }
        }
    }
}