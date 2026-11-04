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
    ///  3. **The key goes out with the session that exists, and the log says which kind.**
    ///     `msg` for an established session, `pkmsg` for one whose pre-key exchange has not
    ///     been answered yet. Both are sealed, because an offer with no key is a phone that
    ///     never rings: the log of 4 November 2026 has exactly that (`offered a call ... to
    ///     393492556507@s.whatsapp.net with no key`, five devices with no established
    ///     session, then `ended the call ... reason timeout`). The reference client does the
    ///     same - `Message.CreateForCall` hands the key to `SessionCipher.encrypt`, which
    ///     answers a pre-key message for a session that is still pending - and `whatsmeow`
    ///     issue 555 (22 March 2024) reports the server dropping such an offer, which has not
    ///     been reproduced here. So the type is logged per device instead of refused: if a
    ///     phone does not ring, the log names the pre-key offer that is the first suspect.
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

        /// <summary>
        /// The `enc` type of a call key sealed for a session whose pre-key exchange has not
        /// been answered. Named for the log rather than refused: an offer without it has no
        /// key at all, and a call nobody can decrypt is a call that never rings.
        /// </summary>
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
        /// Seals the call key for each device that has a session, pending or established.
        ///
        /// `devices` is the same list a one-to-one send uses: device JID to session. A device
        /// with no session at all is skipped and named - nothing can be sealed for it - while
        /// a pending one is sealed as `pkmsg` and named in the log by the caller, because an
        /// offer with no key is a phone that never rings.
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

                if (session == null || !session.HasSession)
                {
                    offer.Skipped.Add(string.IsNullOrEmpty(deviceJid) ? "?" : deviceJid);
                    continue;
                }

                // A session the peer has not answered yet can only be written as a pre-key
                // message, and that is what the reference client puts in an offer too: the
                // alternative is no key, which is the failure this app logged on 4 November.
                bool preKey = session.PreKeyPending;

                // One padding per device: the padding is random bytes, and two ciphertexts
                // that shared an envelope would share its noise as well.
                plaintext = OutgoingMessage.Pad(Message.ForCall(callKey).Encode(), random);

                var payload = new CallKeyPayload();
                payload.Type = preKey ? PreKeyType : EstablishedType;
                payload.Version = KeyVersion;
                payload.RetryCount = 0;
                payload.Ciphertext = session.Encrypt(plaintext);
                offer.Payloads.Add(payload);
            }

            if (offer.Payloads.Count == 0)
            {
                throw new ProtocolException("a call key could not be sealed for any device ("
                    + offer.Skipped.Count + " had no session at all): the offer would name streams "
                    + "nobody can open, and the call would ring nowhere");
            }

            return offer;
        }

        /// <summary>
        /// The call key out of one `<enc>` of an offer: the receiving half of `Wrap`.
        ///
        /// `session` is this device's session with the **device that sent the offer** - the
        /// payload is addressed to this phone, not to its account. `preKey` is read from the
        /// payload's own type, so both kinds of offer open to the same 32 bytes.
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
