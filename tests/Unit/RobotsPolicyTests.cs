using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for the robots.txt half of the politeness work:
    /// <see cref="RobotsPolicy"/> reads a host's robots file once and extracts the
    /// <c>Crawl-delay</c> it asks of this plugin, and <see cref="HttpHostGate"/> spaces that host's
    /// requests by it.
    ///
    /// Scope under test is the decided one: Crawl-delay is honoured, <c>Disallow</c> is read past and
    /// not enforced (several mod-catalogue sites disallow generic crawlers on exactly the pages this
    /// plugin has to read, so enforcing path rules would silently switch sources off).
    ///
    /// Everything here uses a supplied robots body or a counting fake fetcher - no network. Two tests
    /// wait a fraction of a second on purpose, because "the spacing code actually waits" and "a
    /// cancelled wait gives its slots back" cannot be shown any other way; they are sub-second, not
    /// crawl-length.
    /// </summary>
    [TestFixture]
    public class RobotsPolicyTests
    {
        private const string StarGroup = "User-agent: *\n";

        /// <summary>
        /// The wiring tests write into the gate every source shares (keyed by host, and a loopback
        /// server is the same host key as any other loopback server). Cleared here so a declared pace
        /// from one test cannot slow a later fixture's fetches.
        /// </summary>
        [TearDown]
        public void TearDown()
        {
            HttpHostGate.Shared.SetCrawlDelay("127.0.0.1", null);
        }

        // ---- What a robots file may ask of us ----

        [TestCase("User-agent: *\nCrawl-delay: 7\n", 7)]
        [TestCase("user-agent: *\ncrawl-delay: 2\n", 2)]                     // field names are case-insensitive
        [TestCase("User-agent: *\nCrawl-delay: 0.5\n", 0.5)]                // fractional seconds are legal
        [TestCase("User-agent: *\r\nCrawl-delay: 8\r\n", 8)]                // CRLF file
        [TestCase("  User-agent: *   \n   Crawl-delay:   6   \n", 6)]       // sloppy spacing
        [TestCase("User-agent: *\nCrawl-delay: 5 # be gentle\n", 5)]         // trailing comment
        [TestCase("# a robots file\nUser-agent: *\nCrawl-delay: 4\n", 4)]   // leading comment
        [TestCase("User-agent: *\nCrawl-delay: 9999\n", 30)]                // an absurd ask is capped, not obeyed
        [TestCase("User-agent: *\nDisallow: /admin\nCrawl-delay: 3\n", 3)]  // path rules are not enforced, the pace is
        [TestCase("User-agent: *\nCrawl-delay: 2\n\nUser-agent: Googlebot\nCrawl-delay: 60\n", 2)] // only our group counts
        [TestCase("User-agent: *\nCrawl-delay: 4\n\nUser-agent: *\nCrawl-delay: 9\n", 9)]          // records disagree: the polite one wins
        public void ParseCrawlDelay_AStarGroupDelayIsHonouredWithinTheCap(string robotsBody, double expectedSeconds)
        {
            var delay = RobotsPolicy.ParseCrawlDelay(robotsBody);
            Assert.That(delay, Is.Not.Null);
            Assert.That(delay!.Value.TotalSeconds, Is.EqualTo(expectedSeconds).Within(0.001));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   \n\n")]
        [TestCase("User-agent: SomeOtherBot\nCrawl-delay: 30\n")]  // not a rule for us; we send no User-Agent
        [TestCase("Crawl-delay: 12\n")]                            // a delay with no user-agent group says nothing about us
        [TestCase("User-agent: *\nCrawl-delay: soon\n")]           // not a number
        [TestCase("User-agent: *\nCrawl-delay: -3\n")]             // a negative wait is not a wait
        [TestCase("User-agent: *\nCrawl-delay: 0\n")]              // nor is a zero one
        [TestCase("User-agent: *\nDisallow: /admin\n")]            // rules only: nothing to space requests by
        [TestCase("garbage garbage\ngarbage")]
        public void ParseCrawlDelay_WhenNothingIsAskedOfUsThereIsNoDelay(string? robotsBody)
        {
            Assert.That(RobotsPolicy.ParseCrawlDelay(robotsBody), Is.Null, $"robots file: {Describe(robotsBody)}");
        }

        private static string Describe(string? robotsBody)
            => robotsBody == null ? "(none)" : "\"" + robotsBody.Replace("\n", "\\n") + "\"";

        // ---- The lookup: once per host, whatever the answer was ----

        [Test]
        public async Task GetCrawlDelay_AHostIsAskedOnceEvenWhenSeveralSourcesAskAtTheSameTime()
        {
            var fetches = new List<string>();
            var policy = new RobotsPolicy();
            Func<Uri, CancellationToken, Task<string?>> fetch = (uri, _) =>
            {
                fetches.Add(uri.ToString());
                return Task.FromResult<string?>(StarGroup + "Crawl-delay: 7\n");
            };

            var request = new Uri("https://store.steampowered.com/app/1234567/");
            var three = await Task.WhenAll(
                policy.GetCrawlDelayAsync(request, fetch, CancellationToken.None),
                policy.GetCrawlDelayAsync(new Uri("https://store.steampowered.com/app/7654321/"), fetch, CancellationToken.None),
                policy.GetCrawlDelayAsync(new Uri("https://store.steampowered.com/"), fetch, CancellationToken.None));

            Assert.That(fetches.Count, Is.EqualTo(1), "three sources, one host, one robots file");
            Assert.That(fetches[0], Is.EqualTo("https://store.steampowered.com/robots.txt"));
            Assert.That(three.Select(d => d?.TotalSeconds), Is.All.EqualTo(7));
        }

        [Test]
        public async Task GetCrawlDelay_AHostThatFailedToAnswerIsNotAskedAgainThisRun()
        {
            int fetches = 0;
            var policy = new RobotsPolicy();
            Func<Uri, CancellationToken, Task<string?>> fetch = (_, __) =>
            {
                fetches++;
                throw new InvalidOperationException("no route to host");
            };

            var first = await policy.GetCrawlDelayAsync(new Uri("https://example.com/games"), fetch, CancellationToken.None);
            var second = await policy.GetCrawlDelayAsync(new Uri("https://example.com/other"), fetch, CancellationToken.None);

            Assert.That(first, Is.Null, "an unreadable robots file means no declared pace");
            Assert.That(second, Is.Null);
            Assert.That(fetches, Is.EqualTo(1), "asking again per request is the load this policy exists to avoid");
        }

        [Test]
        public async Task GetCrawlDelay_DifferentAuthoritiesAreSeparateLookups()
        {
            var fetches = new List<string>();
            var policy = new RobotsPolicy();
            Func<Uri, CancellationToken, Task<string?>> fetch = (uri, _) =>
            {
                fetches.Add(uri.ToString());
                return Task.FromResult<string?>(null);
            };

            await policy.GetCrawlDelayAsync(new Uri("http://127.0.0.1:8080/games"), fetch, CancellationToken.None);
            await policy.GetCrawlDelayAsync(new Uri("https://127.0.0.1:8080/games"), fetch, CancellationToken.None);

            Assert.That(fetches, Is.EquivalentTo(new[] { "http://127.0.0.1:8080/robots.txt", "https://127.0.0.1:8080/robots.txt" }),
                "a robots file belongs to a scheme+host+port, not to a host name alone");
        }

        [Test]
        public async Task GetCrawlDelay_ACancelledScanLetsTheNextScanAskAgain()
        {
            int fetches = 0;
            var policy = new RobotsPolicy();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Func<Uri, CancellationToken, Task<string?>> fetch = async (uri, token) =>
            {
                fetches++;
                token.ThrowIfCancellationRequested();
                await Task.Yield();
                return StarGroup + "Crawl-delay: 7\n";
            };

            Assert.That(
                async () => await policy.GetCrawlDelayAsync(new Uri("https://example.com/games"), fetch, cts.Token),
                Throws.TypeOf<OperationCanceledException>());
            Assert.That(policy.TrackedAuthorities(), Is.Empty, "a cancelled lookup is not cached for the next run");

            var fresh = new CancellationTokenSource();
            var delay = await policy.GetCrawlDelayAsync(new Uri("https://example.com/games"), fetch, fresh.Token);
            Assert.That(delay?.TotalSeconds, Is.EqualTo(7));
            Assert.That(fetches, Is.EqualTo(2));
        }

        // ---- The gate: what it does with the pace a host asked for ----

        [Test]
        public void SetCrawlDelay_AskIsCappedAndAClearingAskRemovesIt()
        {
            using var gate = new HttpHostGate(1, 4);

            gate.SetCrawlDelay("pcvr-central.com", TimeSpan.FromMinutes(20));
            Assert.That(gate.CrawlDelayFor("pcvr-central.com"), Is.EqualTo(HttpHostGate.MaxCrawlDelay),
                "the site's pace is honoured, its attempt to park the scan is not");

            gate.SetCrawlDelay("pcvr-central.com", TimeSpan.Zero);
            Assert.That(gate.CrawlDelayFor("pcvr-central.com"), Is.EqualTo(TimeSpan.Zero));

            gate.SetCrawlDelay("pcvr-central.com", null);
            Assert.That(gate.CrawlDelayFor("pcvr-central.com"), Is.EqualTo(TimeSpan.Zero));
        }

        [Test]
        public async Task Enter_TheFirstRequestToAHostDoesNotWaitButTheNextOneIsScheduled()
        {
            using var gate = new HttpHostGate(1, 4);
            gate.SetCrawlDelay("compoundvr.com", TimeSpan.FromSeconds(5));

            var before = DateTime.UtcNow;
            await gate.EnterAsync("https://compoundvr.com/games/", CancellationToken.None);
            var returned = DateTime.UtcNow;
            gate.Exit("https://compoundvr.com/games/");

            Assert.That(returned - before, Is.LessThan(TimeSpan.FromSeconds(1)), "nothing was scheduled yet, so nothing to wait for");
            Assert.That(gate.NextAllowedUtcFor("compoundvr.com") - before,
                Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(4)).And.LessThanOrEqualTo(TimeSpan.FromSeconds(20)),
                "the next request to this host is scheduled one crawl-delay away");
        }

        /// <summary>Waits 300 ms on purpose: this is the only way to show the spacing really happens.</summary>
        [Test]
        public async Task Enter_TwoRequestsToTheSameHostAreSpacedByTheCrawlDelay()
        {
            using var gate = new HttpHostGate(1, 4);
            gate.SetCrawlDelay("uevrprofiles.org", TimeSpan.FromMilliseconds(300));

            await gate.EnterAsync("uevrprofiles.org", CancellationToken.None);
            gate.Exit("uevrprofiles.org");
            var firstDone = DateTime.UtcNow;

            await gate.EnterAsync("uevrprofiles.org", CancellationToken.None);
            gate.Exit("uevrprofiles.org");

            var spacing = DateTime.UtcNow - firstDone;
            Assert.That(spacing, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250)),
                "the second request was held to the pace the site asked for");
        }

        /// <summary>
        /// The slot bookkeeping around a wait: a request cancelled while sitting out a crawl-delay must
        /// hand back BOTH the host slot it already took and its global slot, or a scan leaks one slot
        /// per cancelled source and the remaining sources never get a turn.
        /// </summary>
        [Test]
        public async Task Enter_CancellationWhileWaitingOutACrawlDelayReturnsBothSlots()
        {
            using var gate = new HttpHostGate(1, 4);
            gate.SetCrawlDelay("raw.githubusercontent.com", TimeSpan.FromSeconds(30));

            // First request: takes a slot, schedules the next one 30 s out, and gives the slot back.
            await gate.EnterAsync("raw.githubusercontent.com", CancellationToken.None);
            gate.Exit("raw.githubusercontent.com");
            Assert.That(gate.NextAllowedUtcFor("raw.githubusercontent.com"), Is.GreaterThan(DateTime.UtcNow));

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
            var second = gate.EnterAsync("raw.githubusercontent.com", cts.Token);
            // On net462 a cancelled Task.Delay surfaces as TaskCanceledException, which is an
            // OperationCanceledException - InstanceOf, not TypeOf, is the assertion that survives that.
            Assert.That(async () => await second, Throws.InstanceOf<OperationCanceledException>());

            Assert.That(gate.AvailableFor("raw.githubusercontent.com"), Is.EqualTo(gate.PerHostConcurrency),
                "the host slot taken before the wait was released");
            Assert.That(gate.GlobalAvailable, Is.EqualTo(gate.MaxConcurrentRequests), "and so was the global slot");
        }

        [Test]
        public async Task Enter_AHostWithNoDeclaredDelayIsScheduledLikeBefore()
        {
            using var gate = new HttpHostGate(1, 4);
            gate.SetCrawlDelay("spaced.example", TimeSpan.FromSeconds(9));

            await gate.EnterAsync("unspaced.example", CancellationToken.None);
            gate.Exit("unspaced.example");

            Assert.That(gate.NextAllowedUtcFor("unspaced.example"), Is.EqualTo(DateTime.MinValue),
                "no robots rule for this host means no schedule, no pause");
            Assert.That(gate.NextAllowedUtcFor("spaced.example"), Is.EqualTo(DateTime.MinValue),
                "spacing one host does not schedule another");
        }

        // ---- The wiring: a real fetch through the real path, against a loopback host ----

        /// <summary>A parser that leaves <c>ConsultsRobotsPolicy</c> on, i.e. how every real source fetches.</summary>
        private sealed class WiringParser : BaseVrModParser
        {
            public WiringParser() : base("wiring", "Wiring", "http://127.0.0.1/") { }
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
                => Task.FromResult<IEnumerable<VrModEntry>>(Array.Empty<VrModEntry>());
            public override string? GetCanonicalName(VrModEntry entry) => entry.GameName;
            public Task<string> Fetch(string url) => FetchStringAsync(url);
        }

        /// <summary>
        /// Serves <paramref name="responses"/> (in the order the paths arrive is up to the client, so
        /// they are answered by path) on a loopback port, fetches <c>/index.html</c> through the real
        /// fetch path, and reports the body, the paths the server saw and the port it served.
        /// </summary>
        private static async Task<(string Body, List<string> Served, int Port)> ServeAndFetch(
            (string Path, int Status, string Body)[] responses)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            var served = new List<string>();
            var serving = Task.Run(async () =>
            {
                for (int i = 0; i < responses.Length; i++)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await listener.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return; // client gave up
                    }
                    var match = responses.FirstOrDefault(r => r.Path == ctx.Request.Url.AbsolutePath);
                    served.Add(ctx.Request.Url.AbsolutePath);
                    var bytes = Encoding.UTF8.GetBytes(match.Body ?? string.Empty);
                    ctx.Response.StatusCode = match.Status;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
            });

            // Deliberately not disposed: BaseVrModParser.Dispose shuts down the process-wide shared
            // client every other test in this suite still uses.
            var parser = new WiringParser();
            string body = await parser.Fetch($"http://127.0.0.1:{port}/index.html");
            await serving;
            listener.Stop();

            return (body, served, port);
        }

        [Test]
        public async Task Fetch_ASourceThatAskedForCrawlDelayIsHandedToTheSharedGate()
        {
            var (body, served, _) = await ServeAndFetch(new (string, int, string)[]
            {
                ("/robots.txt", 200, "User-agent: *\nCrawl-delay: 3\n"),
                ("/index.html", 200, "game list body")
            });

            Assert.That(body, Does.Contain("game list body"), "the content fetch is unaffected by the policy lookup");
            Assert.That(served, Does.Contain("/robots.txt"), "the host was asked for its pace before its pages were walked");
            Assert.That(served.Count, Is.EqualTo(2), "one robots file per host per run, not one per request");
            // The gate keys on the host, not on host+port: one site is one queue.
            Assert.That(HttpHostGate.Shared.CrawlDelayFor("127.0.0.1"), Is.EqualTo(TimeSpan.FromSeconds(3)),
                "and the gate every source shares is what enforces it");
        }

        [Test]
        public async Task Fetch_ASourceWithNoRobotsFileIsNotSpaced()
        {
            var (body, served, _) = await ServeAndFetch(new (string, int, string)[]
            {
                ("/robots.txt", 404, ""),
                ("/index.html", 200, "game list body")
            });

            Assert.That(body, Does.Contain("game list body"));
            Assert.That(served, Does.Contain("/robots.txt"));
            Assert.That(HttpHostGate.Shared.CrawlDelayFor("127.0.0.1"), Is.EqualTo(TimeSpan.Zero),
                "no robots file means no declared pace, so no pause");
        }
    }
}
