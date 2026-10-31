using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    /// Guards for the catalog index behind per-game matching
    /// (<see cref="VrModTagService.GetCachedEntriesForGame"/>).
    ///
    /// Reconciling a library used to hand every cached catalog entry to the fuzzy matcher, per
    /// game: ~3 100 normalizations and up to ~3 100 Levenshtein tables for each of the ~130 games
    /// a rename batch touches. The index answers the same question by visiting only the entries
    /// that could possibly reach the threshold. That is only an optimization if the entries it
    /// visits are the same ones the full walk would have matched - which is what the differential
    /// test below checks, over a corpus wide enough to contain near-matches, sequels, substring
    /// traps, punctuation-only differences and names that differ wildly.
    /// </summary>
    [TestFixture]
    public class VrModCatalogIndexTests
    {
        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private IPlayniteAPI _mockApi = null!;
        private VrModSourceRegistry _registry = null!;

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
        }

        /// <summary>
        /// Real-looking catalog names, chosen to sit on both sides of every prune the index uses:
        /// sequels and numerals ("street fighter 6" vs "street fighter v"), proper substrings
        /// ("doom" inside "doom 3"), VR-suffix variants, accents, ampersands vs "and", a name that
        /// is empty after normalization, and long unrelated names.
        /// </summary>
        private static readonly string[] CatalogNames =
        {
            "Half-Life 2 VR",
            "Half Life 2",
            "DOOM",
            "DOOM 3",
            "DOOM Eternal VR",
            "BEHOLDER VR",
            "Beholder 2",
            "BoneTomahawk VR",
            "GORN",
            "Onward",
            "Arizona Sunshine",
            "Arizona Sunshine 2",
            "Arizona Sunshine II",
            "Resident Evil 7 VR",
            "resident evil vii",
            "Street Fighter 6",
            "street fighter v",
            "Devil May Cry 5",
            "devil may cry hd",
            "The Walking Dead: Saints & Sinners",
            "The Walking Dead Saints and Sinners",
            "Poppy Playtime Chapter 3",
            "Synth Riders",
            "Pavlov VR",
            "Pavlov VR - Police",
            "Blade & Sorcery",
            "Blade and Sorcery: Nomad",
            "Boneworks",
            "Boneworks Stronghold Single Player",
            "A Fish Named Grot",
            "Metro Awakening",
            "Hellsplit Arena",
            "Hellsplit: Arena",
            "République VR",
            "Cafe VR",
            "",
            "VR",
            "...",
            "Contractors",
            "Contractors ECS",
            "Eleven Table Tennis",
            "Eleven Table Tennis VR",
            "Until You Fall",
            "Under Mine",
            "Under Mine - Dig, Dive, Deliver",
            "Grid Runner VR",
            "Grave Danger VR",
            "After the Fall",
            "Nexomon: Extinction",
            "Cross Blade",
            "Crimson Skies High Altitude Edition VR"
        };

        /// <summary>
        /// Library titles, each one deliberately close to some catalog entry and far from the rest.
        /// </summary>
        private static readonly string[] GameNames =
        {
            "Half-Life 2",
            "Half Life II",
            "DOOM 3",
            "Doom Eternal",
            "Beholder",
            "Gorn",
            "ONWARD",
            "Arizona Sunshine II",
            "Resident Evil VII",
            "Street Fighter VI",
            "Devil May Cry HD",
            "The Walking Dead: Saints & Sinners",
            "Pavlov VR",
            "Blade and Sorcery",
            "Boneworks",
            "A Fish Named GROT",
            "Metro: Awakening",
            "Hellsplit Arena",
            "République",
            "Contractors",
            "Eleven Table Tennis",
            "Until You Fall",
            "Undermine",
            "After The Fall",
            "Unrelated Title With No Match Anywhere At All",
            "x",
            "VR"
        };

        /// <summary>
        /// The index must answer exactly what the full catalog walk answered, at every threshold a
        /// user can pick. A prune that silently drops a real match is the whole risk of this design,
        /// so the comparison is set equality against the exhaustive path, not a spot check.
        /// </summary>
        [TestCase(0.5f)]
        [TestCase(0.6f)]
        [TestCase(0.7f)]
        [TestCase(0.8f)]
        [TestCase(0.85f)]
        [TestCase(0.9f)]
        [TestCase(1.0f)]
        public void IndexedMatch_ReturnsExactlyWhatTheCatalogWalkWouldMatch(float threshold)
        {
            var settings = new VrModTagSettings { FuzzyMatchThreshold = threshold };
            var service = NewService(settings, out _);
            // Compare against what the service actually cached, not against the list this test
            // handed the parser: the cached objects are the ones both paths can return.
            var cached = service.AllCachedEntries.ToList();
            var library = GameNames.Select(n =>
            {
                var game = new Game(n) { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
                _games.Items.Add(game);
                return game;
            }).ToList();

            var prunedCandidates = 0;
            foreach (var game in library)
            {
                var exhaustive = service.GetVrModEntriesForGame(game, cached).ToList();
                var indexed = service.GetCachedEntriesForGame(game);

                var missing = exhaustive.Except(indexed).ToList();
                var extra = indexed.Except(exhaustive).ToList();
                Assert.That(extra, Is.Empty,
                    $"threshold {threshold}: the index matched {extra.Count} entr" +
                    $"(y/ies) the catalog walk rejected for '{game.Name}': {Names(extra)}");
                Assert.That(missing, Is.Empty,
                    $"threshold {threshold}: the index dropped match" +
                    (missing.Count == 1 ? " " : "es ") + $"the catalog walk found for '{game.Name}': {Names(missing)}");

                // Counting candidates the length band skipped, to prove the index really does skip
                // work rather than quietly falling back to scanning everything.
                prunedCandidates += cached.Count - IndexedCandidateBudget(service, game, threshold, cached);
            }

            Assert.That(prunedCandidates, Is.GreaterThan(0),
                "the corpus contained nothing for the index to skip, so this test proved nothing");
        }

        /// <summary>
        /// How many catalog entries the index has to score for one game: the ones whose normalized
        /// length could still reach the threshold. It grows with the shape of the names, not with the
        /// catalog, which is the difference between 3 100 edit distances per game and a dozen.
        /// </summary>
        private static int IndexedCandidateBudget(VrModTagService service, Game game, float threshold, List<VrModEntry> entries)
        {
            var normalized = VrModNameMatcher.Normalize(VrModTagService.NormalizeForMatchFast(game.Name));
            var low = (int)Math.Floor(threshold * normalized.Length) - 1;
            var high = threshold > 0f ? (int)Math.Ceiling(normalized.Length / threshold) + 1 : int.MaxValue;
            return entries.Count(e =>
            {
                var length = VrModNameMatcher.Normalize(VrModTagService.NormalizeForMatchFast(e.GameName)).Length;
                return length >= low && length <= high;
            });
        }

        /// <summary>
        /// An entry that stops being in the cache must stop being matchable, and one that arrives
        /// with a cache refresh must become matchable: the index is only allowed to describe the
        /// cache it was built from. Without this, a stale index would keep offering links for mods
        /// a source stopped listing.
        /// </summary>
        [Test]
        public void IndexFollowsTheCacheItWasBuiltFrom()
        {
            var parser = new CatalogParser("Hellsplit Arena", "After the Fall");
            _registry.Register(parser);
            var service = new VrModTagService(_mockApi, new VrModTagSettings(), new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();

            var game = new Game("Hellsplit Arena") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(game);

            Assert.That(service.GetCachedEntriesForGame(game).Select(e => e.GameName),
                Is.EquivalentTo(new[] { "Hellsplit Arena" }));

            service.ClearSourceCache(_registry.Sources.Single().SourceId);
            Assert.That(service.GetCachedEntriesForGame(game), Is.Empty,
                "the cache was cleared for that source and the index still offered its entry");

            // The same source, a different listing: the site dropped the old mod and added a
            // renamed one. The index must describe the catalog the cache holds now - which means
            // the renamed entry is matchable, and the game whose only match disappeared stops
            // matching. (It does not match "Hellsplit Arena Remastered": the matcher rejects a
            // proper substring pair on purpose, so an extension of a title is a different game.)
            parser.Serve("Hellsplit Arena Remastered");
            service.RunFullScanAsync().GetAwaiter().GetResult();

            var remastered = new Game("Hellsplit Arena Remastered") { TagIds = new List<Guid>(), Links = new ObservableCollection<Link>() };
            _games.Items.Add(remastered);

            Assert.That(service.GetCachedEntriesForGame(remastered).Select(e => e.GameName),
                Is.EquivalentTo(new[] { "Hellsplit Arena Remastered" }),
                "a refreshed cache did not reach the index");
            Assert.That(service.GetCachedEntriesForGame(game), Is.Empty,
                "the index still offered the entry this source no longer lists");
        }

        /// <summary>
        /// The character-count prune is the aggressive one: it rejects a candidate whose letters
        /// cannot supply the shared characters a Levenshtein similarity of at least the threshold
        /// requires. Checked both directions over a random corpus, the way
        /// <c>PruneBound_NeverRejectsAPairThatWouldHaveMatched</c> checks the length prune.
        /// </summary>
        [Test]
        public void CharacterPrune_NeverRejectsAPairThatWouldHaveMatched()
        {
            var random = new System.Random(10707);
            const float threshold = 0.8f;
            var pruned = 0;

            for (var iter = 0; iter < 600; iter++)
            {
                var a = VrModNameMatcher.Normalize(RandomName(random));
                var b = VrModNameMatcher.Normalize(RandomName(random));
                var maxLen = Math.Max(a.Length, b.Length);
                var overlap = LetterOverlapUpperBound(a, b);

                if (overlap < threshold * maxLen - 1)
                {
                    pruned++;
                    Assert.That(VrModNameMatcher.ComputeSimilarity(a, b), Is.LessThan(threshold),
                        $"pruned '{a}' vs '{b}' (letter overlap {overlap}) but its real similarity was " +
                        $"{VrModNameMatcher.ComputeSimilarity(a, b):F3}");
                }
                else
                {
                    // The other direction: a pair that really does reach the threshold must never
                    // fall below the bound.
                    if (VrModNameMatcher.ComputeSimilarity(a, b) >= threshold)
                        Assert.That(overlap, Is.GreaterThanOrEqualTo((int)Math.Ceiling(threshold * maxLen) - 1),
                            $"matching pair '{a}' vs '{b}' had letter overlap {overlap}, below its requirement");
                }
            }

            Assert.That(pruned, Is.GreaterThan(0),
                "the corpus contained no pair for the character prune to reject, so this test proved nothing");
        }

        private static int LetterOverlapUpperBound(string a, string b)
        {
            var counts = new int[27];
            foreach (var ch in a)
            {
                if (ch >= 'a' && ch <= 'z') counts[ch - 'a']++;
                else counts[26]++;
            }
            var other = new int[27];
            foreach (var ch in b)
            {
                if (ch >= 'a' && ch <= 'z') other[ch - 'a']++;
                else other[26]++;
            }
            var sum = 0;
            for (var i = 0; i < counts.Length; i++) sum += Math.Min(counts[i], other[i]);
            return sum;
        }

        private static string RandomName(System.Random random)
        {
            var words = new[] { "vr", "arena", "hell", "split", "the", "fall", "after", "grid", "runner", "high", "altitude", "edition", "1", "ii", "vii", "cafe", "de", "el" };
            var count = random.Next(1, 6);
            return string.Join(" ", Enumerable.Range(0, count).Select(_ => words[random.Next(words.Length)]));
        }

        private static string Names(IEnumerable<VrModEntry> entries) =>
            string.Join(", ", entries.Select(e => $"'{e.GameName}'"));

        private VrModTagService NewService(VrModTagSettings settings, out List<VrModEntry> entries)
        {
            entries = CatalogNames.Select(n => new VrModEntry
            {
                GameName = n,
                Category = "UEVR",
                SourceUrl = "https://example.com/game/" + n.Replace(' ', '-')
            }).ToList();
            _registry.Register(new CatalogParser(CatalogNames));
            var service = new VrModTagService(_mockApi, settings, new NullLogger(), _registry);
            service.RunFullScanAsync().GetAwaiter().GetResult();   // fills the cache the index is built from
            return service;
        }

        private class CatalogParser : BaseVrModParser
        {
            private List<VrModEntry> _entries = new();
            public CatalogParser(params string[] names) : base($"catalog-{Guid.NewGuid()}", "Catalog", "https://example.com/")
            {
                Serve(names);
            }

            /// <summary>
            /// Replace what this source lists, so a test can rescan and see a different catalog.
            /// </summary>
            public void Serve(params string[] names) =>
                _entries = names.Select(n => new VrModEntry { GameName = n, Category = "UEVR" }).ToList();

            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult<IEnumerable<VrModEntry>>(_entries);
            public override string? GetCanonicalName(VrModEntry entry) => StripVrSuffixes(entry.GameName);
        }
    }
}
