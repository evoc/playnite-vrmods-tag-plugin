using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    /// <summary>
    /// Tests for the PCVR Central catalog parser.
    ///
    /// The parser decodes the server-rendered RSC payload inlined on the <c>/mods</c> page,
    /// finds the inlined <c>"games"</c> array, and produces one entry per game. The
    /// fixtures below build raw <c>/mods</c> HTML the same way Next.js emits it: one or more
    /// <c>self.__next_f.push([N,"&lt;js-escaped string&gt;")</c> chunks. The primary fixture
    /// splits the games array across TWO chunks to prove the parser concatenates the decoded
    /// payload before extracting the array.
    /// </summary>
    [TestFixture]
    public class PcvrCentralParserTests
    {
        /// <summary>
        /// Wraps one or more decoded RSC chunks into raw <c>/mods</c> HTML, escaping each
        /// chunk exactly as a JS string literal (the way Next.js streams it).
        /// </summary>
        private static string MakeRsc(params string[] decodedChunks)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < decodedChunks.Length; i++)
            {
                sb.Append("self.__next_f.push([1,\"").Append(JsEscape(decodedChunks[i])).Append("\")\n");
            }
            return sb.ToString();
        }

        /// <summary>JS-escapes a string for use as a JS string-literal body.</summary>
        private static string JsEscape(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '"')
                    sb.Append("\\\"");
                else if (c == '\\')
                    sb.Append("\\\\");
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        // --- Metadata / contract tests (unchanged by the /mods rewrite) ---

        [Test]
        public void SourceId_ReturnsPcvrCentral()
        {
            var parser = new PcvrCentralParser();
            Assert.That(parser.SourceId, Is.EqualTo("PcvrCentral"));
        }

        [Test]
        public void DisplayName_ReturnsCorrectName()
        {
            var parser = new PcvrCentralParser();
            Assert.That(parser.DisplayName, Is.EqualTo("PCVR Central"));
        }

        [Test]
        public void IsEnabled_DefaultsToTrue()
        {
            var parser = new PcvrCentralParser();
            Assert.That(parser.IsEnabled, Is.True);
        }

        [Test]
        public void ChangeIdentifier_ReturnsNullWhenNotFetched()
        {
            var parser = new PcvrCentralParser();
            Assert.That(parser.ChangeIdentifier, Is.Null);
        }

        [Test]
        public void GetCanonicalName_ReturnsGameName()
        {
            var parser = new PcvrCentralParser();
            var entry = new VrModEntry { GameName = "Resident Evil 4" };
            Assert.That(parser.GetCanonicalName(entry), Is.EqualTo("Resident Evil 4"));
        }

        // --- ParseCatalog tests ---

        [Test]
        public void ParseCatalog_TwoChunkConcatenation_ExtractsAllGames()
        {
            var parser = new PcvrCentralParser();

            // The games array is split across two chunks: chunk 1 opens the array (and even
            // carries a leading noise property), chunk 2 closes it (and a trailing one).
            // The parser must concatenate the decoded payload before extracting the array.
            string chunk1 = "{\"a\":\"b\",\"games\":[{\"slug\":\"alpha\",\"title\":\"Alpha One\",\"steamAppId\":111},";
            string chunk2 = "{\"slug\":\"beta\",\"title\":\"Beta Two\",\"steamAppId\":222}," +
                            "{\"slug\":\"gamma\",\"title\":\"Gamma [Limited]\",\"steamAppId\":333}]," +
                            "\"tail\":\"x\"}";

            var result = parser.ParseCatalog(MakeRsc(chunk1, chunk2));

            Assert.That(result, Has.Count.EqualTo(3));

            Assert.That(result[0].GameName, Is.EqualTo("Alpha One"));
            Assert.That(result[0].SteamAppId, Is.EqualTo("111"));
            Assert.That(result[0].Category, Is.EqualTo("PCVR Central"));
            Assert.That(result[0].SourceUrl, Is.EqualTo("https://pcvrcentral.com/games/alpha"));

            Assert.That(result[1].GameName, Is.EqualTo("Beta Two"));
            Assert.That(result[1].SteamAppId, Is.EqualTo("222"));

            // gamma's title contains [ ] brackets; they sit inside a JSON string and must not
            // confuse the bracket-matching that bounds the games array.
            Assert.That(result[2].GameName, Is.EqualTo("Gamma [Limited]"));
            Assert.That(result[2].SteamAppId, Is.EqualTo("333"));
        }

        [Test]
        public void ParseCatalog_SetsCategoryOnEveryEntry()
        {
            var parser = new PcvrCentralParser();
            string chunk = "{\"games\":[{\"slug\":\"a\",\"title\":\"A\"},{\"slug\":\"b\",\"title\":\"B\"}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            Assert.That(result, Has.Count.EqualTo(2));
            foreach (var entry in result)
                Assert.That(entry.Category, Is.EqualTo("PCVR Central"));
        }

        [Test]
        public void ParseCatalog_SkipsSteamAppId_WhenAbsent()
        {
            var parser = new PcvrCentralParser();
            string chunk = "{\"games\":[{\"slug\":\"nosid\",\"title\":\"No Sid Game\"}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("No Sid Game"));
            Assert.That(result[0].SteamAppId, Is.Null);
        }

        [Test]
        public void ParseCatalog_MissingOrNonNumericSteamAppId_LogsSingleSummaryLine()
        {
            var parser = new PcvrCentralParser();
            parser.RunLog = new List<string>();

            // One game with a usable numeric id, one with the site's "$undefined" string,
            // one with no steamAppId property at all: two of the three lack a steamid.
            string chunk = "{\"games\":[" +
                "{\"slug\":\"a\",\"title\":\"Has Sid\",\"steamAppId\":12345}," +
                "{\"slug\":\"b\",\"title\":\"Undefined Sid\",\"steamAppId\":\"$undefined\"}," +
                "{\"slug\":\"c\",\"title\":\"No Sid\"}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            Assert.That(result, Has.Count.EqualTo(3));
            Assert.That(result[0].SteamAppId, Is.EqualTo("12345"));
            Assert.That(result[1].SteamAppId, Is.Null);
            Assert.That(result[2].SteamAppId, Is.Null);

            // One collapsed line for all affected games (not one per game).
            Assert.That(parser.RunLog, Does.Contain("2 games without steamid"));
            Assert.That(string.Join("\n", parser.RunLog), Does.Not.Contain("steamAppId is not a number"));
        }

        [Test]
        public void ParseCatalog_FallsBackToTitle_WhenSlugMissing()
        {
            var parser = new PcvrCentralParser();
            string chunk = "{\"games\":[{\"title\":\"Slugless Game\"}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Slugless Game"));
            Assert.That(result[0].SourceUrl, Is.Null);
        }

        [Test]
        public void ParseCatalog_DeduplicatesBySlug_CaseInsensitive()
        {
            var parser = new PcvrCentralParser();
            // Two games that differ only by slug case must collapse to the first one.
            string chunk = "{\"games\":[{\"slug\":\"Dup\",\"title\":\"First Name\",\"steamAppId\":1}," +
                            "{\"slug\":\"dup\",\"title\":\"Second Name\"}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("First Name"));
            Assert.That(result[0].SteamAppId, Is.EqualTo("1"));
        }

        [Test]
        public void ParseCatalog_NoGamesArray_ReturnsEmpty()
        {
            var parser = new PcvrCentralParser();
            // Valid RSC chunk, but no "games" marker anywhere.
            string chunk = "{\"foo\":\"bar\",\"baz\":[1,2,3]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseCatalog_MalformedJson_ReturnsEmpty_NoThrow()
        {
            var parser = new PcvrCentralParser();
            // "games" marker present, brackets balanced, but the JSON inside is invalid:
            // the parser must fail safe to empty rather than throw.
            string chunk = "{\"games\":[{,}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseCatalog_TruncatedArray_ReturnsEmpty_NoThrow()
        {
            var parser = new PcvrCentralParser();
            // The games array is opened but never closed (unbalanced) — must fail safe.
            string chunk = "{\"games\":[{\"slug\":\"x\",\"title\":\"X\",\"steamAppId\":";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseCatalog_NoRscChunks_ReturnsEmpty()
        {
            var parser = new PcvrCentralParser();
            // Plain HTML with no self.__next_f.push at all.
            var result = parser.ParseCatalog("<html><body>no rsc here</body></html>");

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseCatalog_EmptyOrBlank_ReturnsEmpty()
        {
            var parser = new PcvrCentralParser();
            Assert.That(parser.ParseCatalog(""), Is.Empty);
            Assert.That(parser.ParseCatalog(null), Is.Empty);
            Assert.That(parser.ParseCatalog("   "), Is.Empty);
        }

        // --- Phase 1: per-mod feature mappers (framework / quality / motion) ---

        [Test]
        public void MapFramework_KnownFrameworks_MapToCategories()
        {
            Assert.That(PcvrCentralParser.MapFramework("uevr-profile"), Is.EqualTo(VrModCategories.UeVr));
            // UUVR is its own framework: the site's own methodExplainer calls it "a universal
            // injector for Unity games", while UEVR is the Unreal-engine one. Mapping uuvr onto
            // "UEVR" told the user the wrong tool AND the wrong engine for the game in front of them.
            Assert.That(PcvrCentralParser.MapFramework("uuvr"), Is.EqualTo(VrModCategories.UuVr));
            Assert.That(PcvrCentralParser.MapFramework("reframework"), Is.EqualTo(VrModCategories.ReFramework));
            Assert.That(PcvrCentralParser.MapFramework("vorpx"), Is.EqualTo(VrModCategories.VorpX));
            Assert.That(PcvrCentralParser.MapFramework("luke-ross"), Is.EqualTo(VrModCategories.LukeRoss));
            // "other" is the site's catch-all for a method that fits none of its categories; it gets a
            // category of its own rather than being reported as a Dedicated mod it was never called.
            Assert.That(PcvrCentralParser.MapFramework("other"), Is.EqualTo(VrModCategories.Other));
        }

        [Test]
        public void MapFramework_NativeCustom_And_Depth3D_MapToTheirOwnCategories()
        {
            // These two used to be dropped as "not frameworks". In the site's own words:
            // native-custom is its Dedicated VR mod - "built for this one game" - and is the largest
            // framework group in the catalog (445 of 1,548 mods), so dropping it produced no category
            // tag at all for the biggest group on the site. depth3d is a ReShade depth shader the site
            // itself calls "not a real VR conversion", so it gets its own category instead of being
            // folded in with the real-VR injectors it is not.
            Assert.That(PcvrCentralParser.MapFramework("native-custom"), Is.EqualTo(VrModCategories.Dedicated));
            Assert.That(PcvrCentralParser.MapFramework("depth3d"), Is.EqualTo(VrModCategories.Depth3D));
        }

        [Test]
        public void MapFramework_NonFrameworks_MapToNull()
        {
            // Only values the site does NOT name as a framework may map to null. native-custom and
            // depth3d are named frameworks (see MapFramework_NativeCustom_And_Depth3D_MapToTheirOwnCategories).
            Assert.That(PcvrCentralParser.MapFramework("native"), Is.Null);
            Assert.That(PcvrCentralParser.MapFramework("native_custom"), Is.Null);
            Assert.That(PcvrCentralParser.MapFramework("unknown-framework"), Is.Null);
            Assert.That(PcvrCentralParser.MapFramework(null), Is.Null);
            Assert.That(PcvrCentralParser.MapFramework(""), Is.Null);
            Assert.That(PcvrCentralParser.MapFramework("   "), Is.Null);
        }

        [Test]
        public void MapFramework_CaseAndWhitespaceTolerant()
        {
            Assert.That(PcvrCentralParser.MapFramework("  UEVR-Profile "), Is.EqualTo(VrModCategories.UeVr));
            Assert.That(PcvrCentralParser.MapFramework("Vorpx"), Is.EqualTo(VrModCategories.VorpX));
        }

        [Test]
        public void MapStatus_KnownLabels_MapToFacets()
        {
            // All five values are the site's own labels.status dictionary. The payload key
            // "superseded" is what the site shows its users as "Outdated"; "active" is mapped even
            // though the source marks 1,527 of 1,548 mods active, because no status tag must mean
            // "the source says nothing", not "the source says it works".
            Assert.That(PcvrCentralParser.MapStatus("active"), Is.EqualTo(VrModCategories.StatusActive));
            Assert.That(PcvrCentralParser.MapStatus("update-detected"), Is.EqualTo(VrModCategories.StatusUpdateDetected));
            Assert.That(PcvrCentralParser.MapStatus("superseded"), Is.EqualTo(VrModCategories.StatusOutdated));
            Assert.That(PcvrCentralParser.MapStatus("broken"), Is.EqualTo(VrModCategories.StatusBroken));
            Assert.That(PcvrCentralParser.MapStatus("abandoned"), Is.EqualTo(VrModCategories.StatusAbandoned));
        }

        [Test]
        public void MapStatus_UnknownOrAbsent_MapToNull()
        {
            // A status the source does not use must not become a tag (the vocabulary gate would drop
            // it anyway, but the mapping is where the decision belongs).
            Assert.That(PcvrCentralParser.MapStatus("unknown"), Is.Null);
            Assert.That(PcvrCentralParser.MapStatus("working"), Is.Null);
            Assert.That(PcvrCentralParser.MapStatus("needs an update"), Is.Null);
            Assert.That(PcvrCentralParser.MapStatus(null), Is.Null);
            Assert.That(PcvrCentralParser.MapStatus(""), Is.Null);
        }

        [Test]
        public void MapQuality_KnownLabels_MapToGrades()
        {
            Assert.That(PcvrCentralParser.MapQuality("native-like"), Is.EqualTo("Native-like"));
            Assert.That(PcvrCentralParser.MapQuality("excellent"), Is.EqualTo("Excellent"));
            Assert.That(PcvrCentralParser.MapQuality("playable"), Is.EqualTo("Playable"));
            Assert.That(PcvrCentralParser.MapQuality("experimental"), Is.EqualTo("Experimental"));
        }

        [Test]
        public void MapQuality_UnknownOrAbsent_MapToNull()
        {
            Assert.That(PcvrCentralParser.MapQuality("unknown"), Is.Null);
            Assert.That(PcvrCentralParser.MapQuality(null), Is.Null);
            Assert.That(PcvrCentralParser.MapQuality(""), Is.Null);
        }

        [Test]
        public void MapMotion_KnownLevels_MapToFacets()
        {
            Assert.That(PcvrCentralParser.MapMotion("full"), Is.EqualTo(VrModCategories.MotionFull));
            Assert.That(PcvrCentralParser.MapMotion("partial"), Is.EqualTo(VrModCategories.MotionPartial));
            Assert.That(PcvrCentralParser.MapMotion("gamepad-only"), Is.EqualTo(VrModCategories.MotionGamepadOnly));
            Assert.That(PcvrCentralParser.MapMotion("none"), Is.EqualTo(VrModCategories.MotionNone));
        }

        [Test]
        public void MapMotion_UnknownOrAbsent_MapToNull()
        {
            Assert.That(PcvrCentralParser.MapMotion("unknown"), Is.Null);
            Assert.That(PcvrCentralParser.MapMotion(null), Is.Null);
            Assert.That(PcvrCentralParser.MapMotion(""), Is.Null);
        }

        // --- Phase 1: per-mod feature summary on AdditionalInfo (additive) ---

        [Test]
        public void ParseCatalog_PopulatesAdditionalInfo_FromMods()
        {
            var parser = new PcvrCentralParser();
            string chunk = "{\"games\":[{\"slug\":\"alpha\",\"title\":\"Alpha One\",\"mods\":[{\"name\":\"RE4 Mod\",\"framework\":\"reframework\",\"qualityLabel\":\"excellent\",\"motionControls\":\"full\",\"author\":\"ModderX\"}]}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            Assert.That(result, Has.Count.EqualTo(1));
            string? info = result[0].AdditionalInfo;
            Assert.That(info, Is.Not.Null);
            Assert.That(info, Does.Contain("REFramework"));
            Assert.That(info, Does.Contain("RE4 Mod"));
            Assert.That(info, Does.Contain("quality: Excellent"));
            Assert.That(info, Does.Contain("motion: Full Motion"));
            Assert.That(info, Does.Contain("ModderX"));
        }

        [Test]
        public void ParseCatalog_AdditionalInfo_CarriesThePerModStatus()
        {
            // The facet TAGS are a union over a game's mods, so a tag list cannot say WHICH mod is
            // the broken one. This per-mod text is the only place that keeps it attributable.
            var parser = new PcvrCentralParser();
            string chunk = "{\"games\":[{\"slug\":\"alpha\",\"title\":\"Alpha One\",\"mods\":[" +
                "{\"name\":\"Broken Mod\",\"framework\":\"uevr-profile\",\"status\":\"broken\"}," +
                "{\"name\":\"Working Mod\",\"framework\":\"reframework\",\"status\":\"active\"}" +
                "]}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            string? info = result[0].AdditionalInfo;
            Assert.That(info, Is.Not.Null);
            Assert.That(info, Does.Contain("Broken Mod").And.Contains("status: Broken"));
            Assert.That(info, Does.Contain("Working Mod").And.Contains("status: Active"));
        }

        [Test]
        public void ParseCatalog_AdditionalInfo_OmitsStatus_WhenTheSourceReportsNone()
        {
            // Absent must stay absent: an empty status slot may not print as some status.
            var parser = new PcvrCentralParser();
            string chunk = "{\"games\":[{\"slug\":\"alpha\",\"title\":\"Alpha One\",\"mods\":[" +
                "{\"name\":\"Mod With No Status\",\"framework\":\"reframework\",\"status\":\"unknown\"}" +
                "]}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            string? info = result[0].AdditionalInfo;
            Assert.That(info, Is.Not.Null);
            Assert.That(info, Does.Not.Contain("status:"));
            Assert.That(result[0].Statuses, Is.Empty);
        }

        [Test]
        public void ParseCatalog_NoMods_AdditionalInfoIsNull()
        {
            var parser = new PcvrCentralParser();
            // A game with no mods array must not get an AdditionalInfo.
            string chunk = "{\"games\":[{\"slug\":\"a\",\"title\":\"No Mods Game\"}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].AdditionalInfo, Is.Null);
        }

        [Test]
        public void ParseCatalog_DeduplicatesMods_BySlugAndFramework()
        {
            var parser = new PcvrCentralParser();
            // Two mods with the same slug+framework must collapse to one line.
            string chunk = "{\"games\":[{\"slug\":\"g\",\"title\":\"G\",\"mods\":[{\"name\":\"M1\",\"framework\":\"reframework\"},{\"name\":\"M2\",\"framework\":\"reframework\"}]}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            Assert.That(result, Has.Count.EqualTo(1));
            string? info = result[0].AdditionalInfo;
            Assert.That(info, Is.Not.Null);
            // Header line + exactly one deduplicated mod line.
            Assert.That(info!.Split('\n'), Has.Length.EqualTo(2));
        }

        // --- Phase 2: per-game "meta mod" (union-all features) ---

        [Test]
        public void ParseCatalog_MetaMod_UnionsCategoriesQualitiesAndMotions()
        {
            var parser = new PcvrCentralParser();
            // Two mods with different frameworks, quality grades, and motion levels: the
            // meta-mod entry must carry the UNION of all of them (union-all).
            string chunk = "{\"games\":[{\"slug\":\"alpha\",\"title\":\"Alpha One\",\"mods\":[" +
                "{\"slug\":\"m1\",\"name\":\"Ref Mod\",\"framework\":\"reframework\",\"qualityLabel\":\"excellent\",\"motionControls\":\"full\"}," +
                "{\"slug\":\"m2\",\"name\":\"UEVR Mod\",\"framework\":\"uevr-profile\",\"qualityLabel\":\"playable\",\"motionControls\":\"partial\"}" +
                "]}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            Assert.That(result, Has.Count.EqualTo(1));
            var entry = result[0];

            // Frameworks -> categories (union), with the PcvrCentral catalog category present too.
            Assert.That(entry.Categories, Does.Contain(VrModCategories.PcvrCentral));
            Assert.That(entry.Categories, Does.Contain(VrModCategories.ReFramework));
            Assert.That(entry.Categories, Does.Contain(VrModCategories.UeVr));

            // Every distinct quality grade and motion level is kept (union-all).
            Assert.That(entry.Qualities, Does.Contain("Excellent"));
            Assert.That(entry.Qualities, Does.Contain("Playable"));
            Assert.That(entry.Motions, Does.Contain(VrModCategories.MotionFull));
            Assert.That(entry.Motions, Does.Contain(VrModCategories.MotionPartial));
        }

        [Test]
        public void ParseCatalog_MetaMod_KeepsPcvrCentralCategoryFirst()
        {
            var parser = new PcvrCentralParser();
            string chunk = "{\"games\":[{\"slug\":\"alpha\",\"title\":\"Alpha One\",\"mods\":[" +
                "{\"slug\":\"m1\",\"framework\":\"reframework\",\"qualityLabel\":\"excellent\",\"motionControls\":\"full\"}" +
                "]}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            // Deterministic order: the PcvrCentral catalog category is first (discovery identity),
            // so the legacy single-Category view still reads "PCVR Central".
            var entry = result[0];
            Assert.That(entry.Categories[0], Is.EqualTo(VrModCategories.PcvrCentral));
            Assert.That(entry.Category, Is.EqualTo(VrModCategories.PcvrCentral));
        }

        [Test]
        public void ParseCatalog_MetaMod_NoMods_CatalogOnly_NoFacets()
        {
            var parser = new PcvrCentralParser();
            string chunk = "{\"games\":[{\"slug\":\"a\",\"title\":\"No Mods Game\"}]}";
            var result = parser.ParseCatalog(MakeRsc(chunk));

            var entry = result[0];
            Assert.That(entry.Categories, Is.EquivalentTo(new[] { VrModCategories.PcvrCentral }));
            Assert.That(entry.Qualities, Is.Empty);
            Assert.That(entry.Motions, Is.Empty);
            Assert.That(entry.Statuses, Is.Empty);
        }

        [Test]
        public void ParseCatalog_MetaMod_EmitsRealTags_ViaTagNamesFor()
        {
            var parser = new PcvrCentralParser();
            // The user-facing root cause ("0 tag(s) applied"): a mod-bearing meta-mod entry must
            // now yield real category + quality + motion tags, not just the umbrella.
            string chunk = "{\"games\":[{\"slug\":\"alpha\",\"title\":\"Alpha One\",\"mods\":[" +
                "{\"slug\":\"m1\",\"framework\":\"reframework\",\"qualityLabel\":\"excellent\",\"motionControls\":\"full\"}" +
                "]}]}";
            var entry = parser.ParseCatalog(MakeRsc(chunk)).Single();

            var names = VrModCategories.TagNamesFor(entry);
            Assert.That(names, Does.Contain(VrModCategories.TagNameFor(VrModCategories.ReFramework)));
            Assert.That(names, Does.Contain(VrModCategories.TagNameForQuality("Excellent")));
            Assert.That(names, Does.Contain(VrModCategories.TagNameForMotion(VrModCategories.MotionFull)));
            // A catalog category contributes no tag of its own.
            Assert.That(names, Does.Not.Contain(VrModCategories.TagNameFor(VrModCategories.PcvrCentral)));
        }

        [Test]
        public void ParseCatalog_MetaMod_KeepsEveryModStatus_ActiveAndBroken()
        {
            // The status facet is union-all for the same reason quality and motion are: a game's mods
            // are in different states at the same time. "Active" together with "Broken" means a working
            // mod and a broken one both exist to choose between - not that one mod both works and
            // does not - so both are kept and both become tags.
            var parser = new PcvrCentralParser();
            string chunk = "{\"games\":[{\"slug\":\"alpha\",\"title\":\"Alpha One\",\"mods\":[" +
                "{\"slug\":\"m1\",\"framework\":\"uevr-profile\",\"status\":\"broken\"}," +
                "{\"slug\":\"m2\",\"framework\":\"uevr-profile\",\"status\":\"active\"}," +
                "{\"slug\":\"m3\",\"framework\":\"reframework\",\"status\":\"superseded\"}," +
                "{\"slug\":\"m4\",\"framework\":\"reframework\",\"status\":\"update-detected\"}," +
                "{\"slug\":\"m5\",\"framework\":\"vorpx\",\"status\":\"abandoned\"}" +
                "]}]}";
            var entry = parser.ParseCatalog(MakeRsc(chunk)).Single();

            Assert.That(entry.Statuses, Is.EquivalentTo(new[]
            {
                VrModCategories.StatusBroken,
                VrModCategories.StatusActive,
                VrModCategories.StatusOutdated,      // the site's "superseded", shown to its users as Outdated
                VrModCategories.StatusUpdateDetected,
                VrModCategories.StatusAbandoned
            }));
        }

        [Test]
        public void ParseCatalog_MetaMod_StatusProducesItsOwnTag()
        {
            var parser = new PcvrCentralParser();
            string chunk = "{\"games\":[{\"slug\":\"alpha\",\"title\":\"Alpha One\",\"mods\":[" +
                "{\"slug\":\"m1\",\"framework\":\"reframework\",\"status\":\"abandoned\"}" +
                "]}]}";
            var entry = parser.ParseCatalog(MakeRsc(chunk)).Single();

            var names = VrModCategories.TagNamesFor(entry);
            Assert.That(names, Does.Contain(VrModCategories.TagNameForStatus(VrModCategories.StatusAbandoned)));
            Assert.That(names, Does.Contain("VRMod Abandoned"));
        }

        [Test]
        public void ParseCatalog_MetaMod_ConflictingValues_AllKept()
        {
            var parser = new PcvrCentralParser();
            // Contradictions are kept (union-all): Excellent AND Playable, Full AND Partial.
            string chunk = "{\"games\":[{\"slug\":\"alpha\",\"title\":\"Alpha One\",\"mods\":[" +
                "{\"slug\":\"m1\",\"framework\":\"reframework\",\"qualityLabel\":\"excellent\",\"motionControls\":\"full\"}," +
                "{\"slug\":\"m2\",\"framework\":\"reframework\",\"qualityLabel\":\"playable\",\"motionControls\":\"partial\"}" +
                "]}]}";
            var entry = parser.ParseCatalog(MakeRsc(chunk)).Single();

            Assert.That(entry.Qualities, Is.EquivalentTo(new[] { "Excellent", "Playable" }));
            Assert.That(entry.Motions, Is.EquivalentTo(new[] { VrModCategories.MotionFull, VrModCategories.MotionPartial }));
        }
    }
}
