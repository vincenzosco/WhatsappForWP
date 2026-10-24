using System;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The thread that can draw, and whether this process has one at all.
    ///
    /// Why it exists: the background check runs in a process of its own, in which the
    /// system starts the task class and nothing else - there is no XAML view and no
    /// CoreWindow. Two places had to find that out the hard way, and each found it out
    /// once per wake, 21 times on 23-24 October 2026:
    ///
    /// - `ImageHelper` asked for the window, got none, and built the `BitmapImage`
    ///   anyway: `Contact.LoadAvatarAsync: Exception 0x8001010E` (RPC_E_WRONG_THREAD)
    ///   once per wake, because a XAML object cannot be built where there is no thread
    ///   to marshal it on;
    /// - `ContactDirectory` asked `CoreApplication.MainView.CoreWindow` and the call
    ///   threw: `ContactDirectory/Apply: InvalidOperationException 0x8000000E`
    ///   ("a method was called at an unexpected time"), once per wake.
    ///
    /// Neither was a failure: a process with no screen has nothing to draw and nothing to
    /// rename on it. What was missing was one answer to "is there a window here", and this
    /// is it - so the next caller does not have to guess, and cannot guess differently.
    /// </summary>
    public static class UiThread
    {
        /// <summary>
        /// The dispatcher of this process's window, or null when it has none.
        ///
        /// Never throws: on a headless host the platform answers with an exception rather
        /// than a null (`Window.Current` and `CoreApplication.MainView` both), and here that
        /// answer is the null it means.
        /// </summary>
        public static CoreDispatcher Dispatcher()
        {
            try
            {
                Window window = Window.Current;
                return window == null ? null : window.Dispatcher;
            }
            catch (Exception)
            {
                // No view in this process: the background task host.
                return null;
            }
        }

        /// <summary>True when there is a window to draw in - false in the background check.</summary>
        public static bool HasWindow
        {
            get { return Dispatcher() != null; }
        }
    }
}
