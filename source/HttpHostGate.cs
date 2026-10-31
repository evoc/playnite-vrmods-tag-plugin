using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Caps how many HTTP requests the plugin has in flight at once — per host and overall.
    /// <see cref="Shared"/> is the one every parser uses; the constructor with explicit limits is the
    /// unit-test seam (a test needs its own uncontended gate).
    ///
    /// Why this is needed: every enabled source is fetched concurrently
    /// (<see cref="VrModTagService"/> fans them out with <c>Select(async …)</c> + <c>Task.WhenAll</c>)
    /// and the 500 ms politeness pause lives INSIDE each parallel per-source task, so it bounds
    /// nothing. Several sources talk to the same host (three to <c>store.steampowered.com</c>, three
    /// to <c>raw.githubusercontent.com</c>) and those requests fire at the same moment, so a slow or
    /// rate-limiting host multiplies its own load instead of being queued. One source also fans out
    /// on its own: the Steam Curator source walks up to <c>MaxCuratorPages</c> pages of a single host
    /// back to back.
    ///
    /// The gate serializes requests to one host and caps the total, so a scan is polite to each site
    /// and a hostile or slow site cannot make the plugin stampede it. Waiters are released fairly
    /// (FIFO per semaphore), and a waiter cancelled while queued releases the global slot it already
    /// took instead of leaking it.
    ///
    /// It also spaces a host's requests by the <c>Crawl-delay</c> that host asked for in its robots
    /// file (see <see cref="RobotsPolicy"/> and <see cref="SetCrawlDelay"/>): the per-host slot is
    /// handed out at least that much apart. A host that declares nothing is never waited for, and a
    /// declared delay is capped by <see cref="MaxCrawlDelay"/> — the site's pace is honoured, a site's
    /// attempt to park a user's scan for an hour is not.
    ///
    /// Deadlock analysis: a task holds at most one global slot and one host slot, always takes global
    /// first, and never waits for a third slot while holding a host slot. The holder of a host slot is
    /// therefore always runnable: it is either doing bounded I/O with a request timeout or sitting out a
    /// crawl-delay wait that is capped at <see cref="MaxCrawlDelay"/> and ends when its own
    /// cancellation token fires. Either way every host slot is eventually released and every queued
    /// waiter proceeds. Holding a global slot while waiting out a crawl-delay is deliberate (the wait
    /// belongs to the request that reserved the slot) and is bounded by the same cap: at worst a few
    /// slots idle for a few seconds.
    /// </summary>
    internal sealed class HttpHostGate : IDisposable
    {
        /// <summary>
        /// Maximum number of requests in flight to one host, across all parsers and sources.
        /// One: hitting the same community site from several plugin sources at the same instant is
        /// exactly the pattern that gets a shared IP rate-limited or blocked.
        /// </summary>
        internal const int DefaultPerHostConcurrency = 1;

        /// <summary>
        /// Maximum number of requests in flight overall, whatever host they target. Bounds the
        /// connection pressure one scan can put on the machine and on the network.
        /// </summary>
        internal const int DefaultMaxConcurrentRequests = 4;

        /// <summary>
        /// Longest per-request spacing this gate will make a scan wait, whatever a robots file asks
        /// for. The policy that parses the file caps at the same value; this is the enforcement point,
        /// so a delay handed to the gate by any other caller is bounded too.
        /// </summary>
        internal static readonly TimeSpan MaxCrawlDelay = TimeSpan.FromSeconds(30);

        /// <summary>The gate every parser in the process shares (the production limits).</summary>
        internal static readonly HttpHostGate Shared = new HttpHostGate();

        private readonly int _perHostConcurrency;
        private readonly int _maxConcurrentRequests;
        private readonly SemaphoreSlim _global;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _hosts =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-host request spacing a robots file asked for (absent = no spacing).</summary>
        private readonly ConcurrentDictionary<string, TimeSpan> _crawlDelay =
            new ConcurrentDictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-host earliest next-request time (Utc), the schedule that spacing writes.</summary>
        private readonly ConcurrentDictionary<string, DateTime> _nextAllowedUtc =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gate with the production limits.</summary>
        internal HttpHostGate()
            : this(DefaultPerHostConcurrency, DefaultMaxConcurrentRequests)
        {
        }

        /// <summary>
        /// Gate with explicit limits. Unit tests use their own instance so they never contend with
        /// each other over shared static state.
        /// </summary>
        /// <param name="perHostConcurrency">Concurrent requests allowed to one host (min 1).</param>
        /// <param name="maxConcurrentRequests">Concurrent requests allowed overall (min 1).</param>
        internal HttpHostGate(int perHostConcurrency, int maxConcurrentRequests)
        {
            if (perHostConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(perHostConcurrency));
            if (maxConcurrentRequests < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrentRequests));
            _perHostConcurrency = perHostConcurrency;
            _maxConcurrentRequests = maxConcurrentRequests;
            _global = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
        }

        /// <summary>Number of requests one host may have in flight at once (this gate's limit).</summary>
        internal int PerHostConcurrency => _perHostConcurrency;

        /// <summary>Number of requests this gate allows in flight overall.</summary>
        internal int MaxConcurrentRequests => _maxConcurrentRequests;

        /// <summary>Free global slots right now (diagnostics/tests).</summary>
        internal int GlobalAvailable => _global.CurrentCount;

        /// <summary>Free slots for <paramref name="host"/> right now (diagnostics/tests).</summary>
        internal int AvailableFor(string? host) => GateFor(host).CurrentCount;

        /// <summary>Hosts this gate has created a slot pool for (diagnostics/tests).</summary>
        internal string[] TrackedHosts() => _hosts.Keys.ToArray();

        /// <summary>
        /// Takes a request slot for <paramref name="host"/>, waiting until both the global cap and the
        /// per-host cap allow the request. Release it in a <c>finally</c> with <see cref="Exit"/>.
        /// </summary>
        /// <param name="host">Host the request targets (an absolute URL is accepted too).</param>
        /// <param name="cancellationToken">Cancellation of the scan that owns this request.</param>
        /// <exception cref="OperationCanceledException">
        /// The wait was cancelled; the global slot already taken is released before rethrowing.
        /// </exception>
        internal async Task EnterAsync(string? host, CancellationToken cancellationToken)
        {
            await _global.WaitAsync(cancellationToken).ConfigureAwait(false);
            var hostTaken = false;
            try
            {
                await GateFor(host).WaitAsync(cancellationToken).ConfigureAwait(false);
                hostTaken = true;
                await WaitForCrawlDelayAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Never hold a global slot for a request that did not get its host slot: with 12
                // sources and a cap of 4, one leaked slot per failed request stalls a whole scan. Same
                // rule for a host slot released into a crawl-delay wait that was cancelled.
                if (hostTaken)
                    GateFor(host).Release();
                _global.Release();
                throw;
            }
        }

        /// <summary>
        /// Spacing the host asked for. When its robots file declared a <c>Crawl-delay</c> that applies
        /// to us, this host's requests are scheduled at least that far apart: the next allowed time is
        /// advanced <em>before</em> waiting, so a waiter that takes the host slot while this one waits
        /// is scheduled after it instead of behind it. A host with no declared delay is not waited for
        /// at all, and a delay beyond <see cref="MaxCrawlDelay"/> is cut to that cap.
        /// </summary>
        private async Task WaitForCrawlDelayAsync(string? host, CancellationToken cancellationToken)
        {
            var key = Normalize(host);
            TimeSpan crawlDelay;
            if (!_crawlDelay.TryGetValue(key, out crawlDelay) || crawlDelay <= TimeSpan.Zero)
                return;

            var nowUtc = DateTime.UtcNow;
            DateTime nextAllowed;
            if (!_nextAllowedUtc.TryGetValue(key, out nextAllowed) || nextAllowed < nowUtc)
                nextAllowed = nowUtc;

            _nextAllowedUtc[key] = nextAllowed + crawlDelay;

            var hold = nextAllowed - nowUtc;
            if (hold > TimeSpan.Zero)
                await Task.Delay(hold, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Records the request spacing this host asked for (null or non-positive clears it). Called by
        /// the fetch path with what <see cref="RobotsPolicy"/> read from the host's robots file; the
        /// gate, not the policy, is where the cap is enforced.
        /// </summary>
        internal void SetCrawlDelay(string? host, TimeSpan? delay)
        {
            var key = Normalize(host);
            if (!delay.HasValue || delay.Value <= TimeSpan.Zero)
            {
                _crawlDelay.TryRemove(key, out _);
                return;
            }
            _crawlDelay[key] = delay.Value > MaxCrawlDelay ? MaxCrawlDelay : delay.Value;
        }

        /// <summary>The spacing currently in force for <paramref name="host"/> (zero = none). Diagnostics/tests.</summary>
        internal TimeSpan CrawlDelayFor(string? host)
        {
            TimeSpan delay;
            return _crawlDelay.TryGetValue(Normalize(host), out delay) ? delay : TimeSpan.Zero;
        }

        /// <summary>
        /// The earliest time this host's next request is scheduled for (Utc, <see cref="DateTime.MinValue"/>
        /// when nothing is scheduled). The observable side of the spacing: a test can read the schedule
        /// instead of timing a real wait.
        /// </summary>
        internal DateTime NextAllowedUtcFor(string? host)
        {
            DateTime next;
            return _nextAllowedUtc.TryGetValue(Normalize(host), out next) ? next : DateTime.MinValue;
        }

        /// <summary>Releases the host slot and the global slot taken by <see cref="EnterAsync"/>.</summary>
        internal void Exit(string? host)
        {
            GateFor(host).Release();
            _global.Release();
        }

        /// <summary>
        /// Host part of an absolute URL, i.e. the gate key for a request. Anything that is not an
        /// absolute URL with a host maps to <see cref="string.Empty"/> (one shared pool), so a
        /// malformed URL still gets gated instead of bypassing the gate.
        /// </summary>
        internal static string HostOf(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                return uri.Host;
            return string.Empty;
        }

        private SemaphoreSlim GateFor(string? host)
            => _hosts.GetOrAdd(Normalize(host), _ => new SemaphoreSlim(_perHostConcurrency, _perHostConcurrency));

        private static string Normalize(string? host)
        {
            var h = host ?? string.Empty;
            if (h.Length == 0)
                return string.Empty;
            // Accept either a bare host or a full URL: both must land on the same pool.
            return h.Contains("://") ? HostOf(h) : h;
        }

        /// <summary>
        /// Releases the semaphores this gate owns. The process-lifetime <see cref="Shared"/> is never
        /// disposed (it is a static, and a scan can be running when another component decides to shut
        /// down); a short-lived gate — the per-test seam — owns its own wait handles and can release
        /// them. Disposing while a request is still inside the gate is an owner bug: the in-flight
        /// Enter/Exit then throws <see cref="ObjectDisposedException"/>, which is the intended signal
        /// rather than a silent slot leak.
        /// </summary>
        public void Dispose()
        {
            _global.Dispose();
            foreach (var pair in _hosts)
                pair.Value.Dispose();
            _hosts.Clear();
            _crawlDelay.Clear();
            _nextAllowedUtc.Clear();
            GC.SuppressFinalize(this);
        }
    }
}
