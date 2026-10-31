using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NUnit.Framework;
using Playnite;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for scan cancellation: the wall-clock budget and the caller token that
    /// <see cref="VrModTagPlugin"/> wires to the progress window's Cancel button.
    ///
    /// Before this existed a scan had no way to stop: 12 sources were fetched concurrently, a single
    /// source could spend 3 attempts x a 30 s client timeout, and one source paginated up to 100
    /// pages — minutes of web traffic with no abort, and a Cancel button that only hid the window.
    ///
    /// What these tests pin down:
    /// 1. A cancelled scan stops at the next request instead of working through retries/pages.
    /// 2. A source that never answers is cut off by the scan budget, not by its own timeout chain.
    /// 3. A cancelled scan writes NOTHING to the library (no tags, no links) — a scan whose fetch
    ///    set is incomplete must not rewrite entries the user cannot easily undo.
    /// 4. A cancelled source is recorded as failed, so partial data is never reported as success.
    /// 5. Pagination aborts rather than returning the pages it managed to fetch as "success".
    /// </summary>
    [TestFixture]
    public class VrModScanCancellationTests
    {
        /// <summary>How long a cancelled scan is allowed to take before the test calls it stuck.</summary>
        private const int CancellationGraceMs = 10_000;

        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private IPlayniteAPI _mockApi = null!;
        private VrModSourceRegistry _registry = null!;

        [SetUp]
        public void SetUp()
        {
            _mockApi = Substitute.For<IPlayniteAPI>();
            var mockDb = Substitute.For<IGameDatabaseAPI>();
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();

            mockDb.Games.Returns(_games);
            mockDb.Tags.Returns(_tags);
            mockDb.BufferedUpdate().Returns(new FakeBufferedUpdate());
            _mockApi.Database.Returns(mockDb);

            _registry = new VrModSourceRegistry();
        }

        // -----------------------------------------------------------------------------------------
        // 1 + 2: the parser level — ScanToken stops requests, retries and pagination.
        // -----------------------------------------------------------------------------------------

        /// <summary>A parser whose fetch is the real <c>FetchStringAsync</c> path, exposed for tests.</summary>
        private sealed class RequestParser : BaseVrModParser
        {
            public RequestParser()
                : base($"cancel-{Guid.NewGuid():N}", "Cancellation test source", "https://example.com/")
            {
            }

            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult<IEnumerable<VrModEntry>>(Array.Empty<VrModEntry>());
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);

            /// <summary>Exposes the protected fetch/retry loop (URL supplied by the test).</summary>
            public Task<string> FetchAsync(string url) => FetchStringAsync(url);
        }

        [Test]
        public void ScanToken_CancelledBeforeTheFetch_StopsWithoutTouchTheNetwork()
        {
            var parser = new RequestParser();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            parser.ScanToken = cts.Token;

            var sw = Stopwatch.StartNew();
            var ex = Assert.Catch(() => parser.FetchAsync("https://example.invalid/page").GetAwaiter().GetResult());
            sw.Stop();

            Assert.That(ex, Is.InstanceOf<OperationCanceledException>(),
                "A cancelled scan must surface as cancellation, not as a fetch error.");
            Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(2),
                "Cancellation must short-circuit at the top of the request loop, before any request, retry or backoff.");
        }

        [Test]
        public void ScanToken_CancelledDuringTheRetryBackoff_AbortsWithoutExhaustingRetries()
        {
            // Connection-refused is a transient failure, so the fetch loop would otherwise sit through
            // the whole backoff ladder (1 s + 2 s) and only then fail. Cancellation must cut it off.
            var parser = new RequestParser();
            using var cts = new CancellationTokenSource();
            parser.ScanToken = cts.Token;

            var sw = Stopwatch.StartNew();
            var fetch = Task.Run(() => parser.FetchAsync("http://127.0.0.1:1/").GetAwaiter().GetResult());
            cts.CancelAfter(TimeSpan.FromMilliseconds(150));
            var ex = Assert.Catch(() => fetch.GetAwaiter().GetResult());
            sw.Stop();

            Assert.That(ex, Is.InstanceOf<OperationCanceledException>(),
                "A cancelled scan must abort the retry loop, not finish it and then report an HTTP error.");

            var fullBackoffLadder = BaseVrModParser.ComputeRetryDelay(1) + BaseVrModParser.ComputeRetryDelay(2);
            Assert.That(sw.Elapsed, Is.LessThan(fullBackoffLadder),
                $"Abort must beat the full retry backoff ({fullBackoffLadder.TotalSeconds:0.#} s) — the retry delay must be cancellable.");
            Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(CancellationGraceMs / 1000.0));
        }

        [Test]
        public void CuratorPagination_CancelledScanAborts_InsteadOfReturningPartialPages()
        {
            // The curator source paginates one host up to MaxCuratorPages times. Returning "the pages
            // I got" would count as a successful fetch, and a successful fetch is what lets stale
            // tag/link cleanup act on half the data.
            var parser = new TestCuratorParser();
            using var cts = new CancellationTokenSource();
            parser.CancelAfterFirstPage = cts;
            parser.ScanToken = cts.Token;

            var ex = Assert.Catch(() => parser.FetchEntriesAsync().GetAwaiter().GetResult());

            Assert.That(ex, Is.InstanceOf<OperationCanceledException>(),
                "Cancellation between pages must abort the source, not return a partial page set.");
            Assert.That(parser.PageCalls, Is.EqualTo(1),
                "Pagination must not keep requesting pages after the scan is cancelled.");
        }

        [Test]
        public void CuratorPagination_NotCancelled_FetchesNormally()
        {
            // Control for the test above: the abort comes from cancellation, not from the loop guard
            // misfiring on a normal run.
            var parser = new TestCuratorParser { ScanToken = CancellationToken.None };
            parser.SinglePageOnly = true;

            var entries = parser.FetchEntriesAsync().GetAwaiter().GetResult();

            Assert.That(entries, Is.Not.Null);
            Assert.That(parser.PageCalls, Is.EqualTo(1), "An uncancelled run stops on the pagination budget, not on cancellation.");
        }

        /// <summary>
        /// A curator source with the network replaced by a counter: page 1 returns a valid response
        /// with a large <c>total_count</c> (so the loop wants more pages) and cancels the scan token,
        /// which stands for "the user pressed Cancel between page 1 and page 2".
        /// </summary>
        private sealed class TestCuratorParser : SteamCuratorParser
        {
            /// <summary>Number of pages the pagination loop asked for.</summary>
            public int PageCalls;

            /// <summary>Token source cancelled by the first page (the simulated Cancel press).</summary>
            public CancellationTokenSource? CancelAfterFirstPage;

            /// <summary>When true the fake page reports a list that fits in one page.</summary>
            public bool SinglePageOnly;

            public TestCuratorParser()
                : base("curator-cancel-test", "Curator cancellation test", "https://store.steampowered.com/", 9000001, "Native VR")
            {
            }

            protected override Task<string> FetchCuratorPageAsync(int start)
            {
                PageCalls++;
                int total = SinglePageOnly ? 1 : 500;   // 500 > page size: the loop wants another page
                if (!SinglePageOnly)
                    CancelAfterFirstPage?.Cancel();
                return Task.FromResult("{\"success\":1,\"total_count\":" + total + ",\"results_html\":\"\"}");
            }
        }

        // -----------------------------------------------------------------------------------------
        // 3 + 4: the service level — a cancelled scan must not rewrite the library.
        // -----------------------------------------------------------------------------------------

        /// <summary>A source that never answers; only its scan token can end it.</summary>
        private sealed class HangingParser : BaseVrModParser
        {
            public HangingParser()
                : base($"hang-{Guid.NewGuid():N}", "Hanging source", "https://example.com/")
            {
            }

            public override string? ChangeIdentifier => null;

            public override async Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ScanToken);
                return new[]
                {
                    new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "https://example.com/skyrim" }
                };
            }

            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }

        /// <summary>A source that answers immediately with one match for the library game.</summary>
        private sealed class HealthyParser : BaseVrModParser
        {
            public HealthyParser()
                : base($"healthy-{Guid.NewGuid():N}", "Healthy source", "https://example.com/")
            {
            }

            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult<IEnumerable<VrModEntry>>(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "https://example.com/skyrim" }
            });
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }

        private Game AddSkyrim()
        {
            var game = new Game("Skyrim") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(game);
            return game;
        }

        [Test]
        public void CancelledScan_AbortsHangingSource_AndAppliesNoTags()
        {
            var game = AddSkyrim();
            var hanging = new HangingParser();
            _registry.Register(hanging);
            var settings = new VrModTagSettings();
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);

            using var cts = new CancellationTokenSource();
            var scan = Task.Run(() => service.RunFullScanAsync(null, false, cts.Token));
            cts.CancelAfter(TimeSpan.FromMilliseconds(250));

            // GetResult, not Wait: a faulted task rethrows the original exception instead of wrapping
            // it in AggregateException, which is what the assertion has to look at.
            var ex = Assert.Catch(() => scan.GetAwaiter().GetResult());

            Assert.That(scan.IsCompleted, Is.True, "A cancelled scan must finish, not wait out the source's own timeout chain.");
            Assert.That(ex, Is.InstanceOf<OperationCanceledException>(),
                "A cancelled scan must not carry on into matching and tag application.");
            Assert.That(game.TagIds, Is.Empty, "A scan with an incomplete fetch set must write no tags.");

            var status = StatusFor(settings, hanging);
            Assert.That(status, Is.Not.Null, "The cancelled source must report a result for the last run.");
            Assert.That(status!.LastResult, Is.False, "A cancelled source must not be recorded as a successful fetch.");
            Assert.That(status.LastError, Is.EqualTo("Scan cancelled."));
        }

        [Test]
        public void CancelCurrentScan_StopsFetching_AndWritesNothing()
        {
            // The service-level cancel (what the progress window's Cancel button ends up doing, and
            // the only way to stop a scan started without a caller token).
            var game = AddSkyrim();
            var hanging = new HangingParser();
            _registry.Register(hanging);
            var settings = new VrModTagSettings();
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);

            var scan = Task.Run(() => service.RunFullScanAsync());
            // The scan's cancel source only exists once the run has reached its fetch phase, so keep
            // pressing Cancel (idempotent, and a no-op before that point) until the scan notices it.
            SpinWait.SpinUntil(() =>
            {
                service.CancelCurrentScan();
                return scan.IsCompleted;
            }, CancellationGraceMs);

            Assert.That(scan.IsCompleted, Is.True, "CancelCurrentScan must end a scan that is stuck fetching.");
            Assert.That(scan.IsFaulted, Is.False,
                "A service-level cancel (no caller token) ends the scan quietly: the fetch phase is cut off, " +
                "the cancelled source is recorded as failed, and the run reports instead of throwing.");
            Assert.That(game.TagIds, Is.Empty, "A cancelled scan must write no tags.");

            var status = StatusFor(settings, hanging);
            Assert.That(status, Is.Not.Null);
            Assert.That(status!.LastResult, Is.False, "A cancelled source must not be recorded as a successful fetch.");
            Assert.That(status.LastError, Is.EqualTo("Scan cancelled."));
        }

        [Test]
        public void ScanWallClockBudget_CutsOffAnUnansweringSource_WithoutLosingTheHealthyOne()
        {
            // Budget = the safety net for a scan nobody cancelled: a source that never answers must
            // not hold the scan open for its full retry/pagination budget, and the sources that do
            // answer must still be applied.
            var game = AddSkyrim();
            var hanging = new HangingParser();
            var healthy = new HealthyParser();
            _registry.Register(hanging);
            _registry.Register(healthy);

            var settings = new VrModTagSettings();
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry)
            {
                ScanWallClockBudget = TimeSpan.FromMilliseconds(400)   // test seam for the 10-minute default
            };

            var sw = Stopwatch.StartNew();
            var scan = Task.Run(() => service.RunFullScanAsync());
            var finished = scan.Wait(CancellationGraceMs);
            sw.Stop();

            Assert.That(finished, Is.True, "The scan budget must end the run: " + sw.Elapsed.TotalSeconds + " s elapsed.");
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)),
                "A budgeted scan must not sit through the hung source's 30 s delay / retry ladder.");

            Assert.That(game.TagIds, Is.Not.Empty,
                "Sources that answered must still be applied when another source is cut off.");
            Assert.That(StatusFor(settings, hanging)!.LastResult, Is.False,
                "A source cut off by the budget counts as failed, so stale-tag cleanup does not run on partial data.");
            Assert.That(StatusFor(settings, healthy)!.LastResult, Is.True);
        }

        [Test]
        public void DefaultScanWallClockBudget_IsFinite_AndFarBelowAnUnboundedScan()
        {
            // The default must exist and be finite (0 / infinite would mean "no bound at all"), and
            // long enough for a real 12-source scan (each source: up to 3 attempts x 30 s timeout).
            Assert.That(VrModTagService.DefaultScanWallClockBudget, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(VrModTagService.DefaultScanWallClockBudget, Is.LessThan(TimeSpan.FromHours(1)));
        }

        private static SourceStatus? StatusFor(VrModTagSettings settings, IVrModSource source)
        {
            if (settings.SourceStatuses == null)
                return null;
            settings.SourceStatuses.TryGetValue(source.SourceId.ToLowerInvariant(), out var status);
            return status;
        }
    }
}
