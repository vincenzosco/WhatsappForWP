                // The reader carries the attempt id and its own reader with it: no
                // shared fields, no second reader on the same DataReader. It is
                // started BEFORE the answer is waited for, and this order is the
                // whole point: LastInboundUtc is written by this reader, so a wait
                // started before it would watch a field nothing updates.
#pragma warning disable 4014
                Task.Run(() => ListenForMessagesAsync(attempt, reader));
#pragma warning restore 4014

                // A socket that took the handshake is not a server that could read
                // it. The adapter drops a frame it has no key for and leaves the
                // connection open, which is how a phone whose storage had just been
                // emptied showed a connected app that was mute: every frame refused,
                // nothing refused to the app. The server's first frame is the only
                // proof that it read the ciphertext this phone wrote.
                DispatchOnUiThread(() =>
                {
                    RaiseConnectionStatusChanged(Loc.Get("CommService_Connected", "Connected to the server"));
                    RaiseConnectionEstablished();
                });

                return true;
            }

        /// <summary>
        /// How long the server's first frame after the handshake is waited for. The
        /// adapter answers `hello` with a state frame, and on a cold session it
        /// reads the account before answering: measured at about 15 s on 2026-10-07.
        /// A shorter wait closes connections that were about to work.
        /// </summary>
        private const int HandshakeAnswerMs = 20000;

        /// <summary>
        /// True when the server answered something after the handshake was sent.
        /// Until then the app knows only that the socket accepted bytes, not that
        /// anyone could read them, and calling that a connection is what made a
        /// wrong cipher key look like a server that does not answer.
        /// </summary>
        private async Task<bool> WaitForServerAnswerAsync(int attempt, DateTime sentAtUtc)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(HandshakeAnswerMs);
            while (DateTime.UtcNow < deadline)
            {
                // A frame read after the handshake went out is that answer.
                if (LastInboundUtc > sentAtUtc) return true;

                // A newer attempt took this one's place, or the connection went
                // away while waiting: either way this one is over.
                if (attempt != _connectionId) return false;
                if (!_isConnected) return false;

                await Task.Delay(200);
            }
            return false;
        }
            catch (Exception ex)
            {
                Diag.Failed("ConnectToServerAsync", ex);

                // Only the still-valid attempt can declare the failure: if a newer one
                // has started meanwhile, this is noise and its objects are closed
                // without touching the winning connection.
                DisposeSocket(socket, writer, reader);
                if (attempt == _connectionId)
                {
                    _isConnected = false;
                    DisposePublishedSocket();
                    if (!silent)
                    {
                        DispatchOnUiThread(() =>
                            RaiseErrorOccurred(string.Format(
                                Loc.Get("CommService_ConnectError", "Connection error: {0}"),
                                ExplainConnectionFailure(ex, "socket", Endpoint(address, port)))));
                    }
                }
                return false;
            }
        }
