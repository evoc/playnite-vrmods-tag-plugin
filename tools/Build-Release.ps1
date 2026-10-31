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
# script location by default; -RepoRoot overrides that (used by the CI workflow).

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$DistDir = "",
    [string]$RepoRoot = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = if ($RepoRoot) { (Resolve-Path $RepoRoot).Path } else { (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }
$proj = Join-Path $repoRoot "source\Playnite.VRMods.csproj"
$staging = Join-Path $env:TEMP ("vrmodtags-release-" + [Guid]::NewGuid().ToString("N"))
$DistDir = if ($DistDir) { $DistDir } else { Join-Path $repoRoot "dist" }

try {
    # 1. Clean publish into a fresh staging directory.
    New-Item -ItemType Directory -Path $staging | Out-Null
    dotnet publish $proj -c $Configuration -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    # 1b. -Version overrides the extension version in the staged manifest.
    #     CI passes the version the release workflow resolved from the git tag;
    #     local builds leave it empty and keep the repo's extension.yaml value.
    if ($Version) {
        $overrideParsed = $null
        if (-not [Version]::TryParse($Version, [ref]$overrideParsed)) {
            throw "-Version '$Version' is not a parseable .NET Version"
        }
        $yamlText = Get-Content (Join-Path $staging "extension.yaml") -Raw
        $yamlText = [regex]::Replace(
            $yamlText,
            "(?m)^(\s*Version:\s*)\S+(\s*)$",
            { param($m) $m.Groups[1].Value + $Version + $m.Groups[2].Value }
        )
        [System.IO.File]::WriteAllText((Join-Path $staging "extension.yaml"), $yamlText)
    }

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
    $version = $Matches[1]
    $parsedVersion = $null
    if (-not [Version]::TryParse($version, [ref]$parsedVersion)) {
        throw "extension.yaml: Version '$version' is not a parseable .NET Version"
    }
    if (-not ($yaml -match "(?m)^\s*Module:\s*(\S+)\s*$")) { throw "extension.yaml: missing or blank Module" }
    $module = $Matches[1]

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
    $pkgName = "VRMod Tags-$version.pext"
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
    Write-Host ""
    Write-Host ("Release package : {0}" -f $pkg)
    Write-Host ("  extension Id  : {0}" -f $extId)
    Write-Host ("  version       : {0}" -f $version)
    Write-Host ("  SHA256        : {0}" -f $sha)
    Write-Host ""
    Write-Host "Install: double-click the .pext (or Playnite.DesktopApp.exe --installext <path>)."
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}
