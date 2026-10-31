using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Every regex in this plugin runs over text that came from somebody else's website, on a
    /// Playnite scan thread. A pattern that turns quadratic — because a page is laid out in a way
    /// nobody tested, or because someone edited the pattern — is indistinguishable from Playnite
    /// hanging unless the match has a ceiling. These tests pin that ceiling: a pathological input
    /// must fail bounded, and a realistic multi-megabyte page must still finish well inside it.
    /// </summary>
    public class ScannedRegexTests
    {
        /// <summary>
        /// Exposes <see cref="BaseVrModParser"/>'s protected scanned-content regex helpers so the
        /// ceiling can be tested directly; a parser subclass is how the rest of the test suite
        /// reaches that surface.
        /// </summary>
        private sealed class RegexHarnessParser : BaseVrModParser
        {
            public RegexHarnessParser() : base("regex-harness", "Regex Harness", "https://example.com/") { }

            public override string? ChangeIdentifier => null;
            public override string? GetCanonicalName(VrModEntry entry) => entry.GameName;

            public static Match MatchIt(string input, string pattern) => MatchScanned(input, pattern);
            public static MatchCollection MatchAll(string input, string pattern) => MatchesScanned(input, pattern);
            public static string ReplaceIt(string input, string pattern, string replacement) => ReplaceScanned(input, pattern, replacement);
        }

        [Test]
        public void TheScannedRegexCeilingIsFiniteAndShortEnoughToMatter()
        {
            var timeout = BaseVrModParser.ScannedRegexTimeout;

            Assert.That(timeout > TimeSpan.Zero, Is.True);
            Assert.That(timeout <= TimeSpan.FromSeconds(10), Is.True,
                "a ceiling longer than 10 s is a frozen Playnite with extra steps");
        }

        [Test]
        public void ACatastrophicPatternOverScannedTextTimesOutInsteadOfRunningForever()
        {
            // The textbook exponential-backtracking shape: (a+)+$ against a run of 'a' that ends in
            // a non-matching character explores 2^n partitions of the run. Without a match timeout
            // this call does not return in the lifetime of the session.
            var input = new string('a', 45) + "!";
            var watch = Stopwatch.StartNew();

            var thrown = Assert.Catch(() => RegexHarnessParser.MatchIt(input, @"(a+)+$"));

            watch.Stop();
            Assert.That(thrown, Is.InstanceOf<RegexMatchTimeoutException>(),
                "the ceiling must surface as a timeout the scan can report, not as a hung thread");
            Assert.That(watch.Elapsed, Is.LessThan(BaseVrModParser.ScannedRegexTimeout + TimeSpan.FromSeconds(5)),
                $"the match kept running {watch.Elapsed.TotalSeconds:F1} s past its ceiling");
        }

        [Test]
        public void TheCeilingAlsoAppliesToReplaceAndToTheLazyMatchesEnumeration()
        {
            var input = new string('a', 45) + "!";

            Assert.That(Assert.Catch(() => RegexHarnessParser.ReplaceIt(input, @"(a+)+b", "x")),
                Is.InstanceOf<RegexMatchTimeoutException>());

            // Matches() is lazy: the timeout has to surface while the caller is enumerating, not
            // only when the batch is started.
            var thrown = Assert.Catch(() =>
            {
                var count = 0;
                foreach (Match ignored in RegexHarnessParser.MatchAll(input, @"(a+)+$"))
                    count++;
                TestContext.Error.WriteLine(count);
            });
            Assert.That(thrown, Is.InstanceOf<RegexMatchTimeoutException>());
        }

        [Test]
        public void ARealisticMultiMegabytePageStillParsesWellInsideTheCeiling()
        {
            // ~5000 cards is more than any source in this plugin serves; if the ceiling were too
            // tight for real work, the fix would be a hang disguised as a safety measure.
            var html = new StringBuilder(4 * 1024 * 1024);
            html.Append("<html><body>");
            for (var i = 0; i < 5000; i++)
                html.Append("<div class=\"game-card\"><h3>Some VR Game ").Append(i)
                    .Append("</h3><a class=\"card-image-link\" href=\"/game/").Append(i)
                    .Append("\"></a><a title=\"Steam VR\" href=\"https://store.steampowered.com/app/").Append(i)
                    .Append("/\"></a></div>");
            html.Append("</body></html>");

            var watch = Stopwatch.StartNew();
            var cards = RegexHarnessParser.MatchAll(html.ToString(), @"<div\s+class=""game-card""");
            var found = 0;
            foreach (Match ignored in cards)
                found++;
            watch.Stop();

            Assert.That(found, Is.EqualTo(5000));
            Assert.That(watch.Elapsed, Is.LessThan(BaseVrModParser.ScannedRegexTimeout),
                $"a page this size took {watch.Elapsed.TotalSeconds:F1} s of regex time");
        }

        [Test]
        public void ARealParserRunOverAnOversizedPathologicalPageDoesNotRunUnbounded()
        {
            // A page that is 8 MB of near-matching markup: unclosed tags and repeated card openings
            // with no closing structure. The parser must return something bounded — either few cards
            // quickly, or a timeout the scan reports as a failed source.
            var junk = new StringBuilder(8 * 1024 * 1024);
            for (var i = 0; i < 200000; i++)
                junk.Append("<div class=\"game-card\"><h3><span>");
            var html = junk.ToString();

            var watch = Stopwatch.StartNew();
            var parser = new Flat2VrParser();
            var cards = parser.ParseGameCards(html);
            watch.Stop();

            Assert.That(cards.Count, Is.LessThanOrEqualTo(200000));
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)),
                $"parsing an 8 MB pathological page took {watch.Elapsed.TotalSeconds:F1} s");
        }
    }
}
