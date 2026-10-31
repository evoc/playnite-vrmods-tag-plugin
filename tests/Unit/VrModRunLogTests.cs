using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for the per-source run log shown in the settings "Last run" dialog:
    /// the base parser's <c>LogRun</c>/<c>Snip</c> helpers (append, no-op without a run,
    /// truncation cap) and the parsers' extraction-loop logging — every game row that
    /// fails to parse is named in the log, and each parse ends with a parsed-count
    /// summary, so a 0-entry run is diagnosable from the run itself.
    /// </summary>
    [TestFixture]
    public class VrModRunLogTests
    {
        /// <summary>A minimal concrete parser exposing the protected run-log helpers.</summary>
        private class RunLogParser : BaseVrModParser
        {
            public RunLogParser() : base("runlog-test", "RunLog", "https://example.com/")
            {
            }

            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() =>
                Task.FromResult<IEnumerable<VrModEntry>>(Array.Empty<VrModEntry>());
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);

            public void LogOnce(string line) => LogRun(line);
            public static string SnipIt(string? text) => Snip(text);
        }

        [Test]
        public void LogRun_NullRunLog_IsNoOp()
        {
            var parser = new RunLogParser();
            Assert.That(parser.RunLog, Is.Null);

            parser.LogOnce("hello"); // parser invoked outside a scan: must not throw

            Assert.That(parser.RunLog, Is.Null);
        }

        [Test]
        public void LogRun_AppendsLinesInOrder()
        {
            var parser = new RunLogParser();
            parser.RunLog = new List<string>();
            parser.LogOnce("first");
            parser.LogOnce("second");

            Assert.That(parser.RunLog, Has.Count.EqualTo(2));
            Assert.That(parser.RunLog[0], Is.EqualTo("first"));
            Assert.That(parser.RunLog[1], Is.EqualTo("second"));
        }

        [Test]
        public void LogRun_OverCap_AddsSingleTruncationMarker()
        {
            var parser = new RunLogParser();
            parser.RunLog = new List<string>();
            for (var i = 0; i < BaseVrModParser.MaxRunLogLines + 50; i++)
                parser.LogOnce($"line {i}");

            // The cap is reached, one truncation marker is appended, and the rest is dropped.
            Assert.That(parser.RunLog, Has.Count.EqualTo(BaseVrModParser.MaxRunLogLines + 1));
            Assert.That(parser.RunLog[parser.RunLog.Count - 1], Does.StartWith("... log truncated"));
        }

        [Test]
        public void Snip_ShortTextUnchanged_LongTextTruncated()
        {
            Assert.That(RunLogParser.SnipIt("hello"), Is.EqualTo("hello"));

            var snipped = RunLogParser.SnipIt(new string('x', 250));
            Assert.That(snipped.Length, Is.EqualTo(103)); // 100 chars + "..."
            Assert.That(snipped, Does.EndWith("..."));
        }

        [Test]
        public void Flat2Vr_ParseGameCards_LogsSkippedCardsAndSummary()
        {
            var parser = new Flat2VrParser();
            parser.RunLog = new List<string>();

            var html = @"
                <div class=""game-card""><h3>Cyberpunk 2077</h3></div>
                <div class=""game-card""><div>no title in this card</div></div>";
            var entries = parser.ParseGameCards(html);

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].GameName, Is.EqualTo("Cyberpunk 2077 VR"));
            // The broken card is named in the log, and the run ends with a summary line.
            Assert.That(parser.RunLog.Any(l => l.StartsWith("Game card skipped (no <h3> title")), Is.True);
            Assert.That(parser.RunLog, Does.Contain("Parsed 1 game cards (1 skipped)"));
        }

        [Test]
        public void MrNlceHub_ParseSupportedGames_MissingBlock_LogsLayoutChange()
        {
            var parser = new MrNlceHubParser();
            parser.RunLog = new List<string>();

            var entries = parser.ParseSupportedGames("# PCVR Mods Installer Hub\n\na readme without the supported games block");

            Assert.That(entries, Is.Empty);
            Assert.That(parser.RunLog, Does.Contain(
                "No 'listed titles' block in the README - the repo layout may have changed"));
        }

        [Test]
        public void MrNlceHub_ParseSupportedGames_LogsSkippedBulletsAndCount()
        {
            var parser = new MrNlceHubParser();
            parser.RunLog = new List<string>();

            var readme = "# supported\nlisted titles\n* Hades II\n* (Dolphin VR + Redux)\n* Hades II\n";
            var entries = parser.ParseSupportedGames(readme);

            // "Hades II" is deduped to one entry; the parenthesised sub-entry is skipped and named.
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].GameName, Is.EqualTo("Hades II"));
            Assert.That(parser.RunLog.Any(l => l.StartsWith("Bullet skipped (parenthesised sub-entry)")), Is.True);
            Assert.That(parser.RunLog, Does.Contain("Parsed 1 supported titles (1 bullets skipped)"));
        }

        [Test]
        public void RototRobot_ParseRawMarkdown_LoggedEvenForZeroEntries()
        {
            var parser = new RototRobotVrModParser();
            parser.RunLog = new List<string>();

            var entries = parser.ParseRawMarkdown("no categories at all\njust some text\n");

            // A 0-entry parse must still end with the summary line, so the settings dialog
            // shows *why* the run produced nothing instead of a silent empty list.
            Assert.That(entries, Is.Empty);
            Assert.That(parser.RunLog, Does.Contain("Parsed 0 entries from the raw markdown"));
        }
    }
}
