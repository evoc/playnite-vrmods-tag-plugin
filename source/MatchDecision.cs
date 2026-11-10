namespace Playnite.VRMods
{
    /// <summary>
    /// Represents a user's decision about a game name match between a VR mod source and a Playnite game.
    ///
    /// The matching pipeline finds potential matches between external VR mod entries and games in the
    /// user's Playnite library. Since fuzzy matching can produce false positives, each match starts
    /// as "Pending" and requires user confirmation. Once confirmed (Accepted) or rejected (Rejected),
    /// the decision is saved and the match is handled automatically on future scans.
    /// </summary>
    public enum MatchDecision
    {
        /// <summary>
        /// No decision has been made yet: the user has never been asked about this pairing, so it is
        /// never applied - not by a scan that shows the review dialog, and not by one that does not.
        /// A scan without a dialog holds these pairings and counts them in its summary; they wait until
        /// a dialog offers them. Exact-name pairings arrive there pre-ticked as a yes, which is a
        /// prefilled answer, not an application.
        /// </summary>
        Pending,

        /// <summary>
        /// The user has confirmed this match is correct. On future scans, this match
        /// will be automatically applied without showing the review dialog.
        /// </summary>
        Accepted,

        /// <summary>
        /// The user has rejected this match as incorrect. On future scans, this match
        /// will be automatically skipped without showing the review dialog.
        /// </summary>
        Rejected
    }
}
