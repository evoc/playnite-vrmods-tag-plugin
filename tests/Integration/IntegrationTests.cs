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

namespace PlayniteVRMods.Tests.Integration
{
    /// <summary>
    /// Integration tests simulating real-world VR mod scanning scenarios.
    /// Uses game names and Steam IDs from the playniteDB sample database
    /// and VR mod entries from real-world sources (RototRobot, Flat2VR).
    /// </summary>
    [TestFixture]
    public class IntegrationTests
    {
        private IPlayniteAPI _mockApi = null!;
        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
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

        #region Real-world game matching scenarios

        [Test]
        public void FullScan_Left4Dead2_MatchesBySteamId()
        {
            // Left 4 Dead 2 from playniteDB + RototRobot VR mod source
            var game = new Game("Left 4 Dead 2")
            {
                Id = Guid.Parse("A1B2C3D4-0001-0001-0001-000100010001"),
                TagIds = new List<Guid>()
            };
            game.Links = new ObservableCollection<Link>
            {
                new Link("Steam", "https://store.steampowered.com/app/550/")
            };
            _games.Items.Add(game);

            var parser = new TestParser("rototrobot-steam", new[]
            {
                new VrModEntry
                {
                    GameName = "Left 4 Dead 2",
                    Category = "Full Motion Controls",
                    SteamAppId = "550"
                }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            AssertTagsContain("VRMod");
            AssertTagsContain("VRMod Full Motion");
        }

        [Test]
        public void FullScan_Minecraft_MatchesByName()
        {
            // Minecraft from playniteDB + RototRobot (no Steam ID in source entry)
            var game = new Game("Minecraft")
            {
                Id = Guid.Parse("A1B2C3D4-0002-0002-0002-000200020002"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("rototrobot-name", new[]
            {
                new VrModEntry
                {
                    GameName = "Minecraft",
                    Category = "Full Motion Controls"
                }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        [Test]
        public void FullScan_StarWarsJediSurvivor_FuzzyMatch()
        {
            // "Star Wars Jedi Survivor" vs "Star Wars Jedi Survivor VR mod by Author"
            var game = new Game("Star Wars Jedi: Survivor")
            {
                Id = Guid.Parse("A1B2C3D4-0003-0003-0003-000300030003"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("fuzzy-test", new[]
            {
                new VrModEntry
                {
                    GameName = "Star Wars Jedi: Survivor",
                    Category = "Full Motion Controls"
                }
            });
            _registry.Register(parser);

            var settings = new VrModTagSettings();
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        [Test]
        public void FullScan_TromboneChamp_MultipleSources()
        {
            // Trombone Champ appears in both RototRobot (Full Motion Controls) and Flat2VR (Flat2VR Port)
            var game = new Game("Trombone Champ")
            {
                Id = Guid.Parse("A1B2C3D4-0004-0004-0004-000400040004"),
                TagIds = new List<Guid>()
            };
            game.Links = new ObservableCollection<Link>
            {
                new Link("Steam", "https://store.steampowered.com/app/1432720/")
            };
            _games.Items.Add(game);

            var rototParser = new TestParser("rotot-trombone", new[]
            {
                new VrModEntry
                {
                    GameName = "Trombone Champ",
                    Category = "Full Motion Controls",
                    SteamAppId = "1432720"
                }
            });
            _registry.Register(rototParser);

            var flat2Parser = new TestParser("flat2-trombone", new[]
            {
                new VrModEntry
                {
                    GameName = "Trombone Champ",
                    Category = "Flat2VR Port"
                }
            });
            _registry.Register(flat2Parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(3));
            AssertTagsContain("VRMod");
            AssertTagsContain("VRMod Full Motion");
            AssertTagsContain("VRMod Flat2VR Port");
        }

        [Test]
        public void FullScan_WrathAeonOfRuin_VRSuffixStripped()
        {
            // Flat2VR: "WRATH: AEON OF RUIN VR" should match "WRATH: AEON OF RUIN"
            var game = new Game("WRATH: AEON OF RUIN")
            {
                Id = Guid.Parse("A1B2C3D4-0005-0005-0005-000500050005"),
                TagIds = new List<Guid>()
            };
            game.Links = new ObservableCollection<Link>
            {
                new Link("Steam", "https://store.steampowered.com/app/1282750/")
            };
            _games.Items.Add(game);

            var parser = new TestParser("flat2-wrath", new[]
            {
                new VrModEntry
                {
                    GameName = "WRATH: AEON OF RUIN VR",
                    Category = "Flat2VR Port",
                    SteamAppId = "1282750"
                }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            AssertTagsContain("VRMod Flat2VR Port");
        }

        [Test]
        public void FullScan_ResidentEvil7_HeadTracked()
        {
            // Resident Evil 7 from RototRobot Head Tracked category
            var game = new Game("Resident Evil 7 Biohazard")
            {
                Id = Guid.Parse("A1B2C3D4-0006-0006-0006-000600060006"),
                TagIds = new List<Guid>()
            };
            game.Links = new ObservableCollection<Link>
            {
                new Link("Steam", "https://store.steampowered.com/app/418370/")
            };
            _games.Items.Add(game);

            var parser = new TestParser("rotot-re7", new[]
            {
                new VrModEntry
                {
                    GameName = "Resident Evil 7: Biohazard",
                    Category = "Head Tracked",
                    SteamAppId = "418370"
                }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            AssertTagsContain("VRMod Head Tracked");
        }

        [Test]
        public void FullScan_NoSteamId_FallsBackToNameMatch()
        {
            // Game with Steam ID but source entry without Steam ID should still match by name
            var game = new Game("Portal 2")
            {
                Id = Guid.Parse("A1B2C3D4-0007-0007-0007-000700070007"),
                TagIds = new List<Guid>()
            };
            game.Links = new ObservableCollection<Link>
            {
                new Link("Steam", "https://store.steampowered.com/app/620/")
            };
            _games.Items.Add(game);

            var parser = new TestParser("rotot-portal", new[]
            {
                new VrModEntry
                {
                    GameName = "Portal 2",
                    Category = "Full Motion Controls"
                    // No SteamAppId — should still match by name
                }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        [Test]
        public void FullScan_RoboQuest_VRSuffixStripped()
        {
            // Flat2VR: "ROBOQUEST VR" should match "Roboquest"
            var game = new Game("Roboquest")
            {
                Id = Guid.Parse("A1B2C3D4-0008-0008-0008-000800080008"),
                TagIds = new List<Guid>()
            };
            game.Links = new ObservableCollection<Link>
            {
                new Link("Steam", "https://store.steampowered.com/app/1438560/")
            };
            _games.Items.Add(game);

            var parser = new TestParser("flat2-robo", new[]
            {
                new VrModEntry
                {
                    GameName = "ROBOQUEST VR",
                    Category = "Flat2VR Port",
                    SteamAppId = "1438560"
                }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            AssertTagsContain("VRMod Flat2VR Port");
        }

        [Test]
        public void FullScan_StocksUnchanged_SkipsFetch()
        {
            // When source ChangeIdentifier hasn't changed, fetch should be skipped
            var game = new Game("Test Game")
            {
                Id = Guid.Parse("A1B2C3D4-0009-0009-0009-000900090009"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParserWithChangeId(
                new[] { new VrModEntry { GameName = "Test Game", Category = "Full Motion Controls" } },
                "change-v1");
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);

            // First scan — should fetch and tag
            service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));

            _registry = new VrModSourceRegistry();
            service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);

            // Reset parser to track fetch count
            var resetParser = new FetchCountingParser(
                new[] { new VrModEntry { GameName = "Test Game", Category = "Full Motion Controls" } },
                "change-v1");
            _registry.Register(resetParser);

            // Second scan — ChangeIdentifier unchanged, fetch should be skipped
            service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(resetParser.FetchCount, Is.EqualTo(1), "Source fetch should be skipped when ChangeIdentifier is unchanged");
        }

        #endregion

        #region Stale tag removal

        [Test]
        public void FullScan_SourceRemovedTaggedGame_RemovesStaleTags()
        {
            // Game previously tagged, but no longer matches any source
            var vrModTag = _tags.Add("VRMod");
            var vrModFullTag = _tags.Add("VRMod Full Motion");
            var game = new Game("Old VR Game")
            {
                Id = Guid.Parse("A1B2C3D4-0010-0010-0010-001000100010"),
                TagIds = new List<Guid> { vrModFullTag.Id, vrModTag.Id }
            };
            _games.Items.Add(game);

            // Source no longer includes this game
            var parser = new TestParser("empty-source", Array.Empty<VrModEntry>());
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.And.Empty, "All VRMod tags should be removed from unmached games");
        }

        [Test]
        public void FullScan_GameRemovedFromSource_RemovesVRTags()
        {
            // Game was tagged, source now returns different games
            var oldGame = new Game("Old Game")
            {
                Id = Guid.Parse("A1B2C3D4-0011-0011-0011-001100110011"),
                TagIds = new List<Guid> { _tags.Add("VRMod").Id }
            };
            _games.Items.Add(oldGame);

            var newGame = new Game("New Game")
            {
                Id = Guid.Parse("A1B2C3D4-0012-0012-0012-001200120012"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(newGame);

            var parser = new TestParser("source", new[]
            {
                new VrModEntry { GameName = "New Game", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(oldGame.TagIds, Is.Not.Null.And.Empty);
            Assert.That(newGame.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        #endregion

        #region Source enable/disable toggles

        [Test]
        public void FullScan_SourceDisabled_SkipsSource()
        {
            var game = new Game("Test Game")
            {
                Id = Guid.Parse("A1B2C3D4-0013-0013-0013-001300130013"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var activeParser = new TestParser("active", new[]
            {
                new VrModEntry { GameName = "Test Game", Category = "Full Motion Controls" }
            });
            activeParser.IsEnabled = true;

            var disabledParser = new TestParser("disabled", new[]
            {
                new VrModEntry { GameName = "Test Game", Category = "Flat2VR Port" }
            });
            disabledParser.IsEnabled = false;

            _registry.Register(activeParser);
            _registry.Register(disabledParser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            // Only active source tags applied
            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            AssertTagsContain("VRMod Full Motion");
            AssertTagsDoNotContain("VRMod Flat2VR Port");
        }

        [Test]
        public void FullScan_AllSourcesDisabled_NoTagsApplied()
        {
            var game = new Game("Test Game")
            {
                Id = Guid.Parse("A1B2C3D4-0014-0014-0014-001400140014"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("disabled", new[]
            {
                new VrModEntry { GameName = "Test Game", Category = "Full Motion Controls" }
            });
            parser.IsEnabled = false;
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.And.Empty);
        }

        [Test]
        public void FullScan_SourceReEnabled_CatchesUpTags()
        {
            var game = new Game("Test Game")
            {
                Id = Guid.Parse("A1B2C3D4-0015-0015-0015-001500150015"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser1 = new TestParser("source-a", new[]
            {
                new VrModEntry { GameName = "Test Game", Category = "Full Motion Controls" }
            });
            parser1.IsEnabled = false;

            var parser2 = new TestParser("source-b", new[]
            {
                new VrModEntry { GameName = "Test Game", Category = "Head Tracked" }
            });
            parser2.IsEnabled = true;

            _registry.Register(parser1);
            _registry.Register(parser2);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2)); // VRMod + Head Tracked

            // Re-enable the first source
            parser1.IsEnabled = true;

            var registry2 = new VrModSourceRegistry();
            registry2.Register(parser1);
            registry2.Register(parser2);

            var service2 = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), registry2);
            service2.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(3));
            AssertTagsContain("VRMod Full Motion");
        }

        #endregion

        #region Multiple games scan

        [Test]
        public void FullScan_MultipleGames_BatchTags()
        {
            var hl2 = new Game("Half-Life 2")
            {
                Id = Guid.Parse("A1B2C3D4-0020-0020-0020-002000200020"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(hl2);

            var zelda = new Game("The Legend of Zelda: Breath of the Wild")
            {
                Id = Guid.Parse("A1B2C3D4-0021-0021-0021-002100210021"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(zelda);

            var doom = new Game("DOOM 3")
            {
                Id = Guid.Parse("A1B2C3D4-0022-0022-0022-002200220022"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(doom);

            var parser = new TestParser("multi", new[]
            {
                new VrModEntry { GameName = "Half-Life 2", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "The Legend of Zelda: Breath of the Wild", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "DOOM 3", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(hl2.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            Assert.That(zelda.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            Assert.That(doom.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        [Test]
        public void FullScan_MixedMatchTypes_AllMatched()
        {
            // Test all three match types in one scan:
            // 1. Steam ID match
            // 2. Exact name match
            // 3. Levenshtein similarity match
            var steamGame = new Game("Game with Steam")
            {
                Id = Guid.Parse("A1B2C3D4-0030-0030-0030-003000300030"),
                TagIds = new List<Guid>()
            };
            steamGame.Links = new ObservableCollection<Link>
            {
                new Link("Steam", "https://store.steampowered.com/app/100/")
            };
            _games.Items.Add(steamGame);

            var nameGame = new Game("Game Name Match")
            {
                Id = Guid.Parse("A1B2C3D4-0031-0031-0031-003100310031"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(nameGame);

            var fuzzyGame = new Game("Cyberpunk 2077")
            {
                Id = Guid.Parse("A1B2C3D4-0032-0032-0032-003200320032"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(fuzzyGame);

            var parser = new TestParser("mixed", new[]
            {
                new VrModEntry { GameName = "Game with Steam", Category = "Full Motion Controls", SteamAppId = "100" },
                new VrModEntry { GameName = "Game Name Match", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "Cyberpunk 2077", Category = "Motion Gesture" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(steamGame.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            Assert.That(nameGame.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            Assert.That(fuzzyGame.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        #endregion

        #region Tag management

        [Test]
        public void FullScan_ExistingTag_ReusesTagObject()
        {
            var game = new Game("Test Game")
            {
                Id = Guid.Parse("A1B2C3D4-0040-0040-0040-004000400040"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            // Create the tag manually before scan
            var existingTag = _tags.Add("VRMod Full Motion");

            var parser = new TestParser("reusing", new[]
            {
                new VrModEntry { GameName = "Test Game", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            // Should use existing tag, not create duplicate
            Assert.That(_tags.Items, Has.Count.EqualTo(2));
            Assert.That(game.TagIds, Contains.Item(existingTag.Id));
            var vrModTag = _tags.Items.FirstOrDefault(t => t.Name == "VRMod");
            Assert.That(vrModTag, Is.Not.Null);
            Assert.That(game.TagIds, Contains.Item(vrModTag.Id));
        }

        [Test]
        public void FullScan_MultipleTagsPerGame_AllApplied()
        {
            var game = new Game("Multi VR Game")
            {
                Id = Guid.Parse("A1B2C3D4-0041-0041-0041-004100410041"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("multi-tags", new[]
            {
                new VrModEntry { GameName = "Multi VR Game", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "Multi VR Game", Category = "Head Tracked" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(3));
            AssertTagsContain("VRMod Full Motion");
            AssertTagsContain("VRMod Head Tracked");
        }

        #endregion

        #region Source link dedup

        [Test]
        public void GetLinks_MultipleSourcesSameGame_SavedAccepted_ReturnsOneLinkPerSource()
        {
            var game = new Game("Dedup Test")
            {
                Id = Guid.Parse("A1B2C3D4-0050-0050-0050-005000500050"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser1 = new TestParser("parser-steam1", new[]
            {
                new VrModEntry
                {
                    GameName = "Dedup Test",
                    Category = "Full Motion Controls",
                    SourceUrl = "https://source1.example.com/game"
                }
            });
            _registry.Register(parser1);

            var parser2 = new TestParser("parser-steam2", new[]
            {
                new VrModEntry
                {
                    GameName = "Dedup Test",
                    Category = "Head Tracked",
                    SourceUrl = "https://source2.example.com/game"
                }
            });
            _registry.Register(parser2);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecision(parser1.SourceId, "Dedup Test", game.Id, MatchDecision.Accepted);
            service.Settings.SetDecision(parser2.SourceId, "Dedup Test", game.Id, MatchDecision.Accepted);

            var provider = new VrModMetadataProvider(service, game);
            var links = provider.GetLinks(new GetMetadataFieldArgs()).ToList();

            // One source link per source that saved an accepted match
            Assert.That(links, Is.Not.Null);
            Assert.That(links, Has.Count.EqualTo(2));
            var urls = links.Select(l => l.Url).ToList();
            Assert.That(urls, Does.Contain("https://source1.example.com/game"));
            Assert.That(urls, Does.Contain("https://source2.example.com/game"));
        }

        [Test]
        public void GetLinks_SavedMatches_ExistingLinkSkipped()
        {
            var game = new Game("Multi Link Test")
            {
                Id = Guid.Parse("A1B2C3D4-0051-0051-0051-005100510051"),
                TagIds = new List<Guid>()
            };
            // The game already has a link to source 1's page; only source 2's link is new
            game.Links = new ObservableCollection<Link>
            {
                new Link("Source 1", "https://source1.example.com/game")
            };
            _games.Items.Add(game);

            var parser1 = new TestParser("parser-steam1", new[]
            {
                new VrModEntry
                {
                    GameName = "Multi Link Test",
                    Category = "Full Motion Controls",
                    SourceUrl = "https://source1.example.com/game"
                }
            });
            _registry.Register(parser1);

            var parser2 = new TestParser("parser-steam2", new[]
            {
                new VrModEntry
                {
                    GameName = "Multi Link Test",
                    Category = "Head Tracked",
                    SourceUrl = "https://source2.example.com/game"
                }
            });
            _registry.Register(parser2);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();
            service.Settings.SetDecision(parser1.SourceId, "Multi Link Test", game.Id, MatchDecision.Accepted);
            service.Settings.SetDecision(parser2.SourceId, "Multi Link Test", game.Id, MatchDecision.Accepted);

            var provider = new VrModMetadataProvider(service, game);
            var links = provider.GetLinks(new GetMetadataFieldArgs()).ToList();

            // source 1's URL already exists on the game; only source 2's URL should be added
            Assert.That(links, Is.Not.Null);
            Assert.That(links, Has.Count.EqualTo(1));
            Assert.That(links[0].Url, Is.EqualTo("https://source2.example.com/game"));
        }

        #endregion

        #region Cache behavior

        [Test]
        public void FullScan_SecondCall_UsesCache()
        {
            var game = new Game("Cached Game")
            {
                Id = Guid.Parse("A1B2C3D4-0060-0060-0060-006000600060"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new FetchCountingParser(new[]
            {
                new VrModEntry { GameName = "Cached Game", Category = "Full Motion Controls" }
            }, "change-v1");
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);

            // First scan — fetches from source
            service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(parser.FetchCount, Is.EqualTo(1));

            // Second scan — should use cache
            service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(parser.FetchCount, Is.EqualTo(1), "Source should not be re-fetched on second scan");
        }

        [Test]
        public void FullScan_CacheExpired_SourcesFetchAgain()
        {
            var game = new Game("Expire Game")
            {
                Id = Guid.Parse("A1B2C3D4-0061-0061-0061-006100610061"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new FetchCountingParser(new[]
            {
                new VrModEntry { GameName = "Expire Game", Category = "Full Motion Controls" }
            }, "change-v1");
            _registry.Register(parser);

            var settings = new VrModTagSettings { CacheDurationMinutes = 0 };
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);

            // First scan
            service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(parser.FetchCount, Is.EqualTo(1));

            // Second scan — cache expired (CacheDurationMinutes=0 means immediate expiry)
            service.RunFullScanAsync().GetAwaiter().GetResult();
            Assert.That(parser.FetchCount, Is.EqualTo(2), "Source should be re-fetched when cache expires");
        }

        #endregion

        #region Edge cases

        [Test]
        public void FullScan_EmptyGameName_SkipsEntry()
        {
            var game = new Game("Test Game")
            {
                Id = Guid.Parse("A1B2C3D4-0070-0070-0070-007000700070"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("empty-name", new[]
            {
                new VrModEntry { GameName = "", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.And.Empty);
        }

        [Test]
        public void FullScan_GamesWithNullLinks_DoesNotThrow()
        {
            var game = new Game("No Links Game")
            {
                Id = Guid.Parse("A1B2C3D4-0071-0071-0071-007100710071"),
                TagIds = new List<Guid>()
            };
            game.Links = null;
            _games.Items.Add(game);

            var parser = new TestParser("null-links", new[]
            {
                new VrModEntry { GameName = "No Links Game", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        [Test]
        public void FullScan_SpecialCharsInName_Matches()
        {
            var game = new Game("Half-Life: Alyx")
            {
                Id = Guid.Parse("A1B2C3D4-0072-0072-0072-007200720072"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("special-chars", new[]
            {
                new VrModEntry { GameName = "Half-Life: Alyx", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        [Test]
        public void FullScan_CaseInsensitive_Matches()
        {
            var game = new Game("MINECRAFT")
            {
                Id = Guid.Parse("A1B2C3D4-0073-0073-0073-007300730073"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("case-insensitive", new[]
            {
                new VrModEntry { GameName = "Minecraft", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        [Test]
        public void FullScan_HalfLifeAlyx_SpecialCharsMatch()
        {
            // Real game name from playniteDB: "Half-Life: Alyx"
            // Tests that colon and special characters are handled correctly
            var game = new Game("Half-Life: Alyx")
            {
                Id = Guid.Parse("A1B2C3D4-0072-0072-0072-007200720072"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("hlalyx", new[]
            {
                new VrModEntry { GameName = "Half-Life: Alyx", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            AssertTagsContain("VRMod Full Motion");
        }

        [Test]
        public void FullScan_Ghostrunner_MatchesExactName()
        {
            // Real game name from playniteDB: "Ghostrunner"
            var game = new Game("Ghostrunner")
            {
                Id = Guid.Parse("A1B2C3D4-0080-0080-0080-008000800080"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("ghostrunner", new[]
            {
                new VrModEntry { GameName = "Ghostrunner", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        [Test]
        public void FullScan_Doombblade_NoSubstringMatchWithDOOMEternal()
        {
            // Real game names from playniteDB: "DOOMBLADE" and "doom eternal"
            // DOOMBLADE should NOT match a source entry for "DOOM Eternal" via substring
            var game = new Game("DOOMBLADE")
            {
                Id = Guid.Parse("A1B2C3D4-0081-0081-0081-008100810081"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("doom-test", new[]
            {
                new VrModEntry { GameName = "DOOM Eternal", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.And.Empty,
                "DOOMBLADE should not be tagged with DOOM Eternal VR mod (no substring matching)");
        }

        [Test]
        public void FullScan_TombRaider_MatchesByName()
        {
            // Real game name from playniteDB: "Tomb Raider"
            var game = new Game("Tomb Raider")
            {
                Id = Guid.Parse("A1B2C3D4-0082-0082-0082-008200820082"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("tombraider", new[]
            {
                new VrModEntry { GameName = "Tomb Raider", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        [Test]
        public void FullScan_Ghostrunner2_NoSubstringMatchWithGhostrunner()
        {
            // Real game name from playniteDB: "Ghostrunner 2"
            // Should NOT match source entry for "Ghostrunner" via substring
            var game = new Game("Ghostrunner 2")
            {
                Id = Guid.Parse("A1B2C3D4-0083-0083-0083-008300830083"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("ghostrunner", new[]
            {
                new VrModEntry { GameName = "Ghostrunner", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.And.Empty,
                "Ghostrunner 2 should not be tagged with Ghostrunner VR mod (sequel differentiation)");
        }

        [Test]
        public void FullScan_ResidentEvil3_NoSubstringMatchWithRE7()
        {
            // Real game names from playniteDB: "Resident Evil 3" and "Resident Evil 7 Biohazard"
            // RE3 should NOT match RE7 via substring matching
            var game = new Game("Resident Evil 3")
            {
                Id = Guid.Parse("A1B2C3D4-0084-0084-0084-008400840084"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("re7", new[]
            {
                new VrModEntry { GameName = "Resident Evil 7 Biohazard", Category = "Head Tracked" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.And.Empty,
                "Resident Evil 3 should not be tagged with RE7 VR mod (sequel differentiation)");
        }

        [Test]
        public void FullScan_DeepRockGalactic_NotInSource_NoTagsApplied()
        {
            // Real game name from playniteDB: "Deep Rock Galactic"
            // This game is NOT in any VR mod source — should get no tags
            var game = new Game("Deep Rock Galactic")
            {
                Id = Guid.Parse("A1B2C3D4-0085-0085-0085-008500850085"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("empty", Array.Empty<VrModEntry>());
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.And.Empty,
                "Deep Rock Galactic should not be tagged (not in any VR source)");
        }

        [Test]
        public void FullScan_ChessUltra_NotInSource_NoTagsApplied()
        {
            // Real game name from playniteDB: "Chess Ultra"
            // This game is NOT in any VR mod source — should get no tags
            var game = new Game("Chess Ultra")
            {
                Id = Guid.Parse("A1B2C3D4-0086-0086-0086-008600860086"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("empty", Array.Empty<VrModEntry>());
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.And.Empty,
                "Chess Ultra should not be tagged (not in any VR source)");
        }

        [Test]
        public void FullScan_Grid_NotInSource_NoTagsApplied()
        {
            // Real game name from playniteDB: "GRID"
            // This game is NOT in any VR mod source — should get no tags
            var game = new Game("GRID")
            {
                Id = Guid.Parse("A1B2C3D4-0087-0087-0087-008700870087"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("empty", Array.Empty<VrModEntry>());
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.And.Empty,
                "GRID should not be tagged (not in any VR source)");
        }

        #endregion

        #region Real-world name matching from playniteDB

        [Test]
        public void FullScan_HalfLife2VRMod_DoesNotMatchHalfLife2()
        {
            // Real game names from playniteDB: "Half-Life 2: VR Mod" and "Half-Life 2"
            // "Half-Life 2: VR Mod" should NOT match source entry for "Half-Life 2" (substring policy)
            var vrModGame = new Game("Half-Life 2: VR Mod")
            {
                Id = Guid.Parse("A1B2C3D4-0090-0090-0090-009000900090"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(vrModGame);

            var hl2Game = new Game("Half-Life 2")
            {
                Id = Guid.Parse("A1B2C3D4-0091-0091-0091-009100910091"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(hl2Game);

            // Source entry for Half-Life 2 — should match HL2 but NOT HL2:VR Mod
            var parser = new TestParser("hl2", new[]
            {
                new VrModEntry { GameName = "Half-Life 2", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(hl2Game.TagIds, Is.Not.Null.With.Count.EqualTo(2),
                "Half-Life 2 should be tagged");
            Assert.That(vrModGame.TagIds, Is.Not.Null.And.Empty,
                "Half-Life 2: VR Mod should NOT be tagged with Half-Life 2 VR mod (no substring matching)");
        }

        [Test]
        public void FullScan_TheEvilWithin2_NoSubstringMatchWithEvilWithin()
        {
            // Real game names from playniteDB: "The Evil Within 2" and "The Evil Within"
            // TEW2 should NOT match TEW via substring
            var tew2Game = new Game("The Evil Within 2")
            {
                Id = Guid.Parse("A1B2C3D4-0092-0092-0092-009200920092"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(tew2Game);

            var tewGame = new Game("The Evil Within")
            {
                Id = Guid.Parse("A1B2C3D4-0093-0093-0093-009300930093"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(tewGame);

            var parser = new TestParser("tew", new[]
            {
                new VrModEntry { GameName = "The Evil Within", Category = "Head Tracked" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(tewGame.TagIds, Is.Not.Null.With.Count.EqualTo(2),
                "The Evil Within should be tagged");
            Assert.That(tew2Game.TagIds, Is.Not.Null.And.Empty,
                "The Evil Within 2 should NOT be tagged with TEW VR mod (sequel differentiation)");
        }

        [Test]
        public void FullScan_HellbladeVR_Edition_MatchesByName()
        {
            // Real game name from playniteDB: "Hellblade: Senua's Sacrifice VR Edition"
            var game = new Game("Hellblade: Senua's Sacrifice VR Edition")
            {
                Id = Guid.Parse("A1B2C3D4-0094-0094-0094-009400940094"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("hellblade", new[]
            {
                new VrModEntry { GameName = "Hellblade: Senua's Sacrifice VR Edition", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        [Test]
        public void FullScan_SteamIdMatch_DifferentGamesSameSubstring()
        {
            // Tests that Steam ID matching works even when names share substrings
            // Real game names from playniteDB
            var re3 = new Game("Resident Evil 3")
            {
                Id = Guid.Parse("A1B2C3D4-0095-0095-0095-009500950095"),
                TagIds = new List<Guid>()
            };
            re3.Links = new ObservableCollection<Link>
            {
                new Link("Steam", "https://store.steampowered.com/app/952060/")
            };
            _games.Items.Add(re3);

            var re7 = new Game("Resident Evil 7 Biohazard")
            {
                Id = Guid.Parse("A1B2C3D4-0096-0096-0096-009600960096"),
                TagIds = new List<Guid>()
            };
            re7.Links = new ObservableCollection<Link>
            {
                new Link("Steam", "https://store.steampowered.com/app/418370/")
            };
            _games.Items.Add(re7);

            var parser = new TestParser("residentevil", new[]
            {
                new VrModEntry { GameName = "Resident Evil 3", Category = "Full Motion Controls", SteamAppId = "952060" },
                new VrModEntry { GameName = "Resident Evil 7 Biohazard", Category = "Head Tracked", SteamAppId = "418370" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(re3.TagIds, Is.Not.Null.With.Count.EqualTo(2), "RE3 should get Full Motion Controls");
            Assert.That(re7.TagIds, Is.Not.Null.With.Count.EqualTo(2), "RE7 should get Head Tracked");
        }

        [Test]
        public void FullScan_WolfensteinOldBlood_Youngblood_MixedMatching()
        {
            // Real game names from playniteDB: "Wolfenstein: The Old Blood" and "Wolfenstein: Youngblood"
            // Each should only match its own source entry
            var oldBlood = new Game("Wolfenstein: The Old Blood")
            {
                Id = Guid.Parse("A1B2C3D4-0097-0097-0097-009700970097"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(oldBlood);

            var youngBlood = new Game("Wolfenstein: Youngblood")
            {
                Id = Guid.Parse("A1B2C3D4-0098-0098-0098-009800980098"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(youngBlood);

            var parser = new TestParser("wolfenstein", new[]
            {
                new VrModEntry { GameName = "Wolfenstein: The Old Blood", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "Wolfenstein: Youngblood", Category = "Head Tracked" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(oldBlood.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            Assert.That(youngBlood.TagIds, Is.Not.Null.With.Count.EqualTo(2));
        }

        [Test]
        public void FullScan_MultipleGamesFromPlayniteDB_MixedResults()
        {
            // Mix of games: some that should match, some that shouldn't
            // Real game names from playniteDB
            var shouldMatch1 = new Game("Ghostrunner")
            {
                Id = Guid.Parse("A1B2C3D4-0100-0100-0100-010001000100"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(shouldMatch1);

            var shouldMatch2 = new Game("Dead Island 2")
            {
                Id = Guid.Parse("A1B2C3D4-0101-0101-0101-010101010101"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(shouldMatch2);

            var shouldNotMatch1 = new Game("Chess Ultra")
            {
                Id = Guid.Parse("A1B2C3D4-0102-0102-0102-010201020102"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(shouldNotMatch1);

            var shouldNotMatch2 = new Game("GRID")
            {
                Id = Guid.Parse("A1B2C3D4-0103-0103-0103-010301030103"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(shouldNotMatch2);

            var parser = new TestParser("mixed-real", new[]
            {
                new VrModEntry { GameName = "Ghostrunner", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "Dead Island 2", Category = "Flat2VR Port" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(shouldMatch1.TagIds, Is.Not.Null.With.Count.EqualTo(2), "Ghostrunner should be tagged");
            Assert.That(shouldMatch2.TagIds, Is.Not.Null.With.Count.EqualTo(2), "Dead Island 2 should be tagged");
            Assert.That(shouldNotMatch1.TagIds, Is.Not.Null.And.Empty, "Chess Ultra should NOT be tagged");
            Assert.That(shouldNotMatch2.TagIds, Is.Not.Null.And.Empty, "GRID should NOT be tagged");
        }

        #endregion

        #region Single game scan (ScanGameAsync)

        [Test]
        public void ScanGameAsync_SingleGame_TagApplied()
        {
            // Simulates OnGameInstalled hook — single game scan, not full library scan
            var game = new Game("Ghostrunner")
            {
                Id = Guid.Parse("A1B2C3D4-0110-0110-0110-011001100110"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("scan-single", new[]
            {
                new VrModEntry { GameName = "Ghostrunner", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.ScanGameAsync(game).GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            AssertTagsContain("VRMod");
            AssertTagsContain("VRMod Full Motion");
        }

        [Test]
        public void ScanGameAsync_MultipleCategories_TagApplied()
        {
            // Single game with multiple VR categories
            var game = new Game("Multi VR Game")
            {
                Id = Guid.Parse("A1B2C3D4-0111-0111-0111-011101110111"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("scan-multi-cat", new[]
            {
                new VrModEntry { GameName = "Multi VR Game", Category = "Full Motion Controls" },
                new VrModEntry { GameName = "Multi VR Game", Category = "Flat2VR Port" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.ScanGameAsync(game).GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(3));
            AssertTagsContain("VRMod");
            AssertTagsContain("VRMod Full Motion");
            AssertTagsContain("VRMod Flat2VR Port");
        }

        [Test]
        public void ScanGameAsync_NoMatch_RemovesExistingVRTags()
        {
            // Game previously had VR tags but no longer matches
            var vrModTag = _tags.Add("VRMod");
            var vrModFlatTag = _tags.Add("VRMod Flat2VR Port");
            var game = new Game("Old Game")
            {
                Id = Guid.Parse("A1B2C3D4-0112-0112-0112-011201120112"),
                TagIds = new List<Guid> { vrModTag.Id, vrModFlatTag.Id }
            };
            _games.Items.Add(game);

            var parser = new TestParser("scan-no-match", new[]
            {
                new VrModEntry { GameName = "Different Game", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.ScanGameAsync(game).GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.And.Empty);
        }

        [Test]
        public void ScanGameAsync_UmbrellaTagAndCategoryTagsApplied()
        {
            // Single game scan should produce both umbrella VRMod + category tag
            var game = new Game("Scan Umbrella Test")
            {
                Id = Guid.Parse("A1B2C3D4-0113-0113-0113-011301130113"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("scan-umbrella", new[]
            {
                new VrModEntry { GameName = "Scan Umbrella Test", Category = "Full Motion Controls" }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.ScanGameAsync(game).GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            Assert.That(_tags.Items.Select(t => t.Name), Does.Contain("VRMod"));
            Assert.That(_tags.Items.Select(t => t.Name), Does.Contain("VRMod Full Motion"));
        }

        [Test]
        public void ScanGameAsync_NameMatch()
        {
            // Single game scan with Steam ID matching
            var game = new Game("Left 4 Dead 2")
            {
                Id = Guid.Parse("A1B2C3D4-0114-0114-0114-011401140114"),
                TagIds = new List<Guid>()
            };
            game.Links = new ObservableCollection<Link>
            {
                new Link("Steam", "https://store.steampowered.com/app/550/")
            };
            _games.Items.Add(game);

            var parser = new TestParser("scan-steam", new[]
            {
                new VrModEntry
                {
                    GameName = "Left 4 Dead 2",
                    Category = "Full Motion Controls",
                    SteamAppId = "550"
                }
            });
            _registry.Register(parser);

            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.ScanGameAsync(game).GetAwaiter().GetResult();

            Assert.That(game.TagIds, Is.Not.Null.With.Count.EqualTo(2));
            AssertTagsContain("VRMod");
            AssertTagsContain("VRMod Full Motion");
        }

        #endregion

        private void AssertTagsContain(string tagName)
        {
            var tag = _tags.Items.FirstOrDefault(t => t.Name == tagName);
            Assert.That(tag, Is.Not.Null, $"Expected tag '{tagName}' not found");
            Assert.That(_games.Items.SelectMany(g => g.TagIds ?? Enumerable.Empty<Guid>()),
                Contains.Item(tag.Id), $"Tag '{tagName}' not assigned to any game");
        }

        private void AssertTagsDoNotContain(string tagName)
        {
            var tag = _tags.Items.FirstOrDefault(t => t.Name == tagName);
            if (tag == null) return;
            Assert.That(_games.Items.SelectMany(g => g.TagIds ?? Enumerable.Empty<Guid>()),
                Does.Not.Contain(tag.Id), $"Tag '{tagName}' should not be assigned to any game");
        }

        [Test]
        public void FullScan_ReviewAll_IncludesDecidedMatches()
        {
            // A rejected decision: normally skipped and not reported, but reviewAll includes
            // it so the review dialog lists every matched game, decided or not.
            var game = new Game("Test Game")
            {
                Id = Guid.Parse("A1B2C3D4-0009-0009-0009-000900090009"),
                TagIds = new List<Guid>()
            };
            _games.Items.Add(game);

            var parser = new TestParser("rototrobot-reviewall", new[]
            {
                new VrModEntry
                {
                    GameName = "Test Game",
                    Category = "Full Motion Controls"
                }
            });
            _registry.Register(parser);

            var settings = new VrModTagSettings();
            var key = VrModNameMatcher.MakeDecisionKey("rototrobot-reviewall", "Test Game", game.Id);
            settings.MatchDecisions[key] = "Rejected";

            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);

            // Normal scan: the rejected match is skipped and NOT reported (callback not invoked).
            List<VrModReviewMatchEntry>? normalMatches = null;
            service.RunFullScanAsync(m => normalMatches = m).GetAwaiter().GetResult();
            Assert.That(normalMatches, Is.Null.Or.Empty);

            // reviewAll scan: the rejected match is included for review.
            List<VrModReviewMatchEntry>? reviewMatches = null;
            service.RunFullScanAsync(m => reviewMatches = m, reviewAll: true).GetAwaiter().GetResult();
            Assert.That(reviewMatches, Is.Not.Null);
            Assert.That(reviewMatches!.Count, Is.EqualTo(1));
            Assert.That(reviewMatches![0].Status, Is.EqualTo(MatchDecision.Rejected));
        }

        #region Test helpers

        private class TestParser : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;
            public TestParser(string sourceId, IEnumerable<VrModEntry> entries) : base(sourceId, "Test", "https://example.com/")
            {
                _entries = entries;
            }
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult(_entries);
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }

        private class TestParserWithChangeId : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;
            private readonly string _changeId;
            public TestParserWithChangeId(IEnumerable<VrModEntry> entries, string changeId)
                : base("test-ci", "Test", "https://example.com/")
            {
                _entries = entries;
                _changeId = changeId;
            }
            public override string? ChangeIdentifier => _changeId;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult(_entries);
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }

        private class FetchCountingParser : BaseVrModParser
        {
            private readonly IEnumerable<VrModEntry> _entries;
            private readonly string _changeId;
            public FetchCountingParser(IEnumerable<VrModEntry> entries, string changeId)
                : base("fetch-count", "Test", "https://example.com/")
            {
                _entries = entries;
                _changeId = changeId;
            }
            public int FetchCount { get; private set; }
            public override string? ChangeIdentifier => _changeId;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
            {
                FetchCount++;
                return Task.FromResult(_entries);
            }
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }

        #endregion
    }
}
