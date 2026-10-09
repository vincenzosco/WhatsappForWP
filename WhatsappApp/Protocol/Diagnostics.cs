using System;

namespace WhatsappApp.Protocol
{
    /// <summary>
    /// Where the protocol core writes what it survives.
    ///
    /// The core is compiled in two places: inside the WP8.1 app and inside the
    /// desktop runner of `ProtocolTests`. It therefore cannot call `Diag` (a WinRT
    /// and app-local type): the app passes a Diag-backed implementation in, the
    /// runner passes <see cref="SilentLog"/>, and the core stays portable.
    /// </summary>
    public interface ILog
    {
        /// <summary>A step that worked, on a path worth tracing.</summary>
        void Info(string line);

        /// <summary>A failure the core decided to survive, with its site name.</summary>
        void Failure(string site, Exception error);
    }

    /// <summary>An <see cref="ILog"/> that says nothing, for the desktop runner.</summary>
    public sealed class SilentLog : ILog
    {
        public void Info(string line)
        {
        }

        public void Failure(string site, Exception error)
        {
        }
    }

    /// <summary>
    /// A byte stream that does not follow the WhatsApp binary format: an unknown
    /// tag, a length that runs past the buffer, a node that is not a node.
    ///
    /// The reader throws this instead of returning a default, because a default
    /// here is a node that decoded from a frame nobody understood - the kind of
    /// failure that surfaces three steps later as "WhatsApp refused the login".
    /// </summary>
    public class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message)
        {
        }

        public ProtocolException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
