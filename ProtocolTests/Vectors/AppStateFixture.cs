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

        /// <summary>The encoded `SyncdSnapshot`, base64.</summary>
        public const string Snapshot = "CgIIBxLGAQoiCiAMGmyaCMfy6sLJy6iygBcMHusVC9pXjfKL8Yd+A9koSxKTAQqQAQABAgMEBQYHCAkKCwwNDg/m2QI/klJYV7J7WilrMk840A1w1bsW3u3UrpVBOup+XTi/yj8N3SLxY2+d5yeoxCW6ejsm7Jkjqy+wUvj3zWziWBUpY7fxE3u10k7yqmQEkTBwELMYUe+DcGAMkShmPpSTJq0ITg3xYziJMIkGfiupL7KsScYcX+a5bSIkEGZdmRoKCggKGyw9Tl9gcRLWAQoiCiDZKN80klpVkWSeHugCqhjHeWbLIW5cU63eGR+QPeSm+xKjAQqgARAREhMUFRYXGBkaGxwdHh8U6RxjeZR10GEutIPSQTBEy7Tb0KzsLoEp9qd7t2kI9JjycKqwxfBmILzvRJafSAx82YThgEXYEjE0i7ROcjR78YAON9wDya5Gi2eAYEVRYZX3kREDv4ep2n5Dn+m03oXDRVHxVDJ7qjehvUOJN++iEUCbjBOozh3HGc/an1jUvT88xmd6YXFIgdMe3sFxJ20aCgoIChssPU5fYHEaIGj0//KxyDIa4c1o7DRKzVlaIIFWeLtnnwbGTDebcJ+5IgoKCAobLD1OX2Bx";

        /// <summary>The encoded `SyncdPatch`, base64.</summary>
        public const string Patch = "CgIICBLLAQgAEsYBCiIKIOJ/nLaUf2wSO3dr3xf9ZZ52haSuBm2JYFNIwWvI6bIPEpMBCpABICEiIyQlJicoKSorLC0uL4fsa4KUY0U+dKQPJL7G+f7UUa5uH2fhvYKPW7s5VvWN3K0RBjMXMiKA0NgUxZRhcmIU0NT16IIaUALcEQwksOxhPLn90Sd8R2XBf3UyX1IX/L6FUN0wUP5i1RHRwBPyWHpGsEAdGg6yRAtt1IGP6XIw3bkGFnK1v8WK5aTcfX8wGgoKCAobLD1OX2BxIiAsax7bCBOwrgxSVcnSnW7WW0Fg5XEstndN1143i9f1OSog8OaG3vVfG1tNzD1N+tzhGPOjiNJNifIBXmEzW1bAzHMyCgoIChssPU5fYHE=";

        public const int SnapshotVersion = 7;
        public const int PatchVersion = 8;
    }
}
