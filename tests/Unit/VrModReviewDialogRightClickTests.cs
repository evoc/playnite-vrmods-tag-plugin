using System.Windows;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="VrModReviewDialog.SelectRowForRightClick"/> — the right-click
    /// handler core that scopes the review dialog's context menu to the row under the cursor.
    /// The live-grid selection behavior is exercised by the standalone harness; these tests pin
    /// the safe no-op paths a right-click on empty grid area, a header, or with a null source
    /// must hit without throwing or looping.
    /// </summary>
    [TestFixture]
    public class VrModReviewDialogRightClickTests
    {
        [Test]
        public void NullSource_IsSafeNoOp()
        {
            // The handler forwards e.OriginalSource; a null source must be a safe no-op
            // (this also guards the visual-tree walk from looping forever on a null source).
            VrModReviewDialog.SelectRowForRightClick(null);
        }

        [Test]
        public void SourceOutsideAnyRow_IsSafeNoOp()
        {
            // Right-clicking empty grid area or a column header yields a source with no DataGridRow
            // ancestor; the walk must terminate at the top of the tree and no-op (no throw, no
            // infinite loop). A bare DependencyObject stands in for such a source without needing
            // a realized FrameworkElement (which the test host cannot construct).
            var orphan = new DependencyObject();

            VrModReviewDialog.SelectRowForRightClick(orphan);
        }
    }
}
