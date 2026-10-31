using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Playnite.VRMods
{
    /// <summary>
    /// Parser for the "Luke Ross" (R.E.A.L. VR) per-game VR mods.
    ///
    /// R.E.A.L. VR (Real VR) is a per-game VR framework by Luke Ross that adds VR rendering,
    /// head tracking, and OpenXR-oriented support to major flatscreen AAA titles (Elden Ring,
    /// Horizon, Hogwarts Legacy, DOOM, Far Cry, Final Fantasy VII, Ghost of Tsushima, ...). It
    /// is one of the main ways to play high-profile NON-Unreal games in VR (titles UEVR cannot
    /// handle). This source is CATALOG-LEVEL coverage: one entry per supported game, each
    /// carrying the single category "LukeRoss" (the "VRMod LukeRoss" tag).
    ///
    /// FREE + PUBLIC as of March 2026: after a DMCA dispute Luke Ross made the framework free
    /// (a public release) and it now publishes a full "Supported Games" list on the Patreon
    /// public post (patreon.com/realvr). So the list is NO LONGER Patreon-gated (an earlier
    /// version of this doc said so — that rationale is stale).
    ///
    /// WHY A STATIC LIST (not a live parser): the free list lives ONLY in that Cloudflare-walled
    /// Patreon post (prose, no JSON/RSS/API). The plugin's own fetch (BaseVrModParser.
    /// FetchStringAsync, a shared HttpClient GET on the user's machine, no browser, no cookies,
    /// no JS) would be treated as a bot and get a 403 / Cloudflare CAPTCHA; a browser User-Agent
    /// wouldn't help, because Cloudflare's "Just a moment..." is a JS challenge a bare HttpClient
    /// cannot run. The only machine-readable public copy is a third-party fan mirror (vrmoqu.com)
    /// that is incomplete/stale. Every other source in this plugin scrapes a public, non-walled
    /// endpoint; Patreon is the one that is bot-walled. So this is a STATIC SNAPSHOT (below) that
    /// a maintainer re-syncs in a real browser; see the MAINTAIN note at the bottom of
    /// <c>KnownGames</c>. A live parse FROM THE PLUGIN is not feasible (Cloudflare + no browser in
    /// the Playnite process).
    ///
    /// Still active: R.E.A.L. VR is updated regularly (latest v27.0.0; the supported list grows
    /// each release, 23 → … → 40+). So this snapshot WILL go stale — re-sync on a R.E.A.L. VR
    /// release (check the "as of" date below).
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  WHAT THIS PARSER EXPECTS TO PARSE (so a site/layout change is easy to fix)
    /// ────────────────────────────────────────────────────────────────────────────────────────
    ///  NONE — this is a STATIC SNAPSHOT, not a live parse. It returns the hardcoded
    ///  <c>KnownGames</c> array below with NO network access. The real "Supported Games"
    ///  list lives in a Cloudflare-walled Patreon free-release post (patreon.com/realvr)
    ///  that the plugin's bare HttpClient cannot fetch, so there is no live markup to
    ///  target. <c>KnownGames</c> is the source of truth; a maintainer re-syncs it (and the
    ///  "as of" date) from that post in a real browser on a R.E.A.L. VR release. If it is
    ///  ever converted to a live parse (e.g. an interactive WebView that passes the
    ///  Cloudflare challenge), the target is the "Supported Games" list in that post.
    /// </summary>
    public class LukeRossParser : BaseVrModParser
    {
        /// <summary>Category applied to every entry (drives the "VRMod LukeRoss" tag).</summary>
        private const string Category = VrModCategories.LukeRoss;

        /// <summary>
        /// R.E.A.L. VR (Luke Ross) supported games — a STATIC SNAPSHOT (see the class doc for why
        /// it is static rather than live). Free public release, "as of 15th March 2026" plus
        /// follow-on releases through v27.0.0 (still active — re-sync on a R.E.A.L. VR release).
        /// </summary>
        /// <remarks>
        /// Deliberately OMITTED (documented so a maintainer does not "restore" them by accident):
        /// the titles Luke Ross has PULLED — Take-Two DMCA (Grand Theft Auto V, Red Dead
        /// Redemption 2, Mafia: Definitive Edition) and Cyberpunk 2077 (dropped from the current
        /// authorized suite after its Jan 2026 DMCA). Tagging those would claim a mod Luke Ross
        /// no longer ships. Early-access (paid) only titles (Baldur's Gate 3, Days Gone) are
        /// also omitted because they are not in the free public release.
        ///
        /// MAINTAIN: when R.E.A.L. VR ships a release that adds/removes games, re-sync this array
        /// from the Patreon free-release post (patreon.com/realvr → "Supported Games") and bump
        /// the "as of" date above. It is a snapshot on purpose; do not treat it as live.
        /// </remarks>
        private static readonly string[] KnownGames =
        {
            "Atomic Heart",
            "Avatar: Frontiers of Pandora",
            "Dark Souls: Remastered",
            "Dark Souls II: Scholar of the First Sin",
            "Dark Souls III",
            "Death Stranding Director's Cut",
            "DOOM: Eternal",
            "DOOM: The Dark Ages",
            "Elden Ring",
            "Far Cry 4",
            "Far Cry 5",
            "Far Cry 6",
            "Far Cry New Dawn",
            "Far Cry Primal",
            "Final Fantasy VII Remake Intergrade",
            "Final Fantasy VII Rebirth",
            "Ghost of Tsushima",
            "Ghostrunner",
            "Ghostwire: Tokyo",
            "Grounded",
            "High On Life",
            "Hogwarts Legacy",
            "Horizon Zero Dawn",
            "Horizon Forbidden West",
            "Indiana Jones and the Great Circle",
            "Kingdom Come: Deliverance II",
            "Marvel's Spider-Man Remastered",
            "Marvel's Spider-Man: Miles Morales",
            "Marvel's Spider-Man 2",
            "Star Wars Outlaws",
            "Stray",
            "Uncharted 4: A Thief's End",
            "Watch Dogs",
            "Watch Dogs 2",
            "Watch Dogs: Legion",
            "Crimson Desert",
        };

        private readonly string _staticHash;

        /// <summary>Creates a new LukeRossParser instance.</summary>
        public LukeRossParser() : base("LukeRoss", "Luke Ross (R.E.A.L. VR)", "https://patreon.com/realvr")
        {
            // The list never changes at runtime, so the change identifier is a constant hash of it.
            _staticHash = ComputeHash(string.Join("\n", KnownGames));
        }

        /// <inheritdoc />
        public override string? ChangeIdentifier => _staticHash;

        /// <inheritdoc />
        public override string? GetCanonicalName(VrModEntry entry)
        {
            return entry.GameName;
        }

        /// <inheritdoc />
        /// <remarks>
        /// No network access: the list is a static snapshot (see class doc), so this returns one
        /// entry per <c>KnownGames</c> game.
        /// </remarks>
        public override Task<IEnumerable<VrModEntry>> FetchEntriesAsync()
        {
            var entries = new List<VrModEntry>();
            foreach (string game in KnownGames)
            {
                entries.Add(new VrModEntry
                {
                    GameName = game,
                    Category = Category
                });
            }

            return Task.FromResult<IEnumerable<VrModEntry>>(entries);
        }
    }
}
