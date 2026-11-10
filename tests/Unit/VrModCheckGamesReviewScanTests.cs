using System;
using System.Collections.Generic;
using System.Linq;
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
    /// The game context menu's "Check this game for VR mods..."
    /// (<see cref="VrModTagService.RunScanForGamesWithReview"/>): scan the selection, and hand the
    /// review dialog what it knows about those games afterwards.
    ///
    /// Two decisions in this entry point are worth holding down.
    ///
    /// It returns null when the scan did not run, and the caller must not open a dialog then. That is
    /// not a failure to report: a library scan already in flight, or no source switched on, is a
    /// second tap on the same thing, and the user gets one window (the running scan's) rather than an
    /// empty one pretending to be his result.
    ///
    /// The rows come back for the selection, not for the library, and they are the game's whole
    /// picture - assigned, rejected, pending and the near-miss suggestions - not merely what this scan
    /// turned up. A user who right-clicks one game to ask "what about this one?" gets an answer about
    /// that game; and a game he already said yes to shows that yes, or the dialog reads as amnesia
    /// about a decision he made.
    ///
    /// What these tests hold:
    /// 1. A scan already in flight: null, and immediately - the check never queues behind the scan.
    /// 2. No source enabled, and no games selected: null for the same reason, not an empty dialog.
    /// 3. The rows are scoped to the selection: another game's matched listing does not appear.
    /// 4. A selected game comes back with every status it has, not only this scan's news.
    /// </summary>
    [TestFixture]
    public class VrModCheckGamesReviewScanTests
    {
        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private VrModTagSettings _settings = null!;
        private VrModSourceRegistry _registry = null!;
        private VrModTagService _service = null!;

        [SetUp]
        public void SetUp()
        {
            var api = Substitute.For<IPlayniteAPI>();
            var db = Substitute.For<IGameDatabaseAPI>();
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();
            db.Games.Returns(_games);
            db.Tags.Returns(_tags);
            db.BufferedUpdate().Returns(new FakeBufferedUpdate());
            api.Database.Returns(db);
            _settings = new VrModTagSettings();
            _registry = new VrModSourceRegistry();
            _service = new VrModTagService(api, _settings, new NullLogger(), _registry);
        }

        // -----------------------------------------------------------------------------------------
        // 1: the gate.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void RunScanForGamesWithReview_ScanAlreadyInFlight_ReturnsNullAndDoesNotWait()
        {
            var game = AddGame("Skyrim");
            var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var parser = new BlockingParser(new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } }, block.Task);
            _registry.Register(parser);

            // A full library scan is running - startup or the schedule. Its fetch parks, so the gate is
            // verifiably held before the check-scan is attempted.
            var first = _service.RunFullScanAsync();
            Assert.That(parser.Started.Wait(5000), Is.True, "the library scan never reached its fetch");
            Assert.That(first.IsCompleted, Is.False, "the library scan must still be holding the gate");

            // The menu action runs off Playnite's UI thread, like the plugin's RunFromMenu does.
            var check = Task.Run(() => _service.RunScanForGamesWithReview(new List<Game> { game }));
            Assert.That(check.Wait(5000), Is.True,
                "a check-scan must not sit and wait for a scan it cannot join - that is a frozen menu");
            Assert.That(check.Result, Is.Null,
                "null tells the caller to open nothing: the running scan already has the user's window");

            block.SetResult(true);
            Assert.That(first.Wait(10000), Is.True);
            Assert.That(first.IsFaulted, Is.False);
        }

        [Test]
        public void RunScanForGamesWithReview_AfterTheScanFinished_RunsNormally()
        {
            // The pair to the test above: the gate is released, so the next tap is a real scan and not a
            // permanent null. Without this, the in-flight test would pass just as happily on an entry
            // point that always returns null.
            var parser = new TestParser("src-check", new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } });
            _registry.Register(parser);
            var game = AddGame("Skyrim");

            var rows = _service.RunScanForGamesWithReview(new List<Game> { game });

            Assert.That(rows, Is.Not.Null, "with no scan in flight the check-scan runs");
            Assert.That(rows, Is.Not.Empty);
        }

        // -----------------------------------------------------------------------------------------
        // 2: nothing to scan.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void RunScanForGamesWithReview_NoSourceEnabled_ReturnsNull()
        {
            var game = AddGame("Skyrim");

            var rows = _service.RunScanForGamesWithReview(new List<Game> { game });

            Assert.That(rows, Is.Null,
                "every source switched off is not an empty result: it is nothing to ask about");
        }

        [Test]
        public void RunScanForGamesWithReview_NoGamesSelected_ReturnsNull()
        {
            _registry.Register(new TestParser("src-check", new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } }));
            AddGame("Skyrim");

            Assert.Multiple(() =>
            {
                Assert.That(_service.RunScanForGamesWithReview(new List<Game>()), Is.Null);
                Assert.That(_service.RunScanForGamesWithReview(null!), Is.Null);
                Assert.That(_service.RunScanForGamesWithReview(new List<Game> { (Game)null! }), Is.Null,
                    "a selection that holds nothing is not a selection of one");
            });
        }

        // -----------------------------------------------------------------------------------------
        // 3: the rows are scoped to the selection.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void RunScanForGamesWithReview_ReturnsOnlyTheSelectedGamesRows()
        {
            // Both games match a listing, and the scan fetches for both. The row set is then cut to the
            // selection - this is the assertion that matters, because the rows are built from the cached
            // listings of the whole library, so an unscoped filter would show the user his entire
            // library after a right-click on one game.
            var parser = new TestParser("src-check", new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" },
                new VrModEntry { GameName = "Half-Life: Alyx", Category = "Native VR" }
            });
            _registry.Register(parser);
            var skyrim = AddGame("Skyrim");
            AddGame("Half-Life: Alyx");

            var rows = _service.RunScanForGamesWithReview(new List<Game> { skyrim });

            Assert.Multiple(() =>
            {
                Assert.That(rows, Is.Not.Null);
                Assert.That(rows!.Count, Is.GreaterThan(0), "the selected game does have a suggested mod");
                Assert.That(rows.Select(r => r.PlayniteGame?.Id), Is.All.EqualTo(skyrim.Id),
                    "the review list after a one-game right-click holds that game's rows and nothing else");
                Assert.That(rows.Select(r => r.PlayniteGame?.Name), Does.Not.Contain("Half-Life: Alyx"));
            });
        }

        [Test]
        public void RunScanForGamesWithReview_TwoGamesSelected_ReturnsRowsForBoth()
        {
            var parser = new TestParser("src-check", new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" },
                new VrModEntry { GameName = "Half-Life: Alyx", Category = "Native VR" }
            });
            _registry.Register(parser);
            var skyrim = AddGame("Skyrim");
            var alyx = AddGame("Half-Life: Alyx");

            var rows = _service.RunScanForGamesWithReview(new List<Game> { skyrim, alyx });

            Assert.That(rows!.Select(r => r.PlayniteGame?.Name).Distinct().OrderBy(n => n),
                Is.EquivalentTo(new[] { "Half-Life: Alyx", "Skyrim" }),
                "a multi-selection asks about every game in it");
        }

        // -----------------------------------------------------------------------------------------
        // 4: the game's whole picture, not this scan's news.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void RunScanForGamesWithReview_ListsEveryStatusOfTheGame_NotOnlyWhatThisScanFound()
        {
            // Two mods for one game, one already accepted and one already refused. Note the shape: a
            // source is merged to one entry per game (BaseVrModParser.MergeDuplicateGames), so a game
            // with two mods has them from two sources - and this is the case the dialog exists for.
            // Both rows come back: the dialog the menu opens is the same list the user reviews from the
            // bell, so it shows his decisions - and a game whose accepted mod came back looking Pending
            // would let him un-accept a pairing just by looking at it.
            var first = new TestParser("src-a", new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "https://example.com/a" } });
            var second = new TestParser("src-b", new[] { new VrModEntry { GameName = "Skyrim", Category = "Native VR", SourceUrl = "https://example.com/b" } });
            _registry.Register(first);
            _registry.Register(second);
            var game = AddGame("Skyrim");
            _settings.SetDecisionForListing(first.EntryFor("Skyrim"), first.SourceId, game.Id, MatchDecision.Accepted);
            _settings.SetDecisionForListing(second.EntryFor("Skyrim"), second.SourceId, game.Id, MatchDecision.Rejected);

            var rows = _service.RunScanForGamesWithReview(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(rows, Is.Not.Null);
                Assert.That(rows!.Count, Is.EqualTo(2), "both mods of this game are in the list");
                Assert.That(rows.Select(r => r.Status), Does.Contain(MatchDecision.Accepted),
                    "the mod he already agreed to reads as agreed to");
                Assert.That(rows.Select(r => r.Status), Does.Contain(MatchDecision.Rejected),
                    "and the one he refused reads as refused");
            });
        }

        [Test]
        public void RunScanForGamesWithReview_APairingTheUserWasNeverAskedAbout_IsOfferedWithNothingBehindIt()
        {
            // The scan holds this pairing rather than applying it, and the row is where the question
            // finally gets asked - which is the whole reason the menu item opens a dialog.
            //
            // The row itself reads Accepted, and that is not a decision: a listing whose name is the
            // game's name is prefilled as a yes the user confirms by pressing Save (ReviewRowStatus'
            // autoAccept). The two things that must stay apart are the row's prefill and what is
            // stored - if a prefill ever started persisting itself, an unreviewed library would fill
            // with decisions on the first right-click.
            var parser = new TestParser("src-check", new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } });
            _registry.Register(parser);
            var game = AddGame("Skyrim");

            var rows = _service.RunScanForGamesWithReview(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(rows!.Count, Is.EqualTo(1), "the pairing is in front of the user");
                Assert.That(rows[0].Status, Is.EqualTo(MatchDecision.Accepted),
                    "an exact-name pairing is prefilled as a yes to confirm");
                Assert.That(game.TagIds, Is.Empty,
                    "while the scan itself wrote no tag: a prefill is not consent");
                Assert.That(_settings.GetDecisionByKey(
                        VrModNameMatcher.MakeDecisionKey(parser.SourceId, "Skyrim UEVR", game.Id)),
                    Is.Not.EqualTo(MatchDecision.Accepted),
                    "and no decision was stored behind the prefill");
            });
        }

        // -----------------------------------------------------------------------------------------
        // Helpers, duplicated per fixture as the other scan tests do (this suite has no shared mock
        // fixture to inherit).
        // -----------------------------------------------------------------------------------------

        private Game AddGame(string name)
        {
            var game = new Game(name) { TagIds = new List<Guid>() };
            _games.Items.Add(game);
            return game;
        }

        private class TestParser : BaseVrModParser
        {
            private readonly List<VrModEntry> _entries;

            public TestParser(string sourceId, IEnumerable<VrModEntry> entries)
                : base(sourceId, "Test", "https://example.com/")
            {
                _entries = entries.ToList();
            }

            /// <summary>The listing published under this display name, for keying a decision against it.</summary>
            public VrModEntry EntryFor(string gameName)
                => _entries.Single(e => string.Equals(e.GameName, gameName, StringComparison.OrdinalIgnoreCase));

            public override string? ChangeIdentifier => null;
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult<IEnumerable<VrModEntry>>(_entries);
        }

        /// <summary>A source whose fetch parks on a task the test controls, so the scan gate is held.</summary>
        private class BlockingParser : BaseVrModParser
        {
            private readonly TaskCompletionSource<bool> _started = new();
            private readonly Task _block;
            private readonly List<VrModEntry> _entries;
            private int _entered;

            public BlockingParser(IEnumerable<VrModEntry> entries, Task block)
                : base("src-blocking", "Blocking", "https://example.com/")
            {
                _entries = entries.ToList();
                _block = block;
            }

            /// <summary>Completes when the fetch has started (the caller's scan holds the gate by then).</summary>
            public Task Started => _started.Task;

            public override string? ChangeIdentifier => null;
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);

            public override async Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
            {
                if (System.Threading.Interlocked.Exchange(ref _entered, 1) == 0)
                    _started.TrySetResult(true);
                await _block;
                return _entries;
            }
        }
    }
}
