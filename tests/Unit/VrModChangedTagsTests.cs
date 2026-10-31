using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Covers the "#149 tag change tracking" feature: the facet tag names (single source of
    /// truth in VrModCategories.TagNamesFor), the applied-tag preview on entries, the changed-
    /// tag delta computation, and the RototRobot first/third-person/6DOF facet parsing.
    /// </summary>
    [TestFixture]
    public class VrModChangedTagsTests
    {
        // --- VrModCategories.TagNamesFor (single source of truth) -----------------

        [Test]
        public void TagNamesFor_CategoryOnly_ReturnsCategoryTag()
        {
            var names = VrModCategories.TagNamesFor(new[] { "UEVR" }, null, null, null, null);
            Assert.That(names, Is.EqualTo(new[] { VrModCategories.TagNameFor("UEVR") }));
        }

        [Test]
        public void TagNamesFor_AllFacets_ReturnsAllTags()
        {
            var names = VrModCategories.TagNamesFor(new[] { "UEVR" }, true, true, true, new[] { "Excellent" });
            Assert.That(names, Is.EqualTo(new[]
            {
                VrModCategories.TagNameFor("UEVR"),
                VrModCategories.Tag6DOF,
                VrModCategories.TagFirstPerson,
                VrModCategories.TagThirdPerson,
                VrModCategories.TagNameForQuality("Excellent")
            }));
        }

        [Test]
        public void TagNamesFor_NoFacets_ReturnsCategoryOnly()
        {
            var names = VrModCategories.TagNamesFor(new[] { "Flat2VR Port" }, false, false, false, null);
            Assert.That(names, Is.EqualTo(new[] { VrModCategories.TagNameFor("Flat2VR Port") }));
        }

        [Test]
        public void TagNamesFor_EntryOverload_MatchesFieldsOverload()
        {
            var entry = new VrModEntry
            {
                Category = "UEVR",
                Is6DOF = true,
                IsFirstPerson = true,
                IsThirdPerson = null,
                Quality = "Playable"
            };
            Assert.That(VrModCategories.TagNamesFor(entry),
                Is.EqualTo(VrModCategories.TagNamesFor(new[] { "UEVR" }, true, true, null, new[] { "Playable" })));
        }

        // --- AppliedTagNames / TagPreview (entry + review match) -------------------

        [Test]
        public void Entry_AppliedTagNames_WithFacets()
        {
            var entry = new VrModEntry
            {
                Category = "UEVR",
                Is6DOF = true,
                IsFirstPerson = true,
                IsThirdPerson = null,
                Quality = null
            };
            Assert.That(entry.AppliedTagNames, Is.EqualTo(new[]
            {
                VrModCategories.TagNameFor("UEVR"),
                VrModCategories.Tag6DOF,
                VrModCategories.TagFirstPerson
            }));
        }

        [Test]
        public void ReviewMatch_TagPreview_IncludesFacets()
        {
            var match = new VrModReviewMatchEntry
            {
                SourceId = "rototrobot",
                Category = "UEVR",
                Is6DOF = true,
                IsFirstPerson = true,
                IsThirdPerson = true,
                Quality = "Excellent"
            };
            var preview = match.TagPreview;
            Assert.That(preview, Does.Contain(VrModCategories.TagNameFor("UEVR")));
            Assert.That(preview, Does.Contain(VrModCategories.Tag6DOF));
            Assert.That(preview, Does.Contain(VrModCategories.TagFirstPerson));
            Assert.That(preview, Does.Contain(VrModCategories.TagThirdPerson));
            Assert.That(preview, Does.Contain(VrModCategories.TagNameForQuality("Excellent")));
        }

        [Test]
        public void ReviewMatch_AppliedTagNames_UsesTagNamesFor()
        {
            var match = new VrModReviewMatchEntry
            {
                Category = "UEVR",
                Is6DOF = false,
                IsFirstPerson = null,
                IsThirdPerson = null,
                Quality = null
            };
            Assert.That(match.AppliedTagNames,
                Is.EqualTo(new[] { VrModCategories.TagNameFor("UEVR") }));
        }

        // --- ComputeTagDelta (the changed-tag detection) ---------------------------

        [Test]
        public void ComputeTagDelta_NoBaseline_ReturnsFalse()
        {
            // A decision that predates this feature has no stored baseline → no change.
            bool changed = VrModTagService.ComputeTagDelta(null,
                new[] { VrModCategories.TagNameFor("UEVR"), VrModCategories.Tag6DOF }, out var delta);
            Assert.That(changed, Is.False);
            Assert.That(delta, Is.Empty);
        }

        [Test]
        public void ComputeTagDelta_IdenticalTags_ReturnsFalse()
        {
            var tags = new[] { VrModCategories.TagNameFor("UEVR"), VrModCategories.Tag6DOF };
            bool changed = VrModTagService.ComputeTagDelta(new List<string>(tags), tags, out var delta);
            Assert.That(changed, Is.False);
            Assert.That(delta, Is.Empty);
        }

        [Test]
        public void ComputeTagDelta_SourceAdded6DOF_ReturnsTrueWithPlus()
        {
            var stored = new List<string> { VrModCategories.TagNameFor("UEVR") };
            var current = new[] { VrModCategories.TagNameFor("UEVR"), VrModCategories.Tag6DOF };
            bool changed = VrModTagService.ComputeTagDelta(stored, current, out var delta);
            Assert.That(changed, Is.True);
            Assert.That(delta, Does.StartWith("+ "));
            Assert.That(delta, Does.Contain(VrModCategories.Tag6DOF));
        }

        [Test]
        public void ComputeTagDelta_SourceRemoved6DOF_ReturnsTrueWithMinus()
        {
            var stored = new List<string> { VrModCategories.TagNameFor("UEVR"), VrModCategories.Tag6DOF };
            var current = new[] { VrModCategories.TagNameFor("UEVR") };
            bool changed = VrModTagService.ComputeTagDelta(stored, current, out var delta);
            Assert.That(changed, Is.True);
            Assert.That(delta, Does.Contain("- "));
            Assert.That(delta, Does.Contain(VrModCategories.Tag6DOF));
        }

        [Test]
        public void ComputeTagDelta_BothAddedAndRemoved_ShowsBoth()
        {
            var stored = new List<string> { VrModCategories.TagNameFor("UEVR"), VrModCategories.Tag6DOF };
            var current = new[] { VrModCategories.TagNameFor("UEVR"), VrModCategories.TagFirstPerson };
            bool changed = VrModTagService.ComputeTagDelta(stored, current, out var delta);
            Assert.That(changed, Is.True);
            Assert.That(delta, Does.Contain("+ "));
            Assert.That(delta, Does.Contain("- "));
            Assert.That(delta, Does.Contain(VrModCategories.TagFirstPerson));
            Assert.That(delta, Does.Contain(VrModCategories.Tag6DOF));
        }

        // --- RototRobot facet parsing (1st / 3rd / 6dof) --------------------------

        private static System.Collections.Generic.IEnumerable<VrModEntry> Parse(string html)
        {
            var parser = new RototRobotVrModParser();
            return parser.ParseRenderedHtml(html);
        }

        [Test]
        public void RototRobot_ParsesFirstPersonFacet()
        {
            var html = @"<div class=""game-item"" data-tags=""uevr 1st"">
                <span class=""title-main"">Skyrim</span>
                <span class=""title-sub"">by Author</span>
                <span class=""data-secondary-link"" data-url=""https://store.steampowered.com/app/720000/Skyrim/"" data-text=""View Store Page"">
            </div>";
            var entries = Parse(html).ToList();
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].IsFirstPerson, Is.True);
            Assert.That(entries[0].IsThirdPerson, Is.Null);
        }

        [Test]
        public void RototRobot_ParsesThirdPersonFacet()
        {
            var html = @"<div class=""game-item"" data-tags=""uevr 3rd"">
                <span class=""title-main"">Skyrim</span>
                <span class=""title-sub"">by Author</span>
                <span class=""data-secondary-link"" data-url=""https://store.steampowered.com/app/720000/Skyrim/"" data-text=""View Store Page"">
            </div>";
            var entries = Parse(html).ToList();
            Assert.That(entries[0].IsThirdPerson, Is.True);
            Assert.That(entries[0].IsFirstPerson, Is.Null);
        }

        [Test]
        public void RototRobot_ParsesSixDOFFacet()
        {
            var html = @"<div class=""game-item"" data-tags=""uevr 6dof"">
                <span class=""title-main"">Skyrim</span>
                <span class=""title-sub"">by Author</span>
                <span class=""data-secondary-link"" data-url=""https://store.steampowered.com/app/720000/Skyrim/"" data-text=""View Store Page"">
            </div>";
            var entries = Parse(html).ToList();
            Assert.That(entries[0].Is6DOF, Is.True);
        }

        [Test]
        public void RototRobot_NoFacetTags_FacetsAreNull()
        {
            var html = @"<div class=""game-item"" data-tags=""uevr"">
                <span class=""title-main"">Skyrim</span>
                <span class=""title-sub"">by Author</span>
                <span class=""data-secondary-link"" data-url=""https://store.steampowered.com/app/720000/Skyrim/"" data-text=""View Store Page"">
            </div>";
            var entries = Parse(html).ToList();
            Assert.That(entries[0].Is6DOF, Is.Null);
            Assert.That(entries[0].IsFirstPerson, Is.Null);
            Assert.That(entries[0].IsThirdPerson, Is.Null);
        }
    }
}
