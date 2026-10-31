using Playnite.SDK;
using System;
using System.Collections.Generic; // Playnite's SDK declares ObservableObject in this namespace
using System.Globalization;
using System.Linq;

namespace Playnite.VRMods
{
    /// <summary>
    /// A row of the settings Sources grid. Lives in its own file rather than nested inside
    /// <see cref="VrModSettingsViewModel"/>: a type nested in a public class is part of the
    /// assembly's public surface, and this row model is used by this plugin's own settings UI
    /// and tests only. Un-nesting it keeps the public surface honest (analyzer CA1034) without
    /// changing what the XAML binds to.
    /// </summary>
    /// <summary>
    /// ViewModel for source status entries displayed in the settings UI.
    ///
    /// Wraps an IVrModSource and reads settings to display:
    /// - DisplayName: The source's display name
    /// - SourceId: The source's ID (for lookup in SourceStatuses)
    /// - StatusText: The full status ("Last scan: {time}" / "Error: {message}" /
    ///   "No data: ..." / "Never scanned") — the settings grid uses it as the
    ///   cell tooltip (error messages are longer than the cell can show)
    /// - StatusShort: The short cell text ("Never scanned" / "No data" / "Error"
    ///   / the bare last-scan time) shown next to the status dot in the grid
    /// - HasError: True if the last scan failed
    /// - ListedCount: How many games the source lists (last scan entry count)
    /// - MatchedCount: How many of those matched local games (accepted decisions)
    /// - CoverageText: A compact "lists N · matched M" summary for the row
    /// </summary>
    public class VrModSourceRowViewModel : ObservableObject
    {
        private readonly IVrModSource _source;
        private readonly VrModTagSettings _settings;

        /// <summary>
        /// Raised when the user disables this source in the settings UI (IsEnabled goes true→false).
        /// Wired by VrModSettingsViewModel to prompt about cleaning up the source's tags.
        /// </summary>
        public System.Action<string>? OnSourceDisabled { get; set; }

        /// <summary>
        /// Raised when the user toggles this source's "Links" checkbox in the settings UI.
        /// Wired by VrModSettingsViewModel to reconcile the source's links across the library.
        /// </summary>
        public System.Action<string>? OnLinksToggled { get; set; }

        /// <summary>
        /// Constructs a VrModSourceRowViewModel for a source.
        /// </summary>
        /// <param name="source">The source to display.</param>
        /// <param name="settings">The settings for enable state, website, and scan status.</param>
        public VrModSourceRowViewModel(IVrModSource source, VrModTagSettings settings)
        {
            _source = source;
            _settings = settings;
        }

        /// <summary>Display name of the source (e.g., "RototRobot VRMods-List").</summary>
        public string DisplayName => _source.DisplayName;

        /// <summary>Source ID (e.g., "RototRobot", "Flat2VR").</summary>
        public string SourceId => _source.SourceId;

        /// <summary>Public website for the source (shown as a clickable link).</summary>
        public string WebsiteUrl => _source.WebsiteUrl;

        /// <summary>
        /// Whether this source is enabled by the user.
        /// Reads/writes the corresponding Settings.EnableXxx property based on the
        /// source ID, so the checkbox in the merged source row drives the saved
        /// setting directly.
        /// </summary>
        public bool IsEnabled
        {
            get
            {
                return _source.SourceId.ToLowerInvariant() switch
                {
                    "rototrobot" => _settings.EnableRototRobot,
                    "flat2vr" => _settings.EnableFlat2VR,
                    "uevr" => _settings.EnableUeVR,
                    "reframework" => _settings.EnableReFramework,
                    "pcvrcentral" => _settings.EnablePcvrCentral,
                    "mrnlcehub" => _settings.EnableMrNlceHub,
                    "mrsurvivorhub" => _settings.EnableMrSurvivorHub,
                    "vorpx" => _settings.EnableVorpX,
                    "lukeross" => _settings.EnableLukeRoss,
                    "compoundvr" => _settings.EnableCompoundVR,
                    "steamcuratoruevr" => _settings.EnableSteamCuratorUEVR,
                    "steamcuratorvorpx" => _settings.EnableSteamCuratorVorpX,
                    _ => _source.IsEnabled
                };
            }
            set
            {
                var wasEnabled = IsEnabled;
                switch (_source.SourceId.ToLowerInvariant())
                {
                    case "rototrobot":
                        _settings.EnableRototRobot = value;
                        break;
                    case "flat2vr":
                        _settings.EnableFlat2VR = value;
                        break;
                    case "uevr":
                        _settings.EnableUeVR = value;
                        break;
                    case "reframework":
                        _settings.EnableReFramework = value;
                        break;
                    case "pcvrcentral":
                        _settings.EnablePcvrCentral = value;
                        break;
                    case "mrnlcehub":
                        _settings.EnableMrNlceHub = value;
                        break;
                    case "mrsurvivorhub":
                        _settings.EnableMrSurvivorHub = value;
                        break;
                    case "vorpx":
                        _settings.EnableVorpX = value;
                        break;
                    case "lukeross":
                        _settings.EnableLukeRoss = value;
                        break;
                    case "compoundvr":
                        _settings.EnableCompoundVR = value;
                        break;
                    case "steamcuratoruevr":
                        _settings.EnableSteamCuratorUEVR = value;
                        break;
                    case "steamcuratorvorpx":
                        _settings.EnableSteamCuratorVorpX = value;
                        break;
                    default:
                        _source.IsEnabled = value;
                        break;
                }
                OnPropertyChanged(nameof(IsEnabled));
                if (wasEnabled && !value)
                    OnSourceDisabled?.Invoke(_source.SourceId);
            }
        }

        /// <summary>
        /// Whether game Links should include a link for this source (default: true).
        /// Unchecking adds the source ID to Settings.DisabledLinkSources; checking
        /// removes it again, so the saved setting is driven directly by the checkbox.
        /// </summary>
        public bool LinksEnabled
        {
            get => _settings.IsSourceLinksEnabled(_source.SourceId);
            set
            {
                var list = _settings.DisabledLinkSources;
                if (value)
                {
                    list.RemoveAll(s => string.Equals(s, _source.SourceId, StringComparison.OrdinalIgnoreCase));
                }
                else if (!list.Any(s => string.Equals(s, _source.SourceId, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(_source.SourceId);
                }
                OnPropertyChanged(nameof(LinksEnabled));
                OnLinksToggled?.Invoke(_source.SourceId);
            }
        }

        /// <summary>
        /// The source's last-run record (last scan time, result, entry count, error message),
        /// or null when the source has never been scanned since this Playnite run (scan
        /// statuses are in-memory, [DontSerialize]). Read live from Settings.SourceStatuses.
        ///
        /// Backs the per-source "last run" log dialog (VrModSourceLogDialog), opened from
        /// the settings Sources grid (a per-row button and a right-click context menu).
        /// </summary>
        public SourceStatus? LastRun
        {
            get
            {
                var key = _source.SourceId.ToLowerInvariant();
                return _settings.SourceStatuses.TryGetValue(key, out var status) ? status : null;
            }
        }

        /// <summary>
        /// Status text for the source (e.g., "Last scan: 1/1/2024 12:00 PM" or "Error: Timeout").
        ///
        /// The full status. The settings grid shows it as the Last-scan cell tooltip
        /// (error messages can be arbitrarily long and do not fit a grid cell), and
        /// feeds the ErrorToBrushConverter that colors the dot + short text.
        ///
        /// Looks up the source's scan status in Settings.SourceStatuses.
        /// Returns "Never scanned" if the source has never been scanned.
        /// Returns "No data: ... returned 0 entries" if the scan succeeded but was empty.
        /// Returns "Last scan: {time}" if the scan succeeded with entries.
        /// Returns "Error: {message}" if the scan failed.
        /// </summary>
        public string StatusText
        {
            get
            {
                var key = _source.SourceId.ToLowerInvariant();
                if (!_settings.SourceStatuses.TryGetValue(key, out var status))
                    return "Never scanned";
                if (!status.LastResult)
                    return $"Error: {status.LastError}";
                var lastScan = status.LastScanTime.ToLocalTime();
                if (status.LastEntryCount == 0)
                    return $"No data: last scan {lastScan:G} returned 0 entries";
                return $"Last scan: {lastScan:G}";
            }
        }

        /// <summary>
        /// Short status text for the settings grid's Last-scan cell (the column header
        /// carries the "Last scan" label the full text used to embed inline).
        ///
        /// Returns "Never scanned" if the source has never been scanned,
        /// "No data" if the scan succeeded but was empty,
        /// "Error" if the scan failed (the message is in the cell tooltip),
        /// or the bare last-scan time (local) otherwise.
        /// </summary>
        public string StatusShort
        {
            get
            {
                var key = _source.SourceId.ToLowerInvariant();
                if (!_settings.SourceStatuses.TryGetValue(key, out var status))
                    return "Never scanned";
                if (!status.LastResult)
                    return "Error";
                if (status.LastEntryCount == 0)
                    return "No data";
                return status.LastScanTime.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
            }
        }

        /// <summary>
        /// True if the last scan for this source failed.
        ///
        /// Used by the settings UI to highlight sources with errors.
        /// </summary>
        public bool HasError
        {
            get
            {
                var key = _source.SourceId.ToLowerInvariant();
                return _settings.SourceStatuses.TryGetValue(key, out var status) && !status.LastResult;
            }
        }

        /// <summary>
        /// True if the last scan succeeded but returned no entries (anomalous).
        ///
        /// Used by the settings UI to flag sources that produced no data,
        /// distinct from HasError (a failed fetch).
        /// </summary>
        public bool HasZeroEntries
        {
            get
            {
                var key = _source.SourceId.ToLowerInvariant();
                return _settings.SourceStatuses.TryGetValue(key, out var status)
                    && status.LastResult && status.LastEntryCount == 0;
            }
        }

        /// <summary>
        /// How many games this source lists (the last scan's entry count).
        ///
        /// Read live from Settings.SourceStatuses; 0 if the source has never been
        /// scanned or the last scan returned no entries.
        /// </summary>
        public int ListedCount
        {
            get
            {
                var key = _source.SourceId.ToLowerInvariant();
                return _settings.SourceStatuses.TryGetValue(key, out var status)
                    ? status.LastEntryCount
                    : 0;
            }
        }

        /// <summary>
        /// How many games this source matched to local games (accepted decisions).
        ///
        /// Counts the Accepted decisions whose key belongs to this source. The
        /// decision key is "sourceId||sourceGame||{gameId:D}"; the
        /// source id is the first segment, compared case-insensitively.
        /// </summary>
        public int MatchedCount
        {
            get
            {
                var id = _source.SourceId.ToLowerInvariant();
                var count = 0;
                foreach (var kvp in _settings.SnapshotMatchDecisions())
                {
                    if (Enum.TryParse(kvp.Value, out MatchDecision decision) && decision != MatchDecision.Accepted)
                        continue;
                    var parts = kvp.Key.Split(DecisionKeySeparators, StringSplitOptions.None);
                    if (parts.Length > 0 && parts[0].Equals(id, StringComparison.OrdinalIgnoreCase))
                        count++;
                }
                return count;
            }
        }

        /// <summary>
        /// A compact coverage summary for the source row, e.g. "lists 223 · matched 5".
        ///
        /// Empty string when there is nothing to report (never scanned, so 0 listed
        /// and 0 matched), so the column stays blank until the first scan.
        /// </summary>
        public string CoverageText
        {
            get
            {
                var listed = ListedCount;
                var matched = MatchedCount;
                if (listed == 0 && matched == 0)
                    return string.Empty;
                return $"lists {listed} · matched {matched}";
            }
        }

        private static readonly string[] DecisionKeySeparators = { "||" };
    }
}
