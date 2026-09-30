using System.Runtime.Serialization;

namespace WhatsappApp.Models
{
    /// <summary>
    /// The UDP beacon of the adapter (WhatsappBridge/discovery.js).
    /// The [DataMember] names must stay identical to the JSON keys:
    /// DataContractJsonSerializer is case-sensitive and a field that does not
    /// match stays at its default with no error.
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
