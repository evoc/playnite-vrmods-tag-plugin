# Contributing notes

> Part of [AGENTS.md](../AGENTS.md).

## Docs & screenshots: keep in sync with the GUI

**General rule:** a change to the user-facing GUI — the **settings page**
(`Settings/VrModSettingsView.xaml`) or the **review dialog**
(`VrModReviewDialog.xaml`) — is **not done** until the **documentation and its
screenshots** are updated to match the new UI.

- **Docs:** update the affected `docs/` page(s) — `review.md` / `settings.md`
  (and the `README.md` index if a page is added/renamed) — to describe the new
  controls, hotkeys, tooltips, columns, and filters.
- **Screenshots:** re-capture the `docs/img/` figures those
  pages reference so they show the **current** UI, not the old
  one — otherwise the change ships a stale screenshot to every
  user (the manual, incl. images, is packaged with the `.pext`).
- A GUI change with no doc/screenshot update is
  **incomplete**: leave it as open work, do not mark it done.

## Links stay inside this repo

This repo is the artifact: it is cloned, it is packaged into
the `.pext`, and it is meant to be published. So every link
and every path in it resolves to something a clone will have.

- A relative link may walk up from `docs/` to the root -
  `docs/for-developers.md` links `../AGENTS.md` and `../architecture.md` -
  because that stays inside the repo. What is broken is a target above
  the repository root: on someone else's machine there is nothing there,
  and a link that leaves the repo reads as intact to whoever wrote it.
- The same holds for paths in prose and in code comments. They break at the
  moment someone else checks the repo out, and they describe one machine's
  directory layout in a repo meant for everyone. Name an external tool
  by what it does - "an out-of-repo GUI harness that instantiates this
  real dialog" - not by where it happens to sit on one developer's disk.
- Naming a document is not linking to it. `[architecture.md](architecture.md)`
  resolves nowhere in a repo that has no `architecture.md`,
  and looks fine while reading. Either write a link that
  resolves, or use inline code for a bare reference.

## How the files in this folder are written

- One topic per file. A file stays small enough to read once; when it outgrows
  what its title promises, split it and add an entry to the index in `AGENTS.md`.
- Every file here is reachable from that index and opens with
  `> Part of [AGENTS.md](../AGENTS.md).` so a session that lands on the file
  directly can still find the rest of the guide.
- `AGENTS.md` holds the index and the rules that are cheap to break and
  expensive to debug — the one screen an agent reads without opening anything
  else. Its lines end with a pointer to the file that argues the rule; they
  never summarise the argument.
- A rule travels with its evidence. The measurements that justify a rule stay
  with the rule, in this folder: a number without its claim is noise, and a
  claim without its number becomes folklore one edit later.
