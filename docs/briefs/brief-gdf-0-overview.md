# gdsfactory kits — series overview: open process kits written as gdsfactory packages

**Series:** gdsfactory kits · **Tag:** `R-gdf{n}-m` · **Briefs:** 1–6
**Area:** `tools/pcell-python/` (a new `gdsfactory_bridge` package beside `cni`),
`src/Design/Layout/PCells/Wire/` (`KitPCellLibrary.cs`, `PCellGeneratorManifest.cs`,
`PCellWorkerResolver.cs`, `PCellTrust.cs`), `src/Core/Pdk/` (`PdkImporter`, `PdkFormatRegistry`),
`src/Ui/Layout/TechImport/` (`ProcessTechnologyBuilder`, `LayerPropertiesReader`), `src/Ui/Schematic/KitPaletteMerge.cs`,
`src/Ui/Views/Dialogs/PCellTrustDialog.axaml(.cs)`, `src/Cli/Authoring.cs`, new `docs/design/pcell-gdsfactory-bridge.md`,
`docs/design/pdk-import.md`, `docs/user/src/reference/pdk-authoring.md`
**Found by:** owner review of the gdsfactory project and the open kits published beside it (2026-10-30).

---

## 0. Naming rule for this series

- **`gdsfactory` may be named** in code, comments, tests, docs and commit messages (owner,
  2026-10-30). It is MIT-licensed open source. The Python package is `gdsfactory_bridge`, and the
  doc page may say "gdsfactory".
- **Everything else in `pdk-import.md` §0 is unchanged.** Never name a kit, a foundry, a process
  name, a kit's PyPI distribution name, a device or model name from a kit, or another EDA tool. Refer
  to kits by process only, e.g. "the open 130 nm SiGe BiCMOS kit". Test fixtures are synthetic.
- The **proprietary companion package** that some gdsfactory kits list as a dependency is never
  named. Call it "the proprietary companion package".
- The layout engine gdsfactory depends on is not named either. Call it "gdsfactory's layout-engine
  dependency". Its licence is GPL-3.0-or-later, which is why §4 D10 exists.

## 1. What a gdsfactory kit is

A gdsfactory kit is an ordinary **Python package** that defines a `Pdk` object. That object holds:

| Field | What it holds | circuitRF counterpart |
|---|---|---|
| `cells` | `name → function`. Each function returns a component: polygons on `(layer, datatype)` pairs, plus **ports** (name, centre, width, outward orientation in degrees, layer, port type) | a PCell generator, where a port becomes a `Pin(name, x, y, layer, width, outward_deg)`. The contract already requires exactly these fields |
| `layers` | layer name → `(layer, datatype)` | `LayerKey` |
| `layer_views` | colours and visibility, which it can write as a layer-properties file | the layer table that `LayerPropertiesReader` already reads |
| `layer_stack` | for each patterned layer: `thickness`, `zmin`, `material` (a name) | the stackup in `.ctech` (brief 3) |
| a material module (some kits) | conductivity and permittivity for each material name | `StackupLayer.SigmaSm` / `Epsr` |
| circuit models | photonics-style S-parameter functions | not used. circuitRF's engine is the simulator |

Lengths are **µm floats**. Parameters are plain Python function arguments with defaults and no
declared dimension.

## 2. What was measured (2026-10-30, Python 3.12, every cell built at its defaults)

Five open kits are relevant to an RF tool. Photonics-only kits are out of scope (D8).

| Kit (by process) | gdsfactory pin | Python | Cells | Built at defaults | With ports | Stack levels | Material properties |
|---|---|---|---|---|---|---|---|
| A: 130 nm SiGe BiCMOS | 9.51 | **3.12 only** | 147 | 145 | 138 | 32 | **yes**: σ and εr in a separate material-card module |
| B: superconducting quantum RF | 9.51 | ≥ 3.12 | 75 | 71 | 71 | 10 | names only |
| C: 130 nm CMOS | 9.40 | ≥ 3.12 | 767 | 764 | 682 | 18 | none: every metal is `metal` |
| D: 180 nm CMOS | 9.40 | ≥ 3.12 | 490 | **21** | 14 | 25 | names only |
| E: the generic kit built into gdsfactory | — | ≥ 3.12 | 325 | 316 | 248 | yes | — |

Findings that shape the briefs:

1. **Kits A and C list the proprietary companion package as a hard dependency, but no cell needs it.**
   Installed with `--no-deps` plus their open dependencies, all their cells built. Two of kit B's
   modules mention the package; one mention is a docstring, and no cell failed for want of it. → D6.
2. **Kit D is mostly broken when installed from PyPI.** 469 of its 490 cells fail. Its fixed-GDS
   cells look for their files under the layout-engine dependency's own directory, and the files are
   not there. Its FETs build but carry **no ports**. This is the kit's defect, not ours. circuitRF
   reports it per cell and never works around it.
3. **Kit B's three all-angle cells fail inside gdsfactory itself** (an API mismatch in
   `get_polygons`). Its CPW ports are typed `optical` (137) and `placement` (61), and only 8 are
   `electrical`. Port type must not decide whether something is a pin (R-gdf2-4).
4. **Some floats are not lengths.** Kit A's distributed RF cells take `e_r`, `frequency`,
   `resistance`, `inductance` and `bandwidth`. Kit B has `angle`, `angle_resolution`, `p` and a
   rotation. Kit A's inductor carries `inductance = 3.33e-11`, which **rounds to 0** if it is treated
   as a length and converted to DBU. → D3.
5. **Most string parameters are layer names.** Kit A has 300 string parameters, mostly
   `layer_* = "<name>drawing"`. They are real parameters, but they are not ones a designer edits on an
   instance (R-gdf2-3).
6. **A stack lists only PATTERNED layers.** Kit A has no inter-metal dielectric, substrate or
   passivation levels. Its dielectrics exist only as material cards. Kit C has no material properties
   at all. Kit B states its substrate and vacuum as levels. Turning a stack into a usable `.ctech` is
   a real conversion that has to report what it filled in (brief 3).
7. **Cost.** Importing a kit warm takes 0.8–1.9 s. The first import after installation takes up to
   26 s while Python compiles bytecode. Building every cell of a kit takes 0.4–7.6 s in total. Every
   port is orthogonal, and no port has zero width.
8. **Kits declare their device model.** Kit A's devices carry a `model` parameter default and
   `info["model"]`. This is exactly what `KitPaletteMerge`'s model step pairs on (§3).
9. **No gdsfactory kit ships a schematic symbol circuitRF can read.** Kit B ships an SVG glyph for
   each cell, with no pin data. Kit C ships pictures of digital cells. Kit A has a table mapping its
   cells onto the **upstream** kit's symbol files.

## 3. Will an open kit's parts get symbols in the Library palette?

**From the upstream kits, yes. From the gdsfactory packages alone, no.**

- **The symbols come from each kit's UPSTREAM tree**, the repository each gdsfactory package links in
  its README. Those trees ship text symbol files, SPICE-dialect model libraries with corners, and
  Verilog-A sources, and File ▸ Import PDK reads all three. **Measured by brief 1 (2026-10-30): every
  part of all three kits lands in the Library palette with a symbol** — kit A 43 of 43, kit C 628 of 628
  (79 of them its device library, the rest digital cells), kit D 73 of 73 — and every kit's corners are
  offered. **Placeable is not yet simulatable.** Kit A's MOSFETs and MIM capacitors solve (the MOSFETs
  through the Verilog-A the kit ships, once compiled by its own install step); kit C's MIM capacitor
  solves; most other families stop at one shared gap — a plain SPICE card inside a kit netlist is routed
  only to a compiled model, never to circuitRF's own physics — and kit C/D's MOSFETs use a model their
  simulator builds in, with no source to compile. Brief 1's completion note has the table and the open
  items. Kit A's parametric cells are found through `cni` and run only with consent.
- **A gdsfactory cell with no matching part gets a layout-only tile.** This is `KitPaletteMerge`'s
  existing rule: a generator matching no part still gets a placeable tile of its own. Examples are
  kit A's lines, couplers, divider and filters, every kit-B cell, and kits C/D's layout cells.
- **A gdsfactory cell that declares the same model as an upstream part merges onto that part's tile**
  through the existing model step. Pairing changes nothing about parameters. Where the two sides'
  parameter names differ (kits C and D: `gate_width` against the symbol's own name), the existing
  parameter-interface rule refuses the pair, and the import says so (D7).
- **circuitRF draws no symbol of its own** (`PCellKitSchematicParts`). Brief 4 is an optional
  extension: when a kit ships its OWN glyph (kit B's SVGs), build the `.csym` from that glyph, with
  pins placed from the cell's ports. Build it only on the owner's go-ahead (D9).

## 4. Decisions (owner, 2026-10-30; the defaults below stand unless changed before the build starts)

- **D1 — the name.** `gdsfactory` may appear in the repo. See §0 for everything that still may not.
- **D2 — consent is unchanged, and nothing runs before it.**
  - Finding gdsfactory kits reads only files: `*.dist-info/METADATA` (`Requires-Dist: gdsfactory`) in
    a chosen environment's `site-packages`, or a kit source tree on disk. **No Python runs to list
    kits.** Asking an interpreter for its version is running the interpreter, not kit code, and that
    is allowed.
  - The consent prompt (`PCellTrustDialog`) names the package, its version, its installed directory
    and the interpreter.
  - Consent is keyed on the package's **installed directory**, resolved through symlinks. That keeps
    `PCellTrustStore`'s "keyed by directory" rule, so a different environment asks again.
  - Denied means nothing runs, exactly as today.
  - Headless runs honour a recorded decision, or `--allow-code` granted for that one run. A headless
    run never prompts and never records a decision.
- **D3 — floats pass through EXACTLY, in the kit's own units.** Every float parameter is declared as
  a `real` and travels unconverted. The adapter scales only the geometry it gets back, using
  `Technology.dbu_per_micron` (wire version 2 already permits a generator to scale its own
  constants). Effects:
  - No float is ever rounded to the DBU grid on the way in (finding 4).
  - gdsfactory cells get no drag handles. Handles need `length` parameters.
  - The parameter editor shows the kit's number with no unit, and the tooltip says "kit units (µm
    for dimensions)".
  - *Rejected:* "every float is a length, with an override file". That converts by default and is
    silently wrong on every non-length nobody listed.
- **D4 — the interpreter is per-user, never written into the workspace.**
  - The workspace records the **module name** and the **version last seen**.
  - Each user picks the environment once. It is stored in per-user preferences, keyed by module
    name. A colleague who opens the workspace is asked for theirs, and is never handed a path from
    someone else's machine.
  - A recorded version different from the installed one is a NOTE, not a refusal.
- **D5 — circuitRF installs nothing.** It never runs `pip`. It states the requirement: Python ≥ 3.12,
  and the kit's own pin, read from its METADATA `Requires-Python`. It refuses an interpreter that does
  not meet it, naming the interpreter's version.
- **D6 — the proprietary companion is never needed.** The adapter imports only `gdsfactory` and the
  kit. A cell that fails because a module is missing is reported **with the module's name as the
  error states it**, and nothing else is done. The user doc says kits can be installed without their
  optional proprietary dependency, and does not name it.
- **D7 — no new pairing rule in this series.**
  - gdsfactory generators report their declared model: the `model` parameter's default, else
    `info["model"]` from a default build. `KitPaletteMerge`'s existing four steps decide.
  - When a part and a cell declare the same model but the pair is refused, the import report says
    which, and which parameters failed to cover.
  - A parameter map, for kits whose layout and symbol name the same quantity differently, is a
    follow-up and not part of this series.
- **D8 — photonics-only kits are out of the acceptance matrix.** The adapter is generic and may well
  run them, but nothing in circuitRF simulates light, and a matrix row that proves nothing costs a
  real install.
- **D9 — brief 4 (symbols from a kit's own glyphs) is optional.** Default: **not built** until the
  owner says so after brief 2 lands.
- **D10 — never bundled.** No installer, archive or publish tree contains gdsfactory, a kit, or
  gdsfactory's layout-engine dependency. circuitRF only launches the user's own interpreter, which
  keeps the GPL dependency entirely on the user's side. A packaging test holds this.

## 5. The briefs

| # | Brief | What it adds | Depends on |
|---|---|---|---|
| 1 | [Open kits on the existing importer](brief-gdf-1-open-kits-on-the-existing-importer.md) | the upstream trees of kits A, C and D through File ▸ Import PDK; fixes for whatever breaks; `circuitrf import pdk` | — |
| 1b | [Kit devices on plain model cards](brief-gdf-1b-kit-devices-on-plain-model-cards.md) | brief 1's open items: a level gate, kit devices on cards routed to circuitRF's own physics, pairing on the symbol's stated call (owner decision), an external reference | 1 |
| 2 | [The gdsfactory cell adapter](brief-gdf-2-cell-adapter.md) | `gdsfactory_bridge`, discovery without running code, consent, the per-user interpreter, describe/generate, palette tiles | — |
| 3 | [Layer stack and materials to `.ctech`](brief-gdf-3-layer-stack-to-technology.md) | a technology built from a kit's stack, materials and layer views, through the ONE `ProcessTechnologyBuilder` | 2 |
| 4 | [Symbols from a kit's own glyphs](brief-gdf-4-symbols-from-kit-glyphs.md) | *optional (D9)* | 2 |
| 5 | [Acceptance matrix and docs](brief-gdf-5-acceptance-matrix.md) | every cell of every kit in §2, through circuitRF, compared with gdsfactory's own GDS; the user page | 1–3 |
| 6 | [Follow-ups](brief-gdf-6-follow-ups.md) | headless kits loaded on demand; one Import PDK for the GUI and the CLI (a folder holding both kinds imports both; archives from the CLI); compile advice only where the kit ships source; `--python` per module | 1–5 |

Briefs 1 and 2 are independent and may run in parallel. Brief 3 needs the adapter's process.

## 6. Testing, in three tiers

The owner asked for extensive testing. Kit names cannot enter the repo, so the work splits like this:

| Tier | Where | Needs | What it holds |
|---|---|---|---|
| **A: routine** | `tests/*` default gate | nothing | discovery from synthetic `METADATA` files, manifest writing, consent keying, interpreter preference, float pass-through on the C# side, report sentences |
| **B: gated** | `tests/Ui.Tests/Gdsfactory/` with `[GdsfactoryFact]` | `CRF_GDSFACTORY_PYTHON` pointing at an interpreter that has gdsfactory | a **synthetic** kit committed under `tests/Ui.Tests/Gdsfactory/Fixtures/synthkit/`, written against the real gdsfactory API, plus kit E, which ships inside gdsfactory and names nothing. Without the variable these **Skip with a reason**, the `FixtureFact` pattern, and never fail |
| **C: acceptance** | `tools/GdsfactoryAcceptance/` (a harness, not a test project) | the owner's environments with kits A–D installed | the brief-5 matrix, driven through the CLI as a process. Kit-agnostic: it takes a module name as an argument and contains none |

The **independent check** in tiers B and C: gdsfactory writes its own GDS for a cell, and circuitRF's
generated cell is exported with `GdsiiExport`. The two are compared as **per-layer polygon sets** after
normalisation, never as bytes. Two implementations agreeing is evidence; one implementation agreeing
with itself is not (`tools/pcell-python/README.md`).

Write one test per claim and run only the classes you touch. Tier B is never part of the default gate:
it starts real Python.

**Every piece of UI the series adds or changes is rendered headlessly and inspected** (owner, 2026-10-30):
a dialog, a dialog row, a step in the Import PDK flow, a Settings entry, a palette tile, a parameter-editor
row or tooltip, a report view. Render it with `tools/UiSnapshot` (see its `README.md`); where no mode hosts
that view yet, add one, because the GUI cannot be launched from a shell. Look at each PNG before the brief
closes — overlapping or clipped text, a control that shifts, an empty combo, wrong theme colours — and fix
what it shows. The completion note lists each picture and what was checked. A unit test asserting a view
model's state does not stand in for looking at the view.
