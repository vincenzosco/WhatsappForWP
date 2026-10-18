using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Noise
{
    /// <summary>What the server's first handshake message hands over.</summary>
    public sealed class ServerHandshake
    {
        /// <summary>The server's static public key, decrypted from the message.</summary>
        public byte[] StaticKey;

        /// <summary>Its certificate chain, decrypted from the message.</summary>
        public CertChain Certificates;

        /// <summary>Our static key as the server must receive it, encrypted.</summary>
        public byte[] EncryptedStaticKey;
    }

    /// <summary>
    /// WhatsApp's Noise handshake: `Noise_XX_25519_AESGCM_SHA256`, carried inside
    /// protobuf `HandshakeMessage`s.
    ///
    /// This is the transcription of the reference's noise handler, and the details are
    /// the ones a plausible-looking guess gets wrong:
    ///
    /// - the mode string is 32 bytes, so it **is** the initial hash; it is not hashed
    ///   again;
    /// - the additional data of every pre-split encryption is the **current** hash,
    ///   and the hash is then advanced with the ciphertext;
    /// - the one counter is shared by both directions and resets on every
    ///   `MixIntoKey`, and the IV is twelve bytes: eight zeros then that counter
    ///   big-endian in the last four;
    /// - `MixIntoKey` runs `HKDF(input, 64, salt, "")` - input first, and the previous
    ///   hash is the salt - then splits the 64 bytes into write and read.
    ///
    /// The certificate signatures are **not** verified here. That needs XEdDSA (an
    /// Ed25519 verify), which this milestone does not carry; a handshake that decrypts
    /// the server's keys but does not verify its certificate is not yet secure, and it
    /// is recorded as a ruling in the plan rather than quietly treated as done.
    /// </summary>
    public sealed class NoiseHandshake
    {
        /// <summary>`WA` 0x06 0x03, the four bytes in front of the client's first frame.</summary>
        public static readonly byte[] IntroHeader = new byte[] { 87, 65, 6, 3 };

        /// <summary>27 characters and four NULs: thirty-two bytes, hence the initial hash.</summary>
        public const string Mode = "Noise_XX_25519_AESGCM_SHA256\0\0\0\0";

        private readonly byte[] _ephemeralPrivate;
        private readonly byte[] _ephemeralPublic;

        private byte[] _hash;
        private byte[] _salt;
        private byte[] _encKey;
        private byte[] _decKey;
        private uint _counter;

        private bool _sentIntro;
        private bool _split;
        private byte[] _transportWriteKey;
        private byte[] _transportReadKey;
        private uint _writeCounter;
        private uint _readCounter;

        public NoiseHandshake(byte[] ephemeralPrivate, byte[] ephemeralPublic)
        {
            if (ephemeralPrivate == null || ephemeralPrivate.Length != 32)
            {
                throw new ProtocolException("the handshake needs a 32-byte ephemeral private key");
            }

            if (ephemeralPublic == null || ephemeralPublic.Length != 32)
            {
                throw new ProtocolException("the handshake needs a 32-byte ephemeral public key");
            }

            _ephemeralPrivate = ephemeralPrivate;
            _ephemeralPublic = ephemeralPublic;

            byte[] mode = ModeBytes();
            _hash = mode;
            _salt = Copy(mode);
            _encKey = Copy(mode);
            _decKey = Copy(mode);

            // The prologue, then our ephemeral: both are hashed before any key exists.
            Authenticate(IntroHeader);
            Authenticate(_ephemeralPublic);
        }

        /// <summary>The running handshake hash, which is also the pre-split AAD.</summary>
        public byte[] Hash
        {
            get { return Copy(_hash); }
        }

        /// <summary>`HandshakeMessage{clientHello{ephemeral}}`, ready to frame.</summary>
        public byte[] BuildClientHello()
        {
            return HandshakeMessage.EncodeClientHello(_ephemeralPublic);
        }

        /// <summary>
        /// The `<- e, ee, s, es` step: decrypts the server's static key and its
        /// certificate chain, then produces the encrypted static key for the reply.
        ///
        /// `staticPrivateKey` / `staticPublicKey` are this client's long-lived Noise
        /// key pair, not the ephemeral one: the handshake authenticates the client
        /// with a key that outlives the connection.
        /// </summary>
        public ServerHandshake ProcessServerHello(byte[] serverMessage, byte[] staticPrivateKey, byte[] staticPublicKey)
        {
            Hello serverHello = HandshakeMessage.DecodeServerHello(serverMessage);

            if (serverHello.Static == null) throw new ProtocolException("the serverHello carries no static key");
            if (serverHello.Payload == null) throw new ProtocolException("the serverHello carries no payload");

            Authenticate(serverHello.Ephemeral);
            MixIntoKey(Curve25519.SharedSecret(_ephemeralPrivate, serverHello.Ephemeral));

            byte[] serverStaticKey = Decrypt(serverHello.Static);
            MixIntoKey(Curve25519.SharedSecret(_ephemeralPrivate, serverStaticKey));

            byte[] certificateBytes = Decrypt(serverHello.Payload);

            byte[] encryptedStaticKey = Encrypt(staticPublicKey);
            MixIntoKey(Curve25519.SharedSecret(staticPrivateKey, serverHello.Ephemeral));

            return new ServerHandshake
            {
                StaticKey = serverStaticKey,
                Certificates = CertChain.Decode(certificateBytes),
                EncryptedStaticKey = encryptedStaticKey
            };
        }

        /// <summary>
        /// The split: one more HKDF over an empty message gives the two transport keys,
        /// and from here on the frames are encrypted with a per-direction counter and
        /// no additional data.
        /// </summary>
        public void FinishInit()
        {
            byte[] key = Hkdf.Derive(new byte[0], _salt, new byte[0], 64);
            _transportWriteKey = Slice(key, 0, 32);
            _transportReadKey = Slice(key, 32, 32);
            _writeCounter = 0;
            _readCounter = 0;
            _split = true;
        }

        public byte[] Encrypt(byte[] plaintext)
        {
            if (plaintext == null) plaintext = new byte[0];

            if (_split)
            {
                byte[] result = AesGcm.Seal(_transportWriteKey, CounterIv(_writeCounter), plaintext, new byte[0]);
                _writeCounter++;
                return result;
            }

            byte[] preSplit = AesGcm.Seal(_encKey, CounterIv(_counter), plaintext, _hash);
            _counter++;
            Authenticate(preSplit);
            return preSplit;
        }

        public byte[] Decrypt(byte[] ciphertext)
        {
            if (_split)
            {
                byte[] result = AesGcm.Open(_transportReadKey, CounterIv(_readCounter), ciphertext, new byte[0]);
                _readCounter++;
                return result;
            }

            byte[] preSplit = AesGcm.Open(_decKey, CounterIv(_counter), ciphertext, _hash);
            _counter++;
            Authenticate(ciphertext);
            return preSplit;
        }

        /// <summary>
        /// Frames one payload for the socket: the 4-byte intro on the first frame only,
        /// then a 3-byte big-endian length, then the payload - encrypted once the
        /// handshake has split.
        /// </summary>
        public byte[] EncodeFrame(byte[] payload)
        {
            byte[] body = payload;
            if (_split) body = Encrypt(payload);

            int introLength = _sentIntro ? 0 : IntroHeader.Length;
            var frame = new byte[introLength + 3 + body.Length];

            if (introLength > 0)
            {
                Buffer.BlockCopy(IntroHeader, 0, frame, 0, IntroHeader.Length);
                _sentIntro = true;
            }

            frame[introLength] = (byte)((body.Length >> 16) & 0xff);
            frame[introLength + 1] = (byte)((body.Length >> 8) & 0xff);
            frame[introLength + 2] = (byte)(body.Length & 0xff);
            Buffer.BlockCopy(body, 0, frame, introLength + 3, body.Length);

            return frame;
        }

        private void Authenticate(byte[] data)
        {
            var input = new byte[_hash.Length + data.Length];
            Buffer.BlockCopy(_hash, 0, input, 0, _hash.Length);
            Buffer.BlockCopy(data, 0, input, _hash.Length, data.Length);

            _hash = Sha256.Hash(input);
        }

        private void MixIntoKey(byte[] sharedSecret)
        {
            byte[] key = Hkdf.Derive(sharedSecret, _salt, new byte[0], 64);

            _salt = Slice(key, 0, 32);
            _encKey = Slice(key, 32, 32);
            _decKey = Slice(key, 32, 32);
            _counter = 0;
        }

        private static byte[] CounterIv(uint counter)
        {
            var iv = new byte[12];
            iv[8] = (byte)((counter >> 24) & 0xff);
            iv[9] = (byte)((counter >> 16) & 0xff);
            iv[10] = (byte)((counter >> 8) & 0xff);
            iv[11] = (byte)(counter & 0xff);
            return iv;
        }

        private static byte[] ModeBytes()
        {
            var mode = new byte[32];
            for (int i = 0; i < mode.Length; i++) mode[i] = (byte)Mode[i];
            return mode;
        }

        private static byte[] Copy(byte[] source)
        {
            return Slice(source, 0, source.Length);
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(source, offset, copy, 0, count);
            return copy;
        }
    }

    /// <summary>
    /// Reassembles the incoming frames: the server writes 3-byte big-endian lengths on
    /// a stream, and one WebSocket message can carry a piece of a frame, a whole frame
    /// or several.
    /// </summary>
    public sealed class FrameAccumulator
    {
        private byte[] _pending = new byte[0];

        /// <summary>Adds what arrived and returns every frame that is now complete.</summary>
        public List<byte[]> Append(byte[] data)
        {
            var buffer = new byte[_pending.Length + data.Length];
            Buffer.BlockCopy(_pending, 0, buffer, 0, _pending.Length);
            Buffer.BlockCopy(data, 0, buffer, _pending.Length, data.Length);

            var frames = new List<byte[]>();
            int index = 0;

            while (index + 3 <= buffer.Length)
            {
                int length = (buffer[index] << 16) | (buffer[index + 1] << 8) | buffer[index + 2];
                if (index + 3 + length > buffer.Length) break;

                var frame = new byte[length];
                Buffer.BlockCopy(buffer, index + 3, frame, 0, length);
                frames.Add(frame);
                index += 3 + length;
            }

            _pending = new byte[buffer.Length - index];
            Buffer.BlockCopy(buffer, index, _pending, 0, _pending.Length);
            return frames;
        }

        /// <summary>What is left over: bytes that belong to a frame still on its way.</summary>
        public int PendingLength
        {
            get { return _pending.Length; }
        }

        /// <summary>
        /// Those bytes as hex, at most <paramref name="max"/> of them.
        ///
        /// Why it exists: a connection that ends with a piece of a frame in hand is the
        /// shape the phone saw on 2026-10-10 and again on 2026-10-17 - four bytes, then
        /// `0x80072EFE` - and the four bytes are the only evidence of where the server
        /// stopped talking. A reader that logs the count and not the bytes cannot tell a
        /// truncated length prefix from the beginning of a node.
        /// </summary>
        public string PendingHex(int max)
        {
            int count = _pending.Length;
            if (max > 0 && count > max) count = max;

            var text = new System.Text.StringBuilder(count * 2);
            for (int i = 0; i < count; i++) text.Append(_pending[i].ToString("X2"));

            if (count < _pending.Length) text.Append("...");

            return text.ToString();
        }
    }
}
