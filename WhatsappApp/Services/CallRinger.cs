using System;
using Windows.Phone.Devices.Notification;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The phone ringing: one alert and one vibration, on while a call is ringing.
    ///
    /// Why it exists: an offer arriving while the app is open showed a banner and nothing
    /// else - no sound, no vibration, and no alert at all if the app was in the background
    /// but still connected. A call that only appears as a strip of text is a call nobody
    /// notices.
    ///
    /// What it is not: a ringtone. WP8.1 plays a custom sound only through a `RingtonePlayer`
    /// with a ringtone file that has to be installed as one, which is not something a store
    /// build can rely on; the alert and the vibration are what this build has, and the call
    /// screen is what stays in front of the person.
    ///
    /// Both halves are guarded on their own: a phone whose vibration is refused still shows
    /// the alert, and a build without the vibration capability still rings - the feature must
    /// not be one P/Invoke away from nothing at all.
    /// </summary>
    public static class CallRinger
    {
        /// <summary>How long one vibration lasts. WP8.1 caps a single call at five seconds.</summary>
        private static readonly TimeSpan Buzz = TimeSpan.FromSeconds(2);

        /// <summary>Whether this ringer is on, so a repeated raise does not buzz twice.</summary>
        private static bool _ringing;

        /// <summary>
        /// A call is ringing for this name: the alert, then the buzz. Called from the state
        /// machine (`DataService.RaiseCallChanged`), not from a page, because a page that is
        /// not on screen still has a phone to ring.
        /// </summary>
        public static void Start(string name)
        {
            if (_ringing) return;
            _ringing = true;

            NotificationService.ShowIncomingCall(name);

            try
            {
                VibrationDevice device = VibrationDevice.GetDefault();
                if (device != null) device.Vibrate(Buzz);
            }
            catch (Exception ex)
            {
                // No vibration on this phone, or no capability for it: the alert is enough.
                Diag.Failed("CallRinger/buzz", ex);
            }
        }

        /// <summary>
        /// The call stopped ringing, whichever way it went: answered, refused, ended, or
        /// never answered. Safe to call when nothing is ringing.
        /// </summary>
        public static void Stop()
        {
            if (!_ringing) return;
            _ringing = false;

            try
            {
                VibrationDevice device = VibrationDevice.GetDefault();
                if (device != null) device.Cancel();
            }
            catch (Exception ex)
            {
                Diag.Failed("CallRinger/stop", ex);
            }
        }
    }
}
