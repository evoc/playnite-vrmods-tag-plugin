using System;
using System.Diagnostics;
using System.IO;

namespace Playnite.VRMods
{
    /// <summary>
    /// Opens the version-matched user manual that ships inside the extension package
    /// (<c>docs/README.md</c> — the landing page; the manual's sub-pages and figures in
    /// <c>docs/img/</c> sit next to it). Playnite extracts every file in the .pext into
    /// the install directory (<c>%LOCALAPPDATA%\Playnite\Extensions\<Id>\</c>), so the
    /// manual sits next to this assembly and always matches the installed version. The
    /// manifest <c>Links</c> field cannot be used for this: its URLs are static strings
    /// and the install directory contains the Windows username, so the path must be
    /// resolved at runtime instead.
    /// </summary>
    public static class VrModHelp
    {
        /// <summary>
        /// The manual's landing page, relative to the assembly location.
        /// </summary>
        public const string ManualRelativePath = "docs/README.md";

        // FLIP POINT (when the repository becomes public on GitHub): once the manual is
        // browsable at a stable URL (docs/README.md + docs/img/ on GitHub), set this
        // constant to the hosted landing page — OpenManual() then opens the hosted page
        // instead of the local copy shipped in the package. While it is null, the local
        // manual is opened.
        public const string HostedManualUrl = null;

        /// <summary>
        /// Absolute path of the manual that ships with this assembly
        /// (the copy next to the loaded DLL, not the repository root).
        ///
        /// NOTE: the directory is taken from the assembly's own location, NOT from
        /// <c>AppDomain.CurrentDomain.BaseDirectory</c> — when Playnite loads the plugin,
        /// the app domain's base directory is Playnite's install directory (not the
        /// extension folder), so the manual would be looked for in the wrong place.
        /// </summary>
        public static string GetManualPath()
        {
            return Path.Combine(GetAssemblyDirectory(), ManualRelativePath);
        }

        /// <summary>
        /// The directory the executing assembly was loaded from — the extension install
        /// directory in Playnite, or the test/build output directory under the unit tests.
        /// Falls back to the app domain's base directory only when the assembly has no
        /// file location (e.g. it was loaded from a memory stream).
        /// </summary>
        private static string GetAssemblyDirectory()
        {
            var location = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(location))
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }

            return Path.GetDirectoryName(location) ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>
        /// Opens the manual in the default viewer. While <see cref="HostedManualUrl"/>
        /// is null, the local <c>docs/README.md</c> next to the assembly is opened;
        /// once it is set (see the flip-point comment), the hosted page is opened
        /// instead.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if the open attempt started without throwing;
        /// <see langword="false"/> if the local manual is not present next to the
        /// assembly (nothing is opened, nothing is thrown).
        /// </returns>
        public static bool OpenManual()
        {
            if (!string.IsNullOrEmpty(HostedManualUrl))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = HostedManualUrl,
                    UseShellExecute = true,
                });
                return true;
            }

            var path = GetManualPath();
            if (!File.Exists(path))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            return true;
        }
    }
}
