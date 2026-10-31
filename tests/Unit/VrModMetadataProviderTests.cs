using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using NSubstitute;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    [TestFixture]
    public class VrModMetadataProviderTests
    {
        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private IPlayniteAPI _mockApi = null!;
        private VrModSourceRegistry _registry = null!;

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
        }

        [Test]
        public void AvailableFields_ReturnsTagsAndLinks()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var game = new Game("Test");
            var provider = new VrModMetadataProvider(service, game);

            Assert.That(provider.AvailableFields, Contains.Item(MetadataField.Tags));
            Assert.That(provider.AvailableFields, Contains.Item(MetadataField.Links));
        }

        [Test]
        public void GetTags_CachedEntries_ReturnsTags()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim") { TagIds = new List<Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();

            var provider = new VrModMetadataProvider(service, skyrim);
            var tags = provider.GetTags(null!).ToList();

            Assert.That(tags, Has.Count.EqualTo(1));
            Assert.That((tags[0] as MetadataNameProperty)?.Name, Is.EqualTo("VRMod Full Motion"));
        }

        [Test]
        public async Task GetTags_OnTheFly_SameQualityMultipleEntries_DeduplicatesQualityTag()
        {
            // Two sources return the same category + quality for the same game. The
            // on-the-fly path must dedupe every facet (category, 6DOF, quality) exactly
            // like ComputeTagsForGame does — before the fix the quality tag was added twice.
            var game = new Game("aaaaaaaa cccccccc") { TagIds = new List<Guid>() };
            _games.Items.Add(game);
            var parserA = new TestParser(new[]
            {
                new VrModEntry { GameName = "aaaaaaaa bbbbbbbb", Category = "UEVR", Quality = "Excellent" }
            });
            var parserB = new TestParser(new[]
            {
                new VrModEntry { GameName = "aaaaaaaa bbbbbbbb", Category = "UEVR", Quality = "Excellent" }
            });
            _registry.Register(parserA);
            _registry.Register(parserB);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            // The default threshold (0.8) does not match the 0.5-similarity pair, so the
            // scan applies nothing and the entry cache stays empty — but it populates the
            // source cache, which is what the on-the-fly path below reads.
            await service.RunFullScanAsync();

            service.Settings.FuzzyMatchThreshold = 0.5f;
            var provider = new VrModMetadataProvider(service, game);
            var tags = provider.GetTags(null!).ToList();

            Assert.That(tags.Select(t => (t as MetadataNameProperty)?.Name), Is.EqualTo(new[] { "VRMod UEVR", "VRMod Excellent" }));
        }

        [Test]
        public void GetTags_StatusFacet_SameStatusFromTwoEntries_EmittedOnce()
        {
            // The status facet must dedupe the way every other facet does, and the cached path must
            // agree with the on-the-fly path below - a divergence here means the metadata panel shows
            // a different tag set from a full scan for the same listing.
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim VR") { TagIds = new List<Guid>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim VR", Category = "UEVR", Statuses = { "Outdated" } },
                new VrModEntry { GameName = "Skyrim VR", Category = "UEVR", Statuses = { "outdated" } }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();

            var tags = new VrModMetadataProvider(service, skyrim).GetTags(null!).ToList()
                .Select(t => (t as MetadataNameProperty)?.Name)
                .ToList();

            Assert.That(tags, Does.Contain("VRMod Outdated"), "the status facet is offered");
            Assert.That(tags.Count(n => n == "VRMod Outdated"), Is.EqualTo(1), "once, not once per entry");
        }

        [Test]
        public void GetTags_StatusFacet_OffersNoTagForAStatusTheVocabularyDoesNotKnow()
        {
            // The provider's on-the-fly path re-checks the vocabulary itself. A status string that
            // never made it through a parser mapping - e.g. one written straight into a cache file by
            // hand - must not become a tag name here either.
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var game = new Game("aaaaaaaa bbbbbbbb") { TagIds = new List<Guid>() };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "aaaaaaaa bbbbbbbb", Category = "UEVR", Statuses = { "<b>Outdated</b>" } }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();

            var tags = new VrModMetadataProvider(service, game).GetTags(null!).ToList()
                .Select(t => (t as MetadataNameProperty)?.Name)
                .ToList();

            Assert.That(tags, Does.Contain("VRMod UEVR"));
            Assert.That(tags.Any(n => n != null && n.Contains("<b>")), Is.False, "no tag name from an unmapped status");
        }

        [Test]
        public void GetTags_MultipleCachedSources_ReturnsMultipleTags()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim") { TagIds = new List<Guid>() };
            _games.Items.Add(skyrim);

            var parser1 = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            var parser2 = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" }
            });
            _registry.Register(parser1);
            _registry.Register(parser2);

            service.RunFullScanAsync().GetAwaiter().GetResult();

            var provider = new VrModMetadataProvider(service, skyrim);
            var tags = provider.GetTags(null!).ToList();

            Assert.That(tags, Has.Count.EqualTo(2));
        }

        [Test]
        public void GetTags_EmptyCache_ReturnsEmpty()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim") { TagIds = new List<Guid>() };
            _games.Items.Add(skyrim);

            var provider = new VrModMetadataProvider(service, skyrim);
            var tags = provider.GetTags(null!).ToList();

            Assert.That(tags, Is.Empty);
        }

        /// <summary>
        /// GetLinks hands Link objects to Playnite, which stores them on the game and later opens
        /// them with the OS shell without validating the scheme. A hostile deep link must not be
        /// offered even for a saved accepted match.
        /// </summary>
        [Test]
        public void GetLinks_HostileDeepLink_OffersNoUnsafeLink()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "file:///C:/Windows/System32/calc.exe" },
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://evil.tld/a?x=" + new string('a', 3000) }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            var links = new VrModMetadataProvider(service, skyrim).GetLinks(null!).ToList();

            Assert.That(links.Any(l => !WebUrl.IsSafeLinkUrl(l.Url)), Is.False);
        }

        [Test]
        public void GetLinks_CachedEntries_SavedAccepted_ReturnsSourceLinks()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://example.com/a" },
                new VrModEntry { GameName = "Skyrim", Category = "UEVR", SourceUrl = "https://example.com/b" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);
            service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            var provider = new VrModMetadataProvider(service, skyrim);
            var links = provider.GetLinks(null!).ToList();

            Assert.That(links, Has.Count.EqualTo(2));
            Assert.That(links[0], Is.TypeOf<Link>());
        }

        [Test]
        public void GetLinks_DuplicateSourceUrl_Excluded()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            // The pre-existing link has the same URL as the entry's deep link but a name the
            // plugin never generates, so the scan's link reconciliation leaves it alone and the
            // dedup-by-URL check below still applies.
            var skyrim = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>
                {
                    new Link("Existing", "https://example.com/a")
                }
            };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://example.com/a" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            var provider = new VrModMetadataProvider(service, skyrim);
            var links = provider.GetLinks(null!).ToList();
            Assert.That(links, Is.Empty);
        }

        [Test]
        public void GetLinks_SteamAppId_Alone_CreatesNoLink()
        {
            // The plugin creates no Steam links: a Steam App ID by itself (an unsaved
            // match, no deep link) yields nothing.
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SteamAppId = "72850" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();

            var provider = new VrModMetadataProvider(service, skyrim);
            var links = provider.GetLinks(null!).ToList();

            Assert.That(links, Is.Empty);
        }

        [Test]
        public void GetLinks_EmptyCache_ReturnsEmpty()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(skyrim);

            var provider = new VrModMetadataProvider(service, skyrim);
            var links = provider.GetLinks(null!).ToList();

            Assert.That(links, Is.Empty);
        }

        [Test]
        public void GetLinks_MultipleGames_NoCrossContamination()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            var fallout = new Game("Fallout 4") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(skyrim);
            _games.Items.Add(fallout);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://example.com/skyrim" },
                new VrModEntry { GameName = "Fallout 4", Category = "UEVR", SourceUrl = "https://example.com/fallout" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);
            service.Settings.SetDecision(parser.SourceId, "Fallout 4", fallout.Id, MatchDecision.Accepted);

            var skyrimLinks = new VrModMetadataProvider(service, skyrim).GetLinks(null!).ToList();
            var falloutLinks = new VrModMetadataProvider(service, fallout).GetLinks(null!).ToList();

            Assert.That(skyrimLinks, Has.Count.EqualTo(1));
            Assert.That(falloutLinks, Has.Count.EqualTo(1));
            Assert.That(skyrimLinks[0].Url, Is.EqualTo("https://example.com/skyrim"));
            Assert.That(falloutLinks[0].Url, Is.EqualTo("https://example.com/fallout"));
        }

        [Test]
        public void GetLinks_NullLinksCollection_DoesNotThrow()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim") { TagIds = new List<Guid>(), Links = null };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://example.com/a" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            var provider = new VrModMetadataProvider(service, skyrim);
            var links = provider.GetLinks(null!).ToList();

            Assert.That(links, Has.Count.EqualTo(1));
        }

        [Test]
        public void GetTags_Flat2VRPort_ReturnsCorrectTag()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var game = new Game("Roboquest") { TagIds = new List<Guid>() };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "ROBOQUEST VR", Category = "Flat2VR Port", SteamAppId = "1438560" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();

            var provider = new VrModMetadataProvider(service, game);
            var tags = provider.GetTags(null!).ToList();

            Assert.That(tags, Has.Count.EqualTo(1));
            Assert.That((tags[0] as MetadataNameProperty)?.Name, Is.EqualTo("VRMod Flat2VR Port"));
        }

        [Test]
        public void GetTags_AllCategories_ReturnsAllTagNames()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var game = new Game("All VR Categories") { TagIds = new List<Guid>() };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "All VR Categories", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "All VR Categories", Category = "UEVR" },
                new VrModEntry { GameName = "All VR Categories", Category = "Motion Gesture" },
                new VrModEntry { GameName = "All VR Categories", Category = "Head Tracked" },
                new VrModEntry { GameName = "All VR Categories", Category = "Dedicated" },
                new VrModEntry { GameName = "All VR Categories", Category = "Dedicated" },
                new VrModEntry { GameName = "All VR Categories", Category = "Flat2VR Port" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();

            var provider = new VrModMetadataProvider(service, game);
            var tags = provider.GetTags(null!).ToList();

            Assert.That(tags, Has.Count.EqualTo(6));
            var tagNames = tags.Select(t => (t as MetadataNameProperty)?.Name).ToList();
            Assert.That(tagNames, Does.Contain("VRMod Full Motion"));
            Assert.That(tagNames, Does.Contain("VRMod UEVR"));
            Assert.That(tagNames, Does.Contain("VRMod Motion Gesture"));
            Assert.That(tagNames, Does.Contain("VRMod Head Tracked"));
            Assert.That(tagNames, Does.Contain("VRMod Dedicated"));
            Assert.That(tagNames, Does.Contain("VRMod Flat2VR Port"));
        }

        [Test]
        public void GetTags_MultipleCategories_CorrectTagCount()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var game = new Game("Multi Cat Game") { TagIds = new List<Guid>() };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Multi Cat Game", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "Multi Cat Game", Category = "UEVR" },
                new VrModEntry { GameName = "Multi Cat Game", Category = "Flat2VR Port" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();

            var provider = new VrModMetadataProvider(service, game);
            var tags = provider.GetTags(null!).ToList();

            Assert.That(tags, Has.Count.EqualTo(3));
        }

        [Test]
        public void GetLinks_SavedMatches_CorrectSourceUrls()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var game = new Game("Link Test")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Link Test", Category = "Full Motion Controls", SourceUrl = "https://example.com/1" },
                new VrModEntry { GameName = "Link Test", Category = "Head Tracked", SourceUrl = "https://example.com/2" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecision(parser.SourceId, "Link Test", game.Id, MatchDecision.Accepted);
            service.Settings.SetDecision(parser.SourceId, "Link Test", game.Id, MatchDecision.Accepted);

            var provider = new VrModMetadataProvider(service, game);
            var links = provider.GetLinks(null!).ToList();

            Assert.That(links, Has.Count.EqualTo(2));
            var urls = links.Select(l => l.Url).ToList();
            Assert.That(urls, Does.Contain("https://example.com/1"));
            Assert.That(urls, Does.Contain("https://example.com/2"));
        }

        [Test]
        public void GetLinks_SameSourceUrlMultipleCategories_Deduplicates()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var game = new Game("Dedup Game")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            _games.Items.Add(game);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Dedup Game", Category = "Full Motion Controls", SourceUrl = "https://example.com/dedup" },
                new VrModEntry { GameName = "Dedup Game", Category = "Head Tracked", SourceUrl = "https://example.com/dedup" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecision(parser.SourceId, "Dedup Game", game.Id, MatchDecision.Accepted);
            service.Settings.SetDecision(parser.SourceId, "Dedup Game", game.Id, MatchDecision.Accepted);

            var provider = new VrModMetadataProvider(service, game);
            var links = provider.GetLinks(null!).ToList();

            Assert.That(links, Has.Count.EqualTo(1));
            Assert.That(links[0].Url, Is.EqualTo("https://example.com/dedup"));
        }

        [Test]
        public void GetLinks_SavedAcceptedMatch_AddsSourceLink()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://pcvrcentral.com/game/skyrim" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();

            // Save an ACCEPTED decision for the match (this is what the review dialog does on Save).
            service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            var provider = new VrModMetadataProvider(service, skyrim);
            var links = provider.GetLinks(null!).ToList();

            Assert.That(links, Has.Count.EqualTo(1));
            Assert.That(links[0].Url, Is.EqualTo("https://pcvrcentral.com/game/skyrim"));
            Assert.That(links[0].Name, Is.EqualTo("Test"));
        }

        [Test]
        public void GetLinks_PendingMatch_NoSourceLink()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://pcvrcentral.com/game/skyrim" }
            });
            _registry.Register(parser);

            // A plain scan auto-applies tags but leaves the decision Pending (never saved).
            service.RunFullScanAsync().GetAwaiter().GetResult();

            var provider = new VrModMetadataProvider(service, skyrim);
            var links = provider.GetLinks(null!).ToList();

            // A pending (unsaved) match gets no source link.
            Assert.That(links, Is.Empty);
        }

        [Test]
        public void GetLinks_RejectedMatch_NoSourceLink()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls", SourceUrl = "https://pcvrcentral.com/game/skyrim" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();

            // A rejected (saved) match also gets no source link.
            service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Rejected);

            var provider = new VrModMetadataProvider(service, skyrim);
            var links = provider.GetLinks(null!).ToList();

            Assert.That(links, Is.Empty);
        }

        [Test]
        public void GetLinks_SavedMatchNoDeepLink_FallsBackToSourceWebsite()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            _games.Items.Add(skyrim);

            // Entry without a per-game deep link: the source link must fall back to the
            // source's own website (TestParser's WebsiteUrl).
            var parser = new TestParser(new[]
            {
                new VrModEntry { GameName = "Skyrim", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            var provider = new VrModMetadataProvider(service, skyrim);
            var links = provider.GetLinks(null!).ToList();

            Assert.That(links, Has.Count.EqualTo(1));
            Assert.That(links[0].Url, Is.EqualTo("https://example.com/"));
            Assert.That(links[0].Name, Is.EqualTo("Test"));
        }

        [Test]
        public void GetLinks_SourceLinksDisabled_NoSourceLink()
        {
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            var skyrim = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            _games.Items.Add(skyrim);

            var parser = new TestParser(new[]
            {
                new VrModEntry
                {
                    GameName = "Skyrim",
                    Category = "Full Motion Controls",
                    SourceUrl = "https://pcvrcentral.com/game/skyrim",
                    SteamAppId = "12345"
                }
            });
            _registry.Register(parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);

            // The user unchecked "Links" for this source in the settings UI.
            service.Settings.DisabledLinkSources.Add(parser.SourceId);

            var provider = new VrModMetadataProvider(service, skyrim);
            var links = provider.GetLinks(null!).ToList();

            // No source link — and the plugin creates no other links.
            Assert.That(links, Is.Empty);
        }

        private class TestParser : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;
            public TestParser(IEnumerable<VrModEntry> entries) : base($"test-{Guid.NewGuid()}", "Test", "https://example.com/")
            {
                _entries = entries;
            }
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult(_entries);
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }
    }
}
