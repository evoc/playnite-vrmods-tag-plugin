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
    [TestFixture]
    public class VrModTagServiceTests
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
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();

            mockDb.Games.Returns(_games);
            mockDb.Tags.Returns(_tags);
            mockDb.BufferedUpdate().Returns(new FakeBufferedUpdate());
            _mockApi.Database.Returns(mockDb);

            _registry = new VrModSourceRegistry();
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

        [Test]
        public void RunFullScanAsync_MatchesBySteamId()
        {
            var skyrim = new Game("Skyrim")
            {
                Id = new System.Guid("11111111-1111-1111-1111-111111111111"),
                TagIds = new List<System.Guid>()
            };
            skyrim.Links = new ObservableCollection<Link>
            {
                new Link("Steam", "https://store.steampowered.com/app/72850/")
            };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SteamAppId = "72850" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var taggedGame = _games.Items[0];
            Assert.That(taggedGame.TagIds, Is.Not.Null);
            Assert.That(taggedGame.TagIds, Has.Count.EqualTo(2));
            Assert.That(_tags.Items[0].Name, Is.EqualTo("VRMod Full Motion"));
        }

        [Test]
        public void RunFullScanAsync_MatchesByNameExact()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var taggedGame = _games.Items[0];
            Assert.That(taggedGame.TagIds, Is.Not.Null);
            Assert.That(taggedGame.TagIds, Has.Count.EqualTo(2));
        }

        [Test]
        public void RunFullScanAsync_RecordsLastRunTagCount()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var status = _service.Settings.SourceStatuses[parser.SourceId.ToLowerInvariant()];
            // Found counts (independent of matching/review/apply): the one entry carries at
            // least one tag (its category) and no source deep link.
            Assert.That(status.LastEntryCount, Is.EqualTo(1));
            Assert.That(status.LastTagCount, Is.EqualTo(1));
            Assert.That(status.LastLinkCount, Is.EqualTo(0));
        }

        [Test]
        public void RunFullScanAsync_RecordsLastRunLinkCountForEntryWithSourceUrl()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "https://example.com/skyrim" }
            });
            _registry.Register(parser);

            // No saved decision: the found counts are independent of matching/review/apply -
            // the entry's deep link counts as found even though nothing is applied yet.
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var status = _service.Settings.SourceStatuses[parser.SourceId.ToLowerInvariant()];
            Assert.That(status.LastTagCount, Is.EqualTo(1));
            Assert.That(status.LastLinkCount, Is.EqualTo(1));
        }

        [Test]
        public void RunFullScanAsync_RejectedMatch_RecordsExplicitZeroApplyLine()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" }
            });
            _registry.Register(parser);

            // Pre-reject: the scan applies nothing, but the run log still ends with an explicit
            // 0/0 apply line (an absent line would read as "nothing happened").
            _service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Rejected);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var status = _service.Settings.SourceStatuses[parser.SourceId.ToLowerInvariant()];
            Assert.That(status.LastRunLog, Does.Contain("Apply: 0 tag(s) applied, 0 source link(s) ensured"));
            // The found count is unaffected by the rejection.
            Assert.That(status.LastTagCount, Is.EqualTo(1));
        }

        [Test]
        public void RunFullScanAsync_RecordsLastRunLog()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            // The run's debug log lands on the source's SourceStatus (the settings
            // "Last run" dialog): run start, fetch result, apply milestone.
            var status = _service.Settings.SourceStatuses[parser.SourceId.ToLowerInvariant()];
            Assert.That(status.LastRunLog, Is.Not.Empty);
            Assert.That(status.LastRunLog[0], Does.StartWith("Run started"));
            Assert.That(status.LastRunLog, Does.Contain("Fetched 1 entries"));
            Assert.That(status.LastRunLog, Does.Contain("Apply: 1 tag(s) applied, 0 source link(s) ensured"));
        }

        [Test]
        public void RunFullScanAsync_FailedFetch_RecordsFailedLineInRunLog()
        {
            var parser = new FailingParser();
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var status = _service.Settings.SourceStatuses[parser.SourceId.ToLowerInvariant()];
            Assert.That(status.LastResult, Is.False);
            // The failed fetch is named in the run log too (not just the error field).
            Assert.That(status.LastRunLog.Any(l => l.StartsWith("FAILED:")), Is.True);
            Assert.That(status.LastRunLog.Any(l => l.Contains("Simulated source failure")), Is.True);
        }

        [Test]
        public void RunFullScanAsync_Applies6DOFTagWhenEntryHas6DOF()
        {
            var re2 = new Game("Resident Evil 2") { TagIds = new List<System.Guid>() };
            _games.Items.Add(re2);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Resident Evil 2", Category = "REFramework", Is6DOF = true }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var taggedGame = _games.Items[0];
            // umbrella "VRMod" + category "VRMod REFramework" + "VRMod 6DOF" = 3 tags
            Assert.That(taggedGame.TagIds, Has.Count.EqualTo(3));
            Assert.That(_tags.Items.Any(t => t.Name == "VRMod 6DOF"), Is.True);
        }

        [Test]
        public void RunFullScanAsync_No6DOFTagWhenEntryHasNo6DOF()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", Is6DOF = null }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var taggedGame = _games.Items[0];
            // Only umbrella "VRMod" + category = 2 tags (no 6DOF tag created).
            Assert.That(taggedGame.TagIds, Has.Count.EqualTo(2));
            Assert.That(_tags.Items.Any(t => t.Name == "VRMod 6DOF"), Is.False);
        }

        [Test]
        public void RunFullScanAsync_AppliesQualityTagWhenEntryHasQuality()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", Quality = "Playable" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var taggedGame = _games.Items[0];
            // umbrella "VRMod" + category "VRMod UEVR" + "VRMod Playable" = 3 tags
            Assert.That(taggedGame.TagIds, Has.Count.EqualTo(3));
            Assert.That(_tags.Items.Any(t => t.Name == "VRMod Playable"), Is.True);
        }

        [Test]
        public void RunFullScanAsync_NoQualityTagWhenEntryHasNoQuality()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", Quality = null }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var taggedGame = _games.Items[0];
            // Only umbrella "VRMod" + category = 2 tags (no quality tag created).
            Assert.That(taggedGame.TagIds, Has.Count.EqualTo(2));
        }

        [Test]
        public void RunFullScanAsync_QualityTag_IsOrthogonalTo6DOF()
        {
            var re2 = new Game("Resident Evil 2") { TagIds = new List<System.Guid>() };
            _games.Items.Add(re2);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Resident Evil 2", Category = "REFramework", Is6DOF = true, Quality = "Playable" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var taggedGame = _games.Items[0];
            // umbrella + category + "VRMod 6DOF" + "VRMod Playable" = 4 tags
            Assert.That(taggedGame.TagIds, Has.Count.EqualTo(4));
            Assert.That(_tags.Items.Any(t => t.Name == "VRMod 6DOF"), Is.True);
            Assert.That(_tags.Items.Any(t => t.Name == "VRMod Playable"), Is.True);
        }

        [Test]
        public void RunFullScanAsync_MatchesByNameHyphen()
        {
            var hl2 = new Game("Half-Life 2") { TagIds = new List<System.Guid>() };
            _games.Items.Add(hl2);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Half-Life_2", Category = "Motion Gesture" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_games.Items[0].TagIds, Is.Not.Null);
            Assert.That(_games.Items[0].TagIds, Has.Count.EqualTo(2));
        }

        [Test]
        public void RunFullScanAsync_NoMatch_DoesNotAddTag()
        {
            var game = new Game("NonExistentGame") { TagIds = new List<System.Guid>() };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_games.Items[0].TagIds, Is.Not.Null.And.Empty);
        }

        [Test]
        public void RunFullScanAsync_ThresholdBelowDefault_MatchesLowerSimilarity()
        {
            // 8-character difference in a 16-character name => similarity exactly 0.5, below
            // the old 0.8f default. The user's FuzzyMatchThreshold must be honored end-to-end.
            var game = new Game("aaaaaaaa cccccccc") { TagIds = new List<System.Guid>() };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "aaaaaaaa bbbbbbbb", Category = "UEVR" }
            });
            _registry.Register(parser);

            // Control: the default threshold (0.8) does not match this pair.
            _service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(game.TagIds, Is.Empty);

            // Below the old default: the same pair matches and the category tag applies.
            _service.Settings.FuzzyMatchThreshold = 0.5f;
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var tag = _tags.Items.FirstOrDefault(t => t.Name == "VRMod UEVR");
            Assert.That(tag, Is.Not.Null);
            Assert.That(game.TagIds, Does.Contain(tag!.Id));
        }

        [Test]
        public void RunFullScanAsync_RemovesStaleTags()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var vrModTag = _tags.Add("VRMod Full Motion");
            skyrim.TagIds.Add(vrModTag.Id);

            var parser = new TestParser(Array.Empty<VrModEntry>());
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(skyrim.TagIds, Is.Not.Null.And.Empty);
            Assert.That(_tags.Items, Has.Count.EqualTo(1));
        }

        [Test]
        public void RunFullScanAsync_SourceFails_PreservesPreviouslyAppliedTags()
        {
            // Skyrim was tagged in a previous scan (the tag is persisted in the DB).
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            var vrModTag = _tags.Add("VRMod Full Motion");
            skyrim.TagIds.Add(vrModTag.Id);
            _games.Items.Add(skyrim);

            // The only source that could re-match Skyrim fails this scan (network down,
            // timeout, parse failure...).
            _registry.Register(new FailingParser());

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            // Data-loss guard: a failed source must not cause previously applied tags to be
            // removed — we cannot tell "no longer listed" from "listed by a source that failed".
            Assert.That(skyrim.TagIds, Is.Not.Null.And.EqualTo(new[] { vrModTag.Id }));
        }

        [Test]
        public void RunFullScanAsync_SourceFails_PreservesPreviouslyAppliedTagsAndLinks()
        {
            // Phase 1: source X matches Skyrim, the user accepted it, and the plugin applied
            // the tags and added the source link — a realistic "previously applied" state.
            var skyrim = new Game("Skyrim")
            {
                TagIds = new List<System.Guid>(),
                Links = new System.Collections.ObjectModel.ObservableCollection<Link>()
            };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "https://pcvrcentral.com/game/skyrim" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();
            _service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);
            _service.ReconcileSourceLinks(skyrim);

            // Sanity: the previously applied state (tags + the plugin source link) exists.
            Assert.That(skyrim.TagIds, Is.Not.Empty);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));

            // Phase 2: a fresh scan (cold cache → a fetch is attempted) in which the only
            // source fails (network down, timeout, parse failure...). A failed source must be
            // treated as "unchanged": its previously applied tags AND its source link must
            // both survive, because a failed scan skips stale-tag removal, tag reconciliation
            // and link reconciliation.
            var freshRegistry = new VrModSourceRegistry();
            freshRegistry.Register(new FailingParser());
            var failingService = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), freshRegistry);
            failingService.RunFullScanAsync().GetAwaiter().GetResult();

            // Data-loss guard: tags AND the plugin source link survive a failed source.
            Assert.That(skyrim.TagIds, Is.Not.Empty);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));
            Assert.That(skyrim.Links![0].Url, Is.EqualTo("https://pcvrcentral.com/game/skyrim"));
        }

        [Test]
        public void RemoveTagsForSource_XOnly_CleanWipesAllVrTags()
        {
            // Skyrim was tagged in an earlier scan by source X (category + 6DOF + umbrella).
            var cat = _tags.Add("VRMod UEVR");
            var sixDOF = _tags.Add("VRMod 6DOF");
            var umbrella = _tags.Add("VRMod");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(cat.Id);
            skyrim.TagIds.Add(sixDOF.Id);
            skyrim.TagIds.Add(umbrella.Id);
            _games.Items.Add(skyrim);

            var x = new TestParser(new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } });
            _registry.Register(x);
            x.IsEnabled = false;
            _service.Settings.SetDecision(x.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            var affected = _service.RemoveTagsForSource(x.SourceId);

            // X was the only source → every VRMod tag is wiped from the game.
            Assert.That(affected, Is.EqualTo(1));
            Assert.That(skyrim.TagIds, Is.Empty);
            // Decisions are intentionally NOT cleared, so re-enabling X re-applies tags next scan.
            Assert.That(_service.Settings.GetDecision(x.SourceId, "Skyrim", skyrim.Id),
                Is.EqualTo(MatchDecision.Accepted));
        }

        [Test]
        public void RemoveTagsForSource_XOnly_KeepsNonVrTags()
        {
            var cat = _tags.Add("VRMod UEVR");
            var nonVr = _tags.Add("Action");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(cat.Id);
            skyrim.TagIds.Add(nonVr.Id);
            _games.Items.Add(skyrim);

            var x = new TestParser(new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } });
            _registry.Register(x);
            x.IsEnabled = false;
            _service.Settings.SetDecision(x.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            _service.RemoveTagsForSource(x.SourceId);

            // Only the VR tag is removed; unrelated tags survive.
            Assert.That(skyrim.TagIds, Is.EqualTo(new[] { nonVr.Id }));
        }

        [Test]
        public void RemoveTagsForSource_Shared_RemovesOnlyOrphanedCategory()
        {
            var catX = _tags.Add("VRMod UEVR");         // X's category (becomes orphaned)
            var catY = _tags.Add("VRMod REFramework");  // Y's category (backed by Y)
            var sixDOF = _tags.Add("VRMod 6DOF");
            var umbrella = _tags.Add("VRMod");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(catX.Id);
            skyrim.TagIds.Add(catY.Id);
            skyrim.TagIds.Add(sixDOF.Id);
            skyrim.TagIds.Add(umbrella.Id);
            _games.Items.Add(skyrim);

            var x = new TestParser(new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } });
            _registry.Register(x);
            x.IsEnabled = false;
            var y = new TestParser(new[] { new VrModEntry { GameName = "Skyrim", Category = "REFramework" } });
            _registry.Register(y);
            y.IsEnabled = true;

            _service.Settings.SetDecision(x.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted, VrModCategories.TagNameFor("UEVR"));
            _service.Settings.SetDecision(y.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted, VrModCategories.TagNameFor("REFramework"));

            var affected = _service.RemoveTagsForSource(x.SourceId);

            // Only X's category (UEVR) is removed. Y's category, 6DOF, and the umbrella tag are kept
            // because other sources still match the game (facets/umbrella are not category-specific).
            Assert.That(affected, Is.EqualTo(1));
            Assert.That(skyrim.TagIds, Does.Not.Contain(catX.Id));
            Assert.That(skyrim.TagIds, Contains.Item(catY.Id));
            Assert.That(skyrim.TagIds, Contains.Item(sixDOF.Id));
            Assert.That(skyrim.TagIds, Contains.Item(umbrella.Id));
        }

        [Test]
        public void RemoveTagsForSource_SourceStillEnabled_NoOp()
        {
            var cat = _tags.Add("VRMod UEVR");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(cat.Id);
            _games.Items.Add(skyrim);

            var x = new TestParser(new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } });
            _registry.Register(x);
            x.IsEnabled = true; // still enabled → nothing to remove
            _service.Settings.SetDecision(x.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            var affected = _service.RemoveTagsForSource(x.SourceId);

            Assert.That(affected, Is.Zero);
            Assert.That(skyrim.TagIds, Is.EqualTo(new[] { cat.Id }));
        }

        [Test]
        public void RemoveTagsForSource_NoDecisions_NoOp()
        {
            var cat = _tags.Add("VRMod UEVR");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(cat.Id);
            _games.Items.Add(skyrim);

            var x = new TestParser(new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } });
            _registry.Register(x);
            x.IsEnabled = false;
            // No decision recorded for X → byGame is empty → no games touched.

            var affected = _service.RemoveTagsForSource(x.SourceId);

            Assert.That(affected, Is.Zero);
            Assert.That(skyrim.TagIds, Is.EqualTo(new[] { cat.Id }));
        }

        [Test]
        public void RemoveAllVrModTags_RemovesEveryVrModTagFromEveryGame()
        {
            var cat = _tags.Add("VRMod UEVR");
            var sixDOF = _tags.Add("VRMod 6DOF");
            var umbrella = _tags.Add("VRMod");
            var nonVr = _tags.Add("Action");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(cat.Id);
            skyrim.TagIds.Add(sixDOF.Id);
            skyrim.TagIds.Add(umbrella.Id);
            skyrim.TagIds.Add(nonVr.Id);
            var doom = new Game("Doom") { TagIds = new List<System.Guid>() };
            doom.TagIds.Add(cat.Id);
            doom.TagIds.Add(umbrella.Id);
            var plain = new Game("Plain") { TagIds = new List<System.Guid>() };
            plain.TagIds.Add(nonVr.Id);
            _games.Items.Add(skyrim);
            _games.Items.Add(doom);
            _games.Items.Add(plain);

            var affected = _service.RemoveAllVrModTags();

            // Only the two games that carried VRMod tags are affected.
            Assert.That(affected, Is.EqualTo(2));
            // Every VRMod tag is gone from every game; unrelated tags survive.
            Assert.That(skyrim.TagIds, Is.EqualTo(new[] { nonVr.Id }));
            Assert.That(doom.TagIds, Is.Empty);
            Assert.That(plain.TagIds, Is.EqualTo(new[] { nonVr.Id }));
        }

        [Test]
        public void RemoveAllVrModTags_ReportsPerGameProgressInOrder()
        {
            var cat = _tags.Add("VRMod UEVR");
            var nonVr = _tags.Add("Action");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(cat.Id);
            skyrim.TagIds.Add(nonVr.Id);
            var doom = new Game("Doom") { TagIds = new List<System.Guid>() };
            doom.TagIds.Add(cat.Id);
            var plain = new Game("Plain") { TagIds = new List<System.Guid>() };
            plain.TagIds.Add(nonVr.Id);
            _games.Items.Add(skyrim);
            _games.Items.Add(doom);
            _games.Items.Add(plain);

            var currents = new List<int>();
            var totals = new List<int>();
            var names = new List<string>();
            var affected = _service.RemoveAllVrModTags((current, total, name) =>
            {
                currents.Add(current);
                totals.Add(total);
                names.Add(name);
            });

            // The two games that carried VRMod tags are affected.
            Assert.That(affected, Is.EqualTo(2));
            // Progress is reported once per game, in order, with a running count and the total
            // (this is what drives the global progress bar's "45 / 182" text).
            Assert.That(currents, Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(totals, Is.EqualTo(new[] { 3, 3, 3 }));
            Assert.That(names, Is.EqualTo(new[] { "Skyrim", "Doom", "Plain" }));
            // The non-VRMod tag survives on the affected games.
            Assert.That(skyrim.TagIds, Is.EqualTo(new[] { nonVr.Id }));
            Assert.That(doom.TagIds, Is.Empty);
            Assert.That(plain.TagIds, Is.EqualTo(new[] { nonVr.Id }));
        }

        [Test]
        public void RemoveAllVrModTags_HonorsAnAlreadyCancelledToken()
        {
            var cat = _tags.Add("VRMod UEVR");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(cat.Id);
            _games.Items.Add(skyrim);

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                // Graceful cancellation (no exception): the loop stops at the next game and
                // persists whatever was already processed. Pre-cancelled → nothing is processed.
                var affected = _service.RemoveAllVrModTags((c, t, n) => { }, cts.Token);
                Assert.That(affected, Is.EqualTo(0));
            }

            // Cancellation happened before the first game was processed: nothing was removed.
            Assert.That(skyrim.TagIds, Is.EqualTo(new[] { cat.Id }));
        }

        [Test]
        public void RemoveAllVrModTags_NoVrModTagsExist_NoOp()
        {
            var nonVr = _tags.Add("Action");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(nonVr.Id);
            _games.Items.Add(skyrim);

            var affected = _service.RemoveAllVrModTags();

            Assert.That(affected, Is.Zero);
            Assert.That(skyrim.TagIds, Is.EqualTo(new[] { nonVr.Id }));
        }

        [Test]
        public void RemoveAllVrModTags_GamesWithoutTags_NoOp()
        {
            var plain = new Game("Plain") { TagIds = new List<System.Guid>() };
            _games.Items.Add(plain);

            var affected = _service.RemoveAllVrModTags();

            Assert.That(affected, Is.Zero);
        }

        [Test]
        public void RemoveAllVrModTags_ScopedToMatchedGames_TouchesOnlyThoseGames()
        {
            var cat = _tags.Add("VRMod UEVR");
            var umbrella = _tags.Add("VRMod");
            var nonVr = _tags.Add("Action");
            // A matched game that carries a VRMod tag (should be cleaned).
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(cat.Id);
            skyrim.TagIds.Add(umbrella.Id);
            // A matched game without a VRMod tag (a cheap no-op in the scoped pass).
            var plainMatched = new Game("PlainMatched") { TagIds = new List<System.Guid>() };
            plainMatched.TagIds.Add(nonVr.Id);
            // A game NOT in the scoped set that happens to carry a VRMod tag. In practice a game
            // carries a VRMod tag only if it was matched, so this is unreachable from the review
            // dialog; it documents that the scoped pass leaves out-of-scope games untouched.
            var stray = new Game("Stray") { TagIds = new List<System.Guid>() };
            stray.TagIds.Add(cat.Id);
            _games.Items.Add(skyrim);
            _games.Items.Add(plainMatched);
            _games.Items.Add(stray);

            // Scope the removal to the matched games (skyrim + plainMatched), not the stray.
            var affected = _service.RemoveAllVrModTags(new[] { skyrim, plainMatched });

            // Only the scoped game that carried a VRMod tag is affected.
            Assert.That(affected, Is.EqualTo(1));
            Assert.That(skyrim.TagIds, Is.Empty); // its VRMod tags are gone
            Assert.That(plainMatched.TagIds, Is.EqualTo(new[] { nonVr.Id })); // untouched
            Assert.That(stray.TagIds, Is.EqualTo(new[] { cat.Id })); // NOT touched (out of scope)
        }

        [Test]
        public void RunFullScanAsync_MatchedGameNoLonger6DOF_RemovesStale6DOFTag()
        {
            // Skyrim was 6DOF in a previous scan (the facet tag is persisted).
            var sixDOF = _tags.Add("VRMod 6DOF");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(sixDOF.Id);
            _games.Items.Add(skyrim);

            // The source no longer marks Skyrim as 6DOF.
            var x = new TestParser(new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR", Is6DOF = false } });
            _registry.Register(x);
            x.IsEnabled = true;
            _service.Settings.SetDecision(x.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            // The stale 6DOF facet is reconciled away; the category tag remains.
            Assert.That(skyrim.TagIds, Does.Not.Contain(sixDOF.Id));
            Assert.That(skyrim.TagIds, Does.Contain(_tags.Items.Single(t => t.Name == "VRMod UEVR").Id));
        }

        [Test]
        public void RunFullScanAsync_MatchedGameStill6DOF_Keeps6DOFTag()
        {
            var sixDOF = _tags.Add("VRMod 6DOF");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(sixDOF.Id);
            _games.Items.Add(skyrim);

            var x = new TestParser(new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR", Is6DOF = true } });
            _registry.Register(x);
            x.IsEnabled = true;
            _service.Settings.SetDecision(x.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            // The 6DOF facet is still listed by the source → kept.
            Assert.That(skyrim.TagIds, Contains.Item(sixDOF.Id));
        }

        [Test]
        public void RunFullScanAsync_MatchedGameCategoryChanged_RemovesOldCategoryTag()
        {
            // Skyrim was previously in the UEVR category.
            var oldCat = _tags.Add("VRMod UEVR");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(oldCat.Id);
            _games.Items.Add(skyrim);

            // The source now lists Skyrim under the REFramework category instead.
            var x = new TestParser(new[] { new VrModEntry { GameName = "Skyrim", Category = "REFramework" } });
            _registry.Register(x);
            x.IsEnabled = true;
            _service.Settings.SetDecision(x.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(skyrim.TagIds, Does.Not.Contain(oldCat.Id));
            Assert.That(skyrim.TagIds, Does.Contain(_tags.Items.Single(t => t.Name == "VRMod REFramework").Id));
        }

        [Test]
        public void RunFullScanAsync_SourceFails_MatchedGameFacetsPreserved()
        {
            var sixDOF = _tags.Add("VRMod 6DOF");
            var cat = _tags.Add("VRMod UEVR");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            skyrim.TagIds.Add(sixDOF.Id);
            skyrim.TagIds.Add(cat.Id);
            _games.Items.Add(skyrim);

            _registry.Register(new FailingParser());

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            // A failed source → no reconcile, no stale removal → everything preserved.
            Assert.That(skyrim.TagIds, Contains.Item(sixDOF.Id));
            Assert.That(skyrim.TagIds, Contains.Item(cat.Id));
        }

        [Test]
        public void RunFullScanAsync_OneSourceFails_OtherSourcesStillMatch()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            _registry.Register(new FailingParser());
            var okParser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(okParser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            // The healthy source still matches and applies (exact match auto-applies).
            Assert.That(skyrim.TagIds, Is.Not.Null.And.Not.Empty);
            Assert.That(_tags.Items, Has.Count.EqualTo(2));
            Assert.That(_tags.Items.Any(t => t.Name == "VRMod Full Motion"), Is.True);
        }

        [Test]
        public void RunFullScanAsync_PreservesNonVRModTags()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _tags.Add("Action");
            var actionTag = _tags.FirstOrDefault(t => t.Name == "Action");
            skyrim.TagIds.Add(actionTag!.Id);
            _games.Items.Add(skyrim);

            var parser = new TestParser(Array.Empty<VrModEntry>());
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(skyrim.TagIds, Contains.Item(actionTag.Id));
        }

        [Test]
        public void RunFullScanAsync_ReviewMode_PendingMatchKeepsExistingTag()
        {
            // Skyrim already has a VRMod tag from a prior scan.
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            var vrModTag = _tags.Add("VRMod Full Motion");
            skyrim.TagIds.Add(vrModTag.Id);
            _games.Items.Add(skyrim);

            // The source re-matches Skyrim. In review mode (reviewCallback != null)
            // with no recorded decision, this is a Pending match that goes to
            // pendingMatches, NOT currentMatchedGames.
            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            List<VrModReviewMatchEntry> seen = null!;
            _service.RunFullScanAsync(m => seen = m).GetAwaiter().GetResult();

            // The match was routed to the review dialog...
            Assert.That(seen, Is.Not.Null);
            Assert.That(seen, Has.Count.EqualTo(1));
            // ...and its existing VRMod tag must NOT have been stripped by
            // RemoveStaleTags (regression guard for the review-mode data-loss bug).
            Assert.That(skyrim.TagIds, Contains.Item(vrModTag.Id));
        }

        [Test]
        public void RunFullScanAsync_ReviewMode_SourceUrlFallsBackToWebsiteUrl()
        {
            // A source entry with no per-entry SourceUrl: the review row's SourceUrl must fall
            // back to the source's WebsiteUrl so the review dialog's "open mod page" button works.
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            List<VrModReviewMatchEntry> seen = null!;
            _service.RunFullScanAsync(m => seen = m).GetAwaiter().GetResult();

            Assert.That(seen, Is.Not.Null.And.Not.Empty);
            Assert.That(seen[0].SourceUrl, Is.EqualTo("https://example.com/"));
        }

        [Test]
        public void RunFullScanAsync_ReviewMode_SourceUrlFromEntryOverridesWebsiteUrl()
        {
            // A source entry WITH a per-entry SourceUrl (a PCVR Central per-game deep link) must
            // win over the source's WebsiteUrl on the review row.
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry
                {
                    GameName = "Skyrim",
                    Category = "Full Motion Controls",
                    SourceUrl = "https://pcvrcentral.com/games/skyrim"
                }
            });
            _registry.Register(parser);

            List<VrModReviewMatchEntry> seen = null!;
            _service.RunFullScanAsync(m => seen = m).GetAwaiter().GetResult();

            Assert.That(seen, Is.Not.Null.And.Not.Empty);
            Assert.That(seen[0].SourceUrl, Is.EqualTo("https://pcvrcentral.com/games/skyrim"));
        }

        [Test]
        public void RunFullScanAsync_ReviewMode_NoSavedDecision_HasNullPersistedFields()
        {
            // A match with no saved (JSON) decision: the row's PersistedDecision and SavedAtUtc
            // are null, so the review dialog shows a fresh state and an empty "Saved at" column.
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            List<VrModReviewMatchEntry> seen = null!;
            _service.RunFullScanAsync(m => seen = m).GetAwaiter().GetResult();

            Assert.That(seen, Is.Not.Null.And.Not.Empty);
            Assert.That(seen[0].PersistedDecision, Is.Null);
            Assert.That(seen[0].SavedAtUtc, Is.Null);
        }

        [Test]
        public void RunFullScanAsync_ReviewAll_SavedAcceptedMatch_HasPersistedDecisionAndSavedAtUtc()
        {
            // A mapping accepted in a prior session is saved to JSON. A "Scan & Review VR Mods" review
            // (reviewAll: true) re-runs the accepted match, and the row must carry the persisted
            // decision + save time so the review dialog shows "Saved" with a timestamp.
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);
            var sourceId = parser.SourceId;
            _service.Settings.SetDecision(sourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            List<VrModReviewMatchEntry> seen = null!;
            _service.RunFullScanAsync(m => seen = m, true).GetAwaiter().GetResult();

            Assert.That(seen, Is.Not.Null.And.Not.Empty);
            Assert.That(seen[0].Status, Is.EqualTo(MatchDecision.Accepted));
            Assert.That(seen[0].PersistedDecision, Is.EqualTo(MatchDecision.Accepted));
            Assert.That(seen[0].SavedAtUtc, Is.Not.Null);
        }

        [Test]
        public void RunFullScanAsync_ReviewAll_SavedRejectedMatch_HasPersistedDecision()
        {
            // A previously rejected mapping re-scanned in reviewAll mode: the row is Rejected and
            // carries the persisted Rejected decision + save time.
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);
            var sourceId = parser.SourceId;
            _service.Settings.SetDecision(sourceId, "Skyrim", skyrim.Id, MatchDecision.Rejected);

            List<VrModReviewMatchEntry> seen = null!;
            _service.RunFullScanAsync(m => seen = m, true).GetAwaiter().GetResult();

            Assert.That(seen, Is.Not.Null.And.Not.Empty);
            Assert.That(seen[0].Status, Is.EqualTo(MatchDecision.Rejected));
            Assert.That(seen[0].PersistedDecision, Is.EqualTo(MatchDecision.Rejected));
            Assert.That(seen[0].SavedAtUtc, Is.Not.Null);
        }

        [Test]
        public void ScanGameAsync_MatchesByName()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            _service.ScanGameAsync(skyrim).GetAwaiter().GetResult();

            Assert.That(skyrim.TagIds, Is.Not.Null);
            Assert.That(skyrim.TagIds, Has.Count.EqualTo(2));
        }

        [Test]
        public void ScanGameAsync_RemoveStaleTag()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            var tag = _tags.Add("VRMod Full Motion");
            skyrim.TagIds.Add(tag.Id);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Fallout 4", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            _service.ScanGameAsync(skyrim).GetAwaiter().GetResult();

            Assert.That(skyrim.TagIds, Is.Not.Null.And.Empty);
        }

        [Test]
        public void ScanGameAsync_SourceFails_PreservesPreviouslyAppliedTags()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            var vrModTag = _tags.Add("VRMod Full Motion");
            skyrim.TagIds.Add(vrModTag.Id);

            _registry.Register(new FailingParser());

            _service.ScanGameAsync(skyrim).GetAwaiter().GetResult();

            // Data-loss guard: single-game scan must not strip tags when a source failed.
            Assert.That(skyrim.TagIds, Is.Not.Null.And.EqualTo(new[] { vrModTag.Id }));
        }

        [Test]
        public void ScanGameAsync_SourceFails_PreservesPreviouslyAppliedTagsAndLinks()
        {
            // Phase 1: source X matches Skyrim, the user accepted it, and the plugin applied
            // the tags and added the source link.
            var skyrim = new Game("Skyrim")
            {
                TagIds = new List<System.Guid>(),
                Links = new System.Collections.ObjectModel.ObservableCollection<Link>()
            };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "https://pcvrcentral.com/game/skyrim" }
            });
            _registry.Register(parser);

            _service.ScanGameAsync(skyrim).GetAwaiter().GetResult();
            _service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);
            _service.ReconcileSourceLinks(skyrim);

            Assert.That(skyrim.TagIds, Is.Not.Empty);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));

            // Phase 2: a single-game scan (cold cache → a fetch is attempted) in which the
            // only source fails. The failed source must be treated as "unchanged": its
            // previously applied tags AND its source link must both survive.
            var freshRegistry = new VrModSourceRegistry();
            freshRegistry.Register(new FailingParser());
            var failingService = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), freshRegistry);
            failingService.ScanGameAsync(skyrim).GetAwaiter().GetResult();

            Assert.That(skyrim.TagIds, Is.Not.Empty);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));
            Assert.That(skyrim.Links![0].Url, Is.EqualTo("https://pcvrcentral.com/game/skyrim"));
        }

        [Test]
        public void ScanGameAsync_NoMatch_RemovesAllVRTags()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            var tag = _tags.Add("VRMod Full Motion");
            skyrim.TagIds.Add(tag.Id);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Minecraft", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            _service.ScanGameAsync(skyrim).GetAwaiter().GetResult();

            Assert.That(skyrim.TagIds, Is.Not.Null.And.Empty);
        }

        [Test]
        public void ScanGameAsync_PreservesNonVRModTags()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            var vrTag = _tags.Add("VRMod Full Motion");
            var actionTag = _tags.Add("Action");
            skyrim.TagIds.Add(vrTag.Id);
            skyrim.TagIds.Add(actionTag.Id);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            _service.ScanGameAsync(skyrim).GetAwaiter().GetResult();

            Assert.That(skyrim.TagIds, Contains.Item(vrTag.Id));
            Assert.That(skyrim.TagIds, Contains.Item(actionTag.Id));
        }

        [Test]
        public void ScanGameAsync_RejectedDecision_SkipsTag()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);
            var sourceId = parser.SourceId;

            _service.Settings.SetDecision(sourceId, "Skyrim", skyrim.Id, MatchDecision.Rejected);

            _service.ScanGameAsync(skyrim).GetAwaiter().GetResult();

            Assert.That(skyrim.TagIds, Is.Not.Null.And.Empty);
        }

        [Test]
        public void GetVrTagsForGame_ReturnsOnlyVRModTags()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            var vrModTag = _tags.Add("VRMod Full Motion");
            _tags.Add("Action");
            var actionTag = _tags.FirstOrDefault(t => t.Name == "Action")!;
            skyrim.TagIds.Add(vrModTag.Id);
            skyrim.TagIds.Add(actionTag.Id);
            _games.Items.Add(skyrim);

            var result = _service.GetVrTagsForGame(skyrim);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Name, Is.EqualTo("VRMod Full Motion"));
        }

        [Test]
        public void GetVrTagsForGame_EmptyTagIds_ReturnsEmpty()
        {
            var game = new Game("Game") { TagIds = new List<System.Guid>() };
            var result = _service.GetVrTagsForGame(game);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void GetVrTagsForGame_NullTagIds_ReturnsEmpty()
        {
            var game = new Game("Game");
            game.TagIds = null;
            var result = _service.GetVrTagsForGame(game);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void RunFullScanAsync_MatchesLevenshtein()
        {
            var doom3 = new Game("DOOM 3 BFG Editon") { TagIds = new List<System.Guid>() };
            _games.Items.Add(doom3);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "DOOM 3 BFG Edition", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_games.Items[0].TagIds, Is.Not.Null);
            Assert.That(_games.Items[0].TagIds, Has.Count.EqualTo(2));
        }

        [Test]
        public void RunFullScanAsync_CaseInsensitiveMatch()
        {
            var game = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "SKYRIM", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_games.Items[0].TagIds, Is.Not.Null);
            Assert.That(_games.Items[0].TagIds, Has.Count.EqualTo(2));
        }

        [Test]
        public void RunFullScanAsync_MultipleSources_AddMultipleTags()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser1 = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SteamAppId = "72850" }
            });
            var parser2 = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", SteamAppId = "72850" }
            });
            _registry.Register(parser1);
            _registry.Register(parser2);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_games.Items[0].TagIds, Has.Count.EqualTo(3));
        }

        [Test]
        public void RunFullScanAsync_NoSourcesEnabled_DoesNothing()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            parser.IsEnabled = false;
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_games.Items[0].TagIds, Is.Not.Null.And.Empty);
        }

        [Test]
        public void RunFullScanAsync_ExistingTag_ReusesTag()
        {
            var existingTag = _tags.Add("VRMod Full Motion");
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var game = _games.Items[0];
            Assert.That(game.TagIds, Has.Count.EqualTo(2));
            Assert.That(game.TagIds, Contains.Item(existingTag.Id));
            var vrModTag = _tags.FirstOrDefault(t => t.Name == "VRMod");
            Assert.That(vrModTag, Is.Not.Null);
            Assert.That(game.TagIds, Contains.Item(vrModTag.Id));
            Assert.That(_tags.Items, Has.Count.EqualTo(2));
        }

        [Test]
        public void ComputeTagsForGame_EmptyCache_ReturnsEmpty()
        {
            var skyrim = new Game("Skyrim");
            var result = _service.ComputeTagsForGame(skyrim);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ComputeTagsForGame_CachedEntries_ReturnsTags()
        {
            var skyrim = new Game("Skyrim");
            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var result = _service.ComputeTagsForGame(skyrim);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Name, Is.EqualTo("VRMod Full Motion"));
        }

        [Test]
        public void GetVrModEntriesForGame_NoEntries_ReturnsEmpty()
        {
            var skyrim = new Game("Skyrim");
            var result = _service.GetVrModEntriesForGame(skyrim, Array.Empty<VrModEntry>()).ToList();
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void GetVrModEntriesForGame_MatchesByExactName()
        {
            var skyrim = new Game("Skyrim");
            var entries = new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "Fallout 4", Category = "UEVR" }
            };
            var result = _service.GetVrModEntriesForGame(skyrim, entries).ToList();
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim"));
        }

        [Test]
        public void GetVrModEntriesForGame_MatchesExact()
        {
            var game = new Game("Skyrim Special Edition");
            var entries = new[]
            {
                new VrModEntry { GameName = "Skyrim Special Edition", Category = "Full Motion Controls" }
            };
            var result = _service.GetVrModEntriesForGame(game, entries).ToList();
            Assert.That(result, Has.Count.EqualTo(1));
        }

        [Test]
        public void AllCachedEntries_Empty_ReturnsEmpty()
        {
            var result = _service.AllCachedEntries.ToList();
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void AllCachedEntries_Populated_ReturnsAllEntries()
        {
            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var result = _service.AllCachedEntries.ToList();
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim"));
        }

        [Test]
        public void RefreshCache_ClearsCachedEntries()
        {
            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var before = _service.AllCachedEntries.ToList();
            Assert.That(before, Has.Count.EqualTo(1));

            _service.RefreshCache();

            var after = _service.AllCachedEntries.ToList();
            Assert.That(after, Is.Empty);
        }

        [Test]
        public void Dispose_ClearsCache()
        {
            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            _service.Dispose();

            var after = _service.AllCachedEntries.ToList();
            Assert.That(after, Is.Empty);
        }

        [Test]
        public void RunFullScanAsync_Flat2VRPort_CategoryTagApplied()
        {
            var game = new Game("Roboquest") { TagIds = new List<System.Guid>() };
            game.Links = new System.Collections.ObjectModel.ObservableCollection<Playnite.SDK.Models.Link>
            {
                new Playnite.SDK.Models.Link("Steam", "https://store.steampowered.com/app/1438560/")
            };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "ROBOQUEST VR", Category = "Flat2VR Port", SteamAppId = "1438560" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null);
            Assert.That(game.TagIds, Has.Count.EqualTo(2));
            var flat2Tag = _tags.Items.FirstOrDefault(t => t.Name == "VRMod Flat2VR Port");
            Assert.That(flat2Tag, Is.Not.Null);
            Assert.That(game.TagIds, Contains.Item(flat2Tag.Id));
            var vrModTag = _tags.Items.FirstOrDefault(t => t.Name == "VRMod");
            Assert.That(vrModTag, Is.Not.Null);
            Assert.That(game.TagIds, Contains.Item(vrModTag.Id));
        }

        [Test]
        public void RunFullScanAsync_All7CategoryTags_PresentInTagCollection()
        {
            var game = new Game("Multi VR Game") { TagIds = new List<System.Guid>() };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Multi VR Game", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "Multi VR Game", Category = "UEVR" },
                new VrModEntry { GameName = "Multi VR Game", Category = "Motion Gesture" },
                new VrModEntry { GameName = "Multi VR Game", Category = "Head Tracked" },
                new VrModEntry { GameName = "Multi VR Game", Category = "Dedicated" },
                new VrModEntry { GameName = "Multi VR Game", Category = "Dedicated" },
                new VrModEntry { GameName = "Multi VR Game", Category = "Flat2VR Port" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var tagNames = _tags.Items.Select(t => t.Name).ToList();
            Assert.That(tagNames, Does.Contain("VRMod Full Motion"));
            Assert.That(tagNames, Does.Contain("VRMod UEVR"));
            Assert.That(tagNames, Does.Contain("VRMod Motion Gesture"));
            Assert.That(tagNames, Does.Contain("VRMod Head Tracked"));
            Assert.That(tagNames, Does.Contain("VRMod Dedicated"));
            Assert.That(tagNames, Does.Contain("VRMod Flat2VR Port"));
            Assert.That(tagNames, Does.Contain("VRMod"));
        }

        [Test]
        public void RunFullScanAsync_UmbrellaTagAlwaysPresent_WhenCategoryTagsApplied()
        {
            var game = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "Skyrim", Category = "Head Tracked" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var vrModTag = _tags.Items.FirstOrDefault(t => t.Name == "VRMod");
            Assert.That(vrModTag, Is.Not.Null, "Umbrella VRMod tag should always exist when category tags are applied");
            Assert.That(game.TagIds, Contains.Item(vrModTag.Id), "Umbrella VRMod tag should be assigned to the game");
        }

        [Test]
        public void RunFullScanAsync_MultipleModsForOneGame_GameAccumulatesAllCategoryTags()
        {
            var game = new Game("Dual Mod Game") { TagIds = new List<System.Guid>() };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Dual Mod Game", Category = "Full Motion Controls", Is6DOF = true },
                new VrModEntry { GameName = "Dual Mod Game", Category = "Head Tracked" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var fmcTag = _tags.Items.FirstOrDefault(t => t.Name == "VRMod Full Motion");
            var htTag = _tags.Items.FirstOrDefault(t => t.Name == "VRMod Head Tracked");
            var dofTag = _tags.Items.FirstOrDefault(t => t.Name == "VRMod 6DOF");
            Assert.That(fmcTag, Is.Not.Null, "Category tag for the first mod should exist");
            Assert.That(htTag, Is.Not.Null, "Category tag for the second mod should exist");
            Assert.That(dofTag, Is.Not.Null, "6DOF facet tag should exist");
            Assert.That(game.TagIds, Contains.Item(fmcTag.Id), "First mod's category tag should be on the game");
            Assert.That(game.TagIds, Contains.Item(htTag.Id), "Second mod's category tag should also be on the game (union across mods)");
            Assert.That(game.TagIds, Contains.Item(dofTag.Id), "First mod's 6DOF facet should be on the game");
        }

        [Test]
        public void RunFullScanAsync_OnlyUmbrellaTag_NoCategoryTags()
        {
            var game = new Game("No VR Game") { TagIds = new List<System.Guid>() };
            _games.Items.Add(game);

            var parser = new TestParser(Array.Empty<VrModEntry>());
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_tags.Items, Is.Empty, "No tags should be created when no VR mod entries match");
        }

        [Test]
        public void RunFullScanAsync_SourceThrows_DoesNotCrash()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var failingParser = new FailingTestParser();
            _registry.Register(failingParser);

            var goodParser = new TestParser(Array.Empty<VrModEntry>());
            _registry.Register(goodParser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_games.Items[0].TagIds, Is.Not.Null);
            Assert.That(_games.Items[0].TagIds, Is.Empty);
        }

        [Test]
        public void FetchAndMatchEntries_CachedEntries_ReturnsMatches()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "Fallout 4", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            _service.FetchAndMatchEntries(skyrim, out var allEntries, out var matches);
            var matchList = matches.ToList();

            Assert.That(allEntries, Has.Count.EqualTo(2));
            Assert.That(matchList, Has.Count.EqualTo(1));
            Assert.That(matchList[0].GameName, Is.EqualTo("Skyrim"));
        }

        [Test]
        public void FetchAndMatchEntries_EmptyCache_ReturnsEmpty()
        {
            var skyrim = new Game("Skyrim");
            _service.FetchAndMatchEntries(skyrim, out var allEntries, out var matches);
            var matchList = matches.ToList();

            Assert.That(allEntries, Is.Empty);
            Assert.That(matchList, Is.Empty);
        }

        [Test]
        public void RunFullScanAsync_AllCategories_ProducesCorrectTagNames()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" },
                new VrModEntry { GameName = "Skyrim", Category = "Motion Gesture" },
                new VrModEntry { GameName = "Skyrim", Category = "Head Tracked" },
                new VrModEntry { GameName = "Skyrim", Category = "Dedicated" },
                new VrModEntry { GameName = "Skyrim", Category = "Dedicated" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(skyrim.TagIds, Has.Count.EqualTo(6));
            Assert.That(_tags.Items.Select(t => t.Name),
                Is.SupersetOf(new[] {
                    "VRMod Full Motion",
                    "VRMod UEVR",
                    "VRMod Motion Gesture",
                    "VRMod Head Tracked",
                    "VRMod Dedicated",
                    "VRMod"
                }));
        }

        [Test]
        public void HasSourceChanged_NoStoredChangeId_ReturnsTrue()
        {
            var parser = new TestParserWithChangeId(new[] { new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" } }, "abc123");
            _registry.Register(parser);

            var settings = new VrModTagSettings();
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);

            var hasChanged = service.HasSourceChanged(parser);
            Assert.That(hasChanged, Is.True);
        }

        [Test]
        public void HasSourceChanged_StoredChangeIdMatch_ReturnsFalse()
        {
            var parser = new TestParserWithChangeId(new[] { new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" } }, "abc123");
            _registry.Register(parser);

            var settings = new VrModTagSettings
            {
                SourceStatuses = new System.Collections.Generic.Dictionary<string, SourceStatus>
            {
                { parser.SourceId.ToLowerInvariant(), new SourceStatus { LastChangeId = "abc123" } }
            }
            };
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);

            var hasChanged = service.HasSourceChanged(parser);
            Assert.That(hasChanged, Is.False);
        }

        [Test]
        public void HasSourceChanged_StoredChangeIdMismatch_ReturnsTrue()
        {
            var parser = new TestParserWithChangeId(new[] { new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" } }, "abc123");
            _registry.Register(parser);

            var settings = new VrModTagSettings
            {
                SourceStatuses = new System.Collections.Generic.Dictionary<string, SourceStatus>
            {
                { parser.SourceId.ToLowerInvariant(), new SourceStatus { LastChangeId = "xyz789" } }
            }
            };
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);

            var hasChanged = service.HasSourceChanged(parser);
            Assert.That(hasChanged, Is.True);
        }

        [Test]
        public void UpdateSourceStatus_PopulatesStatusCorrectly()
        {
            var parser = new TestParserWithChangeId(new[] { new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" } }, "change1");
            _registry.Register(parser);

            var settings = new VrModTagSettings();
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);

            service.UpdateSourceStatus(parser, true);

            Assert.That(settings.SourceStatuses, Has.Count.EqualTo(1));
            var status = settings.SourceStatuses[parser.SourceId.ToLowerInvariant()];
            Assert.That(status.LastResult, Is.True);
            Assert.That(status.LastChangeId, Is.EqualTo("change1"));
            Assert.That(status.LastScanTime, Is.Not.EqualTo(default(DateTime)));
            Assert.That(status.DisplayName, Is.EqualTo("Test"));
        }

        [Test]
        public void UpdateSourceStatus_Error_StoresErrorMessage()
        {
            var parser = new TestParserWithChangeId(new[] { new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" } }, "change1");
            _registry.Register(parser);

            var settings = new VrModTagSettings();
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);

            service.UpdateSourceStatus(parser, false, "Connection timeout");

            var status = settings.SourceStatuses[parser.SourceId.ToLowerInvariant()];
            Assert.That(status.LastResult, Is.False);
            Assert.That(status.LastError, Is.EqualTo("Connection timeout"));
        }

        [Test]
        public void UpdateSourceStatus_RecordsEntryCount()
        {
            var parser = new TestParserWithChangeId(new[] { new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" } }, "change1");
            _registry.Register(parser);

            var settings = new VrModTagSettings();
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);

            service.UpdateSourceStatus(parser, true, null, 991);

            var status = settings.SourceStatuses[parser.SourceId.ToLowerInvariant()];
            Assert.That(status.LastResult, Is.True);
            Assert.That(status.LastEntryCount, Is.EqualTo(991));
        }

        [Test]
        public void RunFullScanAsync_ZeroEntrySource_RecordsZeroCount()
        {
            // A source that returns no entries is anomalous; it must be recorded with
            // LastEntryCount = 0 and LastResult = true so health surfaces can flag it.
            var parser = new TestParserWithChangeId(Array.Empty<VrModEntry>(), "empty1");
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var status = _service.Settings.SourceStatuses[parser.SourceId.ToLowerInvariant()];
            Assert.That(status.LastResult, Is.True);
            Assert.That(status.LastEntryCount, Is.EqualTo(0));
        }

        [Test]
        public void RunFullScanAsync_ChangedSource_UpdatesStatus()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParserWithChangeId(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            }, "change1");
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_service.Settings.SourceStatuses.ContainsKey(parser.SourceId.ToLowerInvariant()), Is.True);
            var status = _service.Settings.SourceStatuses[parser.SourceId.ToLowerInvariant()];
            Assert.That(status.LastResult, Is.True);
            Assert.That(status.LastChangeId, Is.EqualTo("change1"));
        }

        [Test]
        public void RunFullScanAsync_FuzzyMatch_PrefersHighestScoringGame()
        {
            // Both games share the entry's 3-char bucket and both clear the fuzzy
            // threshold against the entry; the higher-scoring one must win regardless of
            // dictionary iteration order (which is not stable across processes).
            //
            // entry:  "Resident Evil 7 Biohazard"  (normalized: resident evil 7 biohazard)
            // lower:  "Resident Evil VII Biohazard" -> "resident evil vii biohazard"
            //          identifier 7 == VII, Levenshtein 3 -> similarity ~0.89
            // higher: "Resident Evil 7 Bio hazard" -> "resident evil 7 bio hazard"
            //          one inserted space -> Levenshtein 1 -> similarity ~0.96
            var lower = new Game("Resident Evil VII Biohazard")
            {
                Id = new System.Guid("aaaaaaaa-0000-0000-0000-000000000001"),
                TagIds = new List<System.Guid>()
            };
            var higher = new Game("Resident Evil 7 Bio hazard")
            {
                Id = new System.Guid("aaaaaaaa-0000-0000-0000-000000000002"),
                TagIds = new List<System.Guid>()
            };
            _games.Items.Add(lower);
            _games.Items.Add(higher);

            var parser = new TestParserWithChangeId(new[]
            {
                new VrModEntry { GameName = "Resident Evil 7 Biohazard", Category = "Full Motion Controls" }
            }, "fuzzy-best");
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(higher.TagIds, Has.Count.EqualTo(2),
                "the higher-scoring game got the category tag + umbrella tag");
            Assert.That(lower.TagIds, Is.Null.Or.Empty,
                "the lower-scoring game must not be tagged");
        }

        [Test]
        public void RunFullScanAsync_FuzzyMatch_TieBreaksOnNormalizedName()
        {
            // Both candidates score identically against the entry; the lexicographically
            // smaller normalized name must win so the result is stable across launches.
            //
            // entry: "Dead Space Remake"  -> "dead space remake"
            //   A:  "Dead Spaces Remake"  -> "dead spaces remake"   (one extra space, d=1)
            //   B:  "Dead Spacex Remake"  -> "dead spacex remake"   (one extra 'x',   d=1)
            // Same length, same distance -> same score; 'e' < 'x' so A wins.
            var first = new Game("Dead Spaces Remake")
            {
                Id = new System.Guid("bbbbbbbb-0000-0000-0000-000000000001"),
                TagIds = new List<System.Guid>()
            };
            var second = new Game("Dead Spacex Remake")
            {
                Id = new System.Guid("bbbbbbbb-0000-0000-0000-000000000002"),
                TagIds = new List<System.Guid>()
            };
            _games.Items.Add(second);   // added first: the tie must not depend on order
            _games.Items.Add(first);

            var parser = new TestParserWithChangeId(new[]
            {
                new VrModEntry { GameName = "Dead Space Remake", Category = "UEVR" }
            }, "fuzzy-tie");
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(first.TagIds, Has.Count.EqualTo(2),
                "'dead spaces remake' sorts before 'dead spacex remake' and must win");
            Assert.That(second.TagIds, Is.Null.Or.Empty);
        }

        /// <summary>
        /// The service merges duplicate game entries for EVERY source (one entry per game,
        /// union of features): a source that lists the same game more than once (e.g. once
        /// per category) yields one entry carrying the union, so the matched game gets all
        /// of the source's tags for it, the found-entry count reflects the merged list, and
        /// the source's run log records the merge.
        /// </summary>
        [Test]
        public void RunFullScanAsync_MergesDuplicateGameEntries_UnionOfFeatures()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://example.com/skyrim-a" },
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "https://example.com/skyrim-b" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            // The merged entry keeps BOTH distinct deep links (first-seen order) — each
            // contributes its own Playnite link; dropping one would be data loss.
            var cached = _service.AllCachedEntries
                .Single(e => string.Equals(e.GameName, "Skyrim", System.StringComparison.OrdinalIgnoreCase));
            Assert.That(cached.SourceUrls,
                Is.EqualTo(new[] { "https://example.com/skyrim-a", "https://example.com/skyrim-b" }));

            var taggedGame = _games.Items[0];
            // Union of both occurrences: both category tags + the umbrella "VRMod" tag.
            Assert.That(taggedGame.TagIds, Has.Count.EqualTo(3));
            Assert.That(_tags.Items.Select(t => t.Name),
                Does.Contain("VRMod Full Motion"));
            Assert.That(_tags.Items.Select(t => t.Name),
                Does.Contain("VRMod UEVR"));
            Assert.That(_tags.Items.Select(t => t.Name), Does.Contain("VRMod"));

            var status = _service.Settings.SourceStatuses[parser.SourceId.ToLowerInvariant()];
            Assert.That(status.LastEntryCount, Is.EqualTo(1),
                "the found-entry count must reflect the merged (one entry per game) list");

            Assert.That(parser.RunLog, Is.Not.Null);
            Assert.That(parser.RunLog!.FirstOrDefault(l => l.StartsWith("Merged 1 duplicate game listing(s)")),
                Is.Not.Null, "the source run log must record the duplicate merge");
        }

        /// <summary>
        /// A source whose entries are already one per game: the service-level merge is a
        /// no-op and no merge line is logged.
        /// </summary>
        [Test]
        public void RunFullScanAsync_NoDuplicateEntries_NoMergeLogLine()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(parser.RunLog, Is.Not.Null);
            Assert.That(parser.RunLog!.Any(l => l.StartsWith("Merged")), Is.False,
                "no merge line for a source that already lists one entry per game");
        }

        private static int _testParserCounter;
        class FailingTestParser : BaseVrModParser
        {
            public FailingTestParser() : base("failing-test", "Failing Test", "https://example.com/") { }
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
                => throw new InvalidOperationException("Simulated fetch failure");
            public override string? GetCanonicalName(VrModEntry entry) => entry.GameName;
        }

        private class TestParser : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;
            public TestParser(IEnumerable<VrModEntry> entries) : base($"test-{Interlocked.Increment(ref _testParserCounter)}", "Test", "https://example.com/")
            {
                _entries = entries;
            }
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult(_entries);
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }

        /// <summary>
        /// A source that counts how many times its fetch is called — proves a batch scan fetches
        /// each source once (not once per game) and that a no-op library update fetches nothing.
        /// </summary>
        private class CountingParser : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;
            public int FetchCount;
            public CountingParser(IEnumerable<VrModEntry> entries)
                : base($"counting-{Interlocked.Increment(ref _testParserCounter)}", "Counting", "https://example.com/")
            {
                _entries = entries;
            }
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
            {
                Interlocked.Increment(ref FetchCount);
                return Task.FromResult(_entries);
            }
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }

        /// <summary>
        /// A source whose fetch parks on an externally-controlled <see cref="Task"/> (simulates a
        /// slow network fetch) for the duration — used to prove that a second scan started while
        /// one is in flight is skipped, not run concurrently (the single-scan gate).
        /// </summary>
        private class BlockingParser : BaseVrModParser
        {
            private readonly TaskCompletionSource<bool> _started = new();
            private readonly Task _block;
            private readonly IReadOnlyList<VrModEntry> _entries;
            private int _entered;

            public BlockingParser(IEnumerable<VrModEntry> entries, Task block)
                : base($"blocking-{Interlocked.Increment(ref _testParserCounter)}", "Blocking", "https://example.com/")
            {
                _entries = entries.ToList();
                _block = block;
            }

            /// <summary>Completes when the fetch has started (the caller's scan holds the gate by then).</summary>
            public Task Started => _started.Task;

            public override string? ChangeIdentifier => null;
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
            {
                if (Interlocked.Exchange(ref _entered, 1) == 0)
                    _started.TrySetResult(true);
                return _block.ContinueWith(t => (IEnumerable<VrModEntry>)_entries);
            }
        }

        private class TestParserWithChangeId : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;
            private readonly string _changeId;
            public TestParserWithChangeId(IEnumerable<VrModEntry> entries, string changeId) : base($"test-ci-{Interlocked.Increment(ref _testParserCounter)}", "Test", "https://example.com/")
            {
                _entries = entries;
                _changeId = changeId;
            }
            public override string? ChangeIdentifier => _changeId;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult(_entries);
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }

        /// <summary>
        /// A source whose fetch always fails (simulated network-down / timeout / parse failure).
        /// </summary>
        private class FailingParser : BaseVrModParser
        {
            public FailingParser() : base($"failing-{Interlocked.Increment(ref _testParserCounter)}", "Failing", "https://example.com/")
            {
            }
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromException<IEnumerable<VrModEntry>>(new InvalidOperationException("Simulated source failure."));
            public override string? GetCanonicalName(VrModEntry entry) => throw new System.NotImplementedException();
        }

        /// <summary>
        /// Regression (MEDIUM-2): a second scan started while one is in flight must be skipped,
        /// not run concurrently — the shared tag/index state and the non-concurrent SourceStatuses
        /// dictionary would otherwise race. The first scan holds the single-scan gate while its
        /// (slow) fetch parks; the second scan must return immediately and touch nothing.
        /// </summary>
        [Test]
        public void RunFullScanAsync_ConcurrentScan_IsSkippedWhileFirstIsRunning()
        {
            var game = new Game("Skyrim") { Id = System.Guid.NewGuid(), TagIds = new List<System.Guid>() };
            _games.Items.Add(game);

            var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var parser = new BlockingParser(
                new[] { new VrModEntry { GameName = "Skyrim", Category = "UEVR" } }, block.Task);
            _registry.Register(parser);

            // Scan 1: starts, acquires the gate, and parks in the (blocking) fetch.
            var first = _service.RunFullScanAsync();
            Assert.That(parser.Started.Wait(5000), Is.True, "scan 1 did not reach the fetch");
            Assert.That(first.IsCompleted, Is.False, "scan 1 must still be blocked in the fetch");

            // Scan 2: the gate is held, so this must be a no-op that completes immediately.
            var second = _service.RunFullScanAsync();
            Assert.That(second.IsCompleted, Is.True, "a concurrent scan must be skipped, not run");

            // Release scan 1's fetch; it then runs to completion and releases the gate.
            block.SetResult(true);
            Assert.That(first.Wait(10000), Is.True);
            Assert.That(first.IsFaulted, Is.False);

            // After the in-flight scan is gone, a fresh scan runs again (gate released).
            var third = _service.RunFullScanAsync();
            Assert.That(third.Wait(10000), Is.True);
        }

        // ==================== New-game batch scan (ScanGamesAsync / OnLibraryUpdated) ====================

        /// <summary>
        /// A batch scan tags EVERY new game in one gated scan (the OnLibraryUpdated path): each
        /// of the two new games gets its matched tags, matched against one shared source fetch.
        /// </summary>
        [Test]
        public void ScanGamesAsync_TwoNewGames_BothTagged()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            var doom = new Game("DOOM") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);
            _games.Items.Add(doom);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" },
                new VrModEntry { GameName = "DOOM", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            _service.ScanGamesAsync(new[] { skyrim, doom }).GetAwaiter().GetResult();

            Assert.That(skyrim.TagIds, Is.Not.Null.And.Not.Empty);
            Assert.That(doom.TagIds, Is.Not.Null.And.Not.Empty);
        }

        /// <summary>
        /// A batch scan fetches each source exactly ONCE for the whole batch (not once per game):
        /// the per-game core shares the fetched entries.
        /// </summary>
        [Test]
        public void ScanGamesAsync_BatchFetchesSourcesOnce()
        {
            var skyrim = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            var doom = new Game("DOOM") { TagIds = new List<System.Guid>() };
            _games.Items.Add(skyrim);
            _games.Items.Add(doom);

            var parser = new CountingParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" },
                new VrModEntry { GameName = "DOOM", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.ScanGamesAsync(new[] { skyrim, doom }).GetAwaiter().GetResult();

            Assert.That(parser.FetchCount, Is.EqualTo(1), "a batch scan fetches each source once, not per game");
        }

        /// <summary>
        /// OnLibraryUpdated path: a game added to the library after seeding is detected and
        /// scanned (tags applied) WITHOUT a full scan or a restart.
        /// </summary>
        [Test]
        public void NotifyLibraryUpdated_NewGameIsScanned()
        {
            var existing = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(existing);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" },
                new VrModEntry { GameName = "DOOM", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.SeedKnownGames();

            var newGame = new Game("DOOM") { TagIds = new List<System.Guid>() };
            _games.Items.Add(newGame);
            _service.NotifyLibraryUpdated();
            WaitUntil(() => newGame.TagIds != null && newGame.TagIds.Count > 0, 10000);

            Assert.That(newGame.TagIds, Is.Not.Null.And.Not.Empty);
        }

        /// <summary>
        /// OnLibraryUpdated with NO new games is a no-op: it triggers no scan (no source fetch),
        /// so a fresh library with only already-seeded games stays untouched.
        /// </summary>
        [Test]
        public void NotifyLibraryUpdated_NoNewGames_NoScan()
        {
            var existing = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(existing);

            var parser = new CountingParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" }
            });
            _registry.Register(parser);

            _service.SeedKnownGames();
            _service.NotifyLibraryUpdated();

            // Give a (nonexistent) drain a moment to run, then assert nothing was fetched.
            WaitMs(150);
            Assert.That(parser.FetchCount, Is.EqualTo(0), "no new games => no scan => no fetch");
        }

        /// <summary>
        /// The gate-skip must NOT lose new games: a new game detected while a scan holds the
        /// single-scan gate is kept pending and scanned (drained) as soon as that scan
        /// completes — so it still gets its tags without a restart or a manual full scan.
        /// </summary>
        [Test]
        public void NotifyLibraryUpdated_WhileScanInFlight_DrainedAfterScanCompletes()
        {
            var existing = new Game("Skyrim") { TagIds = new List<System.Guid>() };
            _games.Items.Add(existing);

            var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var parser = new BlockingParser(
                new[]
                {
                    new VrModEntry { GameName = "Skyrim", Category = "UEVR" },
                    new VrModEntry { GameName = "DOOM", Category = "UEVR" }
                }, block.Task);
            _registry.Register(parser);

            _service.SeedKnownGames();

            // Hold the gate: a full scan starts and parks in the (blocking) fetch.
            var scan = _service.RunFullScanAsync();
            Assert.That(parser.Started.Wait(5000), Is.True, "full scan did not reach the fetch");
            Assert.That(scan.IsCompleted, Is.False, "the full scan must still hold the gate");

            // A library update adds a NEW game while the scan holds the gate.
            var newGame = new Game("DOOM") { TagIds = new List<System.Guid>() };
            _games.Items.Add(newGame);
            _service.NotifyLibraryUpdated();

            // The drain is gate-skipped for now (the full scan still holds the gate).
            Assert.That(newGame.TagIds == null || newGame.TagIds.Count == 0,
                "the drain must wait while the full scan holds the gate");

            // Release the full scan's fetch; it completes and, on gate release, drains the new game.
            block.SetResult(true);
            Assert.That(scan.Wait(10000), Is.True);

            WaitUntil(() => newGame.TagIds != null && newGame.TagIds.Count > 0, 10000);
            Assert.That(newGame.TagIds, Is.Not.Null.And.Not.Empty,
                "a new game detected while a scan was in flight must still be scanned (drained)");
        }

        /// <summary>Waits a fixed number of milliseconds (test sync aid).</summary>
        private static void WaitMs(int ms) => System.Threading.Thread.Sleep(ms);

        /// <summary>Polls a condition until it holds or the timeout elapses (test sync aid).</summary>
        private static void WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                    throw new System.TimeoutException("WaitUntil timed out");
                System.Threading.Thread.Sleep(25);
            }
        }

        /// <summary>
        /// A full scan logs per-match detail at Debug and the scan summary at Info —
        /// the per-entry lines would otherwise swamp Playnite's log on every scan.
        /// </summary>
        [Test]
        public void RunFullScanAsync_PerMatchDetails_LogAtDebugNotInfo()
        {
            var game = new Game("Skyrim")
            {
                Id = new System.Guid("22222222-2222-2222-2222-222222222222"),
                TagIds = new List<System.Guid>()
            };
            game.Links = new System.Collections.ObjectModel.ObservableCollection<Link>
            {
                new Link("Steam", "https://store.steampowered.com/app/72850/")
            };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SteamAppId = "72850" }
            });
            _registry.Register(parser);

            var logger = new LevelRecorderLogger();
            _service = new VrModTagService(_mockApi, new VrModTagSettings(), logger, _registry);
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            // Per-match detail goes to Debug...
            Assert.That(logger.DebugMessages.Any(d => d.Contains("Auto-applied (pending, no review)")), Is.True);
            // ...and is NOT repeated at Info...
            Assert.That(logger.InfoMessages.Any(d => d.Contains("Auto-applied (pending, no review)")), Is.False);
            // ...while the scan summary stays at Info.
            Assert.That(logger.InfoMessages.Any(d => d.Contains("Full scan complete")), Is.True);
        }

        /// <summary>Minimal ILogger that records messages per level.</summary>
        private class LevelRecorderLogger : ILogger
        {
            public List<string> DebugMessages = new List<string>();
            public List<string> InfoMessages = new List<string>();
            public void Info(string message) => InfoMessages.Add(message);
            public void Info(Exception exception, string message) => InfoMessages.Add(message + " | " + exception);
            public void Debug(string message) => DebugMessages.Add(message);
            public void Debug(Exception exception, string message) => DebugMessages.Add(message + " | " + exception);
            public void Warn(string message) { }
            public void Warn(Exception exception, string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception exception, string message) { }
        }
    }
}
