using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using WhatsappApp.Protocol.Signal;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The phone's <see cref="ISessionStorage"/>: the blobs in `LocalFolder`, read once
    /// when the store opens and written through behind the caller.
    ///
    /// Why it is shaped this way, because the shape is not the obvious one. The core's
    /// storage interface is synchronous - a session's arithmetic is, and an encrypt cannot
    /// await a file - while the platform's file API is asynchronous all the way down. There
    /// is no `File.ReadAllBytes` in the WP8.1 projection to reach for, and blocking on the
    /// async one (`.Wait()`) would be a deadlock the moment a caller on the UI thread asked
    /// for a session: the continuation it is waiting for is queued to the thread that is
    /// waiting. So the answers come from memory - everything under `LocalFolder/protocol`
    /// is read once, at `OpenAsync` - and the disk catches up behind, one write at a time,
    /// in the order they were asked for.
    ///
    /// The cost is that the blobs are held, which is a few hundred bytes per conversation
    /// and a few hundred conversations on a device that has a chat list anyway.
    ///
    /// The write is a temporary file and then a replace, and that is not tidiness either:
    /// the alternative is a session file that was half written when the phone ran out of
    /// room, and a session with half a key does not fail where the write did - it fails as
    /// a conversation that cannot be read, days later. The record's own digest catches what
    /// this cannot (see `SessionRecord.Encode`), and the two together are why a torn write
    /// ends in a new session rather than in silence.
    /// </summary>
    public sealed class FileSessionStorage : ISessionStorage
    {
        /// <summary>The subfolder, so the app's own files and the protocol's do not share a directory.</summary>
        private const string FolderName = "protocol";

        /// <summary>The suffix a half-written file carries until it is complete.</summary>
        private const string TempSuffix = ".writing";

        /// <summary>The writes, in the order they were asked for. See `SerialQueue`.</summary>
        private static readonly SerialQueue Writes = new SerialQueue();

        private readonly StorageFolder _folder;
        private readonly Dictionary<string, byte[]> _blobs;

        private FileSessionStorage(StorageFolder folder, Dictionary<string, byte[]> blobs)
        {
            _folder = folder;
            _blobs = blobs;
        }

        /// <summary>
        /// Opens the storage: creates the folder if this is the first run, and reads every
        /// blob in it into memory. A file that cannot be read is reported and skipped: one
        /// damaged record is one conversation that starts over, not a device that cannot
        /// start.
        /// </summary>
        public static async Task<FileSessionStorage> OpenAsync()
        {
            StorageFolder folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                FolderName, CreationCollisionOption.OpenIfExists);

            var blobs = new Dictionary<string, byte[]>();
            IReadOnlyList<StorageFile> files = await folder.GetFilesAsync();

            for (int i = 0; i < files.Count; i++)
            {
                StorageFile file = files[i];
                if (file.Name.EndsWith(TempSuffix, StringComparison.Ordinal))
                {
                    // A temporary file at start-up is a write that was interrupted: the
                    // rename never happened, so the record it was replacing is still there
                    // under its real name and this one is a fragment. It is deleted rather
                    // than skipped, because skipping leaves one per crash in the folder
                    // forever and the folder is the app's.
                    try
                    {
                        await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    catch (Exception ex)
                    {
                        Diag.Failed("FileSessionStorage.Open/" + file.Name, ex);
                    }

                    continue;
                }

                try
                {
                    var buffer = await FileIO.ReadBufferAsync(file);
                    var bytes = new byte[buffer.Length];
                    using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
                    {
                        reader.ReadBytes(bytes);
                    }

                    blobs[KeyFor(file.Name)] = bytes;
                }
                catch (Exception ex)
                {
                    Diag.Failed("FileSessionStorage.Open/" + file.Name, ex);
                }
            }

            return new FileSessionStorage(folder, blobs);
        }

        /// <summary>
        /// Forgets every protocol blob: the sessions, the pre-keys and any half-written file a
        /// run that did not finish left behind.
        ///
        /// The caller is a pairing, and the reason is that an identity cannot outlive the state
        /// it was used to build: every session on disk was computed with the old identity key,
        /// and a Signal MAC is computed over **both** identities (`Cipher.Input`), so a phone
        /// that mints a new one at pairing and keeps the old sessions can read nothing from
        /// anybody - which is what the run of 2026-10-18 logged, message after message, from
        /// the account's own phone and from a contact alike.
        ///
        /// The pre-keys go for the same reason and one more: the new account's signed pre-key
        /// has the same **id** (1) as the old one and a different key, and a responder session
        /// checks the id - so leaving the old private key there would build the agreement with
        /// the wrong half of it and fail every MAC of the session it just created.
        ///
        /// Not wrapped in a try: a wipe that failed and was swallowed would leave exactly the
        /// state this exists to prevent, so the pairing reports it and the person scans again.
        /// </summary>
        public static async Task DeleteAllAsync()
        {
            StorageFolder folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                FolderName, CreationCollisionOption.OpenIfExists);

            await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }

        public bool TryRead(string key, out byte[] value)
        {
            if (key == null) throw new ArgumentNullException("key");

            // The blob is handed out as it is held: a caller that mutated it would be
            // changing what the next read returns, and the stores above treat what they
            // read as bytes they own.
            return _blobs.TryGetValue(key, out value);
        }

        public void Write(string key, byte[] value)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (value == null) throw new ArgumentNullException("value");

            _blobs[key] = value;
            Persist(key, value);
        }

        public void Remove(string key)
        {
            if (key == null) throw new ArgumentNullException("key");

            _blobs.Remove(key);

            // The file goes too, or a restart would find what was spent. The queue keeps
            // the delete behind any write of the same key that is still running, which is
            // the order the two were asked for.
            Writes.RunAsync(async delegate
            {
                try
                {
                    StorageFile existing = await _folder.GetFileAsync(FileName(key));
                    await existing.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }
                catch (Exception ex)
                {
                    Diag.Failed("FileSessionStorage.Remove/" + key, ex);
                }
            });
        }

        /// <summary>
        /// The write: to a temporary name first, then over the real one. Nothing is
        /// awaited - the caller is synchronous - so the failure is reported where it
        /// happens and the file is left as it was.
        /// </summary>
        private void Persist(string key, byte[] value)
        {
            Writes.RunAsync(async delegate
            {
                StorageFile temp = null;
                bool replaced = false;

                try
                {
                    temp = await _folder.CreateFileAsync(
                        FileName(key) + TempSuffix, CreationCollisionOption.ReplaceExisting);

                    await FileIO.WriteBytesAsync(temp, value);

                    await temp.RenameAsync(FileName(key), NameCollisionOption.ReplaceExisting);
                    replaced = true;
                }
                catch (Exception ex)
                {
                    Diag.Failed("FileSessionStorage.Write/" + key, ex);
                }

                // The cleanup is out here rather than in the catch, because C# 5 cannot
                // await in one - and it is gated on the rename having failed, because after
                // a rename the same object names the record itself and deleting it would
                // throw away the write that succeeded.
                if (replaced || temp == null) return;

                try
                {
                    await temp.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }
                catch (Exception cleanup)
                {
                    Diag.Failed("FileSessionStorage.Write-cleanup/" + key, cleanup);
                }
            });
        }

        /// <summary>
        /// A key as a file name. Keys are the stores' own - `session/&lt;jid&gt;`,
        /// `prekeys/one-time/&lt;id&gt;` - and a JID carries `@`, `.` and sometimes `:` for a
        /// device, none of which a filesystem guarantees. So everything outside
        /// `A-Za-z0-9._-` is written `%XX`, which is reversible and cannot collide: two keys
        /// that differ produce two names that differ.
        /// </summary>
        private static string FileName(string key)
        {
            // The key is escaped as UTF-8 *bytes*, one `%xx` each, and not as UTF-16 code
            // units: a code unit above U+00FF is more than two hex digits, and the decoder
            // beside this one reads exactly two - so a key holding one came back as a
            // different key and its session was lost on the next start. Not reachable with
            // today's keys (JIDs and pre-key ids are ASCII), which is why it is worth
            // closing before something adds a name to one.
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            var name = new StringBuilder(bytes.Length);

            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                bool safe = (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z')
                    || (b >= '0' && b <= '9') || b == '.' || b == '_' || b == '-';

                if (safe) name.Append((char)b);
                else name.Append('%').Append(b.ToString("x2"));
            }

            return name.ToString();
        }

        /// <summary>The key a file name stands for, for the blobs read at start-up.</summary>
        private static string KeyFor(string name)
        {
            var bytes = new List<byte>(name.Length);

            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] == '%' && i + 2 < name.Length)
                {
                    bytes.Add((byte)Convert.ToInt32(name.Substring(i + 1, 2), 16));
                    i += 2;
                    continue;
                }

                bytes.Add((byte)name[i]);
            }

            return Encoding.UTF8.GetString(bytes.ToArray(), 0, bytes.Count);
        }
    }
}
