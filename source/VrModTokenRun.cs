using System;
using System.Collections.Generic;

namespace Playnite.VRMods
{
    /// <summary>
    /// Verdict of <see cref="VrModNameMatcher.CompareTokenRuns(string, string)"/>: what the fact
    /// that one name's words appear as one unbroken run inside the other name says about the pair.
    /// </summary>
    public enum TokenRunKind
    {
        /// <summary>
        /// No run long enough to go on. Nothing is claimed about the pair.
        /// </summary>
        Unrelated,

        /// <summary>
        /// A leftover word is a series ordinal that the other name does not carry, so the two names
        /// are entries in the same series rather than one product written twice (see
        /// <see cref="VrModNameMatcher.CompareTokenRuns(string, string)"/>).
        /// </summary>
        DifferentProduct,

        /// <summary>
        /// One name's words are the whole of the other's run, and everything left over is wording
        /// that agrees rather than a title: edition/packaging wording ("GOTY Edition", "Complete
        /// Edition", "BFG Edition"), or an ordinal the other name carries as well ("Alan Wake 2"
        /// against "Alan Wake II").
        /// </summary>
        SameProduct,

        /// <summary>
        /// One name's words run inside the other's, but what is left over is real wording
        /// ("Legends", "Goodies Collection"), which a name rule cannot decide the meaning of.
        /// </summary>
        NeedsALook
    }

    /// <summary>
    /// The outcome of comparing two names by token runs, with the leftover tokens named so a
    /// reason can be shown to the user. Produced only by
    /// <see cref="VrModNameMatcher.CompareTokenRuns(string, string)"/>.
    /// </summary>
    public sealed class TokenRunResult
    {
        /// <summary>The verdict.</summary>
        public TokenRunKind Kind { get; }

        /// <summary>Length of the shared contiguous token run (0 when there is none).</summary>
        public int SharedTokenCount { get; }

        /// <summary>Token count of the source-catalog listing name.</summary>
        public int SourceTokenCount { get; }

        /// <summary>Token count of the library game name.</summary>
        public int GameTokenCount { get; }

        /// <summary>
        /// Listing tokens with no place in the shared run — words the listing says that the game
        /// name does not.
        /// </summary>
        public IReadOnlyList<string> ExtraSourceTokens { get; }

        /// <summary>
        /// Game tokens with no place in the shared run — words the game name says that the listing
        /// does not.
        /// </summary>
        public IReadOnlyList<string> ExtraGameTokens { get; }

        /// <summary>
        /// True when the listing name is fully carried by the game name (no listing token is left
        /// out), which is the shape of a listing that names a game shorter than the library does:
        /// "Morrowind" inside "The Elder Scrolls III: Morrowind GOTY Edition".
        /// </summary>
        public bool SourceIsCarriedByGame => ExtraSourceTokens.Count == 0;

        /// <summary>True when the game name is fully carried by the listing name.</summary>
        public bool GameIsCarriedBySource => ExtraGameTokens.Count == 0;

        internal TokenRunResult(TokenRunKind kind, int sharedTokenCount, int sourceTokenCount,
            int gameTokenCount, IReadOnlyList<string> extraSourceTokens, IReadOnlyList<string> extraGameTokens)
        {
            Kind = kind;
            SharedTokenCount = sharedTokenCount;
            SourceTokenCount = sourceTokenCount;
            GameTokenCount = gameTokenCount;
            ExtraSourceTokens = extraSourceTokens ?? Array.Empty<string>();
            ExtraGameTokens = extraGameTokens ?? Array.Empty<string>();
        }

        internal static TokenRunResult Unrelated(IReadOnlyList<string> sourceTokens, IReadOnlyList<string> gameTokens)
            => new TokenRunResult(TokenRunKind.Unrelated, 0, sourceTokens.Count, gameTokens.Count,
                Array.Empty<string>(), Array.Empty<string>());
    }
}
