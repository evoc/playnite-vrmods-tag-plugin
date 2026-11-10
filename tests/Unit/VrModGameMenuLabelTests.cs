using System.Globalization;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// The game context menu's labels (<see cref="VrModTagPlugin.MenuItemAtLabel"/> - the one part of
    /// the menu a test can reach: the items themselves need Playnite's UI, and the plugin class needs
    /// its host to build them).
    ///
    /// Why the label deserves a test of its own: a Playnite menu item has no tooltip
    /// (<c>PluginMenuItem</c> carries Icon, Description, MenuSection only), so the label and the
    /// confirmation dialog have to carry the whole meaning between them. The label is also the only
    /// place the menu says how many games the action will touch - and an action that says "this game"
    /// while acting on nine is worse than no count at all.
    ///
    /// What these tests hold:
    /// 1. A selection of one gets the singular text, verbatim, with no number in it.
    /// 2. A selection of many gets the plural with its own count interpolated - the count comes from
    ///    the caller, so it cannot drift from the selection the action receives.
    /// 3. The count is formatted with the invariant culture: a locale that draws its own digits must
    ///    not produce a label that disagrees with the confirmation dialog's count.
    /// 4. A plural format that forgot its <c>{0}</c> comes back unmangled rather than throwing - the
    ///    mistake then reads as a missing number, which is the failure mode to make visible.
    /// </summary>
    [TestFixture]
    public class VrModGameMenuLabelTests
    {
        // The three pairs the game menu passes today (VrModTagPlugin.GetGameMenuItems). The fixture
        // pins the rule - singular for one, the count for many - and spells the pairs out so a reader
        // sees what the menu actually says. A label that changes here does not break this test; a
        // label that stops following the rule does.
        private const string CheckSingular = "Check this game for VR mods...";
        private const string CheckPlural = "Check these {0} games for VR mods...";
        private const string ResetSingular = "Reset my decisions for this game...";
        private const string ResetPlural = "Reset my decisions for these {0} games...";
        private const string RejectSingular = "Reject all assigned VR mods on this game...";
        private const string RejectPlural = "Reject all assigned VR mods on these {0} games...";

        private CultureInfo _previousCulture = null!;

        [SetUp]
        public void SetUp()
        {
            _previousCulture = CultureInfo.CurrentCulture;
        }

        [TearDown]
        public void TearDown()
        {
            // The label is built on Playnite's UI thread; a test that leaves a foreign culture behind
            // hands it to every test that follows in this worker.
            CultureInfo.CurrentCulture = _previousCulture;
        }

        [TestCase(CheckSingular, CheckPlural, TestName = "Check_this_game_label")]
        [TestCase(ResetSingular, ResetPlural, TestName = "Reset_my_decisions_label")]
        [TestCase(RejectSingular, RejectPlural, TestName = "Reject_all_assigned_label")]
        public void MenuItemLabel_OneGame_IsTheSingularTextVerbatim(string singular, string plural)
        {
            var label = VrModTagPlugin.MenuItemLabel(singular, plural, 1);

            Assert.Multiple(() =>
            {
                Assert.That(label, Is.EqualTo(singular),
                    "one game is spoken about as one game, and the format half must not leak in");
                Assert.That(label, Does.Not.Contain("1"),
                    "no count belongs in the singular form: the menu says \"this game\", not \"this 1 game\"");
            });
        }

        [TestCase(CheckPlural, 2)]
        [TestCase(CheckPlural, 3)]
        [TestCase(CheckPlural, 12)]
        [TestCase(ResetPlural, 4)]
        [TestCase(RejectPlural, 9)]
        public void MenuItemLabel_ManyGames_NamesTheCountItIsAboutToTouch(string plural, int gameCount)
        {
            var label = VrModTagPlugin.MenuItemLabel("unused singular", plural, gameCount);

            Assert.Multiple(() =>
            {
                Assert.That(label, Does.Contain(gameCount.ToString(CultureInfo.InvariantCulture)),
                    "the label is the only place the menu states the size of the action");
                Assert.That(label, Does.Not.Contain("{0}"),
                    "an unformatted placeholder in a menu is a bug the user reads as copy-paste");
                Assert.That(label, Is.Not.EqualTo("unused singular"),
                    "a selection of many never takes the singular form");
            });
        }

        [Test]
        public void MenuItemLabel_BoundaryOfOne_IsSingularAndOfTwo_IsPlural()
        {
            // The whole rule is a comparison against one; both sides of it get pinned here because the
            // menu's count is the caller's list length and off-by-one lives exactly at this boundary.
            Assert.Multiple(() =>
            {
                Assert.That(VrModTagPlugin.MenuItemLabel(CheckSingular, CheckPlural, 1), Is.EqualTo(CheckSingular));
                Assert.That(VrModTagPlugin.MenuItemLabel(CheckSingular, CheckPlural, 2),
                    Is.EqualTo("Check these 2 games for VR mods..."));
            });
        }

        [Test]
        public void MenuItemLabel_CountIsFormattedInvariantly()
        {
            // Playnite's UI thread can carry a user locale whose digits are not ASCII. The label and
            // the confirmation dialog have to show the same number, and the dialog compares invariant,
            // so the label is built invariant too.
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");

            var label = VrModTagPlugin.MenuItemLabel(CheckSingular, CheckPlural, 12);

            Assert.That(label, Is.EqualTo("Check these 12 games for VR mods..."),
                "a native-digit locale must not put a number in the menu that the dialog cannot match");
        }

        [Test]
        public void MenuItemLabel_PluralMissingItsPlaceholder_ComesBackUnchanged()
        {
            // Not a desired label, and documented rather than defended: string.Format leaves a string
            // with no {0} alone. A future pair that forgets the placeholder therefore shows up as a
            // plural that names no count, which a reviewer can see - not as an exception in Playnite's
            // menu builder.
            var label = VrModTagPlugin.MenuItemLabel(CheckSingular, "Check these games for VR mods...", 7);

            Assert.That(label, Is.EqualTo("Check these games for VR mods..."));
        }
    }
}
