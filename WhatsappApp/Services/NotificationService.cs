using System;
using Windows.Data.Xml.Dom;
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
        /// L'immagine non si passa: senza di essa il modello usa il logo
        /// dell'app, che e' quello che il manifest gia' dichiara.
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
        /// </summary>
        private static void AppendBinding(XmlDocument xml, XmlElement visual, TileTemplateType template)
        {
            var other = TileUpdateManager.GetTemplateContent(template);
            var binding = other.SelectSingleNode("/tile/visual/binding");
            if (binding != null) visual.AppendChild(xml.ImportNode(binding, true));
        }

        private static string Cut(string value, int max)
        {
            string text = value ?? "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "\u2026";
        }
    }
}
