# circuitRF — File Formats: `.cnl`, `.csch`, and the workspace

**Status:** Draft (rev 2) for review · **Date:** 2026-06-06 · **Phase:** 6a/6d (rev 2: splotRF serialization precedent, bitmap path policy, Library/Cell/TestBench file model, `.cws`, netlist.cnl-on-simulate)

This note specifies circuitRF's persisted file formats and, critically, the **separation** between the
electrical netlist and the schematic's visual/geometry layer. Companion: `ui-design.md` (interaction),
`ui-architecture.md` (firewall). The on-disk **alpha no-back-compat** policy applies here too
(`src/Core/Data/CLAUDE.md` "File-format stability"): break and regenerate freely until near release; a
`format_version` may be written and rejected-on-mismatch, never migrated.

## The principle: `.cnl` is netlist-only; `.csch` carries the schematic

**`.cnl` is the electrical netlist — and ONLY the netlist.** It carries components, parameters, nets,
variables, directives, and measurements — exactly what the engine elaborates and runs. It contains **no
component placement, no wiring geometry, no visual/cosmetic information.** A `.cnl` authored by hand (as
through Phases 1–5) and a `.cnl` emitted by schematic net-extraction (§5) are the same kind of artifact: a
pure netlist the engine consumes. This keeps the engine's input format clean and framework-free (the same
separation discipline as the firewall), and preserves the invariant that *extraction produces the same model
an authored `.cnl` produces* (`ui-design.md` §5.1).

**`.csch` (circuitRF schematic) is the schematic config — the visual/geometry layer.** It is the GUI-side
document a user's schematic drawing lives in: where each component sits, how wires are routed, where net
labels and junction dots are, the canvas objects (bitmaps/text/primitives/plots), zoom/view state, and the
references that tie it to the electrical design. `.csch` is to a schematic what splotRF's display config is
to a set of plots: the *arrangement and presentation*, not the underlying data.

The two are **complementary, not redundant** — and the relationship is one-way at the engine boundary:

```
  .csch  (schematic: placement, wires, labels, canvas objects, view)
     │  net extraction (ui-design.md §5) — headless, deterministic
     ▼
  design model  ≡  what a .cnl represents
     │  (may be emitted as .cnl, or fed straight to elaboration)
     ▼
  engine → DataSet
```

A `.csch` is the **source of truth for the schematic**; the `.cnl`/design-model is **derived** from it by
extraction. (A hand-authored `.cnl` has no `.csch` — it's a netlist with no drawing, which is fine; it can be
simulated, just not visually edited until/unless a schematic is drawn for it.)

## `.csch` contents (the schematic config)

A `.csch` is a **text, JSON-friendly** document (human-diffable, like the rest of circuitRF's design inputs).
It is produced/consumed by `src/Ui` (it is a GUI document), but its *model* (the schematic data classes)
should be a plain serializable model with **no Avalonia dependency** (firewall — the schematic model is data,
the canvas renders it).

**Serialization precedent — mirror splotRF's `DataDisplayConfig.cs`.** splotRF already serializes its
canvas/display layout to JSON, and `.csch`/`.cdd` should follow the same proven conventions:
- **System.Text.Json** with **`[JsonConverter(typeof(JsonStringEnumConverter))]`** on every enum — enums
  serialize as *names*, keeping files readable and stable when enum order changes.
- **References, not embedded data** (`TraceConfig.SourcePath` reloads the SNP; circuitRF bitmaps store a
  path, plots store which `DataSet`/cubes they bind) — the file is a *layout snapshot*, not a data store.
- **Nullable/defaulted fields** (splotRF's `AxesConfig? Axes` → null = autoscale) so partial/older files load
  gracefully *within* a version; combined with the `format_version` reject-on-mismatch across versions.
- **Per-object config records** (splotRF's `PlotContainerConfig`/`TraceConfig`/`MarkerConfig` nesting) —
  `.csch` mirrors this with per-component / per-wire / per-canvas-object records.
Sonnet should model the `.csch` (and later `.cdd`) serialization code directly on `DataDisplayConfig.cs`.

The `.csch` contains:

- **`format_version`** — for the reject-on-mismatch alpha policy.
- **Components / cell instances:** for each, the **instance name**, its **type/library reference**, its
  **parameter values** (expressions, as strings — the same expressions the engine resolves), its
  **placement** (position, rotation, mirror), the **disable state** (None/Open/Short, §7.2), and which
  parameters are **shown on the schematic** (the per-parameter show flag, §4.5).
- **Wires:** polyline geometry (segment endpoints), and **junction dots** (explicit connection points,
  §5.1).
- **Net labels:** the user-placed labels (text + which wire/point they attach to) — the promote-to-named-net
  mechanism (§4.4/§5).
- **Ports** (for a cell's schematic that defines an interface) — though port/pin definition primarily lives
  in the symbol (`.csym`, below); a schematic references them.
- **Canvas objects (§3.1):** bitmaps (**file path only**, never pixels — §3.1.1), text boxes (text + font/
  size/weight/color/transparency/box geometry), shape primitives (rect/circle/line + style + control points),
  and placed plots (Phase 7 — their config + which `DataSet`/cubes they bind). Each with position, size,
  rotation, transparency, lock state, Z-order.
- **View state:** zoom/pan, grid resolution, snap on/off, and (for a torn-out window) window placement
  (§4.7).
- **Design linkage:** how this schematic maps to the electrical design — e.g. the cell it is the schematic
  *of*, and the binding by which extraction's emitted nets/instances correspond. (Net *names* are owned by
  the extraction/label scheme, §5; `.csch` stores the labels, not a second naming authority.)

**What `.csch` does NOT contain:** the elaborated netlist, matrices, results, or anything the engine
computes. It is purely the editable schematic. Results are `DataSet`s (Phase 5 export/import); the netlist is
the derived `.cnl`/design model.

## Related GUI documents (same family, named here for coherence)

To keep the format family coherent, name the siblings now (full specs when their phases arrive):
- **`.ccell`** — a **cell manifest** (workspace doc §2): the cell's declared **parameters + defaults**, which
  view file is **primary** per type (schematic/symbol/layout), and the **IsTestBench** flag. One per cell
  folder; the cell's interface record (the on-disk form of what `ComponentTypeRegistry` hardcodes today).
- **`.csym`** — a **cell's symbol** (the symbol-editor output, §5A): symbol geometry (primitives/text),
  named ports/pins, the bounding/connection metadata an instance uses. (Phase 6f.)
- **`.cdd`** — a **data-display config** (the data-display canvas, §6): placed plots/tables/contours, their
  binding to a run's `DataSet`, markers, view state, and canvas objects. This is the true analog of splotRF's
  display config. (Phase 7.)
- **Workspace file (`.cws` — “circuit workspace”)** — the top-level project document (`ui-design.md` §1):
  references to the cells/schematics/libraries/data-display configs in the workspace, plus the **Dock
  layout** (§2.0). It references `.csch`/`.csym`/`.cdd`/`.cnl` files rather than embedding them.
- **`.ccolor` — “circuitRF color”** — a named **color theme** (light + dark role→RGBA maps) for rendering.
  Built-in presets ship in `/Assets/Color`; custom themes (forked when a user edits a color) save as
  `.ccolor` in the user themes dir or workspace dir. The `.cws` records which theme name is active; resolution
  order is workspace dir → user themes → `/Assets/Color`. Full spec in `color-themes.md`. (Themes everything
  eventually; schematic first.)

(These share the no-Avalonia-in-the-model rule. `.csch`, `.csym`, and `.cdd` are view configs; `.ccolor` is
cross-cutting presentation; the workspace ties them together.)

## Library, Cell, TestBench — how they map to files

The conceptual hierarchy (`ui-design.md` §1) maps to disk as **folders of the small files above**, not as
monolithic blobs — so everything stays text, diffable, and individually addressable.

### Cell = a folder of views
> **Refined by `workspace-and-project-tree.md` (rev 1).** The cell layout below is updated there: a cell folder
> now contains a single **`.ccell`** manifest (parameters + which view is primary + IsTestBench) at its root
> and three **view sub-folders** `schematic/`, `symbol/`, `layout/`, each holding **any number** of
> `*.csch`/`*.csym`/`*.clay`. The flat `<CellName>.csch` layout shown here is superseded. See that doc §1.2/§2.

A **Cell** is the reusable design unit. It is a **folder** named for the cell, containing its **views** as
separate files (now under per-type sub-folders — see the refinement note above):
```
<CellName>/
   .ccell              cell manifest: parameters + defaults + primary view per type + IsTestBench
   schematic/  *.csch  schematic views  (§4)
   symbol/     *.csym  symbol views      (the symbol-editor output, §5A)
   layout/     *.clay  layout views      (future, folder present-but-empty in v1)
```
- A cell need not have every view: a leaf/primitive wrapper might be `.csym` only; a cell may have a `.csch`
  but no `.csym` yet (see symbol handling below); etc. A view sub-folder with exactly one file makes that
  file primary by default; `.ccell` records the primary when there are several (workspace doc §2).
- **A component with no `.csym` cannot be placed on a canvas by the GUI *until its symbol is defined*.** There
  is **no automatic** symbol generation on placement. If the user attempts to place a component whose cell has
  no symbol, the placement is **deferred or cancelled** and the user is **prompted** to either (a) create the
  symbol in the **Symbol Editor** (§5A) or (b) run the **symbol auto-gen** system (below). Placement proceeds
  once a symbol exists. *(The plain-rectangle `System.Warning` stand-in below is for the separate case of an
  already-placed instance whose symbol reference later breaks — not for first placement.)*
- **User-commanded symbol auto-generation:** the user may explicitly command "generate symbol" for a cell.
  The generated `.csym` is a **rectangle** with the cell's ports split **odd-numbered ports on the left
  side, even-numbered on the right**; each port has a short **pin line extending outward** from the
  rectangle with the **pin at the line's outer end**; and a **port label** (one per port) placed **inside**
  the rectangle as a text object at the **minimum text font size**. This is a starting point the user then
  refines in the symbol editor (§5A) — generation is on command, never automatic.
- The **electrical netlist of a cell is *derived* from its `.csch`** by extraction — it is **not** stored as a
  per-cell `.cnl` (the `.cnl` is netlist-only and is produced on demand, see “netlist.cnl on simulate” below).
- The cell folder is the unit you copy/share to reuse a cell.
- **A placed component references its cell by RELATIVE PATH**, and resolves its glyph/pins via
  `instance → relative path → .ccell → primary .csym` (workspace doc §4). A broken path → a “Not Found” glyph;
  a resolved cell whose **named primary `.csym` is missing** → the plain-rectangle stand-in (and the tree flags
  the cell System.Warning). Changing the primary symbol re-renders instances live (a risky op — pin counts may
  differ; wires that no longer meet a pin show unconnected).

### Library = a folder of cells
A **Library** is a **folder containing cell folders** (plus a small manifest):
```
<LibraryName>/
   .clib               manifest: library name, version, metadata (NOT a cell list — cells are scanned)
   <CellA>/  …         (cell folders as above)
   <CellB>/  …
```
- **Refined by `workspace-and-project-tree.md`:** membership is **filesystem-is-truth** — the Project Tree
  discovers cells by **scanning** the library folder, not from a manifest list. `.clib` is therefore
  *lightweight*: library name, version, metadata only (no cell index). `File → Add Library` references an
  external library by pointing at its folder. Built-in libraries (standard library) ship as real cell folders
  with a `.clib` and a `.ccell` per cell (built-in cells: typically one symbol + one schematic, no layout).
- The **standard component libraries** (lumped elements, sources, simulation directives — the palette
  contents, §2.2) are libraries of this shape shipped with circuitRF.
- A user library is just a folder of cells the user points the workspace at; libraries are shareable as
  folders.

### TestBench = a Cell whose schematic carries analyses + measurements
A **TestBench** is **not a separate file type** — it is a **Cell** (a folder with a `.csch`) whose schematic
includes the **analysis directives** and **measurements** (the `Var`/`Measurement`/analysis content,
§7.2/§5) that make it *runnable*. In other words: any cell schematic that contains analyses is a testbench;
“TestBench” is a role a cell plays, marked by the **`IsTestBench` flag in the cell's `.ccell`** (workspace doc
§2; moved here from `.csch` metadata) that the Project Tree uses to show it as runnable and to drive the
“Only TestBench” filter, not a distinct on-disk format. This matches the engine model
(the TestBench is the top-level runnable design, data-model §2.1) and means a testbench is authored,
saved, copied, and version-controlled exactly like any other cell.

### Workspace (`.cws`) = the project that references the above
> **Refined by `workspace-and-project-tree.md` (rev 1):** the workspace is a **folder** (root folder name =
> workspace name); the file is literally **`.cws`** (no stem), one per workspace. **Membership is
> filesystem-is-truth** — the tree is built by scanning the folder structure, so the `.cws` **member-files
> list below is removed**. The `.cws` records configuration only.

The **`.cws`** file is the **workspace config** — a JSON document that records:
1. **The Dock layout** (§2.0 panel/tab arrangement) — stored as a JSON blob so the user's panel
   arrangement is restored on next open.
2. **Referenced libraries** — relative or absolute paths to external library folders added via
   File → Add Library. Unresolvable → shown System.Warning + italics in the tree.
3. **Known Files** — an arbitrary list of paths to other files the user keeps at hand while working (no
   semantic role; convenient bookmarks). Unresolvable → System.Warning + italics, same as a broken library.
   *(Replaces the old “member files” entry — membership is now the filesystem, not a list.)*
4. **Color scheme name** (`ColorSchemeName`, optional/null) — the `.ccolor` theme to activate on open.
   Resolved via the four-step chain: workspace dir → user themes dir → bundled assets → `ColorTheme.BuiltIn`.
   Null means "use the application-level user preference". Omitted from the file when it is null
   (`WhenWritingNull`).
5. **Tree view-state** (optional) — the user's custom ordering and active filter set, so the tree restores
   as arranged.

It **references, never embeds** — the actual design lives in the cell folders; the same cell/library can
be referenced by multiple workspaces. A workspace is the “what am I working on right now” document; the
cells/libraries are the durable artifacts. The format uses the same conventions as `.csch` (System.Text.Json,
enum-as-string, format_version reject-on-mismatch). Implemented in `WorkspacePersistence` (`src/Ui/Schematic/`).

**Id-not-persisted rule (applies to ALL circuitRF file formats):** Runtime object identity (`Id` fields on
`EditableComponent`, `EditableWire`, etc.) must NOT appear in any persisted file. Ids are auto-generated on
import and have no meaning across sessions. Tests must compare content (positions, types, params, wires),
never Ids.

### `netlist.cnl` on simulate (the only `.cnl` the GUI writes)
The GUI does **not** keep per-cell `.cnl` files. Instead, **when a TestBench is simulated, extraction writes
a single `netlist.cnl` to the workspace root directory** (the design model emitted from the testbench's
`.csch`, §5), then the engine runs it:
- It is **overwritten on every simulation**, regardless of which TestBench was run (one scratch netlist, not
  one per testbench) — the latest simulation's netlist.
- Its **header comment records which TestBench produced it and a timestamp**, e.g.
  `; netlist.cnl — generated from TestBench "PA_loadpull" at 2026-06-06T14:22:31Z`, so a user inspecting it
  knows its provenance.
- This serves three purposes: it is the **human-inspectable** netlist for the last run (debugging “what did
  the engine actually see?”), it enables **headless re-run** (`circuitRF.Cli netlist.cnl`), and it is the
  artifact the **extraction oracle** test compares against an authored `.cnl` (`ui-design.md` §5.1). It is a
  generated scratch artifact, not part of the saved project — the project's source of truth is the `.csch`.

## Port-index conventions — 1-based user-facing, 0-based allowed in the engine

Port/terminal indices appear in two worlds with **different base conventions**, and the **net-extraction
bridge is the one place they cross** — so it must be explicit and tested.

- **User-facing = 1-based.** Anything a user sees or authors indexes ports from **1**: component/cell
  **symbol** port/pin numbers (`.csym`), the schematic, the palette, dialogs, error messages. This is what
  RF engineers expect — a network analyzer has port 1 and port 2, never port 0. **Symbol ports therefore
  number from 1** (and the auto-generated symbol's odd-left/even-right split is over the 1-based numbering:
  ports 1,3,5… left, 2,4,6… right).
- **Engine = 0-based allowed.** The engine is C#; 0-based indexing is natural internally and is fine. The
  engine also uses **reference node 0** for ground (existing convention).
- **`.cnl` does not list port *numbers* at all — it lists net nodes positionally.** A component line names
  its nets in **terminal order**; the port number is **inferred from position in the list** (the 1st net is
  terminal 1's net, the 2nd is terminal 2's, …). So the `.cnl` carries no explicit port index to get
  wrong — it carries **net-node order**, and correctness means *emitting the nets in the symbol's terminal
  order*.
- **The extraction caution (the bug-prone seam):** net extraction (`ui-design.md` §5) must walk each
  component's terminals **in the symbol's defined order** and emit the connected nets **in that same order**
  on the component's `.cnl` line. The symbol's terminals are 1-based; the position in the emitted `.cnl`
  line is what the engine reads as terminal order. **Do not** mix a 1-based symbol terminal with a 0-based
  list slot and shift everything by one; **do not** reorder terminals. A multi-terminal device (e.g. a
  3-terminal FET with d/g/s) is the place an off-by-one or a transposed terminal silently produces a
  *wrong but plausible* netlist — so this is a tested seam: the **extraction oracle** (a schematic drawn to
  match a hero `.cnl` extracts to an equivalent netlist, `ui-design.md` §5.1) catches a terminal-order or
  base-offset error because the emitted net-node order would differ from the authored `.cnl`.
- **Rule of thumb:** convert at the boundary, once. Inside the GUI/symbols, think 1-based; when emitting the
  `.cnl` line, emit nets in terminal order (the engine infers position); never let a raw 0-based loop index
  leak out as a user-visible port number, and never let a 1-based symbol number become a list position
  without the deliberate order mapping.

## Persistence rules

- **Text + JSON-friendly + human-diffable** (consistent with `.cnl` being readable text). Stable key
  ordering so diffs are clean.
- **Paths, not payloads:** bitmaps store a file path, never pixels; if it won't load, the canvas shows a
  placeholder box (§3.1.1). (This mirrors splotRF's proven `SourcePath`-not-data approach in
  `DataDisplayConfig.cs`/`TraceConfig`.)
- **Bitmap path resolution — relative preferred, absolute allowed.** Either form may be stored; the
  **default/preference is a relative path** (relative to the `.csch`/workspace location), so a moved or
  shared project keeps its images. Absolute paths are accepted (and kept as-is) when the user points outside
  the project. Two bitmap context-menu affordances back this up (§3.1.1):
  - **Locate…** — opens a System file dialog so the user can re-point a bitmap whose path no longer
    resolves (the placeholder-box case); the new path is stored (relative if under the project, else
    absolute).
  - **Refresh** — reloads the image from disk at its current path (picks up an edited image without
    reopening the schematic).
- **The schematic model is framework-free:** the serializable schematic classes carry no Avalonia types
  (firewall). The canvas (`src/Ui`) renders the model; the model itself could be read by a headless tool
  (e.g. a future batch "extract `.csch` → `.cnl`" CLI).
- **Alpha no-back-compat** (`src/Core/Data/CLAUDE.md`): `format_version` written; mismatch → clear error, no
  migration; break+regenerate freely until near release.

## Open items
- Exact JSON schema for each format (settled at implementation of the owning phase: `.csch` 6d, `.csym` 6f,
  `.cws`/`.clib` 6b/6d) — modeled on splotRF's `DataDisplayConfig.cs` conventions.
- **`.cdd` (Phase 7.1e) — SETTLED.** circuitRF Data Display config. System.Text.Json,
  `JsonStringEnumConverter` (enums as names), references-not-data (source paths only, no embedded SNP data),
  `FormatVersion` reject-on-mismatch (`CurrentFormatVersion = 1`). Round-trips: tabs (`Tabs` list),
  active tab index (`ActiveTabIndex`), per-tab canvas zoom/pan (`ZoomLevel`/`ViewOffsetX`/`ViewOffsetY`),
  placed plot containers (`PlotContainerConfig` with position/size/type/axes), per-plot traces
  (`TraceConfig` with source path, matrix type, style, markers), per-plot axes zoom (`AxesConfig`).
  Clipboard paste uses the same `DataDisplayConfig` model (v1 `Plots` list path) and is
  unaffected by the version check (clipboard is same-session, same version). Window geometry is
  zeroed for embedded Dock documents (not a floating OS window). `format_version` key is `”FormatVersion”`
  (PascalCase, no `JsonPropertyName` attribute — matches `.cws`/`.csch` convention). **Out of scope for
  7.1e:** `.cws` auto-reopening of open Data Displays on workspace load (Dock-document restore, deferred).
- (Port-index conventions are settled — see “Port-index conventions” above: 1-based user-facing, 0-based
  allowed in the engine, `.cnl` infers from net-node position, extraction emits nets in symbol terminal
  order.)
