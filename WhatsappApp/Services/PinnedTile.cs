using System;
using System.Collections.Generic;
using System.IO.IsolatedStorage;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Phone.Shell;
using Windows.Storage;
using Windows.Storage.Streams;
using WhatsappApp.Protocol.Client;

namespace WhatsappApp.Services
{
    /// <summary>
    /// A chat tile on the Start screen. Windows Phone Silverlight exposes the shell's
    /// FlipTileData model rather than WinRT's secondary-tile notification XML.
    /// </summary>
    public static class PinnedTile
    {
        /// <summary>The prefix carried by the tile's launch route.</summary>
        public const string ArgumentPrefix = "chat:";

        private const char ArgumentSeparator = '|';
        private const int MaxTileIdLength = 64;
        private const int MaxImageBytes = 200 * 1024;
        private const string LocalFolderPrefix = "ms-appdata:///local/";
        private const string ShellContentFolder = "Shared/ShellContent";
        private const string ShellContentUriPrefix = "isostore:/Shared/ShellContent/";
        private const string AppIcon = "Assets/TileIcon.png";
        private const string AppWideIcon = "Assets/TileIconWide.png";

        private static readonly Dictionary<string, string> Sharp =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, int> Attempts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static readonly HashSet<string> Asking = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Refused = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object Pictures = new object();
        private static readonly SerialQueue ShellImageWrites = new SerialQueue();
        private static readonly SerialQueue TileWrites = new SerialQueue();

        private const int MaxPictureAttempts = 3;

        /// <summary>
        /// Returns the stable, shell-safe identity used for the chat. Secondary tiles carry
        /// the original chat id in their query route; this value remains available to callers
        /// that need a compact tile identity.
        /// </summary>
        public static string TileIdFor(string chatId)
        {
            var builder = new StringBuilder("chat-");
            if (chatId == null) return builder.ToString();

            for (int i = 0; i < chatId.Length && builder.Length < MaxTileIdLength; i++)
            {
                char c = chatId[i];
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }

            return builder.ToString();
        }

        /// <summary>Whether this chat already has a secondary tile on Start.</summary>
        public static bool IsPinned(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return false;

            try
            {
                return FindTile(chatId) != null;
            }
            catch (Exception ex)
            {
                Diag.Failed("PinnedTile/IsPinned", ex);
                return false;
            }
        }

        /// <summary>
        /// Pins the chat or removes its tile. Silverlight's synchronous ShellTile APIs do not
        /// expose the WinRT RequestCreateAsync/RequestDeleteAsync result, so the final state is
        /// checked against the shell's active-tile list.
        /// </summary>
        public static async Task<bool> ToggleAsync(string chatId, string name, string avatarData, string body)
        {
            if (string.IsNullOrEmpty(chatId)) return false;

            string display = string.IsNullOrEmpty(name) ? chatId : Cut(name, 40);

            try
            {
                ShellTile existing = FindTile(chatId);
                if (existing != null)
                {
                    existing.Delete();
                    bool remains = FindTile(chatId) != null;
                    Diag.Ok("pinned tile: " + chatId + (remains ? " kept" : " removed"));
                    if (!remains) Forget(chatId);
                    return remains;
                }

                FlipTileData initial = new FlipTileData();
                initial.Title = display;
                initial.BackTitle = display;
                initial.BackContent = " ";
                initial.SmallBackgroundImage = PackageImage(AppIcon);
                initial.BackgroundImage = PackageImage(AppIcon);
                initial.BackBackgroundImage = PackageImage(AppIcon);
                initial.WideBackgroundImage = PackageImage(AppWideIcon);
                initial.WideBackBackgroundImage = PackageImage(AppWideIcon);
                initial.Count = 0;

                ShellTile.Create(NavigationUri(chatId, display), initial, true);
                bool created = FindTile(chatId) != null;
                if (!created) return false;

                Forget(chatId);
                await QueueRenderAsync(chatId, display, 0, PictureFor(chatId, avatarData), body, true);
                AskForPicture(chatId, display, 0, body, true);

                Diag.Ok("pinned tile: " + chatId + " added");
                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("PinnedTile/Toggle", ex);
                return IsPinned(chatId);
            }
        }

        /// <summary>The chat's unread count changed; update it when the chat is pinned.</summary>
        public static void Update(string chatId, string name, int count, string avatarData, string body)
        {
            Update(chatId, name, count, avatarData, body, true);
        }

        /// <summary>
        /// Updates the visible tile. When the count is unknown, Count is left unset so the
        /// shell keeps its existing count rather than treating a cache miss as zero.
        /// </summary>
        public static void Update(string chatId, string name, int count, string avatarData, string body,
            bool countKnown)
        {
            if (string.IsNullOrEmpty(chatId)) return;
            if (!IsPinned(chatId)) return;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("PinnedTile/update",
                QueueRenderAsync(chatId, name, count, PictureFor(chatId, avatarData), body, countKnown));
#pragma warning restore 4014
        }

        /// <summary>Asks WhatsApp for a pinned chat's full-size picture, within a per-run budget.</summary>
        public static void AskForPicture(string chatId, string name, int count, string body, bool countKnown)
        {
            if (string.IsNullOrEmpty(chatId) || !IsPinned(chatId)) return;
            if (!NativeConnection.Instance.IsLoggedIn) return;

            lock (Pictures)
            {
                string sharp;
                if (Sharp.TryGetValue(chatId, out sharp) || Asking.Contains(chatId)) return;

                int attempts;
                Attempts.TryGetValue(chatId, out attempts);
                if (attempts >= MaxPictureAttempts) return;

                Attempts[chatId] = attempts + 1;
                Asking.Add(chatId);
            }

#pragma warning disable 4014
            Guarded.RunGuardedAsync("PinnedTile/picture",
                FetchPictureAsync(chatId, name, count, body, countKnown));
#pragma warning restore 4014
        }

        /// <summary>
        /// Copies a composed WinRT local-folder image into the isolated-storage location the
        /// Silverlight shell can read. Package-relative paths are returned unchanged.
        /// </summary>
        internal static Task<Uri> ShellImageUriAsync(string image)
        {
            if (string.IsNullOrEmpty(image)) return Task.FromResult<Uri>(null);
            if (!image.StartsWith(LocalFolderPrefix, StringComparison.Ordinal))
            {
                return Task.FromResult(new Uri(image, UriKind.RelativeOrAbsolute));
            }

            return ShellImageWrites.RunAsync(delegate { return CopyShellImageAsync(image); });
        }

        /// <summary>The chat id a secondary-tile navigation URI names, or null for other launches.</summary>
        public static string ChatFromArguments(string arguments)
        {
            string payload = TileArguments(arguments);
            if (string.IsNullOrEmpty(payload) || !payload.StartsWith(ArgumentPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            string rest = payload.Substring(ArgumentPrefix.Length);
            int split = rest.IndexOf(ArgumentSeparator);
            return split < 0 ? rest : rest.Substring(0, split);
        }

        /// <summary>The display name stored with a chat's secondary-tile route.</summary>
        public static string NameFromArguments(string arguments)
        {
            string payload = TileArguments(arguments);
            if (string.IsNullOrEmpty(payload) || !payload.StartsWith(ArgumentPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            string rest = payload.Substring(ArgumentPrefix.Length);
            int split = rest.IndexOf(ArgumentSeparator);
            return split < 0 ? null : rest.Substring(split + 1);
        }

        private static string TileArguments(string arguments)
        {
            if (string.IsNullOrEmpty(arguments)) return null;
            if (arguments.StartsWith(ArgumentPrefix, StringComparison.Ordinal)) return arguments;

            int queryStart = arguments.IndexOf('?');
            if (queryStart < 0 || queryStart == arguments.Length - 1) return null;

            string[] fields = arguments.Substring(queryStart + 1).Split('&');
            for (int i = 0; i < fields.Length; i++)
            {
                int equals = fields[i].IndexOf('=');
                if (equals < 0) continue;

                string key = Uri.UnescapeDataString(fields[i].Substring(0, equals));
                if (key != "tile") continue;

                return Uri.UnescapeDataString(fields[i].Substring(equals + 1));
            }

            return null;
        }

        private static Uri NavigationUri(string chatId, string name)
        {
            string arguments = ArgumentPrefix + chatId + ArgumentSeparator + name;
            return new Uri("/Pages/HomePage.xaml?tile=" + Uri.EscapeDataString(arguments), UriKind.Relative);
        }

        private static ShellTile FindTile(string chatId)
        {
            foreach (ShellTile tile in ShellTile.ActiveTiles)
            {
                Uri route = tile.NavigationUri;
                if (route == null) continue;
                if (string.Equals(ChatFromArguments(route.OriginalString), chatId, StringComparison.Ordinal))
                {
                    return tile;
                }
            }

            return null;
        }

        private static string PictureFor(string chatId, string given)
        {
            lock (Pictures)
            {
                string sharp;
                return Sharp.TryGetValue(chatId, out sharp) ? sharp : given;
            }
        }

        private static async Task FetchPictureAsync(string chatId, string name, int count, string body,
            bool countKnown)
        {
            try
            {
                AvatarAnswer picture = await NativeConnection.Instance.RequestAvatarAsync(chatId, false);
                byte[] jpeg = picture == null ? null : picture.Jpeg;

                if (jpeg == null || jpeg.Length == 0)
                {
                    lock (Pictures) Attempts[chatId] = MaxPictureAttempts;
                    Diag.Ok("pinned tile: " + chatId + " has no picture to give");
                    return;
                }

                string sharp = Convert.ToBase64String(jpeg);
                lock (Pictures) Sharp[chatId] = sharp;

                Diag.Ok("pinned tile: " + jpeg.Length + " byte(s) of picture for " + chatId
                    + ", composed again from it");
                await QueueRenderAsync(chatId, name, count, sharp, body, countKnown);
            }
            catch (Exception error)
            {
                Diag.Failed("PinnedTile/picture " + chatId, error);
            }
            finally
            {
                lock (Pictures) Asking.Remove(chatId);
            }
        }

        private static Task QueueRenderAsync(string chatId, string name, int count, string avatarData, string body,
            bool countKnown)
        {
            return TileWrites.RunAsync(delegate
            {
                return RenderAsync(chatId, name, count, avatarData, body, countKnown);
            });
        }

        private static async Task RenderAsync(string chatId, string name, int count, string avatarData, string body,
            bool countKnown)
        {
            lock (Pictures)
            {
                if (Refused.Contains(chatId)) return;
            }

            TileCard card;
            try
            {
                card = await StoreImageAsync(chatId, avatarData);
            }
            catch (Exception ex)
            {
                Diag.Failed("PinnedTile/compose " + chatId, ex);
                return;
            }

            Uri square = card == null ? PackageImage(AppIcon) : await ShellImageUriAsync(card.Square);
            Uri wide = card == null ? PackageImage(AppWideIcon) : await ShellImageUriAsync(card.Wide);
            Uri small = card == null ? PackageImage(AppIcon) : await ShellImageUriAsync(card.Small);

            string sender = string.IsNullOrEmpty(name) ? chatId : name;
            string unread = countKnown ? NotificationService.UnreadText(Math.Max(0, count)) : "";
            TileLines lines = TileText.For(sender, body, unread);

            var data = new FlipTileData();
            data.Title = NonEmpty(lines.First);
            data.BackTitle = NonEmpty(sender);
            data.BackContent = NonEmpty(lines.Second);
            data.WideBackContent = NonEmpty(lines.Second);
            data.SmallBackgroundImage = small;
            data.BackgroundImage = square;
            data.BackBackgroundImage = square;
            data.WideBackgroundImage = wide;
            data.WideBackBackgroundImage = wide;
            if (countKnown) data.Count = Math.Min(Math.Max(0, count), 99);

            ShellTile tile = FindTile(chatId);
            if (tile == null) return;

            Exception failure = null;
            try
            {
                tile.Update(data);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure != null) Refuse(chatId, failure);
        }

        private static string NonEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? " " : value;
        }

        private static Uri PackageImage(string path)
        {
            return new Uri(path, UriKind.Relative);
        }

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

        private static void Forget(string chatId)
        {
            lock (Pictures)
            {
                Refused.Remove(chatId);
                Attempts.Remove(chatId);
            }
        }

        private static void Refuse(string chatId, Exception error)
        {
            lock (Pictures)
            {
                if (!Refused.Add(chatId)) return;
            }

            Diag.Failed("PinnedTile/update " + chatId, error);
            Diag.Ok("pinned tile: the Silverlight shell refused the tile for " + chatId
                + "; it is left unchanged until it is pinned again or the app restarts");
        }

        private static async Task<Uri> CopyShellImageAsync(string image)
        {
            string fileName = image.Substring(LocalFolderPrefix.Length);
            if (string.IsNullOrEmpty(fileName) || fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0)
            {
                throw new InvalidOperationException("The tile image path is not a local file name");
            }

            string shellName = "wa-" + fileName;
            string shellPath = ShellContentFolder + "/" + shellName;
            bool exists;
            using (IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForApplication())
            {
                exists = store.FileExists(shellPath);
            }

            if (!exists)
            {
                IStorageItem item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(fileName);
                StorageFile source = item as StorageFile;
                if (source == null)
                {
                    Diag.Ok("pinned tile: the composed image is missing, using the app mark");
                    return PackageImage(AppIcon);
                }
                IBuffer buffer = await FileIO.ReadBufferAsync(source);
                byte[] bytes = buffer.ToArray();
                if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
                {
                    throw new InvalidOperationException("The tile image is outside the shell size limit");
                }

                using (IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!store.DirectoryExists(ShellContentFolder)) store.CreateDirectory(ShellContentFolder);
                    if (!store.FileExists(shellPath))
                    {
                        using (IsolatedStorageFileStream output = store.CreateFile(shellPath))
                        {
                            output.Write(bytes, 0, bytes.Length);
                        }
                    }
                }
            }

            return new Uri(ShellContentUriPrefix + shellName, UriKind.Absolute);
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
