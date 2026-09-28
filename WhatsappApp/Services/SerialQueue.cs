using System;
using System.Threading.Tasks;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Una cosa alla volta, nell'ordine in cui arrivano.
    ///
    /// Perche' esiste: mezzo servizio di questa app e' asincrono, e i suoi
    /// handler non si aspettano - DispatchOnUiThread e' async void, i gestori
    /// dei frame sono async void, e la scrittura di un file parte senza che
    /// nessuno la aspetti. Due di loro che toccano la stessa risorsa si
    /// intrecciano: due StoreAsync sullo stesso DataWriter mettono il prefisso
    /// di lunghezza di uno davanti al payload dell'altro, e la connessione cade
    /// su un frame che non esiste.
    ///
    /// Il lavoro entra qui e viene eseguito tutto, in fila: la coda e' una
    /// catena di Task, non un thread, quindi non costa niente quando e' vuota.
    /// Un pezzo che fallisce non ferma la coda: il guasto lo vede chi ha
    /// chiamato, e il pezzo dopo parte lo stesso.
    /// </summary>
    public sealed class SerialQueue
    {
        private readonly object _gate = new object();
        private Task _tail = Done();

        private static Task Done()
        {
            var source = new TaskCompletionSource<bool>();
            source.SetResult(true);
            return source.Task;
        }

        /// <summary>
        /// Accoda il lavoro e restituisce il suo esito. La prima parte viene
        /// eseguita subito se la coda e' vuota (ExecuteSynchronously), come
        /// farebbe una chiamata diretta.
        /// </summary>
        public Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            if (work == null) throw new ArgumentNullException("work");

            Task<T> next;
            lock (_gate)
            {
                next = _tail
                    .ContinueWith(delegate { return work(); },
                        TaskContinuationOptions.ExecuteSynchronously)
                    .Unwrap();

                // La coda continua anche se questo pezzo fallisce: l'eccezione
                // resta nel Task che e' stato restituito, e va osservata li'.
                _tail = next.ContinueWith(
                    delegate(Task<T> finished) { AggregateException ignored = finished.Exception; },
                    TaskContinuationOptions.ExecuteSynchronously);
            }
            return next;
        }

        /// <summary>La stessa coda, per un lavoro che non restituisce niente.</summary>
        public Task RunAsync(Func<Task> work)
        {
            if (work == null) throw new ArgumentNullException("work");

            return RunAsync<bool>(async delegate
            {
                await work();
                return true;
            });
        }
    }
}
