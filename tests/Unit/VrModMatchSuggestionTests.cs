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
        /// The review queue is where a human decides, so its size and order are design decisions: every
        /// candidate the gates admit is a row, the tier the rules are closest to certain about first.
        /// There used to be a cap of two rows per listing, and it hid the pair this feature exists to
        /// surface (task 213, decision 5-i): a candidate the rules suspect is cheaper for a person to
        /// read and dismiss than a wrong decision that comes back next scan.
        ///
        /// The Gold row is gone from this fixture on purpose. 'Redout Space Legend Gold' differs from the
        /// listing by re-release wording alone, and that is now asserted rather than asked about — the
        /// same change that took 'Tomb Raider: Game of the Year Edition' out of the queue. What stays is
        /// what a person still has to read: wording the rules agree is packaging but cannot assert is the
        /// same product ('Deluxe Edition' is its own store entry), and a word the listing has never heard
        /// ('Eternity').
        /// </summary>
        [Test]
        public void FindSuggestedGames_ReturnsEveryCandidateAndPutsTheCertainTierFirst()
        {
            var plain = AddGame("Redout Space Legend");
            var gold = AddGame("Redout Space Legend Gold");
            AddGame("Redout Space Legend Deluxe Edition");
            AddGame("Redout Space Legend Eternity");
            var parser = RegisterSource(new VrModEntry { GameName = "Redout Space Legend", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var entry = FindEntry(parser, "Redout Space Legend");
            Assert.That(_service.FindGamesByIndex(entry).Select(g => g.Id),
                Is.EquivalentTo(new[] { plain.Id, gold.Id }),
                "a library copy whose only difference is re-release wording is matched, not queued");

            var suggestions = _service.FindSuggestedGames(entry);

            Assert.That(suggestions, Has.Count.EqualTo(2),
                "both candidates the gates admit, neither cut: a listing can name a family the library "
                + "holds several entries of, and the person reviewing it says which");
            Assert.That(suggestions[0].Kind, Is.EqualTo(TokenRunKind.SameProduct),
                "re-release wording first: the tier a person can accept without reading");
            Assert.That(suggestions[0].Game.Name, Is.EqualTo("Redout Space Legend Deluxe Edition"));
        }

        /// <summary>
        /// The no-cap rule stated as a number, because "show them all" is only testable as one. The fixture
        /// is a listing whose library holds four copies of one product: one carries re-release wording the
        /// token rules read as the same product, three carry title wording they will not assert. The cap that
        /// used to end the search showed a person two of the four. Measured order on this fixture: Deluxe
        /// Edition (SameProduct tier, 2 words over, 0.40), Eternity (1 over, 0.53), Collector Pack (2 over,
        /// 0.40), Year Of The Wolf (4 over, 0.37) — the tier and the leftover counts, not the numbers a
        /// person sees in the Score column.
        /// </summary>
        [Test]
        public void FindSuggestedGames_AFamilyOfFourCandidatesIsFourRowsNotTwo()
        {
            AddGame("Alpha Beta Deluxe Edition");
            AddGame("Alpha Beta Eternity");
            AddGame("Alpha Beta Collector Pack");
            AddGame("Alpha Beta Year Of The Wolf");
            var parser = RegisterSource(new VrModEntry { GameName = "Alpha Beta", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var entry = FindEntry(parser, "Alpha Beta");
            Assert.That(_service.FindGamesByIndex(entry), Is.Empty,
                "none of these four is wording the name rules assert, which is why all four are rows");

            var suggestions = _service.FindSuggestedGames(entry);

            Assert.That(suggestions, Has.Count.EqualTo(4), "the candidate list is not cut to two (task 213)");
            Assert.That(suggestions.Select(s => s.Game.Name), Is.EquivalentTo(new[]
            {
                "Alpha Beta Eternity", "Alpha Beta Collector Pack",
                "Alpha Beta Deluxe Edition", "Alpha Beta Year Of The Wolf",
            }));
            Assert.That(suggestions.Select(s => s.Kind), Is.EqualTo(new[]
            {
                TokenRunKind.SameProduct, TokenRunKind.NeedsALook, TokenRunKind.NeedsALook, TokenRunKind.NeedsALook,
            }), "re-release wording heads the list: the tier a person can accept without reading");
            Assert.That(suggestions[0].Game.Name, Is.EqualTo("Alpha Beta Deluxe Edition"),
                "the tier outranks a smaller leftover count and a higher displayed score: 'Eternity' leaves one word over and scores 0.53 against 0.40, and is still second");
            Assert.That(suggestions.Skip(1).Select(s => s.LeftoverTokenCount), Is.EqualTo(new[] { 1, 2, 4 }),
                "inside one tier the tighter claim on the pair comes first, whatever the Score column shows");
            Assert.That(suggestions[3].Game.Name, Is.EqualTo("Alpha Beta Year Of The Wolf"),
                "the weakest reading is still a row: it is last, and that is what last is for");
        }

        /// <summary>
        /// The whole point of the re-release rule, at the level a person experiences it: the library holds
        /// the same product under the names the stores sold it under, the catalog names it once, and every
        /// copy the person owns gets tagged. 'Anniversary' is in this fixture because it looks like the
        /// same kind of wording and is not — it names the 2007 remake of the 1996 game, so it stays out.
        /// </summary>
        [Test]
        public void FindGamesByIndex_ReachesEveryStoreCopyOfAProductNamedByAReReleaseTail()
        {
            var plain = AddGame("Tomb Raider");
            var goty = AddGame("Tomb Raider: Game of the Year Edition");
            var gotyShouted = AddGame("Tomb Raider GAME OF THE YEAR EDITION");
            var remake = AddGame("Tomb Raider: Anniversary");
            var parser = RegisterSource(new VrModEntry { GameName = "Tomb Raider", Category = "UEVR" });

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var entry = FindEntry(parser, "Tomb Raider");
            var matched = _service.FindGamesByIndex(entry).Select(g => g.Id).ToList();
            Assert.That(matched, Is.EquivalentTo(new[] { plain.Id, goty.Id, gotyShouted.Id }),
                "re-release wording is the only difference between these three library records");
            Assert.That(matched, Does.Not.Contain(remake.Id),
                "'Anniversary' is not re-release wording; it names a different product");
            foreach (var id in matched)
                Assert.That(_games.Items.Single(g => g.Id == id).TagIds, Is.Not.Empty,
                    $"{id} is a copy of the product the listing names, so it gets the mod's tags");

            // The remake stays a question. The rules refuse it as a match and the review dialog is where
            // somebody says whether they want the mod on it - which is the other half of this rule: it
            // asserts the copies whose only difference is re-release wording, and leaves everything else
            // to be read.
            var suggestions = _service.FindSuggestedGames(entry);
            Assert.That(suggestions, Has.Count.EqualTo(1));
            Assert.That(suggestions[0].Game.Id, Is.EqualTo(remake.Id));
        }

        /// <summary>
        /// Why the re-release key is an EXTRA key on library names and never a rewrite of either side: the
        /// words a name wrote are what its comparison tolerates. 'Edition' is what puts this catalog name
        /// one edit from the library's misspelling (0.944); with it taken off, the same pair is 0.588 and
        /// the substring tier refuses it as well, because 'doom 3 bfg' sits inside the longer name.
        /// </summary>
        [Test]
        public void FindGamesByIndex_KeepsTheWordingTheListingItselfWrote()
        {
            var misspelled = AddGame("DOOM 3 BFG Editon");
            var parser = RegisterSource(new VrModEntry { GameName = "DOOM 3 BFG Edition", Category = "UEVR" });

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_service.FindGamesByIndex(FindEntry(parser, "DOOM 3 BFG Edition")).Select(g => g.Id),
                Is.EquivalentTo(new[] { misspelled.Id }));
        }

        /// <summary>
        /// The guard against the wider rule this library was offered: taking packaging words out of names
        /// wherever they sit merges titles that merely share one. Every game here is a real library name
        /// that such a rule matched to 'F.E.A.R. VR' — 'Layers of Fear' because the dots in F.E.A.R. leave
        /// the key reading as the word 'fear', 'Dandara: Trials of Fear Edition' because 'Edition' is a
        /// word it would drop.
        /// </summary>
        [Test]
        public void FindGamesByIndex_DoesNotMergeTitlesThatMerelyShareAPackagingWord()
        {
            var fear = AddGame("F.E.A.R.");
            AddGame("Layers of Fear");
            AddGame("Dandara: Trials of Fear Edition");
            AddGame("The Outer Wilds");
            var parser = RegisterSource(new VrModEntry { GameName = "F.E.A.R. VR", Category = "UEVR" });

            _service.RunFullScanAsync().GetAwaiter().GetResult();

            Assert.That(_service.FindGamesByIndex(FindEntry(parser, "F.E.A.R. VR")).Select(g => g.Id),
                Is.EquivalentTo(new[] { fear.Id }),
                "only the record that IS F.E.A.R. is matched");
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
        /// The rarest word is a guess about which word a listing names its product with, and it can be
        /// wrong. A word that labels how a product was reached ('mod') posts to few games, so it wins a
        /// rarest-first choice, while every game carrying it is some other product the token rules
        /// refuse - and under a single pick the whole search ends there, with the listing offered nothing
        /// even though the word that does name the product was sitting in the same listing name.
        ///
        /// This is the real library and the real catalog name behind it: three library copies of
        /// 'Yooka-Laylee' and the RototRobot listing 'Yooka-Laylee Mod VookaRaylee by Eusth'. Posting
        /// counts are a property of the whole library, which is what makes this fragile rather than
        /// local: measured over 1 348 listing names against a 3 045-game library, a normalizer change to
        /// ONE library name moved 'mod' from 6 postings to 5, 'mod' became rarer than 'yooka', and three
        /// correct pairings vanished. So the listing's words are now tried rarest-first and the search
        /// stops at the first group of equally rare words whose postings survive the token-run rules.
        /// </summary>
        [Test]
        public void FindSuggestedGames_AWordWhoseGamesAreAllRefusedDoesNotEndTheSearch()
        {
            // 'mod' is the rarer word here (1 game carries it, 2 carry 'yooka') and its single game is a
            // different product: 'Just Cause 2: Multiplayer Mod' shares one word with the listing and
            // neither name's words are the whole of the other's run.
            var first = AddGame("Yooka-Laylee");
            var second = AddGame("Yooka-Laylee");
            AddGame("Just Cause 2: Multiplayer Mod");
            var parser = RegisterSource(new VrModEntry { GameName = "Yooka-Laylee Mod VookaRaylee by Eusth", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var entry = FindEntry(parser, "Yooka-Laylee Mod VookaRaylee by Eusth");
            Assert.That(_service.FindGamesByIndex(entry), Is.Empty,
                "0.42 of ten as whole strings: the name rules cannot assert this pair, which is why it is a review row");

            var suggestions = _service.FindSuggestedGames(entry);
            Assert.That(suggestions, Has.Count.EqualTo(2),
                "the rarer word that named nothing must not end the search");
            Assert.That(suggestions.Select(s => s.Game.Id), Is.EquivalentTo(new[] { first.Id, second.Id }));
            foreach (var suggestion in suggestions)
            {
                Assert.That(suggestion.Kind, Is.EqualTo(TokenRunKind.NeedsALook));
                Assert.That(suggestion.Reason, Does.Contain("the listing adds 'mod vookaraylee'"),
                    "the leftover wording is named, including the word that almost hijacked the search");
            }
        }

        /// <summary>
        /// Words of equal rarity are searched as one group. A posting count is a fact about the whole
        /// library, so two words carried by the same number of games are equally good evidence about a
        /// listing — and if the search stopped at the first of them, the order the listing happened to
        /// write its words in would decide which rows a person is offered. Measured on the live catalog:
        /// 44 of 1 348 listing names tie for their rarest library-present word; searching the group costs
        /// 57 CompareTokenRuns calls out of 7 569 (+0.8%) and adds one candidate.
        /// </summary>
        [Test]
        public void FindSuggestedGames_WordsOfEqualRarityAreAllSearchedBeforeTheSearchStops()
        {
            // Every word of the listing is carried by exactly one library game, so 'alpha', 'beta' and
            // 'gamma' tie at one posting each. 'alpha' reaches "Alpha Beta" (the game's words are the
            // first two of the listing's), and 'gamma' reaches "Gamma"; under a per-word stop the second
            // pairing would never be looked at because the first word already named something.
            var shorterGame = AddGame("Alpha Beta");
            var oneWordGame = AddGame("Gamma");
            var parser = RegisterSource(new VrModEntry { GameName = "Alpha Beta Gamma", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var entry = FindEntry(parser, "Alpha Beta Gamma");
            Assert.That(_service.FindGamesByIndex(entry), Is.Empty,
                "0.74 and 0.45 of a whole name: the name rules cannot assert either pairing");

            var suggestions = _service.FindSuggestedGames(entry);
            Assert.That(suggestions.Select(s => s.Game.Id), Is.EquivalentTo(new[] { shorterGame.Id, oneWordGame.Id }),
                "the tied words are one search unit, not a race decided by listing word order");
            Assert.That(suggestions[0].Game.Id, Is.EqualTo(shorterGame.Id),
                "the pairing whose shared words cover more of both names is offered first");
            Assert.That(suggestions[0].SharedTokenCount, Is.EqualTo(2));
            Assert.That(suggestions[1].SharedTokenCount, Is.EqualTo(1));
        }

        /// <summary>
        /// Candidates from one rarity group are ranked by what the pair says, not by the whole-name
        /// similarity the review dialog displays. "Alpha Beta Gamma" against "Alpha Beta Gamma Delta
        /// Epsilon Zeta" is 0.64 similar and against "Alpha Beta" is 0.74 — yet the first is where every
        /// word of the listing sits inside one run, and the second leaves a title word unexplained. The
        /// display score is the last key, for candidates the coverage facts leave level.
        /// </summary>
        [Test]
        public void FindSuggestedGames_RanksByWhatTheSharedWordsExplainBeforeTheDisplayScore()
        {
            var extendedGame = AddGame("Alpha Beta Gamma Delta Epsilon Zeta");
            var shorterGame = AddGame("Alpha Beta");
            // Filler so all three of the listing's words tie at two postings and land in one group:
            // 'gamma' would otherwise be rarer than 'alpha' and 'beta' and form a group of its own.
            // "Gamma Something Else" shares one word with the listing and is refused by the carry rule.
            AddGame("Gamma Something Else");
            var parser = RegisterSource(new VrModEntry { GameName = "Alpha Beta Gamma", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var suggestions = _service.FindSuggestedGames(FindEntry(parser, "Alpha Beta Gamma"));
            Assert.That(suggestions, Has.Count.EqualTo(2));
            Assert.That(suggestions[0].Game.Id, Is.EqualTo(extendedGame.Id),
                "three shared words beat two, even though the whole-name score says the opposite");
            Assert.That(suggestions[0].SharedTokenCount, Is.EqualTo(3));
            Assert.That(suggestions[0].DisplayScore, Is.LessThan(suggestions[1].DisplayScore),
                "the displayed score is not the ranking key, and the row says why in its Reason");
            Assert.That(suggestions[0].Kind, Is.EqualTo(suggestions[1].Kind),
                "the verdict kind is equal here, so coverage is what orders them");
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
            Assert.That(row.CandidateTotal, Is.EqualTo(1), "this listing named one candidate");
            Assert.That(row.CandidateRank, Is.EqualTo(1));
            Assert.That(row.TagsChanged, Is.False);

            // An ordinary match row is not labelled Suggested and carries no reason.
            var ordinary = rows.Single(r => !r.IsSuggestion && r.PlayniteGame.Id == skyrim.Id);
            Assert.That(ordinary.DisplayStatusText, Is.Not.EqualTo("Suggested"));
            Assert.That(ordinary.ReasonTooltip, Is.Null);
        }

        /// <summary>
        /// A listing that names a family puts every candidate in the dialog, and the rank the search put
        /// them in travels with the row. The grid sorts rows by game name, so the evidence order is not
        /// the order a person sees them in — without the rank in the tooltip, several rows of one listing
        /// cannot be told apart by how sure the rules were.
        /// </summary>
        [Test]
        public void BuildReviewMatchesFromCache_CarriesTheEvidenceRankOfEachCandidateOnItsRow()
        {
            AddGame("Alpha Beta Eternity");
            AddGame("Alpha Beta Year Of The Wolf");
            var parser = RegisterSource(new VrModEntry { GameName = "Alpha Beta", Category = "UEVR" });
            _service.RunFullScanAsync().GetAwaiter().GetResult();

            var rows = _service.BuildReviewMatchesFromCache();
            Assert.That(rows, Is.Not.Null);
            var candidates = rows!.Where(r => r.IsSuggestion).OrderBy(r => r.CandidateRank).ToList();
            Assert.That(candidates, Has.Count.EqualTo(2));
            Assert.That(candidates.Select(r => r.CandidateRank), Is.EqualTo(new[] { 1, 2 }));
            foreach (var candidate in candidates)
            {
                Assert.That(candidate.CandidateTotal, Is.EqualTo(2));
                Assert.That(candidate.ReasonTooltip, Does.Contain($"candidate {candidate.CandidateRank} of 2"));
            }
            Assert.That(candidates[0].PlayniteGame.Name, Is.EqualTo("Alpha Beta Eternity"),
                "rank 1 is the pairing the evidence puts first, not the first row the grid happens to show");
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
