using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using NSubstitute;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for the library-change event path — the trigger that actually sees a new game.
    ///
    /// OnLibraryUpdated turned out to be the wrong hook: in Playnite it is raised from exactly one
    /// call site (MainViewModelBase.UpdateEmulationLibrary, the emulator/ROM import), so a game the
    /// user adds through "Add game → Manually…", a store import or the "from installed" wizard never
    /// reaches it (verified live against Playnite: a new game was saved, the plugin logged nothing).
    /// Database.Games.ItemCollectionChanged is raised for every added game, whoever added it, so the
    /// service now subscribes to it.
    ///
    /// The same live session showed the second half: "Add game → Manually…" announces a placeholder
    /// game named "New Game" when the dialog opens, and the title the user types arrives later as an
    /// ItemUpdated. Matching keys off the name, so ItemUpdated is subscribed too — but only name
    /// changes are acted on, because Playnite raises it for every playtime or completion-status edit.
    /// </summary>
    [TestFixture]
    public class VrModNewGameEventTests
    {
        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private IGameDatabaseAPI _mockDb = null!;
        private IPlayniteAPI _mockApi = null!;
        private RecordingLogger _logger = null!;
        private VrModSourceRegistry _registry = null!;

        [SetUp]
        public void SetUp()
        {
            _mockApi = Substitute.For<IPlayniteAPI>();
            _mockDb = Substitute.For<IGameDatabaseAPI>();
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();
            _logger = new RecordingLogger();

            _mockDb.Games.Returns(_games);
            _mockDb.Tags.Returns(_tags);
            _mockDb.BufferedUpdate().Returns(new FakeBufferedUpdate());
            _mockApi.Database.Returns(_mockDb);

            _registry = new VrModSourceRegistry();
        }

        private VrModTagService CreateService(int debounceMs = 50)
            => new VrModTagService(_mockApi, new VrModTagSettings(), _logger, _registry, debounceMs);

        /// <summary>
        /// Overload for tests that must pre-seed decisions (an accepted match, a corrupt record).
        /// </summary>
        private VrModTagService CreateService(VrModTagSettings settings, int debounceMs = 50)
            => new VrModTagService(_mockApi, settings, _logger, _registry, debounceMs);

        private static VrModTagService NoService => null!;

        // --- The headline case: a manually added game is found without waiting -------------

        [Test]
        public void GameAddedWhileRunning_IsScannedThroughTheLibraryEvent()
        {
            var service = CreateService();
            _registry.Register(new TestParser("src-event", new[]
            {
                new VrModEntry { GameName = "Vampyr", Category = "UEVR" }
            }));

            // Startup: the library is empty, so the baseline contains nothing to skip.
            service.SeedKnownGames();

            // The user adds a game: Playnite writes the row, then announces it on the collection.
            var game = new Game("Vampyr") { TagIds = new List<Guid>() };
            _games.Items.Add(game);
            _games.RaiseItemCollectionChanged(addedItems: new[] { game });

            var tagged = SpinWait.SpinUntil(() => game.TagIds.Count > 0, TimeSpan.FromSeconds(20));

            Assert.That(tagged, Is.True,
                "a game Playnite reports as added must be scanned and tagged without waiting for the scan interval. Log:\n" + _logger.Dump());
            var categoryTag = _tags.Items.Single(t => t.Name == VrModCategories.TagNameFor("UEVR"));
            Assert.That(game.TagIds, Does.Contain(categoryTag.Id));
        }

        // --- Playnite imports before OnApplicationStarted has seeded -----------------------

        [Test]
        public void GameAddedBeforeTheBaselineIsSeeded_IsStillScanned()
        {
            var service = CreateService();
            // Playnite raises collection events during its own startup import, before
            // OnApplicationStarted runs SeedKnownGames.
            service.SubscribeToLibraryChanges();
            _registry.Register(new TestParser("src-event", new[]
            {
                new VrModEntry { GameName = "Blade & Sorcery", Category = "UEVR" }
            }));

            var game = new Game("Blade & Sorcery") { TagIds = new List<Guid>() };
            _games.Items.Add(game);
            _games.RaiseItemCollectionChanged(addedItems: new[] { game });

            service.SeedKnownGames();   // the seed replaces the baseline afterwards

            var tagged = SpinWait.SpinUntil(() => game.TagIds.Count > 0, TimeSpan.FromSeconds(20));
            Assert.That(tagged, Is.True,
                "an event that arrives with no baseline yet must still queue the game. Log:\n" + _logger.Dump());
        }

        // --- A burst of adds costs ONE batch scan ------------------------------------------

        [Test]
        public void GameAddedBurst_SettlesIntoASingleBatchScan()
        {
            var service = CreateService(debounceMs: 1500);   // the burst stays inside one window
            _registry.Register(new TestParser("src-event", new[]
            {
                new VrModEntry { GameName = "Alpha Game", Category = "UEVR" },
                new VrModEntry { GameName = "Beta Game", Category = "UEVR" },
                new VrModEntry { GameName = "Gamma Game", Category = "UEVR" }
            }));
            service.SeedKnownGames();

            var games = new[]
            {
                new Game("Alpha Game") { TagIds = new List<Guid>() },
                new Game("Beta Game") { TagIds = new List<Guid>() },
                new Game("Gamma Game") { TagIds = new List<Guid>() }
            };
            foreach (var g in games)
            {
                _games.Items.Add(g);
                _games.RaiseItemCollectionChanged(addedItems: new[] { g });   // three separate events
            }

            var tagged = SpinWait.SpinUntil(() => games.All(g => g.TagIds.Count > 0), TimeSpan.FromSeconds(25));
            Assert.That(tagged, Is.True, "every game of the burst must be tagged. Log:\n" + _logger.Dump());

            var scanLines = _logger.Lines.Where(l => l.Contains("new game(s)...")).ToList();
            Assert.That(scanLines, Has.Count.EqualTo(1),
                "the burst must drain as one batch, not as three scans. Log:\n" + _logger.Dump());
            Assert.That(scanLines[0], Does.Contain("Scanning 3 new game(s)"));
        }

        // --- Removals are not new games ----------------------------------------------------

        [Test]
        public void GameRemovedEvent_QueuesNoScan()
        {
            var service = CreateService();
            _registry.Register(new TestParser("src-event", new[]
            {
                new VrModEntry { GameName = "Vampyr", Category = "UEVR" }
            }));

            var game = new Game("Vampyr") { TagIds = new List<Guid>() };
            _games.Items.Add(game);
            service.SeedKnownGames();

            _games.Remove(game);
            _games.RaiseItemCollectionChanged(removedItems: new[] { game });

            Thread.Sleep(500);   // longer than the settling window
            Assert.That(_logger.Lines.Any(l => l.Contains("new game(s)")), Is.False,
                "a removal must not be treated as an added game. Log:\n" + _logger.Dump());
            Assert.That(game.TagIds, Is.Empty);
        }

        // --- Dispose detaches the handler --------------------------------------------------

        [Test]
        public void Dispose_DetachesTheLibraryEventHandler()
        {
            var service = CreateService();
            _registry.Register(new TestParser("src-event", new[]
            {
                new VrModEntry { GameName = "Vampyr", Category = "UEVR" }
            }));
            service.SeedKnownGames();

            var first = new Game("Vampyr") { TagIds = new List<Guid>() };
            _games.Items.Add(first);
            _games.RaiseItemCollectionChanged(addedItems: new[] { first });
            Assert.That(SpinWait.SpinUntil(() => first.TagIds.Count > 0, TimeSpan.FromSeconds(20)), Is.True);

            service.Dispose();

            // Playnite outlives the plugin: another game arrives on a collection nobody listens to
            // any more (the handler must not run on a disposed service).
            var second = new Game("After Dispose") { TagIds = new List<Guid>() };
            _games.Items.Add(second);
            _games.RaiseItemCollectionChanged(addedItems: new[] { second });

            Thread.Sleep(500);
            Assert.That(second.TagIds, Is.Empty,
                "a disposed service must not scan anything new. Log:\n" + _logger.Dump());
            Assert.That(_logger.Lines.Count(l => l.Contains("new game(s)...")), Is.EqualTo(1),
                "only the pre-dispose game may be scanned. Log:\n" + _logger.Dump());
        }

        // --- "Add game → Manually…" is an add of a placeholder, then a rename --------------

        [Test]
        public void PlaceholderGameRenamedByTheUser_IsScannedUnderItsNewName()
        {
            var service = CreateService();
            _registry.Register(new TestParser("src-event", new[]
            {
                new VrModEntry { GameName = "Heavy Metal FAKK 2 VR", Category = "UEVR" }
            }));
            service.SeedKnownGames();

            // Live-verified against Playnite 10.62: opening "Add game → Manually…" writes a game
            // literally named "New Game" right away, and that is what the collection announces. The
            // title the user types is saved later, as an update.
            var placeholder = new Game("New Game") { TagIds = new List<Guid>() };
            _games.Items.Add(placeholder);
            _games.RaiseItemCollectionChanged(addedItems: new[] { placeholder });

            Assert.That(SpinWait.SpinUntil(() => _logger.Lines.Any(l => l.Contains("new game(s)")), TimeSpan.FromSeconds(20)), Is.True,
                "the placeholder add must itself be queued (it is a real library game). Log:\n" + _logger.Dump());
            Assert.That(placeholder.TagIds, Is.Empty,
                "a placeholder matches no mod entry, so it must not pick up tags. Log:\n" + _logger.Dump());

            // The user fills in the title and presses Save.
            placeholder.Name = "Heavy Metal FAKK 2 VR";
            _games.RaiseItemUpdated(new[] { (oldData: new Game("New Game") { Id = placeholder.Id }, newData: placeholder) });

            var tagged = SpinWait.SpinUntil(() => placeholder.TagIds.Count > 0, TimeSpan.FromSeconds(25));
            Assert.That(tagged, Is.True,
                "a rename is the moment the game becomes matchable; the new name must be scanned. Log:\n" + _logger.Dump());
            var categoryTag = _tags.Items.Single(t => t.Name == VrModCategories.TagNameFor("UEVR"));
            Assert.That(placeholder.TagIds, Does.Contain(categoryTag.Id));
        }

        // --- Playnite also raises ItemUpdated for playtime, completion status, icons … ------

        [Test]
        public void GameUpdatedWithoutANameChange_QueuesNoScan()
        {
            var service = CreateService();
            _registry.Register(new TestParser("src-event", new[]
            {
                new VrModEntry { GameName = "Vampyr", Category = "UEVR" }
            }));

            var game = new Game("Vampyr") { TagIds = new List<Guid>() };
            _games.Items.Add(game);
            service.SeedKnownGames();   // already known and already covered by the startup scan

            var queueLinesBefore = _logger.Lines.Count(l => l.Contains("queued for a VR mod scan"));

            game.Playtime = 3600;
            _games.RaiseItemUpdated(new[] { (oldData: new Game("Vampyr") { Id = game.Id }, newData: game) });

            Thread.Sleep(600);   // longer than the settling window
            Assert.That(_logger.Lines.Count(l => l.Contains("queued for a VR mod scan")), Is.EqualTo(queueLinesBefore),
                "an edit that leaves the name alone changes nothing a match depends on; re-scanning every playtime tick would flood the single-scan gate. Log:\n" + _logger.Dump());
        }

        // --- A new game must not print one line per catalog entry -------------------------

        [Test]
        public void NewGameScan_LogsNothingPerUnrelatedSourceEntry()
        {
            var service = CreateService();
            var entries = new List<VrModEntry> { new VrModEntry { GameName = "Vampyr", Category = "UEVR" } };
            for (var i = 0; i < 40; i++)
                entries.Add(new VrModEntry { GameName = $"Unrelated Source Entry {i} With A Long Title", Category = "UEVR" });
            _registry.Register(new TestParser("src-event", entries));
            service.SeedKnownGames();

            var game = new Game("Vampyr") { TagIds = new List<Guid>() };
            _games.Items.Add(game);
            _games.RaiseItemCollectionChanged(addedItems: new[] { game });

            Assert.That(SpinWait.SpinUntil(() => game.TagIds.Count > 0, TimeSpan.FromSeconds(25)), Is.True,
                "the game must still be matched. Log:\n" + _logger.Dump());

            var skipLines = _logger.Lines.Count(l => l.Contains("Skipped Pending match"));
            Assert.That(skipLines, Is.EqualTo(0),
                "every undecided entry reports as Pending, so the whole catalog lands in the score " +
                "branch; entries the matcher prunes outright (score -1) are not near misses and must " +
                "not each cost a log line - in live Playnite one renamed game logged 3173 of them. " +
                "Log:\n" + _logger.Dump());
        }

        // --- The per-game prune must never skip a pair the user already decided about -------

        [Test]
        public void AcceptedMatchWithUnrelatedNamesSurvivesThePerGameScanPrune()
        {
            var settings = new VrModTagSettings();
            var service = CreateService(settings);
            _registry.Register(new TestParser("src-prune", new[]
            {
                new VrModEntry { GameName = "Big Rig Farming Simulator", Category = "UEVR" }
            }));
            service.SubscribeToLibraryChanges();
            service.SeedKnownGames();

            // A decision that came from a Steam App ID match (or a manual accept): the two names
            // share nothing and no edit distance could bridge them, so the per-game scan's cheap
            // length prune rejects the PAIR - and must still honour the decision. Moving the prune
            // in front of the decision lookup makes this test fail and silently drops accepted
            // matches, which is the bug this test exists to catch.
            var game = new Game("Tractor Operator 2023 Deluxe Edition") { TagIds = new List<Guid>() };
            settings.SetDecision("src-prune", "Big Rig Farming Simulator", game.Id, MatchDecision.Accepted);
            _games.Items.Add(game);
            _games.RaiseItemUpdated(new[]
            {
                (oldData: new Game("Placeholder Import Row") { Id = game.Id }, newData: game)
            });

            Assert.That(SpinWait.SpinUntil(() => game.TagIds.Count > 0, TimeSpan.FromSeconds(25)), Is.True,
                "a stored Accepted decision must be re-applied even when the two names cannot match. Log:\n" + _logger.Dump());
        }

        // --- A bulk rename is cheaper as one catalog walk than as one walk per game ---------

        [Test]
        public void RenameBatchAboveThePerGameLimitIsHandledByOneFullLibraryScan()
        {
            var service = CreateService();
            _registry.Register(new TestParser("src-bulk", new[]
            {
                new VrModEntry { GameName = "Half Life 2 VR", Category = "UEVR" },
                new VrModEntry { GameName = "BoneKing VR Edition", Category = "UEVR" }
            }));
            service.SubscribeToLibraryChanges();
            service.SeedKnownGames();

            // More games in one debounced batch than the per-game path is worth: e.g. a curated VR
            // list pasted in, or a metadata-provider pass that rewrites sorting names.
            var count = VrModTagService.MaxPerGameScanBatch + 3;
            var games = new List<Game>();
            for (var i = 0; i < count; i++)
            {
                var name = i == 0 ? "Half Life 2 VR" : i == 1 ? "BoneKing VR Edition" : $"Renamed Library Entry {i} With A Long Title";
                games.Add(new Game(name) { TagIds = new List<Guid>() });
            }
            foreach (var g in games) _games.Items.Add(g);
            _games.RaiseItemUpdated(games.Select(g => (oldData: new Game($"Old Sorting Name {g.Id}") { Id = g.Id }, newData: g)));

            Assert.That(SpinWait.SpinUntil(() => _logger.Lines.Any(l => l.Contains("full library scan instead of")), TimeSpan.FromSeconds(25)), Is.True,
                "a batch larger than MaxPerGameScanBatch must be handled by one entry-driven scan (the per-game path walks the whole catalog once per game). Log:\n" + _logger.Dump());
            Assert.That(SpinWait.SpinUntil(() => games[0].TagIds.Count > 0 && games[1].TagIds.Count > 0, TimeSpan.FromSeconds(25)), Is.True,
                "the full scan must tag the renamed games, not just log about the batch. Log:\n" + _logger.Dump());
            Assert.That(_logger.Lines.Any(l => l.Contains("New-game scan complete")), Is.False,
                "the per-game path must not ALSO run for the same batch - it would scan them twice. Log:\n" + _logger.Dump());
        }

        [Test]
        public void RenameBatchWithinThePerGameLimitStaysScopedToThoseGames()
        {
            var service = CreateService();
            _registry.Register(new TestParser("src-bulk", new[]
            {
                new VrModEntry { GameName = "Half Life 2 VR", Category = "UEVR" }
            }));
            service.SubscribeToLibraryChanges();
            service.SeedKnownGames();

            var games = new[]
            {
                new Game("Half Life 2 VR") { TagIds = new List<Guid>() },
                new Game("Some Other Renamed Entry") { TagIds = new List<Guid>() }
            };
            foreach (var g in games) _games.Items.Add(g);
            _games.RaiseItemUpdated(games.Select(g => (oldData: new Game($"Old Sorting Name {g.Id}") { Id = g.Id }, newData: g)));

            // Wait for the batch to FINISH, not for the first tag. "New-game scan complete" is written
            // after the last game of the batch, while games[0] gets tagged earlier - asserting on the
            // line immediately after the first tag is a race: the completion line can land in the
            // log after the assert has already run.
            var finished = SpinWait.SpinUntil(
                () => _logger.Lines.Any(l => l.Contains("New-game scan complete for 2 game(s)")),
                TimeSpan.FromSeconds(25));
            Assert.That(finished, Is.True,
                "the 2-game batch never reported a completed scan. Log:\n" + _logger.Dump());
            Assert.That(games[0].TagIds.Count, Is.GreaterThan(0),
                "a small batch must still be scanned and tagged. Log:\n" + _logger.Dump());
            Assert.That(_logger.Lines.Any(l => l.Contains("full library scan instead of")), Is.False,
                "a small batch must not trigger a whole-library rescan. Log:\n" + _logger.Dump());
        }

        /// <summary>
        /// The drain re-queues a batch when the scan did not run, and it can only do that if the
        /// full-scan path reports whether it ran.
        /// </summary>
        [Test]
        public async System.Threading.Tasks.Task RunFullScanAsync_ReportsFalseWhenNothingCouldScan()
        {
            var service = CreateService();

            Assert.That(await service.RunFullScanAsync(), Is.False,
                "no source is enabled, so nothing was scanned; a caller holding a batch must put it back.");
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

        private class RecordingLogger : ILogger
        {
            // The drain runs on a timer thread while the test thread asserts, so both the writes and
            // the reads have to be serialized; Lines hands back a copy for that reason.
            private readonly object _sync = new();
            private readonly List<string> _lines = new();

            public List<string> Lines
            {
                get { lock (_sync) return new List<string>(_lines); }
            }

            public string Dump()
            {
                lock (_sync) return string.Join("\n", _lines);
            }

            private void Add(string line)
            {
                lock (_sync) _lines.Add(line);
            }

            public void Info(string message) => Add("INFO " + message);
            public void Debug(string message) => Add("DEBUG " + message);
            public void Warn(string message) => Add("WARN " + message);
            public void Error(string message) => Add("ERROR " + message);
            public void Error(Exception ex) => Add("ERROR " + ex);
            public void Info(Exception ex, string message) => Add("INFO " + message + " " + ex);
            public void Debug(Exception ex, string message) => Add("DEBUG " + message + " " + ex);
            public void Warn(Exception ex, string message) => Add("WARN " + message + " " + ex);
            public void Error(Exception ex, string message) => Add("ERROR " + message + " " + ex);
            public void Trace(string message) => Add("TRACE " + message);
            public void Trace(Exception ex, string message) => Add("TRACE " + message + " " + ex);
        }
    }
}
