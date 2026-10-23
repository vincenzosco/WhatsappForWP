using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;
using Windows.UI.StartScreen;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The tile of one chat on the Start screen.
    ///
    /// Why it exists: a conversation worth reaching in one tap is worth a tile of its own.
    /// The official client lets a chat be pinned and the tile carries that person's picture
    /// and how many of their messages are waiting; a tap opens the chat.
    ///
    /// **Every size shows the picture and the number**, which is what the request asked and
    /// what the four bindings below are for: 71x71 (the picture, and the count in the badge,
    /// because that size has no line of text), 150x150, 310x150 wide and 310x310. They all
    /// travel in **one** notification, so a change cannot arrive on one size and not on the
    /// others - the same rule `NotificationService` uses for the app's own tile.
    ///
    /// The count is the chat's **unread** messages, not a lifetime total: it goes down when
    /// the chat is opened, which is what makes the tile a thing to act on rather than a
    /// receipt. `DataService` publishes it from the one place that changes it.
    ///
    /// What the tile is not: a copy of the app's tile. That one says "somebody is waiting";
    /// this one says "this person is, and this is their chat".
    /// </summary>
    public static class PinnedTile
    {
        /// <summary>The argument prefix a tile carries, which is how a tap finds its chat.</summary>
        public const string ArgumentPrefix = "chat:";

        /// <summary>
        /// What separates the chat from the name in the arguments. A `|` cannot be part of a
        /// JID or of a name short enough to be a tile's title, so the two cannot be confused.
        /// </summary>
        private const char ArgumentSeparator = '|';

        /// <summary>WP8.1 refuses a tile id over this length, and one with other characters.</summary>
        private const int MaxTileIdLength = 64;

        private const string SmallLogo = "ms-appx:///Assets/Square71x71Logo.png";
        private const string MediumLogo = "ms-appx:///Assets/Logo.png";
        private const string WideLogo = "ms-appx:///Assets/WideLogo.png";

        /// <summary>The app mark, for a chat whose picture has not arrived.</summary>
        private const string AppIcon = "ms-appx:///Assets/TileIcon.png";

        /// <summary>The same mark at the wide tile's proportion: a square one is cropped.</summary>
        private const string AppWideIcon = "ms-appx:///Assets/TileIconWide.png";

        /// <summary>
        /// The tile id of a chat: stable across runs (a second pin of the same chat is the
        /// same tile), and made only of what the platform accepts - letters, digits and
        /// underscores, at most 64 of them.
        /// </summary>
        public static string TileIdFor(string chatId)
        {
            var builder = new StringBuilder("chat-");

            for (int i = 0; i < chatId.Length && builder.Length < MaxTileIdLength; i++)
            {
                char c = chatId[i];
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }

            return builder.ToString();
        }

        /// <summary>Whether this chat already has a tile on the Start screen.</summary>
        public static bool IsPinned(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return false;

            try
            {
                return SecondaryTile.Exists(TileIdFor(chatId));
            }
            catch (Exception ex)
            {
                Diag.Failed("PinnedTile/IsPinned", ex);
                return false;
            }
        }

        /// <summary>
        /// Pins the chat, or takes its tile away when it has one. True when a tile is on the
        /// Start screen at the end.
        ///
        /// `RequestCreateAsync` shows the system's own sheet, which asks the person to
        /// confirm and lets them rename the tile: the app does not ask twice.
        /// </summary>
        public static async Task<bool> ToggleAsync(string chatId, string name, string avatarData, string body)
        {
            if (string.IsNullOrEmpty(chatId)) return false;

            string display = string.IsNullOrEmpty(name) ? chatId : Cut(name, 40);

            try
            {
                if (IsPinned(chatId))
                {
                    bool removed = await new SecondaryTile(TileIdFor(chatId)).RequestDeleteAsync();
                    Diag.Ok("pinned tile: " + chatId + (removed ? " removed" : " kept"));
                    return !removed;
                }

                // The short name and the display name are the person's name; the logo is the
                // square one until the picture arrives. The `TileOptions` constructor is the
                // obsolete one on this platform (CS0618): the name is put on the sizes it
                // fits by `VisualElements`, which is the shape that replaced it.
                var tile = new SecondaryTile(
                    TileIdFor(chatId),
                    display,
                    ArgumentPrefix + chatId + ArgumentSeparator + display,
                    new Uri(MediumLogo),
                    TileSize.Default);

                tile.DisplayName = display;
                tile.VisualElements.ShowNameOnSquare150x150Logo = true;
                tile.VisualElements.ShowNameOnWide310x150Logo = true;

                // The sizes the Start screen can draw this tile at. Each one is given the
                // asset it needs before the tile exists: a size left out falls back to the
                // square logo and loses its proportion.
                tile.VisualElements.Square71x71Logo = new Uri(SmallLogo);
                tile.VisualElements.Wide310x150Logo = new Uri(WideLogo);
                tile.VisualElements.Square310x310Logo = new Uri(MediumLogo);

                bool created = await tile.RequestCreateAsync();
                if (!created) return false;

                // The tile exists with the package's logos; the picture and the count come
                // on a notification, which is the only thing a running app may change.
                await RenderAsync(chatId, display, 0, avatarData, body);

                Diag.Ok("pinned tile: " + chatId + " added");
                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("PinnedTile/Toggle", ex);
                return IsPinned(chatId);
            }
        }

        /// <summary>
        /// The chat's count changed: its tile says so. Called from the one place that changes
        /// a row's unread count, and safe on a chat with no tile - it does nothing.
        /// </summary>
        public static void Update(string chatId, string name, int count, string avatarData, string body)
        {
            if (string.IsNullOrEmpty(chatId)) return;
            if (!IsPinned(chatId)) return;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("PinnedTile/update",
                RenderAsync(chatId, name, count, avatarData, body));
#pragma warning restore 4014
        }

        /// <summary>
        /// The chat a tile's arguments name, or null when the launch did not come from one.
        /// </summary>
        public static string ChatFromArguments(string arguments)
        {
            return PartOfArguments(arguments, true);
        }

        /// <summary>
        /// The name the tile was created with, carried in its arguments so a first start from
        /// a tile can name the chat before the list has loaded.
        /// </summary>
        public static string NameFromArguments(string arguments)
        {
            return PartOfArguments(arguments, false);
        }

        private static string PartOfArguments(string arguments, bool chat)
        {
            if (string.IsNullOrEmpty(arguments)) return null;
            if (!arguments.StartsWith(ArgumentPrefix, StringComparison.Ordinal)) return null;

            string rest = arguments.Substring(ArgumentPrefix.Length);
            int split = rest.IndexOf(ArgumentSeparator);

            if (chat) return split < 0 ? rest : rest.Substring(0, split);
            return split < 0 ? null : rest.Substring(split + 1);
        }

        /// <summary>
        /// Draws the tile in every size the Start screen can ask for, and puts the count on
        /// the badge as well: at 71x71 there is no line to write a number in, so the badge is
        /// what carries it there.
        ///
        /// The two lines are the app tile's two lines (`TileText`): the name in the template's
        /// large font and the last message under it, with the count taking the second line only
        /// when there is no message to quote. The request of 23 October 2026 was that the two
        /// tiles read the same way.
        /// </summary>
        private static async Task RenderAsync(string chatId, string name, int count, string avatarData, string body)
        {
            string tileId = TileIdFor(chatId);

            TileCard card = await StoreImageAsync(chatId, avatarData);
            string image = card == null ? null : card.Square;
            string wide = image == null ? AppWideIcon : TileImage.WideOf(image);

            string sender = string.IsNullOrEmpty(name) ? chatId : name;
            TileLines lines = TileText.For(sender, body, NotificationService.UnreadText(count));

            var updater = TileUpdateManager.CreateTileUpdaterForSecondaryTile(tileId);

            var xml = TileUpdateManager.GetTemplateContent(
                TileTemplateType.TileSquare150x150PeekImageAndText02);
            var visual = xml.SelectSingleNode("/tile/visual") as XmlElement;

            // After the check and not before it: a template with no visual is a tile this
            // method cannot draw, and clearing first left the chat's tile empty with nothing
            // put back in its place.
            if (visual == null) return;

            updater.Clear();

            // The medium tile: who this chat is, in the template's large font, and what they
            // said under it - the app tile's own two lines, so the two are read the same way.
            var medium = visual.SelectSingleNode("binding") as XmlElement;
            Fill(xml, medium, lines.First, lines.Second, image ?? AppIcon);

            // The wide tile and the large one say the same thing. The wide one asks for the
            // wide card: the square card in a 2.07:1 binding is filled and cropped, which
            // magnifies the face and takes its top and chin off.
            Add(xml, visual, TileTemplateType.TileWide310x150PeekImageAndText02,
                lines.First, lines.Second, wide);
            Add(xml, visual, TileTemplateType.TileSquare310x310ImageAndText02,
                lines.First, lines.Second, image ?? AppIcon);

            // 71x71: the picture, and nothing else fits. The number travels on the badge.
            AddSmall(xml, visual, image ?? AppIcon);

            updater.Update(new TileNotification(xml));

            SetBadge(tileId, count);
        }

        /// <summary>One binding for another size, imported from that size's own template.</summary>
        private static void Add(XmlDocument xml, XmlElement visual, TileTemplateType template,
            string first, string second, string image)
        {
            try
            {
                var other = TileUpdateManager.GetTemplateContent(template);
                var binding = other.SelectSingleNode("/tile/visual/binding");
                if (binding == null) return;

                var imported = xml.ImportNode(binding, true) as XmlElement;
                if (imported == null) return;

                visual.AppendChild(imported);
                Fill(xml, imported, first, second, image);
            }
            catch (Exception ex)
            {
                // A size the phone refuses cannot take the others with it.
                Diag.Failed("PinnedTile/Add", ex);
            }
        }

        /// <summary>
        /// The small tile: one picture and no text. Its template ships without an `image`
        /// element, so one is created - with the notification's document, not with the
        /// template's, or inserting it raises.
        /// </summary>
        private static void AddSmall(XmlDocument xml, XmlElement visual, string image)
        {
            try
            {
                var other = TileUpdateManager.GetTemplateContent(TileTemplateType.TileSquare71x71Image);
                var binding = other.SelectSingleNode("/tile/visual/binding");
                if (binding == null) return;

                var imported = xml.ImportNode(binding, true) as XmlElement;
                if (imported == null) return;

                visual.AppendChild(imported);

                var element = imported.SelectSingleNode("image") as XmlElement;
                if (element == null)
                {
                    element = xml.CreateElement("image");
                    element.SetAttribute("id", "1");
                    imported.AppendChild(element);
                }

                element.SetAttribute("src", image);
            }
            catch (Exception ex)
            {
                Diag.Failed("PinnedTile/AddSmall", ex);
            }
        }

        /// <summary>What one binding says: two lines and the picture.</summary>
        private static void Fill(XmlDocument xml, XmlElement binding, string first, string second, string image)
        {
            if (binding == null) return;

            var texts = binding.SelectNodes("text");
            if (texts.Length > 0 && first.Length > 0) texts[0].AppendChild(xml.CreateTextNode(first));
            if (texts.Length > 1 && second.Length > 0) texts[1].AppendChild(xml.CreateTextNode(second));

            var element = binding.SelectSingleNode("image") as XmlElement;
            if (element == null)
            {
                element = xml.CreateElement("image");
                element.SetAttribute("id", "1");
                binding.AppendChild(element);
            }

            element.SetAttribute("src", image);
        }

        /// <summary>
        /// The number on the tile's badge: 0 removes it. It is what says the count at 71x71,
        /// and the only shape of it the shell draws over the picture.
        /// </summary>
        private static void SetBadge(string tileId, int count)
        {
            try
            {
                var updater = BadgeUpdateManager.CreateBadgeUpdaterForSecondaryTile(tileId);

                if (count <= 0)
                {
                    updater.Clear();
                    return;
                }

                var xml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
                var badge = (XmlElement)xml.SelectSingleNode("/badge");
                badge.SetAttribute("value", Math.Min(count, 99).ToString(CultureInfo.InvariantCulture));
                updater.Update(new BadgeNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("PinnedTile/SetBadge", ex);
            }
        }

        /// <summary>
        /// The chat's picture as a file the tile can point at, or null when there is none:
        /// a tile wants a path, not the bytes.
        ///
        /// It is a *card*, not the 96x96 preview: `TileImage` crops the picture to its centre
        /// square, scales it once to the tile and lets it cover the tile, because a template
        /// with an image paints it over the whole tile and a small face blown up to 150 or 310
        /// pixels is a magnified face, not a portrait - and a margin around it is a border
        /// nobody asked for. A chat with no picture gets the app mark inside the same card, at
        /// half the tile, so the glyph keeps its air.
        ///
        /// The name is `pin-…`, of its own, so it cannot collide with the picture the app's
        /// own tile keeps for the same chat and the two cannot take each other's file away.
        /// </summary>
        private static Task<TileCard> StoreImageAsync(string chatId, string avatarData)
        {
            return TileImage.StoreAsync(ImageFileName(chatId), avatarData);
        }

        private static string ImageFileName(string chatId)
        {
            var builder = new StringBuilder("pin-");
            for (int i = 0; i < chatId.Length; i++)
            {
                char c = chatId[i];
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            builder.Append(".jpg");

            return builder.ToString();
        }

        private static string Cut(string value, int max)
        {
            string text = value ?? "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "\u2026";
        }
    }
}
