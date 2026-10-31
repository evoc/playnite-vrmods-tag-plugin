using System;
using System.Diagnostics;

namespace Playnite.VRMods
{
    /// <summary>
    /// Guard for URLs the plugin takes from scraped source data — both for opening in the
    /// default browser (the settings website links and the review dialog "open mod page" button)
    /// and for storing as a Playnite game <c>Link</c>.
    ///
    /// Only plain http/https URLs pass. Every other scheme (<c>file</c>, <c>ftp</c>,
    /// <c>ms-settings</c>, <c>javascript</c>, <c>data</c>, <c>steam</c>, ...) is rejected, so a URL
    /// derived from scraped source data can never be used to make the shell open local files,
    /// launch protocol handlers, or run script URLs. Links matter as much as opens: Playnite's
    /// Details view opens a stored Link with <c>Process.Start(UseShellExecute = true)</c>
    /// (PlayniteGUI <c>GamesDetailsViewModel.AddLink</c>) with no scheme check of its own, so an
    /// unvalidated Link.Url is a shell command waiting for one click.
    /// </summary>
    internal static class WebUrl
    {
        /// <summary>
        /// Maximum accepted URL length. Scraped hrefs are unbounded; a multi-kilobyte "URL" is not
        /// a link but a payload (it would land in the library database, the settings file, log
        /// lines and dialog columns).
        /// </summary>
        public const int MaxUrlLength = 2048;

        /// <summary>
        /// Returns true when <paramref name="url"/> is an absolute http or https URL that is safe
        /// to hand to the OS shell for opening in the default browser.
        /// </summary>
        /// <param name="url">The candidate URL.</param>
        /// <returns>True only for absolute http/https URLs.</returns>
        public static bool IsSafeWebUrl(string? url)
        {
            return IsSafeLinkUrl(url);
        }

        /// <summary>
        /// Returns true when <paramref name="url"/> may be stored as a game Link and opened.
        /// Requires an absolute http/https URL with a host, no URL credentials
        /// (<c>https://label@host/</c> form is a host-confusion vector), and a bounded length.
        /// </summary>
        /// <param name="url">The candidate URL.</param>
        /// <returns>True only for safe http/https link URLs.</returns>
        /// <summary>
        /// Hands a URL to the OS shell after validating it here, so no call site can open a URL that
        /// the scheme check was supposed to filter. Returns false when the shell refused - no
        /// application is associated with the URL type, the association is broken, or policy forbids
        /// the launch. A refusal is the machine's answer, not a fault in the plugin.
        /// </summary>
        /// <param name="url">The candidate URL.</param>
        /// <returns>True when the shell accepted the URL.</returns>
        public static bool TryOpenInDefaultBrowser(string? url)
        {
            if (!IsSafeLinkUrl(url))
                return false;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex) when (
                ex is System.ComponentModel.Win32Exception
                || ex is InvalidOperationException
                || ex is NotSupportedException
                || ex is System.Security.SecurityException)
            {
                return false;
            }
        }

        public static bool IsSafeLinkUrl(string? url)
        {
            if (url == null || string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength)
                return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;
            // Uri.Scheme is always lower-cased, so literal comparison is exact.
            if (uri.Scheme != "http" && uri.Scheme != "https")
                return false;
            if (string.IsNullOrEmpty(uri.Host))
                return false;
            // No credentials in a source link, ever: "https://pcvr-central.com@evil.tld/"
            // reads like the trusted host but connects to another one.
            if (!string.IsNullOrEmpty(uri.UserInfo))
                return false;
            return true;
        }
    }
}
