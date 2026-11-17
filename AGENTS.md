# PlayniteVRmods — Agent Guide

> **For humans:** [docs/README.md](docs/README.md) is the user-facing documentation
> (it is shipped inside the installed package and opened by the in-plugin
> Help button).
> **For agents/contributors starting fresh:** this file is the entry point and
> the index; the guide's detail lives in the `agents/` files listed below. Read
> this screen first — it holds the rules that are cheapest to break.
> Design, data model, UML: [architecture.md](architecture.md) · Playnite database
> structure: [database.md](database.md) · External URLs, SDK pointers, NuGet
> versions: [external-references.md](external-references.md).

A C# (.NET Framework 4.6.2, WPF) Playnite plugin that scrapes
thirteen community VR-mod sources, matches their listings to your
Playnite library (Steam App ID first, fuzzy name match as fallback),
applies `VRMod*` tags to matched games, and — for accepted
matches — records the source link in the game's Links section.

## First commands

```text

dotnet build              # plugin + tests; 0 errors AND 0 warnings
dotnet test               # the full suite
dotnet build -c Release   # the configuration CI builds
git log --oneline -10     # subjects must be Conventional Commits

```

Everything that has to match CI adds `-c Release`: a Debug-only run misses the
analyzers, and CI treats every warning as an error. Commit subjects decide the
released version. **`main` is not pushable** — branch, open a PR, let the gates
run, merge (`agents/ci-and-gates.md` lists every gate a PR faces).

## Hard rules

The ways this project breaks. The full reasoning, with the measurements behind
each rule, is in the file named at the end.

- Reference only `Playnite.SDK.dll`; `Playnite` and `Playnite.Common` are the
  host's, and referencing them breaks loading (`agents/conventions.md`)
- `Playnite.SDK.dll` never goes into the `.pext` — Playnite's package verifier
  rejects the package (`agents/release.md`)
- The version is the git tag `vX.Y.Z`; the `Version:` in `source/extension.yaml`
  is a placeholder that packaging overwrites (`agents/release.md`)
- Every push to `main` is a release; feature branches are built locally, never
  released (`agents/release.md`)
- Fix warnings, do not suppress them; where the SDK forces a suppression, use
  `#pragma warning disable` plus a reason comment (`agents/build-and-test.md`)
- No substring matching in the matcher: a proper substring scores `-1`
  (`agents/conventions.md`)
- A tag change needs `game.TagIds` mutation **and** `Games.Update(game)`; bulk
  work goes in `BufferedUpdate()` (`agents/conventions.md`)
- `CancelEdit` restores settings **in place**; swapping the reference for the
  `BeginEdit` clone loses data (`agents/conventions.md`)
- Progress results are read, not assumed: `Error`, `Canceled` or
  `Result == false` mean "not completed" (`agents/threading.md`)
- Take `_newGameLock` with `using (NewGameLock())`, never `lock (_newGameLock)`:
  the raw form is invisible to the UI-hop guard (`agents/threading.md`)
- Never unit-construct `VrModTagPlugin` with the real registry — a unit test
  would make real network calls (`agents/testing.md`)
- No `Thread.Sleep`, no wall-clock dependence, in tests (`agents/testing.md`)
- A GUI change is not done until its `docs/` page and its `docs/img/` figures
  match the new UI (`agents/contributing.md`)
- No link or path may leave this repo: a clone has nothing outside it
  (`agents/contributing.md`)

## Repo layout

```text
source/                       # The plugin (Playnite.VRMods.csproj, net462, WPF)
├── VrModTagPlugin.cs        # Main plugin class (MetadataPlugin + lifecycle hooks)
├── VrModMetadataProvider.cs # "Download Metadata" flow: GetTags + GetLinks
├── VrModTagService.cs       # Scan, match, tag apply/remove, source-link reconcile
├── VrModTagSettings.cs      # Settings data class + settings view model (ISettings)
├── VrModNameMatcher.cs      # Fuzzy matching (normalize, no-substring, identifier guard)
├── VrModCategories.cs       # Tag-name vocabulary (single source of truth for tag names)
├── VrModEntry.cs            # One source entry (game name, Steam ID, category, deep link…)
├── IVrModSource.cs          # Data source interface
├── VrModSourceRegistry.cs   # Source registration / factory
├── BaseVrModParser.cs       # Shared HTTP fetch + name-match helpers for parsers
├── VrModHelp.cs             # Help button (opens the docs/README.md shipped in the package)
├── WebUrl.cs                # http(s) URL validation helper
├── HttpHostGate.cs          # Per-host request queue, global in-flight cap, crawl-delay spacing
├── RobotsPolicy.cs          # One robots.txt lookup per host per run → Crawl-delay (pace, not path rules)
├── MatchDecision.cs         # Accepted / Rejected / Pending
├── VrModReviewMatchEntry.cs # Review dialog row model
├── VrModReviewDialog.xaml(.cs)    # Review dialog (filter, sort, columns, decisions)
├── ErrorToBrushConverter.cs # WPF converter (source health → brush)
├── Parsers/                 # One parser per source (12 files, see below)
├── Settings/VrModSettingsView.xaml(.cs)  # Settings page (Sources grid + scan settings)
├── Styles/PlayniteTheme.xaml# Playnite-like WPF styles
├── Properties/AssemblyInfo.cs   # InternalsVisibleTo(tests) only (no version here)
├── icon.png                 # Plugin icon
└── extension.yaml           # Playnite manifest (Name, Id, Version)
```

```text
docs/                         # User manual (shipped in the package, Help button target)
├── README.md               # Manual index (links to the pages below)
├── getting-started.md      # First scan, the review dialog, decisions
├── review.md               # Review dialog reference (columns, filters, menus)
├── settings.md             # Settings page reference (Sources grid, scan settings)
├── tags.md                 # Tag vocabulary and link behavior
├── for-developers.md        # Dev setup (devel extensions), troubleshooting
└── img/                    # Figures referenced by the pages
```

```text
agents/                       # This guide's detail, one topic per file
├── build-and-test.md       # Toolchain, the warning rule, how the version is stamped
├── release.md              # .pext rules, local builds, the release procedure
├── ci-and-gates.md         # The workflows, every gate, the lockfile, Dependabot
├── conventions.md          # Behaviour contract, logging, the fragile parsers
├── threading.md            # Leaf locks, the UI-hop guard, published decision maps
├── testing.md              # Test invariants, coverage boundary, live testing
├── analyzers.md            # The code-analysis policy before pushing
└── contributing.md         # Docs/screenshots sync, links stay in repo, file style
```

```text

tests/                      # Playnite.VRMods.Tests.csproj (NUnit + NSubstitute)
├── TestHelpers/FakeItemCollection.cs  # Fake IItemCollection + FakeBufferedUpdate + NullLogger
├── Unit/                   # One fixture per class (service, settings, VM, dialog, …)
├── Parsers/                # One fixture per source + snapshots/ recorded fixtures
└── Integration/IntegrationTests.cs

```

Parsers (in the source ranking from external-references.md; `SteamCuratorParser`
is one file, instantiated twice — once per Steam curator): `PcvrCentralParser`,
`UeVrProfilesParser`, `RototRobotVrModParser`, `MrNlceHubParser`,
`CompoundVrParser`, `MrSurvivorHubParser`, `SteamCuratorParser`,
`ReFrameworkParser`, `Flat2VrParser`, `VorpXParser`, `LukeRossParser`,
`UnflatVrParser`.

## The guide, by file

`agents/build-and-test.md` — the toolchain and the `global.json` pin, the
warning-free rule, how the version reaches the assembly and the manifest.
Read it when building, or touching the csproj, `global.json`, `NuGet.config`.

`agents/release.md` — `.pext` packaging rules, why a local build can never be a
release, the GitHub Actions release procedure and its version arithmetic. Read
it when shipping, or changing `Build-Release.ps1`, a workflow or the manifest.

`agents/ci-and-gates.md` — the two workflows and every gate they run, the
commands to run before opening a PR, the locked restore and its NU1004/NU1403
failures, Dependabot, what a required check really blocks. Read it for any pull
request, and for anything under `.github/`.

`agents/conventions.md` — the behaviour contract: references, persistence,
decision keys, matching rules, source-link ownership, scan guards, logging
levels, the fragile parsers. Read it when changing behaviour in `source/`.

`agents/threading.md` — leaf locks, the UI-hop guard and why it exists, the
copy-on-write decision maps, entry-point guards, matching cost. Read it when
touching service, settings or plugin threading, or when something can deadlock.

`agents/testing.md` — test-suite invariants and the intentional coverage
boundary, plus live testing against a real Playnite install. Read it when
writing tests or verifying against a live instance.

`agents/analyzers.md` — where the analyzers are switched on and why `net462`
needs it spelled out, the 0-warning `Release` gate and its two commands, the
only three ways a warning may legitimately stop being one, the full-sweep
command and why its output is not kept here. Read it before pushing, and when
touching `.editorconfig` severities or a `[SuppressMessage]`.

`agents/contributing.md` — documentation and screenshot sync, the
links-stay-in-repo rule, and how the files in this folder are written. Read it
when changing docs, or adding to or editing this folder.
