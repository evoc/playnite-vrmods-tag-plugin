using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// PCVR Central reads its catalog out of a Next.js flight payload embedded in page markup: the
    /// games array is located by bracket matching, then handed to a JSON reader. A site redeploy
    /// therefore arrives as malformed JSON, or as JSON of a shape this parser never saw. Both have to
    /// end as "no games from this source this run" with the cause named in the run log - never as an
    /// exception leaving the parser (the source would be reported FAILED with a stack trace no user
    /// reads), and never as a silent nothing that looks like an empty site.
    /// </summary>
    [TestFixture]
    public class PcvrCentralParseResilienceTests
    {
        /// <summary>
        /// Wraps payload content in the flight-chunk form <see cref="PcvrCentralParser"/> extracts,
        /// escaping it the way the site escapes the JSON inside its own chunk.
        /// </summary>
        private static string FlightWith(string decodedContent)
        {
            string escaped = decodedContent.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "self.__next_f.push([1,\"" + escaped + "\"])";
        }

        private static PcvrCentralParser ParserWithRunLog(out List<string> log)
        {
            var parser = new PcvrCentralParser();
            log = new List<string>();
            parser.RunLog = log;
            return parser;
        }

        [Test]
        public void MalformedGamesArray_EndsAsNoDataAndNamesTheCause()
        {
            List<string> log;
            var parser = ParserWithRunLog(out log);
            string payload = FlightWith("\"games\":[{\"title\":\"A Site Redeploy\",}]");

            var entries = parser.ParseCatalog("<html><script>" + payload + "</script></html>");

            Assert.That(entries, Is.Empty);
            Assert.That(
                log.Any(l => l.Contains("Malformed JSON in the games array")),
                Is.True,
                string.Join(" | ", log));
            Assert.That(
                log.Any(l => l.Contains("no games parsed (") && l.Contains("Exception")),
                Is.True,
                "the run log has to name the failure type: 'the page changed' and 'our parser is wrong' are otherwise identical");
        }

        [Test]
        public void GamesArrayOfUnexpectedItemShape_DoesNotAbortTheParse()
        {
            List<string> log;
            var parser = ParserWithRunLog(out log);
            string inner = "\"games\":[\"a string where an object belongs\",42,{\"title\":\"Good Game\",\"slug\":\"good-game\"}]";
            string payload = FlightWith(inner);

            var entries = parser.ParseCatalog("<html><script>" + payload + "</script></html>");

            // The two unusable items are skipped; the one real game object still becomes an entry.
            Assert.That(entries.Count, Is.EqualTo(1), string.Join(" | ", log));
            Assert.That(
                log.Any(l => l.Contains("2 games skipped (not an object in the games array)")),
                Is.True,
                string.Join(" | ", log));
        }
    }
}
