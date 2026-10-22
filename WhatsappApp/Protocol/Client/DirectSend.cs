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
            if (deviceJid.EndsWith("@hosted", StringComparison.OrdinalIgnoreCase)) return true;
            if (deviceJid.EndsWith("@hosted.lid", StringComparison.OrdinalIgnoreCase)) return true;

            return deviceJid.StartsWith("99:", StringComparison.OrdinalIgnoreCase);
        }

        private static PreKeyBundle Find(IList<KeyValuePair<string, PreKeyBundle>> bundles, string deviceJid)
        {
            if (bundles == null) return null;

            for (int i = 0; i < bundles.Count; i++)
            {
                if (bundles[i].Key == deviceJid) return bundles[i].Value;
            }

            return null;
        }
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
