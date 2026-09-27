using System;
using Windows.System;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Il budget di memoria di questo telefono, e cosa l'app fa quando lo
    /// avvicina.
    ///
    /// Perche' esiste: su WP8.1 la memoria non si dichiara da nessuna parte - lo
    /// schema della manifest non la prevede - e non si vede. Il sistema avvisa e
    /// poi sospende o termina; il modo documentato di ascoltarlo e'
    /// `MemoryManager`. La chiamata utile qui e' `AppMemoryUsageIncreased`:
    /// quando il livello passa a High, l'app deve liberare quello che puo' subito.
    ///
    /// Due cose che su questa piattaforma NON ci sono, e che il compilatore
    /// segnala invece di lasciarle scoperte a runtime:
    ///
    ///  - `AppMemoryUsageLimitChanging` esiste da Windows 10 1607 (su WP8.1 non
    ///    e' dichiarato);
    ///  - `AppMemoryUsageLevel` qui ha tre valori - Low, Medium, High - e
    ///    **OverLimit non esiste** (CS0117): quello e' arrivato con Windows 10.
    ///    Quindi la soglia da guardare e' `High`, e basta.
    ///
    /// Cosa libera: le bitmap degli avatar decodificate - la cosa pesante, una per
    /// conversazione - e la cronologia delle chat che nessuno sta leggendo. Non
    /// libera quello che l'utente sta guardando: la chat aperta resta intera.
    ///
    /// Finche' la pressione c'e' non si decodifica niente di nuovo: senza questo,
    /// il primo frame di conversazioni rimette dentro tutto quello appena
    /// buttato. Quando il livello riscende, si riprende a decodificare.
    /// </summary>
    public sealed class MemoryWatcher
    {
        private static readonly MemoryWatcher InstanceHolder = new MemoryWatcher();

        private bool _hooked;
        private bool _underPressure;

        public static MemoryWatcher Instance
        {
            get { return InstanceHolder; }
        }

        /// <summary>Vero finche' il livello di memoria e' High.</summary>
        public bool IsUnderPressure
        {
            get { return _underPressure; }
        }

        /// <summary>
        /// Una volta per processo. Lo chiama App.StartServicesOnce: un'app avviata
        /// da una condivisione non passa da OnLaunched, e un secondo aggancio
        /// raddoppierebbe i gestori. Ogni chiamata e' protetta: un telefono che
        /// rifiuta l'evento non deve far cadere l'avvio dell'app.
        /// </summary>
        public void Start()
        {
            if (_hooked) return;
            _hooked = true;

            try
            {
                MemoryManager.AppMemoryUsageIncreased += OnUsageIncreased;
                MemoryManager.AppMemoryUsageDecreased += OnUsageDecreased;

                // Il limite del telefono, una volta sola nel log: e' l'unico posto
                // dove si distingue un dispositivo da 512 MB da uno da 1 GB.
                Diag.Ok("memory budget " + Mbytes(MemoryManager.AppMemoryUsageLimit) + " MB");

                Apply(MemoryManager.AppMemoryUsageLevel);
            }
            catch (Exception ex)
            {
                _hooked = false;
                Diag.Failed("MemoryWatcher.Start", ex);
            }
        }

        private void OnUsageIncreased(object sender, object e)
        {
            try
            {
                Apply(MemoryManager.AppMemoryUsageLevel);
            }
            catch (Exception ex)
            {
                Diag.Failed("MemoryWatcher.OnUsageIncreased", ex);
            }
        }

        private void OnUsageDecreased(object sender, object e)
        {
            try
            {
                Apply(MemoryManager.AppMemoryUsageLevel);
            }
            catch (Exception ex)
            {
                Diag.Failed("MemoryWatcher.OnUsageDecreased", ex);
            }
        }

        /// <summary>
        /// Applica un livello: si libera entrando in pressione, si smette di
        /// liberare uscendone. La liberazione avviene una volta per transizione,
        /// non a ogni evento: sotto pressione gli eventi si susseguono.
        ///
        /// High e' la soglia piu' alta che questo sistema sa nominare: OverLimit
        /// non esiste su WP8.1.
        /// </summary>
        private void Apply(AppMemoryUsageLevel level)
        {
            bool pressure = level == AppMemoryUsageLevel.High;
            if (pressure == _underPressure) return;

            _underPressure = pressure;
            if (pressure)
            {
                Diag.Ok("memory under pressure: releasing decoded images");
                DataService.Instance.TrimForMemory();
            }
        }

        private static ulong Mbytes(ulong bytes)
        {
            return bytes / (1024UL * 1024UL);
        }
    }
}
