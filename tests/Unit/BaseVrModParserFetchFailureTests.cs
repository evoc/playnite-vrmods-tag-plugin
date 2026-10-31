using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for the failure surfacing of <c>BaseVrModParser.FetchStringAsync</c> and its
    /// description helpers: a non-success HTTP status (4xx/5xx) is a failure whose message
    /// carries the status code with its reason text ("HTTP 404 (Not Found)"), and a transport
    /// failure is described with the concrete cause (timeout, socket error) instead of the
    /// generic "An error occurred while sending the request." These strings are what the
    /// settings "Last run" view shows as the error, so they must name the cause.
    ///
    /// Also covers the bounded retry: transient failures (transport errors, 5xx, 429) are
    /// retried up to <see cref="BaseVrModParser.MaxHttpAttempts"/> attempts with backoff
    /// (zeroed out here via the <c>RetryDelay</c> override so the suite stays wall-clock-free);
    /// a definitive 4xx fails on the first attempt and is never retried.
    /// </summary>
    [TestFixture]
    public class BaseVrModParserFetchFailureTests
    {
        /// <summary>A minimal concrete parser exposing the protected fetch helper.</summary>
        private sealed class FetchTestParser : BaseVrModParser
        {
            public FetchTestParser() : base("fetchtest", "FetchTest", "https://example.com/")
            {
            }

            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() =>
                Task.FromResult<IEnumerable<VrModEntry>>(Array.Empty<VrModEntry>());
            public override string? GetCanonicalName(VrModEntry entry) => entry.GameName;

            public Task<string> Fetch(string url) => FetchStringAsync(url);

            // The test suite must stay wall-clock-free: zero the backoff so a retried fetch
            // takes no real time (the retry COUNT and log lines are what these tests check;
            // the delay VALUES are checked against ComputeRetryDelay directly).
            protected override TimeSpan RetryDelay(int failedAttempt) => TimeSpan.Zero;

            /// <summary>
            /// The waits the served answers asked for, in the order the retries were decided (a null
            /// entry means the answer carried no <c>Retry-After</c>). Recorded so the tests can assert
            /// which delay the fetch chose without ever having to wait for it.
            /// </summary>
            public readonly List<TimeSpan?> ServerRequestedWaits = new();

            protected override TimeSpan RetryDelayFor(int failedAttempt, TimeSpan? serverRequested)
            {
                ServerRequestedWaits.Add(serverRequested);
                return TimeSpan.Zero;
            }

            // This harness scripts one answer per arriving request and counts arrivals, so the fetch
            // must not add a robots.txt request to the same host. The robots wiring is tested in
            // RobotsPolicyTests, which owns its own listener.
            protected override bool ConsultsRobotsPolicy => false;
        }

        /// <summary>The outcome of one fetch: the thrown exception (null on success), the body,
        /// and how many HTTP requests the client actually made (retry-count assertions).</summary>
        private sealed class FetchResult
        {
            public HttpRequestException? Exception;
            public string? Body;
            public int RequestsMade;
        }

        private HttpListener _listener = null!;
        private string _baseUrl = null!;
        private FetchTestParser _parser = null!;

        [SetUp]
        public void SetUp()
        {
            // A loopback-only HTTP listener so the fetch goes through the real HttpClient path
            // (status code, reason phrase, body) without touching the internet. The port is
            // found by briefly binding a TcpListener. (Deliberately NOT disposing the parser:
            // BaseVrModParser.Dispose tears down the STATIC shared HttpClient for every test.)
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _baseUrl = $"http://127.0.0.1:{port}";

            _parser = new FetchTestParser();
            _parser.RunLog = new List<string>();
        }

        [TearDown]
        public void TearDown()
        {
            _listener.Close();
        }

        /// <summary>
        /// Races <paramref name="task"/> against a <paramref name="timeoutMs"/> delay and
        /// returns whether the task completed first (a faulted task counts as completed —
        /// <see cref="Task.WhenAny"/> itself never rethrows). The fetch task completes
        /// in-memory as soon as the client has processed its last response, so this tells
        /// the server loop whether the client is about to send another request (a retry) —
        /// the only thing that can keep the fetch pending after a served response.
        /// </summary>
        private static async Task<bool> TaskCompletesWithin(Task task, int timeoutMs)
        {
            return await Task.WhenAny(task, Task.Delay(timeoutMs)) == task;
        }

        /// <summary>
        /// Serves one response per arrived request (per-attempt status, last one repeats) and
        /// runs the fetch, returning the thrown exception / body and how many requests the
        /// client actually made. A failure reads no body. After each served response the
        /// helper waits briefly: a COMPLETED fetch sends no more requests (loop exit); a
        /// still-pending one is mid-retry, so its next request is already in flight and
        /// <c>GetContextAsync</c> is safe to block on.
        /// </summary>
        private async Task<FetchResult> TryFetch(string path, params int[] statuses)
        {
            if (statuses.Length == 0)
                throw new ArgumentException("At least one status is required.", nameof(statuses));

            int served = 0;
            Task<string> fetchTask = _parser.Fetch($"{_baseUrl}{path}");

            while (true)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    break; // listener stopped (TearDown race) — the fetch result decides
                }
                int status = statuses[Math.Min(served, statuses.Length - 1)];
                served++;
                try
                {
                    ctx.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes("body-" + status);
                    await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
                catch (Exception)
                {
                    break; // Connection gone (client gave up) — nothing more to serve.
                }
                if (await TaskCompletesWithin(fetchTask, 100))
                    break;
            }

            var result = new FetchResult();
            try
            {
                result.Body = await fetchTask;
            }
            catch (HttpRequestException e)
            {
                result.Exception = e;
            }
            result.RequestsMade = served;
            return result;
        }

        /// <summary>
        /// Same as <see cref="TryFetch"/>, but every served answer carries a <c>Retry-After</c>
        /// header with <paramref name="retryAfterValue"/> exactly as a server would send it on the
        /// wire (a number of seconds, or an HTTP date). That is the politeness path: a 429/503 that
        /// tells the client when to come back.
        /// </summary>
        private async Task<FetchResult> TryFetchWithRetryAfter(string path, int status, string retryAfterValue)
        {
            int served = 0;
            Task<string> fetchTask = _parser.Fetch($"{_baseUrl}{path}");

            while (true)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    break;
                }
                served++;
                try
                {
                    ctx.Response.StatusCode = status;
                    ctx.Response.Headers["Retry-After"] = retryAfterValue;
                    var bytes = Encoding.UTF8.GetBytes("body-" + status);
                    await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
                catch (Exception)
                {
                    break;
                }
                if (await TaskCompletesWithin(fetchTask, 100))
                    break;
            }

            var result = new FetchResult();
            try
            {
                result.Body = await fetchTask;
            }
            catch (HttpRequestException e)
            {
                result.Exception = e;
            }
            result.RequestsMade = served;
            return result;
        }

        /// <summary>
        /// Parses a <c>Retry-After</c> value through the same header parser <c>HttpClient</c> uses
        /// (rather than constructing a header value by hand), so the tests exercise the wire form a
        /// hostile server would actually send - including values that must parse to nothing.
        /// </summary>
        private static System.Net.Http.Headers.RetryConditionHeaderValue? ParseRetryAfter(string wireValue)
        {
            using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.TryAddWithoutValidation("Retry-After", wireValue);
            return response.Headers.RetryAfter;
        }

        /// <summary>
        /// Runs the fetch against a loopback port with NO listener (connection refused) — the
        /// transport-failure path the retry exists for (the user's "connection refused by the
        /// server side" case). Returns the outcome; each attempt fails instantly.
        /// </summary>
        private async Task<FetchResult> FetchRefused(int port, string path)
        {
            var result = new FetchResult();
            try
            {
                result.Body = await _parser.Fetch($"http://127.0.0.1:{port}{path}");
            }
            catch (HttpRequestException e)
            {
                result.Exception = e;
            }
            // RequestsMade stays 0: with no server there is no request count to observe —
            // the attempt count is asserted via the run log in the test instead.
            return result;
        }

        /// <summary>Finds a loopback port with no listener (connection refused on connect).</summary>
        private static int DeadPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        [Test]
        public async Task Fetch_404_ThrowsHttpStatusWithReasonText()
        {
            var r = await TryFetch("/missing.md", 404);

            Assert.That(r.Exception, Is.Not.Null, "A 404 must be a failure, not \"no data\"");
            Assert.That(r.Exception!.Message, Is.EqualTo("HTTP 404 (Not Found)"));
        }

        [Test]
        public async Task Fetch_404_IsNotRetried_DefinitiveFailure()
        {
            // A 404 is a definitive statement about the URL — retrying gets the same 404.
            // Exactly ONE request must reach the server.
            var r = await TryFetch("/missing.md", 404, 404, 404);

            Assert.That(r.Exception, Is.Not.Null);
            Assert.That(r.RequestsMade, Is.EqualTo(1), "A definitive 4xx must not be retried");
            Assert.That(r.Exception!.Message, Is.EqualTo("HTTP 404 (Not Found)"),
                "No retry note when no retry was made");
        }

        [Test]
        public async Task Fetch_503_RetriedThenFails_SaysStillFailing()
        {
            // 503 is transient (server-side trouble): all MaxHttpAttempts are used, the final
            // message names the retries, and the server saw exactly that many requests.
            var r = await TryFetch("/x", 503, 503, 503);

            Assert.That(r.Exception, Is.Not.Null, "A 503 must be a failure");
            Assert.That(r.RequestsMade, Is.EqualTo(BaseVrModParser.MaxHttpAttempts));
            Assert.That(r.Exception!.Message,
                Is.EqualTo($"HTTP 503 (Service Unavailable) (still failing after {BaseVrModParser.MaxHttpAttempts} attempts)"));
        }

        [Test]
        public async Task Fetch_429_RetriedThenFails_SaysStillFailing()
        {
            // 429 (rate-limited) is transient: the bounded retries fire, then the failure
            // surfaces with the retry note.
            var r = await TryFetch("/x", 429, 429, 429);

            Assert.That(r.Exception, Is.Not.Null, "A 429 must be a failure");
            Assert.That(r.RequestsMade, Is.EqualTo(BaseVrModParser.MaxHttpAttempts));
            Assert.That(r.Exception!.Message,
                Is.EqualTo($"HTTP 429 (Too Many Requests) (still failing after {BaseVrModParser.MaxHttpAttempts} attempts)"));
        }

        [Test]
        public async Task Fetch_Transient503ThenSuccess_ReturnsBody()
        {
            // The user-visible win: a source that is briefly unavailable (503 on the first
            // hit — e.g. a CDN node restarting) succeeds on the retry, and the fetch returns
            // the body instead of failing the source.
            var r = await TryFetch("/flaky", 503, 200);

            Assert.That(r.Exception, Is.Null, "A recovered transient failure must not fail the fetch");
            Assert.That(r.Body, Is.EqualTo("body-200"));
            Assert.That(r.RequestsMade, Is.EqualTo(2));
            Assert.That(_parser.RunLog.Any(l => l.Contains("attempt 1 of ")), Is.True,
                "The failed attempt must be recorded on the run log");
        }

        [Test]
        public async Task Fetch_ConnectionRefused_RetriedThenFails_SaysStillFailing()
        {
            // The reported case: the connection is refused on the server side. A transport
            // failure (no HTTP status at all) is transient — all attempts fire, the final
            // message carries the socket cause plus the retry note, and the run log shows
            // every attempt.
            int port = DeadPort();
            var r = await FetchRefused(port, "/");

            Assert.That(r.Exception, Is.Not.Null, "A refused connection must be a failure");
            Assert.That(r.Exception!.Message,
                Does.EndWith($" (still failing after {BaseVrModParser.MaxHttpAttempts} attempts)"));
            // Two "retrying in" lines (after attempts 1 and 2) and one terminal FAILED line.
            int retryLines = _parser.RunLog.Count(l => l.Contains("retrying in"));
            Assert.That(retryLines, Is.EqualTo(BaseVrModParser.MaxHttpAttempts - 1));
            int failedLines = _parser.RunLog.Count(l => l.Contains("FAILED"));
            Assert.That(failedLines, Is.EqualTo(1));
        }

        [Test]
        public async Task Fetch_200_ReturnsBodyAndLogsStatus()
        {
            var r = await TryFetch("/ok.txt", 200);

            Assert.That(r.Exception, Is.Null, "A 200 is not a failure");
            Assert.That(r.Body, Is.EqualTo("body-200"));
            Assert.That(r.RequestsMade, Is.EqualTo(1));
            Assert.That(_parser.RunLog.Any(l => l.StartsWith("GET ") && l.Contains("-> HTTP 200")), Is.True);
        }

        [Test]
        public async Task Fetch_404_LogsFailureLineWithStatus()
        {
            var r = await TryFetch("/missing.md", 404);

            Assert.That(r.Exception, Is.Not.Null);
            var lastLine = _parser.RunLog.Last();
            Assert.That(lastLine, Does.StartWith("GET "));
            Assert.That(lastLine, Does.Contain("FAILED"));
            Assert.That(lastLine, Does.Contain("HTTP 404"));
        }

        [Test]
        public void ComputeRetryDelay_DoublesFromOneSecond()
        {
            Assert.That(BaseVrModParser.ComputeRetryDelay(1), Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(BaseVrModParser.ComputeRetryDelay(2), Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(BaseVrModParser.ComputeRetryDelay(3), Is.EqualTo(TimeSpan.FromSeconds(4)));
        }

        [Test]
        public void ComputeRetryDelay_RejectsNonPositiveAttempt()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => BaseVrModParser.ComputeRetryDelay(0));
        }

        // ---- Politeness: obey a server that says when to come back -------------------------

        /// <summary>
        /// A <c>Retry-After</c> the server sent is read from the wire form, and only a sane ask
        /// counts as an instruction. Everything here is attacker-chosen text from a loopback stand-in
        /// for a rate-limiting host.
        /// </summary>
        [TestCase("30", true)]
        [TestCase("1", true)]
        [TestCase("0", false)]
        [TestCase("-5", false)]
        [TestCase("soon", false)]
        [TestCase("31536000000", false)]
        public void TryReadRetryAfter_WireValues_AreAcceptedOnlyWhenTheyMeanWaitThatLong(
            string wireValue, bool expected)
        {
            TimeSpan asked;
            Assert.That(BaseVrModParser.TryReadRetryAfter(ParseRetryAfter(wireValue), out asked), Is.EqualTo(expected),
                $"Retry-After: {wireValue}");
            if (expected)
                Assert.That(asked.TotalSeconds, Is.GreaterThan(0));
        }

        [Test]
        public void TryReadRetryAfter_MissingHeaderMeansNoInstruction()
        {
            TimeSpan asked;
            Assert.That(BaseVrModParser.TryReadRetryAfter(null, out asked), Is.False);
        }

        [Test]
        public void TryReadRetryAfter_AFutureDateMeansWaitUntilThen()
        {
            string wire = DateTimeOffset.UtcNow.AddSeconds(90).ToString("R");
            TimeSpan asked;
            Assert.That(BaseVrModParser.TryReadRetryAfter(ParseRetryAfter(wire), out asked), Is.True);
            Assert.That(asked.TotalSeconds, Is.GreaterThanOrEqualTo(85).And.LessThanOrEqualTo(91),
                "the HTTP-date form becomes the distance from now");
        }

        [Test]
        public void TryReadRetryAfter_APastDateMeansNoInstruction()
        {
            TimeSpan asked;
            Assert.That(BaseVrModParser.TryReadRetryAfter(
                ParseRetryAfter("Wed, 21 Oct 2015 07:28:00 GMT"), out asked), Is.False);
        }

        [Test]
        public async Task Fetch_429WithRetryAfter_WaitsTheTimeTheServerAskedFor()
        {
            var r = await TryFetchWithRetryAfter("/limited", 429, "7");

            Assert.That(r.Exception, Is.Not.Null);
            Assert.That(r.RequestsMade, Is.EqualTo(BaseVrModParser.MaxHttpAttempts),
                "an obeyed wait is still bounded by the attempt cap");
            Assert.That(_parser.ServerRequestedWaits, Is.Not.Empty);
            Assert.That(_parser.ServerRequestedWaits[0], Is.EqualTo(TimeSpan.FromSeconds(7)),
                "the server's number replaced the plugin's 1 s backoff");
            Assert.That(_parser.RunLog.Last(line => line.Contains("retrying in")),
                Does.Contain("the delay the server asked for"));
        }

        /// <summary>
        /// The limit of the politeness rule: a scan is user-facing (Playnite's progress dialog is
        /// open) and a wait cap is a wait cap. Asked for more than the plugin will sit through, the
        /// source fails this run after ONE request - which is also the polite outcome, because the
        /// alternative is retrying on our own 1 s schedule directly against an explicit request not
        /// to.
        /// </summary>
        [Test]
        public async Task Fetch_429WithRetryAfterBeyondTheWaitCap_StopsAfterOneRequest()
        {
            var r = await TryFetchWithRetryAfter("/flooded", 429, "3600");

            Assert.That(r.Exception, Is.Not.Null);
            Assert.That(r.RequestsMade, Is.EqualTo(1), "asked to wait an hour: not answered with our own backoff");
            Assert.That(r.Exception!.Message, Does.Contain("asked for a 3600 s wait"));
            Assert.That(_parser.ServerRequestedWaits, Is.Empty, "no retry was scheduled, so no delay was chosen");
        }

        [Test]
        public async Task Fetch_429WithoutRetryAfter_KeepsTheOwnDoublingBackoff()
        {
            var r = await TryFetch("/limited-but-silent", 429);

            Assert.That(r.RequestsMade, Is.EqualTo(BaseVrModParser.MaxHttpAttempts));
            Assert.That(_parser.ServerRequestedWaits, Is.All.Null, "no header, no instruction");
        }

        [Test]
        public void IsTransientHttpFailure_TransportFailure_IsTransient()
        {
            // No response at all (connection refused/reset, DNS, timeout) → transient.
            Assert.That(BaseVrModParser.IsTransientHttpFailure(null), Is.True);
        }

        [Test]
        public void IsTransientHttpFailure_ServerAndThrottleStatuses_AreTransient()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BaseVrModParser.IsTransientHttpFailure(500), Is.True);
                Assert.That(BaseVrModParser.IsTransientHttpFailure(502), Is.True);
                Assert.That(BaseVrModParser.IsTransientHttpFailure(503), Is.True);
                Assert.That(BaseVrModParser.IsTransientHttpFailure(504), Is.True);
                Assert.That(BaseVrModParser.IsTransientHttpFailure(429), Is.True);
            });
        }

        [Test]
        public void IsTransientHttpFailure_ClientErrors_AreDefinitive()
        {
            // Definitive client errors: retrying the same GET gets the same answer.
            Assert.Multiple(() =>
            {
                Assert.That(BaseVrModParser.IsTransientHttpFailure(400), Is.False);
                Assert.That(BaseVrModParser.IsTransientHttpFailure(401), Is.False);
                Assert.That(BaseVrModParser.IsTransientHttpFailure(403), Is.False);
                Assert.That(BaseVrModParser.IsTransientHttpFailure(404), Is.False);
                Assert.That(BaseVrModParser.IsTransientHttpFailure(405), Is.False);
                Assert.That(BaseVrModParser.IsTransientHttpFailure(410), Is.False);
            });
        }

        [Test]
        public void DescribeHttpFailure_Timeout_SaysTimedOut()
        {
            var ex = new HttpRequestException("An error occurred while sending the request.",
                new TaskCanceledException("A task was canceled."));

            Assert.That(BaseVrModParser.DescribeHttpFailure(ex),
                Is.EqualTo($"request timed out after {BaseVrModParser.HttpTimeoutSeconds} s"));
        }

        [Test]
        public void DescribeHttpFailure_DnsFailure_SaysSocketCause()
        {
            // .NET Framework derives the SocketException message from the error code
            // (11001 = WSAHostNotFound, "No such host is known."). assert against se.Message
            // so the test checks the plumbing (the socket cause surfaces), not the locale text.
            var se = new SocketException(11001);
            var ex = new HttpRequestException("An error occurred while sending the request.", se);

            Assert.That(se.Message, Is.Not.Empty);
            Assert.That(BaseVrModParser.DescribeHttpFailure(ex), Is.EqualTo(se.Message));
        }

        [Test]
        public void DescribeHttpFailure_ConnectionReset_SaysSocketCause()
        {
            // 10054 = WSAECONNRESET, "An existing connection was forcibly closed by the
            // remote host." — the exact failure the user's REFramework screenshot hit.
            var se = new SocketException(10054);
            var ex = new HttpRequestException("An error occurred while sending the request.", se);

            Assert.That(se.Message, Is.Not.Empty);
            Assert.That(BaseVrModParser.DescribeHttpFailure(ex), Is.EqualTo(se.Message));
        }

        [Test]
        public void DescribeHttpFailure_UnknownCause_FallsBackToMessage()
        {
            var ex = new InvalidOperationException("something odd happened");

            Assert.That(BaseVrModParser.DescribeHttpFailure(ex), Is.EqualTo("something odd happened"));
        }

        [Test]
        public void DescribeHttpFailure_DeepChain_FindsInnermostCause()
        {
            // HttpClient wraps the socket error one level deep; a handler may wrap once more.
            var se = new SocketException(10061); // WSAECONNREFUSED
            var ex = new HttpRequestException("outer",
                new Exception("mid", se));

            Assert.That(se.Message, Is.Not.Empty);
            Assert.That(BaseVrModParser.DescribeHttpFailure(ex), Is.EqualTo(se.Message));
        }

        [Test]
        public void DescribeHttpStatus_WithServerReasonPhrase_UsesIt()
        {
            Assert.That(BaseVrModParser.DescribeHttpStatus(404, "Not Found"), Is.EqualTo("HTTP 404 (Not Found)"));
        }

        [Test]
        public void DescribeHttpStatus_WithoutReasonPhrase_UsesStandardText()
        {
            Assert.That(BaseVrModParser.DescribeHttpStatus(404, null), Is.EqualTo("HTTP 404 (Not Found)"));
            Assert.That(BaseVrModParser.DescribeHttpStatus(404, "   "), Is.EqualTo("HTTP 404 (Not Found)"));
        }

        [Test]
        public void StandardReasonPhrase_KnownCodes()
        {
            Assert.That(BaseVrModParser.StandardReasonPhrase(400), Is.EqualTo("Bad Request"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(401), Is.EqualTo("Unauthorized"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(403), Is.EqualTo("Forbidden"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(404), Is.EqualTo("Not Found"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(405), Is.EqualTo("Method Not Allowed"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(408), Is.EqualTo("Request Timeout"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(410), Is.EqualTo("Gone"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(429), Is.EqualTo("Too Many Requests"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(500), Is.EqualTo("Internal Server Error"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(502), Is.EqualTo("Bad Gateway"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(503), Is.EqualTo("Service Unavailable"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(504), Is.EqualTo("Gateway Timeout"));
        }

        [Test]
        public void StandardReasonPhrase_CodeNotMappedButDefined_UsesEnumName()
        {
            // 413/301 are on .NET Framework's HttpStatusCode but not in the common map: the
            // enum name is returned, so a real code is still named (never empty).
            Assert.That(BaseVrModParser.StandardReasonPhrase(413), Is.EqualTo("RequestEntityTooLarge"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(301), Is.EqualTo("MovedPermanently"));
        }

        [Test]
        public void StandardReasonPhrase_UndefinedCode_SaysUnknown()
        {
            // 418 (Teapot) is NOT on .NET Framework's HttpStatusCode (a .NET Core addition),
            // so both it and 518 must fall back to "Unknown" rather than crash.
            Assert.That(BaseVrModParser.StandardReasonPhrase(418), Is.EqualTo("Unknown"));
            Assert.That(BaseVrModParser.StandardReasonPhrase(518), Is.EqualTo("Unknown"));
        }
    }
}
