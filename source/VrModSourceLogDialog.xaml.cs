using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Playnite.VRMods
{
    /// <summary>
    /// A small themed, read-only dialog that shows the record of the last scan for one source:
    /// when it ran (local time), the result (Success / No data / Failed) with the failure reason
    /// on the line directly below it when it failed (HTTP status + text, "request timed out
    /// after N s", or the socket cause), how many entries it returned, and a multiline,
    /// selectable debug log the source appended while fetching and parsing (including per-game
    /// lines that failed to extract). Log and error are selectable so they can be copied; the
    /// log is the stretchy element, so it fills the window and grows on resize.
    ///
    /// The data is the in-memory <see cref="VrModTagSettings.SourceStatus"/> record the tag
    /// service keeps per source. It is <c>[DontSerialize]</c>, so it resets to "Never scanned"
    /// when Playnite restarts — a failed source re-fails on the next scan, so the diagnosis
    /// still works. Opened from the add-on settings Sources grid (a per-row "Last run" button
    /// and a right-click context menu on any row); the close button just closes it.
    /// </summary>
    public partial class VrModSourceLogDialog : Window
    {
        /// <summary>
        /// Creates the dialog for one source's last-run record.
        /// </summary>
        /// <param name="sourceName">The source's display name (shown as the heading and in the title).</param>
        /// <param name="lastRun">The source's last-run record, or null when it has never been
        /// scanned (e.g. right after a Playnite restart, since scan statuses are in-memory).</param>
        public VrModSourceLogDialog(string sourceName, SourceStatus? lastRun)
        {
            InitializeComponent();
            SourceNameText.Text = sourceName;
            Title = $"VRMod: {sourceName} — last run";

            if (lastRun == null)
            {
                // Never scanned since this Playnite run: no detail rows, no error line
                // (ErrorText stays collapsed inside the collapsed DetailGrid).
                DetailGrid.Visibility = Visibility.Collapsed;
                NeverScannedText.Text = "This source has not been scanned yet. Scan statuses reset when Playnite restarts, so this is empty until the next scan.";
                // Setting the text is not enough: the element ships Collapsed in the XAML (that is
                // the row a scanned source must not show), so without this line the hint is written
                // and never displayed - a never-scanned source opened this dialog and saw only its
                // own name. Found by an out-of-repo GUI harness that instantiates this real
                // dialog instead of a copy of its XAML. That harness is not part of this repo and
                // could not be, so the guard that ships with the code is the test
                // (VrModSourceLogDialogTests) rather than the harness that found the defect.
                NeverScannedText.Visibility = Visibility.Visible;

                // The 360 DIU default, the 300 DIU floor and the stretch row all exist to give a
                // debug log room, and here there is no log: measured, that left 271 empty DIU of 309
                // - a dialog 88% blank. This case is a heading and two wrapped lines, so it gets a
                // small fixed height and stays resizable by hand.
                // SizeToContent is the obvious alternative and it clips here: WPF measures a wrapping
                // TextBlock against an infinite width, sizes the window for one line, and the real
                // client width then wraps that line into text the window cannot show (measured: 60
                // DIU of content, the hint cut off mid-sentence).
                Height = 170;
                MinHeight = 120;
            }
            else
            {
                // "G" means "the user's own short date/time convention", so the culture is stated
                // rather than left implicit (CA1305 asks for the same reason: an implicit culture is
                // a locale decision nobody made on purpose).
                LastScanText.Text = lastRun.LastScanTime.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);

                // Three states, matching the settings grid: failed / succeeded-but-empty / success.
                string result;
                string resultBrushKey;
                Brush resultFallback;
                if (!lastRun.LastResult)
                {
                    result = "Failed";
                    resultBrushKey = "NegativeRatingBrush";
                    resultFallback = Brushes.Red;
                }
                else if (lastRun.LastEntryCount == 0)
                {
                    result = "No data";
                    resultBrushKey = "MixedRatingBrush";
                    resultFallback = Brushes.Orange;
                }
                else
                {
                    result = "Success";
                    resultBrushKey = "PositiveRatingBrush";
                    resultFallback = Brushes.Green;
                }
                ResultText.Text = result;
                ResultText.Foreground = ThemeBrush(resultBrushKey, resultFallback);
                EntriesText.Text = lastRun.LastEntryCount.ToString(CultureInfo.CurrentCulture);
                // "found" is the source-side count (what this run's entries carry); the
                // bracketed "applied" is the apply-side count (what was actually applied/
                // ensured to matched games this run).
                TagCountText.Text = $"{lastRun.LastTagCount} ({lastRun.LastTagCountApplied} applied)";
                LinkCountText.Text = $"{lastRun.LastLinkCount} ({lastRun.LastLinkCountApplied} applied)";

                // Show the debug log section when the run produced log lines.
                if (lastRun.LastRunLog != null && lastRun.LastRunLog.Count > 0)
                {
                    DebugLogText.Text = string.Join("\n", lastRun.LastRunLog);
                    DebugLogLabel.Visibility = Visibility.Visible;
                    DebugLogBox.Visibility = Visibility.Visible;
                }
                else
                {
                    DebugLogLabel.Visibility = Visibility.Collapsed;
                    DebugLogBox.Visibility = Visibility.Collapsed;
                }

                // The failure reason is an inline line directly under Result (there is no
                // separate error pane): shown only when the scan failed and produced a
                // message. The debug log (below) carries the full detail, selectable for
                // copy & paste.
                if (!lastRun.LastResult && !string.IsNullOrEmpty(lastRun.LastError))
                {
                    ErrorText.Text = lastRun.LastError;
                    ErrorText.Foreground = ThemeBrush("NegativeRatingBrush", Brushes.Red);
                    ErrorText.Visibility = Visibility.Visible;
                }
                else
                {
                    ErrorText.Visibility = Visibility.Collapsed;
                }
            }

            // Playnite dims its own windows that own a child window (HasChildWindow -> Opacity
            // 0.4) and re-evaluates only for its own WindowBase windows. This dialog is a plain
            // Window, so raise the re-evaluation around its open/close the way the review dialog
            // does (see VrModReviewDialog.NotifyPlayniteChildOwnershipChanged): dim the owner
            // while we are open, and lift the dim when we close — otherwise a dim set while we
            // were open would stick (the "grayed out" window).
            ContentRendered += (s, e) => VrModReviewDialog.NotifyPlayniteChildOwnershipChanged(Owner);
            Closed += (s, e) => VrModReviewDialog.NotifyPlayniteChildOwnershipChanged(Owner);
        }

        /// <summary>
        /// Handles the cloned Playnite chrome's close button: closes the dialog (read-only,
        /// nothing to save).
        /// </summary>
        /// <param name="sender">The close button.</param>
        /// <param name="e">RoutedEventArgs.</param>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Resolves a Playnite theme brush by resource key so the result color tracks the active
        /// theme (same keys the settings grid's ErrorToBrushConverter uses). Falls back to
        /// <paramref name="fallback"/> when the key is unavailable (no application, or the theme
        /// doesn't define the key — e.g. unit tests running without a theme).
        /// </summary>
        /// <param name="key">The theme resource key (e.g. "PositiveRatingBrush").</param>
        /// <param name="fallback">Static brush returned when the key is unavailable.</param>
        /// <returns>The theme brush, or the fallback.</returns>
        private static Brush ThemeBrush(string key, Brush fallback)
        {
            var app = Application.Current;
            if (app != null && app.TryFindResource(key) is Brush brush)
                return brush;
            return fallback;
        }
    }
}
