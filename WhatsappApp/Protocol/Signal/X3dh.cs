using System;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Signal
{
    /// <summary>
    /// The first agreement of a session: three Diffie-Hellmans (four when the receiver
    /// had a one-time pre-key left) that both sides can compute, and one key out of it.
    ///
    /// The order is the whole of it, and it is the reference's rather than the
    /// specification's own notation, because only one of the two is what WhatsApp
    /// runs:
    ///
    ///   [0xFF x 32][DH1 = DH(IKA, SPKB)][DH2 = DH(EKA, IKB)][DH3 = DH(EKA, SPKB)][DH4 = DH(EKA, OPKB)]
    ///
    /// and then `HKDF(material, salt = 32 zero bytes, info = "WhisperText")`, whose first
    /// 32-byte chunk is the session's root key. The 32 bytes of `0xFF` in front are not
    /// padding: they are the domain separation the X3DH specification defines for an
    /// X25519 agreement, so that a signature and an agreement over the same bytes cannot
    /// be confused.
    ///
    /// Both sides build the same buffer, and the reference swaps the first two chunks
    /// for the responder rather than writing them in the other order - which is the same
    /// thing, spelled in a way that hides a bug. Here each side writes its own three or
    /// four outputs into the positions above, and the test that proves it is the
    /// reference's own frozen conversation: if the buffer were arranged differently, the
    /// first ciphertext would not come out byte for byte.
    /// </summary>
    public static class X3dh
    {
        /// <summary>The 32-byte root key of a session started from a pre-key bundle.</summary>
        public static byte[] AliceSecret(
            byte[] aliceIdentityPrivate,
            byte[] aliceEphemeralPrivate,
            byte[] bobIdentityPublic,
            byte[] bobSignedPreKeyPublic,
            byte[] bobOneTimePreKeyPublic)
        {
            Require(aliceIdentityPrivate, "Alice's identity private key");
            Require(aliceEphemeralPrivate, "Alice's ephemeral private key");
            Require(bobIdentityPublic, "Bob's identity public key");
            Require(bobSignedPreKeyPublic, "Bob's signed pre-key");

            byte[] bobIdentity = Bare(bobIdentityPublic);
            byte[] bobSignedPreKey = Bare(bobSignedPreKeyPublic);

            byte[] dh1 = Curve25519.SharedSecret(aliceIdentityPrivate, bobSignedPreKey);
            byte[] dh2 = Curve25519.SharedSecret(aliceEphemeralPrivate, bobIdentity);
            byte[] dh3 = Curve25519.SharedSecret(aliceEphemeralPrivate, bobSignedPreKey);

            byte[] oneTime = null;
            if (bobOneTimePreKeyPublic != null && bobOneTimePreKeyPublic.Length > 0)
            {
                oneTime = Curve25519.SharedSecret(aliceEphemeralPrivate, Bare(bobOneTimePreKeyPublic));
            }

            return Derive(dh1, dh2, dh3, oneTime);
        }

        /// <summary>
        /// The same root key from the receiver's side. `bobOneTimePreKeyPrivate` is null
        /// when the message was built without one, which is the ordinary case once a
        /// device has published more pre-keys than it has received messages: the server
        /// hands out what it still has.
        /// </summary>
        public static byte[] BobSecret(
            byte[] bobIdentityPrivate,
            byte[] bobSignedPreKeyPrivate,
            byte[] bobOneTimePreKeyPrivate,
            byte[] aliceIdentityPublic,
            byte[] aliceEphemeralPublic)
        {
            Require(bobIdentityPrivate, "Bob's identity private key");
            Require(bobSignedPreKeyPrivate, "Bob's signed pre-key private half");
            Require(aliceIdentityPublic, "Alice's identity public key");
            Require(aliceEphemeralPublic, "Alice's ephemeral public key");

            byte[] aliceIdentity = Bare(aliceIdentityPublic);
            byte[] aliceEphemeral = Bare(aliceEphemeralPublic);

            byte[] dh1 = Curve25519.SharedSecret(bobSignedPreKeyPrivate, aliceIdentity);
            byte[] dh2 = Curve25519.SharedSecret(bobIdentityPrivate, aliceEphemeral);
            byte[] dh3 = Curve25519.SharedSecret(bobSignedPreKeyPrivate, aliceEphemeral);

            byte[] oneTime = null;
            if (bobOneTimePreKeyPrivate != null && bobOneTimePreKeyPrivate.Length > 0)
            {
                oneTime = Curve25519.SharedSecret(bobOneTimePreKeyPrivate, aliceEphemeral);
            }

            return Derive(dh1, dh2, dh3, oneTime);
        }

        /// <summary>
        /// The buffer and the derivation, shared by both sides so that there is exactly
        /// one place where the order is written down.
        /// </summary>
        private static byte[] Derive(byte[] dh1, byte[] dh2, byte[] dh3, byte[] dh4)
        {
            int chunks = dh4 == null ? 4 : 5;
            var material = new byte[chunks * 32];

            Buffer.BlockCopy(SignalConstants.AgreementPrefix, 0, material, 0, 32);
            Buffer.BlockCopy(dh1, 0, material, 32, 32);
            Buffer.BlockCopy(dh2, 0, material, 64, 32);
            Buffer.BlockCopy(dh3, 0, material, 96, 32);
            if (dh4 != null) Buffer.BlockCopy(dh4, 0, material, 128, 32);

            return Hkdf.Derive(material, new byte[32], Text(SignalConstants.InfoText), 32);
        }

        /// <summary>
        /// A key with the type byte in front, which is how the reference carries identity
        /// and pre-keys, and how this file is handed them. 32 bytes are accepted as well:
        /// the type byte is a Signal convention, and a caller holding a bare X25519 key
        /// should not have to invent one.
        /// </summary>
        public static byte[] Bare(byte[] key)
        {
            // Named before it is read: a null key is a refusal with a sentence, which is
            // what every caller of this file catches, not a NullReferenceException.
            Require(key, "a key");

            if (key.Length == SignalConstants.TypedKeyLength)
            {
                if (key[0] != SignalConstants.KeyTypeByte)
                {
                    throw new ProtocolException("a key of 33 bytes starts with 0x05, not 0x"
                        + key[0].ToString("x2"));
                }

                var bare = new byte[32];
                Buffer.BlockCopy(key, 1, bare, 0, 32);
                return bare;
            }

            return key;
        }

        /// <summary>A 32-byte key in the form the reference's messages carry it.</summary>
        public static byte[] Typed(byte[] key)
        {
            Require(key, "a key");

            if (key.Length == SignalConstants.TypedKeyLength) return key;

            var typed = new byte[SignalConstants.TypedKeyLength];
            typed[0] = SignalConstants.KeyTypeByte;
            Buffer.BlockCopy(key, 0, typed, 1, 32);
            return typed;
        }

        /// <summary>The public half of a private key, typed the way the wire wants it.</summary>
        public static byte[] TypedPublicKey(byte[] privateKey)
        {
            return Typed(Curve25519.PublicKey(privateKey));
        }

        /// <summary>UTF-8 bytes for an HKDF info string, which the core's Hkdf takes as bytes.</summary>
        public static byte[] Text(string value)
        {
            return System.Text.Encoding.UTF8.GetBytes(value);
        }

        private static void Require(byte[] key, string what)
        {
            if (key == null)
            {
                throw new ProtocolException("the agreement needs " + what);
            }

            if (key.Length != 32 && key.Length != SignalConstants.TypedKeyLength)
            {
                throw new ProtocolException(what + " is " + key.Length + " bytes and should be 32");
            }
        }
    }
}
