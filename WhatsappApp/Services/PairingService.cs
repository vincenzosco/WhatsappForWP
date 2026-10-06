using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// What one pairing attempt did, for the caller to render.
    /// </summary>
    public sealed class PairingResult
    {
        /// <summary>True when the server adopted the key this phone generated.</summary>
        public bool Paired;

        /// <summary>
        /// True when the server sent no code: either pairing is off, or it
        /// already has a key of its own and has nothing to replace.
        /// </summary>
        public bool NoCode;

        /// <summary>True when the exchange did not complete: socket, cipher or timeout.</summary>
        public bool Error;

        /// <summary>
        /// The one-time code as the server sent it, for the page to show.
        /// Empty when the user typed the code, and cleared when a turn fails.
        /// </summary>
        public string Code;

        /// <summary>The server's own words when it refused, or the failure's message.</summary>
        public string Message;
    }

    /// <summary>
    /// The pairing exchange, in one place.
    ///
    /// Why it exists: the connection page used to carry the whole protocol, and
    /// the app now runs the same exchange by itself the first time it connects
    /// after opening (see <see cref="TryAutoPairAsync"/>). One copy of the
    /// protocol, two callers.
    ///
    /// The phone generates the key it will use - 32 random bytes, base64url - and
    /// sends it sealed with the one-time code the server prints at startup. The
    /// outer frame is keyed the way this phone is configured now, which before
    /// pairing is the server's default: that is what makes a key of our own
    /// reachable at this step. The server adopts the key, derives this device's
    /// token from its device id and returns it in the `paired` frame.
    /// </summary>
    public static class PairingService
    {
        /// <summary>How long one reply may take before the exchange is abandoned.</summary>
        private const int ReplyTimeoutMs = 8000;

        /// <summary>
        /// The automatic attempt runs once per process: "the app just opened"
        /// means the first connect of a run, not every reconnect.
        /// </summary>
        private static bool _autoTried;

        /// <summary>True once the automatic attempt has been made in this process.</summary>
        public static bool AutoTried
        {
            get { return _autoTried; }
        }

        /// <summary>
        /// Offers a key this phone generates to the server it is about to talk
        /// to, when the phone has none of its own yet. This is what a first open
        /// does by itself: the code is asked of the server, so nothing has to be
        /// typed. It is called before the socket of the connection is opened, so
        /// the handshake that follows is the first frame written with the key the
        /// server just adopted. A server that is not waiting to be paired answers
        /// at once and leaves everything as it was, and the manual pair button
        /// still works when this one does not.
        /// </summary>
        public static async Task<bool> TryAutoPairAsync(string address, int port, string username)
        {
            if (_autoTried) return false;
            _autoTried = true;

            // A phone that already has a key of its own has nothing to offer:
            // the pairing replaces the key the server uses, not the one the
            // phone keeps, and a server that shares that key would lose it.
            if (!string.IsNullOrEmpty(SettingsService.BridgeKey)) return false;

            PairingResult result = await PairAsync(address, port, username, "");

            if (result.Paired)
            {
                Diag.Ok("auto-pair: the server adopted the key this phone generated ("
                    + address + ":" + port + ")");
            }
            else if (result.NoCode)
            {
                Diag.Ok("auto-pair: the server is not waiting to be paired ("
                    + address + ":" + port + ")");
            }
            // A failure has already been a Diag line inside PairAsync: there is
            // nothing more to say here, and the button remains for a retry.

            return result.Paired;
        }

        /// <summary>
        /// One pairing exchange with address:port. The code may be typed by hand;
        /// when it is empty the server is asked for the current one, and an
        /// expired code is replaced with a fresh one on the second turn rather
        /// than shown as a failure. It never throws.
        /// </summary>
        public static async Task<PairingResult> PairAsync(
            string address, int port, string username, string code)
        {
            var result = new PairingResult();

            StreamSocket socket = null;
            DataWriter writer = null;
            DataReader reader = null;
            try
            {
                // The outer frame is keyed the way this phone is configured now:
                // before pairing that is the server's default, which is exactly
                // what makes a key of our own reachable at this step.
                CryptoHelper.SetPassphrase(SettingsService.BridgeKey);

                socket = new StreamSocket();
                // The same deadline as every other connection of the app: a bare
                // ConnectAsync leaves the caller waiting on the TCP stack instead
                // of on the six seconds the rest of the app uses.
                await CommunicationService.ConnectWithDeadlineAsync(
                    socket, new HostName(address), port);

                writer = FrameCodec.CreateFrameWriter(socket.OutputStream);
                reader = FrameCodec.CreateFrameReader(socket.InputStream);

                // Two turns: if the code expired between the server sending it and
                // the pairing arriving, a fresh one is asked for and the pairing is
                // tried again, instead of reporting a failure.
                for (int attempt = 0; attempt < 2 && !result.Paired; attempt++)
                {
                    if (string.IsNullOrEmpty(code))
                    {
                        code = await AskCodeAsync(writer, reader, username);
                        if (string.IsNullOrEmpty(code))
                        {
                            result.NoCode = true;
                            break;
                        }
                        // Shown as the server sent it, so the user sees the code
                        // doing the pairing even though nobody typed it.
                        result.Code = code;
                    }

                    // The key is generated here, on the phone: it becomes the
                    // server's cipher, and nothing on the server can reproduce it.
                    // The token is not drawn here - the server derives it from this
                    // device id and hands it back in the `paired` frame.
                    string newKey = CryptoHelper.NewSecret();
                    string sealedPayload = CryptoHelper.SealWith(NormalizeCode(code),
                        "{\"BridgeKey\":\"" + newKey
                        + "\",\"SenderName\":\"" + JsonEscape(username) + "\"}");

                    var pair = new ChatMessage
                    {
                        Id = "pair",
                        Command = "pair",
                        PairingPayload = sealedPayload,
                        SenderId = SettingsService.DeviceId,
                        SenderName = username,
                        ChatId = "system",
                        Type = MessageType.System,
                        IsIncoming = false
                    };

                    await FrameCodec.WriteFrameAsync(writer,
                        CryptoHelper.Encrypt(Encoding.UTF8.GetBytes(pair.ToJson())));

                    ChatMessage reply = await ReadReplyAsync(reader);

                    if (reply != null && reply.Command == "paired")
                    {
                        // Adopted only now, on the server's word: the key is the one
                        // generated above, the token the one the server derived from
                        // this device id.
                        SettingsService.BridgeKey = newKey;
                        if (!string.IsNullOrEmpty(reply.Token)) SettingsService.Token = reply.Token;
                        result.Paired = true;
                        result.Message = reply.Text;
                    }
                    else
                    {
                        // The code may have run out: forget it and ask for a fresh
                        // one on the next turn. On the last turn the server's own
                        // words are what the caller shows.
                        code = "";
                        result.Code = "";
                        result.Message = reply != null && !string.IsNullOrEmpty(reply.Text)
                            ? reply.Text : "";
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("PairingService/pair", ex);
                result.Error = true;
                result.Message = ex.Message;
            }
            finally
            {
                DisposeSocket(socket, writer, reader);
            }

            return result;
        }

        /// <summary>
        /// Asks the server for the current pairing code: `pair.code` out, a
        /// `pair.info` frame back carrying the code and how long it stays valid.
        /// An empty answer is a server with no window open, not a failure.
        /// </summary>
        private static async Task<string> AskCodeAsync(
            DataWriter writer, DataReader reader, string username)
        {
            var ask = new ChatMessage
            {
                Id = "pair.code",
                Command = "pair.code",
                SenderId = SettingsService.DeviceId,
                SenderName = username,
                ChatId = "system",
                Type = MessageType.System,
                IsIncoming = false
            };

            await FrameCodec.WriteFrameAsync(writer,
                CryptoHelper.Encrypt(Encoding.UTF8.GetBytes(ask.ToJson())));

            ChatMessage reply = await ReadReplyAsync(reader);
            if (reply != null && reply.Command == "pair.info")
                return (reply.PairingCode ?? "").Trim();
            return "";
        }

        /// <summary>
        /// One frame awaited with a timeout. Null never comes back: a closed
        /// connection and a silent server are both raised as exceptions.
        /// </summary>
        private static async Task<ChatMessage> ReadReplyAsync(DataReader reader)
        {
            var readTask = FrameCodec.ReadFrameAsync(reader);
            if (await Task.WhenAny(readTask, Task.Delay(ReplyTimeoutMs)) != readTask)
                throw new TimeoutException("the server did not answer the pairing");

            byte[] payload = await readTask;
            if (payload == null) throw new InvalidOperationException("the server closed the connection");

            byte[] jsonBytes = CryptoHelper.Decrypt(payload);
            return ChatMessage.FromJson(Encoding.UTF8.GetString(jsonBytes, 0, jsonBytes.Length));
        }

        /// <summary>
        /// The code as both sides compare it: uppercase, with the dashes and
        /// spaces of the printed form removed. The server normalizes the same
        /// way, so a code typed with or without its dashes is the same code.
        /// </summary>
        private static string NormalizeCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            var clean = new StringBuilder(code.Length);
            foreach (char c in code.ToUpperInvariant())
            {
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) clean.Append(c);
            }
            return clean.ToString();
        }

        /// <summary>
        /// A string inside a JSON string. The name is the only value here that a
        /// person types, so the two characters that would end it early are the
        /// only ones escaped.
        /// </summary>
        private static string JsonEscape(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// The three objects of the one-shot pairing connection, closed in the
        /// right order. A failure here is not worth reporting: the socket is
        /// going away anyway.
        /// </summary>
        private static void DisposeSocket(StreamSocket socket, DataWriter writer, DataReader reader)
        {
            try { if (writer != null) { writer.DetachStream(); writer.Dispose(); } }
            catch (Exception) { }
            try { if (reader != null) { reader.DetachStream(); reader.Dispose(); } }
            catch (Exception) { }
            try { if (socket != null) socket.Dispose(); }
            catch (Exception) { }
        }
    }
}
