# Playnite VR Mods Tag Plugin

**VRMod Tags** is a Playnite add-on — a metadata provider — that finds VR mods, ports and
conversions for the games already in your library. It reads community-curated sources, matches
their listings against your games, and tags the games it is confident about; the uncertain
matches wait in a review dialog until you decide. Accepted games get a `VRMod` tag, the source's
own category tags, and a link to the mod page.

## Features

- **Auto-tagging** — `VRMod` plus category-specific tags (UEVR, Depth3D, REFramework, and the
  source's own framework and status vocabulary), and a `Link` to the listing the match came from.
- **Twelve community sources** — ten enabled by default: PCVR Central, UeVR Profiles, RototRobot
  VRMods-List, PCVR Mods Installer Hub, CompoundVR, Mr Survivor VR HUB, Steam Curator "UEVR
  Compatibility", Steam Curator "VorpX Games", REFramework, Flat2VR Studios. Two are opt-in and
  stay out of every scan until you enable them: VorpX, and Luke Ross (R.E.A.L. VR). Each source is
  individually toggleable, and the settings page shows each one's health and last result.
- **A review dialog for the matches it is unsure about** — filter, sort, accept, reject, bulk
  actions, column control, open the mod page. Statuses are Saved/New (accepted), Rejected,
  Suggested and Pending.
- **Decisions stick** — an accepted or rejected pairing is keyed by the listing's identity (its App
  ID or page URL) and its game, so it survives a rename on the site and a rename in your library.
- **Scans** — at startup, when a game is added or renamed, and on a configurable interval, with
  per-source caching and a bell notification when something needs reviewing.
- **A run log** — what each source returned, what failed and why, per scan.
- **Clean uninstall** — every tag, link, queued change and setting the plugin added is removed.

## Install

1. Download the release package (`.pext`) from this repository's **Releases** page.
2. Double-click it. Playnite asks for confirmation and installs the add-on; a restart is required,
   because the package is applied on the next startup.
3. Press **F9** for Playnite's **Add-ons** window — the main **Settings** menu does *not* contain
   add-on settings — and configure it under Extensions settings → Metadata Sources → **VRMod Tags**.

Its settings file lives in Playnite's own add-on data directory
(`%APPDATA%\Playnite\ExtensionsData\<plugin id>\config.json`); the library changes it makes are
part of your Playnite library, not a private store.

## Documentation

| Document | For |
| --- | --- |
| [docs/README.md](docs/README.md) | The user manual: quick start, the review dialog, the settings page, every tag and every source. This is what the plugin's **Help** button opens. |
| [docs/getting-started.md](docs/getting-started.md) | Install, first scan, automatic scans, updating, uninstalling. |
| [docs/review.md](docs/review.md) | Every column and status in the review dialog, filters, sorting, bulk actions, resetting decisions. |
| [docs/settings.md](docs/settings.md) | The sources grid column by column, source health, the fuzzy-match threshold, scan interval, cache duration. |
| [docs/tags.md](docs/tags.md) | Every tag the plugin can apply and what it means, plus the community sources. |
| [SECURITY.md](SECURITY.md) | The threat model for scraping untrusted sites, and the gates the code enforces. |
| [architecture.md](architecture.md) | Design, data model, matching and threading contract. |
| [database.md](database.md) | How Playnite stores the library, and what the plugin writes into it. |
| [external-references.md](external-references.md) | Source URLs, Playnite SDK pointers, NuGet versions. |
| [AGENTS.md](AGENTS.md) | Contributor/agent guide: build commands, gates, conventions, where to start. |

## Build

Windows only: the plugin targets `net462` and WPF (Playnite's own extension target).

- .NET SDK 8, pinned by [`global.json`](global.json).
- `PlayniteSDK` 6.15.0 and `GitVersion.MsBuild` 6.8.2 from NuGet.
- Version comes from git through GitVersion: the nearest tag plus the commits since it. GitVersion
  applies one increment per release, with patch as the floor, and the Conventional-Commit messages
  decide whether it escalates to minor or major ([GitVersion.yml](GitVersion.yml)).
  A clone with no tags builds from `0.0.0` plus that one increment, tagged with a prerelease suffix
  counting the commits (`0.0.1-<n>`). `extension.yaml` carries `1.0.0` as a
  baseline: [the release workflow](.github/workflows/release.yml) rewrites the manifest it packages,
  and a local build keeps the repo value unless you pass `-Version` to `tools/Build-Release.ps1`.

### How a release is produced

[.github/workflows/release.yml](.github/workflows/release.yml) releases every push to `main`
(also manually, with an explicit version): it resolves the version, builds on `windows-latest`,
packages the `.pext` users install, tags it `vX.Y.Z`, and publishes the release with the package
attached. An untagged history resolves to `1.0.0`; after that every push bumps at least patch — a
`feat:` takes it to a minor, `<type>!:` or a `BREAKING CHANGE:` footer to a major. Third-party
Actions are pinned to full commit SHAs, and the job fails if anything in the dependency graph is
flagged.

```powershell
dotnet build -c Release source/Playnite.VRMods.csproj   # the add-on
dotnet test  tests/Playnite.VRMods.Tests.csproj         # unit, parser and integration suites
pwsh tools/Build-Release.ps1                            # a .pext you can double-click
```

`tools/Build-Release.ps1` publishes, strips the `Playnite.SDK.dll` that `dotnet publish` copies
(the host process supplies the SDK the plugin actually binds against), checks the package the way
Playnite's installer does, zips it, and re-opens the produced archive to verify it again. To try a
build without packaging, copy the published files to
`%LOCALAPPDATA%\Playnite\Extensions\<plugin id>\` and restart Playnite.

## Security posture

Everything this plugin reads from the twelve sources is untrusted input that becomes library data,
so [SECURITY.md](SECURITY.md) is written as a threat model rather than a vulnerability-report
policy. In short: one HTTP entry point (`BaseVrModParser.FetchStringAsync`); `Links` gated by
`WebUrl.IsSafeLinkUrl` at every point where one is created or merged, so `file:`, `javascript:`,
app-URI schemes, `search-ms:` and credential-bearing URLs never reach the library; automatic
redirects off, hops followed explicitly, capped, and only onto the host the plugin asked for;
`robots.txt` respected; response size, volume and pace capped.

## Related projects

Other projects in the same space, in three groups: Playnite add-ons that tag the library the way this
one does, installers and launchers for the mods themselves, and catalogues that publish the mod lists.
All are independent codebases written by other people: this repository neither maintains nor audits
them, and they share none of this plugin's matching code. Links and versions checked 31 October 2026.

### Playnite add-ons that tag the library

These ask the same question this plugin asks — *which games in my library can I play in VR, and with
what mod?* — and answer it by writing tags and links into the library.

| | [VR Mod Tagger](https://github.com/tmuftee/playnite-vr-mod-tagger) (Talha Muftee) | [PCVR Central Matcher](https://github.com/SW-Fan1138/Playnite-PCVRCentralMatcher) (SW-Fan1138) | VRMod Tags (this plugin) |
| --- | --- | --- | --- |
| Extension type | Generic plugin; menu commands per game or per library | PowerShell script extension; one `Sync` menu command | Metadata provider; scans on its own |
| Sources it reads | Four: PCVR Central, PCVR Gamer, MrSurvivor Installers, PCVR Mods Installer Hub | One: PCVR Central | Twelve, individually toggleable |
| Built-in VR as its own fact | Yes — a separate `Native VR` tag | Yes — a separate `PCVR-Native` tag | No — mod availability and per-mod quality labels only |
| Human check before writing | Shows the change list and asks first; optional review for similar-title matches | Applies on Sync; you create the `PCVR-Mod` and `PCVR-Native` tags yourself first | Tags confident matches automatically; everything else waits in the review dialog |
| Tag vocabulary | `VR Mod`, `Native VR`, optional Quest and per-source tags | `PCVR-Mod`, `PCVR-Native` | `VRMod` plus the source's framework/quality vocabulary |
| Getting rid of it | **Undo last run** | Stale VR tags are dropped by the next Sync | Clean uninstall removes every tag, link, queued change and setting |
| State | v0.1.1 (28 Oct 2026), MIT, beta | v1.0.0 (22 Oct 2026), MIT | This repository's **Releases** page, MIT |

Nine of this plugin's twelve sources appear in neither of the other two; **PCVR Gamer** is the only
source they read that this plugin does not.

Two things worth knowing before you install a second one:

- **Tag vocabularies do not line up.** Each add-on only manages the tags and links it owns, so
  running two gives you `VRMod` and `VR Mod` and `PCVR-Mod` on the same game, and a link per add-on.
  Filtering stays usable, but pick one per library if you want one clean vocabulary.
- **PowerShell extensions are on their way out.** Playnite's own docs state that PowerShell extension
  support is being removed in Playnite 11
  ([intro to extensions](https://api.playnite.link/docs/master/tutorials/extensions/intro.html)), so a
  script-extension add-on needs a C# rewrite to survive the next Playnite major.

Neither this plugin nor either add-on above is listed in Playnite's built-in add-on browser:
submissions to the official [add-on database](https://github.com/JosefNemec/PlayniteAddonDatabase)
are on hold until the new portal that ships with Playnite 11 and is backported to 10, so each one is
installed from its own **Releases** page.

### Mod installers and launchers, not library taggers

A different job, in the same ecosystem: none of these reads a Playnite library or writes a tag. They are
what you use after a tag has told you a mod exists.

- [**Rai Pal**](https://github.com/Raicuparta/rai-pal) (Raicuparta, GPL-3, Windows and Linux) — finds
  your installed and owned games across Steam, GOG, Epic, itch and PC Xbox, detects each game's engine
  and engine version, and installs/updates universal mods (UEVR, UUVR) and their profiles. It answers
  per game, per machine, and never per library tag. No Playnite integration; a Playnite extension was
  requested upstream in [rai-pal discussion #115](https://github.com/Raicuparta/rai-pal/discussions/115).
- [**PCVR Mods Installer Hub**](https://github.com/Mr-Nlce/PCVR-Mods-Installer-Hub) (Mr. Nlce, MIT) —
  a portable batch installer and launcher with Steam detection. It tags nothing, but its mod list is
  one of this plugin's twelve sources.
- [**Unreal Easy Injector**](https://uevrdeluxe.org) ([oduis/UEVRDeluxe](https://github.com/oduis/UEVRDeluxe))
  — a friendlier front end for UEVR with an online database of profiles and supported games.

### Catalogues this plugin does not read today

Listings rather than add-ons, and candidates for future sources: [UnflatVR](https://unflatvr.com/directory/)
(flat-to-VR mod directory split by PCVR, Quest and Steam Frame), [MyUEVR](https://myuevr.pages.dev/)
(UEVR game database and profile catalogue), [UEVR Hub](https://uevr-profiles.github.io/) (community UEVR
profiles, not a game list). A source joins the plugin when it has a current, scrapeable structure and
games no other source already covers — see the ranking in [external-references.md](external-references.md).

### Know one this list is missing?

Open an issue or a PR. To belong here a project should read a public VR-mod listing, write tags or
links into the Playnite library, and publish a public repository with releases — say which sources it
reads and what tags it writes, so the table stays useful.

## Contributing

[AGENTS.md](AGENTS.md) is the entry point, and commit messages are checked by
[commitlint](.github/workflows/commitlint.yml) in Conventional-Commits form. Dependabot is
configured for the NuGet requirements.

## License

[MIT](LICENSE). The twelve community sources this plugin is built on are named and linked in
[docs/tags.md](docs/tags.md) and [external-references.md](external-references.md).
