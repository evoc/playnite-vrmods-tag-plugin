using System;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.TestHelpers
{
    /// <summary>
    /// The decision-key shape a test seeds decisions with.
    ///
    /// Production never builds a decision key from a listing's display name: a mapping's key comes
    /// from <see cref="VrModEntry.MakeDecisionKey(string, Guid)"/>, whose identity half is the
    /// listing's Steam App ID, else its page URL, else - only when the site offers nothing else -
    /// its name. The settings store is keyed by that value, so a test that wants a decision to be
    /// visible to a scan has to seed the key the scan will look for.
    ///
    /// <see cref="Named"/> is the shape a fixture without an App ID or a page URL produces (the
    /// entry's identity component is its name, so the name key IS the mapping's key). A fixture
    /// whose listing does carry an App ID or a page URL must seed through <see cref="ForListing"/>:
    /// seeding such a fixture by name writes a decision the plugin no longer reads, and the test
    /// passes for the wrong reason.
    /// </summary>
    public static class DecisionKeys
    {
        /// <summary>Key of a listing whose identity half is its display name.</summary>
        public static string Named(string sourceId, string listingName, Guid gameId)
            => VrModNameMatcher.MakeDecisionKey(sourceId, listingName, gameId);

        /// <summary>Key of a listing as the plugin itself computes it, identity included.</summary>
        public static string ForListing(VrModEntry listing, string sourceId, Guid gameId)
            => listing.MakeDecisionKey(sourceId, gameId);
    }

    /// <summary>
    /// (sourceId, listing name, game Id) spellings of the settings decision calls, for tests that
    /// describe a mapping by name. Thin forwarders onto the key-shaped API - they add no behaviour.
    /// </summary>
    public static class DecisionShim
    {
        public static void SetDecision(this VrModTagSettings settings, string sourceId,
            string listingName, Guid gameId, MatchDecision decision, string? sourceTags = null)
            => settings.SetDecisionByKey(DecisionKeys.Named(sourceId, listingName, gameId),
                decision, sourceTags);

        public static MatchDecision GetDecision(this VrModTagSettings settings, string sourceId,
            string listingName, Guid gameId)
            => settings.GetDecisionByKey(DecisionKeys.Named(sourceId, listingName, gameId));

        public static bool TryGetDecision(this VrModTagSettings settings, string sourceId,
            string listingName, Guid gameId, out MatchDecision decision)
            => settings.TryGetDecisionByKey(DecisionKeys.Named(sourceId, listingName, gameId),
                out decision);

        public static void ClearDecision(this VrModTagSettings settings, string sourceId,
            string listingName, Guid gameId)
            => settings.ClearDecisionByKey(DecisionKeys.Named(sourceId, listingName, gameId));

        /// <summary>Same calls, keyed by the listing itself instead of by its name.</summary>
        public static void SetDecisionForListing(this VrModTagSettings settings, VrModEntry listing,
            string sourceId, Guid gameId, MatchDecision decision, string? sourceTags = null)
            => settings.SetDecisionByKey(DecisionKeys.ForListing(listing, sourceId, gameId),
                decision, sourceTags);

        public static MatchDecision GetDecisionForListing(this VrModTagSettings settings,
            VrModEntry listing, string sourceId, Guid gameId)
            => settings.GetDecisionByKey(DecisionKeys.ForListing(listing, sourceId, gameId));

        public static void ClearDecisionForListing(this VrModTagSettings settings, VrModEntry listing,
            string sourceId, Guid gameId)
            => settings.ClearDecisionByKey(DecisionKeys.ForListing(listing, sourceId, gameId));
    }
}
