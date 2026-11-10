using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Controls;

namespace Playnite.VRMods
{
    /// <summary>
    /// Main plugin entry point for Playnite's VRMod Tags extension.
    ///
    /// This plugin finds VR mods for the games already in a Playnite library, from entries scraped
    /// from external sources (RototRobot VRMods-List, Flat2VR Studios, UeVR Profiles), and tags the
    /// games whose pairing the user has accepted. It writes nothing he has not agreed to.
    ///
    /// Plugin lifecycle:
    /// 1. OnApplicationStarted: Runs a full scan of all games against all enabled sources
    ///    and shows the review dialog for any Pending matches
    /// 2. OnLibraryUpdated: Scans newly added games for VR mod tags
    /// 3. OnApplicationStopped: Stops the scheduled scan timer and disposes resources
    /// 4. Scheduled timer: Runs periodic full scans at the configured interval
    ///
    /// The plugin is registered as a MetadataPlugin, which means it provides tags and
    /// store links that appear in Playnite's "Download Metadata" UI. It also has
    /// direct event hooks (OnApplicationStarted, OnLibraryUpdated) for automatic scanning.
    ///
    /// Source registry: The plugin registers three data sources:
    /// - RototRobotVrModParser (VRMod Full Motion Controls, UEVR Profiles, Motion Gesture,
    ///   Head Tracked, Injector, Emulators)
    /// - Flat2VrParser (VRMod Flat2VR Port)
    /// - UeVrProfilesParser (VRMod UEVR Profiles)
    ///
    /// Each source can be enabled/disabled independently via settings. When a source
    /// is toggled, the cache is refreshed to ensure stale data is not used.
    ///
    /// Review dialog: the matches a scan found are presented
    /// to the user in a review dialog (ShowReviewDialog). Nothing reaches a game before that dialog
    /// decides. The user can Accept (the tags and the link are written), Reject (never offered again),
    /// or leave as Pending (nothing is written; the pairing waits for another time).
    /// </summary>
    public class VrModTagPlugin : MetadataPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly VrModSourceRegistry _registry;
        private readonly VrModTagService _tagService;
        private readonly VrModSettingsViewModel _settingsViewModel;
        private Timer? _timer;
        private bool _isDisposed;

        /// <summary>
        /// Unique identifier for this plugin.
        /// Used to store plugin settings in Playnite's config directory.
        /// </summary>
        public override Guid Id { get; } = Guid.Parse("E4A2F8D1-3B5C-4E9A-A7D6-8F1C0E2B4A6D");

        /// <summary>
        /// Display name shown in Playnite's extension list.
        /// </summary>
        public override string Name => "VRMod Tags";

        /// <summary>
        /// Lists the metadata fields this plugin provides.
        /// Tags: VRMod category tags (VRMod UEVR Profiles, VRMod Flat2VR Port, etc.)
        /// Links: source entry URLs for VR mod sources
        /// </summary>
        public override List<MetadataField> SupportedFields { get; } = new() { MetadataField.Tags, MetadataField.Links };

        /// <summary>
        /// Exposes the TagService for external access (e.g., tests, other plugins).
        /// </summary>
        public VrModTagService TagService => _tagService;

        /// <summary>
        /// Exposes the settings ViewModel for tests (verifies what the plugin constructed,
        /// e.g. defaults after a corrupted saved-settings file).
        /// </summary>
        internal VrModSettingsViewModel SettingsViewModel => _settingsViewModel;

        /// <summary>
        /// Constructs the plugin and initializes all dependencies.
        ///
        /// Steps performed:
        /// 1. Creates and configures the source registry with all VR mod parsers
        /// 2. Creates the settings ViewModel (loads saved settings from disk)
        /// 3. Creates the tag service (initialized with settings and logger)
        /// 4. Subscribes to settings change events (cache refresh, timer restart)
        ///
        /// TLS is intentionally not touched here: see the comment in the constructor body.
        /// </summary>
        /// <param name="api">The Playnite API instance provided by the framework.</param>
        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Ownership of every parser transfers to _sourceRegistry, which owns them; Dispose() disposes all registered sources, and BaseVrModParser.Dispose() closes the shared HttpClient.")]
        public VrModTagPlugin(IPlayniteAPI api) : base(api)
        {
            // No ServicePointManager.SecurityProtocol assignment here (CA5386). Playnite already
            // publishes a process-wide value itself (Playnite's own PlayniteApplication sets
            // Tls12 | Tls11 | Tls), and this plugin runs inside that same process. The old line
            // replaced Playnite's choice with TLS 1.2 only — for Playnite's own HTTPS traffic and for
            // every other plugin in the library — and made TLS 1.3 unreachable even where the OS
            // supported it. The host decides the host's protocol; a plugin must not restage it.

            // Declare that this plugin has settings
            Properties = new MetadataPluginProperties
            {
                HasSettings = true
            };

            // Register all VR mod data sources in the source ranking from
            // external-references.md (best first) — the settings view shows the
            // sources in this order. VorpX / Luke Ross (opt-in capability/author
            // lists, disabled by default) are last by design.
            _registry = new VrModSourceRegistry();
            _registry.Register(new PcvrCentralParser());
            _registry.Register(new UeVrProfilesParser());
            _registry.Register(new RototRobotVrModParser());
            _registry.Register(new MrNlceHubParser());
            _registry.Register(new CompoundVrParser());
            _registry.Register(new MrSurvivorHubParser());
            _registry.Register(new SteamCuratorParser(
                "SteamCuratorUEVR", "Steam Curator UEVR Compatibility",
                "https://store.steampowered.com/curator/44946997-UEVR-Compatibility/", 44946997,
                VrModCategories.UeVr));
            _registry.Register(new SteamCuratorParser(
                "SteamCuratorVorpX", "Steam Curator VorpX Games",
                "https://store.steampowered.com/curator/38688993-VorpX-Games/", 38688993,
                VrModCategories.VorpX));
            _registry.Register(new ReFrameworkParser());
            _registry.Register(new Flat2VrParser());
            _registry.Register(new VorpXParser());
            _registry.Register(new LukeRossParser());

            // Create settings ViewModel (loads saved settings from disk)
            _settingsViewModel = new VrModSettingsViewModel(this, _registry);

            // Create tag service (initialized with settings and logger)
            _tagService = new VrModTagService(api, _settingsViewModel.Settings, Logger, _registry);

            // Subscribe to settings change events (cache refresh, timer restart, etc.)
            _settingsViewModel.PropertyChanged += OnSettingsChanged;
        }

        /// <summary>
        /// Returns the metadata provider for on-demand metadata fetching.
        ///
        /// Playnite calls this method when the user requests metadata for a game
        /// (e.g., via "Download Metadata" in the game details view).
        ///
        /// The metadata provider implements GetTags() and GetLinks() to provide
        /// VRMod tags and Steam store links for the game.
        /// </summary>
        /// <param name="options">Metadata request options including the game being queried.</param>
        /// <returns>A VrModMetadataProvider instance for the specified game.</returns>
        public override OnDemandMetadataProvider GetMetadataProvider(MetadataRequestOptions options)
        {
            return new VrModMetadataProvider(_tagService, options.GameData);
        }

        /// <summary>
        /// Returns the settings ViewModel for Playnite's settings UI.
        ///
        /// Called when the user opens the plugin settings dialog.
        /// Returns the same ViewModel instance created in the constructor.
        /// </summary>
        /// <param name="firstRunSettings">True if this is the first time settings are being accessed.</param>
        /// <returns>The settings ViewModel for binding to the settings view.</returns>
        public override ISettings GetSettings(bool firstRunSettings)
        {
            return _settingsViewModel;
        }

        /// <summary>
        /// Returns the settings view UserControl for Playnite's settings UI.
        ///
        /// Called when the user opens the plugin settings dialog.
        /// Sets the DataContext to the settings ViewModel for data binding.
        /// </summary>
        /// <param name="firstRunView">True if this is the first time the settings view is being accessed.</param>
        /// <returns>The settings view UserControl.</returns>
        public override UserControl GetSettingsView(bool firstRunView)
        {
            return new VrModSettingsView { DataContext = _settingsViewModel };
        }

        /// <summary>
        /// Called when Playnite application starts (first install or app launch).
        ///
        /// Performs a full scan of all games against all enabled sources.
        /// Shows the review dialog for any Pending matches.
        /// Starts a scheduled scan timer for periodic full scans.
        ///
        /// This is the primary entry point for automatic VR mod detection.
        /// When the plugin is first installed, this method scans all games in the library.
        /// On subsequent app launches, it scans again (respecting cache duration).
        /// </summary>
        /// <param name="args">Application start event arguments (not used).</param>
        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            Logger.Info("VRMod Tags plugin started. Running full scan...");

            // Seed new-game detection: every game in the library right now is "known" and is
            // covered by the startup full scan below; only games added to the library AFTER
            // this point are "new" and get the fast single-game path (OnLibraryUpdated).
            _tagService.SeedKnownGames();

            // Run full scan with review dialog for Pending matches.
            // Observe the task so a fault (e.g. inside the review dialog) is logged
            // instead of being silently swallowed by the fire-and-forget pattern.
            var startupScan = Task.Run(() => RunScanWithProgress(autoScan: true));
            _ = startupScan.ContinueWith(
                t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                        Logger.Error($"VRMod: startup full scan faulted: {t.Exception.Flatten()}");
                    else if (t.IsCanceled)
                        Logger.Warn("VRMod: startup full scan was canceled");
                },
                // CA2008: name the scheduler instead of inheriting whatever the current one is.
                // The continuation only logs, so it belongs on the thread pool, not on a UI
                // dispatcher that a scan happened to be running on.
                TaskScheduler.Default);

            // Set up scheduled scan timer
            StartTimer();
        }

        /// <summary>
        /// Called by Playnite after a library update has added games AND downloaded their
        /// "normal" metadata (the only reliable add-game event — OnGameInstalled fires only
        /// for the interactive install flow, never for store/source adds).
        ///
        /// The service diffs the library against its known-game baseline and batch-scans only
        /// the NEW games for VR mod tags (one gated scan, sources fetched once, each game
        /// gets its tags — even while a scan still holds the gate, new games are queued and
        /// drained).
        /// </summary>
        /// <param name="args">Event arguments (carries no game data — the service diffs itself).</param>
        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            Logger.Info("VRMod: library updated — checking for new games to scan...");
            _tagService.NotifyLibraryUpdated();
        }

        /// <summary>
        /// Called when Playnite application is about to shut down.
        ///
        /// If Playnite queued this plugin for uninstall, performs the one-time clean-uninstall
        /// of the shared library (all VRMod tags, tag definitions, plugin-owned source links,
        /// settings file). This runs BEFORE the tag service is disposed: link reconciliation
        /// needs the source cache (the plugin-owned link URLs), which the service's Dispose
        /// clears.
        ///
        /// Then stops the scheduled scan timer and disposes the tag service
        /// to clean up resources (cache, HTTP client, etc.).
        /// </summary>
        /// <param name="args">Application stop event arguments (not used).</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "Uninstall cleanup runs once, at shutdown, with no retry path and nobody left to report it to.")]
        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            // Playnite removes an uninstalled extension's files only at the next startup: it
            // queues the extension directory in extinstalls.json and deletes it on the next
            // launch — long after this plugin has stopped running. This app-stop event (which
            // Playnite fires before it disposes the plugin and its database) is the only
            // moment to clean the shared library. Normal shutdowns are a no-op: the cleanup
            // runs only when Playnite queued THIS plugin for uninstall.
            if (IsUninstallQueued())
            {
                try
                {
                    Logger.Info("VRMod Tag Plugin: uninstall queued — removing all VRMod tags, source links, tag definitions and settings.");
                    _tagService.UninstallCleanup();
                    DeletePluginSettingsFile();
                }
                catch (Exception e)
                {
                    Logger.Error(e, "VRMod Tag Plugin: uninstall cleanup failed.");
                }
            }

            StopTimer();
            _tagService.Dispose();
        }

        /// <summary>
        /// Runs a scheduled full scan (called by the timer).
        ///
        /// This method is always called on a background thread (timer callback or Task.Run).
        /// The review dialog is invoked on the UI thread via the callback.
        /// </summary>
        private void RunScheduledScan()
        {
            Logger.Info("Running scheduled VR mod scan...");
            RunScanWithProgress(autoScan: true);
        }

        /// <summary>
        /// Runs a full scan.
        ///
        /// When <paramref name="autoScan"/> is true (startup / scheduled timer) the scan runs
        /// in the background with NO title-bar progress window (like a library import) and the
        /// only user-visible result is a notification (click it to review). Otherwise it runs
        /// under the title-bar progress window and the review dialog is presented for any
        /// pending matches (the settings button uses <see cref="RunShowGamesScan"/> instead).
        ///
        /// Must be called from a BACKGROUND thread: the manual path blocks the calling thread
        /// in ActivateGlobalProgress while the progress window is shown, the auto path blocks
        /// it while the scan runs, and the review dialog is then presented modally on the UI
        /// thread. The scan collects matches without showing the dialog inline, so the review
        /// dialog is not nested inside any modal window.
        /// </summary>
        public void RunScanWithProgress(bool autoScan = false)
        {
            RunScanEntryPoint(nameof(RunScanWithProgress), () => RunScanWithProgressCore(reviewAll: false, autoScan));
        }

        /// <summary>
        /// Runs a scan in "show all" mode: the review dialog is presented with EVERY matched game
        /// (accepted + rejected + pending), not just the pending ones, so the user can review or
        /// change their decisions. The scan itself applies the current decisions exactly as a
        /// normal scan would — only the dialog's contents differ.
        /// </summary>
        public void RunShowGamesScan()
        {
            RunScanEntryPoint(nameof(RunShowGamesScan), () => RunScanWithProgressCore(reviewAll: true, autoScan: false));
        }

        /// <summary>
        /// Opens the review dialog for EVERY matched game (accepted + rejected + pending) from
        /// the in-memory source cache, WITHOUT running a scan — no network, no progress window.
        /// This is the settings "Review VR Mods" button: the dialog opens immediately from
        /// what is already known (a startup or earlier manual scan always populated the cache).
        ///
        /// Only when no source data is cached at all (a cold start, before the startup scan has
        /// populated the cache) does it fall back to a full scan (progress window, then the
        /// dialog) — otherwise there would be nothing to show.
        ///
        /// Must be called from a BACKGROUND thread: the dialog is presented modally on the UI
        /// thread, and on the fallback path the caller blocks in the progress window (the same
        /// contract as <see cref="RunShowGamesScan"/>).
        /// </summary>
        public void RunReviewFromCache()
            => RunScanEntryPoint(nameof(RunReviewFromCache), RunReviewFromCacheCore);

        private void RunReviewFromCacheCore()
        {
            Logger.Info("VRMod: Review VR Mods: opening the review dialog from cached source data (no scan)");
            var matches = _tagService.BuildReviewMatchesFromCache();
            if (matches != null)
            {
                // The cache is the last known data, so an empty list here means nothing in it fits a game.
                // An empty dialog would say the same thing by showing nothing, which is not a sentence.
                if (matches.Count == 0)
                    TellMenu("Review VR Mods", "No VR mod in the sources matched any game in your library.");
                else
                    ShowReviewDialog(matches);
                return;
            }

            Logger.Info("VRMod: Review VR Mods: no cached source data (cold start); running a scan first.");
            RunShowGamesScan();
        }

        /// <summary>
        /// The four scan entry points above all BLOCK their caller (title-bar progress window, modal
        /// review dialog, <c>GetAwaiter().GetResult()</c>), so they are only safe off Playnite's UI
        /// thread. Every caller honoured that rule; it was written in comments and nothing checked
        /// it. This turns the comment into a rule: a caller that shows up on the UI thread is moved to
        /// the thread pool, with a warning naming the entry point, instead of freezing Playnite.
        ///
        /// Consequence for a re-dispatched caller: the call returns BEFORE the scan completes. With no
        /// WPF UI at all (unit tests, headless host) the body runs inline, which is what the tests
        /// have always relied on.
        /// </summary>
        private static void RunScanEntryPoint(string entryPoint, Action body)
        {
            var app = System.Windows.Application.Current;
            var dispatcher = app?.Dispatcher;
            if (ScanBodyMayRunOnCallerThread(app != null, dispatcher != null && dispatcher.CheckAccess()))
            {
                body();
                return;
            }

            Logger.Warn($"VRMod: {entryPoint} was called on Playnite's UI thread; the scan blocks its caller, so it runs on a background thread instead and this call returns before the scan finishes.");
            var run = Task.Run(body);
            _ = run.ContinueWith(
                t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                        Logger.Error($"VRMod: {entryPoint} faulted on the re-dispatched thread: {t.Exception.Flatten()}");
                },
                // CA2008: name the scheduler. A log-only continuation must not inherit a UI dispatcher.
                TaskScheduler.Default);
        }

        /// <summary>
        /// The decision behind <see cref="RunScanEntryPoint" />, split out so it is testable without a
        /// WPF application: a blocking scan body may run on the calling thread when there is no UI to
        /// block, or when the caller is already off the UI thread.
        /// </summary>
        internal static bool ScanBodyMayRunOnCallerThread(bool hasWpfUi, bool callerIsOnUiThread)
            => !hasWpfUi || !callerIsOnUiThread;

        // ============================================================================
        // Menu entry points
        //
        // Playnite asks for these items every time a menu opens, so what happens here is a label and
        // two booleans: nothing here may wait for a scan, walk the catalog, or reach the network. All
        // of the slow work sits behind an item's Action, on a background thread.
        //
        // Both menus put the items in a submenu of their own ("@VRMod Tags" under Extensions, "VRMod
        // Tags" in the game menu - the "@" is Playnite's marker for "under Extensions", see
        // Playnite.DesktopApp/Controls/Menus/MainMenu.cs:404 and GameMenu.cs:687, which both group by
        // MenuSection and label by Description). The submenu title is the attribution: it says once
        // which plugin the items belong to, instead of repeating a plugin prefix on every item and
        // pushing the other plugins' items further down the menu.
        // ============================================================================

        /// <summary>Our main-menu submenu, inside Extensions.</summary>
        internal const string MainMenuSection = "@VRMod Tags";

        /// <summary>Our submenu in the game context menu.</summary>
        internal const string GameMenuSection = "VRMod Tags";

        /// <summary>
        /// The plugin's items in Playnite's main menu, under Extensions → VRMod Tags.
        ///
        /// "Scan..." scans the whole library and ends in the review list. The name says what starts,
        /// not what follows: a user who clicks Scan in a menu expects the review anyway, and the
        /// tooltip says the rest.
        ///
        /// "Settings..." opens the plugin's Add-ons page. <see cref="Plugin.OpenSettingsView"/> is a
        /// no-op in fullscreen mode, so the item is not offered there.
        /// </summary>
        /// <param name="args">Playnite gives the main menu nothing to select.</param>
        /// <returns>Both items. Neither depends on a selection.</returns>
        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = MainMenuSection,
                Description = "Scan...",
                Action = _ => RunFromMenu(nameof(RunShowGamesScan), "Scan for VR mods", RunShowGamesScan)
            };

            if (API.Instance.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
                yield break;

            yield return new MainMenuItem
            {
                MenuSection = MainMenuSection,
                Description = "Settings...",
                Action = _ => OpenSettingsView()
            };
        }

        /// <summary>
        /// The plugin's items in the game context menu, under their own "VRMod Tags" submenu, with the
        /// size of the selection in the label. "Check..." is always there; the two decision items only
        /// appear for a selection they can act on, and those are two different questions - see
        /// <see cref="VrModTagService.GetDecisionStateForGames"/>. Both answers come from the plugin's
        /// own saved decisions, which is its record of what it did; the tags on a game are library state
        /// anyone can edit, so they ask nothing the record cannot answer better - and cheaper, since a
        /// decision lookup is indexed per game while a tag answer walks the tag collection.
        /// </summary>
        /// <param name="args">The selected games, in <c>args.Games</c>.</param>
        /// <returns>One to three items.</returns>
        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var games = (args?.Games ?? new List<Game>()).Where(g => g != null).ToList();
            if (games.Count == 0)
                yield break;

            yield return new GameMenuItem
            {
                MenuSection = GameMenuSection,
                Description = MenuItemLabel("Check this game for VR mods...", "Check these {0} games for VR mods...", games.Count),
                Action = _ => RunFromMenu(nameof(RunCheckGamesScan), "Check for VR mods", () => RunCheckGamesScan(games))
            };

            var (anyAssigned, anyStoredDecision) = _tagService.GetDecisionStateForGames(games);

            if (anyStoredDecision)
            {
                yield return new GameMenuItem
                {
                    MenuSection = GameMenuSection,
                    Description = MenuItemLabel("Reset my decisions for this game...", "Reset my decisions for these {0} games...", games.Count),
                    Action = _ => RunResetDecisionsForGames(games)
                };
            }

            if (anyAssigned)
            {
                yield return new GameMenuItem
                {
                    MenuSection = GameMenuSection,
                    Description = MenuItemLabel("Reject all assigned VR mods on this game...", "Reject all assigned VR mods on these {0} games...", games.Count),
                    Action = _ => RunRejectAssignedForGames(games)
                };
            }
        }

        /// <summary>
        /// A menu label for a selection of one or of many. Split out because the labels are the only
        /// part of the menu a test can check without a Playnite UI. Playnite's menu items have no
        /// tooltip - <c>PluginMenuItem</c> carries only Icon, Description and MenuSection - so the
        /// label and the confirmation dialog have to carry the whole meaning between them.
        /// </summary>
        /// <param name="singular">The label for one game.</param>
        /// <param name="pluralFormat">The label for several games, with <c>{0}</c> for the count.</param>
        /// <param name="gameCount">How many games are selected.</param>
        /// <returns>The fitting label.</returns>
        internal static string MenuItemLabel(string singular, string pluralFormat, int gameCount)
            => gameCount == 1 ? singular : string.Format(System.Globalization.CultureInfo.InvariantCulture, pluralFormat, gameCount);

        /// <summary>
        /// Runs a menu action off Playnite's UI thread.
        ///
        /// The scan entry points move a UI-thread caller to the thread pool themselves, but they log a
        /// warning every time they have to - and a menu action always shows up on the UI thread, so the
        /// warning would fire on every click and stop meaning anything. The hop belongs here; the
        /// warning stays what it is: a sign that a caller forgot the rule.
        ///
        /// A fault cannot be left to Playnite: the host shows a dialog for an Action that throws, but a
        /// Task.Run body never gets back into its hands. So it is logged, and answered in a window: the
        /// caption is what he clicked, so the box is recognisable as the thing he asked for.
        /// </summary>
        /// <param name="name">The action to name in the log.</param>
        /// <param name="caption">The window title for a failure, in his words rather than in code terms.</param>
        /// <param name="body">The work.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "A menu action must not fail silently into a discarded task and must not throw into Playnite either; the fault is logged and answered in a window.")]
        private static void RunFromMenu(string name, string caption, Action body)
        {
            var run = Task.Run(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    Logger.Error($"VRMod: menu action {name} failed: {ex}");
                    // His words, not the internal name: the caption already says what he clicked.
                    TellMenu(caption, "This action failed. The plugin log has the details.");
                }
            });
            _ = run.ContinueWith(
                t =>
                {
                    var fault = t.Exception?.Flatten();
                    if (fault == null)
                    {
                        // The task finished normally. Logging an error here is what made every successful
                        // action look like a crash: "faulted outside its own handler:" with nothing after
                        // the colon, because the interpolated t.Exception was null.
                        return;
                    }

                    Logger.Error($"VRMod: menu action {name} faulted outside its own handler: {fault}");
                },
                // This observer is a last resort for a fault that escaped the catch above (or a cancelled
                // task), not a completion receipt. OnlyOnFaulted is not used because the 3-argument
                // ContinueWith overload resolves to the state-taking one here, which turns the options
                // enum into a boxed state and fails to bind; guarding on null is equivalent.
                // CA2008: a logging continuation must not inherit a UI dispatcher.
                TaskScheduler.Default);
        }

        /// <summary>
        /// A corner notification: Playnite's way of saying something later, to someone who is not looking at
        /// the screen now. No menu path ends in one on purpose - he clicked, so he is looking, and a request
        /// gets its answer in a window (<see cref="TellMenu"/>). What is left of this method is the fallback
        /// inside <see cref="TellMenu"/> for a host with no window to own a dialog. The id is its own; the
        /// automatic-scan bell has another one, and the two must never stand in for each other.
        /// </summary>
        /// <param name="text">The message.</param>
        /// <param name="type">How Playnite styles it.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "Playnite's notification API is third-party surface; a notification that will not show must not fail the action that produced it.")]
        private static void NotifyMenu(string text, NotificationType type = NotificationType.Info)
        {
            try
            {
                API.Instance.Notifications.Add(new NotificationMessage("vrmod-menu", text, type));
            }
            catch (Exception ex)
            {
                Logger.Warn($"VRMod: failed to show a menu notification: {ex.Message}");
            }
        }

        /// <summary>
        /// A modal message box answering a menu action he can see the end of. The menu is gone the moment an
        /// item is clicked, so anything he has to read now - nothing was found, the scan did not start, the
        /// work failed - has nothing else on the screen to say it, and the answer goes where he is looking.
        /// What an action achieved is never told here: he started it, he wanted the change, and the library
        /// changing in front of him is the report.
        ///
        /// Menu actions run on a worker thread, and a dialog is a WPF window, so this marshals to Playnite's
        /// UI dispatcher and waits for the box to close - the same route the review dialog takes.
        /// </summary>
        /// <param name="caption">The window title.</param>
        /// <param name="text">What happened, in the user's words.</param>
        private static void TellMenu(string caption, string text)
        {
            var dispatcher = API.Instance.MainView?.UIDispatcher;
            if (dispatcher == null)
            {
                // No window to own a dialog (or no UI at all). The answer still has to reach the user.
                Logger.Warn("VRMod: no UI dispatcher for a menu message box; falling back to a notification.");
                NotifyMenu(text, NotificationType.Error);
                return;
            }

            if (dispatcher.CheckAccess())
                API.Instance.Dialogs.ShowMessage(text, caption);
            else
                dispatcher.Invoke(() => API.Instance.Dialogs.ShowMessage(text, caption));
        }

        /// <summary>
        /// Asks the user before a menu action changes the library. Playnite's themed question, two
        /// options: closing the dialog counts as Cancel.
        /// </summary>
        /// <param name="title">Dialog title.</param>
        /// <param name="text">What will happen, in the user's words.</param>
        /// <param name="acceptLabel">The label of the option that goes ahead.</param>
        /// <returns>True when the user chose to go ahead.</returns>
        private static bool ConfirmMenuAction(string title, string text, string acceptLabel)
        {
            var chosen = API.Instance.Dialogs.ShowMessage(
                text,
                title,
                System.Windows.MessageBoxImage.Question,
                new List<MessageBoxOption>
                {
                    new MessageBoxOption(acceptLabel, isDefault: true),
                    new MessageBoxOption("Cancel", isCancel: true),
                });
            return chosen?.Title == acceptLabel;
        }

        /// <summary>
        /// "Check this game for VR mods...": scans only the selected games, then opens their review
        /// list. Runs under the title-bar progress window like a full scan, and the review dialog opens
        /// after the window closes, not inside it (the same reason as
        /// <see cref="RunScanWithProgressCore"/>).
        /// </summary>
        /// <param name="games">The selected games.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "A menu action must not throw back into Playnite; the fault is logged and reported as a notification.")]
        private void RunCheckGamesScan(IReadOnlyList<Game> games)
        {
            var title = games.Count == 1
                ? $"Checking '{games[0].Name}' for VR mods…"
                : $"Checking {games.Count} games for VR mods…";
            List<VrModReviewMatchEntry>? rows = null;
            var cancelled = false;

            var progRes = API.Instance.Dialogs.ActivateGlobalProgress(
                progArgs =>
                {
                    progArgs.Text = title;
                    try
                    {
                        rows = _tagService.RunScanForGamesWithReview(
                            games,
                            (current, total, gameName) => progArgs.Text = $"{title}\n{gameName} ({current}/{total})",
                            progArgs.CancelToken);
                    }
                    catch (System.OperationCanceledException)
                    {
                        cancelled = true;
                        Logger.Info("VRMod: game-menu check cancelled by user.");
                    }
                },
                new GlobalProgressOptions(title) { IsIndeterminate = true, Cancelable = true });

            if (cancelled)
                return;
            if (progRes.Error != null)
            {
                Logger.Error($"VRMod: game-menu check failed: {progRes.Error}");
                TellMenu("Check for VR mods", "The check did not finish. The plugin log has the details.");
                return;
            }
            if (rows == null)
            {
                // The scan did not run: another scan holds the gate, or no source is enabled. He asked from
                // a menu and is watching for an answer, so this is a window, not a bell he may never open.
                TellMenu("Check for VR mods", "The scan did not start - another VRMod scan may still be running, or no source is enabled.");
                return;
            }
            if (rows.Count == 0)
            {
                // Nothing to open, and the menu closed ages ago: say so where he is looking.
                TellMenu("Check for VR mods", "No VR mod was found for the selected game(s).");
                return;
            }

            ShowReviewDialog(rows);
        }

        /// <summary>
        /// "Reset my decisions for this game...": clears the stored accepted/rejected decisions of the
        /// selected games and takes the tags and links those decisions put there off the games, in the same
        /// action. The next scan proposes the mods again, which is the point - this is also the way back
        /// from a reject.
        /// </summary>
        /// <param name="games">The selected games.</param>
        private void RunResetDecisionsForGames(IReadOnlyList<Game> games)
        {
            if (!ConfirmMenuAction(
                "Reset decisions",
                MenuItemLabel(
                    "Reset your decisions for this game?\n\nIts accepted and rejected VR mods are forgotten and its VR mod tags and links are removed, so the game keeps nothing the plugin no longer stands behind.\n\nThe next scan will offer the mods for review again.",
                    "Reset your decisions for these {0} games?\n\nTheir accepted and rejected VR mods are forgotten and their VR mod tags and links are removed, so the games keep nothing the plugin no longer stands behind.\n\nThe next scan will offer the mods for review again.",
                    games.Count),
                "Reset"))
                return;

            RunFromMenu("reset decisions", "Reset decisions", () =>
            {
                var cleaned = _tagService.ResetDecisionsForGames(games);
                SavePluginSettings(_tagService.Settings);
                Logger.Info($"VRMod: reset the VR mod decisions of {games.Count} game(s) from the game menu; {cleaned} changed on tags or links.");
            });
        }

        /// <summary>
        /// "Reject all assigned VR mods on this game...": vetoes the mods assigned to the selected games
        /// and takes their tags and links off now. Other mods that fit the games keep being suggested -
        /// the veto is per listing and per game, not a ban on the game. "Reset my decisions..." undoes it.
        /// </summary>
        /// <param name="games">The selected games.</param>
        private void RunRejectAssignedForGames(IReadOnlyList<Game> games)
        {
            if (!ConfirmMenuAction(
                "Reject VR mods",
                MenuItemLabel(
                    "Reject the VR mods assigned to this game?\n\nTheir tags and links are removed now, and this plugin will not offer them again.\n\nOther VR mods that fit the game are still suggested. \"Reset my decisions for this game...\" clears the veto; the tags come back when you accept a mod again.",
                    "Reject the VR mods assigned to these {0} games?\n\nTheir tags and links are removed now, and this plugin will not offer them again.\n\nOther VR mods that fit the games are still suggested. \"Reset my decisions for these games...\" brings them back.",
                    games.Count),
                "Reject"))
                return;

            RunFromMenu("reject assigned VR mods", "Reject VR mods", () =>
            {
                var title = "Rejecting the assigned VR mods…";
                (int Rejected, int GamesChanged) result = (0, 0);
                var progRes = API.Instance.Dialogs.ActivateGlobalProgress(
                    progArgs =>
                    {
                        progArgs.Text = title;
                        result = _tagService.RejectAssignedVrMods(
                            games,
                            (current, total, gameName) => progArgs.Text = $"{title}\n{gameName} ({current}/{total})");
                    },
                    new GlobalProgressOptions(title) { IsIndeterminate = true });

                if (progRes.Error != null)
                {
                    Logger.Error($"VRMod: reject failed: {progRes.Error}");
                    return;
                }

                SavePluginSettings(_tagService.Settings);
                Logger.Info($"VRMod: rejected {result.Rejected} assigned VR mod(s) on {games.Count} game(s) from the game menu; {result.GamesChanged} game(s) lost tags or links.");
            });
        }

        /// <summary>
        /// Runs a full scan that shows no review dialog — the settings
        /// "Full Scan" button. Accepted matches are re-applied; a pairing with no stored decision is
        /// held for the review dialog instead, so the button cannot tag anything the user never agreed
        /// to (the name "ApplyOnly" is the old model's: what it applies is what his decisions back).
        /// Runs under
        /// the title-bar progress window; the progress window (and the refreshed "Games with VR
        /// mods" count) is the feedback. Must be called from a BACKGROUND thread (the manual
        /// path blocks the caller in ActivateGlobalProgress while the window is shown).
        /// </summary>
        public void RunFullScanApplyOnly()
            => RunScanEntryPoint(nameof(RunFullScanApplyOnly), RunFullScanApplyOnlyCore);

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "A scan started from Playnite's menu must not throw back into Playnite's command handler.")]
        private void RunFullScanApplyOnlyCore()
        {
            Logger.Info("VRMod: scan: starting (Full Scan: no review dialog, stored decisions only)");
            bool scanStarted = false;
            bool scanCancelled = false;
            try
            {
                var progRes = API.Instance.Dialogs.ActivateGlobalProgress(
                    async progArgs =>
                    {
                        progArgs.Text = "VRMod scan in progress…";
                        // reviewCallback == null → nothing is shown and nothing is asked: a pairing
                        // with no stored decision is held for the next dialog, not tagged. No notification.
                        // progArgs.CancelToken is threaded through: the window is Cancelable, so
                        // Cancel actually aborts the web requests instead of only hiding the window.
                        try
                        {
                            scanStarted = await _tagService.RunFullScanAsync(null, false, progArgs.CancelToken);
                        }
                        catch (System.OperationCanceledException)
                        {
                            scanCancelled = true;
                            Logger.Info("VRMod: scan cancelled by user.");
                        }
                    },
                    new GlobalProgressOptions("VRMod scan in progress…") { IsIndeterminate = true, Cancelable = true });
                if (progRes.Error != null)
                {
                    Logger.Error($"VRMod: scan failed: {progRes.Error}");
                    // He pressed a button and the progress bar closed on him. The settings page has no
                    // status line that survives the scan, so the fact goes in a window; the cause is
                    // in the log.
                    TellMenu("Full Scan", "The scan did not finish. The plugin log has the details.");
                }
                else if (!scanStarted && !scanCancelled)
                {
                    Logger.Warn("VRMod: scan did not start - another scan holds the gate, or no source is enabled.");
                    TellMenu("Full Scan", "The scan did not start - another VRMod scan may still be running, or no source is enabled.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"VRMod: scan failed: {ex}");
                TellMenu("Full Scan", "The scan did not finish. The plugin log has the details.");
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "The progress window is already closed by a finally; the fault is logged and the scan ends.")]
        private void RunScanWithProgressCore(bool reviewAll, bool autoScan)
        {
            var mode = autoScan ? "background, no progress window + notification" : "title-bar progress window + review dialog";
            Logger.Info($"VRMod: scan: starting (reviewAll={reviewAll}, autoScan={autoScan}, {mode})");
            List<VrModReviewMatchEntry>? reviewMatches = null;
            bool scanCancelled = false;
            try
            {
                if (autoScan)
                {
                    // Automatic scan (startup / scheduled timer): run in the background like a
                    // library import — no title-bar progress window. Just collect the matches;
                    // the notification shown afterwards is the only user-visible signal. The
                    // return value is logged rather than shown: when the scan never ran (another
                    // scan holds the single-scan gate, or no source is enabled) there is nothing
                    // to report to a user who asked for nothing - but the log has to say the scan
                    // did not run, or an empty result reads as "it looked and found nothing".
                    if (!_tagService.RunFullScanAsync(matches => reviewMatches = matches, reviewAll).GetAwaiter().GetResult())
                        Logger.Warn("VRMod: automatic scan did not start - another scan holds the gate, or no source is enabled.");
                }
                else
                {
                    // Manual scan (Extensions > VRMod Tags > Scan..., or the settings page's
                    // Review VR Mods when no scan data is cached yet): show the title-bar progress
                    // window while the scan runs.
                    bool scanStarted = false;
                    var progRes = API.Instance.Dialogs.ActivateGlobalProgress(
                        async progArgs =>
                        {
                            progArgs.Text = "VRMod scan in progress…";
                            // Collect matches without presenting the dialog here: the dialog is shown
                            // after the progress window closes, so it is not nested inside the modal
                            // progress window. In reviewAll mode this is every matched game; otherwise
                            // just the pending ones.
                            try
                            {
                                scanStarted = await _tagService.RunFullScanAsync(matches => reviewMatches = matches, reviewAll, progArgs.CancelToken);
                            }
                            catch (System.OperationCanceledException)
                            {
                                // Cancel button (or the service's scan budget firing on the caller's
                                // token): the scan stopped before matching/applying, so nothing was
                                // reported and nothing was written to the library.
                                scanCancelled = true;
                                Logger.Info("VRMod: scan cancelled by user.");
                            }
                        },
                        new GlobalProgressOptions("VRMod scan in progress…") { IsIndeterminate = true, Cancelable = true });
                    if (progRes.Error != null)
                    {
                        Logger.Error($"VRMod: scan failed: {progRes.Error}");
                        // The progress window that carried the scan has closed, so nothing is left on the
                        // screen to report it. A manual scan means he asked, so the failure goes to him in
                        // a window; an automatic one stays in the log alone - it interrupts nobody.
                        if (!autoScan)
                            TellMenu("Scan for VR mods", "The scan did not finish. The plugin log has the details.");
                        return;
                    }

                    if (!scanStarted && !scanCancelled)
                    {
                        // The progress window opened and closed in a blink: the scan never ran,
                        // because another scan holds the single-scan gate or no source is enabled.
                        // Said now, because the alternative is the empty-result window below, which
                        // would claim no mod fits his library - a different fact, and one he would
                        // act on.
                        Logger.Warn("VRMod: scan did not start - another scan holds the gate, or no source is enabled.");
                        TellMenu("Scan for VR mods", "The scan did not start - another VRMod scan may still be running, or no source is enabled.");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"VRMod: scan failed: {ex}");
                if (!autoScan)
                    TellMenu("Scan for VR mods", "The scan did not finish. The plugin log has the details.");
                return;
            }

            if (scanCancelled)
            {
                // A cancelled scan has no complete result set: do not open the review dialog or fire
                // the auto-scan notification on half a scan.
                Logger.Info("VRMod: scan cancelled; no review dialog shown.");
                return;
            }

            var matches = reviewMatches ?? new List<VrModReviewMatchEntry>();
            if (autoScan)
            {
                // Automatic scan (startup / scheduled timer): do not auto-open the review
                // dialog. Show a notification instead; clicking it opens the review dialog.
                // A scan that never started lands here too - BuildAutoScanMessage returns null
                // for no pending matches and no tag changes, so the bell stays silent.
                ShowAutoScanNotification(matches);
            }
            else if (matches.Count == 0)
            {
                // The progress window closed on nothing that was actually found (a scan that never
                // ran returned above), and an empty review dialog is a window with nothing in it.
                // reviewAll is every matched game, so zero of those says no mod fits the library;
                // without it the list is only the pending ones, and zero of those says nothing about
                // how many matched - so the two get different sentences.
                TellMenu("Scan for VR mods", reviewAll
                    ? "No VR mod in the sources matched any game in your library."
                    : "No pending VR mod match to review.");
            }
            else
            {
                // Manual scan (the menu item, or the settings button when the cache was cold): the progress
                // bar just closed and the review dialog follows it.
                ShowReviewDialog(matches);
            }
        }

        /// <summary>
        /// Shows the auto-scan (startup / scheduled timer) notification — unless the scan
        /// found nothing to report. The message text is built by
        /// <see cref="BuildAutoScanMessage"/>; when it returns null (no new matches and no
        /// tag changes) NO notification is posted at all — the only trace of such a scan is
        /// the per-source last-run status in the settings view. When a notification IS
        /// posted it gets a click handler: the click target is chosen by
        /// <see cref="ClassifyAutoScanClickTarget"/> — pending matches open the review
        /// dialog for them, tag changes on already-accepted matches open the full review
        /// dialog.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "Playnite's notification API is third-party surface; a notification that will not show must not fail the scan that produced it.")]
        internal void ShowAutoScanNotification(List<VrModReviewMatchEntry> pendingMatches)
        {
            int noDataCount = _registry.GetEnabledSources().Count(s =>
            {
                var key = s.SourceId.ToLowerInvariant();
                var statuses = _settingsViewModel.Settings.SourceStatuses;
                return statuses != null
                    && statuses.TryGetValue(key, out var st)
                    && st.LastResult && st.LastEntryCount == 0;
            });

            // The auto-scan shows only pending rows, so how many accepted matches changed can't be
            // read off the rows — the service counted them during the scan.
            int changedCount = _tagService.ChangedAcceptedCount;
            string? message = BuildAutoScanMessage(pendingMatches.Count, noDataCount, changedCount);
            if (message == null)
            {
                // The scan found nothing the user can act on (no new matches, no tag changes):
                // no notification at all. Sources that returned no data stay visible in the
                // settings view (per-source last-run status) — that alone does not warrant a
                // notification.
                Logger.Info("VRMod: scan complete — nothing new, no notification.");
                return;
            }
            try
            {
                // Task 17: EVERY notification gets a click handler. Pending matches open the
                // review dialog for them; tag changes on already-accepted matches open the
                // full review dialog (so the user can re-accept the changes).
                Action clickAction;
                switch (ClassifyAutoScanClickTarget(pendingMatches.Count, changedCount))
                {
                    case VrModNotificationClickTarget.ReviewAllMatches:
                        // No pending rows to show — open the full review dialog (every matched
                        // game, like the settings "Review VR Mods" button) so the user can
                        // re-accept the changed matches. Background thread: the modal dialog
                        // (and the cold-start fallback's progress window) must not block the UI
                        // thread the click arrives on.
                        clickAction = () => Task.Run(() => RunReviewFromCache());
                        break;

                    default: // ReviewPendingMatches
                        clickAction = () => ShowReviewDialog(pendingMatches);
                        break;
                }
                var notification = new NotificationMessage("vrmod-scan-complete", message, NotificationType.Info, clickAction);
                API.Instance.Notifications.Add(notification);
            }
            catch (Exception ex)
            {
                Logger.Warn($"VRMod: failed to show scan notification: {ex.Message}");
            }
        }

        /// <summary>
        /// What an auto-scan notification's click handler opens (EVERY notification
        /// gets a click handler). A notification is only posted when the scan found
        /// match-related news — new pending matches, or tag changes on already-accepted
        /// matches — so the click always opens the review dialog, where the user can act on
        /// it. (A scan with neither posts no notification at all — see
        /// <see cref="BuildAutoScanMessage"/>.)
        /// </summary>
        internal enum VrModNotificationClickTarget
        {
            /// <summary>New pending matches: the click opens the review dialog for those matches.</summary>
            ReviewPendingMatches,

            /// <summary>No pending matches, but already-accepted matches have tag changes: the
            /// click opens the full review dialog (every matched game, like the settings
            /// "Review VR Mods" button) so the user can re-accept the changed matches.</summary>
            ReviewAllMatches
        }

        /// <summary>
        /// Chooses the click target for an auto-scan notification (see
        /// <see cref="VrModNotificationClickTarget"/>) from the two counts the scan produces:
        /// <paramref name="pendingCount"/> new pending matches and
        /// <paramref name="changedCount"/> already-accepted matches whose source tags changed
        /// (the service's per-scan count). Match-related news wins: pending matches open the
        /// review dialog for them, changed accepted matches open the full review dialog.
        /// Only called when a notification is posted at all (<c>pendingCount</c> &gt; 0 or
        /// <c>changedCount</c> &gt; 0 — a scan with neither posts no notification). Pure over
        /// the counts — the notification text is built separately by
        /// <see cref="BuildAutoScanMessage"/>.
        /// </summary>
        internal static VrModNotificationClickTarget ClassifyAutoScanClickTarget(int pendingCount, int changedCount)
        {
            if (pendingCount > 0)
                return VrModNotificationClickTarget.ReviewPendingMatches;
            return VrModNotificationClickTarget.ReviewAllMatches;
        }

        /// <summary>
        /// Builds the auto-scan notification message: reports the pending match count (and how
        /// to resolve it by clicking to review), plus notes on sources that returned no data
        /// and tag changes on already-accepted matches.
        /// Returns null when the scan found nothing to report (no new matches, no tag changes)
        /// — then NO notification is posted at all (sources that returned no data stay visible
        /// in the settings view; that alone does not warrant a notification).
        /// </summary>
        internal static string? BuildAutoScanMessage(int pendingCount, int noDataCount, int changedCount)
        {
            if (pendingCount == 0 && changedCount == 0)
                return null;

            if (pendingCount > 0)
            {
                string message = $"VRMod scan: {pendingCount} pending matches. Click to review.";
                if (noDataCount > 0)
                    message += $" {noDataCount} sources returned no data.";
                if (changedCount > 0)
                    message += $" {changedCount} accepted matches have tag changes.";
                return message;
            }

            // No new pending matches, but tag changes on already-accepted matches: report those
            // (and note sources that returned no data).
            string completeMessage = "VRMod scan complete:";
            if (noDataCount > 0)
                completeMessage += $" {noDataCount} sources returned no data.";
            completeMessage += $" {changedCount} accepted matches have tag changes.";
            return completeMessage;
        }

        /// <summary>
        /// Shows the review dialog for Pending matches.
        ///
        /// This method is called from the UI thread (marshaled via UIDispatcher)
        /// to display the VrModReviewDialog window with all Pending matches.
        /// The user can Accept, Reject, or defer each match.
        ///
        /// If there are no Pending matches (matches.Count == 0), this method returns
        /// immediately without showing any dialog.
        /// </summary>
        /// <param name="matches">List of Pending matches requiring user review.</param>
        private void ShowReviewDialog(List<VrModReviewMatchEntry> matches)
        {
            Logger.Debug($"VRMod: ShowReviewDialog: entering with {matches.Count} pending match(es), thread={Environment.CurrentManagedThreadId}");
            if (matches.Count == 0)
                return;

            // The full scan runs on a background thread, but WPF windows must be
            // created and shown on the UI thread. Marshal via the main dispatcher.
            var mainView = API.Instance.MainView;
            var dispatcher = mainView?.UIDispatcher;
            Logger.Debug($"VRMod: ShowReviewDialog: mainView={(mainView != null ? "present" : "NULL")}, dispatcher={(dispatcher != null ? "present" : "NULL")}, onUiThread={(dispatcher != null && dispatcher.CheckAccess())}");
            if (dispatcher == null)
                return;

            if (dispatcher.CheckAccess())
            {
                Logger.Debug("VRMod: ShowReviewDialog: on UI thread; invoking ShowReviewWindow directly");
                ShowReviewWindow(matches);
            }
            else
            {
                Logger.Debug("VRMod: ShowReviewDialog: not on UI thread; marshaling via dispatcher.Invoke (blocks until dialog closes)");
                dispatcher.Invoke(() => ShowReviewWindow(matches));
                Logger.Debug("VRMod: ShowReviewDialog: dispatcher.Invoke returned (dialog closed)");
            }
        }

        private void ShowReviewWindow(List<VrModReviewMatchEntry> matches)
        {
            try
            {
                Logger.Debug($"VRMod: ShowReviewWindow: start (thread={Environment.CurrentManagedThreadId}, matches={matches.Count})");
                var dialog = new VrModReviewDialog(API.Instance, this, matches);

                // WPF requires the Owner to be an already-shown Window. Playnite's
                // CreateWindow returns an UNshown WindowBase (setting it as Owner throws
                // InvalidOperationException), so use Playnite's current (visible) app
                // window as the owner instead. Fall back to screen-centering if none.
                var owner = API.Instance.Dialogs.GetCurrentAppWindow();
                if (owner != null)
                    dialog.Owner = owner;
                else
                    dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

                Logger.Debug($"VRMod: ShowReviewWindow: owner={(owner != null ? owner.GetType().Name : "null (centered on screen)")}; calling ShowDialog (modal, blocks until closed)");
                dialog.ShowDialog();
                Logger.Debug("VRMod: ShowReviewWindow: ShowDialog returned (dialog closed)");
                // Our dialog is a plain Window, so its close is invisible to Playnite's
                // child-ownership tracking (it re-evaluates only for its own WindowBase
                // windows). Re-evaluate now, after the dialog is fully closed and no longer
                // in the owner's OwnedWindows, so the owner's dim (Opacity 0.4, set while we
                // were open) is lifted - Playnite's own dialogs get this from their Closed
                // handler, which a plain Window never fires.
                VrModReviewDialog.NotifyPlayniteChildOwnershipChanged(owner);
                // The dialog may have changed decisions (save, reset, accept/reject). We are on
                // the UI thread, and the settings page shares its settings object with the tag
                // service, so a refresh keeps its counts current.
                _settingsViewModel.RefreshAfterDecisionsChanged();

                // Insurance against a stuck dim: the Save path now closes the dialog only after
                // its database work (and the BufferedUpdate flush's "notify the UI") has
                // completed, so for that path the close-undim above is already the last word.
                // The Reset-and-remove-tags → Cancel path still carries the same hazard: the
                // removal's flush notification can reach the UI thread only after the dialog
                // closed and the synchronous undim above ran. Re-running the re-evaluation at
                // Background priority makes it the last word there — it runs after those queued
                // refreshes have settled, so the owner ends at full opacity.
                if (owner != null)
                    owner.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() => VrModReviewDialog.NotifyPlayniteChildOwnershipChanged(owner)));
            }
            catch (Exception ex)
            {
                Logger.Error($"VRMod: ShowReviewWindow: FAILED: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Handles property change events from the settings ViewModel.
        ///
        /// When settings change:
        /// - CacheDurationMinutes: Logs the new value (cache uses it automatically on next fetch)
        /// - ScanIntervalMinutes: Restarts the scheduled scan timer with the new interval
        /// - EnableRototRobot/EnableFlat2VR/EnableUeVR: Clears cache to force re-fetch
        ///
        /// These events allow the plugin to adapt to configuration changes without
        /// requiring a full application restart.
        /// </summary>
        /// <param name="sender">The settings ViewModel that changed.</param>
        /// <param name="e">Property change event arguments.</param>
        private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VrModTagSettings.CacheDurationMinutes))
            {
                Logger.Info($"Cache duration changed to {_settingsViewModel.Settings.CacheDurationMinutes} minute(s).");
            }

            if (e.PropertyName == nameof(VrModTagSettings.ScanIntervalMinutes))
            {
                RestartTimer();
            }

            if (e.PropertyName is nameof(VrModTagSettings.EnableRototRobot) or nameof(VrModTagSettings.EnableFlat2VR) or nameof(VrModTagSettings.EnableUeVR) or nameof(VrModTagSettings.EnableReFramework) or nameof(VrModTagSettings.EnablePcvrCentral) or nameof(VrModTagSettings.EnableMrNlceHub) or nameof(VrModTagSettings.EnableMrSurvivorHub) or nameof(VrModTagSettings.EnableVorpX) or nameof(VrModTagSettings.EnableLukeRoss))
            {
                Logger.Info($"VR source toggle changed. Clearing cache...");
                _tagService.RefreshCache();
            }
        }

        /// <summary>
        /// Restarts the scheduled scan timer with the current scan interval from settings.
        ///
        /// Stops the existing timer (if any) and creates a new one with the updated interval.
        /// Used when ScanIntervalMinutes changes in settings.
        /// </summary>
        private void RestartTimer()
        {
            StopTimer();
            StartTimer();
        }

        /// <summary>
        /// Creates and starts the scheduled scan timer with the scan interval from settings.
        /// Used at plugin start and whenever the scan interval changes.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "A timer callback that throws would tear down the timer or crash Playnite's dispatcher.")]
        private void StartTimer()
        {
            double ms = _settingsViewModel.Settings.ScanIntervalMinutes * 60d * 1000;
            if (ms > 0)
            {
                _timer = new Timer(ms);
                _timer.Elapsed += (s, e) =>
                {
                    try
                    {
                        // Run off the UI thread: RunScanWithProgress blocks while the
                        // progress window is shown and marshals to the UI thread internally.
                        Task.Run(() => RunScheduledScan());
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Scheduled scan failed: {ex.Message}");
                    }
                };
                _timer.AutoReset = true;
                _timer.Start();
                Logger.Info($"Scheduled scan timer started: every {FormatScanInterval(_settingsViewModel.Settings.ScanIntervalMinutes)}.");
            }
        }

        /// <summary>
        /// Formats a scan interval in minutes for the run log:
        /// 0 = "never", less than an hour as minutes, less than a day as hours,
        /// otherwise as days.
        /// </summary>
        private static string FormatScanInterval(int minutes)
        {
            if (minutes <= 0)
                return "never";
            if (minutes < 60)
                return $"{minutes} min";
            if (minutes < 1440)
                return $"{minutes / 60} h";
            return $"{minutes / 1440} day(s)";
        }

        /// <summary>
        /// Stops and disposes the scheduled scan timer.
        ///
        /// Called when the application is stopping or when the scan interval needs to be changed.
        /// The timer reference is set to null to prevent future callback invocations.
        /// </summary>
        private void StopTimer()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
        }

        /// <summary>
        /// Disposes all resources used by the plugin.
        ///
        /// Called when Playnite unloads the plugin (e.g., during shutdown).
        /// Cleans up:
        /// - Unsubscribes from settings change events
        /// - Stops and disposes the scheduled scan timer
        /// - Disposes the tag service (clears caches)
        /// - Disposes all registered data sources
        ///
        /// Note: the clean-uninstall library cleanup happens in
        /// <see cref="OnApplicationStopped"/> instead — it needs the tag service's source
        /// cache, which this Dispose clears.
        /// </summary>
        public override void Dispose()
        {
            if (_isDisposed)
                return;

            _settingsViewModel.PropertyChanged -= OnSettingsChanged;
            StopTimer();
            _tagService.Dispose();
            foreach (var source in _registry.Sources)
            {
                if (source is IDisposable disposable)
                    disposable.Dispose();
            }
            _isDisposed = true;

            base.Dispose();

            // The plugin type can be subclassed by someone who adds a finalizer; suppressing
            // finalization here is what the Dispose() contract means for an override too.
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Name of Playnite's extension install/uninstall queue file (in the configuration
        /// directory). Playnite writes an entry here when an extension is uninstalled and
        /// deletes the extension's files on the next startup.
        /// </summary>
        private const string ExtensionQueueFileName = "extinstalls.json";

        /// <summary>
        /// True when Playnite queued THIS plugin for uninstall (its extension directory
        /// appears in the extinstalls.json queue as an uninstall item). Used by
        /// <see cref="Dispose"/> to gate the one-time library cleanup.
        /// </summary>
        /// <summary>
        /// What a path or a file owned by someone else can do to us: a path this stack cannot resolve,
        /// a file locked by the running Playnite instance or gone between the check and the read, a
        /// location the security policy will not let us read. Anything outside this list is our own
        /// fault and is not reported as "not queued".
        /// </summary>
        internal static bool IsPathOrFileFailure(Exception ex)
        {
            return ex is IOException
                || ex is UnauthorizedAccessException
                || ex is ArgumentException
                || ex is NotSupportedException
                || ex is System.Security.SecurityException;
        }

        private static bool IsUninstallQueued()
        {
            try
            {
                var queueFile = Path.Combine(API.Instance.Paths.ConfigurationPath, ExtensionQueueFileName);
                var extensionDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return IsUninstallQueued(queueFile, extensionDir);
            }
            catch (Exception e) when (IsPathOrFileFailure(e))
            {
                Logger.Warn("VRMod Tag Plugin: could not check the extension uninstall queue: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// File-based check used by <see cref="IsUninstallQueued"/>: true when the queue file
        /// exists and queues an uninstall for <paramref name="extensionDir"/>.
        /// </summary>
        internal static bool IsUninstallQueued(string queueFilePath, string? extensionDir)
        {
            if (string.IsNullOrEmpty(extensionDir))
                return false;
            if (!File.Exists(queueFilePath))
                return false;

            string text;
            try
            {
                using (var reader = new StreamReader(queueFilePath))
                    text = reader.ReadToEnd();
            }
            catch (Exception ex) when (IsPathOrFileFailure(ex))
            {
                // The queue file is Playnite's, in a directory the user owns: locked by the instance
                // that is writing it, or deleted between the existence check and this read. Both
                // mean "we do not know that an uninstall is queued", which is the safe answer.
                return false;
            }

            return TextQueuesUninstall(text, extensionDir!);
        }

        /// <summary>
        /// Pure check over the raw queue JSON: true when an uninstall item (InstallType 1)
        /// points at <paramref name="extensionDir"/>. The queue is a flat array written by
        /// Playnite: <c>[{"InstallType":1,"Path":"&lt;extension dir&gt;"}]</c>.
        /// </summary>
        internal static bool TextQueuesUninstall(string queueJson, string extensionDir)
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<ExtensionQueueItem>>(queueJson);
                return items != null && items.Any(i => i.InstallType == 1 && SamePath(i.Path, extensionDir));
            }
            catch (JsonException)
            {
                // Fallback when the JSON is not parseable (unexpected format): our directory
                // appears in the file at all. Playnite queues bare directories only for
                // uninstalls (install items are .pext/.pthm file paths), so a directory
                // match is decisive.
                return queueJson != null && queueJson.IndexOf(JsonEscapePath(extensionDir), StringComparison.Ordinal) >= 0;
            }
        }

        private static bool SamePath(string? a, string b)
        {
            if (string.IsNullOrEmpty(a))
                return false;
            try
            {
                a = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                b = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (IsPathOrFileFailure(ex))
            {
                // A queued path this stack cannot resolve: compare the raw strings instead, which is
                // the weaker check the caller still wants an answer from.
            }
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string JsonEscapePath(string path) =>
            path.Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>
        /// Deletes the plugin's own settings file and folder (ExtensionsData/&lt;plugin id&gt;/),
        /// so an uninstall leaves no settings behind. Failures are logged, not fatal: the
        /// library cleanup is the important part.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design", "CA1031:Do not catch general exception types",
            Justification = "Removing plugin settings at uninstall is best-effort; the log line is the report.")]
        private void DeletePluginSettingsFile()
        {
            try
            {
                var dataDir = Path.Combine(API.Instance.Paths.ExtensionsDataPath, Id.ToString());
                var configFile = Path.Combine(dataDir, "config.json");
                if (File.Exists(configFile))
                    File.Delete(configFile);
                Directory.Delete(dataDir, false);

                Logger.Info($"VRMod Tag Plugin: removed plugin settings ({dataDir}).");
            }
            catch (Exception e)
            {
                Logger.Warn("VRMod Tag Plugin: could not delete plugin settings folder: " + e.Message);
            }
        }

        /// <summary>
        /// Shape of one entry in Playnite's extinstalls.json queue (flat array).
        /// </summary>
        private class ExtensionQueueItem
        {
            public int InstallType { get; set; }
            public string? Path { get; set; }
        }
    }
}
