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
    /// The information of a profile as the adapter composes it (command
    /// `contact.info`). The field names are the wire ones: DataContractJson
    /// Serializer is case-sensitive, and the two sides must stay in agreement.
    /// </summary>
    [DataContract]
    public class ContactInfo
    {
        [DataMember]
        public string Name { get; set; }

        /// <summary>The "about" text (the WhatsApp status).</summary>
        [DataMember]
        public string About { get; set; }

        /// <summary>The readable number, for a person. Empty for a group.</summary>
        [DataMember]
        public string Number { get; set; }

        /// <summary>The profile picture in base64, if the adapter has it.</summary>
        [DataMember]
        public string AvatarData { get; set; }

        /// <summary>The business profile. Null when it is not a business account.</summary>
        [DataMember]
        public ContactBusiness Business { get; set; }

        /// <summary>Description and members. Null when it is not a group.</summary>
        [DataMember]
        public ContactGroup Group { get; set; }

        private static readonly DataContractJsonSerializer JsonSerializer =
            new DataContractJsonSerializer(typeof(ContactInfo));

        /// <summary>An unreadable frame is not a failure: it is "nothing to show".</summary>
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

        /// <summary>A single line to show: the day and the hours, or the day alone.</summary>
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

        /// <summary>The name if there is one, otherwise the number: an empty line serves no one.</summary>
        public string Display
        {
            get
            {
                if (!string.IsNullOrEmpty(Name)) return Name;
                return Number ?? "";
            }
        }

        /// <summary>The role, empty for a normal member.</summary>
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
