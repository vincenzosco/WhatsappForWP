using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// SRTP: RTP packets encrypted and authenticated (RFC 3711), AES-CM-128 with an
    /// HMAC-SHA1 tag truncated to 80 bits.
    ///
    /// Why it is here: a WhatsApp call carries its audio as SRTP, and Windows Phone 8.1 has
    /// no SRTP, no DTLS and no RTP. This is the half that can be written and checked without
    /// a peer: given a master key and salt - which the call's signalling is supposed to
    /// deliver in `enc_key`, and which this client does not negotiate yet - it turns an RTP
    /// packet into what goes on the wire and back.
    ///
    /// What it deliberately is **not**:
    /// - **not DTLS.** The master key has to come from somewhere, and in WhatsApp's calls it
    ///   comes from a DTLS handshake. That is the missing piece between this file and a
    ///   working call; nothing here pretends to have it.
    /// - **not a full SRTP stack.** No SRTCP, no rekeying, no replay list beyond the rollover
    ///   counter, and no MKI. A call needs the rollover counter to keep the keystream
    ///   aligned across a sequence wrap, which is why that one is here.
    ///
    /// The key derivation is the specification's (section 4.3.3): the master salt with the
    /// label XORed into bits 48..55, encrypted with the master key, and the keystream taken
    /// from there - a different label gives a different key, which is what keeps the
    /// encryption key, the authentication key and the salt apart.
    /// </summary>
    public sealed class SrtpSession
    {
        /// <summary>The authentication tag SRTP appends: 80 bits (RFC 3711, section 4.2).</summary>
        public const int TagLength = 10;

        private const int BlockLength = 16;

        private readonly byte[] _encryptionKey;
        private readonly byte[] _authKey;
        private readonly byte[] _salt;
        private readonly uint _ssrc;

        private uint _rollover;
        private int _highestSequence;

        /// <summary>
        /// A session for one stream. `masterKey` is 16 bytes (AES-128-CM) or 32 (AES-256-CM)
        /// and `masterSalt` is 14, which is what an SRTP master key pair is; `ssrc` is the
        /// stream the packets belong to and the value the keystream is tied to.
        /// </summary>
        public SrtpSession(byte[] masterKey, byte[] masterSalt, uint ssrc)
        {
            if (masterKey == null || (masterKey.Length != 16 && masterKey.Length != 32))
                throw new ProtocolException("SRTP needs a 16- or 32-byte master key");
            if (masterSalt == null || masterSalt.Length != 14)
                throw new ProtocolException("SRTP needs a 14-byte master salt");

            // The cipher key is as long as the master key: AES-128-CM for 16 bytes and
            // AES-256-CM (RFC 6188) for 32, which is the length a WhatsApp call's expanded key
            // material provides.
            _encryptionKey = DeriveKey(masterKey, masterSalt, 0x00, masterKey.Length);
            _authKey = DeriveKey(masterKey, masterSalt, 0x01, 20);
            _salt = DeriveKey(masterKey, masterSalt, 0x02, 14);
            _ssrc = ssrc;

            _highestSequence = -1;
        }

        /// <summary>
        /// One RTP packet in, the same packet encrypted with its tag out.
        ///
        /// The header is authenticated and not encrypted, which is what lets the far end read
        /// the sequence number it needs to build the same keystream.
        /// </summary>
        public byte[] Seal(byte[] packet, int length)
        {
            Check(packet, length);

            int header = HeaderLength(packet, length);
            uint index = IndexOf(packet[2], packet[3]);

            var protectedBytes = new byte[length + TagLength];
            Buffer.BlockCopy(packet, 0, protectedBytes, 0, length);

            byte[] keystream = Keystream(index, length - header);
            for (int i = header; i < length; i++)
            {
                protectedBytes[i] = (byte)(packet[i] ^ keystream[i - header]);
            }

            byte[] tag = Hmac.ComputeSha1(_authKey, Slice(protectedBytes, 0, length));
            Buffer.BlockCopy(tag, 0, protectedBytes, length, TagLength);

            return protectedBytes;
        }

        /// <summary>
        /// One protected packet in, the RTP packet out, or null when the tag does not verify
        /// or the bytes are not a packet this session can read.
        ///
        /// A tag that fails is not an exception: it is one packet to drop, and a call that
        /// threw on it would end because a router rewrote a byte.
        /// </summary>
        public byte[] Open(byte[] packet, int length)
        {
            if (packet == null) return null;
            if (length <= 0) length = packet.Length;
            if (length < TagLength + 12 || length > packet.Length) return null;

            int plainLength = length - TagLength;
            if (plainLength < 12) return null;

            byte[] tag = Hmac.ComputeSha1(_authKey, Slice(packet, 0, plainLength));
            var expected = new byte[TagLength];
            Buffer.BlockCopy(tag, 0, expected, 0, TagLength);

            var carried = new byte[TagLength];
            Buffer.BlockCopy(packet, plainLength, carried, 0, TagLength);

            if (!Hmac.FixedTimeEquals(expected, carried)) return null;

            int header;
            uint index;

            try
            {
                header = HeaderLength(packet, plainLength);
                index = IndexOf(packet[2], packet[3]);
            }
            catch (ProtocolException)
            {
                return null;
            }

            var plain = new byte[plainLength];
            Buffer.BlockCopy(packet, 0, plain, 0, plainLength);

            byte[] keystream = Keystream(index, plainLength - header);
            for (int i = header; i < plainLength; i++)
            {
                plain[i] = (byte)(packet[i] ^ keystream[i - header]);
            }

            return plain;
        }

        /// <summary>
        /// The keystream for one packet: the session salt, the SSRC and the packet index
        /// combined into the counter block the specification gives (section 4.1.1), then
        /// encrypted block by block.
        /// </summary>
        private byte[] Keystream(uint index, int length)
        {
            if (length == 0) return new byte[0];

            var counter = new byte[BlockLength];

            // k_s * 2^16: the salt in the top 112 bits.
            Buffer.BlockCopy(_salt, 0, counter, 0, 14);

            // XOR SSRC * 2^64: bits 64..95, which is bytes 4 to 7.
            counter[4] ^= (byte)(_ssrc >> 24);
            counter[5] ^= (byte)(_ssrc >> 16);
            counter[6] ^= (byte)(_ssrc >> 8);
            counter[7] ^= (byte)_ssrc;

            // XOR the 48-bit packet index * 2^16: bits 16..63, bytes 8 to 13.
            counter[8] ^= (byte)(index >> 40);
            counter[9] ^= (byte)(index >> 32);
            counter[10] ^= (byte)(index >> 24);
            counter[11] ^= (byte)(index >> 16);
            counter[12] ^= (byte)(index >> 8);
            counter[13] ^= (byte)index;

            var stream = new byte[length];
            var block = new byte[BlockLength];

            for (int done = 0; done < length; done += BlockLength)
            {
                Buffer.BlockCopy(counter, 0, block, 0, BlockLength);

                byte[] encrypted = Aes.EncryptBlock(_encryptionKey, block);
                int take = length - done < BlockLength ? length - done : BlockLength;
                Buffer.BlockCopy(encrypted, 0, stream, done, take);

                Increment(counter);
            }

            return stream;
        }

        /// <summary>
        /// The 48-bit packet index: the rollover counter above the sequence number. A
        /// sequence that jumps back by more than half the space is a wrap, and a wrap that is
        /// not counted puts the two ends on different keystreams.
        /// </summary>
        private uint IndexOf(byte high, byte low)
        {
            int sequence = (high << 8) | low;

            if (_highestSequence < 0)
            {
                _highestSequence = sequence;
            }
            else if (sequence < _highestSequence && _highestSequence - sequence > 32768)
            {
                _rollover++;
                _highestSequence = sequence;
            }
            else if (sequence > _highestSequence)
            {
                _highestSequence = sequence;
            }

            return (_rollover << 16) | (uint)sequence;
        }

        /// <summary>How many bytes of the packet are header: fixed part, CSRC list, extension.</summary>
        private static int HeaderLength(byte[] packet, int length)
        {
            if (length < 12) throw new ProtocolException("an RTP packet has a twelve-byte header");
            if (((packet[0] >> 6) & 0x03) != 2) throw new ProtocolException("not an RTP version 2 packet");

            int at = 12 + (packet[0] & 0x0f) * 4;

            if ((packet[0] & 0x10) != 0)
            {
                if (at + 4 > length) throw new ProtocolException("an RTP extension is cut short");
                int words = (packet[at + 2] << 8) | packet[at + 3];
                at += 4 + words * 4;
            }

            if (at > length) throw new ProtocolException("an RTP header is longer than its packet");
            return at;
        }

        private static void Check(byte[] packet, int length)
        {
            if (packet == null) throw new ProtocolException("SRTP needs a packet");
            if (length <= 0) length = packet.Length;
            if (length > packet.Length) throw new ProtocolException("SRTP was given a length past its packet");
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(source, offset, slice, 0, count);
            return slice;
        }

        private static void Increment(byte[] counter)
        {
            for (int i = counter.Length - 1; i >= 0; i--)
            {
                counter[i]++;
                if (counter[i] != 0) return;
            }
        }

        /// <summary>
        /// One derived key or salt, checked first: the call's key material arrives as one
        /// master key and one salt, and the three values are what both ends have to agree on.
        /// Public because it is the seam the call setup needs, and because the property a test
        /// can pin - that the three labels never collide - is not reachable through a session.
        /// </summary>
        public static byte[] Derive(byte[] masterKey, byte[] masterSalt, byte label, int length)
        {
            if (masterKey == null || (masterKey.Length != 16 && masterKey.Length != 32))
                throw new ProtocolException("SRTP needs a 16- or 32-byte master key");
            if (masterSalt == null || masterSalt.Length != 14)
                throw new ProtocolException("SRTP needs a 14-byte master salt");
            if (length <= 0) throw new ProtocolException("a derived key has a length");

            return DeriveKey(masterKey, masterSalt, label, length);
        }

        /// <summary>
        /// One derived key or salt (RFC 3711, section 4.3.3): the master salt with the label
        /// XORed into bits 48..55, encrypted with the master key, taken as a keystream.
        /// </summary>
        private static byte[] DeriveKey(byte[] masterKey, byte[] masterSalt, byte label, int length)
        {
            var counter = new byte[BlockLength];
            Buffer.BlockCopy(masterSalt, 0, counter, 0, 14);

            // The label sits at bits 48..55 of the 112-bit salt: byte 7 of a 14-byte
            // big-endian number.
            counter[7] ^= label;

            var derived = new byte[length];
            var block = new byte[BlockLength];

            for (int done = 0; done < length; done += BlockLength)
            {
                Buffer.BlockCopy(counter, 0, block, 0, BlockLength);

                // The block counter is *outside* the encrypted block here: each iteration
                // encrypts the next counter value, and the label is what separates the keys.
                byte[] encrypted = Aes.EncryptBlock(masterKey, block);

                int take = length - done < BlockLength ? length - done : BlockLength;
                Buffer.BlockCopy(encrypted, 0, derived, done, take);

                Increment(counter);
            }

            return derived;
        }
    }
}
