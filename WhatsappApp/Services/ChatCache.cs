using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>Il file su disco: un solo campo, l'elenco delle righe.</summary>
    [DataContract]
    internal class ChatCacheFile
    {
        [DataMember]
        public List<ChatMessage> Chats { get; set; }
    }

    /// <summary>
    /// L'elenco chat dell'ultima sessione, tenuto sul telefono.
    ///
    /// Perche' esiste: all'avvio la connessione non c'e' ancora, e l'adapter
    /// risponde alla richiesta dell'elenco solo quando WhatsApp e' collegato.
    /// Senza questa copia l'app si apre su un elenco vuoto per i secondi che
    /// servono, e sembra che non sia successo niente.
    ///
    /// Non e' una verita': e' una fotografia. Appena il server risponde, ogni
    /// riga viene sostituita da quella vera (vedi DataService.ApplyChat). Senza
    /// i byte dell'avatar la copia resta piccola, e l'immagine arriva con il
    /// primo aggiornamento.
    /// </summary>
    public static class ChatCache
    {
        private const string FileName = "chats.json";

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(ChatCacheFile));

        /// <summary>Le righe salvate. Mai un'eccezione: al primo avvio non c'e' file.</summary>
        public static async Task<List<ChatMessage>> LoadAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                string json = await FileIO.ReadTextAsync(file);
                if (!string.IsNullOrEmpty(json))
                {
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    {
                        var cache = Serializer.ReadObject(stream) as ChatCacheFile;
                        if (cache != null && cache.Chats != null) return cache.Chats;
                    }
                }
            }
            catch (Exception ex)
            {
                // Primo avvio, o cache scritta da una versione diversa.
                Diag.Failed("ChatCache.Load", ex);
            }
            return new List<ChatMessage>();
        }

        /// <summary>
        /// Scrive quello che il server ha appena mandato. Senza i byte
        /// dell'avatar: quelli si rifanno a ogni connessione, e un file da
        /// megabyte per un elenco che serve solo a riempire i primi secondi non
        /// e' un buon cambio.
        /// </summary>
        public static async Task SaveAsync(List<ChatMessage> chats)
        {
            if (chats == null || chats.Count == 0) return;

            try
            {
                var slim = new List<ChatMessage>();
                for (int i = 0; i < chats.Count; i++) slim.Add(Slim(chats[i]));

                string json;
                using (var stream = new MemoryStream())
                {
                    Serializer.WriteObject(stream, new ChatCacheFile { Chats = slim });
                    json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                // Una cache che non si scrive non e' un guasto da mostrare:
                // l'elenco arriva comunque dal server.
                Diag.Failed("ChatCache.Save", ex);
            }
        }

        /// <summary>La copia di una riga con i soli campi che l'elenco disegna.</summary>
        private static ChatMessage Slim(ChatMessage row)
        {
            return new ChatMessage
            {
                ChatId = row.ChatId,
                SenderName = row.SenderName,
                Text = row.Text,
                IsGroup = row.IsGroup,
                UnreadCount = row.UnreadCount,
                Timestamp = row.Timestamp
            };
        }
    }
}
