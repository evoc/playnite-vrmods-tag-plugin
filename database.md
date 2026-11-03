# Playnite Database Documentation

Playnite stores its library data in **LiteDB** v4.1.4
files (NOT SQLite). Each database collection is stored in
a separate `.db` file within the `playniteDB/` directory.

## File Layout

| File | Collection | Description |
| --- | --- | --- |
| `games.db` | `Game` | All game entries |
| `tags.db` | `Tag` | Tags (including hierarchy) |
| `platforms.db` | `Platform` | Gaming platforms |
| `companies.db` | `Company` | Publishers, developers, etc. |
| `series.db` | `Series` | Game series/franchises |
| `genres.db` | `Genre` | Game genres |
| `features.db` | `GameFeature` | Game features (Online, Co-op, etc.) |
| `ageratings.db` | `AgeRating` | Content rating systems |
| `sources.db` | `GameSource` | Library sources (Steam, Epic, etc.) |
| `completionstatuses.db` | `CompletionStatus` | Play status (Playing, Completed, etc.) |
| `filterpresets.db` | `FilterPreset` | Saved filter configurations |
| `categories.db` | `Category` | Game categories (pinball, etc.) |
| `importexclusions.db` | `ImportExclusionItem` | Imported games to exclude |
| `emulators.db` | — | Empty |
| `scanners.db` | — | Empty |
| `regions.db` | — | Empty |
| `tools.db` | — | Empty |

Each `.db` file contains a single collection named after its entity type
(singular form: `Game`, `Tag`, `Platform`, etc.).

## Accessing from Plugin Code

Playnite exposes all database collections
through the `IPlayniteAPI.Database` property:

```csharp
// Access via Playnite SDK (runtime)
var api = PlayniteApi;

// Games collection (IItemCollection<Game>)
var games = api.Database.Games;

// Tags collection (IItemCollection<Tag>)
var tags = api.Database.Tags;

// Platforms, sources, genres, etc. are also available as typed collections
```

## Game Model

The `Game` entity contains these relevant fields for VR mod tagging:

```csharp
public class Game
{
    public Guid Id { get; set; }                    // Internal Playnite ID
    // Display name (e.g., "Cyberpunk 2077")
    public string Name { get; set; }
    public List<Guid> TagIds { get; set; }          // Assigned tags
    public List<Guid> PlatformIds { get; set; }     // Assigned platforms
    public List<Guid> GenreIds { get; set; }        // Assigned genres
    public List<Guid> SeriesIds { get; set; }       // Series membership
    public List<Guid> DeveloperIds { get; set; }    // Developers
    public List<Guid> PublisherIds { get; set; }    // Publishers
    // External links (Steam, GOG, etc.)
    public List<Link> Links { get; set; }
    // Library source (Steam, Epic, etc.)
    public Guid SourceId { get; set; }
    public Guid CompletionStatusId { get; set; }    // Play completion status
    public long Playtime { get; set; }              // Total playtime in minutes
    public long PlayCount { get; set; }             // Number of play sessions
    public bool IsInstalled { get; set; }           // Whether game is installed
    // Whether game is currently running
    public bool IsRunning { get; set; }
    // Date game was added to library
    public DateTime Added { get; set; }
    public DateTime LastPlayed { get; set; }        // Last play session date
    public string Description { get; set; }         // Game description
    public string ReleaseDate { get; set; }         // Release date string
    public int? CriticScore { get; set; }           // Critic review score
    public int? CommunityScore { get; set; }        // Community score
}
```

## Link Model (External Store IDs)

The `Link` model stores external store URLs used for **Steam App ID matching**:

```csharp
public class Link
{
    // Display name (e.g., "Steam", "Store Page")
    public string Name { get; set; }
    // Full URL (e.g., "https://store.steampowered.com/app/1091500/")
    public string Url { get; set; }
}
```

### Steam App ID Extraction

Steam App IDs are read by two helpers in `VrModTagService` —
`TryGetSteamAppIdFromLink` (`source/VrModTagService.cs:964`) and
`TryGetSteamAppIdFromProviderId` (`:993`) — and the library side
collects them per game in `GetGameSteamAppIds`. What they accept:

- A store link, `TryGetSteamAppIdFromLink`: an absolute http(s)
  URL on a `steampowered.com` host whose **path** has an `app`
  segment followed by digits. The store's own slug, a tracking
  parameter, a fragment, an `agecheck` hop, a missing `store.`,
  a scheme-less link, a trailing slash and leading zeros are
  all noise that gets ignored (`/app/434120/Resident_Evil_4`
  → `434120`). `https://store.steampowered.com/steamvr`,
  `/appreviewzone/12345/`, a search URL whose query contains `app/12345`, a
  `steamcommunity.com/linkfilter/?u=…` wrapper, a `steam://` protocol link
  and a host like `notsteampowered.com` are read as **no App ID** — locked
  by the negative test cases in `tests/Unit/VrModSteamAppIdTests.cs`
- Playnite's `Game.GameId` (`TryGetSteamAppIdFromProviderId`), and
  only for a game whose `PluginId` is the Steam library integration's

Canonical form both directions: digits only, no longer than a real
App ID, leading zeros stripped, `0` rejected (`TryCanonicalizeAppId`,
`VrModTagService.cs:1000`). The per-game list is `ComputeGameSteamAppIds`
(`:1022`): store links first, then the provider id, deduped in first-seen order.
Source-side ids pass the same shape gate (`VrModEntry.NormalizeSteamAppId`);
a value that cannot be an App ID is dropped rather than matched on.

In a typical library a substantial share of games carry no Steam
link at all, and most catalog entries carry no App ID. Silence
on either side is never read as a disagreement — that is what
keeps name matching working for them. The pairing rules themselves
are in [architecture.md](architecture.md#matching-no-substring).

## Tag Model

Tags support hierarchy (parent-child relationships):

```csharp
public class Tag
{
    public Guid Id { get; set; }            // Unique tag ID
    // Display name (e.g., "VRMod Full Motion Controls")
    public string Name { get; set; }
    // Parent tag ID (null for top-level tags)
    public Guid? ParentId { get; set; }
}
```

**Tag creation in plugin:**

```csharp
// Create or get existing tag
var tag = api.Database.Tags.FirstOrDefault(t => t.Name == tagName);
if (tag == null)
    tag = api.Database.Tags.Add(tagName);

// Assign tag to game
if (game.TagIds == null) game.TagIds = new List<Guid>();
if (!game.TagIds.Contains(tag.Id))
    game.TagIds.Add(tag.Id);

// Remove tag from game
game.TagIds?.Remove(tag.Id);
```

## Matching Strategy

### Steam App ID Match (Preferred)

1. Five sources publish a Steam App ID per listing: Flat2VR, PCVR
   Central, RototRobot, Steam Curator (`SteamCuratorParser.cs:233`
   reads a curator recommendation's `AppId`, so a review is
   keyed to a product and not to review prose), and UeVR Profiles
2. The library side collects each game's ids from
   its `Links` (`store.steampowered.com/app/{id}/`)
   and, for a Steam-integrated game, its `GameId`
3. Compare the ids: `SteamIdsMatch` (`source/VrModTagService.cs:1102`)
   pairs a listing with a game; `SteamIdsConflict` (`:1111`)
   refuses a pairing when both sides carry ids and none agree

```csharp
// The candidate walk, in VrModTagService.cs. Four callers run it once
// per listing - their call sites are :1399 FindSuggestedGames, :1645
// RunFullScanAsync, :1886 BuildReviewMatchesFromCache, :4061
// ReconcileSourceLinksForSource - each against the indexes
// BuildGameIndex (:630) froze at the start of this scan, so a decision
// taken in this scan's review dialog lands at the next scan.
internal List<Game> FindGamesByIndex(VrModEntry entry)   // :1170-1272
{
    // 1. (:1180-1189) A mapping somebody already decided
    //    (_decidedListingGames, keyed by every listing key this entry
    //    could have): a confirmed pair survives a rename on the site.
    // 2. (:1191-1201) Every game that claims this entry's Steam App ID,
    //    from the App ID index.
    // 3. (:1203-1222) Every game sharing the entry's exact normalized
    //    name - unless SteamIdsConflict says the two are different
    //    products, in which case the pair is refused and the refusal is
    //    logged.
    // 4. (:1224-1271) No exact name: the best-scoring 3-character-bucket
    //    neighbour (GetMatchScore, VrModNameMatcher.cs:315), then every
    //    game sharing THAT name - same conflict gate.
}
```

### Name Match (Fallback)

When Steam ID matching fails (no Steam link
on game, or no Steam ID on source entry):

1. **Normalize both names** — `StripVrSuffixes` → lowercase → `-`
   and `_` to spaces → `.` removed, in `VrModNameMatcher.Normalize`
   (`source/VrModNameMatcher.cs:145`). The service's
   hot path uses `VrModTagService.NormalizeForMatchFast`
   (`source/VrModTagService.cs:844`), the same rules in one
   allocation-light pass, held equal to the canonical one by the
   differential test `NormalizeForMatchFast_MatchesCanonicalNormalize`
   (`tests/Unit/VrModNameMatcherTests.cs:572`)
2. **Exact match** — the exact normalized-name lookup (`FindGamesByIndex`
   step 3, `VrModTagService.cs:1203-1222`): every game
   sharing the name, unless the App IDs conflict
3. **Levenshtein similarity** — `VrModNameMatcher.ComputeSimilarity`
   (`VrModNameMatcher.cs:184`), returned only at or above
   `FuzzyMatchThreshold` (default 0.8, validated 0.5-1.0)
4. **A plain substring is never a match** on its own — see the Containment
   rule in [architecture.md](architecture.md#matching-no-substring)

Score is not permission: anything that reaches the review
list stays **Pending** until the user decides, and only
an exact normalized-name match may write tags without one.

```csharp
// Name normalization — VrModNameMatcher.cs:161
public static string Normalize(string name)
{
    var normalized = StripVrSuffixes(name);
    return normalized.ToLowerInvariant()
        .Replace("-", " ")
        .Replace("_", " ")
        .Replace(".", "")
        .Trim();
}

// VR suffix stripping — VrModNameMatcher.cs:75
public static string StripVrSuffixes(string name)
{
    var result = name?.Trim() ?? string.Empty;

    // Parenthetical suffixes, longest-first, mutually exclusive.
    var strippedParen = false;
    if (result.EndsWith(" (VR Port)", StringComparison.OrdinalIgnoreCase))
    {
        result = result.Substring(0, result.Length - 10).Trim();
        strippedParen = true;
    }
    else if (!strippedParen &&
             result.EndsWith(" (VR Mod)", StringComparison.OrdinalIgnoreCase))
    {
        result = result.Substring(0, result.Length - 9).Trim();
        strippedParen = true;
    }
    else if (!strippedParen &&
             result.EndsWith(" (VR)", StringComparison.OrdinalIgnoreCase))
    {
        result = result.Substring(0, result.Length - 5).Trim();
        strippedParen = true;
    }
    else if (!strippedParen &&
             result.EndsWith(" (UEVR)", StringComparison.OrdinalIgnoreCase))
    {
        result = result.Substring(0, result.Length - 7).Trim();
        strippedParen = true;
    }

    // "Game Name VR Mod by Author" (RototRobot naming) → "Game Name"
    var vrModByIdx = result.LastIndexOf(
        " VR Mod by", StringComparison.OrdinalIgnoreCase);
    if (vrModByIdx >= 0)
    {
        result = result.Substring(0, vrModByIdx).Trim();
    }
    else
    {
        var byIdx = result.LastIndexOf(
            " by ", StringComparison.OrdinalIgnoreCase);
        if (byIdx < 0 &&
            result.EndsWith(" by", StringComparison.OrdinalIgnoreCase))
            byIdx = result.Length - 3;
        if (byIdx > 0)
            result = result.Substring(0, byIdx).Trim();
    }

    if (result.EndsWith(" VR", StringComparison.OrdinalIgnoreCase))
        result = result.Substring(0, result.Length - 3).Trim();

    if (result.EndsWith(" Flat2VR", StringComparison.OrdinalIgnoreCase))
        result = result.Substring(0, result.Length - 8).Trim();

    return result;
}
```

## Complete Matching Pipeline

```text
For each game-to-source mapping, ScanGameCore asks FindGamesByIndex (see
architecture.md#matching-algorithm) and then gates what may happen:

1. Candidate selection: exact normalized name, or a shared Steam App ID, or a Levenshtein score
   at/above FuzzyMatchThreshold. A candidate whose App IDs conflict with the entry's is dropped
   at this stage, never scored.
2. A saved decision on that entry+game key is honoured with no review row: Accepted re-applies its
   tags, Rejected skips the game.
3. Score >= threshold with NO saved decision -> Pending review row. Tags are untouched until the
   user decides.
4. Tags are written automatically only on the Saved branch, and only when the name score is 1.00
   (exact normalized names). An App-ID-only pairing scores 1.00 for worth and still waits Pending.
5. Exact normalized names with no App ID on either side are treated as Steam's own naming for a
   Mod, and link to the Mod's own page instead of a guessed App ID.

Which games matched is still tracked for stale-tag detection (`RemoveStaleTags`).
```

## What This Plugin Writes to the Database

The plugin's only database writes are **tags** and **tag assignments**:

1. **`Tag` collection** — creates the `VRMod*` tag hierarchy if it
   does not exist yet (`VRMod` umbrella + framework categories +
   `6DOF` / quality facets; names come from `VrModCategories`).
   Existing tags with the same names are reused, never duplicated.
2. **`Game.TagIds`** — adds the tag IDs to matched games and removes
   them from games that are no longer listed by any enabled source
   (stale-tag sweep, skipped entirely while any source failed the scan).

Everything else the plugin keeps is **not** in the Playnite database:

- **Source data cache** — purely in memory. Per-source fetched
  entries live in a process-local dictionary with a fetch timestamp
  (`CacheDurationMinutes` setting); nothing is written to disk. A Playnite
  restart re-fetches (or reuses nothing — the cache is empty at startup).
- **Match decisions & settings** — stored via Playnite's
  plugin settings mechanism (`config.json` inside the
  plugin's own folder), not the game database. These include
  accepted/rejected/pending decisions and their first-seen timestamps.

In short: the DB diff caused by this plugin is limited
to `VRMod*` tags and the games they are assigned to — the
same surface the stale-tag sweep can safely clean up.

## Inspecting the Database Files

This plugin does not ship a database tool (nothing under `tools/`
is part of the plugin). To inspect Playnite's LiteDB files outside
of Playnite, install an open-source LiteDB tool from the internet.
Playnite must be **closed** first — LiteDB locks the files exclusively.

Recommended (all open source, installed from GitHub):

- **LiteDB Studio** (GUI, official) —
  <https://github.com/litedb-org/LiteDB.Studio>. Grab a
  release binary and open the `playniteDB/*.db` files.
- **litecli** (command line) — <https://github.com/prahladyeri/litedbcli>.
  Build/run from source, then open a file and run LiteDB
  query-language statements (e.g. `list`, `find game 'TagIds != []'`).

> **Version caveat:** Playnite is pinned to **LiteDB v4.1.4**, while the current
> `litedb-org/LiteDB` (and many off-the-shelf tools) are **v5**. Opening a v4 data
> file with a v5 client wants `Upgrade=true` in the connection string, which can
> rewrite the file — and Playnite expects the v4 layout. So open Playnite's DB
> **read-only** where possible, prefer a v4-based reader, and never let a tool
> upgrade/migrate the file. Useful for verifying game names, Steam App IDs,
> and tag
> structures without running Playnite.

## Database Files Location

- **Portable mode:** `{PlayniteInstallDir}\library\`
- **Install mode:** `%AppData%\Playnite\library\`

The `database.json` file in the database
directory contains only the schema version:

```json
{"Version":4}
```

## Technical Notes

- **LiteDB version:** 4.1.4 (not 5.x — Playnite
  explicitly uses v4 for cache control and performance)
- **File format:** Each collection stored in a separate
  `{collection}.db` file under the library directory
- **Connection mode:** `Mode=Exclusive` (Playnite SDK uses exclusive locking)
- **BSON serialization:** Playnite uses a custom
  `BsonMapper` to map C# models to BSON documents
- **File signature:** First bytes are
  `00 00 00 00 01 FF FF FF FF FF FF FF FF 01 00 00` — NOT `SQLite format 3\000`
- **Cannot query with sqlite3** — must use LiteDB library or Playnite's SDK API
