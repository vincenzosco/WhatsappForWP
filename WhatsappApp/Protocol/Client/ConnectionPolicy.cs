using System;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// The decisions a session loop makes, one rule each.
    ///
    /// They are here and not inside `Services/NativeConnection`'s loop because a decision
    /// written into a `while (true)` is a decision only a phone can exercise - and these
    /// three are exactly what the device runs of 2026-10-10 got wrong. The loop stayed put
    /// when the server closed the connection (the QR of 14:45 sat on a socket that was
    /// already gone), it had no answer for a `failure`, and nothing ever published this
    /// device's one-time pre-keys, so a peer could not start a session with it.
    ///
    /// The codes are the reference's, not invented here: `whatsmeow`
    /// (`types/events/events.go`, `IsLoggedOut` -> 401, 403, 406; `connectionevents.go`,
    /// `stream:error` 401 with `conflict type="device_removed"` deletes the store and 515
    /// reconnects) is the authority for the two halves of "the connection ended", and
    /// `ConnectionPolicyTests` carries the numbers.
    /// </summary>
    public static class ConnectionPolicy
    {
        /// <summary>Logged out from another device: this account is no longer ours.</summary>
        public const string ReasonLoggedOut = "401";

        /// <summary>The primary device was logged out. The web client calls this LOCKED.</summary>
        public const string ReasonPrimaryDeviceGone = "403";

        /// <summary>Banned. The web client calls this BANNED.</summary>
        public const string ReasonBanned = "406";

        /// <summary>The stream error that carries a device removal.</summary>
        public const string StreamCodeDeviceRemoved = "401";

        /// <summary>The wait before the nth attempt, in seconds.</summary>
        private static readonly int[] Waits = { 1, 2, 4, 8, 15, 30 };

        /// <summary>
        /// How long to wait before dialling again. `attempt` counts from 1 and anything
        /// past the table waits the table's last entry, so a phone that has been offline
        /// for an hour keeps trying once every half minute instead of once every hour.
        /// </summary>
        public static int ReconnectDelaySeconds(int attempt)
        {
            if (attempt <= 1) return Waits[0];
            if (attempt >= Waits.Length) return Waits[Waits.Length - 1];

            return Waits[attempt - 1];
        }

        /// <summary>
        /// Whether the node that ended the connection took the account with it.
        ///
        /// True for the three `failure` reasons the reference deletes its store for, and for
        /// the one stream error that carries a device removal. Everything else - a dropped
        /// socket, a restart-required 515, an unavailable service 503, and the 405 that means
        /// *this app* is older than the server's floor - is a connection to be remade, because
        /// the account behind it is still good.
        /// </summary>
        public static bool AccountIsGone(BinaryNode node)
        {
            if (node == null) return false;

            if (node.Tag == "failure") return IsLogoutReason(node.Attrs["reason"]);
            if (node.Tag == "stream:error") return IsDeviceRemoved(node);

            return false;
        }

        /// <summary>
        /// Whether the server is running low on this device's one-time pre-keys. A server
        /// that has run out stops handing this phone out to peers, and a peer that cannot
        /// fetch a bundle cannot write to it at all.
        /// </summary>
        public static bool NeedsPreKeys(int remaining)
        {
            return PreKeyUpload.NeedsUpload(remaining, PreKeyUpload.LowWaterMark);
        }

        /// <summary>
        /// Whether nothing has been heard for longer than the ping interval. The rule itself
        /// is the keep-alive's; it is renamed here so the loop has one place to ask.
        /// </summary>
        public static bool ConnectionIsStale(DateTime lastNodeUtc, DateTime nowUtc)
        {
            return SessionKeepAlive.IsStale(lastNodeUtc, nowUtc);
        }

        private static bool IsLogoutReason(string reason)
        {
            return reason == ReasonLoggedOut || reason == ReasonPrimaryDeviceGone || reason == ReasonBanned;
        }

        private static bool IsDeviceRemoved(BinaryNode node)
        {
            if (node.Attrs["code"] != StreamCodeDeviceRemoved) return false;

            BinaryNode conflict = node.Child("conflict");

            return conflict != null && conflict.Attrs["type"] == "device_removed";
        }
    }
}
