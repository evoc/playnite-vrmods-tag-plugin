using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="VrModTagPlugin.BuildAutoScanMessage"/> — the auto-scan
    /// (startup / scheduled timer) notification text. The message must be clear and
    /// actionable: it reports the pending match count (and how to get rid of it — click
    /// to review), a note on sources that returned no data, and how many already-accepted
    /// matches picked up tag changes since the last accept. When the scan found nothing to
    /// report (no new matches, no tag changes), it returns null and NO notification is
    /// posted at all. The old, confusing "0 games tagged, 377 matches pending review"
    /// wording is deliberately gone.
    /// </summary>
    [TestFixture]
    public class VrModAutoScanMessageTests
    {
        [Test]
        public void WithPending_ReturnsReviewMessage()
        {
            var result = VrModTagPlugin.BuildAutoScanMessage(377, 0, 0);

            Assert.That(result, Is.EqualTo("VRMod scan: 377 pending matches. Click to review."));
        }

        [Test]
        public void WithPendingAndNoData_AppendsNoDataSuffix()
        {
            var result = VrModTagPlugin.BuildAutoScanMessage(5, 2, 0);

            Assert.That(result, Is.EqualTo("VRMod scan: 5 pending matches. Click to review. 2 sources returned no data."));
        }

        [Test]
        public void WithSinglePending_StillSaysMatches()
        {
            // The count is always plural ("matches") regardless of how many — avoids the old
            // "match(es)" that wrapped between "match" and "(es)" in the notification.
            var result = VrModTagPlugin.BuildAutoScanMessage(1, 0, 0);

            Assert.That(result, Is.EqualTo("VRMod scan: 1 pending matches. Click to review."));
        }

        [Test]
        public void WithPending_NoMentionOfTaggedGames()
        {
            // The confusing "N games tagged" phrasing is gone: the pending count is what
            // matters, and "click to review" tells the user how to clear it.
            var result = VrModTagPlugin.BuildAutoScanMessage(377, 0, 0);

            Assert.That(result, Does.Not.Contain("tagged"));
            Assert.That(result, Does.Contain("Click to review"));
        }

        [Test]
        public void WithPendingAndChanged_AppendsChangedSuffix()
        {
            var result = VrModTagPlugin.BuildAutoScanMessage(5, 0, 3);

            Assert.That(result, Is.EqualTo("VRMod scan: 5 pending matches. Click to review. 3 accepted matches have tag changes."));
        }

        [Test]
        public void WithPendingNoDataAndChanged_AppendsBothSuffixes()
        {
            var result = VrModTagPlugin.BuildAutoScanMessage(5, 2, 3);

            Assert.That(result, Is.EqualTo("VRMod scan: 5 pending matches. Click to review. 2 sources returned no data. 3 accepted matches have tag changes."));
        }

        [Test]
        public void NoPendingNoChanges_ReturnsNoNotification()
        {
            // A scan that found no new matches and no tag changes reports nothing: null means
            // NO notification is posted at all (not even "no new matches").
            var result = VrModTagPlugin.BuildAutoScanMessage(0, 0, 0);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void NoPendingNoChangesNoDataOnly_ReturnsNoNotification()
        {
            // Sources that returned no data alone do not warrant a notification either — the
            // per-source last-run status in the settings view shows it.
            var result = VrModTagPlugin.BuildAutoScanMessage(0, 3, 0);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void NoPendingWithChanged_ReturnsChangedMessage()
        {
            // A scan with no new pending matches but tag changes on already-accepted matches
            // still reports the changes (that is the whole point of the changed count).
            var result = VrModTagPlugin.BuildAutoScanMessage(0, 0, 3);

            Assert.That(result, Is.EqualTo("VRMod scan complete: 3 accepted matches have tag changes."));
        }
    }
}
