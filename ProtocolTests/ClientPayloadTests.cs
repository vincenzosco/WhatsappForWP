using System;
using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The pairing half of the protocol, offline: the payload the server reads, the
    /// credentials that have to survive a restart, the string a phone scans, and the two
    /// signatures of a pairing reply.
    ///
    /// Nothing here talks to WhatsApp. What proves the payload against the server is
    /// `node tools/test-protocol.js --connect --pair`, and this file is what makes a
    /// change to it visible before that run happens.
    /// </summary>
    public static class ClientPayloadTests
    {
        private const uint RegistrationId = 0x01020304;
        private const uint SignedPreKeyId = 0x00a0b0c0;

        [Test]
        public static void The_pairing_payload_carries_the_device_and_no_account()
        {
            byte[] identity = Filled(32, 0x22);
            byte[] signedPreKey = Filled(32, 0x44);
            byte[] signature = Filled(64, 0x55);

            byte[] payload = ClientPayload.Registration(RegistrationId, identity, SignedPreKeyId,
                signedPreKey, signature, DeviceProps.Encode(1, 0, 0));

            var reader = new ProtoReader(payload);
            byte[] userAgent = null;
            byte[] webInfo = null;
            byte[] pairingData = null;
            int passive = -1;
            int pull = -1;
            bool sawUsername = false;

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.WireType == 2 && field.Number == ClientPayload.UserAgentField) userAgent = reader.ReadBytes();
                else if (field.WireType == 2 && field.Number == ClientPayload.WebInfoField) webInfo = reader.ReadBytes();
                else if (field.WireType == 2 && field.Number == ClientPayload.DevicePairingDataField) pairingData = reader.ReadBytes();
                else if (field.WireType == 0 && field.Number == ClientPayload.PassiveField) passive = (int)reader.ReadVarint();
                else if (field.WireType == 0 && field.Number == ClientPayload.PullField) pull = (int)reader.ReadVarint();
                else if (field.Number == ClientPayload.UsernameField) sawUsername = true;
                else reader.SkipField(field.WireType);
            }

            Assert.IsFalse(sawUsername, "a companion with no account must not name one");
            Assert.AreEqual(0, passive);
            Assert.AreEqual(0, pull);
            Assert.IsTrue(userAgent != null, "the payload must carry the user agent");
            Assert.IsTrue(webInfo != null, "the payload must carry the web info");
            Assert.IsTrue(pairingData != null, "the payload must carry the pairing data");

            AssertRegistration(pairingData, identity, signedPreKey, signature);
            AssertUserAgent(userAgent);
            AssertWebInfo(webInfo);
        }

        [Test]
        public static void The_login_payload_names_the_account_and_asks_for_the_backlog()
        {
            byte[] payload = ClientPayload.Login(393401234567, 8);

            var reader = new ProtoReader(payload);
            ulong username = 0;
            uint device = 0;
            int passive = -1;
            int pull = -1;

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.WireType == 0 && field.Number == ClientPayload.UsernameField) username = reader.ReadVarint();
                else if (field.WireType == 0 && field.Number == ClientPayload.DeviceField) device = (uint)reader.ReadVarint();
                else if (field.WireType == 0 && field.Number == ClientPayload.PassiveField) passive = (int)reader.ReadVarint();
                else if (field.WireType == 0 && field.Number == ClientPayload.PullField) pull = (int)reader.ReadVarint();
                else reader.SkipField(field.WireType);
            }

            Assert.AreEqual((ulong)393401234567, username);
            Assert.AreEqual((uint)8, device);
            Assert.AreEqual(1, passive);
            Assert.AreEqual(1, pull);
        }

        [Test]
        public static void Credentials_are_clamped_the_way_signal_draws_them()
        {
            // A source of bytes the clamping visibly changes: all 0xff, which the ladder
            // would read as a different scalar than the signer does.
            Credentials credentials = Credentials.Create(new CountingRandom());

            Assert.AreEqual(248, credentials.IdentityPrivateKey[0] & 0xf8);
            Assert.AreEqual(0, credentials.IdentityPrivateKey[31] & 0x80);
            Assert.AreEqual(64, credentials.IdentityPrivateKey[31] & 0x40);

            Assert.AreEqual(248, credentials.NoisePrivateKey[0] & 0xf8);
            Assert.AreEqual(0, credentials.NoisePrivateKey[31] & 0x80);
            Assert.AreEqual(64, credentials.NoisePrivateKey[31] & 0x40);

            // The stored public keys must be the public keys of the stored private ones,
            // or the server checks a pair that is not a pair.
            Assert.ByteArraysEqual(Curve25519.PublicKey(credentials.IdentityPrivateKey), credentials.IdentityPublicKey);
            Assert.ByteArraysEqual(Curve25519.PublicKey(credentials.NoisePrivateKey), credentials.NoisePublicKey);

            Assert.IsTrue(credentials.RegistrationId > 0 && credentials.RegistrationId <= (1 << 14) - 1,
                "a registration id is 14 bits and never zero");

            // The signed pre-key's signature covers 0x05 || the key, and verifies with
            // the identity key - the same check the server makes when it stores it.
            Assert.IsTrue(XEdDsa.Verify(credentials.IdentityPublicKey,
                Credentials.SignedPreKeyToSign(credentials.SignedPreKeyPublic),
                credentials.SignedPreKeySignature));
        }

        [Test]
        public static void A_session_survives_the_store_and_a_half_written_one_is_not_a_session()
        {
            var storage = new MemoryStorage();
            var store = new CredentialStore(storage);

            Assert.IsFalse(store.HasSession, "an empty store has no session");

            Credentials credentials = Credentials.Create(new CountingRandom());
            credentials.Jid = "393401234567:8@s.whatsapp.net";
            credentials.Lid = "123456789012345:8@lid";
            credentials.Platform = "uwp";
            credentials.PushName = "Vincenzo";
            store.Save(credentials);

            Assert.IsTrue(store.HasSession);

            Credentials restored = store.Load();
            Assert.ByteArraysEqual(credentials.NoisePrivateKey, restored.NoisePrivateKey);
            Assert.ByteArraysEqual(credentials.IdentityPublicKey, restored.IdentityPublicKey);
            Assert.ByteArraysEqual(credentials.AdvSecret, restored.AdvSecret);
            Assert.ByteArraysEqual(credentials.SignedPreKeySignature, restored.SignedPreKeySignature);
            Assert.AreEqual(credentials.RegistrationId, restored.RegistrationId);
            Assert.AreEqual(credentials.SignedPreKeyId, restored.SignedPreKeyId);
            Assert.AreEqual(credentials.Jid, restored.Jid);
            Assert.AreEqual(credentials.Lid, restored.Lid);
            Assert.AreEqual(credentials.Platform, restored.Platform);
            Assert.AreEqual(credentials.PushName, restored.PushName);

            // Review Focus 3: the app killed in the middle of a save must not leave
            // something the next start reads as a session.
            var partial = new MemoryStorage();
            var partialStore = new CredentialStore(partial);
            partial.Write(CredentialStore.NoisePrivateKey, Convert.ToBase64String(credentials.NoisePrivateKey));
            partial.Write(CredentialStore.IdentityPrivateKey, Convert.ToBase64String(credentials.IdentityPrivateKey));
            Assert.IsFalse(partialStore.HasSession, "half a set of credentials is not a session");

            // And a whole set with one value that does not parse is not one either.
            var damaged = new MemoryStorage();
            var damagedStore = new CredentialStore(damaged);
            damagedStore.Save(credentials);
            damaged.Write(CredentialStore.IdentityPrivateKey, "not base64 at all");
            Assert.IsFalse(damagedStore.HasSession, "a key that does not parse is not a session");

            damaged.Write(CredentialStore.IdentityPrivateKey, Convert.ToBase64String(Filled(16, 0x01)));
            Assert.IsFalse(damagedStore.HasSession, "a key of the wrong length is not a session");

            // Signing out is the only way back to a working QR, and it has to leave nothing behind.
            store.Clear();
            Assert.IsFalse(store.HasSession);
            for (int i = 0; i < CredentialStore.Keys.Length; i++)
            {
                Assert.IsTrue(string.IsNullOrEmpty(storage.Read(CredentialStore.Keys[i])),
                    "Clear left " + CredentialStore.Keys[i] + " behind");
            }
        }

        [Test]
        public static void The_pairing_QR_is_the_string_a_phone_scans()
        {
            var credentials = new Credentials();
            credentials.NoisePublicKey = Filled(32, 0x11);
            credentials.IdentityPublicKey = Filled(32, 0x22);
            credentials.AdvSecret = Filled(32, 0x33);

            // The base64 of those three keys, which is what the reference puts in the
            // string: one shot of `Buffer.alloc(32, 0x11).toString('base64')` and so on,
            // pinned here so a change to the encoding cannot pass unnoticed.
            string expected = "https://wa.me/settings/linked_devices#AP4gQcnVZmX7mJ2v,"
                + "ERERERERERERERERERERERERERERERERERERERERERE=,"
                + "IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=,"
                + "MzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzM=,8";

            Assert.AreEqual(expected, PairingQr.ForRefs(Text("AP4gQcnVZmX7mJ2v"), credentials));
        }

        [Test]
        public static void The_refs_the_server_sends_become_one_code_each()
        {
            var credentials = new Credentials();
            credentials.NoisePublicKey = Filled(32, 0x11);
            credentials.IdentityPublicKey = Filled(32, 0x22);
            credentials.AdvSecret = Filled(32, 0x33);

            var pairDevice = new BinaryNode("pair-device");
            pairDevice.Content = new List<BinaryNode>
            {
                Ref("AP4gQcnVZmX7mJ2v"),
                Ref("Zz09LmFiY2RlZg"),
                // A child that is not a ref is not a code, and must not become an empty one.
                new BinaryNode("device-identity")
            };

            IList<string> codes = PairingSession.CodesFor(pairDevice, credentials);

            Assert.AreEqual(2, codes.Count);
            Assert.IsTrue(codes[0].EndsWith(",8", StringComparison.Ordinal), "every code ends with the client type");
            Assert.IsTrue(codes[0].IndexOf("AP4gQcnVZmX7mJ2v,", StringComparison.Ordinal) > 0);
            Assert.IsTrue(codes[1].IndexOf("Zz09LmFiY2RlZg,", StringComparison.Ordinal) > 0);

            // A pairing request with no ref in it is refused rather than shown as a blank QR.
            var empty = new BinaryNode("pair-device");
            empty.Content = new List<BinaryNode>();
            Assert.Throws<ProtocolException>(delegate { PairingSession.CodesFor(empty, credentials); });
        }

        [Test]
        public static void A_pairing_reply_is_authenticated_with_the_ADV_secret_and_the_account_key()
        {
            // Clamped, because XEdDSA signs with the bytes it is handed and the ladder
            // clamps: an unclamped fixture would sign under one key and verify under
            // another, which is the mistake this test exists to catch in the code.
            byte[] accountPrivate = Clamped(Filled(32, 0x77));
            byte[] accountPublic = Curve25519.PublicKey(accountPrivate);
            byte[] identityPrivate = Clamped(Filled(32, 0x88));
            byte[] identityPublic = Curve25519.PublicKey(identityPrivate);
            byte[] advSecret = Filled(32, 0x99);

            byte[] details = new ProtoWriter()
                .Varint(AdvDeviceIdentity.RawIdField, 42)
                .Varint(AdvDeviceIdentity.TimestampField, 1782766800)
                .Varint(AdvDeviceIdentity.KeyIndexField, 5)
                .Varint(AdvDeviceIdentity.AccountTypeField, 0)
                .Varint(AdvDeviceIdentity.DeviceTypeField, 0)
                .ToArray();

            var identity = new AdvSignedDeviceIdentity();
            identity.Details = details;
            identity.AccountSignatureKey = accountPublic;
            identity.AccountSignature = XEdDsa.Sign(accountPrivate,
                AdvSignatures.Concat(AdvSignatures.AccountSignaturePrefix, details, identityPublic));

            // The account's signature covers this device's key, so it verifies - and it
            // stops verifying for any other device's key, which is the point of it.
            Assert.IsTrue(AdvSignatures.VerifyAccountSignature(identity, identityPublic, false),
                "the account's signature over 0x06 0x00 || details || our key must verify");
            Assert.IsFalse(AdvSignatures.VerifyAccountSignature(identity, Filled(32, 0xaa), false));

            // A hosted account's details carry one more byte, and a client that forgets
            // it computes a signature that verifies nowhere.
            byte[] hostedSignature = XEdDsa.Sign(accountPrivate,
                AdvSignatures.Concat(AdvSignatures.HostedAccountSignaturePrefix, details, identityPublic));
            var hostedIdentity = new AdvSignedDeviceIdentity();
            hostedIdentity.Details = details;
            hostedIdentity.AccountSignatureKey = accountPublic;
            hostedIdentity.AccountSignature = hostedSignature;
            Assert.IsTrue(AdvSignatures.VerifyAccountSignature(hostedIdentity, identityPublic, true),
                "a hosted account's signature uses 0x06 0x05 and must verify with it");
            Assert.IsFalse(AdvSignatures.VerifyAccountSignature(hostedIdentity, identityPublic, false),
                "a hosted signature must not verify under the end-to-end prefix");

            // The HMAC keyed with the ADV secret: the check that the reply belongs to the
            // pairing this device started.
            byte[] identityBytes = new ProtoWriter()
                .Bytes(AdvSignedDeviceIdentityContainer.DetailsField, identity.Encode())
                .Bytes(AdvSignedDeviceIdentityContainer.HmacField,
                    AdvSignatures.DetailsHmac(advSecret, identity.Encode(), false))
                .ToArray();

            AdvSignedDeviceIdentityContainer container = AdvSignedDeviceIdentityContainer.Decode(identityBytes);
            Assert.ByteArraysEqual(identity.Encode(), container.Details);
            Assert.IsTrue(Hmac.FixedTimeEquals(
                AdvSignatures.DetailsHmac(advSecret, container.Details, false), container.Hmac),
                "the ADV secret must authenticate the reply's details");
            Assert.IsFalse(Hmac.FixedTimeEquals(
                AdvSignatures.DetailsHmac(Filled(32, 0x01), container.Details, false), container.Hmac),
                "another device's ADV secret must not authenticate this reply");

            // This device's half of the exchange: 0x06 0x01 || details || our key || the
            // account's key, and it verifies against our own identity key.
            byte[] deviceSignature = AdvSignatures.SignDevice(identity, identityPrivate, identityPublic, false);
            Assert.IsTrue(XEdDsa.Verify(identityPublic,
                AdvSignatures.Concat(AdvSignatures.DeviceSignaturePrefix, details, identityPublic, accountPublic),
                deviceSignature),
                "0x06 0x01 || details || our key || the account's key, signed by us, must verify");

            // What goes back to the server leaves the account's signature key out.
            identity.DeviceSignature = deviceSignature;
            AdvSignedDeviceIdentity sent = AdvSignedDeviceIdentity.Decode(identity.EncodeForServer());
            Assert.ByteArraysEqual(details, sent.Details);
            Assert.ByteArraysEqual(identity.AccountSignature, sent.AccountSignature);
            Assert.ByteArraysEqual(deviceSignature, sent.DeviceSignature);
            Assert.IsTrue(sent.AccountSignatureKey == null, "the account's key is not sent back");
            Assert.IsTrue(sent.DeviceSignature != null, "our own signature is, though");
        }

        private static void AssertRegistration(byte[] pairingData, byte[] identity, byte[] signedPreKey, byte[] signature)
        {
            var reader = new ProtoReader(pairingData);
            byte[] registrationId = null;
            byte[] keyType = null;
            byte[] identifier = null;
            byte[] preKeyId = null;
            byte[] preKey = null;
            byte[] preKeySignature = null;
            byte[] deviceProps = null;

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.WireType != 2) reader.SkipField(field.WireType);
                else if (field.Number == ClientPayload.PairingERegidField) registrationId = reader.ReadBytes();
                else if (field.Number == ClientPayload.PairingEKeyTypeField) keyType = reader.ReadBytes();
                else if (field.Number == ClientPayload.PairingEIdentField) identifier = reader.ReadBytes();
                else if (field.Number == ClientPayload.PairingESkeyIdField) preKeyId = reader.ReadBytes();
                else if (field.Number == ClientPayload.PairingESkeyValField) preKey = reader.ReadBytes();
                else if (field.Number == ClientPayload.PairingESkeySigField) preKeySignature = reader.ReadBytes();
                else if (field.Number == ClientPayload.PairingDevicePropsField) deviceProps = reader.ReadBytes();
                else reader.ReadBytes();
            }

            // Four bytes big-endian, three for the key id: the reference's widths, and
            // the reason these are compared as bytes rather than as numbers.
            Assert.ByteArraysEqual(new byte[] { 0x01, 0x02, 0x03, 0x04 }, registrationId);
            Assert.ByteArraysEqual(new byte[] { 0x05 }, keyType);
            Assert.ByteArraysEqual(identity, identifier);
            Assert.ByteArraysEqual(new byte[] { 0xa0, 0xb0, 0xc0 }, preKeyId);
            Assert.ByteArraysEqual(signedPreKey, preKey);
            Assert.ByteArraysEqual(signature, preKeySignature);

            Assert.IsTrue(deviceProps != null, "the pairing data must carry the device props");
            AssertDeviceProps(deviceProps);
        }

        private static void AssertDeviceProps(byte[] deviceProps)
        {
            var reader = new ProtoReader(deviceProps);
            string os = null;
            int platformType = -1;
            int requireFullSync = -1;
            int storageQuotaMb = -1;
            bool sawHistorySyncConfig = false;

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.WireType == 2 && field.Number == DeviceProps.OsField) os = Utf8(reader.ReadBytes());
                else if (field.WireType == 2 && field.Number == DeviceProps.HistorySyncConfigField)
                {
                    sawHistorySyncConfig = true;
                    var history = new ProtoReader(reader.ReadBytes());
                    while (history.HasMore)
                    {
                        ProtoField inner = history.ReadField();
                        if (inner.WireType == 0 && inner.Number == 3) storageQuotaMb = (int)history.ReadVarint();
                        else history.SkipField(inner.WireType);
                    }
                }
                else if (field.WireType == 0 && field.Number == DeviceProps.PlatformTypeField) platformType = (int)reader.ReadVarint();
                else if (field.WireType == 0 && field.Number == DeviceProps.RequireFullSyncField) requireFullSync = (int)reader.ReadVarint();
                else reader.SkipField(field.WireType);
            }

            Assert.AreEqual("Windows Phone", os);
            Assert.AreEqual(DeviceProps.PlatformTypeUwp, platformType);
            Assert.AreEqual(0, requireFullSync);
            Assert.IsTrue(sawHistorySyncConfig, "a companion that wants its history must say how it wants it");
            Assert.AreEqual((int)DeviceProps.HistoryStorageQuotaMb, storageQuotaMb);
        }

        private static void AssertUserAgent(byte[] userAgent)
        {
            var reader = new ProtoReader(userAgent);
            int platform = -1;
            int releaseChannel = -1;
            int appVersionPrimary = -1;

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.WireType == 2 && field.Number == ClientPayload.UserAgentAppVersionField)
                {
                    var version = new ProtoReader(reader.ReadBytes());
                    while (version.HasMore)
                    {
                        ProtoField inner = version.ReadField();
                        if (inner.WireType == 0 && inner.Number == 1) appVersionPrimary = (int)version.ReadVarint();
                        else version.SkipField(inner.WireType);
                    }
                }
                else if (field.WireType == 0 && field.Number == ClientPayload.UserAgentPlatformField) platform = (int)reader.ReadVarint();
                else if (field.WireType == 0 && field.Number == ClientPayload.UserAgentReleaseChannelField) releaseChannel = (int)reader.ReadVarint();
                else reader.SkipField(field.WireType);
            }

            // WEB is the platform word the reference's own UWP pairing sends, a release
            // build is what this app is, and the version is the current web client's -
            // the server refuses an older one with a bare 405, which is what the first
            // live pairing run came back with.
            Assert.AreEqual(ClientPayload.PlatformWeb, platform);
            Assert.AreEqual(ClientPayload.ReleaseChannelRelease, releaseChannel);
            Assert.AreEqual((int)ClientPayload.WebVersionPrimary, appVersionPrimary);
            Assert.AreEqual("2.3000.1049558099", ClientPayload.WebVersionText);
        }

        private static void AssertWebInfo(byte[] webInfo)
        {
            var reader = new ProtoReader(webInfo);
            int subPlatform = -1;

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();
                if (field.WireType == 0 && field.Number == ClientPayload.WebInfoWebSubPlatformField) subPlatform = (int)reader.ReadVarint();
                else reader.SkipField(field.WireType);
            }

            Assert.AreEqual(ClientPayload.WebSubPlatformWebBrowser, subPlatform);
        }

        private static BinaryNode Ref(string text)
        {
            var node = new BinaryNode("ref");
            node.Content = Text(text);
            return node;
        }

        /// <summary>The private key as Signal generates it, which is what XEdDSA expects.</summary>
        private static byte[] Clamped(byte[] privateKey)
        {
            privateKey[0] = (byte)(privateKey[0] & 248);
            privateKey[31] = (byte)(privateKey[31] & 127);
            privateKey[31] = (byte)(privateKey[31] | 64);
            return privateKey;
        }

        private static byte[] Filled(int length, byte value)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = value;
            return bytes;
        }

        private static byte[] Text(string text)
        {
            return System.Text.Encoding.UTF8.GetBytes(text);
        }

        private static string Utf8(byte[] bytes)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        /// <summary>Bytes a test can predict: each call continues the run.</summary>
        private sealed class CountingRandom : IRandomSource
        {
            private byte _next = 0xff;

            public byte[] Bytes(int count)
            {
                var bytes = new byte[count];
                for (int i = 0; i < count; i++) bytes[i] = _next;
                _next--;
                return bytes;
            }
        }

        /// <summary>The phone's settings map, as the desktop host can hold it.</summary>
        private sealed class MemoryStorage : ICredentialStorage
        {
            private readonly Dictionary<string, string> _values =
                new Dictionary<string, string>(StringComparer.Ordinal);

            public string Read(string key)
            {
                string value;
                return _values.TryGetValue(key, out value) ? value : null;
            }

            public void Write(string key, string value)
            {
                _values[key] = value;
            }
        }
    }
}
