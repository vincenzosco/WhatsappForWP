using System;
using WhatsappApp.Protocol;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Sends the protocol code's log lines into this app's log.
    ///
    /// The protocol is a separate library that only knows an interface, `ILog`: it can say
    /// "here is a line" or "here is a failure", and nothing more. It cannot call `Diag`
    /// directly, because the same library is also compiled into a desktop test runner where
    /// `Diag` (which uses phone APIs) does not exist.
    ///
    /// This class is the adapter: it implements `ILog` and forwards both calls to `Diag`, so a
    /// handshake or a pairing that goes wrong leaves the same `DIAG` lines as everything else.
    /// </summary>
    public sealed class ProtocolLog : ILog
    {
        /// <summary>A normal message from the protocol.</summary>
        public void Info(string line)
        {
            Diag.Ok(line);
        }

        /// <summary>A failure, with the place it happened and the exception.</summary>
        public void Failure(string site, Exception error)
        {
            Diag.Failed(site, error);
        }
    }
}
