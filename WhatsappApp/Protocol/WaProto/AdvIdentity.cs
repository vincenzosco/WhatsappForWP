using System;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.WaProto
{
    /// <summary>
    /// `ADVSignedDeviceIdentityHMAC`: what `pair-success` hands over.
    ///
    /// The details are signed twice - once by the *account* (WhatsApp's own key) and
    /// once by the device - and the whole thing is authenticated with the ADV secret,
    /// which is the secret this client has and nobody else does. Checking that HMAC is
    /// what proves the reply belongs to the pairing this client started; without it a
    /// device would accept an account signature meant for someone else.
    /// </summary>
    public sealed class AdvSignedDeviceIdentityContainer
    {
        public const int DetailsField = 1;
        public const int HmacField = 2;
        public const int AccountTypeField = 3;

        public byte[] Details;
        public byte[] Hmac;

        /// <summary>0 = end to end, 1 = hosted. Only the second changes the prefixes below.</summary>
        public int AccountType;

        public static AdvSignedDeviceIdentityContainer Decode(byte[] bytes)
        {
            var container = new AdvSignedDeviceIdentityContainer();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.WireType == 2 && field.Number == DetailsField) container.Details = reader.ReadBytes();
                else if (field.WireType == 2 && field.Number == HmacField) container.Hmac = reader.ReadBytes();
                else if (field.WireType == 0 && field.Number == AccountTypeField) container.AccountType = (int)reader.ReadVarint();
                else reader.SkipField(field.WireType);
            }

            if (container.Details == null) throw new ProtocolException("the pair-success payload carries no device identity");
            return container;
        }
    }

    /// <summary>`ADVSignedDeviceIdentity`: the device's identity as the account signed it.</summary>
    public sealed class AdvSignedDeviceIdentity
    {
        public const int DetailsField = 1;
        public const int AccountSignatureKeyField = 2;
        public const int AccountSignatureField = 3;
        public const int DeviceSignatureField = 4;

        public byte[] Details;

        /// <summary>The account's public signature key. The account signs with its private half.</summary>
        public byte[] AccountSignatureKey;

        /// <summary>The account's signature over this device's details.</summary>
        public byte[] AccountSignature;

        /// <summary>This device's own signature, which the client adds before replying.</summary>
        public byte[] DeviceSignature;

        public static AdvSignedDeviceIdentity Decode(byte[] bytes)
        {
            var identity = new AdvSignedDeviceIdentity();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.WireType == 2 && field.Number == DetailsField) identity.Details = reader.ReadBytes();
                else if (field.WireType == 2 && field.Number == AccountSignatureKeyField) identity.AccountSignatureKey = reader.ReadBytes();
                else if (field.WireType == 2 && field.Number == AccountSignatureField) identity.AccountSignature = reader.ReadBytes();
                else if (field.WireType == 2 && field.Number == DeviceSignatureField) identity.DeviceSignature = reader.ReadBytes();
                else reader.SkipField(field.WireType);
            }

            if (identity.Details == null) throw new ProtocolException("the signed device identity carries no details");
            return identity;
        }

        /// <summary>Encodes the message back, with whatever fields are set on it.</summary>
        public byte[] Encode()
        {
            return new ProtoWriter()
                .Bytes(DetailsField, Details)
                .Bytes(AccountSignatureKeyField, AccountSignatureKey)
                .Bytes(AccountSignatureField, AccountSignature)
                .Bytes(DeviceSignatureField, DeviceSignature)
                .ToArray();
        }

        /// <summary>
        /// The same message without the account's signature key, which is what the
        /// client sends back in `pair-device-sign`: the key identifies the account and
        /// the server already has it; the reference drops it for the same reason, and
        /// keeps a copy of the whole message as the account it stores.
        /// </summary>
        public byte[] EncodeForServer()
        {
            return new ProtoWriter()
                .Bytes(DetailsField, Details)
                .Bytes(AccountSignatureField, AccountSignature)
                .Bytes(DeviceSignatureField, DeviceSignature)
                .ToArray();
        }
    }

    /// <summary>`ADVDeviceIdentity`: the fields the account's and the device's signatures cover.</summary>
    public sealed class AdvDeviceIdentity
    {
        public const int RawIdField = 1;
        public const int TimestampField = 2;
        public const int KeyIndexField = 3;
        public const int AccountTypeField = 4;
        public const int DeviceTypeField = 5;

        public uint RawId;
        public ulong Timestamp;
        public uint KeyIndex;
        public int AccountType;
        public int DeviceType;

        /// <summary>`ADVEncryptionType.HOSTED`, the one value that changes the prefixes.</summary>
        public const int Hosted = 1;

        public static AdvDeviceIdentity Decode(byte[] bytes)
        {
            var identity = new AdvDeviceIdentity();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.WireType == 0 && field.Number == RawIdField) identity.RawId = (uint)reader.ReadVarint();
                else if (field.WireType == 0 && field.Number == TimestampField) identity.Timestamp = reader.ReadVarint();
                else if (field.WireType == 0 && field.Number == KeyIndexField) identity.KeyIndex = (uint)reader.ReadVarint();
                else if (field.WireType == 0 && field.Number == AccountTypeField) identity.AccountType = (int)reader.ReadVarint();
                else if (field.WireType == 0 && field.Number == DeviceTypeField) identity.DeviceType = (int)reader.ReadVarint();
                else reader.SkipField(field.WireType);
            }

            return identity;
        }
    }

    /// <summary>
    /// The two signatures and the HMAC of a pairing reply, spelled out as the reference
    /// has them: the prefixes are single bytes that say which signature this is, and
    /// they are part of the signed message, not a wrapper around it.
    /// </summary>
    public static class AdvSignatures
    {
        /// <summary>`0x06 0x00`, in front of what the account signs.</summary>
        public static readonly byte[] AccountSignaturePrefix = new byte[] { 6, 0 };

        /// <summary>`0x06 0x01`, in front of what this device signs.</summary>
        public static readonly byte[] DeviceSignaturePrefix = new byte[] { 6, 1 };

        /// <summary>`0x06 0x05`: the hosted account's prefix, and the HMAC's.</summary>
        public static readonly byte[] HostedAccountSignaturePrefix = new byte[] { 6, 5 };

        /// <summary>`0x06 0x06`: the hosted device's half of the same pair.</summary>
        public static readonly byte[] HostedDeviceSignaturePrefix = new byte[] { 6, 6 };

        /// <summary>
        /// The HMAC the server sends over the device identity, keyed with the ADV secret.
        ///
        /// A hosted account's details carry one more byte in front of them (`0x06 0x05`),
        /// and a client that forgets it computes an HMAC that never matches. The
        /// comparison is constant-time, because the value it guards is a secret.
        /// </summary>
        public static byte[] DetailsHmac(byte[] advSecret, byte[] details, bool hosted)
        {
            if (advSecret == null || advSecret.Length == 0) throw new ProtocolException("the HMAC needs the ADV secret");
            if (details == null) throw new ProtocolException("the HMAC needs the details it covers");

            byte[] message = hosted ? Concat(HostedAccountSignaturePrefix, details) : details;
            return Hmac.Compute(advSecret, message);
        }

        /// <summary>
        /// Whether the account really signed this device's details: `0x06 0x00 || details
        /// || our identity public key`, verified with the account's signature key.
        ///
        /// The identity public key is inside the signed message, which is what ties the
        /// signature to *this* device rather than to any device.
        /// </summary>
        public static bool VerifyAccountSignature(AdvSignedDeviceIdentity identity, byte[] identityPublicKey, bool hosted)
        {
            if (identity == null || identity.Details == null) return false;
            if (identity.AccountSignatureKey == null || identity.AccountSignatureKey.Length != 32) return false;
            if (identity.AccountSignature == null || identity.AccountSignature.Length != 64) return false;
            if (identityPublicKey == null || identityPublicKey.Length != 32) return false;

            byte[] prefix = hosted ? HostedAccountSignaturePrefix : AccountSignaturePrefix;
            byte[] message = Concat(prefix, identity.Details, identityPublicKey);
            return XEdDsa.Verify(identity.AccountSignatureKey, message, identity.AccountSignature);
        }

        /// <summary>
        /// This device's signature over its own details: `0x06 0x01 || details || identity
        /// public key || the account's signature key`. Without it the server has no proof
        /// that the key it just paired belongs to this client.
        /// </summary>
        public static byte[] SignDevice(AdvSignedDeviceIdentity identity, byte[] identityPrivateKey,
            byte[] identityPublicKey, bool hosted)
        {
            if (identity == null || identity.Details == null) throw new ProtocolException("there are no details to sign");

            byte[] prefix = hosted ? HostedDeviceSignaturePrefix : DeviceSignaturePrefix;
            byte[] message = Concat(prefix, identity.Details, identityPublicKey, identity.AccountSignatureKey);

            return XEdDsa.Sign(identityPrivateKey, message);
        }

        /// <summary>Concatenates byte runs, in order, into one array.</summary>
        public static byte[] Concat(params byte[][] parts)
        {
            int length = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] != null) length += parts[i].Length;
            }

            var joined = new byte[length];
            int offset = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null) continue;

                Buffer.BlockCopy(parts[i], 0, joined, offset, parts[i].Length);
                offset += parts[i].Length;
            }

            return joined;
        }
    }
}
