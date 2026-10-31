using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Abstract base class for all VR mod data source parsers.
    /// Provides shared functionality including HTTP client management, name normalization,
    /// and the Dispose pattern for cleanup.
    ///
    /// Each concrete parser (RototRobotVrModParser, Flat2VrParser, UeVrProfilesParser)
    /// inherits from this class and implements FetchEntriesAsync() and GetCanonicalName().
    ///
    /// Key responsibilities:
    /// 1. Manage a shared HttpClient with thread-safe initialization (single instance for all parsers)
    /// 2. Provide static utility methods for name normalization
    /// 3. Implement IDisposable to clean up the shared HttpClient
    ///
    /// Thread Safety:
    /// The HttpClient is shared across all parser instances via a lock-protected property.
    /// This prevents multiple HttpClient instances and allows proper disposal.
    /// </summary>
    public abstract class BaseVrModParser : IVrModSource, IDisposable
    {
        /// <summary>
        /// Handler used for all HTTP requests. Created once and reused across parsers.
        /// A separate lock object prevents race conditions during HttpClient creation.
        ///
        /// AllowAutoRedirect is OFF on purpose (the framework default is on, up to 20 silent hops):
        /// a hop is then followed here in <see cref="FetchStringAsync"/>, where the target can be
        /// checked against the host this plugin meant to talk to. Left automatic, a link published by
        /// a source - or by an aggregator mirroring it - could move the request to another site and
        /// the body served THERE would be parsed as this source's game list, feeding spoofed names,
        /// categories and Links into the library with nothing in the log saying the site changed.
        /// </summary>
        private static readonly HttpClientHandler _sharedHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        private static readonly object _httpLock = new();
        private static HttpClient? _sharedHttp;
        private static bool _httpDisposed;

        /// <summary>
        /// Thread-safe shared HttpClient instance used by all parsers.
        /// Created on first access with a lock to prevent multiple instances.
        /// Timeout is set to 30 seconds to prevent hanging on slow connections.
        /// After the parser is disposed, any further access throws ObjectDisposedException.
        ///
        /// This is shared (static) rather than per-instance to reduce socket exhaustion
        /// when multiple sources are fetched concurrently during a scan.
        /// </summary>
        protected static HttpClient SharedHttp
        {
            get
            {
                lock (_httpLock)
                {
                    if (_httpDisposed)
                        throw new ObjectDisposedException(nameof(BaseVrModParser));
                    if (_sharedHttp == null)
                    {
                        _sharedHttp = new HttpClient(_sharedHandler, false) { Timeout = TimeSpan.FromSeconds(30) };
                    }
                    return _sharedHttp;
                }
            }
        }

        /// <summary>
        /// Maximum size in bytes of a single HTTP response accepted by <see cref="FetchStringAsync"/>.
        /// Every real source serves well under a megabyte; this cap is defense in depth so a
        /// compromised or misbehaving source cannot grow the Playnite process out of memory. 16 MB is
        /// already ~16x the largest page any known source serves, and a response that big is a 32 MB
        /// UTF-16 string that every parse pass then walks over — a bigger cap buys parse cost, not
        /// safety.
        /// </summary>
        public const int MaxResponseBytes = 16 * 1024 * 1024;

        /// <summary>
        /// Ceiling for one regex operation on content that came from a source website
        /// (<see cref="MatchScanned"/>, <see cref="MatchesScanned"/>, <see cref="ReplaceScanned"/>).
        ///
        /// Every pattern in this plugin is literal-shaped (<c>&lt;span class="title-main"&gt;</c>,
        /// <c>apiKey:"…"</c>) and linear on realistic input, but they run over text the plugin does not
        /// control, and a pattern edit (or a page laid out in a way nobody tested) is how a regex
        /// turns quadratic. Without a ceiling that cost is paid on Playnite's scan thread and looks
        /// like the application hanging. With one, the operation throws
        /// <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/> and the scan's
        /// per-source catch reports that source as failed for this run while the rest of the library
        /// is still processed.
        ///
        /// 5 seconds per operation: long enough for a legitimate pass over the largest page the
        /// plugin accepts, short enough that a pathological pattern cannot hold a thread. Deliberately
        /// NOT set as a process-wide default (<c>AppContext</c> regex switch): this plugin is loaded
        /// into Playnite's process next to other plugins, and a global regex timeout is not ours to
        /// impose on them.
        /// </summary>
        internal static readonly TimeSpan ScannedRegexTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// <see cref="Regex.Match(string,string,RegexOptions)"/> with <see cref="ScannedRegexTimeout"/>.
        /// Use for any pattern applied to fetched page content; keep plain <see cref="Regex"/> calls for
        /// text this plugin produced itself.
        /// </summary>
        /// <param name="input">Content from a source website.</param>
        /// <param name="pattern">This plugin's own pattern (never content from the site).</param>
        /// <param name="options">Regex options.</param>
        protected static Match MatchScanned(string input, string pattern, RegexOptions options = RegexOptions.None)
            => Regex.Match(input, pattern, options, ScannedRegexTimeout);

        /// <summary>
        /// <see cref="Regex.Matches(string,string,RegexOptions)"/> with <see cref="ScannedRegexTimeout"/>
        /// per match. The result is lazy: the timeout can surface while a <c>foreach</c> is already
        /// part-way through a page, which is why a page-level parse is wrapped by the scan's
        /// per-source catch rather than trusted to finish.
        /// </summary>
        /// <inheritdoc cref="MatchScanned" />
        protected static MatchCollection MatchesScanned(string input, string pattern, RegexOptions options = RegexOptions.None)
            => Regex.Matches(input, pattern, options, ScannedRegexTimeout);

        /// <summary>
        /// <see cref="Regex.Replace(string,string,string,RegexOptions)"/> with
        /// <see cref="ScannedRegexTimeout"/>, for cleanup passes over scraped text.
        /// </summary>
        /// <inheritdoc cref="MatchScanned" />
        protected static string ReplaceScanned(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None)
            => Regex.Replace(input, pattern, replacement, options, ScannedRegexTimeout);

        /// <summary>
        /// Hard cap on the number of lines kept in one run's <see cref="RunLog"/>. The log is
        /// in-memory and per-run, but a source whose page structure breaks can produce a
        /// per-game failure line per listed title (hundreds); the cap keeps the log usable
        /// (and the dialog snappy) — the first line past the cap is replaced by a truncation
        /// marker and every further line is dropped.
        /// </summary>
        public const int MaxRunLogLines = 400;

        /// <inheritdoc />
        public List<string>? RunLog { get; set; }

        /// <summary>
        /// Collapses whitespace and truncates to <paramref name="max"/> characters (with an
        /// ellipsis) so a parser can quote a raw fragment of the source page on a run log line
        /// ("the row that failed to parse") without the line growing without bound.
        /// </summary>
        /// <param name="text">The raw fragment (a tag chunk, a JSON object, a table row...).</param>
        /// <param name="max">Maximum characters kept (default 100).</param>
        protected static string Snip(string? text, int max = 100)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "(empty)";
            var collapsed = ReplaceScanned(text!, @"[\s\u00A0]+", " ").Trim();
            return collapsed.Length <= max ? collapsed : collapsed.Substring(0, max) + "...";
        }

        /// <summary>
        /// Appends one line to this run's <see cref="RunLog"/> (a no-op when no run is in
        /// progress, i.e. <c>RunLog</c> is null — e.g. a parser invoked outside a scan, as in
        /// the parser unit tests). Enforces <see cref="MaxRunLogLines"/>: past the cap a
        /// single truncation marker is appended and further lines are dropped, so a broken
        /// page layout cannot grow the in-memory log without bound.
        /// </summary>
        /// <param name="line">One debug line (no leading/trailing newline).</param>
        protected void LogRun(string line)
        {
            var log = RunLog;
            if (log == null)
                return;
            if (log.Count >= MaxRunLogLines)
            {
                if (log.Count == MaxRunLogLines)
                    log.Add($"... log truncated at {MaxRunLogLines} lines (further lines are not recorded)");
                return;
            }
            log.Add(line);
        }

        /// <summary>
        /// The <see cref="System.Net.Http.HttpClient.Timeout"/> for the shared client, in
        /// seconds. Exposed so the failure description can say "timed out after 30 s".
        /// </summary>
        /// <summary>
        /// Cancellation token of the scan run currently fetching this source, assigned by
        /// <see cref="VrModTagService"/> before its fan-out and cleared back to
        /// <see cref="CancellationToken.None"/> afterwards. Request retries, the shared client's own
        /// request calls and pagination loops honour it, so a cancelled scan (user Cancel, or the
        /// service's wall-clock scan budget) stops talking to the source's site instead of working
        /// through its remaining retries and pages. <see cref="CancellationToken.None"/> when the
        /// parser runs outside a scan (unit tests, single-game metadata fetch).
        /// </summary>
        public CancellationToken ScanToken { get; set; } = CancellationToken.None;

        internal const int HttpTimeoutSeconds = 30;

        /// <summary>
        /// The total number of attempts <see cref="FetchStringAsync"/> makes for one URL before
        /// giving up (one initial attempt + retries). Transient failures (see
        /// <see cref="IsTransientHttpFailure"/>) are retried with a doubling delay
        /// (<see cref="ComputeRetryDelay"/>), so with 3 attempts a persistently failing URL
        /// costs at most the 3 request timeouts plus 1 s + 2 s of backoff — bounded, never
        /// endless. A definitive failure (e.g. HTTP 404) is NOT retried and fails on attempt 1.
        /// </summary>
        internal const int MaxHttpAttempts = 3;

        /// <summary>
        /// How many hops one fetch may follow, and only hops that stay on the host the plugin asked
        /// for (or a host the parser declares as its own mirror, see <see cref="AllowedRedirectHosts"/>).
        /// A source that chains through other sites is not a source any more: every extra hop is a
        /// host that can answer with a page that was never this source's. Five covers the hops real
        /// sites use (http to https, bare domain to <c>www.</c>, one CDN indirection) and nothing near
        /// a loop.
        /// </summary>
        internal const int MaxRedirectHops = 5;

        /// <summary>
        /// Hosts this source may hand the request to, in addition to the host of the URL being
        /// fetched (host equality also accepts the <c>www.</c> variant of the same host, and a
        /// declaration may be written as a host or as that host's URL). A hop may also not change the
        /// port the request was made on, apart from the plain-http to https upgrade. Empty by
        /// default: a redirect that leaves the source's own hosts is refused, and the run log names
        /// the host it tried to move to, so the operator sees a hijacked or mirrored source instead of
        /// quietly parsing somebody else's page. A parser overrides this only for a mirror host its own
        /// site genuinely serves from.
        /// </summary>
        protected virtual System.Collections.Generic.IReadOnlyList<string> AllowedRedirectHosts =>
            System.Array.Empty<string>();

        /// <summary>
        /// The outcome of asking whether one published redirect may be followed: either the absolute
        /// target to fetch next, or the reason it is refused.
        /// </summary>
        internal struct RedirectDecision
        {
            public bool Allowed;
            /// <summary>The target as text, for the run log and the failure message.</summary>
            public string? Target;
            /// <summary>
            /// The target as the <see cref="Uri"/> the policy actually checked. The request is made
            /// with THIS value: a decision that was taken on one Uri must not be re-parsed from text
            /// into a different Uri on the way to the wire.
            /// </summary>
            public Uri? TargetUri;
            public string? Reason;
        }

        /// <summary>
        /// Decides one hop. Instance method (not static) because the parser's
        /// <see cref="AllowedRedirectHosts"/> is part of the answer.
        ///
        /// Refused: a hop budget already spent, a 3xx with no <c>Location</c>, a target that is not a
        /// plain http(s) URL on a named host (the same gate that protects written Links, so
        /// <c>javascript:</c>, <c>file:</c>, a URL with credentials or an over-long target cannot be
        /// reached through a redirect either), and any target on a host that is neither the host asked
        /// for nor a declared mirror.
        /// </summary>
        /// <param name="fromUrl">The URL this plugin requested (absolute).</param>
        /// <param name="location">The response's <c>Location</c> header, relative or absolute.</param>
        /// <param name="hop">Hops already followed in this attempt (0 for the first answer).</param>
        internal RedirectDecision ClassifyRedirectTarget(string fromUrl, Uri? location, int hop)
        {
            var refused = new RedirectDecision();
            if (hop >= MaxRedirectHops)
            {
                refused.Reason = $"already followed {hop} hop(s), limit is {MaxRedirectHops}";
                return refused;
            }
            if (location == null)
            {
                refused.Reason = "no Location header";
                return refused;
            }
            if (!Uri.TryCreate(fromUrl, UriKind.Absolute, out var fromUri))
            {
                refused.Reason = "the requested URL is not absolute";
                return refused;
            }

            Uri absolute;
            try
            {
                absolute = location.IsAbsoluteUri ? location : new Uri(fromUri, location);
            }
            catch (UriFormatException ex)
            {
                // The Location header is scraped material. A target this stack cannot build - a bad
                // scheme, a malformed authority, an over-long URL - is a hop to refuse, not a fault
                // to carry on. Anything else about resolving it is this file's own bug.
                refused.Reason = $"Location could not be resolved: {ex.Message}";
                return refused;
            }

            var targetText = absolute.ToString();
            if (!WebUrl.IsSafeLinkUrl(targetText))
            {
                refused.Reason = $"Location is not a plain http(s) URL on a named host: \"{Snip(targetText, 200)}\"";
                return refused;
            }

            var fromHost = HttpHostGate.HostOf(fromUrl);
            if (SameRegisteredHost(fromHost, absolute.Host))
                return StayOnRegisteredHost(fromUri, absolute, targetText);

            foreach (var mirror in AllowedRedirectHosts)
            {
                if (SameRegisteredHost(NormalizeRegisteredHost(mirror), absolute.Host))
                    return StayOnRegisteredHost(fromUri, absolute, targetText);
            }

            refused.Reason = $"{fromHost} sent the request to {absolute.Host}, which is not one of its hosts";
            return refused;
        }

        /// <summary>
        /// The last check before a hop is taken: the target is on a host this parser asked for, so it
        /// also has to be the service that host was reached on. A hop that changes the port reaches a
        /// different listener on the same machine — <c>127.0.0.1:443</c> and <c>127.0.0.1:9876</c> are
        /// not the same site, and neither is <c>store.steampowered.com:443</c> and a service somebody
        /// put on port 8443 of the same name. The plain-http to https canonical pair (80 to 443) is
        /// allowed because that is the hop real sites publish when they upgrade their own URL.
        /// </summary>
        internal static RedirectDecision StayOnRegisteredHost(Uri fromUri, Uri absolute, string targetText)
        {
            if (absolute.Port == fromUri.Port)
                return new RedirectDecision { Allowed = true, Target = targetText, TargetUri = absolute };

            // The only port change a real site publishes is its own plain-http URL upgrading to
            // https. The reverse (https to http) is a downgrade to plaintext on a host the plugin
            // already trusts, so it is refused; and a hop onto a non-default port is a different
            // listener no matter what the host name says.
            if (fromUri.Scheme == Uri.UriSchemeHttp && fromUri.Port == 80
                && absolute.Scheme == Uri.UriSchemeHttps && absolute.Port == 443)
                return new RedirectDecision { Allowed = true, Target = targetText, TargetUri = absolute };

            return new RedirectDecision
            {
                Reason = $"{absolute.Host} sent the request to port {absolute.Port}, not the port it was asked on ({fromUri.Port})"
            };
        }

        /// <summary>
        /// The host part of a mirror declaration. A parser writes a host (<c>cdn.example.net</c>), but a
        /// declaration written as a URL or with a path (<c>https://cdn.example.net/</c>) must mean the
        /// same mirror instead of silently never matching and refusing that source's own hops.
        /// </summary>
        internal static string NormalizeRegisteredHost(string? declared)
        {
            var value = (declared ?? string.Empty).Trim();
            if (value.Length == 0)
                return string.Empty;
            if (value.Contains("://"))
                return HttpHostGate.HostOf(value);

            var cut = value.IndexOfAny(UrlPathBoundaryChars);
            if (cut >= 0)
                value = value.Substring(0, cut);
            var colon = value.LastIndexOf(':');
            // Only a port suffix is dropped; "[::1]" must not be cut to "[".
            if (colon > 0 && HasOnlyDigits(value, colon + 1))
                value = value.Substring(0, colon);
            return value;
        }

        private static bool HasOnlyDigits(string value, int from)
        {
            if (from >= value.Length)
                return false;
            for (var i = from; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i]))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Host equality that treats <c>example.com</c> and <c>www.example.com</c> as one site (the
        /// redirect a real site publishes when it canonicalises its own host) and nothing else as equal.
        /// </summary>
        internal static bool SameRegisteredHost(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(StripWwwHost(a!), StripWwwHost(b!), StringComparison.OrdinalIgnoreCase);
        }

        private static string StripWwwHost(string host)
            => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host.Substring(4) : host;

        /// <summary>
        /// The backoff delay before retry <paramref name="failedAttempt"/> (1-based: the delay
        /// after the 1st failed attempt, then after the 2nd, …): doubling — 1 s, 2 s, 4 s, …
        /// Capped by <see cref="MaxHttpAttempts"/> in practice (only the delays before attempts
        /// 2…Max are ever taken, i.e. 1 s and 2 s).
        /// </summary>
        /// <param name="failedAttempt">The number of the attempt that just failed (1-based).</param>
        internal static TimeSpan ComputeRetryDelay(int failedAttempt)
        {
            if (failedAttempt < 1)
                throw new ArgumentOutOfRangeException(nameof(failedAttempt));
            return TimeSpan.FromSeconds(1L << (failedAttempt - 1));
        }

        /// <summary>
        /// The backoff delay after <paramref name="failedAttempt"/> failed attempts. Virtual so
        /// tests can override it to zero (the test suite must stay wall-clock-free); production
        /// uses <see cref="ComputeRetryDelay"/>.
        /// </summary>
        /// <param name="failedAttempt">The number of the attempt that just failed (1-based).</param>
        protected virtual TimeSpan RetryDelay(int failedAttempt)
        {
            return ComputeRetryDelay(failedAttempt);
        }

        /// <summary>
        /// The wait before retry <paramref name="failedAttempt"/> failed, taking the server's own
        /// instruction into account (see <see cref="TryReadRetryAfter"/>). A server that answered
        /// 429/503 with <c>Retry-After</c> has already told us when it wants to be asked again, and
        /// that number beats this plugin's guess; anything else falls back to the doubling backoff.
        /// Virtual for the same wall-clock reason as <see cref="RetryDelay"/>.
        /// </summary>
        /// <param name="serverRequested">
        /// The wait the server asked for, already capped by the caller (a longer ask is not retried
        /// at all — see <see cref="MaxServerRequestedWait"/>). Null when the answer carried no
        /// <c>Retry-After</c>.
        /// </param>
        protected virtual TimeSpan RetryDelayFor(int failedAttempt, TimeSpan? serverRequested)
        {
            return serverRequested ?? RetryDelay(failedAttempt);
        }

        /// <summary>
        /// The longest <c>Retry-After</c> this plugin will actually sit through inside a running
        /// scan. A scan is a user-facing operation (Playnite's progress dialog is open, sources are
        /// fetched concurrently), and a hostile or badly-configured page can ask for hours. Past this
        /// limit the source simply fails for this run — which is also the polite outcome: the plugin
        /// neither blocks the scan nor ignores the request by retrying immediately.
        /// </summary>
        internal static readonly TimeSpan MaxServerRequestedWait = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Reads the <c>Retry-After</c> header of an error answer. Both documented forms are
        /// accepted: a number of seconds (<c>Retry-After: 30</c>) and an HTTP date
        /// (<c>Retry-After: Wed, 21 Oct 2026 07:28:00 GMT</c>), which becomes the distance from now.
        /// A missing header, an unparseable value, a negative or zero delay or a date already past
        /// means the server gave no instruction and the doubling backoff is used instead. A VALID ask
        /// of any realistic size is reported as-is and the caller decides whether it can wait that
        /// long (see <see cref="MaxServerRequestedWait"/>) - the point of the politeness rule is that
        /// an explicit "come back later" is never quietly replaced with our own 1 s schedule. Values
        /// beyond a year are treated as garbage (they cannot be a real rate-limit window, and reading
        /// them risks an overflow). Never throws on bad input: this header is attacker-chosen text.
        /// </summary>
        /// <returns>True with the requested delay when the server asked for a sane one.</returns>
        internal static bool TryReadRetryAfter(
            System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter,
            out TimeSpan requested)
        {
            requested = TimeSpan.MinValue;
            if (retryAfter == null)
                return false;

            if (retryAfter.Delta.HasValue)
            {
                var delta = retryAfter.Delta.Value;
                if (delta <= TimeSpan.Zero || delta > TimeSpan.FromDays(365))
                    return false;
                requested = delta;
                return true;
            }

            if (retryAfter.Date.HasValue)
            {
                var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                if (delta <= TimeSpan.Zero || delta > TimeSpan.FromDays(365))
                    return false;
                requested = delta;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a failed attempt is TRANSIENT (worth retrying) or DEFINITIVE (retrying cannot
        /// change the outcome):
        /// <list type="bullet">
        /// <item>NO HTTP response was received (<paramref name="httpStatus"/> is null) — a
        /// transport failure: connection refused or reset, DNS failure, request timeout, proxy
        /// problem. The connection itself never completed, so a retry may well succeed →
        /// TRANSIENT.</item>
        /// <item>A response WAS received: only server-side trouble is transient — 5xx
        /// (the server/CDN is down, restarting, or behind a broken gateway) and 429
        /// (rate-limited: a short pause lets the request through). Every other status is a
        /// definitive statement about the URL (404 moved/removed, 403 forbidden, 400 malformed)
        /// — retrying the same GET gets the same answer → DEFINITIVE.</item>
        /// </list>
        /// Note a success status (2xx/3xx) never reaches this method — those return the body,
        /// they do not fail.
        /// </summary>
        /// <param name="httpStatus">
        /// The HTTP status code if a response was received; <see langword="null"/> when the
        /// request failed at the transport level (no response at all).
        /// </param>
        internal static bool IsTransientHttpFailure(int? httpStatus)
        {
            if (!httpStatus.HasValue)
                return true;
            return httpStatus.Value == 429 || httpStatus.Value >= 500;
        }

        /// <summary>
        /// Fetches <paramref name="url"/> and returns the body as a UTF-8 string, bounded to
        /// <see cref="MaxResponseBytes"/>. Instance method (not static) so every web request a
        /// parser makes is recorded on this run's <see cref="RunLog"/>: the URL, the HTTP
        /// status code, the body size, the elapsed time — and the failure reason when the
        /// request fails (the described failure still propagates to the caller).
        ///
        /// Replaces <c>HttpClient.GetStringAsync</c>, which buffers an unbounded response: if a
        /// source were compromised (or a CDN misbehaved) and streamed gigabytes, the hosting
        /// Playnite process would be pushed out of memory. This helper fails the single source
        /// with an exception instead, leaving the rest of the scan unaffected.
        ///
        /// Failure semantics: a non-success HTTP status (4xx/5xx) is an ERROR, not "no data" —
        /// the source is broken, rate-limited, moved, or down — and the failure carries the
        /// status code and reason text ("HTTP 404 (Not Found)"), so the "Last run" view shows
        /// WHY instead of a generic message. A genuine "no data" is a success status whose
        /// page parsed to zero entries. A transport failure (timeout, DNS, refused/reset
        /// connection) is rethrown with the concrete cause described (<see cref="DescribeHttpFailure"/>).
        ///
        /// Transient failures are RETRIED, bounded by <see cref="MaxHttpAttempts"/> with a
        /// doubling backoff (<see cref="RetryDelay"/>): a transport failure (the connection
        /// refused/reset, DNS, or the request timed out) and a 5xx or 429 status are transient;
        /// a definitive 4xx (404, 403, 400, …) is NOT retried and fails on the first attempt.
        /// Every retry is recorded on the <see cref="RunLog"/> ("attempt N of M failed: …;
        /// retrying in … s"), and a URL that exhausts all attempts names it in both the
        /// run-log line and the thrown message ("(still failing after N attempts)") so the
        /// "Last run" view shows the retry was tried before giving up. A success on a later
        /// attempt is indistinguishable from one on the first (the run log shows the earlier
        /// failed attempts plus the final success line).
        /// </summary>
        /// Redirects are followed here rather than by the HTTP stack: the shared handler has
        /// <c>AllowAutoRedirect = false</c>, each published hop is checked against the host this plugin
        /// asked for (see <see cref="MaxRedirectHops"/> and <see cref="AllowedRedirectHosts"/>), a
        /// followed hop is written to the run log, and a refused hop fails the source definitively with
        /// the host it tried to move to named in the message. Without this, a source's own link (or an
        /// aggregator mirroring it) could point the request at another site and the page served THERE
        /// would be parsed as this source's game list.
        /// </summary>
        /// <param name="url">The absolute URL to fetch.</param>
        /// <summary>
        /// Whether this parser consults the target host's robots file before its requests (see
        /// <see cref="RobotsPolicy"/>). True for every real source. The fetch-path unit tests switch it
        /// off because their loopback server scripts one answer per arriving request, and a robots
        /// lookup is an extra arrival on the same host that would consume a scripted status and a
        /// counted request. The wiring itself is covered by RobotsPolicyTests.
        /// </summary>
        protected virtual bool ConsultsRobotsPolicy => true;

        /// <summary>
        /// The failures the request plumbing itself produces without ever receiving an HTTP answer:
        /// transport errors, the shared client's own timeout surfacing as a cancellation, a disposed
        /// shared client (plugin unload while a scan is in flight), an unsupported proxy, a URL the
        /// stack refuses to build, and the host gate's own waits. Anything else is not a request
        /// failure and is not reported as one.
        /// </summary>
        internal static bool IsRequestPlumbingFailure(Exception ex)
        {
            return ex is HttpRequestException
                || ex is IOException
                || ex is UnauthorizedAccessException
                || ex is OperationCanceledException
                || ex is ObjectDisposedException
                || ex is NotSupportedException
                || ex is UriFormatException;
        }

        protected async Task<string> FetchStringAsync(string url)
        {
            var elapsed = Stopwatch.StartNew();
            for (int attempt = 1; ; attempt++)
            {
                // The scan owns a cancellation token: a cancelled scan stops at the next request.
                ScanToken.ThrowIfCancellationRequested();

                // One attempt is the URL this plugin asked for, plus the hops that source publishes
                // (AllowAutoRedirect is off on the shared handler, so the hops arrive here as 3xx
                // answers and each one is validated). A retry always starts again from `url`, never
                // from a location a server suggested.
                var target = url;
                Uri? targetUri = null;
                for (int hop = 0; ; hop++)
                {
                    var host = HttpHostGate.HostOf(target);

                    // Politeness: the pace this host asked for in its robots file (one lookup per host
                    // per process) is handed to the gate before the request is made. Asked before
                    // taking the host slot on purpose - the robots file is itself a request to that
                    // host, and it must not queue behind a slot this task already holds.
                    TimeSpan? crawlDelay = null;
                    if (ConsultsRobotsPolicy)
                    {
                        try
                        {
                            crawlDelay = await RobotsPolicy.Shared.GetCrawlDelayAsync(
                                targetUri ?? new Uri(target), FetchRobotsTextAsync, ScanToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ScanToken.IsCancellationRequested)
                        {
                            elapsed.Stop();
                            LogRun($"GET {target} cancelled by the running scan after {elapsed.Elapsed.TotalSeconds:0.00} s");
                            throw;
                        }
                    }
                    HttpHostGate.Shared.SetCrawlDelay(host, crawlDelay);

                    HttpResponseMessage? response = null;
                    int? httpStatus = null;
                    string failure = string.Empty;
                    Exception? transportException = null;
                    TimeSpan? serverRequestedWait = null;
                    string? nextTarget = null;
                    Uri? nextTargetUri = null;

                    try
                    {
                        // Serialize requests to the same host and cap total in-flight requests
                        // (HttpHostGate): several plugin sources talk to the same host
                        // (three to store.steampowered.com, three to raw.githubusercontent.com) and all
                        // sources are fetched concurrently, so without a gate a slow or rate-limiting
                        // host gets hit simultaneously instead of being queued. Each hop is gated on
                        // the host it actually goes to.
                        await HttpHostGate.Shared.EnterAsync(host, ScanToken);
                        try
                        {
                            response = await SharedHttp.GetAsync(targetUri ?? new Uri(target), HttpCompletionOption.ResponseHeadersRead, ScanToken);
                        }
                        finally
                        {
                            HttpHostGate.Shared.Exit(host);
                        }
                    }
                    catch (OperationCanceledException) when (ScanToken.IsCancellationRequested)
                    {
                        // The scan was cancelled (user Cancel / scan budget): stop at once, do not
                        // retry. A cancellation that came from the client's own Timeout instead is a
                        // transport failure and falls through to the transient-retry path below.
                        elapsed.Stop();
                        LogRun($"GET {target} cancelled by the running scan after {elapsed.Elapsed.TotalSeconds:0.00} s");
                        throw;
                    }
                    catch (Exception ex) when (IsRequestPlumbingFailure(ex))
                    {
                        // Transport failure: the request never got an HTTP response (timeout,
                        // DNS, connection refused/reset, proxy). Described with the concrete
                        // cause; always transient (see IsTransientHttpFailure). Anything outside
                        // that vocabulary is not a request failure and is not dressed up as one -
                        // it propagates to the handler that reports the source as failed.
                        failure = DescribeHttpFailure(ex);
                        transportException = ex;
                    }

                    bool followedHop = false;
                    if (response != null)
                    {
                        using (response)
                        {
                            httpStatus = (int)response.StatusCode;
                            string reason = string.IsNullOrEmpty(response.ReasonPhrase)
                                ? ""
                                : " " + response.ReasonPhrase;
                            if (httpStatus.Value >= 300 && httpStatus.Value < 400)
                            {
                                var decision = ClassifyRedirectTarget(target, response.Headers.Location, hop);
                                if (decision.Allowed)
                                {
                                    LogRun($"GET {target} -> HTTP {httpStatus}{reason}; following redirect to {decision.Target} (hop {hop + 1} of {MaxRedirectHops})");
                                    nextTarget = decision.Target;
                                    nextTargetUri = decision.TargetUri;
                                    followedHop = true;
                                }
                                else
                                {
                                    // A hop this plugin will not take is DEFINITIVE: the same GET gets
                                    // the same answer, so it is not retried, and the run log names the
                                    // host the source tried to move the request to.
                                    failure = $"HTTP {httpStatus}{reason} redirect refused: {decision.Reason}";
                                }
                            }
                            else if (httpStatus.Value >= 400)
                            {
                                failure = DescribeHttpStatus(httpStatus.Value, response.ReasonPhrase);
                                TimeSpan asked;
                                if (TryReadRetryAfter(response.Headers.RetryAfter, out asked))
                                    serverRequestedWait = asked;
                            }
                            else
                            {
                                // Success: read the bounded body. A "Response too large" failure
                                // here propagates DIRECTLY (no retry) — the server answered fine,
                                // so a retry would just re-fetch the same oversized body.
                                using var stream = await response.Content.ReadAsStreamAsync();
                                var (body, totalBytes) = await ReadBoundedUtf8AsyncWithSize(stream, response.Content.Headers.ContentLength, MaxResponseBytes);
                                elapsed.Stop();
                                LogRun($"GET {target} -> HTTP {httpStatus}{reason}, {totalBytes} bytes in {elapsed.Elapsed.TotalSeconds:0.00} s");
                                return body;
                            }
                        }
                    }

                    if (followedHop)
                    {
                        target = nextTarget!;
                        targetUri = nextTargetUri;
                        continue;
                    }

                    // Failure: a transport error (no response), an HTTP error status, or a refused hop.
                    bool transient = IsTransientHttpFailure(httpStatus);

                    // Politeness first: an answer that says "come back in N" is obeyed, not argued
                    // with. But a scan is user-facing, so an ask beyond MaxServerRequestedWait is
                    // refused outright — the source fails this run instead of the scan sitting there
                    // waiting, and instead of the plugin doing the opposite of what was asked by
                    // retrying on its own 1 s schedule.
                    if (transient && serverRequestedWait.HasValue && serverRequestedWait.Value > MaxServerRequestedWait)
                    {
                        elapsed.Stop();
                        LogRun($"GET {target} FAILED after {elapsed.Elapsed.TotalSeconds:0.00} s: {failure} (asked to wait {serverRequestedWait.Value.TotalSeconds:0} s, more than this plugin waits between attempts: {MaxServerRequestedWait.TotalSeconds:0} s); not retrying this run");
                        throw new HttpRequestException(
                            $"{failure} (server asked for a {serverRequestedWait.Value.TotalSeconds:0} s wait, longer than {MaxServerRequestedWait.TotalSeconds:0} s)",
                            transportException);
                    }

                    if (!transient || attempt >= MaxHttpAttempts)
                    {
                        // Definitive failure, or the bounded retries are exhausted.
                        elapsed.Stop();
                        string retryNote = attempt > 1
                            ? $" (still failing after {attempt} attempts)"
                            : "";
                        string bodyNote = transportException == null ? " (error status; no body read)" : "";
                        LogRun($"GET {target} FAILED after {elapsed.Elapsed.TotalSeconds:0.00} s: {failure}{bodyNote}{retryNote}");
                        throw new HttpRequestException($"{failure}{retryNote}", transportException);
                    }

                    TimeSpan delay = RetryDelayFor(attempt, serverRequestedWait);
                    LogRun($"GET {target} attempt {attempt} of {MaxHttpAttempts} failed: {failure}; retrying in {delay.TotalSeconds:0.##} s{(serverRequestedWait.HasValue ? " (the delay the server asked for)" : "")}");
                    await Task.Delay(delay, ScanToken);
                    break;
                }
            }
        }

        /// <summary>
        /// One human-readable line for a failed web request, more specific than
        /// <see cref="Exception.Message"/> alone: "timed out after N s" when the request was
        /// canceled by the shared client's timeout, the socket error (DNS failure, connection
        /// refused, connection reset) when the connection itself failed, and the raw message
        /// as the last resort. The "Last run" view shows this line as the error, so it must
        /// name the cause a user can act on ("the repo 404s" vs "the site is down").
        /// (A failure that DID receive an HTTP status never reaches this method — those throw
        /// <see cref="DescribeHttpStatus"/>-based messages directly from the response.)
        /// </summary>
        /// <param name="ex">The exception thrown by the failed request.</param>
        internal static string DescribeHttpFailure(Exception ex)
        {
            bool timedOut = false;
            string? socketMessage = null;

            // Walk the exception chain: HttpClient wraps the real cause (a SocketException for
            // DNS/refused/reset, a TaskCanceledException for the timeout) in an
            // HttpRequestException whose own message is often just "An error occurred while
            // sending the request." The deepest link holds the answer.
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is TaskCanceledException)
                    timedOut = true;
                if (e is SocketException se)
                    socketMessage = se.Message;
            }

            if (timedOut)
                return $"request timed out after {HttpTimeoutSeconds} s";
            if (socketMessage != null)
                return socketMessage;
            return ex.Message;
        }

        /// <summary>
        /// Formats an HTTP error status as "HTTP {code} ({reason})" — the status code with the
        /// reason text a user can match against ("Not Found", "Service Unavailable"). The
        /// server's own <paramref name="reasonPhrase"/> wins when present; otherwise the
        /// standard reason text for the code (see <see cref="StandardReasonPhrase"/>).
        /// </summary>
        /// <param name="status">The HTTP status code (e.g. 404).</param>
        /// <param name="reasonPhrase">The server's reason phrase, or null/empty to use the standard text.</param>
        internal static string DescribeHttpStatus(int status, string? reasonPhrase)
        {
            var text = string.IsNullOrWhiteSpace(reasonPhrase)
                ? StandardReasonPhrase(status)
                : reasonPhrase!.Trim();
            return $"HTTP {status} ({text})";
        }

        /// <summary>
        /// The standard reason text for an HTTP status code ("404" → "Not Found"). Covers the
        /// codes a real source is likely to return; anything else falls back to the
        /// <see cref="HttpStatusCode"/> enum name so an unknown code is still named.
        /// </summary>
        /// <param name="status">The HTTP status code.</param>
        internal static string StandardReasonPhrase(int status)
        {
            switch (status)
            {
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 410: return "Gone";
                case 429: return "Too Many Requests";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                default:
                    return Enum.IsDefined(typeof(HttpStatusCode), (HttpStatusCode)status)
                        ? Enum.GetName(typeof(HttpStatusCode), (HttpStatusCode)status)!
                        : "Unknown";
            }
        }

        /// <summary>
        /// Reads <paramref name="stream"/> as a UTF-8 string, enforcing <paramref name="maxBytes"/> in
        /// two places: up front from the declared Content-Length (so an oversized body is never
        /// buffered at all) and on every read (chunked/streaming responses carry no length
        /// header). Throws <see cref="InvalidOperationException"/> when the limit is exceeded.
        /// </summary>
        /// <param name="stream">The response body stream.</param>
        /// <param name="contentLength">The declared Content-Length, if the server sent one.</param>
        /// <param name="maxBytes">Maximum accepted body size in bytes.</param>
        /// <summary>
        /// Reads one host's robots file for <see cref="RobotsPolicy"/>. It goes through the same shared
        /// client as every other request (same handler, same timeout, same no-follow redirect policy),
        /// with its own far smaller byte cap, and answers "nothing known" (null) for anything that is
        /// not a readable 2xx body - a missing or hostile robots file must not change how the plugin
        /// behaves beyond the pause the file actually asks for.
        ///
        /// Deliberately NOT routed through <see cref="HttpHostGate"/>: the caller asks for the policy
        /// before it takes that host's slot, so gating this request here would make it wait on a slot
        /// the waiting task already owns. One robots file per host per process bounds the extra load.
        /// </summary>
        private static async Task<string?> FetchRobotsTextAsync(Uri robotsUri, CancellationToken cancellationToken)
        {
            using var response = await SharedHttp.GetAsync(robotsUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var (text, _) = await ReadBoundedUtf8AsyncWithSize(stream, response.Content.Headers.ContentLength, RobotsPolicy.MaxRobotsBytes).ConfigureAwait(false);
            return text;
        }

        internal static async Task<string> ReadBoundedUtf8Async(Stream stream, long? contentLength, int maxBytes)
        {
            // Awaiting (not t.Result) rethrows the original InvalidOperationException instead of
            // wrapping it in an AggregateException.
            var (text, _) = await ReadBoundedUtf8AsyncWithSize(stream, contentLength, maxBytes);
            return text;
        }

        /// <summary>
        /// Reads <paramref name="stream"/> as a UTF-8 string with the same size enforcement, also
        /// reporting the total bytes actually read (the response body size, for the run log).
        /// </summary>
        /// <param name="stream">The response body stream.</param>
        /// <param name="contentLength">The declared Content-Length, if the server sent one.</param>
        /// <param name="maxBytes">Maximum accepted body size in bytes.</param>
        /// <returns>The decoded text plus the total bytes read from the stream.</returns>
        internal static async Task<(string Text, long Bytes)> ReadBoundedUtf8AsyncWithSize(Stream stream, long? contentLength, int maxBytes)
        {
            if (contentLength.HasValue && contentLength.Value > maxBytes)
                throw new InvalidOperationException(
                    $"Response too large ({contentLength.Value} bytes); limit is {maxBytes} bytes.");

            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new InvalidOperationException(
                        $"Response too large (more than {maxBytes} bytes); limit exceeded.");
                // The copy stays on the async surface (CA1849): a MemoryStream write never blocks,
                // but awaiting here keeps the rule enforced for the whole read path instead of
                // suppressing it per call site.
                await buffer.WriteAsync(chunk, 0, read);
            }

            return (System.Text.Encoding.UTF8.GetString(buffer.ToArray()), total);
        }

        /// <summary>
        /// Creates a new parser instance with the given source identification.
        /// </summary>
        /// <param name="sourceId">Unique identifier for this source (e.g., "RototRobot").</param>
        /// <param name="displayName">Human-readable name shown in settings UI (e.g., "RototRobot VRMods-List").</param>
        /// <param name="websiteUrl">Public, human-readable website for this source (settings UI link).</param>
        protected BaseVrModParser(string sourceId, string displayName, string websiteUrl)
        {
            SourceId = sourceId;
            DisplayName = displayName;
            WebsiteUrl = websiteUrl;
            IsEnabled = true;
        }

        /// <inheritdoc />
        public string SourceId { get; }
        /// <inheritdoc />
        public string DisplayName { get; }
        /// <inheritdoc />
        public string WebsiteUrl { get; }
        /// <inheritdoc />
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Gets an identifier that changes when the source content changes.
        /// Parsers typically compute this as a SHA-256 hash of the fetched content.
        /// Used by VrModTagService.HasSourceChanged() to skip unnecessary network requests.
        /// </summary>
        public abstract string? ChangeIdentifier { get; }

        /// <summary>
        /// Fetches VR mod entries from the external source.
        /// Override this in a subclass to implement the actual fetching and parsing logic.
        /// The default implementation throws NotImplementedException to remind subclasses to override it.
        /// </summary>
        public virtual Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
            => throw new System.NotImplementedException("Override FetchEntriesAsync in a subclass.");

        /// <summary>
        /// Extracts a canonical (clean) game name from an entry.
        /// Override this in a subclass to implement source-specific name extraction.
        /// </summary>
        public abstract string? GetCanonicalName(VrModEntry entry);

        /// <summary>
        /// Normalizes a game name for matching purposes.
        /// Delegates to VrModNameMatcher.Normalize() which handles:
        /// - VR suffix stripping (VR, Flat2VR, UEVR, VR Mod, VR Port, author credits)
        /// - Lowercasing
        /// - Replacing dashes, underscores with spaces
        /// - Removing periods
        /// - Trimming whitespace
        ///
        /// Example: "DOOM VR mod by Author" → "doom"
        /// </summary>
        /// <param name="name">The raw game name to normalize.</param>
        /// <returns>The normalized name suitable for comparison.</returns>
        public static string NormalizeForMatch(string name)
        {
            return VrModNameMatcher.Normalize(name);
        }

        /// <summary>
        /// Strips VR-related suffixes from a game name.
        /// Delegates to VrModNameMatcher.StripVrSuffixes() which handles:
        /// - Parenthetical suffixes: (VR Port), (VR Mod), (UEVR), (VR)
        /// - Author credits: " VR Mod by Author", " by Author", " VR by"
        /// - Trailing VR suffixes: " VR", " Flat2VR"
        ///
        /// Example: "DOOM VR mod by Author" → "DOOM"
        /// Example: "WRATH: AEON OF RUIN VR" → "WRATH: AEON OF RUIN"
        /// </summary>
        /// <param name="name">The raw game name to clean.</param>
        /// <returns>The name with VR suffixes and author credits removed.</returns>
        public static string StripVrSuffixes(string name)
        {
            return VrModNameMatcher.StripVrSuffixes(name);
        }

        /// <summary>
        /// Canonical grouping key for "is this the same game listing", used to merge duplicate
        /// occurrences of one game (a source may list one game more than once — e.g. once per
        /// category). Applies the same VR-suffix stripping as matching, then keeps only
        /// lowercase letters/digits, so case, punctuation, and spacing variants of one name
        /// group together (e.g. "Jedi Knight II: Jedi Outcast" vs "Jedi Knight II - Jedi
        /// Outcast"). Shared by the parsers and the tag service (see <see cref="MergeDuplicateGames"/>).
        /// </summary>
        public static string DuplicateGroupKey(string name)
        {
            var stripped = VrModNameMatcher.StripVrSuffixes(name ?? string.Empty);
            var sb = new StringBuilder(stripped.Length);
            foreach (var ch in stripped)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Merges duplicate occurrences of one game into a single entry (a source may list one
        /// game more than once — e.g. once per category, like the RototRobot site does, or a
        /// loose parse pass may find the same title several times). The merged entry carries
        /// the UNION of all occurrences' features, so one game = one entry = one review row =
        /// one decision = one stable tag set (the same "one entry per game, union of features"
        /// shape as the PCVR Central meta-mod). Without this, the duplicate entries share one
        /// per-mapping decision key and the changed-tag baseline ping-pongs between their tag
        /// sets on every scan (a spurious "Changed" row appears and moves between the
        /// duplicates).
        ///
        /// Shared, source-agnostic helper: the tag service applies it to EVERY source's
        /// entries after the fetch (one entry per game, union of features — all sources
        /// behave the same), and parsers may apply it themselves as well (e.g.
        /// RototRobotVrModParser does, so its parse output is already one entry per game).
        /// For an already-merged list this is a no-op that returns an equal list.
        ///
        /// Merge rules (first-seen order preserved): GameName / SteamAppId / Author / SourceUrl
        /// / SourceId / AdditionalInfo — first occurrence (first non-null for the optionals);
        /// Categories / Qualities / Motions / Statuses — deduped union, case-insensitive, first-seen
        /// order; Is6DOF / IsFirstPerson / IsThirdPerson — true if ANY occurrence is true
        /// (the parsers only ever emit true or null), else null.
        /// </summary>
        public static List<VrModEntry> MergeDuplicateGames(List<VrModEntry> entries)
        {
            var result = new List<VrModEntry>(entries.Count);
            var byKey = new Dictionary<string, VrModEntry>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var key = DuplicateGroupKey(entry.GameName);
                // Unnamed entries (defensive; the parsers skip them) cannot be grouped: give
                // each its own key so it is kept as-is.
                if (key.Length == 0)
                    key = "\u0000" + i;
                if (byKey.TryGetValue(key, out var merged))
                {
                    MergeFeaturesInto(merged, entry);
                }
                else
                {
                    merged = new VrModEntry
                    {
                        GameName = entry.GameName,
                        Categories = new List<string>(entry.Categories),
                        Qualities = new List<string>(entry.Qualities),
                        Motions = new List<string>(entry.Motions),
                        Statuses = new List<string>(entry.Statuses),
                        Is6DOF = entry.Is6DOF,
                        IsFirstPerson = entry.IsFirstPerson,
                        IsThirdPerson = entry.IsThirdPerson,
                        SteamAppId = entry.SteamAppId,
                        Author = entry.Author,
                        SourceUrls = new List<string>(entry.SourceUrls),
                        SourceId = entry.SourceId,
                        AdditionalInfo = entry.AdditionalInfo
                    };
                    byKey[key] = merged;
                    result.Add(merged);
                }
            }
            return result;
        }

        /// <summary>
        /// Folds one occurrence's features into a merged entry (see <see cref="MergeDuplicateGames"/>):
        /// unions the category/quality/motion/status lists and the per-game deep links (case-insensitive,
        /// first-seen order — a merged game keeps every distinct deep link, each contributing its
        /// own Playnite link), ORs the facet flags (true wins; the parsers never emit false), and
        /// fills optional fields (SteamAppId / Author / SourceId / AdditionalInfo) only when still unset.
        /// </summary>
        private static void MergeFeaturesInto(VrModEntry target, VrModEntry source)
        {
            foreach (var c in source.Categories)
                if (target.Categories.Find(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase)) == null)
                    target.Categories.Add(c);
            foreach (var q in source.Qualities)
                if (target.Qualities.Find(x => string.Equals(x, q, StringComparison.OrdinalIgnoreCase)) == null)
                    target.Qualities.Add(q);
            foreach (var m in source.Motions)
                if (target.Motions.Find(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase)) == null)
                    target.Motions.Add(m);
            foreach (var s in source.Statuses)
                if (target.Statuses.Find(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase)) == null)
                    target.Statuses.Add(s);
            if (source.Is6DOF == true) target.Is6DOF = true;
            if (source.IsFirstPerson == true) target.IsFirstPerson = true;
            if (source.IsThirdPerson == true) target.IsThirdPerson = true;
            foreach (var url in source.SourceUrls)
            {
                // SourceUrls is normally already validated by VrModEntry's setter; the merge writes
                // straight into the target list, so re-check here rather than trusting the source.
                if (!WebUrl.IsSafeLinkUrl(url))
                    continue;
                if (target.SourceUrls.Find(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase)) != null)
                    continue;
                target.SourceUrls.Add(url);
            }
            if (target.SteamAppId == null) target.SteamAppId = source.SteamAppId;
            if (target.Author == null) target.Author = source.Author;
            if (target.SourceId == null) target.SourceId = source.SourceId;
            if (target.AdditionalInfo == null) target.AdditionalInfo = source.AdditionalInfo;
        }

        /// <summary>
        /// Computes a SHA-256 hash of the given content string and returns it as a Base64-encoded string.
        ///
        /// This is used by parsers to generate ChangeIdentifiers for detecting when
        /// the source content has been updated. The hash is compared against the
        /// previously stored hash to determine if a network fetch is needed.
        ///
        /// This method is shared (protected static) to eliminate duplication across
        /// all three parser subclasses.
        /// </summary>
        /// <param name="content">The string content to hash.</param>
        /// <returns>Base64-encoded SHA-256 hash of the content.</returns>
        protected static string ComputeHash(string content)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
            return Convert.ToBase64String(bytes);
        }

        // --- IDisposable Implementation ---

        private bool _disposed;
        private static readonly char[] UrlPathBoundaryChars = new[] { '/', '?', '#', '\\' };

        /// <summary>
        /// Releases the shared HttpClient instance.
        /// After disposal, any attempt to use SharedHttp will throw ObjectDisposedException.
        /// This is called by the plugin's Dispose() method when Playnite shuts down.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the shared state. Overridable so a derived parser that owns its own disposable
        /// state cleans up in the override and calls <c>base.Dispose(disposing)</c> instead of
        /// shadowing <see cref="Dispose()"/>.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (!disposing)
                return;
            lock (_httpLock)
            {
                if (!_httpDisposed)
                {
                    _httpDisposed = true;
                    _sharedHttp?.Dispose();
                    _sharedHttp = null;
                }
            }
        }
    }
}
