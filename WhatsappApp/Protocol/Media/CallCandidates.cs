using System;
using System.Collections.Generic;
using System.Text;
using WhatsappApp.Protocol.Client;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The `<te>` children of a call offer: this phone's transport candidates.
    ///
    /// Why this file exists: `CallSignaling` writes a candidate as `<te priority="N">bytes</te>`
    /// and nothing produced the bytes, so every offer this app sent named no transport at all -
    /// the other phone could decrypt the key and had nowhere to send the media. `enc` is the key
    /// and `te` is the way back; this is the missing half.
    ///
    /// The content is the reference client's `voip.Transport` message in protobuf, field by
    /// field: `id` (1, the candidate's own name), `ip` (2, four bytes for IPv4), `port` (3),
    /// `type` (4, `0` for a host candidate), `priority` (5) and `protocol` (6, `0` for UDP).
    /// The priority travels twice - as the `te` node's attribute, which is what this app reads
    /// back, and as a field of the message, which is what the peer's media engine reads.
    ///
    /// The field numbers are the one thing here that a capture would settle and this repository
    /// cannot: they live in `Host` alone, so a wrong number is one file and one test to change,
    /// and a peer that cannot read them ignores the candidate instead of the call.
    /// </summary>
    public static class CallCandidates
    {
        /// <summary>A candidate the phone answers on itself, which is the only kind it has.</summary>
        public const int HostType = 0;

        /// <summary>The protocol of a host candidate: RTP over UDP.</summary>
        public const int UdpProtocol = 0;

        /// <summary>
        /// One host candidate: the address this phone is reached on and the UDP port it listens
        /// on. `priority` orders the candidates against each other, lowest first, which is the
        /// order the official client gives the interfaces of one phone.
        /// </summary>
        public static CallCandidate Host(string address, int port, int priority)
        {
            if (port <= 0 || port > 65535)
            {
                throw new ProtocolException("a call candidate port is 1..65535, not " + port);
            }

            if (priority < 0)
            {
                throw new ProtocolException("a call candidate has no priority below zero, not " + priority);
            }

            byte[] ip = Address(address);

            // The `id` the reference gives a candidate is the endpoint itself: it is how the two
            // media engines name the same candidate in their later `transport` nodes.
            string id = address + ":" + port;

            var bytes = new List<byte>();
            AppendBytes(bytes, 1, Encoding.UTF8.GetBytes(id));
            AppendBytes(bytes, 2, ip);
            AppendNumber(bytes, 3, (ulong)port);
            AppendNumber(bytes, 4, (ulong)HostType);
            AppendNumber(bytes, 5, (ulong)priority);
            AppendNumber(bytes, 6, (ulong)UdpProtocol);

            var candidate = new CallCandidate();
            candidate.Priority = priority;
            candidate.Data = bytes.ToArray();
            return candidate;
        }

        /// <summary>The four bytes of an IPv4 address, or a refusal that names the address.</summary>
        private static byte[] Address(string address)
        {
            if (string.IsNullOrEmpty(address)) throw new ProtocolException("a call candidate needs an address");

            string[] parts = address.Split('.');
            if (parts.Length != 4)
            {
                throw new ProtocolException("a call candidate is an IPv4 address, not " + address);
            }

            var ip = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                int value;
                if (!int.TryParse(parts[i], out value) || value < 0 || value > 255)
                {
                    throw new ProtocolException("a call candidate is an IPv4 address, not " + address);
                }

                ip[i] = (byte)value;
            }

            return ip;
        }

        /// <summary>A length-delimited field: strings and byte strings.</summary>
        private static void AppendBytes(List<byte> to, int field, byte[] value)
        {
            AppendTag(to, field, 2);
            AppendValue(to, (ulong)value.Length);

            for (int i = 0; i < value.Length; i++) to.Add(value[i]);
        }

        /// <summary>A number field: ports, types, priorities, protocols.</summary>
        private static void AppendNumber(List<byte> to, int field, ulong value)
        {
            AppendTag(to, field, 0);
            AppendValue(to, value);
        }

        private static void AppendTag(List<byte> to, int field, int wire)
        {
            AppendValue(to, (ulong)((field << 3) | wire));
        }

        /// <summary>Base 128, low group first, which is the encoding every protobuf field uses.</summary>
        private static void AppendValue(List<byte> to, ulong value)
        {
            while (value >= 0x80)
            {
                to.Add((byte)((value & 0x7F) | 0x80));
                value = value >> 7;
            }

            to.Add((byte)value);
        }
    }
}
