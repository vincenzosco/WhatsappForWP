using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// Group stanzas that arrived before the sender key they are encrypted with.
    ///
    /// Why it exists: a member's sender key travels in a pairwise copy that comes with the
    /// message they make it for - but the two are separate stanzas, and the server does not
    /// promise they arrive in that order. A `skmsg` that lands first used to be refused with
    /// "this phone holds no sender key for ...", which the connection answered with a retry
    /// receipt: the message was read only if the sender happened to send it again, and the
    /// chat showed nothing until then. The key is usually a moment behind, so the message is
    /// kept here instead, still encrypted and still unacknowledged, and opened as soon as a
    /// stanza brings the key for that member and that group.
    ///
    /// Why it is bounded: the key may never come - a member who left mid-send, a device that
    /// was unlinked - and a store that waits forever is a leak with a good excuse. The oldest
    /// entry is dropped when a cap is reached, and the drop is counted so the caller can say
    /// it. A dropped stanza was never acknowledged, so the server still has it: the loss is
    /// this run's, not the phone's.
    ///
    /// What it holds is the node as it arrived, not the bytes: the caller acks and reads it
    /// with the same code that reads a stanza off the wire, because that is what it is.
    /// </summary>
    public sealed class HeldGroupMessages
    {
        /// <summary>How many messages one member may have waiting before the oldest goes.</summary>
        public const int MaxPerSender = 20;

        /// <summary>
        /// How many may wait in the whole process. A group of twenty people whose keys are
        /// all missing at once is a phone that linked mid-conversation, not an attack.
        /// </summary>
        public const int MaxTotal = 60;

        private sealed class Held
        {
            public string Id;
            public BinaryNode Stanza;
            public long Seq;
        }

        /// <summary>The held stanzas, by group and member: the same name the sender key is stored under.</summary>
        private readonly Dictionary<string, List<Held>> _bySender = new Dictionary<string, List<Held>>();

        private long _nextSeq;
        private int _count;
        private int _dropped;

        /// <summary>How many stanzas are waiting right now.</summary>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>How many have been dropped because the caps were reached.</summary>
        public int Dropped
        {
            get { return _dropped; }
        }

        /// <summary>True when this member has at least one message waiting in this group.</summary>
        public bool Has(string groupJid, string participant)
        {
            return Entries(groupJid, participant) != null;
        }

        /// <summary>
        /// Keeps a stanza until the key for `participant` in `groupJid` arrives. False when
        /// this id is already waiting - the server re-delivers what nobody acknowledged, and
        /// holding the same stanza twice would read it twice.
        /// </summary>
        public bool Hold(string groupJid, string participant, string id, BinaryNode stanza)
        {
            if (string.IsNullOrEmpty(groupJid)) throw new ProtocolException("a held message belongs to a group");
            if (string.IsNullOrEmpty(participant)) throw new ProtocolException("a held message names the member who wrote it");
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a held message carries the stanza's id");
            if (stanza == null) throw new ProtocolException("there is no stanza to hold for " + id);

            List<Held> waiting = Entries(groupJid, participant);
            if (waiting == null)
            {
                string name = NameOf(groupJid, participant);
                _bySender[name] = new List<Held>();
                waiting = _bySender[name];
            }

            for (int i = 0; i < waiting.Count; i++)
            {
                if (waiting[i].Id == id) return false;
            }

            if (waiting.Count >= MaxPerSender)
            {
                Drop(waiting, 0);
            }
            else if (_count >= MaxTotal)
            {
                DropOldest();
            }

            var held = new Held();
            held.Id = id;
            held.Stanza = stanza;
            held.Seq = _nextSeq++;

            waiting.Add(held);
            _count++;

            return true;
        }

        /// <summary>
        /// The stanzas waiting for this member, oldest first, and they are no longer held:
        /// the caller reads them, and one it cannot open is held again by name.
        ///
        /// It is not called `Take`: `IList` has no such method, but the C# 5 gate reads the
        /// name as a LINQ extension and wants `using System.Linq;` for it.
        /// </summary>
        public IList<BinaryNode> Drain(string groupJid, string participant)
        {
            var stanzas = new List<BinaryNode>();
            List<Held> waiting = Entries(groupJid, participant);
            if (waiting == null) return stanzas;

            for (int i = 0; i < waiting.Count; i++) stanzas.Add(waiting[i].Stanza);

            _count -= waiting.Count;
            _bySender.Remove(NameOf(groupJid, participant));

            return stanzas;
        }

        private List<Held> Entries(string groupJid, string participant)
        {
            if (string.IsNullOrEmpty(groupJid) || string.IsNullOrEmpty(participant)) return null;

            List<Held> waiting;
            return _bySender.TryGetValue(NameOf(groupJid, participant), out waiting) && waiting.Count > 0
                ? waiting
                : null;
        }

        /// <summary>The name a sender key is stored under: `SessionStore.LoadSenderKey` takes the same one.</summary>
        private static string NameOf(string groupJid, string participant)
        {
            return groupJid + "/" + participant;
        }

        /// <summary>Drops the entry with the smallest sequence: the one that has waited longest.</summary>
        private void DropOldest()
        {
            List<Held> oldest = null;
            int index = -1;
            long seq = long.MaxValue;

            foreach (KeyValuePair<string, List<Held>> pair in _bySender)
            {
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    if (pair.Value[i].Seq >= seq) continue;

                    seq = pair.Value[i].Seq;
                    oldest = pair.Value;
                    index = i;
                }
            }

            if (oldest != null) Drop(oldest, index);
        }

        private void Drop(List<Held> waiting, int index)
        {
            waiting.RemoveAt(index);
            _count--;
            _dropped++;
        }
    }
}
