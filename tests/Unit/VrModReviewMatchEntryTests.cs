using System;
using System.Collections.Generic;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="VrModReviewMatchEntry.TagPreview"/> — the review dialog's
    /// "Tags" column, which previews the exact tags that would be applied on accept.
    /// </summary>
    [TestFixture]
    public class VrModReviewMatchEntryTests
    {
        [Test]
        public void TagPreview_CategoryOnly_ShowsCategoryTag()
        {
            var entry = new VrModReviewMatchEntry { Category = "UEVR" };

            Assert.That(entry.TagPreview, Is.EqualTo("VRMod  |  VRMod UEVR"));
        }

        [Test]
        public void TagPreview_With6DOF_Adds6DOFTag()
        {
            var entry = new VrModReviewMatchEntry { Category = "UEVR", Is6DOF = true };

            Assert.That(entry.TagPreview, Is.EqualTo("VRMod  |  VRMod UEVR  |  VRMod 6DOF"));
        }

        [Test]
        public void TagPreview_WithQuality_AddsQualityTag()
        {
            var entry = new VrModReviewMatchEntry { Category = "Flat2VR Port", Quality = "Playable" };

            Assert.That(entry.TagPreview, Is.EqualTo("VRMod  |  VRMod Flat2VR Port  |  VRMod Playable"));
        }

        [Test]
        public void TagPreview_With6DOFAndQuality_AddsBothFacets()
        {
            var entry = new VrModReviewMatchEntry { Category = "UEVR", Is6DOF = true, Quality = "Playable" };

            Assert.That(entry.TagPreview, Is.EqualTo("VRMod  |  VRMod UEVR  |  VRMod 6DOF  |  VRMod Playable"));
        }

        [Test]
        public void TagPreview_No6DOFAndNoQuality_OnlyCategory()
        {
            var entry = new VrModReviewMatchEntry { Category = "Full Motion Controls", Is6DOF = false, Quality = null };

            Assert.That(entry.TagPreview, Is.EqualTo("VRMod  |  VRMod Full Motion"));
        }

        [Test]
        public void TagPreview_EmptyQuality_NotAdded()
        {
            var entry = new VrModReviewMatchEntry { Category = "UEVR", Quality = string.Empty };

            Assert.That(entry.TagPreview, Is.EqualTo("VRMod  |  VRMod UEVR"));
        }

        [Test]
        public void TagPreview_CatalogCategory_ShowsOnlyUmbrella()
        {
            // A catalog category (PCVR Central / PCVR Mods Hub / Mr Survivor) contributes no
            // category tag of its own - the umbrella alone carries the value, and the source
            // name + link carry the provenance. AppliedTagNames (the changed-tag baseline)
            // is empty for a catalog, so a catalog row never shows a spurious "changed".
            var entry = new VrModReviewMatchEntry { Category = VrModCategories.PcvrCentral };

            Assert.That(entry.TagPreview, Is.EqualTo("VRMod"));
            Assert.That(entry.AppliedTagNames, Is.Empty);
        }

        // --- Inline feature-change indicator (TagPreviewWithMarker / TagDeltaTooltip) ------
        //
        // #146: surface a source feature change (e.g. 6DOF added since the last accept)
        // directly on the Tags cell - an "up arrow" marker + italic (the DataTrigger)
        // driven by TagsChanged, with the delta as the cell tooltip. These reuse the
        // set-once TagsChanged / TagDeltaText / TagPreview members from the scan.

        [Test]
        public void TagPreviewWithMarker_Unchanged_EqualsTagPreview()
        {
            // No change (TagsChanged defaults to false): the marker is not appended, so the
            // Tags cell shows exactly the preview (unchanged rows look as before).
            var entry = new VrModReviewMatchEntry { Category = "UEVR", Is6DOF = true };

            Assert.That(entry.TagPreviewWithMarker, Is.EqualTo(entry.TagPreview));
            Assert.That(entry.TagPreviewWithMarker, Does.Not.Contain("\u2191"));
        }

        [Test]
        public void TagPreviewWithMarker_Changed_AppendsMarker()
        {
            // A changed row: the marker "  ↑" is appended after the preview, so the Tags
            // cell itself flags the change (visible even if the "Changed" column is hidden).
            var entry = new VrModReviewMatchEntry { Category = "UEVR", Is6DOF = true };
            entry.TagsChanged = true;

            Assert.That(entry.TagPreviewWithMarker, Is.EqualTo(entry.TagPreview + "  \u2191"));
        }

        [Test]
        public void TagPreviewWithMarker_ChangedMarkerIsAtTheEnd()
        {
            // The marker is a trailing marker on the tag list, not interleaved with the tags,
            // so the tag names themselves remain intact for reading / searching.
            var entry = new VrModReviewMatchEntry { Category = "Flat2VR Port", Quality = "Playable" };
            entry.TagsChanged = true;

            Assert.That(entry.TagPreviewWithMarker, Does.StartWith(entry.TagPreview));
            Assert.That(entry.TagPreviewWithMarker.EndsWith("\u2191"), Is.True);
        }

        [Test]
        public void TagDeltaTooltip_Unchanged_IsNull()
        {
            // No change: no tooltip (null so WPF does not show an empty tooltip bubble).
            var entry = new VrModReviewMatchEntry { Category = "UEVR" };

            Assert.That(entry.TagDeltaTooltip, Is.Null);
        }

        [Test]
        public void TagDeltaTooltip_Changed_IncludesDeltaWithPrefix()
        {
            // A changed row: the tooltip is a self-explanatory prefix + the same delta the
            // "Changed" column shows (TagDeltaText).
            var entry = new VrModReviewMatchEntry { Category = "UEVR" };
            entry.TagsChanged = true;
            entry.TagDeltaText = "+ VRMod 6DOF";

            Assert.That(entry.TagDeltaTooltip, Is.EqualTo("Changed since you saved this: + VRMod 6DOF"));
        }

        [Test]
        public void TagDeltaTooltip_Changed_MultipleTags_IncludesBothSections()
        {
            // A row that both gained and lost a tag: the tooltip carries the full two-section
            // delta (added + removed), matching the "Changed" column text.
            var entry = new VrModReviewMatchEntry { Category = "UEVR" };
            entry.TagsChanged = true;
            entry.TagDeltaText = "+ VRMod 6DOF  - VRMod First Person";

            Assert.That(entry.TagDeltaTooltip, Is.EqualTo("Changed since you saved this: + VRMod 6DOF  - VRMod First Person"));
        }

        [Test]
        public void StatusSortRank_Pending_IsZero()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Pending };

            Assert.That(entry.StatusSortRank, Is.EqualTo(0));
        }

        [Test]
        public void StatusSortRank_Rejected_IsOne()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Rejected };

            Assert.That(entry.StatusSortRank, Is.EqualTo(1));
        }

        [Test]
        public void StatusSortRank_Accepted_IsTwo()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Accepted };

            Assert.That(entry.StatusSortRank, Is.EqualTo(2));
        }

        [Test]
        public void StatusSortRank_ReviewOrderIsPendingThenRejectedThenAccepted()
        {
            // The review sort must put pending first and accepted last, which differs from
            // the enum declaration order (Pending, Accepted, Rejected). The rank is what the
            // default review-dialog sort orders by ascending.
            var pending = new VrModReviewMatchEntry { Status = MatchDecision.Pending };
            var rejected = new VrModReviewMatchEntry { Status = MatchDecision.Rejected };
            var accepted = new VrModReviewMatchEntry { Status = MatchDecision.Accepted };

            Assert.That(pending.StatusSortRank, Is.LessThan(rejected.StatusSortRank));
            Assert.That(rejected.StatusSortRank, Is.LessThan(accepted.StatusSortRank));
        }

        [Test]
        public void Status_Set_RaisesPropertyChanged()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Pending };
            var raised = new List<string?>();
            entry.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            entry.Status = MatchDecision.Accepted;

            // Setting Status also raises for the derived display members, so the status column
            // text, the italic marker, and the "Saved at" column all refresh in one change.
            Assert.That(raised, Is.EqualTo(new[]
            {
                nameof(VrModReviewMatchEntry.Status),
                nameof(VrModReviewMatchEntry.DisplayStatusText),
                nameof(VrModReviewMatchEntry.IsUnsaved),
                nameof(VrModReviewMatchEntry.SavedAtDisplay),
            }));
        }

        [Test]
        public void Status_SetSameValue_DoesNotRaise()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Pending };
            var raised = 0;
            entry.PropertyChanged += (s, e) => raised++;

            entry.Status = MatchDecision.Pending;

            Assert.That(raised, Is.Zero);
        }

        [Test]
        public void DecidedAtUtc_DefaultsToMinValue()
        {
            var entry = new VrModReviewMatchEntry();

            // No recorded decision time: treated as the oldest by the time filter
            // (only visible under "Always").
            Assert.That(entry.DecidedAtUtc, Is.EqualTo(DateTime.MinValue));
        }

        [Test]
        public void DisplayStatusText_Pending_ReturnsPending()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Pending };

            Assert.That(entry.DisplayStatusText, Is.EqualTo("Pending"));
        }

        [Test]
        public void DisplayStatusText_AcceptedNotSaved_ReturnsNew()
        {
            // Accepted this session, not yet persisted to JSON: shown as "New".
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Accepted };

            Assert.That(entry.DisplayStatusText, Is.EqualTo("New"));
        }

        [Test]
        public void DisplayStatusText_AcceptedSaved_ReturnsSaved()
        {
            // Accepted and already persisted to JSON: shown as "Saved".
            var entry = new VrModReviewMatchEntry
            {
                Status = MatchDecision.Accepted,
                PersistedDecision = MatchDecision.Accepted
            };

            Assert.That(entry.DisplayStatusText, Is.EqualTo("Saved"));
        }

        [Test]
        public void DisplayStatusText_RejectedNotSaved_ReturnsRejectedNew()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Rejected };

            Assert.That(entry.DisplayStatusText, Is.EqualTo("Rejected (new)"));
        }

        [Test]
        public void DisplayStatusText_RejectedSaved_ReturnsRejected()
        {
            var entry = new VrModReviewMatchEntry
            {
                Status = MatchDecision.Rejected,
                PersistedDecision = MatchDecision.Rejected
            };

            Assert.That(entry.DisplayStatusText, Is.EqualTo("Rejected"));
        }

        [Test]
        public void IsUnsaved_Pending_False()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Pending };

            Assert.That(entry.IsUnsaved, Is.False);
        }

        [Test]
        public void IsUnsaved_AcceptedNotPersisted_True()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Accepted };

            Assert.That(entry.IsUnsaved, Is.True);
        }

        [Test]
        public void IsUnsaved_AcceptedPersisted_False()
        {
            var entry = new VrModReviewMatchEntry
            {
                Status = MatchDecision.Accepted,
                PersistedDecision = MatchDecision.Accepted
            };

            Assert.That(entry.IsUnsaved, Is.False);
        }

        [Test]
        public void SavedAtDisplay_Pending_ReturnsDash()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Pending };

            Assert.That(entry.SavedAtDisplay, Is.EqualTo("—"));
        }

        [Test]
        public void SavedAtDisplay_Unsaved_ReturnsDash()
        {
            // A decision made this session (not the saved one): no "Saved at" time, even
            // though a saved time exists from a different prior decision.
            var entry = new VrModReviewMatchEntry
            {
                Status = MatchDecision.Accepted,
                PersistedDecision = MatchDecision.Rejected,
                SavedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };

            Assert.That(entry.SavedAtDisplay, Is.EqualTo("—"));
        }

        [Test]
        public void SavedAtDisplay_Saved_FormatsTimestamp()
        {
            var savedAt = new DateTime(2024, 3, 5, 9, 7, 0, DateTimeKind.Utc);
            var entry = new VrModReviewMatchEntry
            {
                Status = MatchDecision.Accepted,
                PersistedDecision = MatchDecision.Accepted,
                SavedAtUtc = savedAt
            };

            // Displayed in local time (the stored instant is UTC).
            Assert.That(entry.SavedAtDisplay, Is.EqualTo(
                savedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        }

        [Test]
        public void SavedAtDisplay_SavedButNoUtc_ReturnsDash()
        {
            // A saved decision with no recorded save time (defensive): falls back to the dash.
            var entry = new VrModReviewMatchEntry
            {
                Status = MatchDecision.Accepted,
                PersistedDecision = MatchDecision.Accepted
            };

            Assert.That(entry.SavedAtDisplay, Is.EqualTo("—"));
        }

        [Test]
        public void CycleStatus_PendingGoesToAccepted()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Pending };

            VrModReviewDialog.CycleStatus(entry);

            Assert.That(entry.Status, Is.EqualTo(MatchDecision.Accepted));
        }

        [Test]
        public void CycleStatus_AcceptedGoesToRejected()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Accepted };

            VrModReviewDialog.CycleStatus(entry);

            Assert.That(entry.Status, Is.EqualTo(MatchDecision.Rejected));
        }

        [Test]
        public void CycleStatus_RejectedGoesToPending()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Rejected };

            VrModReviewDialog.CycleStatus(entry);

            Assert.That(entry.Status, Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        public void CycleStatus_FullCycleReturnsToStart()
        {
            var entry = new VrModReviewMatchEntry { Status = MatchDecision.Pending };

            VrModReviewDialog.CycleStatus(entry);
            VrModReviewDialog.CycleStatus(entry);
            VrModReviewDialog.CycleStatus(entry);

            Assert.That(entry.Status, Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        public void CycleStatus_NullIsNoOp()
        {
            VrModReviewDialog.CycleStatus(null);

            Assert.Pass();
        }
    }
}
