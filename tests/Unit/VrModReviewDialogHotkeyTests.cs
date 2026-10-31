using System.Windows.Input;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for the review dialog's status hotkeys (<see cref="VrModReviewDialog.HotkeyDecision"/>):
    /// Enter accepts, R rejects, P sets to Pending. Any other key, or any modified key
    /// (Ctrl/Alt/Shift), maps to no status change so arrows, Tab, and reserved combos
    /// (Ctrl+S, Ctrl+R, ...) are left alone.
    /// </summary>
    [TestFixture]
    public class VrModReviewDialogHotkeyTests
    {
        [Test]
        public void Enter_Accepts()
        {
            Assert.That(VrModReviewDialog.HotkeyDecision(Key.Enter, ModifierKeys.None),
                Is.EqualTo(MatchDecision.Accepted));
        }

        [Test]
        public void R_Rejects()
        {
            Assert.That(VrModReviewDialog.HotkeyDecision(Key.R, ModifierKeys.None),
                Is.EqualTo(MatchDecision.Rejected));
        }

        [Test]
        public void P_SetsPending()
        {
            Assert.That(VrModReviewDialog.HotkeyDecision(Key.P, ModifierKeys.None),
                Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        public void NavigationAndOtherKeys_NoDecision()
        {
            // Arrows and Tab move the cursor / focus; they must not change a status.
            Assert.That(VrModReviewDialog.HotkeyDecision(Key.Up, ModifierKeys.None), Is.Null);
            Assert.That(VrModReviewDialog.HotkeyDecision(Key.Down, ModifierKeys.None), Is.Null);
            Assert.That(VrModReviewDialog.HotkeyDecision(Key.Tab, ModifierKeys.None), Is.Null);
            // Unrelated letters are not hotkeys.
            Assert.That(VrModReviewDialog.HotkeyDecision(Key.A, ModifierKeys.None), Is.Null);
        }

        [Test]
        public void ModifiedKeys_NoDecision()
        {
            // Reserved combos: Ctrl+S saves (handled separately), and Ctrl/Shift on the status
            // keys must not accidentally change a status.
            Assert.That(VrModReviewDialog.HotkeyDecision(Key.S, ModifierKeys.Control), Is.Null);
            Assert.That(VrModReviewDialog.HotkeyDecision(Key.R, ModifierKeys.Control), Is.Null);
            Assert.That(VrModReviewDialog.HotkeyDecision(Key.Enter, ModifierKeys.Shift), Is.Null);
        }

        [Test]
        public void AltF_FocusesFilter()
        {
            Assert.That(VrModReviewDialog.IsFocusFilterHotkey(Key.F, ModifierKeys.Alt), Is.True);
        }

        [Test]
        public void OtherKeys_NotFocusFilter()
        {
            // Alt on other letters, and F without the Alt modifier, must not steal focus.
            Assert.That(VrModReviewDialog.IsFocusFilterHotkey(Key.A, ModifierKeys.Alt), Is.False);
            Assert.That(VrModReviewDialog.IsFocusFilterHotkey(Key.F, ModifierKeys.None), Is.False);
            Assert.That(VrModReviewDialog.IsFocusFilterHotkey(Key.F, ModifierKeys.Control), Is.False);
        }

        [Test]
        public void Accelerators_MapAltLetters()
        {
            Assert.That(VrModReviewDialog.AcceleratorForKey(Key.F, ModifierKeys.Alt),
                Is.EqualTo(ReviewAccelerator.FocusFilter));
            Assert.That(VrModReviewDialog.AcceleratorForKey(Key.T, ModifierKeys.Alt),
                Is.EqualTo(ReviewAccelerator.TimeFilter));
            Assert.That(VrModReviewDialog.AcceleratorForKey(Key.S, ModifierKeys.Alt),
                Is.EqualTo(ReviewAccelerator.StatusMenu));
            Assert.That(VrModReviewDialog.AcceleratorForKey(Key.C, ModifierKeys.Alt),
                Is.EqualTo(ReviewAccelerator.ColumnsMenu));
            Assert.That(VrModReviewDialog.AcceleratorForKey(Key.H, ModifierKeys.Alt),
                Is.EqualTo(ReviewAccelerator.Help));
            Assert.That(VrModReviewDialog.AcceleratorForKey(Key.A, ModifierKeys.Alt),
                Is.EqualTo(ReviewAccelerator.AcceptAll));
            Assert.That(VrModReviewDialog.AcceleratorForKey(Key.R, ModifierKeys.Alt),
                Is.EqualTo(ReviewAccelerator.RejectAll));
        }

        [Test]
        public void NonAccelerators_MapToNone()
        {
            // No modifier, a different modifier, or an unrelated letter is not an accelerator.
            Assert.That(VrModReviewDialog.AcceleratorForKey(Key.F, ModifierKeys.None),
                Is.EqualTo(ReviewAccelerator.None));
            Assert.That(VrModReviewDialog.AcceleratorForKey(Key.A, ModifierKeys.Control),
                Is.EqualTo(ReviewAccelerator.None));
            Assert.That(VrModReviewDialog.AcceleratorForKey(Key.S, ModifierKeys.Alt | ModifierKeys.Shift),
                Is.EqualTo(ReviewAccelerator.None));
            Assert.That(VrModReviewDialog.AcceleratorForKey(Key.Q, ModifierKeys.Alt),
                Is.EqualTo(ReviewAccelerator.None));
            Assert.That(VrModReviewDialog.AcceleratorForKey(Key.Enter, ModifierKeys.Alt),
                Is.EqualTo(ReviewAccelerator.None));
        }
    }
}
