using System;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.VRMods
{
    /// <summary>
    /// Provides VR mod tags and source links for Playnite's "Download Metadata" UI.
    ///
    /// This class is instantiated by VrModTagPlugin.GetMetadataProvider() when the user
    /// requests metadata for a game. It implements two metadata fields:
    /// - Tags: VRMod category tags (e.g., "VRMod UEVR Profiles", "VRMod Flat2VR Port")
    /// - Links: one link per matched source (the source's per-game page, or its own
    ///   website) for saved accepted matches. This plugin creates no Steam links.
    ///
    /// The provider uses two strategies to gather data:
    /// 1. Check if the tag service already has cached data (from a recent scan)
    /// 2. If no cache, fetch from sources and match on-the-fly
    ///
    /// This dual approach ensures metadata is available even if no scan has run yet,
    /// while avoiding redundant network requests when cache is fresh.
    ///
    /// Tags and links are computed per-game. The provider does NOT modify the database —
    /// it only returns suggested values for the user to review and accept.
    /// </summary>
    public class VrModMetadataProvider : OnDemandMetadataProvider
    {
        private readonly VrModTagService _tagService;
        private readonly Game _game;

        /// <summary>
        /// Lists the metadata fields this provider can supply.
        /// Tags: VRMod category tags
        /// Links: per-source links for saved accepted matches (no Steam links)
        /// </summary>
        public override List<MetadataField> AvailableFields => new() { MetadataField.Tags, MetadataField.Links };

        /// <summary>
        /// Constructs the metadata provider for a specific game.
        /// </summary>
        /// <param name="tagService">The shared tag service for computing tags and links.</param>
        /// <param name="game">The game being queried for metadata.</param>
        public VrModMetadataProvider(VrModTagService tagService, Game game)
        {
            _tagService = tagService;
            _game = game;
        }

        /// <summary>
        /// Returns VRMod tags for the game.
        ///
        /// Strategy:
        /// 1. First, try to compute tags from the cached game index (fast, no network)
        /// 2. If cache is empty, fetch from sources and match on-the-fly
        ///
        /// The returned tags are suggestions only — Playnite's "Download Metadata"
        /// workflow shows them to the user for review before applying.
        ///
        /// Tag format: "VRMod {Category}" (e.g., "VRMod UEVR Profiles")
        /// </summary>
        /// <param name="args">Metadata field arguments (not used, provided by Playnite).</param>
        /// <returns>List of tag names to apply to the game.</returns>
        public override IEnumerable<MetadataProperty> GetTags(GetMetadataFieldArgs args)
        {
            // Try to get tags from cached index (fast)
            var vrTags = _tagService.ComputeTagsForGame(_game);
            if (vrTags.Count == 0)
            {
                // Cache is empty — fetch and match on-the-fly. Build the same deduped
                // tag set ComputeTagsForGame produces (category + 6DOF facet +
                // optional quality / motion / mod-status facets), so both paths can't diverge.
                _tagService.FetchAndMatchEntries(_game, out _, out var matches);
                vrTags = new List<MetadataNameProperty>();
                var tagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in matches)
                {
                    // One category tag per (non-catalog) category — a mod may have several.
                    foreach (var category in entry.Categories)
                    {
                        var tagName = VrModCategories.TagNameFor(category);
                        if (VrModCategories.IsKnownTagValue(category) && !VrModCategories.IsCatalogCategory(category) && tagNames.Add(tagName))
                            vrTags.Add(new MetadataNameProperty(tagName));
                    }

                    // Capability facet (shared name: VrModCategories.Tag6DOF).
                    if (entry.Is6DOF == true && tagNames.Add(VrModCategories.Tag6DOF))
                        vrTags.Add(new MetadataNameProperty(VrModCategories.Tag6DOF));

                    // Optional quality/grade facet tags (orthogonal to category + 6DOF) - an entry may have several.
                    foreach (var q in entry.Qualities)
                    {
                        if (VrModCategories.IsKnownTagValue(q) && tagNames.Add(VrModCategories.TagNameForQuality(q)))
                            vrTags.Add(new MetadataNameProperty(VrModCategories.TagNameForQuality(q)));
                    }

                    // Optional motion-level facet tags (orthogonal) - an entry may have several.
                    foreach (var m in entry.Motions)
                    {
                        if (VrModCategories.IsKnownTagValue(m) && tagNames.Add(VrModCategories.TagNameForMotion(m)))
                            vrTags.Add(new MetadataNameProperty(VrModCategories.TagNameForMotion(m)));
                    }

                    // Optional mod-status facet tags (orthogonal) - an entry may have several, because
                    // a game's mods can be in different states at the same time.
                    foreach (var s in entry.Statuses)
                    {
                        if (VrModCategories.IsKnownTagValue(s) && tagNames.Add(VrModCategories.TagNameForStatus(s)))
                            vrTags.Add(new MetadataNameProperty(VrModCategories.TagNameForStatus(s)));
                    }
                }
            }
            return vrTags;
        }

        /// <summary>
        /// Returns per-source links for VR mod sources matched to the game.
        /// This plugin creates no Steam links.
        ///
        /// Strategy:
        /// 1. Get all cached VR mod entries from all sources
        /// 2. Filter to entries matching the current game
        /// 3. For each matching entry the user has SAVED as accepted (and whose source has
        ///    links enabled in the settings), create a source link: the per-game deep
        ///    link when the parser provides one, otherwise the source's own website
        ///
        /// Duplicate URLs are deduplicated using a HashSet.
        /// Existing links (already in the game's Links collection) are skipped.
        /// Non-Steam games (Links is null) are handled gracefully.
        ///
        /// This allows users to navigate directly to the VR mod source page from
        /// Playnite's metadata UI.
        ///
        /// Link format: "{Source display name}" → per-game deep link, or the source's
        /// website
        /// </summary>
        /// <param name="args">Metadata field arguments (not used, provided by Playnite).</param>
        /// <returns>List of source links to add to the game.</returns>
        public override IEnumerable<Link> GetLinks(GetMetadataFieldArgs args)
        {
            // Get all cached entries from all sources
            var entries = _tagService.AllCachedEntries.ToList();
            IEnumerable<VrModEntry> vrEntries;

            if (entries.Count != 0)
            {
                // Use cached entries to find matches for this game
                vrEntries = _tagService.GetVrModEntriesForGame(_game, entries).ToList();
            }
            else
            {
                // Cache is empty — fetch and match on-the-fly
                _tagService.FetchAndMatchEntries(_game, out _, out vrEntries);
            }

            // Source links only (this plugin creates no Steam links). Deduplicate URLs
            // and skip existing links.
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var links = new List<Link>();
            foreach (var entry in vrEntries)
            {
                // Source link: ONLY for a match the user has SAVED as accepted, from an
                // enabled source, and only when links are enabled for this source in the
                // settings. A pending or rejected match is not confirmed to be this game,
                // so no source link is offered. The per-game deep link (entry.SourceUrl)
                // is used when the parser provides one; otherwise the source's own website
                // is the fallback, so every source can offer at least a link to itself.
                if (IsSavedAccepted(entry) &&
                    IsSourceEnabled(entry) &&
                    _tagService.Settings.IsSourceLinksEnabled(entry.SourceId ?? string.Empty))
                {
                    // Every distinct deep link the entry owns becomes a Playnite link
                    // (the duplicate-listing merge can leave several); with no deep link
                    // the source's website is the fallback, so every source can offer at
                    // least a link to itself.
                    var deepLinks = new List<string>();
                    foreach (var deepLink in entry.SourceUrls)
                        // Belt and braces with VrModEntry's own filtering: a Link is opened by
                        // Playnite through the OS shell, so only a safe http(s) URL may be offered.
                        if (WebUrl.IsSafeLinkUrl(deepLink))
                            deepLinks.Add(deepLink);
                    if (deepLinks.Count == 0)
                    {
                        var fallback = GetSourceLinkUrl(entry);
                        if (fallback.Length > 0)
                            deepLinks.Add(fallback);
                    }
                    foreach (var sourceUrl in deepLinks)
                    {
                        if (sourceUrl.Length > 0 &&
                            seenUrls.Add(sourceUrl) &&
                            !(_game.Links?.Any(l => l.Url == sourceUrl) ?? false))
                        {
                            links.Add(new Link(GetSourceDisplayName(entry), sourceUrl));
                        }
                    }
                }
            }

            return links;
        }

        /// <summary>
        /// True when the user has SAVED an accepted decision for this match — i.e. the match is
        /// confirmed to be this game. Pending (unconfirmed) and rejected matches return false, so
        /// they get no source link. This is the "only saved matches, not pending" gate for the
        /// source links. Delegates to <see cref="VrModTagService.IsSavedAccepted"/> so the
        /// gate is identical in the metadata provider and the link reconciliation.
        /// </summary>
        private bool IsSavedAccepted(VrModEntry entry)
        {
            return _tagService.IsSavedAccepted(_game, entry);
        }

        /// <summary>
        /// True when the entry's source is registered and enabled. Delegates to
        /// <see cref="VrModTagService.IsSourceEnabled"/> — a disabled source offers no links.
        /// </summary>
        private bool IsSourceEnabled(VrModEntry entry)
        {
            return _tagService.IsSourceEnabled(entry.SourceId);
        }

        /// <summary>
        /// The source display name used as the source link's text (falls back to the source id
        /// when the source is not registered). Delegates to
        /// <see cref="VrModTagService.GetSourceDisplayName"/>.
        /// </summary>
        private string GetSourceDisplayName(VrModEntry entry)
        {
            return _tagService.GetSourceDisplayName(entry);
        }

        /// <summary>
        /// The link target for an entry's source link: the per-game deep link when the parser
        /// provides one, otherwise the source's own website. Delegates to
        /// <see cref="VrModTagService.GetSourceLinkUrl"/>.
        /// </summary>
        private string GetSourceLinkUrl(VrModEntry entry)
        {
            return _tagService.GetSourceLinkUrl(entry);
        }
    }
}
