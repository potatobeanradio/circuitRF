# Sonnet Brief — File menu restructure, and View menu cleanup

**This supersedes item 8 of `brief-layout-testing-fixes.md`.** That item specified a smaller Import/Export
reorganisation; the structure below replaces it entirely. If item 8 has already landed, this rearranges what
it produced — the Import and Export submenu *contents* it defined are unchanged and still apply.

Menu structure and enablement only. **No command behaviour changes** beyond the new `New Symbol` entry and
the enablement rules in §3.

---

## 1. Target File menu structure

```
File
├── New                          ▸   (submenu — §1.1)
├── New Workspace…
├── ──────────────
├── Open Workspace…
├── Open Recent                  ▸
├── Open                         ▸   (submenu — §1.2)
├── ──────────────
├── Save
├── Save Schematic As…
├── Save Symbol As…
├── Save Layout As…
├── Save Workspace As…
├── ──────────────
├── Import                       ▸   (Data… · GDSII… · DXF… — see §1.3)
├── Export                       ▸   (Data… · GDSII… · DXF… · Gerber…)
├── ──────────────
└── Close Workspace                  (→ "Close Window" in a torn-off window — §4A)
```

**One ambiguity, flagged rather than guessed.** The owner's note said "below that separator" for both the
Save group and the Import/Export group, which could mean one shared group. The structure above assumes a
separator **between** Save and Import/Export, since they are unrelated operations and the surrounding
grouping is otherwise consistent. **If that is wrong, it is a one-line change** — but do not silently merge
them without saying so in the completion note.

### 1.1 New submenu — this exact order

```
New
├── New Cell…
├── New Schematic
├── New Symbol          ← NEW COMMAND
├── New Layout
├── New Data Display
└── New Technology…
```

`New Workspace…` stays **directly on File**, not in this submenu.

**`New Symbol` does not exist yet as a menu command** and must be added. Wire it to the existing scratch-symbol
creation path the symbol editor already uses (`NewScratchSymbol` / the New-Symbol-on-launch behaviour
documented in `src/Ui/CLAUDE.md`) — **do not write a second creation path.** If no such command exists, mirror
`New Layout` exactly.

### 1.2 Open submenu

```
Open
├── Open Schematic…     ← NEW COMMAND if absent
├── Open Symbol…
├── Open Layout…
└── Open Data Display…
```

`Open Workspace…` and `Open Recent` are **not** in this submenu — they sit directly on File, above it.

**No separators anywhere between `Open Workspace…`, `Open Recent` and `Open`** — they are one group.

### 1.3 Import submenu

```
Import
├── Data…
├── GDSII…
└── DXF…
```

All three exist: **Data**, **GDSII** (L4a) and **DXF** (L4b, where import was made first-class). `DXF…` is
the only addition, and it wires to the existing importer — **no new import code.**

**Import deliberately does not mirror Export**, because `Gerber` import does not exist. Design doc §8 lists
Gerber as **export-only**: aperture macros, arc interpolation modes, LPD/LPC polarity, and the "assemble a
board from a folder of files" problem, with the conclusion that *"a partial Gerber importer that silently
loses a clearance region is worse than none."*

**R-menu-5. Do not add a `Gerber…` item to Import** — not enabled, and not disabled-with-a-reason either.
A capability the product does not have is not a choice the user is being denied; listing it would only invite
the question again. **Do not add it later for symmetry with Export** — the asymmetry is the design, and this
paragraph is why. If Gerber import is ever wanted it is a substantial L4c addition to be decided on its own
terms, not implied by a menu shape.

## 2. Ellipsis convention

**R-menu-1. A menu item ends in `…` if invoking it needs further input from the user before it can act.
Otherwise it does not.**

The distinction that matters, and the one most often got wrong: **`…` means "I need more from you," not
"something might pop up."** A command that merely *may* raise a confirmation because of incidental state does
**not** take an ellipsis.

Applying it:

| Ellipsis | No ellipsis |
|---|---|
| `New Cell…`, `New Technology…`, `New Workspace…` — prompt for a name or a starting point | `New Schematic`, `New Symbol`, `New Layout`, `New Data Display` — create a scratch document immediately |
| `Open Workspace…`, and every item in the `Open` submenu — file pickers | `Open Recent` — a submenu; picking a leaf acts immediately |
| `Save Schematic As…`, `Save Layout As…`, `Save Workspace As…` | **`Save`** — acts directly. It may prompt when a scratch document has no path, but that is incidental state, not a parameter |
| Everything under `Import` and `Export` — pickers and options dialogs | **`Close Workspace`** — acts directly; the unsaved-changes prompt is a consequence of state, not an input the command needs |
| | `New`, `Open`, `Import`, `Export`, `Open Recent` — submenu parents never take one |

**Audit every File and View item against this rule, not just the ones being moved.** Consistency is the whole
value; a menu where the convention holds four times out of five teaches the user nothing.

## 3. Enablement — disabled with a reason, never missing

Per **R13a** (design doc §6.1): a command is either disabled with a stated reason on hover, or it does
something. Never a silent no-op, and never hidden.

| Item | Enabled when |
|---|---|
| **`Save`** | The active document has unsaved changes. Disabled otherwise — *"Nothing to save."* |
| `Save Schematic As…` | A schematic document is active |
| `Save Symbol As…` | A symbol document is active |
| `Save Layout As…` | A layout document is active |
| **`Export ▸ Data…`** | There is data to export — *"No data to export."* |
| `Export ▸ GDSII… / DXF… / Gerber…` | A layout document is active — *"Requires an active layout document."* (carried over from item 8) |
| `Open Recent` | At least one recent workspace; otherwise disabled, **not** an empty submenu |

The two the owner called out explicitly are `Save` and `Export ▸ Data…`; the As-variants and `Open Recent`
follow the same rule and should be handled while the enablement wiring is open.

## 4. Separators

**R-menu-2. Two separators must never end up adjacent, including when a group is empty or every item in it is
hidden.** Four separators are specified in §1, all between non-empty groups — but the rule has to hold
*dynamically*, not just in the static markup, or a future conditional item reintroduces the defect. If any
group can become empty at runtime, the separator logic must collapse accordingly.

## 4A. Torn-off windows

The File menu must be present in torn-off document windows (`SymbolEditorWindow`, `LayoutEditorWindow`, and
any schematic/data-display equivalent), not only in the main shell.

### 4A.1 `Close Workspace` becomes `Close Window`

**R-menu-3. In a torn-off window the final item reads `Close Window` and closes only that window's document.**
Everything above it is unchanged. It takes **no ellipsis** — the unsaved-changes prompt is a consequence of
state, not an input, exactly as R-menu-1 argues for `Close Workspace` and `Save`. Dirty handling matches
closing the same document as a docked tab; do not introduce a second prompt path.

### 4A.2 The real work: enablement must follow the window, not the shell

**R-menu-4. In a torn-off window, "the active document" means *that window's* document.**

This is the part most likely to go wrong and the reason this is not just markup duplication. The §3
enablement rules are almost certainly computed today from the **main dock's** active dockable. Reuse that
unchanged in a torn-off window and every document-scoped item is wrong — `Save Layout As…` greyed out in a
torn-off layout window, or worse, enabled and acting on whatever the main window happens to be showing.

So the enablement source becomes a per-window notion of the active document: the docked selection in the
main shell, the window's own document in a tear-off. Establish it once and have both menu surfaces read it,
rather than duplicating the resolution per item.

`Save` follows the same rule — it saves *that window's* document.

### 4A.3 Platform difference, stated because it is not symmetric

- **Windows / Linux** — the in-window `MenuItem` menu must be **added** to the tear-off windows, which
  currently have none.
- **macOS** — the `NativeMenuItem` menu bar is application-global and always present, so nothing is "added."
  Instead its contents must **track the key window**: `Close Workspace` ↔ `Close Window` and every §3
  enablement state must update as focus moves between the shell and a torn-off window. A global menu bar
  showing the shell's state while a torn-off window has focus is the specific failure to test for.

### 4A.4 Workspace-scoped commands — investigate, but change nothing

`New Workspace…`, `Open Workspace…`, `Open Recent` and `Save Workspace As…` remain in the menu per the
owner's instruction, which said only that `Close Workspace` is replaced.

**What happens to a torn-off window when the workspace is switched or closed is currently unverified.** The
teardown path (`ResetToBlankShell` and the Remove/Rename Cell paths) force-closes documents; whether it
reaches torn-off windows is unknown.

**R-menu-6. Determine the current behaviour and report it precisely. Do not change it in either direction.**

This is deliberately not a bug report. The owner is actively considering whether a torn-off document
*should* survive a workspace switch — so that a schematic or layout from another workspace can stay open for
reference while authoring elsewhere — and has pointed out that `File ▸ Open ▸ …` already opens arbitrary
files from outside the workspace, so "every open document belongs to the current workspace" is already not
true.

So: **if torn-off windows survive today, do not force-close them; if they are closed today, do not make them
survive.** Report which it is, and note anything that would break if they did survive — particularly
technology resolution, since a layout with `TechRef = null` means "use the workspace default" (L0c) and would
silently re-resolve against a different technology. That finding feeds a separate design decision.

## 5. View menu

**Remove both `Open Symbol Editor…` items.** Their functionality is now `File ▸ Open ▸ Open Symbol…`, so
leaving them duplicates a command in two places under two different names — exactly the confusion this
restructure exists to remove.

Check whether anything else in View duplicates a File command and report it; do not remove anything else
without asking.

## 6. Both menu surfaces

Every change applies to **both** the in-window `MenuItem` menu and the macOS `NativeMenuItem` menu, which
must stay structurally identical.

Watch the **`$parent[Window]` binding gotcha** already documented in `src/Ui/CLAUDE.md` — it has bitten this
codebase before and native menu items bind differently from in-window ones.

Preserve existing keyboard accelerators on the commands that already have them, even where the item moves
into a submenu.

## 7. Guardrails

- **No command behaviour changes.** Only structure, labels, and enablement. The one exception is adding
  `New Symbol` (and `Open Schematic…` if absent), which must reuse existing paths.
- Do not change the Import/Export submenu **contents** defined by item 8.
- Do not remove or rename anything not listed here.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 8. Gate

Gate command is plain `dotnet test`.

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Structure matches §1 exactly** — order, nesting and separator placement, in **both** menu surfaces.
   A structural test asserting the item tree is worth more here than a screenshot.
3. **`New Symbol`** creates a scratch symbol identical to the existing path (assert it routes through the
   same command, not a copy).
4. **`Open` submenu** contains exactly the four items in order; `Open Workspace…` and `Open Recent` are
   **not** in it, and there is no separator between those three.
5. **Ellipses (R-menu-1)** — a test walks every File and View item and asserts the ellipsis matches whether
   the command opens a dialog. `Save` and `Close Workspace` specifically have **none**.
6. **Enablement (§3)** — `Save` disabled with a clean document and enabled when dirty; `Save Schematic As…`,
   `Save Symbol As…` and `Save Layout As…` each enabled only for their own document type;
   `Export ▸ Data…` disabled with no data; layout exports disabled with no layout active. Each carries a
   reason.
6a. **`Import ▸ DXF…` works** — it routes through the existing L4b importer, not a new path, and imports a
   real DXF end to end. The Import submenu contains **exactly three** items; there is no `Gerber…`
   (R-menu-5).
7. **No adjacent separators (R-menu-2)** — assert over the built menu tree, including with `Open Recent`
   empty.
8. **View menu** no longer contains either `Open Symbol Editor…`, and `File ▸ Open ▸ Open Symbol…` still
   opens the symbol editor.
9. **Accelerators preserved** for every command that had one before.
10. **Torn-off menu exists (§4A)** — tear off a layout document and assert the File menu is present with the
    §1 structure, and that its last item reads **`Close Window`**, not `Close Workspace`.
11. **`Close Window` scope (R-menu-3)** — it closes only that window's document; the main shell and every
    other document are untouched. A dirty document prompts exactly as closing the same document as a docked
    tab does.
12. **Enablement follows the window (R-menu-4)** — this is the headline tear-off test. With a **symbol**
    document torn off and a **layout** document active in the main shell: in the torn-off window
    `Save Symbol As…` is enabled and `Save Layout As…` is disabled; in the main shell the reverse. `Save`
    saves the correct document in each. A single shared "active document" would fail this.
13. **macOS key-window tracking (§4A.3)** — moving focus between the shell and a torn-off window updates the
    global menu bar's `Close Workspace`↔`Close Window` and all §3 enablement states.
14. **Workspace teardown with a tear-off open (§4A.4)** — switch or close the workspace while a document is
    torn off and **report exactly what happens**, changing nothing either way (R-menu-6). Note what would
    break if the window survived, technology resolution first.

## 9. On completion

Record in `src/Ui/CLAUDE.md`: the final File menu structure; **R-menu-1's ellipsis rule and the
`Save` / `Close Workspace` / `Close Window` exceptions**, since that is the part a future contributor will
otherwise get wrong; **R-menu-4 — that "the active document" is per-window**, and where that resolution now
lives, because every future menu item inherits it; that this **supersedes item 8** of the testing-fixes
brief; the answer to §1's separator ambiguity — whether Save and Import/Export ended up in one group or two;
and what §4A.4's workspace-teardown check actually found.
