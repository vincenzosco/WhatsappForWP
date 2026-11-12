using System;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// Says that one conversation changed address, and which address absorbed the other.
    ///
    /// WhatsApp can know the same person by two addresses: a `@lid` (a privacy identifier) and
    /// a phone number. They are one conversation, not two. The client merges them, and this
    /// object is the announcement of that merge.
    ///
    /// The pages need to hear it: the chat list, the per-chat settings and the messages are all
    /// stored under the address, so a merge done silently would leave the old row on screen -
    /// with its unread count and its mute - next to the new one.
    ///
    /// It extends `EventArgs` so it can be passed to a normal .NET event.
    /// </summary>
    public sealed class ChatMerge : EventArgs
    {
        /// <summary>The address that is no longer used. It ends with `.@lid`.</summary>
        public string From;

        /// <summary>The address that keeps the conversation. It ends with `.@s.whatsapp.net`.</summary>
        public string To;

        public ChatMerge(string from, string to)
        {
            From = from;
            To = to;
        }
    }
}
