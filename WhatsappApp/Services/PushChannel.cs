// The phone's own notification channel, the one thing on Windows Phone that
// makes notifications arrive with this app closed. The platform gives the device
// a channel, the app tells the server where it is, and from then on the system
// shows arriving notifications by itself - the client reference's own mechanism
// (`MpnsForeground`, `HttpNotificationChannel("message", "mpns.whatsapp.net")`).
//
// Nothing of ours runs anywhere: the channel lives in the phone, the
// registration travels to WhatsApp's own servers on the protocol this app
// already speaks, and the notifications come back through the platform.
using System;
using Microsoft.Phone.Notification;

namespace WhatsappApp.Services
{
    public static class PushChannel
    {
        /// <summary>The channel name the client reference opens (`MpnsForeground`).</summary>
        public const string ChannelName = "message";

        /// <summary>The service the channel is opened against (`Constants.PushService`, 2.17.52).</summary>
        public const string PushService = "mpns.whatsapp.net";

        private static readonly object Gate = new object();
        private static HttpNotificationChannel _channel;
        private static Uri _uri;

        /// <summary>
        /// Where this phone's notifications should be pushed, or null while the
        /// platform has not answered with one yet. A login with no uri sends no
        /// registration; the next one does.
        /// </summary>
        public static Uri ChannelUri
        {
            get { lock (Gate) { return _uri; } }
        }

        /// <summary>Raised when the platform hands out a new uri: the registration must go out again.</summary>
        public static event EventHandler UriChanged;

        /// <summary>
        /// Opens (or adopts) the channel and binds it to the system's toast and
        /// tile. Idempotent: every connection start may call it, and only the
        /// first one touches the platform.
        /// </summary>
        public static void Start()
        {
            lock (Gate)
            {
                if (_channel != null) return;

                try
                {
                    // A channel this app opened before survives in the platform and
                    // is found again by name; a fresh one is opened here.
                    HttpNotificationChannel channel = HttpNotificationChannel.Find(ChannelName);
                    if (channel == null)
                    {
                        channel = new HttpNotificationChannel(ChannelName, PushService);
                    }

                    channel.ChannelUriUpdated += OnChannelUriUpdated;
                    channel.ErrorOccurred += OnChannelError;

                    if (!channel.IsShellToastBound) channel.BindToShellToast();
                    if (!channel.IsShellTileBound) channel.BindToShellTile();

                    if (channel.ChannelUri == null) channel.Open();

                    _channel = channel;
                    TakeUri(channel.ChannelUri);
                }
                catch (Exception error)
                {
                    // No channel is a working app: the registration is simply not
                    // owed, and the scheduled lanes still check on their own.
                    Diag.Failed("PushChannel/start", error);
                }
            }
        }

        private static void OnChannelUriUpdated(object sender, NotificationChannelUriEventArgs e)
        {
            TakeUri(e.ChannelUri);
        }

        private static void OnChannelError(object sender, NotificationChannelErrorEventArgs e)
        {
            Diag.Failed("PushChannel/channel", new InvalidOperationException(
                e.ErrorCode + ": " + (e.Message ?? string.Empty)));
        }

        private static void TakeUri(Uri uri)
        {
            EventHandler raised = null;

            lock (Gate)
            {
                if (_uri == uri || (_uri != null && uri != null && _uri.ToString() == uri.ToString()))
                {
                    return;
                }

                _uri = uri;
                raised = UriChanged;
            }

            if (uri != null)
            {
                Diag.Ok("PushChannel: the platform gave this phone its notification channel");
            }

            if (raised != null)
            {
                raised(null, EventArgs.Empty);
            }
        }
    }
}