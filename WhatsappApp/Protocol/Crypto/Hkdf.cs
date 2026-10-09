namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// HKDF (RFC 5869) over <see cref="Hmac"/>. The Noise handshake derives every key
    /// it uses with it, so it is on the critical path of the very first frame.
    /// </summary>
    public static class Hkdf
    {
        /// <summary>Extract: HMAC(salt, keyMaterial), the pseudorandom key.</summary>
        public static byte[] Extract(byte[] salt, byte[] keyMaterial)
        {
            // RFC 5869: an absent salt is a run of zero bytes as long as the hash.
            if (salt == null || salt.Length == 0) salt = new byte[Sha256.DigestLength];
            if (keyMaterial == null) keyMaterial = new byte[0];

            return Hmac.Compute(salt, keyMaterial);
        }

        /// <summary>Expand: as many HMAC blocks as the length needs, chained by T(n-1).</summary>
        public static byte[] Expand(byte[] pseudorandomKey, byte[] info, int length)
        {
            if (length < 0 || length > 255 * Sha256.DigestLength)
            {
                throw new ProtocolException("HKDF cannot expand to " + length + " bytes");
            }

            if (info == null) info = new byte[0];

            var output = new byte[length];
            var block = new byte[0];
            int written = 0;
            byte counter = 1;

            while (written < length)
            {
                var input = new byte[block.Length + info.Length + 1];
                System.Buffer.BlockCopy(block, 0, input, 0, block.Length);
                System.Buffer.BlockCopy(info, 0, input, block.Length, info.Length);
                input[input.Length - 1] = counter;

                block = Hmac.Compute(pseudorandomKey, input);

                int take = length - written < block.Length ? length - written : block.Length;
                System.Buffer.BlockCopy(block, 0, output, written, take);
                written += take;
                counter++;
            }

            return output;
        }

        /// <summary>Extract then expand in one call, which is how every caller uses it.</summary>
        public static byte[] Derive(byte[] keyMaterial, byte[] salt, byte[] info, int length)
        {
            return Expand(Extract(salt, keyMaterial), info, length);
        }
    }
}
