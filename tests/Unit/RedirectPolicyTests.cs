using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRmods.Tests.Unit
{
    /// <summary>
    /// Redirect policy for the plugin's single HTTP path.
    ///
    /// Why this exists: the shared <see cref="HttpClientHandler"/> used to sit at the framework
    /// default, which follows up to 20 <c>Location</c> hops silently and never tells the caller the
    /// request moved. Every parse path then treated whatever came back as "this source's page" — so a
    /// link a source publishes, or an aggregator mirroring that link, could point the plugin at some
    /// other site and that site's HTML would be parsed into game names, categories, and (before the
    /// Link gate) library Links. Auto-redirect also hid the hop from the run log, so a source that had
    /// been taken over looked like a source that returned odd markup.
    ///
    /// The policy now: auto-redirect is off at the handler; each hop is offered to
    /// <see cref="BaseVrModParser.ClassifyRedirectTarget"/>, which allows staying on the host that was
    /// asked for (or its <c>www.</c> variant, or a mirror host the parser declares), caps the chain at
    /// <see cref="BaseVrModParser.MaxRedirectHops"/>, runs the target through the same URL gate that
    /// protects written Links, and refuses everything else as a definitive (non-retried) failure whose
    /// message names the host the source tried to move to.
    /// </summary>
    [TestFixture]
    public class RedirectPolicyTests
    {
        // ------------------------------------------------------- hop classification ----------------

        /// <summary>A concrete parser that only exposes the hop decision, plus its mirror hosts.</summary>
        private sealed class DecisionParser : BaseVrModParser
        {
            private readonly string[] _mirrors;
            public DecisionParser(params string[] mirrors)
                : base("decision-parser", "DecisionParser", "https://example.com/")
                => _mirrors = mirrors;

            protected override IReadOnlyList<string> AllowedRedirectHosts => _mirrors;
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
                => Task.FromResult<IEnumerable<VrModEntry>>(Array.Empty<VrModEntry>());
            public override string? GetCanonicalName(VrModEntry entry) => entry.GameName;

            public BaseVrModParser.RedirectDecision Decide(string from, string? location, int hop)
                => ClassifyRedirectTarget(from,
                    location == null ? null : new Uri(location, UriKind.RelativeOrAbsolute), hop);
        }

        [Test]
        public void ASameSiteHop_IsFollowedAndResolvedAgainstTheRequestedUrl()
        {
            var decision = new DecisionParser().Decide("https://example.com/games?page=2", "/games?page=3", 0);

            Assert.That(decision.Allowed, Is.True, decision.Reason);
            Assert.That(decision.Target, Is.EqualTo("https://example.com/games?page=3"));
        }

        [Test]
        public void TheWwwVariantOfTheSameHost_CountsAsTheSameSite()
        {
            var decision = new DecisionParser().Decide("https://example.com/list", "https://www.example.com/list", 0);

            Assert.That(decision.Allowed, Is.True, decision.Reason);
        }

        [Test]
        public void BareDomainFromWww_CountsAsTheSameSite()
        {
            var decision = new DecisionParser().Decide("https://www.example.com/list", "https://example.com/list", 0);

            Assert.That(decision.Allowed, Is.True, decision.Reason);
        }

        [Test]
        public void AnUpgradeToHttpsOnTheSameHost_IsFollowed()
        {
            var decision = new DecisionParser().Decide("http://example.com/list", "https://example.com/list", 0);

            Assert.That(decision.Allowed, Is.True, decision.Reason);
            Assert.That(decision.Target, Is.EqualTo("https://example.com/list"));
        }

        [Test]
        public void AnOffSiteHop_IsRefusedAndTheMessageNamesBothHosts()
        {
            var decision = new DecisionParser().Decide("https://pcvr-central.com/game/1", "https://evil.example/picked", 0);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Does.Contain("pcvr-central.com"));
            Assert.That(decision.Reason, Does.Contain("evil.example"));
        }

        [Test]
        public void ASiblingSubdomain_IsNotTheSameSite()
        {
            // "other.example.com" is a different site, not a spelling of "example.com".
            var decision = new DecisionParser().Decide("https://example.com/list", "https://other.example.com/list", 0);

            Assert.That(decision.Allowed, Is.False, decision.Reason ?? "<allowed>");
        }

        [Test]
        public void AMirrorHostTheParserDeclares_IsAccepted()
        {
            var decision = new DecisionParser("cdn.example.net").Decide("https://example.com/list", "https://cdn.example.net/list", 0);

            Assert.That(decision.Allowed, Is.True, decision.Reason);
        }

        [Test]
        public void AHostNobodyDeclared_IsStillRefused()
        {
            var decision = new DecisionParser("cdn.example.net").Decide("https://example.com/list", "https://someone-else.net/list", 0);

            Assert.That(decision.Allowed, Is.False, "<accepted>");
        }

        [TestCase("javascript:alert(1)")]
        [TestCase("file:///C:/Windows/system32/calc.exe")]
        [TestCase("data:text/html,<script>alert(1)</script>")]
        [TestCase("steam://rungameid/72850")]
        [TestCase("ms-settings:locations")]
        [TestCase("https://label:password@example.com/list")]
        public void ALocationThatThePluginWouldNeverWriteIntoTheLibrary_IsNotFollowed(string location)
        {
            // Same gate as a scraped Link, so a redirect cannot reach a scheme the Link gate refuses.
            var decision = new DecisionParser().Decide("https://example.com/list", location, 0);

            Assert.That(decision.Allowed, Is.False, "<accepted>");
            Assert.That(decision.Reason, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void AnOverLongLocation_IsRefusedInsteadOfFetched()
        {
            var location = "https://example.com/" + new string('x', WebUrl.MaxUrlLength);
            var decision = new DecisionParser().Decide("https://example.com/list", location, 0);

            Assert.That(decision.Allowed, Is.False, "<accepted>");
        }

        [Test]
        public void AThreeOhTwoWithNoLocation_IsRefusedRatherThanRetriedForever()
        {
            var decision = new DecisionParser().Decide("https://example.com/list", null, 0);

            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.Reason, Does.Contain("no Location header"));
        }

        [Test]
        public void TheHopBudgetIsSpentOnHopsNotOnRetries()
        {
            var parser = new DecisionParser();

            Assert.That(parser.Decide("https://example.com/a", "https://example.com/b", BaseVrModParser.MaxRedirectHops - 1).Allowed, Is.True);
            var overBudget = parser.Decide("https://example.com/a", "https://example.com/b", BaseVrModParser.MaxRedirectHops);

            Assert.That(overBudget.Allowed, Is.False);
            Assert.That(overBudget.Reason, Does.Contain("limit is 5"));
        }

        [TestCase("example.com", "www.example.com", true)]
        [TestCase("WWW.Example.com", "example.com", true)]
        [TestCase("example.com", "evil.com", false)]
        [TestCase("example.com", "sub.example.com", false)]
        [TestCase("example.com", "example.com.evil.net", false)]
        [TestCase("", "example.com", false)]
        [TestCase(null, "example.com", false)]
        public void HostEqualityTreatsOnlyTheWwwSpellingAsTheSameSite(string? a, string? b, bool expected)
            => Assert.That(BaseVrModParser.SameRegisteredHost(a, b), Is.EqualTo(expected));

        // ------------------------------------------------ port pinning on an allow-listed host --

        [Test]
        public void AHopFromHttpsToHttp_IsRefusedAsADowngrade()
        {
            // 443 -> 80 on a host the plugin already trusts moves the response to plaintext, and the
            // source pages the plugin scans are all https. Only the opposite direction is canonical.
            var decision = new DecisionParser().Decide(
                "https://example.com/games", "http://example.com/games", 0);

            Assert.That(decision.Allowed, Is.False, decision.Reason);
        }

        [Test]
        public void AnUpgradeFromANonDefaultPort_IsNotTheCanonicalPair()
        {
            // "80 -> 443" is the pair real sites publish for their own URL. A hop from an arbitrary
            // port to 443 is a different listener, whatever the host name has in common with it.
            var decision = new DecisionParser().Decide(
                "http://example.com:8088/start", "https://example.com/other", 0);

            Assert.That(decision.Allowed, Is.False, decision.Reason);
        }

        [Test]
        public void TheSameHostWrittenWithItsExplicitPort_IsStillOneSite()
        {
            // "https://example.com/" and "https://example.com:443/" are one site: writing the default
            // port explicitly must not turn a same-site refresh into an off-site hop.
            var decision = new DecisionParser().Decide(
                "https://example.com/games", "https://example.com:443/games?page=3", 0);

            Assert.That(decision.Allowed, Is.True, decision.Reason);
            Assert.That(decision.Target, Is.EqualTo("https://example.com/games?page=3"));
        }

        [Test]
        public void TheSameHostOnAnotherListener_IsNotOneSite()
        {
            // An allow-listed host name is not a licence for every port of that machine: this is how
            // "the CDN host" becomes whatever somebody parked on 8443 of the same box.
            var decision = new DecisionParser().Decide(
                "https://example.com/games", "https://example.com:8443/games", 0);

            Assert.That(decision.Allowed, Is.False, decision.Reason);
            Assert.That(decision.Reason, Does.Contain("8443"));
        }

        [TestCase("cdn.example.net", "cdn.example.net")]
        [TestCase("https://cdn.example.net/", "cdn.example.net")]
        [TestCase("cdn.example.net/assets/1.js", "cdn.example.net")]
        [TestCase("cdn.example.net:8443", "cdn.example.net")]
        [TestCase("", "")]
        public void AMirrorDeclaredAsAUrlMeansTheSameMirrorAsTheBareHost(string declared, string expected)
            => Assert.That(BaseVrModParser.NormalizeRegisteredHost(declared), Is.EqualTo(expected));

        [Test]
        public void AMirrorDeclarationMatchesCaseInsensitively()
            => Assert.That(BaseVrModParser.SameRegisteredHost(
                BaseVrModParser.NormalizeRegisteredHost("CDN.Example.NET/"), "cdn.example.net"), Is.True);

        // ------------------------------------------------------ end-to-end over HTTP ----------------

        private sealed class FetchScriptStep
        {
            public int Status;
            public string? Location;
            public string Body = "";
        }

        private HttpListener _listener = null!;
        private string _baseUrl = null!;
        private int _port;
        private readonly List<string> _servedPaths = new();

        [SetUp]
        public void SetUp()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _baseUrl = $"http://127.0.0.1:{_port}";
            _servedPaths.Clear();
        }

        [TearDown]
        public void TearDown() => _listener.Close();

        /// <summary>
        /// Serves the scripted responses, one per arrived request (the last one repeats), and returns
        /// what the fetch did. Requests the policy refuses are never sent, which is exactly what
        /// <see cref="RequestsServed"/> measures.
        /// </summary>
        private async Task<(HttpRequestException? Error, string? Body)> FetchThroughAsync(
            string path, params FetchScriptStep[] script)
        {
            var serving = Task.Run(async () =>
            {
                int i = 0;
                while (true)
                {
                    var ctx = await _listener.GetContextAsync();
                    _servedPaths.Add(ctx.Request.Url.PathAndQuery);
                    var step = script[Math.Min(i, script.Length - 1)];
                    i++;
                    ctx.Response.StatusCode = step.Status;
                    if (step.Location != null) ctx.Response.RedirectLocation = step.Location;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(step.Body);
                    if (bytes.Length > 0) ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
            });

            var parser = new LoopbackParser();
            parser.RunLog = new List<string>();
            _lastParser = parser;
            var fetch = parser.Fetch($"{_baseUrl}{path}");
            var done = await Task.WhenAny(fetch, Task.Delay(10_000));
            if (done != fetch)
                Assert.Fail("The fetch did not finish: the redirect policy is probably looping.");

            if (fetch.IsFaulted)
            {
                var agg = fetch.Exception!;
                return (agg.InnerException as HttpRequestException ?? (agg.Flatten().InnerExceptions.FirstOrDefault() as HttpRequestException), null);
            }
            return (null, fetch.Result);
        }

        private static FetchScriptStep Redirect(string location) => new FetchScriptStep { Status = 302, Location = location };
        private static FetchScriptStep Ok(string body) => new FetchScriptStep { Status = 200, Body = body };

        private LoopbackParser? _lastParser;

        /// <summary>A parser whose fetch is the real protected path over the loopback listener.</summary>
        private sealed class LoopbackParser : BaseVrModParser
        {
            public LoopbackParser() : base("loopback", "Loopback", "http://127.0.0.1/") { }
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
                => Task.FromResult<IEnumerable<VrModEntry>>(Array.Empty<VrModEntry>());
            public override string? GetCanonicalName(VrModEntry entry) => entry.GameName;
            public Task<string> Fetch(string url) => FetchStringAsync(url);
            protected override TimeSpan RetryDelay(int failedAttempt) => TimeSpan.Zero;

            // The redirect harness scripts one answer per arriving request per hop; a robots.txt
            // lookup would be an extra arrival on the same host and would consume a scripted status.
            protected override bool ConsultsRobotsPolicy => false;
        }

        [Test]
        public async Task ASameSiteRedirect_IsFollowedAndItsBodyIsWhatGetsParsed()
        {
            var (error, body) = await FetchThroughAsync("/list",
                Redirect("/list/after-consent"),
                Ok("GAME ROWS"));

            Assert.That(error, Is.Null, error?.Message);
            Assert.That(body, Is.EqualTo("GAME ROWS"));
            Assert.That(_servedPaths, Is.EqualTo(new[] { "/list", "/list/after-consent" }));
        }

        [Test]
        public async Task AnOffSiteRedirect_IsRefusedAndTheOffSiteRequestIsNeverMade()
        {
            // 127.0.0.2 is loopback but a DIFFERENT host string: it proves the policy refuses the hop
            // instead of following it, without a second listener being involved.
            var (error, body) = await FetchThroughAsync("/list",
                Redirect($"http://127.0.0.2:{_port}/someone-elses-page"));

            Assert.That(body, Is.Null);
            Assert.That(error, Is.Not.Null);
            Assert.That(error!.Message, Does.Contain("redirect refused"));
            Assert.That(error.Message, Does.Contain("127.0.0.2"));
            Assert.That(_servedPaths, Is.EqualTo(new[] { "/list" }),
                "The refused hop must not be requested at all");
        }

        [Test]
        public async Task ARefusedHopIsDefinitive_TheUrlIsNotRetriedBehindIt()
        {
            var (error, _) = await FetchThroughAsync("/list",
                Redirect($"http://127.0.0.2:{_port}/elsewhere"));

            // The retry path (MaxHttpAttempts) must not add requests: a server that always answers the
            // same refused hop gets asked exactly once.
            Assert.That(_servedPaths.Count, Is.EqualTo(1));
            Assert.That(error!.Message, Does.Not.Contain("still failing after"));
        }

        [Test]
        public async Task ARedirectChainLongerThanTheBudget_FailsWithTheBudgetNamed()
        {
            var steps = new List<FetchScriptStep>();
            for (int i = 1; i <= 8; i++)
                steps.Add(Redirect($"/hop{i + 1}"));
            steps.Add(Ok("never reached"));

            var (error, _) = await FetchThroughAsync("/hop1", steps.ToArray());

            Assert.That(error, Is.Not.Null);
            Assert.That(error!.Message, Does.Contain("limit is 5"));
            Assert.That(_servedPaths.Count, Is.EqualTo(BaseVrModParser.MaxRedirectHops + 1),
                "the budget's worth of hops plus the answer that runs out of budget");
        }

        [Test]
        public async Task AThreeOhTwoWithNoLocation_FailsInsteadOfBeingTreatedAsContent()
        {
            // Pre-fix a 3xx (< 400) fell into the success branch and an empty body was parsed as the
            // source's page — the source looked healthy with zero entries instead of broken.
            var (error, body) = await FetchThroughAsync("/list", new FetchScriptStep { Status = 302, Body = "" });

            Assert.That(body, Is.Null);
            Assert.That(error, Is.Not.Null);
            Assert.That(error!.Message, Does.Contain("no Location header"));
        }

        [Test]
        public async Task TheRunLogShowsEveryFollowedHopAndNamesTheRefusedOne()
        {
            // The "Last run" view is the operator's only window into a fetch; a hop that moved the
            // request has to be visible there.
            var (error, _) = await FetchThroughAsync("/list",
                Redirect("/moved"),
                Redirect($"http://127.0.0.2:{_port}/elsewhere"));

            Assert.That(error, Is.Not.Null);
            var parserLog = ParserRunLog();
            Assert.That(parserLog.Any(l => l.Contains("following redirect to") && l.Contains("/moved")), Is.True,
                "a hop that was taken must appear in the run log: " + string.Join(" | ", parserLog));
            Assert.That(parserLog.Any(l => l.Contains("redirect refused") && l.Contains("127.0.0.2")), Is.True,
                "a hop that was refused must appear in the run log with the host it tried to move to: " + string.Join(" | ", parserLog));
        }

        private List<string> ParserRunLog() => _lastParser!.RunLog ?? new List<string>();

        /// <summary>Whether the shared client truly stopped following hops by itself.</summary>
        [Test]
        public async Task AHopToAnotherPortOnTheSameHost_IsRefusedWithoutRequestingIt()
        {
            var (error, _) = await FetchThroughAsync("/start",
                Redirect($"http://127.0.0.1:{_port + 1}/other"),
                Ok("<html>ok</html>"));

            Assert.That(error, Is.Not.Null, "a hop that changes the port must not be followed");
            Assert.That(_servedPaths, Is.EqualTo(new[] { "/start" }),
                "the refused listener must never be asked for content: " + string.Join(", ", _servedPaths));
        }

        [Test]
        public void TheSharedHandlerIsAskedToStopAutoRedirecting()
        {
            // If the framework ever stops honouring AllowAutoRedirect, the hop budget and the host
            // rule below stop being enforceable — this test is the tripwire for that.
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            Assert.That(handler.AllowAutoRedirect, Is.False);
        }
    }
}
