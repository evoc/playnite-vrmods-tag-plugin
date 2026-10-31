# For developers

This manual is for end users. The developer documentation lives at the root of the
repository:

- [AGENTS.md](../AGENTS.md) — the contributor/agent guide: project overview, build and
  test commands, conventions, test invariants, release packaging, live testing.
- [architecture.md](../architecture.md) — the architecture documentation.
- [database.md](../database.md) — what the plugin stores where, including the Playnite
  database structure.
- [external-references.md](../external-references.md) — the researched data sources.

Building and testing need no Playnite install: `dotnet build` and `dotnet test` from the
repository root (Windows, .NET SDK 8 — see AGENTS.md for the full requirements). The root
`Playnite.VRMods.sln` holds both projects — the plugin in `source/`, the tests in `tests/` —
so those two commands are the whole build. They default to a Debug configuration; CI and the
warning/analyser gates add `-c Release`, which is what turns the analyzers on.
