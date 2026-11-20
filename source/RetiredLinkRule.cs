using System;
using System.Text.RegularExpressions;

namespace Playnite.VRMods
{
    /// <summary>
    /// A link row this plugin wrote in an earlier version, under one source's display name, which
    /// that source no longer writes — so the games still holding it should lose it.
    ///
    /// The plugin deletes only links it can identify as its own, and it identifies them by
    /// recomputing what it would write today. A row an older version derived from data that no
    /// longer exists fails that test silently and survives for ever — which is the leak this rule
    /// exists to close. A retirement rule is therefore the one place where the plugin states, in
    /// code, that it wrote a row it can no longer re-derive, so it carries its evidence with it:
    ///
    /// - the <see cref="LinkName"/> it wrote the row under, and
    /// - the <see cref="Urls"/> shape it wrote,
    ///
    /// because neither half alone is evidence. A storefront URL is usually a link the user or
    /// Playnite added; a name is only proof of authorship if the row really came from us. Together
    /// they are: nothing else in a library writes a link named after one of our curator lists.
    ///
    /// A rule is deliberately narrow, and each one says why in <see cref="Why"/>, because a rule
    /// here deletes rows in the user's own library and the run log has to explain the removal
    /// without anyone reading this class.
    /// </summary>
    public sealed class RetiredLinkRule
    {
        /// <param name="linkName">the exact link name the retired version wrote.</param>
        /// <param name="urlPattern">anchored pattern for the URL shape it wrote.</param>
        /// <param name="why">the reason the row goes, quoted in the run log.</param>
        public RetiredLinkRule(string linkName, string urlPattern, string why)
        {
            if (string.IsNullOrWhiteSpace(linkName))
                throw new ArgumentException("A retirement rule must name the link it retires.", nameof(linkName));
            if (string.IsNullOrWhiteSpace(urlPattern))
                throw new ArgumentException("A retirement rule must state the URL shape it retires.", nameof(urlPattern));

            LinkName = linkName;
            Urls = new Regex(urlPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            Why = why ?? string.Empty;
        }

        /// <summary>The link name the old version wrote. Matched case-insensitively, because
        /// Playnite shows link names as the user typed them and we never controlled the casing
        /// of what a source's display name looked like in an older release.</summary>
        public string LinkName { get; }

        /// <summary>The URL shape the old version wrote, anchored by its own pattern.</summary>
        public Regex Urls { get; }

        /// <summary>Why the row goes. Appears in the run log beside each removal.</summary>
        public string Why { get; }

        /// <summary>True when this row is one this rule retires.</summary>
        public bool Matches(string? linkName, string? url)
            => !string.IsNullOrEmpty(linkName)
            && !string.IsNullOrEmpty(url)
            && string.Equals(LinkName, linkName, StringComparison.OrdinalIgnoreCase)
            && Urls.IsMatch(url);
    }
}
