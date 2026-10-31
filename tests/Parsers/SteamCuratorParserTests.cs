using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Parsers
{
    /// <summary>
    /// Tests for <see cref="SteamCuratorParser"/> (the Steam Curator recommendations parser used
    /// by the "Steam Curator UEVR Compatibility" and "Steam Curator VorpX Games" sources).
    /// Drives the JSON→items path against the recorded curator page without the network.
    /// </summary>
    [TestFixture]
    public class SteamCuratorParserTests
    {
        private static string GetSnapshotPath(string fileName)
        {
            var assemblyDir = System.IO.Path.GetDirectoryName(typeof(SteamCuratorParserTests).Assembly.Location);
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(assemblyDir!, "..", "..", "..", "Parsers", "snapshots", fileName));
        }

        private static string ReadFixture(string fileName)
        {
            var path = GetSnapshotPath(fileName);
            Assert.That(File.Exists(path), Is.True, $"Snapshot file not found: {path}");
            return File.ReadAllText(path);
        }

        [Test]
        public void SlugToName_ConvertsUnderscoresToSpaces()
        {
            Assert.That(SteamCuratorParser.SlugToName("dreamslayer"), Is.EqualTo("dreamslayer"));
            Assert.That(SteamCuratorParser.SlugToName("star_wars_jedi"), Is.EqualTo("star wars jedi"));
            Assert.That(SteamCuratorParser.SlugToName("a__b__c"), Is.EqualTo("a b c"));
        }

        [Test]
        public void SlugToName_TrimsAndCollapsesWhitespace()
        {
            Assert.That(SteamCuratorParser.SlugToName("  spaced__out  "), Is.EqualTo("spaced out"));
        }

        [Test]
        public void SlugToName_EmptyReturnsEmpty()
        {
            Assert.That(SteamCuratorParser.SlugToName(""), Is.EqualTo(""));
            Assert.That(SteamCuratorParser.SlugToName("   "), Is.EqualTo(""));
        }

        [Test]
        public void ParseCuratorPage_Fixture_ParsesAllRecommendations()
        {
            var parser = new SteamCuratorParser("SteamCuratorUEVR", "Steam Curator UEVR Compatibility",
                "https://store.steampowered.com/curator/44946997/", 44946997, "UEVR Compatibility");
            var json = ReadFixture("curator-44946997-ajax.json");
            var seen = new HashSet<string>();
            var items = new List<SteamCuratorParser.CuratorItem>();

            var totalCount = parser.ParseCuratorPage(json, seen, items);

            Assert.That(totalCount, Is.EqualTo(21));
            Assert.That(items, Has.Count.EqualTo(21));

            // First recommendation: Dreamslayer (appid 2670250) with the curator's note.
            var first = items[0];
            Assert.That(first.AppId, Is.EqualTo("2670250"));
            Assert.That(first.Slug, Is.EqualTo("Dreamslayer"));
            Assert.That(first.Note, Does.StartWith("This free retro arena shooter plays well in UEVR"));
        }

        [Test]
        public void ParseCuratorPage_EveryItemHasAppIdAndSlug()
        {
            var parser = new SteamCuratorParser("SteamCuratorUEVR", "x", "https://store.steampowered.com/curator/44946997/", 44946997, "UEVR Compatibility");
            var json = ReadFixture("curator-44946997-ajax.json");
            var seen = new HashSet<string>();
            var items = new List<SteamCuratorParser.CuratorItem>();

            parser.ParseCuratorPage(json, seen, items);

            foreach (var i in items)
            {
                Assert.That(i.AppId, Is.Not.Empty);
                Assert.That(i.Slug, Is.Not.Empty);
            }
        }

        [Test]
        public void ParseCuratorPage_SuccessNotOne_Throws()
        {
            var parser = new SteamCuratorParser("SteamCuratorX", "x", "https://store.steampowered.com/curator/1/", 1, "UEVR Compatibility");
            // A curator that is gone/changed returns success != 1 → treated as a FAILURE, not "no data".
            // The failure is typed (InvalidOperationException, not a bare Exception), so a caller can
            // name what it caught.
            var json = "{\"success\": 0, \"total_count\": 0, \"results_html\": \"\"}";

            Assert.Throws<InvalidOperationException>(() => parser.ParseCuratorPage(json, new HashSet<string>(), new List<SteamCuratorParser.CuratorItem>()));
        }

        [Test]
        public void ParseRecommendations_SplitsBlocksAndDedupesByAppId()
        {
            var parser = new SteamCuratorParser("SteamCuratorX", "x", "https://store.steampowered.com/curator/1/", 1, "UEVR Compatibility");
            var html = @"<div class=""recommendation"" data-ds-appid=""111"" data-ds-itemkey=""App_111""><a href=""https://store.steampowered.com/app/111/alpha/?snr=1""></a><div class=""recommendation_desc"">""note one""</div></div>" +
                        // Duplicate appid 111 — must be deduped.
                        @"<div class=""recommendation"" data-ds-appid=""111"" data-ds-itemkey=""App_111""><a href=""https://store.steampowered.com/app/111/alpha/?snr=1""></a><div class=""recommendation_desc"">""dup""</div></div>" +
                        @"<div class=""recommendation"" data-ds-appid=""222"" data-ds-itemkey=""App_222""><a href=""https://store.steampowered.com/app/222/beta/?snr=1""></a><div class=""recommendation_desc"">""note two""</div></div>";
            var seen = new HashSet<string>();
            var items = new List<SteamCuratorParser.CuratorItem>();

            SteamCuratorParser.ParseRecommendations(html, seen, items);

            Assert.That(items, Has.Count.EqualTo(2));
            Assert.That(items[0].AppId, Is.EqualTo("111"));
            Assert.That(items[0].Slug, Is.EqualTo("alpha"));
            Assert.That(items[0].Note, Is.EqualTo("note one"));
            Assert.That(items[1].AppId, Is.EqualTo("222"));
            Assert.That(items[1].Slug, Is.EqualTo("beta"));
        }

        [Test]
        public void ParseRecommendations_EmptyHtml_AddsNothing()
        {
            var parser = new SteamCuratorParser("SteamCuratorX", "x", "https://store.steampowered.com/curator/1/", 1, "UEVR Compatibility");
            var items = new List<SteamCuratorParser.CuratorItem>();
            SteamCuratorParser.ParseRecommendations("", new HashSet<string>(), items);
            Assert.That(items, Is.Empty);
        }

        /// <summary>
        /// REGRESSION: a curator list larger than one page (total_count &gt; PageSize=50) must
        /// fetch AND parse EVERY page. Previously only the first page's items were parsed, so a
        /// 97-item curator silently yielded only the first 50. Driven offline via the
        /// <see cref="SteamCuratorParser.FetchCuratorPageAsync"/> seam (no network).
        /// </summary>
        [Test]
        public async Task FetchEntries_PaginatesAcrossPages_AndAggregatesAllItems()
        {
            var parser = new PagedTestCuratorParser();
            var entries = (await parser.FetchEntriesAsync()).ToList();

            // 50 (page 1) + 47 (page 2) = 97, matching total_count.
            Assert.That(entries.Count, Is.EqualTo(97));
            // An item from page 2 must be present (the old bug dropped the entire second page).
            Assert.That(entries.Select(e => e.SteamAppId), Does.Contain("50"));
            Assert.That(entries.Select(e => e.SteamAppId), Does.Contain("96"));
        }

        /// <summary>
        /// A curator that is exactly one page (total_count &le; PageSize) must fetch a single page
        /// and stop — no over-fetch.
        /// </summary>
        [Test]
        public async Task FetchEntries_SinglePage_StopsAfterFirstPage()
        {
            var parser = new SinglePageTestCuratorParser();
            var entries = (await parser.FetchEntriesAsync()).ToList();

            Assert.That(entries.Count, Is.EqualTo(21));
            // The fetcher must have been called exactly once (start=0 only).
            Assert.That(parser.FetchCalls, Is.EqualTo(1));
        }

        /// <summary>
        /// Test double: serves two recorded pages (50 + 47 = 97) through the virtual fetch seam,
        /// mirroring the real VorpX curator (total_count=97 over 2 pages) without the network.
        /// </summary>
        private sealed class PagedTestCuratorParser : SteamCuratorParser
        {
            public PagedTestCuratorParser()
                : base("SteamCuratorTest", "x", "https://store.steampowered.com/curator/1/", 1, "UEVR Compatibility")
            {
            }

            protected override Task<string> FetchCuratorPageAsync(int start)
            {
                // Page 1: appids 0..49. Page 2: appids 50..96. total_count=97 on both.
                return Task.FromResult(MakePage(start == 0 ? 0 : 50, start == 0 ? 50 : 47, 97));
            }
        }

        /// <summary>
        /// Test double: serves a single 21-item page (total_count=21) and records how many times
        /// the fetch seam is called, to assert the loop does not over-fetch.
        /// </summary>
        private sealed class SinglePageTestCuratorParser : SteamCuratorParser
        {
            public int FetchCalls { get; private set; }

            public SinglePageTestCuratorParser()
                : base("SteamCuratorTest", "x", "https://store.steampowered.com/curator/1/", 1, "UEVR Compatibility")
            {
            }

            protected override Task<string> FetchCuratorPageAsync(int start)
            {
                FetchCalls++;
                return Task.FromResult(MakePage(start, 21, 21));
            }
        }

        /// <summary>
        /// Builds one curator AJAX page of <paramref name="count"/> recommendations starting at
        /// <paramref name="start"/>, each with a unique appid, in the live JSON shape.
        /// </summary>
        private static string MakePage(int start, int count, int totalCount)
        {
            var html = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                var appid = start + i;
                html.Append("<div class=\"recommendation\" data-ds-appid=\"").Append(appid)
                  .Append("\" data-ds-itemkey=\"App_").Append(appid)
                  .Append("\"><a href=\"https://store.steampowered.com/app/").Append(appid)
                  .Append("/slug").Append(appid).Append("/?snr=1\"></a></div>");
            }
            // Serialize so the HTML (which contains double quotes) is correctly JSON-escaped,
            // matching the live curator AJAX shape.
            return JsonSerializer.Serialize(new
            {
                success = 1,
                total_count = totalCount,
                results_html = html.ToString(),
            });
        }
    }
}
