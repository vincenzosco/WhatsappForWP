using System;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// A chat that moved address: the row keyed on a `@lid` and the row keyed on its phone
    /// number are the same conversation, and this says which one absorbed the other.
    ///
    /// Why the pages are told: the chat list, the preferences and the message collections all
    /// key a conversation on its JID. A merge the protocol layer does silently would leave the
    /// old row on screen with its unread count and its mute, next to the new one - the same
    /// duplication in the pages that the merge exists to end in the client.
    /// </summary>
    public sealed class ChatMerge : EventArgs
    {
        /// <summary>The address that is no longer used (`...@lid`).</summary>
        public string From;

        /// <summary>The address that keeps the conversation (`...@s.whatsapp.net`).</summary>
        public string To;

        public ChatMerge(string from, string to)
        {
            From = from;
            To = to;
        }
    }
}
