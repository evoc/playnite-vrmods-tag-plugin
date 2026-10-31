namespace Playnite.VRMods
{
    /// <summary>
    /// Canonical VRMod <b>category</b> names (the value that appears in "VRMod {category}"
    /// tags). Single source of truth so the vocabulary is identical across parsers, the tag
    /// service, and the review dialog.
    /// </summary>
    /// <remarks>
    /// Categories are grouped by <i>how VR is enabled</i> (framework / injector) plus the
    /// <i>input</i> categories the current parsers still emit.
    /// <para>
    /// Framework / injector categories:
    /// <list type="bullet">
    /// <item><see cref="UeVr"/> — universal UE4/5 injector.</item>
    /// <item><see cref="ReFramework"/> — universal RE (Resident Evil) engine injector.</item>
    /// <item><see cref="Flat2Vr"/> — per-title Flat2VR port.</item>
    /// <item><see cref="Dedicated"/> — game-specific VR mod / dedicated injector / emulator VR
    /// (no universal framework applies). PCVR Central's <c>native-custom</c> framework is this
    /// category: the site calls it "A dedicated VR mod built for this one game".</item>
    /// <item><see cref="VorpX"/>, <see cref="LukeRoss"/>, <see cref="UuVr"/>,
    /// <see cref="Depth3D"/>, <see cref="Other"/> — the remaining framework labels PCVR Central
    /// reports per mod. Each one is confirmed against the site's own <c>methodExplainer</c>
    /// dictionary (see external-references.md, "Framework ambiguities"), not inferred from the
    /// slug.</item>
    /// </list>
    /// Input categories (<see cref="FullMotionControls"/>, <see cref="MotionGesture"/>,
    /// <see cref="HeadTracked"/>) describe how the player interacts; a future follow-on may
    /// promote these to orthogonal facet tags (like the 6DOF facet) rather than category
    /// tags.
    /// </para>
    /// </remarks>
    public static class VrModCategories
    {
        // Framework / injector categories (how VR is enabled).
        public const string UeVr = "UEVR";
        public const string ReFramework = "REFramework";
        public const string Flat2Vr = "Flat2VR Port";
        public const string Dedicated = "Dedicated";
        // The PCVR Central payload reports per-mod frameworks; these are the framework
        // categories extracted from it (see external-references.md, "PCVR Central per-mod data").
        // Each name is the site's own label for that framework: "UUVR" is its Unity-game injector (NOT a UEVR variant —
        // UEVR is the Unreal one), "Depth3D" is a ReShade depth shader the site itself says is
        // "not a real VR conversion", and "Other" is the site's catch-all for a method that does
        // not fit its categories.
        public const string VorpX = "VorpX";
        public const string LukeRoss = "LukeRoss";
        public const string UuVr = "UUVR";
        public const string Depth3D = "Depth3D";
        public const string Other = "Other";

        // Catalog categories (a curated directory of games that have VR mods, rather than
        // a specific framework/injector).
        public const string PcvrCentral = "PCVR Central";
        public const string MrNlceHub = "PCVR Mods Hub";
        public const string MrSurvivorHub = "Mr Survivor";
        public const string CompoundVr = "CompoundVR";

        // Input-type categories (how the player interacts).
        public const string FullMotionControls = "Full Motion Controls";
        public const string MotionGesture = "Motion Gesture";
        public const string HeadTracked = "Head Tracked";

        // Motion-facet VALUES (the "VRMod {Motion}" tag value). A source-reported level of
        // motion-controls support per mod (orthogonal facet, like Quality).
        public const string MotionFull = "Full Motion";
        public const string MotionPartial = "Partial Motion";
        public const string MotionGamepadOnly = "Gamepad Only";
        public const string MotionNone = "No Motion";

        /// <summary>
        /// Values two sources spell differently but that assert the same thing about a game, mapped to
        /// the one spelling this plugin mints a tag from. RototRobot's "Full Motion Controls" category
        /// and PCVR Central's "Full Motion" motion level are the same claim; minting both put two tags
        /// on one game for one claim (measured on a real library: 54 games had "Full Motion", 37 had
        /// "Full Motion Controls", 22 had both at once).
        ///
        /// Only the minted tag name moves. The source-side spelling stays in <see cref="KnownTagValues"/>
        /// so values parsed today, values saved by older plugin versions, and tag names already in a
        /// user's library all still resolve - which is what lets the migration in <c>VrModTagService</c>
        /// recognise the old tag as its own and replace it.
        /// </summary>
        public static readonly System.Collections.Generic.IReadOnlyDictionary<string, string> TagValueAliases =
            new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                [FullMotionControls] = MotionFull
            };

        /// <summary>
        /// The canonical spelling a vocabulary value is minted as (see <see cref="TagValueAliases"/>).
        /// An unmapped value is returned unchanged, and so is null/blank input.
        /// </summary>
        /// <param name="value">A category / facet value from a source or from a stored decision.</param>
        /// <returns>The value this plugin tags with.</returns>
        public static string CanonicalTagValue(string? value)
        {
            var trimmed = value == null ? string.Empty : value.Trim();
            if (trimmed.Length == 0) return trimmed;
            return TagValueAliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
        }

        /// <summary>
        /// The tag name a tag name stored by an older plugin version maps to today - identity when the
        /// name is already the current spelling. The changed-tag diff runs both sides through this, so
        /// the plugin renaming its own vocabulary does not report every stored decision as "changed".
        /// </summary>
        /// <param name="tagName">A full tag name, e.g. "VRMod Full Motion Controls".</param>
        /// <returns>The tag name this plugin would use for that claim now.</returns>
        public static string CanonicalTagName(string? tagName)
        {
            if (tagName == null || tagName.Length == 0) return string.Empty;
            if (tagName.Equals(TagUmbrella, System.StringComparison.Ordinal)) return TagUmbrella;
            var prefix = TagUmbrella + " ";
            if (!tagName.StartsWith(prefix, System.StringComparison.Ordinal)) return tagName;
            var value = tagName.Substring(prefix.Length);
            var canonical = CanonicalTagValue(value);
            return canonical == value ? tagName : $"{TagUmbrella} {canonical}";
        }

        /// <summary>
        /// Whether a vocabulary value is an old spelling the plugin still understands but no longer
        /// mints (see <see cref="TagValueAliases"/>).
        /// </summary>
        /// <param name="value">A category / facet value.</param>
        /// <returns>True if the value is a legacy spelling with a canonical replacement.</returns>
        public static bool IsLegacyTagValue(string? value)
        {
            return value != null && !string.IsNullOrWhiteSpace(value) && TagValueAliases.ContainsKey(value.Trim());
        }
        // Quality-facet VALUES a source may report (the vocabulary PCVR Central's qualityLabel is
        // mapped onto). Declared here rather than only inside the parser because the tag whitelist
        // below has to know every value that is allowed to become a tag.
        public const string QualityNativeLike = "Native-like";
        public const string QualityExcellent = "Excellent";
        public const string QualityPlayable = "Playable";
        public const string QualityExperimental = "Experimental";

        // Mod-status facet VALUES (the "VRMod {status}" tag value): what the source currently
        // says about the mod itself, per mod, orthogonal to quality and motion. PCVR Central
        // reports five; the names here are the site's own display labels (its payload key
        // "superseded" is shown as "Outdated"). A game keeps the status of EVERY mod it has —
        // "Active" plus "Broken" on one game means one working mod and one broken one, which is
        // information, not a contradiction (union-all).
        public const string StatusActive = "Active";
        public const string StatusUpdateDetected = "Update Detected";
        public const string StatusOutdated = "Outdated";
        public const string StatusBroken = "Broken";
        public const string StatusAbandoned = "Abandoned";
        // NOTE: the site's own quality vocabulary also has a "Broken" grade (no mod currently
        // uses it). It is deliberately not declared here: it would be the same tag name as the
        // status value above, and two meanings in one tag name is how a tag list becomes
        // unreadable. If the source ever reports it, MapQuality leaves it unmapped.

        /// <summary>
        /// Every value this plugin is allowed to turn into a <c>"VRMod {value}"</c> tag: the
        /// categories above, the motion levels, the quality grades, and the two fixed person-view
        /// facets. The category/quality/motion lists on an entry come from text a website wrote, and
        /// they are what tag names are built from - so the vocabulary lives here, once, instead of in
        /// whichever parser happens to map the scraped text today.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> KnownTagValues =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                UeVr, ReFramework, Flat2Vr, Dedicated, VorpX, LukeRoss, UuVr, Depth3D, Other,
                PcvrCentral, MrNlceHub, MrSurvivorHub, CompoundVr,
                FullMotionControls, MotionGesture, HeadTracked,
                MotionFull, MotionPartial, MotionGamepadOnly, MotionNone,
                QualityNativeLike, QualityExcellent, QualityPlayable, QualityExperimental,
                StatusActive, StatusUpdateDetected, StatusOutdated, StatusBroken, StatusAbandoned,
                // The fixed facet tags below are built from these values, so they have to be known
                // too (ScannedTagVocabularyTests pins that the two lists stay in step).
                "6DOF", "First Person", "Third Person"
            };

        /// <summary>
        /// Whether a category / quality / motion value is one this plugin knows how to tag with.
        /// </summary>
        /// <param name="value">A value reported by a source (or stored in a saved decision).</param>
        /// <returns>True if the value is in <see cref="KnownTagValues"/>.</returns>
        public static bool IsKnownTagValue(string? value)
        {
            return value != null && !string.IsNullOrWhiteSpace(value) && KnownTagValues.Contains(value.Trim());
        }

        /// <summary>
        /// The values in an entry that are <b>not</b> in the vocabulary, i.e. the ones that would
        /// silently disappear from the applied tags. Reported so a source renaming a category on its
        /// site is visible in the log instead of showing up as games quietly losing their category tags.
        /// </summary>
        public static System.Collections.Generic.List<string> UnknownTagValues(VrModEntry? entry)
        {
            var unknown = new System.Collections.Generic.List<string>();
            if (entry == null) return unknown;
            foreach (var value in entry.Categories)
                if (!IsKnownTagValue(value)) unknown.Add(value ?? "<null>");
            foreach (var value in entry.Qualities)
                if (!IsKnownTagValue(value)) unknown.Add(value ?? "<null>");
            foreach (var value in entry.Motions)
                if (!IsKnownTagValue(value)) unknown.Add(value ?? "<null>");
            foreach (var value in entry.Statuses)
                if (!IsKnownTagValue(value)) unknown.Add(value ?? "<null>");
            return unknown;
        }

        /// <summary>
        /// Whether a tag name is one this plugin can legitimately create: the umbrella tag, one of the
        /// three fixed facet tags, or "VRMod {value}" with a value from <see cref="KnownTagValues"/>.
        /// The name is built by concatenating a scraped value onto the umbrella prefix, so without
        /// this check a parser that forwarded raw page text (a mod title, an HTML fragment, a
        /// 200-character description) would write that text into the user's library as a tag.
        /// </summary>
        public static bool IsValidTagName(string? name)
        {
            if (name == null || string.IsNullOrWhiteSpace(name)) return false;
            if (name == TagUmbrella) return true;
            if (name == Tag6DOF || name == TagFirstPerson || name == TagThirdPerson) return true;

            var prefix = TagUmbrella + " ";
            if (name.Length <= prefix.Length || !name.StartsWith(prefix, System.StringComparison.Ordinal))
                return false;
            var value = name.Substring(prefix.Length);
            // IsKnownTagValue trims for comparison; require the composed name to be canonical so a
            // value with padding never produces a second near-duplicate tag in the library.
            return value == value.Trim() && IsKnownTagValue(value);
        }

        /// <summary>
        /// Whether a tag in the library belongs to this plugin, and may therefore be counted, removed
        /// or wiped by it.
        ///
        /// Deliberately stricter than a bare <c>StartsWith("VRMod")</c>: the plugin's names are the
        /// umbrella tag exactly, or the umbrella followed by a space and a vocabulary value. A user's
        /// own tag that merely shares the prefix ("VRModding", "VRModding tips") is not ours and must
        /// survive every removal path, including "Reset and remove tags".
        /// </summary>
        public static bool IsPluginTagName(string? name)
        {
            return name != null && (name.Equals(TagUmbrella, System.StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(TagUmbrella + " ", System.StringComparison.OrdinalIgnoreCase));
        }

        // Tag names (single source of truth for the names the tag service, the metadata
        // provider, the review dialog, and the match-entry preview all build). The
        // umbrella tag is "VRMod" itself; the other facet tags are "VRMod {value}".
        public const string TagUmbrella = "VRMod";
        public const string Tag6DOF = "VRMod 6DOF";
        public const string TagFirstPerson = "VRMod First Person";
        public const string TagThirdPerson = "VRMod Third Person";

        /// <summary>
        /// Whether a category is a CATALOG source (a curated directory of games that have VR
        /// mods, rather than a specific framework/injector): PCVR Central, PCVR Mods Hub, or
        /// Mr Survivor. A catalog match adds NO category tag of its own - it only contributes
        /// the umbrella "VRMod" tag (and the review dialog shows it as its own row with a link
        /// to the source page). The umbrella tag is the single marker that a game has a VR mod,
        /// so a catalog row's Tags column shows just "VRMod".
        /// </summary>
        public static bool IsCatalogCategory(string category)
        {
            return category == PcvrCentral || category == MrNlceHub || category == MrSurvivorHub
                || category == CompoundVr;
        }

        /// <summary>
        /// The category tag name for a category (see the category constants above).
        /// </summary>
        /// <param name="category">A category name (one of the constants above).</param>
        /// <returns>The "VRMod {category}" tag name.</returns>
        public static string TagNameFor(string category)
        {
            return $"{TagUmbrella} {CanonicalTagValue(category)}";
        }

        /// <summary>
        /// The quality-facet tag name for a source-reported quality grade.
        /// </summary>
        /// <param name="quality">A quality grade (e.g. "High").</param>
        /// <returns>The "VRMod {quality}" tag name.</returns>
        public static string TagNameForQuality(string quality)
        {
            return $"{TagUmbrella} {CanonicalTagValue(quality)}";
        }

        /// <summary>
        /// The motion-facet tag name for a source-reported motion-controls level.
        /// </summary>
        /// <param name="motion">A motion level (e.g. "Full Motion").</param>
        /// <returns>The "VRMod {motion}" tag name.</returns>
        public static string TagNameForMotion(string motion)
        {
            return $"{TagUmbrella} {CanonicalTagValue(motion)}";
        }

        /// <summary>
        /// The status-facet tag name for a source-reported mod status.
        /// </summary>
        /// <param name="status">A mod status (e.g. "Outdated").</param>
        /// <returns>The "VRMod {status}" tag name.</returns>
        public static string TagNameForStatus(string status)
        {
            return $"{TagUmbrella} {CanonicalTagValue(status)}";
        }

        /// <summary>
        /// The full set of tag names a matched entry would apply to its game if accepted:
        /// one category tag per (non-catalog) category ("VRMod {category}") plus every facet
        /// that is set - the 6DOF facet, the First/Third Person facet, the quality facet, the
        /// motion-controls facet and the mod-status facet. A CATALOG category contributes no
        /// category tag of its own (see
        /// <see cref="IsCatalogCategory"/>); the umbrella "VRMod" tag an entry still
        /// contributes is applied at the game level, not listed here. This is the single
        /// source of truth for the applied-tag set, shared by the tag service (which applies
        /// them to the game), the review-dialog preview, and the changed-tag detection (which
        /// diffs the current set against the set stored when the match was accepted).
        /// </summary>
        /// <param name="status">Mod-status facet values (the "VRMod {status}" tags); null when
        /// the caller has no status data, e.g. the VRChat parsers, which report none.</param>
        public static System.Collections.Generic.List<string> TagNamesFor(
            System.Collections.Generic.IEnumerable<string> categories, bool? is6DOF, bool? isFirstPerson, bool? isThirdPerson,
            System.Collections.Generic.IReadOnlyCollection<string>? quality = null,
            System.Collections.Generic.IReadOnlyCollection<string>? motion = null,
            System.Collections.Generic.IReadOnlyCollection<string>? status = null)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var category in categories ?? System.Array.Empty<string>())
            {
                // A value outside the vocabulary is skipped rather than turned into a tag name: this
                // method is the single source of truth for the applied-tag set, and it also runs over
                // values saved by older plugin versions.
                if (category != null && IsKnownTagValue(category) && !IsCatalogCategory(category))
                    names.Add(TagNameFor(category));
            }
            if (is6DOF == true)
                names.Add(Tag6DOF);
            if (isFirstPerson == true)
                names.Add(TagFirstPerson);
            if (isThirdPerson == true)
                names.Add(TagThirdPerson);
            foreach (var q in quality ?? System.Array.Empty<string>())
                if (IsKnownTagValue(q))
                    names.Add(TagNameForQuality(q));
            foreach (var m in motion ?? System.Array.Empty<string>())
                if (IsKnownTagValue(m))
                    names.Add(TagNameForMotion(m));
            foreach (var s in status ?? System.Array.Empty<string>())
                if (IsKnownTagValue(s))
                    names.Add(TagNameForStatus(s));
            // Distinct names, in first-seen order. Two different vocabulary paths can now mint the same
            // tag (a source's "Full Motion Controls" category and another source's "Full Motion" motion
            // level are one claim - see TagValueAliases), and this method is the applied-tag set: a
            // duplicate here would show up as a duplicated preview row and as a second tag write.
            if (names.Count > 1)
            {
                var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
                names.RemoveAll(name => !seen.Add(name));
            }
            return names;
        }

        /// <summary>
        /// The applied tag names for a source entry (see the field-based overload).
        /// </summary>
        public static System.Collections.Generic.List<string> TagNamesFor(VrModEntry entry)
        {
            return TagNamesFor(entry.Categories, entry.Is6DOF, entry.IsFirstPerson, entry.IsThirdPerson, entry.Qualities, entry.Motions, entry.Statuses);
        }

    }
}
