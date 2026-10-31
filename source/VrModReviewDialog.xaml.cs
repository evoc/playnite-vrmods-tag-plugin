using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace Playnite.VRMods
{
    /// <summary>
    /// User interface for reviewing and confirming VR mod match decisions.
    ///
    /// This dialog is shown after a full scan when there are Pending matches
    /// (matches that need user confirmation before tags are applied).
    ///
    /// The dialog displays a DataGrid with one row per Pending match, showing:
    /// - Source name (e.g., "RototRobot VRMods-List")
    /// - Source game name (from the VR mod source)
    /// - Playnite game name
    /// - Category (e.g., "UEVR Profiles", "Flat2VR Port")
    /// - Match score (0.0–1.0, higher = more confident match)
    /// - Status (text only): the decision combined with its saved state — "Saved" / "New"
    ///   (accepted), "Rejected" / "Rejected (new)", or "Pending" — colored by the decision and
    ///   italic when not yet saved to JSON. Double-click a status cell to cycle
    ///   Pending → Accepted → Rejected.
    /// - Saved at: when the current decision was last saved to JSON ("—" when none)
    ///
    /// Actions:
    /// - Save: Apply all Accept/Reject decisions and close
    /// - Accept All / Accept Selected: mark the (selected or all) matches Accepted
    /// - Reject All / Reject Selected: mark the (selected or all) matches Rejected
    /// - Cancel: Close without saving (Pending matches remain Pending)
    ///
    /// Each entry's <see cref="VrModReviewMatchEntry.Status"/> is the single source of truth;
    /// Save, the bulk buttons, the right-click menu, and the status double-click all read/write
    /// it directly (no checkbox / visual-tree round-trip). The bulk buttons act on the
    /// DataGrid's selection when one or more rows are selected, otherwise on every match.
    ///
    /// The dialog uses BufferedUpdate to batch all database changes (tag assignments/removals)
    /// into a single transaction, improving performance and preventing partial updates.
    ///
    /// After saving, a notification is shown with the count of accepted/rejected matches.
    /// Settings are persisted immediately so decisions survive plugin restarts.
    /// </summary>
    public partial class VrModReviewDialog : Window
    {
        private readonly IPlayniteAPI _api;
        private readonly VrModTagPlugin _plugin;

        /// <summary>Playnite's logger; used for the review dialog's own failures (the plugin's
        /// logger field is private to the plugin).</summary>
        private static readonly ILogger Log = LogManager.GetLogger();
        private readonly List<VrModReviewMatchEntry> _matches;
        private ListCollectionView? _matchesView;
        private string _filterText = string.Empty;

        // Time filter: "now" captured at construction (stable for the dialog's life) and the
        // current window in seconds (-1 = "Always", i.e. no limit).
        private DateTime _filterNowUtc;
        private int _timeFilterSeconds = -1;

        // Changed-only filter: when true, only rows whose tags changed since the last accept
        // are shown (the "Changed only" checkbox). A discrete toggle — applied immediately,
        // no debounce (unlike the keystroke text filter).
        private bool _changedOnly;

        // Status filter: the set of statuses the user wants to see (multi-select; an empty set
        // means "show all"). Driven by the "Status…" menu (checkable items + quick actions).
        private readonly HashSet<MatchDecision> _statusFilter = new();

        // Debounces the filter box: the filter is applied only after the user pauses
        // typing for this many milliseconds, so a large match list is not re-filtered
        // and re-rendered on every keystroke.
        internal const int FilterDebounceMs = 250;
        private readonly DispatcherTimer _filterDebounce;

        // The grid's columns with their menu name, header text, and header tooltip.
        // Populated in RegisterColumns() (called from the constructor, after
        // InitializeComponent) and used to build the header elements.
        private readonly List<ColumnSpec> _columnSpecs = new List<ColumnSpec>();

        /// <summary>
        /// Constructs the review dialog.
        ///
        /// Steps:
        /// 1. Stores references to Playnite API, plugin, and match list
        /// 2. Initializes the XAML components (InitalizeComponent)
        /// 3. Sets DataContext to the matches list (for DataGrid binding)
        /// 4. Sets window title with match count
        /// </summary>
        /// <param name="api">Playnite API for database access and notifications.</param>
        /// <param name="plugin">The plugin instance for saving settings.</param>
        /// <param name="matches">List of Pending matches requiring user review.</param>
        public VrModReviewDialog(IPlayniteAPI api, VrModTagPlugin plugin, List<VrModReviewMatchEntry> matches)
        {
            _api = api;
            _plugin = plugin;
            // Collapse rows that point at the same (source, category, game) triple: only the same
            // source + category + game (reached through several library entries) collapses to one
            // row. Two DIFFERENT sources for one game stay as two rows (each keeps its own deep
            // link); the same source listing a game under two categories also stays as two rows.
            _matches = DedupeByGame(matches);
            InitializeComponent();
            DataContext = CreateMatchesView();
            Title = $"VRMod: Review Matches ({_matches.Count})";
            // Column headers are only in the visual tree once the grid is loaded, so the
            // header-click sort wiring is deferred to the grid's Loaded event.
            MatchesDataGrid.Loaded += OnMatchesGridLoaded;
            MatchesDataGrid.SelectionChanged += OnSelectionChanged;
            // Right-click a row → scope the context menu to that row (select it first).
            MatchesDataGrid.PreviewMouseRightButtonUp += OnGridPreviewMouseRightButtonUp;
            // Double-click a row → cycle that row's status (see StatusCell_MouseDoubleClick).
            MatchesDataGrid.MouseDoubleClick += StatusCell_MouseDoubleClick;
            // Debounce the filter box: apply the filter once per typing pause, not per
            // keystroke, so a large match list isn't re-filtered/re-rendered continuously.
            _filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FilterDebounceMs) };
            _filterDebounce.Tick += OnFilterDebounceTick;
            // The clear button starts hidden (the filter box is empty at construction).
            UpdateFilterClearButton();
            // Register the columns (menu name, header text, tooltip) and build their header
            // elements. No sort arrow in the default order — arrows appear only after a
            // header click.
            RegisterColumns();
            ApplyColumnHeaders(null, null);
            UpdateEmptyHint();
            // Capture "now" once at construction so the time-filter window is stable for the
            // life of the dialog (it doesn't drift as the user browses).
            _filterNowUtc = DateTime.UtcNow;
            Closed += OnWindowClosed;
            // Playnite dims its own windows that own a child window (HasChildWindow -> Opacity 0.4)
            // and re-evaluates only when one of ITS OWN windows (WindowBase) loads/closes. Our
            // dialog is a plain Window, so without this its open/close is invisible to that
            // mechanism: the owner would never be re-dimmed while we are open, and a dim that was
            // set while we were open (e.g. by a nested Playnite dialog) would stick afterwards
            // (the "grayed out" window). Raising Playnite's re-evaluation around our open/close
            // reproduces the exact behavior its own dialogs get (see the helper below).
            ContentRendered += (s, e) => NotifyPlayniteChildOwnershipChanged(Owner);
        }

        /// <summary>
        /// Builds the sorted collection view that backs the matches DataGrid.
        ///
        /// Wraps the match list in a <see cref="ListCollectionView"/> so the grid is
        /// sortable by the default order and by user column-header clicks. The default
        /// order puts review-pending matches first and already-decided (Accepted) matches
        /// last: primary is <see cref="VrModReviewMatchEntry.StatusSortRank"/> ascending
        /// (Pending → Rejected → Accepted), then the stable secondary tiebreakers Playnite
        /// game name ascending, then source name ascending. Clicking a column header makes
        /// that column the primary sort (see <see cref="RebuildSortDescriptions"/>); the
        /// secondary tiebreakers are always kept. The view presents a sorted order over
        /// <c>_matches</c> without reordering the underlying list, so Save still processes
        /// every match.
        /// </summary>
        private ListCollectionView CreateMatchesView()
        {
            var view = new ListCollectionView(_matches);
            _matchesView = view;
            RebuildSortDescriptions(nameof(VrModReviewMatchEntry.StatusSortRank), ListSortDirection.Ascending);
            view.Filter = MatchFilter;
            return view;
        }

        /// <summary>
        /// Secondary sort criteria that always trail the primary sort (a column-header click
        /// or the default status order): Playnite game name ascending, then source name
        /// ascending. The primary criterion is the clicked column, or
        /// <see cref="VrModReviewMatchEntry.StatusSortRank"/> for the default order.
        /// </summary>
        private static readonly SortDescription[] SecondarySorts =
        {
            new SortDescription("PlayniteGame.Name", ListSortDirection.Ascending),
            new SortDescription("SourceName", ListSortDirection.Ascending),
        };

        /// <summary>
        /// Subscribes every sortable column header to its click so header-click sorting can
        /// keep the secondary rules. Wired on grid load (column headers only exist in the
        /// visual tree once the grid is loaded, so they cannot be hooked in the constructor).
        /// </summary>
        private void OnMatchesGridLoaded(object sender, RoutedEventArgs e)
        {
            // Own the sort entirely: the grid's Sorting event fires on each header click BEFORE
            // WPF applies its single-level sort. Handling it (e.Handled=true) suppresses that
            // built-in sort, so this dialog applies the multi-level sort itself (primary column
            // + the fixed secondary tiebreakers). CanUserSortColumns stays True so the headers
            // remain clickable and show the sort direction.
            MatchesDataGrid.Sorting += OnMatchesGridSorting;

            // No sort arrow in the default order (a deliberate choice): the view is already
            // sorted by status, but an arrow would claim a user action that hasn't happened.
            // Arrows appear only after a header click (OnMatchesGridSorting).

            // The DataGrid auto-selects its first row as it realizes items, which would make
            // the bulk buttons read "…Selected" and act on that single row the moment the dialog
            // opens. Clear it so the dialog starts in "…All" scope (the SelectionChanged handler
            // flips the labels back to "…All").
            MatchesDataGrid.SelectedItems.Clear();
        }


        /// <summary>
        /// Reflects the active primary sort on the column headers: the sorted column's
        /// header shows the direction's sort arrow; every other column's header gets none.
        /// (Playnite's themed header template has no built-in arrow, so the arrow is part
        /// of the code-built header content — see <see cref="BuildColumnHeader"/>.) The
        /// grid's <see cref="DataGridColumn.SortDirection"/> state is kept in sync so the
        /// sort direction toggles correctly on repeated clicks.
        /// </summary>
        private void UpdateSortArrows(string primaryMemberPath, ListSortDirection direction)
        {
            foreach (var spec in _columnSpecs)
            {
                var isPrimary = string.Equals(
                    spec.Column.SortMemberPath, primaryMemberPath, StringComparison.Ordinal);
                spec.Column.SortDirection = isPrimary ? direction : (ListSortDirection?)null;
            }
            ApplyColumnHeaders(_columnSpecs.FirstOrDefault(s =>
                    string.Equals(s.Column.SortMemberPath, primaryMemberPath, StringComparison.Ordinal))?.Column,
                direction);
        }

        /// <summary>
        /// Rebuilds every column's header element from its spec, adding the sort arrow to the
        /// <paramref name="sortedColumn"/> (when non-null) in the given direction. Called at
        /// construction (no arrow), after every header-click sort, and whenever a column's
        /// state changes.
        /// </summary>
        private void ApplyColumnHeaders(DataGridColumn? sortedColumn, ListSortDirection? direction)
        {
            foreach (var spec in _columnSpecs)
            {
                string? arrow = null;
                if (sortedColumn != null && spec.Column == sortedColumn)
                {
                    arrow = direction == ListSortDirection.Descending ? SortArrowDescending : SortArrowAscending;
                }
                spec.Column.Header = BuildColumnHeader(spec, arrow);
            }
        }

        /// <summary>
        /// Registers the dialog's columns with their canonical menu name, visible header text
        /// (may be a symbol — the Link column — or empty), and header tooltip. Called once at
        /// construction, after <c>InitializeComponent</c>.
        /// </summary>
        private void RegisterColumns()
        {
            _columnSpecs.Add(new ColumnSpec(StatusColumn, "Status", "Status",
                "Your decision for this match. Double-click a row to cycle Pending → Accepted → Rejected. Italic means it is not saved yet."));
            _columnSpecs.Add(new ColumnSpec(SourceColumn, "Source", "Source",
                "The website/service this match came from."));
            _columnSpecs.Add(new ColumnSpec(SourceEntryColumn, "Source Entry", "Source Entry",
                "The game or mod listing on the source's site."));
            _columnSpecs.Add(new ColumnSpec(GameColumn, "Game", "Game",
                "The game in your Playnite library that this match maps to."));
            _columnSpecs.Add(new ColumnSpec(ScoreColumn, "Score", "Score",
                "How confident the name match is (1.00 = exact)."));
            _columnSpecs.Add(new ColumnSpec(SavedAtColumn, "Saved at", "Saved at",
                "When the current decision was saved to the library (— = never)."));
            _columnSpecs.Add(new ColumnSpec(TagsColumn, "Tags", "Tags",
                "Tags applied to the game if you accept this match."));
            _columnSpecs.Add(new ColumnSpec(ChangedColumn, "Changed", "Changed",
                "Tags that changed since the last accept (+ added / − removed)."));
            _columnSpecs.Add(new ColumnSpec(LinkColumn, "Link to Mod", "\u2197",
                "Open the exact mod/game page for this row on its source."));
        }

        /// <summary>Ascending sort arrow glyph (rendered inside the sorted column's header).</summary>
        internal const string SortArrowAscending = "\u25B2";

        /// <summary>Descending sort arrow glyph (rendered inside the sorted column's header).</summary>
        internal const string SortArrowDescending = "\u25BC";

        /// <summary>
        /// Builds one column header element: a left-aligned stack with the header text (may be
        /// a symbol) and, when <paramref name="arrow"/> is non-null, the sort arrow. The
        /// canonical menu name is carried in <see cref="FrameworkElement.Tag"/> (the Columns…
        /// menu reads it there), and the tooltip is attached to the outer grid so hovering
        /// anywhere on the header shows it.
        /// </summary>
        internal static FrameworkElement BuildColumnHeader(ColumnSpec spec, string? arrow)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var text = new TextBlock
            {
                Text = spec.HeaderText ?? string.Empty,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(text);

            if (arrow != null)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = arrow,
                    FontSize = 9,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var header = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            header.Children.Add(panel);
            header.Tag = spec.Name;
            if (!string.IsNullOrEmpty(spec.Tooltip))
            {
                header.ToolTip = spec.Tooltip;
            }
            return header;
        }

        /// <summary>
        /// One column in the matches grid: its live <see cref="DataGridColumn"/>, canonical
        /// menu name, visible header text, and header tooltip.
        /// </summary>
        internal sealed class ColumnSpec
        {
            public ColumnSpec(DataGridColumn column, string name, string headerText, string tooltip)
            {
                Column = column;
                Name = name;
                HeaderText = headerText;
                Tooltip = tooltip;
            }

            public DataGridColumn Column { get; }

            /// <summary>Canonical name used by the Columns… menu (may differ from the visible header text).</summary>
            public string Name { get; }

            /// <summary>Text (or symbol) shown in the header cell.</summary>
            public string HeaderText { get; }

            /// <summary>Tooltip shown when hovering the header.</summary>
            public string Tooltip { get; }
        }

        /// <summary>
        /// Pure, testable empty-state rule: the hint is shown only when the filters hide every
        /// row AND at least one filter is actually active (so a genuinely empty match list is
        /// not mistaken for "filtered out").
        /// </summary>
        internal static bool ShouldShowEmptyHint(int visibleCount, bool anyFilterActive)
        {
            return visibleCount == 0 && anyFilterActive;
        }

        /// <summary>
        /// Toggles the empty-state hint over the grid: visible when the active filters hide
        /// every row, hidden otherwise. Called after every filter re-application.
        /// </summary>
        private void UpdateEmptyHint()
        {
            var anyFilterActive = !string.IsNullOrEmpty(_filterText) || _timeFilterSeconds >= 0 || _changedOnly || _statusFilter.Count > 0;
            EmptyHint.Visibility = ShouldShowEmptyHint(_matchesView?.Count ?? 0, anyFilterActive)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// Owns the sort for a header click: suppresses the grid's built-in single-level sort
        /// (<see cref="DataGridSortingEventArgs.Handled"/>) and applies the multi-level sort -
        /// the clicked column as the primary criterion, then the secondary tiebreakers
        /// (<see cref="SecondarySorts"/>). The direction toggles when the clicked column is
        /// already the primary, otherwise it starts ascending.
        /// </summary>
        private void OnMatchesGridSorting(object sender, DataGridSortingEventArgs e)
        {
            if (_matchesView == null || string.IsNullOrEmpty(e.Column.SortMemberPath))
            {
                return;
            }

            // Suppress WPF's single-level sort; apply the multi-level sort ourselves.
            e.Handled = true;

            // We own the sort now, so the column's SortDirection reflects the state before this
            // click: toggle only if this column was already sorted, otherwise start ascending.
            var direction = e.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            RebuildSortDescriptions(e.Column.SortMemberPath, direction);
            UpdateSortArrows(e.Column.SortMemberPath, direction);
        }

        /// <summary>
        /// Rebuilds the matches view sort as: the given primary criterion (member path +
        /// direction), followed by the secondary tiebreakers
        /// (<see cref="SecondarySorts"/>). Used both for the default order (primary =
        /// <see cref="VrModReviewMatchEntry.StatusSortRank"/> ascending) and for a
        /// column-header click (primary = the clicked column).
        /// </summary>
        private void RebuildSortDescriptions(string primaryMemberPath, ListSortDirection primaryDirection)
        {
            if (_matchesView == null)
            {
                return;
            }

            _matchesView.SortDescriptions.Clear();
            _matchesView.SortDescriptions.Add(new SortDescription(primaryMemberPath, primaryDirection));
            foreach (var secondary in SecondarySorts)
            {
                _matchesView.SortDescriptions.Add(secondary);
            }
        }

        /// <summary>
        /// Handles the "Save" button click.
        ///
        /// Partitions all matches by their current <see cref="VrModReviewMatchEntry.Status"/>
        /// (the single source of truth, written by the status column, the bulk buttons, the
        /// right-click menu, and the status double-click), and applies the corresponding action:
        ///
        /// For Accepted matches:
        /// 1. Creates or retrieves the VRMod category tag (e.g., "VRMod UEVR Profiles")
        /// 2. Adds the tag to the game's TagIds (creates list if null)
        /// 3. Ensures the umbrella "VRMod" tag is also present
        /// 4. Records the decision as Accepted in settings
        ///
        /// For Rejected matches:
        /// 1. Removes the VRMod category tag from the game (if present)
        /// 2. If no VRMod tags remain on the game, removes the umbrella "VRMod" tag
        /// 3. Records the decision as Rejected in settings
        ///
        /// All database changes are batched in a BufferedUpdate for atomicity.
        /// Settings are saved immediately after processing.
        ///
        /// For Pending matches:
        /// - No action is taken — the match remains Pending (a deferral, not a rejection).
        /// </summary>
        /// <param name="sender">The Save button.</param>
        /// <param name="e">Click event arguments.</param>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // The persistence runs on a background thread under Playnite's global progress window
            // while this dialog stays open with its footer disabled, so no second write can race
            // it. Everything this method does to WINDOW state happens after ActivateGlobalProgress
            // returns - see FinishSave. The worker's only job is the database and settings work.
            FooterPanel.IsEnabled = false;
            var games = _matches.Select(m => m.PlayniteGame).Distinct().ToList();
            var matchCount = _matches.Count;
            var totalWork = matchCount + games.Count;
            var progress = _api.Dialogs.ActivateGlobalProgress(
                a =>
                {
                    // The action runs on a background thread; ApplyDecisions and
                    // ReconcileSourceLinks issue their Games.Update calls in one BufferedUpdate,
                    // so this is safe off the UI thread (the same pattern as the reset's
                    // RemoveAllVrModTags).
                    using (_api.Database.BufferedUpdate())
                    {
                        ApplyDecisions(_api, _plugin.TagService.Settings, _matches,
                            (current, total, gameName) =>
                            {
                                a.ProgressMaxValue = totalWork;
                                a.CurrentProgressValue = current;
                                a.Text = $"Saving decisions…\n{gameName}\n{current} / {matchCount}";
                            });

                        // Source links follow the decisions: adding them for newly accepted
                        // matches and removing them for matches rejected or reverted to
                        // Pending happens in the same transaction as the tag changes.
                        for (var i = 0; i < games.Count; i++)
                        {
                            a.CurrentProgressValue = matchCount + i + 1;
                            a.Text = $"Saving source links…\n{games[i].Name}\n{matchCount + i + 1} / {totalWork}";
                            _plugin.TagService.ReconcileSourceLinks(games[i]);
                        }
                    }
                    _plugin.SavePluginSettings(_plugin.TagService.Settings);
                },
                new GlobalProgressOptions("Saving VRMod decisions", false) { IsIndeterminate = false });

            FinishSave(progress);
        }

        /// <summary>
        /// Everything the Save button does to window state, once Playnite's progress window is
        /// gone: report a failure and stay open, or drain the queued UI work and close.
        ///
        /// Two orderings, and both are the reason this runs after the call instead of inside the
        /// worker action:
        ///
        /// 1. Parent closes after its child. Playnite's <c>WindowFactory.CreateAndOpenDialog</c>
        ///    sets <c>Window.Owner = WindowManager.CurrentWindow</c>, so while this dialog is open
        ///    the progress window is its CHILD, and Playnite's own <c>Close()</c> carries a
        ///    workaround for a WPF deadlock that happens "if parent of modal window is closed
        ///    before the child window itself is closed" (dotnet/wpf#277) — a guard that only
        ///    protects windows Playnite closes, not this dialog closing its own child. Closing
        ///    here, after <c>ShowDialog</c> has returned, means the progress window is provably
        ///    closed first. The previous shape queued <c>Close()</c> from the worker while the
        ///    child was still open and only survived because Playnite marshals its own close at a
        ///    higher dispatcher priority than our <c>BeginInvoke</c>.
        /// 2. The undim stays the last window-state change. The <see cref="OnWindowClosed"/>
        ///    re-evaluation lifts the owner's dim, and a UI notification queued by the
        ///    BufferedUpdate flush must not land after that and re-apply it (the "stuck dim"
        ///    reported on the Save path). Yielding at <see cref="DispatcherPriority.Background"/>
        ///    runs everything already queued at Normal priority or above first, so the
        ///    notification is served while this dialog is still open. It covers items already
        ///    queued when <c>ShowDialog</c> returned; nothing Playnite queues later can re-dim,
        ///    because this dialog is no longer a child of the owner by then.
        /// </summary>
        /// <param name="progress">What Playnite reports for the save action. Its result was
        /// discarded before: a save whose background action threw closed this dialog exactly like
        /// a successful one.</param>
        private void FinishSave(GlobalProgressResult progress)
        {
            var failure = ProgressFailureText("Saving VRMod decisions", progress);
            if (failure != null)
            {
                Log.Error("VRMod Review Dialog: " + failure);
            }

            if (!ReviewDialogMayClose(progress))
            {
                // Keep the row decisions on screen: the database may hold a partial write, and
                // the user has to see that before choosing what to do next.
                FooterPanel.IsEnabled = true;
                if (failure != null)
                    _api.Dialogs.ShowErrorMessage(failure, "VRMod VR mod tags");
                return;
            }

            Dispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            Close();
        }

        /// <summary>
        /// Pure, testable half of the failure report: the message for a completed global-progress
        /// action, or null when there is nothing to report. A user cancellation is not a failure
        /// (no message, no error dialog), but <see cref="ReviewDialogMayClose"/> still keeps the
        /// dialog open for it. A <c>Result == false</c> with no exception is Playnite closing the
        /// progress window some other way and is reported as an incomplete operation.
        /// </summary>
        /// <param name="operation">Names the operation in the message, e.g. "Saving VRMod decisions".</param>
        /// <param name="progress">Playnite's result for the action.</param>
        internal static string? ProgressFailureText(string operation, GlobalProgressResult? progress)
        {
            if (progress == null)
                return $"{operation} did not complete.";
            if (progress.Error != null)
                return $"{operation} failed: {progress.Error.Message}";
            if (progress.Canceled)
                return null;
            if (progress.Result == false)
                return $"{operation} did not complete.";
            return null;
        }

        /// <summary>
        /// Pure, testable half of the close decision: this dialog may close only when the action
        /// finished without an exception, without being cancelled, and with Playnite reporting a
        /// successful result. Any other outcome leaves it open so the user sees the state the
        /// database is actually in.
        /// </summary>
        /// <param name="progress">Playnite's result for the action.</param>
        internal static bool ReviewDialogMayClose(GlobalProgressResult? progress)
        {
            if (progress == null)
                return false;
            return progress.Error == null && !progress.Canceled && progress.Result != false;
        }

        /// <summary>
        /// Applies the review dialog's current row decisions to the database and settings — the
        /// core of the Save button. Every row's status is mirrored into the decision store so
        /// what the user sees is what gets saved: Accepted rows have their VRMod tags applied and
        /// are recorded as accepted; Rejected rows have their tags removed and are recorded as
        /// rejected; Pending rows have any tags removed and any previously saved decision
        /// <b>cleared</b>, so the match is presented for review again on the next scan (a row that
        /// was never decided has nothing to clear). The caller runs this inside a database
        /// <c>BufferedUpdate</c> and persists settings afterwards.
        /// </summary>
        /// <param name="onProgress">Optional <c>(current, total, gameName)</c> progress report,
        /// called once per match (1-based, in processing order: accepted, then rejected, then
        /// pending) before its work — so a caller running this off the UI thread can drive a
        /// determinate progress bar (Save does).</param>
        internal static void ApplyDecisions(IPlayniteAPI api, VrModTagSettings settings, IList<VrModReviewMatchEntry> matches, System.Action<int, int, string>? onProgress = null)
        {
            // A row with no decision key cannot be persisted: the key is built where the row is
            // created (VrModTagService builds it from the listing's identity), and a write to an empty
            // key would file a decision no lookup can ever read. Skipping is better than throwing
            // here - an exception mid-loop would leave the tags of the rows before it applied with no
            // decision stored behind them. Loud in the log, since a row reaching Apply unkeyed is a
            // bug in whatever built it. The caller's list is left alone: this is the dialog's own
            // collection, and removing rows from it would change what the grid shows.
            var saveable = new List<VrModReviewMatchEntry>(matches.Count);
            foreach (var row in matches)
            {
                if (!string.IsNullOrEmpty(row.DecisionKey))
                {
                    saveable.Add(row);
                    continue;
                }
                System.Diagnostics.Debug.WriteLine(
                    $"[VrModReviewDialog] Review row has no decision key; its decision is not saved. source={row.SourceId} listing='{row.SourceGameName}' game='{row.PlayniteGame?.Name}'");
            }
            matches = saveable;

            var accepted = new List<VrModReviewMatchEntry>();
            var rejected = new List<VrModReviewMatchEntry>();
            var pending = new List<VrModReviewMatchEntry>();

            // Status is the single source of truth, so partition the matches by their current
            // decision:
            // Accepted → apply tags + record decision
            // Rejected → remove tags + record decision
            // Pending  → remove tags (if any) + clear a prior decision (back to "needs review")
            foreach (var match in matches)
            {
                if (match.Status == MatchDecision.Accepted)
                    accepted.Add(match);
                else if (match.Status == MatchDecision.Rejected)
                    rejected.Add(match);
                else
                    pending.Add(match); // Status == Pending
            }

            var total = matches.Count;
            var progress = 0;

            // Process accepted matches
            // One decision write for the whole batch instead of one per row: SetDecisionByKey clones the
            // published decision maps because readers cannot take the settings lock, so a per-row write
            // costs O(live decisions) and a 2 000-row Save costs O(n^2).
            var decisionWrites = new List<(string Key, MatchDecision Decision, string? SourceTags)>();
            var clearedKeys = new List<string>();
            foreach (var match in accepted)
            {
                progress++;
                onProgress?.Invoke(progress, total, match.PlayniteGame?.Name ?? string.Empty);
                // Ensure the game's TagIds list exists (the facet/umbrella blocks below need it).
                if (match.PlayniteGame!.TagIds == null)
                    match.PlayniteGame.TagIds = new List<Guid>();

                // Apply a category tag per (non-catalog) category — a mod may have several.
                // A catalog category contributes no category tag of its own - only the
                // umbrella, which is added below.
                foreach (var category in match.Categories)
                {
                    // IsKnownTagValue, not only "is this a catalog row": the category text came from a
                    // website, and this is one of the two places that create tags in the library.
                    if (VrModCategories.IsKnownTagValue(category) && !VrModCategories.IsCatalogCategory(category))
                    {
                        var tag = GetOrCreateTag(api, VrModCategories.TagNameFor(category));
                        if (!match.PlayniteGame.TagIds.Contains(tag.Id))
                            match.PlayniteGame.TagIds.Add(tag.Id);
                    }
                }

                // Apply the 6DOF capability facet tag when the matched entry is 6DOF.
                if (match.Is6DOF == true)
                {
                    var sixDofTag = GetOrCreateTag(api, VrModCategories.Tag6DOF);
                    if (!match.PlayniteGame.TagIds.Contains(sixDofTag.Id))
                        match.PlayniteGame.TagIds.Add(sixDofTag.Id);
                }

                // Apply the first-person / third-person capability facet tags.
                if (match.IsFirstPerson == true)
                {
                    var fpTag = GetOrCreateTag(api, VrModCategories.TagFirstPerson);
                    if (!match.PlayniteGame.TagIds.Contains(fpTag.Id))
                        match.PlayniteGame.TagIds.Add(fpTag.Id);
                }
                if (match.IsThirdPerson == true)
                {
                    var tpTag = GetOrCreateTag(api, VrModCategories.TagThirdPerson);
                    if (!match.PlayniteGame.TagIds.Contains(tpTag.Id))
                        match.PlayniteGame.TagIds.Add(tpTag.Id);
                }

                // Optional quality/grade facet tags (orthogonal to category + 6DOF) - a match may have several.
                foreach (var q in match.Qualities)
                {
                    if (!VrModCategories.IsKnownTagValue(q)) continue;
                    var qualityTag = GetOrCreateTag(api, VrModCategories.TagNameForQuality(q));
                    if (!match.PlayniteGame.TagIds.Contains(qualityTag.Id))
                        match.PlayniteGame.TagIds.Add(qualityTag.Id);
                }
                // Optional motion-level facet tags (orthogonal) - a match may have several.
                foreach (var m in match.Motions)
                {
                    if (!VrModCategories.IsKnownTagValue(m)) continue;
                    var motionTag = GetOrCreateTag(api, VrModCategories.TagNameForMotion(m));
                    if (!match.PlayniteGame.TagIds.Contains(motionTag.Id))
                        match.PlayniteGame.TagIds.Add(motionTag.Id);
                }
                // Optional mod-status facet tags (orthogonal) - a match may carry several, because a
                // game's mods can be in different states at the same time.
                foreach (var s in match.ModStatuses)
                {
                    if (!VrModCategories.IsKnownTagValue(s)) continue;
                    var statusTag = GetOrCreateTag(api, VrModCategories.TagNameForStatus(s));
                    if (!match.PlayniteGame.TagIds.Contains(statusTag.Id))
                        match.PlayniteGame.TagIds.Add(statusTag.Id);
                }

                // Ensure the umbrella "VRMod" tag is present — an accepted match implies the
                // umbrella, exactly as a full scan adds it unconditionally (RunFullScanAsync).
                // (The old StartsWith("VRMod") guard was wrong: any category/facet/quality tag
                // added above already starts with "VRMod", so it suppressed the umbrella.) The
                // Contains check below keeps this idempotent.
                var umbrellaTag = GetOrCreateTag(api, VrModCategories.TagUmbrella);
                if (!match.PlayniteGame.TagIds.Contains(umbrellaTag.Id))
                    match.PlayniteGame.TagIds.Add(umbrellaTag.Id);

                // Record the decision as Accepted, storing the source's current tag set as the
                // changed-tag baseline (a later source change is detected on the next scan).
                // Keyed by the game's stable Id so each store duplicate of a same-name game
                // gets its own decision (a name-keyed decision was shared and wiped by the
                // stale-removal of a sibling duplicate).
                decisionWrites.Add((match.DecisionKey, MatchDecision.Accepted,
                    string.Join("\n", match.AppliedTagNames)));

                // In-place TagIds mutation is not tracked — persist via Update().
                api.Database.Games.Update(match.PlayniteGame);
            }

            // Process rejected matches
            foreach (var match in rejected)
            {
                progress++;
                onProgress?.Invoke(progress, total, match.PlayniteGame?.Name ?? string.Empty);
                RemoveVrModTagsFromGame(api, match);
                decisionWrites.Add((match.DecisionKey, MatchDecision.Rejected, null));
            }

            // Process pending matches that were previously decided (accepted/rejected) and are
            // now reverted to Pending: remove the tags (so a tag implies an accepted match) and
            // clear the stored decision, so the match is presented for review again next scan.
            // Without this, changing a rejected match to Pending and saving would silently re-load
            // as Rejected the next time the dialog opens. Matches that were never decided
            // (PersistedDecision == null) are already pending — nothing to clear.
            foreach (var match in pending)
            {
                progress++;
                onProgress?.Invoke(progress, total, match.PlayniteGame?.Name ?? string.Empty);
                if (match.PersistedDecision == null)
                    continue;
                RemoveVrModTagsFromGame(api, match);
                clearedKeys.Add(match.DecisionKey);
            }

            settings.SetDecisionsByKey(decisionWrites);
            settings.ClearDecisionsByKey(clearedKeys);
        }

        /// <summary>
        /// Finds a tag by exact name in the database, creating it if absent.
        /// </summary>
        private static Tag GetOrCreateTag(IPlayniteAPI api, string tagName)
        {
            var tag = api.Database.Tags.FirstOrDefault(t => t.Name == tagName);
            if (tag == null)
                tag = api.Database.Tags.Add(tagName);
            return tag;
        }

        /// <summary>
        /// Removes the VRMod category and capability/quality facet tags this match has, and the
        /// umbrella "VRMod" tag as well if no other VRMod tags remain, then persists the game.
        /// Facets are gated on the match's own capabilities so a sibling match's facets survive.
        /// </summary>
        private static void RemoveVrModTagsFromGame(IPlayniteAPI api, VrModReviewMatchEntry match)
        {
            // Remove every VRMod category tag this match has (if present) — a mod may have several.
            foreach (var category in match.Categories)
            {
                if (!VrModCategories.IsKnownTagValue(category)) continue;
                var categoryTag = api.Database.Tags.FirstOrDefault(t => t.Name == VrModCategories.TagNameFor(category));
                if (categoryTag != null && match.PlayniteGame.TagIds?.Contains(categoryTag.Id) == true)
                    match.PlayniteGame.TagIds.Remove(categoryTag.Id);
            }

            // Remove the capability/quality facet tags THIS match has (mirror of the category
            // removal above, and of the add in ApplyDecisions) — otherwise a rejected 6DOF
            // match would keep "VRMod 6DOF" until the next full scan cleaned it. Gated on the
            // match's own capabilities so a sibling match's facets survive.
            if (match.Is6DOF == true)
                RemoveTagIfPresent(api, match, VrModCategories.Tag6DOF);
            if (match.IsFirstPerson == true)
                RemoveTagIfPresent(api, match, VrModCategories.TagFirstPerson);
            if (match.IsThirdPerson == true)
                RemoveTagIfPresent(api, match, VrModCategories.TagThirdPerson);
            foreach (var q in match.Qualities)
                if (VrModCategories.IsKnownTagValue(q))
                    RemoveTagIfPresent(api, match, VrModCategories.TagNameForQuality(q));
            foreach (var m in match.Motions)
                if (VrModCategories.IsKnownTagValue(m))
                    RemoveTagIfPresent(api, match, VrModCategories.TagNameForMotion(m));
            foreach (var s in match.ModStatuses)
                if (VrModCategories.IsKnownTagValue(s))
                    RemoveTagIfPresent(api, match, VrModCategories.TagNameForStatus(s));

            // If no VRMod tags remain, remove the umbrella "VRMod" tag. Build the set of
            // VRMod tag IDs once (O(#tags)) and test TagIds membership (O(#tagIds)) — the
            // previous code ran a linear `Tags.FirstOrDefault(by id)` for every tag on the
            // game (O(#tagIds × #tags)). Read-only here (no tag is added in this method), so
            // the per-call set is exact; result is identical to the old projection.
            var vrModTagIds = new HashSet<Guid>(
                api.Database.Tags.Where(t => VrModCategories.IsPluginTagName(t.Name)).Select(t => t.Id));
            var hasVrModTagsRemaining = match.PlayniteGame.TagIds?.Any(id => vrModTagIds.Contains(id)) == true;
            if (!hasVrModTagsRemaining)
            {
                var umbrellaTag = api.Database.Tags.FirstOrDefault(t => t.Name == VrModCategories.TagUmbrella);
                if (umbrellaTag != null && match.PlayniteGame.TagIds?.Contains(umbrellaTag.Id) == true)
                    match.PlayniteGame.TagIds.Remove(umbrellaTag.Id);
            }

            // In-place TagIds mutation is not tracked — persist via Update().
            api.Database.Games.Update(match.PlayniteGame);
        }

        /// <summary>
        /// Removes the tag with the exact <paramref name="tagName"/> from the match's game if
        /// present (and the tag exists in the database). No-op when the tag is unknown or absent.
        /// </summary>
        private static void RemoveTagIfPresent(IPlayniteAPI api, VrModReviewMatchEntry match, string tagName)
        {
            var tag = api.Database.Tags.FirstOrDefault(t => t.Name == tagName);
            if (tag != null && match.PlayniteGame.TagIds?.Contains(tag.Id) == true)
                match.PlayniteGame.TagIds.Remove(tag.Id);
        }

        /// <summary>
        /// The rows the bulk Accept/Reject buttons act on: the DataGrid's selection when the
        /// user has selected one or more rows, otherwise every match (the "…All" behavior).
        /// </summary>
        private List<VrModReviewMatchEntry> GetBulkTargets()
        {
            return MatchesDataGrid.SelectedItems.Count > 0
                ? MatchesDataGrid.SelectedItems.OfType<VrModReviewMatchEntry>().ToList()
                : _matches;
        }

        /// <summary>
        /// Sets <see cref="VrModReviewMatchEntry.Status"/> on the bulk targets (the selection when
        /// present, otherwise every match) to the given decision. Data-driven: the Status text,
        /// its color/italic, and the "Saved at" cell all refresh via the entry's
        /// <see cref="INotifyPropertyChanged"/> notifications. Shared by the bulk buttons, the
        /// right-click context-menu items, and the status double-click.
        /// </summary>
        /// <param name="decision">The decision to apply to each target row.</param>
        private void SetStatusForTargets(MatchDecision decision)
        {
            foreach (var match in GetBulkTargets())
                match.Status = decision;
        }

        /// <summary>
        /// Handles the Accept bulk button. Marks the target rows Accepted.
        /// </summary>
        /// <param name="sender">The Accept button.</param>
        /// <param name="e">Click event arguments.</param>
        private void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            SetStatusForTargets(MatchDecision.Accepted);
        }

        /// <summary>
        /// Handles the Reject bulk button. Marks the target rows Rejected.
        /// </summary>
        /// <param name="sender">The Reject button.</param>
        /// <param name="e">Click event arguments.</param>
        private void RejectButton_Click(object sender, RoutedEventArgs e)
        {
            SetStatusForTargets(MatchDecision.Rejected);
        }

        /// <summary>
        /// Handles review hotkeys on the matches grid (only while the grid is focused, so typing
        /// in the filter box is unaffected). Enter accepts, R rejects, and P sets the current
        /// (cursor) row's status; Ctrl+S saves. Arrows / Tab and other keys are left unhandled so
        /// the grid's own navigation keeps working.
        /// </summary>
        private void MatchesDataGrid_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+S saves (the Save button is no longer the Enter default, so Enter is free for
            // the Accept hotkey).
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.S)
            {
                Save_Click(this, null!);
                e.Handled = true;
                return;
            }

            var decision = HotkeyDecision(e.Key, Keyboard.Modifiers);
            if (decision == null)
                return;

            var entry = MatchesDataGrid.CurrentItem as VrModReviewMatchEntry;
            if (entry == null)
                return;

            entry.Status = decision.Value;
            e.Handled = true;
        }

        /// <summary>
        /// Maps an unmodified hotkey to the decision it applies: Enter accepts, R rejects, P sets
        /// to Pending. Any other key, or any modified key (Ctrl/Alt/Shift), returns null (no status
        /// change) so arrows, Tab, and reserved combos are left alone. Pure and testable.
        /// </summary>
        internal static MatchDecision? HotkeyDecision(Key key, ModifierKeys modifiers)
        {
            if (modifiers != ModifierKeys.None)
                return null;
            switch (key)
            {
                case Key.Enter: return MatchDecision.Accepted;
                case Key.R: return MatchDecision.Rejected;
                case Key.P: return MatchDecision.Pending;
                default: return null;
            }
        }

        /// <summary>
        /// Window-level preview-key hook for the dialog's Alt accelerators (Alt+F/T/S/C/H/A/R),
        /// which fire from anywhere in the dialog. The grid's
        /// <see cref="MatchesDataGrid_KeyDown"/> only fires while the grid is focused, and a
        /// button keeps focus after a click, so the grid hook alone could not catch them once
        /// focus had moved off the grid. This is a tunneling handler: it sees the key before
        /// any child, but only acts on the Alt accelerators (and marks those handled); every
        /// other key is left alone, so the grid's Enter/R/P/Ctrl+S hotkeys and typing in the
        /// filter box are unaffected (Alt combos are modifier combos, never characters, so
        /// they cannot be mistyped into the filter).
        /// </summary>
        /// <param name="sender">The dialog window.</param>
        /// <param name="e">PreviewKeyDown event arguments.</param>
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var accelerator = AcceleratorForKey(e.Key, Keyboard.Modifiers);
            if (accelerator == ReviewAccelerator.None)
                return;

            // Direct-activate: each accelerator performs exactly what clicking the control
            // does (no intermediate focus step). The menu handlers cast <c>sender</c> to the
            // button, so the real button is passed.
            switch (accelerator)
            {
                case ReviewAccelerator.FocusFilter:
                    FilterBox.Focus();
                    break;
                case ReviewAccelerator.TimeFilter:
                    TimeFilter.IsDropDownOpen = true;
                    break;
                case ReviewAccelerator.StatusMenu:
                    StatusButton_Click(StatusButton, null!);
                    break;
                case ReviewAccelerator.ColumnsMenu:
                    ColumnsButton_Click(ColumnsButton, null!);
                    break;
                case ReviewAccelerator.Help:
                    HelpButton_Click(HelpButton, null!);
                    break;
                case ReviewAccelerator.AcceptAll:
                    AcceptButton_Click(AcceptButton, null!);
                    break;
                case ReviewAccelerator.RejectAll:
                    RejectButton_Click(RejectButton, null!);
                    break;
            }

            e.Handled = true;
        }

        /// <summary>
        /// Maps a key+modifier combo to "focus the filter box": Alt+F does. Any other key, or a
        /// different modifier, returns false so no other combo is stolen. Pure and testable.
        /// </summary>
        /// <param name="key">The key that was pressed.</param>
        /// <param name="modifiers">The modifier keys held (must be exactly Alt).</param>
        /// <returns>True only for Alt+F.</returns>
        internal static bool IsFocusFilterHotkey(Key key, ModifierKeys modifiers)
        {
            return key == Key.F && modifiers == ModifierKeys.Alt;
        }

        /// <summary>
        /// Maps a key+modifier combo to the dialog accelerator it fires: Alt+F focuses the
        /// filter, Alt+T opens the Time dropdown, Alt+S opens the Status menu, Alt+C opens the
        /// Columns menu, Alt+H toggles the Help popup, Alt+A / Alt+R fire Accept/Reject
        /// directly. Any other key, or the same letters under a different modifier, returns
        /// <see cref="ReviewAccelerator.None"/> so no other combo is stolen. Pure and testable.
        /// </summary>
        /// <param name="key">The key that was pressed.</param>
        /// <param name="modifiers">The modifier keys held (must be exactly Alt).</param>
        /// <returns>The accelerator to fire, or <see cref="ReviewAccelerator.None"/>.</returns>
        internal static ReviewAccelerator AcceleratorForKey(Key key, ModifierKeys modifiers)
        {
            if (modifiers != ModifierKeys.Alt)
                return ReviewAccelerator.None;
            switch (key)
            {
                case Key.F: return ReviewAccelerator.FocusFilter;
                case Key.T: return ReviewAccelerator.TimeFilter;
                case Key.S: return ReviewAccelerator.StatusMenu;
                case Key.C: return ReviewAccelerator.ColumnsMenu;
                case Key.H: return ReviewAccelerator.Help;
                case Key.A: return ReviewAccelerator.AcceptAll;
                case Key.R: return ReviewAccelerator.RejectAll;
                default:
                    return ReviewAccelerator.None;
            }
        }

        /// <summary>
        /// Handles the context-menu "Accept" item. Marks the target rows Accepted.
        /// </summary>
        /// <param name="sender">The menu item.</param>
        /// <param name="e">Click event arguments.</param>
        private void AcceptMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SetStatusForTargets(MatchDecision.Accepted);
        }

        /// <summary>
        /// Handles the context-menu "Reject" item. Marks the target rows Rejected.
        /// </summary>
        /// <param name="sender">The menu item.</param>
        /// <param name="e">Click event arguments.</param>
        private void RejectMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SetStatusForTargets(MatchDecision.Rejected);
        }

        /// <summary>
        /// Handles the context-menu "Set to Pending" item. Marks the target rows Pending (defer).
        /// </summary>
        /// <param name="sender">The menu item.</param>
        /// <param name="e">Click event arguments.</param>
        private void PendingMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SetStatusForTargets(MatchDecision.Pending);
        }

        /// <summary>
        /// Double-clicking a row cycles that row's decision: Pending → Accepted → Rejected →
        /// Pending. Restores the per-row, one-click control the checkbox column used to give
        /// (the checkbox was removed in favor of a text-only status). The event is subscribed
        /// on the DataGrid; the row is recovered by walking up the visual tree from the hit
        /// element to the containing <see cref="DataGridRow"/>.
        /// </summary>
        /// <param name="sender">The DataGrid (the subscription site).</param>
        /// <param name="e">Mouse event arguments.</param>
        private void StatusCell_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Walk up from the hit element to the containing DataGridRow to get the entry.
            object? entry = null;
            DependencyObject? node = e.OriginalSource as DependencyObject;
            while (node != null)
            {
                if (node is DataGridRow row)
                {
                    entry = row.Item;
                    break;
                }
                node = VisualTreeHelper.GetParent(node);
            }

            if (entry == null)
                return;

            e.Handled = true;
            CycleStatus(entry as VrModReviewMatchEntry);
        }

        /// <summary>
        /// Cycles a single entry's decision Pending → Accepted → Rejected → Pending. Pure and
        /// testable (no dialog needed).
        /// </summary>
        /// <param name="entry">The entry whose decision is cycled (null is a no-op).</param>
        internal static void CycleStatus(VrModReviewMatchEntry? entry)
        {
            if (entry == null)
                return;
            entry.Status = entry.Status == MatchDecision.Pending
                ? MatchDecision.Accepted
                : entry.Status == MatchDecision.Accepted
                    ? MatchDecision.Rejected
                    : MatchDecision.Pending;
        }

        /// <summary>
        /// A display name for a column, used by the "Columns…" menu. A column with no header
        /// (e.g. the per-row "open mod page" button column) falls back to a placeholder.
        /// Code-built headers (a <see cref="FrameworkElement"/> header) are named from their
        /// <see cref="FrameworkElement.Tag"/> (the canonical name), else from their first
        /// TextBlock (the visible text).
        /// </summary>
        /// <param name="column">The column to name.</param>
        internal static string ColumnHeaderText(DataGridColumn column)
        {
            switch (column.Header)
            {
                case null:
                    return "(column)";
                case string s:
                    return string.IsNullOrWhiteSpace(s) ? "(column)" : s;
                case FrameworkElement element:
                    // Code-built headers carry the canonical menu name in Tag (the visible
                    // header text may be a symbol, e.g. the Link column's "↗").
                    var tag = element.Tag as string;
                    if (tag != null && tag.Trim().Length > 0)
                        return tag;
                    var textBlock = FindFirstTextBlock(element);
                    var text = textBlock?.Text ?? string.Empty;
                    return string.IsNullOrWhiteSpace(text) ? "(column)" : text;
                default:
                    return "(column)";
            }
        }

        /// <summary>
        /// Finds the first TextBlock in an element's subtree (depth-first, pre-order).
        /// Used to fall back to the visible header text when a code-built header carries no
        /// canonical name in <see cref="FrameworkElement.Tag"/>.
        /// </summary>
        private static TextBlock? FindFirstTextBlock(DependencyObject root)
        {
            if (root is TextBlock textBlock)
            {
                return textBlock;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindFirstTextBlock(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>
        /// Builds the "Columns…" context menu for the matches grid: one checkable item per
        /// column (show / hide; checked = visible). Reordering is intentionally NOT in this
        /// menu — the DataGrid's native header drag does it (<c>CanUserReorderColumns</c>) —
        /// so the menu is visibility-only. The passed <paramref name="columns"/> is the grid's
        /// live column collection, so each pick takes effect immediately. Pure and testable: it
        /// builds a <see cref="ContextMenu"/> from the columns without touching the window.
        /// </summary>
        /// <param name="columns">The grid's column collection (mutated in place on a pick).</param>
        /// <returns>The populated context menu.</returns>
        internal static ContextMenu BuildColumnsMenu(IList<DataGridColumn> columns)
        {
            var menu = new ContextMenu();

            // Show / hide: one checkable item per column (checked = visible).
            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                var item = new MenuItem
                {
                    Header = ColumnHeaderText(column),
                    IsCheckable = true,
                    IsChecked = column.Visibility == Visibility.Visible
                };
                item.Checked += (s, a) => column.Visibility = Visibility.Visible;
                item.Unchecked += (s, a) => column.Visibility = Visibility.Collapsed;
                menu.Items.Add(item);
            }

            return menu;
        }

        /// <summary>
        /// Handles the "Help" button: toggles the non-modal help popup (the how-to text).
        /// The popup is StaysOpen=False, so it also closes when it loses focus, and it is
        /// part of this window's visual tree, so it closes together with the window.
        /// </summary>
        /// <param name="sender">The Help button.</param>
        /// <param name="e">Click event arguments.</param>
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            HelpPopup.IsOpen = !HelpPopup.IsOpen;
        }

        /// <summary>
        /// Handles the "Columns…" button: builds (fresh, so it reflects the current
        /// visibility) and opens the column show/hide menu, anchored below the button. The
        /// menu is single-shot — it closes after one pick. (Column reordering is by dragging
        /// a column header, not from this menu.)
        /// </summary>
        /// <param name="sender">The Columns… button.</param>
        /// <param name="e">Click event arguments.</param>
        private void ColumnsButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            var menu = BuildColumnsMenu(MatchesDataGrid.Columns);
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            button.ContextMenu = menu;
            menu.IsOpen = true;
        }

        /// <summary>
        /// Handles the "Status…" button: builds (fresh, so the checkmarks reflect the current
        /// filter) and opens the status filter menu, anchored below the button. The checkable
        /// items (Pending / Accepted / Rejected) toggle membership and leave the menu open for
        /// multi-select; the quick actions ("Pending only", "Show all") are single-shot and
        /// close it.
        /// </summary>
        private void StatusButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            var menu = BuildStatusMenu(_statusFilter, ApplyStatusFilter);
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            button.ContextMenu = menu;
            menu.IsOpen = true;
        }

        /// <summary>
        /// Builds the status filter menu (static, testable): one checkable item per status
        /// (checked = in <paramref name="currentSelection"/>), a separator, and two quick
        /// actions. Toggling a checkable item or clicking a quick action invokes
        /// <paramref name="apply"/> with the new selection. Mirrors <see cref="BuildColumnsMenu"/>
        /// (a fresh ContextMenu each open so the checkmarks reflect the current state).
        /// </summary>
        internal static ContextMenu BuildStatusMenu(
            IEnumerable<MatchDecision> currentSelection,
            System.Action<IEnumerable<MatchDecision>> apply)
        {
            var menu = new ContextMenu();
            var current = new HashSet<MatchDecision>(currentSelection);
            AddStatusItem(menu, "Pending", MatchDecision.Pending, current, apply);
            AddStatusItem(menu, "Accepted", MatchDecision.Accepted, current, apply);
            AddStatusItem(menu, "Rejected", MatchDecision.Rejected, current, apply);

            menu.Items.Add(new Separator());

            var pendingOnly = new MenuItem { Header = "Pending only" };
            pendingOnly.Click += (s, a) => apply(new[] { MatchDecision.Pending });
            menu.Items.Add(pendingOnly);

            var showAll = new MenuItem { Header = "Show all" };
            showAll.Click += (s, a) => apply(Array.Empty<MatchDecision>());
            menu.Items.Add(showAll);

            return menu;
        }

        /// <summary>
        /// Adds one checkable status item to the menu: checked = the status is in
        /// <paramref name="current"/>. Toggling it adds/removes the status in
        /// <paramref name="current"/> and invokes <paramref name="apply"/> with the updated set.
        /// </summary>
        private static void AddStatusItem(
            ContextMenu menu, string label, MatchDecision status,
            HashSet<MatchDecision> current, System.Action<IEnumerable<MatchDecision>> apply)
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = current.Contains(status)
            };
            item.Checked += (s, a) => { current.Add(status); apply(current); };
            item.Unchecked += (s, a) => { current.Remove(status); apply(current); };
            menu.Items.Add(item);
        }

        /// <summary>
        /// Applies a status-filter selection (from the menu) to the dialog's state: replaces
        /// <see cref="_statusFilter"/>, then re-applies it (button label, view, empty hint).
        /// </summary>
        private void ApplyStatusFilter(IEnumerable<MatchDecision> statuses)
        {
            _statusFilter.Clear();
            foreach (var s in statuses)
                _statusFilter.Add(s);
            AfterStatusFilterChanged();
        }

        /// <summary>
        /// Shared tail for any status-filter change (a checkbox toggle or a quick action):
        /// refresh the button label, the collection view, and the empty hint.
        /// </summary>
        private void AfterStatusFilterChanged()
        {
            UpdateStatusButton();
            _matchesView?.Refresh();
            UpdateEmptyHint();
        }

        /// <summary>
        /// Sets the "Status…" button's label to reflect the active filter: "Status: all" when
        /// nothing is selected, otherwise the selected statuses joined with "+".
        /// </summary>
        private void UpdateStatusButton()
        {
            var labels = new List<string>();
            if (_statusFilter.Contains(MatchDecision.Pending)) labels.Add("Pending");
            if (_statusFilter.Contains(MatchDecision.Accepted)) labels.Add("Accepted");
            if (_statusFilter.Contains(MatchDecision.Rejected)) labels.Add("Rejected");
            StatusButton.Content = labels.Count == 0
                ? "Status: all"
                : "Status: " + string.Join(" + ", labels);
        }

        /// <summary>
        /// Handles the "Reset all decisions" button (bottom-left). This is destructive: it clears
        /// every stored decision (so all matches are presented for review again on the next
        /// scan) and sets the visible rows back to Pending. The stored-decision clear and the
        /// save are done here (plugin-backed); the row reset itself is the testable
        /// <see cref="ResetAllMatchStatuses"/>. A confirmation guard prevents an accidental reset.
        /// </summary>
        /// <param name="sender">The Reset all decisions button.</param>
        /// <param name="e">Click event arguments.</param>
        private void ResetAllDecisions_Click(object sender, RoutedEventArgs e)
        {
            // Themed Playnite dialog with three options (a native MessageBox can't express the
            // third outcome): "Reset and remove tags" / "Reset only" / "Cancel". Closing the
            // dialog (X / Esc) also counts as Cancel (ShowMessage returns null).
            var chosen = _api.Dialogs.ShowMessage(
                "Reset ALL match decisions?\n\nEvery accepted/rejected decision will be cleared and all matches will be presented for review again on the next scan.\n\n'Reset only' keeps your games' tags as they are now. 'Reset and remove tags' also removes every VRMod tag from your library.",
                "Reset all decisions",
                MessageBoxImage.Warning,
                new List<MessageBoxOption>
                {
                    new MessageBoxOption(ResetRemoveTagsTitle),
                    new MessageBoxOption(ResetOnlyTitle, isDefault: true),
                    new MessageBoxOption(ResetCancelTitle, isCancel: true),
                });

            var choice = ResetChoiceForTitle(chosen?.Title);
            if (choice == ResetChoice.Cancel)
                return;

            ResetAllMatchStatuses(_matches);
            _plugin.TagService.Settings.ClearAllDecisions();
            _plugin.SavePluginSettings(_plugin.TagService.Settings);
            _matchesView?.Refresh();

            if (choice == ResetChoice.ResetAndRemoveTags)
            {
                RunRemoveAllVrModTagsWithProgress();
            }
        }

        /// <summary>
        /// Removes every VRMod tag under Playnite's global progress dialog — the same centered
        /// progress window (with a determinate bar) Playnite uses for imports/updates. The removal
        /// iterates every game and takes seconds on a big library, so running it silently made the
        /// app look frozen; the dialog instead shows "Removing VRMod tags… 45 / 182". Blocks until
        /// the removal finishes; the review dialog stays open afterwards so the user can Save or
        /// Cancel as usual.
        /// </summary>
        private void RunRemoveAllVrModTagsWithProgress()
        {
            // The removal runs on a background thread (Playnite's global-progress pattern). The
            // progress window is MODAL and Playnite gives it this dialog as its Owner
            // (WindowFactory.CreateAndOpenDialog sets Owner = WindowManager.CurrentWindow), so
            // this dialog is disabled while it runs and the footer can only be re-enabled once
            // ActivateGlobalProgress has returned - which is also the proof that Playnite's child
            // window is closed. Disable the footer (Save/Cancel/Reset/bulk) so a second database
            // write can't race the removal.
            FooterPanel.IsEnabled = false;
            var progress = _api.Dialogs.ActivateGlobalProgress(
                a =>
                {
                    // The action runs on a background thread; the service issues its Games.Update
                    // calls in one BufferedUpdate, so this is safe off the UI thread.
                    // Scope the removal to the matched games (not the whole library): a game
                    // carries a VRMod tag only if it was matched, so this touches only the
                    // games the plugin tagged and is fast on a big library. _matches is reset
                    // to Pending (not cleared) by now, so its PlayniteGame refs are the games.
                    _plugin.TagService.RemoveAllVrModTags(
                        _matches.Where(m => m.PlayniteGame != null).Select(m => m.PlayniteGame).Distinct().ToList(),
                        (current, total, gameName) =>
                        {
                            a.ProgressMaxValue = total;
                            a.CurrentProgressValue = current;
                            a.Text = $"Removing VRMod tags…\n{gameName}\n{current} / {total}";
                        },
                        a.CancelToken);
                },
                new GlobalProgressOptions("Removing VRMod tags", false) { IsIndeterminate = false });

            // Playnite's progress window is closed by now (ShowDialog returned), so this
            // dialog is interactive again and the footer is re-enabled here rather than from
            // the worker. The dialog stays open either way: the user reviews the result in the
            // rows, and a failure that wrote some tags and not others has to be visible there.
            var failure = ProgressFailureText("Removing VRMod tags", progress);
            if (progress != null && progress.Canceled)
                Log.Info("VRMod Review Dialog: tag removal was cancelled by the user.");
            if (failure != null)
            {
                Log.Error("VRMod Review Dialog: " + failure);
                _api.Dialogs.ShowErrorMessage(failure, "VRMod VR mod tags");
            }
            FooterPanel.IsEnabled = true;
        }
        /// <summary>Button text for the "reset everything and also remove the tags" option.</summary>
        internal const string ResetRemoveTagsTitle = "Reset and remove tags";

        /// <summary>Button text for the "reset only" option (the default button).</summary>
        internal const string ResetOnlyTitle = "Reset only";

        /// <summary>Button text for the cancel option.</summary>
        internal const string ResetCancelTitle = "Cancel";

        /// <summary>
        /// Pure, testable mapping from the chosen option's title to a reset choice. An unknown
        /// or null title (dialog closed without a pick) maps to Cancel.
        /// </summary>
        internal static ResetChoice ResetChoiceForTitle(string? title)
        {
            if (string.Equals(title, ResetRemoveTagsTitle, StringComparison.Ordinal))
                return ResetChoice.ResetAndRemoveTags;
            if (string.Equals(title, ResetOnlyTitle, StringComparison.Ordinal))
                return ResetChoice.ResetOnly;
            return ResetChoice.Cancel;
        }

        /// <summary>
        /// Sets every match's <see cref="VrModReviewMatchEntry.Status"/> back to Pending (unchecking
        /// all rows). Pure and testable — the destructive stored-decision clear
        /// (<c>ClearAllDecisions</c>) and the save are done by the caller so this stays decoupled
        /// from the plugin.
        /// </summary>
        /// <param name="matches">The rows to reset.</param>
        internal static void ResetAllMatchStatuses(IList<VrModReviewMatchEntry> matches)
        {
            // Re-stamp the decision time so the reset matches sort to the top of the time filter
            // (they were just (re)created, i.e. they are the most recent).
            var now = DateTime.UtcNow;
            foreach (var match in matches)
            {
                match.Status = MatchDecision.Pending;
                match.DecidedAtUtc = now;
                // Resetting clears the stored decisions, so there is no saved decision anymore:
                // drop the persisted decision + its save time (the status now reads "Pending").
                match.PersistedDecision = null;
                match.SavedAtUtc = null;
            }
        }

        /// <summary>
        /// Right-clicking a row scopes the context menu to that row: if the row under the cursor
        /// isn't already part of the selection, select it (so the menu acts on just it). Right-
        /// clicking an already-selected row keeps the existing (possibly multi-)selection, and a
        /// right-click that isn't on a row leaves the selection untouched (the menu then acts on
        /// the selection, or on every match when nothing is selected).
        /// </summary>
        /// <param name="sender">The matches DataGrid.</param>
        /// <param name="e">Mouse event arguments.</param>
        private void OnGridPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            SelectRowForRightClick(e.OriginalSource as DependencyObject);
        }

        /// <summary>
        /// Pure, testable core of the right-click handler: walks up the visual tree from <paramref
        /// name="source"/> to the enclosing <see cref="DataGridRow"/> and, if that row's item isn't
        /// already selected, selects it (so the context menu scopes to it). No-op when the source is
        /// null, not inside a row, or the row is already selected (preserves multi-selections).
        /// </summary>
        /// <param name="source">The <c>OriginalSource</c> of the right-click (deepest element hit).</param>
        internal static void SelectRowForRightClick(DependencyObject? source)
        {
            // Walk up the visual tree to the enclosing DataGridRow. VisualTreeHelper.GetParent
            // is only valid on a real visual element (a UIElement), so the walk runs only while
            // `current` is a UIElement: a null or non-visual source, or reaching the top of the
            // tree, terminates the walk with no row — the intended no-op for right-clicks on
            // empty grid area, a header, or with a null source (and it cannot loop forever).
            var current = source;
            var row = current as DataGridRow;
            while (row == null && current is UIElement)
            {
                current = VisualTreeHelper.GetParent(current);
                row = current as DataGridRow;
            }
            if (row == null || row.Item is not VrModReviewMatchEntry entry)
            {
                return;
            }

            var grid = ItemsControl.ItemsControlFromItemContainer(row) as DataGrid;
            if (grid == null)
            {
                return;
            }

            // Don't steal an existing multi-selection: only select the row if it isn't already selected.
            if (!grid.SelectedItems.Contains(entry))
            {
                grid.SelectedItem = entry;
            }
        }

        /// <summary>
        /// Keeps the bulk button labels in sync with the selection: "…All" when nothing is
        /// selected, "…Selected" when one or more rows are selected.
        /// </summary>
        /// <param name="sender">The matches DataGrid.</param>
        /// <param name="e">Selection-changed event arguments.</param>
        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var hasSelection = MatchesDataGrid.SelectedItems.Count > 0;
            AcceptButton.Content = hasSelection ? "Accept Selected" : "Accept All";
            RejectButton.Content = hasSelection ? "Reject Selected" : "Reject All";
        }

        /// <summary>
        /// Handles the "Cancel" button click.
        ///
        /// Closes the dialog without applying any changes.
        /// Pending matches remain Pending (no decisions are recorded).
        /// </summary>
        /// <param name="sender">The Cancel button.</param>
        /// <param name="e">Click event arguments.</param>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Opens the exact mod/game page for the clicked row's source in the user's default browser.
        /// The button's DataContext is the row's <see cref="VrModReviewMatchEntry"/>, so the URL is
        /// read from there. Failures (no default browser, bad/empty URL) are silently ignored — a
        /// link click must never crash the review dialog.
        /// </summary>
        /// <param name="sender">The clicked button.</param>
        /// <param name="e">RoutedEventArgs.</param>
        private void OpenModPage_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement element)
                return;
            if (element.DataContext is not VrModReviewMatchEntry entry)
                return;
            OpenInDefaultBrowser(entry.SourceUrl);
        }

        /// <summary>
        /// Opens a URL in the user's default browser. Only absolute http(s) URLs are opened
        /// (see <see cref="WebUrl"]); everything else is a no-op. Failures (no default browser,
        /// bad URL) are silently ignored.
        /// </summary>
        /// <param name="url">The URL to open.</param>
        private static void OpenInDefaultBrowser(string? url)
        {
            // Only absolute http(s) URLs may be handed to the shell (see WebUrl).
            if (!WebUrl.IsSafeWebUrl(url))
                return;
            // Validation and the shell hand-off live in WebUrl. A refusal is the shell saying "no
            // application is associated with this URL type", which this dialog cannot report more
            // usefully than the browser not appearing.
            WebUrl.TryOpenInDefaultBrowser(url);
        }

        /// <summary>
        /// Handles the cloned Playnite chrome's minimize button: minimizes this window.
        /// </summary>
        /// <param name="sender">The minimize button.</param>
        /// <param name="e">Click event arguments.</param>
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        /// <summary>
        /// Handles the cloned Playnite chrome's maximize/restore button: toggles between
        /// maximized and normal (the template swaps the glyph to the restore symbol, "2",
        /// via its WindowState trigger).
        /// </summary>
        /// <param name="sender">The maximize/restore button.</param>
        /// <param name="e">Click event arguments.</param>
        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        /// <summary>
        /// Handles the cloned Playnite chrome's close button: closes the dialog without saving
        /// (same as Cancel — Pending matches remain Pending).
        /// </summary>
        /// <param name="sender">The close button.</param>
        /// <param name="e">Click event arguments.</param>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Name of Playnite's window base class (in Playnite's app assembly, NOT the SDK):
        /// every Playnite window — the main window, the settings/add-ons windows, every
        /// Playnite dialog — is a <c>Playnite.Controls.WindowBase</c>.
        /// </summary>
        internal const string PlayniteWindowBaseTypeName = "Playnite.Controls.WindowBase";

        /// <summary>
        /// Full name of the static Playnite <c>WindowManager</c> class (lives in the same
        /// assembly as <see cref="PlayniteWindowBaseTypeName">WindowBase</c>) that re-evaluates
        /// child-window ownership.
        /// </summary>
        internal const string PlayniteWindowManagerTypeName = "Playnite.Windows.WindowManager";

        /// <summary>
        /// Name of the public static method on Playnite's <c>WindowManager</c> that re-evaluates
        /// <c>HasChildWindow</c> on every Playnite window (which is what lifts the Opacity 0.4
        /// dim once a window no longer owns a child window).
        /// </summary>
        internal const string NotifyChildOwnershipChangesMethodName = "NotifyChildOwnershipChanges";

        /// <summary>
        /// Pure, testable type check: true when the given type is Playnite's <c>WindowBase</c>
        /// or derives from it — i.e. it is some Playnite window. Walks the base-type chain
        /// because the owner is usually a SUBCLASS (the desktop main window is
        /// <c>Playnite.DesktopApp.Controls.AppWindow</c>), not <c>WindowBase</c> itself. Compared
        /// by full name because the type lives in Playnite's app assembly, which the plugin does
        /// not reference (referencing it would break builds outside a Playnite install, e.g. CI).
        /// </summary>
        /// <param name="ownerType">The runtime type of the candidate window (e.g.
        /// <c>owner.GetType()</c>).</param>
        /// <returns>True when <paramref name="ownerType"/> or any of its base types is
        /// <see cref="PlayniteWindowBaseTypeName"/>.</returns>
        internal static bool IsPlayniteWindowBase(System.Type? ownerType)
        {
            return FindPlayniteWindowBaseType(ownerType) != null;
        }

        /// <summary>
        /// Pure, testable: returns the <see cref="Type"/> in the given type's hierarchy (the type
        /// itself or a base type) whose full name is Playnite's <c>WindowBase</c>, or null when
        /// the type is not a Playnite window. This is the <c>WindowBase</c> the owner derives
        /// from; its assembly is Playnite's app assembly, where <c>WindowManager</c> lives.
        /// </summary>
        /// <param name="ownerType">The runtime type of the candidate window (e.g.
        /// <c>owner.GetType()</c>).</param>
        /// <returns>The <c>WindowBase</c> <see cref="Type"/>, or null when not found.</returns>
        internal static System.Type? FindPlayniteWindowBaseType(System.Type? ownerType)
        {
            for (var t = ownerType; t != null; t = t.BaseType)
            {
                if (string.Equals(t.FullName, PlayniteWindowBaseTypeName, StringComparison.Ordinal))
                    return t;
            }
            return null;
        }

        /// <summary>
        /// Pure, testable reflection helper: given Playnite's <c>WindowBase</c> type, returns the
        /// public static <c>WindowManager.NotifyChildOwnershipChanges()</c> <see
        /// cref="System.Reflection.MethodInfo"/> — the exact call Playnite makes to re-evaluate
        /// <c>HasChildWindow</c> on all its windows (which lifts the dim). Null-tolerant at
        /// every step (type, assembly, <c>WindowManager</c> type, method) — any miss is
        /// "Playnite's internals changed, skip the best-effort re-evaluation".
        /// </summary>
        /// <param name="windowBaseType">Playnite's <c>WindowBase</c> type (see
        /// <see cref="FindPlayniteWindowBaseType"/>), or null.</param>
        /// <returns>The <c>NotifyChildOwnershipChanges</c> <see cref="System.Reflection.MethodInfo"/>,
        /// or null when it cannot be found.</returns>
        internal static System.Reflection.MethodInfo? ResolveNotifyChildOwnershipChanges(System.Type? windowBaseType)
        {
            if (windowBaseType == null)
                return null;
            var windowManagerType = windowBaseType.Assembly.GetType(PlayniteWindowManagerTypeName);
            return windowManagerType?.GetMethod(
                NotifyChildOwnershipChangesMethodName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        }

        /// <summary>
        /// Best-effort: asks Playnite to re-evaluate its "has child window" state on all of
        /// its windows, so the owner of this dialog is dimmed while we are open (and undimmed
        /// when we close) exactly like Playnite's own dialogs.
        ///
        /// How Playnite does it: every <c>WindowBase</c> window's template has a DataTrigger on
        /// the <c>HasChildWindow</c> property (true when the window owns a child window, i.e.
        /// <c>OwnedWindows.Count &gt; 0</c>) that sets Opacity 0.4. That property is a plain CLR
        /// property, so the trigger only re-runs when the window fires
        /// <c>PropertyChanged(nameof(HasChildWindow))</c> — which Playnite does via
        /// <c>WindowManager.NotifyChildOwnershipChanges()</c>. This dialog is a plain
        /// <c>Window</c> (not a <c>WindowBase</c>), so its own open/close never triggers that; we
        /// call the same method on the owner so the owner dims/undims exactly like a Playnite
        /// window does.
        ///
        /// For the undim to take effect this must run once the dialog has left the owner's
        /// <c>OwnedWindows</c> (true after the window is closed/disposed); while we are open it
        /// (re-)dims the owner.
        ///
        /// Purely best-effort: the owner may be null (screen-centered fallback), may not be a
        /// Playnite window, or Playnite may have renamed its internals — in every such case this
        /// is a silent no-op (the dialog still works; only the dim may not track us).
        /// </summary>
        /// <param name="owner">The window that owns this dialog (Playnite's current app
        /// window), or null.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "Reflection over Playnite's private shape: any miss means the re-evaluation did not happen, which is the behavior from before this call existed.")]
        internal static void NotifyPlayniteChildOwnershipChanged(Window? owner)
        {
            if (owner == null)
                return;
            var ownerType = owner.GetType();
            if (!IsPlayniteWindowBase(ownerType))
                return;
            try
            {
                ResolveNotifyChildOwnershipChanges(FindPlayniteWindowBaseType(ownerType))?.Invoke(null, null);
            }
            catch (System.Exception)
            {
                // Best-effort re-evaluation: any failure (assembly not resolvable, member
                // renamed) just means the pre-fix behavior (the dialog still works).
            }
        }

        /// <summary>
        /// Handles the filter text box. Restarts the debounce timer so the filter is applied
        /// only after the user pauses typing (see OnFilterDebounceTick) — applying it on every
        /// keystroke would re-filter and re-render a large match list continuously.
        /// </summary>
        /// <param name="sender">The filter TextBox.</param>
        /// <param name="e">Text-changed event arguments.</param>
        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateFilterClearButton();
            _filterDebounce.Stop();
            _filterDebounce.Start();
        }

        /// <summary>
        /// Shows the filter clear button only while the filter box has text (mirrors Playnite's
        /// main-window filter, whose clear icon appears only when there is text to clear).
        /// </summary>
        private void UpdateFilterClearButton()
        {
            FilterClearButton.Visibility = ShouldShowFilterClear(FilterBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// Pure, testable show/hide rule for the filter clear button: it is shown only when the
        /// filter box has non-empty text (so an empty box has no button to clear).
        /// </summary>
        internal static bool ShouldShowFilterClear(string filterText)
        {
            return !string.IsNullOrEmpty(filterText);
        }

        /// <summary>
        /// Clears the filter box on clear-button click. The resulting text change flows through
        /// the usual <see cref="FilterBox_TextChanged"/> path, which re-applies the now-empty filter
        /// (after the debounce) and hides the clear button again.
        /// </summary>
        private void FilterClearButton_Click(object sender, RoutedEventArgs e)
        {
            FilterBox.Clear();
        }

        /// <summary>
        /// Handles the "Changed only" checkbox: toggles the changed-only filter and re-applies it
        /// immediately (a discrete click, not a keystroke, so no debounce).
        /// </summary>
        private void ChangedOnly_Changed(object sender, RoutedEventArgs e)
        {
            _changedOnly = ChangedOnlyCheckBox.IsChecked == true;
            _matchesView?.Refresh();
            UpdateEmptyHint();
        }

        /// <summary>
        /// Fires after the filter box has been quiet for the debounce interval. Applies the
        /// current filter text to the collection view so the grid updates once per typing
        /// pause rather than once per keystroke.
        /// </summary>
        private void OnFilterDebounceTick(object? sender, EventArgs e)
        {
            _filterDebounce.Stop();
            _filterText = FilterBox.Text;
            _matchesView?.Refresh();
            UpdateEmptyHint();
        }

        /// <summary>
        /// Stops the filter debounce timer when the window closes so a pending tick never
        /// fires on a closed window.
        /// </summary>
        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _filterDebounce.Stop();
            // Raise Playnite's child-ownership re-evaluation the way Playnite's own WindowBase does
            // (it fires ClosedRoutedEvent on close → WindowManager.NotifyChildOwnershipChanges).
            // At Closed time this window is out of its owner's OwnedWindows, so the owner's
            // HasChildWindow re-evaluates to false and the dim (Opacity 0.4) is lifted. Done here,
            // at the real close, for every close path (Save / Cancel / close button) — the
            // post-ShowDialog notification in VrModTagPlugin is a second pass.
            NotifyPlayniteChildOwnershipChanged(Owner);
        }

        /// <summary>
        /// The collection-view filter predicate. A row is visible when the filter text is empty
        /// (show all) or when the text is found (case-insensitive) in any of the row's columns:
        /// Source, Source Entry, Playnite Game, Category, Tags, Status, and Saved at.
        /// </summary>
        /// <param name="item">The match entry to test.</param>
        /// <returns>True if the row should be visible.</returns>
        private bool MatchFilter(object item)
        {
            if (item is not VrModReviewMatchEntry m)
                return false;
            return MatchFilterText(m, _filterText)
                   && MatchTimeFilter(m, _timeFilterSeconds, _filterNowUtc)
                   && MatchChangedFilter(m, _changedOnly)
                   && MatchStatusFilter(m, _statusFilter);
        }

        /// <summary>
        /// Pure, testable status filter: an entry is visible when no status is selected (show
        /// all) or when its <see cref="VrModReviewMatchEntry.Status"/> is one of the selected
        /// statuses (a multi-select union).
        /// </summary>
        /// <param name="m">The match entry to test.</param>
        /// <param name="selected">The selected statuses (empty = show all).</param>
        /// <returns>True if the entry should be visible.</returns>
        internal static bool MatchStatusFilter(VrModReviewMatchEntry m, IReadOnlyCollection<MatchDecision> selected)
        {
            return selected.Count == 0 || selected.Contains(m.Status);
        }

        /// <summary>
        /// Pure, testable core of the filter predicate: a match entry is visible when the
        /// filter text is empty (show all) or when it is found (case-insensitive) in any of
        /// the entry's columns: Source, Source Entry, Playnite Game, Category, Tags, Status
        /// (both the raw decision and the display text), and the "Saved at" text.
        /// </summary>
        /// <param name="m">The match entry to test.</param>
        /// <param name="filterText">The current filter text (may be empty).</param>
        /// <returns>True if the entry should be visible.</returns>
        internal static bool MatchFilterText(VrModReviewMatchEntry m, string filterText)
        {
            if (string.IsNullOrEmpty(filterText))
                return true;

            var haystack = string.Join("\n",
                m.SourceName,
                m.SourceGameName,
                m.PlayniteGame?.Name ?? string.Empty,
                string.Join("\n", m.Categories),
                m.TagPreview,
                m.Status.ToString(),
                m.DisplayStatusText,
                m.SavedAtDisplay);
            return haystack.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Pure, testable core of the time filter: a match entry is visible when the window is
        /// "Always" (a negative <paramref name="windowSeconds"/>, i.e. no limit) or when its decision
        /// time (<see cref="VrModReviewMatchEntry.DecidedAtUtc"/>) falls within
        /// <paramref name="windowSeconds"/> seconds before <paramref name="nowUtc"/>. An entry with
        /// no recorded time (<see cref="DateTime.MinValue"/>) is treated as the oldest, so it is
        /// only visible under "Always".
        /// </summary>
        /// <param name="m">The match entry to test.</param>
        /// <param name="windowSeconds">The window in seconds; a negative value means "Always" (no limit).</param>
        /// <param name="nowUtc">The reference "now" (UTC), captured when the dialog opened.</param>
        /// <returns>True if the entry should be visible.</returns>
        internal static bool MatchTimeFilter(VrModReviewMatchEntry m, int windowSeconds, DateTime nowUtc)
        {
            if (windowSeconds < 0)
                return true;
            if (m.DecidedAtUtc == DateTime.MinValue)
                return false;
            return m.DecidedAtUtc >= nowUtc.AddSeconds(-windowSeconds);
        }

        /// <summary>
        /// Pure, testable core of the "Changed only" filter: a match entry is visible when the
        /// filter is off (show all) or when its tags changed since the last accept
        /// (<see cref="VrModReviewMatchEntry.TagsChanged"/>).
        /// </summary>
        /// <param name="m">The match entry to test.</param>
        /// <param name="changedOnly">True when the "Changed only" filter is on.</param>
        /// <returns>True if the entry should be visible.</returns>
        internal static bool MatchChangedFilter(VrModReviewMatchEntry m, bool changedOnly)
        {
            return !changedOnly || m.TagsChanged;
        }

        /// <summary>
        /// Handles the time-filter combo box: reads the selected window (in seconds, carried by
        /// the item's Tag; -1 = "Always") and re-applies the filter through the same debounce used
        /// by the text box, so a large list isn't re-filtered on every intermediate state.
        /// </summary>
        private void TimeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _timeFilterSeconds = int.TryParse((TimeFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var s) ? s : -1;
            _filterDebounce?.Stop();
            _filterDebounce?.Start();
        }

        /// <summary>
        /// Collapses review rows that point at the same (source, category, Playnite game) triple.
        /// Only the SAME source + category + game row (reached through several library entries)
        /// is collapsed to its single highest-scoring match. The game part of the key is the game
        /// Id, NOT the name — store duplicates (two game rows both named "Alien: Isolation")
        /// stay separate rows so each gets its own decision. TWO DIFFERENT sources matching one
        /// game stay as separate rows - each keeps its own deep link (open-mod-page button), so
        /// the game page opens in that specific source. The SAME source listing one game under a
        /// DIFFERENT category also stays separate, so all of a source's tags for a game show up.
        /// </summary>
        /// <param name="matches">The pending matches to collapse.</param>
        /// <returns>One row per (source, category, game Id), plus any rows without a game.</returns>
        internal static List<VrModReviewMatchEntry> DedupeByGame(IEnumerable<VrModReviewMatchEntry> matches)
        {
            var best = new Dictionary<string, VrModReviewMatchEntry>(StringComparer.OrdinalIgnoreCase);
            var unkeyed = new List<VrModReviewMatchEntry>();

            foreach (var m in matches)
            {
                var game = m.PlayniteGame;
                if (game == null)
                {
                    unkeyed.Add(m);
                    continue;
                }

                // Key on (source, categories, game Id): only the SAME source + category set +
                // the SAME Playnite game row (reached through several library entries) collapses
                // to one row. The game part is the Id, not the name — store duplicates (two
                // rows both named "Alien: Isolation") must stay separate so each gets its own
                // decision. A different source stays its own row (its own deep link); the SAME
                // source listing the game under a different category set also stays its own row
                // (so all of a source's tags for a game show up). NUL separators so the fields
                // can't collide.
                var source = (m.SourceId ?? string.Empty).Trim();
                var categories = string.Join("\u0000", m.Categories);
                var key = source + "\u0000" + categories + "\u0000" + game.Id.ToString("D");
                if (!best.TryGetValue(key, out var current) || m.Score > current.Score)
                    best[key] = m;
            }

            var result = new List<VrModReviewMatchEntry>(best.Count + unkeyed.Count);
            result.AddRange(best.Values);
            result.AddRange(unkeyed);
            return result;
        }
    }

    /// <summary>
    /// WPF value converter for MatchDecision → Brush (color).
    ///
    /// Used in the review dialog to color-code the Status column:
    /// - Accepted → Green
    /// - Rejected → OrangeRed
    /// - Pending → Gray
    ///
    /// This provides visual feedback to the user about the current decision state.
    /// </summary>
    public class MatchDecisionToBrushConverter : IValueConverter
    {
        /// <summary>
        /// Converts a MatchDecision to a SolidColorBrush for display.
        /// </summary>
        /// <param name="value">The MatchDecision to convert.</param>
        /// <param name="targetType">Expected type (ignored).</param>
        /// <param name="parameter">Converter parameter (not used).</param>
        /// <param name="culture">Culture info (not used).</param>
        /// <returns>Green brush for Accepted, OrangeRed for Rejected, Gray for Pending.</returns>
        public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is MatchDecision decision)
            {
                if (decision == MatchDecision.Accepted)
                    return new SolidColorBrush(Colors.Green);
                if (decision == MatchDecision.Rejected)
                    return new SolidColorBrush(Colors.OrangeRed);
            }
            return new SolidColorBrush(Colors.Gray);
        }

        /// <summary>
        /// Not implemented (this converter is one-way only).
        /// </summary>
        public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new System.NotImplementedException();
        }
    }

    /// <summary>
    /// The user's choice in the "Reset all decisions" dialog. Maps 1:1 from the chosen
    /// themed-dialog option's title (see <see cref="VrModReviewDialog.ResetChoiceForTitle"/>).
    /// </summary>
    internal enum ResetChoice
    {
        /// <summary>Cancel / closed without a pick: do nothing.</summary>
        Cancel,

        /// <summary>Reset every stored decision; keep the games' tags as they are.</summary>
        ResetOnly,

        /// <summary>Reset every stored decision AND remove every VRMod tag from the library.</summary>
        ResetAndRemoveTags,
    }

    /// <summary>
    /// A review-dialog Alt accelerator, mapped from <see cref="VrModReviewDialog.AcceleratorForKey"/>.
    /// Each one fires the same action as clicking the corresponding control (direct-activate),
    /// so the accelerators work from anywhere in the dialog.
    /// </summary>
    internal enum ReviewAccelerator
    {
        /// <summary>No accelerator (the key is not one of the Alt accelerators).</summary>
        None,

        /// <summary>Alt+F: focus the filter box.</summary>
        FocusFilter,

        /// <summary>Alt+T: open the Time filter dropdown.</summary>
        TimeFilter,

        /// <summary>Alt+S: open the Status filter menu.</summary>
        StatusMenu,

        /// <summary>Alt+C: open the Columns (show/hide) menu.</summary>
        ColumnsMenu,

        /// <summary>Alt+H: toggle the Help popup.</summary>
        Help,

        /// <summary>Alt+A: Accept (the selection, or every match when none is selected).</summary>
        AcceptAll,

        /// <summary>Alt+R: Reject (the selection, or every match when none is selected).</summary>
        RejectAll,
    }

}
