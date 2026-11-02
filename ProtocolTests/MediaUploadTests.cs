using System.Collections.Generic;
using System.Threading.Tasks;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Media;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WaProto;

namespace ProtocolTests
{
    /// <summary>
    /// The upload, and the four things that decide whether the receiver can open the file:
    /// the address the POST goes to, the body (which `MediaDownload` has to be able to take
    /// apart again), the two hashes (over the plaintext and over the body), and the size
    /// check that has to happen before a byte is encrypted.
    ///
    /// The reference for each: `Utils/messages-media.js:643-697` (the URL and the headers),
    /// `:309-383` (the body and the hashes), `:134` (the base64 hash in a URL), and
    /// `:336-343` (the ceiling, `content length exceeded when encrypting`).
    /// </summary>
    public static class MediaUploadTests
    {
        [Test]
        public static void TheUploadAddressIsTheOneTheReferenceBuilds()
        {
            var fetcher = new ScriptedUploader();
            UploadedMedia media = Upload(fetcher, "a photo's bytes", "image", "image/jpeg", null);

            Assert.IsTrue(fetcher.LastUrl.StartsWith("https://media-a.whatsapp.net/mms/image/"),
                "the host and the path map decide the address: <" + fetcher.LastUrl + ">");

            string name = MediaUpload.ForUrl(media.FileEncSha256);

            Assert.IsTrue(fetcher.LastUrl.EndsWith("?auth=tok3n&token=" + name),
                "the token is the hash again, and the auth is the same in both places: <" + fetcher.LastUrl + ">");
            Assert.IsFalse(name.Contains("+") || name.Contains("/") || name.Contains("="),
                "the hash travels URL-safe, without padding: <" + name + ">");

            Assert.AreEqual("application/octet-stream", fetcher.LastHeader("Content-Type"));
            Assert.AreEqual("https://web.whatsapp.com", fetcher.LastHeader("Origin"));
            Assert.AreEqual("https://mmg.whatsapp.net/v/t62/sent", media.Url);
            Assert.AreEqual("/v/t62/sent", media.DirectPath);
        }

        [Test]
        public static void TheEncryptedBodyDecryptsBackToTheFile()
        {
            var fetcher = new ScriptedUploader();
            byte[] plaintext = System.Text.Encoding.UTF8.GetBytes("the file, exactly as it was");

            UploadedMedia media = Upload(fetcher, plaintext, "document", "application/pdf", "report.pdf");

            byte[] back = MediaDownload.Open(fetcher.LastBody, media.MediaKey, media.MediaType,
                media.FileSha256, media.FileEncSha256, media.FileLength, "the upload");

            Assert.ByteArraysEqual(plaintext, back);
        }

        [Test]
        public static void TheHashesAreTheOnesTheReceiverChecks()
        {
            var fetcher = new ScriptedUploader();
            byte[] plaintext = System.Text.Encoding.UTF8.GetBytes("a voice note");

            UploadedMedia media = Upload(fetcher, plaintext, "audio", "audio/ogg; codecs=opus", null);

            // fileSha256 is over the plaintext, which is what the download checks after the
            // decrypt; fileEncSha256 is over the body, the ten-byte mac included.
            Assert.ByteArraysEqual(Sha256.Hash(plaintext), media.FileSha256);
            Assert.ByteArraysEqual(Sha256.Hash(fetcher.LastBody), media.FileEncSha256);
            Assert.AreEqual((long)plaintext.Length, media.FileLength);
        }

        [Test]
        public static void AnOversizeFileIsRefusedBeforeUpload()
        {
            var fetcher = new ScriptedUploader();

            var conn = new MediaConnInfo();
            conn.Auth = "tok3n";
            conn.Hosts.Add("media-a.whatsapp.net");
            conn.MaxContentLength = 16;

            var plaintext = new byte[17];

            ProtocolException failed = null;

            try
            {
                MediaUpload.SendAsync(fetcher, conn, plaintext, MediaKeys.Image, "image/jpeg", null,
                    new FixedRandom(), 0, 0, null).GetAwaiter().GetResult();
            }
            catch (ProtocolException error)
            {
                failed = error;
            }

            Assert.IsTrue(failed != null, "a file WhatsApp will not take is refused here");
            Assert.IsTrue(failed.Message.Contains("17") && failed.Message.Contains("16"),
                "and the sentence carries both numbers: <" + failed.Message + ">");
            Assert.IsTrue(fetcher.LastUrl == null, "and nothing was uploaded");
        }

        [Test]
        public static void AHostThatAnswersNothingIsTriedPast()
        {
            var fetcher = new ScriptedUploader();
            fetcher.Silent.Add("media-a.whatsapp.net");

            var conn = new MediaConnInfo();
            conn.Auth = "tok3n";
            conn.Hosts.Add("media-a.whatsapp.net");
            conn.Hosts.Add("media-b.whatsapp.net");

            UploadedMedia media = MediaUpload.SendAsync(fetcher, conn,
                System.Text.Encoding.UTF8.GetBytes("a file"), MediaKeys.Image, "image/jpeg", null,
                new FixedRandom(), 0, 0, null).GetAwaiter().GetResult();

            Assert.IsTrue(media.Url != null, "the second host took it");
            Assert.IsTrue(fetcher.LastUrl.StartsWith("https://media-b.whatsapp.net/"), "and the address says so");
        }

        [Test]
        public static void TheUploadedFieldsRoundTripThroughTheMessage()
        {
            var fetcher = new ScriptedUploader();
            UploadedMedia media = Upload(fetcher, "una foto", "image", "image/jpeg", null);

            Message built = MediaUpload.Image(media, "ciao");
            Message read = Message.Decode(built.Encode());

            MediaInfo info = MediaMessages.Of(read);

            Assert.IsTrue(info != null, "the message this client built is one it can read back");
            Assert.AreEqual(MediaKeys.Image, info.Type);
            Assert.AreEqual("ciao", info.Caption);
            Assert.AreEqual(media.DirectPath, info.DirectPath);
            Assert.AreEqual(media.Url, info.Url);
            Assert.AreEqual(media.FileLength, info.FileLength);
            Assert.AreEqual(media.MediaKeyTimestamp, info.MediaKeyTimestamp);
            Assert.ByteArraysEqual(media.MediaKey, info.MediaKey);
            Assert.ByteArraysEqual(media.FileSha256, info.FileSha256);
            Assert.ByteArraysEqual(media.FileEncSha256, info.FileEncSha256);
            Assert.IsTrue(info.IsFetchable, "a receiver can fetch what this client sent");
        }

        [Test]
        public static void ADocumentsNameAndBytesSurviveTheMessage()
        {
            var fetcher = new ScriptedUploader();
            UploadedMedia media = Upload(fetcher, "the report", "document", "application/pdf", "report.pdf");

            MediaInfo info = MediaMessages.Of(Message.Decode(MediaUpload.Document(media, "ecco").Encode()));

            Assert.AreEqual(MediaKeys.Document, info.Type);
            Assert.AreEqual("report.pdf", info.FileName);
            Assert.AreEqual("ecco", info.Caption);
        }

        [Test]
        public static void AVoiceNoteKeepsItsSecondsAndItsShape()
        {
            var fetcher = new ScriptedUploader();
            UploadedMedia media = Upload(fetcher, "a note", "audio", "audio/ogg; codecs=opus", null);

            MediaInfo info = MediaMessages.Of(Message.Decode(MediaUpload.Audio(media, 7, true).Encode()));

            Assert.AreEqual(MediaKeys.Audio, info.Type);
            Assert.AreEqual((uint)7, info.Seconds);
            Assert.IsTrue(info.IsVoiceNote, "ptt is 6 in a voice note and it survives the round trip");
        }

        /// <summary>
        /// `ptt` only for Opus in Ogg, because that is what the other end plays. A recording
        /// of this phone is AAC in an MP4: flagged as a voice note it is a bubble that plays
        /// nothing, and as an audio message it plays.
        /// </summary>
        [Test]
        public static void OnlyOpusInOggCanBeAVoiceNote()
        {
            Assert.IsTrue(MediaUpload.CanBeVoiceNote("audio/ogg; codecs=opus"));
            Assert.IsTrue(MediaUpload.CanBeVoiceNote("audio/ogg"));
            Assert.IsTrue(MediaUpload.CanBeVoiceNote("AUDIO/OGG"), "the case of a file name is not a decision");

            Assert.IsFalse(MediaUpload.CanBeVoiceNote("audio/mp4"),
                "a recording of this phone is AAC in an MP4: it travels as an audio message");
            Assert.IsFalse(MediaUpload.CanBeVoiceNote("audio/amr"));
            Assert.IsFalse(MediaUpload.CanBeVoiceNote(null));
        }

        private static UploadedMedia Upload(ScriptedUploader fetcher, string text, string mediaType,
            string mimeType, string fileName)
        {
            return Upload(fetcher, System.Text.Encoding.UTF8.GetBytes(text), mediaType, mimeType, fileName);
        }

        private static UploadedMedia Upload(ScriptedUploader fetcher, byte[] plaintext, string mediaType,
            string mimeType, string fileName)
        {
            return Upload(fetcher, plaintext, mediaType, mimeType, fileName, 0, 0, null);
        }

        private static UploadedMedia Upload(ScriptedUploader fetcher, byte[] plaintext, string mediaType,
            string mimeType, string fileName, int width, int height, byte[] thumbnail)
        {
            var conn = new MediaConnInfo();
            conn.Auth = "tok3n";
            conn.Hosts.Add("media-a.whatsapp.net");

            return MediaUpload.SendAsync(fetcher, conn, plaintext, mediaType, mimeType, fileName,
                new FixedRandom(), width, height, thumbnail).GetAwaiter().GetResult();
        }

        /// <summary>
        /// A photo's size and its thumbnail, where the receiver's own table says they are:
        /// `height` 6, `width` 7 and `jpegThumbnail` 13.
        ///
        /// Why the numbers are read off the encoded bytes and not only through
        /// `MediaMessages`: the reader and the writer are two tables, and they were wrong in
        /// the same place - both said 16 for the thumbnail - so a round trip through them
        /// agreed with itself and a real phone drew nothing. The reference is
        /// `.tools/ref/package/WAProto/index.d.ts`, `IImageMessage`, in field order.
        /// </summary>
        [Test]
        public static void APhotoCarriesItsSizeAndItsThumbnailWhereTheReceiverLooks()
        {
            var fetcher = new ScriptedUploader();
            byte[] thumbnail = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
            byte[] plaintext = System.Text.Encoding.UTF8.GetBytes("un ritratto");

            UploadedMedia media = Upload(fetcher, plaintext, MediaKeys.Image, "image/jpeg", null,
                1200, 800, thumbnail);

            byte[] encoded = MediaUpload.Image(media, "ciao").ImageMessage;

            Assert.AreEqual(1200L, (long)Varint(encoded, 7), "width is 7");
            Assert.AreEqual(800L, (long)Varint(encoded, 6), "height is 6");
            Assert.ByteArraysEqual(thumbnail, Bytes(encoded, 13));
            Assert.IsTrue(Bytes(encoded, 16) == null,
                "16 in a photo is firstScanLength, a varint: a thumbnail there is a photo the "
                + "receiver cannot parse");

            MediaInfo read = MediaMessages.Of(Message.Decode(MediaUpload.Image(media, "ciao").Encode()));
            Assert.AreEqual(1200, read.Width);
            Assert.AreEqual(800, read.Height);
            Assert.ByteArraysEqual(thumbnail, read.Thumbnail);
        }

        /// <summary>A video's table is its own: the cover is 15, and the size is 9 and 10.</summary>
        [Test]
        public static void AVideoCarriesItsCoverInFieldFifteen()
        {
            var fetcher = new ScriptedUploader();
            byte[] thumbnail = { 0xFF, 0xD8, 0xFF, 0xDB };

            UploadedMedia media = Upload(fetcher, System.Text.Encoding.UTF8.GetBytes("un filmato"),
                MediaKeys.Video, "video/mp4", null, 640, 360, thumbnail);

            byte[] encoded = MediaUpload.Video(media, "", 7).VideoMessage;

            Assert.ByteArraysEqual(thumbnail, Bytes(encoded, 15));
            Assert.AreEqual(640L, (long)Varint(encoded, 10), "width is 10 in a video");
            Assert.AreEqual(360L, (long)Varint(encoded, 9), "height is 9 in a video");
            Assert.IsTrue(Bytes(encoded, 16) == null,
                "16 in a video is contextInfo, which is not a cover");
        }

        /// <summary>One length-delimited field of an encoded media message, or null.</summary>
        private static byte[] Bytes(byte[] encoded, int number)
        {
            var reader = new ProtoReader(encoded);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == number)
                {
                    if (field.WireType != 2) return null;

                    return reader.ReadBytes();
                }

                reader.SkipField(field.WireType);
            }

            return null;
        }

        /// <summary>One varint field of an encoded media message, or -1 when it carries none.</summary>
        private static long Varint(byte[] encoded, int number)
        {
            var reader = new ProtoReader(encoded);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == number)
                {
                    if (field.WireType != 0) return -1;

                    return (long)reader.ReadVarint();
                }

                reader.SkipField(field.WireType);
            }

            return -1;
        }

        /// <summary>
        /// A host that takes the body and answers where it put it. The bytes it was handed
        /// are kept, because the two things this test file is about - the body and its hash -
        /// are only checkable on what actually left.
        /// </summary>
        private sealed class ScriptedUploader : IMediaFetcher
        {
            /// <summary>Hosts that answer with an empty object, the reference's "upload failed, reason".</summary>
            public readonly List<string> Silent = new List<string>();

            public byte[] LastBody;
            public string LastUrl;
            private List<KeyValuePair<string, string>> _headers;

            public string LastHeader(string name)
            {
                if (_headers == null) return null;

                for (int i = 0; i < _headers.Count; i++)
                {
                    if (_headers[i].Key == name) return _headers[i].Value;
                }

                return null;
            }

            public Task<byte[]> GetAsync(string url)
            {
                throw new ProtocolException("this fetcher does not download");
            }

            public Task<byte[]> PostAsync(string url, byte[] body,
                IList<KeyValuePair<string, string>> headers)
            {
                LastUrl = url;
                LastBody = body;
                _headers = new List<KeyValuePair<string, string>>(headers);

                for (int i = 0; i < Silent.Count; i++)
                {
                    if (url.StartsWith("https://" + Silent[i] + "/")) return Task.FromResult(Empty());
                }

                return Task.FromResult(Answer());
            }

            private static byte[] Empty()
            {
                return System.Text.Encoding.UTF8.GetBytes("{}");
            }

            private static byte[] Answer()
            {
                return System.Text.Encoding.UTF8.GetBytes(
                    "{\"url\":\"https:\\/\\/mmg.whatsapp.net\\/v\\/t62\\/sent\",\"direct_path\":\"/v/t62/sent\"}");
            }
        }

        /// <summary>A media key that is the same every run, so a failure reads the same twice.</summary>
        private sealed class FixedRandom : IRandomSource
        {
            public byte[] Bytes(int count)
            {
                var bytes = new byte[count];
                for (int i = 0; i < count; i++) bytes[i] = (byte)(0x30 + i);

                return bytes;
            }
        }
    }
}
