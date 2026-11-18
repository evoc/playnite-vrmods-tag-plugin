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
| `VRMod Quest` | UnflatVR (per mod), CompoundVR (per game) | The game's VR comes as a port that runs on a standalone Meta Quest headset. It sits next to the umbrella tag rather than instead of it: the game is playable in VR, but not from the PC that holds this library entry. The two sources publish different things, so the claim differs: from UnflatVR, which labels every mod's platform, a Quest build of *that mod* exists; from CompoundVR, which labels platforms per game and never per mod, the site covers *this game* for Quest. *Which sources mint which* below says what that costs on CompoundVR, where a card its own fields call developer-made VR is not offered by that source at all. |
| `VRMod SteamFrame` | UnflatVR | The same claim for a standalone Steam Frame headset. UnflatVR is the only source that publishes this platform, so the tag is rare by construction rather than by omission — nothing in the other twelve sources says a thing about the Frame. |
| — (no tag: PCVR, and the displays that run it) | CompoundVR also publishes `PCVR`, `PSVR`, `PSVR2`, `Pico`, `Rift`, `Rift S`, `Vive`, `Index`, `Windows Mixed Reality` | All nine deliberately unmapped, each for a reason stated in the table under *Out of scope by decision, not by omission* below. In short: PCVR is this plugin's default and is never subdivided, the tethered displays sit inside that same bucket on retired hardware, and the console and Pico-store listings are not mods this plugin could install or launch. The headset tags mark a standalone device — the one case where the mod is a different artifact from the PC build. |
| — (catalog source: no tag of its own) | PCVR Central, PCVR Mods Hub, Mr Survivor, CompoundVR, UnflatVR | These five are catalog categories (`VrModCategories.IsCatalogCategory`): the source name never becomes a tag — the source link on the game carries the provenance. PCVR Central still contributes the framework categories and the quality/motion/status facets below; UnflatVR and CompoundVR still contribute the headset tags above, which describe a headset rather than a framework. |
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

### Which sources mint which - and what deliberately earns nothing

Thirteen sources contribute tags, and they do not contribute the same kind of
claim. A catalog category source names a *framework or a method*
(`UEVR`, `UUVR`, `Depth3D`); a catalog source may also name a *quality*, a
*motion level* or a *mod status*; and only two sources say anything about
which **device** the VR runs on.

**Headset tags - `Quest` and `SteamFrame` - come from exactly two of the
documented thirteen.** UnflatVR labels every mod with its platforms
(`data-platf`, comma-separated, and its own platform names are not a closed
set), so from UnflatVR the claim is "a build of *this mod* runs on that
standalone headset". CompoundVR labels every *game* (`data-platforms`, a
small closed vocabulary) and never a mod, so from CompoundVR the claim is
"this site covers the game for that headset". Those are the only two sources
that publish a device at all - and the only reason `SteamFrame` is rare is
that UnflatVR is the only one of the thirteen that names it.

CompoundVR also withholds its headset tag, and the entire listing, from a
card its own fields call developer-made VR: `data-route` reading `Native VR`
or `Official Hybrid`, with no mod tool named in `data-tech`. That is not
merely a tag withheld — the card stops being offered by that source, so it
brings no row into the review dialog, no umbrella tag and no source link, on
the grounds that a game whose VR the developer shipped is not something a
flat-to-VR source can offer. Measured on 2026-11-18: 213 of the 564 cards on
the recorded page, and 213 of 565 on the live page that day. A card naming a
mod tool anyway passes, whatever its route says, and a game another source
still lists keeps whatever that source justifies — the library tag is the
union over every source, so one source going quiet is not a loss
(see [the review dialog](review.md)).

#### Out of scope by decision, not by omission

The counts below come from the 562 cards recorded on 2026-11-17. The page
moves by a card or two a day (565 on 2026-11-18), so read the ratios, not the
denominators.

| Device | Why no tag |
| --- | --- |
| PCVR | The default. A tag for the headset you already own tells you nothing, and the plugin does not subdivide PCVR by display. |
| `Rift`, `Rift S`, `Vive`, `Index` | Inside that same PCVR bucket, on retired hardware. Valve made the Index line end-of-life in January 2026; Meta ended Rift S support after 2026-04-20. |
| `Windows Mixed Reality` | Same bucket, and 1 card in those 562: Microsoft ended the platform in November 2025. |
| `PSVR`, `PSVR2` | Where CompoundVR lists PlayStation-store console games and PSVR2-native titles - not mods, and nothing this plugin could install or launch. Measured: 0 of the 124 and 0 of the 80 cards carry a mod route. |
| `Pico` | Where that site lists Pico-store games and PSP emulator packages. Measured: 2 of the 39 cards carry a mod route, neither on a modded PC game, and all 39 also list `Quest` - so the tag would mark console and emulator packages while naming no game the `Quest` tag does not already name. |

`PSVR`, `PSVR2` and `Pico` are the three that were re-checked rather than
inherited: the site does publish them, and they were declined on the evidence
that they mark a store's own catalogue, not a mod.

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
- [UnflatVR](https://unflatvr.com/) — hand-curated directory of flat-screen
  games that gained VR through a mod: one page per mod, a hub per game, a
  Steam App ID for most games, and its own A–D grade per mod
- [PCVR Mods Installer Hub](https://github.com/Mr-Nlce/PCVR-Mods-Installer-Hub)
  — Mr. Nlce's installer hub catalog
- [CompoundVR](https://compoundvr.com/) — curated
  flat-to-VR catalog with per-game tier verdicts, the route by which each game
  gained VR, and the platforms it is reachable on
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
