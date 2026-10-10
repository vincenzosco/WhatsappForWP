using System;
using Windows.Security.Cryptography;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The phone's randomness, from the platform's own generator.
    ///
    /// The core cannot draw it itself: `System.Security.Cryptography` is not in the WP8.1
    /// projection (the C# 5 guard refuses the `using`), and `CryptographicBuffer` is a
    /// WinRT type the desktop host cannot compile. The identity keys and the ADV secret
    /// are what a pairing spends, so this is the one place in the app where "random" has
    /// to mean random: a seeded `Random` would make two installs pair with the same
    /// identity, which is a device anyone holding the other one could impersonate.
    /// </summary>
    public sealed class DeviceRandomSource : IRandomSource
    {
        public byte[] Bytes(int count)
        {
            if (count <= 0) throw new ProtocolException("a key of " + count + " bytes is not a key");

            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(
                CryptographicBuffer.GenerateRandom((uint)count), out bytes);

            // The platform's generator does not return this; a source that did would make
            // every device's keys identical, and the failure would show up as a pairing
            // that hangs rather than as an error. Refusing the draw is the cheap check.
            if (IsAllZero(bytes)) throw new ProtocolException("the platform's generator returned no entropy");

            return bytes;
        }

        private static bool IsAllZero(byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0) return false;
            }

            return true;
        }
    }
}
