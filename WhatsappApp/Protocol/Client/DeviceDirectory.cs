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
    /// - the bundles are one `encrypt` `get` with a `key` holding one `user` per device,
    ///   whose `jid` is the device itself, and whose answer carries `registration`,
    ///   `identity`, `skey` and `key` per device;
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

                    // On the address of the user node, never on a string glued by hand:
                    // `JidUtils.Encode` is what knows that **device 0 is written without
                    // the suffix** (`jid-utils.ts`, `!!device`). Gluing it produced
                    // `15551230000:0@s.whatsapp.net` - the phone itself - and the server
                    // has no device at that address: it answered `ack class="message"` and
                    // delivered the message to nobody, which on the phone of 2026-11-01
                    // was "it says sent but it is not".
                    int id;
                    if (!int.TryParse(device, out id)) continue;

                    DecodedJid user = JidUtils.Decode(jid);
                    if (!user.IsJid) continue;

                    devices.Add(JidUtils.Encode(user.User, user.Server, id));
                }
            }

            return devices;
        }

        /// <summary>
        /// The iq that asks for one pre-key bundle per device: one `user` per device, whose
        /// `jid` **is** the device (`number:device@s.whatsapp.net`), all inside one `key`.
        ///
        /// Why there is no `device` child: the official client's `SendGetPreKeys` builds
        /// `iq > key > user[jid]` and nothing else, and the per-person shape this file
        /// already ships - same nodes, only the jid differs - is the one the server answers.
        /// The `<user><device jid/></user>` written before was a node neither the reference
        /// nor the server has, which is why its answer never came (diag.log, 2 November
        /// 2026: "the per-device bundle query ... the server did not answer").
        /// </summary>
        public static BinaryNode BundleQuery(IList<string> deviceJids, IRandomSource random)
        {
            if (deviceJids == null || deviceJids.Count == 0)
            {
                throw new ProtocolException("a bundle query asks about at least one device");
            }

            if (random == null) throw new ProtocolException("a bundle query needs randomness for its id");

            var requests = new List<BinaryNode>();
            for (int i = 0; i < deviceJids.Count; i++)
            {
                if (string.IsNullOrEmpty(deviceJids[i])) continue;

                // The device address on the user, exactly as the reference writes it: the
                // server keys the bundle on the jid it is given, and a child it does not
                // know is a question it never answers.
                var userNode = new BinaryNode("user");
                userNode.Attrs.Add("jid", deviceJids[i]);
                requests.Add(userNode);
            }

            if (requests.Count == 0)
            {
                throw new ProtocolException("a bundle query asks about at least one device");
            }

            var key = new BinaryNode("key");
            key.Content = requests;

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("to", PairingSession.ServerJid);
            iq.Attrs.Add("type", "get");
            iq.Attrs.Add("xmlns", "encrypt");
            iq.Attrs.Add("id", Hex(random.Bytes(8)));
            iq.Content = new List<BinaryNode> { key };

            return iq;
        }

        /// <summary>
        /// The same question asked per person instead of per device: one `key` naming a
        /// `user`, and WhatsApp answers with the bundles of every device that person has.
        ///
        /// Why it exists at all: on 2026-10-30 the per-device query went out and was never
        /// answered - `diag.log` shows the device list arriving and the bundle query's id
        /// never answered at all, so a one-to-one message died in `the server did not
        /// answer` for every send. A shape the server ignores is not a refusal and cannot
        /// be treated as one; this is the other shape, tried when the first one stays
        /// silent.
        /// </summary>
        public static BinaryNode BundleQueryForUser(IList<string> userJids, IRandomSource random)
        {
            if (userJids == null || userJids.Count == 0)
            {
                throw new ProtocolException("a bundle query asks about at least one person");
            }

            if (random == null) throw new ProtocolException("a bundle query needs randomness for its id");

            var users = new List<BinaryNode>();
            for (int i = 0; i < userJids.Count; i++)
            {
                if (string.IsNullOrEmpty(userJids[i])) continue;

                var userNode = new BinaryNode("user");
                userNode.Attrs.Add("jid", userJids[i]);
                users.Add(userNode);
            }

            if (users.Count == 0) throw new ProtocolException("a bundle query asks about at least one person");

            var key = new BinaryNode("key");
            key.Content = users;

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("to", PairingSession.ServerJid);
            iq.Attrs.Add("type", "get");
            iq.Attrs.Add("xmlns", "encrypt");
            iq.Attrs.Add("id", Hex(random.Bytes(8)));
            iq.Content = new List<BinaryNode> { key };

            return iq;
        }

        /// <summary>`393202178869:29@s.whatsapp.net` as the person the server keys on.</summary>
        public static string UserJid(string deviceJid)
        {
            return string.IsNullOrEmpty(deviceJid) ? deviceJid : Person(deviceJid);
        }

        /// <summary>
        /// The bundles in an answer, keyed by the device JID each one belongs to, with
        /// every signed pre-key verified. A bundle whose signature does not verify stops
        /// the whole answer with the device named: a message encrypted to it is a message
        /// the other side's real device cannot read.
        ///
        /// Two shapes are read, because two queries can produce an answer: the keys hang
        /// off the `user` when the query named a device, and off a `device` child when it
        /// named only the person. Reading one and refusing the other is what turned a real
        /// answer into `the answer has no list in it`.
        /// </summary>
        public static IList<KeyValuePair<string, PreKeyBundle>> Bundles(BinaryNode answer)
        {
            return Bundles(answer, false, null);
        }

        /// <summary>
        /// The bundles in an answer. With `skipIncomplete` a device whose bundle is missing a
        /// node is not fatal: it is added to `skipped` by name and the rest are still read.
        /// A group send uses that, so one device with a broken bundle does not drop the
        /// message for every other device in the group. Without it, the first incomplete
        /// bundle throws, as before.
        /// </summary>
        public static IList<KeyValuePair<string, PreKeyBundle>> Bundles(BinaryNode answer,
            bool skipIncomplete, IList<string> skipped)
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

                // Where the keys are decides how the answer is read: on the user when the
                // query named a device, on a `device` child when it named the person. An
                // answer with neither is still read as one bundle, so that the refusal
                // names the node it lacks ("no registration") instead of answering an
                // empty list - which is what a device list handed in by mistake looks like,
                // and what two tests reported when this branch swallowed it.
                IList<BinaryNode> children = user.Children();
                bool perDevice = false;

                if (user.Child("registration") == null && children != null)
                {
                    for (int d = 0; d < children.Count; d++)
                    {
                        if (children[d].Tag == "device") { perDevice = true; break; }
                    }
                }

                if (!perDevice)
                {
                    try
                    {
                        bundles.Add(new KeyValuePair<string, PreKeyBundle>(jid, ReadBundle(jid, user)));
                    }
                    catch (ProtocolException)
                    {
                        if (!skipIncomplete) throw;
                        if (skipped != null) skipped.Add(jid);
                    }
                    continue;
                }

                for (int d = 0; d < children.Count; d++)
                {
                    if (children[d].Tag != "device") continue;

                    string deviceJid = children[d].Attrs["jid"];
                    if (string.IsNullOrEmpty(deviceJid)) deviceJid = WithDevice(jid, children[d].Attrs["id"]);
                    if (string.IsNullOrEmpty(deviceJid)) continue;

                    try
                    {
                        bundles.Add(new KeyValuePair<string, PreKeyBundle>(
                            deviceJid, ReadBundle(deviceJid, children[d])));
                    }
                    catch (ProtocolException)
                    {
                        if (!skipIncomplete) throw;
                        if (skipped != null) skipped.Add(deviceJid);
                    }
                }
            }

            return bundles;
        }

        /// <summary>
        /// One bundle: the four nodes and the signature check, wherever the answer put them.
        ///
        /// Every node is named before it is read. `Child` answers null for a tag that is
        /// not there, and a `.Bytes()` on it is a NullReferenceException that escapes the
        /// caller's `catch (ProtocolException)` - a failure with no name and no device on
        /// it, for an answer that is simply incomplete.
        /// </summary>
        private static PreKeyBundle ReadBundle(string jid, BinaryNode carrier)
        {
            var bundle = new PreKeyBundle();
            bundle.RegistrationId = Number(Required(jid, carrier, "registration").Bytes());
            bundle.IdentityKey = Required(jid, carrier, "identity").Bytes();

            BinaryNode signedPreKey = Required(jid, carrier, "skey");
            bundle.SignedPreKeyId = Number(Required(jid, signedPreKey, "id").Bytes());
            bundle.SignedPreKeyPublic = Required(jid, signedPreKey, "value").Bytes();
            bundle.SignedPreKeySignature = Required(jid, signedPreKey, "signature").Bytes();
            bundle.HasSignedPreKey = bundle.SignedPreKeyId != 0;

            BinaryNode oneTime = carrier.Child("key");
            if (oneTime != null)
            {
                bundle.PreKeyId = Number(Required(jid, oneTime, "id").Bytes());
                bundle.PreKeyPublic = Required(jid, oneTime, "value").Bytes();
                bundle.HasPreKey = bundle.PreKeyPublic != null;
            }

            bundle.Check();
            Verify(jid, bundle);

            return bundle;
        }

        /// <summary>
        /// `number@server` and a device id as the address that device has: `number:id@server`,
        /// and the bare `number@server` for device 0 - the phone, which WhatsApp never
        /// writes with a suffix (`JidUtils.Encode`). Null when there is no id or no JID.
        /// </summary>
        private static string WithDevice(string userJid, string deviceId)
        {
            if (string.IsNullOrEmpty(userJid) || string.IsNullOrEmpty(deviceId)) return null;

            int id;
            if (!int.TryParse(deviceId, out id)) return null;

            DecodedJid user = JidUtils.Decode(userJid);
            if (!user.IsJid) return null;

            return JidUtils.Encode(user.User, user.Server, id);
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

            // A bundle answer may carry its users at the top of the iq, with no `list`
            // between: WhatsApp answers a `key` query that way. `list` is the shape of a
            // usync answer and of the fixtures, not of every answer there is.
            bool bare = false;
            if (list == null && answer.Child("user") != null)
            {
                list = answer;
                bare = true;
            }

            if (list == null) throw new ProtocolException("the answer has no list in it");

            IList<BinaryNode> users = list.Children();
            if (users == null) return new List<BinaryNode>();

            if (!bare) return users;

            var only = new List<BinaryNode>();
            for (int i = 0; i < users.Count; i++)
            {
                if (users[i].Tag == "user") only.Add(users[i]);
            }

            return only;
        }

        /// <summary>`name:device@server` without the device, which is the account the server keys on.</summary>
        public static string Person(string deviceJid)
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
