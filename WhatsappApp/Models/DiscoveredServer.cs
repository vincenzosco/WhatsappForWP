using System;

namespace WhatsappApp.Models
{
    /// <summary>
    /// Un adapter visto sulla rete. L'indirizzo e' quello del mittente del
    /// beacon, non quello scritto dentro il beacon: il computer ha piu'
    /// interfacce e solo il mittente e' raggiungibile dal telefono.
    /// </summary>
    public class DiscoveredServer
    {
        public string Address { get; set; }
        public int Port { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
        public string AccountJid { get; set; }
        public DateTime LastSeen { get; set; }

        public DiscoveredServer()
        {
            Address = "";
            Port = 0;
            Name = "";
            State = "";
            AccountJid = "";
            LastSeen = DateTime.Now;
        }

        /// <summary>Nome leggibile: l'hostname del computer, o l'indirizzo.</summary>
        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Name) ? Address : Name; }
        }

        /// <summary>Indirizzo e porta, mostrati sotto il nome.</summary>
        public string Endpoint
        {
            get { return Address + ":" + Port; }
        }

        /// <summary>True quando questo adapter ha gia' l'account collegato.</summary>
        public bool IsWhatsAppConnected
        {
            get { return State == "connected"; }
        }
    }
}
