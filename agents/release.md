# Release and packaging

> Part of [AGENTS.md](../AGENTS.md).

- **Release packaging (`.pext`)** — `tools/Build-Release.ps1`. Requirements
  it enforces: Release publish; **`Playnite.SDK.dll` must NOT be in the
  package** (the host provides it — Playnite's `VerifyExtensionPackage` rejects
  packages containing it); manifest + module + icon present; no `Playnite`/
  `Playnite.Common` assembly references; `docs/` (the user manual incl.
  its images) is shipped into the package (csproj `<Content>` copy) so the
  installed extension's Help button opens a version-matched manual (Playnite
  extracts every package file into the extension dir and wipes the whole
  dir on uninstall, so no manifest declaration is needed). The 10.x package
  extension is `.pext` (not `.ext` — that is 9.x). Install: double-click or
  `Playnite.DesktopApp.exe --installext <path>`; applied on next startup.
- **Building locally (never a release)** — `tools/Build-Release.ps1` with no
  version argument. It reads the release tags from `origin` first (`git fetch
  --tags`; GitVersion computes from the tags the clone holds at publish time, so
  tags fetched afterwards are too late to change the number they are meant to
  produce), then packages the git-derived version with a test-only 4th part
  (`1.2.0` → `1.2.0.1`) into `dist\local\`. A release is always 3-part
  (GitVersion's `MajorMinorPatch`), so that number can never name a `vX.Y.Z` tag
  or a CI asset, and `dist\` stays the workflow's directory — a local build
  cannot be mistaken for a release, or attached to one, by forgetting a flag.
  It also refuses a version at or below what `origin` already released: Playnite
  extracts every `.pext` into `Extensions\<Id>` in place and does not compare
  versions first, so an older-labelled build silently downgrades a newer
  install. `-Offline` skips the fetch and guards against this clone's own tags;
  a clone with no `origin` remote does the same and says so. `-Version <x>` is
  the workflow's contract and the re-cut escape: it names the version, skips the
  fetch and the guard, and writes into `dist\` — but it must match the number
  git already stamped into the published DLL. Measured in a scratch clone whose
  HEAD sits one commit past `v1.1.0`: `-Version 9.9.9` refuses and writes no
  package at all (`refusing to package 9.9.9: the Playnite.VRMods.dll inside is
  stamped 1.1.1, and this script does not stamp binaries`), and so does
  `-Version 1.1.0` there, because the label names an older release than the
  commit being packaged. `-AllowStampMismatch` packages that split knowingly and
  says so in a warning. Tag HEAD first and the numbers agree: the same clone at
  `-Version 1.1.1` after `git tag v1.1.1` packaged with no warning, which is the
  order the workflow uses. Version-shaped tags are the
  release namespace, so a tag that is not exactly `vX.Y.Z` stops the build
  instead of being filtered out: `git describe --tags --match v*` (which is how
  the workflow picks its next version) and GitVersion read tags by different
  patterns, and neither match is what this script counts (measured on 6.8.2:
  `v1.2.0-beta1` counts as 1.2.0, `v1.2.0.1` counts as nothing, a bare `2.0.0`
  counts for GitVersion while describe sees no tag - so the workflow would call
  the next release 1.0.0). `-Offline` with `-Version` does nothing and says so;
  `-DistDir` names a folder without making the build release-shaped.
- **Releasing (GitHub Actions)** — `.github/workflows/release.yml` runs
  on every push to main on `windows-latest` (net462/WPF builds only on
  Windows): computes the next version from the Conventional Commits since
  the last tag (config in `GitVersion.yml`; measured on GitVersion 6.8.2,
  not inferred from the regexes: one increment per push, patch is the
  floor — a chore-only push still ships the next patch — and `feat:`→minor
  or `<type>!:`/`BREAKING CHANGE:`→major escalate it; first push without
  any tag→1.0.0), creates+pushes the tag `v<version>`, runs the full
  test suite, packages with `tools/Build-Release.ps1 -Version <version>`,
  and attaches `dist/VRMod.Tags-<version>.pext` to the GitHub Release.
  The asset is dotted, not spaced: GitHub rewrites a space in an asset's
  download name (v1.1.0 was uploaded as `VRMod Tags-1.1.0.pext`, its label
  kept the space, its name and URL became `VRMod.Tags-1.1.0.pext`), so
  file, asset and URL are named the same way only if the file is spelled
  that way first. `fail_on_unmatched_files: true` is set because the
  action's default is to publish anyway when the `files:` glob matches
  nothing. **Every push to main is a release** — no prereleases; feature
  branches are built locally, never released. **`main` is not pushable.** Any
  client, any account: `! [remote rejected] main -> main (push declined due to
  repository rule violations)`, "Changes must be made through a pull request".
  So the procedure is: branch as `type/kebab-description`, open a PR, let the
  gates run, merge. The gate offers **squash or rebase only** - no merge
  commit - and a rebase rewrites the SHAs that landed, so keep the branch you
  merged until your local `main` is re-synced onto the remote one. The prefix
  of each commit subject still decides whether the release
  escalates (a `feat:` mislabeled as
  `fix:`/`chore:` ships a feature inside a patch release; the
  Conventional-Commits gate in the `review-gates` job of
  `.github/workflows/ci.yml` catches non-conventional
  headers). Commit messages stay plain Conventional Commits - no ticket
  or ticket-like code in the type, the scope, or before the colon; the
  gate rejects anything shaped that way and the version rules read only
  the type. What the team calls STE-100 (ASD-STE100 Simplified
  Technical English) is for prose addressed to people - docs, PR
  bodies, agent reports: short active sentences, one idea per
  sentence, plain words. Manual re-runs: "Run workflow" with an optional explicit
  version, which has to be 3-part (a 4-part value would tag a version
  `git describe` matches but GitVersion does not count). Before resolving
  anything the workflow refuses a repo holding a version-shaped tag that is
  not exactly `vX.Y.Z`, since describe and GitVersion would then disagree
  about what is already released. The release body is written by the
  `Build release notes` step from the commit subjects since the previous
  tag (grouped New features / Fixes / Other, prefixes stripped), passed
  as `body_path`. Without it the body was GitHub's auto-generated notes
  alone, which for this repo is only the compare link — measured identical
  on the v1.0.0, v1.0.1, v1.0.2 and v1.1.0 releases. The action prepends
  that body to the notes it still generates, and the resolve step also
  outputs `previous_tag` — the tag this release follows (on a re-run of an
  already-tagged version it steps one commit back to the release before it)
  — so the compare link GitHub adds names the same base this job resolved
  rather than guessing one. Users install from the repo's Releases page —
  [github.com/evoc/playnite-vrmods-tag-plugin](https://github.com/evoc/playnite-vrmods-tag-plugin).
  The stable latest-asset URL appends
  `/releases/latest/download/VRMod.Tags-<version>.pext` to the
  repository URL above. Measured against v1.1.0: that form is
  200, the spaced form `…/download/VRMod%20Tags-1.1.0.pext` is
  404 — every asset since v1.0.0 has been the dotted name, so
  the spaced recipe these docs used to give has never worked.
