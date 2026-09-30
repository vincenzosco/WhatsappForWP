using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using WhatsappApp.Services;

namespace WhatsappApp.Models
{
    /// <summary>
    /// Le informazioni di un profilo come le compone l'adapter (comando
    /// `contact.info`). I nomi dei campi sono quelli del filo: DataContractJson
    /// Serializer e' case-sensitive, e i due capi devono restare d'accordo.
    /// </summary>
    [DataContract]
    public class ContactInfo
    {
        [DataMember]
        public string Name { get; set; }

        /// <summary>Il testo "about" (lo status di WhatsApp).</summary>
        [DataMember]
        public string About { get; set; }

        /// <summary>Il numero leggibile, per una persona. Vuoto per un gruppo.</summary>
        [DataMember]
        public string Number { get; set; }

        /// <summary>L'immagine del profilo in base64, se l'adapter ce l'ha.</summary>
        [DataMember]
        public string AvatarData { get; set; }

        /// <summary>Il profilo aziendale. Null quando non e' un account business.</summary>
        [DataMember]
        public ContactBusiness Business { get; set; }

        /// <summary>Descrizione e membri. Null quando non e' un gruppo.</summary>
        [DataMember]
        public ContactGroup Group { get; set; }

        private static readonly DataContractJsonSerializer JsonSerializer =
            new DataContractJsonSerializer(typeof(ContactInfo));

        /// <summary>Un frame illeggibile non e' un guasto: e' "niente da mostrare".</summary>
        public static ContactInfo FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return (ContactInfo)JsonSerializer.ReadObject(ms);
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("ContactInfo.FromJson", ex);
                return null;
            }
        }
    }

    [DataContract]
    public class ContactBusiness
    {
        [DataMember]
        public string Email { get; set; }

        [DataMember]
        public string Address { get; set; }

        [DataMember]
        public List<string> Categories { get; set; }

        [DataMember]
        public string Timezone { get; set; }

        [DataMember]
        public List<ContactHours> Hours { get; set; }
    }

    [DataContract]
    public class ContactHours
    {
        [DataMember]
        public string Day { get; set; }

        [DataMember]
        public string Mode { get; set; }

        [DataMember]
        public string Open { get; set; }

        [DataMember]
        public string Close { get; set; }

        /// <summary>Una riga sola da mostrare: il giorno e la fascia, o solo il giorno.</summary>
        public string Display
        {
            get
            {
                if (string.IsNullOrEmpty(Open)) return Day ?? "";
                return (Day ?? "") + "  " + Open + " - " + Close;
            }
        }
    }

    [DataContract]
    public class ContactGroup
    {
        [DataMember]
        public string Description { get; set; }

        [DataMember]
        public List<ContactMember> Members { get; set; }
    }

    [DataContract]
    public class ContactMember
    {
        [DataMember]
        public string Jid { get; set; }

        [DataMember]
        public string Number { get; set; }

        [DataMember]
        public string Name { get; set; }

        [DataMember]
        public bool IsAdmin { get; set; }

        [DataMember]
        public bool IsSuperAdmin { get; set; }

        /// <summary>Il nome se c'e', altrimenti il numero: una riga vuota non serve a nessuno.</summary>
        public string Display
        {
            get
            {
                if (!string.IsNullOrEmpty(Name)) return Name;
                return Number ?? "";
            }
        }

        /// <summary>Il ruolo, vuoto per un membro normale.</summary>
        public string Role
        {
            get
            {
                if (IsSuperAdmin) return Loc.Get("ContactInfoPage_SuperAdmin", "Super admin");
                if (IsAdmin) return Loc.Get("ContactInfoPage_Admin", "Admin");
                return "";
            }
        }

        public bool IsRoleVisible
        {
            get { return IsAdmin || IsSuperAdmin; }
        }
    }
}
