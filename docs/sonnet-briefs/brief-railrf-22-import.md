# Brief 22 — the folder asked first, the technology that would not open, and the BOM

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail22-n` · **Phase:** corrective
**Area:** `src/Ui/Views/RailRf/RailRfWindow.Import.cs`, `RailImportDialog.axaml*`,
`RailRfWindow.axaml.cs`, `src/Ui/RailRf/RailImportOptions.cs`
**Depends on:** 1-18 · **Blocks:** nothing
**Found by:** a first-time designer's pass, 2026-09-20

---

## 0. What this brief delivers

| | Defect | Reported as |
|---|---|---|
| `R-rail22-1` | the artwork picker offers a FILE, and asks about a folder much later | *"when you ask to open the art work, it does not propose to select a folder… it is asked but much later"* |
| `R-rail22-2` | the Edit Technology button can do nothing, silently | *"pressing open gerber tech does not work"* |
| `R-rail22-3` | a PDF BOM is not read, and the refusal does not say what to do | *"can the bom be coming from a pdf?"* |
| `R-rail22-4` | closing the technology document closes more than it should | *"if i try to close the technology file, it close everything"* — **unreproduced; investigate first** |

---

## 1. `R-rail22-1` — the folder is asked first, or not asked at all

### What is wrong

`ImportBoardAsync` (`src/Ui/Views/RailRf/RailRfWindow.Import.cs:41`) opens
`StorageProvider.OpenFilePickerAsync`. A Gerber set is **a folder of files** — the shipped filter
even lists eleven extensions that come as a set — and the folder question arrives later, inside
`GerberImportEntry.Run`'s `pickFolder` callback, only if the classifier decides it needs one.

So the first thing a user does is pick one of twelve files that belong together, and the tool asks
about the enclosing folder afterwards. That is backwards, and the designer described the symptom
exactly: it is asked, but much later.

### `R-rail22-1a` — the dialog comes first, with both buttons

Invert the order. **Import Board** opens `RailImportDialog` immediately, with a new first row:

```
Artwork   [                                    ]  [ File… ]  [ Folder… ]
```

`File…` is the existing picker with the existing filter. `Folder…` is
`OpenFolderPickerAsync` — which the code already calls, just later
(`RailRfWindow.Import.cs:106`). Both write the same box. Nothing is pre-selected.

This also puts the artwork on the same dialog as the three companion files, which is where a reader
expects it: those four rows are one question.

### `R-rail22-1b` — the later prompt stays, and stays silent when it can

`GerberImportEntry.Run`'s `pickFolder` is not removed — it is the funnel File ▸ Import ▸ Gerber goes
through and the import may still need it for a set the classifier resolves differently. It simply
has nothing to ask when a folder was named up front. **Removing it would be a second import path**,
which the Import file's own header forbids in its first line.

### `R-rail22-1c` — what a file IS is still settled by CONTENT

The filter is a convenience and cannot admit or exclude anything — the existing comment at line 49
states this and it stands. Adding a folder button does not add a classification rule.

---

## 2. `R-rail22-2` — a button that can do nothing, silently

### What is wrong

`OnBoardEditTechnology` (`RailRfWindow.axaml.cs:538`) has two silent returns:

```csharp
if (Vm?.TechnologyPath is not { Length: > 0 } tech) return;
if (WorkspaceLocator.Any() is not { } workspace) return;
```

The button is hidden when `HasTechnologyFile` is false, so the first cannot normally fire. **The
second can**: `WorkspaceLocator.Any()` returns null when no workspace window is open, and railRF is
an unowned window that outlives one. The button is then visible, enabled, and does nothing at all.

There is a second route to the same symptom: the throwaway import path mints its `.ctech` into a
temp directory (`Import.cs:81`), which no workspace owns, so `OpenTechnologyDocument` has nowhere
to put it.

### Before fixing: establish which one he hit

**The button landed on 2026-09-19** (commit `1a9e10bf` era), and the report is dated 2026-09-20.
Confirm which build he ran before writing a fix — if he was on an earlier beta the button did not
exist at all and the correct answer is "it now does", not a defensive change. Record the finding in
`src/Ui/RESOLVED.md` either way.

### `R-rail22-2a` — no silent return from a visible control

Whichever branch fires, the window says so. *"There is no workspace open to edit this technology
in"*, or *"this board's technology was imported to a temporary location and is not part of any
workspace"*, each with what to do. A control that is live and silent is indistinguishable from a
control that is broken — the rule `CanPickSelectedNet` already states in its own remarks
(`Import.cs:169`).

### `R-rail22-2b` — the throwaway path names its consequence up front

`RailImportDialog` already explains what the "keep the artwork in this workspace" checkbox buys.
It gains one clause: with it off, **the technology is temporary and cannot be edited.** The
checkbox defaults on, so this is the uncommon path — and it is exactly the path whose consequences
are invisible until something does nothing.

---

## 3. `R-rail22-3` — the BOM, and why a PDF is a refusal

### The recommendation

**No PDF parsing.** A PDF bill of materials is a *rendering* of a table, not a table: column
boundaries are inferred from glyph positions, a wrapped cell is indistinguishable from two rows,
and a refdes list that spans a line break silently loses members. The failure is **quiet and
plausible** — a BOM that reads with nine of thirteen parts produces a completely believable railRF
answer for the wrong board.

That is the same class as the Excellon suppression question, which `convert` refuses outright
rather than guessing because leading and trailing suppression differ by four orders of magnitude on
identical text. This series does not guess; it should not start here.

The designer supplied both forms of the same document and said so himself — the CSV is the same
BOM. **The CSV reads correctly today**, including grouped cells: `RefdesCell.Parse` expands
`"C3, C5, C7, C10, C30"` and `C1-C9` into members, and reports what it could not expand
(`BomFile.cs:267`).

### `R-rail22-3a` — the refusal names the answer

Choosing a `.pdf` for the BOM row is refused with a sentence that says what a PDF is and what to
supply instead: the CSV, XLSX or tab-separated export the same tool produces. Not "unsupported
format" — the user has the right file and does not know it.

### `R-rail22-3b` — say what was read

After an import, the window states how many BOM rows were read, how many refdes they expanded to,
and how many cells could not be expanded. `BomFile` already counts all three (`sourceRows`,
`grouped`, `unexpanded`) and nothing shows them. A BOM that read nine parts out of thirteen should
be visible at import, not inferred from an odd answer later.

---

## 4. `R-rail22-4` — closing the technology closes everything

**Unreproduced. Investigate before designing.**

The report is *"if i try to close the technology file, it close everything"*. Plausible mechanisms,
in order of likelihood:

1. the `.ctech` is the workspace default and closing it triggers a re-resolution that closes or
   re-opens dependent documents;
2. a dirty-document prompt on the tech editor is being answered for the whole window;
3. the dock is disposing a branch rather than a tab.

Do not write a fix against a guess. **Reproduce it first**, with the shipped Power Rail example and
its `tech/pcb-4layer-1p6mm.ctech`, and record what actually happens in `src/Ui/RESOLVED.md`. If it
does not reproduce at HEAD, say so and close it — the three railRF window passes on 2026-09-19
changed a good deal in this area.

---

## 5. Gate

`tests/Ui.Tests/RailRf/RailImportDialogTests.cs`.

1. **The dialog opens first and offers both.** Import Board raises `RailImportDialog` with an
   artwork row carrying a File button and a Folder button, and nothing pre-selected in either
   (R-rail22-1a). **Fails at HEAD: the file picker opens first and the dialog has no artwork row.**
2. **A folder named up front is not asked about again.** `pickFolder` is never invoked
   (R-rail22-1b).
3. **`pickFolder` still exists and is still reachable** on the path that needs it (R-rail22-1b).
4. **No silent return.** With no workspace resolvable, pressing Edit Technology produces a refusal
   sentence rather than nothing (R-rail22-2a).
5. **The throwaway path says so.** With the checkbox off, the dialog text names the consequence
   (R-rail22-2b).
6. **A `.pdf` BOM is refused by name**, and the sentence names CSV (R-rail22-3a).
7. **The designer's CSV shape reads.** A synthetic BOM with grouped refdes cells and a quantity
   column expands to the right member count, and the import reports the three counts
   (R-rail22-3b). **No vendor part numbers in the fixture** — synthesise the shape, not the file.

## 6. Scope

- **No second import path.** R-rail22-1b, and the Import file's own header.
- **No PDF reader.** R-rail22-3.
- **No fix for R-rail22-4 until it reproduces.**
