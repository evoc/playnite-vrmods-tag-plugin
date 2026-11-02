using System;
using Playnite.SDK.Models;

namespace Playnite.VRMods
{
    /// <summary>
    /// A pairing the matching rules consider plausible but do not assert: a source-catalog listing
    /// and a library game that the name rules cannot separate, offered to a person to confirm.
    ///
    /// A suggestion is never applied. It puts no tag on the game, adds no source link, and stores
    /// no decision. It becomes a match only when a person accepts it in the review dialog, after
    /// which the saved decision carries it (<see cref="VrModTagService.FindGamesByIndex"/> returns
    /// it on every later scan whatever the listing's name looks like then).
    ///
    /// Produced only by <see cref="VrModTagService.FindSuggestedGames"/>.
    /// </summary>
    public sealed class VrModMatchSuggestion
    {
        /// <summary>The library game the listing might be about.</summary>
        public Game Game { get; }

        /// <summary>The listing's name, as its source publishes it.</summary>
        public string SourceGameName { get; }

        /// <summary>
        /// What the token rules saw, in one line, naming the words that are left over on each side
        /// (see <see cref="VrModTokenRun"/>). Shown as the review row's tooltip.
        /// </summary>
        public string Reason { get; }

        /// <summary>Which verdict produced it.</summary>
        public TokenRunKind Kind { get; }

        /// <summary>
        /// Whole-name similarity, shown in the review dialog's Score column. It is display only:
        /// these pairs are here precisely because the whole-name measure is the wrong instrument for
        /// them, so a low number is expected and says nothing about the verdict.
        /// </summary>
        public float DisplayScore { get; }

        /// <summary>
        /// Whether the two sides name DIFFERENT Steam products (each publishes an App ID and none is
        /// shared). The automatic matching path refuses such a pair; the row is still offered because
        /// a name that agrees while the ids disagree is a question for the owner of the library, and
        /// <see cref="Reason"/> names both ids. Conflicted candidates rank behind every non-conflicted
        /// one, so a suggestion never reads as a recommendation the plugin itself would not act on.
        /// </summary>
        public bool SteamIdsDisagree { get; }

        /// <summary>
        /// How many words the two names share as one unbroken run. Used only to order candidates:
        /// when several words of the same rarity feed one candidate list, the pair whose shared run
        /// covers more of both names is the stronger claim, and the whole-name similarity cannot say
        /// that (a listing "Morrowind" inside "The Elder Scrolls III: Morrowind GOTY Edition" scores
        /// 0.20 and is the certain pair).
        /// </summary>
        public int SharedTokenCount { get; }

        /// <summary>
        /// How many words sit outside that run on either side. Second ordering key: same shared run,
        /// fewer leftover words is the tighter claim on the pair.
        /// </summary>
        public int LeftoverTokenCount { get; }

        internal VrModMatchSuggestion(Game game, string sourceGameName, string reason,
            TokenRunKind kind, float displayScore, bool steamIdsDisagree = false,
            int sharedTokenCount = 0, int leftoverTokenCount = 0)
        {
            Game = game ?? throw new ArgumentNullException(nameof(game));
            SourceGameName = sourceGameName ?? string.Empty;
            Reason = reason ?? string.Empty;
            Kind = kind;
            DisplayScore = displayScore;
            SteamIdsDisagree = steamIdsDisagree;
            SharedTokenCount = sharedTokenCount;
            LeftoverTokenCount = leftoverTokenCount;
        }
    }
}
