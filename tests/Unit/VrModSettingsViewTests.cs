using NSubstitute;
using NUnit.Framework;
using Playnite.SDK;
using Playnite.VRMods;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for the settings view's XAML binding contract.
    ///
    /// Regression test: the source row template bound <c>DisplayName</c> (a
    /// get-only property) through a <see cref="System.Windows.Documents.Run"/>,
    /// whose <c>Text</c> property binds TwoWay by default. WPF throws
    /// InvalidOperationException ("A TwoWay or OneWayToSource binding cannot
    /// work on the read-only property ...") the moment the row template is
    /// materialized, which escaped as XamlParseException and crashed Playnite
    /// when the user opened the plugin settings.
    ///
    /// The view is hosted in a real (never-painted) Window and given a
    /// synchronous layout pass — the same code path the Playnite settings
    /// dialog uses: bindings attach, the ItemsControl generates one container
    /// per source row, each container applies the row DataTemplate, and
    /// template binding activation is what threw the live exception. Any
    /// TwoWay/OneWayToSource binding to a read-only property in the template
    /// will fail this test.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    [TestFixture]
    public class VrModSettingsViewTests
    {
        [Test]
        public void SettingsView_SourceRows_LayoutMaterializesTemplates()
        {
            // LoadPluginSettings is unstubbed: the substitute returns null and the
            // ViewModel falls back to default settings, exactly like a first run.
            var plugin = Substitute.For<VrModTagPlugin>(Substitute.For<IPlayniteAPI>());
            var registry = new VrModSourceRegistry();
            // Same registration the plugin constructor performs — one row per source.
            registry.Register(new RototRobotVrModParser());
            registry.Register(new Flat2VrParser());
            registry.Register(new UeVrProfilesParser());
            registry.Register(new ReFrameworkParser());
            registry.Register(new PcvrCentralParser());
            registry.Register(new MrNlceHubParser());
            registry.Register(new MrSurvivorHubParser());

            var vm = new VrModSettingsViewModel(plugin, registry);
            Assert.That(vm.SourceRows, Has.Count.EqualTo(7),
                "one row per registered source — the template must materialize all of them");

            var window = new Window();
            try
            {
                var view = new VrModSettingsView();
                view.DataContext = vm;
                window.Content = view;
                // Show without a message pump: creates the HWND and connects the tree
                // (so bindings attach), but nothing is ever painted.
                window.Show();
                // The live crash: synchronous layout pass → item containers → row
                // DataTemplate application → binding activation.
                window.UpdateLayout();

                var itemsControl = FindItemsControl(view);
                Assert.That(itemsControl, Is.Not.Null, "the source rows ItemsControl must exist in the visual tree");
                Assert.That(itemsControl!.Items, Has.Count.EqualTo(7),
                    "layout must materialize one item per source row");
            }
            finally
            {
                window.Close();
            }
        }

        /// <summary>
        /// Finds the first ItemsControl in the visual tree under <paramref name="root"/>.
        /// </summary>
        private static ItemsControl? FindItemsControl(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ItemsControl itemsControl)
                    return itemsControl;
                var found = FindItemsControl(child);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
