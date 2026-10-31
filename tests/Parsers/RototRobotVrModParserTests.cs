using NUnit.Framework;
using Playnite.VRMods;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteVRMods.Tests.Parsers
{
    [TestFixture]
    public class RototRobotVrModParserTests
    {
        [Test]
        public void SourceId_ReturnsRototRobot()
        {
            var parser = new RototRobotVrModParser();
            Assert.That(parser.SourceId, Is.EqualTo("RototRobot"));
        }

        [Test]
        public void DisplayName_ReturnsCorrectName()
        {
            var parser = new RototRobotVrModParser();
            Assert.That(parser.DisplayName, Is.EqualTo("RototRobot VRMods-List"));
        }

        [Test]
        public void IsEnabled_DefaultsToTrue()
        {
            var parser = new RototRobotVrModParser();
            Assert.That(parser.IsEnabled, Is.True);
        }

        [Test]
        public void ChangeIdentifier_ReturnsNullWhenNotFetched()
        {
            var parser = new RototRobotVrModParser();
            Assert.That(parser.ChangeIdentifier, Is.Null);
        }

        [Test]
        public void GetCanonicalName_StripsVrSuffixes()
        {
            var parser = new RototRobotVrModParser();
            var entry = new VrModEntry { GameName = "Skyrim VR Mod by Author" };
            var result = parser.GetCanonicalName(entry);
            Assert.That(result, Is.EqualTo("Skyrim"));
        }

        [Test]
        public void GetCanonicalName_RemovesByAuthor()
        {
            var parser = new RototRobotVrModParser();
            var entry = new VrModEntry { GameName = "GameName Fork by Author" };
            var result = parser.GetCanonicalName(entry);
            Assert.That(result, Is.EqualTo("GameName Fork"));
        }

        [Test]
        public void GetCanonicalName_KeepsNameWithoutSuffix()
        {
            var parser = new RototRobotVrModParser();
            var entry = new VrModEntry { GameName = "Half-Life 2" };
            var result = parser.GetCanonicalName(entry);
            Assert.That(result, Is.EqualTo("Half-Life 2"));
        }

        [Test]
        public void ParseRenderedHtml_ExtractsSingleGameItem()
        {
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""motion 1st"">
                <span class=""title-main"">Skyrim</span>
                <span class=""title-sub"">by Author</span>
                <span class=""data-secondary-link"" data-url=""https://store.steampowered.com/app/720000/Skyrim/"" data-text=""View Store Page"">
            </div>";

            var result = parser.ParseRenderedHtml(html);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim"));
            Assert.That(result[0].Category, Is.EqualTo("Full Motion Controls"));
            Assert.That(result[0].SteamAppId, Is.EqualTo("720000"));
            Assert.That(result[0].Author, Is.EqualTo("by Author"));
        }

        [Test]
        public void ParseRenderedHtml_ExtractsMultipleGameItems()
        {
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""motion 1st"">
                <span class=""title-main"">Skyrim</span>
                <span class=""title-sub"">by Author1</span>
                <span class=""data-secondary-link"" data-url=""https://store.steampowered.com/app/720000/Skyrim/"" data-text=""View Store Page"">
            </div>
            <div class=""game-item"" data-tags=""uevr"">
                <span class=""title-main"">DOOM</span>
                <span class=""title-sub"">Profile by Author2</span>
                <span class=""data-secondary-link"" data-url=""https://store.steampowered.com/app/3024200/DOOM_Eternal/"" data-text=""View Store Page"">
            </div>
            <div class=""game-item"" data-tags=""emulator"">
                <span class=""title-main"">Dolphin</span>
                <span class=""title-sub"">by Author3</span>
                <span class=""data-secondary-link"" data-url=""https://example.com"" data-text=""View Store Page"">
            </div>";

            var result = parser.ParseRenderedHtml(html);
            Assert.That(result, Has.Count.EqualTo(3));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim"));
            Assert.That(result[0].Category, Is.EqualTo("Full Motion Controls"));
            Assert.That(result[1].GameName, Is.EqualTo("DOOM"));
            Assert.That(result[1].Category, Is.EqualTo("UEVR"));
            Assert.That(result[2].GameName, Is.EqualTo("Dolphin"));
            Assert.That(result[2].Category, Is.EqualTo("Dedicated"));
        }

        [Test]
        public void ParseRenderedHtml_MapsTagsToCategories()
        {
            var parser = new RototRobotVrModParser();
            var testCases = new (string Tags, string ExpectedCategory)[]
            {
                ("motion 1st", "Full Motion Controls"),
                ("1st motion", "Full Motion Controls"),
                ("uevr 6dof", "UEVR"),
                ("gesture 1st", "Motion Gesture"),
                ("3dof 1st", "Head Tracked"),
                ("tool 1st", "Dedicated"),
                ("emulator 1st", "Dedicated"),
            };

            foreach (var (tags, expectedCategory) in testCases)
            {
                var html = $@"<div class=""game-item"" data-tags=""{tags}"">
                    <span class=""title-main"">Test Game</span>
                    <span class=""title-sub"">by Test</span>
                    <span class=""data-secondary-link"" data-url=""https://example.com"" data-text=""View"">
                </div>";
                var result = parser.ParseRenderedHtml(html);
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result[0].Category, Is.EqualTo(expectedCategory), $"Tags '{tags}' should map to '{expectedCategory}'");
            }
        }

        [Test]
        public void ParseRenderedHtml_PrefersMoreSpecificTags()
        {
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""motion 1st uevr"">
                <span class=""title-main"">Palworld</span>
                <span class=""title-sub"">by DRey86</span>
                <span class=""data-secondary-link"" data-url=""https://store.steampowered.com/app/1623730/Palworld/"" data-text=""View Store Page"">
            </div>";
            var result = parser.ParseRenderedHtml(html);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Category, Is.EqualTo("Full Motion Controls"));
        }

        [Test]
        public void ParseRenderedHtml_ExtractsSteamAppId()
        {
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""motion 1st"">
                <span class=""title-main"">Skyrim</span>
                <span class=""title-sub"">by Author</span>
                <span class=""data-secondary-link"" data-url=""https://store.steampowered.com/app/720000/Skyrim/"" data-text=""View Store Page"">
            </div>";
            var result = parser.ParseRenderedHtml(html);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].SteamAppId, Is.EqualTo("720000"));
        }

        [Test]
        public void ParseRenderedHtml_NoSteamId_ReturnsNull()
        {
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""motion 1st"">
                <span class=""title-main"">Minecraft</span>
                <span class=""title-sub"">by jrbudda</span>
                <span class=""data-secondary-link"" data-url=""https://www.minecraft.net/"" data-text=""View Store Page"">
            </div>";
            var result = parser.ParseRenderedHtml(html);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].SteamAppId, Is.Null);
        }

        [Test]
        public void ParseRenderedHtml_SkipsItemsWithoutTitleMain()
        {
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""motion 1st"">
                <span class=""title-sub"">by Author</span>
                <span class=""data-secondary-link"" data-url=""https://example.com"" data-text=""View"">
            </div>";
            var result = parser.ParseRenderedHtml(html);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseRenderedHtml_EmptyHtml_ReturnsEmpty()
        {
            var parser = new RototRobotVrModParser();
            var result = parser.ParseRenderedHtml("");
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseRenderedHtml_NoGameItems_ReturnsEmpty()
        {
            var parser = new RototRobotVrModParser();
            var html = "<html><body><h1>VRMods List</h1><p>No games</p></body></html>";
            var result = parser.ParseRenderedHtml(html);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseRenderedHtml_DefaultsToFullMotionControls_WhenUnknownTags()
        {
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""someunknown 1st"">
                <span class=""title-main"">Test Game</span>
                <span class=""title-sub"">by Test</span>
                <span class=""data-secondary-link"" data-url=""https://example.com"" data-text=""View"">
            </div>";
            var result = parser.ParseRenderedHtml(html);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Category, Is.EqualTo("Full Motion Controls"));
        }

        [Test]
        public void MapTagsToCategories_MapsAllKnownTags()
        {
            Assert.That(RototRobotVrModParser.MapTagsToCategories("motion 1st"), Is.EqualTo(new[] { "Full Motion Controls" }));
            Assert.That(RototRobotVrModParser.MapTagsToCategories("uevr 6dof"), Is.EqualTo(new[] { "UEVR" }));
            Assert.That(RototRobotVrModParser.MapTagsToCategories("gesture 1st"), Is.EqualTo(new[] { "Motion Gesture" }));
            Assert.That(RototRobotVrModParser.MapTagsToCategories("3dof 1st"), Is.EqualTo(new[] { "Head Tracked" }));
            Assert.That(RototRobotVrModParser.MapTagsToCategories("tool 1st"), Is.EqualTo(new[] { "Dedicated" }));
            Assert.That(RototRobotVrModParser.MapTagsToCategories("emulator 1st"), Is.EqualTo(new[] { "Dedicated" }));
        }

        [Test]
        public void MapTagsToCategories_CaseInsensitive()
        {
            Assert.That(RototRobotVrModParser.MapTagsToCategories("Motion 1st"), Is.EqualTo(new[] { "Full Motion Controls" }));
            Assert.That(RototRobotVrModParser.MapTagsToCategories("UEVR 6dof"), Is.EqualTo(new[] { "UEVR" }));
            Assert.That(RototRobotVrModParser.MapTagsToCategories("GESTURE 1st"), Is.EqualTo(new[] { "Motion Gesture" }));
        }

        [Test]
        public void MapTagsToCategories_YieldsEveryMatchingCategory()
        {
            // The RototRobot bug fix: a mod tagged with several categories yields ALL of them
            // (not just the first match), in priority order, deduped.
            Assert.That(RototRobotVrModParser.MapTagsToCategories("motion 1st uevr"),
                Is.EqualTo(new[] { "Full Motion Controls", "UEVR" }));
            Assert.That(RototRobotVrModParser.MapTagsToCategories("tool emulator"),
                Is.EqualTo(new[] { "Dedicated" }));
        }

        [Test]
        public void MapTagsToCategories_DefaultsWhenNoKnownTags()
        {
            Assert.That(RototRobotVrModParser.MapTagsToCategories("1st 3rd"), Is.EqualTo(new[] { "Full Motion Controls" }));
            Assert.That(RototRobotVrModParser.MapTagsToCategories("6dof 1st 3rd"), Is.EqualTo(new[] { "Full Motion Controls" }));
        }

        [Test]
        public void ParseRawMarkdown_DetectsFullMotionControlsCategory()
        {
            var parser = new RototRobotVrModParser();
            var markdown = "# Full Motion Controls\nSkyrim\nFallout 4\n";
            var result = parser.ParseRawMarkdown(markdown);
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim"));
            Assert.That(result[0].Category, Is.EqualTo("Full Motion Controls"));
        }

        [Test]
        public void ParseRawMarkdown_DetectsUEVRProfilesCategory()
        {
            var parser = new RototRobotVrModParser();
            var markdown = "# UEVR Profiles\nDOOM\nQuake\n";
            var result = parser.ParseRawMarkdown(markdown);
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].Category, Is.EqualTo("UEVR"));
        }

        [Test]
        public void ParseRawMarkdown_DetectsMotionGestureCategory()
        {
            var parser = new RototRobotVrModParser();
            var markdown = "# Motion Gesture Games\nBeat Saber\nSuperhot VR\n";
            var result = parser.ParseRawMarkdown(markdown);
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].Category, Is.EqualTo("Motion Gesture"));
        }

        [Test]
        public void ParseRawMarkdown_DetectsHeadTrackedCategory()
        {
            var parser = new RototRobotVrModParser();
            var markdown = "# Head Tracked Games\nKerbal Space Program\nElite Dangerous\n";
            var result = parser.ParseRawMarkdown(markdown);
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].Category, Is.EqualTo("Head Tracked"));
        }

        [Test]
        public void ParseRawMarkdown_DetectsInjectorCategory()
        {
            var parser = new RototRobotVrModParser();
            var markdown = "# Injector tools\nGame A\nGame B\n";
            var result = parser.ParseRawMarkdown(markdown);
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].Category, Is.EqualTo("Dedicated"));
        }

        [Test]
        public void ParseRawMarkdown_DetectsEmulatorsCategory()
        {
            var parser = new RototRobotVrModParser();
            var markdown = "# VR Emulators\nDolphin\nRPCS3\n";
            var result = parser.ParseRawMarkdown(markdown);
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].Category, Is.EqualTo("Dedicated"));
        }

        [Test]
        public void ParseRawMarkdown_SkipsNonCategoryHeaders()
        {
            var parser = new RototRobotVrModParser();
            var markdown = "# Some Other Header\nGame A\nGame B\n# Full Motion Controls\nSkyrim\n";
            var result = parser.ParseRawMarkdown(markdown);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim"));
        }

        [Test]
        public void ParseRawMarkdown_StripsVRSuffixes()
        {
            var parser = new RototRobotVrModParser();
            var markdown = "# Full Motion Controls\nSkyrim VR\nFallout 4 VR mod by Author\n";
            var result = parser.ParseRawMarkdown(markdown);
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim"));
            Assert.That(result[1].GameName, Is.EqualTo("Fallout 4"));
        }

        [Test]
        public void ParseRawMarkdown_SkipsPipeLines()
        {
            var parser = new RototRobotVrModParser();
            var markdown = "# Full Motion Controls\n| Game A | VR | 2024 |\nSkyrim\n";
            var result = parser.ParseRawMarkdown(markdown);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim"));
        }

        [Test]
        public void ParseRawMarkdown_EmptyMarkdown_ReturnsEmpty()
        {
            var parser = new RototRobotVrModParser();
            var result = parser.ParseRawMarkdown("");
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseRawMarkdown_NoCategories_ReturnsEmpty()
        {
            var parser = new RototRobotVrModParser();
            var markdown = "# Other Section\nGame A\nGame B\n";
            var result = parser.ParseRawMarkdown(markdown);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ParseRawMarkdown_MultipleCategories()
        {
            var parser = new RototRobotVrModParser();
            var markdown = "# Full Motion Controls\nSkyrim\n# UEVR Profiles\nDOOM\n";
            var result = parser.ParseRawMarkdown(markdown);
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim"));
            Assert.That(result[0].Category, Is.EqualTo("Full Motion Controls"));
            Assert.That(result[1].GameName, Is.EqualTo("DOOM"));
            Assert.That(result[1].Category, Is.EqualTo("UEVR"));
        }

        [Test]
        public void ParseRenderedHtml_MergesDuplicateGameIntoOneEntry()
        {
            // The site lists some games in several categories (e.g. Kerbal Space Program
            // under both "motion 3rd" and "3dof 3rd"): the duplicates merge into ONE entry
            // carrying the UNION of the occurrences' features (PCVR Central meta-mod shape),
            // so one game = one review row = one decision = one stable tag set.
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""motion 3rd"">
                <span class=""title-main"">Kerbal Space Program</span>
                <span class=""title-sub"">by AuthorA</span>
                <span class=""data-secondary-link"" data-url=""https://store.steampowered.com/app/648190/Kerbal_Space_Program/"" data-text=""View"">
            </div>
            <div class=""game-item"" data-tags=""3dof 3rd"">
                <span class=""title-main"">Kerbal Space Program</span>
                <span class=""title-sub"">by AuthorB</span>
                <span class=""data-secondary-link"" data-url=""https://store.steampowered.com/app/648190/Kerbal_Space_Program/"" data-text=""View"">
            </div>";

            var result = parser.ParseRenderedHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Kerbal Space Program"));
            Assert.That(result[0].Categories, Is.EqualTo(new[] { "Full Motion Controls", "Head Tracked" }));
            Assert.That(result[0].IsThirdPerson, Is.True);
            Assert.That(result[0].SteamAppId, Is.EqualTo("648190"));
            Assert.That(result[0].Author, Is.EqualTo("by AuthorA"));
        }

        [Test]
        public void ParseRenderedHtml_DuplicateFacetsAreUnioned()
        {
            // "motion 1st" (first person) + "3dof 3rd" (third person) → the merged entry is
            // BOTH first and third person (any-true facet union).
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""motion 1st"">
                <span class=""title-main"">Test Game</span>
            </div>
            <div class=""game-item"" data-tags=""3dof 3rd"">
                <span class=""title-main"">Test Game</span>
            </div>";

            var result = parser.ParseRenderedHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].IsFirstPerson, Is.True);
            Assert.That(result[0].IsThirdPerson, Is.True);
        }

        [Test]
        public void ParseRenderedHtml_MergesPunctuationVariantNames()
        {
            // The site is inconsistent about punctuation ("Jedi Knight II: Jedi Outcast" vs
            // "Jedi Knight II - Jedi Outcast"): the duplicates still group into one entry.
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""3dof 1st"">
                <span class=""title-main"">Jedi Knight II: Jedi Outcast</span>
            </div>
            <div class=""game-item"" data-tags=""3dof"">
                <span class=""title-main"">Jedi Knight II - Jedi Outcast</span>
            </div>";

            var result = parser.ParseRenderedHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Jedi Knight II: Jedi Outcast"));
        }

        [Test]
        public void ParseRenderedHtml_PureDuplicateLists_CollapseToOne()
        {
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""motion 1st"">
                <span class=""title-main"">Quake</span>
            </div>
            <div class=""game-item"" data-tags=""motion 1st"">
                <span class=""title-main"">Quake</span>
            </div>";

            var result = parser.ParseRenderedHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Categories, Is.EqualTo(new[] { "Full Motion Controls" }));
        }

        [Test]
        public void ParseRenderedHtml_DistinctGamesStaySeparate()
        {
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""motion 1st"">
                <span class=""title-main"">DOOM</span>
            </div>
            <div class=""game-item"" data-tags=""uevr"">
                <span class=""title-main"">DOOM Eternal</span>
            </div>";

            var result = parser.ParseRenderedHtml(html);

            Assert.That(result, Has.Count.EqualTo(2));
        }

        [Test]
        public void ParseRenderedHtml_MergedEntryTakesFirstNonNullOptionals()
        {
            // The first occurrence has no Steam App ID but the duplicate does: the merged
            // entry picks up the first non-null value.
            var parser = new RototRobotVrModParser();
            var html = @"<div class=""game-item"" data-tags=""motion 1st"">
                <span class=""title-main"">Test Game</span>
            </div>
            <div class=""game-item"" data-tags=""3dof"">
                <span class=""title-main"">Test Game</span>
                <span class=""data-secondary-link"" data-url=""https://store.steampowered.com/app/12345/"" data-text=""View"">
            </div>";

            var result = parser.ParseRenderedHtml(html);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].SteamAppId, Is.EqualTo("12345"));
        }

        [Test]
        public void ParseRawMarkdown_MergesGameListedInTwoCategories()
        {
            var parser = new RototRobotVrModParser();
            var markdown = "# Full Motion Controls\nSkyrim\n# Head Tracked Games\nSkyrim\n";

            var result = parser.ParseRawMarkdown(markdown);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].GameName, Is.EqualTo("Skyrim"));
            Assert.That(result[0].Categories, Is.EqualTo(new[] { "Full Motion Controls", "Head Tracked" }));
        }

        [Test]
        public void DuplicateGroupKey_StripsCasePunctuationAndVrSuffixes()
        {
            Assert.That(BaseVrModParser.DuplicateGroupKey("Skyrim VR Mod by X"),
                Is.EqualTo(BaseVrModParser.DuplicateGroupKey("Skyrim")));
            Assert.That(BaseVrModParser.DuplicateGroupKey("Jedi Knight II: Jedi Outcast"),
                Is.EqualTo(BaseVrModParser.DuplicateGroupKey("Jedi Knight II - Jedi Outcast")));
            Assert.That(BaseVrModParser.DuplicateGroupKey("HALF-LIFE 2"),
                Is.EqualTo(BaseVrModParser.DuplicateGroupKey("Half Life 2")));
            Assert.That(BaseVrModParser.DuplicateGroupKey("DOOM"),
                Is.Not.EqualTo(BaseVrModParser.DuplicateGroupKey("DOOM Eternal")));
        }

        [Test]
        public void MergeDuplicateGames_EmptyList_ReturnsEmpty()
        {
            var result = BaseVrModParser.MergeDuplicateGames(new List<VrModEntry>());

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void MergeDuplicateGames_NoDuplicates_PreservesOrder()
        {
            var entries = new List<VrModEntry>
            {
                new VrModEntry { GameName = "Alpha", Categories = { "Full Motion Controls" } },
                new VrModEntry { GameName = "Beta", Categories = { "UEVR" } }
            };

            var result = BaseVrModParser.MergeDuplicateGames(entries);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].GameName, Is.EqualTo("Alpha"));
            Assert.That(result[1].GameName, Is.EqualTo("Beta"));
        }

        [Test]
        public void MergeDuplicateGames_DistinctDeepLinks_AreUnioned()
        {
            // A merged game keeps EVERY distinct per-game deep link (first-seen order) —
            // each contributes its own Playnite link (mirroring the Categories/Qualities/
            // Motions union). Dropping the second URL would be data loss.
            var entries = new List<VrModEntry>
            {
                new VrModEntry { GameName = "Skyrim", Categories = { "Full Motion Controls" }, SourceUrl = "https://example.com/a" },
                new VrModEntry { GameName = "Skyrim", Categories = { "Head Tracked" }, SourceUrl = "https://example.com/b" }
            };

            var result = BaseVrModParser.MergeDuplicateGames(entries);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].SourceUrls, Is.EqualTo(new[] { "https://example.com/a", "https://example.com/b" }));
        }

        [Test]
        public void MergeDuplicateGames_Statuses_AreUnioned()
        {
            // Same rule as categories/qualities/motions: two listings of one game in different states
            // produce ONE row that keeps both facts - "Active" plus "Broken" is a working mod and a
            // broken one to choose between, and dropping either would lose the choice.
            var entries = new List<VrModEntry>
            {
                new VrModEntry { GameName = "Skyrim", Categories = { "UEVR" }, Statuses = { "Broken" } },
                new VrModEntry { GameName = "Skyrim", Categories = { "REFramework" }, Statuses = { "Active" } }
            };

            var result = BaseVrModParser.MergeDuplicateGames(entries);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Statuses, Is.EqualTo(new[] { "Broken", "Active" }), "first-seen order preserved");
        }

        [Test]
        public void MergeDuplicateGames_Statuses_DedupedCaseInsensitively()
        {
            var entries = new List<VrModEntry>
            {
                new VrModEntry { GameName = "Skyrim", Categories = { "UEVR" }, Statuses = { "Outdated" } },
                new VrModEntry { GameName = "Skyrim", Categories = { "UEVR" }, Statuses = { "outdated" } }
            };

            var result = BaseVrModParser.MergeDuplicateGames(entries);

            Assert.That(result[0].Statuses, Is.EqualTo(new[] { "Outdated" }), "one row, the first spelling");
        }

        [Test]
        public void MergeDuplicateGames_SameDeepLink_Deduplicated()
        {
            var entries = new List<VrModEntry>
            {
                new VrModEntry { GameName = "Skyrim", Categories = { "Full Motion Controls" }, SourceUrl = "https://example.com/a" },
                new VrModEntry { GameName = "Skyrim", Categories = { "Head Tracked" }, SourceUrl = "https://example.com/a" }
            };

            var result = BaseVrModParser.MergeDuplicateGames(entries);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].SourceUrls, Is.EqualTo(new[] { "https://example.com/a" }));
        }

        [Test]
        public void MergeDuplicateGames_NoDeepLinks_StaysEmpty()
        {
            var entries = new List<VrModEntry>
            {
                new VrModEntry { GameName = "Skyrim", Categories = { "Full Motion Controls" } },
                new VrModEntry { GameName = "Skyrim", Categories = { "Head Tracked" } }
            };

            var result = BaseVrModParser.MergeDuplicateGames(entries);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].SourceUrls, Is.Empty);
            Assert.That(result[0].SourceUrl, Is.Null);
        }
    }
}
