using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    /// <summary>
    /// Integration tests that parse actual snapshot files from the source websites.
    /// These verify that the parsers work against real-world HTML content.
    ///
    /// Snapshot files are stored in tests/Parsers/snapshots/ and should be refreshed
    /// when the source website structure changes.
    /// </summary>
    [TestFixture]
    public class SnapshotParserTests
    {
        private string GetSnapshotPath(string fileName)
        {
            var assemblyDir = Path.GetDirectoryName(typeof(SnapshotParserTests).Assembly.Location);
            return Path.GetFullPath(Path.Combine(assemblyDir!, "..", "..", "..", "Parsers", "snapshots", fileName));
        }

        [Test]
        public void RototRobot_ParseRenderedHtml_ProducesValidEntries()
        {
            var snapshotPath = GetSnapshotPath("rototrobot-rendered-v2.html");
            Assert.That(File.Exists(snapshotPath), Is.True, "Snapshot file not found");

            var html = File.ReadAllText(snapshotPath);
            var parser = new RototRobotVrModParser();
            var entries = parser.ParseRenderedHtml(html);

            Assert.That(entries, Is.Not.Empty, "Should parse at least one entry from snapshot");
            Assert.That(entries, Has.Count.GreaterThanOrEqualTo(100), $"Expected 100+ entries, got {entries.Count}");

            // Verify all entries have valid data
            var validCategories = new[] { "Full Motion Controls", "UEVR", "Motion Gesture", "Head Tracked", "Dedicated", "Dedicated" };
            foreach (var entry in entries)
            {
                Assert.That(entry.GameName, Is.Not.Empty, "Every entry should have a game name");
                Assert.That(entry.Category, Is.Not.Empty, "Every entry should have a category");
                Assert.That(validCategories, Does.Contain(entry.Category), $"Unexpected category: {entry.Category}");
            }

            // Verify category distribution makes sense
            var motionEntries = entries.Count(e => e.Category == "Full Motion Controls");
            Assert.That(motionEntries, Is.GreaterThan(50), $"Expected 50+ Full Motion Controls entries, got {motionEntries}");
        }

        [Test]
        public void RototRobot_ParseRenderedHtml_OneEntryPerGameAfterDuplicateMerge()
        {
            // The live site lists some games in several categories; the parser must merge the
            // duplicates so the result is exactly one entry per game (grouped by name).
            var snapshotPath = GetSnapshotPath("rototrobot-rendered-v2.html");
            Assert.That(File.Exists(snapshotPath), Is.True, "Snapshot file not found");

            var html = File.ReadAllText(snapshotPath);
            var parser = new RototRobotVrModParser();
            var entries = parser.ParseRenderedHtml(html);

            var keys = entries.Select(e => BaseVrModParser.DuplicateGroupKey(e.GameName)).ToList();
            Assert.That(keys.Distinct().Count(), Is.EqualTo(keys.Count()),
                "Parser output must be one entry per game (duplicate listings merged)");
        }

        [Test]
        public void RototRobot_ParseRenderedHtml_ExtractsSteamAppIds_WhenAvailable()
        {
            var snapshotPath = GetSnapshotPath("rototrobot-rendered-v2.html");
            Assert.That(File.Exists(snapshotPath), Is.True, "Snapshot file not found");

            var html = File.ReadAllText(snapshotPath);
            var parser = new RototRobotVrModParser();
            var entries = parser.ParseRenderedHtml(html);

            var withSteamId = entries.Where(e => e.SteamAppId != null).ToList();
            Assert.That(withSteamId, Is.Not.Empty, "Should extract at least some Steam App IDs");

            // Verify Steam App IDs are numeric
            foreach (var entry in withSteamId)
            {
                Assert.That(entry.SteamAppId, Does.Match(@"^\d+$"), $"Steam App ID should be numeric: {entry.SteamAppId}");
            }
        }

        [Test]
        public void Flat2VR_ParseHtml_ProducesValidEntries()
        {
            var snapshotPath = GetSnapshotPath("flat2vr.html");
            Assert.That(File.Exists(snapshotPath), Is.True, "Snapshot file not found");

            var html = File.ReadAllText(snapshotPath);
            var parser = new Flat2VrParser();
            var entries = parser.ParseHtml(html);

            Assert.That(entries, Is.Not.Empty, "Should parse at least one entry from snapshot");

            // All entries should be categorized as Flat2VR Port
            foreach (var entry in entries)
            {
                Assert.That(entry.Category, Is.EqualTo("Flat2VR Port"), $"Entry {entry.GameName} should be Flat2VR Port, got {entry.Category}");
                Assert.That(entry.GameName, Does.EndWith(" VR"), $"Flat2VR entries should end with ' VR': {entry.GameName}");
            }
        }

        [Test]
        public void UeVr_ParseFirestore_ReturnsEntries()
        {
            // Test the UeVR parser against live Firestore API
            // This requires network access
            var parser = new UeVrProfilesParser();
            var entries = parser.FetchEntriesAsync().Result;

            Assert.That(entries, Is.Not.Empty, "Should parse at least one entry from Firestore");
            Assert.That(entries.Count, Is.GreaterThanOrEqualTo(50), "Expected 50+ entries");

            // All entries should be UEVR category
            foreach (var entry in entries)
            {
                Assert.That(entry.Category, Is.EqualTo("UEVR"));
                Assert.That(entry.GameName, Is.Not.Empty);
            }

            // Many should have Steam App IDs
            var withSteamId = entries.Where(e => e.SteamAppId != null).ToList();
            Assert.That(withSteamId, Is.Not.Empty, "Should have at least some Steam App IDs");
        }
    }
}
