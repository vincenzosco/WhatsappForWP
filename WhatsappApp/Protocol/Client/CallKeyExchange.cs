using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Media;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>What a call key wrap produced, and who it could not be produced for.</summary>
    public sealed class CallKeyOffer
    {
        /// <summary>One payload per device, in the order the devices were given.</summary>
        public IList<CallKeyPayload> Payloads = new List<CallKeyPayload>();

        /// <summary>
        /// The devices whose session is not established (or missing), so no key was sealed
        /// for them. Named, not silently dropped: each one is a companion phone that will
        /// not decrypt this call.
        /// </summary>
        public IList<string> Skipped = new List<string>();
    }

    /// <summary>
    /// The call key, sealed into the `<enc>` children of an offer.
    ///
    /// Why this file exists: `CallSignaling` could carry a `CallKeyPayload` but nothing
    /// produced one, so an offer could name its media and never deliver the key - the other
    /// phone rings and has nothing to decrypt. This is the missing step, and it has three
    /// rules that are the protocol's, not this app's:
    ///
    ///  1. **One payload per device.** WhatsApp is multi-device: `whatsmeow` and the call
    ///     corpus (`wacrg.org`, "Call offer") both describe `enc` as `1..n` children, one per
    ///     recipient device, because a Signal ciphertext is addressed to one session.
    ///  2. **The plaintext is a `Message`, not the bare key.** `Message.call.callKey` (field
    ///     10, then field 1) is what the reference encrypts (`Message.CreateForCall`), and a
    ///     receiver reads the key out of the message it decrypted.
    ///  3. **`msg`, never `pkmsg`.** A call offer whose key is a pre-key message is dropped by
    ///     the server (`whatsmeow` issue 555, 22 March 2024: "the server drops the call offer
    ///     if the call key is encrypted using pre-keys"). A session that is still pending has
    ///     to be established by an ordinary message first; until then this device cannot
    ///     call, and saying so is better than a stanza the server throws away.
    ///
    /// The key is 32 bytes (`CallKeys.CallKeyLength`) and the same for every device: it is
    /// the media key of the call, and each device gets its own ciphertext of it.
    /// </summary>
    public static class CallKeyExchange
    {
        /// <summary>The version an `<enc>` child of a call offer carries (`v="2"`).</summary>
        public const int KeyVersion = 2;

        /// <summary>The `enc` type of a call key: an established session, always.</summary>
        public const string EstablishedType = "msg";

        /// <summary>The `enc` type the server refuses for a call key, named for the log.</summary>
        public const string PreKeyType = "pkmsg";

        /// <summary>A fresh call key: the 32 bytes the media engine is handed.</summary>
        public static byte[] NewCallKey(IRandomSource random)
        {
            if (random == null) throw new ProtocolException("a call key needs randomness");

            byte[] key = random.Bytes(CallKeys.CallKeyLength);
            if (key == null || key.Length != CallKeys.CallKeyLength)
            {
                throw new ProtocolException("the platform answered " + (key == null ? 0 : key.Length)
                    + " bytes for a call key, not " + CallKeys.CallKeyLength);
            }

            return key;
        }

        /// <summary>
        /// Seals the call key for each device that already has a session.
        ///
        /// `devices` is the same list a one-to-one send uses: device JID to session. A device
        /// whose session is missing or still pending is skipped and named - it cannot be
        /// called from here until an ordinary message establishes the session, and a
        /// `pkmsg` here would make the server drop the whole offer.
        /// </summary>
        public static CallKeyOffer Wrap(
            byte[] callKey, IList<KeyValuePair<string, SessionRecord>> devices, IRandomSource random)
        {
            if (callKey == null || callKey.Length != CallKeys.CallKeyLength)
            {
                throw new ProtocolException("a call key is " + CallKeys.CallKeyLength + " bytes");
            }

            if (devices == null || devices.Count == 0)
            {
                throw new ProtocolException("a call key needs at least one device to be sealed for");
            }

            var offer = new CallKeyOffer();
            byte[] plaintext = null;

            for (int i = 0; i < devices.Count; i++)
            {
                string deviceJid = devices[i].Key;
                SessionRecord session = devices[i].Value;

                if (string.IsNullOrEmpty(deviceJid)) continue;

                if (session == null || !session.HasSession || session.PreKeyPending)
                {
                    offer.Skipped.Add(string.IsNullOrEmpty(deviceJid) ? "?" : deviceJid);
                    continue;
                }

                // One padding per device: the padding is random bytes, and two ciphertexts
                // that shared an envelope would share its noise as well.
                plaintext = OutgoingMessage.Pad(Message.ForCall(callKey).Encode(), random);

                var payload = new CallKeyPayload();
                payload.Type = EstablishedType;
                payload.Version = KeyVersion;
                payload.RetryCount = 0;
                payload.Ciphertext = session.Encrypt(plaintext);
                offer.Payloads.Add(payload);
            }

            if (offer.Payloads.Count == 0)
            {
                throw new ProtocolException("a call key could not be sealed for any device ("
                    + offer.Skipped.Count + " had no established session): the server drops an "
                    + "offer keyed with " + PreKeyType + ", so an ordinary message has to establish "
                    + "the session first");
            }

            return offer;
        }

        /// <summary>
        /// The call key out of one `<enc>` of an offer: the receiving half of `Wrap`.
        ///
        /// `session` is this device's session with the **device that sent the offer** - the
        /// payload is addressed to this phone, not to its account. `preKey` is read from the
        /// payload's own type; a `pkmsg` is readable but is the shape the server refuses on
        /// an offer, and the answer here is the same 32 bytes.
        /// </summary>
        public static byte[] Unwrap(CallKeyPayload payload, SessionRecord session)
        {
            if (payload == null || payload.Ciphertext == null || payload.Ciphertext.Length == 0)
            {
                throw new ProtocolException("a call key payload carries no ciphertext");
            }

            if (session == null)
            {
                throw new ProtocolException("a call key needs the session it was sealed in");
            }

            byte[] plaintext = session.Decrypt(
                payload.Ciphertext,
                string.Equals(payload.Type, PreKeyType, StringComparison.OrdinalIgnoreCase));

            byte[] callKey = Message.DecodePadded(plaintext).CallKey();

            if (callKey == null || callKey.Length != CallKeys.CallKeyLength)
            {
                throw new ProtocolException("the decrypted call key is "
                    + (callKey == null ? null : callKey.Length.ToString())
                    + " bytes, not " + CallKeys.CallKeyLength);
            }

            return callKey;
        }
    }
}
