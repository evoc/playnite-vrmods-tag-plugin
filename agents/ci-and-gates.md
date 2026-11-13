# CI gates and supply chain

> Part of [AGENTS.md](../AGENTS.md).

Two workflows, both SHA-pinned (a tag is a moving ref anyone can
re-point; a SHA pin is what makes "the action we ran" mean something),
pinned to the **commit the tag points at** rather than to the tag object:
softprops/action-gh-release v3.0.3 is an annotated tag whose object SHA
(e598afbe…) differs from the commit it labels (efb35369…), and a
tag-object SHA is not a valid `uses:` ref. `ci.yml` runs at
`permissions: contents: read`; `release.yml` runs at `contents: write`
because it pushes the version tag it creates:

- `.github/workflows/ci.yml` (pull request), job `review-gates`, in the
  order the steps run: locked restore -> Conventional-Commits gate ->
  `dotnet format whitespace|style --verify-no-changes` gate
  (`--verify-changes` is not a flag in SDK 8; `--verify-no-changes` is the
  real one) -> vulnerable-package gate -> code-analysis gate -> test gate.
  The two build-running gates pass `-p:DisableGitVersionTask=true`: an
  assembly version stamp has no place in a review, and on the PR merge ref
  (shallow, no tags, branch name `pull/N/merge`) GitVersion 6.8.2 calls
  the branch orphaned and throws `No base versions determined` (measured
  on run 42034123457) - version calculation is the release job's job,
  where `main` and its tags are fetched first.
  Every PR gate lives in this one job, on the commit GitHub actually built
  for the PR (`github.sha`, the merge commit), so what gets reviewed is the
  tree that would be merged: a former split into two jobs meant the message
  and format gates judged the PR tip while the analyzer gate, the tests and
  the shipped artifact judged the merge commit, which is a different tree
  whenever base and head have diverged (measured from this repo's own run
  records for PR #6: 9268491d193b versus 2a28664a93e1).
- `.github/workflows/release.yml` (push to main), in the order the steps
  run: fetch history by named refspec -> vulnerable-package gate (both
  projects) -> code-analysis gate -> test gate -> resolve version + tag ->
  `Build-Release.ps1` -> GitHub release with the `.pext` attached and its
  SHA-256 in the body. The gates run **before** the tag is created: the tag
  is the version, so a tag pushed before a gate has run has already consumed
  that version number, named a commit that was never shipped, and become the
  base the next release measures its version and its notes against. The tag
  is still made before `Build-Release.ps1` runs: that script gives
  `dotnet publish` no version argument, so the only version the shipped DLL
  can report is one read from the tags reachable from HEAD, and the script
  compares that stamp against the package label. Version from Conventional
  Commits via GitVersion
  (`GitVersion.MsBuild` pinned to exactly `6.8.2`: a `6.*` range lets an
  upstream major silently change how the *version number itself* is
  computed). `GitVersion.Tool` is installed at the same pin and the step
  asserts the version that actually landed, because a tool install is outside
  the project graph and the vulnerable-package audit never sees it.

**Before opening a PR.** Every command below reproduces one `review-gates`
step; run them from the repository root in this order and each must come back
clean.

```text
dotnet restore Playnite.VRMods.sln --locked-mode
dotnet format whitespace --verify-no-changes --no-restore
dotnet format style --verify-no-changes --no-restore
dotnet build source/Playnite.VRMods.csproj -c Release -v n
dotnet test tests/Playnite.VRMods.Tests.csproj -c Release --no-restore
```

The locked restore is the one people skip: a package reference the lock file does
not name fails the gate with NU1004, and the fix is to run a plain `dotnet restore`,
then commit the regenerated `source/packages.lock.json` and `tests/packages.lock.json`
onto the PR branch. NU1403 (a package no longer matching its recorded hash) is not
fixable that way - it is the supply-chain signal, and a human has to read the package
first.

The test gate adds `-p:DisableGitVersionTask=true` because the PR merge ref is shallow,
tagless and named `pull/N/merge`, where GitVersion calls the branch orphaned; locally
your clone has tags and needs no such flag.

Two gates have no local command. The vulnerable-package audit runs, per
project, `dotnet list <project> package --vulnerable --include-transitive
--format json` and fails closed when an audit names no project - an unaudited
graph is not a pass. The Conventional-Commits gate is inline pwsh in `ci.yml`:
header shape `<type>(scope)!: message`, the type list printed by the step on
failure. Nothing here checks your commit messages before you push, and a wrong
type is not a style slip - the type decides the released version.

Neither workflow uses `fetch-depth: 0` (checkout documents that as all
history for all branches and tags, and on a `pull_request` event the
repository also carries `refs/pull/<n>/head`, a ref whose commits come from
the PR author's repository). Each names the refs or commit ids it reads and
fetches those, and `release.yml` refuses to run unless its branch name
matches the literal `main` it releases from.

Restore is locked: `Directory.Build.props` sets `RestorePackagesWithLockFile`
for both projects (`source/packages.lock.json`, `tests/packages.lock.json`,
both committed) and turns `RestoreLockedMode` on when
`ContinuousIntegrationBuild` is on. Measured on SDK 8.0.427: a pin the lock
file does not name fails with NU1004 (`The package reference NUnit version
has changed from [4.2.2, ) to [4.99.0, )`), and a lock entry whose recorded
sha512 no longer matches the package fails with NU1403 (`Package content
hash validation failed for GitVersion.MsBuild.6.8.2. The package is
different than the last restore`). `NuGet.config` names one feed
(nuget.org) after a `clear` of inherited machine and user config; note the
XML trap found while writing it — a comment containing two consecutive
hyphens makes the whole file invalid and breaks every restore.

Both csproj files pin `Microsoft.NETFramework.ReferenceAssemblies`
1.0.3 explicitly: a machine without the net462 targeting pack gets that
package injected by the SDK, a machine with it installed does not, so
the two kinds of machine otherwise evaluate different project package
references against the same committed lock file - which is exactly how
the first release run after the lock files landed failed with NU1004 on
windows-latest while the identical build passed on a dev machine.

The `review-gates` restore answers the two locked-restore failures
differently: NU1403 fails hard with no regeneration, because a package's
content no longer matching its recorded entry is the supply-chain
signal, and someone has to review the package before any lock file
changes; NU1004 - a version reference without its lock refresh -
regenerates the lock files in the runner's throwaway checkout, prints
the exact diff, and fails, so a human commits that diff onto the PR
branch. NU1004 is the everyday case because Dependabot opens NuGet
bump PRs without regenerating lock files (open upstream issue), see
below.

`.github/dependabot.yml` opens weekly PRs for `github-actions`, `/source`
and `/tests` NuGet (majors refused; every NuGet bump PR needs the
refreshed lock file printed by the restore step before it can go
green). PlayniteSDK bumps are refused outright
because that version is the user's Playnite install, not this repo's build
choice (the SDK assemblies are stripped from the shipped `.pext`), while its
advisories stay reachable: an `ignore` rule that names `update-types` filters
version updates only, not security updates. `SECURITY.md` is the
vulnerability-report channel.

Branch protection has to name the checks for any of this to block a merge, and
the name must match what the job reports: a required check for a name that no
job ever posts (the renamed `commitlint` job, or the release job while it was
called `build`) blocks nothing, because a required check can only be required
once it has reported on a commit at least once. A merge to `main` waits for every
required check, whatever produces it: some are configured outside `.github/workflows`.
A required check that never reports blocks every PR, so a silent one is treated
as a broken gate and repaired at once.

- Analyzers: the posture and the policy are in [analyzers.md](analyzers.md).
