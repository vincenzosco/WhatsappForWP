using System.Collections.Generic;
using System.Threading.Tasks;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Media;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WaProto;
using ProtocolTests.Vectors;

namespace ProtocolTests
{
    /// <summary>
    /// The media keys and the download, against a blob built with the reference's own scheme.
    ///
    /// Three assertions matter more than the rest and they are the three that a plausible but
    /// wrong implementation fails: the three keys are the reference's (a different HKDF info
    /// string or a different expansion length gives three different keys and a body nobody
    /// can open), a moved ciphertext byte is refused *before* the decrypt, and a body that
    /// decrypts but does not match `fileSha256` is refused after it.
    /// </summary>
    public static class MediaTests
    {
        [Test]
        public static void TheThreeKeysAreTheReferenceOnes()
        {
            byte[] mediaKey = Hex(MediaFixture.MediaKey);

            Assert.ByteArraysEqual(Hex(MediaFixture.Iv), MediaKeys.Iv(mediaKey, MediaKeys.Image));
            Assert.ByteArraysEqual(Hex(MediaFixture.CipherKey), MediaKeys.CipherKey(mediaKey, MediaKeys.Image));
            Assert.ByteArraysEqual(Hex(MediaFixture.MacKey), MediaKeys.MacKey(mediaKey, MediaKeys.Image));
        }

        [Test]
        public static void TheInfoStringIsTheOneTheReferenceBuilds()
        {
            Assert.AreEqual(MediaFixture.Info, MediaKeys.Info(MediaKeys.Image));
            Assert.AreEqual("WhatsApp Audio Keys", MediaKeys.Info(MediaKeys.Audio));
            Assert.AreEqual("WhatsApp Video Keys", MediaKeys.Info(MediaKeys.Video));
            Assert.AreEqual("WhatsApp Document Keys", MediaKeys.Info(MediaKeys.Document));

            Assert.Throws<ProtocolException>(delegate { MediaKeys.Info("sticker"); });
        }

        [Test]
        public static void ABlobFromTheReferenceDecryptsToItsPlaintext()
        {
            var fetcher = new ScriptedFetcher();
            fetcher.Add("https://mmg.whatsapp.net/v/t62/x", Hex(MediaFixture.Body));

            byte[] plaintext = MediaDownload.FetchAsync(fetcher, Hex(MediaFixture.MediaKey),
                MediaKeys.Image, "/v/t62/x", null, Hex(MediaFixture.FileSha256),
                Hex(MediaFixture.FileEncSha256), Hex(MediaFixture.Plaintext).Length)
                .GetAwaiter().GetResult();

            Assert.ByteArraysEqual(Hex(MediaFixture.Plaintext), plaintext);
        }

        [Test]
        public static void AShortBodyIsRefusedBeforeTheDecrypt()
        {
            byte[] body = Hex(MediaFixture.Body);
            var shortBody = new byte[6];
            System.Buffer.BlockCopy(body, 0, shortBody, 0, shortBody.Length);

            Assert.Throws<ProtocolException>(delegate
            {
                MediaDownload.Open(shortBody, Hex(MediaFixture.MediaKey), MediaKeys.Image,
                    null, null, 0, "the media");
            });
        }

        [Test]
        public static void AMovedCiphertextByteIsANamedFailure()
        {
            ProtocolException failed = null;

            try
            {
                MediaDownload.Open(Hex(MediaFixture.MovedByte), Hex(MediaFixture.MediaKey),
                    MediaKeys.Image, null, null, 0, "the media");
            }
            catch (ProtocolException error)
            {
                failed = error;
            }

            Assert.IsTrue(failed != null, "a body with a moved byte is not accepted");
            Assert.IsTrue(failed.Message.Contains("signature"), "and the failure names the signature, not the cipher: <" + failed.Message + ">");
        }

        [Test]
        public static void AWrongSha256IsRefusedAfterTheDecrypt()
        {
            byte[] wrong = Hex(MediaFixture.FileSha256);
            wrong[0] = (byte)(wrong[0] ^ 0xff);

            ProtocolException failed = null;

            try
            {
                MediaDownload.Open(Hex(MediaFixture.Body), Hex(MediaFixture.MediaKey),
                    MediaKeys.Image, wrong, null, 0, "the media");
            }
            catch (ProtocolException error)
            {
                failed = error;
            }

            Assert.IsTrue(failed != null, "a body that decrypts to something else is refused");
            Assert.IsTrue(failed.Message.Contains("fileSha256"), "and the sentence names fileSha256: <" + failed.Message + ">");
        }

        [Test]
        public static void AMediaWithNoAddressIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate { MediaDownload.Address(null, null); });
            Assert.AreEqual("https://fixed/x", MediaDownload.Address("https://fixed/x", null));
            Assert.AreEqual("https://mmg.whatsapp.net/v/t62/y", MediaDownload.Address("/v/t62/y", null));
        }

        [Test]
        public static void APhotoIsFoundInTheMessageThatCarriesIt()
        {
            // The envelope the server sends, built the way it does: a Message whose
            // imageMessage is field 3, holding the four media fields that matter.
            byte[] image = new WhatsappApp.Protocol.WaProto.ProtoWriter()
                .Text(2, "image/jpeg")
                .Text(3, "una foto")
                .Bytes(4, Hex(MediaFixture.FileSha256))
                .Varint(5, 51)
                .Bytes(8, Hex(MediaFixture.MediaKey))
                .Bytes(9, Hex(MediaFixture.FileEncSha256))
                .Text(11, "/v/t62/x")
                .ToArray();
            byte[] message = new WhatsappApp.Protocol.WaProto.ProtoWriter()
                .Bytes(WhatsappApp.Protocol.WaProto.Message.ImageField, image)
                .ToArray();

            MediaInfo info = WhatsappApp.Protocol.WaProto.MediaMessages.Of(
                WhatsappApp.Protocol.WaProto.Message.Decode(message));

            Assert.IsTrue(info != null, "a photo in a message is found, not skipped");
            Assert.AreEqual(MediaKeys.Image, info.Type);
            Assert.AreEqual("image/jpeg", info.MimeType);
            Assert.AreEqual("una foto", info.Caption);
            Assert.AreEqual("/v/t62/x", info.DirectPath, "directPath is 11 in a photo, and it is the path a download uses");
            Assert.AreEqual(51L, info.FileLength);
            Assert.IsTrue(info.IsFetchable, "a key and a path: this one can be downloaded");
            Assert.ByteArraysEqual(Hex(MediaFixture.FileSha256), info.FileSha256);
        }

        [Test]
        public static void AVoiceNoteIsReadByItsOwnTable()
        {
            byte[] audio = new WhatsappApp.Protocol.WaProto.ProtoWriter()
                .Text(2, "audio/ogg; codecs=opus")
                .Bytes(3, Hex(MediaFixture.FileSha256))
                .Varint(4, 51)
                .Varint(5, 7)
                .Varint(6, 1)
                .Bytes(7, Hex(MediaFixture.MediaKey))
                .Bytes(8, Hex(MediaFixture.FileEncSha256))
                .Text(9, "/v/t62/voice")
                .ToArray();

            MediaInfo info = WhatsappApp.Protocol.WaProto.MediaMessages.Read(audio, MediaKeys.Audio);

            Assert.AreEqual("/v/t62/voice", info.DirectPath, "9 in a voice note, not 11: the numbers differ per type");
            Assert.AreEqual(51L, info.FileLength, "fileLength is 4 here, not 5");
            Assert.AreEqual((uint)7, info.Seconds);
            Assert.IsTrue(info.IsVoiceNote);
            Assert.ByteArraysEqual(Hex(MediaFixture.MediaKey), info.MediaKey);
        }

        /// <summary>A fetcher that answers the URLs it was given and refuses the rest.</summary>
        private sealed class ScriptedFetcher : IMediaFetcher
        {
            private readonly Dictionary<string, byte[]> _bodies = new Dictionary<string, byte[]>();

            public void Add(string url, byte[] body)
            {
                _bodies[url] = body;
            }

            public Task<byte[]> GetAsync(string url)
            {
                byte[] body;
                if (!_bodies.TryGetValue(url, out body))
                {
                    throw new ProtocolException("nothing is stored at " + url);
                }

                return Task.FromResult(body);
            }
        }

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)System.Convert.ToInt32(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }
    }
}
