# Brief 5 — the acceptance matrix, and the page a user reads

**Series:** [gdsfactory kits](brief-gdf-0-overview.md) · **Tag:** `R-gdf5-n` · **Phase:** acceptance, docs
**Area:** new `tools/GdsfactoryAcceptance/` (`run.py`, `README.md`), `tests/Ui.Tests/PackagingScriptTests.cs`,
`docs/design/pcell-gdsfactory-bridge.md`, `docs/user/src/reference/pdk-authoring.md` (or a new page beside it
in `_nav.txt`), `src/Design/RESOLVED.md`
**Depends on:** 1, 2, 3 · **Blocks:** —
**Rule:** overview §0. The harness contains **no kit name**: it takes a module name as an argument.
Results are recorded by process.

---

## 0. What "extensive" means here

Every cell of every kit in overview §2 (A–E) goes through circuitRF, **headless and end to end**:

1. import
2. consent
3. describe
4. generate
5. export

Each result is compared against **gdsfactory's own GDS** for the same cell at the same parameters.
This is done at the defaults, and again with each numeric parameter varied one at a time. Pins are
checked against ports. A cell failing anywhere is attributed to one of three causes:

- **gdsfactory or the kit:** it fails in gdsfactory alone too.
- **circuitRF:** it builds in gdsfactory and fails, or differs, through circuitRF.
- **Out of scope:** a decision in the overview excludes it.

## 1. `R-gdf5-1` — the harness

`tools/GdsfactoryAcceptance/run.py` runs **under the kit's own interpreter**, so it can build the
reference GDS in-process:

```
<kit-python> -I tools/GdsfactoryAcceptance/run.py --module <m> --circuitrf <path-to-cli> --out <dir>
                                                  [--variations 2] [--cells <glob>] [--max-cells N]
```

1. `circuitrf new workspace <dir>/ws`, then
   `circuitrf import pdk --module <m> --python <sys.executable> --into … --allow-code --technology`.
   The import report is saved.
2. **The parameter sets.** For each offered cell:
   - the defaults
   - then each `real` at ×0.5 and ×2
   - each `integer` at default + 1
   - each `flag` flipped

   One parameter at a time, **never a cross product**. A set gdsfactory itself refuses is recorded as
   the kit's refusal and is not sent to circuitRF.
3. **The reference.** `gf.get_component(name, **params).write_gds(…)`, in the harness process.
4. **circuitRF's version.**
   - Author one `.clay` per cell, placing one instance per parameter set, using the instance syntax
     `circuitrf reference topic=layout` documents.
   - Then run `circuitrf convert <cell>.clay -o <cell>.gds`.
   - **First, verify that `convert` regenerates kit cells headlessly through the worker.** If it
     exports a generated instance as empty, or as a cached stale cell, that is a circuitRF defect.
     Fix it in this brief, because a headless export that silently drops generated geometry is the
     worst outcome this series could produce.
5. **The comparison**, per layer:
   - XOR the two polygon sets with gdsfactory's own boolean API.
   - Pass when the XOR area is ≤ (edge count × 1 DBU × 1 DBU), which allows for rounding and nothing
     more.
   - Pins: every port except `placement` has a pin with the same name, position within 1 DBU, the
     same width and the same outward angle.
6. **Timing:** describe wall-clock, generate p50/p95 per cell, and the total.
7. **Output:** `<dir>/report.md` and `report.json`. One row per (cell, set) giving outcome, attribution,
   XOR area and the pin diff. Nothing is written inside the repo.

The README states the environment recipe in generic terms:

- Python 3.12.
- Install gdsfactory at the kit's pin.
- Install the kit `--no-deps` and then its open dependencies, because some kits list a proprietary
  package their cells do not use (overview D6).

## 2. `R-gdf5-2` — the gates

| Kit | Gate |
|---|---|
| A, B, C, E | **Every (cell, set) gdsfactory builds matches** (XOR within tolerance, pins equal). Zero failures attributed to circuitRF |
| D | Every cell gdsfactory builds matches. **The set of failures equals gdsfactory's own failures**, so circuitRF adds no failure. Its broken fixed-GDS cells and port-less FETs are recorded as the kit's |
| all | Every **not offered** cell has a reason in the import report that matches the harness's own attribution |
| all | Brief 3's technology builds, and the NOTE/WARNING counts are recorded |
| A, B | Brief 3 §5's EM gates, run once each, short |

A failure attributed to circuitRF is fixed in the brief that owns the code (2 or 3). The matrix is
re-run **for that kit only**. Do not re-run the whole matrix to find out what broke: the report says.

## 3. `R-gdf5-3` — never bundled (D10)

Extend `PackagingScriptTests` so every publish tree a packaging script produces contains **no
`site-packages` directory, no `*.dist-info` directory and no `.whl`**. The assertion names no package,
because it does not need to.

## 4. `R-gdf5-4` — docs

- **`docs/design/pcell-gdsfactory-bridge.md`** (brief 2 starts it, this brief completes it):
  - the mapping table (overview §1)
  - D2–D7 with their reasons
  - the classification and gap-filling rules (brief 3 §2)
  - the attribution rule
  - the measured kit table **by process**
- **The user page.** "Using a gdsfactory kit": what to install, choosing the environment, the consent
  prompt, what you get, and what you do not get. **It opens with "Two kinds of open kit"** (owner,
  2026-10-30), because which one a user needs is the first question, and nothing else on the page makes
  sense until it is answered:
  - **A table, one column per kind.** *The kit's own tree*: a folder downloaded from the kit's
    publisher, brought in with File ▸ Import PDK, no Python needed. It gives the **schematic side**:
    parts with symbols, their models and corners, and whatever layout cells circuitRF can run.
    *A gdsfactory kit*: a Python package installed with `pip` into the user's own Python (≥ 3.12),
    which installs gdsfactory and its layout engine with it. It gives the **layout side**: parametric
    layout cells circuitRF runs, their ports (position, width, direction), and the kit's layer stack
    and materials as a technology.
  - **Which do I need?** Schematic and simulation: the kit's own tree. Layout artwork, or a technology
    for EM: the gdsfactory kit. Both: import both, and a layout cell that names the same device as a
    part merges onto that part's palette tile (symbol and model from the tree, artwork from the
    package). Either works alone.
  - **What gdsfactory adds over the kit's own tree:**
    - layout cells where the kit's own are written for another layout tool's macro system, which
      circuitRF cannot run;
    - RF structures the kit's own tree does not have, such as lines, couplers and filters;
    - a kit that exists only as a gdsfactory package;
    - ports with geometry;
    - the layer stack with material properties.
  - **What it does not add:** schematic symbols, circuit models and corners. Those come only from the
    kit's own tree.
  - **Nothing else to install.** The layout engine is installed by `pip` with gdsfactory; no separate
    layout application is needed, and circuitRF installs nothing. A kit's own tree needs no Python unless
    its layout cells are Python, in which case they run under the consent prompt.
  - **GDS export is circuitRF's.** A gdsfactory cell's geometry becomes an ordinary generated cell in the
    layout and exports through File ▸ Export and `circuitrf convert` like any other; exporting never
    needs Python.
  - Name no kit, foundry or process, and no outside tool other than gdsfactory (overview §0): the layout
    engine is "the layout engine gdsfactory installs", another tool's macros are "another layout tool's
    macro system".

  Cover also:
  - **What you get:** layout cells in the Library palette (merged onto a kit part's tile when they
    pair, otherwise layout-only), a technology built from the kit's stack, and every filled value
    listed.
  - **What you do not get:** circuit models from a gdsfactory package (the upstream kit provides
    those, through Import PDK), and drag handles on gdsfactory cells. Parameters are in the kit's own
    units: "µm for dimensions".
  - **Kits that list a proprietary dependency** can be installed without it, worded generically (D6).
  - **`docs/user/src/reference/pdk-integration.md`** gains a short "Two kinds of open kit" section beside
    its existing open-kit section, the table's two columns in two sentences each, linking to the new
    page. The reader importing a kit's own tree is on that page and needs to learn the other kind exists.
  - Edit the source pages only. **DocGen is the owner's, at the end of the series**, never per
    brief.
- **`src/Design/RESOLVED.md`:** the attribution findings that were circuitRF's, once each.

## 5. Completion note (here, by process)

**UI is rendered headlessly and inspected** (overview §6): every screenshot the user page uses, and the palette for one kit per row of the matrix (symbol and artwork, symbol only, layout-only tiles). Render each with `tools/UiSnapshot`
(add a mode where none hosts it), look at the PNG, fix what it shows, and list the pictures in the completion
note.


- The §2 table, filled in.
- Per kit: cells offered / matched / not offered (by reason) / failing (by attribution), and timings.
- **The answer to the owner's palette question**, combining brief 1's symbol counts with this brief's
  layout tiles, per kit: parts with a symbol and artwork, parts with a symbol only, and layout-only
  tiles.

---

## Completion note (2026-10-30)

**Built.** `tools/GdsfactoryAcceptance/run.py` + `README.md` (kit-agnostic; each reference set built in a forked
child killed at `--build-timeout`, because one kit cell loops forever inside compiled code on one halved parameter);
CliSmoke check 6, "no Python ships" (`tools/CliSmoke/BundledPython.cs`, compiled into `PackagingScriptTests` too);
the user page `docs/user/src/reference/gdsfactory-kits.md` (nav entry, "Two kinds of open kit" in
`pdk-integration.md`), figures `gdsfactory-trust` / `gdsfactory-import-report` in `FigureCatalog`; design doc §0/§0a
(mapping table, D2–D7 with reasons) and §6c; UiSnapshot `--gdsfactory palette --workspace [--cell a,b]`.

**§2, by process** (Python 3.12; nothing written in the repo; the kit→module map is in the session scratchpad only):

| Kit | Cells / offered | Sets | Matched | gdsfactory refused | Failing: kit / circuitRF | Not offered (all agree) | Technology (notes, warnings) | Import | Generate p50/p95 |
|---|---|---|---|---|---|---|---|---|---|
| A 130 nm SiGe BiCMOS | 147 / 145 | 437 | 403 | 34 | 0 / **0** | 2 required args | 8 cond, 7 via, 13 filled (26, 2) | 6.9 s | 5 / 108 ms |
| B superconducting RF | 75 / 70 | 483 | 95 | 44 | 0 / **344 — one event, open** | 3 raised, 1 req, 1 built-in | 2, 1, 3 (15, 9) | 5.6 s | — |
| C 130 nm CMOS | 767 / 764 | 1004 | 998 | 6 | 0 / **0** | 3 required args | 6, 5, 9 (18, 8) | 13.4 s | 4 / 37 ms |
| D 180 nm CMOS | 490 / 21 | 146 | 137 | 9 | 0 / **0** | 469 raised (= gdsfactory's own) | 6, 5, 8 (19, 8) | 3.2 s | 5 / 37 ms |
| E generic | 325 / 314 | 2399 | 2329 | 66 | 4 (draws the clock) / **0** | 4 req, 5 raised, 2 built-in | 3, 2, 4 (20, 4) | 5.2 s | 4 / 106 ms |

**Kit B is not closed.** 341 of its 344 are one event: the kit's worker dies partway through a `convert` (≈50 min,
memory climbing), circuitRF does not restart a dead worker, and every later cell of that run fails "pipe is broken".
The other 3 are a GDSII coordinate refusal. Next: find the set that kills the worker (log `convert`'s stderr per
chunk), make the harness count a worker death once, and decide whether the resolver should restart a dead worker.

**Brief 3 §5 EM gates (A inductor, B CPW): not run** — tier C, owner-run, and kit A's measured reference is still to
be located in the kit's tree.

**circuitRF defects found and fixed** (`src/Design/RESOLVED.md`): export writers ignored a run's generated-cell
redirects (a stale-named kit cell exported as nothing, exit 0); a cell that prints desynchronised the worker
(`claim_stdout`); a >8191-point polygon aborted `convert` (`GdsiiPolygonSplit` — the generic spiral now matches 9/9);
`convert` now refuses on any writer exception. Also, at the owner's request: **a gdsfactory kit's cell wins a name
collision** with another kit; and **kit-tree symbols snap their pins as a set** with the drawing moved to match, so
FET leads are straight (TranslationVersion 5 — existing workspaces re-import their kits).

**Palette, per kit** (kit's own tree + gdsfactory package in one workspace): A — 23 symbol+artwork, 20 symbol only,
126 + 9 layout-only (22 same-name cells, the package's used); C — 0 / 628 / 764 (the parameter-interface rule refuses
every pair, D7); D — 4 / 69 / 17; B — 37 symbol (from the kit's glyphs) + artwork / 0 / 33; E — 0 / 0 / 314.

**Pictures looked at:** the two doc figures, light and dark (`gdsfactory-trust`, `gdsfactory-import-report` — resized
after the first look); each kit's palette (A tree and package headings, B, C, D); the FET/control before-and-after
sets on A, C, D through three iterations of the symbol fix.

**Tests run** (only touched classes): LayoutGdsiiExportTests, GdsiiPolygonSplitTests, GdsiiExportByteIdentityTests,
PackagingScriptTests.NoPublishTree…, KitSymbol*/KitTemplateSymbol/KitDrawnSymbol/KitPaletteWiring/PdkReferenceManager/
WorkspaceArchive/ImportPdkCliVerb/PCellWorkerHost/ConvertCliVerb/DialogOwnerVisibility/GdsfactoryAdapter — pass;
gated GdsfactoryKitTests.TheGenericKit 313/313; `verify.py` both interpreters; Firewall 79/79.
**DocsFactoryTests.EveryCapturedFigureExists… fails until DocGen writes the two new figures** (owner's, end of series).
