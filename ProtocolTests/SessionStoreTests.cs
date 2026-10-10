using System;
using System.Collections.Generic;
using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Signal;

namespace ProtocolTests
{
    /// <summary>
    /// What the app keeps between runs, and what happens when a file is not what it was.
    ///
    /// Three things are being checked here and none of them is the signal protocol: that a
    /// session survives being written and read (a ratchet that forgets where it is does not
    /// fail loudly - it decrypts the next message into noise), that a damaged record is a
    /// *new session* rather than an exception thrown out of a decryption, and that a
    /// one-time pre-key can be spent exactly once. The second is the one review focus 2
    /// names, and the reason the record carries its own digest: a torn write that still
    /// parses is worse than one that cannot be read at all.
    /// </summary>
    public static class SessionStoreTests
    {
        /// <summary>
        /// A record survives the round trip, and the session that comes back out is the one
        /// that went in: byte for byte, and able to continue the conversation in both
        /// directions - which is what "the same message key material" means in practice,
        /// because the state is a ratchet and comparing a field or two would not notice the
        /// one that moved.
        /// </summary>
        [Test]
        public static void ARecordRoundTrips()
        {
            var storage = new Memory();
            var random = new SignalSessions.CounterRandom();

            SessionStore store = new SessionStore(storage, random);
            SessionRecord alice = SignalSessions.Alice(random);
            SessionRecord bob = SignalSessions.Bob(random);

            // One message before the round trip, so the record that is written is a session
            // that has already moved: counters, keys and the pending pre-key are all part of
            // what has to come back.
            Send(alice, bob, "before");

            byte[] written = alice.Encode();
            store.Save("bob", alice);

            SessionRecord restored = store.Load("bob");
            Assert.IsTrue(restored != null, "a saved session has to come back");
            Assert.ByteArraysEqual(written, restored.Encode());
            Assert.IsTrue(restored.HasSession);
            Assert.IsTrue(restored.PreKeyPending,
                "she has had no reply yet, so her messages are still pre-key messages: that is state, and it has to survive too");

            // And it still works: his reply opens on the restored session, and hers opens on
            // his - which is the ratchet step the round trip had to preserve.
            Deliver(bob, restored, "after");
            Deliver(restored, bob, "again");
        }

        /// <summary>
        /// A record that is not whole is not a session, and never an exception: truncated
        /// anywhere, damaged in the middle, or written by a format this build does not know.
        /// The store answers the same way for all of them, which is what makes a torn file
        /// one conversation that starts over instead of a crash in the middle of a
        /// decryption.
        /// </summary>
        [Test]
        public static void AHalfWrittenRecordIsNoSession()
        {
            byte[] whole = SignalSessions.Alice(new SignalSessions.CounterRandom()).Encode();
            Assert.IsTrue(SessionRecord.Decode(whole, new SignalSessions.CounterRandom()) != null,
                "the whole record is the control: if this one is null the test proves nothing");

            // Every place a write can be cut: before the framing, inside the length, inside
            // the body, in the digest.
            int[] cuts = new int[] { 0, 1, 4, 5, whole.Length / 2, whole.Length - 1 };
            for (int i = 0; i < cuts.Length; i++)
            {
                var torn = new byte[cuts[i]];
                Buffer.BlockCopy(whole, 0, torn, 0, cuts[i]);

                Assert.IsTrue(SessionRecord.Decode(torn, new SignalSessions.CounterRandom()) == null,
                    "a record cut at " + cuts[i] + " is not a session");
                Assert.IsTrue(SessionRecord.Decode(whole, new SignalSessions.CounterRandom()) != null,
                    "and the whole one still is, at cut " + cuts[i]);
            }

            Assert.IsTrue(SessionRecord.Decode(null, new SignalSessions.CounterRandom()) == null);
            Assert.IsTrue(SessionRecord.Decode(new byte[0], new SignalSessions.CounterRandom()) == null);

            // A byte in the middle: the lengths are all still right, and the digest is what
            // notices. Flipped one at a time and checked at each, because a single offset
            // that happens to land on padding would make this test a coincidence.
            for (int i = 0; i < whole.Length; i++)
            {
                var damaged = new byte[whole.Length];
                Buffer.BlockCopy(whole, 0, damaged, 0, whole.Length);
                damaged[i] ^= 0x01;

                Assert.IsTrue(SessionRecord.Decode(damaged, new SignalSessions.CounterRandom()) == null,
                    "a record with byte " + i + " changed is not a session");
            }

            // A version this build does not know: refused rather than read as if the fields
            // it added were never there.
            var later = new byte[whole.Length];
            Buffer.BlockCopy(whole, 0, later, 0, whole.Length);
            later[0] = 2;
            Assert.IsTrue(SessionRecord.Decode(later, new SignalSessions.CounterRandom()) == null);

            // And through the store, which is where a file actually arrives.
            var storage = new Memory();
            var random = new SignalSessions.CounterRandom();
            var store = new SessionStore(storage, random);

            storage.Write("session/bob", new byte[] { 1, 2, 3 });
            Assert.IsTrue(store.Load("bob") == null);

            storage.Write("session/bob", whole);
            Assert.IsTrue(store.Load("bob") != null);

            Assert.IsTrue(store.Load("someone-else") == null, "a peer with no record has no session");
        }

        /// <summary>
        /// One record per peer, and no crossing: what is saved for one is what is loaded for
        /// one, saving a second does not disturb the first, and forgetting one forgets only
        /// that one.
        /// </summary>
        [Test]
        public static void TheStoreKeepsOneSessionPerPeer()
        {
            var storage = new Memory();
            var store = new SessionStore(storage, new SignalSessions.CounterRandom());

            SessionRecord first = SignalSessions.Alice(new SignalSessions.CounterRandom());
            SessionRecord second = SignalSessions.Alice(new SignalSessions.CounterRandom());

            // One of them has sent a message, so the two records are not the same bytes: a
            // load that crossed would show up as the wrong *session*, and not as the same
            // session read twice, which is the failure a sloppier test would miss.
            Send(first, SignalSessions.Bob(new SignalSessions.CounterRandom()), "a");
            Assert.IsFalse(Same(first.Encode(), second.Encode()), "two sessions are two records");

            store.Save("15551111111@s.whatsapp.net", first);
            store.Save("15552222222@s.whatsapp.net", second);

            Assert.ByteArraysEqual(first.Encode(), store.Load("15551111111@s.whatsapp.net").Encode());
            Assert.ByteArraysEqual(second.Encode(), store.Load("15552222222@s.whatsapp.net").Encode());

            store.Save("15551111111@s.whatsapp.net", second);
            Assert.ByteArraysEqual(second.Encode(), store.Load("15551111111@s.whatsapp.net").Encode());

            store.Remove("15551111111@s.whatsapp.net");
            Assert.IsTrue(store.Load("15551111111@s.whatsapp.net") == null);
            Assert.ByteArraysEqual(second.Encode(), store.Load("15552222222@s.whatsapp.net").Encode());

            // Removing one that is not there is not an error, which is what an unblocked
            // contact and a second app start both do.
            store.Remove("15553333333@s.whatsapp.net");
        }

        /// <summary>
        /// A one-time pre-key is handed out exactly once, and a restart does not hand it out
        /// again. The second half is why the store writes an index as well as the keys: a
        /// device that published five pre-keys, spent two and restarted has to know it has
        /// three left, and the ids it has already spent have to stay spent - the message
        /// that used one is on its way, and answering it twice with the same key is the one
        /// mistake a one-time key exists to prevent.
        /// </summary>
        [Test]
        public static void ThePreKeyStoreNeverHandsTheSameOneTimeKeyTwice()
        {
            var storage = new Memory();
            var store = new PreKeyStore(storage);

            var keys = new byte[3][];
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = new byte[32];
                for (int b = 0; b < keys[i].Length; b++) keys[i][b] = (byte)(0x20 + i);

                store.Publish((uint)(i + 1), keys[i]);
            }

            Assert.AreEqual(3, store.Remaining);

            Assert.ByteArraysEqual(keys[1], store.Take(2));
            Assert.AreEqual(2, store.Remaining);

            Assert.IsTrue(store.Take(2) == null, "a key that was handed out is not handed out again");
            Assert.IsTrue(store.Take(99) == null, "and an id that was never published is not a key");
            Assert.AreEqual(2, store.Remaining);

            // A restart: a second store over the same bytes. The remaining ids are where the
            // first one left them, and the spent one stays spent.
            var restarted = new PreKeyStore(storage);

            Assert.AreEqual(2, restarted.Remaining);
            Assert.IsTrue(restarted.Take(2) == null);
            Assert.ByteArraysEqual(keys[2], restarted.Take(3));
            Assert.AreEqual(1, restarted.Remaining);

            // The signed pre-key, which is the other half of the same object: its id, its
            // private half and its signature survive the restart together, because a bundle
            // published from one and a message answered from the other is a signature over
            // the wrong key.
            restarted.SetSignedPreKey(SignalFixture.X3dh.BobSignedPreKeyId,
                SignalFixture.BobSide.SignedPreKeyPrivate,
                SignalFixture.X3dh.BobSignedPreKeySignature);

            var after = new PreKeyStore(storage);

            Assert.IsTrue(after.HasSignedPreKey);
            Assert.AreEqual(SignalFixture.X3dh.BobSignedPreKeyId, after.SignedPreKeyId);
            Assert.ByteArraysEqual(SignalFixture.BobSide.SignedPreKeyPrivate, after.SignedPreKeyPrivate);
            Assert.ByteArraysEqual(SignalFixture.X3dh.BobSignedPreKeySignature, after.SignedPreKeySignature);

            // A store with nothing in it answers the same way rather than throwing: an
            // install that has not published anything yet is an ordinary install.
            var empty = new PreKeyStore(new Memory());
            Assert.AreEqual(0, empty.Remaining);
            Assert.IsTrue(empty.Take(1) == null);
            Assert.IsFalse(empty.HasSignedPreKey);
        }

        /// <summary>One message, delivered the way the client does it; see `SignalSessionTests.Deliver`.</summary>
        private static void Send(SessionRecord from, SessionRecord to, string text)
        {
            bool preKey = from.PreKeyPending;

            to.Decrypt(from.Encrypt(Encoding.UTF8.GetBytes(text)), preKey);
        }

        /// <summary>And back, which is the direction that makes the peer's chain move.</summary>
        private static void Deliver(SessionRecord from, SessionRecord to, string text)
        {
            Send(from, to, text);
        }

        private static bool Same(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }

            return true;
        }

        /// <summary>
        /// The phone without a filesystem: the same three methods, over a dictionary. It is
        /// what makes the two stores testable off the phone at all, and it is why
        /// `ISessionStorage` is an interface rather than a class that reads files.
        /// </summary>
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
