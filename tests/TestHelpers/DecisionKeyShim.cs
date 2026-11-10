using System;
using System.Linq;
using System.Threading.Tasks;
using Playnite.SDK.Models;
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

        /// <summary>
        /// Records the user saying yes to every listing a test source publishes, for the given games.
        ///
        /// A scan puts a tag on a game for one reason: the user accepted that pairing. A test that
        /// asserts tags therefore has to leave that state behind - the same state the review dialog
        /// leaves in a real library, and the state "did the plugin pair these two records?" is really
        /// asking about, since the plugin consults a decision only for a pairing its match rules
        /// already produced. The source id comes from the source and the identity from the listing, so
        /// the seeded key is the key the scan looks for even when the listing carries an App ID or a
        /// page URL.
        /// </summary>
        public static void AcceptListingsFrom(this VrModTagSettings settings, BaseVrModParser source,
            params Guid[] gameIds)
        {
            foreach (var listing in source.FetchEntriesAsync().GetAwaiter().GetResult())
                foreach (var gameId in gameIds)
                    settings.SetDecisionForListing(listing, source.SourceId, gameId, MatchDecision.Accepted);
        }

        /// <summary>
        /// Records the user saying yes to ONE pairing: the listing a source publishes under
        /// <paramref name="listingName"/>, against the games named here.
        ///
        /// <see cref="AcceptListingsFrom"/> accepts every listing of a source for every game listed, and
        /// for a multi-listing source that seeds pairings no test means: accepting the 'Resident Evil 7'
        /// listing against the 'Resident Evil 3' game is a second question, and answering it puts a third
        /// tag on that game - which a test counting tags then reads as a bug in the plugin.
        /// </summary>
        public static void AcceptListing(this VrModTagSettings settings, BaseVrModParser source,
            string listingName, params Guid[] gameIds)
        {
            var listing = source.FetchEntriesAsync().GetAwaiter().GetResult()
                .Single(e => string.Equals(e.GameName, listingName, StringComparison.OrdinalIgnoreCase));
            foreach (var gameId in gameIds)
                settings.SetDecisionForListing(listing, source.SourceId, gameId, MatchDecision.Accepted);
        }
    }
}
