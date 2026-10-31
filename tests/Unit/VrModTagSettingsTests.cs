using System;
using NUnit.Framework;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;
using System.Collections.Generic;

namespace PlayniteVRMods.Tests.Unit
{
    [TestFixture]
    public class VrModTagSettingsTests
    {
        [Test]
        public void DefaultValues_AreCorrect()
        {
            var settings = new VrModTagSettings();
            Assert.That(settings.ScanIntervalMinutes, Is.EqualTo(10080));
            Assert.That(settings.EnableRototRobot, Is.True);
            Assert.That(settings.EnableFlat2VR, Is.True);
            Assert.That(settings.EnableUeVR, Is.True);
        }

        [Test]
        public void PreviouslyTaggedGames_DefaultIsEmpty()
        {
            var settings = new VrModTagSettings();
            Assert.That(settings.PreviouslyTaggedGames, Is.Not.Null.And.Empty);
        }

        [Test]
        public void SourceStatuses_DefaultIsEmpty()
        {
            var settings = new VrModTagSettings();
            Assert.That(settings.SourceStatuses, Is.Not.Null.And.Empty);
        }

        [Test]
        public void CacheDurationMinutes_DefaultIs10()
        {
            var settings = new VrModTagSettings();
            Assert.That(settings.CacheDurationMinutes, Is.EqualTo(10));
        }

        [Test]
        public void FuzzyMatchThreshold_DefaultIs08()
        {
            var settings = new VrModTagSettings();
            Assert.That(settings.FuzzyMatchThreshold, Is.EqualTo(0.8f));
        }

        [Test]
        public void FuzzyMatchThreshold_CanBeSet()
        {
            var settings = new VrModTagSettings();
            settings.FuzzyMatchThreshold = 0.9f;
            Assert.That(settings.FuzzyMatchThreshold, Is.EqualTo(0.9f));
        }

        [Test]
        public void FuzzyMatchThreshold_OutOfRange_IsClampedAndFlagged()
        {
            var settings = new VrModTagSettings { FuzzyMatchThreshold = -1.0f };

            Assert.That(settings.FuzzyMatchThreshold, Is.EqualTo(VrModTagSettings.MinFuzzyMatchThreshold),
                "the setter clamps rather than storing a threshold that would match the whole library");
            Assert.That(settings.FuzzyMatchThresholdWasAdjusted, Is.True,
                "a clamp the user never hears about is a silently different setting");
        }

        [Test]
        public void SourceStatus_HasLastChangeIdProperty()
        {
            var status = new SourceStatus { LastChangeId = "abc123" };
            Assert.That(status.LastChangeId, Is.EqualTo("abc123"));
        }

        [Test]
        public void SetDecision_And_GetDecision_ReturnsCorrectValue()
        {
            var settings = new VrModTagSettings();
            var gameId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "SomeGame", gameId, MatchDecision.Accepted);
            Assert.That(settings.GetDecision("rototrobot", "SomeGame", gameId), Is.EqualTo(MatchDecision.Accepted));
        }

        [Test]
        public void SetDecision_And_GetDecision_Rejected()
        {
            var settings = new VrModTagSettings();
            var gameId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "SomeGame", gameId, MatchDecision.Rejected);
            Assert.That(settings.GetDecision("rototrobot", "SomeGame", gameId), Is.EqualTo(MatchDecision.Rejected));
        }

        [Test]
        public void GetDecision_ReturnsPending_ForUnsetDecision()
        {
            var settings = new VrModTagSettings();
            var gameId = Guid.NewGuid();
            Assert.That(settings.GetDecision("rototrobot", "SomeGame", gameId), Is.EqualTo(MatchDecision.Pending));
        }

        /// <summary>
        /// The per-game scan prunes candidate pairs on a length bound before scoring, and may only
        /// do that for pairs the user has never reviewed. It depends on this lookup telling an
        /// unreviewed pair apart from a stored decision — including a stored Pending, which is still
        /// a decision the user made.
        /// </summary>
        [Test]
        public void TryGetDecision_ReportsWhetherAPairHasBeenReviewedAtAll()
        {
            var settings = new VrModTagSettings();
            var gameId = Guid.NewGuid();

            Assert.That(settings.TryGetDecision("rototrobot", "SomeGame", gameId, out var unset), Is.False,
                "no stored record: this pair is free to be pruned by a name bound");
            Assert.That(unset, Is.EqualTo(MatchDecision.Pending));

            settings.SetDecision("rototrobot", "SomeGame", gameId, MatchDecision.Pending);
            Assert.That(settings.TryGetDecision("rototrobot", "SomeGame", gameId, out var storedPending), Is.True,
                "an explicit Pending is a stored decision, not an unreviewed pair");
            Assert.That(storedPending, Is.EqualTo(MatchDecision.Pending));

            settings.SetDecision("rototrobot", "SomeGame", gameId, MatchDecision.Accepted);
            Assert.That(settings.TryGetDecision("rototrobot", "SomeGame", gameId, out var accepted), Is.True);
            Assert.That(accepted, Is.EqualTo(MatchDecision.Accepted));

            settings.MatchDecisions[VrModNameMatcher.MakeDecisionKey("rototrobot", "SomeGame", gameId)] = "corrupt";
            Assert.That(settings.TryGetDecision("rototrobot", "SomeGame", gameId, out var corrupt), Is.False,
                "an unreadable record reads as unreviewed instead of throwing, same degradation as GetDecision");
            Assert.That(corrupt, Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        public void GetDecision_CorruptValue_ReturnsPending_DoesNotThrow()
        {
            var settings = new VrModTagSettings();
            // Inject a corrupt stored value at the exact decision key (simulates bad persisted
            // data). Old code used Enum.Parse (throws on an invalid value); new code uses
            // Enum.TryParse and degrades to Pending. This is the LOW-3 regression.
            var gameId = Guid.NewGuid();
            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "SomeGame", gameId);
            settings.MatchDecisions[key] = "NotAValidDecision";
            Assert.That(settings.GetDecision("rototrobot", "SomeGame", gameId), Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        public void GetDecision_ReturnsPending_ForUnsetDecision_DifferentSource()
        {
            var settings = new VrModTagSettings();
            var gameId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "SomeGame", gameId, MatchDecision.Accepted);
            Assert.That(settings.GetDecision("flat2vr", "SomeGame", gameId), Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        public void ClearDecision_RemovesSpecificDecision()
        {
            var settings = new VrModTagSettings();
            var gameId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "SomeGame", gameId, MatchDecision.Accepted);
            settings.ClearDecision("rototrobot", "SomeGame", gameId);
            Assert.That(settings.GetDecision("rototrobot", "SomeGame", gameId), Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        public void ClearDecision_DoesNotAffectOtherDecisions()
        {
            var settings = new VrModTagSettings();
            // Clearing one mapping's decision does not affect a different mapping (different
            // source) for the same game.
            var gameId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "SomeGame", gameId, MatchDecision.Accepted);
            settings.SetDecision("flat2vr", "SomeGame", gameId, MatchDecision.Rejected);
            settings.ClearDecision("rototrobot", "SomeGame", gameId);
            Assert.That(settings.GetDecision("flat2vr", "SomeGame", gameId), Is.EqualTo(MatchDecision.Rejected));
        }

        [Test]
        public void ClearAllDecisions_RemovesAllDecisions()
        {
            var settings = new VrModTagSettings();
            var game1Id = Guid.NewGuid();
            var game2Id = Guid.NewGuid();
            var game3Id = Guid.NewGuid();
            settings.SetDecision("rototrobot", "Game1", game1Id, MatchDecision.Accepted);
            settings.SetDecision("rototrobot", "Game2", game2Id, MatchDecision.Rejected);
            settings.SetDecision("flat2vr", "Game3", game3Id, MatchDecision.Accepted);
            settings.ClearAllDecisions();
            Assert.That(settings.GetDecision("rototrobot", "Game1", game1Id), Is.EqualTo(MatchDecision.Pending));
            Assert.That(settings.GetDecision("rototrobot", "Game2", game2Id), Is.EqualTo(MatchDecision.Pending));
            Assert.That(settings.GetDecision("flat2vr", "Game3", game3Id), Is.EqualTo(MatchDecision.Pending));
        }

        [Test]
        public void ClearAllDecisions_EmptyMatchDecisions_DoesNotThrow()
        {
            var settings = new VrModTagSettings();
            Assert.DoesNotThrow(() => settings.ClearAllDecisions());
        }

        [Test]
        public void ClearDecisionsForGame_RemovesAllDecisionsForThatGame()
        {
            var settings = new VrModTagSettings();
            // Two sources map to the same playnite game; both are per-mapping decisions for it.
            var doomId = Guid.NewGuid();
            var sekiroId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "DOOM", doomId, MatchDecision.Accepted);
            settings.SetDecision("flat2vr", "DOOM", doomId, MatchDecision.Rejected);
            settings.SetDecision("rototrobot", "SEKIRO", sekiroId, MatchDecision.Accepted);
            settings.ClearDecisionsForGame(doomId);
            Assert.That(settings.GetDecision("rototrobot", "DOOM", doomId), Is.EqualTo(MatchDecision.Pending));
            Assert.That(settings.GetDecision("flat2vr", "DOOM", doomId), Is.EqualTo(MatchDecision.Pending));
            Assert.That(settings.GetDecision("rototrobot", "SEKIRO", sekiroId), Is.EqualTo(MatchDecision.Accepted));
        }

        [Test]
        public void ClearDecisionsForGame_DoesNotAffectOtherGames()
        {
            var settings = new VrModTagSettings();
            var someGameId = Guid.NewGuid();
            var otherGameId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "SomeGame", someGameId, MatchDecision.Accepted);
            settings.SetDecision("rototrobot", "OtherGame", otherGameId, MatchDecision.Rejected);
            settings.ClearDecisionsForGame(someGameId);
            Assert.That(settings.GetDecision("rototrobot", "SomeGame", someGameId), Is.EqualTo(MatchDecision.Pending));
            Assert.That(settings.GetDecision("rototrobot", "OtherGame", otherGameId), Is.EqualTo(MatchDecision.Rejected));
        }

        [Test]
        public void MatchDecisions_Serializable_CanSetAndReadDictionaryContents()
        {
            var settings = new VrModTagSettings();
            var gameAId = Guid.NewGuid();
            var gameBId = Guid.NewGuid();
            var gameCId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "GameA", gameAId, MatchDecision.Accepted);
            settings.SetDecision("rototrobot", "GameB", gameBId, MatchDecision.Rejected);
            settings.SetDecision("flat2vr", "GameC", gameCId, MatchDecision.Accepted);
            Assert.That(settings.MatchDecisions, Has.Count.EqualTo(3));
            // Per-mapping keys are 3-part (sourceId||sourceGame||{gameId:D}) — no category.
            Assert.That(settings.MatchDecisions.ContainsKey($"rototrobot||GameA||{gameAId:D}"));
            Assert.That(settings.MatchDecisions.ContainsKey($"rototrobot||GameB||{gameBId:D}"));
            Assert.That(settings.MatchDecisions.ContainsKey($"flat2vr||GameC||{gameCId:D}"));
            Assert.That(settings.MatchDecisions[$"rototrobot||GameA||{gameAId:D}"], Is.EqualTo("Accepted"));
            Assert.That(settings.MatchDecisions[$"rototrobot||GameB||{gameBId:D}"], Is.EqualTo("Rejected"));
            Assert.That(settings.MatchDecisions[$"flat2vr||GameC||{gameCId:D}"], Is.EqualTo("Accepted"));
        }

        [Test]
        public void ClearDecisionsForGame_DoesNotOverMatchSequels()
        {
            var settings = new VrModTagSettings();
            var doomId = Guid.NewGuid();
            var doom2Id = Guid.NewGuid();
            var doomEternalId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "DOOM", doomId, MatchDecision.Accepted);
            settings.SetDecision("rototrobot", "DOOM 2", doom2Id, MatchDecision.Rejected);
            settings.SetDecision("rototrobot", "DOOM Eternal", doomEternalId, MatchDecision.Accepted);
            settings.ClearDecisionsForGame(doomId);
            Assert.That(settings.GetDecision("rototrobot", "DOOM", doomId), Is.EqualTo(MatchDecision.Pending));
            Assert.That(settings.GetDecision("rototrobot", "DOOM 2", doom2Id), Is.EqualTo(MatchDecision.Rejected));
            Assert.That(settings.GetDecision("rototrobot", "DOOM Eternal", doomEternalId), Is.EqualTo(MatchDecision.Accepted));
        }

        [Test]
        public void ClearDecisionsForGame_ClearedByPlayniteGameNotSourceGame()
        {
            var settings = new VrModTagSettings();
            // Same source-game name, different playnite game (Id): only the matching
            // playnite game's decision is cleared.
            var skyrimId = Guid.NewGuid();
            var skyrimVrId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "Skyrim VR", skyrimId, MatchDecision.Accepted);
            settings.SetDecision("flat2vr", "Skyrim VR", skyrimVrId, MatchDecision.Accepted);
            settings.ClearDecisionsForGame(skyrimId);
            Assert.That(settings.GetDecision("rototrobot", "Skyrim VR", skyrimId), Is.EqualTo(MatchDecision.Pending));
            Assert.That(settings.GetDecision("flat2vr", "Skyrim VR", skyrimVrId), Is.EqualTo(MatchDecision.Accepted));
        }

        // --- Decision timestamps (time-filter backing store) --------------------------

        [Test]
        public void EnsureDecisionTimestampUtc_NewKey_StoresAndReturnsNow()
        {
            var settings = new VrModTagSettings();
            var gameId = Guid.NewGuid();
            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "SomeGame", gameId);
            var now = DateTime.UtcNow;

            var result = settings.EnsureDecisionTimestampUtc(key, now);

            Assert.That(result, Is.EqualTo(now));
            Assert.That(settings.GetDecisionTimestampUtc(key), Is.EqualTo(now));
        }

        [Test]
        public void EnsureDecisionTimestampUtc_ExistingKey_DoesNotOverwrite()
        {
            var settings = new VrModTagSettings();
            var gameId = Guid.NewGuid();
            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "SomeGame", gameId);
            var firstSeen = DateTime.UtcNow.AddHours(-5);
            var later = DateTime.UtcNow;

            settings.EnsureDecisionTimestampUtc(key, firstSeen);
            var result = settings.EnsureDecisionTimestampUtc(key, later);

            // Keeps the original (first-seen) time; does not bump to the later scan time.
            Assert.That(result, Is.EqualTo(firstSeen));
            Assert.That(settings.GetDecisionTimestampUtc(key), Is.EqualTo(firstSeen));
        }

        [Test]
        public void GetDecisionTimestampUtc_Absent_ReturnsNull()
        {
            var settings = new VrModTagSettings();
            var gameId = Guid.NewGuid();
            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "SomeGame", gameId);

            Assert.That(settings.GetDecisionTimestampUtc(key), Is.Null);
        }

        [Test]
        public void SetDecision_StoresTimestamp()
        {
            var settings = new VrModTagSettings();
            var gameId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "SomeGame", gameId, MatchDecision.Accepted);

            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "SomeGame", gameId);
            var ts = settings.GetDecisionTimestampUtc(key);
            Assert.That(ts, Is.Not.Null);
            // The decision was just made, so the timestamp is very recent.
            Assert.That(DateTime.UtcNow - ts!.Value, Is.LessThan(TimeSpan.FromMinutes(5)));
        }

        [Test]
        public void ClearAllDecisions_ClearsTimestamps()
        {
            var settings = new VrModTagSettings();
            var gameId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "SomeGame", gameId, MatchDecision.Accepted);
            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "SomeGame", gameId);
            Assert.That(settings.GetDecisionTimestampUtc(key), Is.Not.Null);

            settings.ClearAllDecisions();

            Assert.That(settings.GetDecisionTimestampUtc(key), Is.Null);
        }

        [Test]
        public void ClearDecision_RemovesTimestamp()
        {
            var settings = new VrModTagSettings();
            var gameId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "SomeGame", gameId, MatchDecision.Accepted);
            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "SomeGame", gameId);
            Assert.That(settings.GetDecisionTimestampUtc(key), Is.Not.Null);

            settings.ClearDecision("rototrobot", "SomeGame", gameId);

            Assert.That(settings.GetDecisionTimestampUtc(key), Is.Null);
        }

        [Test]
        public void ClearDecisionsForGame_RemovesTimestamp_OnlyForThatGame()
        {
            var settings = new VrModTagSettings();
            var doomId = Guid.NewGuid();
            var doom2Id = Guid.NewGuid();
            settings.SetDecision("rototrobot", "DOOM", doomId, MatchDecision.Accepted);
            settings.SetDecision("rototrobot", "DOOM 2", doom2Id, MatchDecision.Rejected);
            var clearedKey = VrModNameMatcher.MakeDecisionKey("rototrobot", "DOOM", doomId);
            var otherKey = VrModNameMatcher.MakeDecisionKey("rototrobot", "DOOM 2", doom2Id);

            settings.ClearDecisionsForGame(doomId);

            Assert.That(settings.GetDecisionTimestampUtc(clearedKey), Is.Null);
            Assert.That(settings.GetDecisionTimestampUtc(otherKey), Is.Not.Null);
        }

        // --- Changed-tag baseline (SourceTagsByDecision) --------------------------

        [Test]
        public void SetDecision_Accepted_WithSourceTags_StoresBaseline()
        {
            var settings = new VrModTagSettings();
            var doomId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "DOOM", doomId, MatchDecision.Accepted, "VRMod Flat2VR Port\nVRMod 6DOF");

            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "DOOM", doomId);
            Assert.That(settings.GetSourceTags(key), Is.EqualTo(new[] { "VRMod Flat2VR Port", "VRMod 6DOF" }));
        }

        [Test]
        public void SetDecision_Accepted_WithoutSourceTags_NoBaseline()
        {
            var settings = new VrModTagSettings();
            var doomId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "DOOM", doomId, MatchDecision.Accepted);

            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "DOOM", doomId);
            Assert.That(settings.GetSourceTags(key), Is.Null);
        }

        [Test]
        public void GetSourceTags_Absent_ReturnsNull()
        {
            var settings = new VrModTagSettings();
            var absentId = Guid.NewGuid();
            Assert.That(settings.GetSourceTags($"nope||nope||{absentId:D}"), Is.Null);
        }

        [Test]
        public void ClearDecision_RemovesSourceTagsBaseline()
        {
            var settings = new VrModTagSettings();
            var doomId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "DOOM", doomId, MatchDecision.Accepted, "VRMod Flat2VR Port");
            settings.ClearDecision("rototrobot", "DOOM", doomId);

            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "DOOM", doomId);
            Assert.That(settings.GetSourceTags(key), Is.Null);
        }

        [Test]
        public void ClearAllDecisions_ClearsSourceTagsBaseline()
        {
            var settings = new VrModTagSettings();
            var doomId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "DOOM", doomId, MatchDecision.Accepted, "VRMod Flat2VR Port");
            settings.ClearAllDecisions();

            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "DOOM", doomId);
            Assert.That(settings.GetSourceTags(key), Is.Null);
        }

        [Test]
        public void SetDecision_Rejected_RemovesAnyExistingBaseline()
        {
            var settings = new VrModTagSettings();
            var doomId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "DOOM", doomId, MatchDecision.Accepted, "VRMod Flat2VR Port");
            // Re-deciding as rejected (user flips it) drops the baseline.
            settings.SetDecision("rototrobot", "DOOM", doomId, MatchDecision.Rejected);

            var key = VrModNameMatcher.MakeDecisionKey("rototrobot", "DOOM", doomId);
            Assert.That(settings.GetSourceTags(key), Is.Null);
        }

        [Test]
        public void IsSourceLinksEnabled_Default_AllSourcesEnabled()
        {
            var settings = new VrModTagSettings();

            Assert.That(settings.DisabledLinkSources, Is.Empty);
            Assert.That(settings.IsSourceLinksEnabled("PcvrCentral"), Is.True);
            Assert.That(settings.IsSourceLinksEnabled("Flat2VR"), Is.True);
        }

        [Test]
        public void IsSourceLinksEnabled_DisabledSource_ReturnsFalse()
        {
            var settings = new VrModTagSettings();
            settings.DisabledLinkSources.Add("pCvRcEnTrAl");

            // Case-insensitive match against the source ID.
            Assert.That(settings.IsSourceLinksEnabled("PcvrCentral"), Is.False);
            Assert.That(settings.IsSourceLinksEnabled("Flat2VR"), Is.True);
        }

        [Test]
        public void IsSourceLinksEnabled_EmptyId_ReturnsFalse()
        {
            var settings = new VrModTagSettings();

            Assert.That(settings.IsSourceLinksEnabled(string.Empty), Is.False);
            Assert.That(settings.IsSourceLinksEnabled(null!), Is.False);
        }

        [Test]
        public void RemoveLegacyNameKeyedDecisions_RemovesLegacyKeys_KeepsIdKeyed()
        {
            var settings = new VrModTagSettings();
            var doomId = Guid.NewGuid();
            string idKey = VrModNameMatcher.MakeDecisionKey("rototrobot", "DOOM", doomId);

            // Legacy 3-segment name-keyed entry (pre-Id-migration format), in all
            // three dictionaries.
            string legacyKey = "rototrobot||DOOM||DOOM";
            settings.MatchDecisions[legacyKey] = "Accepted";
            settings.DecisionTimestamps[legacyKey] = "2026-10-01T00:00:00Z";
            settings.SourceTagsByDecision[legacyKey] = "VRMod PCVR Port";

            // Even older 4-segment key, orphaned in the timestamps dictionary only.
            string legacyFourSegment = "rototrobot||DOOM||DOOM||PCVR Mods Hub";
            settings.DecisionTimestamps[legacyFourSegment] = "2026-09-01T00:00:00Z";

            // Current-format entry that must survive.
            settings.MatchDecisions[idKey] = "Accepted";
            settings.DecisionTimestamps[idKey] = "2026-10-17T00:00:00Z";
            settings.SourceTagsByDecision[idKey] = "VRMod PCVR Port";

            int removed = settings.RemoveLegacyNameKeyedDecisions();

            Assert.That(removed, Is.EqualTo(2));
            Assert.That(settings.MatchDecisions, Does.Not.ContainKey(legacyKey));
            Assert.That(settings.DecisionTimestamps, Does.Not.ContainKey(legacyKey));
            Assert.That(settings.SourceTagsByDecision, Does.Not.ContainKey(legacyKey));
            Assert.That(settings.DecisionTimestamps, Does.Not.ContainKey(legacyFourSegment));
            Assert.That(settings.MatchDecisions[idKey], Is.EqualTo("Accepted"));
            Assert.That(settings.DecisionTimestamps, Does.ContainKey(idKey));
            Assert.That(settings.SourceTagsByDecision, Does.ContainKey(idKey));
        }

        [Test]
        public void RemoveLegacyNameKeyedDecisions_AlreadyClean_ReturnsZero()
        {
            var settings = new VrModTagSettings();
            var doomId = Guid.NewGuid();
            settings.SetDecision("rototrobot", "DOOM", doomId, MatchDecision.Accepted, "VRMod PCVR Port");

            Assert.That(settings.RemoveLegacyNameKeyedDecisions(), Is.EqualTo(0));
            Assert.That(settings.GetDecision("rototrobot", "DOOM", doomId),
                Is.EqualTo(MatchDecision.Accepted));
        }

        [Test]
        public void RemoveLegacyNameKeyedDecisions_EmptySettings_ReturnsZero()
        {
            var settings = new VrModTagSettings();

            Assert.That(settings.RemoveLegacyNameKeyedDecisions(), Is.EqualTo(0));
        }
    }
}
