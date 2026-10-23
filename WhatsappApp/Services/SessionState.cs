using System;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The one fact about the stored account that cannot be read off the account itself:
    /// whether WhatsApp has already refused it.
    ///
    /// A session that the phone was unlinked from parses perfectly - every key is there,
    /// every length is right - so `CredentialStore.HasSession` says yes and the app would
    /// open on a chat list that can never fill. The refusal is not in the storage; it is
    /// what the server answered the last time the app tried, and this is where that answer
    /// is kept between runs.
    ///
    /// It lives next to `SessionService`'s `Session.Section`, under the same prefix. The
    /// keys with real material in them are `CredentialStore`'s, and it writes and clears
    /// only the ones it lists: a flag sitting next to them is not a credential, which is
    /// why `Clear()` leaves it alone until a pairing succeeds and this file clears it.
    /// </summary>
    public static class SessionState
    {
        /// <summary>`Session.Section` is the other one; the prefix is the app's convention, not the store's.</summary>
        private const string KeyExpired = "Session.Expired";

        private static bool _loaded;
        private static bool _expired;

        /// <summary>
        /// True when the last connection was refused: the app then shows the QR, with the
        /// sentence that says why, instead of the chats.
        /// </summary>
        public static bool Expired
        {
            get
            {
                EnsureLoaded();
                return _expired;
            }
            set
            {
                EnsureLoaded();

                _expired = value;

                // Guarded: the caller is the connection's refusal handler, which sets this on
                // its way out of the loop. A store that refuses the write (full, locked) threw
                // from inside that handler, so the loop never reached its tail and `_running`
                // stayed true - a refused account and no way to try again this run.
                try
                {
                    if (value) ApplicationData.Current.LocalSettings.Values[KeyExpired] = 1;
                    else ApplicationData.Current.LocalSettings.Values.Remove(KeyExpired);
                }
                catch (Exception ex)
                {
                    Diag.Failed("SessionState/Expired", ex);
                }
            }
        }

        /// <summary>
        /// Reads the flag once, from whatever a previous run left. The native session is
        /// what sets it (a handshake the server refuses); until that exists nothing does,
        /// and the states that matter today - no session at all, a session that does not
        /// parse - are decided by `CredentialStore.HasSession`.
        /// </summary>
        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            object stored;
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeyExpired, out stored)
                && stored != null)
            {
                _expired = stored.ToString() == "1";
            }
        }
    }
}
