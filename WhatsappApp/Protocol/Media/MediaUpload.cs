using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// A file that is now on WhatsApp's servers: everything the message the receiver gets
    /// has to carry for them to fetch it back - the key, the address, the two hashes and
    /// the size - plus the two facts this client knows and the message does not need.
    /// </summary>
    public sealed class UploadedMedia
    {
        /// <summary>`image`, `video`, `audio` or `document`: the word the keys were derived with.</summary>
        public string MediaType;

        public string MimeType;
        public string FileName;

        public byte[] MediaKey;
        public string DirectPath;
        public string Url;
        public byte[] FileSha256;
        public byte[] FileEncSha256;
        public long FileLength;
        public long MediaKeyTimestamp;
        public byte[] JpegThumbnail;

        /// <summary>
        /// The ciphertext with its mac, built once by <see cref="Prepare"/> and put on the
        /// wire by <see cref="Body"/>. It is kept because encrypting the file twice - once
        /// for the hashes and once for the upload - is a second full AES pass over a video
        /// on a phone with 512 MB, for bytes that are identical by construction.
        /// </summary>
        public byte[] EncryptedBody;
    }

    /// <summary>
    /// Sending a file: encrypt it the way the receiver will decrypt it, upload the
    /// ciphertext, and build the message that says where it is.
    ///
    /// The scheme is the reference's (`@whiskeysockets/baileys`, `Utils/messages-media.js`):
    ///
    /// - the keys come off `MediaKeys` - the same three from the same 32-byte media key -
    ///   and the body is `AES-256-CBC(cipherKey, iv, file) || mac`, where the mac is the
    ///   first **ten** bytes of `HMAC-SHA256(macKey, iv || ciphertext)` (`encryptedStream`,
    ///   lines 309 to 383);
    /// - `fileSha256` is over the **plaintext** and `fileEncSha256` over the **body**
    ///   including the mac, which is what the receiver checks and therefore what is
    ///   pinned here (`ProtocolTests/MediaUploadTests.cs` recomputes both);
    /// - the upload is a `POST` to `https://{host}{/mms/<type>}/{enc}?auth=&token=`, with
    ///   `Content-Type: application/octet-stream` and `Origin: https://web.whatsapp.com`,
    ///   and the host answers `{ "url": ..., "direct_path": ... }`
    ///   (`getWAUploadToServer`, lines 643 to 697; the path map is `Defaults/index.js`
    ///   lines 85 to 93, the origin `Defaults/index.js:7`).
    ///
    /// The `/` and `+` of the base64 hash are turned into `-` and `_` and the padding is
    /// dropped before it goes in the URL (`encodeBase64EncodedStringForUpload`,
    /// `Utils/messages-media.js:134`) - in **both** places, as the reference does: the
    /// `auth` is percent-encoded and the hash is not, because the hash is already URL-safe
    /// by then.
    /// </summary>
    public static class MediaUpload
    {
        /// <summary>The origin every media request carries, upload and download alike.</summary>
        public const string Origin = "https://web.whatsapp.com";

        public const int MediaKeyLength = 32;

        /// <summary>The path each media type is uploaded to (`Defaults/index.js:85-93`).</summary>
        public static string PathFor(string mediaType)
        {
            if (mediaType == MediaKeys.Image) return "/mms/image";
            if (mediaType == MediaKeys.Video) return "/mms/video";
            if (mediaType == MediaKeys.Audio) return "/mms/audio";
            if (mediaType == MediaKeys.Document) return "/mms/document";

            throw new ProtocolException("not a media type this client uploads: " + mediaType);
        }

        /// <summary>
        /// The file and everything about it, short of where it now lives: the key, the body,
        /// the hashes and the sizes. Split out from <see cref="SendAsync"/> because the
        /// encryption is the half that has to be right, and it is worth being able to test
        /// it without a server.
        /// </summary>
        public static UploadedMedia Prepare(byte[] plaintext, string mediaType, string mimeType,
            string fileName, IRandomSource random)
        {
            if (plaintext == null || plaintext.Length == 0) throw new ProtocolException("there is no file to send");
            if (random == null) throw new ProtocolException("a media key needs randomness");

            // The type is validated here rather than at the far end of the upload: a
            // misspelled type would derive three wrong keys from the right media key, and
            // the receiver's failure would name a MAC, not the typo.
            MediaKeys.NameOf(mediaType);

            var media = new UploadedMedia();
            media.MediaType = mediaType;
            media.MimeType = mimeType;
            media.FileName = fileName;
            media.FileLength = plaintext.Length;
            media.MediaKey = random.Bytes(MediaKeyLength);
            media.MediaKeyTimestamp = NowSeconds();

            byte[] iv = MediaKeys.Iv(media.MediaKey, mediaType);
            byte[] cipherKey = MediaKeys.CipherKey(media.MediaKey, mediaType);
            byte[] macKey = MediaKeys.MacKey(media.MediaKey, mediaType);

            byte[] ciphertext = Aes.CbcEncrypt(cipherKey, iv, plaintext);
            byte[] mac = Hmac.Compute(macKey, Concat(iv, ciphertext));

            media.FileSha256 = Sha256.Hash(plaintext);

            // The body is built here and handed to `Body`, not built again there: the hash is
            // over exactly these bytes, so a second encryption would be the same work twice.
            media.EncryptedBody = Concat(ciphertext, Head(mac, MediaDownload.MacLength));
            media.FileEncSha256 = Sha256.Hash(media.EncryptedBody);

            return media;
        }

        /// <summary>
        /// The body that goes on the wire: the ciphertext with the ten-byte mac after it -
        /// exactly the shape <see cref="MediaDownload.Open"/> takes apart, which is what
        /// makes the download test the upload's own check.
        /// </summary>
        public static byte[] Body(byte[] plaintext, UploadedMedia media)
        {
            if (plaintext == null || media == null) throw new ProtocolException("there is no file to upload");
            if (media.EncryptedBody != null) return media.EncryptedBody;

            byte[] iv = MediaKeys.Iv(media.MediaKey, media.MediaType);
            byte[] cipherKey = MediaKeys.CipherKey(media.MediaKey, media.MediaType);
            byte[] macKey = MediaKeys.MacKey(media.MediaKey, media.MediaType);

            byte[] ciphertext = Aes.CbcEncrypt(cipherKey, iv, plaintext);

            return Concat(ciphertext, Head(Hmac.Compute(macKey, Concat(iv, ciphertext)), MediaDownload.MacLength));
        }

        /// <summary>
        /// Encrypts, uploads, and hands back where the file is. The hosts are tried in the
        /// order the server named them, and the first one that answers with an address wins
        /// - a host that answers without one (the reference's `upload failed, reason`) is a
        /// host that did not take the file.
        /// </summary>
        public static async Task<UploadedMedia> SendAsync(IMediaFetcher fetcher, MediaConnInfo conn,
            byte[] plaintext, string mediaType, string mimeType, string fileName, IRandomSource random)
        {
            if (fetcher == null) throw new ProtocolException("an upload needs something to upload with");
            if (conn == null || string.IsNullOrEmpty(conn.Auth)) throw new ProtocolException("there is no media host to upload to");

            // `Length` as a long and not `LongLength`: the WP8.1 reference metadata the phone
            // compiles against has no `LongLength` on an array (the desktop has it, which is
            // exactly the kind of difference the phone build exists to catch).
            if (conn.MaxContentLength > 0 && plaintext != null && (long)plaintext.Length > conn.MaxContentLength)
            {
                throw new ProtocolException("this file is " + plaintext.Length
                    + " bytes and WhatsApp accepts " + conn.MaxContentLength);
            }

            UploadedMedia media = Prepare(plaintext, mediaType, mimeType, fileName, random);
            byte[] body = Body(plaintext, media);

            string name = ForUrl(media.FileEncSha256);
            string query = "?auth=" + Uri.EscapeDataString(conn.Auth) + "&token=" + name;

            var headers = new List<KeyValuePair<string, string>>();
            headers.Add(new KeyValuePair<string, string>("Content-Type", "application/octet-stream"));
            headers.Add(new KeyValuePair<string, string>("Origin", Origin));

            string failed = null;

            for (int i = 0; i < conn.Hosts.Count; i++)
            {
                string url = "https://" + conn.Hosts[i] + PathFor(mediaType) + "/" + name + query;

                try
                {
                    byte[] answer = await fetcher.PostAsync(url, body, headers);

                    string text = answer == null ? null : System.Text.Encoding.UTF8.GetString(answer, 0, answer.Length);
                    string uploadedUrl = Json(text, "url");
                    string directPath = Json(text, "direct_path");

                    if (!string.IsNullOrEmpty(uploadedUrl) || !string.IsNullOrEmpty(directPath))
                    {
                        media.Url = uploadedUrl;
                        media.DirectPath = directPath;
                        return media;
                    }

                    failed = conn.Hosts[i] + " answered " + (text == null ? "nothing" : text);
                }
                catch (ProtocolException error)
                {
                    failed = conn.Hosts[i] + ": " + error.Message;
                }
            }

            throw new ProtocolException("the upload reached no host" + (failed == null ? "" : " - " + failed));
        }

        /// <summary>
        /// The picture message the receiver needs: the address, the key, both hashes, the
        /// size, the moment the key was made and any words that came with it. The field
        /// numbers are the ones `MediaMessages` reads a photo out of, which is the same
        /// table seen from the other side.
        /// </summary>
        public static Message Image(UploadedMedia media, string caption)
        {
            Check(media);

            var message = new Message();
            message.ImageMessage = new ProtoWriter()
                .Text(1, media.Url)
                .Text(2, media.MimeType)
                .Text(3, caption)
                .Bytes(4, media.FileSha256)
                .Varint(5, (ulong)media.FileLength)
                .Bytes(8, media.MediaKey)
                .Bytes(9, media.FileEncSha256)
                .Text(11, media.DirectPath)
                .Varint(12, (ulong)media.MediaKeyTimestamp)
                .Bytes(16, media.JpegThumbnail)
                .ToArray();

            return message;
        }

        /// <summary>A voice note: `ptt` is 6 and the seconds are 5, and it has no caption.</summary>
        public static Message Audio(UploadedMedia media, uint seconds, bool voiceNote)
        {
            Check(media);

            var message = new Message();
            message.AudioMessage = new ProtoWriter()
                .Text(1, media.Url)
                .Text(2, media.MimeType)
                .Bytes(3, media.FileSha256)
                .Varint(4, (ulong)media.FileLength)
                .Varint(5, seconds)
                .Varint(6, voiceNote ? 1u : 0u)
                .Bytes(7, media.MediaKey)
                .Bytes(8, media.FileEncSha256)
                .Text(9, media.DirectPath)
                .Varint(10, (ulong)media.MediaKeyTimestamp)
                .ToArray();

            return message;
        }

        /// <summary>A video: `fileSha256` is 3 here, `mediaKey` 6 and `directPath` 13.</summary>
        public static Message Video(UploadedMedia media, string caption, uint seconds)
        {
            Check(media);

            var message = new Message();
            message.VideoMessage = new ProtoWriter()
                .Text(1, media.Url)
                .Text(2, media.MimeType)
                .Bytes(3, media.FileSha256)
                .Varint(4, (ulong)media.FileLength)
                .Varint(5, seconds)
                .Bytes(6, media.MediaKey)
                .Text(7, caption)
                .Bytes(11, media.FileEncSha256)
                .Text(13, media.DirectPath)
                .Varint(14, (ulong)media.MediaKeyTimestamp)
                .Bytes(16, media.JpegThumbnail)
                .ToArray();

            return message;
        }

        /// <summary>A document: the name is 8 and `mediaKey` 7, and the caption is 20.</summary>
        public static Message Document(UploadedMedia media, string caption)
        {
            Check(media);

            var message = new Message();
            message.DocumentMessage = new ProtoWriter()
                .Text(1, media.Url)
                .Text(2, media.MimeType)
                .Text(3, media.FileName)
                .Bytes(4, media.FileSha256)
                .Varint(5, (ulong)media.FileLength)
                .Bytes(7, media.MediaKey)
                .Text(8, media.FileName)
                .Bytes(9, media.FileEncSha256)
                .Text(10, media.DirectPath)
                .Varint(11, (ulong)media.MediaKeyTimestamp)
                .Text(20, caption)
                .ToArray();

            return message;
        }

        /// <summary>The base64 hash as it goes in a URL: `-` and `_`, no padding.</summary>
        public static string ForUrl(byte[] hash)
        {
            if (hash == null) throw new ProtocolException("the upload address is built from the file's hash");

            return Convert.ToBase64String(hash)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        private static void Check(UploadedMedia media)
        {
            if (media == null) throw new ProtocolException("there is no uploaded media to build a message from");
            if (media.MediaKey == null || media.FileSha256 == null || media.FileEncSha256 == null)
            {
                throw new ProtocolException("this media has no key or no hash: it was never uploaded");
            }
        }

        private static uint NowSeconds()
        {
            return (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static byte[] Head(byte[] bytes, int count)
        {
            var head = new byte[count];
            Buffer.BlockCopy(bytes, 0, head, 0, count);

            return head;
        }

        private static byte[] Concat(byte[] left, byte[] right)
        {
            var joined = new byte[left.Length + right.Length];
            Buffer.BlockCopy(left, 0, joined, 0, left.Length);
            Buffer.BlockCopy(right, 0, joined, left.Length, right.Length);

            return joined;
        }

        /// <summary>
        /// One string field out of the host's small JSON answer. It is a scan and not a
        /// parser on purpose: the answer carries `url` and `direct_path`, this client wants
        /// nothing else out of it, and a DataContract type for two fields on a phone with no
        /// JSON library linked is more code than the scan it replaces.
        /// </summary>
        public static string Json(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return null;

            int at = json.IndexOf("\"" + name + "\"", StringComparison.Ordinal);
            if (at < 0) return null;

            int colon = json.IndexOf(':', at + name.Length + 2);
            if (colon < 0) return null;

            int quote = json.IndexOf('"', colon + 1);
            if (quote < 0) return null;

            var text = new System.Text.StringBuilder();

            for (int i = quote + 1; i < json.Length; i++)
            {
                char current = json[i];

                if (current == '"') return text.ToString();

                if (current == '\\')
                {
                    i++;
                    if (i >= json.Length) break;

                    char escaped = json[i];
                    if (escaped == 'n') text.Append('\n');
                    else if (escaped == 't') text.Append('\t');
                    else if (escaped == 'r') text.Append('\r');
                    else if (escaped == 'u' && i + 4 < json.Length)
                    {
                        int code;
                        if (int.TryParse(json.Substring(i + 1, 4),
                            System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out code))
                        {
                            text.Append((char)code);
                            i += 4;
                        }
                    }
                    else text.Append(escaped);

                    continue;
                }

                text.Append(current);
            }

            return null;
        }
    }
}
