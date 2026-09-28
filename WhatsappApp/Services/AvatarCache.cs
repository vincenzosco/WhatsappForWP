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
    /// <summary>Il file su disco: un'immagine per chat.</summary>
    [DataContract]
    internal class AvatarCacheEntry
    {
        [DataMember]
        public string ChatId { get; set; }

        [DataMember]
        public string Data { get; set; }
    }

    /// <summary>Il file intero: l'elenco delle immagini.</summary>
    [DataContract]
    internal class AvatarCacheFile
    {
        [DataMember]
        public List<AvatarCacheEntry> Chats { get; set; }
    }

    /// <summary>
    /// Le immagini del profilo che l'adapter ha mandato, tenute sul telefono.
    ///
    /// Perche' esiste: l'elenco chat arriva con l'immagine di ogni
    /// conversazione, e l'app la decodifica e la disegna. Ma i byte non
    /// sopravvivevano a niente - la copia dell'elenco (ChatCache) li lascia
    /// fuori di proposito, perche' e' il file che si legge prima che la
    /// connessione esista e deve restare piccolo, e la copia decodificata la
    /// butta via MemoryWatcher sotto pressione. Risultato: dopo un riavvio, o
    /// dopo un picco di memoria, la lista mostra le iniziali finche' l'adapter
    /// non rimanda ogni riga, cioe' due richieste HTTP per chat (l'indirizzo
    /// dell'immagine, e poi i byte dal CDN).
    ///
    /// Qui i byte si tengono. Non e' una verita': e' una cache, e la riga che
    /// arriva dal server la sostituisce appena arriva (vedi ApplyChat).
    ///
    /// Tetti, perche' quello che non ha un tetto cresce: MaxChats chat (una in
    /// piu' di CHATS_LIMIT non verrebbe mai disegnata), MaxEntryChars per una
    /// singola immagine e MaxTotalChars per il file. Chi non entra non si
    /// tiene, e la riga torna alle iniziali - che e' quello che faceva prima.
    /// </summary>
    public static class AvatarCache
    {
        private const string FileName = "avatar-cache.json";

        /// <summary>Quante chat si tengono.</summary>
        public const int MaxChats = 40;

        /// <summary>Il tetto di una singola immagine, in caratteri base64 (~110 KB).</summary>
        public const int MaxEntryChars = 150000;

        /// <summary>Il tetto di tutto il file, in caratteri base64 (~1,1 MB).</summary>
        public const int MaxTotalChars = 1500000;

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(AvatarCacheFile));

        /// <summary>ChatId -> immagine in base64.</summary>
        private static readonly Dictionary<string, string> Known =
            new Dictionary<string, string>();

        /// <summary>L'ordine in cui le chat sono entrate: da qui esce chi e' di troppo.</summary>
        private static readonly List<string> Order = new List<string>();

        /// <summary>Le chat la cui immagine e' troppo grande: si dice una volta sola.</summary>
        private static readonly HashSet<string> TooBig = new HashSet<string>();

        /// <summary>I caratteri che Known occupa adesso: sommarli a ogni controllo costerebbe.</summary>
        private static long _chars;

        /// <summary>
        /// Il lucchetto di Known, Order, TooBig e _chars. Serve perche' Remember
        /// lo chiama il thread UI (ApplyChat) e la scrittura la esegue la coda.
        /// </summary>
        private static readonly object Gate = new object();

        /// <summary>Le scritture del file, una alla volta e in ordine.</summary>
        private static readonly SerialQueue Writes = new SerialQueue();

        /// <summary>Cresce a ogni cambiamento: dice se c'e' qualcosa da scrivere.</summary>
        private static int _version;

        /// <summary>L'ultima versione arrivata sul disco.</summary>
        private static int _written;

        private static bool _loaded;

        /// <summary>Quante immagini si tengono adesso. Solo per la diagnosi.</summary>
        public static int Count
        {
            get { lock (Gate) { return Known.Count; } }
        }

        /// <summary>
        /// Legge il file una volta sola, e va aspettata prima di applicare le
        /// righe salvate: senza, quelle righe non trovano nessuna immagine.
        /// Mai un'eccezione: al primo avvio il file non c'e'.
        /// </summary>
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
                    var cache = Serializer.ReadObject(stream) as AvatarCacheFile;
                    if (cache == null || cache.Chats == null) return;

                    lock (Gate)
                    {
                        for (int i = 0; i < cache.Chats.Count; i++)
                        {
                            var entry = cache.Chats[i];
                            if (entry == null) continue;
                            if (string.IsNullOrEmpty(entry.ChatId)) continue;
                            if (string.IsNullOrEmpty(entry.Data)) continue;
                            if (Known.ContainsKey(entry.ChatId)) continue;

                            Known[entry.ChatId] = entry.Data;
                            Order.Add(entry.ChatId);
                            _chars += entry.Data.Length;
                        }

                        Evict();

                        // Quello che si e' letto e' gia' sul disco: non c'e'
                        // niente da riscrivere.
                        _written = _version;
                    }
                }
            }
            catch (Exception ex)
            {
                // Primo avvio, o file scritto da una versione diversa.
                Diag.Failed("AvatarCache.Load", ex);
            }
        }

        /// <summary>L'immagine tenuta per questa chat, o null. Dopo LoadAsync.</summary>
        public static string Get(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return null;

            lock (Gate)
            {
                string data;
                return Known.TryGetValue(chatId, out data) ? data : null;
            }
        }

        /// <summary>
        /// Tiene l'immagine di una chat. La chiama ApplyChat a ogni riga che ne
        /// porta una: se sono gli stessi byte di prima non si fa niente, e il
        /// server le rimanda uguali a ogni elenco, quindi un aggiornamento non
        /// riscrive il file.
        /// </summary>
        public static void Remember(string chatId, string base64)
        {
            if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(base64)) return;

            bool changed = false;
            lock (Gate)
            {
                if (base64.Length > MaxEntryChars)
                {
                    if (TooBig.Add(chatId))
                    {
                        Diag.Ok("avatar-cache: image of " + chatId + " is " + base64.Length +
                            " chars, over the " + MaxEntryChars + " limit, not kept");
                    }
                    return;
                }

                string current;
                if (Known.TryGetValue(chatId, out current) && current == base64) return;

                if (current != null)
                {
                    _chars -= current.Length;
                    Order.Remove(chatId);
                }

                Known[chatId] = base64;
                Order.Add(chatId);
                _chars += base64.Length;
                Evict();

                _version++;
                changed = true;
            }

            if (changed) Save();
        }

        /// <summary>
        /// Tiene il file dentro i tetti: esce la chat entrata per prima. Va
        /// chiamata sotto Gate, perche' tocca Known, Order e _chars insieme.
        /// </summary>
        private static void Evict()
        {
            while (Order.Count > MaxChats || _chars > MaxTotalChars)
            {
                if (Order.Count == 0) return;

                string oldest = Order[0];
                Order.RemoveAt(0);

                string data;
                if (Known.TryGetValue(oldest, out data)) _chars -= data.Length;
                Known.Remove(oldest);
            }
        }

        /// <summary>
        /// Mette una scrittura in coda. Lo scatto si prende dentro la coda,
        /// non qui: venti righe che arrivano insieme - un elenco chat - con
        /// questa forma scrivono il file una volta sola, e serializzare sta
        /// fuori dal lucchetto, perche' chi chiama Remember e' il thread UI e
        /// un megabyte di JSON non e' roba da tenergli in mano.
        /// </summary>
        private static void Save()
        {
#pragma warning disable 4014
            Writes.RunAsync(delegate { return WriteIfChangedAsync(); });
#pragma warning restore 4014
        }

        /// <summary>Scrive se qualcosa e' cambiato da quando e' stato scritto l'ultima volta.</summary>
        private static async Task WriteIfChangedAsync()
        {
            int version;
            List<AvatarCacheEntry> snapshot;

            lock (Gate)
            {
                if (_version == _written) return;

                version = _version;
                snapshot = new List<AvatarCacheEntry>();
                for (int i = 0; i < Order.Count; i++)
                {
                    snapshot.Add(new AvatarCacheEntry
                    {
                        ChatId = Order[i],
                        Data = Known[Order[i]]
                    });
                }
            }

            if (!await WriteFileAsync(Serialize(snapshot))) return;

            lock (Gate)
            {
                if (_written < version) _written = version;
            }
        }

        /// <summary>Le voci in JSON.</summary>
        private static string Serialize(List<AvatarCacheEntry> entries)
        {
            using (var stream = new MemoryStream())
            {
                Serializer.WriteObject(stream, new AvatarCacheFile { Chats = entries });
                return Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
            }
        }

        /// <summary>
        /// Scrive il file. Non aspetta nessuno - chi guarda l'elenco chat non
        /// ha niente a che fare con l'esito - quindi cattura da sola: un
        /// deposito senza padrone non deve poter far cadere la pagina. Dice se
        /// e' andata bene, perche' solo allora la versione e' sul disco.
        /// </summary>
        private static async Task<bool> WriteFileAsync(string json)
        {
            try
            {
                StorageFile storage = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(storage, json);
                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("AvatarCache.Save", ex);
                return false;
            }
        }
    }
}
