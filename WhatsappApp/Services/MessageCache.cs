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
    /// <summary>Il file su disco: un solo campo, i messaggi di una chat.</summary>
    [DataContract]
    internal class MessageCacheFile
    {
        [DataMember]
        public List<ChatMessage> Messages { get; set; }
    }

    /// <summary>
    /// Gli ultimi messaggi di una conversazione, tenuti sul telefono.
    ///
    /// Perche' esiste: aprendo una chat l'elenco partiva vuoto e aspettava la
    /// cronologia dal server, che e' l'unica cosa che il telefono non puo'
    /// affrettare. Con questa copia la conversazione si vede subito, e i
    /// messaggi veri la sostituiscono quando arrivano.
    ///
    /// E' una fotografia, non una verita': si tiene solo l'ultima parte della
    /// conversazione, e si riscrive uscendo dalla chat (non a ogni messaggio,
    /// che scriverebbe un file a raffica).
    /// </summary>
    public static class MessageCache
    {
        /// <summary>Quanti messaggi si tengono per chat: abbastanza per riempire
        /// lo schermo, non abbastanza per pesare.</summary>
        private const int Keep = 60;

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(MessageCacheFile));

        /// <summary>I messaggi salvati. Mai un'eccezione: la prima volta non c'e' file.</summary>
        public static async Task<List<ChatMessage>> LoadAsync(string chatId)
        {
            var empty = new List<ChatMessage>();
            if (string.IsNullOrEmpty(chatId)) return empty;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileNameFor(chatId));
                string json = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrEmpty(json)) return empty;

                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var cache = Serializer.ReadObject(stream) as MessageCacheFile;
                    if (cache != null && cache.Messages != null) return cache.Messages;
                }
            }
            catch (Exception ex)
            {
                // Prima apertura, o cache scritta da una versione diversa.
                Diag.Failed("MessageCache.Load", ex);
            }
            return empty;
        }

        /// <summary>Scrive la coda della conversazione. Mai un'eccezione.</summary>
        public static async Task SaveAsync(string chatId, IList<ChatMessage> messages)
        {
            if (string.IsNullOrEmpty(chatId) || messages == null || messages.Count == 0) return;

            try
            {
                var slim = new List<ChatMessage>();
                int from = messages.Count > Keep ? messages.Count - Keep : 0;
                for (int i = from; i < messages.Count; i++) slim.Add(Slim(messages[i]));

                string json;
                using (var stream = new MemoryStream())
                {
                    Serializer.WriteObject(stream, new MessageCacheFile { Messages = slim });
                    json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileNameFor(chatId), CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                // Una cache che non si scrive non e' un guasto da mostrare:
                // la cronologia arriva comunque dal server.
                Diag.Failed("MessageCache.Save", ex);
            }
        }

        /// <summary>
        /// Butta la copia di una chat. La chiama l'eliminazione: senza questa, la
        /// conversazione cancellata tornerebbe a schermo al primo apri-e-chiudi,
        /// perche' DataService legge la cache quando la lista in memoria e'
        /// vuota. Mai un'eccezione: un file che non c'e' non e' un guasto.
        /// </summary>
        public static async Task DeleteAsync(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileNameFor(chatId));
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception ex)
            {
                Diag.Failed("MessageCache.Delete", ex);
            }
        }

        /// <summary>
        /// La copia di un messaggio senza i byte del media: quelli riempirebbero
        /// il file, e per un video non ci starebbero nemmeno. Marca IsHistory,
        /// cosi' un messaggio pescato dalla cache non conta come non letto e non
        /// alza un avviso, esattamente come la cronologia vera.
        /// </summary>
        private static ChatMessage Slim(ChatMessage message)
        {
            return new ChatMessage
            {
                Id = message.Id,
                Text = message.Text,
                SenderId = message.SenderId,
                SenderName = message.SenderName,
                ChatId = message.ChatId,
                Timestamp = message.Timestamp,
                Status = message.Status,
                Type = message.Type,
                IsIncoming = message.IsIncoming,
                MediaMimeType = message.MediaMimeType,
                MediaFileName = message.MediaFileName,
                MediaType = message.MediaType,
                IsHistory = true
            };
        }

        /// <summary>Un nome di file per chat: l'id ripulito dai caratteri che un
        /// nome di file non accetta.</summary>
        private static string FileNameFor(string chatId)
        {
            var builder = new StringBuilder("messages_");
            for (int i = 0; i < chatId.Length; i++)
            {
                char c = chatId[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '-' || c == '_')
                {
                    builder.Append(c);
                }
            }
            builder.Append(".json");
            return builder.ToString();
        }
    }
}
