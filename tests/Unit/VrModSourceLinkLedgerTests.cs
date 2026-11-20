using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// The source-link ledger: one settings map recording every Link row this plugin wrote, so a row
    /// stays removable after the match, the tag and the decision that produced it are all gone.
    ///
    /// Two layers are under test here. The stored format, because a claim that fails to round-trip is a
    /// row nobody can prove is ours - which is the exact failure the ledger exists to prevent, so a
    /// format bug reproduces the leak it fixes. And the map's write discipline, because Playnite
    /// serializes this map while a scan writes it (see <see cref="VrModSettingsPublicationTests"/> for
    /// the same argument about the three decision maps).
    /// </summary>
    [TestFixture]
    public class VrModSourceLinkLedgerTests
    {
        private static readonly Guid GameA = Guid.Parse("11111111-2222-3333-4444-555555555555");
        private static readonly Guid GameB = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        #region The stored claim format

        [Test]
        public void A_plain_claim_round_trips()
        {
            var stored = new SourceLinkClaim("PcvrCentral", "PCVR Central", "https://pcvr-central.com/games/hl/").Encode();

            var parsed = SourceLinkClaim.Parse(stored);

            Assert.That(parsed, Has.Length.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(parsed[0].SourceId, Is.EqualTo("PcvrCentral"));
                Assert.That(parsed[0].Name, Is.EqualTo("PCVR Central"));
                Assert.That(parsed[0].Url, Is.EqualTo("https://pcvr-central.com/games/hl/"));
            });
        }

        /// <summary>
        /// The two characters that carry structure in the stored form are ';' (between claims) and '|'
        /// (between the parts of one claim). Both are legal inside a URL and a display name, so the
        /// format has to survive them: unescaped, one claim with a ';' in its URL would be read back as
        /// two corrupt claims, and the row it describes would become unremovable litter.
        /// </summary>
        [Test]
        public void Separators_inside_a_url_or_a_name_round_trip()
        {
            const string nastyUrl = "https://example.com/a;b?x=1||2&y=%3B";
            const string nastyName = "A;B||C\\D";

            var stored = new SourceLinkClaim("src-1", nastyName, nastyUrl).Encode();

            // The escaped form must not itself contain a bare structural character, or the split that
            // reads it back would be a guess.
            Assert.That(stored.Split(';'), Has.Length.EqualTo(1), "an escaped claim still splits on ';'");

            var parsed = SourceLinkClaim.Parse(stored);
            Assert.That(parsed, Has.Length.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(parsed[0].Name, Is.EqualTo(nastyName));
                Assert.That(parsed[0].Url, Is.EqualTo(nastyUrl));
            });
        }

        [Test]
        public void Several_claims_on_one_game_round_trip_in_order()
        {
            var claims = new[]
            {
                new SourceLinkClaim("src-a", "A", "https://a.example/x"),
                new SourceLinkClaim("src-b", "B;named", "https://b.example/y"),
                new SourceLinkClaim("src-c", "C", "https://c.example/z?q=1||2")
            };

            var parsed = SourceLinkClaim.Parse(SourceLinkClaim.EncodeAll(claims));

            Assert.That(parsed, Has.Length.EqualTo(3));
            Assert.That(parsed.Select(c => c.SourceId), Is.EqualTo(new[] { "src-a", "src-b", "src-c" }));
            Assert.That(parsed[1].Name, Is.EqualTo("B;named"));
            Assert.That(parsed[2].Url, Is.EqualTo("https://c.example/z?q=1||2"));
        }

        /// <summary>A part that cannot be a claim must be dropped, never half-guessed: every consumer of
        /// a claim treats it as a removal instruction, so a partly-parsed claim is a row we might delete
        /// for the wrong reason.</summary>
        [Test]
        public void A_part_that_is_not_a_claim_is_dropped_and_the_good_ones_survive()
        {
            var stored = "garbage"
                + ";" + "only-two" + SourceLinkClaim.PartSeparator + "parts"
                + ";" + SourceLinkClaim.PartSeparator + SourceLinkClaim.PartSeparator + "no-source-id"
                + ";" + new SourceLinkClaim("src-ok", "Ok", "https://ok.example/").Encode();

            var parsed = SourceLinkClaim.Parse(stored);

            Assert.That(parsed, Has.Length.EqualTo(1));
            Assert.That(parsed[0].SourceId, Is.EqualTo("src-ok"));
        }

        [Test]
        public void An_empty_or_null_value_is_no_claims()
        {
            Assert.Multiple(() =>
            {
                Assert.That(SourceLinkClaim.Parse(null), Has.Length.EqualTo(0));
                Assert.That(SourceLinkClaim.Parse(string.Empty), Has.Length.EqualTo(0));
            });
        }

        /// <summary>Ownership is name AND url. URL alone would also match a link the user typed that
        /// happens to point at the same page, and deleting a user's own link is the one thing this
        /// whole mechanism must never do.</summary>
        [Test]
        public void A_claim_owns_only_the_row_it_wrote()
        {
            var claim = new SourceLinkClaim("src", "Test", "https://example.com/game/x");

            Assert.Multiple(() =>
            {
                Assert.That(claim.OwnsRow("Test", "https://example.com/game/x"), Is.True);
                Assert.That(claim.OwnsRow("Test", "https://EXAMPLE.com/game/x"), Is.True, "host case is not authorship evidence either way");
                Assert.That(claim.OwnsRow("My own link", "https://example.com/game/x"), Is.False, "the same URL under another name is the user's link");
                Assert.That(claim.OwnsRow("Test", "https://example.com/game/other"), Is.False, "the same name on another URL is a different row");
                Assert.That(claim.OwnsRow(null, "https://example.com/game/x"), Is.False);
            });
        }

        #endregion

        #region The settings map

        [Test]
        public void Recording_a_claim_is_idempotent_and_publishes_nothing_the_second_time()
        {
            var settings = new VrModTagSettings();

            Assert.That(settings.RecordLinkClaim(GameA, "src", "Test", "https://example.com/x"), Is.True);
            var published = settings.AddedLinksByGame;

            Assert.That(settings.RecordLinkClaim(GameA, "src", "Test", "https://example.com/x"), Is.False);

            Assert.Multiple(() =>
            {
                Assert.That(settings.AddedLinksByGame, Is.SameAs(published), "a write that changed nothing swapped the published map anyway");
                Assert.That(settings.LinkClaimCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void A_map_handed_to_a_caller_is_never_mutated_by_a_later_ledger_write()
        {
            var settings = new VrModTagSettings();
            settings.RecordLinkClaim(GameA, "src", "Test", "https://example.com/x");

            var published = settings.AddedLinksByGame;
            var snapshot = published.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // Every writer shape the ledger has: add, remove, prune, clear-one-game, clear-all, adopt.
            settings.RecordLinkClaim(GameB, "src", "Test", "https://example.com/y");
            settings.RemoveLinkClaim(GameA, "src", "https://example.com/x");
            settings.PruneLinkClaims(GameB, new[] { "https://elsewhere.example/" });
            settings.ClearLinkClaimsForGame(GameB);
            settings.AdoptLinkClaims(new[] { (GameA, "src", "Test", "https://example.com/z") });
            settings.ClearAllLinkClaims();

            Assert.Multiple(() =>
            {
                Assert.That(published, Is.EquivalentTo(snapshot), "the ledger a caller is walking was mutated");
                Assert.That(settings.AddedLinksByGame, Is.Not.SameAs(published), "a ledger write reused the published instance instead of publishing a new one");
            });
        }

        [Test]
        public void A_walk_survives_a_scan_and_a_review_dialog_writing_the_ledger_at_the_same_time()
        {
            // The shape of the real collision: Playnite serializing the settings on one thread while a
            // scan records and drops claims, and the review dialog doing the same from the UI thread.
            var settings = new VrModTagSettings();
            for (var i = 0; i < 200; i++)
            {
                settings.RecordLinkClaim(Guid.NewGuid(), "src", "Test", $"https://example.com/seed/{i}");
            }

            var failures = new List<string>();
            var writes = 0;
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var scanThread = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var gameId = Guid.NewGuid();
                        settings.RecordLinkClaim(gameId, "src", "Test", "https://example.com/scan");
                        settings.PruneLinkClaims(gameId, Array.Empty<string>());
                        Interlocked.Increment(ref writes);
                    }
                });

                var dialogThread = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        settings.AdoptLinkClaims(new[] { (GameA, "src", "Test", "https://example.com/dialog") });
                        settings.ClearLinkClaimsForGame(GameA);
                        Interlocked.Increment(ref writes);
                    }
                });

                var walker = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            foreach (var kvp in settings.AddedLinksByGame)
                            {
                                var parsed = SourceLinkClaim.Parse(kvp.Value);
                                if (parsed.Length == 0 && kvp.Value.Length > 0)
                                {
                                    failures.Add("a stored ledger value parsed to nothing: " + kvp.Value);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            failures.Add("walk threw: " + ex.GetType().Name + " " + ex.Message);
                            break;
                        }
                    }
                });

                Task.WaitAll(new[] { scanThread, dialogThread, walker }, TimeSpan.FromSeconds(15));
            }

            Assert.Multiple(() =>
            {
                Assert.That(writes, Is.GreaterThan(0), "the writers never ran, so this test proved nothing");
                Assert.That(failures, Is.Empty);
            });
        }

        /// <summary>Two sources can want the same URL. Each keeps its own claim, and one source stopping
        /// must not take the row with it - the row is correct as long as any claim survives.</summary>
        [Test]
        public void Removing_one_claimants_claim_leaves_the_others()
        {
            var settings = new VrModTagSettings();
            settings.RecordLinkClaim(GameA, "src-a", "Test", "https://example.com/x");
            settings.RecordLinkClaim(GameA, "src-b", "Test", "https://example.com/x");

            Assert.That(settings.RemoveLinkClaim(GameA, "src-a", "https://example.com/x"), Is.True);

            var claims = settings.GetLinkClaims(GameA);
            Assert.Multiple(() =>
            {
                Assert.That(claims, Has.Count.EqualTo(1));
                Assert.That(claims[0].SourceId, Is.EqualTo("src-b"));
            });
        }

        [Test]
        public void A_claim_for_a_row_that_no_longer_exists_is_pruned()
        {
            var settings = new VrModTagSettings();
            settings.RecordLinkClaim(GameA, "src-a", "Test", "https://example.com/kept");
            settings.RecordLinkClaim(GameA, "src-b", "Test", "https://example.com/user-deleted-this");

            var pruned = settings.PruneLinkClaims(GameA, new[] { "https://example.com/kept" });

            Assert.Multiple(() =>
            {
                Assert.That(pruned, Is.EqualTo(1));
                Assert.That(settings.GetLinkClaims(GameA).Select(c => c.Url), Is.EqualTo(new[] { "https://example.com/kept" }));
            });
        }

        [Test]
        public void Pruning_with_everything_still_present_costs_no_publish()
        {
            var settings = new VrModTagSettings();
            settings.RecordLinkClaim(GameA, "src", "Test", "https://example.com/kept");
            var published = settings.AddedLinksByGame;

            var pruned = settings.PruneLinkClaims(GameA, new[] { "https://example.com/kept" });

            Assert.Multiple(() =>
            {
                Assert.That(pruned, Is.EqualTo(0));
                Assert.That(settings.AddedLinksByGame, Is.SameAs(published), "an unchanged prune cloned the map");
            });
        }

        /// <summary>Prune row for a removed game: a game leaving the library must take its ledger
        /// entry with it, or the map grows for every game the user ever deletes.</summary>
        [Test]
        public void Clearing_a_game_removes_its_key_entirely()
        {
            var settings = new VrModTagSettings();
            settings.RecordLinkClaim(GameA, "src", "Test", "https://example.com/a");
            settings.RecordLinkClaim(GameB, "src", "Test", "https://example.com/b");

            Assert.That(settings.ClearLinkClaimsForGame(GameA), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(settings.GetGamesWithLinkClaims().Select(g => g), Is.EquivalentTo(new[] { GameB }));
                Assert.That(settings.AddedLinksByGame.ContainsKey(GameA.ToString("D")), Is.False, "an empty claim list left an empty key behind");
            });
        }

        [Test]
        public void Games_with_claims_are_listed_by_id()
        {
            var settings = new VrModTagSettings();
            settings.RecordLinkClaim(GameA, "src", "Test", "https://example.com/a");
            settings.RecordLinkClaim(GameB, "src", "Test", "https://example.com/b");

            Assert.That(settings.GetGamesWithLinkClaims(), Is.EquivalentTo(new[] { GameA, GameB }));
        }

        [Test]
        public void Adoption_skips_what_is_already_recorded_and_publishes_once()
        {
            var settings = new VrModTagSettings();
            settings.RecordLinkClaim(GameA, "src", "Test", "https://example.com/x");
            var published = settings.AddedLinksByGame;

            var adopted = settings.AdoptLinkClaims(new[]
            {
                (GameA, "src", "Test", "https://example.com/x"),      // already ours
                (GameA, "src", "Test", "https://example.com/y"),      // new
                (GameB, "src", "Test", "https://example.com/z")       // new game
            });

            Assert.Multiple(() =>
            {
                Assert.That(adopted, Is.EqualTo(2));
                Assert.That(settings.AddedLinksByGame, Is.Not.SameAs(published));
                Assert.That(settings.LinkClaimCount, Is.EqualTo(3));
                Assert.That(settings.GetLinkClaims(GameA), Has.Count.EqualTo(2));
            });
        }

        [Test]
        public void Adoption_of_nothing_changes_nothing()
        {
            var settings = new VrModTagSettings();
            settings.RecordLinkClaim(GameA, "src", "Test", "https://example.com/x");
            var published = settings.AddedLinksByGame;

            var adopted = settings.AdoptLinkClaims(new (Guid, string, string, string)[0]);

            Assert.Multiple(() =>
            {
                Assert.That(adopted, Is.EqualTo(0));
                Assert.That(settings.AddedLinksByGame, Is.SameAs(published));
            });
        }

        [Test]
        public void Clearing_everything_empties_the_ledger_and_reports_the_count()
        {
            var settings = new VrModTagSettings();
            settings.RecordLinkClaim(GameA, "src", "Test", "https://example.com/a");
            settings.RecordLinkClaim(GameB, "src", "Test", "https://example.com/b");

            var dropped = settings.ClearAllLinkClaims();

            Assert.Multiple(() =>
            {
                Assert.That(dropped, Is.EqualTo(2));
                Assert.That(settings.LinkClaimCount, Is.EqualTo(0));
                Assert.That(settings.GetGamesWithLinkClaims(), Is.Empty);
                Assert.That(settings.ClearAllLinkClaims(), Is.EqualTo(0), "an empty ledger reported work it did not do");
            });
        }

        [Test]
        public void A_ledger_map_survives_the_shape_settings_are_loaded_with()
        {
            // The ledger is a plain Dictionary property, so Newtonsoft's default ObjectCreationHandling
            // reuses whatever the getter returns and adds into it. A getter that handed out a copy would
            // silently discard a saved ledger at load - which would bring the leak back for every
            // existing user at the same time.
            var settings = new VrModTagSettings();
            settings.RecordLinkClaim(GameA, "src", "Test", "https://example.com/x");
            var stored = settings.AddedLinksByGame.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            var reloaded = new VrModTagSettings();
            foreach (var kvp in stored)
            {
                reloaded.AddedLinksByGame[kvp.Key] = kvp.Value;
            }

            Assert.That(reloaded.GetLinkClaims(GameA).Select(c => c.Url), Is.EqualTo(new[] { "https://example.com/x" }));
        }

        /// <summary>The ledger is only worth persisting when it changed. The settings file is ~480 KB
        /// and scans run on a timer, so "save it anyway" would rewrite it every interval whether the
        /// library moved or not - and "never save it" loses the change, and the row it describes with
        /// it, because Playnite does not rewrite an extension's settings file when it exits.</summary>
        [Test]
        public void Only_a_real_ledger_change_asks_for_a_save()
        {
            var settings = new VrModTagSettings();
            Assert.That(settings.ConsumePendingLedgerWrites(), Is.False, "a settings object that changed nothing asked to be written");

            settings.RecordLinkClaim(GameA, "src", "Test", "https://example.com/x");
            Assert.That(settings.ConsumePendingLedgerWrites(), Is.True, "a ledger write went unannounced");
            Assert.That(settings.ConsumePendingLedgerWrites(), Is.False, "the request was never cleared");

            // Reads and no-op writes must not re-arm it: every reconciled game reads the ledger, and a
            // steady-state pass changes it nowhere.
            settings.GetLinkClaims(GameA);
            settings.RecordLinkClaim(GameA, "src", "Test", "https://example.com/x");
            settings.PruneLinkClaims(GameA, new[] { "https://example.com/x" });
            Assert.That(settings.ConsumePendingLedgerWrites(), Is.False, "a pass that changed nothing asked for another write");
        }

        #endregion
    }
}
