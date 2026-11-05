using System;
using System.Text;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The four pieces a call's media path needs before a datagram can move: the two ICE
    /// credentials and the two SSRCs, one pair per side.
    ///
    /// Why they are derived and not read: the call stanza carries the media (audio, video), the
    /// encrypted call key and the candidates, and nothing else. There is no `ufrag` field in it
    /// and no SSRC field - the reference derives both from the call key, which is what its
    /// `DefaultCryptoCallback` exists for (`hkdfSha256`, `getSecureSsrc`), and the two ends agree
    /// on them because they hold the same 32 bytes and the same call id.
    ///
    /// The derivation below is an **inference**, and named as one: the APK shows the reference
    /// hashes the call key into its media material, but the labels it uses for the ICE pair are
    /// inside the native engine. What makes this shape defensible is that it is symmetric -
    /// the caller's local credential is the callee's remote one, by construction, because both
    /// come from the same key and the same call id with the roles swapped - and that a wrong
    /// label fails on the peer's first connectivity check, with a 401 this side can see in its
    /// log, rather than silently a call with no media.
    ///
    /// The SSRCs use the reference's own `generateSecureSsrc` shape: HKDF over the call key with
    /// a four-byte salt and a role tag as the info.
    /// </summary>
    public sealed class CallMediaSetup
    {
        /// <summary>How many bytes an ICE ufrag is derived from: eight hex characters.</summary>
        private const int UfragBytes = 4;

        /// <summary>How many bytes an ICE password is derived from: 32 hex characters.</summary>
        private const int PasswordBytes = 16;

        /// <summary>Whether this side places the call, which is also the DTLS client's side.</summary>
        public bool Caller { get; private set; }

        public string LocalUfrag { get; private set; }

        public string LocalPassword { get; private set; }

        public string RemoteUfrag { get; private set; }

        public string RemotePassword { get; private set; }

        /// <summary>The SSRC this side sends with.</summary>
        public uint LocalSsrc { get; private set; }

        /// <summary>The SSRC the other side sends with, which is what the jitter buffer expects.</summary>
        public uint RemoteSsrc { get; private set; }

        private CallMediaSetup()
        {
        }

        /// <summary>
        /// The setup of one side of one call. `callKey` is the 32 bytes the `enc` node carried,
        /// `callId` the one every node of the call agrees on, and `caller` decides which half of
        /// the derivation is this side's.
        /// </summary>
        public static CallMediaSetup Derive(byte[] callKey, string callId, bool caller)
        {
            if (callKey == null || callKey.Length != CallKeys.CallKeyLength)
            {
                throw new ProtocolException("a call media setup takes the " + CallKeys.CallKeyLength
                    + "-byte call key");
            }

            if (string.IsNullOrEmpty(callId))
            {
                throw new ProtocolException("a call media setup needs the call it belongs to");
            }

            var setup = new CallMediaSetup();
            setup.Caller = caller;

            // The two roles, and not "this side"/"the other": the caller's local credential has
            // to be the callee's remote one, and a label that said "local" would give the two
            // ends two different pairs.
            bool them = !caller;

            setup.LocalUfrag = Text(callKey, callId, "ufrag", caller, UfragBytes);
            setup.LocalPassword = Text(callKey, callId, "password", caller, PasswordBytes);
            setup.RemoteUfrag = Text(callKey, callId, "ufrag", them, UfragBytes);
            setup.RemotePassword = Text(callKey, callId, "password", them, PasswordBytes);

            setup.LocalSsrc = Ssrc(callKey, callId, caller);
            setup.RemoteSsrc = Ssrc(callKey, callId, them);

            return setup;
        }

        /// <summary>One credential, as lowercase hex: printable, and no length prefix to mis-count.</summary>
        private static string Text(byte[] callKey, string callId, string what, bool caller, int length)
        {
            string info = "wp8-call-" + what + ":" + (caller ? "caller" : "callee") + ":" + callId;
            byte[] value = CallKeys.Derive(callKey, null, Encoding.UTF8.GetBytes(info), length);

            var text = new StringBuilder(value.Length * 2);
            for (int i = 0; i < value.Length; i++) text.Append(value[i].ToString("x2"));

            return text.ToString();
        }

        /// <summary>One SSRC, big-endian: the reference derives four bytes and the RTP header reads them so.</summary>
        private static uint Ssrc(byte[] callKey, string callId, bool caller)
        {
            string info = "wp8-call-ssrc:" + (caller ? "caller" : "callee") + ":" + callId;
            byte[] salt = CallKeys.Derive(callKey, null,
                Encoding.UTF8.GetBytes("wp8-call-ssrc-salt:" + callId), CallKeys.SecureSsrcLength);

            byte[] value = CallKeys.SecureSsrc(callKey, Encoding.UTF8.GetBytes(info), salt);

            return (uint)((value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3]);
        }
    }
}
