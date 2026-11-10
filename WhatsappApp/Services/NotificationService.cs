using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Phone.Shell;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Local alerts for the running client. Windows Phone Silverlight supplies system
    /// toasts, ShellTile updates and the Windows.UI.Notifications tile queue: the queue is
    /// what rotates the app tile between the waiting messages.
    /// </summary>
    public static class NotificationService
    {
        private static readonly SerialQueue TileWrites = new SerialQueue();

        /// <summary>The newest person and message used by the app tile.</summary>
        private static string _senderName;
        private static string _senderImage;
        private static string _senderBody;
        private static IList<TileEntry> _waiting = new List<TileEntry>();

        private static bool _backgroundCheck;
        private static bool _foreground;
        private static int _unread;
        private static int _unseenStatuses;
        private static bool _tileDrawn;

        private const string KeySender = "Tile.Sender";
        private const string KeyBody = "Tile.Body";
        private const string KeyImage = "Tile.Image";
        private const string KeyUnread = "Tile.Unread";
        private const string KeyStatuses = "Tile.Statuses";
        private const string KeyStatusSeenAt = "Notifications.StatusSeenAt";
        private const string KeyMissedCallSeenAt = "Notifications.MissedCallSeenAt";
        private const string TileIconUri = "Assets/TileIcon.png";
        private const string TileWideIconUri = "Assets/TileIconWide.png";
        private const string TileIconPackageUri = "ms-appx:///Assets/TileIcon.png";
        private const string TileWideIconPackageUri = "ms-appx:///Assets/TileIconWide.png";

        /// <summary>Remembers the most recent person who wrote to an unread one-to-one chat.</summary>
        public static void RememberSender(string chatId, string name, string avatarData, string body)
        {
            if (!SettingsService.NotificationsEnabled) return;
            if (string.IsNullOrEmpty(chatId)) return;
            if (chatId.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) return;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("NotificationService/RememberSender", RememberSenderAsync(chatId, name,
                avatarData, body));
#pragma warning restore 4014
        }

        private static async Task RememberSenderAsync(string chatId, string name, string avatarData, string body)
        {
            try
            {
                _senderName = name;
                _senderBody = body;

                TileCard card = await StoreTileImageAsync(chatId, avatarData);
                string image = card == null ? null : card.Square;
                _senderImage = image;
                RememberLastMessage(name, body, image);

                var entry = new TileEntry();
                entry.ChatId = chatId;
                entry.Sender = name;
                entry.Body = body;
                entry.Image = image;
                _waiting = TileCycle.Remember(_waiting, entry);

                RenderTile();
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.RememberSenderAsync", ex);
            }
        }

        private static Task<TileCard> StoreTileImageAsync(string chatId, string avatarData)
        {
            return TileImage.StoreAsync(TileFileName(chatId), avatarData);
        }

        private static string TileFileName(string chatId)
        {
            var builder = new StringBuilder("tile-");
            for (int i = 0; i < chatId.Length; i++)
            {
                char c = chatId[i];
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            builder.Append(".jpg");
            return builder.ToString();
        }

        private static void RememberLastMessage(string name, string body, string image)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[KeySender] = name ?? "";
                ApplicationData.Current.LocalSettings.Values[KeyBody] = body ?? "";
                ApplicationData.Current.LocalSettings.Values[KeyImage] = image ?? "";
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService/RememberLastMessage", ex);
            }
        }

        private static void RestoreLastMessage()
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeySender, out value) && value != null)
                {
                    _senderName = value.ToString();
                }
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeyBody, out value) && value != null)
                {
                    _senderBody = value.ToString();
                }
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeyImage, out value) && value != null)
                {
                    string image = value.ToString();
                    _senderImage = TileImage.IsCurrentCard(image) ? image : null;
                }
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeyUnread, out value)
                    && value is int)
                {
                    _unread = (int)value;
                }
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeyStatuses, out value)
                    && value is int)
                {
                    _unseenStatuses = (int)value;
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService/RestoreLastMessage", ex);
            }
        }

        /// <summary>Starts a background check without treating missing cached counts as reads.</summary>
        public static void BeginBackgroundCheck()
        {
            _backgroundCheck = true;
            RestoreLastMessage();
        }

        /// <summary>Ends a background check and restores foreground count behavior.</summary>
        public static void EndBackgroundCheck()
        {
            _backgroundCheck = false;
        }

        /// <summary>An alert for a message that arrived outside the open conversation.</summary>
        public static void ShowMessage(string title, string body)
        {
            if (!SettingsService.MessageAlertsEnabled) return;
            if (_foreground) return;
            Toast(title, body);
        }

        /// <summary>Whether the app is visible; messages in front do not need a second alert.</summary>
        public static void SetForeground(bool foreground)
        {
            _foreground = foreground;
        }

        /// <summary>A status post that is newer than this phone's last announced update.</summary>
        public static void ShowStatus(string name, DateTime postedAt)
        {
            if (!SettingsService.StatusAlertsEnabled) return;
            if (!IsToastNews(KeyStatusSeenAt, postedAt)) return;
            Toast(Loc.Get("Notification_Status", "New status"), name);
        }

        /// <summary>An incoming call alert.</summary>
        public static void ShowIncomingCall(string name)
        {
            if (!SettingsService.CallAlertsEnabled) return;
            Toast(Loc.Get("Notification_IncomingCall", "Incoming call"), name);
        }

        /// <summary>A missed-call alert that is raised only once for each call-log watermark.</summary>
        public static void ShowMissedCall(string name, DateTime arrivedAt)
        {
            if (!SettingsService.CallAlertsEnabled) return;
            if (!IsToastNews(KeyMissedCallSeenAt, arrivedAt)) return;
            Toast(Loc.Get("Notification_MissedCall", "Missed call"), name);
        }

        /// <summary>Advances the local missed-call watermark through the visible call history.</summary>
        public static void MarkMissedCallsSeen(DateTime through)
        {
            if (through == default(DateTime)) return;

            try
            {
                object stored;
                long ticks = 0;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeyMissedCallSeenAt, out stored)
                    && stored != null)
                {
                    long.TryParse(stored.ToString(), out ticks);
                }

                if (through.Ticks <= ticks) return;
                ApplicationData.Current.LocalSettings.Values[KeyMissedCallSeenAt] = through.Ticks;
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService/MarkMissedCallsSeen", ex);
            }
        }

        private static bool IsToastNews(string key, DateTime moment)
        {
            if (moment == default(DateTime)) return false;
            if (!SettingsService.NotificationsEnabled) return false;

            try
            {
                long ticks = 0;
                object stored;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out stored) && stored != null)
                {
                    long.TryParse(stored.ToString(), out ticks);
                }

                if (ticks <= 0)
                {
                    ApplicationData.Current.LocalSettings.Values[key] = moment.Ticks;
                    Diag.Ok("notification: " + key + " starts at " + moment.ToString("dd/MM HH:mm")
                        + ", nothing said");
                    return false;
                }

                if (moment.Ticks <= ticks) return false;
                ApplicationData.Current.LocalSettings.Values[key] = moment.Ticks;
                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService/IsToastNews", ex);
                return false;
            }
        }

        private static void Toast(string title, string body)
        {
            if (!SettingsService.NotificationsEnabled) return;

            try
            {
                var toast = new ShellToast();
                toast.Title = Cut(title, 60);
                toast.Content = Cut(body, 200);
                toast.Show();
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.Toast", ex);
            }
        }

        /// <summary>Updates the shell tile count and its current flip-tile text and picture.</summary>
        public static void SetUnread(int count)
        {
            count = count < 0 ? 0 : count;
            if (_backgroundCheck && count < _unread) count = _unread;

            _unread = count;
            RememberUnread(count);
            RenderTile();
        }

        /// <summary>Updates the status turn without mixing status counts into unread messages.</summary>
        public static void SetStatuses(int count)
        {
            count = count < 0 ? 0 : count;
            if (_backgroundCheck && count < _unseenStatuses) count = _unseenStatuses;
            if (count == _unseenStatuses && _tileDrawn) return;

            _unseenStatuses = count;
            RememberStatuses(count);
            RenderTile();
        }

        /// <summary>Removes a read conversation from the tile's in-memory rotation.</summary>
        public static void Forget(string chatId)
        {
            _waiting = TileCycle.Forget(_waiting, chatId);
        }

        /// <summary>Recomposes the shell tile after its theme image changes.</summary>
        public static void Refresh()
        {
            RenderTile();
        }

        /// <summary>Clears tile and unread state when this app starts serving another account.</summary>
        public static void ResetForNewAccount()
        {
            _senderName = null;
            _senderImage = null;
            _senderBody = null;
            _waiting = new List<TileEntry>();
            _unread = 0;
            _unseenStatuses = 0;
            _tileDrawn = false;
            RememberUnread(0);
            RememberStatuses(0);
            RenderTile();
        }

        /// <summary>Clears all in-memory conversation turns after every chat was read.</summary>
        public static void ForgetAll()
        {
            _waiting = new List<TileEntry>();
        }

        private static void RememberUnread(int count)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[KeyUnread] = count;
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService/RememberUnread", ex);
            }
        }

        private static void RememberStatuses(int count)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[KeyStatuses] = count;
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService/RememberStatuses", ex);
            }
        }

        private static void RenderTile()
        {
            // The turn the queue is handed: the waiting messages take the whole
            // queue, and the updates turn is built only when nobody is waiting -
            // `TileCycle.WithStatuses` appends it after them, so the tile and the
            // lock screen open on the person who wrote and not on a count.
            IList<TileEntry> waiting = _waiting ?? new List<TileEntry>();
            TileEntry statuses = _unread <= 0 && _unseenStatuses > 0 ? StatusTurn(_unseenStatuses) : null;
            IList<TileEntry> turns = TileCycle.WithStatuses(
                TileCycle.Turns(waiting, TileCycle.MaxEntries), statuses, TileCycle.MaxEntries);

#pragma warning disable 4014
            Guarded.RunGuardedAsync("NotificationService/RenderTile", TileWrites.RunAsync(delegate
            {
                return RenderTileAsync(turns);
            }));
#pragma warning restore 4014
        }

        private static async Task RenderTileAsync(IList<TileEntry> turns)
        {
            try
            {
                TileTheme.Restore();

                int count = turns == null ? 0 : turns.Count;
                if (count == 0)
                {
                    // The empty tile: the app mark and the app name, one turn.
                    var alone = new List<TileEntry>();
                    alone.Add(null);
                    turns = alone;
                }

                // Every turn is composed before anything is pushed: a queue that is
                // emptied first and then fails to compose leaves the tile with no
                // content at all, and a new notification replaces the content anyway.
                var built = new List<TileNotification>();
                for (int i = 0; i < turns.Count; i++)
                {
                    built.Add(await TurnNotificationAsync(turns[i]));
                }

                TileTheme.EnableNotificationQueue(true);
                TileUpdater updater = TileUpdateManager.CreateTileUpdaterForApplication();
                updater.Clear();
                for (int i = 0; i < built.Count; i++)
                {
                    updater.Update(built[i]);
                }

                _tileDrawn = true;
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.RenderTile", ex);
            }
        }

        /// <summary>
        /// One turn of the queue as the tile payload. Every image binding gets its
        /// own `src`: a template does not take the icon from the manifest, and an
        /// empty `src` draws a tile with no icon and throws nothing.
        /// </summary>
        private static async Task<TileNotification> TurnNotificationAsync(TileEntry entry)
        {
            TileLines lines;
            Uri square;
            Uri wide;
            Uri small;

            if (entry == null)
            {
                // Empty tile strings mean "keep the old value" to the shell; the
                // manifest title and a space replace the last sender's text.
                lines = new TileLines();
                lines.First = "WhatsApp";
                lines.Second = " ";
                square = PackageImage(TileIconUri);
                wide = PackageImage(TileWideIconUri);
                small = PackageImage(TileIconUri);
            }
            else
            {
                string sender = string.IsNullOrEmpty(entry.Sender) ? "" : entry.Sender;
                string unread = _unread > 0 ? UnreadText(_unread) : "";
                lines = TileText.For(sender, entry.Body, unread);

                if (string.IsNullOrEmpty(entry.Image))
                {
                    square = await ThemeImageAsync(TileTheme.Square, TileIconUri);
                    wide = await ThemeImageAsync(TileTheme.Wide, TileWideIconUri);
                    small = await ThemeImageAsync(TileTheme.Small, TileIconUri);
                }
                else
                {
                    square = await PinnedTile.ShellImageUriAsync(entry.Image);
                    wide = await PinnedTile.ShellImageUriAsync(TileImage.WideOf(entry.Image));
                    small = await PinnedTile.ShellImageUriAsync(TileImage.SmallOf(entry.Image));
                }
            }

            var payload = new XmlDocument();
            payload.LoadXml(
                "<tile><visual version=\"2\">" +
                "<binding template=\"TileSquare71x71Image\">" +
                "<image id=\"1\" src=\"\"/>" +
                "</binding>" +
                "<binding template=\"TileSquare150x150Image\">" +
                "<image id=\"1\" src=\"\"/>" +
                "</binding>" +
                "<binding template=\"TileWide310x150ImageAndText02\">" +
                "<image id=\"1\" src=\"\"/>" +
                "<text id=\"1\"></text>" +
                "<text id=\"2\"></text>" +
                "</binding>" +
                "</visual></tile>");

            BindImage(payload, "TileSquare71x71Image", small);
            BindImage(payload, "TileSquare150x150Image", square);
            BindImage(payload, "TileWide310x150ImageAndText02", wide);
            BindText(payload, "TileWide310x150ImageAndText02", "1", NonEmpty(lines.First));
            BindText(payload, "TileWide310x150ImageAndText02", "2", NonEmpty(lines.Second));

            return new TileNotification(payload);
        }

        private static void BindImage(XmlDocument payload, string template, Uri src)
        {
            XmlElement image = payload.SelectSingleNode(
                "//binding[@template='" + template + "']/image") as XmlElement;
            if (image == null) return;

            image.SetAttribute("src", src == null ? TileIconPackageUri : src.OriginalString);
        }

        private static void BindText(XmlDocument payload, string template, string id, string text)
        {
            XmlElement node = payload.SelectSingleNode(
                "//binding[@template='" + template + "']/text[@id='" + id + "']") as XmlElement;
            if (node == null) return;

            node.InnerText = text;
        }

        private static async Task<Uri> ThemeImageAsync(string image, string fallback)
        {
            return string.IsNullOrEmpty(image)
                ? PackageImage(fallback)
                : await PinnedTile.ShellImageUriAsync(image);
        }

        private static Uri PackageImage(string path)
        {
            if (string.Equals(path, TileWideIconUri, StringComparison.Ordinal))
            {
                return new Uri(TileWideIconPackageUri, UriKind.Absolute);
            }
            if (string.Equals(path, TileIconUri, StringComparison.Ordinal))
            {
                return new Uri(TileIconPackageUri, UriKind.Absolute);
            }
            return new Uri(path, UriKind.Relative);
        }

        private static string NonEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? " " : value;
        }

        private static TileEntry Entry(string sender, string body, string image)
        {
            var entry = new TileEntry();
            entry.Sender = sender;
            entry.Body = body;
            entry.Image = image;
            return entry;
        }

        private static TileEntry StatusTurn(int count)
        {
            var entry = new TileEntry();
            entry.Sender = Loc.Get("TileStatusLabel", "Updates");
            entry.Body = StatusesText(count);
            return entry;
        }

        /// <summary>The localized text used for the unread count on a tile or toast.</summary>
        public static string UnreadText(int count)
        {
            if (count == 1) return Loc.Get("TileUnreadOne", "1 unread message");
            return string.Format(Loc.Get("TileUnreadMany", "{0} unread messages"), count);
        }

        /// <summary>The localized text for unseen status updates.</summary>
        public static string StatusesText(int count)
        {
            if (count == 1) return Loc.Get("TileStatusOne", "1 new update");
            return string.Format(Loc.Get("TileStatusMany", "{0} new updates"), count);
        }

        private static string Cut(string value, int max)
        {
            string text = value ?? "";
            if (text.Length <= max) return text;

            int keep = max - 1;
            if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
            return text.Substring(0, keep) + "\u2026";
        }
    }
}
