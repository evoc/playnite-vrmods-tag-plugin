# Security Policy

## What this plugin does with untrusted input

The VRMod Tags plugin reads twelve third-party websites on a schedule
(`BaseVrModParser.FetchStringAsync` is the only HTTP entry point) and turns what
it reads into Playnite library data: tags, game Links, and matching decisions.
That input is not trusted, and the threat model the code is written against:

- **A hostile or compromised source site** can return any markup, any JSON, any
  status code, and any `Location` header.
- **Anything written into the library is executable later**: Playnite opens a
  game's Link with the shell, so a Link is the dangerous write surface. Scraped
  text is gated by `WebUrl.IsSafeLinkUrl` at every point where a Link is
  created or merged (`VrModEntry.Link`, `VrModMetadataProvider`,
  `VrModTagService`), so `file:`, `javascript:`, app-URI schemes,
  `search-ms:`, credentials in the URL, and over-long URLs never reach the
  library.
- **A redirect is a host change.** The shared handler has
  `AllowAutoRedirect = false`; hops are followed explicitly, capped at
  `BaseVrModParser.MaxRedirectHops`, and only onto the host the plugin asked
  for (or a mirror host a parser declares). A hop off-site fails the source and
  is written to the run log instead of being parsed.
- **Volume and pace are capped**: response size (`MaxResponseBytes`; a robots
  file gets its own 64 KB cap), entry counts, scraped field lengths on
  `VrModEntry` (name 200, author 100, additional info 400, Steam App ID 32),
  curator pagination, per-host request serialization and a global in-flight cap
  (`HttpHostGate`), a 60 s ceiling on a `Retry-After` the plugin will sit
  through (`MaxServerRequestedWait`), a 30 s ceiling on a `Crawl-delay`
  (`HttpHostGate.MaxCrawlDelay`), and a wall-clock scan budget with a Cancel
  button. A site that asks for longer than one of those ceilings gets a source
  that failed this run — not a stalled scan, and not a retry it asked us not to
  do.
- **robots.txt is read for its pace, not its rules.** `RobotsPolicy` fetches one
  robots file per host per run and honours the `Crawl-delay` written for
  `User-agent: *` (the plugin sends no User-Agent, so records naming some other
  bot say nothing about it). `Disallow` is **deliberately not enforced**: a large
  share of mod-catalogue sites disallow generic crawlers on precisely the pages
  this plugin has to read, and honouring that would silently switch sources off
  and leave the user with an empty tag set and no explanation. Reading the file
  for its load request and not the path rules is a stated position, not an
  oversight.

## Supported versions

| Version | Supported |
| --- | --- |
| The latest release tag (`vX.Y.Z`) on this repository | ✅ |
| Anything older | ❌ (upgrade; older builds are not reviewed or patched) |

## Reporting a vulnerability

Report privately instead of opening a public issue: a public issue tells every
user of the plugin that a machine-as-code path exists before it is fixed, and
this plugin runs on user machines against attacker-chosen websites.

- GitHub security advisory: **Security → Report a vulnerability** on this
  repository (private to the maintainers), or
  `https://github.com/evoc/playnite-vrmods-tag-plugin/security/advisories/new`.
- What helps: which source URL / fixture reproduces it, the Playnite version,
  and what the plugin wrote into the library as a result.

Expected handling: an acknowledgement within a week, and a fixed release (a
patch bump, released the same way as everything else: a push to `main`) as
soon as the fix is verified. Fixes for anything that can write into the library
are shipped without waiting for the next feature round.

## Scope of "secure" here

The plugin does not run a browser, does not execute page scripts, does not
deserialize untyped JSON, and renders scraped text as plain `TextBlock` text.
What it cannot promise: the *content* of a mod list is only as truthful as the
site it came from — a source that lies about which games have mods produces
wrong tags, and matching a mod entry to the wrong game row is a correctness
problem, not a security boundary. Tag and Link writes always go through the
review dialog unless auto-tagging is enabled explicitly in the settings.
