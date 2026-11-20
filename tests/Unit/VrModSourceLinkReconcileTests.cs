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
            Assert.That(skyrim.Links![0].Url, Is.EqualTo("https://example.com/game/skyrim"));
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
            Assert.That(skyrim.Links![0].Url, Is.EqualTo("https://example.com/game/skyrim"));
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
            Assert.That(skyrim.Links![0].Url, Is.EqualTo("https://example.com/game/skyrim"));
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

        /// <summary>
        /// A source link is a page on that source's own site. When a scraped card links somewhere
        /// else — the off-site cards Flat2VR publishes for some of its ports, a game's store page
        /// — what the user gets instead is the source's front door, where they can look the game
        /// up, rather than a stranger's page carrying neither the mod nor the source's name.
        /// </summary>
        [Test]
        public void Reconcile_OffSiteDeepLink_WritesTheSourcePageInstead()
        {
            var game = new Game("Skyrim") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(game);
            var parser = new TestParser(
                new[] { new VrModEntry { GameName = "Skyrim", Category = "Flat2VR Port", SourceUrl = "https://game-vendor.example/" } },
                "https://mod-source.example/");
            _registry.Register(parser);
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, game.Id, MatchDecision.Accepted);

            service.ReconcileSourceLinks(game);

            Assert.That(game.Links, Has.Count.EqualTo(1));
            Assert.That(game.Links![0].Name, Is.EqualTo("Test"));
            Assert.That(game.Links![0].Url, Is.EqualTo("https://mod-source.example/"));
        }

        /// <summary>Same site, different spelling: "www." on either side is still the source's own
        /// page, so the deep link survives instead of degrading to the front door.</summary>
        [Test]
        public void Reconcile_DeepLinkCarriesWwwWhileTheSiteDoesNot_KeepsThePage()
        {
            var game = new Game("Skyrim") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(game);
            var parser = new TestParser(
                new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "https://www.example.com/games/skyrim/" } },
                "https://example.com/");
            _registry.Register(parser);
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, game.Id, MatchDecision.Accepted);

            service.ReconcileSourceLinks(game);

            Assert.That(game.Links, Has.Count.EqualTo(1));
            Assert.That(game.Links![0].Url, Is.EqualTo("https://www.example.com/games/skyrim/"));
        }

        /// <summary>With no source registered there is no site to compare the link against, and
        /// guessing there means throwing away a link that works: the entry keeps its own page.</summary>
        [Test]
        public void GetSourceLinkUrl_SourceNotRegistered_KeepsTheDeepLink()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var entry = new VrModEntry
            {
                GameName = "Skyrim",
                Category = "UEVR",
                SourceId = "unregistered",
                SourceUrl = "https://nothing-to-compare-against.example/mod"
            };

            Assert.That(service.GetSourceLinkUrl(entry), Is.EqualTo("https://nothing-to-compare-against.example/mod"));
        }

        /// <summary>
        /// Pinned so nobody claims the host rule covers the curator sources: their own page and
        /// every game's store page both live on store.steampowered.com, so a store page passes the
        /// same-site test untouched. The curator parser has to stop emitting the store page — the
        /// central rule will not do it on its behalf.
        /// </summary>
        [Test]
        public void GetSourceLinkUrl_StorePageOnTheCuratorsOwnHost_PassesTheHostRule()
        {
            var parser = new TestParser(
                new[] { new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://store.steampowered.com/app/72850/" } },
                "https://store.steampowered.com/curator/38688993-VorpX-Games/");
            _registry.Register(parser);
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);

            Assert.That(
                service.GetSourceLinkUrl(parser.EntryFor("Skyrim")),
                Is.EqualTo("https://store.steampowered.com/app/72850/"));
        }

        /// <summary>
        /// The review dialog's "reset and remove tags" writes settings <em>before</em> it asks for the
        /// removal, so the removal's own ledger change had no write behind it: the rows came off the games
        /// and the file went on describing them. A claim whose row is gone is the one state this ledger
        /// exists to prevent - and the first orphan claims the live test caught were exactly that - so the
        /// operation asks for the write itself instead of trusting a caller that already wrote.
        /// </summary>
        [Test]
        public void RemoveAllVrModTags_RemovingOurOwnLink_AsksForTheWriteThatDropsTheClaim()
        {
            var service = NewService(out var skyrim, out var parser);
            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));
            Assert.That(service.Settings.GetLinkClaims(skyrim.Id), Has.Count.EqualTo(1));

            // The flow the dialog runs: decisions cleared, settings saved, then the cleanup asked for.
            service.Settings.ClearAllDecisions();
            service.Settings.ConsumePendingLedgerWrites();
            var persistRequests = 0;
            service.SettingsPersistRequested = () => persistRequests++;

            service.RemoveAllVrModTags(new[] { skyrim });

            Assert.That(skyrim.Links, Is.Empty, "the source link should have come off with the decision");
            Assert.That(service.Settings.GetLinkClaims(skyrim.Id), Is.Empty, "and its claim should have gone with the row");
            Assert.That(persistRequests, Is.EqualTo(1), "the ledger dropped a claim and nothing was asked to write it");

            // And a second pass with nothing left to remove must not ask again: the file is ~480 KB.
            service.RemoveAllVrModTags(new[] { skyrim });
            Assert.That(persistRequests, Is.EqualTo(1), "a pass that changed nothing still asked for a write");
        }

        /// <summary>The per-source cleanup runs from the settings dialog, which writes the file only if
        /// the user presses Save afterwards. Its links come off whatever the user does next, so the claim
        /// drops it makes have to be announced by the operation itself.</summary>
        [Test]
        public void RemoveTagsForSource_RemovingOurOwnLink_AsksForTheWriteThatDropsTheClaim()
        {
            var service = NewService(out var skyrim, out var parser);
            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);
            Assert.That(service.Settings.GetLinkClaims(skyrim.Id), Has.Count.EqualTo(1));

            parser.IsEnabled = false;
            service.Settings.ConsumePendingLedgerWrites();
            var persistRequests = 0;
            service.SettingsPersistRequested = () => persistRequests++;

            service.RemoveTagsForSource(parser.SourceId);

            Assert.That(skyrim.Links, Is.Empty, "disabling the source should have taken its link with it");
            Assert.That(service.Settings.GetLinkClaims(skyrim.Id), Is.Empty, "and the claim");
            Assert.That(persistRequests, Is.EqualTo(1), "the ledger dropped a claim and nothing was asked to write it");
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

        /// <summary>
        /// What a library is left with when a source changes what it links to and nothing retires
        /// the old rows: the row an older version wrote (a storefront, under our own name) and the
        /// row the current one writes (a page of ours) sit side by side, because the reconcile owns
        /// a link only if it can re-derive one, and the old row's source data is gone. One scan with
        /// the retirement rule collapses that back to the current row.
        /// </summary>
        [Test]
        public void Reconcile_RetiredStorePageRow_CollapsesToTheCurrentLink()
        {
            var game = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>
                {
                    new Link("Retiring Test Source", "https://store.steampowered.com/app/72850/")
                }
            };
            _games.Items.Add(game);
            var parser = new RetiringTestParser(
                new[] { new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SteamAppId = "72850" } },
                "https://store.steampowered.com/curator/38688993-VorpX-Games/");
            _registry.Register(parser);
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, game.Id, MatchDecision.Accepted);

            service.ReconcileSourceLinks(game);

            Assert.That(game.Links, Has.Count.EqualTo(1));
            Assert.That(game.Links![0].Name, Is.EqualTo("Retiring Test Source"));
            Assert.That(game.Links![0].Url, Is.EqualTo("https://store.steampowered.com/curator/38688993-VorpX-Games/"));
        }

        /// <summary>The same library holding the same store page under a name we never wrote —
        /// Playnite's own Steam import names its link "Steam", and a user's own link can be named
        /// anything. It is not ours to delete, and the name half of the rule is what protects it:
        /// only our row is replaced, the user's stays beside it.</summary>
        [Test]
        public void Reconcile_RetiredRule_LeavesAStorePageLinkWeDidNotWrite()
        {
            var game = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>
                {
                    new Link("Steam", "https://store.steampowered.com/app/72850/")
                }
            };
            _games.Items.Add(game);
            var parser = new RetiringTestParser(
                new[] { new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SteamAppId = "72850" } },
                "https://store.steampowered.com/curator/38688993-VorpX-Games/");
            _registry.Register(parser);
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, game.Id, MatchDecision.Accepted);

            service.ReconcileSourceLinks(game);

            Assert.That(game.Links, Has.Count.EqualTo(2));
            Assert.That(game.Links!.Any(l => l.Name == "Steam"), Is.True);
            Assert.That(
                game.Links!.Any(l => l.Url == "https://store.steampowered.com/curator/38688993-VorpX-Games/"),
                Is.True);
        }

        /// <summary>A retired row goes even when the listing behind it is gone from the catalog —
        /// that is the situation the rule exists for. Retirement is independent of whether the
        /// source is switched off or has its link toggle on, because removing a row we wrote is not
        /// authoring a new one.</summary>
        [Test]
        public void Reconcile_RetiredRowWithNoListingLeft_StillComesOff()
        {
            var game = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>
                {
                    new Link("Retiring Test Source", "https://store.steampowered.com/app/72850/")
                }
            };
            _games.Items.Add(game);
            var parser = new RetiringTestParser(
                Array.Empty<VrModEntry>(),
                "https://store.steampowered.com/curator/38688993-VorpX-Games/");
            _registry.Register(parser);
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);

            service.RunFullScanAsync().GetAwaiter().GetResult();

            service.ReconcileSourceLinks(game);

            Assert.That(game.Links, Is.Empty);
        }

        #region The ledger: rows we wrote stay ours

        /// <summary>The moment a claim is written is the moment the row is added - that is the only
        /// point at which source, name and URL are all known together.</summary>
        [Test]
        public void Ledger_AClaimIsRecordedWhenThePluginAddsALink()
        {
            var service = NewService(out var skyrim, out var parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);

            var claims = service.Settings.GetLinkClaims(skyrim.Id);
            Assert.That(claims, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(claims[0].SourceId, Is.EqualTo(parser.SourceId));
                Assert.That(claims[0].Name, Is.EqualTo("Test"));
                Assert.That(claims[0].Url, Is.EqualTo("https://example.com/game/skyrim"));
            });
        }

        /// <summary>The leak, at the scale of one game. A year on, the listing is gone from the source
        /// and the game's decision went with it, so neither the catalog nor any saved decision can name
        /// the row still sitting on the game. Without the ledger the reconcile owns nothing and the row
        /// is permanent; with it, the row is ours and comes off.</summary>
        [Test]
        public void Ledger_AClaimedRowComesOffWhenNothingMatchesItAnymore()
        {
            var settings = new VrModTagSettings();
            var firstParser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://example.com/game/skyrim" }
            });
            _registry.Register(firstParser);
            var first = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);
            var game = new Game("Skyrim") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(game);

            first.RunFullScanAsync().GetAwaiter().GetResult();
            first.Settings.SetDecisionForListing(firstParser.EntryFor("Skyrim"), firstParser.SourceId, game.Id, MatchDecision.Accepted);
            first.ReconcileSourceLinks(game);
            Assert.That(game.Links, Has.Count.EqualTo(1), "the setup left no row to leak");

            // A year later: the source stopped listing the game, and the stale sweep cleared the decision.
            settings.ClearAllDecisions();
            var laterRegistry = new VrModSourceRegistry();
            laterRegistry.Register(new TestParser(Array.Empty<VrModEntry>()));
            var later = new VrModTagService(_mockApi, settings, new NullLogger(), laterRegistry);
            later.RunFullScanAsync().GetAwaiter().GetResult();

            later.ReconcileSourceLinks(game);

            Assert.Multiple(() =>
            {
                Assert.That(game.Links, Is.Empty, "a row we wrote outlived every record of why it was added");
                Assert.That(settings.GetLinkClaims(game.Id), Is.Empty, "the claim survived the row it described");
            });
        }

        /// <summary>Two things can share a URL. A row that was already there - which we therefore never
        /// wrote - must not gain a claim, or a later pass would treat the user's link as ours.</summary>
        [Test]
        public void Ledger_DoesNotClaimARowThatWasAlreadyOnTheGame()
        {
            var service = NewService(out var skyrim, out var parser);
            skyrim.Links!.Add(new Link("My own mod page", "https://example.com/game/skyrim"));

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);

            Assert.Multiple(() =>
            {
                Assert.That(skyrim.Links, Has.Count.EqualTo(1), "the plugin added a second link beside the user's");
                Assert.That(skyrim.Links![0].Name, Is.EqualTo("My own mod page"), "the user's link was renamed");
                Assert.That(service.Settings.GetLinkClaims(skyrim.Id), Is.Empty, "a row we did not write was claimed");
            });
        }

        /// <summary>And when our row goes, the user's row beside it - same URL, different name - stays.
        /// Removal is name-and-URL scoped for exactly this.</summary>
        [Test]
        public void Ledger_LeavesTheUsersRowBesideOursWhenOursGoes()
        {
            var service = NewService(out var skyrim, out var parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);
            skyrim.Links!.Add(new Link("My own mod page", "https://example.com/game/skyrim"));

            service.Settings.ClearDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id);
            service.ReconcileSourceLinks(skyrim);

            Assert.Multiple(() =>
            {
                Assert.That(skyrim.Links!.Select(l => l.Name), Is.EqualTo(new[] { "My own mod page" }));
                Assert.That(service.Settings.GetLinkClaims(skyrim.Id), Is.Empty);
            });
        }

        /// <summary>The user deleted our row by hand. The claim has to follow it, because a claim with
        /// no row would make a link the user later types by hand ours again - and the next pass would
        /// delete it from under them.</summary>
        [Test]
        public void Ledger_ForgetsAClaimWhoseRowTheUserDeleted()
        {
            var service = NewService(out var skyrim, out var parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);
            Assert.That(service.Settings.GetLinkClaims(skyrim.Id), Has.Count.EqualTo(1));

            skyrim.Links!.Clear();
            service.Settings.ClearDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, skyrim.Id);
            service.ReconcileSourceLinks(skyrim);

            Assert.That(service.Settings.GetLinkClaims(skyrim.Id), Is.Empty, "a claim outlived the row it described");
        }

        /// <summary>Two sources, one URL: the row is right as long as either claim survives, and the
        /// surviving claimant must not churn it. Before the ledger this case removed the row and added
        /// it back inside one pass, because the losing claimant's key read owned-and-not-desired.</summary>
        [Test]
        public void Ledger_KeepsASharedRowAndDoesNotChurnIt()
        {
            // Two listings, deliberately two objects: the catalog index dedupes candidates by entry
            // identity, so sharing one VrModEntry between two sources would model one listing, not two.
            VrModEntry Listing() => new VrModEntry
            {
                GameName = "Skyrim",
                Category = "Full Motion Controls",
                SourceUrl = "https://example.com/game/skyrim"
            };
            var first = new TestParser(new[] { Listing() });
            var second = new TestParser(new[] { Listing() });
            _registry.Register(first);
            _registry.Register(second);
            var game = new Game("Skyrim") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(game);
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecisionForListing(first.EntryFor("Skyrim"), first.SourceId, game.Id, MatchDecision.Accepted);
            service.Settings.SetDecisionForListing(second.EntryFor("Skyrim"), second.SourceId, game.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(game);

            Assert.Multiple(() =>
            {
                Assert.That(game.Links, Has.Count.EqualTo(1), "two claimants on one URL wrote two rows");
                Assert.That(service.Settings.GetLinkClaims(game.Id), Has.Count.EqualTo(2));
            });

            // One claimant withdraws. The row is still wanted by the other, so nothing may move - no
            // remove-and-re-add, which is what this case did before the ledger.
            service.Settings.ClearDecisionForListing(second.EntryFor("Skyrim"), second.SourceId, game.Id);
            var changed = service.ReconcileSourceLinksCore(game);

            Assert.Multiple(() =>
            {
                Assert.That(changed, Is.False, "a row still wanted by one claimant was removed and re-added");
                Assert.That(game.Links, Has.Count.EqualTo(1));
            });

            // The withdrawn claimant's claim stays while its row does. This is deliberate and it is the
            // whole reason the ledger can be trusted: a claim is the evidence that a row is ours, so a
            // claim that dies because one source stopped wanting the URL turns that row into a permanent
            // orphan - the exact failure the ledger exists to close. Claims die with their row instead.
            Assert.That(service.Settings.GetLinkClaims(game.Id), Has.Count.EqualTo(2), "a claim died while its row lived");

            // Last claimant withdraws: the row goes, and every claim on it goes with it.
            service.Settings.ClearDecisionForListing(first.EntryFor("Skyrim"), first.SourceId, game.Id);
            service.ReconcileSourceLinks(game);

            Assert.Multiple(() =>
            {
                Assert.That(game.Links, Is.Empty, "a row nobody wants any more stayed on the game");
                Assert.That(service.Settings.GetLinkClaims(game.Id), Is.Empty, "claims outlived the row they describe");
            });
        }

        /// <summary>A scan writes the ledger, and a scan is not a settings edit: Playnite does not
        /// rewrite an extension's config.json on exit, so the service has to ask - and only when the
        /// ledger actually moved, because that file is 480 KB and scans run on a timer.</summary>
        [Test]
        public void Ledger_AScanAsksToPersistOnlyWhenItChangedTheLedger()
        {
            var settings = new VrModTagSettings();
            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://example.com/game/skyrim" }
            });
            _registry.Register(parser);
            var game = new Game("Skyrim") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(game);
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);
            var saves = 0;
            service.SettingsPersistRequested = () => saves++;

            // Nothing accepted yet: the scan matches, tags nothing, writes no link, asks for no write.
            service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(saves, Is.EqualTo(0), "a scan that wrote no link asked to rewrite the settings file");

            settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, game.Id, MatchDecision.Accepted);
            service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.Multiple(() =>
            {
                Assert.That(game.Links, Has.Count.EqualTo(1), "the accepted match wrote no link");
                Assert.That(saves, Is.EqualTo(1), "a scan that wrote the ledger never asked to persist it");
            });

            // Steady state: same decision, same link, same claim. Nothing to save.
            service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(saves, Is.EqualTo(1), "a steady-state scan rewrote the settings file again");
        }

        #endregion

        #region Adoption: the rows older versions wrote

        [Test]
        public void Adopt_ClaimsWhatOnlyWeCouldHaveWritten()
        {
            var service = NewService(out var skyrim, out var parser);
            skyrim.Links!.Add(new Link("Test", "https://example.com/game/from-an-older-version"));

            Assert.That(service.AdoptExistingSourceLinks(), Is.EqualTo(1));
            Assert.That(service.Settings.GetLinkClaims(skyrim.Id).Select(c => c.Url),
                Is.EqualTo(new[] { "https://example.com/game/from-an-older-version" }));
        }

        [Test]
        public void Adopt_ClaimsTheSourcesOwnPageUnderItsName()
        {
            var service = NewService(out var skyrim, out var parser);
            skyrim.Links!.Add(new Link("Test", "https://example.com/"));

            Assert.That(service.AdoptExistingSourceLinks(), Is.EqualTo(1));
        }

        /// <summary>A row on somebody else's site is not ours however it is captioned: the site of the
        /// source that published the mod is half the evidence, and without it adoption would sweep up
        /// links to the original game's page.</summary>
        [Test]
        public void Adopt_LeavesAnOffSiteRowAloneEvenUnderOurName()
        {
            var service = NewService(out var skyrim, out var parser);
            skyrim.Links!.Add(new Link("Test", "https://somebody-else.example/game/skyrim"));

            Assert.Multiple(() =>
            {
                Assert.That(service.AdoptExistingSourceLinks(), Is.EqualTo(0));
                Assert.That(service.Settings.GetLinkClaims(skyrim.Id), Is.Empty);
            });
        }

        [Test]
        public void Adopt_LeavesARowWithAnotherName_Alone()
        {
            var service = NewService(out var skyrim, out var parser);
            skyrim.Links!.Add(new Link("Skyrim VR patch", "https://example.com/game/skyrim"));

            Assert.That(service.AdoptExistingSourceLinks(), Is.EqualTo(0));
        }

        /// <summary>Retire before adopt, in this pass too: a row a source stopped writing must go, not
        /// gain a claim that keeps it alive.</summary>
        [Test]
        public void Adopt_SkipsAShapeTheSourceRetired()
        {
            var game = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link> { new Link("Retiring Test Source", "https://store.steampowered.com/app/72850/") }
            };
            _games.Items.Add(game);
            var parser = new RetiringTestParser(Array.Empty<VrModEntry>(), "https://store.steampowered.com/");
            _registry.Register(parser);
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);

            Assert.That(service.AdoptExistingSourceLinks(), Is.EqualTo(0));
            Assert.That(service.Settings.GetLinkClaims(game.Id), Is.Empty);
        }

        [Test]
        public void Adopt_RunsOnce_AndClaimsNothingTwice()
        {
            var service = NewService(out var skyrim, out var parser);
            skyrim.Links!.Add(new Link("Test", "https://example.com/game/from-an-older-version"));

            Assert.Multiple(() =>
            {
                Assert.That(service.AdoptExistingSourceLinks(), Is.EqualTo(1));
                Assert.That(service.AdoptExistingSourceLinks(), Is.EqualTo(0), "a second pass re-claimed the same row");
                Assert.That(service.Settings.LinkClaimCount, Is.EqualTo(1));
            });
        }

        /// <summary>The whole leak, end to end and through the scan rather than a direct call: a game we
        /// linked in an older version, which nothing matches any more, is not tagged, and holds no
        /// decision - so no loop driven by current matches ever looks at it. Adoption makes the row
        /// visible to the reconcile, and the ledger is the index that gets the scan to that game.</summary>
        [Test]
        public void Adopt_ThenScan_RemovesTheOrphanFromAnUntaggedGame()
        {
            var settings = new VrModTagSettings();
            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://example.com/game/skyrim" }
            });
            _registry.Register(parser);

            var matched = new Game("Skyrim") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            var orphan = new Game("Some Older Game")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link> { new Link("Test", "https://example.com/game/some-older-game") }
            };
            _games.Items.Add(matched);
            _games.Items.Add(orphan);

            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);
            Assert.That(service.AdoptExistingSourceLinks(), Is.EqualTo(1), "the orphan row was not adopted");

            // The other game has a saved accepted match, so the scan gives it a link: the sweep must
            // clean the orphan without disturbing a game that still matches.
            settings.SetDecisionForListing(parser.EntryFor("Skyrim"), parser.SourceId, matched.Id, MatchDecision.Accepted);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.Multiple(() =>
            {
                Assert.That(orphan.Links, Is.Empty, "the orphan outlived the scan that was supposed to clean it");
                Assert.That(settings.GetLinkClaims(orphan.Id), Is.Empty);
                Assert.That(matched.Links, Has.Count.EqualTo(1), "the sweep damaged a game that still matches");
            });
        }

        /// <summary>The uninstall promise, which is the same leak in a different costume: "no plugin link
        /// survives" cannot be true if the only links it can find are on games that still carry a tag.</summary>
        #endregion

        private VrModTagService NewServiceForGame(Game game, out TestParser parser)
        {
            parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://example.com/game/skyrim" },
                new VrModEntry { GameName = "Fallout 4", Category = "UEVR", SourceUrl = "https://example.com/game/fallout" }
            });
            _registry.Register(parser);
            return new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
        }

        private class TestParser : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;
            public TestParser(IEnumerable<VrModEntry> entries, string websiteUrl = "https://example.com/")
                : base($"test-{Guid.NewGuid()}", "Test", websiteUrl)
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

        /// <summary>A source that links a page of its own today and carries a retirement rule for the
        /// store page an earlier version of it wrote — the curator lists, modelled so the service's
        /// behaviour is under test and not only the rule's.</summary>
        private sealed class RetiringTestParser : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;

            public RetiringTestParser(IEnumerable<VrModEntry> entries, string websiteUrl)
                : base($"retiring-{Guid.NewGuid()}", "Retiring Test Source", websiteUrl)
            {
                _entries = entries;
                RetiredLinks = new[]
                {
                    new RetiredLinkRule(
                        DisplayName,
                        @"^https?://store\.steampowered\.com/app/\d+(?:[/?#]|$)",
                        "test rule: a storefront is not a page this source publishes")
                };
            }

            public VrModEntry EntryFor(string listingName) => _entries.First(e => e.GameName == listingName);

            public override IReadOnlyList<RetiredLinkRule> RetiredLinks { get; }
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult(_entries);
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }
    }
}
