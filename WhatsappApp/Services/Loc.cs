using System;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using System.Reflection;
using System.Windows;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The localized string lookup for the foreground app and its headless scheduled task.
    /// </summary>
    public static class Loc
    {
        private static ResourceManager _manager;
        private static bool _managerUnavailable;
        private static bool _prewarmed;

        public static void Prewarm()
        {
            _prewarmed = true;
            Get("Nav_Chats", "Chats");
            RememberForBackground("TileUnreadOne", "1 unread message");
            RememberForBackground("TileUnreadMany", "{0} unread messages");
        }

        private static void RememberForBackground(string key, string fallback)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values["Loc." + key] = Get(key, fallback);
            }
            catch (Exception ex)
            {
                Diag.Failed("Loc.RememberForBackground " + key, ex);
            }
        }

        private static string RememberedForBackground(string key)
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue("Loc." + key, out value)
                    && value != null)
                {
                    string text = value.ToString();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("Loc.RememberedForBackground " + key, ex);
            }

            return null;
        }

        public static string Get(string key, string fallback)
        {
            if (string.IsNullOrEmpty(key)) return fallback;

            if (!_prewarmed)
            {
                string remembered = RememberedForBackground(key);
                return remembered ?? fallback;
            }

            ResourceManager manager = Manager();
            if (manager == null) return fallback;

            try
            {
                string value = manager.GetString(key, CultureInfo.CurrentUICulture);
                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            catch (Exception ex)
            {
                _managerUnavailable = true;
                _manager = null;
                Diag.Failed("Loc.Get key " + key, ex);
                return fallback;
            }
        }

        private static ResourceManager Manager()
        {
            if (_manager != null || _managerUnavailable) return _manager;

            Assembly assembly = typeof(Loc).Assembly;
            var candidates = new List<string>();
            candidates.Add("WhatsappApp.Strings.Resources");
            candidates.Add("WhatsappApp.Resources");
            candidates.Add("Resources");

            string[] resourceNames = assembly.GetManifestResourceNames();
            for (int i = 0; i < resourceNames.Length; i++)
            {
                string resourceName = resourceNames[i];
                const string Extension = ".resources";
                if (!resourceName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;

                string baseName = resourceName.Substring(0, resourceName.Length - Extension.Length);
                if (baseName.IndexOf("Resources", StringComparison.OrdinalIgnoreCase) >= 0
                    && !candidates.Contains(baseName))
                {
                    candidates.Add(baseName);
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                try
                {
                    var candidate = new ResourceManager(candidates[i], assembly);
                    string probe = candidate.GetString("Nav_Chats", CultureInfo.CurrentUICulture);
                    if (!string.IsNullOrEmpty(probe))
                    {
                        _manager = candidate;
                        return _manager;
                    }
                }
                catch (MissingManifestResourceException)
                {
                }
                catch (Exception)
                {
                }
            }

            _managerUnavailable = true;
            Diag.Failed("Loc.ResourceManager",
                new MissingManifestResourceException("the localized Silverlight resources are not registered"));
            return null;
        }
    }
}
