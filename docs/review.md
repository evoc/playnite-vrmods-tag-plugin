# Review dialog

The review dialog is where you decide which
suggested matches are right. You open it:

- from the **Review VR Mods** button on the settings page — without rescanning
  (it uses the data of your last scan), it lists every matched game (new and
  already-decided), pending first, so you can review or change your decisions;
- from the bell notification — the pending matches when there are new ones,
  or every matched game when your saved matches picked up tag changes (a scan
  that found nothing new posts no notification at all).

![The review dialog](img/review-dialog.png)

## Columns

| Column | What it shows |
| --- | --- |
| **Status** | The decision for this match: **Saved** or **New** (accepted), **Rejected** or **Rejected (new)**, **Suggested**, or **Pending**. The status is *italic* while the decision has not yet been saved to your library. |
| **Source** | Which source the match came from. |
| **Source Entry** | The entry in that source's listing. |
| **Game** | The game in your library the entry matched. When the same title is in your library more than once (for example imported from Steam and from Epic), each import is its own row, shown with its store name in brackets. |
| **Score** | Match confidence — 1.00 means the plugin is certain about the pairing: either the two names match exactly (accepted by default), or the listing and the game publish the same Steam App ID (shown as **Pending** for you to decide — see "Matches by Steam App ID"). |
| **Saved at** | When the current decision was saved. Hidden by default — enable it via the **Columns…** menu. |
| **Tags** | The tags the game gets when you accept: the `VRMod` umbrella tag plus the category tags. Italic with an "↑" marker when the source's tags changed since you last accepted. |
| **Changed** | Flagged when the source's tags changed since your last acceptance (for example, 6DOF support added to the listing later). Tick **Changed only** to filter to just those rows. |
| **Link to Mod** | The "↗" button opens the mod page for that entry in your browser. |

## Suggested rows

A **Suggested** row is a source listing and a library game where **one
name is the other name with words added**: *Morrowind* against *The Elder
Scrolls III: Morrowind GOTY Edition*, *Trine 2* against *Trine 2: Complete
Story*, *We Were Here* against *We Were Here Together*. The words in common
line up in order, and what is added is either re-release packaging (`GOTY`,
`Complete Edition`, a franchise prefix) or wording the name rules cannot judge.
The plugin never tags such a pairing on its own, because "is this the same
product or a different release of it" is your call, not a string metric's.

A disagreement is **not** suggested. If the two names carry different numbers
(*Age of Wonders 3* against *Age of Wonders 4*) or disagree about an edition
word, the pair is refused and never reaches the review grid at all — those rows
would be noise, and a wrong Accept there would write tags for the wrong game.

When several library games could fit one listing, **every** candidate the name
rules can offer is shown — not a first two — ranked with the strongest pairing
first: re-release packaging before wording the rules cannot judge, then the
longest unbroken run of shared words, then the fewest words left over on either
side. The **Score** number is not that ranking — *Morrowind* against *The Elder
Scrolls III: Morrowind GOTY Edition* scores 0.20 and is the strongest of those
two facts, which is why the number is shown as information rather than used to
decide what you are offered, or in what order.

- Hover the **Status** cell to read the reason and the rank: which words
  of the listing name the game name contains, which words of
  the game name are left over, whether a number disagrees, and where the
  row sits in its listing's candidate list (**candidate 3 of 8**). The grid
  sorts by column, so the rank the search decided lives in the tooltip.
- Accepting the row records the pairing by name. Every later scan then
  matches that listing to that game whatever either is called — for that
  listing and that game only, so a sibling sequel is not dragged in.
- Suggestions are never applied by a scan, and they are never counted
  as matches. Accept one and the tags appear at the next save.

## Matches by Steam App ID

Some listings say which Steam product they are for, and some library
games do too: a Steam import carries its product id, a listing from Steam
Curators or PCVR Central carries one, and both sides of a pairing can
therefore point at the same Steam App ID even when their names share
nothing — a listing called *Trageous Hallows* and a library game called
*Outlast 2*, both publishing 2521050. Those pairings are real matches
and they arrive as **Pending** rows at score 1.00: the App ID proves
which product the listing is for, not that you want this mod on it.
Nothing is tagged until you Accept the row, and a pending row keeps a
game's existing tags from being cleaned up as stale while you decide.

A listing and a game that publish **different** Steam App IDs are not
matched, even when their names are identical — *Resident Evil 4* (434120)
and the library's *Resident Evil 4* (2443190) are two products. If the names
argue for the pairing it still shows up as a **Suggested** row, flagged
with the disagreement and ranked last, so you can settle it yourself.

A side that says nothing is not a refusal. Listings from
sources that publish no App ID at all, and library games
with no Steam link, match by name exactly as they always did.

## Making decisions

- **Double-click a row** to cycle its status: Pending → Accepted → Rejected.
- **Right-click a row** for a quick menu:
  **Accept**, **Reject**, **Set to Pending**.

  ![The row menu](img/row-menu.png)
- **Keyboard** (with the list focused): **Enter** accepts the
  row, **R** rejects it, and **P** sets it to Pending — handy
  for reviewing row by row with the arrow keys. **Ctrl+S**
  saves (the **Save** button is no longer the Enter default).
- **Alt accelerators** (from anywhere in the dialog): **Alt+F** the filter
  box, **Alt+T** the time window, **Alt+S** the status menu, **Alt+C**
  the columns menu, **Alt+H** the help, and **Alt+A** / **Alt+R** accept
  / reject directly (the selected rows, or every row the filters are showing
  when none are selected). Each control also shows its accelerator in its
  tooltip.

- **Bulk buttons** (bottom right): with no row selected, **Accept All**
  / **Reject All** act on the rows the filters are showing. A row hidden by
  the filter box, the status menu, the time window or **Changed only** keeps
  the status it already has, and the caption counts both halves
  (**Reject 12 shown · 118 keep their status**). With one or more rows
  selected they become **Accept Selected** / **Reject Selected** and act on
  the selection alone.
- A decision you change in the dialog is *italic* in the **Status** column
  until you click **Save** — **Save** persists every changed decision to
  your library (tags and links are applied), **Cancel** closes the dialog
  and discards the unsaved changes. **Save** is disabled while everything
  is already saved. The **×** in the title bar also closes without saving.

The dialog is a normal Playnite window: it can be
minimized, maximized, and resized from the edges.

## Finding rows

- **Filter** box — text filter over the list.
- **Time** filter — All times / Last hour / Last day / Last week / Last month.
- **Status** filter — show only rows with the chosen decision.
  The dropdown lists **Pending** / **Accepted** / **Rejected**
  (tick several for a union); **Pending only** and **Show all** are
  one-click shortcuts. "Status: all" (nothing ticked) shows everything.
- **Changed only** — show only rows whose
  tags changed since your last acceptance.
- **Sorting** — click a column header to sort by it.
- **Columns…** menu — show and hide columns; a
  column can be reordered by dragging its header.

  ![The Columns menu](img/columns-menu.png)

## Reset all decisions

**Reset all decisions** (bottom left) clears every decision so
all matches are reviewed again. It asks what to do with the tags:

- **Reset only** — keep the tags already applied to your games;
- **Reset and remove tags** — also remove every `VRMod*` tag from your library.
