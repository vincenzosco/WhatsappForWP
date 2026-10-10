using System;
using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// Who a peer's devices are, the bundles they answer with, and the session a first send
    /// builds from one of them.
    ///
    /// The signature check is the reason this file exists: a bundle is data from a server,
    /// and the only thing that says it belongs to the device it names is the identity key
    /// signing the signed pre-key. A client that skips that check has a session with
    /// whoever answered the query.
    /// </summary>
    public static class DeviceDirectoryTests
    {
        [Test]
        public static void TheDeviceListReadsTheReferencesShape()
        {
            IList<string> devices = DeviceDirectory.Devices(PeerBundleFixture.DevicesAnswer());

            Assert.AreEqual(2, devices.Count);
            Assert.AreEqual(PeerBundleFixture.PeerPhone, devices[0]);
            Assert.AreEqual(PeerBundleFixture.PeerTablet, devices[1]);
        }

        [Test]
        public static void AFixtureAnswerBecomesOneBundlePerDevice()
        {
            Credentials peer = Credentials.Create(new SignalSessions.CounterRandom());
            BinaryNode answer = TwoDevices(peer, true);

            IList<KeyValuePair<string, PreKeyBundle>> bundles = DeviceDirectory.Bundles(answer);

            Assert.AreEqual(2, bundles.Count);
            Assert.AreEqual(PeerBundleFixture.PeerPhone, bundles[0].Key);
            Assert.AreEqual(PeerBundleFixture.PeerTablet, bundles[1].Key);

            Assert.AreEqual(peer.RegistrationId, bundles[0].Value.RegistrationId);
            Assert.IsTrue(bundles[0].Value.HasPreKey, "this answer carried a one-time pre-key");
            // The cast is the assertion's own trap: AreEqual compares objects, and a
            // boxed int 7 is not a boxed uint 7.
            Assert.AreEqual((uint)7, bundles[0].Value.PreKeyId);
        }

        [Test]
        public static void ASignedPreKeyWhoseSignatureDoesNotVerifyIsRefused()
        {
            Credentials peer = Credentials.Create(new SignalSessions.CounterRandom());

            string named = Failed(delegate
            {
                DeviceDirectory.Bundles(PeerBundleFixture.TamperedBundleAnswer(peer, PeerBundleFixture.PeerTablet));
            });

            Assert.IsTrue(named != null, "a bundle signed by somebody else has to be refused");
            Assert.IsTrue(named.Contains(PeerBundleFixture.PeerTablet),
                "the refusal names the device it refused, and it said: " + named);
        }

        [Test]
        public static void APeerWithNoOneTimePreKeyStillGetsASession()
        {
            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            Credentials peer = Credentials.Create(new SignalSessions.CounterRandom());
            var store = new SessionStore(new Memory(), new SignalSessions.CounterRandom());

            // The server had nothing left to hand out for this device, which is the
            // ordinary case for a device that has received more messages than it published
            // pre-keys.
            PreKeyBundle bundle = PeerBundleFixture.Bundle(peer, PeerBundleFixture.PeerTablet, false);

            Assert.IsFalse(bundle.HasPreKey);
            Assert.IsTrue(bundle.HasSignedPreKey);

            SessionRecord session = DeviceDirectory.Session(
                PeerBundleFixture.PeerTablet, bundle, us, store, new SignalSessions.CounterRandom());

            Assert.IsTrue(session.HasSession, "a session from the signed pre-key alone is still a session");
            Assert.IsTrue(store.Load(PeerBundleFixture.PeerTablet) != null, "and it is the stored one");
        }

        [Test]
        public static void AFirstSendBuildsTheSessionItNeeds()
        {
            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            Credentials peer = Credentials.Create(new SignalSessions.CounterRandom());
            var store = new SessionStore(new Memory(), new SignalSessions.CounterRandom());
            PreKeyBundle bundle = PeerBundleFixture.Bundle(peer, PeerBundleFixture.PeerTablet, true);

            SessionRecord first = DeviceDirectory.Session(
                PeerBundleFixture.PeerTablet, bundle, us, store, new SignalSessions.CounterRandom());

            Assert.IsTrue(store.Load(PeerBundleFixture.PeerTablet) != null,
                "the session is saved before it is handed back");

            // The second call reads what the first one wrote: a second session for the same
            // device is a message the peer cannot decrypt.
            SessionRecord again = DeviceDirectory.Session(
                PeerBundleFixture.PeerTablet, bundle, us, store, new SignalSessions.CounterRandom());

            Assert.ByteArraysEqual(first.Encode(), again.Encode());
        }

        [Test]
        public static void AnEstablishedSessionIsNotReplaced()
        {
            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            Credentials peer = Credentials.Create(new SignalSessions.CounterRandom());
            var store = new SessionStore(new Memory(), new SignalSessions.CounterRandom());

            SessionRecord first = DeviceDirectory.Session(PeerBundleFixture.PeerTablet,
                PeerBundleFixture.Bundle(peer, PeerBundleFixture.PeerTablet, true),
                us, store, new SignalSessions.CounterRandom());

            // A second bundle for the same device, with different keys in it: a re-query
            // during the same conversation must not throw the working session away.
            Credentials other = Credentials.Create(new SignalSessions.CounterRandom());
            SessionRecord second = DeviceDirectory.Session(PeerBundleFixture.PeerTablet,
                PeerBundleFixture.Bundle(other, PeerBundleFixture.PeerTablet, true),
                us, store, new SignalSessions.CounterRandom());

            Assert.ByteArraysEqual(first.Encode(), second.Encode());
        }

        [Test]
        public static void TheBundleQueryNamesEveryDevice()
        {
            BinaryNode iq = DeviceDirectory.BundleQuery(
                new string[] { PeerBundleFixture.PeerPhone, PeerBundleFixture.PeerTablet },
                new SignalSessions.CounterRandom());

            Assert.AreEqual("iq", iq.Tag);
            Assert.AreEqual("encrypt", iq.Attrs["xmlns"]);
            Assert.AreEqual("get", iq.Attrs["type"]);
            Assert.AreEqual(2, iq.Children().Count);
            Assert.AreEqual(PeerBundleFixture.Peer,
                iq.Children()[1].Child("user").Attrs["jid"]);
            Assert.AreEqual(PeerBundleFixture.PeerTablet,
                iq.Children()[1].Child("device").Attrs["jid"]);
        }

        [Test]
        public static void TheDeviceQueryAsksForTheDevicesProtocol()
        {
            BinaryNode iq = DeviceDirectory.Query(PeerBundleFixture.Peer, new SignalSessions.CounterRandom());

            Assert.AreEqual("get", iq.Attrs["type"]);
            Assert.AreEqual(DeviceDirectory.Usync, iq.Attrs["xmlns"]);

            BinaryNode usync = iq.Child("usync");
            Assert.AreEqual("query", usync.Attrs["mode"]);
            Assert.AreEqual("2", usync.Child("query").Child("devices").Attrs["version"]);
            Assert.AreEqual(PeerBundleFixture.Peer, usync.Child("list").Child("user").Attrs["jid"]);
        }

        /// <summary>One answer carrying two devices, which is what a contact with a phone and a tablet gets.</summary>
        private static BinaryNode TwoDevices(Credentials peer, bool withOneTimePreKey)
        {
            BinaryNode first = PeerBundleFixture.BundleAnswer(peer, PeerBundleFixture.PeerPhone, withOneTimePreKey);
            BinaryNode second = PeerBundleFixture.BundleAnswer(peer, PeerBundleFixture.PeerTablet, withOneTimePreKey);

            var users = new List<BinaryNode>();
            users.AddRange(first.Child("list").Children());
            users.AddRange(second.Child("list").Children());

            var list = new BinaryNode("list");
            list.Content = users;

            var iq = new BinaryNode("iq");
            iq.Content = new List<BinaryNode> { list };
            return iq;
        }

        /// <summary>The refusal's own words, or null when nothing was refused.</summary>
        private static string Failed(Action action)
        {
            try
            {
                action();
            }
            catch (ProtocolException refused)
            {
                return refused.Message;
            }

            return null;
        }

        private sealed class Memory : ISessionStorage
        {
            private readonly Dictionary<string, byte[]> _blobs = new Dictionary<string, byte[]>();

            public bool TryRead(string key, out byte[] value)
            {
                return _blobs.TryGetValue(key, out value);
            }

            public void Write(string key, byte[] value)
            {
                _blobs[key] = value;
            }

            public void Remove(string key)
            {
                _blobs.Remove(key);
            }
        }
    }
}
