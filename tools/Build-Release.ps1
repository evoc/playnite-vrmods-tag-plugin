# Build the VRMod Tags release package (.pext).
#
# A .pext is a ZIP that Playnite installs (double-click it, or:
#   Playnite.DesktopApp.exe --installext <path>).
# Playnite (ExtensionInstaller.VerifyExtensionPackage / InstallPackedExtension):
#   - reads extension.yaml from the ZIP root (non-empty Id, parseable Version)
#   - rejects the package if it contains Playnite.dll / Playnite.Common.dll /
#     Playnite.SDK.dll entries (the host process provides the SDK - the host's
#     copy is what the plugin actually binds against)
#   - rejects packages containing .sln entries
#   - extracts everything to %LOCALAPPDATA%\Playnite\Extensions\<extension Id>\
#     (an existing directory = in-place update)
#
# This script: clean publish -> strip the Playnite.SDK.dll that dotnet publish
# copies -> verify the constraints above -> zip the folder contents to the ZIP
# root -> re-open the produced zip and verify it the same way Playnite does.
#
# The script lives in the repo (tools/), so the repo root resolves from the
# script location; -RepoRoot overrides that. The release workflow runs it in
# place (./tools/Build-Release.ps1) and relies on the default - it passes no
# -RepoRoot.
#
# Version resolution (step 3b) - which version the package claims:
#   -Version X        exactly X, 3-part, written to dist\. No tag sync and no
#                     version guard: this is the release workflow's path (it
#                     resolves the number itself and tags HEAD with it before it
#                     runs this script) and the escape hatch for a deliberate
#                     re-cut. -Version with an empty value is an error. The
#                     label must match the number git already stamped into the
#                     binary; -AllowStampMismatch packages it anyway.
#   otherwise         the version GitVersion.MsBuild already stamped on the
#                     published assembly - the same git-derived number the
#                     release workflow tags - read off the artifact instead of
#                     recomputed by a second GitVersion install. Every build
#                     that is not -Version is a TEST build: that number with a
#                     4th part (1.2.0 -> 1.2.0.1), written to dist\local.
#
# A release is always 3-part (GitVersion's MajorMinorPatch), so a 4th part can
# never name a v-tag or a CI release asset, and dist\ is where the release
# workflow writes. A local build therefore cannot be mistaken for a release -
# not by forgetting a flag, because there is no flag to remember. Making a
# release is the workflow's job: merge to main.
#
# Without -Version the script reads the release tags before it builds (step 0:
# git fetch --tags origin, then origin's tag set) and refuses a version at or
# below what is already released, so a stale checkout cannot silently downgrade
# an installed extension. -Offline skips the fetch and guards against this
# clone's own tags; -Version skips both.
#
# The v-tags are the release namespace, so a v-tag that is not exactly vX.Y.Z
# stops the build instead of being filtered out: 'git describe --tags --match
# v*' matches it, which is how the release workflow picks its version, and
# GitVersion counts some of them as a version this script cannot. -Offline with
# -Version is a no-op and says so; -DistDir names a folder without making a
# build release-shaped.

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$DistDir = "",
    [string]$RepoRoot = "",
    [string]$Version = "",
    [switch]$Offline,
    [switch]$AllowStampMismatch
)

$ErrorActionPreference = "Stop"

# One place decides whether a version was named by hand. -Version means "use
# exactly this number": no tag sync, no version guard (the release workflow, a
# deliberate re-cut). Every other build takes its version from git and is
# guarded. An empty -Version is an error rather than a silent fall through to
# the guarded path, which is what makes the two paths unambiguous.
$explicitVersion = $PSBoundParameters.ContainsKey("Version")
if ($explicitVersion -and -not $Version.Trim()) {
    throw "-Version was passed without a value: pass -Version <major.minor.patch>, or omit it and take the version from git"
}
if ($explicitVersion -and $Offline) {
    Write-Warning "-Offline has no effect with -Version: that path fetches no tags, checks no tag set, and runs no version guard"
}

# The version GitVersion.MsBuild stamped into a published assembly.
function Get-StampedVersion([string]$path) {
    # FileVersion is the 4-part stamp (measured on a local publish: 1.1.0.0).
    # ProductVersion carries the informational semver with GitVersion's distance
    # and sha suffix (1.1.0-3+Branch.main.Sha...), so only its leading
    # major.minor.patch is usable. FileVersionInfo reads the VERSIONINFO resource;
    # nothing is loaded into this process.
    if (-not (Test-Path $path)) { throw "staged module '$path' missing, cannot read its version stamp" }
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    $v = $null
    if ($info.FileVersion -and [Version]::TryParse($info.FileVersion, [ref]$v)) {
        return [Version]::new($v.Major, $v.Minor, [Math]::Max($v.Build, 0))
    }
    if ($info.ProductVersion -and $info.ProductVersion -match '^\s*(\d+\.\d+\.\d+)') {
        $p = $null
        if ([Version]::TryParse($Matches[1], [ref]$p)) { return $p }
    }
    $leaf = Split-Path $path -Leaf
    throw "could not read a version stamp from '$leaf' (FileVersion='$($info.FileVersion)', ProductVersion='$($info.ProductVersion)')"
}

# The release tags of a repository, split into the ones these rules can count
# (exactly vX.Y.Z) and the ones they cannot, as @{ Tags = name -> Version map;
# OffScheme = tag names }. $null means the tag set could not be read at all; an
# empty Tags map means it was read and holds no release tag (nothing has been
# released yet). Those two states have to stay apart, because "origin has no
# release tags" is an answer while "origin could not be read" is not - and a
# local tag set must never be allowed to impersonate origin's.
# Off-scheme tags are reported, never dropped: they are part of the release line
# for git describe and/or GitVersion even when this script cannot count them.
# Read from origin (-Remote) or from this clone. The origin reading is the one
# that matters: a push does not hand back the tag the release runner created
# afterwards (measured on this clone: local tags were v1.0.0 + v1.0.1 while
# origin had v1.0.2, so anything anchored on the local set sat one whole release
# behind).
function Get-ReleaseTagSet([string]$root, [switch]$Remote) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"          # git may write to stderr on success
    $lines = if ($Remote) { git -C $root ls-remote --tags origin 2>&1 } else { git -C $root tag --list 2>&1 }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($code -ne 0) { return $null }
    $tags = [ordered]@{}
    $offScheme = @()
    foreach ($line in @($lines)) {
        $entry = "$line".Trim()
        if ($Remote) {
            if ($entry -notmatch '\srefs/tags/(\S+)$') { continue }
            $name = ($Matches[1] -replace '\^\{\}$', '')     # peeled tags: v1.0.2^{}
        } else {
            if ($entry -notmatch '^(\S+)$') { continue }
            $name = $Matches[1]
        }
        # Version-shaped tags belong to the release line even without the v prefix:
        # GitVersion counts a bare 2.0.0 as a released version while 'git describe
        # --tags --match v*' sees no tag at all (measured on 6.8.2). Tags that are
        # not version-shaped at all (backup-2026) are outside the release line and
        # are ignored, which is also what GitVersion does with them.
        if ($name -notmatch '^v?\d+\.\d+') { continue }
        $v = $null
        if ($name -notmatch '^v\d+\.\d+\.\d+$' -or -not [Version]::TryParse($name.Substring(1), [ref]$v)) {
            $offScheme += $name
            continue
        }
        $tags[$name] = $v
    }
    return @{ Tags = $tags; OffScheme = $offScheme }
}

# Highest version in a Get-ReleaseTagSet map; $null when the map is empty (nothing
# released) - the caller keeps $null itself for a map that could not be read.
function Get-MaxTagVersion($tags) {
    if ($null -eq $tags -or $tags.Count -eq 0) { return $null }
    $best = $null
    foreach ($v in $tags.Values) { if (-not $best -or $v -gt $best) { $best = $v } }
    return $best
}

# The clone's remote named origin, or $null when there is none - a repo copied
# from an archive, a fork-less export. Without one, the tag checks below can only
# see tags this clone already holds, which may sit behind what was really
# released, and that has to be said out loud instead of assumed away.
function Get-OriginUrl([string]$root) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $url = git -C $root remote get-url origin 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($code -ne 0) { return $null }
    $url = ("$url").Trim()
    if (-not $url) { return $null }
    return $url
}

# The message for a release tag these rules cannot count. It is a refusal, not a
# warning, because the tag is not harmless: the release workflow resolves its
# next version through 'git describe --tags --match v*', which matches it, while
# GitVersion reads it differently (measured on 6.8.2: v1.2.0-beta1 is counted as
# 1.2.0 and v1.2.0.1 is not counted at all, so a 4-part release leaves the
# workflow free to publish 0.1.0 under a version already numbered 1.2.0.1).
function Get-OffSchemeMessage([string[]]$names, [string]$anchor) {
    $msg = "release tag(s) outside the vX.Y.Z scheme on $anchor : " + ($names -join ", ") + ". "
    $msg += "This build stops rather than ignore them. Such a tag is not inert: the release workflow " +
            "picks its version from 'git describe --tags --match v*', and GitVersion reads tags by its " +
            "own pattern, so the two disagree here. Measured on GitVersion 6.8.2: v1.2.0-beta1 is counted " +
            "as 1.2.0, v1.2.0.1 is not counted at all, and a bare 2.0.0 is counted by GitVersion while " +
            "describe sees no tag - the workflow then labels the next release 1.0.0, under a version that " +
            "already exists. Releases here are 3-part with a v prefix: delete or rename the tag, or pass " +
            "-Version <major.minor.patch> to name the version by hand and skip these checks."
    return $msg
}

# Bring the tag set current *before* the build. GitVersion.MsBuild computes the
# version from the local tag set during the publish, so tags fetched afterwards
# would be too late to change the stamp they are supposed to produce.
function Sync-RepoTagsFromOrigin([string]$root) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"          # git writes progress to stderr even on success
    $out = git -C $root fetch --tags origin 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    $text = ((@($out) | ForEach-Object { "$_".Trim() } |
        Where-Object { $_ -and $_ -notmatch 'RemoteException|CategoryInfo|FullyQualifiedErrorId|\+ ~' }) | Select-Object -Last 6) -join " | "
    if ($code -ne 0) {
        $msg = "git fetch --tags origin failed (exit {0}): {1}" -f $code, $text
        $msg += " Refusing to build a version off a tag set that could not be read: a stale or " +
                "colliding tag makes the computed version wrong, not merely unverified. A collision " +
                "('would clobber existing tag') is cleared with 'git tag -d v<version>'. To build " +
                "offline instead, pass -Offline (the guard then runs against this clone's own tags); " +
                "to name the number yourself, pass -Version <version> - that path skips both the " +
                "fetch and the guard."
        throw $msg
    }
    return $text
}
$repoRoot = if ($RepoRoot) { (Resolve-Path $RepoRoot).Path } else { (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }
$proj = Join-Path $repoRoot "source\Playnite.VRMods.csproj"
$staging = Join-Path $env:TEMP ("vrmodtags-release-" + [Guid]::NewGuid().ToString("N"))
# dist\ is the release workflow's output directory (its release step attaches
# dist\VRMod.Tags-<version>.pext). A build that is not -Version goes beside it.
$DistDir = if ($DistDir) { $DistDir } elseif ($explicitVersion) { Join-Path $repoRoot "dist" } else { Join-Path $repoRoot "dist\local" }
if (-not $explicitVersion -and $PSBoundParameters.ContainsKey("DistDir")) {
    Write-Warning "-DistDir names the output folder only: without -Version this is still a test build (4th version part), so a folder you picked in order to attach something is the wrong place for it"
}

try {
    # 0. Read what has already been released, before anything is built. This has
    #    to precede the publish: the version GitVersion.MsBuild stamps is computed
    #    from the tags this clone has at publish time. Skipped by -Version, which
    #    names its own number.
    #      $releasedKnown - a tag set was actually read (it may be empty: nothing
    #                       released yet, which is an answer, not a failure)
    #      $releasedMax   - highest vX.Y.Z in it
    #      $anchorName    - whose tags those are, stated so a local fallback is
    #                       never read as if it were origin
    $releasedMax = $null
    $releasedKnown = $false
    $anchorName = ""
    if (-not $explicitVersion) {
        $originUrl = Get-OriginUrl $repoRoot
        $readFromOrigin = ($null -ne $originUrl) -and -not $Offline

        if ($null -eq $originUrl) {
            Write-Warning ("no 'origin' remote in this clone: the release checks can only use the tags stored here, which may sit behind what was actually released. Add the remote, or pass -Version to name the version yourself")
            $anchorName = "this clone (no origin remote)"
        } elseif ($Offline) {
            Write-Warning "-Offline: tags were not fetched from origin, so the release checks use this clone's own tag set, which may be stale"
            $anchorName = "this clone (-Offline)"
        } else {
            $null = Sync-RepoTagsFromOrigin $repoRoot
            $anchorName = "origin"
        }

        $localSet = Get-ReleaseTagSet $repoRoot
        if ($readFromOrigin) {
            $originSet = Get-ReleaseTagSet $repoRoot -Remote
            if ($null -ne $originSet) {
                $releasedKnown = $true
                $offScheme = @()
                $offScheme += $originSet.OffScheme
                if ($null -ne $localSet) { $offScheme += $localSet.OffScheme }
                $offScheme = @($offScheme | Select-Object -Unique)
                if ($offScheme.Count -gt 0) { throw (Get-OffSchemeMessage $offScheme $anchorName) }

                # Tags that exist here and not on origin, matched by name rather
                # than by highest version: a stale tag sitting between two
                # released ones is just as wrong, and comparing only the two
                # maxima cannot see it.
                $localNames = if ($null -eq $localSet) { @() } else { @($localSet.Tags.Keys) }
                $localOnly = @($localNames | Where-Object { -not $originSet.Tags.Contains($_) })
                if ($localOnly.Count -gt 0) {
                    $msg = "tag(s) " + ($localOnly -join ", ") + " exist in this clone but not on origin. "
                    $msg += "A tag created locally and never pushed makes GitVersion compute - and this " +
                            "script stamp - a version that skips a release. Delete them: " +
                            (($localOnly | ForEach-Object { "git tag -d $_" }) -join "; ")
                    throw $msg
                }
                $releasedMax = Get-MaxTagVersion $originSet.Tags
            }
        } elseif ($null -ne $localSet) {
            $releasedKnown = $true
            if (@($localSet.OffScheme).Count -gt 0) { throw (Get-OffSchemeMessage $localSet.OffScheme $anchorName) }
            $releasedMax = Get-MaxTagVersion $localSet.Tags
        }

        $highest = if ($releasedMax) { "v$releasedMax" } elseif ($releasedKnown) { "none yet" } else { "unreadable" }
        Write-Host ("Release tags read from {0}: highest {1}" -f $anchorName, $highest)
    }

    # 1. Clean publish into a fresh staging directory.
    New-Item -ItemType Directory -Path $staging | Out-Null
    dotnet publish $proj -c $Configuration -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    # 2. Strip the host-provided Playnite assemblies (dotnet publish copies the SDK).
    foreach ($forbidden in @("Playnite.dll", "Playnite.Common.dll", "Playnite.SDK.dll")) {
        $p = Join-Path $staging $forbidden
        if (Test-Path $p) { Remove-Item $p }
    }

    # 3. Read Id / Version / Module from the staged manifest.
    $yamlPath = Join-Path $staging "extension.yaml"
    if (-not (Test-Path $yamlPath)) { throw "extension.yaml missing from publish output" }
    $yaml = Get-Content $yamlPath -Raw
    if (-not ($yaml -match "(?m)^\s*Id:\s*(\S+)\s*$")) { throw "extension.yaml: missing or blank Id" }
    $extId = $Matches[1]
    if (-not ($yaml -match "(?m)^\s*Version:\s*(\S+)\s*$")) { throw "extension.yaml: missing or blank Version" }
    $manifestVersion = $Matches[1]
    if (-not ($yaml -match "(?m)^\s*Module:\s*(\S+)\s*$")) { throw "extension.yaml: missing or blank Module" }
    $module = $Matches[1]

    # 3b. Resolve which version this package claims, then stamp the manifest.
    # The stamp is read even when -Version is given, so the manifest and the
    # binary beside it can be compared. It is only *required* when -Version is
    # absent: an explicit -Version (the release workflow, a deliberate re-cut)
    # still builds if the stamp cannot be read, exactly as it did before.
    $assemblyStamp = $null
    try {
        $assemblyStamp = Get-StampedVersion (Join-Path $staging $module)
    } catch {
        if (-not $explicitVersion) { throw "version unresolved: $($_.Exception.Message)" }
        Write-Warning ("assembly version unreadable ({0}); -Version is used, and the label check below cannot compare the two" -f $_.Exception.Message)
    }
    $resolved = $null
    $versionSource = ""
    $isTestBuild = $false
    if ($explicitVersion) {
        if (-not [Version]::TryParse($Version, [ref]$resolved)) {
            throw "-Version '$Version' is not a parseable .NET Version"
        }
        $versionSource = "-Version, named by hand (no tag sync, no version guard)"
    } else {
        # The version git says this commit is, plus a 4th part. A release is
        # always 3-part (GitVersion's MajorMinorPatch), so 1.2.0.1 can never name
        # a v-tag or a CI release asset: a build off a working branch, a build of
        # a commit that is already released, and a build of a stale checkout are
        # all unmistakable from the number alone.
        $resolved = [Version]::new($assemblyStamp.Major, $assemblyStamp.Minor, $assemblyStamp.Build, 1)
        $versionSource = "assembly stamp (GitVersion.MsBuild, from git) + a test-only 4th part"
        $isTestBuild = $true
    }
    $versionText = "{0}.{1}.{2}" -f $resolved.Major, $resolved.Minor, $resolved.Build
    if ($isTestBuild) { $versionText = "$versionText.$($resolved.Revision)" }

    # A hand-named -Version must match the number already stamped into the
    # binary. This script never tells the compiler a version: dotnet publish is
    # given no version argument, so the assembly's stamp comes from git tags and
    # from nothing else (measured on this project: FileVersion 1.1.0.0,
    # ProductVersion 1.1.0+Branch.main.Sha.<commit>). The release workflow stays
    # consistent because it tags HEAD before it runs this script. A -Version the
    # tags do not name would wrap a DLL stamped one version in a package labelled
    # another - the drift that once put a 1.1.0 assembly in a package marked
    # 1.0.0 - and the two numbers then answer from different places: the release
    # page and manifest say one, the file's own version properties and any crash
    # report say the other. That is refused here. -AllowStampMismatch packages it
    # knowingly.
    if ($explicitVersion) {
        if (-not $assemblyStamp) {
            if (-not $AllowStampMismatch) {
                throw ("refusing to package {0}: the version stamped in {1} could not be read, so the " -f $versionText, $module) +
                      "package label and the binary inside it cannot be compared. Pass -AllowStampMismatch " +
                      "to package without that comparison."
            }
        } else {
            $stampedText = "{0}.{1}.{2}" -f $assemblyStamp.Major, $assemblyStamp.Minor, $assemblyStamp.Build
            if ($stampedText -ne $versionText) {
                if ($AllowStampMismatch) {
                    Write-Warning ("-AllowStampMismatch: packaging {0} around a {1} stamped {2}" -f $versionText, $module, $stampedText)
                } else {
                    $msg = "refusing to package {0}: the {1} inside is stamped {2}, and this script does not " -f $versionText, $module, $stampedText
                    $msg += "stamp binaries - dotnet publish gets no version argument, so the assembly's " +
                            "version comes from the git tags reachable from HEAD. A -Version those tags do " +
                            "not name ships a package labelled $versionText around a DLL that reports " +
                            "$stampedText in its own file properties and in crash reports. Tag the commit " +
                            "you mean to release (git tag v$versionText, then git push origin v$versionText) " +
                            "and build again, or pass -AllowStampMismatch to package the split knowingly."
                    throw $msg
                }
            }
        }
    }

    # Refuse to build a version at or below what has already been released.
    # Playnite extracts every .pext into Extensions\<Id> in place and does not
    # compare versions first, so an older-labelled build silently downgrades a
    # newer install. The 4th part alone does not cover a stale checkout: HEAD
    # behind origin reaches a stamp lower than the release that already went out,
    # and 1.0.3.1 still looks newer than 1.0.3 while sitting under a released
    # 1.1.0. -Version is not guarded: the release workflow names the number it
    # resolved from its own tag, and a deliberate re-cut is meant to be allowed.
    if (-not $explicitVersion) {
        if (-not $releasedKnown) {
            Write-Warning "release tags unreadable ($anchorName); version guard skipped"
        } elseif (-not $releasedMax) {
            # Measured on a tag-less clone: GitVersion stamps 0.1.0 when there is
            # no tag to count from, while the release workflow names a first
            # release 1.0.0 itself. The numbers only line up once a tag exists.
            Write-Host ("  note          : no vX.Y.Z release tag on {0} yet, so there is nothing to stay above; with no tags GitVersion stamps {1}.{2}.{3}, and the release workflow labels a first release 1.0.0" -f $anchorName, $assemblyStamp.Major, $assemblyStamp.Minor, $assemblyStamp.Build)
        } elseif ($resolved -le $releasedMax) {
            $msg = "refusing to build as {0}: v{1} is already released ({2} was read just now, which " -f $versionText, $releasedMax, $anchorName
            $msg += "includes anything fetched from it). This build resolved to " + $versionText + ", which " +
                    "is not higher than that - the checkout is behind what has already been released, or " +
                    "is exactly that release. Update the checkout (git fetch + git pull) and build again, " +
                    "or pass -Version <version> to name the version yourself (that skips the fetch and " +
                    "the guard)."
            throw $msg
        } elseif ($assemblyStamp -le $releasedMax) {
            # Allowed, but worth saying out loud: this commit has already been
            # released as a 3-part version, and installing this build replaces it.
            Write-Host ("  note          : this commit is already released as v{0}; the test build ({1}) installs over it" -f $releasedMax, $versionText)
        }
    }

    # Stamp the staged manifest, then re-read it: the manifest must end up
    # agreeing with the binary that ships beside it.
    if ($versionText -ne $manifestVersion) {
        $yamlText = [regex]::Replace(
            $yaml,
            "(?m)^(\s*Version:\s*)\S+(\s*)$",
            { param($m) $m.Groups[1].Value + $versionText + $m.Groups[2].Value }
        )
        [System.IO.File]::WriteAllText($yamlPath, $yamlText)
        $yaml = Get-Content $yamlPath -Raw
    }
    if (-not ($yaml -match "(?m)^\s*Version:\s*(\S+)\s*$") -or $Matches[1] -ne $versionText) {
        throw "staged manifest does not carry the resolved version '$versionText' after stamping"
    }
    $packageVersion = $Matches[1]
    $parsedVersion = $null
    if (-not [Version]::TryParse($packageVersion, [ref]$parsedVersion)) {
        throw "extension.yaml: Version '$packageVersion' is not a parseable .NET Version"
    }

    # 4. Verify module + icon exist and no forbidden entry is left anywhere in staging.
    foreach ($need in @($module, "icon.png")) {
        if (-not (Test-Path (Join-Path $staging $need))) { throw "publish output missing $need" }
    }
    $forbiddenLeft = Get-ChildItem $staging -Recurse -File |
        Where-Object { $_.Name -eq "Playnite.dll" -or $_.Name -eq "Playnite.Common.dll" -or $_.Name -eq "Playnite.SDK.dll" -or $_.Extension -eq ".sln" }
    if ($forbiddenLeft) {
        throw ("forbidden package entries left in staging: " + ($forbiddenLeft.Name -join ", "))
    }

    # 5. Zip the folder contents to the ZIP root (CreateFromDirectory = no leading
    #    folder, forward-slash entry names - what Playnite's ZipFile.OpenRead expects).
    if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir | Out-Null }
    # The stem is dotted, not spaced: GitHub rewrites a space in an asset's
    # download name (the v1.1.0 upload kept the label "VRMod Tags-1.1.0.pext"
    # but its name, and therefore its URL, became VRMod.Tags-1.1.0.pext), so a
    # spaced file never downloads under the name the build reports. Measured on
    # every release since v1.0.0: the %20 form 404s, the dotted name 200s.
    # Naming the file the way the URL spells it keeps one spelling everywhere.
    $pkgName = "VRMod.Tags-$packageVersion.pext"
    $pkg = Join-Path $DistDir $pkgName
    if (Test-Path $pkg) { Remove-Item $pkg }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $pkg)

    # 6. Verify the produced package the way Playnite does.
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Convert-Path $pkg))
    try {
        $entries = $zip.Entries | ForEach-Object { $_.FullName }
        if ($entries -notcontains "extension.yaml") {
            throw "produced package: extension.yaml is not at the ZIP root"
        }
        if ($entries -notcontains $module) {
            throw "produced package: $module is not at the ZIP root"
        }
        $bad = $entries | Where-Object {
            $_ -eq "Playnite.dll" -or $_ -eq "Playnite.Common.dll" -or $_ -eq "Playnite.SDK.dll" -or $_ -like "*.sln"
        }
        if ($bad) {
            throw ("produced package contains forbidden entries: " + ($bad.Name -join ", "))
        }
    } finally {
        $zip.Dispose()
    }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead((Convert-Path $pkg))
    try {
        $shaBytes = $sha256.ComputeHash($stream)
    } finally {
        $stream.Close()
    }
    $sha = -join ($shaBytes | ForEach-Object { $_.ToString("X2") })

    # The manifest as written is re-read and compared with the version the
    # package reported, so a manifest that never got stamped cannot slip out
    # beside a binary of a different version. The label-versus-stamp decision
    # itself ran earlier, before anything was packaged.
    $stampedText = if ($assemblyStamp) { "{0}.{1}.{2}" -f $assemblyStamp.Major, $assemblyStamp.Minor, $assemblyStamp.Build } else { "unreadable" }
    if ($assemblyStamp -and $stampedText -ne $packageVersion) {
        # The remaining legitimate difference is a test build's deliberate 4th
        # part, so it is stated rather than warned about.
        if ($isTestBuild) {
            Write-Host ("  note          : manifest {0} = assembly {1} + a test-only 4th part" -f $packageVersion, $stampedText)
        } else {
            Write-Warning ("manifest says {0} but the shipped {1} is stamped {2}" -f $packageVersion, $module, $stampedText)
        }
    }

    Write-Host ""
    Write-Host ("{0} package : {1}" -f $(if ($isTestBuild) { "Local test" } else { "Release" }), $pkg)
    Write-Host ("  extension Id  : {0}" -f $extId)
    Write-Host ("  version       : {0}  ({1})" -f $packageVersion, $versionSource)
    Write-Host ("  assembly stamp: {0}" -f $stampedText)
    if ($releasedKnown) { Write-Host ("  last released : {0} ({1})" -f $(if ($releasedMax) { "v$releasedMax" } else { "none yet" }), $anchorName) }
    Write-Host ("  SHA256        : {0}" -f $sha)
    Write-Host ""
    Write-Host "Install: double-click the .pext (or Playnite.DesktopApp.exe --installext <path>)."
    if ($isTestBuild) {
        Write-Host "This is a test build, not a release: its 4th version part can never name a v-tag or a CI release asset, and it is written beside dist\, not into it."
    }
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}
