using System.Windows;
using System.Windows.Controls;
using System.Threading;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for the review dialog's "Columns…" menu (<see cref="VrModReviewDialog.BuildColumnsMenu"/>):
    /// one checkable show/hide item per column (checked = visible). Column reordering is NOT in this
    /// menu — the DataGrid's native header drag does it (<c>CanUserReorderColumns</c>) — so the menu
    /// is visibility-only.
    /// </summary>
    [TestFixture]
    public class VrModReviewDialogColumnsMenuTests
    {
        // ---------------------------------------------------------------------------
        // BuildColumnsMenu — structure and behavior (against a real DataGrid).
        // ---------------------------------------------------------------------------

        [Test]
        [Apartment(ApartmentState.STA)]
        public void BuildColumnsMenu_HasOneVisibilityItemPerColumn()
        {
            var grid = new DataGrid();
            grid.Columns.Add(new DataGridTextColumn { Header = "A" });
            grid.Columns.Add(new DataGridTextColumn { Header = "B" });
            grid.Columns.Add(new DataGridTextColumn { Header = "C" });

            var menu = VrModReviewDialog.BuildColumnsMenu(grid.Columns);

            // Exactly one checkable show/hide item per column — no separator, no reorder items.
            Assert.That(menu.Items.Count, Is.EqualTo(3));
            Assert.That(menu.Items[0], Is.InstanceOf<MenuItem>());
            Assert.That(menu.Items[1], Is.InstanceOf<MenuItem>());
            Assert.That(menu.Items[2], Is.InstanceOf<MenuItem>());

            // Each item carries the column header and starts checked (columns start visible).
            var first = (MenuItem)menu.Items[0];
            Assert.That(first.Header, Is.EqualTo("A"));
            Assert.That(first.IsCheckable, Is.True);
            Assert.That(first.IsChecked, Is.True, "columns start visible");
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void BuildColumnsMenu_VisibilityToggle_HidesColumn()
        {
            var grid = new DataGrid();
            var column = new DataGridTextColumn { Header = "A" };
            grid.Columns.Add(column);

            var menu = VrModReviewDialog.BuildColumnsMenu(grid.Columns);
            var item = (MenuItem)menu.Items[0];

            Assert.That(item.IsChecked, Is.True);
            item.IsChecked = false;
            Assert.That(column.Visibility, Is.EqualTo(Visibility.Collapsed));
        }
    }
}
