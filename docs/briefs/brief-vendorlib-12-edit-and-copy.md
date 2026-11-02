# Brief VL-12 — Edit Vendor Model and Copy to Workspace

**Series:** [Model libraries](brief-vendorlib-6-model-library-format.md) · **Tag:** `R-vl12-n` · **Phase:** UI, authoring
**Area:** the schematic canvas context menu and Push In (`schematic-editor.md#hierarchy`: the menu item, toolbar
18, Ctrl/⌘+]); `src/Ui/ViewModels/WorkspaceViewModel.CellDrop.cs` (the copy step to reuse); `ModelLibraryExport`
(brief 10, which gains `Refresh`); `src/Design/Cells/` (`.ccell`); the document save path for a `.csch`; `src/Cli`
(`export part --refresh`, `import part`)
**Depends on:** 9, 10

---

## 0. Why

A placed library part is somebody else's design: the publisher's, or the shared library's. Pushing into it
by accident and editing it would change every design that places it, so Push In must not reach it. There
are still two legitimate reasons to change one: the library's owner fixing the part, and a designer who
wants a variant. Those are two different commands, and each says what it does.

## 1. `R-vl12-1` — no Push In

On a `vendormodel://` instance (brief 9), **Push In** is not offered: not on the menu, not on toolbar 18,
and not on Ctrl/⌘+]. Toolbar 18 is disabled with the tooltip *"A library part: use Edit Vendor Model… or
Copy to Workspace…"*, and the key does nothing beyond showing that sentence in the status line.
Double-click opens the Parameter Editor, as it does for any part.

## 2. `R-vl12-2` — Edit Vendor Model…

- **`R-vl12-2a`** Right-click ▸ **Edit Vendor Model…** opens the model folder's `.csch` as **its own
  document tab**. It does not push in: the breadcrumb is the model's, not this design's.
- **`R-vl12-2b`** **Greyed, with the reason as its tooltip**, when:
  - the part is a SPICE-text part (*"a SPICE subcircuit; there is no schematic to edit"*)
  - the library's `library.clib` says `ReadOnly` (*"the publisher marks this library read-only; Copy to
    Workspace makes an editable copy"*)
  - the model folder cannot be written. Probe it, don't guess. Typical causes are an installed tree, a
    share mounted read-only, or no permission.
- **`R-vl12-2c`** **Before the first edit is saved**, one confirmation per model per session: *"Saving
  changes TSTFET100 in <library> for every design that places it. This workspace places it N times."*
  Other workspaces cannot be counted, and the sentence does not pretend otherwise.
- **`R-vl12-2d` Saving refreshes the model.** Saving that `.csch` calls `ModelLibraryExport.Refresh(modelDir)`.
  Refresh is the same function as brief 10's Export, minus the dialog. It:
  - copies in any newly referenced file and rewrites the reference model-relative
  - converts any newly added technology-bound part using the plan in `part.cpart`'s `Converted` entries
    (brief 11), or 0–20 GHz / 1001 points when there are none
  - re-extracts the `.cnl`
  - updates `Pins` and `Parameters` in `part.cpart`

  It does **not** touch `PublishedHash`, so `check` and Validate then say "edited since exported" (brief
  6), which is what lets a team see that a shared part has diverged. `Revision` changes only on Export.
- **`R-vl12-2e` A refresh that is refused still saves the `.csch`.** Examples: a kit part was added, or a
  file does not resolve. The user's work is never discarded. The `.cnl` is left as it was, and Messages
  says *"TSTFET100 still runs its previous netlist: <reason>."* `check` reports the `.cnl`/`.csch`
  mismatch as an error (brief 9 §4a) until it is fixed.
- **`R-vl12-2f`** Open designs that place the part re-resolve its symbol and pins after a save. A pin that
  disappeared draws as a dangling wire end, as a cell's removed pin does today. No new behaviour is
  needed.

## 3. `R-vl12-3` — Copy to Workspace…

- **`R-vl12-3a`** Right-click ▸ **Copy to Workspace…** copies the model folder into the open workspace as
  an ordinary cell. The name is the part number, de-duplicated with brief 10's suffix rule.
  - `models/`, `cells/` and `docs/` come with it.
  - Every file reference is rewritten from model-relative to the form a workspace document stores
    (`SnpPathPolicy.ToStored`, workspace-root-relative), so the copy runs as the model did. Brief 8's
    owning-root rule makes that a rewrite of strings only.
  - `part.cpart` and the `.cnl` are **not** copied: a workspace cell is extracted from its schematic.
- **`R-vl12-3b`** **Reuse Add Cell to Workspace's copy step.** It currently lives in
  `WorkspaceViewModel.CellDrop.cs`. If the copy itself (not the prompt) is in the view model, move it to
  `src/Design` first, under the authoring rule: an operation that lives only in a view model is not a
  capability.
- **`R-vl12-3c`** The `.ccell` gains an optional `CopiedFrom` record (library name, part, `Revision`,
  `PublishedHash`). Nothing runs on it. It is for a person reading the cell later, and `explain` prints it.
- **`R-vl12-3d`** Then one question: **Point this instance at the copy / Point every TSTFET100 in this
  schematic at the copy / Leave them on the library part**. The first is the default. Re-pointing keeps
  each instance's name, position, wires and parameter values, and it is one undo entry. The pins match
  because the symbol was copied with the cell.
- **`R-vl12-3e`** Greyed for a SPICE-text part, with the same reason as 2b's first case.

## 4. `R-vl12-4` — headless

- `circuitrf export part <model folder> --refresh` runs §2d after a `.csch` was edited by writing it,
  which is how an agent edits a document here. Exit 1 on §2e's refusal, with its sentence.
- `circuitrf import part <model folder> --into <workspace>` runs §3a, if `import part`'s contract
  (`ComponentImport`) can take a model folder without bending it. If it cannot, use a noun on the same
  verb and say why in the completion note. The re-pointing in §3d is a GUI convenience with no headless
  spelling: a headless caller rewrites the reference in the `.csch`.

## 5. Tests (minimal)

1. Push In is not available on a library part. The command's `CanExecute` is false, with the reason.
2. Edit Vendor Model is greyed for a SPICE-text part, a `ReadOnly` library and an unwritable folder, each
   with its own reason.
3. Edit, then save: the `.cnl` changes, `PublishedHash` does not, and `check` warns "edited since
   exported". A placed instance in another open schematic runs the new netlist.
4. An edit adding a kit part: the `.csch` is saved, the `.cnl` is unchanged, and `check` reports the
   mismatch as an error.
5. Copy to Workspace: the copied cell's S-parameters equal the library part's to 1e-12. Re-pointing every
   instance is one undo, and keeps wires and values.
6. The refresh logic exists once: a source scan of the view models and `src/Cli` finds no copy of it.

Run only the classes you touch.

## 6. Docs

`pdk-integration.md#vendor-placed` (a placed model part: no Push In, the two commands, "edited since
exported"), `schematic-editor.md#hierarchy` (one sentence: a library part cannot be pushed into), and
`cli.md` (`--refresh`, `import part` on a model). UiSnapshot: the context menu on a library part with
Edit greyed for a read-only library, and the re-point question. Inspect both and list them in the
completion note.
