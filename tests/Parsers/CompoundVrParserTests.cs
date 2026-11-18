using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    /// <summary>
    /// Tests for <see cref="CompoundVrParser"/>.
    ///
    /// Scope rule under test (see the parser's doc comment): a card is EXCLUDED only when its
    /// data-tech carries an unambiguous "natively-VR product" token (e.g. "Official VR Mode",
    /// "Quest Exclusive", "Mixed Reality"). Everything else is EMITTED — including flat-to-VR
    /// mods that merely use a render API (OpenXR/SteamVR) and emulator/retro titles that gain
    /// VR via a mod. When in doubt, emit.
    /// </summary>
    [TestFixture]
    public class CompoundVrParserTests
    {
        [Test]
        public void SourceId_ReturnsCompoundVR()
        {
            var parser = new CompoundVrParser();
            Assert.That(parser.SourceId, Is.EqualTo("CompoundVR"));
        }

        [Test]
        public void ChangeIdentifier_ReturnsNullWhenNotFetched()
        {
            var parser = new CompoundVrParser();
            Assert.That(parser.ChangeIdentifier, Is.Null);
        }

        [Test]
        public void GetCanonicalName_StripsVrSuffixes()
        {
            var parser = new CompoundVrParser();
            var entry = new VrModEntry { GameName = "Skyrim VR" };
            Assert.That(parser.GetCanonicalName(entry), Is.EqualTo("Skyrim"));
        }

        [Test]
        public void ParseCards_EmptyHtml_ReturnsEmpty()
        {
            var parser = new CompoundVrParser();
            var (entries, total, excluded, gated) = parser.ParseCards("");
            Assert.That(entries, Is.Empty);
            Assert.That(total, Is.EqualTo(0));
            Assert.That(excluded, Is.EqualTo(0));
            Assert.That(gated, Is.EqualTo(0));

            var (entries2, total2, excluded2, gated2) = parser.ParseCards(null);
            Assert.That(entries2, Is.Empty);
            Assert.That(total2, Is.EqualTo(0));
            Assert.That(excluded2, Is.EqualTo(0));
            Assert.That(gated2, Is.EqualTo(0));
        }

        [Test]
        public void ParseCards_EmitsApiUsingMod_AndEmulator_ExcludesOnlyOfficialNative()
        {
            var parser = new CompoundVrParser();
            var html = Card("cyberpunk-2077", "Cyberpunk 2077 VR", "Full VR Mod|OpenXR")
                     + Card("res-evil-village", "Resident Evil Village VR", "Official VR Mode|Sense Controllers")
                     + Card("metroid-prime", "Metroid Prime VR", "Emulator|6DOF Head Tracking");

            var (entries, total, excluded, _) = parser.ParseCards(html);

            Assert.That(total, Is.EqualTo(3));
            // Only "Official VR Mode" is an unambiguous natively-VR signal.
            Assert.That(excluded, Is.EqualTo(1));
            Assert.That(entries, Has.Count.EqualTo(2));
            // OpenXR is a render API, not a native-VR signal → the mod is emitted.
            Assert.That(entries[0].GameName, Is.EqualTo("Cyberpunk 2077 VR"));
            // An emulator/retro title that gains VR via a mod is in scope → emitted.
            Assert.That(entries[1].GameName, Is.EqualTo("Metroid Prime VR"));
        }

        [Test]
        public void ParseCards_SetsGameNameCategoryAndDeepLink()
        {
            var parser = new CompoundVrParser();
            var html = Card("cyberpunk-2077", "Cyberpunk 2077 VR", "Full VR Mod|OpenXR");

            var (entries, _, _, _) = parser.ParseCards(html);

            Assert.That(entries, Has.Count.EqualTo(1));
            var e = entries[0];
            Assert.That(e.GameName, Is.EqualTo("Cyberpunk 2077 VR"));
            Assert.That(e.Categories, Does.Contain(VrModCategories.CompoundVr));
            Assert.That(e.SourceUrl, Is.EqualTo("https://compoundvr.com/games/cyberpunk-2077/"));
        }

        [Test]
        public void ParseCards_DecodesHtmlEntitiesInTitle()
        {
            var parser = new CompoundVrParser();
            var html = Card("assassins-creed", "Assassin&#39;s Creed VR", "Full VR Mod");

            var (entries, _, _, _) = parser.ParseCards(html);

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].GameName, Is.EqualTo("Assassin's Creed VR"));
        }

        [Test]
        public void ParseCards_SkipsCardWithoutTitle()
        {
            var parser = new CompoundVrParser();
            // A card anchor with no <h3> title is counted in Total but produces no entry.
            var html = "<a class=\"game-card\" href=\"/games/x/\" data-tech=\"Full VR Mod\"> no title </a>";

            var (entries, total, excluded, _) = parser.ParseCards(html);

            Assert.That(total, Is.EqualTo(1));
            Assert.That(excluded, Is.EqualTo(0));
            Assert.That(entries, Is.Empty);
        }

        [Test]
        public void ParseCards_Fixture_ProducesExpectedCounts()
        {
            var html = ReadFixture("compoundvr-games.html");
            var parser = new CompoundVrParser();
            var (entries, total, excluded, gated) = parser.ParseCards(html);

            // The 35-card fixture: 4 excluded by a natively-VR tech token (the scope filter, which
            // predates the route gate), 15 gated out because the route says Native VR (14 of them) or
            // Official Hybrid (1) and nothing on the card names a mod, 16 offered. The two counters
            // are kept apart because they answer different questions — what the source does not
            // cover, versus what the source says is not a mod — and an operator reading the run log
            // has to be able to tell them apart.
            Assert.That(total, Is.EqualTo(35));
            Assert.That(excluded, Is.EqualTo(4));
            Assert.That(gated, Is.EqualTo(15));
            Assert.That(entries, Has.Count.EqualTo(16));
        }

        // The route gate removes tags from real libraries, so the exact set it takes from the
        // recorded page is pinned BY NAME. If the site starts labelling a different set as Native
        // VR, this fails loudly in review instead of quietly retagging somebody's library.
        [Test]
        public void ParseCards_Fixture_DropSetIsThePinnedFifteenGatedCardsAndTheFourAlreadyExcluded()
        {
            var html = ReadFixture("compoundvr-games.html");
            var (entries, _, excluded, gated) = new CompoundVrParser().ParseCards(html);

            var offered = entries.ConvertAll(e => e.GameName);
            var dropped = FixtureTitles(html);
            dropped.RemoveAll(t => offered.Contains(t));

            // 19 of 35 stop being offered. These 15 are the gate — 14 games that shipped in VR, each
            // read individually, plus Hitman: World of Assassination VR, whose VR mode the developer
            // shipped (the owner's parked question, closed 2026-11-18) — and the remaining 4 were
            // already excluded by a natively-VR tech token before any route gate was proposed. That
            // last distinction matters: a first count of the affected games named seventeen, and
            // three of those names were never being offered in the first place.
            Assert.That(gated, Is.EqualTo(15));
            Assert.That(excluded, Is.EqualTo(4));
            Assert.That(dropped, Is.EquivalentTo(new[]
            {
                // gated by the route
                "VTOL VR",
                "Hitman: World of Assassination VR",
                "Beat Saber VR",
                "Moss VR",
                "Lone Echo II",
                "Half-Life: Alyx VR",
                "The Walking Dead: Saints & Sinners VR",
                "Pistol Whip VR",
                "Tetris Effect: Connected VR",
                "Astro Bot Rescue Mission VR",
                "The Elder Scrolls V: Skyrim VR",
                "Lone Echo VR",
                "SUPERHOT VR",
                "Moss: Book II VR",
                "Gunman Contracts VR",
                // excluded by a natively-VR tech token, before and after the route gate
                "Resident Evil Village VR",
                "Asgard's Wrath 2 VR",
                "Assassin's Creed Nexus VR",
                "Asgard's Wrath VR",
            }));
        }

        /// <summary>
        /// Every card title on the recorded page, as the test's own view of it: the same anchor split
        /// and the same title pattern the parser uses, decoded the same way. Only used to name the
        /// cards that did NOT arrive — the counts come from the parser itself, so this cannot make a
        /// wrong number look right.
        /// </summary>
        private static System.Collections.Generic.List<string> FixtureTitles(string html)
        {
            var titles = new System.Collections.Generic.List<string>();
            foreach (var block in html.Split(new[] { "<a class=\"game-card\"" }, System.StringSplitOptions.None))
            {
                var m = Regex.Match(block, "<h3 class=\"game-card__title\"[^>]*>([^<]*)</h3>");
                if (!m.Success)
                    continue;
                titles.Add(m.Groups[1].Value
                    .Replace("&amp;", "&").Replace("&#39;", "'").Replace("&apos;", "'")
                    .Replace("&quot;", "\"").Replace("&nbsp;", " "));
            }
            return titles;
        }

        [Test]
        public void ParseCards_Fixture_EmitsKnownFlatToVrModsAndEmulators()
        {
            var html = ReadFixture("compoundvr-games.html");
            var parser = new CompoundVrParser();
            var (entries, _, _, _) = parser.ParseCards(html);

            var names = new System.Collections.Generic.List<string>();
            foreach (var e in entries)
                names.Add(e.GameName);

            // Flat-to-VR mods that use a render API — must be emitted (not excluded as native).
            Assert.That(names, Does.Contain("Cyberpunk 2077 VR"));
            Assert.That(names, Does.Contain("Grand Theft Auto: Vice City VR"));
            // Beat Saber shipped in VR, so this source stopped offering it when the route gate
            // landed. It used to arrive tagless — that assertion lives beside the gate's
            // own test, ParseCards_NativeVrCard_IsNoLongerOfferedByThisSource.
            Assert.That(names, Does.Not.Contain("Beat Saber VR"));
            // Emulator/retro titles that gain VR via a mod — must be emitted.
            Assert.That(names, Does.Contain("Metroid Prime VR"));
            // The 4 unambiguous natively-VR products — must be excluded.
            Assert.That(names, Does.Not.Contain("Resident Evil Village VR"));
            Assert.That(names, Does.Not.Contain("Asgard's Wrath 2 VR"));
            Assert.That(names, Does.Not.Contain("Asgard's Wrath VR"));
            Assert.That(names, Does.Not.Contain("Assassin's Creed Nexus VR"));
        }

        #region platform tags and classification

        /// <summary>
        /// A card carrying the route and platform fields. <c>updated</c> is empty by default so
        /// <c>AdditionalInfo</c> can be asserted exactly; pass it when a test wants the freshness
        /// stamp too. Separate from <see cref="Card"/> so the older scope-filter tests keep their
        /// shape and these ones say which attributes they are about.
        /// </summary>
        private static string CardWith(string route, string tech, string platforms,
                                       string slug = "star-game", string title = "Star Game VR",
                                       string updated = "")
        {
            var stamp = updated.Length > 0 ? " data-sort-updated=\"" + updated + "\"" : string.Empty;
            return "<a class=\"game-card\" href=\"/games/" + slug + "/\" data-route=\"" + route +
                   "\" data-tech=\"" + tech + "\" data-platforms=\"" + platforms + "\"" + stamp +
                   "> <h3 class=\"game-card__title\">" + title +
                   "</h3> <span class=\"tier-badge tier--s\"> S </span> </a>";
        }

        /// <summary>The categories of the single entry a one-card fragment yields.</summary>
        private static System.Collections.Generic.List<string> CategoriesOfOne(string route, string tech, string platforms)
        {
            var (entries, _, _, _) = new CompoundVrParser().ParseCards(CardWith(route, tech, platforms));
            Assert.That(entries, Has.Count.EqualTo(1));
            return entries[0].Categories;
        }

        private static string HashOf(string fragment)
            => string.Join("\n", CompoundVrParser.BuildHashLines(new CompoundVrParser().ParseCards(fragment).Entries));

        // The classification table, one row per measured case. These rows are the argument: a named
        // mod tool outranks the site's own route label (measured 2026-11-17 —
        // Grand Theft Auto IV VR and Lies of P VR are labelled Native VR while naming mod tools),
        // moddability is not a mod (VTOL VR, Skyrim VR), and nothing the source has not ruled on may
        // be rejected: Unknown maps forward like Mod, which is what stops a vocabulary change on the
        // site from silently deleting a user's mod.
        // CardRoute is internal, kept off the plugin's public surface, so a public
        // NUnit test cannot take it as a parameter (CS0051). The table therefore stays in strings and
        // compares on the value's name; renaming an enum member now fails here instead of silently
        // retargeting a TestCase.
        [TestCase("Full VR Mod", "", "Mod")]
        [TestCase("Framework Only", "", "Mod")]
        [TestCase("Multi-Route Coverage", "", "Mod")]
        [TestCase("Native VR", "", "NotMod")]
        [TestCase("NATIVE VR", "", "NotMod")]
        [TestCase(" native vr ", "", "NotMod")]
        [TestCase("Native VR", "UEVR", "Mod")]
        [TestCase("Native VR", "REFramework|Motion Controls", "Mod")]
        [TestCase("Native VR", "full vr mod", "Mod")]
        [TestCase("Native VR", "Mod Support", "NotMod")]
        [TestCase("Native VR", "Moddable", "NotMod")]
        [TestCase("Official Hybrid", "", "NotMod")]
        [TestCase("Official Hybrid", "REFramework", "Mod")]
        [TestCase("Quantum Stereoscope", "", "Unknown")]
        [TestCase("", "", "Unknown")]
        [TestCase(null, null, "Unknown")]
        public void ClassifyCard_AModToolOutranksTheLabelAndTheUnRuledFailsOpen(
            string route, string tech, string expected)
            => Assert.That(CompoundVrParser.ClassifyCard(route, tech).ToString(), Is.EqualTo(expected));

        [Test]
        public void ParseCards_QuestCard_EarnsTheHeadsetTagNextToTheCatalogCategory()
            => Assert.That(CategoriesOfOne("Full VR Mod", "Full VR Mod|UEVR", "PCVR|Quest"),
                           Is.EquivalentTo(new[] { VrModCategories.CompoundVr, VrModCategories.Quest }));

        [Test]
        public void ParseCards_PcvrOnlyCard_EarnsNoHeadsetTag()
            => Assert.That(CategoriesOfOne("Full VR Mod", "Full VR Mod|OpenXR", "PCVR"),
                           Is.EquivalentTo(new[] { VrModCategories.CompoundVr }));

        [Test]
        public void ParseCards_QuestListedTwice_EarnsTheTagOnce()
            => Assert.That(CategoriesOfOne("Full VR Mod", "Full VR Mod", "Quest|Quest"),
                           Is.EquivalentTo(new[] { VrModCategories.CompoundVr, VrModCategories.Quest }));

        // Every unmapped token, whose reason is recorded in the parser's class header. This test is
        // the other half of that record: it is what keeps header and behaviour from drifting apart.
        [TestCase("PSVR")]
        [TestCase("PSVR2")]
        [TestCase("Pico")]
        [TestCase("Rift")]
        [TestCase("Rift S")]
        [TestCase("Vive")]
        [TestCase("Index")]
        [TestCase("Windows Mixed Reality")]
        public void ParseCards_OutListedPlatform_EarnsNoHeadsetTag(string platform)
            => Assert.That(CategoriesOfOne("Full VR Mod", "Full VR Mod", "PCVR|" + platform),
                           Is.EquivalentTo(new[] { VrModCategories.CompoundVr }));

        [TestCase("Questy")]
        [TestCase("Quests")]
        [TestCase("Meta Quest")]
        [TestCase("PCVR|Questing")]
        [TestCase("Quest 3")]
        public void ParseCards_PlatformMatchIsTokenExact_NeverASubstring(string platforms)
            => Assert.That(CategoriesOfOne("Full VR Mod", "Full VR Mod", platforms),
                           Has.No.Member(VrModCategories.Quest));

        [Test]
        public void ParseCards_NativeVrCard_IsNoLongerOfferedByThisSource()
        {
            // Beat Saber VR, as the live site and the recorded page both publish it: data-route says
            // Native VR and nothing on the card names a mod. The headset tag was withheld from these
            // cards first; the row itself only went once the gate had passed a measured review of the
            // cards it would take, which happened on 2026-11-17. So now the card itself is gone: no
            // row, no umbrella tag, no source link. What was said about the blast radius when the tag
            // was withheld still governs and is pinned service-side — the library tag is the union
            // over every source, so a game another source still lists keeps its tags.
            var parser = new CompoundVrParser { RunLog = new System.Collections.Generic.List<string>() };
            var (entries, total, excluded, gated) = parser.ParseCards(CardWith(
                "Native VR", "Native VR|Room-Scale|Motion Controls", "PCVR|Quest|PSVR|PSVR2",
                "beat-saber", "Beat Saber VR"));

            Assert.That(total, Is.EqualTo(1));
            Assert.That(excluded, Is.EqualTo(0));
            Assert.That(gated, Is.EqualTo(1));
            Assert.That(entries, Is.Empty);

            // The drop is reported with its reason and a few titles, so an
            // operator sees a mass drop in the source log before a user notices missing tags.
            Assert.That(string.Join(" ", parser.RunLog),
                        Does.Contain("excluded 0 by tech + 1 by route (Native VR 1)").And.Contain("Beat Saber VR"));
        }

        [Test]
        public void ParseCards_NativeRouteNamingAModTool_PassesTheGateAndKeepsItsHeadsetTag()
        {
            // The case a route-only gate would have lost, measured on the live page 2026-11-17:
            // Grand Theft Auto IV VR and Lies of P VR are labelled Native VR while naming UEVR and
            // REFramework. The constraint on this whole change is "do not lose actual VR mods", so the
            // outranking is pinned end to end through the gate, not only in the classification table.
            var (entries, _, _, gated) = new CompoundVrParser().ParseCards(CardWith(
                "Native VR", "UEVR|REFramework", "PCVR|Quest", "lies-of-p", "Lies of P VR"));

            Assert.That(gated, Is.EqualTo(0));
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Categories, Does.Contain(VrModCategories.Quest));
        }

        [Test]
        public void ParseCards_OfficialHybridCard_IsGatedButAHybridNamingAModIsNot()
        {
            // The owner's parked question, closed on 2026-11-18 by reading the 32 hybrid cards the
            // source was then offering: not one named a mod tool in data-tech, and the two that
            // ARE mods sit on the same recorded page — F-Zero X VR and Mario Kart 64 VR, both
            // data-route "Official Hybrid" with data-tech "Full VR Mod | Community VR Port". So the
            // gate takes the developer-made one and the mod-proof test spares the community ones.
            // That pairing is the reason this gate is safe: the standing constraint on the whole
            // change is "do not lose actual VR mods", and here it decides a card the label calls
            // official. The recorded fixture tells the same pair of stories — Hitman: World of
            // Assassination VR is gated, Resident Evil Village VR survives on REFramework (and is
            // dropped by the scope filter besides).
            Assert.That(new CompoundVrParser().ParseCards(
                    CardWith("Official Hybrid", "Dual-wielding|Room-scale interactions", "PCVR|Quest",
                        "hitman-3", "Hitman 3: World of Assassination VR")).GatedByRoute,
                        Is.EqualTo(1));

            var survivor = new CompoundVrParser().ParseCards(
                CardWith("Official Hybrid", "Full VR Mod|Community VR Port", "PCVR|Quest|Pico",
                    "mario-kart-64", "Mario Kart 64 VR"));
            Assert.That(survivor.GatedByRoute, Is.EqualTo(0));
            Assert.That(survivor.Entries, Has.Count.EqualTo(1));
            Assert.That(survivor.Entries[0].Categories, Does.Contain(VrModCategories.Quest));
        }

        [Test]
        public void ParseCards_CardsThisSourceHasNotRuledOn_PassTheGate()
        {
            // The tag half of this rule already exists; this pins that Unknown also keeps
            // arriving. That is what makes a vocabulary change on the site fail open instead of
            // deleting somebody's mod. Every route value the source published on 2026-11-18 is now
            // ruled on, so what is left failing open is vocabulary this parser has not met yet.
            Assert.That(new CompoundVrParser().ParseCards(
                    CardWith("Quantum Stereoscope", "", "Quest", "future", "Future Game VR")).GatedByRoute,
                        Is.EqualTo(0));
            Assert.That(new CompoundVrParser().ParseCards(
                    CardWith("", "", "Quest", "no-route", "Routeless Game VR")).GatedByRoute,
                        Is.EqualTo(0));
        }

        [Test]
        public void ParseCards_GatedCard_StillNamesItsPlatformVocabulary()
        {
            // The gate takes a fifth of the source, so the order matters: platform drift is recorded
            // before the gate, so that a brand-new headset family is still named on the run log even
            // if every card carrying it happens to be gated. After the gate, the gate would hide the
            // one signal that says the site changed.
            var parser = new CompoundVrParser { RunLog = new System.Collections.Generic.List<string>() };
            var (entries, _, _, gated) = parser.ParseCards(CardWith(
                "Native VR", "Native VR", "PCVR|HoloLens", "gated-drift", "Gated Game VR"));

            Assert.That(gated, Is.EqualTo(1));
            Assert.That(entries, Is.Empty);
            Assert.That(string.Join(" ", parser.RunLog), Does.Contain("HoloLens"));
        }

        [Test]
        public void ParseCards_NativeRouteWithAModToolNamed_StillEarnsTheTag()
            => Assert.That(CategoriesOfOne("Native VR", "UEVR|Community VR Port", "PCVR|Quest"),
                           Contains.Item(VrModCategories.Quest));

        [Test]
        public void ParseCards_UnseenRouteValue_StillEarnsTheTag()
            => Assert.That(CategoriesOfOne("Quantum Stereoscope", "", "Quest"),
                           Contains.Item(VrModCategories.Quest));

        [Test]
        public void ParseCards_AdditionalInfoCarriesTheRouteVerbatim()
        {
            var (entries, _, _, _) = new CompoundVrParser().ParseCards(
                CardWith("Full VR Mod", "Full VR Mod", "PCVR", "metro-exodus", "Metro Exodus VR"));

            Assert.That(entries[0].AdditionalInfo, Is.EqualTo("Tier S · Full VR Mod"));
        }

        [Test]
        public void ParseCards_CardWithoutARoute_LeavesNoDanglingSeparator()
        {
            var (entries, _, _, _) = new CompoundVrParser().ParseCards(
                CardWith("", "Full VR Mod", "PCVR", "no-route"));

            Assert.That(entries[0].AdditionalInfo, Is.EqualTo("Tier S"));
        }

        [Test]
        public void ParseCards_RouteAndFreshness_BothReachTheReviewer()
        {
            var (entries, _, _, _) = new CompoundVrParser().ParseCards(
                CardWith("Multi-Route Coverage", "Full VR Mod", "PCVR", updated: "1790640000000"));

            Assert.That(entries[0].AdditionalInfo,
                        Does.Match(@"^Tier S · Multi-Route Coverage · updated \d{4}-\d{2}-\d{2}$"));
        }

        // The change identifier has to move when the fields that drive tags move, or a Quest port
        // added to a card with an unchanged title and URL is never seen: HasSourceChanged keeps
        // serving the cached parse and the tag stays frozen at whatever the site said the first time
        // the game was cached.
        [Test]
        public void BuildHashLines_MovesWhenOnlyThePlatformChanges()
            => Assert.That(HashOf(CardWith("Full VR Mod", "Full VR Mod", "PCVR|Quest")),
                           Is.Not.EqualTo(HashOf(CardWith("Full VR Mod", "Full VR Mod", "PCVR"))));

        [Test]
        public void BuildHashLines_MovesWhenOnlyTheRouteChanges()
            => Assert.That(HashOf(CardWith("Native VR", "Full VR Mod", "PCVR")),
                           Is.Not.EqualTo(HashOf(CardWith("Full VR Mod", "Full VR Mod", "PCVR"))));

        [Test]
        public void BuildHashLines_IsStableForTheSameParse()
            => Assert.That(HashOf(CardWith("Full VR Mod", "Full VR Mod", "PCVR|Quest", updated: "1790640000000")),
                           Is.EqualTo(HashOf(CardWith("Full VR Mod", "Full VR Mod", "PCVR|Quest", updated: "1790640000000"))));

        [Test]
        public void ParseCards_UnknownPlatformTokens_AreNamedOnTheRunLogAndNeverMapped()
        {
            var parser = new CompoundVrParser { RunLog = new System.Collections.Generic.List<string>() };
            parser.ParseCards(CardWith("Full VR Mod", "Full VR Mod", "PCVR|HoloLens|Quest 3"));

            Assert.That(string.Join(" ", parser.RunLog), Does.Contain("HoloLens").And.Contain("Quest 3"));
        }

        [Test]
        public void ParseCards_UnknownPlatformTokens_EarnNoTagEvenBesideARecognisedOne()
            => Assert.That(CategoriesOfOne("Full VR Mod", "Full VR Mod", "HoloLens|Quest|Quest 3"),
                           Is.EquivalentTo(new[] { VrModCategories.CompoundVr, VrModCategories.Quest }));

        [Test]
        public void ParseCards_UnseenRouteValue_IsNamedOnTheRunLog()
        {
            var parser = new CompoundVrParser { RunLog = new System.Collections.Generic.List<string>() };
            parser.ParseCards(CardWith("Quantum Stereoscope", "Full VR Mod", "PCVR"));

            Assert.That(string.Join(" ", parser.RunLog), Does.Contain("Quantum Stereoscope"));
        }

        [Test]
        public void ParseCards_Fixture_PublishesNoPlatformThisSourceHasNotRuledOn()
        {
            // Drift, not a conclusion: the day the site starts publishing a fifth headset family,
            // this fails and the run log names it, which is the trigger to re-read the out-list.
            var parser = new CompoundVrParser { RunLog = new System.Collections.Generic.List<string>() };
            parser.ParseCards(ReadFixture("compoundvr-games.html"));

            Assert.That(string.Join(" ", parser.RunLog), Does.Not.Contain("unrecognised platform"));
        }

        [Test]
        public void ParseCards_Fixture_EarnsTheHeadsetTagOnThePinnedNumberOfCards()
        {
            var (entries, _, _, _) = new CompoundVrParser().ParseCards(ReadFixture("compoundvr-games.html"));

            // 13 of the 35 recorded cards list Quest. Four sit on a card this source can call a mod,
            // and those four are all that still arrive — the other nine are now gated out entirely
            // so this number counts something different from the 13 it once
            // shadowed. Re-pin deliberately: a different number means the site moved, or this
            // classification did, and only one of those is a reason to edit the test.
            Assert.That(entries.FindAll(e => e.Categories.Contains(VrModCategories.Quest)), Has.Count.EqualTo(4));
        }

        #endregion

        [Test]
        public void ParseCards_Fixture_PublishesNoRouteThisSourceHasNotRuledOn()
        {
            // The same drift tripwire as the platform one, on the other field: the five route values
            // measured on the recorded page are all known, so the run log stays quiet about them.
            var parser = new CompoundVrParser { RunLog = new System.Collections.Generic.List<string>() };
            parser.ParseCards(ReadFixture("compoundvr-games.html"));

            Assert.That(string.Join(" ", parser.RunLog), Does.Not.Contain("unrecognised data-route"));
        }

        [Test]
        public void ParseCards_UnseenRouteValue_IsNamedOnTheRunLogEvenWhenAModToolSettlesTheVerdict()
        {
            // The reason drift is recorded independently of the verdict: this card classifies as Mod
            // on its tech token, so a verdict-tied log would never mention the renamed route.
            var parser = new CompoundVrParser { RunLog = new System.Collections.Generic.List<string>() };
            parser.ParseCards(CardWith("Quantum Stereoscope", "Full VR Mod", "PCVR|Quest"));

            Assert.That(string.Join(" ", parser.RunLog), Does.Contain("Quantum Stereoscope"));
        }

        private static string GetSnapshotPath(string fileName)
        {
            var assemblyDir = Path.GetDirectoryName(typeof(CompoundVrParserTests).Assembly.Location);
            return Path.GetFullPath(Path.Combine(assemblyDir!, "..", "..", "..", "Parsers", "snapshots", fileName));
        }

        private static string ReadFixture(string fileName)
        {
            var path = GetSnapshotPath(fileName);
            Assert.That(File.Exists(path), Is.True, $"Snapshot file not found: {path}");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Builds a game card in the shape of the live site (game-card anchor, /games/ href,
        /// data-tech, h3 title).
        /// </summary>
        private static string Card(string slug, string title, string tech)
        {
            return $@"<a class=""game-card"" href=""/games/{slug}/"" data-sort-updated=""1790640000000"" data-tech=""{tech}""> <h3 class=""game-card__title"">{title}</h3> </a>";
        }
    }
}
