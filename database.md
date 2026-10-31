# Playnite Database Documentation

Playnite stores its library data in **LiteDB** v4.1.4 files (NOT SQLite). Each database collection is stored in a separate `.db` file within the `playniteDB/` directory.

## File Layout

| File | Collection | Description |
|------|-----------|-------------|
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

Each `.db` file contains a single collection named after its entity type (singular form: `Game`, `Tag`, `Platform`, etc.).

## Accessing from Plugin Code

Playnite exposes all database collections through the `IPlayniteAPI.Database` property:

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
    public string Name { get; set; }                // Display name (e.g., "Cyberpunk 2077")
    public List<Guid> TagIds { get; set; }          // Assigned tags
    public List<Guid> PlatformIds { get; set; }     // Assigned platforms
    public List<Guid> GenreIds { get; set; }        // Assigned genres
    public List<Guid> SeriesIds { get; set; }       // Series membership
    public List<Guid> DeveloperIds { get; set; }    // Developers
    public List<Guid> PublisherIds { get; set; }    // Publishers
    public List<Link> Links { get; set; }           // External links (Steam, GOG, etc.)
    public Guid SourceId { get; set; }              // Library source (Steam, Epic, etc.)
    public Guid CompletionStatusId { get; set; }    // Play completion status
    public long Playtime { get; set; }              // Total playtime in minutes
    public long PlayCount { get; set; }             // Number of play sessions
    public bool IsInstalled { get; set; }           // Whether game is installed
    public bool IsRunning { get; set; }             // Whether game is currently running
    public DateTime Added { get; set; }             // Date game was added to library
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
    public string Name { get; set; }    // Display name (e.g., "Steam", "Store Page")
    public string Url { get; set; }     // Full URL (e.g., "https://store.steampowered.com/app/1091500/")
}
```

### Steam App ID Extraction

Steam App IDs are embedded in `Link` URLs:

```csharp
// URL format: https://store.steampowered.com/app/{appid}/
// Example: https://store.steampowered.com/app/1091500/ → AppID = 1091500

// Extract from game links:
var steamAppId = game.Links?
    .FirstOrDefault(l => l.Url?.Contains("store.steampowered.com/app/") == true)?
    .Url?
    .Split('/')
    .FirstOrDefault(part => long.TryParse(part, out _) && long.Parse(part) > 0);
```

In a typical library a substantial share of games carry no Steam link, so those fall
through to name-based matching. The Steam App ID path is the fast, definitive one;
name matching (exact / substring / Levenshtein) covers the rest.

## Tag Model

Tags support hierarchy (parent-child relationships):

```csharp
public class Tag
{
    public Guid Id { get; set; }            // Unique tag ID
    public string Name { get; set; }        // Display name (e.g., "VRMod Full Motion Controls")
    public Guid? ParentId { get; set; }     // Parent tag ID (null for top-level tags)
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

1. External source provides a Steam App ID (e.g., from RototRobot rendered HTML, UeVR Profiles API)
2. Iterate through Playnite game `Links` collection for `store.steampowered.com/app/{id}/` URLs
3. Compare extracted App IDs — exact match is definitive

```csharp
// In VrModTagService:
private Game FindGameBySteamId(string steamAppId)
{
    if (string.IsNullOrEmpty(steamAppId)) return null;
    
    return api.Database.Games.FirstOrDefault(g =>
        g.Links?.Any(l => l.Url?.Contains("store.steampowered.com/app/") == true &&
                          l.Url.Split('/').Contains(steamAppId)) == true);
}
```

### Name Match (Fallback)

When Steam ID matching fails (no Steam link on game, or no Steam ID on source entry):

1. **Normalize both names** — `StripVrSuffixes()` → lowercase → replace `-`, `_`, `.` with spaces
2. **Exact match** — compare normalized strings directly
3. **Substring match** — one contains the other (confidence: 0.95)
4. **Levenshtein similarity** — threshold 0.8, uncertain range 0.8-0.95 triggers confirmation prompt

```csharp
// Name normalization (from BaseVrModParser)
protected string NormalizeName(string name)
{
    name = StripVrSuffixes(name);
    return name.ToLowerInvariant()
        .Replace("-", " ")
        .Replace("_", " ")
        .Replace(".", "")
        .Trim();
}

// VR suffix stripping
protected string StripVrSuffixes(string name)
{
    var result = name.Trim();
    if (result.EndsWith(" VR", StringComparison.OrdinalIgnoreCase))
        result = result.Substring(0, result.Length - 3).Trim();
    if (result.EndsWith(" Flat2VR", StringComparison.OrdinalIgnoreCase))
        result = result.Substring(0, result.Length - 8).Trim();
    if (result.EndsWith(" VR Mod by ", StringComparison.OrdinalIgnoreCase))
        result = result.Substring(0, result.Length - 11).Trim();
    if (result.EndsWith(" VR by ", StringComparison.OrdinalIgnoreCase))
        result = result.Substring(0, result.Length - 7).Trim();
    
    var byIndex = result.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
    if (byIndex > 0 && byIndex < result.Length - 4)
        result = result.Substring(0, byIndex).Trim();
    
    return result;
}
```

## Complete Matching Pipeline

```
For each VrModEntry from external source:
1. If entry.SteamAppId is populated:
   a. Search Playnite games for matching Steam link (store.steampowered.com/app/{id}/)
   b. If found → exact match, apply tags
   c. If not found → fall through to name matching

2. Name matching:
   a. Normalize both names (StripVrSuffixes → lowercase → replace -/_/.)
   b. Exact match → apply tags (confidence: 1.0)
   c. Substring match (one contains other) → apply tags (confidence: 0.95)
   d. Levenshtein similarity >= 0.8 → apply tags if Accepted; Pending → review dialog; Rejected → skip
   e. No match → skip game

3. Track matched game IDs for stale tag detection
```

## What This Plugin Writes to the Database

The plugin's only database writes are **tags** and **tag assignments**:

1. **`Tag` collection** — creates the `VRMod*` tag hierarchy if it does not exist yet
   (`VRMod` umbrella + framework categories + `6DOF` / quality facets; names come from
   `VrModCategories`). Existing tags with the same names are reused, never duplicated.
2. **`Game.TagIds`** — adds the tag IDs to matched games and removes them from games
   that are no longer listed by any enabled source (stale-tag sweep, skipped entirely
   while any source failed the scan).

Everything else the plugin keeps is **not** in the Playnite database:

- **Source data cache** — purely in memory. Per-source fetched entries live in a process-local
  dictionary with a fetch timestamp (`CacheDurationMinutes` setting); nothing is written to
  disk. A Playnite restart re-fetches (or reuses nothing — the cache is empty at startup).
- **Match decisions & settings** — stored via Playnite's plugin settings mechanism
  (`config.json` inside the plugin's own folder), not the game database. These include
  accepted/rejected/pending decisions and their first-seen timestamps.

In short: the DB diff caused by this plugin is limited to `VRMod*` tags and the games
they are assigned to — the same surface the stale-tag sweep can safely clean up.

## Inspecting the Database Files

This plugin does not ship a database tool (nothing under `tools/` is part of the
plugin). To inspect Playnite's LiteDB files outside of Playnite, install an
open-source LiteDB tool from the internet. Playnite must be **closed** first —
LiteDB locks the files exclusively.

Recommended (all open source, installed from GitHub):

- **LiteDB Studio** (GUI, official) — <https://github.com/litedb-org/LiteDB.Studio>.
  Grab a release binary and open the `playniteDB/*.db` files.
- **litecli** (command line) — <https://github.com/prahladyeri/litedbcli>. Build/run
  from source, then open a file and run LiteDB query-language statements (e.g.
  `list`, `find game 'TagIds != []'`).

> **Version caveat:** Playnite is pinned to **LiteDB v4.1.4**, while the current
> `litedb-org/LiteDB` (and many off-the-shelf tools) are **v5**. Opening a v4 data
> file with a v5 client wants `Upgrade=true` in the connection string, which can
> rewrite the file — and Playnite expects the v4 layout. So open Playnite's DB
> **read-only** where possible, prefer a v4-based reader, and never let a tool
> upgrade/migrate the file. Useful for verifying game names, Steam App IDs, and tag
> structures without running Playnite.

## Database Files Location

- **Portable mode:** `{PlayniteInstallDir}\library\`
- **Install mode:** `%AppData%\Playnite\library\`

The `database.json` file in the database directory contains only the schema version:
```json
{"Version":4}
```

## Technical Notes

- **LiteDB version:** 4.1.4 (not 5.x — Playnite explicitly uses v4 for cache control and performance)
- **File format:** Each collection stored in a separate `{collection}.db` file under the library directory
- **Connection mode:** `Mode=Exclusive` (Playnite SDK uses exclusive locking)
- **BSON serialization:** Playnite uses a custom `BsonMapper` to map C# models to BSON documents
- **File signature:** First bytes are `00 00 00 00 01 FF FF FF FF FF FF FF FF 01 00 00` — NOT `SQLite format 3\000`
- **Cannot query with sqlite3** — must use LiteDB library or Playnite's SDK API
