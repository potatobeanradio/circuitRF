# Library Palette — Fix v2: cached ToolControl template (keep tabbed tool docks) (Claude Code / Sonnet)

**Replaces the split-dock fix.** The owner rejects splitting the left column into one-tool-per-ToolDock: it
sacrifices tabbed tool docks (valuable for saving screen space) AND it does **not** actually fix the
Properties + Analyses dock, which suffers the **same** bug. The principled fix is the one already proven for
documents in this app: **give `ToolControl` a non-deferred cached content template**, mirroring the existing
`DocumentControl` override — so tabbed tool docks work everywhere (Project Tree + Library, Properties +
Analyses) without layout surgery. Sub-gated; **instrument/confirm before finalizing**; report between layers.
Firewall green.

> Read first: `src/Ui/App.axaml` — the **existing, working** document fix:
> `<Style Selector="dockCtrl|DocumentControl"><Setter Property="Template"
> Value="{DynamicResource DockDocumentControlCachedContentTemplate}"/></Style>` and its comment explaining the
> `DeferredContentControl` deferred-realization problem (this is the exact analog). Context:
> `src/Ui/ViewModels/Dock/CircuitRfDockFactory.cs` (Sonnet split it into 3 single-tool ToolDocks — **revert**
> to the tabbed layout: Project Tree + Library in one ToolDock, Properties + Analyses in another),
> `src/Ui/Views/ProjectTree/ProjectTreeView.axaml` + `PaletteToolView.axaml` + `Properties/PropertiesView`
> + `Analyses/AnalysesToolView` (`x:DataType` compiled-binding views whose content must swap on tab switch),
> the **Dock.Avalonia.Themes.Fluent 12.0.0.2** package theme XAML — the `ToolControl` default template and the
> `DockDocumentControlCachedContentTemplate` resource (in the NuGet cache:
> `~/.nuget/packages/dock.avalonia.themes.fluent/12.0.0.2/...` — extract the `.axaml`/compiled XAML to see both
> templates' structure). Diagnosis already established (Sonnet): Dock 12.0.0.2 `ToolControl` uses
> `DeferredContentControl`, which **retains the prior view and only swaps DataContext** on tab switch, so a
> retained `ProjectTreeView` rebinds to a `PaletteTool` and its `x:DataType` bindings no-op → "No workspace
> open". The package ships a cached template for `DocumentControl` but **not** for `ToolControl`.

## The spine (do not violate)
- **Keep tabbed tool docks.** Project Tree + Library Palette share one ToolDock; Properties + Analyses share
  another. **Revert Sonnet's 3-ToolDock split.** Tabbing is a deliberate UX choice (screen space).
- **Fix the mechanism, not the layout** — the bug is `ToolControl`'s deferred content template reusing the
  realized view across DataContext changes. Defeat it the same way the document fix does: host the active
  dockable in a **plain, non-deferred content host resolved via the App DataTemplates**, so a tab switch
  **realizes the correct view** for the new dockable (not the retained one).
- **Mirror the existing document fix** — `DocumentControl` already works because of
  `DockDocumentControlCachedContentTemplate`. Author the **tool** equivalent: a `ControlTemplate` for
  `ToolControl` (since the package ships none) that reproduces the package's `ToolControl` chrome (tab strip +
  content area) but swaps the deferred content host for a plain `ContentControl` bound to the active
  dockable. Apply it via `<Style Selector="dockCtrl|ToolControl"><Setter Property="Template" .../></Style>`.
- **Don't regress** Project Tree, Properties, Analyses, Messages, or the document tabs; keep the existing
  `ToolControl` background style.
- **Scope fence:** the cached `ToolControl` template + revert the layout split. No other changes.

---

## LAYER 1 — confirm the template structure + author the cached ToolControl template

1. **Extract** the Dock.Avalonia.Themes.Fluent 12.0.0.2 theme XAML from the NuGet cache and locate (a) the
   default `ToolControl` `ControlTemplate` (to mirror its tab-strip + content chrome) and (b)
   `DockDocumentControlCachedContentTemplate` (to mirror how the document fix hosts content non-deferred).
   **Report** the relevant structure (the content-host element each uses) before authoring.
2. **Author a cached `ToolControl` `ControlTemplate`** (e.g. in App.axaml resources or a small dedicated
   `.axaml` ResourceDictionary, keyed like `CrfToolControlCachedContentTemplate`): identical tab-strip chrome
   to the package template, but the content area is a **plain `ContentControl`** whose `Content` is the
   ToolDock's **active dockable** and whose content is resolved by the App DataTemplates (the same
   `{x:Type dockVm:PaletteTool} → PaletteToolView` etc. mappings) — NOT a `DeferredContentControl`. This makes
   the content presenter re-evaluate the DataTemplate per active dockable, realizing the correct view on each
   tab switch.
3. **Apply** via `<Style Selector="dockCtrl|ToolControl"><Setter Property="Template"
   Value="{DynamicResource CrfToolControlCachedContentTemplate}"/></Style>` in App.axaml (next to the
   `DocumentControl` one, with an analogous explanatory comment).

**Layer 1 gate:** the cached `ToolControl` template compiles and is applied; the app launches with the dock
rendering (tab strips + content present, chrome intact). Report the extracted template structure + the new
template. Report.

---

## LAYER 2 — revert the layout split + verify all tabbed tool docks switch correctly

1. **Revert** `CircuitRfDockFactory` to the tabbed layout: **Project Tree + Library Palette** in one ToolDock
   (`projectTreeDock` with `VisibleDockables = [ProjectTreeTool, PaletteTool]`), **Properties + Analyses** in
   another (`propertiesDock` with `[PropertiesTool, AnalysesTool]`). Remove the separate `paletteDock` and the
   3-way split. Update the class comment (the deferred-content bug is now fixed via the cached template, not
   avoided via layout).
2. **Verify the fix** by tab-switching every multi-tab tool dock:
   - Project Tree ↔ Library: each tab shows its **own** view (tree with its content; Palette with the category
     ComboBox + search + **all built-in component tiles**) — **no "No workspace open" on the Library tab**.
   - Properties ↔ Analyses: each tab shows its own view (this pair was also broken — confirm it now works).
   - Messages (single tab), document tabs (Schematic/Symbol/Cell editors): unregressed.
3. **Instrument if needed** — if any tab still shows the wrong view, log the `ToolControl` content host's
   resolved view type + DataContext on tab switch to confirm the plain `ContentControl` is re-resolving;
   report before further changes.

**Layer 2 gate:** with the tabbed layout restored, switching Project Tree↔Library and Properties↔Analyses
shows each dockable's correct view (Library shows the populated tile grid, no "No workspace open"); Messages +
document tabs unregressed; the left column is back to the tabbed arrangement (not 3 split docks). Report
(screenshot description of both tab pairs).

## Acceptance
1. A cached `ToolControl` `ControlTemplate` (non-deferred plain `ContentControl`, App-DataTemplate-resolved,
   package chrome mirrored) is applied via a `dockCtrl|ToolControl` Template style — the tool analog of the
   existing `DocumentControl` cached-template fix.
2. The layout split is **reverted**: Project Tree + Library and Properties + Analyses are tabbed ToolDocks
   again; both pairs switch tabs correctly (each shows its own view; Library shows all built-in tiles, no "No
   workspace open").
3. Messages, document tabs, dock chrome (tab strips, splitters, backgrounds) all unregressed.
4. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **Keep tabbed tool docks** — revert the split; tabbing is the desired UX.
- **Fix the mechanism** — cached non-deferred `ToolControl` template mirroring the working `DocumentControl`
  fix; do NOT solve via layout isolation.
- **Mirror the package chrome** — extract + reproduce the tab-strip/content structure so only the deferred
  content host changes; don't lose tool-dock chrome.
- **Confirm by tab-switching both pairs** (Project Tree↔Library, Properties↔Analyses) — instrument if a tab
  still shows the wrong view.
- **Scope fence:** cached ToolControl template + layout revert only.
- Sub-gate the two layers; report and stop between each.
- Update `library-palette.md` + `src/Ui/CLAUDE.md`: the `ToolControl` deferred-content bug is fixed app-wide
  via a cached template (mirroring `DocumentControl`); tabbed tool docks are supported; record the resource
  name + that it must mirror the package chrome on Dock upgrades.

*Exit: tabbed tool docks work app-wide — Project Tree + Library and Properties + Analyses each switch tabs to
their own view — fixed at the mechanism (a cached `ToolControl` template, the tool analog of the document fix)
rather than by splitting docks, preserving the screen-space-saving tabbed UX.*
