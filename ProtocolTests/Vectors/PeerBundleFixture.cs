using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The two answers a first message to somebody reads, in the shapes the reference
    /// parses them from (`@whiskeysockets/baileys`):
    ///
    /// - the device list: `USyncDeviceProtocol.parser` reads
    ///   `devices &gt; device-list &gt; device[id]` inside a `usync` node, and the query that
    ///   asks for it is the `devices` protocol at `version` 2;
    /// - the bundles: one `user` per device JID, each carrying `registration`, `identity`,
    ///   `skey` (`id`, `value`, `signature`) and, when the server still had one, `key`
    ///   (`id`, `value`).
    ///
    /// The keys inside are a real credential set, drawn in the test that uses it, so the
    /// signature over the signed pre-key verifies exactly as a peer's own would.
    /// </summary>
    public static class PeerBundleFixture
    {
        public const string Peer = "15551230000@s.whatsapp.net";
        public const string PeerPhone = "15551230000:0@s.whatsapp.net";
        public const string PeerTablet = "15551230000:5@s.whatsapp.net";

        /// <summary>The one-time pre-key inside <see cref="BundleAnswer"/>, as the peer drew it.</summary>
        public static byte[] OneTimePrivate()
        {
            var key = new byte[Curve25519.KeyLength];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)(0x40 + i);
            return key;
        }

        /// <summary>The device list answer: the phone, and a tablet.</summary>
        public static BinaryNode DevicesAnswer()
        {
            return DevicesAnswer(new string[] { "0", "5" });
        }

        public static BinaryNode DevicesAnswer(IList<string> deviceIds)
        {
            var deviceList = new BinaryNode("device-list");
            var devices = new List<BinaryNode>();

            for (int i = 0; i < deviceIds.Count; i++)
            {
                var device = new BinaryNode("device");
                device.Attrs.Add("id", deviceIds[i]);
                devices.Add(device);
            }

            deviceList.Content = devices;

            var devicesNode = new BinaryNode("devices");
            devicesNode.Content = new List<BinaryNode> { deviceList };

            var user = new BinaryNode("user");
            user.Attrs.Add("jid", Peer);
            user.Content = new List<BinaryNode> { devicesNode };

            var list = new BinaryNode("list");
            list.Content = new List<BinaryNode> { user };

            var usync = new BinaryNode("usync");
            usync.Content = new List<BinaryNode> { list };

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("type", "result");
            iq.Content = new List<BinaryNode> { usync };

            return iq;
        }

        /// <summary>
        /// The bundle answer for one device. `withOneTimePreKey` false is what the server
        /// answers for a device that has spent them all: the bundle is still usable, and
        /// the session is built from the signed pre-key alone.
        /// </summary>
        public static BinaryNode BundleAnswer(Credentials peer, string deviceJid, bool withOneTimePreKey)
        {
            return BundleAnswer(peer, deviceJid, withOneTimePreKey, false);
        }

        /// <summary>The same, with the signed pre-key's signature moved by one byte.</summary>
        public static BinaryNode TamperedBundleAnswer(Credentials peer, string deviceJid)
        {
            return BundleAnswer(peer, deviceJid, true, true);
        }

        private static BinaryNode BundleAnswer(Credentials peer, string deviceJid, bool withOneTimePreKey, bool tamper)
        {
            var registration = new BinaryNode("registration");
            registration.Content = BigEndian(peer.RegistrationId, 4);

            var identity = new BinaryNode("identity");
            identity.Content = X3dh.Typed(peer.IdentityPublicKey);

            var signedPreKey = new BinaryNode("skey");
            var signedPreKeyId = new BinaryNode("id");
            signedPreKeyId.Content = BigEndian(peer.SignedPreKeyId, 3);
            var signedPreKeyValue = new BinaryNode("value");
            signedPreKeyValue.Content = X3dh.Typed(peer.SignedPreKeyPublic);

            byte[] signature = new byte[peer.SignedPreKeySignature.Length];
            System.Buffer.BlockCopy(peer.SignedPreKeySignature, 0, signature, 0, signature.Length);
            if (tamper) signature[0] = (byte)(signature[0] ^ 0x01);

            var signedPreKeySignature = new BinaryNode("signature");
            signedPreKeySignature.Content = signature;
            signedPreKey.Content = new List<BinaryNode>
            {
                signedPreKeyId, signedPreKeyValue, signedPreKeySignature
            };

            var user = new BinaryNode("user");
            user.Attrs.Add("jid", deviceJid);
            var children = new List<BinaryNode> { registration, identity, signedPreKey };

            if (withOneTimePreKey)
            {
                var preKey = new BinaryNode("key");
                var preKeyId = new BinaryNode("id");
                preKeyId.Content = new byte[] { 0x00, 0x00, 0x07 };
                var preKeyValue = new BinaryNode("value");
                preKeyValue.Content = X3dh.Typed(Curve25519.PublicKey(OneTimePrivate()));
                preKey.Content = new List<BinaryNode> { preKeyId, preKeyValue };
                children.Add(preKey);
            }

            user.Content = children;

            var list = new BinaryNode("list");
            list.Content = new List<BinaryNode> { user };

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("type", "result");
            iq.Content = new List<BinaryNode> { list };

            return iq;
        }

        /// <summary>The bundle the fixture builds, without going through the parser.</summary>
        public static PreKeyBundle Bundle(Credentials peer, string deviceJid, bool withOneTimePreKey)
        {
            IList<KeyValuePair<string, PreKeyBundle>> bundles =
                DeviceDirectory.Bundles(BundleAnswer(peer, deviceJid, withOneTimePreKey));

            for (int i = 0; i < bundles.Count; i++)
            {
                if (bundles[i].Key == deviceJid) return bundles[i].Value;
            }

            throw new ProtocolException("the fixture answer carried no bundle for " + deviceJid);
        }

        private static byte[] BigEndian(uint value, int bytes)
        {
            var result = new byte[bytes];
            for (int i = 0; i < bytes; i++)
            {
                result[bytes - 1 - i] = (byte)(value >> (8 * i));
            }

            return result;
        }
    }
}
