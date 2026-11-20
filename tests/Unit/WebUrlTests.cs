using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for WebUrl.IsSafeWebUrl — the guard that decides which URLs may be handed to
    /// the OS shell for opening in the default browser. Only absolute http/https URLs pass.
    /// </summary>
    [TestFixture]
    public class WebUrlTests
    {
        [TestCase("https://pcvrcentral.com/games/doom")]
        [TestCase("http://example.com/")]
        [TestCase("https://example.com")]
        [TestCase("HTTPS://EXAMPLE.COM/PATH?A=1#Frag")]
        public void HttpAndHttps_AreSafe(string url)
        {
            Assert.That(WebUrl.IsSafeWebUrl(url), Is.True);
        }

        [TestCase("file:///C:/Program Files/game.exe")]
        [TestCase("file:///etc/passwd")]
        [TestCase("ftp://example.com/mod.zip")]
        [TestCase("ms-settings:")]
        [TestCase("javascript:alert(1)")]
        [TestCase("data:text/html;base64,PHNjcmlwdD4=")]
        [TestCase("steam://openapp/730")]
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not a url")]
        [TestCase("/relative/path")]
        [TestCase("example.com/path")]
        public void NonHttpSchemes_AreRejected(string? url)
        {
            Assert.That(WebUrl.IsSafeWebUrl(url), Is.False);
        }

        /// <summary>
        /// Link policy (WebUrl.IsSafeLinkUrl) is what guards the URLs the plugin writes into the
        /// library as game Links. Playnite opens a stored Link with the OS shell
        /// (Process.Start(UseShellExecute = true)) without validating the scheme itself, so a Link
        /// URL must be a plain http(s) URL with a host, no URL credentials and a bounded length.
        /// </summary>
        [TestCase("file:///C:/Windows/System32/calc.exe")]
        [TestCase("javascript:alert(1)")]
        [TestCase("steam://rungameid/730")]
        [TestCase("https://user:pass@example.com/mod")]
        [TestCase("https://pcvr-central.com@evil.tld/skyrim")]
        [TestCase("https:///no-host/path")]
        [TestCase(null)]
        [TestCase("")]
        public void LinkPolicy_RejectsHostileUrls(string? url)
        {
            Assert.That(WebUrl.IsSafeLinkUrl(url), Is.False);
        }

        [Test]
        public void LinkPolicy_RejectsOversizedUrl()
        {
            var tooLong = "https://example.com/" + new string('a', WebUrl.MaxUrlLength);
            Assert.That(WebUrl.IsSafeLinkUrl(tooLong), Is.False);
            Assert.That(WebUrl.IsSafeLinkUrl("https://example.com/game/a"), Is.True);
        }

        /// <summary>
        /// The shell hand-off validates inside the helper (WebUrl.TryOpenInDefaultBrowser), so a call
        /// site cannot open a URL the scheme check was meant to filter. A rejection returns false and
        /// starts nothing. The accepted path is not exercised here: it would launch a real browser on
        /// the machine running the suite, and the acceptance rule itself is the IsSafeLinkUrl tests
        /// above.
        /// </summary>
        [TestCase("file:///C:/Windows/System32/calc.exe")]
        [TestCase("javascript:void(0)")]
        [TestCase("steam://rungameid/730")]
        [TestCase("ms-settings:regionlanguage")]
        [TestCase("https://pcvr-central.com@evil.tld/mod")]
        [TestCase(null)]
        [TestCase("")]
        public void OpenInBrowser_RejectsHostileUrlsBeforeTheShellSeesThem(string? url)
        {
            Assert.That(WebUrl.TryOpenInDefaultBrowser(url), Is.False);
        }

        [TestCase("https://example.com/game/a")]
        [TestCase("https://example.com:8443/game/a?x=1#y")]
        [TestCase("http://localhost/mod")]
        public void LinkPolicy_AcceptsPlainHttpAndHttps(string url)
        {
            Assert.That(WebUrl.IsSafeLinkUrl(url), Is.True);
        }

        /// <summary>
        /// Same-site test behind the source-link rule: a link the plugin writes must live on the
        /// site of the source that published it, so it is a page that source owns — a mod page, a
        /// game page on the source, or the source's front door. "One site" ignores a leading
        /// "www." (sources publish both spellings) and the scheme, but not the port.
        /// </summary>
        [TestCase("https://www.example.com/games/skyrim/", "https://example.com/")]
        [TestCase("https://example.com/games/skyrim/", "https://www.example.com/")]
        [TestCase("https://example.com", "https://www.example.com")]
        [TestCase("https://example.com/mod", "http://example.com/")]
        [TestCase("https://EXAMPLE.com/a", "https://example.com/")]
        [TestCase("https://example.com:8443/a", "https://example.com:8443/")]
        [TestCase("https://compoundvr.com/games/a-plague-tale-innocence/", "https://compoundvr.com")]
        public void SameSiteHost_AcceptsOneSiteUnderBothSpellings(string url, string siteUrl)
        {
            Assert.That(WebUrl.IsSameSiteHost(url, siteUrl), Is.True);
        }

        /// <summary>
        /// Everything a source link must not be: another site's page for the game (the Flat2VR
        /// off-site cards, the curator sources' store pages), a host built to look familiar, a
        /// different port, a credential-confusion URL, or something that is not a link the plugin
        /// could vouch for.
        /// </summary>
        [TestCase("https://postal2vr.com/", "https://flat2vrstudios.com/")]
        [TestCase("https://store.steampowered.com/app/72850/", "https://modsource.example/")]
        [TestCase("https://pcvrcentral.com.evil.tld/games/a", "https://pcvrcentral.com/")]
        [TestCase("https://www2.example.com/a", "https://example.com/")]
        [TestCase("https://example.com:8443/a", "https://example.com/")]
        [TestCase("https://example.com/a", "https://other.example/")]
        [TestCase("https://pcvr-central.com@evil.tld/skyrim", "https://pcvrcentral.com/")]
        [TestCase("https://example.com/a", null)]
        [TestCase(null, "https://example.com/")]
        [TestCase("games/skyrim", "https://example.com/")]
        [TestCase("file:///C:/mods/skyrim.html", "https://example.com/")]
        public void SameSiteHost_RejectsAnyOtherSite(string? url, string? siteUrl)
        {
            Assert.That(WebUrl.IsSameSiteHost(url, siteUrl), Is.False);
        }

        /// <summary>Which side of the pair carries the "www." must not change the answer, or the
        /// rule depends on how a source happened to register itself.</summary>
        [Test]
        public void SameSiteHost_IsSymmetric()
        {
            Assert.That(
                WebUrl.IsSameSiteHost("https://www.example.com/a", "https://example.com/"),
                Is.EqualTo(WebUrl.IsSameSiteHost("https://example.com/a", "https://www.example.com/")));
        }
    }
}
