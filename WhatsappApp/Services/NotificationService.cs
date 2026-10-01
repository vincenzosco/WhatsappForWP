using System;
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
        public static void RememberSender(string chatId, string name, string avatarData)
        {
            if (!SettingsService.NotificationsEnabled) return;
            if (string.IsNullOrEmpty(chatId)) return;
            if (chatId.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) return;

#pragma warning disable 4014
            RememberSenderAsync(chatId, name, avatarData);
#pragma warning restore 4014
        }

        /// <summary>
        /// The picture of the sender is written to a file first, then the tile is
        /// drawn: a tile wants a path, not the bytes.
        ///
        /// The bytes come from the adapter already in base64, and are written as
        /// they are. Over the system ceiling the picture is left alone and the tile
        /// falls back to the app icon: better an icon than a tile the phone rejects.
        /// </summary>
        private static async Task RememberSenderAsync(string chatId, string name, string avatarData)
        {
            try
            {
                _senderName = name;
                _senderImage = await StoreTileImageAsync(chatId, avatarData);
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
        /// The tile, in one place and one notification: how many messages are
        /// waiting, then who the last one was, over their picture.
        ///
        /// The number is written here, and not left to the badge alone. On WP8.1 the
        /// badge is the only thing that draws a count on the tile, and a badge that
        /// the shell does not draw leaves the tile with no number at all - which is
        /// what happened on the phone this was fixed on: the tile showed the app
        /// icon and nothing else.
        ///
        /// One notification, replaced in place, and **no notification queue**. The
        /// queue kept one tile per message, and each of them counted the number of
        /// its own moment: the tile on screen went on showing the count of a message
        /// that had already been read. A queue of tiles can hold many faces, but not
        /// one true total, and the total is the thing the tile is for.
        /// </summary>
        private static void RenderTile()
        {
            try
            {
                var updater = TileUpdateManager.CreateTileUpdaterForApplication();

                // The queue belongs to the device, not to this run: a version of the
                // app that used it leaves it on, and a tile posted now would be added
                // to the old ones instead of replacing them. It is turned off once,
                // and its own failure cannot take the tile away: the old tiles are
                // cleared the first time everything has been read.
                if (!_queueDisabled)
                {
                    try { updater.EnableNotificationQueue(false); }
                    catch (Exception ex) { Diag.Failed("NotificationService.RenderTile/queue", ex); }
                    _queueDisabled = true;
                }

                if (_unread <= 0)
                {
                    updater.Clear();
                    return;
                }

                var xml = TileUpdateManager.GetTemplateContent(
                    TileTemplateType.TileSquare150x150PeekImageAndText02);
                var visual = xml.SelectSingleNode("/tile/visual") as XmlElement;
                if (visual == null) return;

                FillBinding(xml, visual.SelectSingleNode("binding") as XmlElement);
                AddWideBinding(xml, visual);
                AddSmallBinding(xml, visual);

                updater.Update(new TileNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.RenderTile", ex);
            }
        }

        /// <summary>
        /// What one binding of the notification says: how many are waiting, then the
        /// last sender over their picture.
        ///
        /// One helper for both sizes. The medium tile and the wide one say the same
        /// thing, so a change here cannot arrive on one of them and not on the other.
        /// </summary>
        private static void FillBinding(XmlDocument xml, XmlElement binding)
        {
            if (binding == null) return;

            var texts = binding.SelectNodes("text");
            if (texts.Length > 0) texts[0].AppendChild(xml.CreateTextNode(UnreadText(_unread)));
            // Nobody wrote yet - a count read back from the local copy at startup,
            // before any message: the line stays empty instead of holding an empty
            // node.
            string sender = Cut(_senderName, 30);
            if (texts.Length > 1 && sender.Length > 0) texts[1].AppendChild(xml.CreateTextNode(sender));

            SetImage(xml, binding, _senderImage ?? TileIconUri);
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
        private static void AddWideBinding(XmlDocument xml, XmlElement visual)
        {
            try
            {
                var binding = ImportBinding(xml, visual, TileTemplateType.TileWide310x150PeekImageAndText02);
                if (binding == null) return;

                FillBinding(xml, binding);
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
        /// How many are waiting, as the tile says it. One message is not "1
        /// messages": the two forms are two keys, so whoever translates writes both
        /// instead of guessing a plural rule.
        /// </summary>
        private static string UnreadText(int count)
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

        /// <summary>The count the tile is showing, and the one the badge shows.</summary>
        private static int _unread;

        /// <summary>
        /// True after the notification queue has been turned off. It is a property
        /// of the updater, not of the single update, and the tile it belongs to is
        /// read again on every message.
        /// </summary>
        private static bool _queueDisabled;

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
            _unread = count < 0 ? 0 : count;
            RenderTile();
            SetBadge(_unread);
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

        private static string Cut(string value, int max)
        {
            string text = value ?? "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "\u2026";
        }
    }
}
