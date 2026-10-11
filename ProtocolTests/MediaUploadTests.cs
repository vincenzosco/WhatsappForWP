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
                    new FixedRandom()).GetAwaiter().GetResult();
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
                new FixedRandom()).GetAwaiter().GetResult();

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

        private static UploadedMedia Upload(ScriptedUploader fetcher, string text, string mediaType,
            string mimeType, string fileName)
        {
            return Upload(fetcher, System.Text.Encoding.UTF8.GetBytes(text), mediaType, mimeType, fileName);
        }

        private static UploadedMedia Upload(ScriptedUploader fetcher, byte[] plaintext, string mediaType,
            string mimeType, string fileName)
        {
            var conn = new MediaConnInfo();
            conn.Auth = "tok3n";
            conn.Hosts.Add("media-a.whatsapp.net");

            return MediaUpload.SendAsync(fetcher, conn, plaintext, mediaType, mimeType, fileName,
                new FixedRandom()).GetAwaiter().GetResult();
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
