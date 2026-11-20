using System;
using System.Collections.Generic;
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
        /// Filters and case-insensitively de-duplicates a batch of scraped URLs, keeping
        /// first-seen order. Equivalent to appending them to an empty list with
        /// <see cref="AppendSafeLinkUrls"/>.
        /// </summary>
        /// <param name="urls">The scraped URLs, in source order. Null means none.</param>
        /// <returns>A new list of the safe, distinct URLs.</returns>
        public static List<string> SafeLinkUrls(IEnumerable<string>? urls)
        {
            var safe = new List<string>();
            AppendSafeLinkUrls(safe, urls);
            return safe;
        }

        /// <summary>
        /// Appends every URL from <paramref name="incoming"/> that passes
        /// <see cref="IsSafeLinkUrl"/> and that <paramref name="target"/> does not already hold
        /// (case-insensitive), in arrival order. The target is read into a set once, so merging
        /// N incoming URLs into a target already holding M costs O(N + M) rather than the O(N x M)
        /// of rescanning the target for every candidate - which a source that publishes many
        /// pages per game pays on every duplicate-game merge.
        /// </summary>
        /// <param name="target">The list to append to; kept in order and free of duplicates.</param>
        /// <param name="incoming">The candidate URLs. Null means none.</param>
        public static void AppendSafeLinkUrls(List<string> target, IEnumerable<string>? incoming)
        {
            if (incoming == null)
                return;
            var seen = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
            foreach (var url in incoming)
            {
                if (!IsSafeLinkUrl(url))
                    continue;
                if (!seen.Add(url!))
                    continue;
                target.Add(url!);
            }
        }

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

        /// <summary>
        /// Whether <paramref name="url"/> lives on the same site as <paramref name="siteUrl"/>.
        /// Both must pass <see cref="IsSafeLinkUrl"/>, and the hosts must match ignoring case and
        /// a leading "www." (a source publishes the same site under both spellings). A port counts
        /// only when the URL states one: two URLs that leave the port out are one site even across
        /// http and https, while an explicit port never matches a missing one — another port is
        /// another machine as far as a link target goes.
        /// False when either URL is missing or unparseable — when a link's home cannot be
        /// established, it cannot be the source's own page.
        /// </summary>
        /// <param name="url">The scraped deep link candidate.</param>
        /// <param name="siteUrl">The source's registered website.</param>
        /// <returns>True only when both URLs are safe and their hosts are one site.</returns>
        public static bool IsSameSiteHost(string? url, string? siteUrl)
        {
            if (!IsSafeLinkUrl(url) || !IsSafeLinkUrl(siteUrl))
                return false;
            var left = new Uri(url!);
            var right = new Uri(siteUrl!);
            // -1 for "no port stated", so http and https of one host compare equal while an
            // explicit port still has to be met.
            var leftPort = left.IsDefaultPort ? -1 : left.Port;
            var rightPort = right.IsDefaultPort ? -1 : right.Port;
            if (leftPort != rightPort)
                return false;
            var leftHost = WithoutWww(left.IdnHost);
            var rightHost = WithoutWww(right.IdnHost);
            return string.Equals(leftHost, rightHost, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A host with a leading "www." removed, so "www.vorpx.com" and "vorpx.com" compare equal.
        /// Only that literal prefix is stripped: "www2.example.com" keeps its host intact.
        /// </summary>
        private static string WithoutWww(string host)
            => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host.Substring(4) : host;
    }
}
