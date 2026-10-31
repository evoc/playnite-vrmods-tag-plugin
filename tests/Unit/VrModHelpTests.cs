using System;
using System.IO;
using NUnit.Framework;
using Playnite.VRMods;

namespace PlayniteVRMods.Tests.Unit
{
    /// <summary>
    /// Tests for VrModHelp — the runtime resolver that opens the user manual shipped
    /// inside the installed extension package (docs/README.md next to the loaded
    /// assembly, so it always matches the installed version).
    /// </summary>
    [TestFixture]
    public class VrModHelpTests
    {
        [Test]
        public void ManualRelativePath_IsStable()
        {
            Assert.That(VrModHelp.ManualRelativePath, Is.EqualTo("docs/README.md"));
        }

        [Test]
        public void HostedManualUrl_IsNullUntilRepoGoesPublic()
        {
            // The Help button opens the local manual until the flip point (repository
            // public on GitHub) sets HostedManualUrl to the hosted landing page.
            Assert.That(VrModHelp.HostedManualUrl, Is.Null);
        }

        [Test]
        public void GetManualPath_IsInTheDocsFolderNextToTheLoadedAssembly()
        {
            var path = VrModHelp.GetManualPath();
            Assert.That(Path.GetFileName(path), Is.EqualTo("README.md"));
            // The manual must live in docs/ next to the LOADED assembly (the extension
            // install folder inside Playnite, the test bin folder under the test host)
            // — NOT in the app domain's base directory: when Playnite hosts the plugin
            // that is Playnite's install directory, where no docs/ exists.
            var assemblyDir = Path.GetDirectoryName(typeof(VrModHelp).Assembly.Location);
            Assert.That(Path.GetDirectoryName(path), Is.EqualTo(Path.Combine(assemblyDir, "docs")));
        }

        [Test]
        public void OpenManual_WithNoManualNextToAssembly_ReturnsFalse()
        {
            // Make sure no docs/README.md is present next to the assembly, then OpenManual
            // must report "not found" (and not throw, and not open anything).
            var manual = Path.Combine(
                Path.GetDirectoryName(typeof(VrModHelp).Assembly.Location),
                VrModHelp.ManualRelativePath);
            if (File.Exists(manual))
            {
                File.Delete(manual);
            }

            Assert.That(VrModHelp.OpenManual(), Is.False);
        }
    }
}
