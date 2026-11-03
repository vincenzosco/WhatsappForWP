using System;
using System.Collections.Generic;
using System.Text;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// DTLS 1.2: the constants and the wire shapes of the handshake a call's media channel
    /// starts with (RFC 6347), and the record layer it is carried in.
    ///
    /// Why it is here: WhatsApp's calls hand their media to SRTP, and SRTP's master key is
    /// supposed to arrive from a DTLS handshake - `Srtp.cs` says as much and stops there. This
    /// is that missing half: the record framing, the TLS 1.2 PRF, the key schedule, and the
    /// RFC 5764 exporter that turns a finished handshake into an SRTP master key and salt.
    ///
    /// What is implemented, exactly:
    ///  - the DTLS record layer, 13-byte header, epoch and 48-bit sequence, with
    ///    `TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256` (0xC02B) and its RSA twin (0xC02F) as the
    ///    only two suites, because they are the two an AES-GCM record can serve with the
    ///    primitives this project already has;
    ///  - the TLS 1.2 PRF over SHA-256 (`P_hash`), the master secret, the key block and the
    ///    Finished verify_data;
    ///  - the `use_srtp` extension (profile `SRTP_AES128_CM_HMAC_SHA1_80`) and the exporter
    ///    of RFC 5764, section 4.2.
    ///
    /// What is **not** here, and is not pretended: ECDSA/RSA signature verification of the
    /// ServerKeyExchange. This project has no curve arithmetic for P-256 and no RSA, so a
    /// client built on this file cannot prove the server owns the certificate it presented.
    /// `DtlsClient` therefore refuses to call the handshake trusted unless the caller supplies
    /// the certificate's SHA-256 out of band - the way WebRTC carries a fingerprint in the
    /// signalling - and it reports that decision instead of assuming it.
    /// </summary>
    public static class Dtls
    {
        /// <summary>DTLS 1.2 (RFC 6347). DTLS 1.0 is 0xFEFF, TLS 1.2 is 0x0303.</summary>
        public const ushort Version = 0xFEFD;

        public const byte TypeChangeCipherSpec = 20;
        public const byte TypeAlert = 21;
        public const byte TypeHandshake = 22;
        public const byte TypeApplicationData = 23;

        public const byte HandshakeClientHello = 1;
        public const byte HandshakeServerHello = 2;
        public const byte HandshakeCertificate = 11;
        public const byte HandshakeServerKeyExchange = 12;
        public const byte HandshakeServerHelloDone = 14;
        public const byte HandshakeClientKeyExchange = 16;
        public const byte HandshakeFinished = 20;

        /// <summary>TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256: the suite a WebRTC peer offers first.</summary>
        public const ushort SuiteEcdheEcdsaAes128GcmSha256 = 0xC02B;

        /// <summary>TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256: the same record, a different certificate.</summary>
        public const ushort SuiteEcdheRsaAes128GcmSha256 = 0xC02F;

        /// <summary>Named group X25519 (RFC 8422): the one key exchange this project can do.</summary>
        public const ushort GroupX25519 = 29;

        /// <summary>`SRTP_AES128_CM_HMAC_SHA1_80`, the profile the SRTP half of this project speaks.</summary>
        public const ushort SrtpProfileAes128CmHmacSha1_80 = 0x0007;

        public const int RecordHeaderLength = 13;
        public const int HandshakeHeaderLength = 12;

        /// <summary>32 bytes of client or server random, as TLS 1.2 defines it.</summary>
        public const int RandomLength = 32;

        /// <summary>A handshake message that fits one datagram: no fragmentation is reassembled.</summary>
        public const int MaxPlaintext = 1200;

        /// <summary>The verify_data of a Finished message: 12 bytes for every TLS 1.2 suite here.</summary>
        public const int VerifyDataLength = 12;
    }

    /// <summary>
    /// The TLS 1.2 pseudo-random function over SHA-256 (RFC 5246, section 5).
    ///
    /// Why its own class: three different things ask for it - the master secret, the key block
    /// and the two Finished values - and the SRTP exporter asks for it a fourth time. Written
    /// once, with the label prepended to the seed as the specification says, so a caller
    /// cannot get the order wrong.
    /// </summary>
    public static class DtlsPrf
    {
        /// <summary>`PRF(secret, label, seed)` expanded to `length` bytes.</summary>
        public static byte[] Prf(byte[] secret, string label, byte[] seed, int length)
        {
            if (secret == null || secret.Length == 0)
            {
                throw new ProtocolException("the PRF needs a secret");
            }

            if (length <= 0)
            {
                throw new ProtocolException("the PRF needs a length");
            }

            byte[] labelBytes = Encoding.UTF8.GetBytes(label == null ? "" : label);
            int seedLength = seed == null ? 0 : seed.Length;

            var combined = new byte[labelBytes.Length + seedLength];
            Buffer.BlockCopy(labelBytes, 0, combined, 0, labelBytes.Length);
            if (seedLength > 0) Buffer.BlockCopy(seed, 0, combined, labelBytes.Length, seedLength);

            return PHash(secret, combined, length);
        }

        /// <summary>
        /// P_SHA256: A(1) = HMAC(secret, seed), A(n+1) = HMAC(secret, A(n)), and each block is
        /// HMAC(secret, A(n) || seed). The concatenation of the seed with the running A is the
        /// detail a hand-written version usually drops.
        /// </summary>
        public static byte[] PHash(byte[] secret, byte[] seed, int length)
        {
            var output = new byte[length];
            byte[] a = seed;
            int done = 0;

            while (done < length)
            {
                a = Hmac.Compute(secret, a);

                var input = new byte[a.Length + seed.Length];
                Buffer.BlockCopy(a, 0, input, 0, a.Length);
                Buffer.BlockCopy(seed, 0, input, a.Length, seed.Length);

                byte[] block = Hmac.Compute(secret, input);
                int take = Math.Min(block.Length, length - done);
                Buffer.BlockCopy(block, 0, output, done, take);
                done += take;
            }

            return output;
        }
    }

    /// <summary>One record as it arrived: its header fields and, once opened, its fragment.</summary>
    public sealed class DtlsRecordView
    {
        public byte Type;
        public ushort Epoch;
        public ulong Sequence;
        public byte[] Fragment;
    }

    /// <summary>One direction's record keys: the AES-128-GCM key and its 4-byte fixed IV.</summary>
    public sealed class DtlsRecordKeys
    {
        public readonly byte[] Key;
        public readonly byte[] FixedIv;

        public DtlsRecordKeys(byte[] key, byte[] fixedIv)
        {
            if (key == null || key.Length != 16)
            {
                throw new ProtocolException("a DTLS AES-GCM key is 16 bytes");
            }

            if (fixedIv == null || fixedIv.Length != 4)
            {
                throw new ProtocolException("a DTLS AES-GCM fixed IV is 4 bytes");
            }

            Key = key;
            FixedIv = fixedIv;
        }
    }

    /// <summary>
    /// The DTLS record layer: the 13-byte header, the AES-GCM protection of RFC 5288 as DTLS
    /// applies it, and the plaintext form the first flight travels in.
    ///
    /// The nonce, in DTLS, is not carried on the wire: RFC 6347, section 4.1.2.1 says the
    /// explicit part *is* the epoch and the sequence number, which the header already holds.
    /// That is why a sealed record is 16 bytes longer than its fragment and not 24, and why
    /// moving a record's sequence number breaks it - the number is authenticated twice, once
    /// in the nonce and once in the additional data.
    /// </summary>
    public static class DtlsRecord
    {
        /// <summary>A record the caller builds before any key exists (ClientHello, ServerHello).</summary>
        public static byte[] Plain(byte type, ushort epoch, ulong sequence, byte[] fragment)
        {
            if (fragment == null) fragment = new byte[0];
            if (fragment.Length > 65535) throw new ProtocolException("a DTLS fragment is shorter than 64 KB");

            var record = new byte[13 + fragment.Length];
            WriteHeader(record, type, epoch, sequence, fragment.Length);
            Buffer.BlockCopy(fragment, 0, record, 13, fragment.Length);
            return record;
        }

        /// <summary>Seals one fragment into a record: header in the clear, fragment authenticated.</summary>
        public static byte[] Seal(DtlsRecordKeys keys, byte type, ushort epoch, ulong sequence, byte[] fragment)
        {
            if (keys == null) throw new ProtocolException("a sealed record needs keys");
            if (fragment == null) fragment = new byte[0];

            byte[] nonce = Nonce(keys.FixedIv, epoch, sequence);
            byte[] additional = AdditionalData(type, epoch, sequence, fragment.Length);
            byte[] sealedBytes = AesGcm.Seal(keys.Key, nonce, fragment, additional);

            var record = new byte[13 + sealedBytes.Length];
            WriteHeader(record, type, epoch, sequence, sealedBytes.Length);
            Buffer.BlockCopy(sealedBytes, 0, record, 13, sealedBytes.Length);
            return record;
        }

        /// <summary>
        /// Reads one record: its header, and - when keys are given - its verified fragment.
        /// A tag that does not verify answers null: one datagram to drop, not a crash.
        /// It never throws on bytes it did not make.
        ///
        /// It answers one object rather than three `out` parameters because the C# 5 guard
        /// reads `out byte x` in a signature as C# 6's inline declaration, and a guard rule is
        /// not worth weakening for a signature (`Aes` and `JidUtils` made the same trade).
        /// </summary>
        public static DtlsRecordView Open(DtlsRecordKeys keys, byte[] record, int length)
        {
            if (record == null) return null;
            if (length <= 0) length = record.Length;
            if (length < 13 || length > record.Length) return null;

            var view = new DtlsRecordView();
            view.Type = record[0];
            view.Epoch = (ushort)((record[3] << 8) | record[4]);
            for (int i = 0; i < 6; i++) view.Sequence = (view.Sequence << 8) | record[5 + i];

            int declared = (record[11] << 8) | record[12];
            if (declared < 0 || 13 + declared > length) return null;

            var body = new byte[declared];
            Buffer.BlockCopy(record, 13, body, 0, declared);

            if (keys == null)
            {
                view.Fragment = body;
                return view;
            }

            if (declared < AesGcm.TagLength) return null;

            byte[] nonce = Nonce(keys.FixedIv, view.Epoch, view.Sequence);
            byte[] additional = AdditionalData(view.Type, view.Epoch, view.Sequence,
                declared - AesGcm.TagLength);

            try
            {
                view.Fragment = AesGcm.Open(keys.Key, nonce, body, additional);
            }
            catch (ProtocolException)
            {
                return null;
            }

            return view;
        }

        /// <summary>The 12-byte GCM nonce: the fixed IV, then the epoch, then the sequence.</summary>
        public static byte[] Nonce(byte[] fixedIv, ushort epoch, ulong sequence)
        {
            var nonce = new byte[12];
            Buffer.BlockCopy(fixedIv, 0, nonce, 0, 4);
            nonce[4] = (byte)(epoch >> 8);
            nonce[5] = (byte)epoch;
            for (int i = 0; i < 6; i++) nonce[6 + i] = (byte)(sequence >> (8 * (5 - i)));
            return nonce;
        }

        /// <summary>
        /// The additional data the tag covers: epoch, sequence, type, version and the length of
        /// the *plaintext*. The version is here and not only in the header for the same reason
        /// the sequence is: the header can be rewritten by anyone.
        /// </summary>
        public static byte[] AdditionalData(byte type, ushort epoch, ulong sequence, int plainLength)
        {
            var additional = new byte[13];
            additional[0] = (byte)(epoch >> 8);
            additional[1] = (byte)epoch;
            for (int i = 0; i < 6; i++) additional[2 + i] = (byte)(sequence >> (8 * (5 - i)));
            additional[8] = type;
            additional[9] = (byte)(Dtls.Version >> 8);
            additional[10] = (byte)(Dtls.Version & 0xFF);
            additional[11] = (byte)(plainLength >> 8);
            additional[12] = (byte)plainLength;
            return additional;
        }

        private static void WriteHeader(byte[] record, byte type, ushort epoch, ulong sequence, int bodyLength)
        {
            record[0] = type;
            record[1] = (byte)(Dtls.Version >> 8);
            record[2] = (byte)(Dtls.Version & 0xFF);
            record[3] = (byte)(epoch >> 8);
            record[4] = (byte)epoch;
            for (int i = 0; i < 6; i++) record[5 + i] = (byte)(sequence >> (8 * (5 - i)));
            record[11] = (byte)(bodyLength >> 8);
            record[12] = (byte)bodyLength;
        }
    }

    /// <summary>A handshake message, with the twelve-byte header DTLS gives it.</summary>
    public sealed class DtlsHandshakeMessage
    {
        public byte Type;
        public ushort Sequence;
        public byte[] Body;

        public DtlsHandshakeMessage(byte type, ushort sequence, byte[] body)
        {
            Type = type;
            Sequence = sequence;
            Body = body == null ? new byte[0] : body;
        }

        /// <summary>The framed bytes: type, length, sequence, fragment offset and length.</summary>
        public byte[] ToBytes()
        {
            var message = new byte[Dtls.HandshakeHeaderLength + Body.Length];
            message[0] = Type;
            message[1] = (byte)(Body.Length >> 16);
            message[2] = (byte)(Body.Length >> 8);
            message[3] = (byte)Body.Length;
            message[4] = (byte)(Sequence >> 8);
            message[5] = (byte)Sequence;
            message[6] = 0;
            message[7] = 0;
            message[8] = 0;
            message[9] = (byte)(Body.Length >> 16);
            message[10] = (byte)(Body.Length >> 8);
            message[11] = (byte)Body.Length;

            Buffer.BlockCopy(Body, 0, message, Dtls.HandshakeHeaderLength, Body.Length);
            return message;
        }

        /// <summary>
        /// Parses one message. A fragment - an offset that is not zero, or a fragment shorter
        /// than its declared length - is refused by name rather than half-read: this client does
        /// not reassemble, and a message it cannot see whole is a message it cannot hash.
        /// </summary>
        public static DtlsHandshakeMessage Parse(byte[] data, int offset, int length)
        {
            if (data == null) throw new ProtocolException("a handshake message needs bytes");
            if (length < Dtls.HandshakeHeaderLength || offset + length > data.Length)
            {
                throw new ProtocolException("a handshake message is at least twelve bytes");
            }

            int bodyLength = (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            int sequence = (data[offset + 4] << 8) | data[offset + 5];
            int fragmentOffset = (data[offset + 6] << 16) | (data[offset + 7] << 8) | data[offset + 8];
            int fragmentLength = (data[offset + 9] << 16) | (data[offset + 10] << 8) | data[offset + 11];

            if (fragmentOffset != 0 || fragmentLength != bodyLength)
            {
                throw new ProtocolException("a fragmented handshake message is not reassembled here (offset "
                    + fragmentOffset + " of " + bodyLength + ")");
            }

            if (Dtls.HandshakeHeaderLength + bodyLength > length)
            {
                throw new ProtocolException("a handshake message is shorter than it says");
            }

            var body = new byte[bodyLength];
            Buffer.BlockCopy(data, offset + Dtls.HandshakeHeaderLength, body, 0, bodyLength);

            return new DtlsHandshakeMessage(data[offset], (ushort)sequence, body);
        }
    }

    /// <summary>A builder for the little-endian-free world of TLS: lengths are big-endian.</summary>
    public sealed class DtlsWriter
    {
        private readonly List<byte> _bytes = new List<byte>();

        public int Length
        {
            get { return _bytes.Count; }
        }

        public void Byte(int value)
        {
            _bytes.Add((byte)value);
        }

        public void UInt16(int value)
        {
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
        }

        public void UInt24(int value)
        {
            _bytes.Add((byte)(value >> 16));
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
        }

        public void Bytes(byte[] value)
        {
            if (value != null) _bytes.AddRange(value);
        }

        /// <summary>Bytes behind a one-byte length, the shape every TLS vector has.</summary>
        public void Vector8(byte[] value)
        {
            byte[] bytes = value == null ? new byte[0] : value;
            Byte(bytes.Length);
            Bytes(bytes);
        }

        /// <summary>Bytes behind a two-byte length.</summary>
        public void Vector16(byte[] value)
        {
            byte[] bytes = value == null ? new byte[0] : value;
            UInt16(bytes.Length);
            Bytes(bytes);
        }

        /// <summary>Bytes behind a three-byte length, which is how a TLS list of lists is built.</summary>
        public void Vector24(byte[] value)
        {
            byte[] bytes = value == null ? new byte[0] : value;
            UInt24(bytes.Length);
            Bytes(bytes);
        }

        /// <summary>An extension: its type, then its own length, then its body.</summary>
        public void Extension(int type, byte[] body)
        {
            UInt16(type);
            Vector16(body);
        }

        public byte[] ToArray()
        {
            return _bytes.ToArray();
        }
    }

    /// <summary>A reader that refuses to read past its end, by name.</summary>
    public sealed class DtlsReader
    {
        private readonly byte[] _data;
        private int _at;

        public DtlsReader(byte[] data)
        {
            if (data == null) throw new ProtocolException("a reader needs bytes");
            _data = data;
        }

        public int Remaining
        {
            get { return _data.Length - _at; }
        }

        public int Position
        {
            get { return _at; }
        }

        public byte Byte()
        {
            Need(1);
            return _data[_at++];
        }

        public int UInt16()
        {
            Need(2);
            int value = (_data[_at] << 8) | _data[_at + 1];
            _at += 2;
            return value;
        }

        public int UInt24()
        {
            Need(3);
            int value = (_data[_at] << 16) | (_data[_at + 1] << 8) | _data[_at + 2];
            _at += 3;
            return value;
        }

        public byte[] Bytes(int count)
        {
            Need(count);
            var value = new byte[count];
            Buffer.BlockCopy(_data, _at, value, 0, count);
            _at += count;
            return value;
        }

        public byte[] Vector8()
        {
            return Bytes(Byte());
        }

        public byte[] Vector16()
        {
            return Bytes(UInt16());
        }

        public byte[] Vector24()
        {
            return Bytes(UInt24());
        }

        private void Need(int count)
        {
            if (count < 0 || _at + count > _data.Length)
            {
                throw new ProtocolException("a handshake message ended before its own field at byte " + _at);
            }
        }
    }
}
