using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NSubstitute;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for multi-store duplicates — the "Alien: Isolation" regression. The same title
    /// imported from several stores yields several <see cref="Game"/> rows with an identical
    /// name. Each duplicate must: match a source entry on its own, carry its own saved
    /// decision, and be disambiguated in the review grid with its store name in brackets.
    /// </summary>
    [TestFixture]
    public class VrModDuplicateGamesTests
    {
        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private IGameDatabaseAPI _mockDb = null!;
        private IPlayniteAPI _mockApi = null!;
        private VrModTagService _service = null!;
        private VrModSourceRegistry _registry = null!;

        [SetUp]
        public void SetUp()
        {
            _mockApi = Substitute.For<IPlayniteAPI>();
            _mockDb = Substitute.For<IGameDatabaseAPI>();
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();

            _mockDb.Games.Returns(_games);
            _mockDb.Tags.Returns(_tags);
            _mockDb.BufferedUpdate().Returns(new FakeBufferedUpdate());
            _mockApi.Database.Returns(_mockDb);

            _registry = new VrModSourceRegistry();
            _service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
        }

        // --- Duplicate matching: every same-name game matches one source entry -----------

        [Test]
        public void Scan_EntryMatchesAllSameNameDuplicates_EachGetsItsOwnTags()
        {
            var steam = new Game("Alien: Isolation") { TagIds = new List<Guid>() };
            var epic = new Game("Alien: Isolation") { TagIds = new List<Guid>() };
            _games.Items.Add(steam);
            _games.Items.Add(epic);

            var parser = new TestParser("src-rotot", new[]
            {
                new VrModEntry { GameName = "Alien: Isolation", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var categoryTag = _tags.Items.Single(t => t.Name == VrModCategories.TagNameFor("UEVR"));
            Assert.That(steam.TagIds, Does.Contain(categoryTag.Id),
                "the first duplicate must be matched");
            Assert.That(epic.TagIds, Does.Contain(categoryTag.Id),
                "the second same-name duplicate must be matched independently");
        }

        // --- Per-store decisions: each duplicate carries its own saved decision -----------

        [Test]
        public void Scan_PerStoreDecisions_AcceptedDuplicateTagged_RejectedDuplicateSkipped()
        {
            var steam = new Game("Alien: Isolation") { TagIds = new List<Guid>() };
            var epic = new Game("Alien: Isolation") { TagIds = new List<Guid>() };
            _games.Items.Add(steam);
            _games.Items.Add(epic);

            var parser = new TestParser("src-rotot", new[]
            {
                new VrModEntry { GameName = "Alien: Isolation", Category = "UEVR" }
            });
            _registry.Register(parser);

            // The user accepted the Steam import and rejected the Epic import.
            _service.Settings.SetDecision(parser.SourceId, "Alien: Isolation", steam.Id, MatchDecision.Accepted);
            _service.Settings.SetDecision(parser.SourceId, "Alien: Isolation", epic.Id, MatchDecision.Rejected);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var categoryTag = _tags.Items.Single(t => t.Name == VrModCategories.TagNameFor("UEVR"));
            Assert.That(steam.TagIds, Does.Contain(categoryTag.Id));
            Assert.That(epic.TagIds, Is.Empty, "the rejected duplicate must stay untagged");
        }

        [Test]
        public void ClearDecisionsForGame_ClearsOnlyThatGameId_NotSameNameDuplicates()
        {
            var steam = new Game("Alien: Isolation") { TagIds = new List<Guid>() };
            var epic = new Game("Alien: Isolation") { TagIds = new List<Guid>() };
            _games.Items.Add(steam);
            _games.Items.Add(epic);

            _service.Settings.SetDecision("src-rotot", "Alien: Isolation", steam.Id, MatchDecision.Accepted);
            _service.Settings.SetDecision("src-rotot", "Alien: Isolation", epic.Id, MatchDecision.Accepted);

            _service.Settings.ClearDecisionsForGame(steam.Id);

            // The old name-keyed ClearDecisionsForGame("Alien: Isolation") wiped both rows at
            // once — that is what made the accepted match reappear as Pending every scan.
            Assert.That(_service.Settings.GetDecision("src-rotot", "Alien: Isolation", steam.Id),
                Is.EqualTo(MatchDecision.Pending));
            Assert.That(_service.Settings.GetDecision("src-rotot", "Alien: Isolation", epic.Id),
                Is.EqualTo(MatchDecision.Accepted));
        }

        // --- Review grid disambiguation: "Name (Store)" only for duplicate names ---------

        [Test]
        public void ReviewMatches_DuplicateNameShowsStoreSource()
        {
            var steamId = Guid.NewGuid();
            var epicId = Guid.NewGuid();
            var steam = new Game("Alien: Isolation") { TagIds = new List<Guid>(), SourceId = steamId };
            var epic = new Game("Alien: Isolation") { TagIds = new List<Guid>(), SourceId = epicId };
            var solo = new Game("Unique Game") { TagIds = new List<Guid>(), SourceId = steamId };
            _games.Items.Add(steam);
            _games.Items.Add(epic);
            _games.Items.Add(solo);

            var sources = new FakeItemCollection<GameSource>();
            sources.Items.Add(new GameSource("Steam") { Id = steamId });
            sources.Items.Add(new GameSource("Epic Games") { Id = epicId });
            _mockDb.Sources.Returns(sources);

            var parser = new TestParser("src-rotot", new[]
            {
                new VrModEntry { GameName = "Alien: Isolation", Category = "UEVR" },
                new VrModEntry { GameName = "Unique Game", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var rows = _service.BuildReviewMatchesFromCache()!;
            Assert.That(rows.Single(r => r.PlayniteGame == steam).GameDisplayName,
                Is.EqualTo("Alien: Isolation (Steam)"));
            Assert.That(rows.Single(r => r.PlayniteGame == epic).GameDisplayName,
                Is.EqualTo("Alien: Isolation (Epic Games)"));
            // A name with a single game stays plain — no bracket suffix.
            Assert.That(rows.Single(r => r.PlayniteGame == solo).GameDisplayName,
                Is.EqualTo("Unique Game"));
        }

        [Test]
        public void ReviewMatches_DuplicateWithoutSourceNames_FallsBackToPlainName()
        {
            // No Sources exposed (or a game without a SourceId): duplicates still match,
            // the grid just can't disambiguate and shows the plain name.
            var steam = new Game("Alien: Isolation") { TagIds = new List<Guid>(), SourceId = Guid.NewGuid() };
            var epic = new Game("Alien: Isolation") { TagIds = new List<Guid>(), SourceId = Guid.NewGuid() };
            _games.Items.Add(steam);
            _games.Items.Add(epic);

            var parser = new TestParser("src-rotot", new[]
            {
                new VrModEntry { GameName = "Alien: Isolation", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var rows = _service.BuildReviewMatchesFromCache()!;
            Assert.That(rows, Has.Count.EqualTo(2), "both duplicates must still be review rows");
            Assert.That(rows.Select(r => r.GameDisplayName), Is.EquivalentTo(new[] { "Alien: Isolation", "Alien: Isolation" }));
        }

        private class TestParser : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;

            public TestParser(string sourceId, IEnumerable<VrModEntry> entries)
                : base(sourceId, "Test", "https://example.com/")
            {
                _entries = entries;
            }

            public override string? ChangeIdentifier => null;

            public override System.Threading.Tasks.Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
                => System.Threading.Tasks.Task.FromResult(_entries);

            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }
    }
}
