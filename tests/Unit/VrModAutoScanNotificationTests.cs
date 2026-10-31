using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="VrModTagPlugin.ClassifyAutoScanClickTarget"/> — the auto-scan
    /// (startup / scheduled timer) notification's click target (EVERY notification
    /// gets a click handler). Match-related news wins: new pending matches open the review
    /// dialog for them, tag changes on already-accepted matches open the full review dialog
    /// (so the user can re-accept the changes), and only a scan with neither opens the
    /// plugin's settings view. The notification TEXT is covered by
    /// <see cref="VrModAutoScanMessageTests"/>; this fixture pins the click decision.
    /// </summary>
    [TestFixture]
    public class VrModAutoScanNotificationTests
    {
        // --- Pending matches → the review dialog for the pending matches -------------

        [Test]
        public void WithPendingMatches_OpensReviewDialog()
        {
            var target = VrModTagPlugin.ClassifyAutoScanClickTarget(377, 0);

            Assert.That(target, Is.EqualTo(VrModTagPlugin.VrModNotificationClickTarget.ReviewPendingMatches));
        }

        [Test]
        public void WithSinglePendingMatch_StillOpensReviewDialog()
        {
            var target = VrModTagPlugin.ClassifyAutoScanClickTarget(1, 0);

            Assert.That(target, Is.EqualTo(VrModTagPlugin.VrModNotificationClickTarget.ReviewPendingMatches));
        }

        // --- Changed accepted matches → the full review dialog (the task-17 gap) ------

        [Test]
        public void WithChangedAcceptedMatches_OpensFullReviewDialog()
        {
            // No pending matches, but accepted matches picked up tag changes: clicking opens
            // the review dialog (every matched game) so the user can re-accept the changes —
            // before task 17 this notification had no click action at all.
            var target = VrModTagPlugin.ClassifyAutoScanClickTarget(0, 3);

            Assert.That(target, Is.EqualTo(VrModTagPlugin.VrModNotificationClickTarget.ReviewAllMatches));
        }

        [Test]
        public void WithSingleChangedAcceptedMatch_OpensFullReviewDialog()
        {
            var target = VrModTagPlugin.ClassifyAutoScanClickTarget(0, 1);

            Assert.That(target, Is.EqualTo(VrModTagPlugin.VrModNotificationClickTarget.ReviewAllMatches));
        }

        // --- Pending AND changed → the pending review dialog wins ----------------------

        [Test]
        public void WithPendingAndChanged_OpensReviewDialogForPending()
        {
            // Both kinds of match-related news in one scan: the pending matches get the
            // review dialog for them (the changed accepted matches are still re-acceptable
            // from the settings "Review VR Mods" button).
            var target = VrModTagPlugin.ClassifyAutoScanClickTarget(5, 3);

            Assert.That(target, Is.EqualTo(VrModTagPlugin.VrModNotificationClickTarget.ReviewPendingMatches));
        }

        [Test]
        public void WithPendingAndChanged_SingleEach_OpensReviewDialogForPending()
        {
            var target = VrModTagPlugin.ClassifyAutoScanClickTarget(1, 1);

            Assert.That(target, Is.EqualTo(VrModTagPlugin.VrModNotificationClickTarget.ReviewPendingMatches));
        }
    }
}
