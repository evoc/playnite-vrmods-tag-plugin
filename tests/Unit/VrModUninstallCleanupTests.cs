using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
    /// Tests for the clean-uninstall behavior:
    /// - <see cref="VrModTagService.UninstallCleanup"/> removes every VRMod tag (from games
    ///   and from the tag collection), every plugin-owned source link, and all decisions.
    /// - <see cref="VrModTagPlugin.IsUninstallQueued"/> / <see cref="VrModTagPlugin.TextQueuesUninstall"/>
    ///   recognize Playnite's extinstalls.json queue (uninstall items for this plugin's
    ///   directory, and only those).
    /// </summary>
    [TestFixture]
    public class VrModUninstallCleanupTests
    {
        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private IPlayniteAPI _mockApi = null!;
        private VrModSourceRegistry _registry = null!;
        private string _tempDir = null!;

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

            _tempDir = Path.Combine(Path.GetTempPath(), "vrmod-uninstall-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Best effort: temp dir cleanup must not fail the test.
            }
        }

        // ---------------------------------------------------------------- service

        [Test]
        public void UninstallCleanup_RemovesTagsLinksTagDefinitionsAndDecisions()
        {
            var service = NewService(out var skyrim, out var fallout, out var parser);

            service.RunFullScanAsync().GetAwaiter().GetResult();
            // Each game gets the umbrella tag plus its category tag.
            Assert.That(skyrim.TagIds, Has.Count.EqualTo(2));
            Assert.That(fallout.TagIds, Has.Count.EqualTo(2));
            Assert.That(_tags.Items, Has.Count.EqualTo(3));

            service.Settings.SetDecision(parser.SourceId, "Skyrim", skyrim.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(skyrim);
            service.Settings.SetDecision(parser.SourceId, "Fallout 4", fallout.Id, MatchDecision.Accepted);
            service.ReconcileSourceLinks(fallout);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));
            Assert.That(fallout.Links, Has.Count.EqualTo(1));

            // A foreign link the plugin could not have generated must survive the cleanup.
            skyrim.Links.Add(new Link("Steam", "https://store.steampowered.com/app/720000"));

            service.UninstallCleanup();

            Assert.That(skyrim.TagIds, Is.Empty);
            Assert.That(fallout.TagIds, Is.Empty);
            Assert.That(skyrim.Links, Has.Count.EqualTo(1));
            Assert.That(skyrim.Links[0].Name, Is.EqualTo("Steam"));
            Assert.That(fallout.Links, Is.Empty);
            Assert.That(_tags.Items, Is.Empty);
            Assert.That(service.Settings.MatchDecisions, Is.Empty);
        }

        [Test]
        public void UninstallCleanup_EmptyLibrary_DoesNotThrow()
        {
            var service = NewService(out _, out _, out _);
            service.UninstallCleanup();

            Assert.That(_tags.Items, Is.Empty);
            Assert.That(service.Settings.MatchDecisions, Is.Empty);
        }

        // ------------------------------------------------------ queue-file checks

        [Test]
        public void TextQueuesUninstall_UninstallItemForOurDir_ReturnsTrue()
        {
            string json = "[{\"InstallType\":1,\"Path\":\"C:\\\\Ext\\\\E4A2\"}]";
            Assert.That(VrModTagPlugin.TextQueuesUninstall(json, @"C:\Ext\E4A2"), Is.True);
        }

        [Test]
        public void TextQueuesUninstall_PrettyPrinted_ReturnsTrue()
        {
            string json = "[\n  {\n    \"InstallType\": 1,\n    \"Path\": \"C:\\\\Ext\\\\E4A2\"\n  }\n]";
            Assert.That(VrModTagPlugin.TextQueuesUninstall(json, @"C:\Ext\E4A2"), Is.True);
        }

        [Test]
        public void TextQueuesUninstall_CaseAndTrailingSlashVariants_ReturnsTrue()
        {
            string json = "[{\"InstallType\":1,\"Path\":\"c:\\\\ext\\\\e4a2\"}]";
            Assert.That(VrModTagPlugin.TextQueuesUninstall(json, @"C:\Ext\E4A2\"), Is.True);
        }

        [Test]
        public void TextQueuesUninstall_MultipleItems_OursSecond_ReturnsTrue()
        {
            string json = "[{\"InstallType\":1,\"Path\":\"C:\\\\Other\"},{\"InstallType\":1,\"Path\":\"C:\\\\Ext\\\\E4A2\"}]";
            Assert.That(VrModTagPlugin.TextQueuesUninstall(json, @"C:\Ext\E4A2"), Is.True);
        }

        [Test]
        public void TextQueuesUninstall_DifferentDirectory_ReturnsFalse()
        {
            string json = "[{\"InstallType\":1,\"Path\":\"C:\\\\Other\"}]";
            Assert.That(VrModTagPlugin.TextQueuesUninstall(json, @"C:\Ext\E4A2"), Is.False);
        }

        [Test]
        public void TextQueuesUninstall_InstallItemOnly_ReturnsFalse()
        {
            // Install items queue .pext file paths, never bare directories.
            string json = "[{\"InstallType\":0,\"Path\":\"C:\\\\Ext\\\\plugin.pext\"}]";
            Assert.That(VrModTagPlugin.TextQueuesUninstall(json, @"C:\Ext\E4A2"), Is.False);
        }

        [Test]
        public void TextQueuesUninstall_EmptyQueue_ReturnsFalse()
        {
            Assert.That(VrModTagPlugin.TextQueuesUninstall("[]", @"C:\Ext\E4A2"), Is.False);
            Assert.That(VrModTagPlugin.TextQueuesUninstall("", @"C:\Ext\E4A2"), Is.False);
        }

        [Test]
        public void TextQueuesUninstall_UnparseableJson_WithOurDir_FallsBackToTrue()
        {
            // The fallback matches the JSON-escaped form of the path (backslashes doubled),
            // which is how the directory would appear inside JSON text.
            string garbage = "this is not json at all C:\\\\Ext\\\\E4A2";
            Assert.That(VrModTagPlugin.TextQueuesUninstall(garbage, @"C:\Ext\E4A2"), Is.True);
        }

        [Test]
        public void TextQueuesUninstall_UnparseableJson_WithoutOurDir_ReturnsFalse()
        {
            Assert.That(VrModTagPlugin.TextQueuesUninstall("random garbage without a path", @"C:\Ext\E4A2"), Is.False);
        }

        [Test]
        public void IsUninstallQueued_MissingFile_ReturnsFalse()
        {
            Assert.That(VrModTagPlugin.IsUninstallQueued(Path.Combine(_tempDir, "missing.json"), @"C:\Ext\E4A2"), Is.False);
        }

        [Test]
        public void IsUninstallQueued_NullExtensionDir_ReturnsFalse()
        {
            Assert.That(VrModTagPlugin.IsUninstallQueued(Path.Combine(_tempDir, "missing.json"), null), Is.False);
        }

        [Test]
        public void IsUninstallQueued_QueueFile_RoundTrip()
        {
            string queueFile = Path.Combine(_tempDir, "extinstalls.json");
            string json = "[{\"InstallType\":1,\"Path\":\"C:\\\\Ext\\\\E4A2\"}]";
            File.WriteAllText(queueFile, json);

            Assert.That(VrModTagPlugin.IsUninstallQueued(queueFile, @"C:\Ext\E4A2"), Is.True);
            Assert.That(VrModTagPlugin.IsUninstallQueued(queueFile, @"C:\Other"), Is.False);
        }

        // ---------------------------------------------------------------- helpers

        private VrModTagService NewService(out Game skyrim, out Game fallout, out TestParser parser)
        {
            skyrim = new Game("Skyrim")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            fallout = new Game("Fallout 4")
            {
                TagIds = new List<Guid>(),
                Links = new ObservableCollection<Link>()
            };
            _games.Items.Add(skyrim);
            _games.Items.Add(fallout);

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
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult(_entries);
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }
    }
}
