using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// Where randomness comes from.
    ///
    /// The core cannot draw it itself: `System.Security.Cryptography` is not in the
    /// WP8.1 projection (the C# 5 guard refuses the using), and the WinRT generator
    /// (`CryptographicBuffer`) is a `Windows.*` type the desktop host cannot compile.
    /// So the one thing the core cannot own is injected, and the phone supplies a
    /// WinRT-backed source while a test supplies a fixed one - which is what makes
    /// `Credentials.Create` reproducible enough to assert on.
    /// </summary>
    public interface IRandomSource
    {
        /// <summary>The next `count` random bytes. Never all zeros and never short.</summary>
        byte[] Bytes(int count);
    }

    /// <summary>
    /// Everything this device is, as far as WhatsApp is concerned: who it is (the
    /// identity and Noise key pairs), what it is (the signed pre-key and the ADV
    /// secret), and whose account it is (the JID, once the server has paired it).
    ///
    /// This is the account. The identity private key and the ADV secret are the two
    /// things that let anyone holding them impersonate the device, so they live in
    /// <see cref="CredentialStore"/> and nowhere else - not in a log line, not in a
    /// crash dump, not in the source.
    /// </summary>
    public sealed class Credentials
    {
        /// <summary>32 bytes, the long-lived Noise key that authenticates this client.</summary>
        public byte[] NoisePrivateKey;
        public byte[] NoisePublicKey;

        /// <summary>32 bytes, the Signal identity key. XEdDSA signs with these bytes as they are.</summary>
        public byte[] IdentityPrivateKey;
        public byte[] IdentityPublicKey;

        /// <summary>32 bytes: the secret behind the account's HMAC. Rotating it re-keys the account.</summary>
        public byte[] AdvSecret;

        /// <summary>14 bits, drawn fresh for a device that has no account yet.</summary>
        public uint RegistrationId;

        /// <summary>The signed pre-key's id (three bytes on the wire) and its key pair.</summary>
        public uint SignedPreKeyId;
        public byte[] SignedPreKeyPublic;
        public byte[] SignedPreKeyPrivate;
        public byte[] SignedPreKeySignature;

        /// <summary>The account and device, once the server has paired this client.</summary>
        public string Jid;
        public string Lid;

        /// <summary>What the server calls this client's platform, from `pair-success`.</summary>
        public string Platform;

        /// <summary>The account's own display name, when `pair-success` carries one.</summary>
        public string PushName;

        /// <summary>
        /// The app-state keys the primary has shared with this device, by id.
        ///
        /// Kept with the account and not in memory: the phone sends the share **once**, when
        /// it links the device, and a client that kept it only in memory lost the ability to
        /// read its own account's app state - the saved contact names above all - at the first
        /// restart. They are emptied with the account (`CredentialStore.Clear`): they are that
        /// account's keys and mean nothing to the next one.
        /// </summary>
        public IList<AppStateKey> AppStateKeys = new List<AppStateKey>();

        /// <summary>Whether this set describes a paired device rather than a candidate one.</summary>
        public bool IsPaired
        {
            get { return !string.IsNullOrEmpty(Jid); }
        }

        /// <summary>
        /// Draws a fresh set: the two key pairs, the ADV secret, a registration id and a
        /// signed pre-key.
        ///
        /// The private keys are **clamped** here, the way Signal generates them
        /// (`k[0] &amp;= 248; k[31] &amp;= 127; k[31] |= 64`), because XEdDSA signs with
        /// those bytes unreduced: a signer that fed the unclamped bytes to the scalar
        /// arithmetic would produce a signature that verifies nowhere. Clamping at the
        /// source also means the public key stored beside the private one is the public
        /// key of the clamped scalar, which is the pair the server checks.
        /// </summary>
        public static Credentials Create(IRandomSource random)
        {
            if (random == null) throw new ProtocolException("creating credentials needs randomness");

            var credentials = new Credentials();

            credentials.NoisePrivateKey = Clamp(random.Bytes(32));
            credentials.NoisePublicKey = Curve25519.PublicKey(credentials.NoisePrivateKey);

            credentials.IdentityPrivateKey = Clamp(random.Bytes(32));
            credentials.IdentityPublicKey = Curve25519.PublicKey(credentials.IdentityPrivateKey);

            credentials.AdvSecret = random.Bytes(32);
            credentials.RegistrationId = RegistrationIdFrom(random.Bytes(4));

            credentials.SignedPreKeyId = 1;
            credentials.SignedPreKeyPrivate = Clamp(random.Bytes(32));
            credentials.SignedPreKeyPublic = Curve25519.PublicKey(credentials.SignedPreKeyPrivate);
            credentials.SignedPreKeySignature = XEdDsa.Sign(
                credentials.IdentityPrivateKey, SignedPreKeyToSign(credentials.SignedPreKeyPublic));

            return credentials;
        }

        /// <summary>
        /// The bytes a signed pre-key's signature covers: `0x05` and the public key.
        ///
        /// That prefix is not decoration - the reference signs the same 33 bytes, and a
        /// signature over the bare 32-byte key is one the server refuses when the key is
        /// uploaded.
        /// </summary>
        public static byte[] SignedPreKeyToSign(byte[] signedPreKeyPublic)
        {
            if (signedPreKeyPublic == null || signedPreKeyPublic.Length != Curve25519.KeyLength)
            {
                throw new ProtocolException("a signed pre-key is 32 bytes");
            }

            var message = new byte[1 + Curve25519.KeyLength];
            message[0] = 0x05;
            Buffer.BlockCopy(signedPreKeyPublic, 0, message, 1, Curve25519.KeyLength);
            return message;
        }

        /// <summary>Signal's registration ids are 14 bits, and zero is not one of them.</summary>
        public const int RegistrationIdBits = 14;

        private static uint RegistrationIdFrom(byte[] fourBytes)
        {
            uint value = 0;
            for (int i = 0; i < fourBytes.Length; i++) value = (value << 8) | fourBytes[i];

            return (value % ((1U << RegistrationIdBits) - 1)) + 1;
        }

        /// <summary>
        /// Signal's clamping, in place: `k[0] &amp;= 248; k[31] &amp;= 127; k[31] |= 64`.
        ///
        /// Public because a sender key signing key needs the same treatment for the same
        /// reason (`SenderKeyRecord.Create`): XEdDSA signs with the bytes as they are.
        /// </summary>
        public static byte[] Clamp(byte[] privateKey)
        {
            if (privateKey == null || privateKey.Length != Curve25519.KeyLength)
            {
                throw new ProtocolException("a private key is 32 bytes");
            }

            privateKey[0] = (byte)(privateKey[0] & 248);
            privateKey[Curve25519.KeyLength - 1] = (byte)(privateKey[Curve25519.KeyLength - 1] & 127);
            privateKey[Curve25519.KeyLength - 1] = (byte)(privateKey[Curve25519.KeyLength - 1] | 64);
            return privateKey;
        }
    }
}
