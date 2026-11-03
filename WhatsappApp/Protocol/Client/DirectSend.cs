using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// Who a message to a person is encrypted for, in the order the reference asks the
    /// questions: the device list says **who**, the bundles supply **keys**, and the two
    /// are two different answers.
    ///
    /// Why this lives here and not in `NativeConnection`: it lived there, and the two
    /// answers were read by one parser - the device list handed to `Bundles` - so every
    /// one-to-one send died before a stanza existed. No test could see it, because the
    /// service is WinRT code this desktop runner cannot compile. The rule is in the
    /// protocol core now, where a test can drive it end to end, and the service asks it
    /// instead of repeating it.
    /// </summary>
    public static class DirectSend
    {
        /// <summary>
        /// The address a one-to-one send names: the chat's `@lid` when this phone knows the
        /// pair, and the chat itself otherwise.
        ///
        /// Why the LID and not the number: WhatsApp moves accounts to LIDs, and the phone
        /// of 2026-11-01 showed what happens when a chat is addressed by the number it used
        /// to have. The message to `393492556507@s.whatsapp.net` - correct spelling, device
        /// 0 written bare - was taken by the server (`ack class="message"`, the single tick
        /// this app draws as "sent"), no delivery receipt came back, no retry receipt came
        /// back, and the other phone showed nothing at all. The same person writes from
        /// `179585517920463@lid` and every receipt the server sends names devices in
        /// `@lid`. A device list for an address that has moved is a list of devices nobody
        /// reads.
        ///
        /// The chat **row** stays keyed on the number: `NameBook.Canonical` is what folds
        /// the two spellings into one conversation, its pin and its cache, and that has not
        /// changed. What changes is the address on the wire, which is a different question
        /// from the key of a row.
        /// </summary>
        public static string WireAddress(string jid, string lid)
        {
            if (string.IsNullOrEmpty(jid)) throw new ProtocolException("a send needs somebody to go to");
            if (string.IsNullOrEmpty(lid)) return jid;

            // Only the pair of *this* chat is used: a LID handed in for another account
            // would address a message to the wrong person, which is worse than a number.
            if (!lid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase)) return jid;

            return lid;
        }

        /// <summary>
        /// The devices a one-to-one message goes to: the peer's, without this phone's own
        /// (nobody writes to themselves) and without the server-side ones.
        ///
        /// The list is the `usync` answer (`user &gt; devices &gt; device-list &gt; device[id]`),
        /// not the bundle answer; a bundle answer has no `device-list` at all.
        /// </summary>
        public static IList<string> Targets(BinaryNode deviceListAnswer, string ourJid, string ourLid)
        {
            if (string.IsNullOrEmpty(ourJid) && string.IsNullOrEmpty(ourLid))
            {
                throw new ProtocolException("a send has to know which of those devices is this phone");
            }

            IList<string> listed = DeviceDirectory.Devices(deviceListAnswer);
            var targets = new List<string>();

            for (int i = 0; i < listed.Count; i++)
            {
                string device = listed[i];
                if (string.IsNullOrEmpty(device)) continue;
                if (device == ourJid || device == ourLid) continue;
                if (IsHosted(device)) continue;

                targets.Add(device);
            }

            if (targets.Count == 0)
            {
                throw new ProtocolException("the device list names nobody but this phone");
            }

            return targets;
        }

        /// <summary>
        /// The devices of `targets` that still need a bundle: the ones with no session, and
        /// no more. An established session is the ratchet, and asking for its keys again is
        /// a round trip nobody reads.
        /// </summary>
        public static IList<string> WithoutSession(IList<string> targets, SessionStore store)
        {
            if (targets == null) throw new ProtocolException("a send needs the devices it goes to");
            if (store == null) throw new ProtocolException("a send needs the store its sessions are kept in");

            var missing = new List<string>();
            for (int i = 0; i < targets.Count; i++)
            {
                SessionRecord existing = store.Load(targets[i]);
                if (existing == null || !existing.HasSession) missing.Add(targets[i]);
            }

            return missing;
        }

        /// <summary>
        /// The sessions to seal for: the stored one per device, or one built from its bundle.
        ///
        /// A device the server hands no key for - and this phone has never talked to - is
        /// **skipped**, not fatal: one unreachable device must not take the message away from
        /// the others. It comes back in `Skipped` so the caller can name it in the log.
        ///
        /// `bundleAnswer` is null when every device already had a session, and reading a
        /// device-list answer here is refused by `Bundles` by name: it is the mistake that
        /// made the one-to-one send impossible, and it must not be silently survivable.
        /// </summary>
        public static DirectSendPlan Plan(IList<string> targets, BinaryNode bundleAnswer,
            Credentials credentials, SessionStore store, IRandomSource random)
        {
            if (targets == null) throw new ProtocolException("a send needs the devices it goes to");
            if (store == null) throw new ProtocolException("a send needs the store its sessions are kept in");
            if (credentials == null) throw new ProtocolException("a send needs this device's credentials");

            IList<KeyValuePair<string, PreKeyBundle>> bundles = bundleAnswer == null
                ? null
                : DeviceDirectory.Bundles(bundleAnswer);

            var plan = new DirectSendPlan();
            plan.Devices = new List<KeyValuePair<string, SessionRecord>>();
            plan.Skipped = new List<string>();

            for (int i = 0; i < targets.Count; i++)
            {
                string deviceJid = targets[i];
                SessionRecord existing = store.Load(deviceJid);
                PreKeyBundle bundle = Find(bundles, deviceJid);

                if ((existing == null || !existing.HasSession) && bundle == null)
                {
                    plan.Skipped.Add(deviceJid);
                    continue;
                }

                plan.Devices.Add(new KeyValuePair<string, SessionRecord>(deviceJid,
                    DeviceDirectory.Session(deviceJid, bundle, credentials, store, random)));
            }

            return plan;
        }

        /// <summary>WhatsApp's server-side devices and the placeholder device 99: the reference leaves all three out.</summary>
        private static bool IsHosted(string deviceJid)
        {
            if (string.IsNullOrEmpty(deviceJid)) return true;
            if (deviceJid.EndsWith("@hosted", StringComparison.OrdinalIgnoreCase)) return true;
            if (deviceJid.EndsWith("@hosted.lid", StringComparison.OrdinalIgnoreCase)) return true;

            // The device is inside the JID (`name:99@server`), so the check parses it: the
            // front of the string is the phone number, never the device id.
            return JidUtils.IsPlaceholderDevice(deviceJid);
        }

        private static PreKeyBundle Find(IList<KeyValuePair<string, PreKeyBundle>> bundles, string deviceJid)
        {
            if (bundles == null) return null;

            for (int i = 0; i < bundles.Count; i++)
            {
                if (bundles[i].Key == deviceJid || IsPrimaryDeviceAlias(bundles[i].Key, deviceJid))
                {
                    return bundles[i].Value;
                }
            }

            // A per-user bundle response is not always written back with the exact
            // spelling used by the device list. The server may omit device zero, or
            // answer a phone-number target with the corresponding LID device JID.
            // Sessions are still stored under the device-list JID; associate only the
            // same device number, never an arbitrary bundle from the answer.
            DecodedJid target = JidUtils.Decode(deviceJid);
            if (!target.IsJid) return null;

            for (int i = 0; i < bundles.Count; i++)
            {
                DecodedJid candidate = JidUtils.Decode(bundles[i].Key);
                if (!candidate.IsJid) continue;

                int targetDevice = target.HasDevice ? target.Device : 0;
                int candidateDevice = candidate.HasDevice ? candidate.Device : 0;
                if (targetDevice != candidateDevice) continue;

                if (target.User == candidate.User && target.Server == candidate.Server)
                {
                    return bundles[i].Value;
                }

                if (JidUtils.IsLid(deviceJid) != JidUtils.IsLid(bundles[i].Key))
                {
                    return bundles[i].Value;
                }
            }

            return null;
        }

        /// <summary>
        /// The primary device is sometimes returned without its explicit `:0` in the
        /// bundle answer, although the device list names it as `user:0@server`.
        /// Linked devices must remain exact matches: collapsing their device ids would
        /// let one device's bundle be used for another.
        /// </summary>
        private static bool IsPrimaryDeviceAlias(string bundleJid, string deviceJid)
        {
            DecodedJid bundle = JidUtils.Decode(bundleJid);
            DecodedJid device = JidUtils.Decode(deviceJid);

            return bundle.IsJid
                && device.IsJid
                && !bundle.HasDevice
                && device.HasDevice
                && device.Device == 0
                && bundle.User == device.User
                && bundle.Server == device.Server;
        }
    }

    /// <summary>
    /// Where a one-to-one send goes and what it was sealed for: the address the stanza
    /// names, and the devices standing behind it.
    ///
    /// The two travel together because they have to agree: a stanza whose `to` is the
    /// number and whose `enc` nodes are `@lid` devices is the mixed shape the server
    /// refuses, and a send that heard the wrong family is a send that can be taken and
    /// never delivered (`DirectSend.WireAddress`).
    /// </summary>
    public sealed class DirectTarget
    {
        /// <summary>The JID the `message` node names, in the family the devices are in.</summary>
        public string To { get; set; }

        /// <summary>The device and the session to seal for it, one per device.</summary>
        public IList<KeyValuePair<string, SessionRecord>> Devices { get; set; }
    }

    /// <summary>
    /// What a one-to-one send was able to seal for, and who it had to leave out for want of
    /// a key. Both are answers: a device in `Skipped` is a message the peer's other devices
    /// still get, and a log line that names it.
    /// </summary>
    public sealed class DirectSendPlan
    {
        public IList<KeyValuePair<string, SessionRecord>> Devices { get; set; }

        public IList<string> Skipped { get; set; }
    }
}
