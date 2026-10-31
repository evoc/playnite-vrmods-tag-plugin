using System;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="VrModReviewDialog.ResetAllMatchStatuses"/> — the pure,
    /// plugin-free core of the review dialog's "Reset all decisions" button: every row's
    /// status is set back to Pending so all matches are presented for review again.
    /// </summary>
    [TestFixture]
    public class VrModReviewDialogResetTests
    {
        [Test]
        public void ResetsAllStatusesToPending()
        {
            var matches = new[]
            {
                new VrModReviewMatchEntry { Status = MatchDecision.Accepted },
                new VrModReviewMatchEntry { Status = MatchDecision.Rejected },
                new VrModReviewMatchEntry { Status = MatchDecision.Pending },
            };

            VrModReviewDialog.ResetAllMatchStatuses(matches);

            Assert.That(matches[0].Status, Is.EqualTo(MatchDecision.Pending));
            Assert.That(matches[1].Status, Is.EqualTo(MatchDecision.Pending));
            Assert.That(matches[2].Status, Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        public void EmptyList_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => VrModReviewDialog.ResetAllMatchStatuses(new VrModReviewMatchEntry[0]));
        }

        [Test]
        public void Reset_ReStampsDecidedAtUtcToNow()
        {
            var matches = new[]
            {
                new VrModReviewMatchEntry { Status = MatchDecision.Accepted, DecidedAtUtc = DateTime.MinValue },
                new VrModReviewMatchEntry { Status = MatchDecision.Rejected, DecidedAtUtc = DateTime.MinValue },
            };

            var before = DateTime.UtcNow;
            VrModReviewDialog.ResetAllMatchStatuses(matches);
            var after = DateTime.UtcNow;

            // Both rows are re-stamped to "just now" (within the call window), so they sort
            // to the top of the time filter after a reset. The old statuses are cleared too.
            Assert.That(matches[0].DecidedAtUtc, Is.InRange(before, after));
            Assert.That(matches[1].DecidedAtUtc, Is.InRange(before, after));
            Assert.That(matches[0].Status, Is.EqualTo(MatchDecision.Pending));
            Assert.That(matches[1].Status, Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        public void Reset_ClearsPersistedDecisionAndSavedAtUtc()
        {
            var savedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var matches = new[]
            {
                new VrModReviewMatchEntry
                {
                    Status = MatchDecision.Accepted,
                    PersistedDecision = MatchDecision.Accepted,
                    SavedAtUtc = savedAt
                },
                new VrModReviewMatchEntry
                {
                    Status = MatchDecision.Rejected,
                    PersistedDecision = MatchDecision.Rejected,
                    SavedAtUtc = savedAt
                },
            };

            VrModReviewDialog.ResetAllMatchStatuses(matches);

            // Reset reverts every row to a fresh, unsaved Pending: no saved decision remains,
            // so the "Saved at" column and saved status all clear too.
            Assert.That(matches[0].Status, Is.EqualTo(MatchDecision.Pending));
            Assert.That(matches[0].PersistedDecision, Is.Null);
            Assert.That(matches[0].SavedAtUtc, Is.Null);
            Assert.That(matches[1].Status, Is.EqualTo(MatchDecision.Pending));
            Assert.That(matches[1].PersistedDecision, Is.Null);
            Assert.That(matches[1].SavedAtUtc, Is.Null);
        }
    }
}
