using System;
using System.Threading;
using System.Windows;
using NUnit.Framework;
using Playnite.VRMods;

namespace Playnite.VRMods.Tests
{
    /// <summary>
    /// Tests for the review dialog's cloned Playnite window chrome and the best-effort
    /// child-ownership re-evaluation that keeps Playnite's own windows correctly dimmed
    /// (Opacity 0.4) while the dialog is open and undimmed after it closes.
    ///
    /// The helpers are pure on purpose: the real <c>Playnite.Controls.WindowBase</c> and
    /// <c>Playnite.Windows.WindowManager</c> live in Playnite's app assembly (not the SDK),
    /// which the plugin never references. So the type check works on the full type name and
    /// the re-evaluation method is resolved by reflection. The fake types at the bottom of this
    /// file reproduce the real layout (a <c>WindowBase</c>, a subclass of it, and a static
    /// <c>WindowManager</c> in the same assembly) so the exact resolution path — including the
    /// subclass case the old code got wrong — is exercised. Everything degrades to a silent
    /// no-op when Playnite's internals change.
    /// </summary>
    [TestFixture]
    public class VrModReviewDialogWindowChromeTests
    {
        // The fake types (declared at the bottom of this file) mirror the real Playnite layout:
        //   Playnite.Controls.WindowBase            — the base class (full name matches the real one)
        //   Playnite.DesktopApp.Controls.AppWindow  — a subclass (the real owner's shape)
        //   Playnite.Windows.WindowManager          — static, same assembly, with the re-evaluate method
        // Referenced by full name so the resolver's name-based matching is exercised for real.

        [Test]
        public void FindPlayniteWindowBaseType_ReturnsTheTypeWhenItIsTheWindowBase()
        {
            Assert.That(
                VrModReviewDialog.FindPlayniteWindowBaseType(typeof(Playnite.Controls.WindowBase)),
                Is.SameAs(typeof(Playnite.Controls.WindowBase)));
        }

        [Test]
        public void FindPlayniteWindowBaseType_WalksUpToTheWindowBaseForASubclass()
        {
            // The owner is a SUBCLASS (AppWindow : WindowBase); the resolver must walk the
            // base-type chain and return the WindowBase itself, not the leaf type.
            Assert.That(
                VrModReviewDialog.FindPlayniteWindowBaseType(typeof(Playnite.DesktopApp.Controls.AppWindow)),
                Is.SameAs(typeof(Playnite.Controls.WindowBase)));
        }

        [TestCase(null)]
        [TestCase(typeof(Window))]
        [TestCase(typeof(string))]
        public void FindPlayniteWindowBaseType_ReturnsNullWhenNotAPlayniteWindow(System.Type? type)
        {
            Assert.That(VrModReviewDialog.FindPlayniteWindowBaseType(type), Is.Null);
        }

        [TestCase(typeof(Playnite.Controls.WindowBase), true)]
        [TestCase(typeof(Playnite.DesktopApp.Controls.AppWindow), true)] // subclass: recognized (the old-code bug)
        [TestCase(typeof(Window), false)]
        [TestCase(typeof(string), false)]
        [TestCase(null, false)]
        public void IsPlayniteWindowBase_TrueForWindowBaseAndSubclasses(System.Type? type, bool expected)
        {
            Assert.That(VrModReviewDialog.IsPlayniteWindowBase(type), Is.EqualTo(expected));
        }

        [Test]
        public void ResolveNotifyChildOwnershipChanges_ReturnsTheWindowManagerMethod()
        {
            var method = VrModReviewDialog.ResolveNotifyChildOwnershipChanges(typeof(Playnite.Controls.WindowBase));
            Assert.That(method, Is.Not.Null);
            Assert.That(method!.Name, Is.EqualTo("NotifyChildOwnershipChanges"));
            Assert.That(method.IsStatic, Is.True);
        }

        [Test]
        public void ResolveNotifyChildOwnershipChanges_ReturnsNullForNull()
        {
            Assert.That(VrModReviewDialog.ResolveNotifyChildOwnershipChanges(null), Is.Null);
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void NotifyPlayniteChildOwnershipChanged_IsANoOpForNullAndPlainWindows()
        {
            // null owner (screen-centered fallback) and a plain Window (not a Playnite window)
            // must be silent no-ops: no exception, no re-evaluation invoked.
            Playnite.Windows.WindowManager.Invocations = 0;
            VrModReviewDialog.NotifyPlayniteChildOwnershipChanged(null);
            VrModReviewDialog.NotifyPlayniteChildOwnershipChanged(new Window());
            Assert.That(Playnite.Windows.WindowManager.Invocations, Is.Zero);
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void NotifyPlayniteChildOwnershipChanged_InvokesForAPlayniteWindowAndItsSubclass()
        {
            // A Playnite window (the WindowBase) and its subclass (the real owner's shape) must
            // resolve and invoke WindowManager.NotifyChildOwnershipChanges exactly once.
            Playnite.Windows.WindowManager.Invocations = 0;
            VrModReviewDialog.NotifyPlayniteChildOwnershipChanged(new Playnite.Controls.WindowBase());
            Assert.That(Playnite.Windows.WindowManager.Invocations, Is.EqualTo(1));

            Playnite.Windows.WindowManager.Invocations = 0;
            VrModReviewDialog.NotifyPlayniteChildOwnershipChanged(new Playnite.DesktopApp.Controls.AppWindow());
            Assert.That(Playnite.Windows.WindowManager.Invocations, Is.EqualTo(1));
        }
    }
}

namespace Playnite.Controls
{
    // Fake Playnite WindowBase. Full name "Playnite.Controls.WindowBase" matches the real type's
    // name (the resolver compares full names, not identity). Derives from WPF Window so a real
    // instance can be passed to the orchestrator the way the live dialog's owner is.
    internal class WindowBase : System.Windows.Window
    {
    }
}

namespace Playnite.DesktopApp.Controls
{
    // Fake desktop main window: a subclass of the fake WindowBase (the real owner's shape).
    internal class AppWindow : Playnite.Controls.WindowBase
    {
    }
}

namespace Playnite.Windows
{
    // Fake Playnite WindowManager (same assembly as the fake WindowBase, like the real
    // Playnite.dll). The static Invocations counter lets a test assert the resolved method was
    // actually invoked.
    internal static class WindowManager
    {
        public static int Invocations;

        public static void NotifyChildOwnershipChanges()
        {
            Invocations++;
        }
    }
}
