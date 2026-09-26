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

        /// <summary>Il numero sull'icona: 0 toglie il badge.</summary>
        public static void SetUnread(int count)
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
                Diag.Failed("NotificationService.SetUnread", ex);
            }
        }

        private static string Cut(string value, int max)
        {
            string text = value ?? "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "\u2026";
        }
    }
}
