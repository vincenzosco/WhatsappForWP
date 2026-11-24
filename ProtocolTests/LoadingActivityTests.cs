using System;
using System.Collections.Generic;
using System.Threading;
using WhatsappApp.Services;

namespace ProtocolTests
{
    public static class LoadingActivityTests
    {
        [Test]
        public static void TheCoordinatorStartsInactive()
        {
            Assert.IsFalse(LoadingActivity.IsActive);
        }

        [Test]
        public static void OneLeaseActivatesAndItsReleaseDeactivates()
        {
            var transitions = new List<bool>();
            EventHandler handler = delegate { transitions.Add(LoadingActivity.IsActive); };
            LoadingActivity.Changed += handler;
            try
            {
                IDisposable lease = LoadingActivity.Begin("one");
                Assert.IsTrue(LoadingActivity.IsActive);
                lease.Dispose();
                Assert.IsFalse(LoadingActivity.IsActive);
                Assert.AreEqual(2, transitions.Count);
                Assert.IsTrue(transitions[0]);
                Assert.IsFalse(transitions[1]);
            }
            finally
            {
                LoadingActivity.Changed -= handler;
            }
        }

        [Test]
        public static void OverlappingLeasesKeepTheCoordinatorActiveUntilTheLastRelease()
        {
            var first = LoadingActivity.Begin("first");
            var second = LoadingActivity.Begin("second");
            try
            {
                Assert.IsTrue(LoadingActivity.IsActive);
                first.Dispose();
                Assert.IsTrue(LoadingActivity.IsActive);
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }

            Assert.IsFalse(LoadingActivity.IsActive);
        }

        [Test]
        public static void ReleasingOneLeaseTwiceDoesNotEmitAnotherTransition()
        {
            int transitions = 0;
            EventHandler handler = delegate { transitions++; };
            LoadingActivity.Changed += handler;
            try
            {
                IDisposable lease = LoadingActivity.Begin("one");
                lease.Dispose();
                lease.Dispose();
                Assert.AreEqual(2, transitions);
                Assert.IsFalse(LoadingActivity.IsActive);
            }
            finally
            {
                LoadingActivity.Changed -= handler;
            }
        }

        [Test]
        public static void ConcurrentLeaseReleasesLeaveTheCounterInactive()
        {
            const int Count = 64;
            var leases = new IDisposable[Count];
            for (int i = 0; i < leases.Length; i++) leases[i] = LoadingActivity.Begin("parallel");

            var threads = new Thread[Count];
            for (int i = 0; i < threads.Length; i++)
            {
                int index = i;
                threads[i] = new Thread(new ThreadStart(delegate { leases[index].Dispose(); }));
                threads[i].Start();
            }
            for (int i = 0; i < threads.Length; i++) threads[i].Join();

            Assert.IsFalse(LoadingActivity.IsActive);
        }
    }
}
