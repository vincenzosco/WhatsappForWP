using System;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The decisions the session loop makes, one rule each.
    ///
    /// Which codes end an *account* and which only end a *connection* is not this client's
    /// invention, and these tests carry the numbers they were read from:
    /// `/tmp/ref/whatsmeow-main/types/events/events.go` - `IsLoggedOut` is 401
    /// ("logged out from another device"), 403 ("primary device was logged out", called
    /// LOCKED in the web client) and 406 ("logged out for unknown reason", called BANNED);
    /// 405 is the client being old and 409 a rejected user agent, and neither takes the
    /// account away. `/tmp/ref/whatsmeow-main/connectionevents.go` - a `stream:error` deletes
    /// the store only for code 401 **with** `conflict type="device_removed"`, while 515
    /// ("restart required") and 503 are reconnects.
    ///
    /// Why the distinction is worth a test: the two answers to "the connection ended" are
    /// opposite. One says delete the account and show the QR, the other says dial again. A
    /// loop that guesses wrong either keeps an account WhatsApp has already taken away - and
    /// sits on a chat list that can never fill - or throws away a perfectly good one because
    /// the Wi-Fi dropped.
    /// </summary>
    public static class ConnectionPolicyTests
    {
        [Test]
        public static void TheBackoffGrowsAndThenStops()
        {
            Assert.AreEqual(1, ConnectionPolicy.ReconnectDelaySeconds(1));
            Assert.AreEqual(2, ConnectionPolicy.ReconnectDelaySeconds(2));
            Assert.AreEqual(4, ConnectionPolicy.ReconnectDelaySeconds(3));
            Assert.AreEqual(8, ConnectionPolicy.ReconnectDelaySeconds(4));
            Assert.AreEqual(15, ConnectionPolicy.ReconnectDelaySeconds(5));
            Assert.AreEqual(30, ConnectionPolicy.ReconnectDelaySeconds(6));
            Assert.IsTrue(ConnectionPolicy.ReconnectDelaySeconds(20) == 30, "the wait stops growing");
            Assert.IsTrue(ConnectionPolicy.ReconnectDelaySeconds(0) == 1,
                "the first attempt waits a second");
            Assert.IsTrue(ConnectionPolicy.ReconnectDelaySeconds(-3) == 1,
                "a negative attempt is still a first attempt, not an exception");
        }

        [Test]
        public static void ALostAccountIsToldApartFromALostConnection()
        {
            Assert.IsTrue(ConnectionPolicy.AccountIsGone(Failure("401")),
                "401 is logged out from another device");
            Assert.IsTrue(ConnectionPolicy.AccountIsGone(Failure("403")),
                "403 is the primary device gone");
            Assert.IsTrue(ConnectionPolicy.AccountIsGone(Failure("406")),
                "406 is banned");
            Assert.IsFalse(ConnectionPolicy.AccountIsGone(Failure("405")),
                "405 is this app being old, not the account being gone");
            Assert.IsFalse(ConnectionPolicy.AccountIsGone(Failure("503")),
                "503 is the service being unavailable");
            Assert.IsFalse(ConnectionPolicy.AccountIsGone(Failure("409")),
                "409 is the user agent being refused, not the account being taken away");
        }

        [Test]
        public static void OnlyADeviceRemovedErrorTakesTheAccountAway()
        {
            Assert.IsTrue(ConnectionPolicy.AccountIsGone(StreamError("401", "device_removed")),
                "the reference deletes the store for exactly this one");
            Assert.IsFalse(ConnectionPolicy.AccountIsGone(StreamError("401", null)),
                "a bare 401 stream error is a dropped connection, not a deletion");
            Assert.IsFalse(ConnectionPolicy.AccountIsGone(StreamError("515", null)),
                "515 is restart required: dial again");
            Assert.IsFalse(ConnectionPolicy.AccountIsGone(StreamError("503", null)));
            Assert.IsFalse(ConnectionPolicy.AccountIsGone(StreamError("500", "device_removed")),
                "only 401 carries a removal");
        }

        [Test]
        public static void SuccessAndOrdinaryNodesDoNotEndAnything()
        {
            Assert.IsFalse(ConnectionPolicy.AccountIsGone(new BinaryNode("success")));
            Assert.IsFalse(ConnectionPolicy.AccountIsGone(new BinaryNode("iq")));
            Assert.IsFalse(ConnectionPolicy.AccountIsGone(null),
                "a node that is not there cannot end an account");
        }

        [Test]
        public static void ThePreKeyFloorIsTheConstant()
        {
            Assert.IsFalse(ConnectionPolicy.NeedsPreKeys(PreKeyUpload.LowWaterMark + 1));
            Assert.IsFalse(ConnectionPolicy.NeedsPreKeys(PreKeyUpload.LowWaterMark),
                "exactly the floor is still enough");
            Assert.IsTrue(ConnectionPolicy.NeedsPreKeys(PreKeyUpload.LowWaterMark - 1));
            Assert.IsTrue(ConnectionPolicy.NeedsPreKeys(0), "a store that is empty needs keys");
        }

        [Test]
        public static void ASilencePastTheIntervalIsStale()
        {
            var start = new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

            Assert.IsFalse(ConnectionPolicy.ConnectionIsStale(start, start.AddSeconds(29)));
            Assert.IsFalse(ConnectionPolicy.ConnectionIsStale(start, start.AddSeconds(30)),
                "exactly one interval is not late");
            Assert.IsTrue(ConnectionPolicy.ConnectionIsStale(start, start.AddSeconds(31)));
            Assert.IsFalse(ConnectionPolicy.ConnectionIsStale(start, start.AddSeconds(-5)),
                "a clock that moved backwards is not a stale connection");
        }

        [Test]
        public static void APingNobodyAnswersIsADeadConnection()
        {
            var sent = new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);

            Assert.IsFalse(ConnectionPolicy.PingUnanswered(sent, sent, sent.AddSeconds(20)),
                "exactly the ceiling is still a connection worth waiting on");
            Assert.IsTrue(ConnectionPolicy.PingUnanswered(sent, sent, sent.AddSeconds(21)),
                "past it nothing is coming back and the socket is gone");
            Assert.IsFalse(ConnectionPolicy.PingUnanswered(sent, sent.AddSeconds(3), sent.AddSeconds(21)),
                "a node that arrived after the ping is the answer, whatever else is happening");
            Assert.IsFalse(ConnectionPolicy.PingUnanswered(sent, sent, sent.AddSeconds(-5)),
                "a clock that moved backwards is not a dead connection");
        }

        private static BinaryNode Failure(string reason)
        {
            var node = new BinaryNode("failure");
            node.Attrs.Add("reason", reason);
            node.Attrs.Add("location", "frc");

            return node;
        }

        private static BinaryNode StreamError(string code, string conflictType)
        {
            var node = new BinaryNode("stream:error");
            node.Attrs.Add("code", code);

            if (conflictType == null) return node;

            var conflict = new BinaryNode("conflict");
            conflict.Attrs.Add("type", conflictType);
            node.Content = new System.Collections.Generic.List<BinaryNode> { conflict };

            return node;
        }
    }
}
