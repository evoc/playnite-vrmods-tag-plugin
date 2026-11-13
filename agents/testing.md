# Test suite invariants

> Part of [AGENTS.md](../AGENTS.md).

- No `Thread.Sleep` / wall-clock dependence (time-based tests use fixed
  `DateTime` values). No shared mutable static state that tests assert on.
  Per-test `SetUp` isolation. `[Test]` + `[TestCase]` data-driven style.
- `VrModTagService` is fully injectable (`IPlayniteAPI`
  via NSubstitute, `FakeItemCollection<Game|Tag>` for
  the database) — test the service, not the plugin.
- **Coverage boundary (intentional):** `VrModTagPlugin` orchestration
  (startup scan, scheduled-scan timer, scan-progress UI, notifications) is
  NOT unit-tested — the plugin ctor constructs the real service and registry
  (no injection), so a unit test would trigger real network fetches. That
  path is covered by the integration tests that drive the service directly,
  by one plugin-ctor test (corrupt settings), and by live verification. **Do
  NOT add unit tests that construct `VrModTagPlugin` with the real registry.**
- Parsers are tested against recorded fixtures in `tests/Parsers/snapshots/`
  (plus page-token and snapshot tests for paginated
  sources). The UeVR test hits the real Firestore API.

## Live testing (any machine)

Building/testing needs no Playnite. For live end-to-end
verification you need a Playnite 10.x install:

1. `dotnet publish source\Playnite.VRMods.csproj -c Release -o <some-dir>`
2. Add `<some-dir>` to Playnite's external-extensions list (Settings →
   **For developers** → External extensions; config key `DevelExtenions` — a
   typo in Playnite itself, **do not "fix"** it). The value must be a JSON
   **array of objects**: `[{"Selected": true, "Item": "<some-dir>"}]` —
   a plain string array fails deserialization and Playnite won't start.
3. Restart Playnite; the log shows `Loaded ... Playnite.VRMods.dll`.

**Reaching the settings page (live Playnite):** press **F9** to open
the Add-ons window; the plugin's settings page is under **Extensions
settings → Metadata Sources → VRMod Tags** (it lands there because
the plugin is a `MetadataProvider`). From that page, **Full Scan**
re-scans every enabled source without a dialog, and **Review VR
Mods** scans then opens the review dialog for every matched game.

Dev extensions shadow installed extensions with the same manifest Id, so a dev
entry is safe while a release is installed and reversible. Prove which copy
a running instance loaded by re-opening each candidate `Playnite.VRMods.dll`
with `FileShare.None`: the loaded one throws a sharing violation, the
shadowed one stays free. To tell which **build** a folder holds, read
the dll's ProductVersion, because GitVersion writes the full commit into
it (`…+Branch.main.Sha.<sha>`). A file hash alone is not enough: MSBuild
can leave a build output untouched across a repeat `dotnet publish`,
so bytes on disk can predate the commit they are attributed to.

Machine-specific dev setup (launch targets, live library
state, GUI-driving notes) is **not in this repo**.
