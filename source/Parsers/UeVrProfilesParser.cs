using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Parser for UeVR Profiles website (uevr-profiles.com).
    ///
    /// Fetches VR mod data from the UeVR Profiles Firebase Firestore database.
    /// The website is a React SPA that loads game data from Firestore, so this parser
    /// queries the Firestore REST API directly.
    ///
    /// Category: "UEVR Profiles" (results in "VRMod UEVR Profiles" tag)
    ///
    /// Firestore collection: "games"
    /// Each document has fields:
    ///   - gameName (string): The game's name
    ///   - appID (string): Steam App ID
    ///   - profiles (array): List of UEVR profiles with author, description, etc.
    ///   - tags (array): Status tags like "Works Perfectly"
    ///
    /// Matching: This parser extracts both game names and Steam App IDs,
    /// enabling Steam ID-based matching (preferred over name matching).
    ///
    /// Note: VR suffixes are stripped from game names to improve matching.
    /// For example, "DOOM VR" becomes "DOOM" for matching purposes.

    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  WHAT THIS PARSER EXPECTS TO PARSE (so a site/layout change is easy to fix)
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  A single request per page returns a Firestore page of the "games" collection (the
    ///  API key + project ID are scraped from the site's JS bundle; see the TEMP FIX below):

    ///      GET https://firestore.googleapis.com/v1/projects/&lt;projectId&gt;/databases/(default)/
    ///          documents/games?alt=json&amp;key=&lt;apiKey&gt;&amp;pageToken=&lt;nextPage?&gt;

    ///      {
    ///        "documents": [
    ///          {
    ///            "fields": {
    ///              "gameName": { "stringValue": "DOOM" },            ← game name (VR suffix stripped)
    ///              "appID":    { "stringValue": "306840" },         ← Steam App ID (exact match)
    ///              "profiles": { "arrayValue": { "values": [ ... ] } },
    ///              "tags":     { "arrayValue": { "values": [ ... ] } }
    ///            }
    ///          }
    ///        ],
    ///        "nextPageToken": "..."                                  ← paginate until absent
    ///      }

    ///  Fields read per document: fields.gameName.stringValue + fields.appID.stringValue
    ///  (Firestore wraps each scalar in a typed object; the parser unwraps stringValue).
    ///  A document without a name is skipped; a missing appID is fine (falls back to name matching).

    ///
    /// ──────────────────────────────────────────────────────────────────
    ///  TEMPORARY FIX (2026-08-30) — replace when a stable API exists
    /// ──────────────────────────────────────────────────────────────────
    ///  This parser scrapes the Firebase web API key and project ID from the
    ///  site's JS bundle on every fetch. The key is a public Firebase web
    ///  API key (NOT an auth token) embedded in the React bundle for client
    ///  access; it is not a secret. Hardcoded fallback values are used if
    ///  the site is unreachable.
    ///
    ///  The Firestore REST endpoint is an undocumented public API that may
    ///  change or be restricted by Google at any time. If the site moves
    ///  to an authenticated backend, this parser will need to be rewritten.
    ///
    ///  TODO: Replace with a stable, versioned API or an official data
    ///  export from the UeVR team when one becomes available.
    /// ──────────────────────────────────────────────────────────────────
    /// </summary>
    public class UeVrProfilesParser : BaseVrModParser
    {
        /// <summary>
        /// Hard cap on the number of Firestore pages fetched per scan. The collection is a few
        /// hundred documents (a page or two); the cap bounds request count and memory so a
        /// server that keeps issuing next-page tokens cannot loop the fetch forever.
        /// </summary>
        internal const int MaxFirestorePages = 50;

        // ── Temporary hardcoded fallbacks (2026-08-30) ──────────────────
        // These values are scraped dynamically from the site's JS bundle
        // on each fetch. They exist so the parser still works if the site
        // is briefly unreachable. Remove when a stable API exists.
        private const string FallbackFirebaseApiKey = "AIzaSyAQYgGp-skpNcm6Mzh5RLADYWnxsr22hRU";
        private const string FallbackProjectId = "uevrprofiles";

        /// <summary>
        /// URL of the UeVR Profiles SPA shell.
        /// Used to locate the main JS bundle for Firebase config extraction.
        /// </summary>
        private const string SpaUrl = "https://uevr-profiles.com/";

        /// <summary>
        /// SHA-256 hash of the last fetched content.
        /// Used as the ChangeIdentifier to detect when the source has been updated.
        /// </summary>
        private string? _lastContentHash;

        /// <summary>
        /// Cached Firebase API key extracted from the site's JS bundle.
        /// Falls back to <see cref="FallbackFirebaseApiKey"/> if extraction fails.
        /// </summary>
        private string? _firebaseApiKey;

        /// <summary>
        /// Cached Firebase project ID extracted from the site's JS bundle.
        /// Falls back to <see cref="FallbackProjectId"/> if extraction fails.
        /// </summary>
        private string? _projectId;

        /// <summary>
        /// Creates a new UeVrProfilesParser instance.
        /// Initializes the base class with source ID "UeVR" and display name "UeVR Profiles".
        /// </summary>
        public UeVrProfilesParser() : base("UeVR", "UeVR Profiles", "https://uevr-profiles.com/")
        {
        }

        /// <inheritdoc />
        public override string? ChangeIdentifier => _lastContentHash;

        /// <inheritdoc />
        public override async Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
        {
            var entries = new List<VrModEntry>();

            // Extract Firebase config (API key + project ID) from the site's JS bundle.
            // Falls back to hardcoded values if the site is unreachable.
            // TEMPORARY: replace with a stable API when available.
            await TryExtractFirebaseConfigAsync();
            LogRun(_firebaseApiKey == FallbackFirebaseApiKey && _projectId == FallbackProjectId
                ? "Using FALLBACK Firebase config (the key/project scrape from the site failed)"
                : "Using Firebase config scraped from the site's JS bundle");

            // Build the Firestore base URL from the (possibly dynamic) project ID.
            // Last line of defence: the project id goes into the request PATH, where nothing escapes
            // it (the key and page token are escaped, a path segment is not). Only a validated id is
            // interpolated, so the site's bundle can never move this request to another project, another
            // database, another collection, or smuggle extra query segments.
            if (!IsUsableFirebaseProjectId(_projectId))
            {
                LogRun($"Firebase project id rejected before building the request (shape: {Snip(_projectId)}); using the pinned fallback project");
                _projectId = FallbackProjectId;
            }
            var baseUrl = $"https://firestore.googleapis.com/v1/projects/{_projectId}/databases/(default)/documents";

            // Fetch all pages of the games collection. The page cap bounds both request count
            // and memory: a server that keeps issuing next-page tokens must not loop the fetch
            // forever.
            string? pageToken = null;
            var allDocuments = new List<JsonElement>();

            for (int page = 0; page < MaxFirestorePages; page++)
            {
                var url = $"{baseUrl}/games?alt=json&key={Uri.EscapeDataString(_firebaseApiKey ?? FallbackFirebaseApiKey)}";
                if (pageToken != null)
                {
                    url += $"&pageToken={Uri.EscapeDataString(pageToken)}";
                }

                var json = await FetchStringAsync(url);
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;

                    if (root.TryGetProperty("documents", out var documents))
                    {
                        foreach (var docElement in documents.EnumerateArray())
                        {
                            allDocuments.Add(docElement.Clone());
                        }
                    }

                    pageToken = GetNextPageToken(root);
                }

                if (pageToken == null)
                    break;
            }

            var hashParts = new List<string>();
            foreach (var d in allDocuments)
            {
                var name = "";
                var id = "";
                if (d.TryGetProperty("fields", out var fields))
                {
                    if (fields.TryGetProperty("gameName", out var n))
                        name = UnwrapFirestoreString(n) ?? "";
                    if (fields.TryGetProperty("appID", out var a))
                        id = UnwrapFirestoreString(a) ?? "";
                }
                hashParts.Add($"{name}|{id}");
            }
            _lastContentHash = ComputeHash(string.Join("\n", hashParts));

            // Parse each document into a VrModEntry
            int skipped = 0;
            int withoutAppId = 0;
            foreach (var docElement in allDocuments)
            {
                if (!docElement.TryGetProperty("fields", out var fields))
                {
                    skipped++;
                    LogRun($"Document skipped (no \"fields\" property): {Snip(docElement.GetRawText())}");
                    continue;
                }

                string? gameName = null;
                string? appID = null;

                // Firestore REST API wraps scalar values in typed objects:
                //   "gameName": {"stringValue": "My Game"}
                //   "appID": {"stringValue": "12345"}
                // So we need to unwrap the stringValue.
                if (fields.TryGetProperty("gameName", out var nameProp))
                {
                    gameName = UnwrapFirestoreString(nameProp);
                }

                if (fields.TryGetProperty("appID", out var appIdProp))
                {
                    appID = UnwrapFirestoreString(appIdProp);
                }

                if (string.IsNullOrEmpty(gameName))
                {
                    // A profile document with no name: cannot match a game.
                    skipped++;
                    LogRun($"Document skipped (no gameName): {Snip(docElement.GetRawText())}");
                    continue;
                }

                if (string.IsNullOrEmpty(appID))
                    withoutAppId++;

                // Strip VR suffixes for cleaner names
                var cleanName = StripVrSuffixes(gameName!).Trim();
                if (string.IsNullOrEmpty(cleanName))
                {
                    skipped++;
                    LogRun($"Game skipped (empty name after VR-suffix strip): {Snip(gameName)}");
                    continue;
                }

                entries.Add(new VrModEntry
                {
                    GameName = cleanName,
                    Category = VrModCategories.UeVr,
                    SteamAppId = appID
                });
            }

            LogRun($"Parsed {entries.Count} games from {allDocuments.Count} Firestore document(s) ({skipped} skipped, {withoutAppId} without appID - those rely on name matching)");
            return entries;
        }

        /// <inheritdoc />
        public override string? GetCanonicalName(VrModEntry entry)
        {
            return StripVrSuffixes(entry.GameName);
        }

        /// <summary>
        /// Returns the <c>nextPageToken</c> from a Firestore list response, or null when there
        /// is no next page: the property is absent, is not a string, or is empty (an empty
        /// token would otherwise re-request the first page forever).
        /// </summary>
        internal static string? GetNextPageToken(JsonElement root)
        {
            if (!root.TryGetProperty("nextPageToken", out var token) || token.ValueKind != JsonValueKind.String)
                return null;

            var value = token.GetString();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>
        /// Unwraps a Firestore REST API scalar value. Firestore wraps scalars in typed objects:
        ///   "stringValue" for strings, "integerValue" for ints, etc.
        /// This method extracts the string value if present, otherwise returns null.
        /// </summary>
        private static string? UnwrapFirestoreString(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("stringValue", out var sv))
                return sv.GetString();
            if (element.ValueKind == JsonValueKind.String)
                return element.GetString();
            return null;
        }

        /// <summary>
        /// Extracts the Firebase web API key and project ID from the UeVR Profiles site.
        ///
        /// The site is a React SPA; the Firebase config is embedded in the main JS bundle
        /// as a minified object literal: <c>apiKey:"AIza...",projectId:"uevrprofiles",...</c>.
        /// This method fetches the SPA shell, finds the JS bundle path, fetches the bundle,
        /// and extracts <c>apiKey</c> and <c>projectId</c> via regex.
        ///
        /// If any step fails (site unreachable, bundle renamed, format change), the method
        /// falls back to the hardcoded <see cref="FallbackFirebaseApiKey"/> and
        /// <see cref="FallbackProjectId"/> so the parser keeps working.
        ///
        /// The Firebase web API key is a public key embedded in client-side JS for
        /// unauthenticated Firestore reads. It is NOT an auth token and is not a secret.
        /// Google restricts which Firestore collections are publicly readable via
        /// security rules, not via the API key.
        ///
        /// TEMPORARY (2026-08-30): replace when a stable API exists.
        /// </summary>
        private async Task TryExtractFirebaseConfigAsync()
        {
            _firebaseApiKey = FallbackFirebaseApiKey;
            _projectId = FallbackProjectId;

            try
            {
                // Step 1: Fetch the SPA shell
                var shell = await FetchStringAsync(SpaUrl);

                // Step 2: Find the main JS bundle path in the HTML.
                // Pattern: src="/static/js/main.&lt;hash&gt;.js"
                // Find the main JS bundle path: /static/js/main.<hash>.js
                var pathMatch = MatchScanned(shell, @"(/static/js/main\.[a-f0-9]+\.js)");
                if (!pathMatch.Success)
                    return; // Fall back to hardcoded values

                var bundleUrl = $"https://uevr-profiles.com{pathMatch.Groups[1].Value}";

                // Step 3: Fetch the JS bundle
                var bundle = await FetchStringAsync(bundleUrl);

                // Step 4: Extract apiKey and projectId from the bundle. Both come from fetched
                // data, not configuration, so each has to match the shape a real Firebase web config
                // can have before it is used in a request (see ParseFirebaseConfigFromBundle).
                var scraped = ParseFirebaseConfigFromBundle(bundle);

                if (scraped.ApiKey != null)
                    _firebaseApiKey = scraped.ApiKey;
                else if (scraped.ApiKeySeen)
                    LogRun($"Rejected scraped Firebase apiKey (not a plausible API key shape: {Snip(scraped.RejectedApiKey)}); keeping the pinned fallback key");

                if (scraped.ProjectId != null)
                    _projectId = scraped.ProjectId;
                else if (scraped.ProjectIdSeen)
                    LogRun($"Rejected scraped Firebase projectId (not a plausible project id shape: {Snip(scraped.RejectedProjectId)}); keeping the pinned fallback project");
            }
            catch (Exception ex) when (IsRequestPlumbingFailure(ex))
            {
                // Site unreachable — keep the pinned fallback key and project id. A failure that is
                // not request plumbing is a fault in this extraction, and hiding it behind a working
                // fallback is how a broken extraction stays broken forever.
            }
        }

        /// <summary>
        /// Shape a Firebase web API key is allowed to have before the plugin puts it in a request:
        /// 20-128 characters of the base64url alphabet only (Google API keys are
        /// <c>AIza…</c>-prefixed alphanumerics with <c>-</c>/<c>_</c>). No quotes, slashes, colons,
        /// <c>&amp;</c>, <c>?</c>, whitespace or control characters — none of those appear in a real key,
        /// and each of them is how a scraped value would break out of the <c>key=</c> query parameter
        /// it is interpolated into.
        /// </summary>
        private static readonly Regex FirebaseApiKeyShape =
            new Regex(@"^[A-Za-z0-9_-]{20,128}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>
        /// Shape a Firebase/GCP project id is allowed to have before the plugin puts it in a request
        /// PATH: lowercase alphanumerics plus <c>-</c>/<c>_</c>, 2-64 characters, starting and ending
        /// with an alphanumeric. This is the load-bearing one: <c>projectId</c> is interpolated raw
        /// into <c>/v1/projects/&lt;id&gt;/databases/(default)/documents</c>, so <c>..</c>, <c>/</c>,
        /// <c>?</c> or <c>#</c> in a scraped value would change which Firestore path the plugin reads
        /// — and that data is what later becomes game names and Links.
        /// </summary>
        private static readonly Regex FirebaseProjectIdShape =
            new Regex(@"^[a-z0-9][a-z0-9_-]{0,62}[a-z0-9]$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>
        /// Finds <c>apiKey:"…"</c> / <c>projectId:"…"</c> in the site's minified JS bundle.
        /// Length-bounded captures (a 200 KB "key" is a payload, not a key) and a 1 s match timeout,
        /// so a hostile bundle cannot make the plugin spend a scan on regex work.
        /// </summary>
        private static readonly Regex BundleApiKeyPattern =
            new Regex(@"apiKey:""([^""]{1,512})""", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <inheritdoc cref="BundleApiKeyPattern" />
        private static readonly Regex BundleProjectIdPattern =
            new Regex(@"projectId:""([^""]{1,512})""", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>True when <paramref name="apiKey"/> has the shape of a Firebase web API key.</summary>
        /// <param name="apiKey">Candidate scraped from the site's bundle.</param>
        internal static bool IsUsableFirebaseApiKey(string? apiKey)
            => apiKey != null && FirebaseApiKeyShape.IsMatch(apiKey);

        /// <summary>True when <paramref name="projectId"/> is safe to interpolate into a request path.</summary>
        /// <param name="projectId">Candidate scraped from the site's bundle.</param>
        internal static bool IsUsableFirebaseProjectId(string? projectId)
            => projectId != null && FirebaseProjectIdShape.IsMatch(projectId);

        /// <summary>
        /// Reads the Firebase config out of the site's JS bundle and validates it (internal, and the
        /// bundle is supplied by the caller, so unit tests can feed a hostile bundle without the
        /// network). A scraped value is used only when it matches the shape of a real Firebase web
        /// config; anything else is reported as rejected and the caller keeps the pinned fallback.
        /// </summary>
        /// <param name="bundle">The fetched JS bundle text.</param>
        internal static ScrapedFirebaseConfig ParseFirebaseConfigFromBundle(string? bundle)
        {
            var result = new ScrapedFirebaseConfig();
            if (string.IsNullOrEmpty(bundle))
                return result;

            var keyMatch = BundleApiKeyPattern.Match(bundle);
            if (keyMatch.Success)
            {
                result.ApiKeySeen = true;
                var raw = keyMatch.Groups[1].Value;
                if (IsUsableFirebaseApiKey(raw))
                    result.ApiKey = raw;
                else
                    result.RejectedApiKey = raw;
            }

            var projMatch = BundleProjectIdPattern.Match(bundle);
            if (projMatch.Success)
            {
                result.ProjectIdSeen = true;
                var raw = projMatch.Groups[1].Value;
                if (IsUsableFirebaseProjectId(raw))
                    result.ProjectId = raw;
                else
                    result.RejectedProjectId = raw;
            }

            return result;
        }

        /// <summary>
        /// What the site's JS bundle claimed as Firebase config, after shape validation.
        /// A <see cref="ApiKey"/> / <see cref="ProjectId"/> of null means "absent or rejected" — the
        /// caller must then use the pinned fallback values, never the raw claim
        /// (<see cref="RejectedApiKey"/> / <see cref="RejectedProjectId"/> exist only for the run log).
        /// </summary>
        internal sealed class ScrapedFirebaseConfig
        {
            /// <summary>Validated API key, or null when absent or rejected.</summary>
            public string? ApiKey;
            /// <summary>Validated project id, or null when absent or rejected.</summary>
            public string? ProjectId;
            /// <summary>The bundle mentioned an apiKey at all (used to log a rejection).</summary>
            public bool ApiKeySeen;
            /// <summary>The bundle mentioned a projectId at all (used to log a rejection).</summary>
            public bool ProjectIdSeen;
            /// <summary>Rejected raw API key, for the log line only (never used in a request).</summary>
            public string? RejectedApiKey;
            /// <summary>Rejected raw project id, for the log line only (never used in a request).</summary>
            public string? RejectedProjectId;
        }

        /// <summary>
        /// Computes a SHA-256 hash of the given content string.
        /// Used to generate ChangeIdentifiers for detecting source content changes.
        /// Delegates to the inherited BaseVrModParser.ComputeHash method.
        /// </summary>
        /// <param name="content">The string content to hash.</param>
        /// <returns>Base64-encoded SHA-256 hash of the content.</returns>
        protected static new string ComputeHash(string content) => BaseVrModParser.ComputeHash(content);
    }
}
