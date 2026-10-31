using System.Text.Json;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    /// <summary>
    /// Tests for UeVrProfilesParser.GetNextPageToken — the pagination guard for the
    /// Firestore list loop — and for the MaxFirestorePages cap that bounds the loop.
    /// </summary>
    [TestFixture]
    public class UeVrProfilesPageTokenTests
    {
        private static JsonElement Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        [Test]
        public void GetNextPageToken_Present_ReturnsToken()
        {
            var root = Parse("{\"documents\":[],\"nextPageToken\":\"abc123\"}");
            Assert.That(UeVrProfilesParser.GetNextPageToken(root), Is.EqualTo("abc123"));
        }

        [Test]
        public void GetNextPageToken_Absent_ReturnsNull()
        {
            var root = Parse("{\"documents\":[]}");
            Assert.That(UeVrProfilesParser.GetNextPageToken(root), Is.Null);
        }

        [Test]
        public void GetNextPageToken_Empty_ReturnsNull()
        {
            // An empty token would otherwise re-request the first page forever.
            var root = Parse("{\"documents\":[],\"nextPageToken\":\"\"}");
            Assert.That(UeVrProfilesParser.GetNextPageToken(root), Is.Null);
        }

        [Test]
        public void GetNextPageToken_NonString_ReturnsNull()
        {
            var root = Parse("{\"nextPageToken\":123}");
            Assert.That(UeVrProfilesParser.GetNextPageToken(root), Is.Null);
        }

        [Test]
        public void MaxFirestorePages_IsBounded()
        {
            // Guards against the cap being removed (unbounded pagination loop) or set so
            // low that a large collection could never be read.
            Assert.That(UeVrProfilesParser.MaxFirestorePages, Is.InRange(1, 100));
        }
    }
}
