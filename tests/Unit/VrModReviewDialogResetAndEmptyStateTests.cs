using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for the review dialog's new pure logic:
    /// <list type="bullet">
    /// <item><see cref="VrModReviewDialog.ShouldShowEmptyHint"/> — when the "no matches" hint shows.</item>
    /// <item><see cref="VrModReviewDialog.ResetChoiceForTitle"/> — themed-reset dialog result → action.</item>
    /// <item><see cref="VrModSettingsViewModel.ShouldRemoveTagsForSource"/> — source-disabled dialog result.</item>
    /// <item><see cref="VrModReviewDialog.BuildColumnHeader"/> / <see cref="VrModReviewDialog.ColumnHeaderText"/> —
    /// code-built headers (canonical name in Tag, visible text/symbol in the element).</item>
    /// </list>
    /// The dialog's event handlers are intentionally not unit-tested (they are thin WPF plumbing over
    /// these pure functions + the service); this file pins the behavior that is.
    /// </summary>
    [TestFixture]
    public class VrModReviewDialogResetAndEmptyStateTests
    {
        // ---------------------------------------------------------------------------
        // ShouldShowEmptyHint
        // ---------------------------------------------------------------------------

        [Test]
        public void ShouldShowEmptyHint_ZeroVisibleWithFilter_True()
        {
            Assert.That(VrModReviewDialog.ShouldShowEmptyHint(0, true), Is.True);
        }

        [Test]
        public void ShouldShowEmptyHint_ZeroVisibleNoFilter_False()
        {
            // No filters active and zero rows: that is "nothing to review", not
            // "your filter hides everything" — the hint must NOT show.
            Assert.That(VrModReviewDialog.ShouldShowEmptyHint(0, false), Is.False);
        }

        [Test]
        public void ShouldShowEmptyHint_NonZeroVisible_False()
        {
            Assert.That(VrModReviewDialog.ShouldShowEmptyHint(3, true), Is.False);
            Assert.That(VrModReviewDialog.ShouldShowEmptyHint(3, false), Is.False);
        }

        // ---------------------------------------------------------------------------
        // ResetChoiceForTitle
        // ---------------------------------------------------------------------------

        [Test]
        public void ResetChoiceForTitle_KnownTitles_MapToTheirChoices()
        {
            Assert.That(
                VrModReviewDialog.ResetChoiceForTitle(VrModReviewDialog.ResetRemoveTagsTitle),
                Is.EqualTo(ResetChoice.ResetAndRemoveTags));
            Assert.That(
                VrModReviewDialog.ResetChoiceForTitle(VrModReviewDialog.ResetOnlyTitle),
                Is.EqualTo(ResetChoice.ResetOnly));
            Assert.That(
                VrModReviewDialog.ResetChoiceForTitle(VrModReviewDialog.ResetCancelTitle),
                Is.EqualTo(ResetChoice.Cancel));
        }

        [Test]
        public void ResetChoiceForTitle_UnknownOrNull_IsCancel()
        {
            // Defensive: a title that matches no option (or the dialog's null result)
            // must map to "do nothing", never to a destructive action.
            Assert.That(VrModReviewDialog.ResetChoiceForTitle(null), Is.EqualTo(ResetChoice.Cancel));
            Assert.That(VrModReviewDialog.ResetChoiceForTitle("Something else"), Is.EqualTo(ResetChoice.Cancel));
        }

        // ---------------------------------------------------------------------------
        // VrModSettingsViewModel.ShouldRemoveTagsForSource
        // ---------------------------------------------------------------------------

        [Test]
        public void ShouldRemoveTagsForSource_OnlyRemoveTagsOption_IsTrue()
        {
            Assert.That(
                VrModSettingsViewModel.ShouldRemoveTagsForSource(VrModSettingsViewModel.RemoveTagsOptionTitle),
                Is.True);
        }

        [Test]
        public void ShouldRemoveTagsForSource_CancelUnknownOrNull_IsFalse()
        {
            Assert.That(
                VrModSettingsViewModel.ShouldRemoveTagsForSource(VrModSettingsViewModel.CancelOptionTitle),
                Is.False);
            Assert.That(VrModSettingsViewModel.ShouldRemoveTagsForSource(null), Is.False);
            Assert.That(VrModSettingsViewModel.ShouldRemoveTagsForSource("Nope"), Is.False);
        }

        // ---------------------------------------------------------------------------
        // BuildColumnHeader / ColumnHeaderText
        // ---------------------------------------------------------------------------

        [Test]
        [Apartment(ApartmentState.STA)]
        public void BuildColumnHeader_CarriesCanonicalNameHeaderTextAndTooltip()
        {
            var column = new DataGridTextColumn();
            var spec = new VrModReviewDialog.ColumnSpec(column, "Link to Mod", "↗", "Open the mod page");

            var header = VrModReviewDialog.BuildColumnHeader(spec, null);

            // The canonical menu name rides in Tag (the visible text is a symbol).
            Assert.That(header.Tag, Is.EqualTo("Link to Mod"));
            Assert.That(header.ToolTip, Is.EqualTo("Open the mod page"));

            // Structure: Grid > StackPanel > TextBlock(s). No arrow → exactly one text.
            var grid = (Grid)header;
            var panel = (StackPanel)grid.Children[0];
            Assert.That(panel.Children.Count, Is.EqualTo(1));
            Assert.That(((TextBlock)panel.Children[0]).Text, Is.EqualTo("↗"));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void BuildColumnHeader_WithArrow_AddsArrowTextBlock()
        {
            var column = new DataGridTextColumn();
            var spec = new VrModReviewDialog.ColumnSpec(column, "Status", "Status", "The review decision");

            var header = VrModReviewDialog.BuildColumnHeader(spec, VrModReviewDialog.SortArrowAscending);

            var grid = (Grid)header;
            var panel = (StackPanel)grid.Children[0];
            Assert.That(panel.Children.Count, Is.EqualTo(2));
            Assert.That(((TextBlock)panel.Children[0]).Text, Is.EqualTo("Status"));
            Assert.That(((TextBlock)panel.Children[1]).Text, Is.EqualTo(VrModReviewDialog.SortArrowAscending));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void ColumnHeaderText_CodeBuiltHeader_UsesCanonicalTagName()
        {
            var column = new DataGridTextColumn();
            var spec = new VrModReviewDialog.ColumnSpec(column, "Link to Mod", "↗", "tip");
            column.Header = VrModReviewDialog.BuildColumnHeader(spec, null);

            // The menu must show "Link to Mod", not the visible "↗" symbol.
            Assert.That(VrModReviewDialog.ColumnHeaderText(column), Is.EqualTo("Link to Mod"));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void ColumnHeaderText_StringAndEmptyHeaders()
        {
            Assert.That(
                VrModReviewDialog.ColumnHeaderText(new DataGridTextColumn { Header = "Status" }),
                Is.EqualTo("Status"));
            Assert.That(
                VrModReviewDialog.ColumnHeaderText(new DataGridTextColumn { Header = "" }),
                Is.EqualTo("(column)"));
            Assert.That(
                VrModReviewDialog.ColumnHeaderText(new DataGridTextColumn { Header = null }),
                Is.EqualTo("(column)"));
        }
    }
}