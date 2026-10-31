using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace Playnite.VRMods
{
    /// <summary>
    /// Data model for a single match entry in the review dialog.
    ///
    /// This class holds all information needed to display and process a match
    /// between a VR mod source entry and a Playnite game.
    ///
    /// Each instance represents one potential VR mod tag for a game, with:
    /// - Source information (ID, display name, original game name)
    /// - Playnite game reference
    /// - Match quality (score between 0.0 and 1.0)
    /// - VR mod category (e.g., "UEVR Profiles", "Flat2VR Port")
    /// - User decision status (Pending, Accepted, or Rejected)
    ///
    /// This object is passed to VrModReviewDialog and displayed in the DataGrid.
    /// When the user makes a decision (Accept/Reject), the decision is recorded in
    /// VrModTagSettings.MatchDecisions under <see cref="DecisionKey"/> - the row carries the
    /// key its decision was read from, so the write lands on exactly that mapping (a name is
    /// never re-derived here, and a category is not part of a decision at all).
    ///
    /// The Status property is updated in-place when the user confirms decisions
    /// in the review dialog.
    /// </summary>
    public class VrModReviewMatchEntry : INotifyPropertyChanged
    {
        /// <summary>
        /// Raised when <see cref="Status"/> changes and, because the display members are derived
        /// from it, when <see cref="DisplayStatusText"/>, <see cref="IsUnsaved"/>, and
        /// <see cref="SavedAtDisplay"/> change as a result. The review dialog's status column,
        /// italic marker, and "Saved at" column are bound to those members; this notification
        /// is what makes them refresh when a bulk Accept/Reject action, a double-click cycle,
        /// or a programmatic change sets the decision on the data.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        private MatchDecision _status = MatchDecision.Pending;

        /// <summary>
        /// User decision status (Pending, Accepted, or Rejected). Single source of truth for
        /// the review dialog: the status column (and the derived display members) are bound to
        /// this, and the bulk buttons / double-click cycle write to it directly.
        /// </summary>
        public MatchDecision Status
        {
            get => _status;
            set
            {
                if (_status == value)
                    return;
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                // Display members derived from the decision must refresh too: the text status,
                // the unsaved (italic) marker, and the "Saved at" text all change with Status.
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayStatusText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUnsaved)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SavedAtDisplay)));
            }
        }

        /// <summary>Source ID (e.g., "RototRobot", "Flat2VR", "UeVR").</summary>
        public string SourceId { get; set; } = string.Empty;

        /// <summary>
        /// The settings key this row's decision was read from, built by
        /// <see cref="VrModTagService"/> from the listing's stable identity (its Steam App ID,
        /// else its page URL, else its name) and the matched game's Id.
        ///
        /// It travels with the row because the row's identity half cannot be rebuilt from the
        /// row itself: the review dialog shows the listing's DISPLAY NAME, and a name is not an
        /// identity. Every decision the dialog writes goes to this key, so accepting a row
        /// re-decides the mapping the scan found rather than a name-shaped guess next to it.
        /// </summary>
        public string DecisionKey { get; set; } = string.Empty;

        /// <summary>Source display name (e.g., "RototRobot VRMods-List").</summary>
        public string SourceName { get; set; } = string.Empty;

        /// <summary>Game name from the VR mod source entry.</summary>
        public string SourceGameName { get; set; } = string.Empty;

        /// <summary>
        /// URL to the exact mod/game page for this match on its source: a per-game deep link when
        /// the source provides one (e.g. PCVR Central), otherwise the source's website. Opened in
        /// the user's default browser by the review dialog's "open mod page" button. Null if both
        /// the per-entry URL and the source website are unknown.
        /// </summary>
        public string? SourceUrl { get; set; }

        /// <summary>Reference to the Playnite game object for this match.</summary>
        public Playnite.SDK.Models.Game PlayniteGame { get; set; } = null!;

        /// <summary>
        /// The display text for the game in the review grid. Equals <see cref="PlayniteGame"/>'s
        /// name, with the store source in brackets ("Alien: Isolation (Steam)") only when the
        /// same name is shared by more than one game (a multi-store duplicate). Computed at
        /// build time by the tag service, which knows the duplicate set; a unique name is
        /// shown unchanged.
        /// </summary>
        public string GameDisplayName { get; set; } = string.Empty;

        /// <summary>Match quality score (0.0–1.0, higher = more confident match).</summary>
        public float Score { get; set; }

        /// <summary>
        /// The categories this match's mod belongs to (a mod may have several — e.g. both
        /// "Full Motion Controls" and "UEVR"). Each non-catalog category contributes a
        /// "VRMod {category}" tag on accept; the decision is per-mapping, so one decision
        /// covers all of a mod's categories.
        /// </summary>
        public List<string> Categories { get; set; } = new();

        /// <summary>
        /// Legacy single-category alias (one category per mod). Setting it replaces <see
        /// cref="Categories"/> with that single category (or clears it), so pre-existing
        /// single-category constructors/writers keep compiling and stay consistent. Reading
        /// returns the first category (or null when there are none).
        /// </summary>
        public string? Category
        {
            get => Categories.Count > 0 ? Categories[0] : null;
            set
            {
                if (string.IsNullOrEmpty(value))
                    Categories = new List<string>();
                else
                    Categories = new List<string> { value! };
            }
        }

        /// <summary>
        /// Whether the matched VR mod provides 6DOF (mirrors VrModEntry.Is6DOF).
        /// When true, the "VRMod 6DOF" capability tag is applied on accept.
        /// </summary>
        public bool? Is6DOF { get; set; }

        /// <summary>
        /// Whether the matched VR mod is a first-person experience (mirrors
        /// VrModEntry.IsFirstPerson). When true, the "VRMod First Person" facet tag is
        /// applied on accept.
        /// </summary>
        public bool? IsFirstPerson { get; set; }

        /// <summary>
        /// Whether the matched VR mod is a third-person experience (mirrors
        /// VrModEntry.IsThirdPerson). When true, the "VRMod Third Person" facet tag is
        /// applied on accept.
        /// </summary>
        public bool? IsThirdPerson { get; set; }

        /// <summary>
        /// The quality/grade(s) of this match's VR experience (mirrors VrModEntry.Qualities).
        /// Each value contributes a "VRMod {quality}" facet tag on accept. A collection because
        /// the PCVR Central meta-mod keeps the union of all its mods' grades (union-all). Empty =
        /// no quality tag.
        /// </summary>
        public System.Collections.Generic.List<string> Qualities { get; set; } = new();

        /// <summary>
        /// Legacy single-grade alias (mirrors VrModEntry.Quality). Setting it replaces <see
        /// cref="Qualities"/> with that single grade (or clears it), so pre-existing writers and
        /// unit tests keep compiling and stay consistent with <see cref="Qualities"/>. Reading
        /// returns the first grade, or null when there are none.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? Quality
        {
            get => Qualities.Count > 0 ? Qualities[0] : null;
            set
            {
                if (string.IsNullOrEmpty(value))
                    Qualities = new System.Collections.Generic.List<string>();
                else
                    Qualities = new System.Collections.Generic.List<string> { value! };
            }
        }

        /// <summary>
        /// The motion-controls level(s) of this match's VR experience (mirrors VrModEntry.Motions).
        /// Each value contributes a "VRMod {motion}" facet tag on accept. A collection for the
        /// same union-all reason as <see cref="Qualities"/>. Empty = no motion tag.
        /// </summary>
        public System.Collections.Generic.List<string> Motions { get; set; } = new();

        /// <summary>
        /// Legacy single motion-level alias (mirrors VrModEntry.Motion). Setting it replaces <see
        /// cref="Motions"/> with that single level (or clears it), mirroring <see cref="Quality"/>
        /// and <see cref="Qualities"/>. Reading returns the first level, or null when there are none.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? Motion
        {
            get => Motions.Count > 0 ? Motions[0] : null;
            set
            {
                if (string.IsNullOrEmpty(value))
                    Motions = new System.Collections.Generic.List<string>();
                else
                    Motions = new System.Collections.Generic.List<string> { value! };
            }
        }

        /// <summary>
        /// The mod-status value(s) the source currently reports for this match's mod(s)
        /// (mirrors VrModEntry.Statuses): e.g. "Active", "Update Detected", "Outdated", "Broken",
        /// "Abandoned". Each value contributes a "VRMod {status}" facet tag on accept. Named
        /// ModStatuses rather than Statuses because <see cref="Status"/> on this type already means
        /// something else here: the user's Pending/Accepted/Rejected decision for the match.
        /// A collection for the same union-all reason as <see cref="Qualities"/>: a game's mods can
        /// be in different states, and a game keeps every one of them. Empty = no status tag.
        /// </summary>
        public System.Collections.Generic.List<string> ModStatuses { get; set; } = new();

        /// <summary>
        /// Whether this row is a SUGGESTION rather than a match: the name rules found a pairing worth
        /// a decision but refused to assert it (see <see cref="VrModMatchSuggestion"/>). A suggestion
        /// row carries no applied tags and its game was not tagged by the scan; accepting it is what
        /// makes it a mapping. Drives the row's "Suggested" status text.
        /// </summary>
        public bool IsSuggestion { get; set; }

        /// <summary>
        /// Why the pairing is being shown, in one line, naming the words left over on each side of the
        /// name comparison. Empty on an ordinary match row. Shown as the status cell's tooltip; the row
        /// exists precisely because the similarity number in <see cref="Score"/> does not explain it.
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// The status cell's tooltip: <see cref="Reason"/> on a suggestion row, null on an ordinary
        /// match row (WPF shows no tooltip for a null value, and an empty one would render an empty
        /// box). Without it a "Suggested" row is a grey word with no way to find out what was
        /// suspected about it.
        /// </summary>
        public string? ReasonTooltip => string.IsNullOrEmpty(Reason) ? null : Reason;

        /// <summary>
        /// Sort rank for the decision status, in review priority order: Pending (0) first,
        /// Rejected (1) next, Accepted (2) last. This is what the review dialog's default
        /// (primary) sort orders by ascending — "pending first, accepted last". It is a
        /// separate property from <see cref="Status"/> because the enum declaration order
        /// (Pending, Accepted, Rejected) does not match the desired review order (Accepted
        /// must sort last, not Rejected).
        /// </summary>
        public int StatusSortRank
        {
            get
            {
                switch (Status)
                {
                    case MatchDecision.Accepted:
                        return 2;
                    case MatchDecision.Rejected:
                        return 1;
                    default: // Pending
                        return 0;
                }
            }
        }

        /// <summary>
        /// This match's OWN tag names (the ones it contribute beyond the umbrella): one category
        /// tag per (non-catalog) category ("VRMod {category}") plus every set facet (6DOF,
        /// First/Third Person, quality, motion, mod status). A CATALOG category contributes none of
        /// these, so a catalog match's set is empty. This is the single source of truth shared with
        /// the tag service and the changed-tag detection (the stored baseline diffs against this set).
        /// </summary>
        public List<string> AppliedTagNames
        {
            get => VrModCategories.TagNamesFor(Categories, Is6DOF, IsFirstPerson, IsThirdPerson, Qualities, Motions, ModStatuses);
        }

        /// <summary>
        /// The full tag names this match would put on its game if accepted: the umbrella
        /// "VRMod" tag (applied to every matched game, so it always appears) plus this entry's
        /// own tags (<see cref="AppliedTagNames"/>). Used by the review dialog's "Tags" column.
        /// A catalog row (no category/facet tags of its own) therefore shows just the umbrella
        /// "VRMod" - it is never empty. The umbrella is a display addition only: the
        /// changed-tag baseline tracks <see cref="AppliedTagNames"/> (this entry's own tags),
        /// so a catalog match (empty own-tags) never shows as changed.
        /// </summary>
        public string TagPreview
        {
            get
            {
                var names = new List<string> { VrModCategories.TagUmbrella };
                names.AddRange(AppliedTagNames);
                return string.Join("  |  ", names);
            }
        }

        /// <summary>
        /// When the decision for this match was made (UTC): first-seen for a Pending
        /// match, last-modified for an Accepted / Rejected one. Populated by the scan
        /// (see <see cref="VrModTagService"/>). Used by the review dialog's time filter.
        /// Defaults to <see cref="DateTime.MinValue"/> (no timestamp), which the time
        /// filter treats as the oldest entry (only visible under "Always").
        /// </summary>
        public DateTime DecidedAtUtc { get; set; } = DateTime.MinValue;

        /// <summary>
        /// The decision persisted in the plugin's JSON at the moment the review dialog opened
        /// (<see cref="MatchDecision.Accepted"/> or <see cref="MatchDecision.Rejected"/>), or
        /// <c>null</c> when the mapping has no saved decision yet (i.e. it is pending / never
        /// decided). This is the "already saved to JSON" axis, orthogonal to the live
        /// <see cref="Status"/>: comparing the two is what lets the UI tell an *already-saved*
        /// mapping from a *new (not-yet-saved)* one, and an *old* rejection from a fresh one.
        /// Set once at construction by the scan; it never changes during a dialog session (a
        /// decision only reaches the JSON when the dialog is saved).
        /// </summary>
        public MatchDecision? PersistedDecision { get; internal set; }

        /// <summary>
        /// When the persisted decision (<see cref="PersistedDecision"/>) was last saved to the
        /// plugin's JSON (UTC), or <c>null</c> when there is no saved decision. Drives the
        /// "Saved at" column in the review dialog. Set once at construction by the scan; it
        /// never changes during a dialog session (a decision only reaches the JSON when the
        /// dialog is saved).
        /// </summary>
        public DateTime? SavedAtUtc { get; internal set; }

        /// <summary>
        /// True when the current <see cref="Status"/> is a decision made in this dialog session
        /// that is not yet the one saved to JSON — i.e. <see cref="Status"/> differs from
        /// <see cref="PersistedDecision"/> (or there is no saved decision). Pending is never
        /// "unsaved" (it is not a recorded decision). Drives the italic marker in the status
        /// text and blanks the "Saved at" cell for a just-made decision.
        /// </summary>
        public bool IsUnsaved
        {
            get
            {
                if (Status == MatchDecision.Pending)
                    return false;
                return PersistedDecision != Status;
            }
        }

        /// <summary>
        /// The text shown in the review dialog's status column, combining the decision with the
        /// saved/unsaved axis so the four cases that matter are distinguishable at a glance:
        /// "Saved" (an accepted mapping already persisted to JSON), "New" (an accepted mapping
        /// made this session, not yet saved), "Rejected" (a previously saved rejection),
        /// "Rejected (new)" (a rejection made this session, not yet saved), and "Pending"
        /// (a fuzzy match whose correctness is uncertain and needs review).
        /// </summary>
        public string DisplayStatusText
        {
            get
            {
                switch (Status)
                {
                    case MatchDecision.Accepted:
                        return IsUnsaved ? "New" : "Saved";
                    case MatchDecision.Rejected:
                        return IsUnsaved ? "Rejected (new)" : "Rejected";
                    default: // Pending
                        // A suggestion is pending too, but it is a different kind of question: a
                        // Pending row is a fit the rules are confident about that nobody has confirmed
                        // yet, a Suggested row is a pairing the rules will not call a fit at all.
                        // Saying "Pending" for both hides which row needs reading.
                        return IsSuggestion ? "Suggested" : "Pending";
                }
            }
        }

        /// <summary>
        /// The text shown in the review dialog's "Saved at" column: when the current decision
        /// was last saved to JSON (formatted <c>yyyy-MM-dd HH:mm</c>, local time), or "—" when there is no
        /// saved decision (a pending match, or a decision just made in this session that has not
        /// been saved yet). Only shows a time when the current decision is the saved one.
        /// </summary>
        public string SavedAtDisplay
        {
            get
            {
                if (Status == MatchDecision.Pending || IsUnsaved)
                    return "—";
                return SavedAtUtc is { } savedAt
                    ? savedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : "—";
            }
        }

        /// <summary>
        /// True when the tags this accepted match would apply differ from the set stored when
        /// the match was last accepted — i.e. the source added or removed a tag (e.g. 6DOF)
        /// since the decision was saved. Only meaningful for an already-saved Accepted
        /// decision (a saved decision has a stored baseline to compare); always false
        /// otherwise. Drives the changed-tag marker in the review dialog. Set once at
        /// construction by the scan; it never changes during a dialog session.
        /// </summary>
        public bool TagsChanged { get; internal set; }

        /// <summary>
        /// Human-readable description of the tag change since the last accept (e.g.
        /// "+ VRMod 6DOF" or "- VRMod First Person"), or empty when there is no change. Set
        /// once at construction by the scan, alongside <see cref="TagsChanged"/>.
        /// </summary>
        public string TagDeltaText { get; internal set; } = string.Empty;

        /// <summary>
        /// The Tags column text with a trailing " \u2191" (up-arrow) marker appended when this
        /// accepted match's source tags changed since the last accept (<see cref="TagsChanged"/>)
        /// - the inline, at-a-glance feature-change indicator on the Tags cell itself (so a
        /// changed row is flagged even when the separate "Changed" column is hidden or off-screen).
        /// The detailed added/removed tags are in <see cref="TagDeltaText"/> (the "Changed"
        /// column) and <see cref="TagDeltaTooltip"/> (the cell tooltip). Equals
        /// <see cref="TagPreview"/> when unchanged. Derived from set-once members, so it never
        /// changes during a dialog session and needs no notification.
        /// </summary>
        public string TagPreviewWithMarker => TagsChanged ? TagPreview + "  \u2191" : TagPreview;

        /// <summary>
        /// The tooltip for the Tags cell: a human description of the tag change since the last
        /// accept, or <c>null</c> when there is no change (so WPF shows no tooltip bubble). The
        /// same delta as the "Changed" column (<see cref="TagDeltaText"/>) with a prefix that
        /// makes it self-explanatory on hover. Derived from set-once members, so it never changes
        /// during a dialog session and needs no notification.
        /// </summary>
        public string? TagDeltaTooltip => TagsChanged ? "Changed since you saved this: " + TagDeltaText : null;
    }
}
