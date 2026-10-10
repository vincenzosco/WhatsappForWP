using System;

namespace WhatsappApp.Protocol.Signal
{
    /// <summary>
    /// What the server hands out about a device that wants to be talked to: its identity
    /// key, its signed pre-key with the signature over it, and one of its one-time
    /// pre-keys if it still has any.
    ///
    /// The signature is over the *typed* signed pre-key (`0x05` then the 32 bytes) and it
    /// is XEdDSA, which is why this class carries it: a bundle whose signature does not
    /// verify is a bundle from somebody else, and the check is the only thing standing
    /// between a session and a man in the middle. The verify itself belongs to
    /// <see cref="XEdDsa.Verify"/> and is the caller's step, not this type's: a bundle is
    /// data, and data does not verify itself.
    /// </summary>
    public sealed class PreKeyBundle
    {
        /// <summary>The owner's registration id, which the first message carries back to them.</summary>
        public uint RegistrationId;

        /// <summary>The owner's identity key, 33 bytes with its type byte.</summary>
        public byte[] IdentityKey;

        /// <summary>Which one-time pre-key was handed out; ignored when <see cref="HasPreKey"/> is false.</summary>
        public uint PreKeyId;

        /// <summary>False when the server had no one-time pre-key left for this device.</summary>
        public bool HasPreKey;

        /// <summary>That pre-key's public half, 33 bytes.</summary>
        public byte[] PreKeyPublic;

        /// <summary>Which signed pre-key was handed out.</summary>
        public uint SignedPreKeyId;

        /// <summary>
        /// Whether the bundle names a signed pre-key id. A server always does, and the
        /// flag exists for the same reason the message's presence flags do: the wire says
        /// absent, not zero.
        /// </summary>
        public bool HasSignedPreKey;

        /// <summary>Its public half, 33 bytes.</summary>
        public byte[] SignedPreKeyPublic;

        /// <summary>The owner's XEdDSA signature over the typed signed pre-key, 64 bytes.</summary>
        public byte[] SignedPreKeySignature;

        /// <summary>
        /// The bundle a session can actually be built from. It is checked where the
        /// session is started rather than on every field's use, so that a bundle missing
        /// its identity key fails once, with a sentence, instead of three steps later.
        /// </summary>
        public void Check()
        {
            if (IdentityKey == null) throw new ProtocolException("a bundle without an identity key is not a bundle");
            if (SignedPreKeyPublic == null) throw new ProtocolException("a bundle without a signed pre-key is not a bundle");

            if (HasPreKey && PreKeyPublic == null)
            {
                throw new ProtocolException("the bundle names a one-time pre-key it does not carry");
            }
        }
    }
}
