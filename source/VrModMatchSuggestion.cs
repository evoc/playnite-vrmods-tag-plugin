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

        internal VrModMatchSuggestion(Game game, string sourceGameName, string reason,
            TokenRunKind kind, float displayScore)
        {
            Game = game ?? throw new ArgumentNullException(nameof(game));
            SourceGameName = sourceGameName ?? string.Empty;
            Reason = reason ?? string.Empty;
            Kind = kind;
            DisplayScore = displayScore;
        }
    }
}
