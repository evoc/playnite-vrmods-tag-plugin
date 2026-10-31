using System;
using NUnit.Framework;
using Playnite.SDK.Models;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="VrModReviewDialog.MatchFilterText"/> — the review dialog's
    /// filter predicate. A row is visible when the filter is empty (show all) or when the
    /// text is found (case-insensitive) in any of the row's columns.
    /// </summary>
    [TestFixture]
    public class VrModReviewDialogFilterTests
    {
        private static VrModReviewMatchEntry Make(string sourceName, string sourceGame, string playniteGame, string category)
        {
            return new VrModReviewMatchEntry
            {
                SourceId = sourceName,
                SourceName = sourceName,
                SourceGameName = sourceGame,
                PlayniteGame = new Game(playniteGame),
                Category = category
            };
        }

        [Test]
        public void EmptyFilter_ShowsAll()
        {
            var m = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");

            Assert.That(VrModReviewDialog.MatchFilterText(m, string.Empty), Is.True);
        }

        [Test]
        public void MatchInSourceName_Visible()
        {
            var m = Make("UeVR Profiles", "Skyrim", "Skyrim SE", "UEVR");

            Assert.That(VrModReviewDialog.MatchFilterText(m, "uevr profiles"), Is.True);
        }

        [Test]
        public void MatchInSourceGameName_Visible()
        {
            var m = Make("UeVR", "Elder Scrolls V", "Skyrim SE", "UEVR");

            Assert.That(VrModReviewDialog.MatchFilterText(m, "scrolls"), Is.True);
        }

        [Test]
        public void MatchInPlayniteGameName_Visible()
        {
            var m = Make("UeVR", "Skyrim", "The Elder Scrolls V: Skyrim SE", "UEVR");

            Assert.That(VrModReviewDialog.MatchFilterText(m, "elder scrolls"), Is.True);
        }

        [Test]
        public void MatchInCategory_Visible()
        {
            var m = Make("UeVR", "Skyrim", "Skyrim SE", "Flat2VR Port");

            Assert.That(VrModReviewDialog.MatchFilterText(m, "flat2vr"), Is.True);
        }

        [Test]
        public void MatchInStatus_Visible()
        {
            var m = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            m.Status = MatchDecision.Accepted;

            Assert.That(VrModReviewDialog.MatchFilterText(m, "accepted"), Is.True);
        }

        [Test]
        public void MatchInTagPreviewVia6DOF_Visible()
        {
            var m = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            m.Is6DOF = true;

            Assert.That(VrModReviewDialog.MatchFilterText(m, "6dof"), Is.True);
        }

        [Test]
        public void CaseInsensitive_Matches()
        {
            var m = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");

            Assert.That(VrModReviewDialog.MatchFilterText(m, "SKYRIM"), Is.True);
        }

        [Test]
        public void NoMatch_Hidden()
        {
            var m = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");

            Assert.That(VrModReviewDialog.MatchFilterText(m, "nonexistent-zzz"), Is.False);
        }

        [Test]
        public void FilterDebounce_IsAReasonableTypingPause()
        {
            // Pins the typing-pause window the filter box waits before (re)applying the filter,
            // so a large match list is not re-filtered/re-rendered on every keystroke. The
            // window must be long enough to coalesce a burst of keystrokes but short enough
            // to still feel instant.
            Assert.That(VrModReviewDialog.FilterDebounceMs, Is.InRange(50, 1000).And.EqualTo(250));
        }

        // --- Filter clear button (ShouldShowFilterClear) ---------------------------------

        [Test]
        public void FilterClearButton_EmptyText_Hidden()
        {
            // An empty box has nothing to clear, so the clear button is hidden (Playnite's main
            // filter only shows its clear icon once there is text).
            Assert.That(VrModReviewDialog.ShouldShowFilterClear(string.Empty), Is.False);
        }

        [Test]
        public void FilterClearButton_HasText_Shown()
        {
            Assert.That(VrModReviewDialog.ShouldShowFilterClear("skyrim"), Is.True);
        }

        [Test]
        public void FilterClearButton_WhitespaceOnlyText_Shown()
        {
            // Matches Playnite's main filter (IsNullOrEmpty, not IsNullOrWhiteSpace): a space is
            // text the user typed, so the clear button shows.
            Assert.That(VrModReviewDialog.ShouldShowFilterClear(" "), Is.True);
        }

        // --- Time filter (MatchTimeFilter) ------------------------------------------------

        [Test]
        public void TimeFilter_AlwaysWindow_ShowsAll()
        {
            var m = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");

            // "Always" (a negative window) shows everything, even with no recorded time.
            Assert.That(VrModReviewDialog.MatchTimeFilter(m, -1, DateTime.UtcNow), Is.True);
        }

        [Test]
        public void TimeFilter_NoTimestamp_HiddenUnderWindow()
        {
            var m = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            // DecidedAtUtc defaults to DateTime.MinValue (no recorded time).

            // Hidden under any bounded window.
            Assert.That(VrModReviewDialog.MatchTimeFilter(m, 3600, DateTime.UtcNow), Is.False);
        }

        [Test]
        public void TimeFilter_WithinWindow_Visible()
        {
            var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
            var m = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            m.DecidedAtUtc = now.AddHours(-1); // one hour ago

            // Exactly at the 1-hour boundary (inclusive).
            Assert.That(VrModReviewDialog.MatchTimeFilter(m, 3600, now), Is.True);
            // Comfortably inside a 1-day window.
            Assert.That(VrModReviewDialog.MatchTimeFilter(m, 86400, now), Is.True);
        }

        [Test]
        public void TimeFilter_OutsideWindow_Hidden()
        {
            var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
            var m = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            m.DecidedAtUtc = now.AddHours(-2); // two hours ago

            // Outside a 1-hour window, inside a 1-day window.
            Assert.That(VrModReviewDialog.MatchTimeFilter(m, 3600, now), Is.False);
            Assert.That(VrModReviewDialog.MatchTimeFilter(m, 86400, now), Is.True);
        }

        [Test]
        public void TimeFilter_FutureTimestamp_Visible()
        {
            var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
            var m = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            m.DecidedAtUtc = now.AddMinutes(5); // slightly in the future (clock skew)

            // A future timestamp is still "within" the window (>= cutoff).
            Assert.That(VrModReviewDialog.MatchTimeFilter(m, 3600, now), Is.True);
        }

        [Test]
        public void ChangedFilterOff_ShowsAll()
        {
            // Filter off (changedOnly=false): every row is visible, changed or not.
            var changed = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            changed.TagsChanged = true;
            var unchanged = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");

            Assert.That(VrModReviewDialog.MatchChangedFilter(changed, false), Is.True);
            Assert.That(VrModReviewDialog.MatchChangedFilter(unchanged, false), Is.True);
        }

        [Test]
        public void ChangedFilterOn_ShowsOnlyChanged()
        {
            // Filter on (changedOnly=true): only rows whose tags changed since the last
            // accept are visible.
            var changed = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            changed.TagsChanged = true;
            var unchanged = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");

            Assert.That(VrModReviewDialog.MatchChangedFilter(changed, true), Is.True);
            Assert.That(VrModReviewDialog.MatchChangedFilter(unchanged, true), Is.False);
        }

        // --- Status filter (MatchStatusFilter) -------------------------------------------

        [Test]
        public void StatusFilter_NothingSelected_ShowsAll()
        {
            // An empty selection means "show all".
            var pending = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            var accepted = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            accepted.Status = MatchDecision.Accepted;

            Assert.That(VrModReviewDialog.MatchStatusFilter(pending, new MatchDecision[0]), Is.True);
            Assert.That(VrModReviewDialog.MatchStatusFilter(accepted, new MatchDecision[0]), Is.True);
        }

        [Test]
        public void StatusFilter_PendingOnly_HidesOtherStatuses()
        {
            var pending = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR"); // default Status = Pending
            var accepted = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            accepted.Status = MatchDecision.Accepted;
            var rejected = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            rejected.Status = MatchDecision.Rejected;

            var filter = new[] { MatchDecision.Pending };
            Assert.That(VrModReviewDialog.MatchStatusFilter(pending, filter), Is.True);
            Assert.That(VrModReviewDialog.MatchStatusFilter(accepted, filter), Is.False);
            Assert.That(VrModReviewDialog.MatchStatusFilter(rejected, filter), Is.False);
        }

        [Test]
        public void StatusFilter_MultiSelect_IsAUnion()
        {
            var pending = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            var accepted = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            accepted.Status = MatchDecision.Accepted;
            var rejected = Make("UeVR", "Skyrim", "Skyrim SE", "UEVR");
            rejected.Status = MatchDecision.Rejected;

            // Pending + Rejected selected: both visible, accepted hidden.
            var filter = new[] { MatchDecision.Pending, MatchDecision.Rejected };
            Assert.That(VrModReviewDialog.MatchStatusFilter(pending, filter), Is.True);
            Assert.That(VrModReviewDialog.MatchStatusFilter(accepted, filter), Is.False);
            Assert.That(VrModReviewDialog.MatchStatusFilter(rejected, filter), Is.True);
        }
    }
}
