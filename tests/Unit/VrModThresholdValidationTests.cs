using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NSubstitute;
using NUnit.Framework;
using Playnite.SDK;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// The fuzzy match threshold decides which library entries get tags and source links, and it is
    /// read from a JSON file on disk as well as from the settings slider. A value of 0.0 is accepted
    /// by a plain "0.0–1.0" range check, and at 0.0 the name matcher calls nearly every (entry, game)
    /// pair a match — hundreds of writes per scan and a review dialog nobody can finish. These tests
    /// pin the floor, including the path that range check never covered: a settings file on disk.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    [TestFixture]
    public class VrModThresholdValidationTests
    {
        private static (VrModSettingsViewModel vm, VrModTagSettings settings) CreatePage()
        {
            var api = Substitute.For<IPlayniteAPI>();
            api.Paths.ExtensionsDataPath.Returns(Path.Combine(Path.GetTempPath(),
                "vrmodthreshold_" + Guid.NewGuid().ToString("N")));
            var plugin = Substitute.For<VrModTagPlugin>(api);
            var registry = new VrModSourceRegistry();
            var vm = new VrModSettingsViewModel(plugin, registry);
            // The settings page and the tag service share one settings object in the real plugin; the
            // same wiring is needed here or VerifySettings would validate a different object than the
            // one the test changed.
            var settings = plugin.TagService.Settings;
            vm.Settings = settings;
            return (vm, settings);
        }

        [Test]
        public void ATresholdOfZeroIsRaisedToTheFloorAndReportedInsteadOfSaved()
        {
            var (vm, settings) = CreatePage();

            settings.FuzzyMatchThreshold = 0.0f;

            Assert.That(settings.FuzzyMatchThreshold, Is.EqualTo(VrModTagSettings.MinFuzzyMatchThreshold));
            Assert.That(settings.FuzzyMatchThresholdWasAdjusted, Is.True);

            var valid = vm.VerifySettings(out var errors);
            Assert.That(valid, Is.False, "an out-of-range threshold must not be saved quietly");
            Assert.That(errors.Count, Is.EqualTo(1));
            Assert.That(errors[0], Does.Contain("Fuzzy match threshold"));
            Assert.That(errors[0], Does.Contain("50%"));

            // Reported once: a second Save after the user has moved the slider back should not keep
            // failing for a value that is now fine.
            Assert.That(vm.VerifySettings(out var again), Is.True);
            Assert.That(again.Count, Is.EqualTo(0));
        }

        [Test]
        public void ANumberFromASettingsFileIsClampedIntoRangeOnTheWayIn()
        {
            Assert.That(VrModTagSettings.SanitizeFuzzyMatchThreshold(0.0f), Is.EqualTo(0.5f));
            Assert.That(VrModTagSettings.SanitizeFuzzyMatchThreshold(-3f), Is.EqualTo(0.5f));
            Assert.That(VrModTagSettings.SanitizeFuzzyMatchThreshold(42f), Is.EqualTo(1.0f));
            // NaN is not caught by range comparisons at all (every comparison against NaN is false),
            // so it has to be handled explicitly or it poisons every "score >= threshold" test.
            Assert.That(VrModTagSettings.SanitizeFuzzyMatchThreshold(float.NaN), Is.EqualTo(0.8f));
            Assert.That(VrModTagSettings.SanitizeFuzzyMatchThreshold(0.65f), Is.EqualTo(0.65f));
        }

        [Test]
        public void AThresholdAssignedOutsideTheRangeIsClampedWhateverSetsIt()
        {
            // Assigned through the property, which is how a deserializer reaches it.
            var settings = new VrModTagSettings { FuzzyMatchThreshold = 0.0f };
            Assert.That(settings.FuzzyMatchThreshold, Is.EqualTo(0.5f));

            settings.FuzzyMatchThreshold = float.NaN;
            Assert.That(settings.FuzzyMatchThreshold, Is.EqualTo(0.8f));

            settings.FuzzyMatchThreshold = 3.5f;
            Assert.That(settings.FuzzyMatchThreshold, Is.EqualTo(1.0f));
        }

        [Test]
        public void ATresholdOfZeroInASettingsFileArrivesClamped()
        {
            // Playnite materializes this class by setting its public properties from the saved JSON, so
            // a hand-edited file, an older plugin version, or a half-written save reaches the clamp
            // through the setter rather than through the settings dialog.
            var fromFile = System.Text.Json.JsonSerializer.Deserialize<VrModTagSettings>("{\"FuzzyMatchThreshold\": 0.0}");

            Assert.That(fromFile, Is.Not.Null);
            Assert.That(fromFile!.FuzzyMatchThreshold, Is.EqualTo(VrModTagSettings.MinFuzzyMatchThreshold),
                "a settings file must not be able to install a threshold that matches the whole library");
        }

        [Test]
        public void AValidThresholdIsKeptExactlyAsEntered()
        {
            var (vm, settings) = CreatePage();

            settings.FuzzyMatchThreshold = 0.65f;

            Assert.That(settings.FuzzyMatchThreshold, Is.EqualTo(0.65f));
            Assert.That(settings.FuzzyMatchThresholdWasAdjusted, Is.False);
            Assert.That(vm.VerifySettings(out var errors), Is.True);
            Assert.That(errors, Is.Empty);
        }

        /// <summary>
        /// The floor has to be a threshold the matcher is still sane at: unrelated names must not
        /// match at 0.5, otherwise raising 0.0 to 0.5 would only move mass matching slightly.
        /// </summary>
        [Test]
        public void TheFloorThresholdStillRejectsUnrelatedNames()
        {
            var floor = VrModTagSettings.MinFuzzyMatchThreshold;

            Assert.That(VrModNameMatcher.GetMatchScore("resident evil 4", "death stranding", floor), Is.EqualTo(-1));
            Assert.That(VrModNameMatcher.GetMatchScore("half life 2", "beat blade", floor), Is.EqualTo(-1));
            Assert.That(VrModNameMatcher.GetMatchScore("beat blade", "beat saber", floor), Is.GreaterThanOrEqualTo(floor),
                "the floor must still admit the loose-but-plausible pairs it exists to keep");
            Assert.That(VrModNameMatcher.GetMatchScore("beat blade", "beat saber", 0.8f), Is.EqualTo(-1),
                "i.e. the floor is genuinely looser than the default, not a re-labelled default");
        }
    }
}
