# Build & test (Windows only)

> Part of [AGENTS.md](../AGENTS.md).

- **.NET Framework 4.6.2 + WPF → Windows-only.** .NET SDK 8 (`global.json`
  pins `8.0.421`; any 8.0.4xx with xx ≥ 421 works). Internet
  access to nuget.org for restore (PlayniteSDK 6.18.0, NUnit,
  NSubstitute, …). No Playnite install is needed to build or test.

```text

dotnet build    # root Playnite.VRMods.sln builds plugin + tests; must be 0 errors AND 0 warnings
dotnet test     # runs the full suite (same solution)
```

(The root `Playnite.VRMods.sln` references both `source/Playnite.VRMods.csproj`
and `tests/Playnite.VRMods.Tests.csproj`, so the bare commands work from the
repository root. `dotnet build source/Playnite.VRMods.csproj` still works to
build just the plugin.) Bare commands build **Debug**; everything that has to
match CI here adds `-c Release` (that is the configuration whose analyzers are
on, and the warning gates below are run against it).

- **The build must stay warning-free.** Fix warnings; do not suppress them
  unless the SDK forces it (then `#pragma warning disable` + reason comment).
  A plain `dotnet build`/`dotnet test` does NOT fail on a warning, so the
  release job checks them by name: `Code analysis (release gate)` in
  `.github/workflows/release.yml` fails on any `: warning CA` line.
- **Version:** the git tag (`vX.Y.Z`) is the version. GitVersion
  injects it into the assembly at build time (`GitVersion.MsBuild`
  package; config in `GitVersion.yml`), and `tools/Build-Release.ps1`
  stamps the packaged `extension.yaml` from the stamp it reads off the
  published DLL, so the manifest and the assembly inside one package
  agree. The script never stamps the assembly — `dotnet publish`
  is called with no version argument — so that agreement is the CI
  ordering (tag HEAD, then build), not a number the script hands to
  the compiler. A `-Version` those tags do not name is refused; see
  the packaging bullet. Before that stamp existed the packaged manifest
  kept the committed `Version` from `source/extension.yaml` while
  the DLL beside it carried a higher git-derived one. The committed
  value is a placeholder that packaging overwrites — no packaged build
  reports it, and a bare `dotnet build` leaves it in `source/bin/`
  beside a git-stamped assembly. The `Properties/AssemblyInfo.cs`
  file carries no version — only `InternalsVisibleTo`.
