---
title: File Formats
slug: reference/file-formats.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > File Formats
lede: circuitRF stores a project as a folder of small, human-readable, text files — diffable and version-control friendly. The core idea: the **schematic** (what you draw) is the source of truth; the **netlist** (what the engine runs) is derived from it.
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#principle">Schematic vs. netlist</a></li>
      <li><a href="#family">The file family at a glance</a></li>
      <li><a href="#hierarchy">Workspace › Library › Cell</a></li>
      <li><a href="#testbench">What makes a TestBench</a></li>
      <li><a href="#netlistcnl">The generated <code>netlist.cnl</code></a></li>
      <li><a href="#rules">Shared rules</a></li>
    </ol>
  </nav>

## Schematic vs. netlist {#principle}

Two complementary artifacts, related one way at the engine boundary:

```text
  .csch  (schematic: placement, wires, labels, canvas objects, view state)
     │  net extraction  — headless, deterministic
     ▼
  design model  ≡  what a .cnl represents
     │
     ▼
  engine → DataSet (results)
```

- **`.csch`** — the schematic: where each component sits, how wires route, net labels, junction
  dots, canvas objects, zoom/pan. It carries *no* elaborated netlist, matrices, or results. This is
  what you edit and save.

- **`.cnl`** — the netlist, and *only* the netlist: components, parameters, nets, variables,
  analyses, measurements. A hand-authored `.cnl` and one produced by extracting a schematic are the
  same kind of artifact — a pure netlist the engine consumes. See the [Netlist format](netlist.html)
  chapter.

A hand-authored `.cnl` with no `.csch` is fine — it simulates, it just has no drawing to edit
visually until you draw one.

## The file family at a glance {#family}

<table>
    <thead><tr><th>Extension</th><th>What it is</th></tr></thead>
    <tbody>
      <tr><td><code>.csch</code></td><td>Schematic view — placement, wires, labels, canvas objects, analyses, measurements.</td></tr>
      <tr><td><code>.csym</code></td><td>Symbol view — a cell's glyph: drawing primitives + pins mapped to ports. See the <a href="symbol-editor.html">Symbol Editor</a>.</td></tr>
      <tr><td><code>.ccell</code></td><td>Cell manifest — the cell's declared parameters + defaults, which view is primary per type, and the <code>IsTestBench</code> flag.</td></tr>
      <tr><td><code>.clay</code></td><td>Layout view — the cell's physical geometry: shapes on layers, instances, PCell placements, bitmaps, the display unit and the snap grid. A real, first-class view; see <a href="layout-editor.html">The Layout Editor</a>.</td></tr>
      <tr><td><code>.cdd</code></td><td>Data Display config — placed plots/tables/contours, their binding to a run's results, markers, view state.</td></tr>
      <tr><td><code>.cnl</code></td><td>Netlist — the engine's input (derived from a schematic, or hand-authored).</td></tr>
      <tr><td><code>.cws</code></td><td>Workspace config — the top-level "what am I working on" document. References libraries, known files, the active color theme, and panel layout.</td></tr>
      <tr><td><code>.clib</code></td><td>Library manifest — name, version, metadata (cells are discovered by scanning, not listed here).</td></tr>
      <tr><td><code>.ccolor</code></td><td>Color theme — a named light+dark palette for rendering.</td></tr>
      <tr><td><code>.ctech</code></td><td>Technology — the layer table (GDSII layer/datatype pairs, colours, purposes), the substrate stackup, the DRC rules, and the default display unit and snap grid. Shared at workspace level: every layout in the workspace resolves against one of these. See <a href="layout-editor.html#technology">Technology</a>.</td></tr>
      <tr><td><code>.cem</code></td><td>EM setup — one electromagnetic run's configuration: which layout, the stackup mapping, the ports and their reference impedances, the mesh settings, the frequency plan and the de-embedding choice. See <a href="em-setup.html">EM Setup</a>.</td></tr>
      <tr><td><code>.charm</code></td><td>harmonicaRF document — the DUT, the source and load termination planes at every harmonic, the package, the display configuration and the markers. See <a href="harmonicarf.html">harmonicaRF</a>.</td></tr>
      <tr><td><code>.wBond</code></td><td>wBond design — bondwire geometry: the wires, their arrays and profiles, the substrate and the solver settings. Self-contained and shareable. See <a href="wbond.html">wBond</a>.</td></tr>
    </tbody>
  </table>

<div class="callout note">
<span class="label">Which of these are documents you open</span>
<p><code>.csch</code>, <code>.csym</code>, <code>.clay</code>, <code>.cdd</code>, <code>.ctech</code>,
<code>.cem</code>, <code>.charm</code> and <code>.wBond</code> each open as a tab in the workspace.
<code>.ccell</code>, <code>.clib</code> and <code>.cws</code> are manifests the application maintains
for you. <code>.cnl</code> is the engine's input and is normally derived rather than edited.</p>
</div>

## Workspace › Library › Cell {#hierarchy}

The conceptual hierarchy maps to folders of the small files above — never one monolithic blob.

### Cell = a folder of views

```text
<CellName>/
   .ccell              manifest: parameters + defaults + primary view per type + IsTestBench
   schematic/  *.csch  schematic views
   symbol/     *.csym  symbol views
   layout/     *.clay  layout views
```

A cell need not have every view. A view sub-folder with exactly one file makes that file primary
by default; `.ccell` records the primary when there are several. A placed component references its
cell by **relative path** and resolves its glyph through the cell's primary symbol — so a broken
path shows a "Not Found" glyph, and a resolved cell with a missing primary symbol shows a
plain-rectangle stand-in. The cell folder is the unit you copy to share or reuse a cell.

### Library = a folder of cells

```text
<LibraryName>/
   .clib               manifest: name, version, metadata
   <CellA>/  …
   <CellB>/  …
```

Membership is **filesystem-is-truth** — the Project Tree discovers cells by scanning, so the
`.clib` stays lightweight (no cell index). The standard component libraries ship in this shape.
*File → Add Library* points the workspace at an external library folder.

### Workspace = the project that references the above

A workspace is a **folder** (its name = the folder name) containing a `.cws` file. Membership is
the filesystem; the `.cws` records configuration only: the panel/dock layout, referenced
libraries, "Known Files" bookmarks, the active color theme, and tree view-state. It **references,
never embeds** — the same cell or library can be used by multiple workspaces.

## What makes a TestBench {#testbench}

A **TestBench is not a separate file type** — it is a cell whose schematic carries
[analyses](simulations.html) and [measurements](measurements.html), making it runnable.
"TestBench" is a role, marked by the `IsTestBench` flag in the cell's `.ccell`, which the Project
Tree uses to show it as runnable. A testbench is authored, saved, and version-controlled exactly
like any other cell.

<div class="callout note">
    <span class="label">Port numbers are 1-based for you, positional in the netlist</span>
    <p>Everything you see — symbol pins, dialogs, error messages — numbers ports from <strong>1</strong> (a VNA
    has port 1 and port 2, never port 0). The <code>.cnl</code> doesn't list port numbers at all: a component
    line names its nets in <em>terminal order</em>, and the engine infers the port from position. Net extraction
    emits nets in the symbol's terminal order — the one seam where the two conventions meet, and a tested one.</p>
  </div>

## The generated `netlist.cnl` {#netlistcnl}

The GUI does not keep per-cell `.cnl` files. When you simulate a TestBench, extraction writes a
single `netlist.cnl` to the workspace root and the engine runs it. It is:

- **Overwritten every run** — one scratch netlist for the latest simulation, whichever testbench
  produced it.

- **Stamped with provenance** — a header comment records which TestBench produced it and when
  (e.g. `; netlist.cnl — generated from TestBench "PA_loadpull" at 2026-06-06T14:22:31Z`).

- **Human-inspectable and re-runnable** — it's exactly what the engine saw, and you can re-run it
  headless from <a href="cli.html">the command line</a>.

It is a generated scratch artifact, not part of the saved project — the `.csch` is the source of
truth.

## Shared rules {#rules}

- **Text, JSON-friendly, human-diffable**, with stable key ordering for clean diffs.

- **Paths, not payloads** — bitmaps store a file path, never pixels; a missing image shows a
  placeholder box, never a crash. Relative paths (under the project) are preferred so a moved or
  shared project keeps its images; absolute paths are accepted.

- **Enums serialize as names**, so files stay readable and stable across versions.

- **Runtime identity is never persisted** — internal object IDs are regenerated on load and carry
  no meaning across sessions.

- **Alpha policy** — a `format_version` is written and rejected on mismatch; there is no silent
  migration. (circuitRF is pre-release; formats may break and regenerate until near release.)

---

<p class="small">See also: <a href="netlist.html">Netlist format</a> · <a href="grid.html">Grid &amp;
  Connectivity</a> · <a href="symbol-editor.html">Symbol Editor</a>. Full design:
  <code>docs/design/project-file-formats.md</code>.</p>
