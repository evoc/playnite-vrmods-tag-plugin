using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Parser for a Steam <b>Curator</b> "recommendations" list.
    ///
    /// A Steam curator is a community account that "recommends" Steam apps; its list of
    /// recommendations is exactly the data this parser reads. Two curator lists are
    /// registered as sources (see <see cref="VrModTagPlugin"/>):
    ///   - "Steam Curator UEVR Compatibility" (curator 44946997) — games playtested with UEVR.
    ///   - "Steam Curator VorpX Games"        (curator 38688993) — games that work with VorpX.
    ///
    /// This is one parser class, instantiated once per curator (each instance has its own
    /// curator id, display name, and category), because both lists are served by the SAME
    /// undocumented Steam AJAX endpoint.
    ///
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  WHAT THIS PARSER EXPECTS TO PARSE (so a site/layout change is easy to fix)
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  Each page of the list is fetched from the same AJAX endpoint the curator page itself
    ///  calls (not the rendered HTML page):
    ///
    ///      GET https://store.steampowered.com/curator/{CURATOR_ID}/ajaxgetcuratorrecommendations/render/?query=&start={N}&count=50
    ///
    ///  where {CURATOR_ID} is the numeric curator id and {N} is the 0-based start offset
    ///  (pages of 50). The response is JSON:
    ///
    ///      { "success": 1, "total_count": 21, "results_html": "&lt;div class=\"recommendation\" ...&gt;..." }
    ///
    ///  - "success"     : integer; 1 means OK. Any other value means the curator is gone/changed
    ///                    → the fetch is treated as a FAILURE (not "no data").
    ///  - "total_count" : integer; the total number of recommendations (drives pagination).
    ///  - "results_html": an HTML fragment, one block per recommendation. Each block starts
    ///                    with the marker  <div class="recommendation"  and carries, inside it:
    ///
    ///        data-ds-appid="2670250"                       ← the Steam App ID
    ///        href="https://store.steampowered.com/app/2670250/Dreamslayer/?snr=..."
    ///                                                       ← the app slug (last path segment
    ///                                                          before the next "/")
    ///        <div class="recommendation_desc">"curator's playtest note ..."</div>
    ///                                                       ← the curator's written note
    ///
    ///  Pagination: fetch start=0,50,100,... until start >= total_count (a hard page cap bounds
    ///  a misbehaving server).
    ///
    ///  PER-ENTRY OUTPUT (one entry per recommended app):
    ///    - SteamAppId   = data-ds-appid            → PRIMARY matching (exact App ID).
    ///    - GameName     = slug (underscores → spaces) → display / fallback name match.
    ///    - Category     = the category this instance was created with (UEVR / VorpX).
    ///    - SourceUrl    = https://store.steampowered.com/app/{APPID}/   (the game's store page).
    ///    - AdditionalInfo = the curator's note (trimmed, unquoted).
    ///
    ///  ── TEMPORARY FIX (2026-10-09) ────────────────────────────────────────────────────────
    ///  This endpoint is the undocumented AJAX API the Steam curator page uses; it has no
    ///  version guarantee and may change or be rate-limited. If the JSON shape or the
    ///  "recommendation" block markup changes, update ParseRecommendations below (the doc
    ///  block above describes exactly what is expected).
    /// ────────────────────────────────────────────────────────────────────────────────────────
    /// </summary>
    public class SteamCuratorParser : BaseVrModParser
    {
        /// <summary>
        /// Hard cap on the number of AJAX pages fetched per scan (50/page). The largest curator
        /// list we track is ~100 apps (2 pages); the cap bounds request count and memory so a
        /// server that keeps reporting a huge total_count cannot loop the fetch forever.
        /// </summary>
        internal const int MaxCuratorPages = 100;

        /// <summary>Page size requested from the endpoint (matches the endpoint's own default).</summary>
        private const int PageSize = 50;

        /// <summary>
        /// The exact marker that begins each recommendation block inside "results_html". The
        /// list is split on this string; everything up to the next marker is one recommendation.
        /// </summary>
        private const string RecommendationMarker = "<div class=\"recommendation\"";

        private readonly int _curatorId;
        private readonly string _category;
        private string? _lastContentHash;

        /// <summary>
        /// Creates a Steam curator source.
        /// </summary>
        /// <param name="sourceId">Unique source id (also the settings key; e.g. "SteamCuratorUEVR").</param>
        /// <param name="displayName">Name shown in the settings UI.</param>
        /// <param name="websiteUrl">The public curator page (settings UI link).</param>
        /// <param name="curatorId">The numeric Steam curator id used in the AJAX endpoint.</param>
        /// <param name="category">The category applied to every entry (drives the "VRMod {category}" tag).</param>
        public SteamCuratorParser(string sourceId, string displayName, string websiteUrl, int curatorId, string category)
            : base(sourceId, displayName, websiteUrl)
        {
            _curatorId = curatorId;
            _category = category;
        }

        /// <inheritdoc />
        public override string? ChangeIdentifier => _lastContentHash;

        /// <inheritdoc />
        public override string? GetCanonicalName(VrModEntry entry)
        {
            return StripVrSuffixes(entry.GameName);
        }

        /// <inheritdoc />
        public override async Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
        {
            var items = new List<CuratorItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            int start = 0;
            int totalPages = 0;
            int totalCount = -1;

            while (totalPages < MaxCuratorPages)
            {
                // A cancelled scan aborts instead of returning the pages it got as "success": the
                // caller would count this source as fetched, and a scan that appears to have
                // succeeded is what lets stale-tag removal run on half the data.
                if (ScanToken.IsCancellationRequested)
                    throw new OperationCanceledException("VRMod scan was cancelled while paginating the Steam Curator source.");

                var json = await FetchCuratorPageAsync(start);

                // ParseCuratorPage validates "success" (throwing when the curator is gone/changed —
                // a BROKEN source, not "no data"), appends the page's recommendations, and returns
                // the list's total_count (drives pagination). It MUST be called for EVERY page so
                // each page's items are parsed; the first page's total_count drives the loop.
                var pageTotal = ParseCuratorPage(json, seen, items);
                if (totalCount < 0)
                    totalCount = pageTotal;

                totalPages++;
                start += PageSize;
                if (start >= totalCount)
                    break;
            }

            // Change identifier: a content hash that changes when the curator adds/removes/
            // rewrites a recommendation (appid + slug + note per app), so an unchanged list
            // skips re-matching on the next scan.
            var hashLines = new List<string>(items.Count);
            foreach (var i in items)
                hashLines.Add(i.AppId + "|" + i.Slug + "|" + i.Note);
            _lastContentHash = ComputeHash(string.Join("\n", hashLines));

            var entries = new List<VrModEntry>(items.Count);
            foreach (var i in items)
            {
                var name = SlugToName(i.Slug);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                entries.Add(new VrModEntry
                {
                    GameName = name,
                    Categories = new List<string> { _category },
                    SteamAppId = i.AppId,
                    SourceUrls = new List<string> { $"https://store.steampowered.com/app/{i.AppId}/" },
                    AdditionalInfo = string.IsNullOrWhiteSpace(i.Note) ? null : i.Note,
                });
            }

            LogRun($"curator {_curatorId}: {items.Count} recommendation(s) over {totalPages} page(s); emitted {entries.Count}");
            return entries;
        }

        /// <summary>
        /// Fetches one page of the curator AJAX endpoint at the given 0-based start offset.
        /// Virtual so unit tests can override it to drive the pagination loop against recorded
        /// pages without the network.
        /// </summary>
        protected virtual Task<string> FetchCuratorPageAsync(int start)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://store.steampowered.com/curator/{0}/ajaxgetcuratorrecommendations/render/?query=&start={1}&count={2}",
                _curatorId, start, PageSize);
            return FetchStringAsync(url);
        }

        /// <summary>
        /// Parses one page of the curator AJAX JSON: validates "success", appends the page's
        /// recommendations to <paramref name="items"/>, and returns the page's "total_count"
        /// (the list's total recommendation count; -1 when absent). Internal so unit tests can
        /// drive the JSON→items path against a recorded page without the network.
        /// </summary>
        internal int ParseCuratorPage(string json, HashSet<string> seen, List<CuratorItem> items)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("success", out var success) || success.GetInt32() != 1)
            {
                LogRun($"curator {_curatorId} returned success != 1 (curator unavailable/changed): {Snip(root.GetRawText())}");
                throw new InvalidOperationException($"Steam curator {_curatorId} is unavailable or changed (endpoint success != 1).");
            }

            var totalCount = root.TryGetProperty("total_count", out var tc) ? tc.GetInt32() : -1;
            var html = root.TryGetProperty("results_html", out var rh) ? (rh.GetString() ?? "") : "";
            ParseRecommendations(html, seen, items);
            return totalCount;
        }

        /// <summary>
        /// Splits one page of "results_html" into per-recommendation items (appid, slug, note).
        /// Internal (not public) so unit tests can feed a recorded fragment directly, while the
        /// type it fills stays off the assembly's public surface.
        /// </summary>
        /// <param name="html">The "results_html" fragment for one page.</param>
        /// <param name="seen">App IDs already collected (dedup across pages).</param>
        /// <param name="items">The list to append new items to.</param>
        internal static void ParseRecommendations(string html, HashSet<string> seen, List<CuratorItem> items)
        {
            if (string.IsNullOrEmpty(html))
                return;

            // Each block starts with the marker; the first split element is the (empty) prefix
            // before the first marker, so skip it.
            var blocks = html.Split(new[] { RecommendationMarker }, StringSplitOptions.None);
            for (var b = 1; b < blocks.Length; b++)
            {
                var block = blocks[b];

                var appId = FirstCapture(block, "data-ds-appid=\"(\\d+)\"");
                if (appId.Length == 0 || !seen.Add(appId))
                    continue;

                var slug = FirstCapture(block, @"store\.steampowered\.com/app/\d+/([^/?]+)");
                slug = UnescapeDataStringSafe(slug);
                var note = ExtractNote(block);

                items.Add(new CuratorItem { AppId = appId, Slug = slug, Note = note });
            }
        }

        /// <summary>
        /// Extracts the curator's written note from a recommendation block. The note sits in
        /// <c>&lt;div class="recommendation_desc"&gt;</c> and is wrapped in literal double quotes
        /// (Steam's own quoting); the quotes and surrounding whitespace are stripped.
        /// </summary>
        private static string ExtractNote(string block)
        {
            const string marker = "<div class=\"recommendation_desc\">";
            var idx = block.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
                return "";

            var rest = block.Substring(idx + marker.Length);
            var end = rest.IndexOf("</div>", StringComparison.Ordinal);
            if (end < 0)
                end = rest.Length;

            var note = UnescapeHtml(rest.Substring(0, end)).Trim();
            if (note.Length > 1 && note[0] == '"' && note[note.Length - 1] == '"')
                note = note.Substring(1, note.Length - 2);
            return note.Trim();
        }

        /// <summary>
        /// Turns an app slug into a readable game name (underscores → spaces). The curator list
        /// does not give a display title, so the slug is our best name; the App ID is the primary
        /// match, so this is display/fallback quality only.
        /// </summary>
        internal static string SlugToName(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
                return "";
            var sb = new StringBuilder(slug);
            for (var i = 0; i < sb.Length; i++)
                if (sb[i] == '_')
                    sb[i] = ' ';
            return ReplaceScanned(sb.ToString(), @"\s{2,}", " ").Trim();
        }

        /// <summary>
        /// The three facts per recommendation (Steam App ID, slug, curator note). Internal:
        /// this parser's own steps and the test assembly (InternalsVisibleTo) are its only readers.
        /// </summary>
        internal sealed class CuratorItem
        {
            public string AppId { get; set; } = "";
            public string Slug { get; set; } = "";
            public string Note { get; set; } = "";
        }

        private static string FirstCapture(string text, string pattern)
        {
            var m = MatchScanned(text, pattern);
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string UnescapeDataStringSafe(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            try
            {
                return Uri.UnescapeDataString(s);
            }
            catch (UriFormatException)
            {
                return s; // malformed % escape — use the string exactly as scraped
            }
        }

        private static string UnescapeHtml(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            return s.Replace("&quot;", "\"")
                     .Replace("&#39;", "'")
                     .Replace("&amp;", "&")
                     .Replace("&lt;", "<")
                     .Replace("&gt;", ">")
                     .Replace("&nbsp;", " ");
        }
    }
}
