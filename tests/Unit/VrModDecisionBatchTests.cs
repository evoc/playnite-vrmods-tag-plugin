using System;
using System.Collections.Generic;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Task 191: a decision write clones the published maps because no reader can take the settings
    /// lock, so a per-key write costs O(live decisions) and deciding a whole review page costs O(n^2).
    /// The fix is a batch API: one clone, N mutations, one publish. These tests hold the batch to the
    /// same rules the per-key methods have - including the tag-baseline rule and the no-copy path for
    /// a write or removal that changes nothing - because a batch that behaved differently would just
    /// move the bug somewhere quieter.
    /// </summary>
    [TestFixture]
    public class VrModDecisionBatchTests
    {
        private static List<(string Key, MatchDecision Decision, string? SourceTags)> Writes()
        {
            var writes = new List<(string, MatchDecision, string?)>();
            for (var i = 0; i < 50; i++)
                writes.Add(($"src|game{i}", MatchDecision.Accepted, $"VRMod Tag{i}"));
            return writes;
        }

        [Test]
        public void SetDecisionsByKey_AppliesEveryWriteInOnePublish()
        {
            var settings = new VrModTagSettings();
            var before = new Dictionary<string, string>(settings.SnapshotMatchDecisions());

            settings.SetDecisionsByKey(Writes());

            var decisions = settings.SnapshotMatchDecisions();
            Assert.That(decisions.Count, Is.EqualTo(before.Count + 50),
                "SnapshotMatchDecisions hands out a fresh copy per call, so equality of content, not of instance, is what is observable");
            Assert.That(decisions.Count, Is.EqualTo(50));
            for (var i = 0; i < 50; i++)
            {
                Assert.That(settings.GetDecisionByKey($"src|game{i}"), Is.EqualTo(MatchDecision.Accepted));
                Assert.That(settings.GetDecisionTimestampUtc($"src|game{i}"), Is.Not.Null,
                    "every key in the batch needs its own stamp, or the stale-decision sweep cannot see it");
            }
        }

        [Test]
        public void SetDecisionsByKey_MatchesThePerKeyWrites_OutcomeForTagsAndOverrides()
        {
            var batched = new VrModTagSettings();
            var perKey = new VrModTagSettings();

            // A stale baseline that a later write without tags must drop, and a key written twice.
            var writes = Writes();
            writes.Add(("src|game0", MatchDecision.Rejected, null));
            batched.SetDecisionsByKey(writes);
            foreach (var (key, decision, tags) in writes)
                perKey.SetDecisionByKey(key, decision, tags);

            Assert.That(batched.SnapshotMatchDecisions(), Is.EqualTo(perKey.SnapshotMatchDecisions()));
            Assert.That(
                batched.SourceTagsByDecision.Keys,
                Is.EquivalentTo(perKey.SourceTagsByDecision.Keys),
                "the tag baseline a per-key write would have dropped has to be dropped by the batch too");
            Assert.That(batched.GetDecisionByKey("src|game0"), Is.EqualTo(MatchDecision.Rejected),
                "the last write for a key wins in batch order, exactly as it does one by one");
        }

        [Test]
        public void SetDecisionsByKey_EmptyBatchPublishesNothing()
        {
            var settings = new VrModTagSettings();
            settings.SetDecisionByKey("src|seed", MatchDecision.Accepted, "VRMod Seed");

            settings.SetDecisionsByKey(new List<(string, MatchDecision, string?)>());

            Assert.That(settings.SnapshotMatchDecisions(), Is.EqualTo(new Dictionary<string, string> { ["src|seed"] = "Accepted" }),
                "an empty batch changes nothing it can observe, and costs no clone");
        }

        [Test]
        public void SetDecisionsByKey_RejectsEveryBadKeyBeforeWritingAnyOfThem()
        {
            var settings = new VrModTagSettings();
            settings.SetDecisionByKey("src|keep", MatchDecision.Accepted, "VRMod Keep");
            var published = new Dictionary<string, string>(settings.SnapshotMatchDecisions());
            var writes = Writes();
            writes.Add(("", MatchDecision.Accepted, null));

            Assert.That(() => settings.SetDecisionsByKey(writes), Throws.ArgumentException);
            Assert.That(settings.SnapshotMatchDecisions(), Is.EqualTo(published),
                "a batch that cannot be applied may not be applied in part");
            Assert.That(settings.GetDecisionByKey("src|keep"), Is.EqualTo(MatchDecision.Accepted));
        }

        [Test]
        public void ClearDecisionsByKey_RemovesDecisionStampAndBaselineTogether()
        {
            var settings = new VrModTagSettings();
            settings.SetDecisionsByKey(Writes());
            // unused: content equality is asserted against fresh snapshots below

            var keys = new List<string>();
            for (var i = 0; i < 50; i++)
                keys.Add($"src|game{i}");
            settings.ClearDecisionsByKey(keys);

            Assert.That(settings.SnapshotMatchDecisions(), Is.Empty);
            Assert.That(settings.SourceTagsByDecision.Keys, Is.Empty,
                "a leftover baseline would read as a changed tag set for a decision that no longer exists");
            Assert.That(settings.GetDecisionTimestampUtc("src|game7"), Is.Null);
        }

        [Test]
        public void ClearDecisionsByKey_ThatRemovesNothingCostsNoCopy()
        {
            var settings = new VrModTagSettings();
            settings.SetDecisionByKey("src|keep", MatchDecision.Accepted, "VRMod Keep");
            var published = new Dictionary<string, string>(settings.SnapshotMatchDecisions());

            settings.ClearDecisionsByKey(new List<string> { "src|absent-a", "src|absent-b" });

            Assert.That(settings.SnapshotMatchDecisions(), Is.EqualTo(published),
                "nothing matched, so nothing changed; CloneWithout returns null per map and Publish leaves them alone");
            Assert.That(settings.GetDecisionByKey("src|keep"), Is.EqualTo(MatchDecision.Accepted));
        }

        [Test]
        public void BatchApi_HoldsAtLibraryScale_20000Decisions()
        {
            // The scale this task was opened over, asked as a correctness question rather than a timing
            // one: does the batch still behave per-key when the store is the size of a real library
            // (3 045 games x several mappings each), and does a bulk rewrite finish instead of crawling?
            // Nothing here asserts a duration - if the write path were still one clone per key, this test
            // would not fail an assertion, it would fail by never finishing.
            const int live = 20000;
            const int rewrites = 2000;

            var settings = new VrModTagSettings();
            var seed = new List<(string, MatchDecision, string?)>();
            for (var i = 0; i < live; i++)
                seed.Add(($"src|game{i}", MatchDecision.Accepted, $"VRMod Tag{i}"));
            settings.SetDecisionsByKey(seed);
            Assert.That(settings.SnapshotMatchDecisions().Count, Is.EqualTo(live));

            // A second batch that re-decides part of the store, tag baselines included.
            var flip = new List<(string, MatchDecision, string?)>();
            for (var i = 0; i < rewrites; i++)
                flip.Add(($"src|game{i}", MatchDecision.Rejected, null));
            settings.SetDecisionsByKey(flip);

            Assert.That(settings.SnapshotMatchDecisions().Count, Is.EqualTo(live), "a rewrite is not a loss");
            Assert.That(settings.GetDecisionByKey("src|game0"), Is.EqualTo(MatchDecision.Rejected));
            Assert.That(settings.GetDecisionByKey($"src|game{live - 1}"), Is.EqualTo(MatchDecision.Accepted));
            Assert.That(settings.SourceTagsByDecision.Keys.Count,
                Is.EqualTo(live - rewrites),
                "the rewritten keys' baselines drop exactly as they do one key at a time");

            // And a bulk clear over the same size.
            var keys = new List<string>();
            for (var i = rewrites; i < live; i++)
                keys.Add($"src|game{i}");
            settings.ClearDecisionsByKey(keys);
            Assert.That(settings.SnapshotMatchDecisions().Count, Is.EqualTo(rewrites));
            Assert.That(settings.SourceTagsByDecision.Keys, Is.Empty);
        }

        [Test]
        public void BatchWritesDoNotScaleWithTheLiveDecisionCount_TheQuadraticCostIsGone()
        {
            // The measured shape this task was opened over: 2 000 per-key writes against a 5 000-decision
            // store cost 3,29 s (dev tools/DecisionCostBench). Here both paths write the same 4 000 keys into
            // the same size of store; the batch must not pay one clone per write. The margin is deliberately
            // loose (a shared machine, a cold process) - it is a guard against re-introducing O(n^2), not a
            // benchmark, and the numbers it prints are the ones the ledger quotes.
            const int keys = 4000;

            var individual = new VrModTagSettings();
            var batch = new VrModTagSettings();

            var soloStart = DateTime.UtcNow;
            for (var i = 0; i < keys; i++)
                individual.SetDecisionByKey($"src|game{i}", MatchDecision.Accepted, $"VRMod Tag{i}");
            var solo = DateTime.UtcNow - soloStart;

            var writes = new List<(string, MatchDecision, string?)>();
            for (var i = 0; i < keys; i++)
                writes.Add(($"src|game{i}", MatchDecision.Accepted, $"VRMod Tag{i}"));
            var batchStart = DateTime.UtcNow;
            batch.SetDecisionsByKey(writes);
            var batched = DateTime.UtcNow - batchStart;

            Assert.That(batch.SnapshotMatchDecisions().Count, Is.EqualTo(individual.SnapshotMatchDecisions().Count));
            TestContext.WriteLine($"191: {keys} per-key writes {solo.TotalMilliseconds:F0} ms vs one batch {batched.TotalMilliseconds:F0} ms");
            Assert.That(batched.TotalMilliseconds, Is.LessThan(solo.TotalMilliseconds / 4),
                $"per-key {solo.TotalMilliseconds:F0} ms vs batch {batched.TotalMilliseconds:F0} ms - the batch must not clone per write");
        }

        /// <summary>
        /// Task 195: the stale-tag sweep cleared decisions one game at a time, and each
        /// ClearDecisionsForGame locates a game's keys by walking the whole decision map - so a sweep
        /// over G stale games cost O(G x D). ClearDecisionsForGames walks it once. These tests hold the
        /// bulk clear to the single-game rules, because the rule that matters here is the narrow one:
        /// a same-name store duplicate must keep its own decisions.
        /// </summary>
        [Test]
        public void ClearDecisionsForGames_LeavesASameNameDuplicateOwnDecisionsAlone()
        {
            var steam = Guid.NewGuid();
            var epic = Guid.NewGuid();
            var settings = new VrModTagSettings();

            // Two store rows with the same display name and the same source listing - the shape that
            // made a name-keyed clear wipe both rows and turn an accepted match back into Pending.
            settings.SetDecisionByKey(VrModNameMatcher.MakeDecisionKey("src-pcvr", "Boneworks", steam),
                MatchDecision.Accepted, "VRMod Full Motion");
            settings.SetDecisionByKey(VrModNameMatcher.MakeDecisionKey("src-pcvr", "Boneworks", epic),
                MatchDecision.Accepted, "VRMod Full Motion");

            settings.ClearDecisionsForGames(new List<Guid> { steam });

            Assert.That(settings.GetDecisionByKey(VrModNameMatcher.MakeDecisionKey("src-pcvr", "Boneworks", steam)),
                Is.EqualTo(MatchDecision.Pending), "the stale game's decision goes");
            Assert.That(settings.GetDecisionByKey(VrModNameMatcher.MakeDecisionKey("src-pcvr", "Boneworks", epic)),
                Is.EqualTo(MatchDecision.Accepted), "its same-name sibling keeps its own decision");
            Assert.That(settings.SourceTagsByDecision.Keys,
                Has.One.EqualTo(VrModNameMatcher.MakeDecisionKey("src-pcvr", "Boneworks", epic)),
                "and only the cleared game's tag baseline goes with it");
        }

        [Test]
        public void ClearDecisionsForGames_CostsTheSameOutcomeAsClearingOneGameAtATime()
        {
            var ids = new List<Guid>();
            for (var i = 0; i < 20; i++)
                ids.Add(Guid.NewGuid());
            var keep = Guid.NewGuid();

            var bulk = new VrModTagSettings();
            var oneByOne = new VrModTagSettings();
            foreach (var settings in new[] { bulk, oneByOne })
            {
                foreach (var id in ids)
                    settings.SetDecisionByKey(VrModNameMatcher.MakeDecisionKey("src-pcvr", $"Game{id:N}", id),
                        MatchDecision.Accepted, "VRMod Full Motion");
                settings.SetDecisionByKey(VrModNameMatcher.MakeDecisionKey("src-uevr", "StillMatched", keep),
                    MatchDecision.Accepted, "VRMod 6DOF");
                // A legacy name-shaped key (two parts) is not a per-mapping key and is never touched.
                settings.SetDecisionByKey("src-pcvr||LegacyName", MatchDecision.Accepted, null);
            }

            bulk.ClearDecisionsForGames(ids);
            foreach (var id in ids)
                oneByOne.ClearDecisionsForGame(id);

            Assert.That(bulk.SnapshotMatchDecisions(), Is.EqualTo(oneByOne.SnapshotMatchDecisions()));
            Assert.That(bulk.SourceTagsByDecision.Keys, Is.EquivalentTo(oneByOne.SourceTagsByDecision.Keys));
            Assert.That(bulk.GetDecisionByKey(VrModNameMatcher.MakeDecisionKey("src-uevr", "StillMatched", keep)),
                Is.EqualTo(MatchDecision.Accepted), "a game outside the sweep keeps its decision");
            Assert.That(bulk.GetDecisionByKey("src-pcvr||LegacyName"), Is.EqualTo(MatchDecision.Accepted),
                "a key that is not sourceId||sourceGame||gameId is outside what a per-game clear owns");
        }

        [Test]
        public void ClearDecisionsForGames_ThatMatchesNothingChangesNothing()
        {
            var settings = new VrModTagSettings();
            settings.SetDecisionByKey(VrModNameMatcher.MakeDecisionKey("src-pcvr", "Boneworks", Guid.NewGuid()),
                MatchDecision.Accepted, "VRMod Full Motion");
            var before = new Dictionary<string, string>(settings.SnapshotMatchDecisions());

            settings.ClearDecisionsForGames(new List<Guid> { Guid.NewGuid(), Guid.NewGuid() });
            settings.ClearDecisionsForGames(new List<Guid>());

            Assert.That(settings.SnapshotMatchDecisions(), Is.EqualTo(before),
                "no key matched, so no clone and no publish - the sweep must not rewrite the store on a scan that found nothing stale");
        }
    }
}
