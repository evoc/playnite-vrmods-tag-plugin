using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// The decision maps are published copy-on-write, and that is the only thing standing between
    /// Playnite and a dictionary a scan thread is mutating.
    ///
    /// Playnite reads <see cref="VrModTagSettings.MatchDecisions"/>,
    /// <see cref="VrModTagSettings.DecisionTimestamps"/> and
    /// <see cref="VrModTagSettings.SourceTagsByDecision"/> as plain properties: every write of
    /// the plugin's config.json serializes them (Playnite does not rewrite them when it exits —
    /// the plugin's own save paths are what write it), and when the settings page opens
    /// Serialization.GetClone walks them. Both happen on a thread that is not a scan thread, and no
    /// lock of ours is ever taken on that path. A plain Dictionary enumerated while another thread
    /// adds a key throws "Collection was modified" inside Playnite's settings save; a map read
    /// half-applied is worse, because it writes a torn decision set to disk with no symptom at all.
    /// The getters cannot hand out copies instead - Newtonsoft's default ObjectCreationHandling
    /// reuses whatever a getter returns and adds into it, so a copy-returning getter silently
    /// discards everything loaded from the settings file (test 4 below pins that). So the guarantee
    /// has to come from the writer side: an instance that has been published is never mutated.
    /// </summary>
    [TestFixture]
    public class VrModSettingsPublicationTests
    {
        private static readonly Guid GameA = Guid.Parse("11111111-1111-1111-1111-111111111111");

        [Test]
        public void A_map_handed_to_a_caller_is_never_mutated_by_a_later_write()
        {
            var settings = new VrModTagSettings();
            settings.SetDecision("PcvrCentral", "Half-Life", GameA, MatchDecision.Accepted, "VRMod PCVR Port");

            var published = settings.MatchDecisions;
            var publishedTimestamps = settings.DecisionTimestamps;
            var publishedTags = settings.SourceTagsByDecision;
            var snapshotOfPublished = published.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // Every writer shape the plugin has: add, overwrite, remove-for-game, clear-one,
            // clear-all, and the scan's first-seen timestamp upsert.
            settings.SetDecision("PcvrCentral", "Portal VR", GameA, MatchDecision.Pending);
            settings.SetDecision("PcvrCentral", "Half-Life", GameA, MatchDecision.Rejected);
            settings.ClearDecision("PcvrCentral", "Portal VR", GameA);
            settings.ClearDecisionsForGame(Guid.NewGuid());
            settings.EnsureDecisionTimestampUtc("PcvrCentral||Half-Life||" + Guid.NewGuid().ToString("D"), DateTime.UtcNow);
            settings.ClearAllDecisions();

            Assert.Multiple(() =>
            {
                // What the caller is holding is exactly what it was handed - a walk in progress
                // cannot have its legs pulled out from under it.
                Assert.That(published, Is.EquivalentTo(snapshotOfPublished), "the published map was mutated");
                Assert.That(published.Count, Is.EqualTo(1), "the map a caller holds gained or lost entries");
                Assert.That(settings.MatchDecisions, Is.Not.SameAs(published), "a write reused the published instance instead of publishing a new one");
                Assert.That(settings.DecisionTimestamps, Is.Not.SameAs(publishedTimestamps), "timestamps were mutated in place");
                Assert.That(settings.SourceTagsByDecision, Is.Not.SameAs(publishedTags), "the tag baseline map was mutated in place");
            });
        }

        [Test]
        public void A_walk_survives_a_scan_and_a_review_dialog_writing_at_the_same_time()
        {
            // The shape of the real collision: Playnite serializing / cloning the settings while a
            // scan thread and the review dialog's UI thread write decisions. The walk is what a
            // serializer does - enumerate each published map and read every entry.
            var settings = new VrModTagSettings();
            for (var i = 0; i < 200; i++)
            {
                settings.SetDecision("PcvrCentral", "Seed " + i, Guid.NewGuid(), MatchDecision.Accepted, "VRMod PCVR Port");
            }

            var writes = 0;
            var walks = 0;
            var failures = new List<string>();
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var scanThread = Task.Run(() =>
                {
                    var n = 0;
                    while (!stop.IsCancellationRequested)
                    {
                        var gameId = Guid.NewGuid();
                        settings.SetDecision("PcvrCentral", "Scan " + n, gameId, MatchDecision.Accepted, "VRMod PCVR Port");
                        settings.EnsureDecisionTimestampUtc("PcvrCentral||Scan " + n + "||" + gameId.ToString("D"), DateTime.UtcNow);
                        settings.ClearDecisionsForGame(gameId);
                        n++;
                        Interlocked.Increment(ref writes);
                    }
                });

                var dialogThread = Task.Run(() =>
                {
                    var n = 0;
                    while (!stop.IsCancellationRequested)
                    {
                        settings.SetDecision("PcvrCentral", "Dialog " + n, GameA, n % 2 == 0 ? MatchDecision.Pending : MatchDecision.Rejected);
                        settings.ClearDecision("PcvrCentral", "Dialog " + (n - 1), GameA);
                        n++;
                        Interlocked.Increment(ref writes);
                    }
                });

                var walkers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            // Three independent walks, the way a serializer walks them: one map at a
                            // time, straight off the properties, no lock.
                            var decisions = new List<KeyValuePair<string, string>>(settings.MatchDecisions);
                            var timestamps = new List<KeyValuePair<string, string>>(settings.DecisionTimestamps);
                            var tags = new List<KeyValuePair<string, string>>(settings.SourceTagsByDecision);
                            Interlocked.Increment(ref walks);

                            // What a walk must never see is a map that was copied mid-write: a
                            // stored value that is not a decision, or a timestamp that is not a
                            // timestamp. Cross-map pairing (a baseline whose decision landed in a
                            // newer map) is NOT claimed here on purpose: a walker reads the three
                            // properties one after another, so it can straddle a write - that skew
                            // is benign and self-healing on the next scan, and pretending otherwise
                            // would be a claim the design does not make.
                            foreach (var kvp in decisions)
                            {
                                if (!Enum.TryParse(kvp.Value, out MatchDecision _))
                                    failures.Add($"decision value read torn: '{kvp.Value}' for key '{kvp.Key}'");
                            }

                            foreach (var kvp in timestamps)
                            {
                                if (!DateTime.TryParse(kvp.Value, System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.RoundtripKind, out DateTime _))
                                    failures.Add($"timestamp read torn: '{kvp.Value}' for key '{kvp.Key}'");
                            }

                            foreach (var key in tags.Select(kvp => kvp.Key))
                            {
                                if (string.IsNullOrWhiteSpace(key))
                                    failures.Add("tag baseline with an empty key");
                            }
                        }
                        catch (Exception ex)
                        {
                            // This is the actual bug this test was written for: enumerating a
                            // dictionary somebody else is writing throws.
                            failures.Add($"{ex.GetType().Name} while walking the settings: {ex.Message}");
                        }
                    }
                })).ToArray();

                Task.WaitAll(scanThread, dialogThread, walkers[0], walkers[1], walkers[2]);
            }

            Assert.Multiple(() =>
            {
                Assert.That(writes, Is.GreaterThan(300), "the writers barely ran, so this test proved nothing");
                Assert.That(walks, Is.GreaterThan(100), "the walkers barely ran, so this test proved nothing");
                Assert.That(failures, Is.Empty, "walks and writes collided:\n" + string.Join("\n", failures.Take(5)));
                Assert.That(settings.MatchDecisions.Count, Is.GreaterThanOrEqualTo(200), "the seed decisions were lost under the writes");
            });
        }

        [Test]
        public void A_steady_state_scan_publishes_nothing_because_it_changes_nothing()
        {
            // The reason this design is affordable: a scan over matches the settings file already
            // knows does not clone a single dictionary, so the map every reader is holding stays
            // the same instance for the whole scan. Without this, copy-on-write would mean tens of
            // thousands of copies per scan for no change at all.
            var settings = new VrModTagSettings();
            var keys = new List<string>();
            for (var i = 0; i < 400; i++)
            {
                var gameId = Guid.NewGuid();
                settings.SetDecision("PcvrCentral", "Known " + i, gameId, MatchDecision.Pending);
                keys.Add("PcvrCentral||Known " + i + "||" + gameId.ToString("D"));
            }

            var published = settings.DecisionTimestamps;
            var decisions = settings.MatchDecisions;

            for (var pass = 0; pass < 3; pass++)
            {
                foreach (var key in keys)
                {
                    settings.EnsureDecisionTimestampUtc(key, DateTime.UtcNow);
                    Assert.That(settings.DecisionTimestamps, Is.SameAs(published), "a no-change write published a different map");
                    Assert.That(settings.MatchDecisions, Is.SameAs(decisions), "a timestamp upsert republished the decision map");
                }
            }

            // A new match is the one case that does publish, and it publishes only the map it
            // actually changed.
            var newKey = "PcvrCentral||Brand new||" + Guid.NewGuid().ToString("D");
            settings.EnsureDecisionTimestampUtc(newKey, DateTime.UtcNow);
            Assert.That(settings.DecisionTimestamps, Is.Not.SameAs(published), "the map that did change was not published as a new instance");
            Assert.That(settings.MatchDecisions, Is.SameAs(decisions), "the map that did not change was swapped for nothing");
        }

        [Test]
        public void Deserialization_still_populates_the_live_published_map()
        {
            // What forbids the obvious "fix" of returning a copy from the getter: a serializer with
            // Newtonsoft's default ObjectCreationHandling gets the live instance from the getter and
            // adds into it. A copy would be added into, then dropped, and the plugin would come back
            // with no decisions at all - silently, on every start.
            var settings = new VrModTagSettings();

            var loadTarget = settings.MatchDecisions;              // what a getter hands a deserializer
            loadTarget["PcvrCentral||Half-Life||" + GameA.ToString("D")] = "Accepted";
            loadTarget["PcvrCentral||Portal||" + GameA.ToString("D")] = "Rejected";
            settings.DecisionTimestamps["PcvrCentral||Half-Life||" + GameA.ToString("D")] = "2026-10-01T00:00:00.0000000Z";

            Assert.Multiple(() =>
            {
                Assert.That(settings.GetDecision("PcvrCentral", "Half-Life", GameA), Is.EqualTo(MatchDecision.Accepted),
                    "decisions added the way Playnite's serializer adds them were not visible to the plugin");
                Assert.That(settings.SnapshotMatchDecisions().Count, Is.EqualTo(2), "the loaded decisions did not survive a snapshot");
                Assert.That(settings.GetDecisionTimestampUtc("PcvrCentral||Half-Life||" + GameA.ToString("D")), Is.Not.Null,
                    "timestamps added through the getter were dropped");
            });

            // And a write after a load builds on what was loaded instead of replacing it.
            settings.SetDecision("PcvrCentral", "Half-Life", GameA, MatchDecision.Rejected);
            Assert.Multiple(() =>
            {
                Assert.That(settings.SnapshotMatchDecisions().Count, Is.EqualTo(2), "a write after a load lost the other loaded decision");
                Assert.That(settings.GetDecision("PcvrCentral", "Half-Life", GameA), Is.EqualTo(MatchDecision.Rejected));
                Assert.That(settings.GetDecision("PcvrCentral", "Portal", GameA), Is.EqualTo(MatchDecision.Rejected),
                    "the decision loaded from disk was overwritten by the new write");
                Assert.That(settings.GetDecisionTimestampUtc("PcvrCentral||Half-Life||" + GameA.ToString("D")), Is.Not.Null,
                    "the timestamp loaded from disk was lost when the decision was rewritten");
            });
        }

        [Test]
        public void Reset_replaces_the_published_maps_instead_of_clearing_ones_a_caller_holds()
        {
            var settings = new VrModTagSettings();
            settings.SetDecision("PcvrCentral", "Half-Life", GameA, MatchDecision.Accepted, "VRMod PCVR Port");
            var held = settings.MatchDecisions;

            settings.ClearAllDecisions();

            Assert.Multiple(() =>
            {
                Assert.That(held, Is.Not.Empty, "Reset cleared a map a caller was holding");
                Assert.That(settings.MatchDecisions, Is.Empty, "Reset did not clear the live decision map");
                Assert.That(settings.SourceTagsByDecision, Is.Empty, "Reset left a tag baseline behind");
                Assert.That(settings.SnapshotMatchDecisions(), Is.Empty);
            });
        }
    }
}
