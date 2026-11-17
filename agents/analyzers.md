# Code-analysis policy: clean before pushing

> Part of [AGENTS.md](../AGENTS.md).

## Where the analyzers are turned on

Analyzers: `source/Playnite.VRMods.csproj` sets `EnableNETAnalyzers`
with `AnalysisLevel=latest` + `AnalysisMode=All`. On `net462` the SDK
leaves analyzers **off** by default (it enables them for `net5+`), so
this has to be set explicitly: the plugin parses thirteen attacker-chosen
websites and the framework's own security rules have to actually run
on it. `AnalysisMode=Security` is not the posture — it switches off
the style/performance and design families, and those families have
named real weaknesses here (`CA1848`, `CA1307`, `CA1051`, `CA1034`).

**No branch is ready to merge, and nothing is ready to push, while the plugin
build reports a code-analysis warning.** The gate is the `Release` build of
`source/Playnite.VRMods.csproj`: 0 warnings, 0 errors. Run it before pushing:

```text
dotnet build source/Playnite.VRMods.csproj -c Release -v n                        # what CI runs
dotnet build source/Playnite.VRMods.csproj -c Release -p:TreatWarningsAsErrors=true
```

Both must come back clean. `dotnet build` alone does not fail on a warning,
so `.github/workflows/release.yml` carries a `Code analysis (release gate)`
step that fails the release on any `: warning CA` line — that step is the
backstop, not the policy, and it is not a licence to push a build that fails it.

How a warning may legitimately stop being a warning:

- **Fix it.** Suppression is the last resort, and where the SDK forces it,
  `#pragma warning disable` plus a reason comment at the site.
- **Exclude a family only with a judgement written down.** Exclusions live in
  `.editorconfig` as `dotnet_diagnostic.CAxxxx.severity = suggestion` lines and
  each one carries a sentence saying why that family is off for this plugin — a
  reason, not a count. No exclusion enters `.editorconfig` without it.
- **A broad `catch` (`CA1031`) is not a silenced family.** It is
  acceptable only where being broad is the contract — Playnite's UI
  handlers, its extension seam, settings persistence, best-effort
  uninstall cleanup — and each such site carries a `[SuppressMessage]`
  with its reason. Everywhere else, catch a named failure vocabulary
  (`IsRequestPlumbingFailure` for fetch plumbing, `IsJsonShapeFailure` for
  JSON reads, `IsPathOrFileFailure` for settings and uninstall-queue files),
  each pinned by a test that states what it absorbs and what it must rethrow.

The full sweep — every family, exclusions included — is a **work list,
not a gate**, and it is a local command: `.editorconfig` severities
win over `-p:AnalysisLevel`, so it needs those lines commented
out, and they must be back in place before anything is committed.

```text
dotnet build source/Playnite.VRMods.csproj -c Release -p:AnalysisLevel=latest-All --no-incremental
```

The sweep's output is not a record to keep here: what is fixed
is in the code, what is excluded is in `.editorconfig` with its
reason, and what is still open is open work, not a number in a guide.
