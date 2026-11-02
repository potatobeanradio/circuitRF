# Brief VL-10 — Export to Library

**Series:** [Model libraries](brief-vendorlib-6-model-library-format.md) · **Tag:** `R-vl10-n` · **Phase:** authoring, UI, CLI
**Area:** new `src/Design/VendorLibraries/ModelLibrary/ModelLibraryExport.cs`; `src/Ui/Archive/DocumentFileRefs.cs`
(moves to `src/Design`, §2c); new `src/Ui/Views/Dialogs/ExportToLibraryDialog.axaml(.cs)` and its view model;
the schematic toolbar (`schematic-editor.md#toolbar`); `src/Cli/Authoring.cs` (or a sibling) and
`src/Cli/Serve/ToolCatalog.cs`
**Depends on:** 6, 8, 9

---

## 0. Why

Nothing in circuitRF turns a cell into a part someone else can place. Today the only way is to send a
whole workspace, and its references to files outside it break on arrival. Export to Library makes a model
folder (brief 6) from a cell. The folder is self-contained, checked, and listed the next time the library
is indexed. An empty target folder starts a new library.

## 1. `R-vl10-1` — the command

- **`R-vl10-1a`** A schematic-toolbar button **Export to Library…**, also on the **File** menu. It is
  enabled when the active document is a **saved cell's primary schematic with at least one Pin**.
  Otherwise it is disabled, with a tooltip that says which condition failed (a test bench, unsaved, or
  no pins).
- **`R-vl10-1b`** **One function does the work**: `ModelLibraryExport.Export(cellDir, ExportOptions,
  RunControl?)` in `src/Design`. The dialog and the CLI both call it. Neither has any logic of its own
  (`Authoring.cs`' rule, and a comment-stripped source scan holds it, as the authoring verbs' gates do).

## 2. `R-vl10-2` — what Export does, in order

- **`R-vl10-2a` Refusals first; nothing is written until all pass.** Each is a sentence that names the
  instance:
  - a kit (`pdk://`) part (D6)
  - a file reference that does not resolve
  - a cell with no Pin
  - a part that takes its substrate from the technology (*"TL1 (MLIN) takes its substrate from the
    technology; a model cannot carry one."*). Brief 11 replaces this refusal with a conversion.
- **`R-vl10-2b` Build in a temporary sibling folder, then rename.** `<library>/<category>/<part>.tmp-*`,
  renamed to `<part>` only when §2f passes. A failure or Cancel leaves no folder behind. Replacing an
  existing part number needs confirmation in the dialog and `--replace` headlessly. The replacement's
  `Revision` is the old one plus one.
- **`R-vl10-2c` Copy the cell and everything it uses.** Write `.ccell`, `schematic/` and `symbol/`, renamed
  to the part number. Sub-cells, including any from a referenced workspace, go to `cells/<name>/`, and
  their instance references are rewritten.
  - Every file a document references goes to `models/`, and its reference is rewritten **relative to
    the model folder** (brief 8). That covers SnP / ComposedFET / VerilogA / SpiceModel `File`, a `.csym`
    named by `Symbol`, and anything else.
  - A `.va` brings every file it `` `include ``s, keeping their relative layout so the include still
    resolves.
  - Name collisions in `models/` get a numeric suffix, and the reference follows it.
  - Finding the references is **`DocumentFileRefs`' job**: it already finds and rewrites every file
    reference a design document holds, and two walks would disagree. Move it from `src/Ui/Archive` to
    `src/Design`. The archive keeps calling it from there.
  - A placed **vendor part** inside the cell is copied in too: a SPICE part's own `.subckt` block into
    `models/`, and a model part as a sub-cell. The dialog lists each one with the library it came from,
    because that library's terms now travel with this one.
- **`R-vl10-2d` Write the netlist.** `<part>.cnl` = `ModelNetlist.FromCell` of the **copied** cell
  (brief 9), so its references are already model-relative. Top-level lines other than comments are not
  possible by construction. Test that anyway.
- **`R-vl10-2e` Write `part.cpart`** from the dialog's fields and the cell (pins and parameters from the
  `.ccell`, so D7's agreement holds by construction). Write `library.clib` if the library has none (§3b).
  `PublishedHash` is computed last.
- **`R-vl10-2f` Check, then self-check, before the rename.**
  - Brief 6's `check` on the new folder must be clean of errors.
  - The **self-check** proves the copy changed nothing. It builds a transient bench with a 50 Ω port from
    every pin to ground, at the cell's default parameters, and compares the source cell with the
    exported model: S-parameters at 11 frequencies (DC, then log-spaced over the stated `Frequency`
    range, or 1 MHz–10 GHz when none is stated), and the DC operating point. The tolerance is 1e-9,
    relative, per entry. Equality is the claim, not physics, so an unbiased transistor is a valid
    subject.
  - A mismatch is a refusal naming the worst entry and frequency, and the temporary folder is deleted.
- **`R-vl10-2g`** One Messages line on success: *"Exported TSTFET100 to <library>/Amplifiers (revision
  1)."*

## 3. `R-vl10-3` — the dialog

- **`R-vl10-3a` Target.** The **Library** folder (a picker, plus a drop-down of the workspace's
  referenced vendor libraries) and the **Category** (a combo of the existing sub-folders; typing
  `Amplifiers/LNA` creates them).
- **`R-vl10-3b` Library fields**, enabled only when the folder has no `library.clib`: Name (the folder's
  name), Publisher, Version (`1.0.0`), License. An empty folder is a new library.
- **`R-vl10-3c` Part fields.**
  - **Part number** (the cell's name; `NameValidator`)
  - **Description**
  - **Kind**: Active when any component is nonlinear (VerilogA, ComposedFET, the FET family, diodes,
    SDD, ExtDevice), Passive otherwise. Shown, and changeable.
  - **Value** (Passive only; optional)
  - **Frequency range** (optional)
  - **Conditions** (free text: bias, temperature)
  - **Footprint** (brief 2's case pair; optional)
  - **Documents** (files to copy into `docs/`)
- **`R-vl10-3d` Symbol**, one picture with a menu, as in brief 7. The default, first match wins:
  1. the cell's own primary `.csym`, if it has one
  2. 3 or 4 pins, Active, and the pin names bind under `FetPinBinding` (brief 7) → the FET glyph
  3. 2 pins, Passive, with a `Value` whose unit is a capacitance, inductance or resistance → C, L or R
  4. the auto-generated box

  A built-in glyph is **written into the model as `symbol/<part>.csym`**, with its pins named after the
  cell's pins, so the model draws the same in any circuitRF without a rule having to be re-applied. Write
  `Glyph` to `part.cpart`.
- **`R-vl10-3e`** The dialog lists what will be copied in (files, sub-cells, vendor parts) and every
  refusal, live, before **Export** is pressed. Export is disabled while any refusal stands.
- **`R-vl10-3f`** **Add this library to the workspace**: a checkbox, on when the workspace does not
  already reference the folder, so the part is in the palette as soon as the export finishes.
- **`R-vl10-3g`** Progress and Cancel ride `RunControl`. The self-check can take seconds on a large model.

## 4. `R-vl10-4` — headless

`circuitrf export part <cell folder> --library <dir> [--category a/b] [--part PN] [--description …]
[--kind active|passive] [--value …] [--freq-low … --freq-high …] [--symbol …] [--replace] [--json]`.

- Library fields come from `--library-name/--publisher/--license/--library-version`, used only when the
  library is new.
- A choice the dialog would have **asked** about is a refusal naming the flag that answers it. Example:
  a vendor part inside the cell needs `--include-vendor-parts`, so its library's terms are accepted
  explicitly.
- The created paths are the result, on stdout and in `--json`.
- Expose it to agents through `ToolCatalog`, as `import part` is exposed.

This is a **new verb with a noun**, `export`, which leaves room for later `export` nouns. If the owner
would rather fold it under an existing verb, decide before the build.

## 5. Tests (minimal)

1. **End to end:** a synthetic cell (R, C, a VerilogA with an `` `include ``, an SnP outside the
   workspace, one sub-cell) exports into an **empty** folder. The result `check`s clean, has
   `library.clib`, has every file in `models/` with model-relative references, and passes the self-check.
2. **Place it and run it:** the exported model, added as a vendor library and placed (brief 9), gives the
   source cell's S-parameters to 1e-9. This is the self-check's claim, proved from the outside.
3. Each refusal in 2a is one test, and none leaves a folder behind.
4. Replace without confirmation is refused. With it, `Revision` goes 1 → 2.
5. Symbol defaults: a `g d s` active cell gets the FET glyph; a 2-pin `10 pF` passive gets C; a cell
   with its own `.csym` keeps it.
6. The CLI process's output folder equals the in-process call's, byte for byte, `part.cpart` included.
   The format holds no timestamp, so there is nothing to exclude.
7. A source scan shows that the view model and `src/Cli` hold no copy of the export logic.

Run only the classes you touch.

## 6. Docs

`model-library-format.md` § Making a library (brief 6 left it as a stub). `schematic-editor.md#toolbar`:
the new button. `cli.md`: `export part`. UiSnapshot: the dialog for a new library with one refusal
showing, and the same dialog ready to export. Inspect both and list them in the completion note.
