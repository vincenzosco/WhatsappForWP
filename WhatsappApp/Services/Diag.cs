using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Un posto solo per i guasti che l'app decide di sopravvivere.
    ///
    /// Perche' esiste: ogni `catch` silenzioso nasconde un guasto vero, e nel log
    /// del debugger un'eccezione WinRT arriva come
    ///
    ///     A first chance exception of type 'System.Exception' occurred ...
    ///     WinRT information: The operation identifier is not valid.
    ///
    /// senza dire *quale* chiamata l'ha lanciata. Qui il guasto viene registrato
    /// con il punto esatto e con l'HRESULT, che e' l'unica cosa che distingue un
    /// membro assente sulla piattaforma (E_NOTIMPL) da una chiamata fatta dal
    /// thread sbagliato (E_ILLEGAL_METHOD_CALL) o da un'operazione non piu'
    /// valida (0x800710DD).
    ///
    /// Ogni riga compare una volta sola: senza la deduplica, un ciclo che fallisce
    /// ogni due secondi (il beacon di scoperta) riempirebbe il log e nasconderebbe
    /// tutto il resto. Debug.WriteLine e' compilato via nelle build di rilascio,
    /// quindi in produzione questo codice non scrive e non costa.
    /// </summary>
    public static class Diag
    {
        private static readonly List<string> Seen = new List<string>();
        private static readonly object Gate = new object();

        /// <summary>Da chiamare dentro un catch, per un guasto da cui si prosegue.</summary>
        public static void Failed(string where, Exception ex)
        {
            Write(where + ": " + Describe(ex));
        }

        /// <summary>
        /// Una capacita' che invece c'e'. Il probe di avvio la registra una volta
        /// sola per esecuzione: la riga dice "questo telefono sa farlo".
        /// </summary>
        public static void Ok(string what)
        {
            Write("ok: " + what);
        }

        /// <summary>
        /// Tipo, HRESULT in esadecimale e messaggio: senza il codice due guasti
        /// diversi restano indistinguibili nel log.
        /// </summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "(nessuna eccezione)";
            return ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + " " + (ex.Message ?? "");
        }

        private static void Write(string line)
        {
            lock (Gate)
            {
                if (Seen.Contains(line)) return;
                Seen.Add(line);
            }
            Debug.WriteLine("DIAG " + line);
        }
    }
}
