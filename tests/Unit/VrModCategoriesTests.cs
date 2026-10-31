using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Pins the tag vocabulary: the exact tag names that appear in Playnite.
    /// A rename here changes what the user sees in the tag UI.
    /// </summary>
    [TestFixture]
    public class VrModCategoriesTests
    {
        [Test]
        public void UmbrellaAnd6DOF_Tags()
        {
            Assert.That(VrModCategories.TagUmbrella, Is.EqualTo("VRMod"));
            Assert.That(VrModCategories.Tag6DOF, Is.EqualTo("VRMod 6DOF"));
        }

        [TestCase("UEVR")]
        [TestCase("REFramework")]
        [TestCase("Flat2VR Port")]
        [TestCase("Dedicated")]
        [TestCase("PCVR Central")]
        [TestCase("PCVR Mods Hub")]
        [TestCase("Mr Survivor")]
        [TestCase("Motion Gesture")]
        [TestCase("Head Tracked")]
        public void TagNameFor_ProducesVrModPrefixedName(string category)
            => Assert.That(VrModCategories.TagNameFor(category), Is.EqualTo($"VRMod {category}"));

        // The one vocabulary rename this plugin has made: RototRobot's "Full Motion Controls"
        // category and PCVR Central's "Full Motion" motion level assert the same thing about a game,
        // and minting both put two tags on one game for one claim.
        [Test]
        public void TagNameFor_LegacyMotionCategory_MintsTheCanonicalMotionTag()
        {
            Assert.That(VrModCategories.TagNameFor(VrModCategories.FullMotionControls), Is.EqualTo("VRMod Full Motion"));
            Assert.That(VrModCategories.TagNameForMotion(VrModCategories.MotionFull), Is.EqualTo("VRMod Full Motion"));
            Assert.That(VrModCategories.TagNameFor(VrModCategories.FullMotionControls),
                Is.EqualTo(VrModCategories.TagNameForMotion(VrModCategories.MotionFull)),
                "the two source spellings must land on one tag, which is the whole point of the alias");
        }

        [Test]
        public void TagNamesFor_CategoryAndFacetSpellingsOfTheSameClaim_MintOneTag()
        {
            var names = VrModCategories.TagNamesFor(
                new[] { VrModCategories.FullMotionControls }, null, null, null,
                null, new[] { VrModCategories.MotionFull }, null);
            Assert.That(names, Is.EquivalentTo(new[] { "VRMod Full Motion" }));
            Assert.That(names, Has.No.Member("VRMod Full Motion Controls"));
        }

        [Test]
        public void LegacySpelling_StillKnownSoOldDataAndOldLibraryTagsStillResolve()
        {
            Assert.That(VrModCategories.IsKnownTagValue(VrModCategories.FullMotionControls), Is.True,
                "a value saved by an older plugin version must not become unknown");
            Assert.That(VrModCategories.IsValidTagName("VRMod Full Motion Controls"), Is.True);
            Assert.That(VrModCategories.IsPluginTagName("VRMod Full Motion Controls"), Is.True,
                "the plugin has to own the old tag or no removal path will ever clean it up");
            Assert.That(VrModCategories.IsLegacyTagValue(VrModCategories.FullMotionControls), Is.True);
            Assert.That(VrModCategories.IsLegacyTagValue(VrModCategories.MotionFull), Is.False);
        }

        [TestCase("VRMod Full Motion Controls", "VRMod Full Motion")]
        [TestCase("VRMod Full Motion", "VRMod Full Motion")]
        [TestCase("VRMod", "VRMod")]
        [TestCase("VRMod 6DOF", "VRMod 6DOF")]
        [TestCase("VRMod UEVR", "VRMod UEVR")]
        [TestCase("VRModding", "VRModding")]
        [TestCase("Some User Tag", "Some User Tag")]
        public void CanonicalTagName_MapsOnlyTheRetiredSpelling(string tagName, string expected)
            => Assert.That(VrModCategories.CanonicalTagName(tagName), Is.EqualTo(expected));

        [Test]
        public void ComputeTagDelta_LegacyBaselineAgainstTheCanonicalName_IsNoChange()
        {
            // A decision accepted before the rename stored "VRMod Full Motion Controls". The match now
            // produces "VRMod Full Motion": the same claim, so it must not be reported as changed.
            var stored = new System.Collections.Generic.List<string> { "VRMod", "VRMod Full Motion Controls", "VRMod UEVR" };
            var current = new System.Collections.Generic.List<string> { "VRMod", "VRMod Full Motion", "VRMod UEVR" };
            Assert.That(VrModTagService.ComputeTagDelta(stored, current, out var delta), Is.False, $"delta was: {delta}");
            Assert.That(delta, Is.Empty);
        }

        [Test]
        public void ComputeTagDelta_StillReportsARealFacetChange()
        {
            var stored = new System.Collections.Generic.List<string> { "VRMod Full Motion Controls" };
            var current = new System.Collections.Generic.List<string> { "VRMod Full Motion", "VRMod 6DOF" };
            Assert.That(VrModTagService.ComputeTagDelta(stored, current, out var delta), Is.True);
            Assert.That(delta, Does.Contain("+ VRMod 6DOF"));
            Assert.That(delta, Does.Not.Contain("Full Motion"), "the rename must not appear as added or removed");
        }

        [TestCase("High")]
        [TestCase("Medium")]
        [TestCase("Low")]
        public void TagNameForQuality_ProducesVrModPrefixedName(string quality)
            => Assert.That(VrModCategories.TagNameForQuality(quality), Is.EqualTo($"VRMod {quality}"));

        [TestCase(VrModCategories.PcvrCentral)]
        [TestCase(VrModCategories.MrNlceHub)]
        [TestCase(VrModCategories.MrSurvivorHub)]
        public void IsCatalogCategory_CatalogCategory_True(string category)
            => Assert.That(VrModCategories.IsCatalogCategory(category), Is.True);

        [TestCase("UEVR")]
        [TestCase("REFramework")]
        [TestCase("Flat2VR Port")]
        [TestCase("Full Motion Controls")]
        public void IsCatalogCategory_FrameworkCategory_False(string category)
            => Assert.That(VrModCategories.IsCatalogCategory(category), Is.False);

        [Test]
        public void TagNamesFor_CatalogCategory_ExcludesCategoryTag()
        {
            // A catalog category contributes no category tag of its own, and a catalog entry
            // carries no facet metadata, so TagNamesFor is empty for a bare catalog entry.
            Assert.That(VrModCategories.TagNamesFor(new[] { VrModCategories.PcvrCentral }, null, null, null, null), Is.Empty);
        }

        [Test]
        public void TagNamesFor_FrameworkCategory_IncludesCategoryTag()
        {
            // A framework category contributes its category tag (no facets here).
            Assert.That(VrModCategories.TagNamesFor(new[] { "UEVR" }, null, null, null, null), Is.EqualTo(new[] { "VRMod UEVR" }));
        }

        [Test]
        public void TagNamesFor_FacetsStillApplyForCatalog()
        {
            // Even though a catalog category tag is suppressed, a catalog entry that DID
            // declare a facet would still contribute it (kept for correctness/forward-compat).
            Assert.That(
                VrModCategories.TagNamesFor(new[] { VrModCategories.PcvrCentral }, true, true, null, null),
                Is.EqualTo(new[] { VrModCategories.Tag6DOF, VrModCategories.TagFirstPerson }));
        }

        [TestCase("UUVR")]
        [TestCase("Depth3D")]
        [TestCase("Other")]
        public void FrameworkCategoryNames_PinExactSpelling(string name)
        {
            // These three are the framework names PCVR Central's own vocabulary contributes. A rename
            // here silently changes every tag a PCVR Central listing produces, and the tag service's
            // changed-tag detection would read it as the source changing, not as a rename.
            Assert.That(VrModCategories.IsKnownTagValue(name), Is.True);
            Assert.That(VrModCategories.IsCatalogCategory(name), Is.False,
                "a framework is taggable; only the four catalog directories are not");
        }

        [TestCase("Active")]
        [TestCase("Update Detected")]
        [TestCase("Outdated")]
        [TestCase("Broken")]
        [TestCase("Abandoned")]
        public void TagNameForStatus_ProducesVrModPrefixedName(string status)
            => Assert.That(VrModCategories.TagNameForStatus(status), Is.EqualTo($"VRMod {status}"));

        [Test]
        public void ModStatusValues_AreKnownTagValuesAndNotCategories()
        {
            // The five status values are the vocabulary's own; they are facet values, not the site
            // names, so a status must never be mistaken for a source that gets the no-tag treatment.
            foreach (var status in new[]
            {
                VrModCategories.StatusActive, VrModCategories.StatusUpdateDetected,
                VrModCategories.StatusOutdated, VrModCategories.StatusBroken, VrModCategories.StatusAbandoned
            })
            {
                Assert.That(VrModCategories.IsKnownTagValue(status), Is.True, status);
                Assert.That(VrModCategories.IsCatalogCategory(status), Is.False, status);
            }
        }

        [Test]
        public void TagNamesFor_IncludesEveryStatusFacetAndNoForeignOne()
        {
            // The applied-tag set is one list built from one vocabulary: a status the vocabulary does
            // not know contributes nothing, exactly as an unknown category or quality does.
            var names = VrModCategories.TagNamesFor(
                new[] { "UEVR" }, null, null, null, null, null,
                new[] { VrModCategories.StatusOutdated, "Probably Fine-ish" });

            Assert.That(names, Is.EquivalentTo(new[] { "VRMod UEVR", "VRMod Outdated" }));
        }
    }
}
