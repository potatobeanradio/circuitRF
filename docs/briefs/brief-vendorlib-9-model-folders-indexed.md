# Brief VL-9 — model folders in a vendor library: index, palette, placement, run

**Series:** [Model libraries](brief-vendorlib-6-model-library-format.md) · **Tag:** `R-vl9-n` · **Phase:** index, UI, extraction
**Area:** `src/Design/VendorLibraries/VendorLibraryIndex.cs`, `VendorLibraryRefs.cs` (`Classify`, `HasKitStructure`,
`Validate`), `VendorPart.cs`, `VendorLibrarySearch.cs`, new `src/Design/VendorLibraries/ModelLibrary/ModelNetlist.cs`
and `VendorModelSymbolProvider.cs`, `src/Design/Schematic/CellSymbolResolver.cs`, `NetExtractor.cs`,
`src/Ui/ViewModels/Dock/PaletteTool.cs`, `src/Ui/ViewModels/ParameterEditorViewModel.SpiceModel.cs` (the vendor
panel), `src/Cli/Find.cs`, `src/Cli/Check.cs`
**Depends on:** 6, 8

---

## 0. Why

Briefs 1–5 index one kind of part: a `.subckt` block in a SPICE file. A model folder (brief 6) is a second
kind, and it should arrive by the same route — **Add Vendor Library…**, the same palette menus, the same
search, the same placed-part panel. A user should not need to know which kind a part is in order to find
and place it.

## 1. `R-vl9-1` — Add and the index

- **`R-vl9-1a`** `Classify`: a folder holding any `part.cpart` is a vendor library. `HasKitStructure`
  never looks inside a model folder. A model's `.csch`, `.cnl`, `.va` and `.s2p` are its own files, not kit
  structure. Without this, every model library is sent to the kit import.
- **`R-vl9-1b`** The index walk treats a model folder as a **leaf**. It reads `part.cpart` and nothing else
  there, and never descends into it for SPICE files. A `.lib` in `models/` is never listed as a part.
- **`R-vl9-1c`** **One entry type, one search.** Either extend `VendorLibraryEntry` with the model fields
  (folder, pins, parameters, glyph, kind), with the SPICE byte block unused, or add a sibling record behind
  a common interface. Either way `VendorLibrarySearch`, the palette and `find --parts` handle both kinds
  through one path. `part.cpart`'s `Description`, `Value`, `Frequency` and case codes fill the same fields
  a SPICE header fills, so value-aware search (`10p`) and the case footprint work unchanged.
- **`R-vl9-1d`** A model folder whose `part.cpart` cannot be read is counted and named in the library's
  summary, as an unreadable SPICE file is. It never stops the rest of the index.
- **`R-vl9-1e`** The responsiveness rules of overview §3 hold, **gated by counters**: an index build reads
  one `part.cpart` per model folder and opens no `.cnl`, `.csch` or `.csym`.

## 2. `R-vl9-2` — placing a model

- **`R-vl9-2a`** A placed model is a **cell instance** whose cell reference is the virtual form
  `vendormodel://<library>/<model folder, library-relative>`. Resolve it in `CellSymbolResolver` beside the
  other virtual forms (`pdk://`, `spicemodel://`, `composedfet://`), and add it to `NeedsNoBaseDirectory`
  when it carries a glyph. A distinct scheme keeps it from being mistaken for a SpiceModel's
  `vendorlib://` `File`.
- **`R-vl9-2b`** `VendorModelSymbolProvider` draws the model folder's primary `.csym`, through `CellFolder`'s
  primacy rule. The instance also stores `Glyph` (from `part.cpart`), so that with the library missing it
  still draws the right shape, in brief 2's `NotFound` treatment, with brief 1's Repair.
- **`R-vl9-2c`** The instance's parameters are the model's cell parameters, with `part.cpart` defaults,
  and are edited in the Parameter Editor as a cell instance's are. The vendor panel (brief 3) shows the
  library, part, description, frequency range and the **?** button, as it does for a SPICE part. The
  **Part** field searches the same library's models **with the same pin names**. **Value ▼/▲** applies
  to passive models that state a `Value`, under brief 3's "same kind" rule with the category folder in
  place of the file.
- **`R-vl9-2d`** Recently Used, drag payloads and `VendorPartRef` carry the kind, so a model remembered
  from an earlier session places as a model.

## 3. `R-vl9-3` — running a model

- **`R-vl9-3a`** New `ModelNetlist` in `src/Design`:
  - `FromCell(cellDir)` extracts a cell's schematic as **define blocks only** (the top cell and its
    sub-cells), through the same `NetExtractor` and `CnlWriter` a run uses. It wraps the cell in a
    transient bench and keeps only the `define`s. Brief 10 writes a model's `.cnl` with it, and `check`
    compares with it.
  - `Read(modelDir)` reads a model's `.cnl` and returns its cells.
- **`R-vl9-3b`** The extractor, for a `vendormodel://` instance:
  - reads that model's `.cnl` **once per distinct part per run** (counter)
  - rebases its file references (brief 8)
  - renames its cells to names that cannot collide with the design's own, using the `claimedCells`
    pattern `SpiceModelNetlist` already uses
  - emits the instance with its parameter overrides

  **The `.csch` is never read to run** (D1).
- **`R-vl9-3c`** A missing library, a part the library no longer holds, or an unreadable `.cnl` is an
  extraction **refusal** naming the part and the library (brief 3's rule). It is never a silent skip.
- **`R-vl9-3d`** Brief 3's frequency-range warning reads `part.cpart`'s `Frequency`. For a model, a DC
  operating point is **not** exempt unless `Low` is 0 Hz. Brief 3 exempted DC because a passive R/L/C
  network's DC solve is exact. A model built on measured S-parameters that start at 100 MHz has no such
  guarantee.

## 4. `R-vl9-4` — Validate, check, find

- **`R-vl9-4a`** Manage Libraries' **Validate** and `circuitrf check` run brief 6's model-folder checks
  and add one more: the `.cnl` must equal `ModelNetlist.FromCell` of the folder's own `.csch`. A
  difference is an **error**: *"<part>: its netlist is not what its schematic extracts to; the part runs
  its netlist. Export it again, or Edit Vendor Model and save."* This is D1's gate.
- **`R-vl9-4b`** `find --parts` lists models with a Kind column and prints the `vendormodel://` reference
  an agent writes into a `.csch` by hand.
- **`R-vl9-4c`** Archive Workspace is unchanged: a library row, unticked by default (brief 3).

## 5. Tests (minimal)

1. `Classify` calls a folder of two synthetic model folders a vendor library, not a kit.
2. Index counters: N model folders give N `part.cpart` reads and zero `.cnl`/`.csch`/`.csym` reads. A
   `.lib` inside `models/` is not an entry.
3. Search finds a passive model by value (`10p`), alongside a SPICE part of the same value.
4. **Same answer as a local cell:** a synthetic RLC model placed from a library, and the same cell placed
   from the workspace, give S-parameters equal to 1e-12.
5. Two instances of one part read its `.cnl` once.
6. A missing library is a refusal naming the part. The placed instance draws its stored glyph as
   `NotFound`.
7. A `.cnl` edited by hand away from its `.csch` is a `check` error.

Run only the classes you touch.

## 6. Docs

`pdk-integration.md#vendor`: a vendor library may hold model folders too, with a link to
`model-library-format.md`. `schematic-editor.md#palette-vendor`: a model part in the palette and on the
canvas. `cli.md`: `find --parts`' Kind column and the `vendormodel://` reference. UiSnapshot: the palette
sub-menu holding one SPICE part and one model part, and a placed model with its vendor panel. Inspect both
and list them in the completion note.
