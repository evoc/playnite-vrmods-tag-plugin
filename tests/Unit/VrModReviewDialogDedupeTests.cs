using NUnit.Framework;
using Playnite.SDK.Models;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="VrModReviewDialog.DedupeByGame"/> — collapsing review rows
    /// that point at the same (source, category, Playnite game) triple so each is shown once.
    /// Only the SAME source + category + SAME game row (reached through several library
    /// entries) is collapsed to its highest-scoring match. TWO DIFFERENT sources matching one
    /// game stay as separate rows (each keeps its own deep link / open-mod-page button); the
    /// SAME source listing one game under a DIFFERENT category also stays separate, so all of
    /// a source's tags for a game show up. The game part of the key is the game Id, NOT the
    /// name — store duplicates (two game rows with the same name, same source, same category)
    /// are NOT collapsed, so each duplicate can be decided independently.
    /// </summary>
    [TestFixture]
    public class VrModReviewDialogDedupeTests
    {
        private static VrModReviewMatchEntry Row(Game game, float score, string source, string category = "UEVR")
        {
            return new VrModReviewMatchEntry
            {
                SourceId = source,
                SourceName = source,
                Category = category,
                PlayniteGame = game,
                Score = score,
            };
        }

        [Test]
        public void SameGameTwoEntries_KeepsHighestScore()
        {
            var game = new Game("Mass Effect 2");
            var low = Row(game, 0.7f, "Flat2VR");
            var high = Row(game, 0.95f, "Flat2VR");

            var result = VrModReviewDialog.DedupeByGame(new[] { low, high });

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Score, Is.EqualTo(0.95f));
        }

        [Test]
        public void StoreDuplicates_SameNameSameSourceSameCategory_BothKept()
        {
            // Two game rows with the same name from two stores — the regression: the old
            // name-keyed dedupe collapsed these into one row, so the user could only decide
            // one duplicate and the other stayed Pending (re-appearing every scan).
            var steam = new Game("Alien: Isolation");
            var epic = new Game("Alien: Isolation");
            var a = Row(steam, 1.0f, "Flat2VR");
            var b = Row(epic, 1.0f, "Flat2VR");

            var result = VrModReviewDialog.DedupeByGame(new[] { a, b });

            Assert.That(result, Has.Count.EqualTo(2),
                "store duplicates must stay separate rows so each gets its own decision");
        }

        [Test]
        public void DifferentSources_SameGame_BothKept()
        {
            var game = new Game("Mass Effect 2");
            var a = Row(game, 0.7f, "Flat2VR");
            var b = Row(game, 0.95f, "RototRobot");

            var result = VrModReviewDialog.DedupeByGame(new[] { a, b });

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result, Has.Exactly(1).Property("SourceId").EqualTo("Flat2VR"));
            Assert.That(result, Has.Exactly(1).Property("SourceId").EqualTo("RototRobot"));
        }

        [Test]
        public void SameSource_DifferentCategories_BothKept()
        {
            var game = new Game("Mass Effect 2");
            var uevr = Row(game, 0.7f, "Flat2VR", category: "UEVR");
            var gesture = Row(game, 0.95f, "Flat2VR", category: "Motion Gesture");

            var result = VrModReviewDialog.DedupeByGame(new[] { uevr, gesture });

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result, Has.Exactly(1).Property("Category").EqualTo("UEVR"));
            Assert.That(result, Has.Exactly(1).Property("Category").EqualTo("Motion Gesture"));
        }

        [Test]
        public void DifferentNames_AllKept()
        {
            var a = Row(new Game("Game A"), 0.9f, "A");
            var b = Row(new Game("Game B"), 0.8f, "B");

            var result = VrModReviewDialog.DedupeByGame(new[] { a, b });

            Assert.That(result, Has.Count.EqualTo(2));
        }

        [Test]
        public void NullGame_PassesThrough()
        {
            var e = new VrModReviewMatchEntry { SourceName = "X", Score = 0.5f };

            var result = VrModReviewDialog.DedupeByGame(new[] { e });

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].PlayniteGame, Is.Null);
        }

        [Test]
        public void SingleRow_Unchanged()
        {
            var e = Row(new Game("Only"), 0.9f, "A");

            var result = VrModReviewDialog.DedupeByGame(new[] { e });

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].PlayniteGame.Name, Is.EqualTo("Only"));
        }

        [Test]
        public void EmptyInput_EmptyResult()
        {
            var result = VrModReviewDialog.DedupeByGame(new VrModReviewMatchEntry[0]);

            Assert.That(result, Is.Empty);
        }
    }
}
