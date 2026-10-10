using System;
using System.Text;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// The string a phone scans to link this device.
    ///
    /// It is four secrets in a row, and the reference builds it exactly this way:
    /// `https://wa.me/settings/linked_devices#&lt;ref&gt;,&lt;noise&gt;,&lt;identity&gt;,&lt;adv&gt;,&lt;client
    /// type&gt;`, where the ref is the bytes the server sent *as they are* (they are
    /// already text) and the three keys are base64. The last field is the client type
    /// word: `8` is UWP, which is what this client announces in its `DeviceProps`.
    ///
    /// A wrong character here is not an error message, it is a scan that hangs: the
    /// scanning phone has no way to say what it did not understand, so the tests pin the
    /// whole string rather than its parts.
    /// </summary>
    public static class PairingQr
    {
        /// <summary>Where the scanning phone sends what it read.</summary>
        public const string UriPrefix = "https://wa.me/settings/linked_devices#";

        /// <summary>`PairClientUWP` in the reference: the fifth field for a Windows client.</summary>
        public const string ClientTypeUwp = "8";

        /// <summary>
        /// The QR payload for one ref the server issued.
        ///
        /// `refBytes` is the content of a `&lt;ref&gt;` child of the server's
        /// `&lt;pair-device&gt;`: text, not a binary blob, which is why it is written
        /// through as text and not re-encoded.
        /// </summary>
        public static string ForRefs(byte[] refBytes, Credentials credentials)
        {
            if (refBytes == null || refBytes.Length == 0)
            {
                throw new ProtocolException("a pairing ref cannot be empty");
            }

            if (credentials == null) throw new ProtocolException("a pairing QR needs the credentials it links");

            if (credentials.NoisePublicKey == null || credentials.IdentityPublicKey == null
                || credentials.AdvSecret == null)
            {
                throw new ProtocolException("a pairing QR needs the noise key, the identity key and the ADV secret");
            }

            var text = new StringBuilder(UriPrefix.Length + 4 * 48);
            text.Append(UriPrefix);
            text.Append(Encoding.UTF8.GetString(refBytes, 0, refBytes.Length));
            text.Append(',');
            text.Append(Convert.ToBase64String(credentials.NoisePublicKey));
            text.Append(',');
            text.Append(Convert.ToBase64String(credentials.IdentityPublicKey));
            text.Append(',');
            text.Append(Convert.ToBase64String(credentials.AdvSecret));
            text.Append(',');
            text.Append(ClientTypeUwp);

            return text.ToString();
        }
    }
}
