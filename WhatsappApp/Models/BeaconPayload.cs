using System.Runtime.Serialization;

namespace WhatsappApp.Models
{
    /// <summary>
    /// Beacon UDP dell'adapter (WhatsappBridge/discovery.js).
    /// I [DataMember] devono restare identici alle chiavi del JSON:
    /// DataContractJsonSerializer e' case-sensitive e un campo che non combacia
    /// resta al valore di default senza nessun errore.
    /// </summary>
    [DataContract]
    public class BeaconPayload
    {
        [DataMember(Name = "service")]
        public string Service { get; set; }

        [DataMember(Name = "version")]
        public int Version { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "port")]
        public int Port { get; set; }

        [DataMember(Name = "state")]
        public string State { get; set; }

        [DataMember(Name = "account")]
        public string Account { get; set; }
    }
}
