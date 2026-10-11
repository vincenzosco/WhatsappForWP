using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The login that follows a pairing, as far as it can be checked without WhatsApp.
    ///
    /// The part that matters - whether the server accepts the account back - is not here:
    /// it needs the real endpoint, and the run that decides it is the probe (`--connect`)
    /// and the phone. What is here is the half that is a rule: what counts as the server
    /// saying no, and that a device with no account is refused before anything is dialled
    /// rather than half-way through a handshake.
    /// </summary>
    public static class NativeSessionTests
    {
        [Test]
        public static void A_failure_node_is_a_refusal_with_its_reason()
        {
            var failure = new BinaryNode("failure");
            failure.Attrs.Add("reason", "401");
            failure.Attrs.Add("location", "frc");

            string refusal = NativeSession.Refusal(failure);

            Assert.IsTrue(refusal != null, "a failure is a refusal");
            Assert.IsTrue(refusal.Contains("401"), "the reason has to be the answer, not the shape");
        }

        [Test]
        public static void A_stream_error_is_a_refusal_with_its_code()
        {
            var error = new BinaryNode("stream:error");
            error.Attrs.Add("code", "515");

            string refusal = NativeSession.Refusal(error);

            Assert.IsTrue(refusal != null, "a stream error is a refusal");
            Assert.IsTrue(refusal.Contains("515"), "the code has to be the answer");
        }

        [Test]
        public static void A_failure_with_no_reason_is_still_a_refusal()
        {
            // The whole node and not null: the sentence is the diagnostic, and a login
            // loop that carried on over a bare failure would read frames nobody wants.
            string refusal = NativeSession.Refusal(new BinaryNode("failure"));

            Assert.IsTrue(refusal != null, "a failure with no attributes is still a failure");
        }

        [Test]
        public static void Success_and_ordinary_nodes_are_not_refusals()
        {
            Assert.IsTrue(NativeSession.Refusal(new BinaryNode("success")) == null, "success is the login working");

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("type", "get");
            Assert.IsTrue(NativeSession.Refusal(iq) == null, "an iq is the server working, not the server refusing");
        }

        [Test]
        public static void A_login_with_no_account_is_refused_before_the_transport()
        {
            var transport = new UntouchedTransport();
            var session = new NativeSession(transport, new SilentLog(), new Counter());

            // No JID: these are the credentials of a device that has never paired, and the
            // payload they would buy is the pairing one - a second registration, refused by
            // the server much further away from here.
            Credentials fresh = Credentials.Create(new Counter());

            Assert.Throws<ProtocolException>(delegate
            {
                session.LoginAsync(fresh, WhatsAppSocket.DefaultUrl).GetAwaiter().GetResult();
            });

            Assert.IsFalse(transport.Connected, "a login with no account must not dial");
        }

        [Test]
        public static void A_session_that_is_not_open_cannot_send()
        {
            var session = new NativeSession(new UntouchedTransport(), new SilentLog(), new Counter());

            Assert.Throws<ProtocolException>(delegate { session.PingAsync(); });
            Assert.IsFalse(session.IsOpen, "a login was never run on this session");
        }

        /// <summary>A transport that remembers being dialled: the assertion is that it is not.</summary>
        private sealed class UntouchedTransport : ITransport
        {
            public bool Connected;

            public Task ConnectAsync(string url)
            {
                Connected = true;
                return Done();
            }

            public Task SendAsync(byte[] payload)
            {
                throw new AssertionException("nothing may be sent on a session with no account");
            }

            public Task<byte[]> ReceiveAsync()
            {
                throw new AssertionException("nothing may be read on a session with no account");
            }

            public void Close()
            {
            }

            private static Task Done()
            {
                var completion = new TaskCompletionSource<bool>();
                completion.SetResult(true);
                return completion.Task;
            }
        }

        /// <summary>Deterministic bytes: a key pair that parses, the same one every run.</summary>
        private sealed class Counter : IRandomSource
        {
            private byte _next = 1;

            public byte[] Bytes(int count)
            {
                var bytes = new byte[count];
                for (int i = 0; i < count; i++) bytes[i] = _next++;

                return bytes;
            }
        }
    }
}
