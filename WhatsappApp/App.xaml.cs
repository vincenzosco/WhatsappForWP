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
            // the crash had no name. These two handlers put the type, the HRESULT
            // and the message in the log before that happens.
            this.UnhandledException += this.OnUnhandled;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += this.OnUnobservedTask;
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

                // After a termination (the OS closed the process while the app was
                // suspended) we restart from the section the user was in, instead
                // of always from the chats. Contacts and messages are not
                // restored: the adapter sends them again on connection.
                Type startPage;
                if (!SettingsService.HasSavedSettings)
                {
                    startPage = typeof(ConnectionPage);
                }
                else if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    startPage = SectionNav.PageFor(SessionService.Section);
                }
                else
                {
                    startPage = typeof(ChatsPage);
                }
                if (!rootFrame.Navigate(startPage, e.Arguments))
                {
                    // The page name and the real exception, not just the template
                    // sentence: without them a broken XAML is a silent crash.
                    throw new Exception(
                        "Failed to create initial page: " + startPage.FullName,
                        this.navigationFailure);
                }
            }

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

            // If the OS terminates the process while the app is suspended,
            // OnLaunched restarts from here.
            SessionService.Section = CurrentSection();

            // The socket is not closed here: the OS closes it on its own while
            // the app is suspended, and closing it ourselves would leave the app
            // marked as disconnected with nobody retrying. OnResuming handles
            // that, finding a silent connection and redoing it.
            //
            // But the OS freezes the process, and while it is frozen the socket
            // can stay open: the server would keep counting this phone as a
            // watching client and WhatsApp would keep showing the account
            // online. This frame is sent now, within the deferral, so the
            // contacts see the last access time instead. OnResuming takes it
            // back.
#pragma warning disable 4014
            LeaveWatchingAsync(deferral);
#pragma warning restore 4014
        }

        /// <summary>
        /// Sends the "I am going away" frame and then completes the suspension.
        ///
        /// The wait is bounded on purpose: a suspended app is not given
        /// unlimited time, and a server that does not answer must not hold the
        /// app in the foreground. The deferral is completed in a finally, so a
        /// failure releases the app too.
        /// </summary>
        private async void LeaveWatchingAsync(SuspendingDeferral deferral)
        {
            try
            {
                System.Threading.Tasks.Task sending = CommunicationService.Instance.SendWatchingAsync(false);
                await System.Threading.Tasks.Task.WhenAny(sending, System.Threading.Tasks.Task.Delay(2000));
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
        /// The app comes back to the foreground. The socket it had is almost
        /// always already dead - the OS closes it when suspending the process -
        /// but `CommunicationService.IsConnected` is still true, because a socket
        /// closed from the outside raises no event until it is used.
        ///
        /// We check right away instead of waiting for the watchdog tick: those
        /// twenty seconds would be twenty seconds of an app that looks connected
        /// and receives nothing.
        /// </summary>
        private void OnResuming(object sender, object e)
        {
            ConnectionWatchdog.Instance.CheckNow();

            // The other half of the suspension frame. If the watchdog found the
            // socket dead it reconnects, and the handshake is what marks the
            // phone as watching again; if the socket survived the freeze, this
            // is the frame that puts the account back online.
#pragma warning disable 4014
            Guarded.RunGuardedAsync("App/OnResuming",
                CommunicationService.Instance.SendWatchingAsync(true));
#pragma warning restore 4014
        }

        /// <summary>
        /// Tries to reconnect in the background. Deliberately silent: any message
        /// is written by the settings page, which is also the only place where the
        /// language is already ready.
        /// </summary>
        private async void StartAutoConnect()
        {
            await AutoConnector.Instance.TryConnectAsync(SettingsService.Username, 6);
        }

        /// <summary>Section of the foreground page (a chat lives inside the chats).</summary>
        private static AppSection CurrentSection()
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null) return AppSection.Chats;
            if (frame.Content is StatusPage) return AppSection.Status;
            if (frame.Content is CallsPage) return AppSection.Calls;
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

            // Same reason as the loader: the dispatcher is reliably available
            // only here, on the UI thread. Resolving it later, from a network
            // thread, left the service without a dispatcher for the whole session.
            CommunicationService.Instance.Prewarm();

            // The data service is hooked up here: it used to be created at the
            // first page that touched it, and the messages that arrived
            // meanwhile (or the synced contacts) had no listener.
            DataService.Instance.Start();

            // The phone memory budget is watched from here on: it is the only
            // way to know the app is about to be closed because of memory, and to
            // free early what can be rebuilt.
            MemoryWatcher.Instance.Start();

#if DEBUG
            // Debug only: it says in three lines what this phone can really do,
            // instead of letting a silent catch discover it. It does not exist in
            // release, so it costs nothing at startup.
            SelfCheck.RunAsync();
#endif

            // Automatic reconnection: the app does not retry by itself after a
            // restart, and without this the chat list stays empty until the user
            // opens the settings.
            if (SettingsService.HasSavedSettings) StartAutoConnect();

            // And then it keeps the connection alive: WP8.1 closes the socket
            // when suspending the app, and on resume the connection looks active
            // but nothing gets through anymore (see ConnectionWatchdog).
            ConnectionWatchdog.Instance.Start();
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
            if (!(rootFrame.Content is ChatsPage))
            {
                rootFrame.Navigate(typeof(ChatsPage));
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
                                await reader.LoadAsync(size);
                                var buffer = new byte[size];
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