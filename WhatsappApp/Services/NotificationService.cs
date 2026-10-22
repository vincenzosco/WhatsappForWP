using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The alerts this app can raise while it runs: a toast, the number on the
    /// icon and the live tile. There is no cloud service behind it, so a message
    /// that arrives with the app suspended produces nothing: the TCP connection
    /// belongs to the app, and WP8.1 closes it when it suspends it. Real push
    /// notifications would require an external service this project does not have.
    ///
    /// Every call is guarded: a phone that rejects the toast must not bring down
    /// the message reception.
    ///
    /// The tile is a queue and not one notification: Windows rotates a tile through
    /// the notifications it holds, and the reference client uses that - with four
    /// people waiting, the tile is four turns. What it must not be is one turn per
    /// **message**, which is what the queue was used for once and why it was taken out:
    /// the count written on each was the count of its own moment, and the tile on
    /// screen kept showing a number nobody had verified since. Here the queue holds one
    /// entry per **conversation** (see `TileCycle`) and the number comes from the live
    /// total on every render.
    /// </summary>
    public static class NotificationService
    {
        /// <summary>
        /// The last person who wrote in a chat that is not the open one: the tile
        /// shows their picture and their name while something is waiting.
        ///
        /// People only: a group has no single face, and putting a group picture on
        /// the tile would say even less than nothing. The group name, without a
        /// picture, is left out as well.
        ///
        /// The filesystem cannot be touched from here (we are on the UI thread that
        /// just received the message), so the update starts and carries on by
        /// itself.
        /// </summary>
        public static void RememberSender(string chatId, string name, string avatarData, string body)
        {
            if (!SettingsService.NotificationsEnabled) return;
            if (string.IsNullOrEmpty(chatId)) return;
            if (chatId.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) return;

#pragma warning disable 4014
            RememberSenderAsync(chatId, name, avatarData, body);
#pragma warning restore 4014
        }

        /// <summary>
        /// What the lock screen's detailed status shows: the sender and the message.
        /// It is written to LocalSettings too, because a background check is a new
        /// process: the fields above would be empty and the tile would read a count
        /// twice instead of naming who wrote.
        /// </summary>
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

        /// <summary>
        /// Puts the last sender, the last message, their picture and the count back into
        /// this process, from the copy the previous run wrote. Called at the start of a
        /// background check.
        /// </summary>
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
                    _senderImage = image.Length == 0 ? null : image;
                }
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeyUnread, out value)
                    && value is int)
                {
                    _unread = (int)value;
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService/RestoreLastMessage", ex);
            }
        }

        /// <summary>
        /// The start of a background check, which is a new process with nothing in memory.
        ///
        /// Why the last state is read back: the check runs while the app is closed, and
        /// the tile it is about to draw has to name who wrote even when this run saw no
        /// new message. And why the count may not go down for the length of it: a check
        /// **reads nothing**, so a zero it computes is the local copy having no counts in
        /// it (`ChatCache` leaves them out), not the messages having been read. Taking
        /// that zero is what cleared the tile and the badge at every wake - the check ran
        /// and the phone showed no change at all.
        /// </summary>
        public static void BeginBackgroundCheck()
        {
            _backgroundCheck = true;
            RestoreLastMessage();
        }

        /// <summary>The check is over: the foreground's rules apply again.</summary>
        public static void EndBackgroundCheck()
        {
            _backgroundCheck = false;
        }

        /// <summary>
        /// The picture of the sender is written to a file first, then the tile is
        /// drawn: a tile wants a path, not the bytes.
        ///
        /// The bytes arrive as base64 with the message, and are written as
        /// they are. Over the system ceiling the picture is left alone and the tile
        /// falls back to the app icon: better an icon than a tile the phone rejects.
        /// </summary>
        private static async Task RememberSenderAsync(string chatId, string name, string avatarData, string body)
        {
            try
            {
                _senderName = name;
                _senderBody = body;

                string image = await StoreTileImageAsync(chatId, avatarData);
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

        /// <summary>
        /// The sender picture in an app file, and the path to put in the tile. Null
        /// when there is no picture or when it is too large.
        /// </summary>
        private static async Task<string> StoreTileImageAsync(string chatId, string avatarData)
        {
            if (string.IsNullOrEmpty(avatarData)) return null;

            try
            {
                byte[] bytes = Convert.FromBase64String(avatarData);
                if (bytes.Length == 0 || bytes.Length > MaxTileImageBytes) return null;

                string fileName = TileFileName(chatId);
                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, bytes);
                return "ms-appdata:///local/" + fileName;
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.StoreTileImageAsync", ex);
                return null;
            }
        }

        /// <summary>
        /// A file name from a chat: a JID has an at sign and maybe two dots, which
        /// are a risk in a path, so only what is a letter or a digit is kept.
        /// </summary>
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

        /// <summary>
        /// The ceiling the system accepts for a tile image. Above it the update is
        /// rejected: the tile is drawn without it.
        /// </summary>
        private const int MaxTileImageBytes = 200 * 1024;

        /// <summary>
        /// The tile: how many messages are waiting, then one turn per conversation - who
        /// wrote and what they said, over their picture.
        ///
        /// The number is written here, and not left to the badge alone. On WP8.1 the
        /// badge is the only thing that draws a count on the tile, and a badge that
        /// the shell does not draw leaves the tile with no number at all - which is
        /// what happened on the phone this was fixed on: the tile showed the app
        /// icon and nothing else.
        ///
        /// Every notification carries the **same, live** total: the queue rotates, the
        /// number does not belong to a moment. That is the difference between this and
        /// the per-message queue that was taken out - a tile could then be showing the
        /// count of a message that had already been read.
        /// </summary>
        private static void RenderTile()
        {
            try
            {
                var updater = TileUpdateManager.CreateTileUpdaterForApplication();

                // The queue is the whole point: it is what makes the tile rotate through
                // the people who are waiting instead of staying on the last one. It is a
                // property of the device and it survives this run, so it is set once.
                if (!_queueEnabled)
                {
                    try { updater.EnableNotificationQueue(true); }
                    catch (Exception ex) { Diag.Failed("NotificationService.RenderTile/queue", ex); }
                    _queueEnabled = true;
                }

                // Cleared and drawn again, because the queue is a set and not a log: a
                // message that has just been read must not keep its turn, and one Update
                // per message would fill the five slots with copies of the same thing.
                try { updater.Clear(); }
                catch (Exception ex) { Diag.Failed("NotificationService.RenderTile/clear", ex); }

                if (_unread <= 0) return;

                IList<TileEntry> waiting = _waiting;
                if (waiting == null || waiting.Count == 0)
                {
                    // Nobody named in this process - a count read back from the local copy
                    // before any message: one notification with the last person the phone
                    // knows, which is what the lock screen shows too.
                    Post(updater, _senderName, _senderBody, _senderImage);
                    return;
                }

                for (int i = 0; i < waiting.Count; i++)
                {
                    TileEntry entry = waiting[i];
                    if (entry == null) continue;

                    Post(updater, entry.Sender, entry.Body, entry.Image);
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.RenderTile", ex);
            }
        }

        /// <summary>
        /// One notification: a turn in the queue, for one conversation.
        ///
        /// The three sizes are one notification on purpose (see `ImportBinding`): the
        /// medium and the wide one say the same person, and a size that disagrees with
        /// the other about who wrote would be two tiles in one.
        /// </summary>
        private static void Post(TileUpdater updater, string sender, string body, string image)
        {
            var xml = TileUpdateManager.GetTemplateContent(
                TileTemplateType.TileSquare150x150PeekImageAndText02);
            var visual = xml.SelectSingleNode("/tile/visual") as XmlElement;
            if (visual == null) return;

            FillBinding(xml, visual.SelectSingleNode("binding") as XmlElement, sender, image);
            AddWideBinding(xml, visual, sender, body, image);
            AddSmallBinding(xml, visual);

            updater.Update(new TileNotification(xml));
        }

        /// <summary>
        /// What one binding of the notification says: how many are waiting, then the
        /// last sender over their picture.
        ///
        /// One helper for both sizes. The medium tile and the wide one say the same
        /// thing, so a change here cannot arrive on one of them and not on the other.
        /// </summary>
        private static void FillBinding(XmlDocument xml, XmlElement binding, string senderName, string image)
        {
            if (binding == null) return;

            var texts = binding.SelectNodes("text");
            if (texts.Length > 0) texts[0].AppendChild(xml.CreateTextNode(UnreadText(_unread)));
            // Nobody wrote yet - a count read back from the local copy at startup,
            // before any message: the line stays empty instead of holding an empty
            // node.
            string sender = Cut(senderName, 30);
            if (texts.Length > 1 && sender.Length > 0) texts[1].AppendChild(xml.CreateTextNode(sender));

            SetImage(xml, binding, image ?? TileIconUri);
        }

        /// <summary>
        /// One binding for another tile size, imported from the template of that
        /// size and appended to this notification.
        ///
        /// A node belongs to its own document, so the binding is imported: appending
        /// it as it is raises. Importing is also what lets the three sizes live in
        /// one notification, which is what keeps the tile a single thing to reason
        /// about instead of one per size.
        /// </summary>
        private static XmlElement ImportBinding(XmlDocument xml, XmlElement visual, TileTemplateType template)
        {
            var other = TileUpdateManager.GetTemplateContent(template);
            var binding = other.SelectSingleNode("/tile/visual/binding");
            if (binding == null) return null;

            var imported = xml.ImportNode(binding, true) as XmlElement;
            if (imported == null) return null;

            visual.AppendChild(imported);
            return imported;
        }

        /// <summary>
        /// The wide tile says what the medium one says.
        ///
        /// Without this the wide size keeps the logo of the manifest whatever the
        /// app has to tell: `TileWide310x150IconWithBadge` does not exist on WP8.1
        /// (CS0117), and there is no other wide badge template to fall back on.
        ///
        /// A wide template that the phone refuses cannot take the medium tile away
        /// with it, so this one is guarded on its own.
        /// </summary>
        private static void AddWideBinding(XmlDocument xml, XmlElement visual, string sender, string body, string image)
        {
            try
            {
                var binding = ImportBinding(xml, visual, TileWideTemplate);
                if (binding == null) return;

                FillWide(xml, binding, sender, body, image);
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.AddWideBinding", ex);
            }
        }

        /// <summary>
        /// The small tile, pinned at 71x71: it carries the app mark, whatever the
        /// other sizes are showing.
        ///
        /// The size has no line of text to write the count in, and no room for one
        /// either, so the only thing it can be is the app itself. Its template is
        /// the one that does not ship an `image` element: `SetImage` creates it.
        ///
        /// The picture of the sender is not used here on purpose. At 71 px a face
        /// is a smear, and the size is meant to say "this app has something for
        /// you", while the medium and the wide tile next to it say what and who.
        /// </summary>
        private static void AddSmallBinding(XmlDocument xml, XmlElement visual)
        {
            try
            {
                var binding = ImportBinding(xml, visual, TileTemplateType.TileSquare71x71IconWithBadge);
                if (binding == null) return;

                SetImage(xml, binding, TileIconUri);
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.AddSmallBinding", ex);
            }
        }

        /// <summary>
        /// The wide tile is the lock screen's detailed status: it says **who** and
        /// **what**, and the number is left to the badge (the quick status). That is
        /// the pair Windows offers in Settings -> Lock screen, and the reason the two
        /// bindings say different things.
        /// </summary>
        private static void FillWide(XmlDocument xml, XmlElement binding, string senderName, string bodyText, string image)
        {
            if (binding == null) return;

            var texts = binding.SelectNodes("text");

            // With nothing to name - a count restored from the cache before any
            // message in this process - the count is the only true line.
            string sender = Cut(senderName, 30);
            if (sender.Length == 0) sender = UnreadText(_unread);

            string body = Cut(bodyText, 160);
            if (body.Length == 0) body = UnreadText(_unread);

            if (texts.Length > 0) texts[0].AppendChild(xml.CreateTextNode(sender));
            if (texts.Length > 1) texts[1].AppendChild(xml.CreateTextNode(body));

            // The wide tile has its own asset: the square one is scaled to fill
            // 310x150 and loses its top and bottom, which cut the mark. This one is
            // drawn at the tile's proportion, so the mark is whole.
            SetImage(xml, binding, image ?? TileWideIconUri);
        }

        /// <summary>
        /// How many are waiting, as the tile says it. One message is not "1
        /// messages": the two forms are two keys, so whoever translates writes both
        /// instead of guessing a plural rule.
        /// </summary>
        public static string UnreadText(int count)
        {
            if (count == 1) return Loc.Get("TileUnreadOne", "1 unread message");
            return string.Format(Loc.Get("TileUnreadMany", "{0} unread messages"), count);
        }

        /// <summary>
        /// The last sender's name and the file of their picture, kept because the
        /// count changes on its own - a chat is read, another message arrives - and
        /// the tile has to be drawn again with the same face on it.
        /// </summary>
        private static string _senderName;
        private static string _senderImage;

        /// <summary>The last thing that was written, for the lock screen text.</summary>
        private static string _senderBody;

        /// <summary>
        /// The conversations the tile rotates through, newest first. One entry per
        /// conversation, never one per message: see `TileCycle`.
        /// </summary>
        private static IList<TileEntry> _waiting = new List<TileEntry>();

        /// <summary>
        /// True for the length of a background check. It is what makes a zero from that
        /// run a fact about the local copy and not about the messages: see
        /// <see cref="BeginBackgroundCheck"/>.
        /// </summary>
        private static bool _backgroundCheck;

        /// <summary>Where the last sender, message, picture and count survive a process.</summary>
        private const string KeySender = "Tile.Sender";
        private const string KeyBody = "Tile.Body";
        private const string KeyImage = "Tile.Image";
        private const string KeyUnread = "Tile.Unread";

        /// <summary>
        /// The wide template. WP8.1 has `PeekImageAndText01` and `02`, and only the
        /// second carries two lines: a name and a message. There is no wide template
        /// with a peek image and three lines, which is why the count is not on the
        /// wide tile at all.
        /// </summary>
        private static readonly TileTemplateType TileWideTemplate =
            TileTemplateType.TileWide310x150PeekImageAndText02;

        /// <summary>The count the tile is showing, and the one the badge shows.</summary>
        private static int _unread;

        /// <summary>
        /// True after the notification queue has been turned on. It is a property of the
        /// updater, not of a single update, and the tile it belongs to is drawn again on
        /// every change.
        /// </summary>
        private static bool _queueEnabled;

        /// <summary>An alert for a message that arrived in a closed chat.</summary>
        public static void ShowMessage(string title, string body)
        {
            if (!SettingsService.NotificationsEnabled) return;

            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(Cut(title, 60)));
                texts[1].AppendChild(xml.CreateTextNode(Cut(body, 200)));
                ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.ShowMessage", ex);
            }
        }

        /// <summary>
        /// The unread count, in the two places WP8.1 can show it: the number on the
        /// icon (the badge) and the tile. They are two different notifications and
        /// one can fail without the other, so each has its own guard. With 0
        /// everything is cleared: the tile goes back to the manifest one.
        ///
        /// The badge is set **after** the tile, because clearing the tile can take
        /// the badge with it: the other order would leave the number off the icon
        /// until the next message.
        /// </summary>
        public static void SetUnread(int count)
        {
            count = count < 0 ? 0 : count;

            // A background check reads nothing, so a number below what the tile already
            // shows is that run reading the local copy - which carries no unread counts
            // at all (`ChatCache` leaves them out) - and not the messages being read. It
            // is the count that may not go down, or the tile goes blank at every wake.
            if (_backgroundCheck && count < _unread) count = _unread;

            _unread = count;
            RememberUnread(count);

            RenderTile();
            SetBadge(_unread);
        }

        /// <summary>
        /// A conversation was read: its message leaves the tile's queue. The count that
        /// follows comes from the caller, which is what draws the tile again.
        /// </summary>
        public static void Forget(string chatId)
        {
            _waiting = TileCycle.Forget(_waiting, chatId);
        }

        /// <summary>Everything was read: no conversation is waiting for a turn.</summary>
        public static void ForgetAll()
        {
            _waiting = new List<TileEntry>();
        }

        /// <summary>
        /// The count as the next process will find it. Written on every change for the same
        /// reason the last sender is: a background check starts with nothing in memory, and
        /// a number it has to invent is a number it gets wrong.
        /// </summary>
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

        /// <summary>The number on the icon: 0 removes it.</summary>
        private static void SetBadge(int count)
        {
            try
            {
                var updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
                if (count <= 0)
                {
                    updater.Clear();
                    return;
                }

                var xml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
                var badge = (XmlElement)xml.SelectSingleNode("/badge");
                badge.SetAttribute("value", Math.Min(count, 99).ToString());
                updater.Update(new BadgeNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.SetBadge", ex);
            }
        }

        /// <summary>
        /// Writes the picture on the binding image, creating it if the template has
        /// none: the element must be created with the destination document, not
        /// with the template one, otherwise insertion raises an exception.
        /// </summary>
        private static void SetImage(XmlDocument xml, XmlElement binding, string uri)
        {
            var image = binding.SelectSingleNode("image") as XmlElement;
            if (image == null)
            {
                image = xml.CreateElement("image");
                image.SetAttribute("id", "1");
                binding.AppendChild(image);
            }
            image.SetAttribute("src", uri);
        }

        /// <summary>
        /// The app icon, in one place only. It is what the tile shows while there is
        /// no picture of the sender: a transparent PNG with no padding, because the
        /// manifest logos have the padding the system expects and on a 150 px tile
        /// that padding eats the drawing.
        /// </summary>
        private const string TileIconUri = "ms-appx:///Assets/TileIcon.png";

        /// <summary>
        /// The same mark, drawn at the wide tile's proportion (310x150 and its 240%
        /// version). The square asset is not reused here on purpose: the system
        /// scales it to fill the wide tile and crops it, and the mark came out cut.
        /// </summary>
        private const string TileWideIconUri = "ms-appx:///Assets/TileIconWide.png";

        private static string Cut(string value, int max)
        {
            string text = value ?? "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "\u2026";
        }
    }
}
