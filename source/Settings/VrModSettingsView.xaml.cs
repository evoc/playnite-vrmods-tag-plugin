using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Playnite.VRMods
{
    /// <summary>
    /// Settings view UserControl for the VRMod Tags plugin.
    ///
    /// This is the visual representation of the plugin's settings dialog, shown when
    /// the user opens Settings → Extensions → VRMod Tags in Playnite.
    ///
    /// The UI layout and bindings are defined entirely in VrModSettingsView.xaml.
    /// This code-behind file exists primarily to initialize the XAML components
    /// via InitializeComponent(). All logic is in the ViewModel (VrModSettingsViewModel).
    ///
    /// The DataContext is set to a VrModSettingsViewModel instance by the plugin's
    /// GetSettingsView() method, enabling data binding to settings properties
    /// and the merged source rows (enable checkbox, website link, scan status).
    /// </summary>
    public partial class VrModSettingsView : UserControl
    {
        /// <summary>
        /// Creates a new instance and initializes the XAML components.
        ///
        /// The DataContext should be set to a VrModSettingsViewModel instance
        /// by the plugin (in VrModTagPlugin.GetSettingsView) before this
        /// UserControl is displayed.
        /// </summary>
        public VrModSettingsView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Opens the website of the source row whose name link was clicked.
        ///
        /// The Hyperlink's DataContext is the row's VrModSourceRowViewModel (inherited
        /// from the ItemsControl item), so the website URL is read from there and
        /// opened in the user's default browser. Failures (no default browser,
        /// bad URL) are silently ignored — a link click must never crash settings.
        /// </summary>
        /// <param name="sender">The clicked Hyperlink.</param>
        /// <param name="e">RoutedEventArgs.</param>
        private void OnSourceWebsiteClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Hyperlink link)
                return;
            if (link.DataContext is not VrModSourceRowViewModel row)
                return;
            OpenInDefaultBrowser(row.WebsiteUrl);
        }

        /// <summary>
        /// Opens the per-source "last run" log dialog for the source row whose per-row button
        /// was clicked. The button sits in a cell DataTemplate, so its DataContext is the row's
        /// VrModSourceRowViewModel. A last-run click must never crash settings, so an unresolvable
        /// row is silently ignored.
        /// </summary>
        /// <param name="sender">The clicked Button.</param>
        /// <param name="e">RoutedEventArgs.</param>
        private void OnShowLastRunClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement element
                || element.DataContext is not VrModSourceRowViewModel row)
                return;
            OpenLastRunDialog(row);
        }

        /// <summary>
        /// Opens the per-source last-run log dialog (modal) for the given row, owned by the
        /// window hosting this settings view. Mirrors how the review dialog is shown: set the
        /// owner, ShowDialog (modal), then re-raise Playnite's child-ownership re-evaluation so
        /// the owner's dim (Opacity 0.4, set while we were open) is lifted when we close.
        /// </summary>
        /// <param name="row">The source row whose last run is shown.</param>
        private void OpenLastRunDialog(VrModSourceRowViewModel row)
        {
            var dialog = new VrModSourceLogDialog(row.DisplayName, row.LastRun);
            var owner = Window.GetWindow(this);
            if (owner != null)
                dialog.Owner = owner;
            else
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            VrModReviewDialog.NotifyPlayniteChildOwnershipChanged(owner);
        }

        /// <summary>
        /// Opens a URL in the user's default browser. Failures are silently ignored.
        /// </summary>
        /// <param name="url">The URL to open.</param>
        private static void OpenInDefaultBrowser(string url)
        {
            // Validation and the shell hand-off live in WebUrl. A refusal is the shell saying "no
            // application is associated with this URL type", which this dialog cannot report more
            // usefully than the browser not appearing.
            WebUrl.TryOpenInDefaultBrowser(url);
        }
    }
}
