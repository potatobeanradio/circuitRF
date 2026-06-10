# Library Palette — Fixes: multi-category metadata + the Palette view doesn't render (Claude Code / Sonnet)

Two bugs + one design change from testing. **(Bug 1+2 are one root cause)**: the **Library (Palette) tab
renders the Project Tree's view** — its header + "No workspace open" — instead of `PaletteToolView`, so no
components show. **(Change 3)**: let a component belong to **more than one category** (e.g. MLIN in both
Microstrip and Transmission Line). Sub-gated; report between layers. Firewall green.

> Context code: `src/Ui/ViewModels/Dock/CircuitRfDockFactory.cs` (`projectTreeDock.VisibleDockables =
> [ProjectTreeTool, PaletteTool]` — the two tools are **tabs in one ToolDock**), `src/Ui/App.axaml` (the
> `DataTemplate {x:Type dockVm:PaletteTool} → palv:PaletteToolView` mapping **is present**; also the
> `DockDocumentControlCachedContentTemplate` override on `DocumentControl`), `src/Ui/Views/Palette/
> PaletteToolView.axaml(.cs)` (the view — binds `DisplayedItems`/`Categories`/`SelectedCategory`/`HasNoItems`;
> `x:DataType="dockVm:PaletteTool"`), `src/Ui/ViewModels/Dock/PaletteTool.cs` (`DisplayedItems` etc. — the VM
> is fully implemented), `src/Ui/Schematic/LibraryCatalog.cs` (`AllItems`/`ByCategory`/`Common`/`Search` —
> `BuildAllItems` reads `info.Category`/`SearchTerms`/`IsCommon`), `src/Ui/Schematic/ComponentTypeRegistry.cs`
> (`ComponentTypeInfo` has `Category` (singular) + `SearchTerms` + `IsCommon`; `Registry` dict). Design doc:
> `library-palette.md` §2 (categories), §5 (the tile grid that should render).

## The spine
- **Bug 1+2 = one root cause:** the Palette tab is **not rendering `PaletteToolView`** — it shows Project-Tree
  content. The DataTemplate mapping exists in App.axaml, so this is a runtime/Dock view-resolution issue, not
  a missing template. **Instrument first** (this depends on Dock internals) — diagnose *why* the Palette
  tool's content isn't its own view, then fix.
- **Change 3:** a component's category becomes a **set**, not a single value — filtering shows a component in
  **every** category it belongs to. Additive, registry-driven (the contribution point).
- **Scope fence:** these two. No new Palette features.

---

## LAYER 1 — multi-category metadata (change 3)

Let a component declare **multiple categories**:
1. **`ComponentTypeInfo.Category` (singular) → `Categories` (a set/list)** — `IReadOnlyList<ComponentCategory>`
   (or keep `Category` as the *primary* for sort and add `ExtraCategories` — pick the cleaner; the design
   intent is "appears in any of N categories"). Update the `Registry` entries to the new shape (most have one
   category; the mechanism allows N). Keep `Lumped`/`Sources`/`Terminals` as today, but e.g. a future MLIN can
   be `[Microstrip, TransmissionLine]`.
2. **`PaletteItem`** carries the category set (or primary + extras). **`LibraryCatalog.ByCategory(cat)`** now
   returns items where the set **contains** `cat` (so MLIN appears under both Microstrip and Transmission
   Line). `BuildAllItems` sort: order by the **primary/first** category (keep a stable `CategorySortKey` on
   the primary), then display name — so an item still has one stable position in "All", but matches multiple
   category filters.
3. **`CategorySortKey`** + the header category list (`PaletteTool.BuildCategories`) unchanged in spirit — a
   real category appears in the ComboBox if **any** item lists it.
4. Tests: a component with two categories appears in `ByCategory` for **both**; `AllItems` still lists it once;
   `Common`/search unaffected.

**Layer 1 gate:** a component declaring `[Microstrip, TransmissionLine]` (add a temporary test entry or use a
real one if MLIN exists) appears under **both** category filters and once in All; existing single-category
items unchanged; tests green. Report.

---

## LAYER 2 — INSTRUMENT then FIX the Palette view rendering (bug 1+2)

The Palette tab shows the Project Tree view + "No workspace open" instead of `PaletteToolView`. The
DataTemplate mapping is present in App.axaml, so **diagnose at runtime before changing code**:
1. **Instrument:** confirm whether `PaletteToolView` is constructed at all (ctor breakpoint/log), whether its
   `DataContext` is a `PaletteTool` (vs. a `ProjectTreeTool`), and whether a **binding/Build exception** is
   thrown when the Palette tab activates (compiled-binding `x:DataType` mismatch, or a throw in the view ctor
   → Dock falls back to a sibling's content). Check: does selecting the Palette tab in the shared ToolDock
   actually swap `ToolControl` content, or does it keep showing the previously-active tool (`ProjectTreeTool`)?
   **Report findings — no fix yet.**
2. **Likely candidates** (confirm against the instrumentation, don't patch blind):
   - the two tools share one `ToolDock` and the `ToolControl` content template isn't re-resolving on tab
     switch (a Dock cached-template / `DeferredContentControl` issue analogous to the documented
     `DocumentControl` cached-template fix already in App.axaml — a `ToolControl` may need the same
     non-deferred template override);
   - a compiled-binding failure in `PaletteToolView` (e.g. a bound member name/typo, or `x:DataType`
     resolution) causing a fallback;
   - the DataTemplate ordering (the `ViewLocator` `IDataTemplate` listed first may be greedily matching the
     tool before the explicit `PaletteTool` DataTemplate — though `ViewLocator.Match` only matches
     `ViewModelBase`, confirm `Tool` isn't somehow matched).
3. **Fix** per the finding. If it's the shared-ToolDock content-template issue, the cleanest fix may be either
   the `ToolControl` cached-template override (mirroring the existing `DocumentControl` one) **or** giving the
   Palette its **own ToolDock** (a separate tabbed pane) so it's not sharing the Project Tree's — decide based
   on the diagnosis and what's least disruptive. Verify the Project Tree, Properties, Analyses, and Messages
   tools still render correctly after the fix.

**Layer 2 gate:** the Library tab renders `PaletteToolView` — category ComboBox + search + the **tile grid
showing all built-in components** (R/L/C/V/VTone/GND/Port/FET/SDD/Z/Generic); switching between the Project
Tree and Library tabs shows each one's own view; no "No workspace open" on the Library tab; the other dock
tools are unregressed. Report.

## Acceptance
1. Components can belong to **multiple categories**; `ByCategory` returns an item for **each** of its
   categories; it appears once in All; existing items unchanged; tests green.
2. The Library tab renders `PaletteToolView` with the populated tile grid (all built-ins) + working
   category/search; no Project-Tree content leaks into it; other tools unregressed.
3. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **Instrument before fixing bug 1+2** — the DataTemplate exists; the cause is a runtime/Dock resolution
  issue. Diagnose, report, then fix per the finding (don't guess-patch).
- **Multi-category is additive + registry-driven** — `ByCategory` uses set-containment; `AllItems` lists once
  (stable primary-category sort).
- Don't regress the other dock tools' rendering.
- Sub-gate the two layers; report and stop between each.
- Update `library-palette.md` §2 (multi-category) + §10 notes, and `src/Ui/CLAUDE.md` (the Palette view-
  resolution fix, if it reveals a reusable Dock gotcha).

*Exit: the Library Palette renders its own view with all built-in components shown, components can appear in
multiple categories, and the Project-Tree content no longer leaks into the Library tab.*
