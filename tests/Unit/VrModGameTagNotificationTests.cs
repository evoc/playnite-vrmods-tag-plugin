using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// A game's tag list has to change in a way Playnite's own views are listening for.
    ///
    /// Reported by the owner against a live build: the per-game menu actions worked - the mod was
    /// applied, the mod was refused - but the game he had open kept showing the tags it had before,
    /// and the change appeared only after he clicked another game and back. The writes were correct in
    /// games.db; the screen was not told.
    ///
    /// Playnite's views refresh from the change notification on the <see cref="Game.TagIds"/> property
    /// (its setter also raises the derived Tags field the field binds to). Adding to or removing from
    /// the list a game already holds raises nothing: the object is the same, the reference is the same,
    /// and WPF has no reason to look again. So the plugin hands a new list to the property instead of
    /// editing the one that is there - <see cref="VrModGameTags"/>, one rule, used by every writer.
    ///
    /// What these tests hold:
    /// 1. A scan that applies a mod announces the tag change.
    /// 2. The per-game menu's "Reject all assigned VR mods..." announces it on the way back out too -
    ///    the removal side, which is the side that misleads most: a tag that refuses to leave looks
    ///    like the action never ran at all.
    /// 3. A scan with nothing to change announces nothing: the fix must not turn into re-notifying
    ///    every game in the library on every scan, which for a few thousand games is the library view
    ///    redrawing itself for no reason.
    /// </summary>
    [TestFixture]
    public class VrModGameTagNotificationTests
    {
        private static readonly Guid UmbrellaTagId = new Guid("cccccccc-0000-0000-0000-000000000001");
        private static readonly Guid UevrTagId = new Guid("cccccccc-0000-0000-0000-000000000002");
        private const string SourceId = "src-notify";
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
            AddTag(UmbrellaTagId, VrModCategories.TagUmbrella);
            AddTag(UevrTagId, VrModCategories.TagNameFor("UEVR"));
        }

        [Test]
        public void RunFullScan_AppliesTags_RaisesTheTagChangePlayniteRedrawsFrom()
        {
            var parser = RegisterSource();
            var game = AddGame("Skyrim");
            _settings.AcceptListing(parser, "Skyrim", game.Id);

            var raised = WatchTags(game);
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.Multiple(() =>
            {
                Assert.That(TagNames(game), Does.Contain(VrModCategories.TagUmbrella),
                    "setup: the mod really was applied, so there is a change to announce");
                Assert.That(raised(), Does.Contain(nameof(Game.TagIds)),
                    "and the game said so: a tag added to the list where it stands is in games.db and " +
                    "invisible on screen until the user clicks the game again");
            });
        }

        [Test]
        public void RejectAssignedVrMods_TakesTagsOff_RaisesTheTagChangePlayniteRedrawsFrom()
        {
            // The menu item the owner was using. The removal side is the one that lies best: the tag
            // stays on screen, so the action reads as having done nothing, and the user does it again.
            var game = AddGame("Skyrim", UmbrellaTagId, UevrTagId);
            _settings.SetDecisionByKey(
                VrModNameMatcher.MakeDecisionKey(SourceId, "Skyrim UEVR", game.Id), MatchDecision.Accepted);

            var raised = WatchTags(game);
            var (rejected, gamesChanged) = _service.RejectAssignedVrMods(new List<Game> { game });

            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.EqualTo(1));
                Assert.That(gamesChanged, Is.EqualTo(1));
                Assert.That(TagNames(game), Is.Empty, "setup: the tags really came off");
                Assert.That(raised(), Does.Contain(nameof(Game.TagIds)),
                    "and the game said so");
            });
        }

        [Test]
        public void RunFullScan_NothingToChange_RaisesNoTagChangeForTheGame()
        {
            // The other half of the rule: making the change visible must not become announcing a
            // change every scan whether there was one or not. A library scan touches every game, and a
            // notification per game is the library view redrawing a few thousand times for nothing.
            var parser = RegisterSource();
            var game = AddGame("Skyrim", UmbrellaTagId, UevrTagId);
            _settings.AcceptListing(parser, "Skyrim", game.Id);
            // The link the accepted pairing stands for, so the reconcile pass has nothing to add either.
            game.Links = new System.Collections.ObjectModel.ObservableCollection<Link>(
                new[] { new Link("Test", "https://example.com/game/skyrim") });

            var raised = WatchTags(game);
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.Multiple(() =>
            {
                Assert.That(TagNames(game), Is.EquivalentTo(new[] { VrModCategories.TagUmbrella, VrModCategories.TagNameFor("UEVR") }),
                    "the game was already correct, which is the point of the test");
                Assert.That(raised(), Does.Not.Contain(nameof(Game.TagIds)),
                    "so nothing was announced - the writes happen where a change happened, not per scan");
            });
        }

        // -----------------------------------------------------------------------------------------
        // Helpers, duplicated per fixture as the other scan tests do (this suite has no shared mock
        // fixture to inherit).
        // -----------------------------------------------------------------------------------------

        private TestParser RegisterSource()
        {
            var parser = new TestParser(SourceId, new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "https://example.com/game/skyrim" }
            });
            _registry.Register(parser);
            return parser;
        }

        private void AddTag(Guid id, string name) => _tags.Items.Add(new Tag(name) { Id = id });

        private Game AddGame(string name, params Guid[] tagIds)
            => AddGame(name, tagIds.AsEnumerable());

        private Game AddGame(string name, IEnumerable<Guid> tagIds)
        {
            var game = new Game(name) { TagIds = tagIds.ToList() };
            _games.Items.Add(game);
            return game;
        }

        /// <summary>
        /// Records every property change this game announces from now on. Playnite's models raise the
        /// changes its views bind to, so the list is the same signal the library view acts on - the
        /// check is that the signal happens, not what the plugin believes it did.
        /// </summary>
        private Func<List<string>> WatchTags(Game game)
        {
            var raised = new List<string>();
            ((INotifyPropertyChanged)game).PropertyChanged += (_, e) =>
            {
                lock (raised) raised.Add(e.PropertyName);
            };
            return () => { lock (raised) return new List<string>(raised); };
        }

        /// <summary>The names of the tags on a game - what a user reads in the Tags field.</summary>
        private List<string> TagNames(Game game)
            => (game.TagIds ?? new List<Guid>())
                .Select(id => _tags.Items.FirstOrDefault(t => t.Id == id)?.Name ?? $"<missing {id}>")
                .ToList();

        private class TestParser : BaseVrModParser
        {
            private readonly List<VrModEntry> _entries;

            public TestParser(string sourceId, IEnumerable<VrModEntry> entries)
                : base(sourceId, "Test", "https://example.com/")
            {
                _entries = entries.ToList();
            }

            public override string? ChangeIdentifier => null;
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult<IEnumerable<VrModEntry>>(_entries);
        }
    }
}
