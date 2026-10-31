# Getting started

## Installing

1. Download the latest release package (`VRMod Tags-<version>.pext`) from the project's
   GitHub Releases page.
2. Double-click the package. Playnite registers itself as the handler for `.pext` files,
   asks for confirmation, and installs the add-on.
3. Restart Playnite — the package is applied on the next startup.

You'll find the plugin's settings page in the **Add-ons** window (press **F9**
or click the **Add-ons** icon in Playnite's toolbar) under
**Extensions settings** → **Metadata Sources** → **VRMod Tags**.

> **Note:** this is *not* Playnite's main **Settings** window. Add-on settings
> are not listed there — the only place to configure this plugin is the
> **Add-ons** window.

## What happens on install and on the first scan

Installing the add-on doesn't change anything in your library yet. As soon as Playnite
starts with the add-on installed, it runs a scan:

- It downloads the listing of every **enabled** source (the first run does the
  downloads; afterwards each listing is cached for the duration you configure, so
  normal starts are fast).
- It matches every listing against your library. Matching is exact first (a Steam App
  ID match is preferred whenever the source has one), then by normalized game name with
  a similarity threshold (the default is 80% — see [Settings](settings.md)).
- Exact matches (score 1.00) are accepted by default; everything else starts as
  **Pending** and waits for you in the review dialog.

When the scan finds matches, Playnite posts a notification — the bell in the title bar
gets a badge and the panel says:

![The VRMod scan notification](img/bell.png)

"VRMod scan: 382 pending matches. Click to review." Every notification is
**clickable**: new pending matches — or tag changes on matches you already saved —
open the review dialog. A scan that finds nothing new posts no notification at
all — the per-source status in the settings page says what happened (e.g. a
source that returned no data).

## Automatic scans

The plugin scans automatically:

- **at Playnite startup**,
- **when you install a new game** — the matches for that single game are applied
  silently, without any window,
- **on a schedule you configure** — the default is every 7 days; see
  [Settings](settings.md).

Automatic scans run in the background. When there is news, you get the bell
notification; clicking it opens the review dialog — the pending matches, or every
matched game when your saved matches picked up tag changes. A scan that finds
nothing new stays silent. You can also
open the review dialog any time from the settings page: the **Review VR Mods**
button opens it directly from the data of your last scan (without rescanning) — with
every matched game (new and already-decided), so you can review or change your
decisions; the **Full Scan** button runs the scan and applies the matches right away,
without a dialog.

## Updating

Double-click the new `.pext` — the same flow as the first install. Your settings and
your saved decisions survive the update.

## Uninstalling

Remove "VRMod Tags" from Playnite's **Add-ons** window, then restart Playnite. The
plugin detects the queued uninstall as Playnite shuts down and removes everything it
added: all `VRMod*` tags from your games, the source links on matched games, the
`VRMod*` tag definitions, and its own settings file. Playnite then deletes the plugin
files on startup — nothing of the plugin is left behind.
