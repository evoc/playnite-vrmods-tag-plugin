using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for the review dialog's "Status…" menu (<see cref="VrModReviewDialog.BuildStatusMenu"/>):
    /// one checkable item per status (checked = in the current selection), a separator, and two
    /// quick actions. Toggling a status item or clicking a quick action invokes the apply callback
    /// with the new selection (a multi-select union; an empty selection means "show all").
    /// </summary>
    [TestFixture]
    public class VrModReviewDialogStatusMenuTests
    {
        [Test]
        [Apartment(ApartmentState.STA)]
        public void BuildStatusMenu_HasThreeStatusItemsSeparatorAndTwoQuickActions()
        {
            var menu = VrModReviewDialog.BuildStatusMenu(new MatchDecision[0], _ => { });

            // 3 status items + 1 separator + 2 quick actions = 6 items.
            Assert.That(menu.Items.Count, Is.EqualTo(6));
            Assert.That(menu.Items[0], Is.InstanceOf<MenuItem>());
            Assert.That(menu.Items[1], Is.InstanceOf<MenuItem>());
            Assert.That(menu.Items[2], Is.InstanceOf<MenuItem>());
            Assert.That(menu.Items[3], Is.InstanceOf<Separator>());
            Assert.That(menu.Items[4], Is.InstanceOf<MenuItem>());
            Assert.That(menu.Items[5], Is.InstanceOf<MenuItem>());

            // The status items carry the status headers and start checkable; with no selection
            // none are checked.
            var pending = (MenuItem)menu.Items[0];
            Assert.That(pending.Header, Is.EqualTo("Pending"));
            Assert.That(pending.IsCheckable, Is.True);
            Assert.That(pending.IsChecked, Is.False, "no selection → unchecked");
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void BuildStatusMenu_CheckmarksReflectCurrentSelection()
        {
            var menu = VrModReviewDialog.BuildStatusMenu(new[] { MatchDecision.Pending }, _ => { });

            var pending = (MenuItem)menu.Items[0];
            var accepted = (MenuItem)menu.Items[1];
            Assert.That(pending.IsChecked, Is.True, "Pending is selected");
            Assert.That(accepted.IsChecked, Is.False, "Accepted is not selected");
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void BuildStatusMenu_TogglingItemInvokesApplyWithUpdatedSet()
        {
            MatchDecision[]? applied = null;
            var menu = VrModReviewDialog.BuildStatusMenu(new MatchDecision[0], s => applied = s.ToArray());

            var pending = (MenuItem)menu.Items[0];
            pending.IsChecked = true; // toggling Pending on fires the Checked handler.

            Assert.That(applied, Is.Not.Null);
            Assert.That(applied, Has.Length.EqualTo(1));
            Assert.That(applied![0], Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void BuildStatusMenu_QuickActionPendingOnly_AppliesPending()
        {
            MatchDecision[]? applied = null;
            var menu = VrModReviewDialog.BuildStatusMenu(new MatchDecision[0], s => applied = s.ToArray());

            var pendingOnly = (MenuItem)menu.Items[4];
            Assert.That(pendingOnly.Header, Is.EqualTo("Pending only"));
            RaiseClick(pendingOnly);

            Assert.That(applied, Has.Length.EqualTo(1));
            Assert.That(applied![0], Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void BuildStatusMenu_QuickActionShowAll_AppliesEmpty()
        {
            MatchDecision[]? applied = null;
            var menu = VrModReviewDialog.BuildStatusMenu(new[] { MatchDecision.Rejected }, s => applied = s.ToArray());

            var showAll = (MenuItem)menu.Items[5];
            Assert.That(showAll.Header, Is.EqualTo("Show all"));
            RaiseClick(showAll);

            Assert.That(applied, Is.Not.Null);
            Assert.That(applied, Is.Empty);
        }

        /// <summary>
        /// Raises a MenuItem's Click event the way the menu would on user activation (the Click
        /// routed event is raised through the element).
        /// </summary>
        private static void RaiseClick(MenuItem item)
        {
            // MenuItem.Click is MenuItem.ClickEvent (MenuItem does NOT derive from ButtonBase),
            // so raise that exact routed event to fire the Click handler.
            item.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent, item));
        }
    }
}
