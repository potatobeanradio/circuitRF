# Brief VL-6 — model libraries: series overview and the folder format

**Series:** [Vendor libraries](brief-vendorlib-0-overview.md), second part (VL-6…13) · **Tag:** `R-vl{n}-m`
**Area:** new `src/Design/VendorLibraries/ModelLibrary/`, `src/Cli/DocumentKinds.cs`, `src/Cli/Check.cs`, `src/Cli/Serve/ToolCatalog.cs` (the `reference` topic),
new `docs/user/src/reference/model-library-format.md`, `docs/user/src/_nav.txt`
**Found by:** a public discussion (2026-11-02) asking how to turn a nonlinear device model into a library
part with a transistor symbol, and whether such a library can be shared. Today the only library a workspace
can add is a folder of SPICE passive subcircuits, a Verilog-A part always draws as a plain box, and nothing
packages a cell as a part someone else can place.
**Rule:** overview §0's naming rule holds unchanged. No manufacturer, part number or other simulator is
named anywhere. Fixtures are synthetic (`TSTFET100`, `TSTCAP0402`).

---

## 0. What this series adds

A **model library** is a folder of **model folders**. Each model folder is a part someone drew in circuitRF
as an ordinary cell, with pins and cell parameters. It can contain any component circuitRF has: R/L/C,
Verilog-A, a ComposedFET, an SnP, a SpiceModel, sub-cells. **Export to Library…** packages that cell into a
self-contained folder. **Add Vendor Library…** then lists it in the palette next to the SPICE parts briefs 1–5
already index.

**circuitRF defines this format, and other tools adapt to it.** Each model ships its netlist as a `.cnl`,
which is documented plain text, and every model file it uses (`.va`, `.sNp`, …) sits beside it. Another
simulator can read the `.cnl` or take the `.va` directly. circuitRF does nothing on another tool's behalf.

## 1. The briefs

| # | Brief | What it adds | Depends on |
|---|---|---|---|
| 6 | this one | the folder format, its readers and writers, `check` on it, the user-doc page that is the published spec | — |
| 7 | [Symbol on a Verilog-A part](brief-vendorlib-7-veriloga-symbol.md) | a `Symbol` parameter; a 3- or 4-terminal model draws the FET glyph, as a ComposedFET does | — |
| 8 | [Paths resolve against the owning folder](brief-vendorlib-8-owning-root-paths.md) | a relative file reference inside a model folder, or inside a cell from a referenced workspace, resolves where that document lives | 6 |
| 9 | [Model folders in a vendor library](brief-vendorlib-9-model-folders-indexed.md) | Add / index / palette / placement / run through the `.cnl` / Validate | 6, 8 |
| 10 | [Export to Library](brief-vendorlib-10-export-to-library.md) | the toolbar command and dialog; copying files in; the self-check; `circuitrf export part` | 6, 8, 9 |
| 11 | [Technology parts become S-parameters](brief-vendorlib-11-technology-parts-to-snp.md) | every stackup-bound part is replaced by an SnP sampled on the author's technology, with DC always included | 10 |
| 12 | [Edit Vendor Model / Copy to Workspace](brief-vendorlib-12-edit-and-copy.md) | the two right-click commands; Push In refused on a library part | 9, 10 |
| 13 | [A model's tolerances and corners](brief-vendorlib-13-model-tolerances-and-corners.md) | spreads written as distribution functions; named corners in the model; both in the Yield and Corners panels | 9, 10 |

Build in order. Brief 7 is independent and may be built first.

The unwritten catalogue + Touchstone-edition brief, previously numbered VL-6, becomes **VL-14**.

[VL-15, protected models](brief-vendorlib-15-protected-models.md), follows: compiled-only model folders and
signed libraries. It depends on 6, 9 and 10, and is independent of 11–14.
[VL-16, Compile Netlist](brief-vendorlib-16-compile-netlist.md) follows it: Export turns a cell, ComposedFETs
included, into one compiled module, and proves that module matches the cell.

## 2. Decisions (owner, 2026-11-02)

- **D1 — The `.cnl` is what runs; the `.csch` is the source.** Both are shipped. They must agree: the
  `.cnl` is exactly what extracting the `.csch` writes, and `check` says so when they do not (brief 9).
- **D2 — "Export to Library…"** is the command's name.
- **D3 — A model never carries a technology.** Every part that takes its substrate from a technology is
  converted to an SnP at export (brief 11): linear, **0 to 20 GHz, 1001 points, with DC always included**,
  on the EM Setup's sweep editor.
- **D4 — No Push In on a placed library part.** Right-click ▸ **Edit Vendor Model…** opens it for editing,
  and **Copy to Workspace…** makes an editable local cell (brief 12). Neither is offered for a SPICE-text
  part, which has no schematic.
- **D5 — Tolerances are distribution functions written in the model's own values.** Corners are named
  parameter sets the model declares (brief 13).
- **D6 — Kit (PDK) parts cannot be exported.** A model must run on a machine that does not have the kit,
  and a kit's terms usually forbid redistribution.
- **D7 — Anything that changes a result lives in the `.cnl`.** `part.cpart` holds only what finds and
  describes the part. Where the index needs a fact the `.cnl` also states (pins, parameters), Export writes
  both from one source, and `check` proves they agree.

The file names in §3 (`library.clib`, `part.cpart`, `models/`, `cells/`, `docs/`) are this brief's choice.
Change them before the build starts if wanted; after VL-6 ships they are a published format.

## 3. The format

```
<library>/
  library.clib                 optional; JSON
  <Category>/<Sub>/            ordinary folders = palette sub-menus (brief 2's rule, unchanged)
    <PartNumber>/              a model folder — and a valid cell folder
      .ccell                   the cell: Parameters, PrimarySchematic, NumPorts (existing format)
      schematic/<PartNumber>.csch
      symbol/<PartNumber>.csym
      part.cpart               JSON; its presence is what makes this folder a model folder
      <PartNumber>.cnl         the netlist (D1)
      models/                  .va (+ every `include`d file), .cmodel, .sNp, SPICE files a SpiceModel names
      cells/<Sub>/             sub-cell folders the model's schematic places, if any
      docs/                    optional; brief 3's ? button opens these
```

**Model folders and SPICE files can share one library.** The SPICE index never looks inside a model folder
(brief 9), so a `.lib` in `models/` is the model's own file and is never listed as a part.

### 3a. `library.clib`

```json
{
  "Format": "circuitrf-model-library",
  "FormatVersion": 1,
  "Name": "Example Devices",
  "Publisher": "",
  "LibraryVersion": "1.0.0",
  "License": "",
  "Url": "",
  "ReadOnly": false
}
```

Every key is optional except `Format` and `FormatVersion`. A library without the file takes its folder's
name (brief 1's rule). `ReadOnly: true` is a publisher saying "copy it to change it". It greys out Edit Vendor
Model (brief 12), and nothing else.

### 3b. `part.cpart`

```json
{
  "Format": "circuitrf-model",
  "FormatVersion": 1,
  "Part": "TSTFET100",
  "Cell": "TSTFET100",
  "Kind": "Active",
  "Description": "Low-noise pHEMT, packaged",
  "Pins": ["g", "d", "s"],
  "Parameters": [ { "Name": "Vds", "Default": "3 V" } ],
  "Glyph": "FET",
  "Value": "",
  "Frequency": { "Low": "0 Hz", "High": "20 GHz" },
  "Conditions": "",
  "Footprint": "",
  "CaseMetric": "", "CaseImperial": "",
  "Revision": 1,
  "PublishedHash": "sha256:…",
  "Converted": [ { "Instance": "TL1", "Type": "MLIN", "File": "models/TL1.s2p" } ]
}
```

- `Part` is the part number and equals the folder name. `Cell` is the top `define` in the `.cnl`.
- `Kind` is `Active` or `Passive`. `Glyph` is `R`, `C`, `L`, `FET` or empty (the model's own symbol).
- `Value`, for a passive, makes the part findable by value through brief 2's value-aware search
  (`10 pF`).
- `Frequency` is the range the model is valid over. Brief 3's out-of-range warning reads it.
- `Revision` counts exports over the same part number. `PublishedHash` is written by Export only (brief 12
  uses it to say "edited since exported"). It is SHA-256 over every file in the folder except `part.cpart`
  itself, taken in sorted relative-path order, with CRLF normalised to LF in text files (`.cnl .csch .csym
  .ccell .va .vams .sNp .lib .cir .sub .txt .md`). A checkout that changes line endings is then not an edit.
- `Converted` lists what brief 11 replaced. It is provenance, and nothing reads it to run.

### 3c. The `.cnl`

One or more `define … end` blocks, and **nothing at the top level except comments**. A model has no
analyses, no global variables, no `technology` line and no `measure` lines. The top cell is `Cell`, and the
others are its sub-cells. A file-valued parameter inside it is **relative to the model folder** (brief 8).
Brief 13 adds `corner` lines inside a `define`.

## 4. `R-vl6-1` — readers and writers

- **`R-vl6-1a`** `ModelLibraryManifest` (`library.clib`) and `ModelPartManifest` (`part.cpart`) in
  `src/Design/VendorLibraries/ModelLibrary/`: records, a `Read(path)` that returns the record or a refusal
  sentence, and a `Write(path, record)` through `AtomicFile`. An unknown key is kept on a round trip, and a
  `FormatVersion` above the one this build knows is a refusal naming both versions.
- **`R-vl6-1b`** `ModelPartHash.Compute(modelDir)` implements §3b's rule. It is a single function, because
  Export (10), Edit (12) and Validate (9) all call it.
- **`R-vl6-1c`** `ModelFolder.Is(dir)` (does it hold `part.cpart`) and `ModelFolder.OwningModelFolder(dir)`
  (the nearest ancestor that does, or null). Brief 8 builds on the second.

## 5. `R-vl6-2` — `check` and `explain`

- **`R-vl6-2a`** `DocumentKinds.Classify` recognises a model folder, a `part.cpart` and a `library.clib`.
- **`R-vl6-2b`** `circuitrf check <model folder>` reports, using validators that already exist wherever one
  exists (`check`'s own rule, CLAUDE.md):
  - the cell-folder checks `check` already runs on a cell
  - `part.cpart` unreadable or with a missing required key (error)
  - `Part` different from the folder name (error)
  - the `.cnl` missing, unreadable, or holding a top-level line other than a comment (error)
  - the `.cnl`'s top `define` name or pin list different from `Cell`/`Pins` (error)
  - a file reference leaving the model folder (error, which is what makes a model self-contained)
  - `PublishedHash` not matching the folder (**warning**: "edited since exported")
- **`R-vl6-2c`** `check <library>` runs 2b on every model folder and adds library-level findings, for
  example two model folders with one part number.

## 6. `R-vl6-3` — the published spec

- **`R-vl6-3a`** New user-doc page `docs/user/src/reference/model-library-format.md`, in `_nav.txt` beside
  the vendor-library material, linked from `pdk-integration.md#vendor`. Audience: someone writing a library
  by hand or by script, and someone writing another tool's reader. Contents: §3 with every key in a table;
  §3c's rules; what a reader must do (resolve relative to the model folder; run the `.cnl`; never assume a
  technology); a complete small example that the page's own test builds and `check`s; and how to make a
  library (Export to Library, brief 10, with its CLI spelling).
- **`R-vl6-3b`** The page is also served as the `reference` topic `model-library`, the way `cmodel` is.
  The MCP `reference` tool and `circuitrf reference` then give an agent the same bytes.

## 7. Tests (minimal)

1. `part.cpart` and `library.clib` round-trip, keeping an unknown key. A higher `FormatVersion` is
   refused.
2. `PublishedHash` is unchanged by CRLF↔LF in a text file and changed by one changed byte.
3. `check` on a synthetic model folder: clean; then one error per 2b row, each written into its own copy.
4. The doc page's example folder `check`s clean. The test writes it from the page's own text, so the
   published example cannot rot.

Run only the classes you touch.

## 8. Not in this series

- Shipping precompiled `.osdi` files beside a `.va`. Everyone who places a `.va` model needs a Verilog-A
  compiler (`veriloga.md` already says so), and the format page says so too.
- An optional SPICE `.subckt` export of a model whose elements all map across.
- Artwork. A model has no layout view. Its footprint, when given, is brief 2's case-code footprint.
- A model that carries a technology (D3).

## 9. On completion (every brief in 6–13)

Findings go in `src/Design/RESOLVED.md`, and the UI half in `src/Ui/RESOLVED.md`, never in any
`CLAUDE.md`. Each brief names the user-doc pages it changes. Do not run DocGen: the owner regenerates at the
end of the series. **Do not reply to the discussion** until the series is built and committed; the owner
will.
