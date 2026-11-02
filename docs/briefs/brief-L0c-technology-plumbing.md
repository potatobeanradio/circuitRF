# Sonnet Brief — Phase L0c: technology plumbing, resolution, and the fallback palette

**Design:** `docs/design/layout-view.md` §2.4 (the `.ctech` file), §2.1/§2.2 (layer identity and colors),
§1.2 (per-workspace defaults). **Consumes L0a** (`Technology`, `StarterTechnologies`, `TechPersistence`,
`TechValidation`) and **L0b** (`LayoutDocument`, `LayoutEditorViewModel`, the metadata bar).

**Scope is L0c ONLY: make technologies *reach* a layout.** The `tech/` folder, the workspace default, a
resolver with a cache and a change seam, the generated fallback palette, and the New Workspace technology
choice. **There is no `.ctech` editor in this phase** — that is L0d, which will be a document on top of this
plumbing exactly as L0b was a document on top of L0a's persistence.

This is the phase that makes §11's L0 gate line *"both starter techs create a working empty layout"* true.

## Goal

Creating a workspace picks a starter technology, which is written to `tech/` and recorded in `.cws`.
**New Layout** in a PCB workspace opens showing **mils** with a 1 mil snap; in an MMIC workspace it opens
showing **µm** with a 5 nm snap — because both come from the resolved technology, not from a hardcoded
default. A layout whose technology is missing still opens, still edits, and reports why.

## Verified substrate (consume — already exists)

- **L0a**: `Technology`, `LayerDef`, `LayerKey`, `Stackup`, `DrcRule`, `TechPersistence.LoadFromFile/SaveToFile`
  (with the gzip sniff), `TechValidation.Validate` (never throws), `StarterTechnologies.Pcb2Layer()` /
  `MmicGaAs()`. **Do not modify these** except where this brief says so.
- **L0b**: `LayoutEditorViewModel` and its metadata bar; `NewLayoutCommand` currently seeds hardcoded
  defaults (`DisplayUnit = Um`, `SnapDbu = 1000`) and leaves `TechRef` null — both change here.
- **`WorkspaceScanner`** already turns a non-cell root sub-folder into a `NodeKind.UserFolder` and its files
  into nodes via `BuildFileNode`. A `tech/` folder therefore already *appears*; only the file's `NodeKind`
  classification needs adding (mirror how `.ccolor` → `NodeKind.ColorThemeFile` is done).
- **`CwsFile`** (`src/Ui/Schematic/WorkspacePersistence.cs`) — `FormatVersion = 2`, `LibraryRefs`,
  `KnownFiles`, `DockLayout`, `CwsTreeViewState` with per-category filter flags.
- **The New Workspace dialog** (`src/Ui/CLAUDE.md` §"New Workspace dialog + Open Workspace + Recent
  Workspaces") — extend it, don't rebuild it.
- `IMessageSink` / the Messages pane for every warning below.

## Code changes

### 1. `src/Ui/Layout/TechnologyResolver.cs` — framework-free resolution

**The resolver returns diagnostics; it never posts them.** Keep it free of `IMessageSink` and of Avalonia so
it stays headless-testable; `WorkspaceViewModel` posts what comes back. This is the same split that keeps
`WorkspaceScanner` testable.

```csharp
public sealed record TechResolution(
    Technology?              Tech,          // null = unresolved
    string?                  ResolvedPath,  // absolute, when found
    TechResolutionSource     Source,        // LayoutRef | WorkspaceDefault | None
    IReadOnlyList<string>    Diagnostics);  // human-readable, may be non-empty even on success
```

**Resolution order** — exactly this, and document it in the class header:

1. `LayoutView.TechRef` non-null → resolve **relative to the `.clay` file's own directory**.
2. Otherwise → the workspace default (`CwsFile.DefaultTechRef`), resolved **relative to the workspace root**.
3. Otherwise → `Tech = null`, `Source = None`.

**`TechRef = null` means "use the workspace default" — it is not an error, and it is the normal case.**
A `.clay` only stores a `TechRef` when it deliberately deviates from the workspace default. This convention
matters: it means Save-As and cell moves never have to rewrite a relative path, which is the kind of brittle
bookkeeping that silently breaks later. Say so in the header comment.

Failure modes, all **non-fatal**, each producing a diagnostic and `Tech = null`:
missing file; unreadable/corrupt JSON; `InvalidDataException` from a newer `FormatVersion`. On success, run
`TechValidation.Validate` and append whatever it returns to `Diagnostics` — a technology with problems still
resolves and is still usable (§2.4: never block on it).

### 2. `src/Ui/Layout/TechnologyCache.cs` — one load per file, plus a change seam

- `Technology? Get(string absPath)` — loads on first request, caches by absolute path
  (`OrdinalIgnoreCase` on Windows/macOS; use `StringComparer.OrdinalIgnoreCase`).
- `void Invalidate(string absPath)` / `void InvalidateAll()`.
- `event Action<string>? TechnologyChanged` — raised on invalidate, carrying the absolute path.

**No `FileSystemWatcher`.** Cross-platform watchers need debouncing, behave differently on each OS, and fire
during our own atomic writes. Invalidation is explicit: the tree's "Reload Technology" command (§5), a
workspace rescan, and — in L0d — the `.ctech` editor on save. State this as a deliberate non-goal in the
class header so nobody adds one later thinking it was an oversight.

### 3. `src/Ui/Layout/FallbackPalette.cs` — a layer definition for anything undefined

`static LayerDef For(LayerKey key)` — **deterministic**, framework-free, no state:

- Name: `$"L{key.Layer}/{key.Datatype}"`.
- Color: derive a hue from a stable hash of `(Layer, Datatype)` at fixed saturation/value, converted to the
  existing `CircuitRF.Ui.Theming.Rgba`. **Determinism is the requirement** — the same layer must get the same
  color on every machine and every session, or two people comparing screenshots will disagree about which
  layer is which. Golden-value tests, not "looks random enough".
- `FillOpacity = 0.35`, `ZOrder = key.Layer * 1000 + key.Datatype`, `Visible`/`Selectable` true.

Two distinct callers, both worth handling here:
- **No technology at all** (`Source = None`) — every layer comes from the palette.
- **A resolved technology that simply doesn't define this layer** (common after a GDSII import in L4). The
  palette fills the gap; the caller warns **once per unknown layer per load**, never once per shape.

### 4. `CwsFile.DefaultTechRef` + tree filter flag

- Add `public string? DefaultTechRef { get; set; }` to `CwsFile` (relative to the workspace root),
  `[JsonIgnore(WhenWritingNull)]`, no `FormatVersion` bump — an absent field means "no default", which is a
  valid state and loads gracefully. This matches the alpha policy already in force.
- Add `public bool TechFiles { get; set; } = true;` to `CwsTreeViewState`, mirroring `ColorThemes`.

### 5. Project tree — classify, default, reload

- Add `NodeKind.TechFile` and classify `.ctech` in `BuildFileNode`, mirroring `.ccolor` →
  `NodeKind.ColorThemeFile` exactly. Wire the new `TechFiles` filter flag alongside `ColorThemes`.
- Give the node an icon and two context-menu commands (**no editor yet — these are the two useful actions
  that exist without one**):
  - **"Set as Workspace Default"** — writes `DefaultTechRef` into `.cws`, invalidates the cache, refreshes
    open layouts. Show a check/radio affordance on the node that currently is the default.
  - **"Reload Technology"** — `TechnologyCache.Invalidate(path)`, so a hand-edited `.ctech` takes effect
    without restarting.
- Double-click currently does nothing for `.ctech` (L0d opens the editor). Leave it a documented no-op —
  do **not** open it in a text editor or a generic viewer.

### 6. `WorkspaceViewModel` — wiring

- Own a `TechnologyCache` for the lifetime of a workspace; clear it in `NewWorkspace` / `SwitchToWorkspace` /
  `ResetToBlankShell` alongside the other per-workspace state.
- `TechResolution ResolveTechFor(LayoutView view, string? clayPath)` — calls the resolver, **posts every
  diagnostic to Messages** at Warning level, and returns the result.
- Subscribe to `TechnologyChanged` and push the newly-resolved technology into every open `LayoutDocument`
  whose resolution used that path. **This is the live-refresh seam** — in L0c the visible effect is limited
  to the metadata bar (§8), but L1/L2 will hook the renderer to exactly this event, so get the shape right
  now: the document is *told*, it does not poll.

### 7. New Workspace — choose a technology

Extend the existing New Workspace dialog with a **Technology** choice: **PCB (2-layer FR-4)** /
**MMIC (GaAs)** / **None**. On create:

- Make `tech/` at the workspace root.
- Write the chosen starter via `TechPersistence.SaveToFile` to `tech/pcb-2layer.ctech` or
  `tech/mmic-gaas.ctech`.
- Set `CwsFile.DefaultTechRef` to the relative path.
- **None** creates neither the folder nor the reference — a perfectly valid workspace that resolves to the
  fallback palette.

### 8. `LayoutEditorViewModel` / metadata bar — consume the technology

- Add `public Technology? Technology { get; }` (or a setter the document calls when the seam fires) plus
  `TechNameText` and `LayerCountText` for the metadata bar — e.g. `PCB 2-layer · 8 layers`, or
  `No technology · fallback colors` when unresolved. This readout is the only visible evidence in L0c that
  resolution works, so make it accurate rather than decorative.
- **`NewLayoutCommand` seeds from the resolved workspace default**: `DisplayUnit = tech.DefaultDisplayUnit`,
  `SnapDbu = tech.DefaultSnapDbu`, `TechRef = null` (per §1's convention). With no technology, keep L0b's
  current hardcoded defaults.
- When the change seam fires for an open document, refresh the technology and the readout. **Do not** touch
  `DisplayUnit`/`SnapDbu` on an already-open layout — those are the document's own state now, and silently
  re-seeding them from a changed technology would discard a user's choice.

## Scope guardrails (do NOT do in L0c)

- **No `.ctech` editor document** — no layer-table grid, no stackup UI, no DRC-rule grid, no new document
  type at all (L0d). The graphical stackup diagram from §10.4 is **L6**, not L0d.
- No geometry rendering, no tools, no undo (L1). No layer visibility/lock UI — the flags exist in the model
  and are not yet driven by anything.
- No `FileSystemWatcher` (see §2). No technology *inheritance* or includes between `.ctech` files.
- No DRC execution (L5b), no stackup consumption (L6), no interchange layer mapping (L4).
- Don't touch `src/Core`, `src/Engine`, `RfCore`, or L0a's model files beyond what §4 specifies.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **The headline gate.** New Workspace → PCB, then **New Layout**: the metadata bar reads mils, snap 1 mil,
   `PCB 2-layer · 8 layers`. New Workspace → MMIC, then New Layout: µm, snap 5 nm, `MMIC GaAs · 8 layers`.
   New Workspace → None, then New Layout: L0b's defaults and `No technology · fallback colors`.
3. **Resolution order** — headless tests for all three branches: a `.clay` with a `TechRef` resolves relative
   to its own directory and **wins over** the workspace default; a `.clay` with `TechRef = null` resolves to
   the workspace default; neither present → `Source = None`, `Tech = null`, no exception.
4. **Every failure is non-fatal.** Missing file, corrupt JSON, and a newer `FormatVersion` each produce
   `Tech = null` with a diagnostic, and the layout still opens and still edits. Assert the document is usable
   in each case — this is §2.4's "never block on it" as a test.
5. **A technology that fails validation still resolves** — `Tech` non-null, `Diagnostics` carries what
   `TechValidation` reported, and the layout opens.
6. **Fallback palette is deterministic** — golden `Rgba` values for a handful of `LayerKey`s; the same key
   twice in one run and across a reload yields identical colors.
7. **Unknown layer inside a valid technology** falls back per-layer and warns **once**, not once per shape.
8. **Cache** — two resolutions of the same path load the file once; `Invalidate` forces a reload;
   `TechnologyChanged` fires with the path.
9. **Change seam** — with a layout open, rewrite its `.ctech` on disk, invoke "Reload Technology" from the
   tree, and assert the open document's `TechNameText`/`LayerCountText` update **without** its `DisplayUnit`
   or `SnapDbu` changing.
10. **"Set as Workspace Default"** writes `DefaultTechRef` to `.cws`, survives a workspace reopen, and
    re-resolves layouts that were using the previous default.
11. `.ctech` files appear in the tree as `NodeKind.TechFile` and honour the `TechFiles` filter flag.

## On completion

1. Add a "Phase L0c — COMPLETE" entry at the top of `src/Ui/CLAUDE.md` in the established style: the
   resolver and its resolution order, the **`TechRef = null` means workspace-default** convention (call this
   out explicitly — it is the non-obvious rule future work will trip over), the cache and the deliberate
   absence of a file watcher, the fallback palette's determinism guarantee, the `.cws` field, the tree node
   and its two commands, the New Workspace choice, and the test file names.
2. Report back before L0d (the `.ctech` editor document — layer table, stackup list, DRC-rule grid, live
   validation surfacing, and firing the change seam on save) is briefed.
