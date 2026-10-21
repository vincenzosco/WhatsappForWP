using System.Collections.Generic;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// Who a contact's devices are, and the pre-key bundle each of them answers with -
    /// the two questions a first message to somebody asks before there is a session to
    /// encrypt with.
    ///
    /// Three shapes, all of them the reference's:
    ///
    /// - the device list is a `usync` `query` for the `devices` protocol (version 2),
    ///   whose answer is `usync &gt; list &gt; user &gt; devices &gt; device-list &gt; device[id]`;
    /// - the bundles are one `encrypt` `get` with a `key` per device, `user` and `device`
    ///   inside it, whose answer carries `registration`, `identity`, `skey` and `key` per
    ///   device;
    /// - a bundle's signed pre-key is verified here, against the identity key in the same
    ///   bundle, because that is the one check standing between this session and somebody
    ///   else's: a signature that does not verify is a device a message must not be sent
    ///   to, and it is refused before any session is built from it.
    /// </summary>
    public static class DeviceDirectory
    {
        /// <summary>The answer really answers this contact and nobody else.</summary>
        public const string Usync = "usync";

        /// <summary>The iq that asks which devices a contact has.</summary>
        public static BinaryNode Query(string jid, IRandomSource random)
        {
            if (string.IsNullOrEmpty(jid)) throw new ProtocolException("a device query asks about somebody");

            return Query(new List<string> { jid }, random);
        }

        /// <summary>
        /// The same query for several JIDs at once, which is what a group send needs: one
        /// `usync` carries a `user` per member, and the answer comes back as one `list`
        /// (`Socket/messages-send.js`, `getUSyncDevices(participantsList, ...)`), so a group
        /// of twenty members costs one round trip rather than twenty.
        /// </summary>
        public static BinaryNode Query(IList<string> jids, IRandomSource random)
        {
            if (jids == null || jids.Count == 0) throw new ProtocolException("a device query asks about somebody");
            if (random == null) throw new ProtocolException("a device query needs randomness for its two ids");

            var query = new BinaryNode("query");
            var devices = new BinaryNode("devices");
            devices.Attrs.Add("version", "2");
            query.Content = new List<BinaryNode> { devices };

            var users = new List<BinaryNode>();
            for (int i = 0; i < jids.Count; i++)
            {
                if (string.IsNullOrEmpty(jids[i])) continue;

                var user = new BinaryNode("user");
                user.Attrs.Add("jid", jids[i]);
                users.Add(user);
            }

            if (users.Count == 0) throw new ProtocolException("a device query asks about somebody");

            var list = new BinaryNode("list");
            list.Content = users;

            var usync = new BinaryNode("usync");
            usync.Attrs.Add("sid", Hex(random.Bytes(8)));
            usync.Attrs.Add("mode", "query");
            usync.Attrs.Add("last", "true");
            usync.Attrs.Add("index", "0");
            usync.Attrs.Add("context", "message");
            usync.Content = new List<BinaryNode> { query, list };

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("to", PairingSession.ServerJid);
            iq.Attrs.Add("type", "get");
            iq.Attrs.Add("xmlns", Usync);
            iq.Attrs.Add("id", Hex(random.Bytes(8)));
            iq.Content = new List<BinaryNode> { usync };

            return iq;
        }

        /// <summary>
        /// The device JIDs in a device-list answer: `user:device`, one per entry, the
        /// phone's own device included - who to send to is the caller's question, because
        /// only the caller knows which of them is itself.
        /// </summary>
        public static IList<string> Devices(BinaryNode answer)
        {
            var devices = new List<string>();
            IList<BinaryNode> users = Users(answer);

            for (int i = 0; i < users.Count; i++)
            {
                string jid = users[i].Attrs["jid"];
                if (string.IsNullOrEmpty(jid)) continue;

                BinaryNode devicesNode = users[i].Child("devices");
                BinaryNode deviceList = devicesNode == null ? null : devicesNode.Child("device-list");
                IList<BinaryNode> entries = deviceList == null ? null : deviceList.Children();
                if (entries == null) continue;

                for (int d = 0; d < entries.Count; d++)
                {
                    if (entries[d].Tag != "device") continue;

                    string device = entries[d].Attrs["id"];
                    if (string.IsNullOrEmpty(device)) continue;

                    // The device goes inside the JID, not after it: `name:device@server`
                    // is the address the server knows, and `name@server:device` is a
                    // string nobody has a device for.
                    int at = jid.IndexOf('@');
                    devices.Add(at < 0
                        ? jid + ":" + device
                        : jid.Substring(0, at) + ":" + device + jid.Substring(at));
                }
            }

            return devices;
        }

        /// <summary>The iq that asks for one pre-key bundle per device.</summary>
        public static BinaryNode BundleQuery(IList<string> deviceJids, IRandomSource random)
        {
            if (deviceJids == null || deviceJids.Count == 0)
            {
                throw new ProtocolException("a bundle query asks about at least one device");
            }

            if (random == null) throw new ProtocolException("a bundle query needs randomness for its id");

            var keys = new List<BinaryNode>();
            for (int i = 0; i < deviceJids.Count; i++)
            {
                string deviceJid = deviceJids[i];
                string user = User(deviceJid);

                var userNode = new BinaryNode("user");
                userNode.Attrs.Add("jid", user);

                var deviceNode = new BinaryNode("device");
                deviceNode.Attrs.Add("jid", deviceJid);

                var key = new BinaryNode("key");
                key.Content = new List<BinaryNode> { userNode, deviceNode };
                keys.Add(key);
            }

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("to", PairingSession.ServerJid);
            iq.Attrs.Add("type", "get");
            iq.Attrs.Add("xmlns", "encrypt");
            iq.Attrs.Add("id", Hex(random.Bytes(8)));
            iq.Content = keys;

            return iq;
        }

        /// <summary>
        /// The bundles in an answer, keyed by the device JID each one belongs to, with
        /// every signed pre-key verified. A bundle whose signature does not verify stops
        /// the whole answer with the device named: a message encrypted to it is a message
        /// the other side's real device cannot read.
        /// </summary>
        public static IList<KeyValuePair<string, PreKeyBundle>> Bundles(BinaryNode answer)
        {
            var bundles = new List<KeyValuePair<string, PreKeyBundle>>();
            IList<BinaryNode> users = Users(answer);

            for (int i = 0; i < users.Count; i++)
            {
                BinaryNode user = users[i];
                string jid = user.Attrs["jid"];
                if (string.IsNullOrEmpty(jid)) continue;

                if (user.Child("error") != null)
                {
                    throw new ProtocolException("the server answered an error for " + jid);
                }

                // Every node is named before it is read. `Child` answers null for a
                // tag that is not there, and a `.Bytes()` on it is a
                // NullReferenceException that escapes the caller's `catch
                // (ProtocolException)` - a failure with no name and no device on it,
                // for an answer that is simply incomplete.
                var bundle = new PreKeyBundle();
                bundle.RegistrationId = Number(Required(jid, user, "registration").Bytes());
                bundle.IdentityKey = Required(jid, user, "identity").Bytes();

                BinaryNode signedPreKey = Required(jid, user, "skey");
                bundle.SignedPreKeyId = Number(Required(jid, signedPreKey, "id").Bytes());
                bundle.SignedPreKeyPublic = Required(jid, signedPreKey, "value").Bytes();
                bundle.SignedPreKeySignature = Required(jid, signedPreKey, "signature").Bytes();
                bundle.HasSignedPreKey = bundle.SignedPreKeyId != 0;

                BinaryNode oneTime = user.Child("key");
                if (oneTime != null)
                {
                    bundle.PreKeyId = Number(Required(jid, oneTime, "id").Bytes());
                    bundle.PreKeyPublic = Required(jid, oneTime, "value").Bytes();
                    bundle.HasPreKey = bundle.PreKeyPublic != null;
                }

                bundle.Check();
                Verify(jid, bundle);

                bundles.Add(new KeyValuePair<string, PreKeyBundle>(jid, bundle));
            }

            return bundles;
        }

        /// <summary>
        /// A node a bundle cannot be read without, or the refusal that says which one is
        /// missing and for which device.
        /// </summary>
        private static BinaryNode Required(string jid, BinaryNode parent, string tag)
        {
            BinaryNode node = parent.Child(tag);
            if (node == null)
            {
                throw new ProtocolException("the bundle of " + jid + " has no " + tag);
            }

            return node;
        }

        /// <summary>
        /// The session to encrypt with for one device: the stored one when it is there,
        /// and a new one started from the bundle otherwise - saved before it is handed
        /// back, because a session that only exists in a local variable is a session the
        /// next message starts again, with a fresh pre-key the peer did not expect.
        /// </summary>
        public static SessionRecord Session(string deviceJid, PreKeyBundle bundle,
            Credentials credentials, SessionStore store, IRandomSource random)
        {
            if (string.IsNullOrEmpty(deviceJid)) throw new ProtocolException("a session is stored under a device's name");
            if (credentials == null) throw new ProtocolException("starting a session needs this device's credentials");
            if (store == null) throw new ProtocolException("starting a session needs the store it is kept in");
            if (random == null) throw new ProtocolException("starting a session needs randomness");

            SessionRecord existing = store.Load(deviceJid);
            if (existing != null && existing.HasSession) return existing;

            if (bundle == null) throw new ProtocolException("there is no bundle to start a session with for " + deviceJid);

            bundle.Check();
            Verify(deviceJid, bundle);

            SessionRecord started = SessionRecord.StartAsInitiator(
                credentials.IdentityPrivateKey,
                random.Bytes(Curve25519.KeyLength),
                random.Bytes(Curve25519.KeyLength),
                credentials.RegistrationId,
                bundle,
                random);

            store.Save(deviceJid, started);

            // The stored copy, not the one just built: the store is what every later
            // message reads, and handing back a second copy is how two ratchets start.
            return store.Load(deviceJid);
        }

        /// <summary>
        /// The signed pre-key is signed by the identity key that came with it, over the
        /// typed key (`0x05` then the 32 bytes), which is the same 33 bytes the server
        /// stores and the same check WhatsApp Web makes.
        /// </summary>
        private static void Verify(string jid, PreKeyBundle bundle)
        {
            byte[] identity = X3dh.Bare(bundle.IdentityKey);
            byte[] signedPreKey = X3dh.Bare(bundle.SignedPreKeyPublic);

            if (bundle.SignedPreKeySignature == null
                || !XEdDsa.Verify(identity, Credentials.SignedPreKeyToSign(signedPreKey), bundle.SignedPreKeySignature))
            {
                throw new ProtocolException("the signed pre-key of " + jid + " is not signed by its identity key");
            }
        }

        /// <summary>The `list` of an answer, whether the answer is the iq or the list itself.</summary>
        private static IList<BinaryNode> Users(BinaryNode answer)
        {
            if (answer == null) throw new ProtocolException("there is no answer to read");

            BinaryNode list = answer.Tag == "list" ? answer : answer.Child("list");

            // A device list answers inside `usync`, a bundle answer at the top of the iq:
            // both are the same list of users one level down.
            if (list == null)
            {
                BinaryNode usync = answer.Child("usync");
                list = usync == null ? null : usync.Child("list");
            }

            if (list == null) throw new ProtocolException("the answer has no list in it");

            IList<BinaryNode> users = list.Children();
            return users == null ? new List<BinaryNode>() : users;
        }

        /// <summary>`name:device@server` without the device, which is the user the server keys on.</summary>
        private static string User(string deviceJid)
        {
            int colon = deviceJid.IndexOf(':');
            if (colon < 0) return deviceJid;

            int at = deviceJid.IndexOf('@');
            if (at >= 0 && colon > at) return deviceJid;

            return at < 0
                ? deviceJid.Substring(0, colon)
                : deviceJid.Substring(0, colon) + deviceJid.Substring(at);
        }

        private static uint Number(byte[] bigEndian)
        {
            if (bigEndian == null) throw new ProtocolException("a number the answer does not carry");

            uint value = 0;
            for (int i = 0; i < bigEndian.Length; i++)
            {
                value = (value << 8) | bigEndian[i];
            }

            return value;
        }

        private static string Hex(byte[] bytes)
        {
            var text = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("X2"));

            return text.ToString();
        }
    }
}
