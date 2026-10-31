using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NSubstitute;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for the token-run rules (<see cref="VrModNameMatcher.CompareTokenRuns"/>) and for the
    /// suggestions they produce
    /// (<see cref="VrModMatchSuggestion"/>, <see cref="VrModTagService.FindSuggestedGames"/>).
    ///
    /// Why these tests exist at all: a whole-name similarity score compares two strings, and the
    /// library and the catalogs do not name products with the same string. The real library names a
    /// product by its full title ("The Elder Scrolls III: Morrowind GOTY Edition", a GOG copy with no
    /// Steam link at all) while PCVR Central names the same product by the shortest thing it can
    /// ("Morrowind"). Scored as strings that pair is 0.20 of ten, and it was reported as "no match",
    /// which is the shape every failure of this feature takes: silent, and invisible to the person
    /// whose library it misdescribes.
    ///
    /// The rules below compare WORDS instead, and what they refuse to conclude is as much of the
    /// design as what they accept: an ordinal after the shared words is a different product, an
    /// ordinal before them is the series, and leftover wording the other name does not share is
    /// somebody else's question. The corpus assertions are the ones that matter — they are real names
    /// from the live catalog and a real 3 045-game library, not invented pairs.
    /// </summary>
    [TestFixture]
    public class VrModMatchSuggestionTests
    {
        // ------------------------------------------------------------------ tokens and runs

        [Test]
        public void TokenizeForMatch_SplitsOnEverySeparatorTheCatalogsUse()
        {
            Assert.That(VrModNameMatcher.TokenizeForMatch("Grand Theft Auto: Vice City - Definitive Edition"),
                Is.EquivalentTo(new[] { "grand", "theft", "auto", "vice", "city", "definitive", "edition" }));
            // The hyphen is a separator, not a joiner: "half-life" is two words, which is what makes
            // it comparable with a catalog that writes "Half Life" (both normalize to the same two).
            Assert.That(VrModNameMatcher.TokenizeForMatch("Half-Life"),
                Is.EquivalentTo(new[] { "half", "life" }));
            // Punctuation INSIDE a token is dropped without a space, which glues dotted initials into
            // one word. That is deliberate and it is applied to both sides of every comparison: the
            // library writes "S.T.A.L.K.E.R. Shutdown" and a catalog writes "S.T.A.L.K.E.R. Shutdown
            // Arbri Epsilon", and glued initials compare as one word on both sides instead of
            // dissolving into single letters that no name rule can weigh.
            Assert.That(VrModNameMatcher.TokenizeForMatch("S.T.A.L.K.E.R. Shutdown"),
                Is.EquivalentTo(new[] { "stalker", "shutdown" }));
            Assert.That(VrModNameMatcher.TokenizeForMatch("Assassin's Creed III Remastered"),
                Is.EquivalentTo(new[] { "assassins", "creed", "iii", "remastered" }));
            Assert.That(VrModNameMatcher.TokenizeForMatch("  "), Is.Empty);
            Assert.That(VrModNameMatcher.TokenizeForMatch(null!), Is.Empty);
        }

        [Test]
        public void CompareTokenRuns_ListingInsideAGameNameIsCarriedAndNamesWhatTheGameAdds()
        {
            // The pair that started this: PCVR Central's listing, and the real library record for it.
            var run = VrModNameMatcher.CompareTokenRuns("Morrowind", "The Elder Scrolls III: Morrowind GOTY Edition");

            Assert.That(run.SourceIsCarriedByGame, Is.True,
                "every word of the listing name has to appear in the game name, in order");
            Assert.That(run.GameIsCarriedBySource, Is.False);
            Assert.That(run.ExtraSourceTokens, Is.Empty);
            Assert.That(run.ExtraGameTokens, Is.EquivalentTo(new[] { "the", "elder", "scrolls", "iii", "goty", "edition" }));
            // Not a refusal: the ordinal sits BEFORE the shared words (the series) and what the game
            // name adds after them is packaging. NeedsALook rather than SameProduct is the honest
            // verdict — "the elder scrolls" is title wording the rules cannot classify on their own.
            Assert.That(run.Kind, Is.EqualTo(TokenRunKind.NeedsALook));
        }

        [Test]
        public void CompareTokenRuns_ReReleaseWordingOnEitherSideIsTheSameProduct()
        {
            foreach (var pair in new[]
            {
                new[] { "Dishonored", "Dishonored - Definitive Edition" },
                new[] { "Doom 3", "DOOM 3: BFG Edition" },
                new[] { "Fallout 3", "Fallout 3: Game of the Year Edition" },
                new[] { "Grand Theft Auto V", "Grand Theft Auto V Enhanced" },
                // The other direction: the listing carries the re-release wording, the library name is
                // the plain title.
                new[] { "Little Nightmares Enhanced Edition", "Little Nightmares" },
                new[] { "Gamedec - Definitive Edition", "Gamedec" },
                new[] { "Battlefield 3™", "Battlefield 3" },
            })
            {
                var run = VrModNameMatcher.CompareTokenRuns(pair[0], pair[1]);
                Assert.That(run.Kind, Is.EqualTo(TokenRunKind.SameProduct),
                    $"'{pair[0]}' vs '{pair[1]}' — adds: {string.Join(" ", run.ExtraGameTokens)} / {string.Join(" ", run.ExtraSourceTokens)}");
            }
        }

        [Test]
        public void CompareTokenRuns_OrdinalAfterTheSharedWordsIsADifferentProduct()
        {
            // These are the pairs the substring rule was written to refuse, now refused with a reason.
            foreach (var pair in new[]
            {
                new[] { "Doom", "DOOM 3" },
                new[] { "Half-Life", "Half-Life 2" },
                new[] { "Borderlands 2", "Borderlands" },
                new[] { "Beholder", "Beholder 2" },
                // Two names that share words but are different entries in a series: the shared words
                // are not the whole of either name, and the shorter one ends in an ordinal.
                new[] { "Redout Space Legend", "Space Legend 2" },
            })
            {
                var run = VrModNameMatcher.CompareTokenRuns(pair[0], pair[1]);
                Assert.That(run.Kind, Is.EqualTo(TokenRunKind.DifferentProduct),
                    $"'{pair[0]}' vs '{pair[1]}' must not be offered as a candidate");
            }
        }

        [Test]
        public void CompareTokenRuns_AReleaseWordTheRulesCannotClassifyIsAQuestionNotARefusal()
        {
            // "Demo" is not packaging wording this plugin recognises, and it is not a series ordinal.
            // The rules report it as a leftover to be judged by a person rather than deciding that a
            // demo of a game is or is not that game.
            var run = VrModNameMatcher.CompareTokenRuns("Boneworks", "Boneworks Demo");
            Assert.That(run.Kind, Is.EqualTo(TokenRunKind.NeedsALook));
            Assert.That(run.ExtraGameTokens, Is.EquivalentTo(new[] { "demo" }));
        }

        [Test]
        public void CompareTokenRuns_OrdinalBeforeTheSharedWordsIsTheSeriesNotASequel()
        {
            // The position rule, stated as a test because it is the difference between "Morrowind"
            // matching its GOTY edition and every short listing name being refused by an ordinal that
            // belongs to the library's fuller title, not to a different product.
            var run = VrModNameMatcher.CompareTokenRuns("Skyrim", "The Elder Scrolls V: Skyrim Special Edition");
            Assert.That(run.Kind, Is.Not.EqualTo(TokenRunKind.DifferentProduct));
            Assert.That(run.SourceIsCarriedByGame, Is.True);
            Assert.That(run.ExtraGameTokens, Is.EquivalentTo(new[] { "the", "elder", "scrolls", "v", "special", "edition" }));

            // An ordinal BOTH names carry is not a disagreement about which product is meant.
            var agreed = VrModNameMatcher.CompareTokenRuns("Dark Souls II", "DARK SOULS II: Scholar of the First Sin");
            Assert.That(agreed.Kind, Is.Not.EqualTo(TokenRunKind.DifferentProduct));
            Assert.That(agreed.Kind, Is.EqualTo(TokenRunKind.NeedsALook),
                "the leftover is a subtitle the rules cannot classify, which is a question not a refusal");
        }

        [Test]
        public void CompareTokenRuns_SharedWordNamesAreCarriedByNeitherSide()
        {
            // The filter that keeps the review queue a review queue: two games that happen to share a
            // word. On the live catalog this filter is the difference between 127 review rows and 496.
            var run = VrModNameMatcher.CompareTokenRuns("Assassin's Creed Valhalla", "Valhalla Hills");
            Assert.That(run.SharedTokenCount, Is.EqualTo(1), "they do share 'valhalla'");
            Assert.That(run.SourceIsCarriedByGame, Is.False, "'assassin's creed' is not in the game name");
            Assert.That(run.GameIsCarriedBySource, Is.False, "'hills' is not in the listing name");
        }

        [Test]
        public void CompareTokenRuns_OrdinalsAloneProveNothingAndEmptyNamesAreUnrelated()
        {
            Assert.That(VrModNameMatcher.CompareTokenRuns("2", "Borderlands 2").Kind, Is.EqualTo(TokenRunKind.Unrelated),
                "a run of nothing but ordinals is a number landing on a number");
            Assert.That(VrModNameMatcher.CompareTokenRuns("", "Skyrim").Kind, Is.EqualTo(TokenRunKind.Unrelated));
            Assert.That(VrModNameMatcher.CompareTokenRuns("Skyrim", "VR").Kind, Is.EqualTo(TokenRunKind.Unrelated));
        }

        // ------------------------------------------------------------------ the service side

        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private VrModTagService _service = null!;
        private VrModSourceRegistry _registry = null!;
        private IPlayniteAPI _mockApi = null!;

        [SetUp]
        public void SetUp()
        {
            _mockApi = Substitute.For<IPlayniteAPI>();
            var mockDb = Substitute.For<IGameDatabaseAPI>();
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();
            mockDb.Games.Returns(_games);
            mockDb.Tags.Returns(_tags);
            mockDb.BufferedUpdate().Returns(new FakeBufferedUpdate());
            _mockApi.Database.Returns(mockDb);

            _registry = new VrModSourceRegistry();
            _service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
        }

        /// <summary>
        /// The library record this feature exists for: a GOG copy of Morrowind with no Steam link, so
        /// the App-ID route cannot see it, and a name whose first three letters are not the listing's.
        /// A scan must still report the pairing — as a suggestion, not as an applied match.
        /// </summary>
        [Test]
        public void FindSuggestedGames_CarriedListingNameIsSuggestedWithTheLeftoverWordsNamed()
        {
            var morrowind = AddGame("The Elder Scrolls III: Morrowind GOTY Edition");
            var parser = RegisterSource(new VrModEntry { GameName = "Morrowind", Category = "UEVR" });

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var entry = FindEntry(parser, "Morrowind");
            var suggestions = _service.FindSuggestedGames(entry);

            Assert.That(suggestions, Has.Count.EqualTo(1));
            Assert.That(suggestions[0].Game.Id, Is.EqualTo(morrowind.Id));
            Assert.That(suggestions[0].Kind, Is.EqualTo(TokenRunKind.NeedsALook));
            Assert.That(suggestions[0].Reason, Does.Contain("every word of the listing name appears in the game name"));
            Assert.That(suggestions[0].Reason, Does.Contain("iii"));
            Assert.That(suggestions[0].Reason, Does.Contain("goty edition"));
            // A suggestion is not a match: nothing was applied, and the pairing is still reported as
            // unmatched by the ordinary path.
            Assert.That(_service.FindGamesByIndex(entry), Is.Empty);
            Assert.That(morrowind.TagIds ?? new List<Guid>(), Is.Empty);
        }

        /// <summary>
        /// The reason is read by a person deciding whether to accept a pairing, so its grammar is part
        /// of the feature: a two-word run has to say "all 2 words ... appear", not "every words ...
        /// appears". Live output (271 listings in one scan) is where this was found.
        /// </summary>
        [Test]
        public void FindSuggestedGames_ReasonAgreesInNumberWithTheWordsItNames()
        {
            AddGame("Cyberpunk 2077 Goodies Collection");
            var parser = RegisterSource(new VrModEntry { GameName = "Cyberpunk 2077", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var suggestion = _service.FindSuggestedGames(FindEntry(parser, "Cyberpunk 2077")).Single();

            Assert.That(suggestion.Reason, Does.Contain("all 2 words of the listing name appear in the game name"));
            Assert.That(suggestion.Reason, Does.Not.Contain("every words"), "the live wording this replaces");
            Assert.That(suggestion.Reason, Does.Contain("the game name adds 'goodies collection'"));
        }

        /// <summary>
        /// A word the library has never heard is not a candidate set. The pair this feature exists to
        /// surface is the one whose extra word is new to the library — a listing called
        /// "Brothers: A Tale of Two Sons Remake" against the library's "Brothers: A Tale of Two Sons"
        /// — and treating the absent word as the rarest token (zero postings) used to abort the search
        /// before it began. Measured live: 638 of 1 122 listing names carry a word this library lacks.
        /// </summary>
        [Test]
        public void FindSuggestedGames_AWordAbsentFromTheLibraryDoesNotEndTheSearch()
        {
            var brothers = AddGame("Brothers: A Tale of Two Sons");
            var parser = RegisterSource(new VrModEntry { GameName = "Brothers: A Tale of Two Sons Remake", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var suggestions = _service.FindSuggestedGames(FindEntry(parser, "Brothers: A Tale of Two Sons Remake"));

            Assert.That(suggestions, Has.Count.EqualTo(1));
            Assert.That(suggestions[0].Game.Id, Is.EqualTo(brothers.Id));
            Assert.That(suggestions[0].Reason, Does.Contain("the listing adds 'remake'"));
            Assert.That(_service.FindGamesByIndex(FindEntry(parser, "Brothers: A Tale of Two Sons Remake")), Is.Empty);
        }

        /// <summary>
        /// The one case where nothing is suggested: the library shares not a single word with the
        /// listing, so there is no candidate to judge.
        /// </summary>
        [Test]
        public void FindSuggestedGames_NoSharedWordWithTheLibrarySuggestsNothing()
        {
            AddGame("Skyrim");
            var parser = RegisterSource(new VrModEntry { GameName = "Zoonomaly", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_service.FindSuggestedGames(FindEntry(parser, "Zoonomaly")), Is.Empty);
        }

        /// <summary>
        /// A confirmed mapping is a match on every later scan whatever either side is called: this is
        /// what a review is FOR, and it is the only route that survives a rename on the site.
        /// </summary>
        [Test]
        public void FindGamesByIndex_RestoresAGameTheUserConfirmedForThisListing()
        {
            var morrowind = AddGame("The Elder Scrolls III: Morrowind GOTY Edition");
            var parser = RegisterSource(new VrModEntry { GameName = "Morrowind", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            // Somebody accepted it in the review dialog (the decision is keyed by listing identity or
            // name plus the game id, which is what the restored index is cut from).
            _service.Settings.SetDecision(parser.SourceId, "Morrowind", morrowind.Id, MatchDecision.Accepted);
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var entry = FindEntry(parser, "Morrowind");
            Assert.That(_service.FindGamesByIndex(entry).Select(g => g.Id), Is.EquivalentTo(new[] { morrowind.Id }),
                "the confirmed pairing must be a match again without the name rules ever agreeing to it");
            Assert.That(_service.FindSuggestedGames(entry), Is.Empty,
                "an already-decided pair is not asked about twice");
        }

        /// <summary>
        /// A rejected candidate stays rejected. Suggestions re-derive candidates on every scan, so
        /// without this the same refused pairing would be back in the review queue forever.
        /// </summary>
        [Test]
        public void FindSuggestedGames_DoesNotResurfaceAPairSomebodyRejected()
        {
            var morrowind = AddGame("The Elder Scrolls III: Morrowind GOTY Edition");
            var parser = RegisterSource(new VrModEntry { GameName = "Morrowind", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            _service.Settings.SetDecision(parser.SourceId, "Morrowind", morrowind.Id, MatchDecision.Rejected);

            Assert.That(_service.FindSuggestedGames(FindEntry(parser, "Morrowind")), Is.Empty);
        }

        /// <summary>
        /// A different product is not a candidate, in either direction. Without this the review dialog
        /// would be full of "Doom" next to "DOOM 3" — the mistake the substring rule was written to
        /// prevent, re-opened by the feature meant to widen matching.
        /// </summary>
        [Test]
        public void FindSuggestedGames_RefusesOrdinalDisagreements()
        {
            AddGame("DOOM 3");
            AddGame("Borderlands 2");
            var parser = RegisterSource(
                new VrModEntry { GameName = "Doom", Category = "UEVR" },
                new VrModEntry { GameName = "Borderlands 2", Category = "UEVR" });

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_service.FindSuggestedGames(FindEntry(parser, "Doom")), Is.Empty);
            Assert.That(_service.FindSuggestedGames(FindEntry(parser, "Borderlands 2")), Is.Empty);
        }

        /// <summary>
        /// The review queue is where a human decides, so its size and order are design decisions: at
        /// most <see cref="VrModTagService.SuggestionRowsPerEntry"/> rows per listing, the tier the
        /// rules are closest to certain about first.
        /// </summary>
        [Test]
        public void FindSuggestedGames_CapsTheRowsAndPutsTheCertainTierFirst()
        {
            AddGame("Redout Space Legend");
            AddGame("Redout Space Legend Gold");
            AddGame("Redout Space Legend Eternity");
            var parser = RegisterSource(new VrModEntry { GameName = "Redout Space Legend", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var suggestions = _service.FindSuggestedGames(FindEntry(parser, "Redout Space Legend"));

            Assert.That(suggestions, Has.Count.EqualTo(VrModTagService.SuggestionRowsPerEntry),
                "a listing can name a family the library holds several entries of; the dialog must not become an inventory");
            Assert.That(suggestions[0].Kind, Is.EqualTo(TokenRunKind.SameProduct),
                "re-release wording first: the tier a person can accept without reading");
            Assert.That(suggestions[0].Game.Name, Is.EqualTo("Redout Space Legend Gold"));
        }

        /// <summary>
        /// The carry filter on the service side: a listing and a library game that share one word are
        /// not a product family, and the review dialog must not be where a person is asked to say so.
        /// </summary>
        [Test]
        public void FindSuggestedGames_DoesNotSuggestAGameThatMerelySharesAWord()
        {
            AddGame("Valhalla Hills");
            var parser = RegisterSource(new VrModEntry { GameName = "Assassin's Creed Valhalla", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_service.FindSuggestedGames(FindEntry(parser, "Assassin's Creed Valhalla")), Is.Empty);
        }

        /// <summary>
        /// The rarest-word lookup is a bounded search, and the bound is real: when the rarest word a
        /// listing uses is carried by most of the library, the posting list is not a candidate set, it
        /// is the library. The search says so and returns nothing rather than walking it.
        /// </summary>
        [Test]
        public void FindSuggestedGames_StopsWhenThePostingListIsTheLibrary()
        {
            for (var i = 0; i < VrModTagService.SuggestionPostingCap + 5; i++)
                AddGame($"Zebra Games Pack {i}");
            var parser = RegisterSource(new VrModEntry { GameName = "Zebra Games Pack", Category = "UEVR" });

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_service.FindSuggestedGames(FindEntry(parser, "Zebra Games Pack")), Is.Empty,
                "the posting-list cap must refuse to search a third of the library");
        }

        /// <summary>
        /// The review dialog is where a suggestion is decided, so the no-rescan review path must show
        /// it — as a row marked Suggested, whose status text differs from an ordinary Pending row.
        /// </summary>
        [Test]
        public void BuildReviewMatchesFromCache_SuggestsRowsAndLabelsThemSuggested()
        {
            var morrowind = AddGame("The Elder Scrolls III: Morrowind GOTY Edition");
            var skyrim = AddGame("Skyrim");
            var parser = RegisterSource(
                new VrModEntry { GameName = "Morrowind", Category = "UEVR" },
                new VrModEntry { GameName = "Skyrim", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var rows = _service.BuildReviewMatchesFromCache();

            Assert.That(rows, Is.Not.Null);
            var forMorrowind = rows!.Where(r => r.PlayniteGame.Id == morrowind.Id).ToList();
            Assert.That(forMorrowind, Has.Count.EqualTo(1), "one row for the pairing, not a match row and a suggestion row");
            var row = forMorrowind[0];
            Assert.That(row.IsSuggestion, Is.True, "the pairing has to reach the dialog a person can decide in");
            Assert.That(row.GameDisplayName, Does.Contain("Morrowind"));
            Assert.That(row.Status, Is.EqualTo(MatchDecision.Pending));
            Assert.That(row.DisplayStatusText, Is.EqualTo("Suggested"));
            Assert.That(row.ReasonTooltip, Is.Not.Null.And.Not.Empty);
            Assert.That(row.TagsChanged, Is.False);

            // An ordinary match row is not labelled Suggested and carries no reason.
            var ordinary = rows.Single(r => !r.IsSuggestion && r.PlayniteGame.Id == skyrim.Id);
            Assert.That(ordinary.DisplayStatusText, Is.Not.EqualTo("Suggested"));
            Assert.That(ordinary.ReasonTooltip, Is.Null);
        }

        /// <summary>
        /// A plain scan tells a user who never opens a dialog that these pairings exist: the run
        /// summary reports them, and they are not counted as matches or applied as tags.
        /// </summary>
        [Test]
        public void RunFullScan_ReportsSuggestionsWithoutApplyingThem()
        {
            var morrowind = AddGame("The Elder Scrolls III: Morrowind GOTY Edition");
            var parser = RegisterSource(new VrModEntry { GameName = "Morrowind", Category = "UEVR" });

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_service.LastSuggestions, Is.Not.Null);
            Assert.That(_service.LastSuggestions!.Any(s => s.GameName.Contains("Morrowind")), Is.True);
            Assert.That(morrowind.TagIds ?? new List<Guid>(), Is.Empty,
                "a suggestion applies nothing until somebody accepts it");
            Assert.That(_service.FindGamesByIndex(FindEntry(parser, "Morrowind")), Is.Empty,
                "the listing is still not a match: a suggestion is a question, not a mapping");
        }

        /// <summary>
        /// A pair the shipped rules must answer as a question, not silence. The old substring rule
        /// refused it at similarity 0,500; after the token pass it has
        /// to be a question — the listing's one word is carried whole and the game adds one word the rules
        /// cannot classify as re-release wording — and "no candidate at all" is the failure mode to catch.
        /// </summary>
        [Test]
        public void CompareTokenRuns_MinecraftAgainstMinecraftLegendsIsAQuestionNotSilence()
        {
            var run = VrModNameMatcher.CompareTokenRuns("Minecraft", "Minecraft: Legends");

            Assert.That(run.Kind, Is.EqualTo(TokenRunKind.NeedsALook),
                "Unrelated here means the pair never reaches a person");
            Assert.That(run.SharedTokenCount, Is.EqualTo(1));
            Assert.That(run.SourceIsCarriedByGame, Is.True);
            Assert.That(run.ExtraGameTokens, Is.EqualTo(new[] { "legends" }));
        }

        /// <summary>
        /// The same pair through the scan: an index posting, a candidate, a ranked row. A library that also
        /// holds 'Minecraft Launcher' is the live shape, so the test says what happens to the second game too.
        /// </summary>
        [Test]
        public void FindSuggestedGames_MinecraftListingSurfacesMinecraftLegendsFromTheScan()
        {
            var legends = AddGame("Minecraft: Legends");
            var launcher = AddGame("Minecraft Launcher");
            var parser = RegisterSource(new VrModEntry { GameName = "Minecraft", Category = "UEVR" });

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var entry = FindEntry(parser, "Minecraft");
            var matched = _service.FindGamesByIndex(entry).Select(g => g.Id).ToList();
            var suggestions = _service.FindSuggestedGames(entry);

            Assert.That(suggestions.Select(s => s.Game.Id), Does.Contain(legends.Id),
                "193: the listing 'Minecraft' produced no candidate for the library's 'Minecraft: Legends'");
            Assert.That(suggestions.Single(s => s.Game.Id == legends.Id).Kind, Is.EqualTo(TokenRunKind.NeedsALook));
            Assert.That(suggestions.Single(s => s.Game.Id == legends.Id).Reason, Does.Contain("legends"));
            // Whatever the ordinary path decides about the launcher, it is accounted for exactly once:
            // either as a match or as a question, never as both.
            foreach (var id in matched)
                Assert.That(suggestions.Select(s => s.Game.Id), Does.Not.Contain(id),
                    "a game the ordinary path already matched is not also a question");
            Assert.That(matched.Contains(launcher.Id) ^ suggestions.Select(s => s.Game.Id).Contains(launcher.Id),
                Is.True, "'Minecraft Launcher' is neither matched nor asked about");
        }

        // ------------------------------------------------------------------ harness

        private Game AddGame(string name)
        {
            var game = new Game(name) { Id = Guid.NewGuid(), TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(game);
            return game;
        }

        private TestParser RegisterSource(params VrModEntry[] entries)
        {
            var parser = new TestParser($"src-{Guid.NewGuid()}", entries);
            _registry.Register(parser);
            return parser;
        }

        private static VrModEntry FindEntry(TestParser parser, string gameName)
            => parser.CachedEntries.Single(e => e.GameName == gameName);

        private class TestParser : BaseVrModParser
        {
            private readonly VrModEntry[] _entries;

            public TestParser(string sourceId, VrModEntry[] entries)
                : base(sourceId, "Test", "https://example.com/")
            {
                _entries = entries;
            }

            /// <summary>The entries as the scan saw them (SourceId stamped by the fetch pipeline).</summary>
            public IReadOnlyList<VrModEntry> CachedEntries => _entries;

            public override string? ChangeIdentifier => null;

            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult<IEnumerable<VrModEntry>>(_entries);

            public override string? GetCanonicalName(VrModEntry entry) => entry.GameName;
        }
    }
}
