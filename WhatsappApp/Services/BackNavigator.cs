using System;
using Windows.Phone.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The hardware Back button of the phone, wired to the page stack.
    ///
    /// Why it exists: a Windows Phone 8.1 Runtime app does not get this for free.
    /// In a Silverlight app the system pops the frame itself; in a Runtime app it
    /// does not, and the press leaves the app from the first page on - so a chat
    /// sent the user to the Start screen instead of back to the conversations.
    /// The event is the only handle on it: subscribing once, at startup, and
    /// popping the frame while there is something to pop.
    ///
    /// At a section root the frame has nothing to go back to and the event is left
    /// alone, so the system suspends the app the way it always did: that is the
    /// one press the user expects to leave.
    /// </summary>
    public static class BackNavigator
    {
        private static bool _started;

        /// <summary>
        /// Subscribes once per process. Must be called on the UI thread, before
        /// the first page (see App.StartServicesOnce). Every call is guarded: a
        /// device that refuses the event must not take the app down at startup.
        /// </summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;

            try
            {
                HardwareButtons.BackPressed += OnBackPressed;
                Diag.Ok("hardware back button wired to the page stack");
            }
            catch (Exception ex)
            {
                Diag.Failed("BackNavigator.Start", ex);
            }
        }

        private static void OnBackPressed(object sender, BackPressedEventArgs e)
        {
            try
            {
                var frame = Window.Current.Content as Frame;
                if (frame == null || !frame.CanGoBack) return;

                // Handled first: the system would otherwise leave the app while the
                // frame is still popping.
                e.Handled = true;
                frame.GoBack();
            }
            catch (Exception ex)
            {
                // A frame that refuses to pop must not cost the app: the event
                // stays unhandled and the system does what it would have done.
                Diag.Failed("BackNavigator.OnBackPressed", ex);
            }
        }
    }
}