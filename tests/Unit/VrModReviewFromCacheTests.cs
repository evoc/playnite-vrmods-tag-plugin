using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NSubstitute;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for <see cref="VrModTagService.BuildReviewMatchesFromCache"/> — the "Review VR Mods"
    /// (no-rescan) path used by the settings dialog button — and for the pure
    /// <see cref="VrModTagService.ReviewRowStatus"/> row-collection rule it shares with a full scan.
    /// </summary>
    [TestFixture]
    public class VrModReviewFromCacheTests
    {
        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private VrModTagService _service = null!;
        private VrModSourceRegistry _registry = null!;
        private IPlayniteAPI _mockApi = null!;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _mockApi = Substitute.For<IPlayniteAPI>();
            var mockDb = Substitute.For<IGameDatabaseAPI>();
            mockDb.Games.Returns(_games);
            mockDb.Tags.Returns(_tags);
            mockDb.BufferedUpdate().Returns(new FakeBufferedUpdate());
            _mockApi.Database.Returns(mockDb);
        }

        [SetUp]
        public void SetUp()
        {
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();
            _mockApi.Database.Games.Returns(_games);
            _mockApi.Database.Tags.Returns(_tags);
            _mockApi.Database.BufferedUpdate().Returns(new FakeBufferedUpdate());

            _registry = new VrModSourceRegistry();
            _service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
        }

        /// <summary>
        /// Cold start (no source has ever cached data): the button falls back to a real scan, so
        /// the service must report "nothing to show" with <see langword="null"/>.
        /// </summary>
        [Test]
        public void BuildReviewMatchesFromCache_NoCachedSourceData_ReturnsNull()
        {
            var parser = new CountingTestParser("src-none", new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" }
            });
            _registry.Register(parser);

            // No scan has run, so nothing is cached yet.
            var matches = _service.BuildReviewMatchesFromCache();

            Assert.That(matches, Is.Null);
            Assert.That(parser.FetchCount, Is.EqualTo(0), "must not fetch to answer 'nothing cached'");
        }

        /// <summary>
        /// Warm case (the normal one — a startup scan always caches the sources): the full review
        /// list is recomputed from the cache with NO network fetch, and rows carry the same data
        /// a scan would present (score, categories, mapping, decision status).
        /// </summary>
        [Test]
        public void BuildReviewMatchesFromCache_AfterScan_ReturnsFullRowsWithoutRescan()
        {
            var skyrim = new Game("Skyrim")
            {
                Id = new System.Guid("11111111-1111-1111-1111-111111111111"),
                TagIds = new List<System.Guid>()
            };
            _games.Items.Add(skyrim);

            var parser = new CountingTestParser("src-skyrim", new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" }
            });
            _registry.Register(parser);

            // The startup/earlier scan populates the cache (exactly one fetch).
            _service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(parser.FetchCount, Is.EqualTo(1));

            var matches = _service.BuildReviewMatchesFromCache();

            Assert.That(parser.FetchCount, Is.EqualTo(1), "no-rescan path must not re-fetch the sources");
            Assert.That(matches, Is.Not.Null);
            Assert.That(matches, Has.Count.EqualTo(1));

            var row = matches![0];
            Assert.That(row.PlayniteGame, Is.SameAs(skyrim));
            Assert.That(row.SourceGameName, Is.EqualTo("Skyrim"));
            Assert.That(row.Categories, Does.Contain("UEVR"));
            Assert.That(row.Score, Is.EqualTo(1.0f));
            // Exact match, undecided mapping → the review row is Accepted.
            Assert.That(row.Status, Is.EqualTo(MatchDecision.Accepted));
        }

        /// <summary>
        /// Stored decisions are part of what the no-rescan dialog shows: a mapping the user
        /// rejected is presented as Rejected, exactly like a scan would present it.
        /// </summary>
        [Test]
        public void BuildReviewMatchesFromCache_RespectsStoredDecisions()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new CountingTestParser("src-decisions", new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            // The user rejects the mapping (this is what the review dialog persists).
            _service.Settings.SetDecision("src-decisions", "Skyrim", skyrim.Id, MatchDecision.Rejected);

            var matches = _service.BuildReviewMatchesFromCache();

            Assert.That(matches, Is.Not.Null);
            Assert.That(matches, Has.Count.EqualTo(1));
            Assert.That(matches![0].Status, Is.EqualTo(MatchDecision.Rejected));
        }

        /// <summary>
        /// The row-collection rule the no-rescan path shares with a full scan: accepted and
        /// rejected mappings always become rows; a pending mapping becomes a row only at or
        /// above the threshold (an exact 1.00 match as Accepted, anything else as Pending);
        /// below the threshold a pending mapping produces no row.
        /// </summary>
        [Test]
        public void ReviewRowStatus_Rules()
        {
            const float threshold = 0.8f;

            // Accepted / rejected mappings are always rows, regardless of score.
            Assert.That(VrModTagService.ReviewRowStatus(MatchDecision.Accepted, 0f, threshold),
                Is.EqualTo(MatchDecision.Accepted));
            Assert.That(VrModTagService.ReviewRowStatus(MatchDecision.Rejected, 0f, threshold),
                Is.EqualTo(MatchDecision.Rejected));

            // Pending: reviewable at or above the threshold...
            Assert.That(VrModTagService.ReviewRowStatus(MatchDecision.Pending, 0.9f, threshold),
                Is.EqualTo(MatchDecision.Pending));
            // ...an exact match (1.00) as Accepted...
            Assert.That(VrModTagService.ReviewRowStatus(MatchDecision.Pending, 1.0f, threshold),
                Is.EqualTo(MatchDecision.Accepted));
            // ...and below the threshold no row at all.
            Assert.That(VrModTagService.ReviewRowStatus(MatchDecision.Pending, 0.5f, threshold), Is.Null);
        }

        /// <summary>
        /// A parser that counts its fetches, to prove the no-rescan path does no network.
        /// </summary>
        private class CountingTestParser : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;
            private int _fetchCount;

            public int FetchCount => _fetchCount;

            public CountingTestParser(string sourceId, IEnumerable<VrModEntry> entries)
                : base(sourceId, "Test", "https://example.com/")
            {
                _entries = entries;
            }

            public override string? ChangeIdentifier => null;

            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
            {
                Interlocked.Increment(ref _fetchCount);
                return Task.FromResult(_entries);
            }

            public override string? GetCanonicalName(VrModEntry entry) => entry.GameName;
        }
    }
}
