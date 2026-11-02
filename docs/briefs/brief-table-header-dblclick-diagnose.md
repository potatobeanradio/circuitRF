# Diagnostic Brief — Table trace-header double-click does nothing (INSTRUMENT FIRST, do not fix yet)

## Status
The double-click handler chain for the Table trace-header inline editor IS present and looks correct
on paper (this is why it was previously marked complete):
- `PlotContainerView.OnViewDoubleTapped` → `_plotControl.HandleDoubleTapAt(pos)`.
- `PlotControl.HandleDoubleTapAt` → Table branch → `TableHitKind.TraceHeader` →
  `ShowPlotInspector(idx)` + `_inspectorView?.FocusSpecTextBox(idx)`.
- `PlotInspectorView.FocusSpecTextBox` scrolls + focuses the spec TextBox.

But at runtime, double-clicking a Table column (trace) header does NOT open/focus the inline spec
editor. The code path exists; it is not firing (or fires into the wrong branch, or focus is stolen
back). **This is a runtime timing/dispatch bug — reading the code further will not resolve it. We must
observe which hop fails.** DO NOT write a fix in this pass. Add the probes below, run, report the
console output, and STOP.

## Why instrument (context)
`PlotContainerView` captures the pointer to itself on the FIRST left press
(`OnViewPointerPressed`: `e.Pointer.Capture(this); e.Handled = true;`) and treats each non-drag
release as a select click. The `DoubleTapped` gesture is then expected to fire on the UserControl
(`OnViewDoubleTapped`). There are at least three distinct ways this silently fails, and they need
different fixes — so we identify which one BEFORE touching anything:
  (A) `DoubleTapped` never fires (capture + `e.Handled=true` on first press suppresses the synthesized
      gesture) → neither probe 1 nor probe 2 prints.
  (B) `DoubleTapped` fires but the table hit-test returns a non-TraceHeader kind (geometry/zoom
      mismatch, or the tap lands on FreqHeader/DataCell/ResizeHandle boundary) → probe 1 prints,
      probe 2 prints the WRONG kind, and we fall through to the `else → ShowPlotInspector()` (no focus).
  (C) Both fire correctly but focus is stolen back (flyout open race, or the TextBox isn't found
      because the cards haven't templated yet) → probes 1+2+3 all print but the box never focuses.

NOTE on the ContextMenu question: the Table context menus (`ShowTraceHeaderContextMenu`,
`BuildTableContextMenu`) are built and opened imperatively in code-behind, only from
`OnPointerReleased` on the RIGHT button. There is NO `ContextMenu=` attribute in
PlotContainerView.axaml. So a XAML-attached ContextMenu is NOT eating the left double-tap. The
likelier culprit is the pointer-capture + `e.Handled=true` on first left press (mechanism A). The
probes will confirm.

## Probes (add exactly these; remove after diagnosis)

### Probe 1 — does the double-tap reach the handler, and where does it route?
File: `src/Ui/Views/DataDisplay/PlotContainerView.axaml.cs`, in `OnViewDoubleTapped`, at the very top
(before the `DataContext is PlotContainerViewModel vm` block):
```csharp
System.Diagnostics.Debug.WriteLine(
    $"[DBL] OnViewDoubleTapped pos={e.GetPosition(this)} " +
    $"plotType={(DataContext as PlotContainerViewModel)?.PlotVM.Plot.PlotType}");
```
And immediately before the final `_plotControl.HandleDoubleTapAt(...)` fallthrough line:
```csharp
System.Diagnostics.Debug.WriteLine($"[DBL] → HandleDoubleTapAt {e.GetPosition(_plotControl)}");
```

### Probe 2 — what kind does the table hit-test return?
File: `src/Ui/DataDisplay/Controls/PlotControl.cs`, in `HandleDoubleTapAt`, inside the
`if (_plot.PlotType == PlotType.Table)` block, right AFTER the `var hit = TableRenderer.HitTest(...)`
line:
```csharp
System.Diagnostics.Debug.WriteLine(
    $"[DBL] table hit kind={hit.Kind} trace={(hit.HitTrace is null ? \"null\" : \"set\")} " +
    $"row={hit.RowIndex} col={hit.ColIndex}");
```
And inside the `TableHitKind.TraceHeader` branch, after computing `idx`:
```csharp
System.Diagnostics.Debug.WriteLine($"[DBL] TraceHeader idx={idx} -> ShowPlotInspector+FocusSpecTextBox");
```

### Probe 3 — does focus find its target?
File: `src/Ui/Views/DataDisplay/PlotInspectorView.axaml.cs`, in `FocusSpecTextBox`, at the top:
```csharp
System.Diagnostics.Debug.WriteLine($"[DBL] FocusSpecTextBox idx={traceIndex} vm={(_vm is null ? \"null\" : \"set\")}");
```
And inside the `Dispatcher.UIThread.Post(...)` lambda, just before the `foreach (var tb ...)`:
```csharp
System.Diagnostics.Debug.WriteLine(
    $"[DBL] focus pass: targetVm={(targetVm is null ? \"null\" : \"set\")} " +
    $"textboxes={this.GetVisualDescendants().OfType<TextBox>().Count()}");
```
(`FocusSpecTextBox` already imports `System.Linq` and `Avalonia.VisualTree`, so `OfType` and
`GetVisualDescendants` resolve without new usings.)

## Run + report
1. Build (Debug). Open a Data Display with a **Table** plot that has >=2 traces (columns).
2. Double-click squarely on a **trace column header** (the header cell above a data column — NOT the
   leftmost Freq header, NOT a data cell, NOT the column-border resize handle).
3. Copy the full `[DBL] ...` console output here. Also note: did the Plot Inspector flyout open at all?
   Did any text field get focus/selection?
4. STOP. Do not implement a fix. The probe output tells us which branch (A/B/C above) is the bug, and
   the fix differs per branch:
     - (A) → fix in the pointer-capture/gesture path (e.g. don't swallow the gesture; or let
       PlotControl handle its own DoubleTapped for Table; or synthesize the double-tap from the press
       timestamps the container already sees).
     - (B) → fix in `TableRenderer.HitTest` header-band geometry / zoom scaling.
     - (C) → fix the focus timing (raise Dispatcher priority, await flyout Loaded, or focus after the
       card's template is realized).

## Gate
No behavior change this pass — probes only. Build 0W/0E. Report console output. A follow-up brief will
carry the actual fix once we know which hop fails.

## Note
Bug 2 was previously reported COMPLETE because the code path was added (brief-table-cube-layout-fixes
#5). The path compiles and is wired, but does not fire at runtime — "code exists" was mistaken for
"works." This diagnostic closes that gap.
