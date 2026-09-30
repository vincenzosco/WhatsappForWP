using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Gli avvisi che questa app puo' dare mentre gira: un toast e il numero
    /// sull'icona. Non c'e' nessun servizio cloud dietro, quindi un messaggio
    /// che arriva con l'app sospesa non produce niente: la connessione TCP e'
    /// dell'app, e WP8.1 la chiude quando la sospende. Le notifiche push vere
    /// richiederebbero un servizio esterno che questo progetto non ha.
    ///
    /// Ogni chiamata e' protetta: un telefono che rifiuta il toast non deve
    /// far cadere la ricezione del messaggio.
    /// </summary>
    public static class NotificationService
    {
        /// <summary>
        /// Un messaggio arrivato da una persona, in una chat che non e' quella
        /// aperta: la tile ruota su di lei, con la sua foto e il suo nome.
        ///
        /// Solo le persone: un gruppo non ha una faccia sola, e mettere la
        /// foto di un gruppo sulla tile direbbe meno di niente. Il nome del
        /// gruppo, senza foto, lo lascia fuori anche lui.
        ///
        /// Il filesystem non si puo' toccare da qui (siamo sul thread UI che
        /// ha appena ricevuto il messaggio), quindi l'aggiornamento parte e va
        /// avanti per conto suo.
        /// </summary>
        public static void RotateSenderTile(string chatId, string name, string avatarData)
        {
            if (!SettingsService.NotificationsEnabled) return;
            if (string.IsNullOrEmpty(chatId)) return;
            if (chatId.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)) return;

#pragma warning disable 4014
            UpdateSenderTileAsync(chatId, name, avatarData);
#pragma warning restore 4014
        }

        /// <summary>
        /// La tile del mittente. La coda delle notifiche e' quello che fa
        /// ruotare davvero la tile: senza EnableNotificationQueue una tile
        /// tiene un avviso solo, e il secondo sostituisce il primo invece di
        /// aggiungersi.
        ///
        /// La foto viene dai byte che l'adapter ha gia' mandato (base64): si
        /// scrivono come sono su un file locale, perche' una tile vuole un
        /// percorso, non un'immagine in memoria. Oltre il tetto del sistema la
        /// foto si lascia stare e la tile resta di solo testo: meglio un nome
        /// che una tile che il telefono rifiuta.
        /// </summary>
        private static async Task UpdateSenderTileAsync(string chatId, string name, string avatarData)
        {
            try
            {
                var updater = TileUpdateManager.CreateTileUpdaterForApplication();
                if (!_queueEnabled)
                {
                    updater.EnableNotificationQueue(true);
                    _queueEnabled = true;
                }

                string image = await StoreTileImageAsync(chatId, avatarData);

                var xml = image == null
                    ? TileUpdateManager.GetTemplateContent(TileTemplateType.TileSquare150x150Text02)
                    : TileUpdateManager.GetTemplateContent(TileTemplateType.TileSquare150x150PeekImageAndText01);

                var texts = xml.GetElementsByTagName("text");
                if (texts.Length > 0) texts[0].AppendChild(xml.CreateTextNode(Cut(name, 30)));

                if (image != null)
                {
                    var binding = xml.SelectSingleNode("/tile/visual/binding") as XmlElement;
                    if (binding != null) SetImage(xml, binding, image);
                }

                updater.Update(new TileNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.UpdateSenderTileAsync", ex);
            }
        }

        /// <summary>
        /// La foto del mittente su un file dell'app, e il percorso da mettere
        /// nella tile. Null quando non c'e' una foto o quando e' troppo grande.
        /// </summary>
        private static async Task<string> StoreTileImageAsync(string chatId, string avatarData)
        {
            if (string.IsNullOrEmpty(avatarData)) return null;

            try
            {
                byte[] bytes = Convert.FromBase64String(avatarData);
                if (bytes.Length == 0 || bytes.Length > MaxTileImageBytes) return null;

                string fileName = TileFileName(chatId);
                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, bytes);
                return "ms-appdata:///local/" + fileName;
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.StoreTileImageAsync", ex);
                return null;
            }
        }

        /// <summary>
        /// Un nome di file da una chat: un JID ha una chiocciola e forse due
        /// punti, che in un percorso sono un rischio, quindi si tiene solo cio'
        /// che e' lettera o cifra.
        /// </summary>
        private static string TileFileName(string chatId)
        {
            var builder = new StringBuilder("tile-");
            for (int i = 0; i < chatId.Length; i++)
            {
                char c = chatId[i];
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            builder.Append(".jpg");
            return builder.ToString();
        }

        /// <summary>
        /// Il tetto che il sistema accetta per l'immagine di una tile. Sopra,
        /// l'aggiornamento viene rifiutato: si preferisce la tile di testo.
        /// </summary>
        private const int MaxTileImageBytes = 200 * 1024;

        /// <summary>
        /// Vero dopo che la coda delle notifiche e' stata accesa. Si accende
        /// una volta sola: e' una proprieta' dell'updater, non del singolo
        /// aggiornamento, e richiamarla a ogni messaggio non cambia niente.
        /// </summary>
        private static bool _queueEnabled;

        /// <summary>Un avviso per un messaggio arrivato in una chat chiusa.</summary>
        public static void ShowMessage(string title, string body)
        {
            if (!SettingsService.NotificationsEnabled) return;

            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(Cut(title, 60)));
                texts[1].AppendChild(xml.CreateTextNode(Cut(body, 200)));
                ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.ShowMessage", ex);
            }
        }

        /// <summary>
        /// Il numero di non letti, sui due posti dove WP8.1 lo sa mostrare: il
        /// badge dell'icona e la tile. Sono due notifiche diverse e una puo'
        /// fallire senza l'altra, quindi ognuna ha la sua guardia. Con 0 si
        /// azzera tutto: la tile torna a quella del manifest.
        /// </summary>
        public static void SetUnread(int count)
        {
            SetBadge(count);
            SetTileBadge(count);
        }

        /// <summary>Il numero sull'icona: 0 lo toglie.</summary>
        private static void SetBadge(int count)
        {
            try
            {
                var updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
                if (count <= 0)
                {
                    updater.Clear();
                    return;
                }

                var xml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
                var badge = (XmlElement)xml.SelectSingleNode("/badge");
                badge.SetAttribute("value", Math.Min(count, 99).ToString());
                updater.Update(new BadgeNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.SetBadge", ex);
            }
        }

        /// <summary>
        /// Mette l'icona dell'app sulla tile, nelle due misure che WP8.1 sa
        /// aggiornare con un'icona: 150x150 e 71x71 (il modello IconWithBadge).
        ///
        /// Da notare, perche' e' il punto: **il numero lo disegna il badge, non
        /// la tile.** Questo aggiornamento serve a tenere la tile sull'icona
        /// dell'app mentre il badge e' attivo, e a riportarla a quella del
        /// manifest quando non c'e' piu' niente da leggere (Clear).
        ///
        /// La misura larga non si tocca: su WP8.1 il modello
        /// `TileWide310x150IconWithBadge` non esiste, e comunque il badge viene
        /// disegnato anche sulla tile larga, quindi il numero si vede lo stesso.
        ///
        /// L'icona invece va passata: il modello IconWithBadge NON la prende dal
        /// manifest, la vuole nel payload. Con src vuoto la tile resta senza
        /// icona - e senza sollevare niente, quindi in silenzio.
        /// </summary>
        private static void SetTileBadge(int count)
        {
            try
            {
                var updater = TileUpdateManager.CreateTileUpdaterForApplication();
                if (count <= 0)
                {
                    updater.Clear();
                    return;
                }

                var xml = TileUpdateManager.GetTemplateContent(
                    TileTemplateType.TileSquare150x150IconWithBadge);
                var visual = (XmlElement)xml.SelectSingleNode("/tile/visual");
                if (visual == null) return;

                SetTileIcon(xml, visual, TileIconUri);
                AppendBinding(xml, visual, TileTemplateType.TileSquare71x71IconWithBadge);

                updater.Update(new TileNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.SetTileBadge", ex);
            }
        }

        /// <summary>
        /// Copia il binding di un altro modello dentro il documento della tile.
        /// Un nodo appartiene al suo documento, quindi va importato: appenderlo
        /// cosi' com'e' solleva un'eccezione.
        ///
        /// Il binding importato e' un binding che parte, quindi vuole la sua
        /// icona come l'altro: senza, la misura che lo riceve si disegna senza.
        /// </summary>
        private static void AppendBinding(XmlDocument xml, XmlElement visual, TileTemplateType template)
        {
            var other = TileUpdateManager.GetTemplateContent(template);
            var binding = other.SelectSingleNode("/tile/visual/binding");
            if (binding == null) return;

            var imported = xml.ImportNode(binding, true) as XmlElement;
            if (imported == null) return;

            SetImage(xml, imported, TileIconUri);
            visual.AppendChild(imported);
        }

        /// <summary>L'icona sul binding che sta dentro il visual del modello.</summary>
        private static void SetTileIcon(XmlDocument xml, XmlElement visual, string uri)
        {
            var binding = visual.SelectSingleNode("binding") as XmlElement;
            if (binding == null) return;
            SetImage(xml, binding, uri);
        }

        /// <summary>
        /// Scrive l'icona sull'image del binding, creandola se il modello non ne
        /// ha una: l'elemento va creato con il documento di destinazione, non con
        /// quello del modello, altrimenti l'inserimento solleva un'eccezione.
        /// </summary>
        private static void SetImage(XmlDocument xml, XmlElement binding, string uri)
        {
            var image = binding.SelectSingleNode("image") as XmlElement;
            if (image == null)
            {
                image = xml.CreateElement("image");
                image.SetAttribute("id", "1");
                binding.AppendChild(image);
            }
            image.SetAttribute("src", uri);
        }

        /// <summary>
        /// L'immagine della tile, in un posto solo. E' un PNG trasparente senza
        /// padding: i logo del manifest hanno il padding che il sistema si
        /// aspetta, e su una tile da 150 px quel padding si mangia il disegno.
        /// </summary>
        private const string TileIconUri = "ms-appx:///Assets/TileIcon.png";

        private static string Cut(string value, int max)
        {
            string text = value ?? "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "\u2026";
        }
    }
}
