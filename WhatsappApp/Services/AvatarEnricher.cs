using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WhatsappApp.Models;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The pictures of the chat list, asked for one chat at a time.
    ///
    /// Why it exists: nothing filled `AvatarCache` any more. The adapter the app used to talk
    /// to carried the picture of every chat on its row; the native client has no such row - a
    /// conversation carries a name and no bytes - so the list drew initials for every person
    /// and every group. `NativeConnection.RequestAvatarAsync` was already there for the
    /// contact's page; this is the same query, asked for the rows.
    ///
    /// Why one at a time and with a pause: every picture is two round trips (the query that
    /// answers a signed URL, then the JPEG), and a hundred and seventy of them fired at once
    /// is the shape WhatsApp throttles. The bytes are small (`preview`, not `image`), so the
    /// whole list costs a couple of minutes of background work and about a megabyte on disk
    /// (`AvatarCache`, capped).
    ///
    /// The list is read on every step instead of copied: a row that arrives while this runs is
    /// one more to fetch, and a row that has a picture already is skipped.
    /// </summary>
    public static class AvatarEnricher
    {
        /// <summary>Between two chats. About three chats a second, each two round trips.</summary>
        private const int DelayMs = 350;

        /// <summary>How long to wait when there is no session to ask on.</summary>
        private const int NoSessionDelayMs = 3000;

        /// <summary>How many times a missing session is waited for before giving up.</summary>
        private const int NoSessionRetries = 3;

        private static readonly object Gate = new object();

        /// <summary>The chats asked about this run, picture or not: one question per chat.</summary>
        private static readonly HashSet<string> Asked = new HashSet<string>();

        /// <summary>Whether somebody wants pictures right now (the chat list is in front).</summary>
        private static bool _wanted;

        private static bool _running;

        /// <summary>Bumped by Stop: a loop that sees a different number ends.</summary>
        private static int _generation;

        /// <summary>Starts (or rejoins) the fetch. Call on the UI thread, from the list.</summary>
        public static void Start()
        {
            lock (Gate)
            {
                _wanted = true;
                _generation++;
            }

            Launch();
        }

        /// <summary>The list is no longer in front: the next step stops.</summary>
        public static void Stop()
        {
            lock (Gate)
            {
                _wanted = false;
                _generation++;
            }
        }

        /// <summary>Forgets which chats were asked, so a new login asks again.</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                Asked.Clear();
            }
        }

        private static void Launch()
        {
            lock (Gate)
            {
                if (_running) return;

                _running = true;
            }

            int generation;
            lock (Gate)
            {
                generation = _generation;
            }

            // C# 5 cannot await inside catch, and this is the only shape that keeps the
            // exception owned: Guarded names it instead of leaving it unobserved.
#pragma warning disable 4014
            Guarded.RunGuardedAsync("AvatarEnricher/Run",
                delegate { return RunAsync(generation); });
#pragma warning restore 4014
        }

        private static async Task RunAsync(int generation)
        {
            int index = 0;
            int noSession = 0;
            int failures = 0;

            try
            {
                while (Wanted(generation))
                {
                    IList<Contact> rows = DataService.Instance.Contacts;

                    if (rows.Count == 0 || index >= rows.Count)
                    {
                        // Nothing to ask yet, or every row asked: a list that is still filling
                        // is worth waiting for, and one that has finished is worth leaving.
                        if (DataService.Instance.HistoryComplete && index >= rows.Count) break;

                        index = 0;
                        await Task.Delay(1000);
                        continue;
                    }

                    Contact row = rows[index];
                    index++;

                    string id = row == null ? null : row.Id;
                    if (!WorthAsking(id)) continue;
                    if (AvatarCache.Get(id) != null) continue;
                    if (!FirstAsk(id)) continue;

                    if (MemoryWatcher.Instance.IsUnderPressure)
                    {
                        await Task.Delay(1000);
                        continue;
                    }

                    // The wait is outside the catch blocks on purpose: the WP8.1 compiler
                    // answers CS1985 to an await inside one (C# 5 syntax, see
                    // tools/check-csharp5.js).
                    byte[] jpeg = null;
                    int wait = 0;
                    try
                    {
                        jpeg = await NativeConnection.Instance.RequestAvatarAsync(id, true);
                        noSession = 0;
                    }
                    catch (ProtocolException error)
                    {
                        // No socket yet (or none any more): the first rows arrive from the
                        // local copy before the login, and asking now would be one exception
                        // per row. Wait for the connection instead of giving up on it - the
                        // loop that broke here left every row with initials until the tab was
                        // left and entered again, because a login that finished a second
                        // later had nobody to tell.
                        noSession++;
                        Forget(id);

                        if (noSession == NoSessionRetries)
                        {
                            Diag.Ok("avatar: no session to ask on, the pass waits for the login: "
                                + error.Message);
                        }

                        // From the first row: the ones this pass stepped over are the ones
                        // that still have no picture, and a completed list would otherwise
                        // make the loop end as if there was nothing left to ask.
                        index = 0;
                        wait = NoSessionDelayMs;
                    }
                    catch (Exception error)
                    {
                        Forget(id);

                        if (failures < 5)
                        {
                            failures++;
                            Diag.Failed("AvatarEnricher/" + id, error);
                        }

                        wait = DelayMs;
                    }

                    if (wait > 0)
                    {
                        await Task.Delay(wait);
                        continue;
                    }

                    if (jpeg != null && jpeg.Length > 0)
                    {
                        string base64 = Convert.ToBase64String(jpeg);
                        AvatarCache.Remember(id, base64);

                        if (row != null)
                        {
                            row.AvatarData = base64;
                            await row.LoadAvatarAsync();
                        }
                    }
                    else
                    {
                        // "No picture" is an answer, not a failure: the row keeps its initials
                        // and this chat is not asked again this run.
                        Diag.Ok("avatar: " + id + " has no picture");
                    }

                    await Task.Delay(DelayMs);
                }
            }
            finally
            {
                bool again = false;

                lock (Gate)
                {
                    _running = false;

                    // A Start that arrived while this loop was ending asked for a run this one
                    // did not do: the generation moved, and the rows are still there.
                    if (_wanted && generation != _generation) again = true;
                }

                if (again) Launch();
            }
        }

        /// <summary>Whether this address is one a picture can be asked for.</summary>
        private static bool WorthAsking(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return false;
            if (jid.EndsWith("@bot", StringComparison.OrdinalIgnoreCase)) return false;
            if (jid.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(jid, "status@broadcast", StringComparison.OrdinalIgnoreCase)) return false;

            return jid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase)
                || jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)
                || jid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Wanted(int generation)
        {
            lock (Gate)
            {
                return _wanted && generation == _generation;
            }
        }

        /// <summary>True the first time this chat is asked about; false afterwards.</summary>
        private static bool FirstAsk(string jid)
        {
            lock (Gate)
            {
                return Asked.Add(jid);
            }
        }

        private static void Forget(string jid)
        {
            lock (Gate)
            {
                Asked.Remove(jid);
            }
        }
    }
}
