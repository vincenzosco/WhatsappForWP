using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>Quello che questo telefono ha deciso di una chat.</summary>
    [DataContract]
    internal class ChatPreference
    {
        [DataMember]
        public string ChatId { get; set; }

        [DataMember]
        public bool Pinned { get; set; }

        [DataMember]
        public bool Muted { get; set; }

        [DataMember]
        public bool Hidden { get; set; }
    }

    /// <summary>Il file su disco: un solo campo, l'elenco delle decisioni.</summary>
    [DataContract]
    internal class ChatPreferenceFile
    {
        [DataMember]
        public List<ChatPreference> Chats { get; set; }
    }

    /// <summary>
    /// Pinnare, silenziare ed eliminare una chat sono decisioni di questo
    /// telefono, non di WhatsApp: GOWA non ha nessun endpoint per nessuna delle
    /// tre, e l'elenco delle conversazioni che l'adapter manda e' di sola
    /// lettura.
    ///
    /// Perche' non nella cache dell'elenco (ChatCache): quella e' una fotografia
    /// che il server sostituisce riga per riga, e una chat che in quell'elenco
    /// non compare piu' (vecchia, o tagliata fuori da CHATS_LIMIT) perderebbe il
    /// suo pin. Qui invece sopravvive al server.
    /// </summary>
    public static class ChatPreferences
    {
        private const string FileName = "chat-preferences.json";

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(ChatPreferenceFile));

        // Snapshot in memoria: l'elenco chat chiede queste risposte per ogni
        // riga, e leggerle dal disco ogni volta sarebbe un file per messaggio.
        private static readonly Dictionary<string, ChatPreference> Known =
            new Dictionary<string, ChatPreference>();

        private static bool _loaded;

        /// <summary>Vero quando il file e' stato letto. Solo per la diagnosi.</summary>
        public static bool IsLoaded
        {
            get { return _loaded; }
        }

        /// <summary>Legge il file una volta sola. Mai un'eccezione: al primo avvio non c'e'.</summary>
        public static async Task LoadAsync()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                string json = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrEmpty(json)) return;

                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var known = Serializer.ReadObject(stream) as ChatPreferenceFile;
                    if (known == null || known.Chats == null) return;

                    for (int i = 0; i < known.Chats.Count; i++)
                    {
                        var entry = known.Chats[i];
                        if (entry == null || string.IsNullOrEmpty(entry.ChatId)) continue;
                        Known[entry.ChatId] = entry;
                    }
                }
            }
            catch (Exception ex)
            {
                // Primo avvio, o file scritto da una versione diversa.
                Diag.Failed("ChatPreferences.Load", ex);
            }
        }

        public static bool IsPinned(string chatId)
        {
            var entry = Find(chatId);
            return entry != null && entry.Pinned;
        }

        public static bool IsMuted(string chatId)
        {
            var entry = Find(chatId);
            return entry != null && entry.Muted;
        }

        public static bool IsHidden(string chatId)
        {
            var entry = Find(chatId);
            return entry != null && entry.Hidden;
        }

        public static void SetPinned(string chatId, bool pinned)
        {
            var entry = EntryFor(chatId);
            if (entry == null) return;
            entry.Pinned = pinned;
            Forget(chatId, entry);
            Save();
        }

        public static void SetMuted(string chatId, bool muted)
        {
            var entry = EntryFor(chatId);
            if (entry == null) return;
            entry.Muted = muted;
            Forget(chatId, entry);
            Save();
        }

        /// <summary>
        /// La chat e' stata eliminata da questo telefono: resta fuori
        /// dall'elenco anche quando il server la rimanda. Non e' la fine della
        /// conversazione - Reveal la fa tornare, e la chiama un messaggio nuovo,
        /// come fa WhatsApp.
        /// </summary>
        public static void Hide(string chatId)
        {
            var entry = EntryFor(chatId);
            if (entry == null) return;
            entry.Hidden = true;
            Save();
        }

        /// <summary>La chat torna nell'elenco. Pin e silenzio restano come erano.</summary>
        public static void Reveal(string chatId)
        {
            var entry = Find(chatId);
            if (entry == null || !entry.Hidden) return;
            entry.Hidden = false;
            Forget(chatId, entry);
            Save();
        }

        private static ChatPreference Find(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return null;

            ChatPreference entry;
            return Known.TryGetValue(chatId, out entry) ? entry : null;
        }

        private static ChatPreference EntryFor(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return null;

            var entry = Find(chatId);
            if (entry != null) return entry;

            entry = new ChatPreference { ChatId = chatId };
            Known[chatId] = entry;
            return entry;
        }

        /// <summary>
        /// Una voce senza niente da dire non si tiene: un file di righe tutte a
        /// false cresce con l'elenco delle conversazioni, che cambia da solo.
        /// </summary>
        private static void Forget(string chatId, ChatPreference entry)
        {
            if (entry.Pinned || entry.Muted || entry.Hidden) return;
            Known.Remove(chatId);
        }

        private static void Save()
        {
            var file = new ChatPreferenceFile { Chats = new List<ChatPreference>() };
            foreach (var entry in Known.Values) file.Chats.Add(entry);

#pragma warning disable 4014
            WriteAsync(file);
#pragma warning restore 4014
        }

        /// <summary>
        /// Scrive il file. Non aspetta nessuno - chi cambia un pin non ha niente
        /// da fare con l'esito - quindi cattura da sola: un deposito senza
        /// padrone non deve poter far cadere la pagina. Mai un'eccezione.
        /// </summary>
        private static async Task WriteAsync(ChatPreferenceFile file)
        {
            try
            {
                string json;
                using (var stream = new MemoryStream())
                {
                    Serializer.WriteObject(stream, file);
                    json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }

                StorageFile storage = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(storage, json);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPreferences.Save", ex);
            }
        }
    }
}
