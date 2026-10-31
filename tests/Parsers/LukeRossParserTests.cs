using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    [TestFixture]
    public class LukeRossParserTests
    {
        [Test]
        public void SourceId_ReturnsLukeRoss()
        {
            var parser = new LukeRossParser();
            Assert.That(parser.SourceId, Is.EqualTo("LukeRoss"));
        }

        [Test]
        public void DisplayName_ReturnsCorrectName()
        {
            var parser = new LukeRossParser();
            Assert.That(parser.DisplayName, Is.EqualTo("Luke Ross (R.E.A.L. VR)"));
        }

        [Test]
        public void IsEnabled_BaseDefaultsToTrue()
        {
            // The base parser defaults to enabled; the DISABLED-by-default behavior lives in
            // the settings layer (EnableLukeRoss defaults to false), applied via the registry.
            var parser = new LukeRossParser();
            Assert.That(parser.IsEnabled, Is.True);
        }

        [Test]
        public void ChangeIdentifier_IsStableAndNonNull()
        {
            // The static list never changes at runtime, so the identifier is a constant.
            var a = new LukeRossParser();
            var b = new LukeRossParser();
            Assert.That(a.ChangeIdentifier, Is.Not.Null);
            Assert.That(a.ChangeIdentifier, Is.EqualTo(b.ChangeIdentifier));
        }

        [Test]
        public void GetCanonicalName_ReturnsGameName()
        {
            var parser = new LukeRossParser();
            var entry = new VrModEntry { GameName = "Elden Ring" };
            Assert.That(parser.GetCanonicalName(entry), Is.EqualTo("Elden Ring"));
        }

        [Test]
        public async Task FetchEntries_ReturnsKnownGames()
        {
            var parser = new LukeRossParser();
            var result = (await parser.FetchEntriesAsync()).ToList();

            Assert.That(result, Has.Count.EqualTo(36));
            Assert.That(
                result.Select(e => e.GameName),
                Is.EquivalentTo(new[]
                {
                    "Atomic Heart",
                    "Avatar: Frontiers of Pandora",
                    "Dark Souls: Remastered",
                    "Dark Souls II: Scholar of the First Sin",
                    "Dark Souls III",
                    "Death Stranding Director's Cut",
                    "DOOM: Eternal",
                    "DOOM: The Dark Ages",
                    "Elden Ring",
                    "Far Cry 4",
                    "Far Cry 5",
                    "Far Cry 6",
                    "Far Cry New Dawn",
                    "Far Cry Primal",
                    "Final Fantasy VII Remake Intergrade",
                    "Final Fantasy VII Rebirth",
                    "Ghost of Tsushima",
                    "Ghostrunner",
                    "Ghostwire: Tokyo",
                    "Grounded",
                    "High On Life",
                    "Hogwarts Legacy",
                    "Horizon Zero Dawn",
                    "Horizon Forbidden West",
                    "Indiana Jones and the Great Circle",
                    "Kingdom Come: Deliverance II",
                    "Marvel's Spider-Man Remastered",
                    "Marvel's Spider-Man: Miles Morales",
                    "Marvel's Spider-Man 2",
                    "Star Wars Outlaws",
                    "Stray",
                    "Uncharted 4: A Thief's End",
                    "Watch Dogs",
                    "Watch Dogs 2",
                    "Watch Dogs: Legion",
                    "Crimson Desert"
                }));
        }

        [Test]
        public async Task FetchEntries_ExcludesPulledAndEarlyAccessTitles()
        {
            // Locks in the DELIBERATE omissions from the snapshot: the titles Luke Ross has
            // PULLED (Take-Two DMCA: GTA V / RDR2 / Mafia DE; Cyberpunk 2077 after its Jan 2026
            // DMCA) and the early-access (paid) only titles not in the free public release
            // (Baldur's Gate 3, Days Gone). Tagging any of these would claim a mod Luke Ross no
            // longer ships (or one not in the free release). If a future sync re-adds one on
            // purpose, update this test and the KnownGames remarks together.
            var parser = new LukeRossParser();
            var names = (await parser.FetchEntriesAsync()).Select(e => e.GameName).ToList();

            string[] omitted =
            {
                "Grand Theft Auto V",
                "Red Dead Redemption 2",
                "Mafia: Definitive Edition",
                "Cyberpunk 2077",
                "Baldur's Gate 3",
                "Days Gone"
            };

            foreach (string omittedGame in omitted)
                Assert.That(names, Is.Not.EqualTo(omittedGame), "pulled/early-access title must not be tagged");
        }

        [Test]
        public async Task FetchEntries_SetsLukeRossCategory()
        {
            var parser = new LukeRossParser();
            var result = (await parser.FetchEntriesAsync()).ToList();

            Assert.That(result, Is.Not.Empty);
            foreach (var entry in result)
                Assert.That(entry.Category, Is.EqualTo("LukeRoss"));
        }
    }
}
