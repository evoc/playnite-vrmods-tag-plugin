using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// The UeVR parser scrapes its Firebase API key and project id out of the site's JS bundle and
    /// then interpolates them into a Firestore request — the project id into the request PATH, the
    /// key into <c>key=</c>. Those values are fetched data, not configuration: whoever controls the
    /// bundle would otherwise control which Firestore project/collection the plugin reads, and that
    /// data is what later becomes game names and Links. These tests pin the shape validation that
    /// stands between the bundle and the request, including shapes a normal web page never contains.
    /// </summary>
    public class UeVrProfilesFirebaseConfigTests
    {
        // The values the site actually uses today, duplicated from the parser's pinned fallbacks
        // (FallbackFirebaseApiKey / FallbackProjectId) so a change to either shows up here.
        private const string PinnedApiKey = "AIzaSyAQYgGp-skpNcm6Mzh5RLADYWnxsr22hRU";
        private const string PinnedProjectId = "uevrprofiles";

        private static string Bundle(string apiKey, string projectId)
            => $"const firebaseConfig={{apiKey:\"{apiKey}\",projectId:\"{projectId}\",storageBucket:\"x\"}};";

        [Test]
        public void ARealFirebaseConfigIsAcceptedAsIs()
        {
            var scraped = UeVrProfilesParser.ParseFirebaseConfigFromBundle(Bundle(PinnedApiKey, PinnedProjectId));

            Assert.That(scraped.ApiKeySeen, Is.True);
            Assert.That(scraped.ProjectIdSeen, Is.True);
            Assert.That(scraped.ApiKey, Is.EqualTo(PinnedApiKey));
            Assert.That(scraped.ProjectId, Is.EqualTo(PinnedProjectId));
            Assert.That(scraped.RejectedApiKey, Is.Null);
            Assert.That(scraped.RejectedProjectId, Is.Null);
        }

        [Test]
        public void ThePinnedFallbackValuesPassTheSameValidationTheyAreCheckedAgainst()
        {
            // Otherwise a "rejected -> keep the fallback" path would reject its own fallback.
            Assert.That(UeVrProfilesParser.IsUsableFirebaseApiKey(PinnedApiKey), Is.True);
            Assert.That(UeVrProfilesParser.IsUsableFirebaseProjectId(PinnedProjectId), Is.True);
        }

        [Test]
        public void ABundleWithoutAnyFirebaseConfigClaimsNothing()
        {
            var scraped = UeVrProfilesParser.ParseFirebaseConfigFromBundle("<html><script>var unrelated = 1;</script></html>");

            Assert.That(scraped.ApiKeySeen, Is.False);
            Assert.That(scraped.ProjectIdSeen, Is.False);
            Assert.That(scraped.ApiKey, Is.Null);
            Assert.That(scraped.ProjectId, Is.Null);
        }

        [Test]
        public void AnEmptyOrMissingBundleClaimsNothing()
        {
            Assert.That(UeVrProfilesParser.ParseFirebaseConfigFromBundle(string.Empty).ApiKey, Is.Null);
            Assert.That(UeVrProfilesParser.ParseFirebaseConfigFromBundle(null).ProjectId, Is.Null);
        }

        [Test]
        public void OnlyTheFirstClaimOfEachKindIsUsed()
        {
            // The real site lists one config; a hostile bundle can list several. The last claim must
            // not win over the first one.
            var bundle = Bundle(PinnedApiKey, PinnedProjectId)
                + Bundle("AIzaSyBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", "attackerproject");

            var scraped = UeVrProfilesParser.ParseFirebaseConfigFromBundle(bundle);

            Assert.That(scraped.ApiKey, Is.EqualTo(PinnedApiKey));
            Assert.That(scraped.ProjectId, Is.EqualTo(PinnedProjectId));
        }

        // Shapes that would move the Firestore request somewhere else: the project id is interpolated
        // raw into /v1/projects/<id>/databases/(default)/documents/games?alt=json&key=…
        [TestCase("uevrprofiles/../otherproject")]
        [TestCase("uevrprofiles/../../../v1/projects/victim")]
        [TestCase("uevrprofiles/databases/other")]
        [TestCase("uevrprofiles?alt=json&key=stolen")]
        [TestCase("uevrprofiles#x")]
        [TestCase("uevrprofiles%2F..")]
        [TestCase("uevrprofiles x")]
        [TestCase("uevrprofiles\nx")]
        [TestCase("UEVRProfiles")]
        [TestCase("-uevrprofiles")]
        [TestCase("uevrprofiles-")]
        [TestCase(".")]
        [TestCase("..")]
        [TestCase("x")]
        public void AProjectIdThatIsNotAProjectIdIsRejectedAndNeverUsed(string claimed)
        {
            var scraped = UeVrProfilesParser.ParseFirebaseConfigFromBundle(Bundle(PinnedApiKey, claimed));

            Assert.That(scraped.ProjectIdSeen, Is.True, "the bundle did claim this, so the rejection must be visible");
            Assert.That(scraped.RejectedProjectId, Is.EqualTo(claimed));
            Assert.That(scraped.ProjectId, Is.Null, "a rejected project id must never reach the request path");
            Assert.That(UeVrProfilesParser.IsUsableFirebaseProjectId(claimed), Is.False);
        }

        [Test]
        public void AnEmptyQuotedProjectIdIsNotEvenAClaim()
        {
            var scraped = UeVrProfilesParser.ParseFirebaseConfigFromBundle(Bundle(PinnedApiKey, string.Empty));

            Assert.That(scraped.ProjectIdSeen, Is.False);
            Assert.That(scraped.ProjectId, Is.Null);
        }

        [Test]
        public void AnOverlongClaimedProjectIdIsRejected()
        {
            var claimed = "a" + new string('b', 5000) + "c";

            var scraped = UeVrProfilesParser.ParseFirebaseConfigFromBundle(Bundle(PinnedApiKey, claimed));

            Assert.That(scraped.ProjectId, Is.Null);
            Assert.That(UeVrProfilesParser.IsUsableFirebaseProjectId(claimed), Is.False);
        }

        // The key goes into ?…&key=<key>: a slash, space, ampersand, hash or dot in it would add
        // query parameters or change the request instead of staying inside one value.
        [TestCase("AIzaSyAQYgGp-skpNcm6Mzh5RLADYWnxsr22hRU&key=other")]
        [TestCase("AIzaSyAQYgGp-skpNcm6Mzh5RLADYWnxsr22hRU#fragment")]
        [TestCase("AIzaSyAQYgGp-skpNcm6Mzh5RLADYWnxsr22hRU?alt=cm")]
        [TestCase("AIzaSyAQYgGp-skpNcm6Mzh5RLADYWnxsr22hRU /../x")]
        [TestCase("AIza key with spaces")]
        [TestCase("AIzaSyAQYgGp-skpNcm6Mzh5RLADYWnxsr22hRU.")]
        [TestCase("short")]
        public void AnApiKeyThatIsNotAnApiKeyIsRejectedAndNeverUsed(string claimed)
        {
            var scraped = UeVrProfilesParser.ParseFirebaseConfigFromBundle(Bundle(claimed, PinnedProjectId));

            Assert.That(scraped.ApiKeySeen, Is.True);
            Assert.That(scraped.RejectedApiKey, Is.EqualTo(claimed));
            Assert.That(scraped.ApiKey, Is.Null, "a rejected key must never reach the request");
            Assert.That(UeVrProfilesParser.IsUsableFirebaseApiKey(claimed), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("uevrprofiles/../x")]
        [TestCase("https://firestore.googleapis.com/v1/projects/x")]
        public void TheShapeHelpersRejectWhatTheParserMustNeverInterpolate(string? candidate)
        {
            Assert.That(UeVrProfilesParser.IsUsableFirebaseProjectId(candidate), Is.False);
            Assert.That(UeVrProfilesParser.IsUsableFirebaseApiKey(candidate), Is.False);
        }
    }
}
