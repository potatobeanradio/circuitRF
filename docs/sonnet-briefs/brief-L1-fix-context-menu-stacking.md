# Sonnet Brief — L1 fix: right-click stacks multiple context menus on the layout canvas

Owner report: right-clicking the layout canvas opens a context menu, but **every subsequent right-click opens
another one** without the previous being dismissed. Only one context menu should ever exist.

---

## 1. Root cause

`LayoutCanvas.ShowShapeContextMenu` ends with:

```csharp
var menu = new ContextMenu { ItemsSource = items };
menu.Open(this);
```

A **brand-new `ContextMenu` is constructed on every right-click**, opened manually, and then never closed,
never tracked, and never reused. Each call adds another popup to the window. The newest sits on top, so the
user sees one menu while N are live underneath — which is exactly the reported "creates more context menus
without displaying the previously created" behaviour, and why dismissing peels them off one at a time.

It also leaks: every menu retains its `MenuItem`s and their `Click` closures, which capture `_viewModel`.

## 2. The layout canvas is the outlier — again

The symbol editor already solves this the idiomatic Avalonia way, and it is worth copying exactly rather
than repairing the manual path.

`SymbolEditorView.axaml` (~line 535) declares **one** menu on the control:

```xml
<ctrl:SymbolEditorCanvas.ContextMenu>
    <ContextMenu Opening="OnBitmapContextMenuOpening">
        <MenuItem x:Name="CtxBitmapResolvePath" Header="Resolve Path…" Click="OnCtxBitmapResolvePath"/>
        ...
    </ContextMenu>
</ctrl:SymbolEditorCanvas.ContextMenu>
```

and `SymbolEditorCanvas.OnPointerPressed` merely **records what was under the right-click** into a field,
with the comment *"Right click → hit-test for bitmap context menu; Avalonia opens ContextMenu on release"*
and *"−1 means no bitmap under the pointer; Avalonia cancels the ContextMenu in that case."*

Framework-owned means single-instance, light-dismiss, correct placement, Menu-key / Shift+F10 support, and
proper teardown all come for free. This is the second bug in a row caused by the layout editor diverging from
an established convention in this codebase (the Scale dialog's `TextChanged` commit was the first) — so
prefer converging on the house pattern over patching the divergent one.

## 3. Fix

**R-fix-1. One `ContextMenu` instance, owned by the control, opened by Avalonia — never `new`-ed per click.**

1. Declare a single `<ContextMenu Opening="OnLayoutContextMenuOpening">` on the `LayoutCanvas` element in
   `LayoutEditorView.axaml`, mirroring `SymbolEditorView.axaml`.
2. In `LayoutCanvas.OnPointerPressed`, keep the existing right-click hit-testing but **only record state** —
   the world coordinates of the click, and the `FindEdgeForContextMenu` / `FindVertexForContextMenu` results.
   Delete the `ShowShapeContextMenu` call and the manual `menu.Open(this)`.
3. In the `Opening` handler, **rebuild `ItemsSource` from the recorded state** using the existing item-building
   logic (`AddBooleanAndFlattenMenuItems` and the edge/vertex items move over unchanged), then let Avalonia
   open it.
4. **Cancel in `Opening`** (`e.Cancel = true`) for the cases where no menu should appear — currently the
   Ctrl+right-click path that routes to ordinary press handling instead. This replaces today's "just don't
   call Show" with the framework's own suppression mechanism, exactly as the symbol editor cancels when no
   bitmap is under the pointer.

**Build fresh `MenuItem` objects each opening** and assign them to the single menu's `ItemsSource`. Do not
reuse item instances and re-subscribe `Click` — that accumulates handlers and fires an action N times on the
Nth opening, which is a nastier bug than the one being fixed.

**If `Opening` proves awkward** for obtaining the click position, the fallback is a single `ContextMenu` held
in a `_contextMenu` field: `Close()` it before every `Open(this)`, and never construct another. That fixes
the stacking but keeps the manual path; prefer R-fix-1.

## 4. Check the other canvases

`SchematicCanvas` was not inspected during this diagnosis. **Grep the whole of `src/Ui` for `new ContextMenu`
and for `.Open(` on a menu**, and confirm no other control constructs one per click. If `SchematicCanvas`
does, note it in the completion write-up — but **do not fix it in this pass**; it gets its own brief so the
regression surface stays small.

## 5. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Ten consecutive right-clicks on the canvas leave exactly one menu open.** Assert structurally, not
   visually: after N right-clicks, the count of open popups / `ContextMenu` instances associated with the
   canvas is 1. If that cannot be reached headlessly, assert that `LayoutCanvas` constructs **zero**
   `ContextMenu` objects at runtime (the single instance comes from XAML) — that is the invariant which makes
   stacking impossible, and it is checkable.
3. **Right-click, dismiss with Escape, right-click again** — the menu opens with correct, freshly-rebuilt
   items both times.
4. **Items are rebuilt per opening** — right-click on an edge (conversion items present), dismiss, right-click
   on empty canvas (conversion items absent, selection items still present per R-L1h-3).
5. **Click handlers fire exactly once** — open the menu five times and invoke the same command; assert the
   underlying view-model method ran once, not five times. This is the regression test for the
   re-subscription mistake §3 warns about.
6. **Ctrl+right-click still routes to press handling** and opens no menu (`e.Cancel`).
7. **Enablement is preserved** — R-L1h-3's disabled-with-reason items still appear disabled with their
   tooltips after the conversion.

## 6. Guardrails

- Layout canvas context menu only. No changes to the menu's *contents*, enablement rules, or any command.
- Do not touch `SymbolEditorCanvas` / `SymbolEditorView` — they are the reference, not the target.
- Do not fix `SchematicCanvas` in this pass even if it has the same pattern (§4).
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 7. On completion

Add an "L1 fix (context menu)" note at the top of `src/Ui/CLAUDE.md` recording: that `LayoutCanvas` was
constructing a **new `ContextMenu` per right-click** and opening it manually, so popups stacked and leaked;
that the fix is the **single XAML-declared menu with an `Opening` handler that rebuilds items and cancels when
nothing should show**, mirroring `SymbolEditorView.axaml`; and the result of the §4 audit of other canvases.
