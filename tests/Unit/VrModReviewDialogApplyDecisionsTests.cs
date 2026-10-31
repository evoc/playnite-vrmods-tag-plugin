using System;
using System.Collections.Generic;
using System.Linq;
using NSubstitute;
using NUnit.Framework;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.VRMods;
using PlayniteVRMods.Tests.TestHelpers;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Regression tests for <see cref="VrModReviewDialog.ApplyDecisions"/> — the core of the
    /// review dialog's Save button. The reported bug: a match changed to <b>Pending</b> that
    /// already had a saved decision (Accepted/Rejected) must have that decision cleared, so it
    /// does not silently re-load as the old value the next time the dialog opens.
    /// </summary>
    [TestFixture]
    public class VrModReviewDialogApplyDecisionsTests
    {
        private const string SourceId = "rototrobot";
        private const string Category = "UEVR";

        private IPlayniteAPI _api = null!;
        private FakeItemCollection<Game> _games = null!;
        private FakeItemCollection<Tag> _tags = null!;
        private VrModTagSettings _settings = null!;

        [SetUp]
        public void SetUp()
        {
            _settings = new VrModTagSettings();
            _games = new FakeItemCollection<Game>();
            _tags = new FakeItemCollection<Tag>();

            var db = Substitute.For<IGameDatabaseAPI>();
            db.Games.Returns(_games);
            db.Tags.Returns(_tags);
            db.BufferedUpdate().Returns(new FakeBufferedUpdate());

            _api = Substitute.For<IPlayniteAPI>();
            _api.Database.Returns(db);
        }

        private Game MakeGame(string name)
        {
            var game = new Game(name) { Id = Guid.NewGuid(), TagIds = new List<Guid>() };
            _games.Items.Add(game);
            return game;
        }

        private VrModReviewMatchEntry MakeMatch(Game game, string sourceGame, MatchDecision status, MatchDecision? persisted)
        {
            return new VrModReviewMatchEntry
            {
                SourceId = SourceId,
                SourceGameName = sourceGame,
                DecisionKey = DecisionKeys.Named(SourceId, sourceGame, game.Id),
                PlayniteGame = game,
                Category = Category,
                Status = status,
                PersistedDecision = persisted
            };
        }

        // One tag per name (like the real database) so FirstOrDefault-by-name resolves consistently.
        private Tag GetOrCreateTag(string name)
        {
            var tag = _tags.FirstOrDefault(t => t.Name == name);
            if (tag == null)
                tag = _tags.Add(name);
            return tag;
        }

        private void GiveGameVrTags(Game game)
        {
            game.TagIds.Add(GetOrCreateTag(VrModCategories.TagNameFor(Category)).Id);
            game.TagIds.Add(GetOrCreateTag(VrModCategories.TagUmbrella).Id);
        }

        private bool GameHasTag(Game game, string tagName) =>
            game.TagIds.Any(id => _tags.FirstOrDefault(t => t.Id == id)?.Name == tagName);

        private void AssertDecision(string sourceGame, Guid playniteGameId, MatchDecision expected) =>
            Assert.That(_settings.GetDecision(SourceId, sourceGame, playniteGameId),
                Is.EqualTo(expected));

        // --- The reported bug: changing a match to Pending must survive Save --------------

        [Test]
        public void ApplyDecisions_RejectedToPending_ClearsSavedDecision()
        {
            var game = MakeGame("DOOM 3");
            var match = MakeMatch(game, "DOOM 3 VR", MatchDecision.Pending, MatchDecision.Rejected);
            _settings.SetDecision(SourceId, "DOOM 3 VR", game.Id, MatchDecision.Rejected);

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            // The saved Rejected decision is gone — the match is back to Pending.
            AssertDecision("DOOM 3 VR", game.Id, MatchDecision.Pending);
        }

        [Test]
        public void ApplyDecisions_RejectedToPending_ClearsTimestamp()
        {
            var game = MakeGame("DOOM 3");
            var match = MakeMatch(game, "DOOM 3 VR", MatchDecision.Pending, MatchDecision.Rejected);
            _settings.SetDecision(SourceId, "DOOM 3 VR", game.Id, MatchDecision.Rejected);

            var key = VrModNameMatcher.MakeDecisionKey(SourceId, "DOOM 3 VR", game.Id);
            Assert.That(_settings.GetDecisionTimestampUtc(key), Is.Not.Null);

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            Assert.That(_settings.GetDecisionTimestampUtc(key), Is.Null);
        }

        [Test]
        public void ApplyDecisions_AcceptedToPending_ClearsDecisionAndUntags()
        {
            var game = MakeGame("DOOM 3");
            GiveGameVrTags(game);
            var match = MakeMatch(game, "DOOM 3 VR", MatchDecision.Pending, MatchDecision.Accepted);
            _settings.SetDecision(SourceId, "DOOM 3 VR", game.Id, MatchDecision.Accepted);

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            // Decision cleared AND the VRMod category tag is removed (a tag implies accepted).
            AssertDecision("DOOM 3 VR", game.Id, MatchDecision.Pending);
            Assert.That(GameHasTag(game, VrModCategories.TagNameFor(Category)), Is.False);
        }

        [Test]
        public void ApplyDecisions_PendingNeverDecided_IsNoOp()
        {
            var game = MakeGame("DOOM 3");
            var match = MakeMatch(game, "DOOM 3 VR", MatchDecision.Pending, null);

            Assert.DoesNotThrow(() =>
                VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match }));

            AssertDecision("DOOM 3 VR", game.Id, MatchDecision.Pending);
        }

        // --- Regression: Accepted / Rejected behave exactly as before --------------------

        [Test]
        public void ApplyDecisions_Accepted_AppliesTagsAndRecords()
        {
            var game = MakeGame("DOOM 3");
            var match = MakeMatch(game, "DOOM 3 VR", MatchDecision.Accepted, null);

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            AssertDecision("DOOM 3 VR", game.Id, MatchDecision.Accepted);
            Assert.That(GameHasTag(game, VrModCategories.TagNameFor(Category)), Is.True);
        }

        // --- Regression (MEDIUM-1): an accepted match must get the umbrella "VRMod" tag -----
        // The old code added the umbrella only when no tag STARTING with "VRMod" was present;
        // but any category/facet/quality tag added in the same Save already starts with
        // "VRMod", so the umbrella was silently skipped. A full scan adds the umbrella
        // unconditionally, so a review Save must too — otherwise a review-accepted game is
        // missing the master tag until the next full scan (never, for a cache-only run).

        [Test]
        public void ApplyDecisions_Accepted_AddsUmbrellaTag()
        {
            var game = MakeGame("DOOM 3");
            var match = MakeMatch(game, "DOOM 3 VR", MatchDecision.Accepted, null);

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            // Both the category tag AND the master umbrella tag are present.
            Assert.That(GameHasTag(game, VrModCategories.TagNameFor(Category)), Is.True);
            Assert.That(GameHasTag(game, VrModCategories.TagUmbrella), Is.True);
        }

        [Test]
        public void ApplyDecisions_Accepted_FacetOnly_AddsUmbrellaTag()
        {
            var game = MakeGame("Facet Only");
            var match = MakeMatch(game, "Facet 6DOF", MatchDecision.Accepted, null);
            match.Categories.Clear();
            match.Is6DOF = true;

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            // The 6DOF facet AND the master umbrella tag are present.
            Assert.That(GameHasTag(game, VrModCategories.Tag6DOF), Is.True);
            Assert.That(GameHasTag(game, VrModCategories.TagUmbrella), Is.True);
        }

        [Test]
        public void ApplyDecisions_Rejected_RecordsAndUntags()
        {
            var game = MakeGame("DOOM 3");
            GiveGameVrTags(game); // a stale tag that must come off
            var match = MakeMatch(game, "DOOM 3 VR", MatchDecision.Rejected, MatchDecision.Rejected);
            _settings.SetDecision(SourceId, "DOOM 3 VR", game.Id, MatchDecision.Rejected);

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            AssertDecision("DOOM 3 VR", game.Id, MatchDecision.Rejected);
            Assert.That(GameHasTag(game, VrModCategories.TagNameFor(Category)), Is.False);
        }

        // --- Regression (LOW-1): a rejected / reverted match drops its facet tags too -------

        [Test]
        public void ApplyDecisions_Rejected_RemovesFacetTags()
        {
            var game = MakeGame("DOOM 3");
            // The game carries a 6DOF facet tag and a category tag — both must come off.
            game.TagIds.Add(GetOrCreateTag(VrModCategories.Tag6DOF).Id);
            game.TagIds.Add(GetOrCreateTag(VrModCategories.TagNameFor(Category)).Id);

            var match = MakeMatch(game, "DOOM 3 VR", MatchDecision.Rejected, MatchDecision.Rejected);
            match.Is6DOF = true;
            _settings.SetDecision(SourceId, "DOOM 3 VR", game.Id, MatchDecision.Rejected);

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            // Both the 6DOF facet AND the category tag are removed for a rejected match.
            Assert.That(GameHasTag(game, VrModCategories.Tag6DOF), Is.False);
            Assert.That(GameHasTag(game, VrModCategories.TagNameFor(Category)), Is.False);
        }

        [Test]
        public void ApplyDecisions_Rejected_NotFacetMatch_KeepsSiblingFacet()
        {
            var game = MakeGame("Game");
            // The game has a 6DOF facet tag, but THIS match is not 6DOF — it belongs to a sibling
            // match, so it must survive this match's reject (the fix must not over-remove).
            game.TagIds.Add(GetOrCreateTag(VrModCategories.Tag6DOF).Id);

            var match = MakeMatch(game, "Src", MatchDecision.Rejected, MatchDecision.Rejected);
            match.Categories.Clear(); // no category for this match
            _settings.SetDecision(SourceId, "Src", game.Id, MatchDecision.Rejected);

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            // The sibling's 6DOF facet survives (this match isn't 6DOF).
            Assert.That(GameHasTag(game, VrModCategories.Tag6DOF), Is.True);
        }

        // --- Mod-status facet through the review path ---------------------------------------
        // A source-reported mod status is a facet like quality and motion: it is applied on accept,
        // removed on reject, and a tag a match never claimed belongs to a sibling match and stays.

        [Test]
        public void ApplyDecisions_Accepted_AppliesModStatusTags()
        {
            var game = MakeGame("DOOM 3");
            var match = MakeMatch(game, "DOOM 3 VR", MatchDecision.Accepted, null);
            match.ModStatuses = new List<string> { VrModCategories.StatusOutdated, VrModCategories.StatusAbandoned };

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            // Both statuses the source reports for this listing's mods land on the game; a game with
            // a superseded mod and an abandoned one shows both, which is the choice the user has.
            Assert.That(GameHasTag(game, VrModCategories.TagNameForStatus(VrModCategories.StatusOutdated)), Is.True);
            Assert.That(GameHasTag(game, VrModCategories.TagNameForStatus(VrModCategories.StatusAbandoned)), Is.True);
        }

        [Test]
        public void ApplyDecisions_Rejected_RemovesModStatusTag()
        {
            var game = MakeGame("DOOM 3");
            game.TagIds.Add(GetOrCreateTag(VrModCategories.TagNameForStatus(VrModCategories.StatusOutdated)).Id);
            game.TagIds.Add(GetOrCreateTag(VrModCategories.TagNameFor(Category)).Id);

            var match = MakeMatch(game, "DOOM 3 VR", MatchDecision.Rejected, MatchDecision.Rejected);
            match.ModStatuses = new List<string> { VrModCategories.StatusOutdated };
            _settings.SetDecision(SourceId, "DOOM 3 VR", game.Id, MatchDecision.Rejected);

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            Assert.That(GameHasTag(game, VrModCategories.TagNameForStatus(VrModCategories.StatusOutdated)), Is.False);
        }

        [Test]
        public void ApplyDecisions_Rejected_KeepsSiblingMatchsModStatusTag()
        {
            var game = MakeGame("Game");
            // The Abandoned tag came from a different listing of the same game; this match never
            // claimed it, so rejecting this match must not strip it.
            game.TagIds.Add(GetOrCreateTag(VrModCategories.TagNameForStatus(VrModCategories.StatusAbandoned)).Id);

            var match = MakeMatch(game, "Src", MatchDecision.Rejected, MatchDecision.Rejected);
            match.Categories.Clear();
            _settings.SetDecision(SourceId, "Src", game.Id, MatchDecision.Rejected);

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            Assert.That(GameHasTag(game, VrModCategories.TagNameForStatus(VrModCategories.StatusAbandoned)), Is.True);
        }

        [Test]
        public void ApplyDecisions_StatusOutsideTheVocabularyAppliesNoTag()
        {
            // Whatever a cache file or a site redeploy puts in the status slot, a tag name here is
            // still only ever built from a value the vocabulary knows (same gate as every other facet).
            var game = MakeGame("DOOM 3");
            var match = MakeMatch(game, "DOOM 3 VR", MatchDecision.Accepted, null);
            match.ModStatuses = new List<string> { "<b>Outdated</b>" };

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry> { match });

            Assert.That(_tags.Items.Any(t => t.Name.Contains("Outdated")), Is.False);
            Assert.That(GameHasTag(game, VrModCategories.TagNameFor(Category)), Is.True);
        }

        [Test]
        public void ApplyDecisions_MixedBatch_EachRowAppliedIndependently()
        {
            var gAcc = MakeGame("GameA");
            var mAcc = MakeMatch(gAcc, "SrcA", MatchDecision.Accepted, null);

            var gRej = MakeGame("GameB");
            GiveGameVrTags(gRej);
            var mRej = MakeMatch(gRej, "SrcB", MatchDecision.Rejected, null);

            var gPend = MakeGame("GameC");
            GiveGameVrTags(gPend);
            var mPend = MakeMatch(gPend, "SrcC", MatchDecision.Pending, MatchDecision.Accepted);
            _settings.SetDecision(SourceId, "SrcC", gPend.Id, MatchDecision.Accepted);

            VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry>
            {
                mAcc, mRej, mPend
            });

            AssertDecision("SrcA", gAcc.Id, MatchDecision.Accepted);
            Assert.That(GameHasTag(gAcc, VrModCategories.TagNameFor(Category)), Is.True);

            AssertDecision("SrcB", gRej.Id, MatchDecision.Rejected);
            Assert.That(GameHasTag(gRej, VrModCategories.TagNameFor(Category)), Is.False);

            // The previously-accepted row reverted to Pending: cleared + untagged.
            AssertDecision("SrcC", gPend.Id, MatchDecision.Pending);
            Assert.That(GameHasTag(gPend, VrModCategories.TagNameFor(Category)), Is.False);
        }

        [Test]
        public void ApplyDecisions_EmptyList_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
                VrModReviewDialog.ApplyDecisions(_api, _settings, new List<VrModReviewMatchEntry>()));
        }
    }
}
