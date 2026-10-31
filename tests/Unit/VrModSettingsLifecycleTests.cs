using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NSubstitute;
using NUnit.Framework;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Regression tests for the settings page edit lifecycle (BeginEdit / EndEdit /
    /// CancelEdit) and the shared-settings invariant.
    ///
    /// The plugin constructs the tag service with the settings page's
    /// <see cref="VrModTagSettings"/> instance, so both share one object: decisions
    /// made in the review dialog, by a scan, or by a reset are immediately the
    /// page's decisions. These tests pin that invariant, which a real incident
    /// broke: a CancelEdit that replaced the shared object with a stale snapshot
    /// left the page displaying and persisting decisions the service had long
    /// since moved on from (a later "Save" wiped 378 saved decisions to zero).
    /// </summary>
    [Apartment(ApartmentState.STA)]
    [TestFixture]
    public class VrModSettingsLifecycleTests
    {
        /// <summary>
        /// The Playnite SDK's <see cref="Serialization"/> static holds its serializer in a
        /// private field and its Init is internal; seed it via reflection so
        /// BeginEdit's GetClone works in the test process. The SDK's real serializer
        /// lives in the Playnite core assembly, which the tests do not reference.
        /// </summary>
        [OneTimeSetUp]
        public void InitSdkSerialization()
        {
            var serializationType = typeof(Serialization);
            var field = serializationType.GetField("serializer",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (field != null && field.GetValue(null) == null)
                field.SetValue(null, new TestSerializer());
        }

        private static (IPlayniteAPI api, VrModTagPlugin plugin, VrModSettingsViewModel vm,
            VrModTagSettings shared) CreateWired()
        {
            var api = Substitute.For<IPlayniteAPI>();
            api.Paths.ExtensionsDataPath.Returns(Path.Combine(Path.GetTempPath(),
                "vrmodlifecycle_" + Guid.NewGuid().ToString("N")));
            var plugin = Substitute.For<VrModTagPlugin>(api);
            var registry = new VrModSourceRegistry();
            // Same registration the plugin constructor performs — one row per source.
            registry.Register(new RototRobotVrModParser());
            registry.Register(new Flat2VrParser());
            registry.Register(new UeVrProfilesParser());
            registry.Register(new ReFrameworkParser());
            registry.Register(new PcvrCentralParser());
            registry.Register(new MrNlceHubParser());
            registry.Register(new MrSurvivorHubParser());

            var vm = new VrModSettingsViewModel(plugin, registry);
            // The plugin's real service holds the plugin-internal page's settings
            // object; point the test page at that same object, exactly as the
            // plugin does for the page it hosts.
            var shared = plugin.TagService.Settings;
            vm.Settings = shared;
            return (api, plugin, vm, shared);
        }

        [Test]
        public void CancelEdit_RestoresEditableValues_KeepsSharedObjectAndLiveDecisions()
        {
            var (api, plugin, vm, shared) = CreateWired();
            var playniteAId = Guid.NewGuid();
            var playniteBId = Guid.NewGuid();
            shared.SetDecision("RototRobot", "GameA", playniteAId, MatchDecision.Accepted);
            shared.SetDecision("Flat2VR", "GameB", playniteBId, MatchDecision.Accepted);

            vm.BeginEdit();
            var intervalBefore = shared.ScanIntervalMinutes;
            vm.Settings.ScanIntervalMinutes = 1440;
            vm.Settings.EnableRototRobot = false;

            vm.CancelEdit();

            Assert.That(ReferenceEquals(vm.Settings, shared), Is.True,
                "Cancel must not replace the object shared with the tag service");
            Assert.That(vm.Settings.ScanIntervalMinutes, Is.EqualTo(intervalBefore),
                "editable values roll back to the snapshot");
            Assert.That(vm.Settings.EnableRototRobot, Is.True,
                "editable values roll back to the snapshot");
            Assert.That(shared.MatchDecisions.Count, Is.EqualTo(2),
                "live decisions survive Cancel untouched");
            Assert.That(vm.AcceptedCount, Is.EqualTo(2),
                "page counts the live decisions after Cancel");
        }

        [Test]
        public void EndEdit_PersistsLiveSettingsObject_WithoutClobberingDecisions()
        {
            var (api, plugin, vm, shared) = CreateWired();
            var playniteAId = Guid.NewGuid();
            var playniteBId = Guid.NewGuid();
            shared.SetDecision("RototRobot", "GameA", playniteAId, MatchDecision.Accepted);

            vm.BeginEdit();
            // Simulates the review dialog changing decisions on the shared object
            // while the settings page is open.
            shared.SetDecision("Flat2VR", "GameB", playniteBId, MatchDecision.Rejected);
            vm.EndEdit();

            Assert.That(ReferenceEquals(vm.Settings, shared), Is.True);
            Assert.That(shared.MatchDecisions.Count, Is.EqualTo(2),
                "EndEdit must not clobber the live decisions");
            var setDir = Path.Combine(api.Paths.ExtensionsDataPath, plugin.Id.ToString());
            Assert.That(Directory.Exists(setDir), Is.True,
                "settings must be persisted to disk");
        }

        [Test]
        public void RefreshAfterDecisionsChanged_UpdatesPageAndRowCounts()
        {
            var (api, plugin, vm, shared) = CreateWired();
            Assert.That(vm.AcceptedCount, Is.EqualTo(0));

            var playniteAId = Guid.NewGuid();
            var playniteCId = Guid.NewGuid();
            shared.SetDecision("RototRobot", "GameA", playniteAId, MatchDecision.Accepted);
            shared.SetDecision("RototRobot", "GameC", playniteCId, MatchDecision.Accepted);

            vm.RefreshAfterDecisionsChanged();

            Assert.That(vm.AcceptedCount, Is.EqualTo(2));
            var rototRow = vm.SourceRows.First(r => r.SourceId == "RototRobot");
            Assert.That(rototRow.MatchedCount, Is.EqualTo(2));
        }

        [Test]
        public void SavedAtDisplay_FormatsInLocalTime()
        {
            var entry = new VrModReviewMatchEntry
            {
                Status = MatchDecision.Accepted,
                PersistedDecision = MatchDecision.Accepted,
                SavedAtUtc = new DateTime(2026, 9, 12, 15, 40, 38, DateTimeKind.Utc),
            };
            Assert.That(entry.SavedAtDisplay, Is.EqualTo(
                new DateTime(2026, 9, 12, 15, 40, 38).ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
                "Saved at must show local time, not the raw UTC instant");
        }

        [Test]
        public void SourceRow_StatusText_FormatsLastScanInLocalTime()
        {
            var settings = new VrModTagSettings();
            settings.SourceStatuses["rototrobot"] = new SourceStatus
            {
                LastScanTime = new DateTime(2026, 9, 12, 15, 40, 0, DateTimeKind.Utc),
                LastResult = true,
                LastEntryCount = 5,
            };
            var source = Substitute.For<IVrModSource>();
            source.SourceId.Returns("RototRobot");
            var row = new VrModSourceRowViewModel(source, settings);

            var expected = new DateTime(2026, 9, 12, 15, 40, 0).ToLocalTime().ToString("G");
            Assert.That(row.StatusText, Does.Contain($"Last scan: {expected}"));
        }
    }

    /// <summary>
    /// Minimal <see cref="IDataSerializer"/> for tests: only the members the plugin
    /// lifecycle actually touches — <see cref="GetClone{T}"/> (reflection copy) and
    /// <see cref="ToJson(object, bool)"/> (marker text) — are functional.
    /// </summary>
    internal class TestSerializer : IDataSerializer
    {
        public string ToYaml(object obj) => throw new NotImplementedException();

        public T FromYaml<T>(string yaml) where T : class => throw new NotImplementedException();

        public bool TryFromYaml<T>(string yaml, out T content) where T : class
            => throw new NotImplementedException();

        public bool TryFromYaml<T>(string yaml, out T content, out Exception error) where T : class
            => throw new NotImplementedException();

        public T FromYamlFile<T>(string filePath) where T : class
            => throw new NotImplementedException();

        public bool TryFromYamlFile<T>(string filePath, out T content) where T : class
            => throw new NotImplementedException();

        public bool TryFromYamlFile<T>(string filePath, out T content, out Exception error)
            where T : class => throw new NotImplementedException();

        public string ToJson(object obj, bool formatted = false) => "TEST";

        public void ToJsonStream(object obj, Stream stream, bool formatted = false)
            => throw new NotImplementedException();

        public T FromJson<T>(string json) where T : class => throw new NotImplementedException();

        public bool TryFromJson<T>(string json, out T content) where T : class
            => throw new NotImplementedException();

        public bool TryFromJson<T>(string json, out T content, out Exception error)
            where T : class => throw new NotImplementedException();

        public T FromJsonStream<T>(Stream stream) where T : class
            => throw new NotImplementedException();

        public bool TryFromJsonStream<T>(Stream stream, out T content) where T : class
            => throw new NotImplementedException();

        public bool TryFromJsonStream<T>(Stream stream, out T content, out Exception error)
            where T : class => throw new NotImplementedException();

        public T FromJsonFile<T>(string filePath) where T : class
            => throw new NotImplementedException();

        public bool TryFromJsonFile<T>(string filePath, out T content) where T : class
            => throw new NotImplementedException();

        public bool TryFromJsonFile<T>(string filePath, out T content, out Exception error)
            where T : class => throw new NotImplementedException();

        public T FromToml<T>(string toml) where T : class => throw new NotImplementedException();

        public bool TryFromToml<T>(string toml, out T content) where T : class
            => throw new NotImplementedException();

        public bool TryFromToml<T>(string toml, out T content, out Exception error)
            where T : class => throw new NotImplementedException();

        public T FromTomlFile<T>(string filePath) where T : class
            => throw new NotImplementedException();

        public bool TryFromTomlFile<T>(string filePath, out T content) where T : class
            => throw new NotImplementedException();

        public bool TryFromTomlFile<T>(string filePath, out T content, out Exception error)
            where T : class => throw new NotImplementedException();

        public T GetClone<T>(T source) where T : class
        {
            var type = source.GetType();
            var clone = (T)Activator.CreateInstance(type, nonPublic: true);
            foreach (var prop in type.GetProperties())
            {
                if (prop.SetMethod == null)
                    continue;
                prop.SetValue(clone, prop.GetValue(source));
            }
            return clone;
        }

        public U GetClone<T, U>(T source) where T : class where U : class
            => throw new NotImplementedException();

        public bool AreObjectsEqual(object object1, object object2)
            => ReferenceEquals(object1, object2);
    }
}
