using System.Collections.Generic;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// One call as the log shows it: who, whether it was video, how long, and how it ended.
    ///
    /// `IsMissed` is not a field WhatsApp sends: it is the `callResult` of the record
    /// (`CallLogRecord.CallResult`, `WAProto/index.d.ts:2345`), where `MISSED` is 4 and the
    /// values around it are `REJECTED` 1, `CANCELLED` 2, `CONNECTED` 0. A call the phone never
    /// answered and a call that was declined are different things on a call log's screen, and
    /// this keeps them different: `IsMissed` is `MISSED` alone.
    /// </summary>
    public sealed class CallLogEntryModel
    {
        public string Id;
        public string Jid;
        public bool IsVideo;
        public bool IsMissed;
        public bool IsIncoming;

        /// <summary>The moment the call started, in seconds.</summary>
        public long Timestamp;

        /// <summary>How long it lasted, in seconds. Zero for one that never connected.</summary>
        public int Seconds;
    }

    /// <summary>
    /// The call log, and the words of the call protocol.
    ///
    /// The rows come from the **history sync** and from nowhere else: WhatsApp puts the call
    /// log in `HistorySync.callLogRecords`, which is why a call made while the phone was off
    /// is in the list the next time the app opens and a call made while it was on arrives with
    /// the next sync. That is also why there is no `query` here: there is nothing to ask.
    ///
    /// The node builders are here as one family, and they are `CallSignaling`'s: this file is
    /// where the rest of the client looks for "the call protocol" (`Calls.Reject` was its first
    /// member), not a second author of the shapes. Every verb the official client's
    /// `VoipSignaling.WriteVoipNode` writes is reachable from here, so a page or the connection
    /// never has to build a call node by hand.
    /// </summary>
    public static class Calls
    {
        /// <summary>The `callResult` a missed call has (`CallLogRecord.CallResult.MISSED`).</summary>
        public const int Missed = 4;

        /// <summary>
        /// The log as rows, newest first: a call log reads like a chat, and the sync sends its
        /// records oldest first.
        ///
        /// `ownJid` is this account, and it has to be told rather than guessed: the record's
        /// `callCreatorJID` is the caller, so for a call this phone made it is this account
        /// itself and the chat is only knowable by taking the other name off the participant
        /// list. A caller that passed null would get its own number as the chat of every
        /// outgoing call.
        /// </summary>
        public static IList<CallLogEntryModel> FromHistory(HistorySync sync, string ownJid)
        {
            var calls = new List<CallLogEntryModel>();
            if (sync == null || sync.CallLogRecords == null) return calls;

            for (int i = 0; i < sync.CallLogRecords.Count; i++)
            {
                CallLogRecord record = sync.CallLogRecords[i];
                if (record == null) continue;

                string jid = string.IsNullOrEmpty(record.Group) ? Other(record, ownJid) : record.Group;

                // A record that names nobody but this account is a call with no conversation
                // to appear in, on WhatsApp and here alike.
                if (string.IsNullOrEmpty(jid)) continue;

                var entry = new CallLogEntryModel();
                entry.Id = record.CallId;
                entry.Jid = jid;
                entry.IsVideo = record.IsVideo;
                entry.IsIncoming = record.IsIncoming;
                entry.IsMissed = record.Result == Missed;
                entry.Timestamp = record.StartTime;

                // A call that never connected has no duration, and the absence is a zero here
                // and not a random: `AMissedCallWithNoDurationSaysZero`.
                entry.Seconds = record.Duration > 0 && record.Duration < int.MaxValue
                    ? (int)record.Duration
                    : 0;

                calls.Add(entry);
            }

            calls.Sort(delegate (CallLogEntryModel left, CallLogEntryModel right)
            {
                return right.Timestamp.CompareTo(left.Timestamp);
            });

            return calls;
        }

        /// <summary>
        /// The other party of a one-to-one call, or null when the record names nobody else.
        ///
        /// The order matters: the participant list first, because the creator is this account
        /// on every outgoing call and the list is where the person we called is; the creator
        /// second, for a record whose participant list did not come with the sync. The device
        /// suffix is ignored, because the same person appears with one device here and
        /// another there.
        /// </summary>
        private static string Other(CallLogRecord record, string ownJid)
        {
            bool known = !string.IsNullOrEmpty(ownJid);

            for (int i = 0; i < record.Participants.Count; i++)
            {
                string who = record.Participants[i];

                // Without its device, as the doc comment says: the call row is keyed on the
                // chat's address, and `15551230000:5` is no chat's address - the list then
                // printed the number with its device in it and found no name for it.
                if (!known || !SamePerson(who, ownJid)) return JidUtils.Normalize(who);
            }

            if (!SamePerson(record.CallCreatorJid, ownJid)) return JidUtils.Normalize(record.CallCreatorJid);

            return null;
        }

        /// <summary>The user half of two JIDs, ignoring the device and the server.</summary>
        private static bool SamePerson(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;

            return User(left) == User(right);
        }

        private static string User(string jid)
        {
            int at = jid.IndexOf('@');
            string user = at < 0 ? jid : jid.Substring(0, at);

            int colon = user.IndexOf(':');
            return colon < 0 ? user : user.Substring(0, colon);
        }

        /// <summary>
        /// Refusing a call: `call` with a `reject` child carrying the call's id, addressed to
        /// the caller (`whatsmeow`, `send.go` and `call.go`, `RejectCall`: `<call to=. id=.>
        /// <reject/></call>`).
        /// </summary>
        public static BinaryNode Reject(string callId, string jid, string callCreator, string reason)
        {
            return CallSignaling.Reject(callId, jid, callCreator, reason);
        }

        public static BinaryNode Reject(string callId, string jid)
        {
            // Built by `CallSignaling` and not here, because the rest of the family
            // (`offer`, `preaccept`, `accept`, `terminate`) lives there now: one node with
            // one verb child is a shape with one author, and a `reject` built by a second
            // one is how the two answers drift apart.
            return CallSignaling.Reject(callId, jid, null);
        }

        /// <summary>
        /// The refusal that carries the reason the official client has for a call it could not
        /// decrypt: `reason="enc"`, the retry count, and this phone's registration id.
        /// </summary>
        public static BinaryNode RejectUndecryptable(
            string callId, string jid, string callCreator, uint retryCount, uint registrationId)
        {
            return CallSignaling.RejectUndecryptable(callId, jid, callCreator, retryCount, registrationId);
        }

        public static BinaryNode RejectUndecryptable(
            string callId, string jid, uint retryCount, uint registrationId)
        {
            return CallSignaling.RejectUndecryptable(callId, jid, retryCount, registrationId);
        }

        /// <summary>
        /// The offer the official client sends: the audio streams, the video descriptions, the
        /// encrypted call key and the transport candidates. Without the last two the other
        /// phone rings and can neither decrypt the media nor reach this one.
        /// </summary>
        public static BinaryNode Offer(
            string callId,
            string jid,
            string caller,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            CallKeyPayload key,
            IList<CallCandidate> candidates)
        {
            return CallSignaling.Offer(callId, jid, caller, audio, video, key, candidates);
        }

        /// <summary>The same offer with one `<enc>` per recipient device.</summary>
        public static BinaryNode Offer(
            string callId,
            string jid,
            string caller,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            IList<CallKeyPayload> keys,
            IList<CallCandidate> candidates)
        {
            return CallSignaling.Offer(callId, jid, caller, audio, video, keys, candidates);
        }

        /// <summary>The same offer with this device's signed identity, for an offer that opens a session.</summary>
        public static BinaryNode Offer(
            string callId,
            string jid,
            string caller,
            byte[] deviceIdentity,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            IList<CallKeyPayload> keys,
            IList<CallCandidate> candidates)
        {
            return CallSignaling.Offer(callId, jid, caller, deviceIdentity, audio, video, keys, candidates);
        }

        /// <summary>
        /// The same offer with the fields this build cannot invent (`caller_pn`,
        /// `caller_country_code`, `joinable` and the `metadata`/`rte`/`voip_settings`
        /// children), from `CallWireProfile`. A null profile is the offer without them.
        /// </summary>
        public static BinaryNode Offer(
            string callId,
            string jid,
            string caller,
            byte[] deviceIdentity,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            IList<CallKeyPayload> keys,
            IList<CallCandidate> candidates,
            CallWireProfile profile)
        {
            return CallSignaling.Offer(callId, jid, caller, deviceIdentity, audio, video, keys, candidates, profile);
        }

        /// <summary>The same offer with one key and the identity that proves it.</summary>
        public static BinaryNode Offer(
            string callId,
            string jid,
            string caller,
            byte[] deviceIdentity,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            CallKeyPayload key,
            IList<CallCandidate> candidates)
        {
            return CallSignaling.Offer(callId, jid, caller, deviceIdentity, audio, video, key, candidates);
        }

        /// <summary>The answer that names the stream it will receive and how to reach this phone.</summary>
        public static BinaryNode Accept(
            string callId,
            string jid,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            IList<CallCandidate> candidates)
        {
            return CallSignaling.Accept(callId, jid, audio, video, candidates);
        }

        /// <summary>
        /// The answer with the fingerprint of this side's media certificate, which is the one
        /// the caller pins the handshake against.
        /// </summary>
        public static BinaryNode Accept(
            string callId,
            string jid,
            string callCreator,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            IList<CallCandidate> candidates,
            string fingerprint)
        {
            return CallSignaling.Accept(callId, jid, callCreator, audio, video, candidates, fingerprint);
        }

        public static BinaryNode Accept(
            string callId,
            string jid,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            IList<CallCandidate> candidates,
            string fingerprint)
        {
            return CallSignaling.Accept(callId, jid, audio, video, candidates, fingerprint);
        }

        /// <summary>The minimal pre-accept with the creator identity copied from the offer.</summary>
        public static BinaryNode PreAccept(string callId, string jid, string callCreator)
        {
            return CallSignaling.PreAccept(callId, jid, callCreator);
        }

        /// <summary>The pre-accept, with the same media description the answer will use.</summary>
        public static BinaryNode PreAccept(
            string callId, string jid, string callCreator,
            IList<CallAudio> audio, IList<CallVideoDescription> video)
        {
            return CallSignaling.PreAccept(callId, jid, callCreator, audio, video);
        }

        public static BinaryNode PreAccept(
            string callId, string jid, IList<CallAudio> audio, IList<CallVideoDescription> video)
        {
            return CallSignaling.PreAccept(callId, jid, audio, video);
        }

        /// <summary>A terminate with the creator identity copied from the offer.</summary>
        public static BinaryNode Terminate(string callId, string jid, string callCreator, string reason)
        {
            return CallSignaling.Terminate(callId, jid, callCreator, reason);
        }

        /// <summary>A terminate that says how long the call lasted and what the video was doing.</summary>
        public static BinaryNode Terminate(
            string callId, string jid, string callCreator, string reason,
            int durationSeconds, int videoState)
        {
            return CallSignaling.Terminate(callId, jid, callCreator, reason, durationSeconds, videoState);
        }

        public static BinaryNode Terminate(
            string callId, string jid, string reason, int durationSeconds, int videoState)
        {
            return CallSignaling.Terminate(callId, jid, reason, durationSeconds, videoState);
        }

        /// <summary>Fresh candidates with the creator identity copied from the offer.</summary>
        public static BinaryNode Transport(
            string callId, string jid, string callCreator, IList<CallCandidate> candidates)
        {
            return CallSignaling.Transport(callId, jid, callCreator, candidates);
        }

        /// <summary>Fresh candidates for the other side: `<transport call-id><te/></transport>`.</summary>
        public static BinaryNode Transport(string callId, string jid, IList<CallCandidate> candidates)
        {
            return CallSignaling.Transport(callId, jid, candidates);
        }

        /// <summary>Reports one relay candidate's measured RTT.</summary>
        public static BinaryNode RelayLatency(string callId, string creatorJid, string jid,
            CallRelayLatency relay, IList<string> destinationDeviceJids)
        {
            return CallSignaling.RelayLatency(callId, creatorJid, jid, relay, destinationDeviceJids);
        }

        /// <summary>The legacy relay-election action; this does not bind the media route.</summary>
        public static BinaryNode RelayElection(string callId, string jid, string callCreator,
            int latency, byte[] token)
        {
            return CallSignaling.RelayElection(callId, jid, callCreator, latency, token);
        }

        public static BinaryNode RelayElection(string callId, string jid, int latency, byte[] token)
        {
            return CallSignaling.RelayElection(callId, jid, latency, token);
        }

        /// <summary>Video on, off, or turned.</summary>
        public static BinaryNode Video(string callId, string jid, string callCreator,
            int state, int orientation)
        {
            return CallSignaling.Video(callId, jid, callCreator, state, orientation);
        }

        public static BinaryNode Video(string callId, string jid, int state, int orientation)
        {
            return CallSignaling.Video(callId, jid, state, orientation);
        }

        /// <summary>The other phone's battery, which the official client sends during a call.</summary>
        public static BinaryNode Notify(string callId, string jid, string callCreator, int batteryState)
        {
            return CallSignaling.Notify(callId, jid, callCreator, batteryState);
        }

        public static BinaryNode Notify(string callId, string jid, int batteryState)
        {
            return CallSignaling.Notify(callId, jid, batteryState);
        }

        /// <summary>The call was put on hold (`begin`) or picked up again (`end`).</summary>
        public static BinaryNode Interruption(string callId, string jid, string callCreator, bool begin)
        {
            return CallSignaling.Interruption(callId, jid, callCreator, begin);
        }

        public static BinaryNode Interruption(string callId, string jid, bool begin)
        {
            return CallSignaling.Interruption(callId, jid, begin);
        }

        /// <summary>The call was muted (`begin`) or unmuted (`end`).</summary>
        public static BinaryNode Mute(string callId, string jid, string callCreator, bool muted)
        {
            return CallSignaling.Mute(callId, jid, callCreator, muted);
        }

        public static BinaryNode Mute(string callId, string jid, bool muted)
        {
            return CallSignaling.Mute(callId, jid, muted);
        }
    }
}
