# External References

External locations this plugin depends on: the VR mod data sources it reads,
the sites it was evaluated against, the Playnite SDK, and the packages it
builds with.

## VR mod data sources

Every source the plugin ships. `Listings` is how many mod entries the source's
parser emits from a live fetch. `Served as` is what the plugin has to deal with
to get them. Sources are listed best first; this is also the row order in the
settings grid and the order they are registered in.

The rating axes are **quality** (curatedness and structure), **actuality** (how
live), **access** (how scrapeable) and **fit** (how well the data maps onto the
plugin's framework, quality, motion and status facets).

| Rank | Source | Site | Served as | Listings | Quality | Actuality | Access | Fit |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PCVR Central | [pcvrcentral.com/mods](https://pcvrcentral.com/mods) | Next.js RSC payload inlined in the catalogue page | 1174 | ★★★★★ | ★★★★ | ★★★ | ★★★★★ |
| 2 | UeVR Profiles | [uevr-profiles.com](https://uevr-profiles.com/) | Public Firestore REST endpoint | 484 | ★★★★ | ★★★★★ | ★★★★★ | ★★★★ |
| 3 | RototRobot VRMods-List | [rototrobot.github.io/VRMods-List](https://rototrobot.github.io/VRMods-List/) | GitHub markdown, rendered site too | 233 | ★★★★ | ★★★★★ | ★★★★★ | ★★★★★ |
| 4 | UnflatVR | [unflatvr.com](https://unflatvr.com/) | One inline JSON island holding the whole catalogue | 734 | ★★★★ | ★★★★★ | ★★★★ | ★★★★ |
| 5 | PCVR Mods Installer Hub | [github.com/Mr-Nlce/PCVR-Mods-Installer-Hub](https://github.com/Mr-Nlce/PCVR-Mods-Installer-Hub) | GitHub markdown | 360 | ★★★★ | ★★★★ | ★★★★★ | ★★★★ |
| 6 | CompoundVR | [compoundvr.com](https://compoundvr.com/) | HTML cards | 486 | ★★★★ | ★★★★ | ★★★★ | ★★★★ |
| 7 | Mr Survivor VR HUB | [mrsurvivor-installers.com](https://mrsurvivor-installers.com/) | HTML table | 75 | ★★★★ | ★★★★★ | ★★★★ | ★★★ |
| 8 | Steam Curator "UEVR Compatibility" | [store.steampowered.com](https://store.steampowered.com/) | Curator recommendations endpoint | 22 | ★★★ | ★★★ | ★★★★ | ★★★ |
| 9 | Steam Curator "VorpX Games" | [store.steampowered.com](https://store.steampowered.com/) | Curator recommendations endpoint | 97 | ★★★ | ★★ | ★★★★ | ★★★ |
| 10 | REFramework | [github.com/praydog/REFramework](https://github.com/praydog/REFramework) | README in the repository | 19 | ★★★ | ★★★★ | ★★★★★ | ★★★ |
| 11 | Flat2VR Studios | [flat2vrstudios.com](https://flat2vrstudios.com/) | HTML grid | 10 | ★★★ | ★★★ | ★★★★ | ★★ |
| 12 | VorpX | [vorpx.com](https://www.vorpx.com/) | Supported-games table | 271 | ★★★★ | ★★★★ | ★★★★ | ★★ |
| 13 | Luke Ross / R.E.A.L. VR | [patreon.com/realvr](https://www.patreon.com/realvr) | Patreon posts | 36 | ★★★ | ★★ | ★★ | ★★ |
| | **Total** | | | **4001** | | | | |

VorpX and Luke Ross / R.E.A.L. VR are **opt-in** and disabled by default: a
paid driver's supported-games table and one author's catalogue are not general
flat-to-VR mod catalogues, so they stay off until someone asks for them.

### What each source brings

- **PCVR Central** - Hand-curated catalogue, one page per game, structured mod
  facts with quality labels and a visible freshness date per claim. Most of its
  entries carry a Steam App ID, so most matches are exact rather than
  name-based. It is the only source publishing per-mod framework, quality,
  motion and status data; how that is mapped is in
  [architecture.md](architecture.md).
- **UeVR Profiles** - Live Firestore endpoint, the freshest data in the set, and
  it carries Steam App IDs. Unreal Engine only.
- **RototRobot VRMods-List** - Community-standard flat list in GitHub markdown.
  The best fit-per-effort of the general sources: small, and entirely relevant.
- **UnflatVR** - Directory of flat-screen games that gained VR through a mod: a
  hub per game, a page per mod, its own A-D grade per mod, and Steam App IDs on
  most hubs. One unauthenticated request returns the entire catalogue, so a
  scan costs a single fetch. Its per-mod status, control and framework data is
  published but not read (see *Not built*).
- **PCVR Mods Installer Hub (Mr. Nlce)** - MIT-licensed installer list,
  trivially scrapeable, and one of the two sources that carry motion-control
  information.
- **CompoundVR** - Curated flat-to-VR catalogue with a per-game S to F tier, a
  written verdict and a per-card update timestamp. Contributes titles no other
  source lists. No Steam App IDs, so it matches by name only.
- **Mr Survivor VR HUB** - Current, with per-game platform and framework
  (UEVR / VRO / Meta Quest). Clean HTML table.
- **Steam Curator "UEVR Compatibility"** - Playtested indie titles absent from
  the canonical UEVR list, each with the curator's own note.
- **Steam Curator "VorpX Games"** - Older VorpX titles the vendor's own current
  list omits, so it fills a real gap. A curator's archive rather than a live
  feed.
- **REFramework** and **Flat2VR Studios** - Single-franchise and single-port
  coverage. Free to run, negligible ceiling.
- **VorpX** - The supported-games table of one paid VR driver. Broad but low
  fit: it says a game runs on VorpX, not that a flat-to-VR mod exists.
- **Luke Ross / R.E.A.L. VR** - One author's catalogue. Static, and small.

### Not built

- **UnflatVR's per-mod facets.** Status, control scheme and framework are
  published per mod and would feed the quality, motion and status facets.
  Reading them needs a second fetch phase, one request per matched game; the
  source as shipped uses the listing level only.
- **A matching layer over the Steam Web API** for titles whose source spelling
  does not survive normalisation.

## Candidate sources evaluated and not used

Flat-to-VR collections that were fetched, compared against every source above,
and rejected. Recorded so they are not re-investigated. The comparison is source
against source, not against any particular library.

| Source | Site | Result |
| --- | --- | --- |
| Steam Curator "VR Moddable" | [store.steampowered.com/curator/45016999-VR-Moddable](https://store.steampowered.com/curator/45016999-VR-Moddable/) | 90 apps, all but two already covered by App ID; the two exceptions are a native-VR title and a stereo-3D tip, both out of scope. Updated in one bulk wave, and it curates from sources already read. |
| SpaceRoachVR | [spaceroachvr.com/vr-mods](https://spaceroachvr.com/vr-mods) | 19 flat-to-VR entries, every one of them already present in PCVR Central at mod level. Fresh, but nothing to gain. |
| Beyond Flatscreen VR Mod Hub | [beyondflatscreen.vercel.app](https://beyondflatscreen.vercel.app/) | 107 games through a clean JSON API; all but one already covered, and the one net-new entry is a native-VR port. Best API of the candidates. |
| Flat2VR Modding Group | [beastsaber.notion.site](https://beastsaber.notion.site/Flat2VR-Modding-Group-8eb9ae0535144eac843f428abb104de9) | Not published to the web: the URL answers 200 with Notion's generic marketing metadata and the public page API rejects anonymous reads. Unreachable without a workspace token. Worth revisiting if it ever goes public. |

## General game databases

Not VR mod sources. Name-resolution and metadata services, relevant if matching
needs a canonical title rather than a source's own spelling.

- **Steam Web API** (`api.steampowered.com`) - free, no key required.
- **Steam Grid DB API v2** - icons and metadata by App ID, free.
- **IGDB** (`api.igdb.com/v4/`) - the richest cross-platform metadata, but it
  needs a user-supplied Twitch developer app and OAuth credentials.

## Playnite SDK and documentation

| Item | Location |
| --- | --- |
| Playnite SDK | `PlayniteSDK/` folder of a Playnite source checkout |
| Playnite source repo | root of the same checkout |
| Playnite test plugin (event and database patterns) | `source/Tests/TestPlugin/` of the same checkout |
| Plugin API docs | <https://api.playnite.link/docs/> |
| Plugin tutorial | <https://api.playnite.link/docs/tutorials/extensions/plugins.html> |

## Packages

| Package | Version | Where |
| --- | --- | --- |
| PlayniteSDK | 6.18.0 | plugin |
| System.Text.Json | 8.0.5 | plugin |
| System.ValueTuple | 4.5.0 | plugin, .NET Framework 4.6.2 tuple support |
| NUnit | 4.2.2 | tests |
| NUnit3TestAdapter | 4.6.0 | tests |
| NSubstitute | 5.1.0 | tests |
| Microsoft.NET.Test.Sdk | 17.11.1 | tests |
| GitVersion.MsBuild | 6.8.2 | build |
| Microsoft.NETFramework.ReferenceAssemblies | 1.0.3 or newer | build |
