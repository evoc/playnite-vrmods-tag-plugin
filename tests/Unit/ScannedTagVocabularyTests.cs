using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// <summary>
    /// The tag vocabulary gate. A parser's category / quality / motion text is something a website
    /// wrote, and those three lists are what tag <i>names</i> are built from — so without a whitelist
    /// a parser that forwarded raw page text (a mod title, an HTML fragment, a 200-character
    /// description) would write that text into the user's library as a tag name, where it stays after
    /// the source disappears and shows up in every tag list, filter and export.
    ///
    /// These tests pin the three layers of the gate: the vocabulary itself, the parsers that map
    /// scraped labels onto it, and the one place in the service where source entries are accepted.
    /// </summary>
    [TestFixture]
    public class ScannedTagVocabularyTests
    {
        /// <summary>
        /// Shapes a hostile page can put into a category/quality/motion slot. None of them is a
        /// vocabulary value; the last three are what a source that renames its own categories looks
        /// like, which has to fail the same way a hostile value does.
        /// </summary>
        private static readonly string[] HostileValues =
        {
            "<script>alert(document.cookie)</script>",
            "</script><img src=x onerror=alert(1)>",
            "<img src=\"https://evil.example/tr.png\">",
            "{{7*7}}",
            "' OR 1=1--",
            "javascript:alert(1)",
            "Mod Name (2024) | Category | 6DOF",
            new string('x', 400),
            "https://evil.example/?utm=<b>",
            "\0hidden",
            "VRMod",
            "",
            "   ",
            "\t\n ",
            "Cool New Framework",
            "uevr",           // case variant of a known value is accepted by IsKnownTagValue, see below
        };

        // ---------------------------------------------------------------- vocabulary ----------------

        [TestCase("UEVR")]
        [TestCase("REFramework")]
        [TestCase("Flat2VR Port")]
        [TestCase("Dedicated")]
        [TestCase("VorpX")]
        [TestCase("LukeRoss")]
        [TestCase("PCVR Central")]
        [TestCase("PCVR Mods Hub")]
        [TestCase("Mr Survivor")]
        [TestCase("CompoundVR")]
        [TestCase("Full Motion Controls")]
        [TestCase("Motion Gesture")]
        [TestCase("Head Tracked")]
        [TestCase("Full Motion")]
        [TestCase("Partial Motion")]
        [TestCase("Gamepad Only")]
        [TestCase("No Motion")]
        [TestCase("Native-like")]
        [TestCase("Excellent")]
        [TestCase("Playable")]
        [TestCase("Experimental")]
        [TestCase("6DOF")]
        [TestCase("First Person")]
        [TestCase("Third Person")]
        public void VocabularyValues_AreAccepted(string value)
            => Assert.That(VrModCategories.IsKnownTagValue(value), Is.True);

        [TestCaseSource(nameof(HostileValues))]
        public void ScrapedTextOutsideTheVocabulary_IsNotAKnownTagValue(string value)
        {
            // "uevr" (a case variant) is deliberately accepted: the vocabulary test above pins the
            // canonical spellings, and a source that writes the same value in another case is the
            // same value. Everything else in this list must be refused.
            var isCaseVariantOfAKnownValue = value.Equals("UEVR", StringComparison.OrdinalIgnoreCase);
            Assert.That(VrModCategories.IsKnownTagValue(value), Is.EqualTo(isCaseVariantOfAKnownValue));
        }

        [Test]
        public void UnknownValuesOnAnEntry_AreReportedInsteadOfVanishingQuietly()
        {
            var entry = new VrModEntry
            {
                GameName = "Skyrim",
                Category = "UEVR",
                Categories = new List<string> { "UEVR", "<script>alert(1)</script>" },
                Qualities = new List<string> { "Excellent", "Recommended" },
                Motions = new List<string> { "Full Motion", "insane motion" }
            };

            Assert.That(VrModCategories.UnknownTagValues(entry),
                Is.EquivalentTo(new[] { "<script>alert(1)</script>", "Recommended", "insane motion" }));
        }

        /// <summary>
        /// Drift guard: every string constant the plugin declares as a category / facet / facet value
        /// must be in the vocabulary, and every name it declares as a tag must be a name the plugin is
        /// allowed to create. Adding a category constant without adding it to the whitelist (or a tag
        /// name that the whitelist cannot produce) fails here instead of quietly dropping tags from
        /// every game the category appears on.
        /// </summary>
        [Test]
        public void EveryDeclaredConstantIsInTheVocabularyItBuildsTagsWith()
        {
            var constants = typeof(VrModCategories)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
                .ToList();

            Assert.That(constants, Is.Not.Empty);
            foreach (var field in constants)
            {
                var value = (string)field.GetRawConstantValue();
                if (field.Name.StartsWith("Tag", StringComparison.Ordinal))
                {
                    Assert.That(VrModCategories.IsValidTagName(value), Is.True,
                        $"{field.Name} = \"{value}\" is advertised as a tag name but the plugin would refuse to create it");
                }
                else
                {
                    Assert.That(VrModCategories.IsKnownTagValue(value), Is.True,
                        $"{field.Name} = \"{value}\" is a category/facet value but is not in the tag vocabulary");
                }
            }
        }

        // ------------------------------------------------------------ tag name rules ----------------

        [TestCase("VRMod")]
        [TestCase("VRMod UEVR")]
        [TestCase("VRMod Excellent")]
        [TestCase("VRMod 6DOF")]
        [TestCase("VRMod First Person")]
        [TestCase("VRMod No Motion")]
        public void PluginTagNames_AreValid(string name)
            => Assert.That(VrModCategories.IsValidTagName(name), Is.True);

        [TestCase("VRMod <script>alert(1)</script>")]
        [TestCase("VRMod Mod Name (2024) | Category | 6DOF")]
        [TestCase("VRMod Cool New Framework")]
        [TestCase("VRMod ")]
        [TestCase("VRMod UEVR ")]          // padded value: would create a near-duplicate tag
        [TestCase("VRMod  UEVR")]         // double space
        [TestCase("VRModding")]           // the user's own tag
        [TestCase("VRModding tips")]
        [TestCase("Best VRMod")]
        [TestCase("")]
        [TestCase(null)]
        public void TagNamesBuiltFromScrapedText_AreRejected(string name)
            => Assert.That(VrModCategories.IsValidTagName(name), Is.False);

        [TestCase("VRMod", true)]
        [TestCase("vrmod", true)]                          // case-insensitive ownership
        [TestCase("VRMod UEVR", true)]
        [TestCase("VRMod Cool New Framework", true)]       // ours (removable) even though not creatable
        [TestCase("VRMod <script>alert(1)</script>", true)]
        [TestCase("VRModding", false)]                     // the user's own tag must survive removal
        [TestCase("VRModding tips", false)]
        [TestCase("My VRMod list", false)]
        [TestCase("BestVRMod", false)]
        [TestCase(null, false)]
        public void OwnershipOfATagName_IsStricterThanAPrefixMatchButStillOwnsAnythingShapedLikeOurs(
            string name, bool owned)
            => Assert.That(VrModCategories.IsPluginTagName(name), Is.EqualTo(owned));

        // ------------------------------------------------------- parser label mapping ----------------

        [TestCase("uevr-profile")]
        [TestCase("uuvr")]
        [TestCase("reframework")]
        [TestCase("vorpx")]
        [TestCase("luke-ross")]
        [TestCase("native-custom")]
        [TestCase("depth3d")]
        [TestCase("other")]
        public void PcvrCentralFrameworkLabels_MapOntoVocabularyValues(string label)
            => Assert.That(VrModCategories.IsKnownTagValue(PcvrCentralParser.MapFramework(label)), Is.True);

        [TestCase("native-like")]
        [TestCase("excellent")]
        [TestCase("playable")]
        [TestCase("experimental")]
        public void PcvrCentralQualityLabels_MapOntoVocabularyValues(string label)
            => Assert.That(VrModCategories.IsKnownTagValue(PcvrCentralParser.MapQuality(label)), Is.True);

        [TestCase("full")]
        [TestCase("partial")]
        [TestCase("gamepad-only")]
        [TestCase("none")]
        public void PcvrCentralMotionLabels_MapOntoVocabularyValues(string label)
            => Assert.That(VrModCategories.IsKnownTagValue(PcvrCentralParser.MapMotion(label)), Is.True);

        [TestCase("<script>alert(1)</script>")]
        [TestCase("the best framework ever")]
        public void PcvrCentralFrameworkLabelMapping_NeverReturnsAStringItDidNotChoose(string label)
            => Assert.That(PcvrCentralParser.MapFramework(label), Is.Null);

        [TestCase("active")]
        [TestCase("update-detected")]
        [TestCase("superseded")]
        [TestCase("broken")]
        [TestCase("abandoned")]
        public void PcvrCentralStatusLabels_MapOntoVocabularyValues(string label)
            => Assert.That(VrModCategories.IsKnownTagValue(PcvrCentralParser.MapStatus(label)), Is.True);

        [TestCase("<script>alert(1)</script>")]
        [TestCase("brand-new status")]
        [TestCase("unknown")]
        public void PcvrCentralStatusLabelMapping_NeverReturnsAStringItDidNotChoose(string label)
            => Assert.That(PcvrCentralParser.MapStatus(label), Is.Null);

        [TestCase("<img src=x>")]
        [TestCase("recommended")]
        [TestCase("amazing")]
        public void PcvrCentralQualityLabelMapping_NeverReturnsAStringItDidNotChoose(string label)
            => Assert.That(PcvrCentralParser.MapQuality(label), Is.Null);

        [TestCase("<script>")]
        [TestCase("insane")]
        [TestCase("unknown")]
        public void PcvrCentralMotionLabelMapping_NeverReturnsAStringItDidNotChoose(string label)
            => Assert.That(PcvrCentralParser.MapMotion(label), Is.Null);

        // ------------------------------------------------- service ingestion gate (e2e) ------------

        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private IPlayniteAPI _api = null!;
        private VrModSourceRegistry _registry = null!;
        private RecordingLogger _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _api = Substitute.For<IPlayniteAPI>();
            var db = Substitute.For<IGameDatabaseAPI>();
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();
            db.Games.Returns(_games);
            db.Tags.Returns(_tags);
            db.BufferedUpdate().Returns(new FakeBufferedUpdate());
            _api.Database.Returns(db);

            _registry = new VrModSourceRegistry();
            _logger = new RecordingLogger();
        }

        private void Scan(params VrModEntry[] entries)
        {
            _games.Items.Add(new Game("Skyrim") { TagIds = new List<Guid>() });
            var service = new VrModTagService(_api, new VrModTagSettings(), _logger, _registry);
            service.Settings.FuzzyMatchThreshold = 0.9f;
            _registry.Register(new VocabularyTestParser(entries));
            service.RunFullScanAsync().GetAwaiter().GetResult();
        }

        private List<string> CreatedTagNames() => _tags.Items.Select(t => t.Name!).ToList();

        [Test]
        public void ScanWithOnlyHostileFeatureValues_TagsTheGameWithTheUmbrellaOnly()
        {
            Scan(new VrModEntry
            {
                GameName = "Skyrim",
                Category = "<script>alert(document.cookie)</script>",
                Quality = "Recommended",
                Motion = "insane motion"
            });

            Assert.That(CreatedTagNames(), Is.EquivalentTo(new[] { "VRMod" }));
        }

        [Test]
        public void ScanKeepsVocabularyValuesOnTheSameEntryAndDropsTheRest()
        {
            Scan(new VrModEntry
            {
                GameName = "Skyrim",
                Category = "UEVR",
                Categories = new List<string> { "UEVR", "<script>alert(1)</script>" },
                Quality = "Excellent",
                Qualities = new List<string> { "Excellent", "Recommended" },
                Motion = "Full Motion",
                Motions = new List<string> { "Full Motion", "insane motion" }
            });

            Assert.That(CreatedTagNames(), Is.EquivalentTo(new[]
            {
                "VRMod", "VRMod UEVR", "VRMod Excellent", "VRMod Full Motion"
            }));
        }

        [Test]
        public void ScanTagsAKnownModStatusAndDropsAHostileOne()
        {
            // The status facet goes through the same gate as the other three: a status the source
            // invented becomes no tag, and a hostile string in the status slot becomes no tag - while
            // the vocabulary status still tags the game.
            Scan(new VrModEntry
            {
                GameName = "Skyrim",
                Category = "UEVR",
                Statuses = new List<string> { "Outdated", "<script>alert(document.cookie)</script>", "Fully Working Probably" }
            });

            Assert.That(CreatedTagNames(), Is.EquivalentTo(new[] { "VRMod", "VRMod UEVR", "VRMod Outdated" }));
            Assert.That(_logger.Warnings.Any(m => m.Contains("outside the tag vocabulary") && m.Contains("<script>")), Is.True);
        }

        [Test]
        public void ScanWritesNoTagNameThatThePluginWouldItselfRefuse()
        {
            Scan(new VrModEntry
            {
                GameName = "Skyrim",
                Category = "Mod Name (2024) | Category | 6DOF",
                Quality = new string('q', 200)
            });

            foreach (var name in CreatedTagNames())
                Assert.That(VrModCategories.IsValidTagName(name), Is.True,
                    $"created tag \"{name}\" is not a name the plugin is allowed to create");
        }

        [Test]
        public void DroppedValuesAreReportedToTheLogInsteadOfSkippedQuietly()
        {
            Scan(new VrModEntry { GameName = "Skyrim", Category = "Cool New Framework" });

            Assert.That(_logger.Warnings.Any(m =>
                m.Contains("outside the tag vocabulary") && m.Contains("Cool New Framework")), Is.True);
        }

        [Test]
        public void ASourceThatRenamedItsCategoriesIsVisibleInTheLogNotAsMissingTags()
        {
            // The operator's diagnosis matters as much as the drop: the warning names the source.
            Scan(new VrModEntry { GameName = "Skyrim", Category = "Brand New Framework Name" });

            Assert.That(_logger.Warnings.Any(m =>
                m.Contains("Ignored 1 value(s)") && m.Contains("Brand New Framework Name")), Is.True);
        }

        [Test]
        public void ControlCharactersInADroppedValueDoNotForgeLogLines()
        {
            Scan(new VrModEntry { GameName = "Skyrim", Category = "evil\u000D\u000Afake log line" });

            Assert.That(_logger.Warnings, Has.Count.GreaterThan(0));
            foreach (var message in _logger.Warnings)
            {
                Assert.That(message.Contains("\r"), Is.False);
                Assert.That(message.Contains("\n"), Is.False);
            }
        }

        [Test]
        public void AUserTagThatMerelySharesThePrefixSurvivesARescanOfTheSameGame()
        {
            // Ownership is what the stale-tag cleanup removes by; a user tag named "VRModding"
            // must never be swept up by it.
            _games.Items.Add(new Game("Skyrim") { TagIds = new List<Guid>() });
            var userTag = new Tag("VRModding") { Id = Guid.NewGuid() };
            _tags.Items.Add(userTag);
            _games.Items[0].TagIds.Add(userTag.Id);

            var service = new VrModTagService(_api, new VrModTagSettings(), _logger, _registry);
            service.Settings.FuzzyMatchThreshold = 0.9f;
            _registry.Register(new VocabularyTestParser(Array.Empty<VrModEntry>()));
            service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_games.Items[0].TagIds, Does.Contain(userTag.Id));
        }

        /// <summary>
        /// Keeps every logged message so the tests can assert on what the operator is told, which a
        /// throwaway mock cannot express without NSubstitute argument matchers on a void call.
        /// </summary>
        private class RecordingLogger : ILogger
        {
            public readonly List<string> Warnings = new();
            public void Warn(string message) => Warnings.Add(message);
            public void Warn(Exception ex, string message) => Warnings.Add(message);
            public void Info(string message) { }
            public void Debug(string message) { }
            public void Error(string message) { }
            public void Error(Exception ex) { }
            public void Info(Exception ex, string message) { }
            public void Debug(Exception ex, string message) { }
            public void Error(Exception ex, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception ex, string message) { }
        }

        private class VocabularyTestParser : BaseVrModParser
        {
            private static int _counter;
            private readonly IEnumerable<VrModEntry> _entries;
            public VocabularyTestParser(IEnumerable<VrModEntry> entries)
                : base($"vocab-{Interlocked.Increment(ref _counter)}", "Vocab Test", "https://example.com/")
                => _entries = entries;
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult(_entries);
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }
    }
}
