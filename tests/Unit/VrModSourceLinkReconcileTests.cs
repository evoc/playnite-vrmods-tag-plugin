using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /// Tests for the bidirectional source-link lifecycle in <see cref="VrModTagService"/>:
    /// links are added for saved accepted matches and removed again when the match is
    /// rejected, reverted to pending, the source is disabled, or the source's "Links"
    /// toggle is turned off. Foreign links (ones the plugin could not have generated)
    /// are never touched.
    /// </summary>
    [TestFixture]
    public class VrModSourceLinkReconcileTests
    {
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

        /// <summary>
        /// Hostile deep links from a compromised or aggregator-supplied source (app URI, file: URI,
        /// credential host-confusion URL) must never reach game.Links: Playnite's Details view opens
        /// a stored Link with the OS shell (Process.Start(UseShellExecute = true)) without validating
        /// its scheme. The match is still accepted and still owns links; only the unsafe URL is
        /// dropped, and the source's own registered website becomes the fallback link.
        /// </summary>
        [Test]
        public void Reconcile_HostileDeepLinks_AddNoUnsafeLink()
        {
            var game = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "file:///C:/Windows/System32/calc.exe" },
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "steam://rungameid/730" },
                new VrModEntry { GameName = "Skyrim", Category = "Native VR", SourceUrl = "https://pcvr-central.com@evil.tld/skyrim" }
            });
            _registry.Register(parser);
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, game.Id, MatchDecision.Accepted);

            service.ReconcileSourceLinks(game);

            Assert.That(game.Links!.Any(l => !WebUrl.IsSafeLinkUrl(l.Url)), Is.False);
            Assert.That(game.Links!.Select(l => l.Url), Is.EquivalentTo(new[] { "https://example.com/" }));
        }

        [Test]
        public void Reconcile_SavedAcceptedMatch_AddsSourceLink()
        {
            var service = NewService(out var skyrim, out var parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);

            service.ReconcileSourceLinks(skyrim);

            Assert.That(skyrim.Links, Has.Count.EqualTo(1));
            Assert.That(skyrim.Links![0].Name, Is.EqualTo("Test"));
            Assert.That(skyrim.Links![0].Url, Is.EqualTo("https://pcvrcentral.com/game/skyrim"));
        }

        [Test]
        public void Reconcile_RejectedAfterAccept_RemovesSourceLink()
        {
            var service = NewService(out var skyrim, out var parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));

            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Rejected);
            service.ReconcileSourceLinks(skyrim);

            Assert.That(skyrim.Links, Is.Empty);
        }

        [Test]
        public void Reconcile_PendingClearedAfterAccept_RemovesSourceLink()
        {
            var service = NewService(out var skyrim, out var parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));

            // The review dialog reverts the row to Pending and clears the saved decision.
            service.Settings.ClearDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id);
            service.ReconcileSourceLinks(skyrim);

            Assert.That(skyrim.Links, Is.Empty);
        }

        [Test]
        public void Reconcile_ToggleOffRemovesLink_ToggleOnRestoresIt()
        {
            var service = NewService(out var skyrim, out var parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));

            service.Settings.DisabledLinkSources.Add(parser.SourceId);
            service.ReconcileSourceLinks(skyrim);
            Assert.That(skyrim.Links, Is.Empty);

            service.Settings.DisabledLinkSources.Remove(parser.SourceId);
            service.ReconcileSourceLinks(skyrim);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));
            Assert.That(skyrim.Links![0].Url, Is.EqualTo("https://pcvrcentral.com/game/skyrim"));
        }

        [Test]
        public void Reconcile_SourceDisabled_RemovesSourceLink()
        {
            var service = NewService(out var skyrim, out var parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));

            parser.IsEnabled = false;
            service.ReconcileSourceLinks(skyrim);

            Assert.That(skyrim.Links, Is.Empty);
        }

        [Test]
        public void Reconcile_ForeignLinks_Untouched()
        {
            var service = NewService(out var skyrim, out var parser);

            var foreign1 = new Link("Steam", "https://store.steampowered.com/app/72850");
            var foreign2 = new Link("My Note", "https://example.com/other");
            skyrim.Links.Add(foreign1);
            skyrim.Links.Add(foreign2);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Rejected);
            service.ReconcileSourceLinks(skyrim);

            // Both foreign links survive the full accept→reject cycle.
            Assert.That(skyrim.Links, Has.Count.EqualTo(2));
            Assert.That(skyrim.Links, Does.Contain(foreign1));
            Assert.That(skyrim.Links, Does.Contain(foreign2));
        }

        [Test]
        public void Reconcile_Twice_NoDuplicateLinks()
        {
            var service = NewService(out var skyrim, out var parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);
            service.ReconcileSourceLinks(skyrim);

            Assert.That(skyrim.Links, Has.Count.EqualTo(1));
        }

        [Test]
        public void Scan_AddsLinkForPreviouslySavedMatch()
        {
            var service = NewService(out var skyrim, out var parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            // The user saved the match in an earlier session (decision persisted in settings).
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);

            // A fresh scan reconciles links for the games it matches.
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(skyrim.Links, Has.Count.EqualTo(1));
        }

        [Test]
        public void ReconcileForSource_ToggleOffRemovesOwnedLinksAcrossLibrary()
        {
            var service = NewService(out var skyrim, out var parser);
            var fallout = new Game("Fallout 4") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(fallout);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.Settings.SetDecisionForListing(parser.EntryFor("Fallout 4"), parser.SourceId, fallout.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);
            service.ReconcileSourceLinks(fallout);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));
            Assert.That(fallout.Links, Has.Count.EqualTo(1));

            service.Settings.DisabledLinkSources.Add(parser.SourceId);
            service.ReconcileSourceLinksForSource(parser.SourceId);

            Assert.That(skyrim.Links, Is.Empty);
            Assert.That(fallout.Links, Is.Empty);
        }

        [Test]
        public void ReconcileForSource_ToggleOnRestoresLinks()
        {
            var service = NewService(out var skyrim, out var parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);

            service.Settings.DisabledLinkSources.Add(parser.SourceId);
            service.ReconcileSourceLinksForSource(parser.SourceId);
            Assert.That(skyrim.Links, Is.Empty);

            service.Settings.DisabledLinkSources.Remove(parser.SourceId);
            service.ReconcileSourceLinksForSource(parser.SourceId);

            Assert.That(skyrim.Links, Has.Count.EqualTo(1));
            Assert.That(skyrim.Links![0].Url, Is.EqualTo("https://pcvrcentral.com/game/skyrim"));
        }

        [Test]
        public void ReconcileForSource_ColdCache_ToggleOnRestoresNothing()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<Guid>(), Links = null };
            _games.Items.Add(skyrim);

            // A fresh service: no scan ran, so the entry cache is cold. The decision exists
            // (persisted from an earlier session) but no entries are known.
            var service = NewServiceForGame(skyrim, out var parser);
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);

            service.ReconcileSourceLinksForSource(parser.SourceId);

            Assert.That(skyrim.Links, Is.Null);
        }

        [Test]
        public void ReconcileForSource_ColdCache_ToggleOffRemovesWebsiteLink()
        {
            var skyrim = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>
                {
                    new Link("Test", "https://example.com/"),
                    new Link("Steam", "https://store.steampowered.com/app/72850")
                }
            };
            _games.Items.Add(skyrim);

            var service = NewServiceForGame(skyrim, out var parser);
            // Cold cache: the only recognizable owned link is the source's website link.
            service.Settings.DisabledLinkSources.Add(parser.SourceId);

            service.ReconcileSourceLinksForSource(parser.SourceId);

            Assert.That(skyrim.Links, Has.Count.EqualTo(1));
            Assert.That(skyrim.Links![0].Url, Is.EqualTo("https://store.steampowered.com/app/72850"));
        }

        // --- What one per-game reconcile actually costs, measured --------------------------

        /// <summary>
        /// Reconciling one game must not cost the whole source catalog. It used to, twice:
        /// <c>ReconcileSourceLinksCore</c> ran a fuzzy pass over every cached entry AND a
        /// <c>GetDecision</c> lookup per cached entry - and it ran all of that on Playnite's UI
        /// thread, because the whole method was marshaled for the sake of mutating one
        /// <c>ObservableCollection</c>. This plugin really fetches ~3 100 entries, so a 40-game
        /// rename batch paid 40 catalog walks and 40 x 3 100 key-building lookups on the UI thread.
        /// The assertion is about SCALING, not about this machine's speed: if the cost per game is
        /// the same at 300 and 3 000 cached entries, the catalog is no longer inside the loop. The
        /// catalog here is deliberately mixed in length, so some entries DO fall inside the length
        /// band and have to be rejected by the character bound instead.
        /// </summary>
        [Test]
        public void Reconcile_PerGameCostDoesNotGrowWithEveryCachedCatalogEntry()
        {
            const int gamesToReconcile = 40;
            var small = MeasureReconcilePerGameMs(300, gamesToReconcile, MixedLengthCatalog);
            var big = MeasureReconcilePerGameMs(3000, gamesToReconcile, MixedLengthCatalog);

            TestContext.Out.WriteLine($"reconcile cost: {small:F2} ms/game at 300 cached entries, {big:F2} ms/game at 3000");

            Assert.That(big, Is.LessThan(10),
                $"one reconcile costs {big:F1} ms at a 3 000-entry catalog; a 40-game rename batch " +
                $"puts {big * gamesToReconcile / 1000:F1} s of work behind Playnite's UI dispatcher");
            Assert.That(big, Is.LessThan(small * 4 + 1),
                $"cost per game grew {big / Math.Max(small, 0.01):F1}x when the cached catalog grew 10x - " +
                $"the catalog is still being walked per game ({small:F1} ms -> {big:F1} ms)");
        }

        /// <summary>
        /// The case the length band cannot help with: a catalog where every entry is exactly the
        /// right length to be a candidate for a game. Those all survive the length prune, and the
        /// character-count bound is what keeps them from costing a Levenshtein table each.
        /// </summary>
        [Test]
        public void Reconcile_PerGameCostStaysSmallWhenEveryCatalogEntryIsTheRightLength()
        {
            const int gamesToReconcile = 40;
            var small = MeasureReconcilePerGameMs(300, gamesToReconcile, SameLengthDisjointCatalog);
            var big = MeasureReconcilePerGameMs(3000, gamesToReconcile, SameLengthDisjointCatalog);

            TestContext.Out.WriteLine($"reconcile cost, length-compatible catalog: {small:F2} ms/game at 300, {big:F2} ms/game at 3000");

            Assert.That(big, Is.LessThan(10),
                $"{big:F1} ms per game when every one of 3 000 cached entries is a length-wise " +
                $"candidate; the character bound is not carrying the load");
            Assert.That(big, Is.LessThan(small * 4 + 1),
                $"cost per game grew {big / Math.Max(small, 0.01):F1}x with catalog size in the " +
                $"length-compatible case ({small:F1} ms -> {big:F1} ms)");
        }

        private static string MixedLengthCatalog(int i) => (i % 3) switch
        {
            0 => $"Catalog Entry {i}",
            1 => $"Catalog Entry {i} With A Longer Name For Blocking",
            _ => $"Game {i} VR Mod Pack Two Words"
        };

        /// <summary>
        /// Same shape and length as the library titles below, letters chosen so almost none of them
        /// are shared: every entry stays inside the length band and none of them can share enough
        /// characters to reach the similarity threshold.
        /// </summary>
        private static string SameLengthDisjointCatalog(int i) => $"Kxqzwm Fjvpd {i} Ybgh Sockq";

        private static double MeasureReconcilePerGameMs(int catalogSize, int gameCount, Func<int, string> catalogName)
        {
            var games = new FakeItemCollection<Game>();
            var tags = new FakeItemCollection<Tag>();
            var api = Substitute.For<IPlayniteAPI>();
            var db = Substitute.For<IGameDatabaseAPI>();
            db.Games.Returns(games);
            db.Tags.Returns(tags);
            db.BufferedUpdate().Returns(new FakeBufferedUpdate());
            api.Database.Returns(db);

            var registry = new VrModSourceRegistry();
            var entries = new List<VrModEntry>();
            for (var i = 0; i < catalogSize; i++)
                entries.Add(new VrModEntry { GameName = catalogName(i), Category = "UEVR" });
            registry.Register(new TestParser(entries));

            var library = new List<Game>();
            for (var i = 0; i < gameCount; i++)
            {
                var g = new Game($"Library Game {i} Plain Title") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
                games.Items.Add(g);
                library.Add(g);
            }

            var service = new VrModTagService(api, new VrModTagSettings(), new NullLogger(), registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();   // fills the entry cache; not timed

            // First timed pass measured the JIT and the index build, not reconcile: the 300-entry
            // run came out MORE expensive than the 3 000-entry run until this warm-up pass existed.
            foreach (var g in library) service.ReconcileSourceLinks(g);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (var g in library) service.ReconcileSourceLinks(g);
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds / gameCount;
        }

        private VrModTagService NewService(out Game skyrim, out TestParser parser)
        {
            skyrim = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            _games.Items.Add(skyrim);
            return NewServiceForGame(skyrim, out parser);
        }

        private VrModTagService NewServiceForGame(Game game, out TestParser parser)
        {
            parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://pcvrcentral.com/game/skyrim" },
                new VrModEntry { GameName = "Fallout 4", Category = "UEVR", SourceUrl = "https://pcvrcentral.com/game/fallout" }
            });
            _registry.Register(parser);
            return new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
        }

        private class TestParser : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;
            public TestParser(IEnumerable<VrModEntry> entries) : base($"test-{Guid.NewGuid()}", "Test", "https://example.com/")
            {
                _entries = entries;
            }
            /// <summary>The fixture listing a test wants to decide on, so the test can seed the
            /// decision under the same identity key the scan reads it from.</summary>
            public VrModEntry EntryFor(string listingName) => _entries.First(e => e.GameName == listingName);

            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult(_entries);
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }
    }
}
