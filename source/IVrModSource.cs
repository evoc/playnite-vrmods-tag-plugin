using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Interface that every VR mod data source must implement.
    /// A "source" is an external website or API that lists games with VR mods.
    /// Examples: RototRobot VRMods-List, Flat2VR Studios, UeVR Profiles.
    ///
    /// Each source is fetched periodically, parsed into VrModEntry objects,
    /// and those entries are matched against the user's Playnite library.
    /// </summary>
    public interface IVrModSource
    {
        /// <summary>
        /// Unique identifier for this source (e.g., "RototRobot", "Flat2VR").
        /// Used as a cache key and in decision persistence keys.
        /// </summary>
        string SourceId { get; }

        /// <summary>
        /// Human-readable display name shown in the settings UI.
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// The public, human-readable website for this source (the page a user would
        /// browse to see the source's listings). Shown as a clickable link in the
        /// settings UI. Distinct from the raw fetch URL used internally.
        /// </summary>
        string WebsiteUrl { get; }

        /// <summary>
        /// Whether this source is currently enabled by the user.
        /// When false, the source is skipped during scans.
        /// Set by the settings ViewModel syncing logic.
        /// </summary>
        bool IsEnabled { get; set; }

        /// <summary>
        /// An identifier that changes when the source content changes.
        /// Used by the service to skip re-fetching unchanged sources.
        /// Typically a SHA-256 hash of the fetched HTML content.
        /// Returns null if no change identifier is available.
        /// </summary>
        string? ChangeIdentifier { get; }

        /// <summary>
        /// The debug log for this source's current run: a fresh list the tag service assigns
        /// before each scan run, which the source appends a line to for every web request it
        /// makes (URL, HTTP status code, body size, elapsed time) and every per-game
        /// extraction problem it hits (a row that failed to parse, a missing field, ...).
        /// Null when no run is in progress (e.g. a parser unit test fetching outside a scan).
        ///
        /// After the run the service copies the list into the source's
        /// <c>SourceStatus.LastRunLog</c> (the settings "Last run" view shows it).
        /// </summary>
        List<string>? RunLog { get; set; }

        /// <summary>
        /// Cancellation token of the scan run that is currently fetching this source, assigned by
        /// the tag service before the fan-out and cleared back to <see cref="CancellationToken.None"/>
        /// afterwards. A source's fetch loops (request retries, pagination) must stop when it is
        /// cancelled, so a user-cancelled scan — or one that hit the service's wall-clock budget —
        /// does not keep hammering the source's site.
        /// </summary>
        CancellationToken ScanToken { get; set; }

        /// <summary>
        /// Fetches VR mod entries from the external source.
        /// Performs an HTTP request, parses the response, and returns a list of VrModEntry objects.
        /// Each entry represents a game that has a VR mod available from this source.
        ///
        /// This method throws when the request fails: a network error (timeout, DNS, refused/
        /// reset connection — the message names the cause), or a non-success HTTP status
        /// (4xx/5xx — the message is "HTTP {code} ({reason})"). A success status whose page
        /// parses to zero entries is NOT an error: it returns an empty list ("no data").
        /// Callers (VrModTagService) catch exceptions and log errors.
        /// </summary>
        /// <returns>A collection of VR mod entries, each with a game name, category, and optional Steam ID.</returns>
        Task<IEnumerable<VrModEntry>> FetchEntriesAsync();

        /// <summary>
        /// Extracts a "canonical" (clean) game name from an entry for matching purposes.
        /// Strips author credits, VR suffixes, and other non-essential text.
        /// For example: "DOOM VR mod by Author" → "DOOM".
        ///
        /// This is used by parsers for internal processing, but the primary matching
        /// logic in VrModTagService uses its own normalization pipeline.
        /// </summary>
        /// <param name="entry">The VR mod entry to extract a name from.</param>
        /// <returns>The cleaned game name, or null if extraction fails.</returns>
        string? GetCanonicalName(VrModEntry entry);
    }
}
