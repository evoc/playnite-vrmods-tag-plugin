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
    /// The persisted match decisions live in a plain Dictionary and are touched from two threads:
    /// the review dialog writes them on Playnite's UI thread, a scan writes them when a game stops
    /// matching, and the settings page, the statistics view and the uninstall cleanup all read them.
    /// Enumerating a Dictionary while another thread adds or removes a key throws
    /// InvalidOperationException, and in a plugin that surfaces as a settings page that crashes.
    /// These tests hold the two guarantees that let the accessors be used at all: reads go through a
    /// private copy, and the per-game lookup answers what the caller used to get by scanning the
    /// whole catalog.
    /// </summary>
    [TestFixture]
    public class VrModSettingsDecisionSyncTests
    {
        private static readonly Guid GameA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid GameB = Guid.Parse("22222222-2222-2222-2222-222222222222");

        [Test]
        public void SnapshotMatchDecisions_IsAPrivateCopy()
        {
            var settings = new VrModTagSettings();
            var skyrim = VrModNameMatcher.MakeDecisionKey("src", "Skyrim", GameA);
            var fallout = VrModNameMatcher.MakeDecisionKey("src", "Fallout 4", GameA);
            settings.SetDecision("src", "Skyrim", GameA, MatchDecision.Accepted);

            var snapshot = settings.SnapshotMatchDecisions();
            settings.SetDecision("src", "Fallout 4", GameA, MatchDecision.Rejected);

            Assert.That(snapshot.Keys, Is.EquivalentTo(new[] { skyrim }),
                "the snapshot tracked the live dictionary instead of standing still");

            snapshot.Remove(skyrim);
            Assert.That(settings.MatchDecisions.Keys, Is.EquivalentTo(new[] { skyrim, fallout }),
                "the snapshot shared the store's dictionary: removing from the copy removed a saved decision");
        }

        /// <summary>
        /// The crash this class exists to prevent: a scan thread recording/clearing decisions for
        /// the games it just re-evaluated while the settings page walks the decisions to render its
        /// per-source counts. A plain Dictionary throws InvalidOperationException ("Collection was
        /// modified") on exactly this interleaving, and the tests would pass without the lock only by
        /// luck of timing - so the writer runs long enough that a regression cannot hide.
        ///
        /// Volume is chosen for interleaving DENSITY, not throughput. Every write publishes a fresh
        /// copy of the map (copy-on-write since Round 31, `WritableCopyOf`), so a write costs O(live
        /// decisions) and this test used to cost O(writes x decisions): with 40 000 writes over a
        /// 2 000-key store it measured 3 m 03 s run alone and died on its own 3-minute deadline, which
        /// is a flake with a cause (green only when the box happened to be fast enough). 300 stored
        /// decisions and 6 000 writes still put a mutation inside every reader's enumeration many times
        /// over, and a plain Dictionary fails that within the first rounds - the guarantee is what is
        /// being tested, not how fast the plugin can clone a dictionary.
        /// </summary>
        [Test]
        public void SnapshotAndPerGameReads_SurviveAConcurrentWriter()
        {
            const int InitialDecisions = 300;
            const int WriterIterations = 6000;
            const int ReaderRounds = 250;

            var settings = new VrModTagSettings();
            for (var i = 0; i < InitialDecisions; i++)
                settings.SetDecision("src", $"Entry {i}", GameB, MatchDecision.Accepted);

            var failures = new List<Exception>();
            var stop = false;
            var gameIds = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToArray();
            foreach (var id in gameIds)
                settings.SetDecision("src", "Entry 0", id, MatchDecision.Rejected);

            var writerDone = 0;
            var writer = Task.Run(() =>
            {
                try
                {
                    var rnd = new Random(7);
                    for (var i = 0; i < WriterIterations && !Volatile.Read(ref stop); i++)
                    {
                        var id = gameIds[rnd.Next(gameIds.Length)];
                        settings.SetDecision("src", $"Entry {rnd.Next(InitialDecisions)}", id, MatchDecision.Accepted);
                        if (i % 7 == 0) settings.ClearDecisionsForGame(id);
                        if (i % 11 == 0) settings.ClearDecision("src", "Entry 0", id);
                        Volatile.Write(ref writerDone, i + 1);
                    }
                }
                catch (Exception ex) { lock (failures) failures.Add(ex); }
            });

            var roundsDone = new int[3];
            var readers = Enumerable.Range(0, 3).Select(slot => Task.Run(() =>
            {
                try
                {
                    for (var round = 0; round < ReaderRounds && !Volatile.Read(ref stop); round++)
                    {
                        var snapshot = settings.SnapshotMatchDecisions();
                        foreach (var kvp in snapshot) { Assert.That(kvp.Value, Is.Not.Null); }
                        foreach (var id in gameIds)
                        {
                            Assert.That(settings.GetDecisionsForGame(id).Count, Is.GreaterThanOrEqualTo(0));
                            Assert.That(settings.GetDecision("src", "Entry 0", id), Is.Not.Null);
                        }
                        Volatile.Write(ref roundsDone[slot], round + 1);
                    }
                }
                catch (Exception ex) { lock (failures) failures.Add(ex); }
            })).ToArray();

            Assert.That(Task.WaitAll(readers.Concat(new[] { writer }).ToArray(), TimeSpan.FromMinutes(2)), Is.True,
                $"a reader or writer never finished (writer got to {writerDone}/{WriterIterations}, " +
                $"readers to {string.Join("/", roundsDone.Select(r => r + "/" + ReaderRounds))}), " +
                "which is the other way a lock problem shows up");
            Volatile.Write(ref stop, true);

            // The work has to have actually been done, or "no exception" proves nothing: a test that
            // passes because both sides bailed early would be green forever.
            Assert.That(writerDone, Is.EqualTo(WriterIterations), "the writer stopped early");
            Assert.That(roundsDone, Is.All.EqualTo(ReaderRounds), "a reader stopped early");
            Assert.That(failures, Is.Empty,
                "decision maps were read while another thread wrote them: " +
                string.Join("; ", failures.Select(f => f.GetType().Name + ": " + f.Message).Take(3)));
        }

        /// <summary>
        /// Reconciling a game's links needs every decision that points at that game. This is the
        /// accessor that replaced "ask every cached catalog entry whether it has a decision", so its
        /// answer has to be exactly the decisions that query would have found: this game's only,
        /// settled ones included, legacy name-keyed records and corrupt values excluded.
        /// </summary>
        [Test]
        public void GetDecisionsForGame_ReturnsThatGamesDecisionsAndNothingElses()
        {
            var settings = new VrModTagSettings();
            settings.SetDecision("gr", "BoneTomahawk VR", GameA, MatchDecision.Accepted);
            settings.SetDecision("dlc", "Skyrim - Falselight", GameA, MatchDecision.Rejected);
            settings.SetDecision("nvrm", "Skyrim VR", GameA, MatchDecision.Pending);
            settings.SetDecision("gr", "BoneTomahawk VR", GameB, MatchDecision.Accepted);

            // A pre-per-mapping record (two segments) and a corrupt value: neither used to produce
            // a decision through GetDecision, and neither may produce one here either.
            settings.MatchDecisions["gr||BoneTomahawk VR"] = "Accepted";
            settings.MatchDecisions["dlc||Broken Game||" + GameA.ToString("D")] = "Maybe";

            var forA = settings.GetDecisionsForGame(GameA);
            Assert.That(forA.Select(d => (d.SourceId, d.SourceGame, d.Decision)),
                Is.EquivalentTo(new[]
                {
                    ("gr", "BoneTomahawk VR", MatchDecision.Accepted),
                    ("dlc", "Skyrim - Falselight", MatchDecision.Rejected),
                    ("nvrm", "Skyrim VR", MatchDecision.Pending)
                }),
                "the decisions reported for a game are not the decisions stored for it");

            Assert.That(settings.GetDecisionsForGame(GameB).Select(d => (d.SourceId, d.SourceGame)),
                Is.EquivalentTo(new[] { ("gr", "BoneTomahawk VR") }),
                "another game's decisions leaked into this one");

            Assert.That(settings.GetDecisionsForGame(Guid.NewGuid()), Is.Empty,
                "a game with no stored decision reported one");
        }

        /// <summary>
        /// Clearing a game's decisions and listing them must agree on which records belong to the
        /// game, or a link reconcile would keep owning links the settings page just deleted (or
        /// refuse to drop links that are still saved).
        /// </summary>
        [Test]
        public void ClearDecisionsForGame_AndGetDecisionsForGame_AgreeOnOwnership()
        {
            var settings = new VrModTagSettings();
            settings.SetDecision("gr", "BoneTomahawk VR", GameA, MatchDecision.Accepted);
            settings.SetDecision("dlc", "BoneTomahawk DLC", GameA, MatchDecision.Rejected);
            settings.SetDecision("gr", "BoneTomahawk VR", GameB, MatchDecision.Accepted);

            settings.ClearDecisionsForGame(GameA);

            Assert.That(settings.GetDecisionsForGame(GameA), Is.Empty);
            Assert.That(settings.GetDecisionsForGame(GameB).Select(d => d.SourceId),
                Is.EquivalentTo(new[] { "gr" }),
                "clearing one game took another game's decision with it");
            Assert.That(settings.SnapshotMatchDecisions().Count, Is.EqualTo(1));
        }

        /// <summary>
        /// A decision key whose stored value is not a decision name must behave like "no decision",
        /// both through the old single lookup and through the per-game listing - not throw, and not
        /// claim an unparseable record is a settled match.
        /// </summary>
        [Test]
        public void CorruptStoredValuesDegradeToNoDecision()
        {
            var settings = new VrModTagSettings();
            settings.MatchDecisions[VrModNameMatcher.MakeDecisionKey("gr", "BoneTomahawk VR", GameA)] = "Perhaps";

            Assert.That(settings.GetDecision("gr", "BoneTomahawk VR", GameA), Is.EqualTo(MatchDecision.Pending));
            Assert.That(settings.GetDecisionsForGame(GameA), Is.Empty);
        }
    }
}
