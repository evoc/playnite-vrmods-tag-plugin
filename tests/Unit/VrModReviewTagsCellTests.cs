using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace Playnite.VRMods.Tests.Unit
{
    /// <summary>
    /// Task 189: the review dialog's Tags preview was clipped with no ellipsis and no tooltip - a
    /// five-tag row's TextBlock measured 864 px inside a 495 px column at 150 % DPI, and the cell cut
    /// it mid-word. The shipped fix is trimming plus a tooltip carrying the text the clip removed.
    /// These tests read the shipped XAML because the clipping belongs to the column and its cell
    /// template, not to any type a unit test can instantiate.
    /// </summary>
    [TestFixture]
    public class VrModReviewTagsCellTests
    {
        private const string DialogRelative = @"source\VrModReviewDialog.xaml";

        private static XElement TagsColumn()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, DialogRelative)))
            {
                dir = dir.Parent;
            }

            Assert.That(dir, Is.Not.Null, "could not locate " + DialogRelative + " above " + AppContext.BaseDirectory);

            // The dialog's XAML declares a default namespace, so every name match has to ignore it.
            var doc = XDocument.Load(Path.Combine(dir!.FullName, DialogRelative));
            return doc.Descendants().Single(e =>
                e.Name.LocalName == "DataGridTemplateColumn" &&
                Named(e, "TagsColumn"));
        }

        private static bool Named(XElement element, string name)
        {
            return element.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name);
        }

        private static XElement TagsCell()
        {
            return TagsColumn().Descendants().First(e => e.Name.LocalName == "TextBlock");
        }

        private static string Attr(XElement element, string localName)
        {
            var attribute = element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName);
            return attribute?.Value ?? string.Empty;
        }

        [Test]
        public void TagsCell_TrimsWithAnEllipsisInsteadOfCuttingMidWord()
        {
            Assert.That(Attr(TagsCell(), "TextTrimming"), Is.EqualTo("CharacterEllipsis"),
                "a clipped preview has to say that it is clipped");
        }

        [Test]
        public void TagsCell_HasATooltipCarryingTheTextTheClipRemoves()
        {
            var tooltip = Attr(TagsCell(), "ToolTip");
            Assert.That(tooltip, Is.Not.Empty, "the clipped text needs somewhere to be read");
            Assert.That(tooltip, Does.Contain("TagPreviewWithMarker"),
                "by default the tooltip repeats the whole preview, which is what the column cannot display");
        }

        [Test]
        public void TagsCell_StillPrefersTheDeltaTooltipWhenTheTagsChanged()
        {
            var trigger = TagsCell().Descendants()
                .Single(e => e.Name.LocalName == "DataTrigger" && (Attr(e, "Binding") ?? "").Contains("TagsChanged"));
            var setters = trigger.Descendants()
                .Where(e => e.Name.LocalName == "Setter")
                .ToDictionary(e => Attr(e, "Property"), e => Attr(e, "Value"));
            Assert.That(setters.Keys, Does.Contain("ToolTip"),
                "a changed row must keep showing the tag delta, not only the preview");
            Assert.That(setters["ToolTip"], Does.Contain("TagDeltaTooltip"));
            Assert.That(setters.Keys, Does.Contain("FontStyle"), "the italic marker for a changed row must survive");
        }

        [Test]
        public void TagsCell_KeepsTheStarWidthColumnItWasMeasuredIn()
        {
            Assert.That(Attr(TagsColumn(), "Width"), Is.EqualTo("*"),
                "the tags column still takes what the other columns leave over: trimming is the fix, not width");
        }
    }
}
