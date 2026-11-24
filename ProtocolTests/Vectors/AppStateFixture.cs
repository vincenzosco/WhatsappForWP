namespace ProtocolTests.Vectors
{
    /// <summary>
    /// One app-state snapshot and one patch, built by tools/emit-appstate-fixture.js with
    /// Node's own crypto from the reference's rules - not by the code under test.
    ///
    /// What it holds: a `regular` collection at version 7 with two saved
    /// contacts (Mamma, and Papà who is known by a LID and a phone number), and the patch to
    /// version 8 that saves a third (Nonna). The MACs are real: a reader that
    /// gets the LT-Hash, the key derivation or the field numbers wrong fails on them.
    /// </summary>
    internal static class AppStateFixture
    {
        /// <summary>The key id the records name, base64 as the phone shares it.</summary>
        public const string KeyId = "ChssPU5fYHE=";

        /// <summary>The 32 bytes every mutation key is derived from, base64.</summary>
        public const string KeyData = "ABEiM0RVZneImaq7zN3u/wARIjNEVWZ3iJmqu8zd7v8=";

        /// <summary>An encoded external snapshot reference, base64.</summary>
        public const string ExternalReference = "CiABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fIBIML3YvdDYyL3N0YXRlGgxzdGF0ZS1oYW5kbGUg1AMqIP5CGEHT8l+Tz5eR/HZeu8vzNhrkN+2EVtUHhYzF/NjMMiDgbNJHA6J1Mbl5l4C7gcJMdmY5uzPcuYNNd2czvB8NBw==";

        /// <summary>The encrypted external blob body for `Snapshot`, base64.</summary>
        public const string ExternalBlob = "lVMQhP3PigFPQ35pDvkyFMku2i/4+iYvLe5IAqM6mujwjxwF2VKi1zCKof9TWoBTK9PY0raiAgNmCXXxWbOwQoJYVoLIbr0ImWEGDszs7tPb4qEjsHQNjikRZEt92bzun+Ly5EwE5wid00cu4eUI9vCImhha4+Gd2J51X9OINhxCyiUXwAqV55FGSLyP25JyCmci+pfPKsPhjcWEgNtVi/uaXMCJb9hdR5IVprwLtG7HRnjLP2AleBd8m1myxVGOzABds4i659Dg3EsCSewatrZCDj0XZxXUBdL3YAJTFH5BDqLl3aZsTmabJYSOa7NTRtCq+g8vEOjOQUgfP7uUkyhLmi/3HQvUC5+J9iwIVOFkWTe2xkQyRoIYsDblFI6fBjs/2NfQmgZ0RQY0l2Z7FMU7GkNopueEdDVWGB/czqvvfzPcHCj/mAEC4fzcIl/oH0WwY+KVlUEz4xtfNmupUkxsJjLmUJ9UGmWqtBo1MZKjE4R9w+sh6hwgVKgVibPTj22brdXbS1fFAgFc9+u2pQ9VzUsRFgELJJHUp3Mj+Zc+TnQdFEK2noPWa2Da+0y4z+RObqdxI70ChLczJZ8P+KCTVD1YyvnHUhRur/NZi3TggLXWZHFJgqIrUahNBg7/hPIMMQZSBbHLtA==";

        /// <summary>The encoded `SyncdSnapshot`, base64.</summary>
        public const string Snapshot = "CgIIBxLGAQoiCiAMGmyaCMfy6sLJy6iygBcMHusVC9pXjfKL8Yd+A9koSxKTAQqQAQABAgMEBQYHCAkKCwwNDg/m2QI/klJYV7J7WilrMk840A1w1bsW3u3UrpVBOup+XTi/yj8N3SLxY2+d5yeoxCW6ejsm7Jkjqy+wUvj3zWziWBUpY7fxE3u10k7yqmQEkTBwELMYUe+DcGAMkShmPpSTJq0ITg3xYziJMIkGfiupL7KsScYcX+a5bSIkEGZdmRoKCggKGyw9Tl9gcRLWAQoiCiDZKN80klpVkWSeHugCqhjHeWbLIW5cU63eGR+QPeSm+xKjAQqgARAREhMUFRYXGBkaGxwdHh8U6RxjeZR10GEutIPSQTBEy7Tb0KzsLoEp9qd7t2kI9JjycKqwxfBmILzvRJafSAx82YThgEXYEjE0i7ROcjR78YAON9wDya5Gi2eAYEVRYZX3kREDv4ep2n5Dn+m03oXDRVHxVDJ7qjehvUOJN++iEUCbjBOozh3HGc/an1jUvT88xmd6YXFIgdMe3sFxJ20aCgoIChssPU5fYHEaIGj0//KxyDIa4c1o7DRKzVlaIIFWeLtnnwbGTDebcJ+5IgoKCAobLD1OX2Bx";

        /// <summary>The encoded `SyncdPatch`, base64.</summary>
        public const string Patch = "CgIICBLLAQgAEsYBCiIKIOJ/nLaUf2wSO3dr3xf9ZZ52haSuBm2JYFNIwWvI6bIPEpMBCpABICEiIyQlJicoKSorLC0uL4fsa4KUY0U+dKQPJL7G+f7UUa5uH2fhvYKPW7s5VvWN3K0RBjMXMiKA0NgUxZRhcmIU0NT16IIaUALcEQwksOxhPLn90Sd8R2XBf3UyX1IX/L6FUN0wUP5i1RHRwBPyWHpGsEAdGg6yRAtt1IGP6XIw3bkGFnK1v8WK5aTcfX8wGgoKCAobLD1OX2BxIiAsax7bCBOwrgxSVcnSnW7WW0Fg5XEstndN1143i9f1OSog8OaG3vVfG1tNzD1N+tzhGPOjiNJNifIBXmEzW1bAzHMyCgoIChssPU5fYHE=";

        public const int SnapshotVersion = 7;
        public const int PatchVersion = 8;
    }
}
