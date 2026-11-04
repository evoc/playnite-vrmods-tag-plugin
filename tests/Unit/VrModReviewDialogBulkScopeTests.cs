using System.Collections.Generic;
using NUnit.Framework;
using Playnite.SDK.Models;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Unit tests for the review dialog's bulk-action scope (task 212, owner decision 2-b): bulk
    /// Accept/Reject means the visible selection when there is one, otherwise the rows the filters are
    /// showing — never a row a filter hides, because nobody can mean a row they could not see.
    /// <see cref="VrModReviewDialog.BulkTargets"/> is the one decision the buttons, the row context
    /// menu and Alt+A/Alt+R all route through; the captions and tooltips carry the same scope in
    /// numbers, so the promise is testable where a person reads it.
    /// </summary>
    [TestFixture]
    public class VrModReviewDialogBulkScopeTests
    {
        private static VrModReviewMatchEntry Row(string name)
        {
            return new VrModReviewMatchEntry
            {
                SourceId = "UeVR",
                SourceName = "UeVR Profiles",
                SourceGameName = name,
                PlayniteGame = new Game(name),
                Category = "UEVR"
            };
        }

        // --- Which rows a bulk action means (BulkTargets) --------------------------------

        /// <summary>
        /// With nothing selected the action means the rows the filters are showing. The fixture carries a
        /// third row that the view does not yield — a filter hid it — and the promise is that it is not
        /// among the rows changed. This is the whole of decision 2-b in one call.
        /// </summary>
        [Test]
        public void BulkTargets_NoSelection_TakesShownRowsAndNoHiddenOne()
        {
            var shown = new List<VrModReviewMatchEntry> { Row("Skyrim"), Row("Half-Life Alyx") };
            var hiddenByFilter = Row("Fallout 4");

            var targets = VrModReviewDialog.BulkTargets(new List<VrModReviewMatchEntry>(), shown);

            Assert.That(targets, Has.Count.EqualTo(2));
            Assert.That(targets, Has.None.EqualTo(hiddenByFilter));
        }

        /// <summary>
        /// A row selected before a filter closed over it stays selected in the grid. Choosing it must not
        /// reach through the filter to change it: a filter change is a person's decision about what they
        /// are looking at, and a bulk action follows that decision, not the stale selection.
        /// </summary>
        [Test]
        public void BulkTargets_SelectionNeverReachesAHiddenRow()
        {
            var shown = new List<VrModReviewMatchEntry> { Row("Skyrim"), Row("Half-Life Alyx") };
            var hiddenByFilter = Row("Fallout 4");
            var selected = new List<VrModReviewMatchEntry> { shown[1], hiddenByFilter };

            var targets = VrModReviewDialog.BulkTargets(selected, shown);

            Assert.That(targets, Is.EquivalentTo(new[] { shown[1] }));
            Assert.That(targets, Has.None.EqualTo(hiddenByFilter));
        }

        /// <summary>
        /// A selection is not a hint added to the full action, it is the action: two rows selected out of
        /// two shown means the one selected.
        /// </summary>
        [Test]
        public void BulkTargets_SelectionMeansJustThoseRows()
        {
            var shown = new List<VrModReviewMatchEntry> { Row("Skyrim"), Row("Half-Life Alyx") };

            var targets = VrModReviewDialog.BulkTargets(new[] { shown[0] }, shown);

            Assert.That(targets, Is.EquivalentTo(new[] { shown[0] }));
        }

        /// <summary>
        /// When the filters match nothing there is nothing to change, and the action must say so by doing
        /// nothing. (A live grid in this state cannot be built in a test host, so the pure decision
        /// carries it: an empty visible set yields no targets.)
        /// </summary>
        [Test]
        public void BulkTargets_NothingShownMeansNothingToChange()
        {
            var targets = VrModReviewDialog.BulkTargets(
                new List<VrModReviewMatchEntry>(), new List<VrModReviewMatchEntry>());

            Assert.That(targets, Is.Empty);
        }

        // --- What the caption says (BulkButtonLabel) -------------------------------------

        /// <summary>
        /// The caption carries both halves of the scope, not just the half that changes: 12 of 130 means
        /// "12 shown · 118 keep their status". A person deciding whether to click should not have to work
        /// out what happens to the rest.
        /// </summary>
        [Test]
        public void BulkButtonLabel_WithHiddenRowsCountsBothHalves()
        {
            Assert.That(VrModReviewDialog.BulkButtonLabel("Reject", 0, 12, 118),
                Is.EqualTo("Reject 12 shown \u00B7 118 keep their status"));
        }

        /// <summary>
        /// With nothing hidden there is nothing to qualify: the old caption is the right caption, so an
        /// unfiltered dialog does not start explaining a scope nobody asked about.
        /// </summary>
        [Test]
        public void BulkButtonLabel_WithNothingHiddenStaysPlain()
        {
            Assert.That(VrModReviewDialog.BulkButtonLabel("Accept", 0, 354, 0), Is.EqualTo("Accept All"));
        }

        /// <summary>
        /// A selection is its own scope and needs no arithmetic: the row count sits on the rows a person
        /// already highlighted.
        /// </summary>
        [Test]
        public void BulkButtonLabel_WithSelectionSaysSelected()
        {
            Assert.That(VrModReviewDialog.BulkButtonLabel("Reject", 2, 12, 118), Is.EqualTo("Reject Selected"));
        }

        /// <summary>
        /// A filter that matches nothing is stated as "Reject nothing" rather than a caption that reads as
        /// a no-op or a stale count.
        /// </summary>
        [Test]
        public void BulkButtonLabel_WithNothingShownSaysNothing()
        {
            Assert.That(VrModReviewDialog.BulkButtonLabel("Reject", 0, 0, 118), Is.EqualTo("Reject nothing"));
        }

        // --- What the tooltip promises (BulkButtonTooltip) -----------------------------

        /// <summary>
        /// Where the caption is too short to be unambiguous, the tooltip is: hidden rows keep the status
        /// they already have — status is not cleared, not set to Pending, not decided.
        /// </summary>
        [Test]
        public void BulkButtonTooltip_WithHiddenRowsSaysTheyKeepTheirStatus()
        {
            var tip = VrModReviewDialog.BulkButtonTooltip("Reject", "Alt+R", 0, 12, 118);

            Assert.That(tip, Does.Contain("Reject the 12 rows the filters are showing"));
            Assert.That(tip, Does.Contain("Hidden rows (118) keep the status they already have"));
            Assert.That(tip, Does.Contain("Alt+R"));
        }

        /// <summary>
        /// The selection case states the boundary in words the caption cannot hold: no other row changes.
        /// </summary>
        [Test]
        public void BulkButtonTooltip_WithSelectionSaysNoOtherRowChanges()
        {
            var tip = VrModReviewDialog.BulkButtonTooltip("Reject", "Alt+R", 2, 12, 118);

            Assert.That(tip, Is.EqualTo("Reject the 2 selected rows. No other row changes. Alt+R"));
        }

        /// <summary>
        /// When the filters have hidden every row, the tooltip says what happened to them instead of
        /// leaving "Reject nothing" to be read as a failure.
        /// </summary>
        [Test]
        public void BulkButtonTooltip_WhenEverythingIsHiddenSaysSo()
        {
            var tip = VrModReviewDialog.BulkButtonTooltip("Reject", "Alt+R", 0, 0, 118);

            Assert.That(tip, Does.Contain("the filters are hiding all 118 rows"));
            Assert.That(tip, Does.Contain("They keep the status they already have"));
        }

        /// <summary>
        /// A single row reads as "row", not "rows" — the counts in these captions are read aloud by people
        /// deciding what a click means.
        /// </summary>
        [Test]
        public void BulkButtonTooltip_OneSelectedRowReadsAsRow()
        {
            var tip = VrModReviewDialog.BulkButtonTooltip("Accept", "Alt+A", 1, 1, 0);

            Assert.That(tip, Is.EqualTo("Accept the 1 selected row. No other row changes. Alt+A"));
        }
    }
}
