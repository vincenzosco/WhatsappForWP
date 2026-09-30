using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The alerts this app can raise while it runs: a toast and the number on the
    /// icon. There is no cloud service behind it, so a message that arrives with
    /// the app suspended produces nothing: the TCP connection belongs to the app,
    /// and WP8.1 closes it when it suspends it. Real push notifications would
    /// require an external service this project does not have.
    ///
    /// Every call is guarded: a phone that rejects the toast must not bring down
    /// the message reception.
    /// </summary>
    public static class NotificationService
    {
        /// <summary>
        /// A message arrived from a person, in a chat that is not the open one:
        /// the tile rotates to it, with their picture and their name.
        ///
        /// People only: a group has no single face, and putting a group picture on
        /// the tile would say even less than nothing. The group name, without a
        /// picture, is left out as well.
        ///
        /// The filesystem cannot be touched from here (we are on the UI thread that
        /// just received the message), so the update starts and carries on by
        /// itself.
        /// </summary>
        public static void RotateSenderTile(string chatId, string name, string avatarData)
        {
            if (!SettingsService.NotificationsEnabled) return;
            if (string.IsNullOrEmpty(chatId)) return;
            if (chatId.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) return;

#pragma warning disable 4014
            UpdateSenderTileAsync(chatId, name, avatarData);
#pragma warning restore 4014
        }

        /// <summary>
        /// The sender tile. The notification queue is what really makes the tile
        /// rotate: without EnableNotificationQueue a tile holds a single alert, and
        /// the second replaces the first instead of adding to it.
        ///
        /// The picture comes from the bytes the adapter already sent (base64): they
        /// are written as they are to a local file, because a tile wants a path,
        /// not an in-memory image. Over the system ceiling the picture is left
        /// alone and the tile stays text-only: better a name than a tile the phone
        /// rejects.
        /// </summary>
        private static async Task UpdateSenderTileAsync(string chatId, string name, string avatarData)
        {
            try
            {
                var updater = TileUpdateManager.CreateTileUpdaterForApplication();
                if (!_queueEnabled)
                {
                    updater.EnableNotificationQueue(true);
                    _queueEnabled = true;
                }

                string image = await StoreTileImageAsync(chatId, avatarData);

                var xml = image == null
                    ? TileUpdateManager.GetTemplateContent(TileTemplateType.TileSquare150x150Text02)
                    : TileUpdateManager.GetTemplateContent(TileTemplateType.TileSquare150x150PeekImageAndText01);

                var texts = xml.GetElementsByTagName("text");
                if (texts.Length > 0) texts[0].AppendChild(xml.CreateTextNode(Cut(name, 30)));

                if (image != null)
                {
                    var binding = xml.SelectSingleNode("/tile/visual/binding") as XmlElement;
                    if (binding != null) SetImage(xml, binding, image);
                }

                updater.Update(new TileNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.UpdateSenderTileAsync", ex);
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
        /// rejected: the text tile is preferred.
        /// </summary>
        private const int MaxTileImageBytes = 200 * 1024;

        /// <summary>
        /// True after the notification queue has been enabled. It is enabled once
        /// only: it is a property of the updater, not of the single update, and
        /// calling it on every message changes nothing.
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
        /// The unread count, in the two places WP8.1 can show it: the icon badge
        /// and the tile. They are two different notifications and one can fail
        /// without the other, so each has its own guard. With 0 everything is
        /// cleared: the tile goes back to the manifest one.
        /// </summary>
        public static void SetUnread(int count)
        {
            SetBadge(count);
            SetTileBadge(count);
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
        /// Puts the app icon on the tile, in the two sizes WP8.1 can update with an
        /// icon: 150x150 and 71x71 (the IconWithBadge template).
        ///
        /// Worth noting, because it is the point: **the badge draws the number, not
        /// the tile.** This update keeps the tile on the app icon while the badge is
        /// active, and brings it back to the manifest one when there is nothing
        /// left to read (Clear).
        ///
        /// The wide size is not touched: on WP8.1 the
        /// `TileWide310x150IconWithBadge` template does not exist, and the badge is
        /// drawn on the wide tile anyway, so the number is visible all the same.
        ///
        /// The icon, on the other hand, must be passed in: the IconWithBadge
        /// template does NOT take it from the manifest, it wants it in the payload.
        /// With an empty src the tile stays without an icon - and without raising
        /// anything, so silently.
        /// </summary>
        private static void SetTileBadge(int count)
        {
            try
            {
                var updater = TileUpdateManager.CreateTileUpdaterForApplication();
                if (count <= 0)
                {
                    updater.Clear();
                    return;
                }

                var xml = TileUpdateManager.GetTemplateContent(
                    TileTemplateType.TileSquare150x150IconWithBadge);
                var visual = (XmlElement)xml.SelectSingleNode("/tile/visual");
                if (visual == null) return;

                SetTileIcon(xml, visual, TileIconUri);
                AppendBinding(xml, visual, TileTemplateType.TileSquare71x71IconWithBadge);

                updater.Update(new TileNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.SetTileBadge", ex);
            }
        }

        /// <summary>
        /// Copies the binding of another template into the tile document. A node
        /// belongs to its own document, so it must be imported: appending it as is
        /// raises an exception.
        ///
        /// The imported binding is a fresh binding, so it wants its own icon like
        /// the other one: without it, the size that receives it draws without one.
        /// </summary>
        private static void AppendBinding(XmlDocument xml, XmlElement visual, TileTemplateType template)
        {
            var other = TileUpdateManager.GetTemplateContent(template);
            var binding = other.SelectSingleNode("/tile/visual/binding");
            if (binding == null) return;

            var imported = xml.ImportNode(binding, true) as XmlElement;
            if (imported == null) return;

            SetImage(xml, imported, TileIconUri);
            visual.AppendChild(imported);
        }

        /// <summary>The icon on the binding inside the template visual.</summary>
        private static void SetTileIcon(XmlDocument xml, XmlElement visual, string uri)
        {
            var binding = visual.SelectSingleNode("binding") as XmlElement;
            if (binding == null) return;
            SetImage(xml, binding, uri);
        }

        /// <summary>
        /// Writes the icon on the binding image, creating it if the template has
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
        /// The tile image, in one place only. It is a transparent PNG with no
        /// padding: the manifest logos have the padding the system expects, and on
        /// a 150 px tile that padding eats the drawing.
        /// </summary>
        private const string TileIconUri = "ms-appx:///Assets/TileIcon.png";

        private static string Cut(string value, int max)
        {
            string text = value ?? "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "\u2026";
        }
    }
}
