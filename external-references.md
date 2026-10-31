# External References

Locations for external URLs, SDK paths, source code, databases, and tools referenced during development.

## External VR Mod Data Sources

| Source | URL | Format |
| -------- | ----- | -------- |
| RototRobot VRMods-List (raw) | <https://github.com/RototRobot/VRMods-List/blob/gh-pages/index.md> | Markdown |
| RototRobot VRMods-List (rendered) | <https://rototrobot.github.io/VRMods-List/> | HTML (filterable game cards) |
| Flat2VR Studios | <https://flat2vrstudios.com/> | HTML grid |
| UeVR Profiles | <https://uevr-profiles.com/> | JavaScript SPA |

### Additional source links (user-provided 2026-08-31)

Links the user provided for additional VR mod sources. All are now
integrated sources. Kept here verbatim so they are never lost again.

| Source | URL | Status |
| -------- | ----- | -------- |
| Mr Survivor Installers | <https://mrsurvivor-installers.com/> | Integrated (Mr Survivor VR HUB source) |
| PCVR Mods Installer Hub (Mr. Nlce) | <https://github.com/Mr-Nlce/PCVR-Mods-Installer-Hub> | Integrated (PCVR Mods Installer Hub source) |
| RototRobot VRMods-List (rendered) | <https://rototrobot.github.io/VRMods-List/> | Already integrated (existing source) |
| PCVR Central (mods catalog) | <https://pcvrcentral.com/mods> | Integrated (PCVR Central source) |

### Source ranking (researched 2026-08-31, reranked 2026-10-10 over all 12 integrated sources)

Per-title VR-mod *data sources* ranked by **quality** (curatedness / structuredness /
reliability), **actuality** (how current / live), **access** (ease of scraping), and
**fit** (maps to our category + quality / 6DOF / motion facets). All 12 integrated
sources are ranked below (game counts from the 2026-10-09 snapshot). This ranking is
also the display order of the sources in the plugin's settings view (best first).

| Rank | Source | Games | Quality | Actuality | Access | Fit |
| ------ | -------- | ------- | --------- | ----------- | -------- | ----- |
| 1 | PCVR Central (`pcvrcentral.com/mods`) | 1011 | ★★★★★ | ★★★★ | ★★★ | ★★★★★ |
| 2 | UeVR Profiles | 484 | ★★★★ | ★★★★★ | ★★★★★ | ★★★★ |
| 3 | RototRobot VRMods-List | 226 | ★★★★ | ★★★★★ | ★★★★★ | ★★★★★ |
| 4 | PCVR Mods Installer Hub (Mr. Nlce) | 302 | ★★★★ | ★★★★ | ★★★★★ | ★★★★ |
| 5 | CompoundVR (`compoundvr.com`) | 422 (191 flat-to-VR) | ★★★★ | ★★★★ | ★★★★ | ★★★★ |
| 6 | Mr Survivor VR HUB (`mrsurvivor-installers.com`) | 62 | ★★★★ | ★★★★★ | ★★★★ | ★★★ |
| 7 | Steam Curator "UEVR Compatibility" | 21 | ★★★ | ★★★ | ★★★★ | ★★★ |
| 8 | Steam Curator "VorpX Games" | 97 | ★★★ | ★★ | ★★★★ | ★★★ |
| 9 | REFramework | 19 | ★★★ | ★★★★ | ★★★★★ | ★★★ |
| 10 | Flat2VR Studios | 10 | ★★★ | ★★★ | ★★★★ | ★★ |
| 11 | VorpX (`vorpx.com`) | 271 | ★★★★ | ★★★★ | ★★★★ | ★★ |
| 12 | Luke Ross / R.E.A.L. VR | 36 | ★★★ | ★★ | ★★ | ★★ |

**Notes / rationale**

- **PCVR Central** - Hand-curated canonical catalog: one page per game, every mod with
  structured facts, **quality labels** + a **visible freshness date per claim**. Maps
directly onto our #28 quality facet and the "actuality" gap from #23. **Integrated
  (#44):** the /mods page's inlined RSC payload is the data source (871+ carry
  steamAppId for exact Steam matching).
- **UeVR Profiles** - Live Firestore REST; the strongest *actuality* (always current).
  UE-only framework.
- **RototRobot VRMods-List** - Community-standard flat list; live GitHub raw markdown;
  best fit/effort of the general sources.
- **PCVR Mods Installer Hub (Mr. Nlce)** - MIT; portable batch installer with Steam
detection; carries **motion-controls tags** (maps to our motion / 6DOF facet); GitHub raw
  markdown = trivially scrapeable.
- **CompoundVR** - Curated flat-to-VR catalog: per-game S/A/B/C/D/F tier + verdict and a
  per-card update timestamp (free freshness data); 422 game cards (191 in-scope
  flat-to-VR). The only source with a significant net-new coverage: ~55–65 in-scope
  games in no other source (task 97.3).
- **Mr Survivor VR HUB** - Very current (v0.9.90, Aug 2026) with per-game platform +
  framework (UEVR / VRO / Meta Quest); clean HTML table to parse.
- **Steam Curator "UEVR Compatibility"** - 21 games; 15 UEVR-playtested indies not in the
  canonical UEVR list, each with the curator's per-game playtest note; last review
  2026-05-22 (task 97.4, included by user decision 2026-10-09).
- **Steam Curator "VorpX Games"** - 97 games; 28 classic (2000s–2010s) VorpX titles not in
  vorpx.com's current list; static 2020-09-06 snapshot (task 97.5, included by user
  decision 2026-10-09).
- **REFramework / Flat2VR** - Narrow single-franchise / port coverage; zero-cost but low ceiling.
- **VorpX / Luke Ross** - Rank last despite decent quality: they are capability/author
  lists (one paid VR-driver product's supported-games table; one mod author's static
  36-game snapshot), not general flat-to-VR mod catalogs → lowest fit. They stay in the
  plugin as opt-in (disabled-by-default) sources.

**Different axis - general game databases** (for name resolution / matching, NOT VR
coverage): **Steam Web API** (`api.steampowered.com` - free, no key; our titles are already
Steam-native, so this is the best low-friction matching source), **Steam Grid DB API v2**
(icons / metadata by AppId, free), **IGDB** (40k+ games, cross-platform, richest metadata,
but requires Twitch OAuth: account + 2FA + developer-portal app -> JWT to
`api.igdb.com/v4/`).

**Recommendation**

1. **Highest-value new source: PCVR Central.** ~900 games, hand-curated, with quality
   labels + freshness dates that map onto the #28 quality facet and close the #23
   actuality gap. **Integrated (#44):** reads the /mods RSC payload (991 games; 871 with steamAppId).
2. **Easiest win: Mr. Nlce PCVR Mods Installer Hub.** ~100+ games, MIT, GitHub raw markdown
   (trivially scrapeable like RototRobot), motion-controls tags map to our facet.
3. **Low-effort complement: Mr Survivor VR HUB.** 35 games, very current, per-game
   platform + framework. Clean HTML table.
4. **Keep the current four.** UeVR (live) + RototRobot (general) are the strongest live
   pair; REFramework / Flat2VR are narrow but zero-cost.
5. **Use Steam Web API as the matching layer** (not a VR source): canonical Steam titles,
   free / no key, and our names are already Steam-derived. IGDB is the richer
   cross-platform option but needs a user-supplied Twitch OAuth key.

### Tested and excluded candidate sources (task 97, checked 2026-10-09)

Candidate flat-to-VR collections that were **dry-fetched, measured against all existing
sources, and tested out** during task 97. None of them is included as a plugin source. Full per-source reasoning and the
check date are recorded here so they are not re-investigated.

| # | Source | URL | Checked | Dry-fetch result | Excluded because |
| --- | ------- | ----- | ------- | ---------------- | ------------------ |
| 97.1 | Steam Curator "VR Moddable" | <https://store.steampowered.com/curator/45016999-VR-Moddable/> | 2026-10-09 | 90 apps | 0 in-scope new games (2 raw-uncovered, both out-of-scope: 1 native-VR, 1 stereo-3D tip); redundant with existing sources |
| 97.2 | SpaceRoachVR /vr-mods | <https://spaceroachvr.com/vr-mods> | 2026-10-09 | 19 flat-to-VR entries (+2 in-progress) | 0 new games / 0 new mods (19/19 mods already in PCVR Central) |
| 97.6 | Beyond Flatscreen \| VR Mod Hub | <https://beyondflatscreen.vercel.app/> | 2026-10-09 | 107 games (JSON API) | 1 net-new game (Prey 2017 VR); ~106/107 already covered by existing sources |
| 97.7 | uevr-profiles.com | <https://uevr-profiles.com/> | 2026-10-09 | 484 games, 760 user profiles (public Firestore) | 100% redundant: 484/484 AppIDs = exact 1:1 match with the existing UeVR source's game set; value is user-profile enrichment, not coverage |
| 97.8 | Flat2VR Modding Group (Notion) | <https://beastsaber.notion.site/Flat2VR-Modding-Group-8eb9ae0535144eac843f428abb104de9> | 2026-10-09 | not fetchable | PRIVATE Notion page (generic og-meta, `loadPageChunk` → 400) — needs Notion workspace access or an API token; unverifiable without credentials. Revisit only if it becomes public or a token is provided |

**Per-source reasoning (all checked 2026-10-09, one-off dry fetches against saved copies of
the candidate sources):**

- **97.1 Steam Curator "VR Moddable"** — 90 apps; 88 covered by AppID, 2 raw-uncovered
  (both out-of-scope: 1 native-VR, 1 stereo-3D cinema tip) → **0 in-scope new games**.
  Bulk-updated 2026-08-08 (single review wave, not a live-updated source). Redundant with
  the existing sources it curates from.
- **97.2 SpaceRoachVR /vr-mods** — 19 flat-to-VR entries (+2 in-progress). Mod-level check
  compared mod-by-mod against PCVR Central's mod list: 19/19 mods already exist there → **0 new games,
  0 new mods**. Freshness is good (last-checked 2026-10-03) but there is nothing new to
  gain from it.
- **97.6 Beyond Flatscreen | VR Mod Hub** — 107 games via clean JSON API
  (`/api/games?platform=all&page=1&limit=200`); 91 covered by exact AppID + 7 by name;
  of the 9 raw-uncovered, 8 are name-variants of covered games and 1 is a native-VR Quest
  port (out of scope) → **1 net-new game (Prey 2017 VR)**. Entries are fresh
  (lastUpdated max 2026-09-30) but the source's games are ~106/107 already in existing
  sources, so freshness yields no new coverage.
- **97.7 uevr-profiles.com** — public Firestore (project `uevrprofiles`, collection
  `games`), read directly: **484 games, 760 user profiles**. All 484 AppIDs are covered,
  and they are an **exact 1:1 match with the existing UeVR source's 484 AppIDs** (identical
  sets — both track "games that work with UEVR"). 100% redundant for coverage; the unique
  data is enrichment (community profile archives, authors, links, "Works Perfectly"
  tags), not coverage. Live (updateTime max 2026-10-09) but nothing new to gain.
- **97.8 Flat2VR Modding Group (Notion)** — **private**, not published to the web: the
  page URL returns HTTP 200 but serves Notion's generic marketing meta (`og:url` = app
  root, not the page's own URL; no page content in HTML), and the public `loadPageChunk`
  API returns `ValidationError (400)` even with proper client headers. Cannot be scored
  without a Notion workspace account or an API integration token. (Noted value if it
  ever becomes public: "Released VR Mods" list, mostly NOT UEVR-injector related.)

**Included from the same evaluation (for contrast):** 97.3 CompoundVR (~55–65 in-scope
uncovered games), 97.4 Steam Curator "UEVR Compatibility" (21 games; 15 UEVR-playtested
indies) and 97.5 Steam Curator "VorpX Games" (97 games; 28 classics) — the latter two
included per user decision 2026-10-09 despite the initial EXCLUDE recommendation
(marginal overlap with the existing UeVR / VorpX sources; valued for the playtested-
indie and classic-game coverage + notes).

## PCVR Central per-mod data: framework / quality / motion / status

Everything learned about the data PCVR Central carries **per mod** (the `mods[]` array inside
the `/mods` catalog payload) and how it maps onto this plugin's tag vocabulary. The
implementation is `source/Parsers/PcvrCentralParser.cs` (`MapFramework` / `MapQuality` /
`MapMotion` / `MapStatus`, `ComputeFeatureUnions`); the source's position among all sources
is the ranking above.

**Status: shipped.** Phase 1 (parse `mods[]` additively), Phase 2 (**meta-mod**: one entry per
**game** carrying the union of every feature across its mods) and Phase 3 (the four ambiguous
framework labels decided against the site's own label dictionaries, and `status` added as a tag
facet) are all implemented and unit-tested. PCVR Central is no longer catalog-only: matched
games now receive framework / quality / motion / status tags, while the review dialog and the
saved decision stay **one per game**. The heavier per-mod entry model stays as the documented
fallback (see "Behavior change").

### What the payload carries

The `/mods` RSC payload inlines a `"games":[ ... ]` array. Two captures are quoted below, and
they are not interchangeable: **2026-09-16** — **all 991 games** had a non-empty `mods` array,
**1 332 mods** total (most games have 1; 64 games have >1 distinct framework) — and **2026-10-30**,
**1 122 game listings / 1 548 mod records**. The `framework` and `status` counts are the 2026-10-30
capture; the `qualityLabel`, `motionControls` and `conversionType` counts are still the 2026-09-16
one. Each `mods[]` item has:

`slug`, `name`, `author`, `framework`, `conversionType`, `platforms`, `price`,
`motionControls`, `qualityLabel`, `status`, `difficulty`, `staffPick`, `headsetsTested`,
`addedDate`, `updatedDate`, `releaseDate`, `sourceUpdatedDate`, `externalRating`.

Three fields were the target of the design; `status` was added in Phase 3:

**`framework`** - *how VR is enabled* (2026-10-30 counts, games / mods, read from the payload's
label dictionaries — which count **mods** not games — and they grow as the site adds mods):

| value | count (games / mods) | category |
|----------------|-------|-------------------------------|
| `uevr-profile` | 720 / 743 | `UEVR` (existing) |
| `native-custom`| 258 / 445 | `Dedicated` |
| `vorpx`        | 269 / 269 | `VorpX` (**new**) |
| `luke-ross`    | 47 / 48 | `LukeRoss` (**new**) |
| `reframework`  | 23 / 25 | `REFramework` (existing) |
| `other`        | 10 / 13 | `Other` (**new**) |
| `uuvr`         | 3 / 3 | `UUVR` (**new**) |
| `depth3d`      | 2 / 2 | `Depth3D` (**new**) |

**`qualityLabel`** - *how good the VR is* (the first source to feed the `Quality` facet; 2026-09-16
counts, 1 332 mods): `playable` 453, `excellent` 445, `native-like` 263, `experimental` 109,
`unknown` 62.

**`motionControls`** - *how the player interacts* (2026-09-16 counts): `unknown` 629, `partial` 318,
`full` 247, `gamepad-only` 124, `none` 14.

**`status`** - what the source currently says about the mod itself (2026-10-30 label counts:
`active` 1 527 of 1 548 mods; `superseded` 8, `update-detected` 5, `broken` 5, `abandoned` 3).

Also available, not used (2026-09-16 counts): `conversionType` (`vr-framework` 1 064 / `vr-mod` 240 /
`vr-engine-port` 28), `difficulty`, `staffPick`.

### The model: orthogonal dimensions, union-all

A `VrModEntry` carries a **set** of categories plus orthogonal facets (`Is6DOF`,
`IsFirstPerson`, `IsThirdPerson`, `Qualities`, `Motions`, `Statuses`).
`VrModTagService.ComputeTagsForGame` accumulates the tags of **every** matched entry into a
set, so all of a game's tags coexist **even when they conflict**. The governing principle is
**union-all**: *if a source yields a tag, it is applied to the game; conflicting values are
both kept.* A game is `VRMod First Person` **and** `VRMod Third Person` when its sources
declare both; a game with one `excellent` UEVR profile (`full` motion) and one `playable`
VorpX mod (`partial` motion) carries `VRMod UEVR`, `VRMod VorpX`, `VRMod Excellent`,
`VRMod Playable`, `VRMod Full Motion`, `VRMod Partial Motion`. Each value is a per-mod claim;
the game carries every distinct one, which reads as "has at least one X".

Multi-valued fields inside **one** entry: `VrModEntry.Quality` / `.Motion` used to be single
fields, but a game's mods can differ, so Phase 2 made them collections (`Qualities` /
`Motions`, `IReadOnlyCollection<string>`) with the old single fields kept as legacy aliases
(first value, or null when empty). Frameworks already fit the `Categories` set; Phase 3 added
`Statuses` the same way.

`docs/tags.md` is **not a fixed boundary** - it is adjustable. This work extended it: the
`VorpX` / `LukeRoss` / `UUVR` / `Depth3D` / `Other` categories, the `Motion` and `Status`
facets, and the reconciliation still open in "Open questions" below.

### Normalized mapping (what ships)

```
framework:  uevr-profile->UEVR   reframework->REFramework   vorpx->VorpX
            luke-ross->LukeRoss   native-custom->Dedicated   other->Other
            uuvr->UUVR   depth3d->Depth3D
quality:    native-like->Native-like   excellent->Excellent   playable->Playable
            experimental->Experimental   unknown->(none)
motion:     full->Full Motion   partial->Partial Motion   gamepad-only->Gamepad Only
            none->No Motion   unknown->(none)
status:     active->Active   superseded->Outdated   update-detected->Update Detected
            broken->Broken   abandoned->Abandoned   unknown->(none)
```

Constants live in `source/VrModCategories.cs` (`VorpX`, `LukeRoss`, `UuVr`, `Depth3D`,
`Other`, `MotionFull/Partial/GamepadOnly/None`, `Quality*`, `Status*`) and are whitelisted in
`KnownTagValues`. Every framework label the feed ever emits now has an explicit mapping: the
parser forwards **no raw website string into a tag**, and an unmapped value is dropped with a
log line (`DropUnknownFeatureValues`).

### Framework ambiguities (decided 2026-09-16 - four new categories, not four skips)

Authority for each decision is the payload's own `frameworkLabels` / `labels.framework` /
`methodExplainer` dictionaries - the site's label for its own slug - not a guess from the
slug.

| Site slug | Decision | Why | Original recommendation (kept for the record) |
|---|---|---|---|
| `native-custom` (445 mods) | **`Dedicated`** | The site labels it "Native/Custom VR (no universal framework)" / "a dedicated VR mod built for this one game" - the fact `Dedicated` already records for every other source (Mod Loader, VRAlly, Flat2VR Port). **Caveat kept on record:** the site's display text leans to "the game has native/custom VR support", so a user can see `VRMod Dedicated` on a game whose VR is built in. A `Native VR` tag would need its own constant + vocabulary-test placement; "dedicated to this game" is true either way. | *No* framework category (umbrella + quality + motion only); do **not** force it into `Dedicated`. |
| `other` (13) | **`Other`** (new) | `Dedicated` claims "a game-specific mod exists"; `other` only claims "the site could not classify this". Collapsing them states something the source does not say. | `Dedicated`, or skip. |
| `uuvr` (3) | **`UUVR`** (new) | The site's label is "UUVR (Unity Support)": a **Unity** injector, while UEVR is the **Unreal** tool. Tagging a Unity game `UEVR` names the wrong tool and the wrong engine. Not a `UEVR Variant` either - the vocabulary test asserts `UeVrVariant` is derived from a `UEVR` name, which "UUVR" is not. | `UEVR` (same injector family). |
| `depth3d` (2) | **`Depth3D`** (new) | The site itself says "Depth3D (Shader) (not a real VR conversion)" - it is right that it is not a headset VR mode. But an unmapped value is not silently skipped: a `frameworks` value with no `Dedicated` category lands in `ModQualities`, and `DropUnknownFeatureValues` **removes** it and logs a complaint. A named tag for a method 2 mods actually use is cheaper than a permanent log line and a lost fact, and it is honest: `Depth3D` says "depth-shader 3D effect" and is never mixed into the real VR injectors. | Skip the framework category (not a VR framework). |

### `status`: a facet, not a suppression rule

The design question was whether a mod's `status` should **suppress** its framework tag. **It
does not.** Status is a fact about *the mod*, framework is a fact about *how it injects* -
orthogonal dimensions, and the model is union-all. Suppressing `UEVR` because a mod is
`Broken` would make the library claim the game has no UEVR mod, which is false: there is one,
it does not currently work.

- `superseded` -> **`Outdated`**, not `Superseded`. The site's label is "Superseded (newer
  version available)", i.e. "this mod has been overtaken", and `Outdated` is already this
  plugin's word for that fact (PCVR Mods Hub uses it) - one concept, one tag name, no synonym.
- `active` is **kept** although it covers 1 527 of 1 548 mods. Dropping the majority value
  would be the invisible move: a game with no status tag would then mean either "no source
  lists this mod" or "a source lists it and says nothing", and those are different facts.
  Keeping it makes "no status tag" mean "no source says anything".
- Status is **per mod, and a game keeps all of them** (`VrModEntry.Statuses`, union-all like
  `Qualities`/`Motions`): `Active` + `Broken` means a working mod and a broken one both exist.
  Which mod each status belongs to stays in `AdditionalInfo` (the per-mod feature summary) - a
  tag union cannot say *which* mod is the broken one, that text can.
- The site's quality vocabulary also has a `Broken` **grade** (no mod currently uses it). It is
  deliberately **not** mapped: it would collide with the status tag name, and two meanings in
  one tag name is how a tag list becomes unreadable.

### The root cause this fixed (the "0 tag(s) applied" log)

The catalog entry carried `Category = "PCVR Central"` - a catalog category that contributes no
tag - and no facet fields, so `TagNamesFor(entry)` was empty and `ApplyEntryTags` applied
nothing: matched games got only the umbrella `VRMod` tag plus source links. The meta-mod is
what makes that count > 0, while the `PcvrCentral` catalog category is **kept** for discovery
and still adds no tag.

### Behavior change and the two entry models

- **Meta-mod (shipped):** all of a game's mods are folded into **one entry per game** carrying
  the union of every framework, quality grade, motion level and status. Contradictions are
  kept (`Excellent` *and* `Playable`, `Full Motion` *and* `Partial Motion`) - the claim is
  "has at least one X", and a user who wants detail follows the per-game deep link to the
  pcvrcentral game page. The review dialog stays **one row per game** and the decision stays
  per `(source, sourceGame, game)`: no per-mod decisions, no decision migration.
- **Per-mod (deferred fallback):** one entry per mod (deduplicated by `slug` + framework),
  `IsCatalogCategory("PCVR Central")` flips to **false**, per-mod decisions (accept the UEVR
  profile, reject the VorpX mod), per-mod review rows with a quality/motion preview, and a
  migration story for existing per-game decisions. More granular, costlier; kept as the option
  if per-mod granularity is ever wanted.

### Related fix found by the same investigation: RototRobot multi-category (done)

RototRobot's `MapTagsToCategory` used to pick the **first** matching tag per mod (`motion` >
`uevr` > gesture > 3dof > tool/emulator), so a mod tagged `1st motion uevr` got
`Category = Full Motion Controls` and the **`UEVR` framework was dropped**. Live data: 20 games
carried `uevr`, 16 of them also carried `motion`, so 16 lost the framework - a union-all
violation. Fixed by the flat-tags model: `MapTagsToCategories` yields **every** matching
category and `VrModEntry.Categories` carries them, so that mod now yields both
`Full Motion Controls` **and** `UEVR`. See "RototRobot multi-category fix" in
[architecture.md](architecture.md).

### Open questions

- **Motion vocabulary** (open): the `Motion` facet values (`VRMod Full Motion`,
  `VRMod Partial Motion`, `VRMod Gamepad Only`, `VRMod No Motion`) sit next to the existing
  input-type **categories** (`VRMod Full Motion Controls`, `VRMod Motion Gesture`,
  `VRMod Head Tracked`). Rather than leaving near-duplicates, reconcile them into one
  vocabulary in `docs/tags.md`: either the facet reuses the category names, or input type
  becomes the `Motion` facet and the old categories are retired. Decide the tag names before
  implementing.
- **Per-mod rows:** whether to add the framework rows *in addition to* the catalog row
  (discovery value vs. row noise) or keep only the meta-mod. Current answer: the meta-mod plus
  the kept catalog category.
- `conversionType` is still unread (`vr-framework` / `vr-mod` / `vr-engine-port`) - a candidate
  facet if a future source or UI needs it.

## Playnite SDK & Source

| Item | Path |
| ------ | ------ |
| Playnite SDK | `PlayniteSDK/` folder of a local Playnite source checkout |
| Playnite source repo | root of the same checkout |
| Playnite test plugin (event + DB patterns) | `source/Tests/TestPlugin/` of the same checkout |
| Playnite API docs | <https://api.playnite.link/docs/> |
| Plugin creation tutorial | <https://api.playnite.link/docs/tutorials/extensions/plugins.html> |

## Playnite Install

| Item | Path |
| ------ | ------ |
| Plugin config storage | `%APPDATA%\Playnite\ExtensionsData\<Plugin-Id>\config.json` |

## Local Data & Tools

| Item | Path |
|------|------|
| Playnite game database sample | `playniteDB` (LiteDB v4.1.4 format; local working copy, not part of the repo) |
| LiteDB database reader (not part of the plugin) | see [database.md → Inspecting the Database Files](database.md#inspecting-the-database-files) for open-source tools to install from the internet (LiteDB Studio, litecli) |

## Test Helpers

| Item | Location | Purpose |
| ------ | ---------- | --------- |
| `FakeItemCollection<T>` | `tests/TestHelpers/FakeItemCollection.cs` | Fake IItemCollection for unit tests |
| `FakeBufferedUpdate` | `tests/TestHelpers/FakeItemCollection.cs` | Fake buffered update for DB tests |
| `NullLogger` | `tests/TestHelpers/FakeItemCollection.cs` | ILogger implementation that discards messages (consolidated here, not duplicated) |

## NuGet Packages

| Package | Version | Purpose |
| --------- | --------- | --------- |
| Playnite.SDK | 6.15.0 | Plugin SDK |
| System.ValueTuple | 4.5.0 | Tuple support for .NET Framework 4.6.2 |
| NUnit | 4.2.2 | Unit testing (test project) |
| NUnit3TestAdapter | 4.6.0 | Test adapter (test project) |
| NSubstitute | 5.1.0 | Mocking (test project) |
| Microsoft.NET.Test.Sdk | 17.11.1 | Test SDK (test project) |
