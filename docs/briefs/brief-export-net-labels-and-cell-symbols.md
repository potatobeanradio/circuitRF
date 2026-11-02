# Brief: fix PDF/SVG/Bitmap export — net labels + cell-instance symbols

The clipboard exporters (`TryRenderToPdf` / `…Svg` / `…AvaloniaImage`) all reuse `SchematicRenderer.Draw`
on a throwaway model built by `BuildSelectionModel`. Two omissions there cause both bugs:

1. **Net labels missing** — `BuildSelectionModel` adds components, wires, and canvas objects to the temp
   model but **not** net labels, so `rm.NetLabels` is empty and none are drawn.
2. **Cell-instance symbols wrong** — the temp model's `SchematicDirectory` is null, so
   `ResolveAllCellRefs()` returns null; each cell-ref component then takes `ToRenderComponent`'s built-in
   path and renders as its `Generic` placeholder symbol instead of the resolved cell symbol.

Fix: thread the selection's net labels and the source `SchematicDirectory` into `BuildSelectionModel`.
Net labels are anchored to wires, and the copy passes the real wire objects (same Ids), so each label
re-anchors against its wire and renders; setting the directory makes cell-refs resolve exactly as on-screen.

Size: **S**. Files: `SchematicClipboard.cs`, `SchematicView.axaml.cs`, `SchematicViewModel.cs`.

## 1. `SchematicClipboard.cs`

### 1a. `CopyAsync` — accept net labels + directory, forward to the renderers

Add two trailing params:
```csharp
    public static async Task CopyAsync(
        IClipboard clipboard,
        IReadOnlyList<EditableComponent>    components,
        IReadOnlyList<EditableWire>         wires,
        IReadOnlyList<EditableCanvasObject> canvasObjects,
        double gridSize = 100.0,
        IReadOnlyList<EditableNetLabel>?    netLabels = null,
        string?                             schematicDirectory = null)
```
Pass them into the three render helpers:
```csharp
            byte[]? pdf = TryRenderToPdf(components, wires, canvasObjects, renderTheme, transparent, excludeGrid, netLabels, schematicDirectory);
            …
            string? svg = TryRenderToSvg(components, wires, canvasObjects, renderTheme, transparent, excludeGrid, netLabels, schematicDirectory);
            …
            Bitmap? bmp = TryRenderToAvaloniaImage(components, wires, canvasObjects, renderTheme, transparent, excludeGrid, netLabels, schematicDirectory);
```
(JSON serialization is unchanged — net labels appear in the rendered image, not the JSON paste payload;
see Notes.)

### 1b. Each `TryRenderTo*` — accept + forward the two params

Add to all three signatures (after `excludeGrid`):
```csharp
        IReadOnlyList<EditableNetLabel>? netLabels = null,
        string?                          schematicDirectory = null)
```
and change each `BuildSelectionModel(components, wires, canvasObjects)` call to:
```csharp
            var m = BuildSelectionModel(components, wires, canvasObjects, netLabels, schematicDirectory);
```

### 1c. `BuildSelectionModel` — add net labels + directory + bbox union

Signature:
```csharp
    private static (SchematicModel Rm, SchematicSpatialIndex Idx,
                    double WorldW, double WorldH, double BbMinX, double BbMinY)?
        BuildSelectionModel(
            IReadOnlyList<EditableComponent>    components,
            IReadOnlyList<EditableWire>         wires,
            IReadOnlyList<EditableCanvasObject> canvasObjects,
            IReadOnlyList<EditableNetLabel>?    netLabels = null,
            string?                             schematicDirectory = null)
```
Body — set the directory and add net labels before building:
```csharp
        var tmp = new SchematicEditModel { GridSize = 100, SchematicDirectory = schematicDirectory };
        foreach (var c in components)      tmp.Components.Add(c);
        foreach (var w in wires)           tmp.Wires.Add(w);
        foreach (var obj in canvasObjects) tmp.CanvasObjects.Add(obj);
        if (netLabels is not null)
            foreach (var nl in netLabels)  tmp.NetLabels.Add(nl);
        var (rm, idx) = tmp.BuildRenderModel();
```
After the existing bitmap-rect union loop (and before the `if (bbMinX == double.MaxValue)` check), union
the rendered net-label boxes so long labels aren't clipped (positions are recomputed from the wires in
`BuildRenderModel`):
```csharp
        // Union net-label text boxes (drawn left-aligned at (X,Y); estimate extent so they aren't clipped).
        foreach (var nl in rm.NetLabels)
        {
            double left = nl.X, right = nl.X + Math.Max(1, nl.Name.Length) * 40.0;
            double top  = nl.Y - 55.0, bot = nl.Y + 20.0;
            if (left  < bbMinX) bbMinX = left;
            if (top   < bbMinY) bbMinY = top;
            if (right > bbMaxX) bbMaxX = right;
            if (bot   > bbMaxY) bbMaxY = bot;
        }
```

## 2. `SchematicView.axaml.cs` — pass labels + directory from the copy path

In `CopySelectionToClipboardAsync`, after the segment-clone loop, gather the net labels anchored to the
selected whole wires (reuse the `wholeWireIds` set already computed there) and pass them with the directory:
```csharp
        if (comps.Count == 0 && wires.Count == 0 && objs.Count == 0) return;

        var netLabels = model.NetLabels
            .Where(n => n.IsAnchored && wholeWireIds.Contains(n.OwnerWireId))
            .ToList();
        await SchematicClipboard.CopyAsync(clipboard, comps, wires, objs, model.GridSize,
                                           netLabels, model.SchematicDirectory);
        if (cut) vm.DeleteSelection();
```

## 3. `SchematicViewModel.cs` — same in the VM copy path

In `ClipboardCopyAsync`, mirror it:
```csharp
        if (comps.Count == 0 && wires.Count == 0 && objs.Count == 0) return;

        var netLabels = EditModel.NetLabels
            .Where(n => n.IsAnchored && wholeWireIds.Contains(n.OwnerWireId))
            .ToList();
        await SchematicClipboard.CopyAsync(clipboard, comps, wires, objs, EditModel.GridSize,
                                           netLabels, EditModel.SchematicDirectory);
        if (cut) DeleteSelection();
```
(Both call sites already build `wholeWireIds = new HashSet<string>(wires.Select(w => w.Id))` before the
segment loop — that set is exactly the selected whole-wire Ids, which is what the labels are anchored to.)

## Verification

1. Select a wire with a net label, copy, paste into Preview/Keynote (PDF), Illustrator/Inkscape (SVG), and
   Word/Pages (PNG) → the net label text appears in all three, positioned on the wire.
2. A long label near the selection edge isn't clipped (bbox union covers it).
3. Select a **cell instance**, copy → the exported image shows the cell's real symbol, not the Generic
   placeholder box. A built-in component still renders as before.
4. Select a cell instance whose cell can't resolve → it shows the same Not-Found/placeholder glyph the
   on-screen canvas shows (consistent), not a crash.
5. Selection with no labels / no cell instances → exports unchanged.

## Acceptance

- PDF, SVG, and PNG exports include net labels (anchored to the copied wires) and render cell-instance
  symbols identically to the on-screen canvas.
- No change to JSON copy/paste, on-screen rendering, or the export sizing/padding logic beyond the
  net-label bbox union.

## Notes

- Net labels are added to the rendered image only; the JSON clipboard payload (`SerializeSelection`) still
  doesn't carry them, so in-app paste won't reproduce labels. That's a separate item (the JSON
  clipboard/paste format doesn't model net labels yet) — out of scope here unless you want it.
- `SchematicDirectory` is null only for unsaved schematics, which can't contain cell instances (placement
  requires a saved schematic), so passing null stays correct.

## Note for a future EMF (CF_ENHMETAFILE) exporter

There is no EMF exporter today — `CopyAsync` explicitly omits it (Windows-only; needs
`System.Drawing.Imaging` + Svg.NET; see the comment referencing splotRF's `WindowsClipboard.cs`). When EMF
is added, route it through `BuildSelectionModel` exactly like `TryRenderToPdf/Svg/AvaloniaImage` and pass
the **same** `netLabels` + `schematicDirectory` arguments — then it inherits these two fixes (net labels
rendered, cell-instance symbols resolved) and the net-label bbox union for free. An EMF path that builds its
own model or rasterizes separately must wire in those two arguments itself, or it will reproduce both bugs.
