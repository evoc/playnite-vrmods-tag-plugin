using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// The host-confusion shapes a hostile <c>Location</c> header actually uses to LOOK like the site
    /// the plugin asked for while naming a different one: URL credentials in front of a trusted host,
    /// a host that merely ends with a trusted host or with a declared mirror, a protocol-relative
    /// Location, and a port switch on an otherwise identical host.
    ///
    /// <see cref="RedirectPolicyTests"/> covers the ordinary cases (same site, <c>www.</c>, declared
    /// mirror, off-site refusal, hop budget). These are the adversarial spellings of those cases, and
    /// they are what a compromised or aggregating source would publish: every rule here is a string
    /// comparison that a plausible <c>EndsWith</c> or "host starts with the source host" implementation
    /// would get wrong.
    /// </summary>
    [TestFixture]
    public class RedirectHostConfusionTests
    {
        /// <summary>A parser pinned to https://example.com/ plus whatever mirrors it declares.</summary>
        private sealed class GuardedParser : BaseVrModParser
        {
            private readonly string[] _mirrors;

            public GuardedParser(params string[] mirrors)
                : base("guarded-parser", "GuardedParser", "https://example.com/")
                => _mirrors = mirrors;

            protected override IReadOnlyList<string> AllowedRedirectHosts => _mirrors;
            public override string? ChangeIdentifier => null;
            public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
                => Task.FromResult<IEnumerable<VrModEntry>>(Array.Empty<VrModEntry>());
            public override string? GetCanonicalName(VrModEntry entry) => entry.GameName;

            public BaseVrModParser.RedirectDecision Decide(string fromUrl, string location, int hop = 0)
                => ClassifyRedirectTarget(fromUrl, new Uri(location, UriKind.RelativeOrAbsolute), hop);
        }

        private static void AssertRefused(string fromUrl, string location, params string[] mirrors)
        {
            var decision = new GuardedParser(mirrors).Decide(fromUrl, location);

            Assert.That(decision.Allowed, Is.False,
                $"'{location}' was followed to {decision.Target ?? "<no target>"}; it must not be");
            Assert.That(decision.Reason, Is.Not.Null.And.Not.Empty,
                $"'{location}' must be refused with a reason the operator can read");
            TestContext.Out.WriteLine($"refused '{location}': {decision.Reason}");
        }

        // ------------------------------------------------- credentials in front of a host --------------

        /// <summary>
        /// <c>https://example.com@evil.tld/</c> reads as the host this plugin asked for and connects to
        /// another one. The redirect path has to apply the same credential rule that guards written
        /// Links, otherwise a hop reaches a host the Link gate would never have accepted.
        /// </summary>
        [TestCase("https://example.com@evil.tld/mod")]
        [TestCase("https://example.com@evil.tld:443/mod")]
        [TestCase("https://user@example.com/mod")]
        [TestCase("https://user:pass@evil.tld/mod")]
        [TestCase("https://evil.tld@example.com/mod")]
        public void ALocationWithCredentials_IsRefusedEvenWhenItNamesATrustedHost(string location)
            => AssertRefused("https://example.com/list", location);

        // ------------------------------------------------- hosts that merely END right ------------------

        /// <summary>
        /// Suffix matching is the mistake these cases exist to catch: <c>example.com.evil.tld</c> ends
        /// with the trusted host and is a different site, and so is a label that only looks nested.
        /// </summary>
        [TestCase("https://example.com.evil.tld/mod")]
        [TestCase("https://www.example.com.evil.tld/mod")]
        [TestCase("https://example.com.evil.tld:443/mod")]
        [TestCase("https://notexample.com/mod")]
        [TestCase("https://evil.tld/example.com/mod")]
        public void AHostThatOnlyEndsWithTheRequestedHost_IsRefused(string location)
            => AssertRefused("https://example.com/list", location);

        /// <summary>
        /// A declared mirror is a host allow-list entry, not a prefix: nothing hanging off the end of
        /// it becomes allowed.
        /// </summary>
        [TestCase("https://cdn.example.net.evil.tld/mod")]
        [TestCase("https://cdn.example.net.evil.tld:443/mod")]
        [TestCase("https://evil-cdn.example.net/mod")]
        [TestCase("https://cdn.example.net@evil.tld/mod")]
        public void AHostThatOnlyEndsWithADeclaredMirror_IsRefused(string location)
            => AssertRefused("https://example.com/list", location, "cdn.example.net");

        // ------------------------------------------------- Location spellings ---------------------------

        /// <summary>
        /// <c>//evil.tld/x</c> is relative in form and absolute in effect (it keeps the requested
        /// scheme and changes the host), so it has to be resolved and checked as an absolute target.
        /// </summary>
        [TestCase("//evil.tld/mod")]
        [TestCase("//evil.tld:443/mod")]
        public void AProtocolRelativeLocation_IsCheckedAsTheAbsoluteTargetItBecomes(string location)
            => AssertRefused("https://example.com/list", location);

        /// <summary>
        /// Backslash is not a URI delimiter, but HTTP stacks and parsers disagree about treating one as
        /// a separator. Whatever the interpretation, the hop must not end up on a foreign host.
        /// </summary>
        [TestCase("\\evil.tld\\mod")]
        [TestCase("https:\\evil.tld\\mod")]
        [TestCase("/\\evil.tld/mod")]
        public void ALocationWithBackslashes_NeitherLeavesTheHostNorIsFollowedBlindly(string location)
        {
            var decision = new GuardedParser().Decide("https://example.com/list", location);

            if (decision.Allowed)
            {
                var host = new Uri(decision.Target!, UriKind.Absolute).Host;
                Assert.That(host, Is.EqualTo("example.com").Or.EqualTo("www.example.com"),
                    $"the hop resolved to host '{host}' from Location '{location}'");
            }
            else
            {
                Assert.That(decision.Reason, Is.Not.Null.And.Not.Empty);
            }

            TestContext.Out.WriteLine($"'{location}' -> " + (decision.Allowed ? decision.Target : "refused: " + decision.Reason));
        }

        /// <summary>
        /// A trailing dot is a fully-qualified FQDN spelling of the same host. Either spelling is
        /// acceptable as long as the hop cannot reach a different host through it, so this pins the
        /// invariant instead of a preference.
        /// </summary>
        [TestCase("https://example.com./mod")]
        [TestCase("https://www.example.com./mod")]
        public void ATrailingDotHost_CannotReachAnythingButTheRequestedHost(string location)
        {
            var decision = new GuardedParser().Decide("https://example.com/list", location);

            if (decision.Allowed)
                Assert.That(new Uri(decision.Target!, UriKind.Absolute).Host,
                    Is.EqualTo("example.com").Or.EqualTo("www.example.com").Or.EqualTo("example.com.")
                        .Or.EqualTo("www.example.com."));
            else
                Assert.That(decision.Reason, Is.Not.Null.And.Not.Empty);

            TestContext.Out.WriteLine($"'{location}' -> " + (decision.Allowed ? decision.Target : "refused: " + decision.Reason));
        }

        // ------------------------------------------------- same host, different service ----------------

        /// <summary>
        /// Host equality alone still lets a hop move the request to another listener on the same name
        /// (a local admin service, a dev server, an internal port exposed on a public host). Only the
        /// canonical 80 to 443 upgrade pair is the same service.
        /// </summary>
        [TestCase("https://example.com/list", "https://example.com:8443/mod")]
        [TestCase("https://example.com/list", "http://example.com:8080/mod")]
        [TestCase("https://example.com:8443/a", "https://example.com:9000/b")]
        [TestCase("http://example.com/list", "https://example.com:8443/mod")]
        public void AHopThatMatchesTheHostButChangesThePort_IsRefused(string fromUrl, string location)
            => AssertRefused(fromUrl, location);

        [TestCase("https://example.com/a", "https://example.com/b")]
        [TestCase("http://example.com/a", "https://example.com/b")]
        [TestCase("https://example.com:8443/a", "https://example.com:8443/b")]
        [TestCase("http://example.com:8080/a", "http://example.com:8080/b")]
        [TestCase("https://example.com/a", "https://www.example.com/b")]
        public void TheHopsRealSitesPublishOnTheirOwnHostAndPort_AreFollowed(string fromUrl, string location)
        {
            var decision = new GuardedParser().Decide(fromUrl, location);

            Assert.That(decision.Allowed, Is.True, decision.Reason ?? "<refused without reason>");
            Assert.That(decision.Target, Is.Not.Null.And.Not.Empty);
        }

        // ------------------------------------------------- mirror declarations --------------------------

        /// <summary>
        /// A mirror is usually written as a host, but writing it as that host's URL has to mean the
        /// same mirror. Silently never matching is fail-closed, yet it breaks the source's own CDN hop
        /// and reads as a hijack in the run log, which trains the operator to ignore that message.
        /// </summary>
        [TestCase("cdn.example.net")]
        [TestCase("https://cdn.example.net")]
        [TestCase("https://cdn.example.net/")]
        [TestCase("http://cdn.example.net/path")]
        [TestCase(" https://cdn.example.net/ ")]
        public void AMirrorDeclaredAsAUrl_IsTheSameMirrorAsTheBareHost(string declaredMirror)
        {
            var decision = new GuardedParser(declaredMirror).Decide("https://example.com/list", "https://cdn.example.net/mod");

            Assert.That(decision.Allowed, Is.True,
                $"mirror '{declaredMirror}' did not match its own host: {decision.Reason ?? "<no reason>"}");
        }

    }
}
