using System;

namespace WhatsappApp.Models
{
    /// <summary>
    /// An adapter seen on the network. The address is that of the beacon's
    /// sender, not the one written inside the beacon: the computer has several
    /// interfaces and only the sender is reachable from the phone.
    /// </summary>
    public class DiscoveredServer
    {
        /// <summary>
        /// The id of this server in the registry. It is what makes a server
        /// replace its own row instead of adding another, and it is empty for a
        /// server found on the LAN, which has no registry at all.
        /// </summary>
        public string Id { get; set; }
        public string Address { get; set; }
        public int Port { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
        public string AccountJid { get; set; }
        public DateTime LastSeen { get; set; }

        public DiscoveredServer()
        {
            Id = "";
            Address = "";
            Port = 0;
            Name = "";
            State = "";
            AccountJid = "";
            LastSeen = DateTime.Now;
        }

        /// <summary>Readable name: the computer host name, or the address.</summary>
        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Name) ? Address : Name; }
        }

        /// <summary>Address and port, shown below the name.</summary>
        public string Endpoint
        {
            get { return Address + ":" + Port; }
        }

        /// <summary>True when this adapter already has the account linked.</summary>
        public bool IsWhatsAppConnected
        {
            get { return State == "connected"; }
        }
    }
}
