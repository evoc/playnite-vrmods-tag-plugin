using NUnit.Framework;
using Playnite.VRMods;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteVRMods.Tests.Unit
{
    [TestFixture]
    public class VrModSourceRegistryTests
    {
        [Test]
        public void Register_AddsSourceToList()
        {
            var registry = new VrModSourceRegistry();
            var source = new TestSource("test", "Test Source");

            registry.Register(source);

            Assert.That(registry.Sources, Has.Count.EqualTo(1));
            Assert.That(registry.Sources[0], Is.SameAs(source));
        }

        [Test]
        public void GetEnabledSources_ReturnsOnlyEnabled()
        {
            var registry = new VrModSourceRegistry();
            var enabled = new TestSource("a", "A");
            var disabled = new TestSource("b", "B") { IsEnabled = false };
            var enabled2 = new TestSource("c", "C");

            registry.Register(enabled);
            registry.Register(disabled);
            registry.Register(enabled2);

            var result = registry.GetEnabledSources().ToList();

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result, Does.Contain(enabled));
            Assert.That(result, Does.Contain(enabled2));
            Assert.That(result, Does.Not.Contain(disabled));
        }

        [Test]
        public void GetEnabledSources_EmptyRegistry_ReturnsEmpty()
        {
            var registry = new VrModSourceRegistry();
            var result = registry.GetEnabledSources().ToList();
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void GetEnabledSources_AllDisabled_ReturnsEmpty()
        {
            var registry = new VrModSourceRegistry();
            registry.Register(new TestSource("a", "A") { IsEnabled = false });
            registry.Register(new TestSource("b", "B") { IsEnabled = false });

            var result = registry.GetEnabledSources().ToList();
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void GetEnabledSources_AllEnabled_ReturnsAll()
        {
            var registry = new VrModSourceRegistry();
            var s1 = new TestSource("a", "A");
            var s2 = new TestSource("b", "B");
            var s3 = new TestSource("c", "C");

            registry.Register(s1);
            registry.Register(s2);
            registry.Register(s3);

            var result = registry.GetEnabledSources().ToList();
            Assert.That(result, Has.Count.EqualTo(3));
        }

        [Test]
        public void Register_ToggleEnableDisable_AffectsGetEnabledSources()
        {
            var registry = new VrModSourceRegistry();
            var source = new TestSource("a", "A");
            registry.Register(source);

            Assert.That(registry.GetEnabledSources(), Does.Contain(source));

            source.IsEnabled = false;
            Assert.That(registry.GetEnabledSources(), Does.Not.Contain(source));

            source.IsEnabled = true;
            Assert.That(registry.GetEnabledSources(), Does.Contain(source));
        }

        private class TestSource : IVrModSource
        {
            public TestSource(string id, string displayName)
            {
                SourceId = id;
                DisplayName = displayName;
                IsEnabled = true;
            }
            public string SourceId { get; }
            public string DisplayName { get; }
            public string WebsiteUrl => "https://example.com/";
            public bool IsEnabled { get; set; }
            public string? ChangeIdentifier => null;
            public List<string>? RunLog { get; set; }
            public System.Threading.CancellationToken ScanToken { get; set; }
            public Task<IEnumerable<VrModEntry>> FetchEntriesAsync() => Task.FromResult<IEnumerable<VrModEntry>>(Array.Empty<VrModEntry>());
            public string? GetCanonicalName(VrModEntry entry) => null;
        }
    }
}
