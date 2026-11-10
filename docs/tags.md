# Tags & data sources

## What tags are applied

| Tag | Source | Description |
| --- | --- | --- |
| `VRMod` | any | Umbrella tag for games with any VR mod match. |
| `VRMod Full Motion` | RototRobot (its "Full Motion Controls" category) | Full-body / motion-capture VR mods. Tagged under the same name PCVR Central's motion level uses (see the motion facet below). |
| `VRMod UEVR` | RototRobot, UeVR Profiles | UE4/UE5 injector profiles. |
| `VRMod Motion Gesture` | RototRobot | Gesture-based VR mods. |
| `VRMod Head Tracked` | RototRobot | Head-tracking (3DoF) mods. |
| `VRMod Dedicated` | RototRobot, PCVR Central (`native-custom`) | Dedicated VR mods, injectors, and emulators — a mod built for one game. |
| `VRMod Flat2VR Port` | Flat2VR | Official Flat2VR Studios ports. |
| `VRMod REFramework` | REFramework | RE Engine (Resident Evil) titles. |
| `VRMod VorpX` | VorpX | VorpX 6DOF tracking support (catalog of supported games). |
| `VRMod LukeRoss` | Luke Ross (R.E.A.L. VR) | R.E.A.L. VR motion-tracking support (catalog of supported games). |
| `VRMod UUVR` | PCVR Central (each mod's `framework`) | UUVR, the Unity-engine injector. It is its own tag rather than `VRMod UEVR` because UEVR is the Unreal-engine tool — tagging a Unity game `UEVR` names the wrong tool and the wrong engine. |
| `VRMod Depth3D` | PCVR Central (each mod's `framework`) | A ReShade depth-shader 3D effect. The site itself calls this "not a real VR conversion", so it is its own tag rather than being mixed in with the real VR injectors. |
| `VRMod Other` | PCVR Central (each mod's `framework`) | The site's catch-all for a mod whose method fits none of its framework categories. |
| — (catalog source: no tag of its own) | PCVR Central, PCVR Mods Hub, Mr Survivor, CompoundVR | These four are catalog categories (`VrModCategories.IsCatalogCategory`): the source name never becomes a tag — the source link on the game carries the provenance. PCVR Central still contributes the framework categories and the quality/motion/status facets below. |
| `VRMod 6DOF` | REFramework (facet) | The mod supports 6DOF tracking. |
| `VRMod First Person` | (facet) | The mod is a first-person experience. |
| `VRMod Third Person` | (facet) | The mod is a third-person experience. |
| `VRMod Native-like`, `VRMod Excellent`, `VRMod Playable`, `VRMod Experimental` | PCVR Central (each mod's `qualityLabel`) | Quality facet: how close the mod plays to a native VR game. A listing with several mods contributes the union of their grades. |
| `VRMod Full Motion`, `VRMod Partial Motion`, `VRMod Gamepad Only`, `VRMod No Motion` | PCVR Central (each mod's `motionControls`) | Motion facet: how much of the body the mod tracks. `VRMod Full Motion` is also the tag RototRobot's "Full Motion Controls" category produces - one tag for one claim. |
| `VRMod Active`, `VRMod Update Detected`, `VRMod Outdated`, `VRMod Broken`, `VRMod Abandoned` | PCVR Central (each mod's `status`) | Mod-status facet: what the source currently says about the mod itself — whether it still works with the game's current build, whether a newer mod superseded it, whether the author dropped it. `VRMod Active` is on almost every PCVR Central listing (1,527 of its 1,548 mods), and that is deliberate: a game with **no** status tag then means "no source says anything about this mod", which is a different fact from "a source says it works". The four problem statuses cover 21 mods. |

Facet tags (`6DOF`, `First Person`, `Third Person`, quality, motion, mod
status) are orthogonal to the category: one mod can carry several. Quality
grades are `Native-like`, `Excellent`, `Playable` and `Experimental`; motion
levels are `Full Motion`, `Partial Motion`, `Gamepad Only` and `No Motion`; mod
statuses are `Active`, `Update Detected`, `Outdated`, `Broken` and `Abandoned`
— each becomes `VRMod {value}`, so `VRMod Excellent`, `VRMod Partial Motion`
or `VRMod Outdated`. A game keeps the status of **every** mod listed for
it: `VRMod Active` together with `VRMod Broken` means one working mod and
one broken one exist to choose between, not that one mod both works and does
not. Only values defined in `VrModCategories` become tags: a source value
the plugin does not know is skipped rather than turned into a new tag name,
which is what keeps the tag set finite when a website changes its labels.
`VRMod Full Motion Controls` was once a second tag for the same claim as
`VRMod Full Motion` - RototRobot's category spelling against PCVR Central's
motion level - so a game matched by both sources carried both. They are one
tag now. The source-side spelling is still understood (so decisions saved by an
older version still resolve), the tag that gets minted is `VRMod Full Motion`,
and every full scan renames the retired tag on any game that still carries it.

**One mod, several categories:** a single mod can yield more
than one category tag (for example a RototRobot mod that is both
full-motion *and* a UEVR profile yields `VRMod Full Motion` *and*
`VRMod UEVR`). A game's tag set is the union of every tag yielded by
every matched mod, so a game can carry several category tags at once.

## How a tag comes off again

Your saved decisions are the state; the tags and the links to mod
pages are what those decisions put into your library. A tag this
plugin owns comes off a game as soon as **nothing backs it any
more** — the same rule on every path, whether the change came from
you or from a scan. So there are as many ways to take a tag off as
there are to change what backs it:

| What you do | What comes off |
| --- | --- |
| Reject a pairing in the [review dialog](review.md) and **Save** | That mod's category and facet tags, and its link to the mod page. The umbrella `VRMod` tag goes as well if no other VR mod tag is left on the game. Other accepted mods keep their tags. |
| Set a decided row back to *Pending* and **Save** | The same tags come off, and the saved decision is cleared — the pairing is offered for review again. |
| **Reset my decisions for this game...** (a game's right-click menu → **VRMod Tags**) | Every tag and link the plugin put on those games, and the decisions behind them. The next scan asks again. |
| **Reject all assigned VR mods on this game...** (the same menu) | The assigned mods' tags and links. The decisions stay, as refusals, so those pairings are not offered again. |
| **Reset all decisions** (review dialog) → **Reset and remove tags** | Every `VRMod*` tag in your library. The **Reset only** option clears the decisions and leaves every tag where it is. |
| Turn a source off in [settings](settings.md) | Playnite asks *Remove the VRMod tags that came from "…"?* Tags another source also backs are kept. **Cancel** — or closing that question — leaves them all. |
| Nothing at all | When a mod's listing disappears from every source, the next full scan takes the tags it used to back off the game. A tag never outlives the match behind it. (If a source failed during that scan, the plugin removes nothing — it keeps the tags rather than guess. See [when a source is down](getting-started.md#when-a-source-is-down-or-you-cancel-a-scan).) |

**What counts as "ours" is the tag's name.** The plugin only ever
removes a tag named `VRMod` or `VRMod …`, and it never relies on a
tag id. So a tag you made yourself that happens to carry one of
those names cannot be told apart from the plugin's own, and goes
with them; every other name is untouched — your own tags and links
stay yours.

## Data sources

This plugin is built on the data and curation of these
community projects. Thanks to all the maintainers:

- [PCVR Central](https://pcvrcentral.com/mods) — VR mod catalog
- [UeVR Profiles](https://uevr-profiles.com/) — UEVR injector profiles
- [RototRobot VRMods-List](https://rototrobot.github.io/VRMods-List/)
  — community-curated, categorized list of VR mods (Full Motion
  Controls, UEVR, Motion Gesture, Head Tracked, Dedicated)
- [PCVR Mods Installer Hub](https://github.com/Mr-Nlce/PCVR-Mods-Installer-Hub)
  — Mr. Nlce's installer hub catalog
- [CompoundVR](https://compoundvr.com/) — curated
  flat-to-VR catalog with per-game tier verdicts
- [Mr Survivor VR HUB](https://mrsurvivor-installers.com/)
  — Mr Survivor's installer hub catalog
- [Steam Curator “UEVR Compatibility”](https://store.steampowered.com/curator/44946997-UEVR-Compatibility/)
  — community curator's UEVR playtest list
- [Steam Curator “VorpX Games”](https://store.steampowered.com/curator/38688993-VorpX-Games/)
  — community curator's classic VorpX list
- [REFramework](https://github.com/praydog/REFramework) — RE Engine (Resident
  Evil) mod framework; also the 6DOF capability facet
- [Flat2VR Studios](https://flat2vrstudios.com/) — official VR ports
- [VorpX](https://www.vorpx.com/supported-games/)
  — 6DOF tracking mod (supported-games list)
- [Luke Ross (R.E.A.L. VR)](https://www.patreon.com/lukerossvr)
  — R.E.A.L. VR motion-tracking mod
