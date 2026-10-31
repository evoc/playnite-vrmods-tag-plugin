using NSubstitute;
using NUnit.Framework;
using Playnite.SDK;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteVRMods.Tests.Unit
{
    [TestFixture]
    public class VrModSettingsViewModelTests
    {
        [Test]
        public void BeginEdit_CreatesClone()
        {
            var settings = new VrModTagSettings { ScanIntervalMinutes = 30240 };
            var clone = new VrModTagSettings
            {
                ScanIntervalMinutes = settings.ScanIntervalMinutes,
                EnableRototRobot = settings.EnableRototRobot,
                EnableFlat2VR = settings.EnableFlat2VR,
                EnableUeVR = settings.EnableUeVR
            };

            Assert.That(clone, Is.Not.Null);
            Assert.That(clone.ScanIntervalMinutes, Is.EqualTo(30240));
        }

        [Test]
        public void CancelEdit_RestoresPreviousValues()
        {
            var original = new VrModTagSettings { ScanIntervalMinutes = 30240 };
            var settings = new VrModTagSettings { ScanIntervalMinutes = original.ScanIntervalMinutes };
            settings.ScanIntervalMinutes = 1440;
            // Simulate cancel: restore from original
            settings.ScanIntervalMinutes = original.ScanIntervalMinutes;

            Assert.That(settings.ScanIntervalMinutes, Is.EqualTo(30240));
        }

        [Test]
        public void DefaultSettings_AreCorrect()
        {
            var settings = new VrModTagSettings();

            Assert.That(settings.ScanIntervalMinutes, Is.EqualTo(10080));
        }

        [Test]
        public void LoadPluginSettings_ReturnsSavedValue()
        {
            var settings = new VrModTagSettings { ScanIntervalMinutes = 43200 };

            Assert.That(settings.ScanIntervalMinutes, Is.EqualTo(43200));
        }

        [Test]
        public void Settings_EnableRototRobot_DefaultsToTrue()
        {
            var settings = new VrModTagSettings();
            Assert.That(settings.EnableRototRobot, Is.True);
        }

        [Test]
        public void Settings_EnableFlat2VR_DefaultsToTrue()
        {
            var settings = new VrModTagSettings();
            Assert.That(settings.EnableFlat2VR, Is.True);
        }

        [Test]
        public void Settings_EnableUeVR_DefaultsToTrue()
        {
            var settings = new VrModTagSettings();
            Assert.That(settings.EnableUeVR, Is.True);
        }

        [Test]
        public void Settings_EnableReFramework_DefaultsToTrue()
        {
            var settings = new VrModTagSettings();
            Assert.That(settings.EnableReFramework, Is.True);
        }

        [Test]
        public void Plugin_CorruptedSettingsFile_ConstructsWithDefaults()
        {
            var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrmod-cfg-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var paths = Substitute.For<IPlaynitePathsAPI>();
                paths.ExtensionsDataPath.Returns(tempRoot);
                var api = Substitute.For<IPlayniteAPI>();
                api.Paths.Returns(paths);

                // Corrupt the saved-settings file the plugin will load from
                // (<ExtensionsDataPath>/<plugin.Id>/config.json).
                var dataDir = System.IO.Path.Combine(tempRoot, System.Guid.Parse("E4A2F8D1-3B5C-4E9A-A7D6-8F1C0E2B4A6D").ToString());
                System.IO.Directory.CreateDirectory(dataDir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dataDir, "config.json"), "{ corrupted settings file !!");

                // Before the fix, this threw JsonSerializationException out of the plugin
                // constructor, so the extension failed to load entirely.
                var plugin = new VrModTagPlugin(api);
                Assert.That(plugin.SettingsViewModel.Settings.EnableUeVR, Is.True);
                Assert.That(plugin.SettingsViewModel.Settings.ScanIntervalMinutes, Is.EqualTo(10080));
            }
            finally
            {
                System.IO.Directory.Delete(tempRoot, true);
            }
        }

        [Test]
        public void Ctor_SyncsDefaultSettings_DisablesOptInSourcesInRegistry()
        {
            var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrmod-cfg-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var paths = Substitute.For<IPlaynitePathsAPI>();
                paths.ExtensionsDataPath.Returns(tempRoot);
                var api = Substitute.For<IPlayniteAPI>();
                api.Paths.Returns(paths);

                // No saved settings file → default settings. The 7 original sources default to
                // enabled; the opt-in VorpX / Luke Ross sources default to disabled. Before the fix
                // the registry (every source constructed enabled in BaseVrModParser) was not synced
                // at construction, so the startup scan fetched VorpX / Luke Ross too. The ctor must
                // mirror the settings into the registry so GetEnabledSources() is correct from the
                // first scan.
                var plugin = new VrModTagPlugin(api);
                var sources = plugin.SettingsViewModel.SourceRegistry.Sources;

                var vorpx = sources.FirstOrDefault(s => s.SourceId == "VorpX");
                var lukeRoss = sources.FirstOrDefault(s => s.SourceId == "LukeRoss");
                Assert.That(vorpx, Is.Not.Null, "VorpX source expected to be registered");
                Assert.That(lukeRoss, Is.Not.Null, "Luke Ross source expected to be registered");
                Assert.That(vorpx.IsEnabled, Is.False, "opt-in VorpX must be disabled by default in the registry");
                Assert.That(lukeRoss.IsEnabled, Is.False, "opt-in Luke Ross must be disabled by default in the registry");

                // A default-enabled source must remain enabled, proving the ctor sync mirrors the
                // settings (not a blanket disable).
                var rototRobot = sources.FirstOrDefault(s => s.SourceId == "RototRobot");
                Assert.That(rototRobot, Is.Not.Null);
                Assert.That(rototRobot.IsEnabled, Is.True);
            }
            finally
            {
                System.IO.Directory.Delete(tempRoot, true);
            }
        }

        [Test]
        public void SyncSettingsToRegistry_PropagatesReFramework()
        {
            var registry = new VrModSourceRegistry();
            registry.Register(new TestSource("reframework", "REFramework"));

            var settings = new VrModTagSettings { EnableReFramework = false };
            SyncToRegistry(settings, registry);

            Assert.That(registry.Sources[0].IsEnabled, Is.False);
        }

        [Test]
        public void SyncSettingsToRegistry_PropagatesRototRobot()
        {
            var registry = new VrModSourceRegistry();
            registry.Register(new TestSource("rototrobot", "RototRobot"));

            var settings = new VrModTagSettings { EnableRototRobot = false };
            SyncToRegistry(settings, registry);

            Assert.That(registry.Sources[0].IsEnabled, Is.False);
        }

        [Test]
        public void SyncSettingsToRegistry_PropagatesFlat2VR()
        {
            var registry = new VrModSourceRegistry();
            registry.Register(new TestSource("flat2vr", "Flat2VR"));

            var settings = new VrModTagSettings { EnableFlat2VR = false };
            SyncToRegistry(settings, registry);

            Assert.That(registry.Sources[0].IsEnabled, Is.False);
        }

        [Test]
        public void SyncSettingsToRegistry_PropagatesUeVR()
        {
            var registry = new VrModSourceRegistry();
            registry.Register(new TestSource("uevr", "UeVR"));

            var settings = new VrModTagSettings { EnableUeVR = true };
            SyncToRegistry(settings, registry);

            Assert.That(registry.Sources[0].IsEnabled, Is.True);
        }

        [Test]
        public void SyncSettingsToRegistry_MultipleSources()
        {
            var registry = new VrModSourceRegistry();
            registry.Register(new TestSource("rototrobot", "RototRobot"));
            registry.Register(new TestSource("flat2vr", "Flat2VR"));
            registry.Register(new TestSource("uevr", "UeVR"));

            var settings = new VrModTagSettings
            {
                EnableRototRobot = false,
                EnableFlat2VR = true,
                EnableUeVR = true
            };
            SyncToRegistry(settings, registry);

            Assert.That(registry.Sources[0].IsEnabled, Is.False);
            Assert.That(registry.Sources[1].IsEnabled, Is.True);
            Assert.That(registry.Sources[2].IsEnabled, Is.True);
        }

        [Test]
        public void SyncSettingsToRegistry_UnknownSourceId_KeepsOriginal()
        {
            var registry = new VrModSourceRegistry();
            var source = new TestSource("unknown", "Unknown");
            source.IsEnabled = false;
            registry.Register(source);

            var settings = new VrModTagSettings();
            SyncToRegistry(settings, registry);

            Assert.That(source.IsEnabled, Is.False);
        }

        private static void SyncToRegistry(VrModTagSettings settings, VrModSourceRegistry registry)
        {
            foreach (var source in registry.Sources)
            {
                source.IsEnabled = source.SourceId switch
                {
                    "rototrobot" => settings.EnableRototRobot,
                    "flat2vr" => settings.EnableFlat2VR,
                    "uevr" => settings.EnableUeVR,
                    "reframework" => settings.EnableReFramework,
                    _ => source.IsEnabled
                };
            }
        }

        [Test]
        public void StatusText_SucceededZeroEntries_ShowsNoData()
        {
            var source = new TestSource("pcvrcentral", "PCVR Central");
            var settings = new VrModTagSettings
            {
                SourceStatuses = new Dictionary<string, SourceStatus>
                {
                    ["pcvrcentral"] = new SourceStatus { LastResult = true, LastEntryCount = 0, LastScanTime = DateTime.UtcNow }
                }
            };
            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.StatusText, Does.StartWith("No data:"));
            Assert.That(vm.StatusText, Does.Contain("0 entries"));
            Assert.That(vm.HasZeroEntries, Is.True);
        }

        [Test]
        public void StatusText_SucceededWithEntries_ShowsLastScan()
        {
            var source = new TestSource("rototrobot", "RototRobot");
            var settings = new VrModTagSettings
            {
                SourceStatuses = new Dictionary<string, SourceStatus>
                {
                    ["rototrobot"] = new SourceStatus { LastResult = true, LastEntryCount = 42, LastScanTime = DateTime.UtcNow }
                }
            };
            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.StatusText, Does.StartWith("Last scan:"));
            Assert.That(vm.HasZeroEntries, Is.False);
        }

        [Test]
        public void StatusText_FailedScan_ShowsErrorNotNoData()
        {
            var source = new TestSource("flat2vr", "Flat2VR");
            var settings = new VrModTagSettings
            {
                SourceStatuses = new Dictionary<string, SourceStatus>
                {
                    ["flat2vr"] = new SourceStatus { LastResult = false, LastError = "timeout", LastEntryCount = 0, LastScanTime = DateTime.UtcNow }
                }
            };
            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.StatusText, Does.StartWith("Error:"));
            Assert.That(vm.HasError, Is.True);
            Assert.That(vm.HasZeroEntries, Is.False); // a failure is not "no data"
        }

        [Test]
        public void StatusText_NeverScanned_ShowsNeverScanned()
        {
            var source = new TestSource("uevr", "UeVR");
            var settings = new VrModTagSettings();
            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.StatusText, Is.EqualTo("Never scanned"));
            Assert.That(vm.HasZeroEntries, Is.False);
        }

        // StatusShort is the settings grid's Last-scan cell text: the column header carries
        // the "Last scan" label, and the (unbounded) error message stays in the cell
        // tooltip (StatusText) — the cell itself shows a fixed short state.

        [Test]
        public void StatusShort_NeverScanned_ShowsNeverScanned()
        {
            var source = new TestSource("uevr", "UeVR");
            var vm = new VrModSourceRowViewModel(source, new VrModTagSettings());

            Assert.That(vm.StatusShort, Is.EqualTo("Never scanned"));
        }

        [Test]
        public void StatusShort_FailedScan_ShowsErrorWithoutTheMessage()
        {
            var source = new TestSource("flat2vr", "Flat2VR");
            var settings = new VrModTagSettings
            {
                SourceStatuses = new Dictionary<string, SourceStatus>
                {
                    ["flat2vr"] = new SourceStatus { LastResult = false, LastError = "timeout", LastEntryCount = 0, LastScanTime = DateTime.UtcNow }
                }
            };
            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.StatusShort, Is.EqualTo("Error"));
            // The message is not lost — it is the cell tooltip's source.
            Assert.That(vm.StatusText, Does.Contain("timeout"));
        }

        [Test]
        public void StatusShort_SucceededZeroEntries_ShowsNoData()
        {
            var source = new TestSource("pcvrcentral", "PCVR Central");
            var settings = new VrModTagSettings
            {
                SourceStatuses = new Dictionary<string, SourceStatus>
                {
                    ["pcvrcentral"] = new SourceStatus { LastResult = true, LastEntryCount = 0, LastScanTime = DateTime.UtcNow }
                }
            };
            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.StatusShort, Is.EqualTo("No data"));
        }

        [Test]
        public void StatusShort_SucceededWithEntries_ShowsBareLocalTime()
        {
            var source = new TestSource("rototrobot", "RototRobot");
            var utcTime = new DateTime(2026, 9, 13, 7, 36, 18, DateTimeKind.Utc);
            var settings = new VrModTagSettings
            {
                SourceStatuses = new Dictionary<string, SourceStatus>
                {
                    ["rototrobot"] = new SourceStatus { LastResult = true, LastEntryCount = 42, LastScanTime = utcTime }
                }
            };
            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.StatusShort, Is.EqualTo(utcTime.ToLocalTime().ToString("G")));
        }

        [Test]
        public void IsEnabled_ReadsFromSettings_BySourceId()
        {
            var source = new TestSource("rototrobot", "RototRobot");
            var settings = new VrModTagSettings { EnableRototRobot = false };
            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.IsEnabled, Is.False);
        }

        [Test]
        public void IsEnabled_Set_WritesToSettings_BySourceId()
        {
            var source = new TestSource("flat2vr", "Flat2VR");
            var settings = new VrModTagSettings();
            var vm = new VrModSourceRowViewModel(source, settings);

            vm.IsEnabled = false;

            Assert.That(settings.EnableFlat2VR, Is.False);
        }

        [Test]
        public void IsEnabled_UnknownSource_FallsBackToSourceProperty()
        {
            var source = new TestSource("unknown", "Unknown");
            var settings = new VrModTagSettings();
            var vm = new VrModSourceRowViewModel(source, settings);

            vm.IsEnabled = false;

            Assert.That(vm.IsEnabled, Is.False);
            Assert.That(source.IsEnabled, Is.False);
        }

        [Test]
        public void LinksEnabled_DefaultsToTrue()
        {
            var source = new TestSource("rototrobot", "RototRobot");
            var settings = new VrModTagSettings();
            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.LinksEnabled, Is.True);
        }

        [Test]
        public void LinksEnabled_Uncheck_AddsSourceIdToDisabledList()
        {
            var source = new TestSource("flat2vr", "Flat2VR");
            var settings = new VrModTagSettings();
            var vm = new VrModSourceRowViewModel(source, settings);

            vm.LinksEnabled = false;

            Assert.That(settings.DisabledLinkSources, Does.Contain("flat2vr"));
            Assert.That(settings.IsSourceLinksEnabled("flat2vr"), Is.False);
            Assert.That(vm.LinksEnabled, Is.False);
        }

        [Test]
        public void LinksEnabled_CheckAgain_RemovesFromDisabledList()
        {
            var source = new TestSource("flat2vr", "Flat2VR");
            var settings = new VrModTagSettings();
            settings.DisabledLinkSources.Add("flat2vr");
            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.LinksEnabled, Is.False);

            vm.LinksEnabled = true;

            Assert.That(settings.DisabledLinkSources, Is.Empty);
            Assert.That(vm.LinksEnabled, Is.True);
        }

        [Test]
        public void OnSourceDisabled_FiresWhenUnchecking()
        {
            var source = new TestSource("flat2vr", "Flat2VR");
            var settings = new VrModTagSettings(); // EnableFlat2VR defaults to true
            var vm = new VrModSourceRowViewModel(source, settings);

            string? fired = null;
            vm.OnSourceDisabled = id => fired = id;
            vm.IsEnabled = false; // true → false transition

            Assert.That(fired, Is.EqualTo("flat2vr"));
            Assert.That(settings.EnableFlat2VR, Is.False);
        }

        [Test]
        public void OnSourceDisabled_DoesNotFireOnEnable()
        {
            var source = new TestSource("flat2vr", "Flat2VR");
            var settings = new VrModTagSettings { EnableFlat2VR = false };
            var vm = new VrModSourceRowViewModel(source, settings);

            string? fired = null;
            vm.OnSourceDisabled = id => fired = id;
            vm.IsEnabled = true; // false → true (enable) — must not fire

            Assert.That(fired, Is.Null);
        }

        [Test]
        public void OnSourceDisabled_DoesNotFireWhenAlreadyDisabled()
        {
            var source = new TestSource("flat2vr", "Flat2VR");
            var settings = new VrModTagSettings { EnableFlat2VR = false };
            var vm = new VrModSourceRowViewModel(source, settings);

            string? fired = null;
            vm.OnSourceDisabled = id => fired = id;
            vm.IsEnabled = false; // already disabled → no true→false transition

            Assert.That(fired, Is.Null);
        }

        [Test]
        public void OnSourceDisabled_DoesNotFireOnConstruction()
        {
            var source = new TestSource("flat2vr", "Flat2VR");
            var settings = new VrModTagSettings();
            var vm = new VrModSourceRowViewModel(source, settings);

            string? fired = null;
            vm.OnSourceDisabled = id => fired = id; // construction must not have fired it
            Assert.That(fired, Is.Null);
        }

        [Test]
        public void WebsiteUrl_PassesThroughFromSource()
        {
            var source = new TestSource("rototrobot", "RototRobot");
            var settings = new VrModTagSettings();
            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.WebsiteUrl, Is.EqualTo(source.WebsiteUrl));
        }

        [Test]
        public void ListedCount_ReflectsLastEntryCount()
        {
            var source = new TestSource("rototrobot", "RototRobot");
            var settings = new VrModTagSettings
            {
                SourceStatuses = new Dictionary<string, SourceStatus>
                {
                    ["rototrobot"] = new SourceStatus { LastResult = true, LastEntryCount = 223, LastScanTime = DateTime.UtcNow }
                }
            };
            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.ListedCount, Is.EqualTo(223));
        }

        [Test]
        public void ListedCount_NeverScanned_IsZero()
        {
            var source = new TestSource("rototrobot", "RototRobot");
            var vm = new VrModSourceRowViewModel(source, new VrModTagSettings());

            Assert.That(vm.ListedCount, Is.Zero);
        }

        [Test]
        public void MatchedCount_CountsOnlyAcceptedDecisionsForSource()
        {
            var source = new TestSource("rototrobot", "RototRobot");
            var settings = new VrModTagSettings();
            var gameAId = Guid.NewGuid();
            var gameBId = Guid.NewGuid();
            var gameCId = Guid.NewGuid();
            var gameDId = Guid.NewGuid();
            settings.SetDecision("RototRobot", "gameA", gameAId, MatchDecision.Accepted);
            settings.SetDecision("RototRobot", "gameB", gameBId, MatchDecision.Accepted);
            settings.SetDecision("RototRobot", "gameC", gameCId, MatchDecision.Rejected);
            settings.SetDecision("UeVR", "gameD", gameDId, MatchDecision.Accepted);

            var vm = new VrModSourceRowViewModel(source, settings);

            // Only the two accepted RototRobot decisions count (not the rejected one,
            // not the other source's accepted decision).
            Assert.That(vm.MatchedCount, Is.EqualTo(2));
        }

        [Test]
        public void CoverageText_EmptyWhenNothingScanned()
        {
            var source = new TestSource("rototrobot", "RototRobot");
            var vm = new VrModSourceRowViewModel(source, new VrModTagSettings());

            Assert.That(vm.CoverageText, Is.Empty);
        }

        [Test]
        public void CoverageText_ShowsListedAndMatched()
        {
            var source = new TestSource("rototrobot", "RototRobot");
            var settings = new VrModTagSettings
            {
                SourceStatuses = new Dictionary<string, SourceStatus>
                {
                    ["rototrobot"] = new SourceStatus { LastResult = true, LastEntryCount = 223, LastScanTime = DateTime.UtcNow }
                }
            };
            var gameAId = Guid.NewGuid();
            settings.SetDecision("RototRobot", "gameA", gameAId, MatchDecision.Accepted);

            var vm = new VrModSourceRowViewModel(source, settings);

            Assert.That(vm.CoverageText, Is.EqualTo("lists 223 · matched 1"));
        }

        private class TestSource : IVrModSource
        {
            public TestSource(string id, string displayName)
            {
                SourceId = id;
                DisplayName = displayName;
                IsEnabled = true;
            }
            public string SourceId { get; }
            public string DisplayName { get; }
            public string WebsiteUrl => "https://example.com/";
            public bool IsEnabled { get; set; }
            public string? ChangeIdentifier => null;
            public List<string>? RunLog { get; set; }
            public System.Threading.CancellationToken ScanToken { get; set; }
            public Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult<IEnumerable<VrModEntry>>(Array.Empty<VrModEntry>());
            public string? GetCanonicalName(VrModEntry entry) => null;
        }
    }
}
