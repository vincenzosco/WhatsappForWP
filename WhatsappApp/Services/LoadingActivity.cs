using System;
using System.Threading;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Tracks finite foreground loads shared by pages and services.
    /// </summary>
    public static class LoadingActivity
    {
        private static readonly object Gate = new object();
        private static int _activeCount;

        public static event EventHandler Changed;

        public static bool IsActive
        {
            get
            {
                lock (Gate)
                {
                    return _activeCount != 0;
                }
            }
        }

        public static IDisposable Begin(string name)
        {
            EventHandler handler = null;
            lock (Gate)
            {
                if (_activeCount++ == 0) handler = Changed;
            }

            if (handler != null) handler(null, EventArgs.Empty);
            return new Lease();
        }

        private static void End()
        {
            EventHandler handler = null;
            lock (Gate)
            {
                if (_activeCount <= 0) return;
                if (--_activeCount == 0) handler = Changed;
            }

            if (handler != null) handler(null, EventArgs.Empty);
        }

        private sealed class Lease : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                End();
            }
        }
    }
}
