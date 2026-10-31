using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Reads each host's <c>robots.txt</c> once per process and turns the <c>Crawl-delay</c> it
    /// declares into a minimum spacing between this plugin's requests to that host
    /// (see <see cref="HttpHostGate.SetCrawlDelay"/>).
    ///
    /// Scope, and why it stops at Crawl-delay: this plugin's whole job is
    /// reading other people's mod-catalogue pages, and a sizeable share of those pages sit behind a
    /// <c>Disallow</c> that was written for generic search crawlers, not for a user's own library
    /// tool. Interpreting <c>Disallow</c> would silently switch sources off and the user would see an
    /// empty tag set with no idea why. <c>Crawl-delay</c>, on the other hand, is a request about load
    /// and obeying it costs nothing except a short pause. So: the pace a host asks for is honoured,
    /// the path rules are read as information about a site's intent and are not enforced. That is a
    /// deliberate, documented position, not an oversight.
    ///
    /// Bounded by construction, because a robots file is attacker-chosen text like any other response:
    /// <list type="bullet">
    /// <item>one fetch per host per process (three plugin sources on <c>store.steampowered.com</c>
    /// share one lookup, and a host whose robots file fails is not asked again);</item>
    /// <item>the body is read with the same byte cap as any other response, at a fraction of it;</item>
    /// <item>the delay is capped at <see cref="MaxCrawlDelay"/>: a site that wants an hour between
    /// requests does not get an hour of a user sitting in front of a scan.</item>
    /// </list>
    ///
    /// Requests are sent with no <c>User-Agent</c> header, so per RFC 9309 only records written for
    /// <c>User-agent: *</c> apply to this plugin — and only those records are parsed here. A
    /// <c>Crawl-delay</c> that appears with no applicable user-agent line is ignored, and where
    /// several applicable records disagree the largest delay wins (the polite reading).
    /// </summary>
    internal sealed class RobotsPolicy
    {
        /// <summary>The lookup every parser shares (one robots file per host per process).</summary>
        internal static readonly RobotsPolicy Shared = new RobotsPolicy();

        /// <summary>Longest Crawl-delay honoured. Beyond this the cap is used instead of the ask.</summary>
        internal static readonly TimeSpan MaxCrawlDelay = TimeSpan.FromSeconds(30);

        /// <summary>Byte cap on a robots file. Real ones are a few kilobytes; this is headroom, not a licence.</summary>
        internal const int MaxRobotsBytes = 64 * 1024;

        /// <summary>One entry per host (scheme + host + port, i.e. the robots file's own authority).</summary>
        private readonly ConcurrentDictionary<string, Task<TimeSpan?>> _lookup =
            new ConcurrentDictionary<string, Task<TimeSpan?>>();

        /// <summary>Guards <see cref="_lookup"/> so a host is fetched once even when several sources ask at the same instant.</summary>
        private readonly object _lookupLock = new object();

        /// <summary>
        /// The Crawl-delay to space requests to <paramref name="requestUri"/>'s host by, or
        /// <see langword="null"/> when the host declares none for us (no robots file, an unreadable
        /// one, no applicable record, or a delay we will not wait out beyond the cap).
        /// Cached: the first call for a host performs the fetch, every later call reuses its result.
        /// </summary>
        /// <param name="requestUri">Any request about to be made to that host (its scheme/host/port select the robots file).</param>
        /// <param name="fetchRobots">Reads the robots file (injected so it goes through the parser's shared client, and so tests can supply one).</param>
        /// <param name="cancellationToken">Cancellation of the scan that owns the request.</param>
        internal Task<TimeSpan?> GetCrawlDelayAsync(
            Uri requestUri,
            Func<Uri, CancellationToken, Task<string?>> fetchRobots,
            CancellationToken cancellationToken)
        {
            if (requestUri == null)
                throw new ArgumentNullException(nameof(requestUri));
            if (fetchRobots == null)
                throw new ArgumentNullException(nameof(fetchRobots));

            var authority = RobotsAuthority(requestUri);
            if (authority.Length == 0)
                return Task.FromResult<TimeSpan?>(null);

            Task<TimeSpan?> cached;
            lock (_lookupLock)
            {
                if (!_lookup.TryGetValue(authority, out cached))
                {
                    cached = ResolveAsync(authority, fetchRobots, cancellationToken);
                    _lookup[authority] = cached;
                }
            }

            // A lookup cancelled by the scan that started it must not be what the next scan reuses.
            // The entry has to be evicted after the fact: ResolveAsync reports the cancellation through
            // the task it returns, and that task is already parked in the dictionary by the time it
            // faults. Identity is checked so a newer lookup for the same host is never evicted by an
            // older one finishing late.
            // TaskScheduler.Default: the continuation touches only this class's dictionary, so it must
            // not inherit whatever context the caller awaited from (Playnite's dispatcher in production).
            cached.ContinueWith(
                faulted =>
                {
                    lock (_lookupLock)
                    {
                        Task<TimeSpan?> current;
                        if (_lookup.TryGetValue(authority, out current) && ReferenceEquals(current, faulted))
                            _lookup.TryRemove(authority, out _);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnCanceled,
                TaskScheduler.Default);

            return cached;
        }

        /// <summary>
        /// One robots file, parsed once. A fetch that fails (404, 403, a transport error, an oversized
        /// or unparseable file) is cached as "no delay": the plugin has asked that host once and gets
        /// no information from it, and asking again per request would be exactly the load the policy
        /// exists to avoid.
        /// </summary>
        // A robots-policy lookup is politeness bookkeeping: failing to read or parse robots.txt -
        // including a fault in this file's own parser - must never turn into a source failure the
        // user sees. The scan's own cancellation is rethrown above so a cancelled scan still stops.
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "Bookkeeping for politeness must not fail the request it guards.")]
        private static async Task<TimeSpan?> ResolveAsync(
            string authority,
            Func<Uri, CancellationToken, Task<string?>> fetchRobots,
            CancellationToken cancellationToken)
        {
            TimeSpan? delay = null;
            try
            {
                var body = await fetchRobots(new Uri(authority + "/robots.txt"), cancellationToken).ConfigureAwait(false);
                delay = ParseCrawlDelay(body);
            }
            catch (OperationCanceledException)
            {
                // No information gained; rethrown so the caller stops with its scan. The dictionary
                // entry is dropped by the eviction continuation in GetCrawlDelayAsync.
                throw;
            }
            catch (Exception)
            {
                delay = null;
            }
            return delay;
        }

        /// <summary>
        /// Extracts the Crawl-delay that applies to this plugin from a robots file. Case-insensitive
        /// field names, <c>#</c> comments stripped, records separated by blank lines, and only a
        /// record whose <c>User-agent</c> is <c>*</c> counts (this plugin sends no User-Agent, so a
        /// record naming some other bot says nothing about us). Values that are not a finite positive
        /// number are ignored; the largest applicable delay wins; anything above
        /// <see cref="MaxCrawlDelay"/> is capped.
        /// </summary>
        /// <returns>The delay to space requests by, or <see langword="null"/> when the file declares none for us.</returns>
        internal static TimeSpan? ParseCrawlDelay(string? robotsBody)
        {
            // The net462 targeting pack carries no nullability annotation on IsNullOrWhiteSpace, so
            // the compiler cannot flow the guard below into the Split - the ?? is for the compiler, not
            // for a case this method would otherwise miss.
            var body = robotsBody ?? string.Empty;
            if (body.Trim().Length == 0)
                return null;

            TimeSpan? largest = null;
            bool appliesToUs = false;

            foreach (var rawLine in body.Split('\n'))
            {
                var line = rawLine.Trim();
                var comment = line.IndexOf('#');
                if (comment >= 0)
                    line = line.Substring(0, comment).Trim();
                if (line.Length == 0)
                {
                    // A blank line ends a record: the next field starts a fresh group, which does not
                    // apply to us until a "User-agent: *" line says it does.
                    appliesToUs = false;
                    continue;
                }

                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;

                var field = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();

                if (field.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
                {
                    appliesToUs = value == "*";
                    continue;
                }

                if (!appliesToUs || !field.Equals("Crawl-delay", StringComparison.OrdinalIgnoreCase))
                    continue;

                double seconds;
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                    continue;
                if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
                    continue;

                if (seconds > MaxCrawlDelay.TotalSeconds)
                    seconds = MaxCrawlDelay.TotalSeconds;

                var delay = TimeSpan.FromSeconds(seconds);
                if (largest == null || delay > largest)
                    largest = delay;
            }

            return largest;
        }

        /// <summary>
        /// The scheme + host + port a robots file belongs to (RFC 9309 resolves it against the request
        /// URL's authority, so an http test server and an https production host do not share an entry).
        /// Empty for anything without one, which simply means "no policy known".
        /// </summary>
        private static string RobotsAuthority(Uri requestUri)
        {
            if (!requestUri.IsAbsoluteUri || string.IsNullOrEmpty(requestUri.Host))
                return string.Empty;
            return requestUri.GetLeftPart(UriPartial.Authority);
        }

        /// <summary>Hosts this policy has already asked (diagnostics/tests).</summary>
        internal string[] TrackedAuthorities()
        {
            lock (_lookupLock)
                return new System.Collections.Generic.List<string>(_lookup.Keys).ToArray();
        }
    }
}
