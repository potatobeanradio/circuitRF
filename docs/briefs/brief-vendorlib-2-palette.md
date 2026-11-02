# Brief 2 — the palette: a Type menu with sub-menus, a part list that scales, search that knows values

**Series:** [Vendor libraries](brief-vendorlib-0-overview.md) · **Tag:** `R-vl2-n` · **Phase:** UI
**Area:** `src/Ui/Views/Palette/PaletteToolView.axaml(.cs)`, `src/Ui/ViewModels/Dock/PaletteTool.cs`
(`PaletteCategoryEntry`, `BuildCategories`, `DisplayedItems`, `ArmedFor`, the MRU),
`src/Ui/Schematic/PendingPlacement.cs`, `src/Ui/Schematic/PlacementService` (arming),
new `src/Design/VendorLibraries/VendorLibrarySearch.cs`, `src/Design/Schematic/SpiceModelSymbolProvider.cs`
(`IsPanelParameter`, the glyph), `src/Design/Schematic/LibraryCatalog.cs` (`PaletteItem`)
**Depends on:** 1 · **Blocks:** 3
**Rule:** overview §0. The overview's responsiveness rule is this brief's first acceptance criterion.

---

## 0. Why the palette as it stands cannot show a library

The palette is a non-virtualized `WrapPanel` of `PaletteTile`s, each rendering its own glyph.
`BuildCategories` lists each kit's categories as indented entries in one flat `ComboBox`. At 34,000
parts both break:

- 34,000 realized tiles is a frozen panel.
- One library has dozens of series under several folder groups, which a flat indented list cannot
  carry.
- 34,000 identical capacitor glyphs tell the user nothing. What tells parts apart is text: part
  number, value, size, dielectric, voltage.

## 1. `R-vl2-1` — the Type menu, with sub-menus

- **`R-vl2-1a`** The category `ComboBox` becomes a **drop-down button opening a cascading menu**. The
  button shows the current selection as a breadcrumb, e.g. *Library name ▸ Capacitors ▸ Series*,
  trimmed from the left when narrow. The menu contains:
  - at the top, the virtual and built-in categories, in today's order and flat
  - a separator
  - one item per PDK kit, whose kit categories (today's indented entries) move into its sub-menu
  - a separator
  - one item per vendor library, whose sub-menu is the library's **own folder tree**, with each file
    as a leaf. Leaves show their part count, e.g. *Series (12,619)*.
  - last, **Add Vendor Library…**
- **`R-vl2-1b`** Every node with children opens with an **"All in <node>"** first item, so a folder is
  selectable as well as openable. A folder with exactly one child collapses into it, and a tree that
  starts with a single folder skips it, so the user never clicks through a level that offers no
  choice. Show the collapsed path in the breadcrumb.
- **`R-vl2-1c`** The selection survives a rebuild exactly as kit selections do today (`SetPdkParts`
  keeps the selection when it still exists). If the selected node is gone, fall back to All.
- **`R-vl2-1d`** Keyboard: the menu's native arrow-key navigation. The button takes focus like the
  combo box did. Do not write custom key handling.

## 2. `R-vl2-2` — a virtualized part list for a library selection

- **`R-vl2-2a`** When the selection is a vendor library or any node inside one, the tile area becomes
  a **virtualized list** (`ListBox` with a virtualizing panel). One row per part: part number, value,
  then the description fields (or the file stem for a part with none), then the frequency range.
  - Sort by value within the selection by default. Clicking a column header sorts by that field.
  - Every other selection keeps today's tile grid unchanged.
- **`R-vl2-2b`** A row **arms placement on click and drags onto a canvas**, exactly as a tile does,
  through the same `PlacementService`. `PendingPlacement` gains the vendor part's identity (library
  name + part name). `ArmedFor` must compare that identity, never the `SymbolKind`. Every vendor part
  shares `SpiceModel`, so a kind comparison lights up every row at once, which is the bug `ArmedFor`'s
  own comment already describes for kit parts.
- **`R-vl2-2c`** While a library's index is still building (brief 1 §2e), the list shows *Indexing
  <name>…* in place of rows. The palette is never blocked.
- **`R-vl2-2d`** Recently Used records vendor parts as well as `SymbolKind`s: the same MRU, de-duped,
  capped, persisted. A designer places the same 100 pF part twenty times, and it should be one click
  away. In Recently Used a vendor part shows as a compact tile with its glyph (§3) and its part
  number as the label.

## 3. `R-vl2-3` — placing a part, and its glyph

- **`R-vl2-3a`** Placing a vendor part creates a `SymbolKind.SpiceModel` instance with:
  - `File = vendorlib://<library>/<relative file>`
  - `Name = <part>`
  - `Glyph = C | L | (blank)`, taken from the indexed Property quantity

  `Glyph` joins `SpiceModelSymbolProvider.IsPanelParameter`. It is stored on the instance so a
  schematic whose library is MISSING still draws a capacitor where a capacitor was, marked by the
  existing `NotFound` treatment.
- **`R-vl2-3b`** With `Glyph` = C or L and two ports, the symbol is the **built-in capacitor or inductor
  geometry** at the standard pitch, so wiring and rotation behave exactly like a placed C or L. It is
  labelled with the part number and the value. Any other part (more ports, other quantity) keeps
  today's generated SpiceModel box. **Resolving this symbol reads no file**: it comes from the
  instance's own parameters and, for pin names, the index.
- **`R-vl2-3c`** Instance prefix: `C` / `L` for those glyphs, otherwise the SpiceModel's own prefix.
- **`R-vl2-3d`** **The footprint comes from the part's own case size.** A library carries no land
  patterns, so the existing footprint machinery supplies them.
  - When the index entry has a case-code pair (brief 1 §2a) and the technology is a board
    (`LandPatternLayers.IsBoardTechnology`), the placed part's `Footprint` is that case at the default
    density (`FootprintRef.For(case)`, written `smt:<imperial>@N`).
  - The case is found only when a `SmtCaseTable` row matches **both** codes: its `Code` equals the
    imperial code and its `MetricTwin` equals the metric code. Matching either code alone is exactly
    the 0201-imperial / 0201-metric error `footprints.md` warns about. The pair removes the ambiguity,
    so use all of it.
  - No match (a size the table lacks), no pair (every inductor: its header states no size, and the
    part NUMBER is never decoded), or no board technology gives **None**. The user picks from the
    ordinary Footprint picker. There is no guess and no nearest size.
  - Put the decision in `src/Design/Layout/Footprints/FootprintDefaults` beside `For(SymbolKind,
    Technology)`, e.g. `ForCaseCodes(metric, imperial, technology)`, so `SchematicViewModel`'s
    placement hook and `find --parts` (brief 1 §5b, which prints the `Footprint` to write) call one
    function.
  - It is applied **at placement only**, on `FootprintDefaults.For`'s terms (R-fp2-3d). Brief 3 §1d
    adds the one exception: a part change.
  - Confirm the SpiceModel kind is `FootprintDefaults.IsOffered`, so the Footprint row appears in the
    parameter editor. If it is not, offer it for `vendorlib://` instances only.
  - In `docs/user/src/reference/footprints.md` §Imported parts, add one sentence: a vendor part's
    land pattern is circuitRF's generated pattern for its stated case size, **not** the
    manufacturer's recommended land.

## 4. `R-vl2-4` — search: tokens, and values

- **`R-vl2-4a`** `VendorLibrarySearch.Match(entry, query)` lives in `src/Design`, shared with `find
  --parts`. The query splits on whitespace and every token must match (AND). A token matches when
  either:
  - it is a case-insensitive substring of the part name, the description, the file stem or a folder
    segment, or
  - it **reads as a value**: a number with an engineering suffix and an optional unit (`10p`, `10pF`,
    `2.2n`, `4n7`, `1u`), and the entry's Property value equals it within 0.5 %. So `10p` finds a part
    whose header says `1.00e-05[uF]`.

  `4n7`-style infix notation is a standard spelling for 4.7 n, and it parses here.
- **`R-vl2-4b`** Under a library selection, search filters that list.
- **`R-vl2-4c`** Under **All** with a query, library parts do **not** enter the tile grid. One row
  per library with matches appears at the top: *"312 parts in <library> match"*. Clicking it selects
  that library with the query kept. The All grid therefore never holds more than the built-ins plus
  kit parts.
- **`R-vl2-4d`** **A keystroke reads no file.** Search runs over the in-memory index only. It runs on
  the UI thread when the measured per-keystroke cost over 35,000 entries is under one frame. Record the
  number in `src/Ui/RESOLVED.md` from the scratch harness (overview §3). If it is over, move the match
  off the UI thread with a cancel-on-next-keystroke, and record why.

## 5. Tests (minimal — one per claim; counters, never timings)

1. **Virtualization is real:** a headless view test with a generated 20,000-entry index under one
   library selection realizes at most (visible rows + the panel's buffer) row containers, and still
   realizes at most that many after scrolling to the end. Use the existing headless Avalonia harness
   pattern in `tools/UiSnapshot` / `tests/Ui.Tests`.
2. **A keystroke reads no file:** typing a query with a file-read counter (the index's own, or an
   injected file-system seam) gives zero reads.
3. **Value search:** `10p` matches a `1.00e-05[uF]` header, `4n7` matches 4.7 nH, and `10p C0G`
   ANDs.
4. **The Type menu** builds the folder tree with leaf counts, "All in" items, and single-child
   collapse.
5. **Arming identifies the part:** arming one vendor part leaves every other row unarmed.
6. **Glyph without a file:** resolving a placed C-glyph part whose library folder is absent draws the
   capacitor geometry and reads no file.
7. **Footprint from the case pair:**
   - `1005M(0402)` on a board technology gives `smt:0402@N`.
   - `0603M(0201)` gives `smt:0201@N`, and `0201M(008004)` gives `smt:008004@N`. Both codes must
     match, so the two 0201s never cross.
   - A pair the table lacks, a part with no pair, and a non-board technology each give None.

Run only the classes you touch.

## 6. Scope

No parameter panel, extraction, frequency warning, archive or `check` (brief 3). No facet drop-downs:
the menu tree, sortable columns and value-aware search cover it, and the owner chose sub-menus over
filters.
