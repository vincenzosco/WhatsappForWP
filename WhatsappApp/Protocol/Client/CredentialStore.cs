using System;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// The phone's settings map, seen from the core: a key, a string, nothing else.
    ///
    /// `ApplicationData.Current.LocalSettings` on WP8.1 is exactly this and nothing
    /// more, so the interface is the smallest thing that can stand in for it - and the
    /// test host can implement it with a dictionary instead of a phone.
    /// </summary>
    public interface ICredentialStorage
    {
        /// <summary>The value stored under a key, or null/empty when there is none.</summary>
        string Read(string key);

        /// <summary>Stores a value. An empty value means the key is not there.</summary>
        void Write(string key, string value);
    }

    /// <summary>
    /// Where the device's account lives between runs.
    ///
    /// One key per field, because the storage is a flat string map and a single blob
    /// would have to be parsed to find out whether it is whole - the failure this
    /// guards against is a session written halfway (the app killed mid-pairing, two
    /// starts racing) being read back as a session. So <see cref="HasSession"/> is true
    /// only when **every** key the account needs is present *and* parses: a half-written
    /// set is simply not a session, and the app shows the QR again - which is the right
    /// answer, because the half that survived is of no use to anyone.
    ///
    /// The two key lengths are checked rather than assumed: a key of the wrong size is
    /// a corrupted store, and a corrupted store must read as "no session" rather than
    /// fail deep inside a handshake.
    ///
    /// Binary values are base64 and text values (the JID, the LID, the platform word,
    /// the push name) are stored as text, so a person looking at the phone's settings
    /// can tell which account is linked without decoding anything.
    /// </summary>
    public sealed class CredentialStore
    {
        /// <summary>Every key is prefixed, so a leftover setting from an older build cannot be read as one of these.</summary>
        public const string KeyPrefix = "Session.";

        public const string NoisePrivateKey = KeyPrefix + "NoisePrivate";
        public const string NoisePublicKey = KeyPrefix + "NoisePublic";
        public const string IdentityPrivateKey = KeyPrefix + "IdentityPrivate";
        public const string IdentityPublicKey = KeyPrefix + "IdentityPublic";
        public const string AdvSecretKey = KeyPrefix + "AdvSecret";
        public const string RegistrationIdKey = KeyPrefix + "RegistrationId";
        public const string SignedPreKeyIdKey = KeyPrefix + "SignedPreKeyId";
        public const string SignedPreKeyPublicKey = KeyPrefix + "SignedPreKeyPublic";
        public const string SignedPreKeyPrivateKey = KeyPrefix + "SignedPreKeyPrivate";
        public const string SignedPreKeySignatureKey = KeyPrefix + "SignedPreKeySignature";
        public const string JidKey = KeyPrefix + "Jid";
        public const string LidKey = KeyPrefix + "Lid";
        public const string PlatformKey = KeyPrefix + "Platform";
        public const string PushNameKey = KeyPrefix + "PushName";

        /// <summary>What has to be there for a session to exist. `PushName` is deliberately not in it.</summary>
        private static readonly string[] RequiredKeys = new string[]
        {
            NoisePrivateKey, NoisePublicKey, IdentityPrivateKey, IdentityPublicKey, AdvSecretKey,
            RegistrationIdKey, SignedPreKeyIdKey, SignedPreKeyPublicKey, SignedPreKeyPrivateKey,
            SignedPreKeySignatureKey, JidKey, LidKey, PlatformKey
        };

        /// <summary>
        /// Every key this store writes, and therefore everything <see cref="Clear"/> has
        /// to empty. Public because a test proves it leaves nothing behind, and because
        /// the footprint of the account is worth being able to read off the class.
        /// </summary>
        public static readonly string[] Keys = new string[]
        {
            NoisePrivateKey, NoisePublicKey, IdentityPrivateKey, IdentityPublicKey, AdvSecretKey,
            RegistrationIdKey, SignedPreKeyIdKey, SignedPreKeyPublicKey, SignedPreKeyPrivateKey,
            SignedPreKeySignatureKey, JidKey, LidKey, PlatformKey, PushNameKey
        };

        private readonly ICredentialStorage _storage;

        public CredentialStore(ICredentialStorage storage)
        {
            if (storage == null) throw new ProtocolException("a credential store needs somewhere to write");
            _storage = storage;
        }

        /// <summary>
        /// True only when a whole, parseable account is stored. Every other answer -
        /// missing key, half-written value, a key of the wrong length, a number that is
        /// not a number - is false, and the app then shows the QR.
        /// </summary>
        public bool HasSession
        {
            get
            {
                for (int i = 0; i < RequiredKeys.Length; i++)
                {
                    string value = _storage.Read(RequiredKeys[i]);
                    if (string.IsNullOrEmpty(value)) return false;
                }

                try
                {
                    Load();
                    return true;
                }
                catch (ProtocolException)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The stored account.
        ///
        /// Throws rather than returning null when there is none or it is damaged: a
        /// caller that reached here without checking <see cref="HasSession"/> has a bug,
        /// and a handshake with empty keys fails much further away from the cause.
        /// </summary>
        public Credentials Load()
        {
            uint registrationId = ReadNumber(RegistrationIdKey);
            uint signedPreKeyId = ReadNumber(SignedPreKeyIdKey);

            var credentials = new Credentials();
            credentials.NoisePrivateKey = ReadKey(NoisePrivateKey, 32);
            credentials.NoisePublicKey = ReadKey(NoisePublicKey, 32);
            credentials.IdentityPrivateKey = ReadKey(IdentityPrivateKey, 32);
            credentials.IdentityPublicKey = ReadKey(IdentityPublicKey, 32);
            credentials.AdvSecret = ReadKey(AdvSecretKey, 32);
            credentials.RegistrationId = registrationId;
            credentials.SignedPreKeyId = signedPreKeyId;
            credentials.SignedPreKeyPublic = ReadKey(SignedPreKeyPublicKey, 32);
            credentials.SignedPreKeyPrivate = ReadKey(SignedPreKeyPrivateKey, 32);
            credentials.SignedPreKeySignature = ReadKey(SignedPreKeySignatureKey, 64);
            credentials.Jid = ReadText(JidKey);
            credentials.Lid = ReadText(LidKey);
            credentials.Platform = ReadText(PlatformKey);
            credentials.PushName = ReadText(PushNameKey);

            return credentials;
        }

        /// <summary>
        /// Writes the whole account. Every key is written on every save, including the
        /// ones whose value is empty, so a save can never leave an older value behind a
        /// newer one.
        /// </summary>
        public void Save(Credentials credentials)
        {
            if (credentials == null) throw new ProtocolException("there is nothing to save");

            if (credentials.NoisePrivateKey == null || credentials.IdentityPrivateKey == null
                || credentials.AdvSecret == null)
            {
                throw new ProtocolException("a session without keys is not worth storing");
            }

            _storage.Write(NoisePrivateKey, Convert.ToBase64String(credentials.NoisePrivateKey));
            _storage.Write(NoisePublicKey, Convert.ToBase64String(credentials.NoisePublicKey));
            _storage.Write(IdentityPrivateKey, Convert.ToBase64String(credentials.IdentityPrivateKey));
            _storage.Write(IdentityPublicKey, Convert.ToBase64String(credentials.IdentityPublicKey));
            _storage.Write(AdvSecretKey, Convert.ToBase64String(credentials.AdvSecret));
            _storage.Write(RegistrationIdKey, credentials.RegistrationId.ToString());
            _storage.Write(SignedPreKeyIdKey, credentials.SignedPreKeyId.ToString());
            _storage.Write(SignedPreKeyPublicKey, Convert.ToBase64String(credentials.SignedPreKeyPublic));
            _storage.Write(SignedPreKeyPrivateKey, Convert.ToBase64String(credentials.SignedPreKeyPrivate));
            _storage.Write(SignedPreKeySignatureKey, Convert.ToBase64String(credentials.SignedPreKeySignature));
            _storage.Write(JidKey, Value(credentials.Jid));
            _storage.Write(LidKey, Value(credentials.Lid));
            _storage.Write(PlatformKey, Value(credentials.Platform));
            _storage.Write(PushNameKey, Value(credentials.PushName));
        }

        /// <summary>
        /// Forgets the account. Opening the app afterwards lands on the QR, which is the
        /// only way back to a working session once the server has unlinked this device.
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < Keys.Length; i++) _storage.Write(Keys[i], string.Empty);
        }

        private byte[] ReadKey(string key, int expectedLength)
        {
            string value = ReadText(key);

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                throw new ProtocolException("the stored " + key + " is not base64");
            }

            if (bytes.Length != expectedLength)
            {
                throw new ProtocolException("the stored " + key + " is " + bytes.Length
                    + " bytes and should be " + expectedLength);
            }

            return bytes;
        }

        private uint ReadNumber(string key)
        {
            string value = ReadText(key);

            uint parsed;
            if (!uint.TryParse(value, out parsed))
            {
                throw new ProtocolException("the stored " + key + " is not a number");
            }

            return parsed;
        }

        private string ReadText(string key)
        {
            string value = _storage.Read(key);
            if (string.IsNullOrEmpty(value)) throw new ProtocolException("no " + key + " is stored");

            return value;
        }

        private static string Value(string text)
        {
            return text == null ? string.Empty : text;
        }
    }
}
