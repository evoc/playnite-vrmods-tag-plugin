using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for <see cref="HttpHostGate"/> — the cap on how many web requests the plugin has in
    /// flight.
    ///
    /// Why it exists: <see cref="VrModTagService"/> fetches every enabled source CONCURRENTLY, and
    /// the 500 ms politeness pause lives inside each parallel per-source task, so it bounds nothing.
    /// Several sources talk to the same host (three to store.steampowered.com, three to
    /// raw.githubusercontent.com), so without a gate one slow site is hit by several sources at the
    /// same instant, and the Steam Curator source walks up to 100 pages of a single host on its own.
    ///
    /// Each test builds its own gate instance (the constructor seam) so no test contends with
    /// another over shared static state; <see cref="HttpHostGate.Shared"/> keeps the production
    /// limits for the plugin itself.
    /// </summary>
    [TestFixture]
    public class HttpHostGateTests
    {
        /// <summary>How long a queued request is expected to STAY queued while nothing frees its slot.</summary>
        private const int QueuedMs = 250;

        /// <summary>Upper bound for a queued request to start once its slot is released.</summary>
        private const int ReleaseGraceMs = 5000;

        private static string NewHost(string label) => $"{label}-{Guid.NewGuid():N}.gate.invalid";

        /// <summary>Starts <see cref="HttpHostGate.EnterAsync"/> on a worker so the test can observe queuing.</summary>
        private static Task EnterInBackground(HttpHostGate gate, string host, CancellationToken ct)
            => Task.Run(() => gate.EnterAsync(host, ct));

        [Test]
        public void HostOf_UsesTheUrlHost_AndMapsAnythingElseToTheSharedPool()
        {
            Assert.That(HttpHostGate.HostOf("https://store.steampowered.com/app/12840/"), Is.EqualTo("store.steampowered.com"));
            Assert.That(HttpHostGate.HostOf("http://pcvr-central.com/games/?page=2"), Is.EqualTo("pcvr-central.com"));
            // A malformed URL must still be gated (one shared pool), not bypass the gate.
            Assert.That(HttpHostGate.HostOf("not a url"), Is.Empty);
            Assert.That(HttpHostGate.HostOf(""), Is.Empty);
            Assert.That(HttpHostGate.HostOf(null), Is.Empty);
        }

        [Test]
        public void SameHost_SecondRequest_WaitsUntilTheFirstReleases()
        {
            var gate = new HttpHostGate();
            var host = NewHost("same");

            gate.EnterAsync(host, CancellationToken.None).GetAwaiter().GetResult();
            var queued = EnterInBackground(gate, host, CancellationToken.None);
            try
            {
                Assert.That(gate.AvailableFor(host), Is.EqualTo(HttpHostGate.DefaultPerHostConcurrency - 1),
                    "Taking the only per-host slot must leave none free.");
                Assert.That(queued.Wait(QueuedMs), Is.False,
                    "A second request to the same host must not start while the first one is still in flight.");

                gate.Exit(host);

                Assert.That(queued.Wait(ReleaseGraceMs), Is.True,
                    "The queued request must start as soon as the in-flight request releases the host slot.");
            }
            finally
            {
                gate.Exit(host);
            }
        }

        [Test]
        public void DifferentHosts_DoNotQueueBehindEachOther()
        {
            var gate = new HttpHostGate();
            var held = NewHost("held");
            var other = NewHost("other");

            gate.EnterAsync(held, CancellationToken.None).GetAwaiter().GetResult();
            try
            {
                var concurrent = EnterInBackground(gate, other, CancellationToken.None);
                Assert.That(concurrent.Wait(ReleaseGraceMs), Is.True,
                    "Requests to different hosts must run side by side; only same-host requests queue.");
                gate.Exit(other);
            }
            finally
            {
                gate.Exit(held);
            }
        }

        [Test]
        public void HostKey_IsCaseInsensitive_AndAcceptsBareHostOrFullUrl()
        {
            var gate = new HttpHostGate();
            // The three Steam-sourced parsers all reach the same host through different URL shapes
            // (path casing, query strings, a bare host in tests): they must share ONE pool.
            gate.EnterAsync("https://store.steampowered.com/app/12840/", CancellationToken.None).GetAwaiter().GetResult();
            try
            {
                Assert.That(gate.AvailableFor("STORE.SteamPowered.COM"), Is.EqualTo(0),
                    "Host matching must be case-insensitive.");
                Assert.That(gate.AvailableFor("https://STORE.steampowered.com/curator/999/ajax/?start=50"), Is.EqualTo(0),
                    "A different URL on the same host must hit the same pool.");
                Assert.That(gate.AvailableFor("store.steampowered.com"), Is.EqualTo(0),
                    "A bare host and a full URL of the same host must hit the same pool.");
                Assert.That(gate.TrackedHosts().Length, Is.EqualTo(1),
                    "One host must mean one slot pool, whatever URL shape it was reached through.");
            }
            finally
            {
                gate.Exit("https://store.steampowered.com/app/12840/");
            }
        }

        [Test]
        public void GlobalCap_BoundsTotalInFlightRequests_RegardlessOfHost()
        {
            var gate = new HttpHostGate(perHostConcurrency: 1, maxConcurrentRequests: 2);
            var hosts = new[] { NewHost("cap0"), NewHost("cap1"), NewHost("cap2") };

            gate.EnterAsync(hosts[0], CancellationToken.None).GetAwaiter().GetResult();
            gate.EnterAsync(hosts[1], CancellationToken.None).GetAwaiter().GetResult();
            var queued = EnterInBackground(gate, hosts[2], CancellationToken.None);
            try
            {
                Assert.That(gate.GlobalAvailable, Is.Zero, "Both global slots are taken.");
                Assert.That(gate.AvailableFor(hosts[2]), Is.EqualTo(1),
                    "The third host has a free HOST slot but must still wait for the global cap.");
                Assert.That(queued.Wait(QueuedMs), Is.False,
                    "No request may start beyond the global cap, even on an untouched host.");

                gate.Exit(hosts[0]);

                Assert.That(queued.Wait(ReleaseGraceMs), Is.True,
                    "Releasing one global slot must let the queued request through.");
            }
            finally
            {
                gate.Exit(hosts[1]);
                if (queued.IsCompleted && !queued.IsFaulted && !queued.IsCanceled)
                    gate.Exit(hosts[2]);
            }
        }

        [Test]
        public void CancelledWaiter_ReleasesTheGlobalSlotItAlreadyTook()
        {
            // A queued waiter that is cancelled must give back the global slot it took before waiting
            // for its host: with 12 sources and a cap of 4, one leaked slot per cancelled request
            // would stall a scan.
            var gate = new HttpHostGate(perHostConcurrency: 1, maxConcurrentRequests: 2);
            var busyHost = NewHost("busy");
            var idleHost = NewHost("idle");

            gate.EnterAsync(busyHost, CancellationToken.None).GetAwaiter().GetResult();
            var busyStillHeld = true;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                var cancelled = EnterInBackground(gate, busyHost, cts.Token);

                Assert.Catch(() => cancelled.GetAwaiter().GetResult(),
                    "A waiter cancelled while queued must fail with cancellation.");
                // Exactly one global slot is still held — by the in-flight request above. Had the
                // cancelled waiter kept the global slot it took before queueing, this would be 0.
                Assert.That(gate.GlobalAvailable, Is.EqualTo(1),
                    "The cancelled waiter must not keep a global slot it never got a host slot for.");

                gate.Exit(busyHost);
                busyStillHeld = false;

                // Proof that the cap is intact end to end: with the busy host free again and no leaked
                // slot, one further request on it and one on another host must both be admitted.
                var after1 = EnterInBackground(gate, busyHost, CancellationToken.None);
                Assert.That(after1.Wait(ReleaseGraceMs), Is.True, "A freed slot must admit the next request.");
                var after2 = EnterInBackground(gate, idleHost, CancellationToken.None);
                Assert.That(after2.Wait(ReleaseGraceMs), Is.True, "A freed slot must admit the next request.");

                gate.Exit(idleHost);
                gate.Exit(busyHost);
            }
            finally
            {
                if (busyStillHeld)
                    gate.Exit(busyHost);
            }
        }

        [Test]
        public void Constructor_RejectsNonPositiveLimits()
        {
            Assert.Catch(() => new HttpHostGate(0, 4), "perHostConcurrency must be at least 1");
            Assert.Catch(() => new HttpHostGate(1, 0), "maxConcurrentRequests must be at least 1");
        }

        /// <summary>
        /// The gate owns semaphores, so it is disposable (CA1001). A disposed gate must REFUSE requests
        /// rather than grant a slot from a disposed semaphore: the alternative is a silent slot leak that
        /// stops capping everything that runs afterwards. Double disposal is tolerated because plugin
        /// shutdown paths overlap (Playnite calls Dispose, the plugin's own Dispose calls it too).
        /// </summary>
        [Test]
        public void Dispose_RefusesLaterRequestsInsteadOfLeakingSlots()
        {
            var gate = new HttpHostGate(perHostConcurrency: 1, maxConcurrentRequests: 2);
            var host = NewHost("disposed-gate");
            gate.EnterAsync(host, CancellationToken.None).GetAwaiter().GetResult();
            gate.Exit(host);

            gate.Dispose();
            gate.Dispose();

            var failure = Assert.Catch(() => gate.EnterAsync(host, CancellationToken.None).GetAwaiter().GetResult());
            Assert.That(failure, Is.InstanceOf<ObjectDisposedException>(),
                "A disposed gate must refuse the request, not hand out a slot from a disposed semaphore.");
        }
    }
}
