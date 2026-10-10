using System;
using WhatsappApp.Protocol;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The protocol core's log, pointed at this app's.
    ///
    /// The core writes through `ILog` and cannot call `Diag` itself: it is compiled into
    /// the desktop runner as well, where `Diag` - a WinRT and app-local type - does not
    /// exist. So the app hands it this, and a pairing or a handshake that goes wrong
    /// leaves the same `DIAG` lines in the log as everything else the app does.
    /// </summary>
    public sealed class ProtocolLog : ILog
    {
        public void Info(string line)
        {
            Diag.Ok(line);
        }

        public void Failure(string site, Exception error)
        {
            Diag.Failed(site, error);
        }
    }
}
