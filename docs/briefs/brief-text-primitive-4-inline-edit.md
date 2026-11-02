# Brief: text-primitive (4/5) — double-click inline edit in the Symbol Editor

Fourth of the 5-brief Text sequence (1–3 landed). Double-clicking a text primitive in the Symbol Editor
opens an inline TextBox to edit its content, committing via the existing `SetTextPrimitiveCommand`
(undoable). Mirrors `SchematicView`'s inline editor but much leaner — symbol text has only `Content` to
edit, no label rows / prefixes.

Size: **M**. Files: `SymbolEditorCanvas.cs` (3 small additions), `SymbolEditorViewModel.cs`
(event + double-click + commit), `SymbolEditorView.axaml` (TextBox overlay), `SymbolEditorView.axaml.cs`
(inline-edit handlers).

## 1. `SymbolEditorCanvas.cs` — expose viewport + world→screen

Add public members (the inline box needs these to position itself and follow pan/zoom):
```csharp
    public double CurrentZoom => _zoom;

    public (double X, double Y) WorldToScreen(double wx, double wy)
        => ((wx - _panX) * _zoom, (wy - _panY) * _zoom);

    public event EventHandler? ViewportChanged;
```
Raise `ViewportChanged?.Invoke(this, EventArgs.Empty);` in the spots that change pan/zoom:
- in `OnPointerMoved`, inside the `if (_isPanning)` branch (after updating `_panX/_panY`);
- at the end of `OnPointerWheel` (after recomputing `_panX/_panY`);
- in the public `ZoomToFit()` (after `ZoomToFitInternal()`);
- in `OnLayoutUpdated`, after the initial `ZoomToFitInternal()`.

## 2. `SymbolEditorViewModel.cs` — request event, double-click, commit

Add the request payload + event (near the other public members):
```csharp
    /// <summary>Payload raised on double-click of a text primitive; the view opens an inline editor.</summary>
    public readonly record struct TextEditRequest(int Index, double WorldX, double WorldY,
                                                  string Content, double FontSize);

    public event Action<TextEditRequest>? TextEditRequested;
```

Forward the click count into `SelectToolPress`. In `OnPointerPressed`:
```csharp
        if (ActiveTool == Tool.Select)
        {
            SelectToolPress(lx, ly, mods, clickCount);
            return;
        }
```
Change the signature and add the double-click branch at the **top** of `SelectToolPress`:
```csharp
    private void SelectToolPress(double lx, double ly, KeyModifiers mods, int clickCount = 1)
    {
        // Double-click a text primitive → request inline content edit (handled by the view).
        if (clickCount >= 2 && !IsLocked)
        {
            int th = HitTestTopmost(lx, ly);
            if (th >= 0 && EditableSymbol.Primitives[th] is TextPrimitive tp)
            {
                _selection.Clear();
                _selectedPins.Clear();
                _selection.Add(th);
                RebuildOverlay();
                var (bx0, by0, _, _) = SymbolGeometry.BboxOf(tp);   // top-left = box anchor for the editor
                TextEditRequested?.Invoke(new TextEditRequest(th, bx0, by0, tp.Content, tp.FontSize));
                return;
            }
        }

        bool shift = (mods & KeyModifiers.Shift) != 0;
        // … existing body unchanged …
```

Add the commit method (uses the existing internal `SetTextPrimitiveCommand`, already imported):
```csharp
    /// <summary>Commits an inline text edit (undoable). No-op when locked, unchanged, or the index is
    /// stale / not a TextPrimitive.</summary>
    public void CommitTextEdit(int index, string newContent)
    {
        if (IsLocked || index < 0 || index >= EditableSymbol.Primitives.Count) return;
        if (EditableSymbol.Primitives[index] is not TextPrimitive tp) return;
        if (tp.Content == newContent) return;
        Execute(new SetTextPrimitiveCommand(EditableSymbol, tp, newContent, tp.FontSize, tp.FontStyle));
    }
```

## 3. `SymbolEditorView.axaml` — TextBox overlay over the canvas

Wrap the canvas in a `Panel` and add a hidden inline `TextBox` sibling. Replace the existing
`<ctrl:SymbolEditorCanvas …> … </ctrl:SymbolEditorCanvas>` block with:
```xml
        <Panel>
            <ctrl:SymbolEditorCanvas x:Name="SymbolEditorCanvasCtrl"
                                     ViewModel="{Binding ViewModel}"
                                     ClipToBounds="True">
                <ctrl:SymbolEditorCanvas.ContextMenu>
                    <ContextMenu Opening="OnBitmapContextMenuOpening">
                        <MenuItem x:Name="CtxBitmapResolvePath" Header="Resolve Path…"  Click="OnCtxBitmapResolvePath"/>
                        <MenuItem x:Name="CtxBitmapRefreshCache" Header="Refresh Cache" Click="OnCtxBitmapRefreshCache"/>
                    </ContextMenu>
                </ctrl:SymbolEditorCanvas.ContextMenu>
            </ctrl:SymbolEditorCanvas>

            <TextBox x:Name="InlineEditBox"
                     IsVisible="False"
                     HorizontalAlignment="Left" VerticalAlignment="Top"
                     Padding="4,2" BorderThickness="1"
                     FontFamily="IBM Plex Sans, sans-serif"
                     AcceptsReturn="False"
                     KeyDown="OnInlineEditKeyDown"
                     LostFocus="OnInlineEditLostFocus"/>
        </Panel>
```

## 4. `SymbolEditorView.axaml.cs` — inline-edit handlers

Add `using Avalonia.Threading;` (Thickness/`Avalonia`, `Avalonia.Input`, `Avalonia.Interactivity` already
imported). In the constructor, after the existing wiring:
```csharp
        DataContextChanged += OnDataContextChanged;
        SymbolEditorCanvasCtrl.ViewportChanged += (_, _) => RepositionInlineEditBox();
```
Add the members + handlers:
```csharp
    private SymbolEditorViewModel? _subscribedVm;
    private SymbolEditorViewModel.TextEditRequest? _editReq;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm is not null) _subscribedVm.TextEditRequested -= OnTextEditRequested;
        _subscribedVm = (DataContext as SymbolEditorDocument)?.ViewModel;
        if (_subscribedVm is not null) _subscribedVm.TextEditRequested += OnTextEditRequested;
    }

    private void OnTextEditRequested(SymbolEditorViewModel.TextEditRequest req)
    {
        _editReq = req;
        double zoom = SymbolEditorCanvasCtrl.CurrentZoom;
        InlineEditBox.FontSize = Math.Max(zoom * req.FontSize, 9.0);
        var (sx, sy) = SymbolEditorCanvasCtrl.WorldToScreen(req.WorldX, req.WorldY);
        InlineEditBox.Margin = new Thickness(sx - 4, sy - 2, 0, 0);
        InlineEditBox.Text   = req.Content;
        InlineEditBox.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => { InlineEditBox.Focus(); InlineEditBox.SelectAll(); },
            Avalonia.Threading.DispatcherPriority.Input);
    }

    private void RepositionInlineEditBox()
    {
        if (!InlineEditBox.IsVisible || _editReq is not { } req) return;
        double zoom = SymbolEditorCanvasCtrl.CurrentZoom;
        InlineEditBox.FontSize = Math.Max(zoom * req.FontSize, 9.0);
        var (sx, sy) = SymbolEditorCanvasCtrl.WorldToScreen(req.WorldX, req.WorldY);
        InlineEditBox.Margin = new Thickness(sx - 4, sy - 2, 0, 0);
    }

    private void OnInlineEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) { CommitInlineEdit(); e.Handled = true; }
        else if (e.Key == Key.Escape)         { DismissInlineEdit(); e.Handled = true; }
    }

    private void OnInlineEditLostFocus(object? sender, RoutedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (InlineEditBox.IsVisible && !InlineEditBox.IsKeyboardFocusWithin) CommitInlineEdit();
        }, Avalonia.Threading.DispatcherPriority.Background);

    private void CommitInlineEdit()
    {
        if (_editReq is not { } req) { DismissInlineEdit(); return; }
        string text = InlineEditBox.Text ?? "";
        DismissInlineEdit();
        (DataContext as SymbolEditorDocument)?.ViewModel.CommitTextEdit(req.Index, text);
    }

    private void DismissInlineEdit()
    {
        _editReq = null;
        InlineEditBox.IsVisible = false;
        SymbolEditorCanvasCtrl.Focus();
    }
```
Finally, guard the existing shortcut tunnel so typing in the box isn't intercepted — at the **top** of
`OnViewKeyDownTunnel`, before the `vm` lookup:
```csharp
        if (InlineEditBox.IsKeyboardFocusWithin) return;   // inline box owns its own Enter/Esc
```

## Verification (runtime)

1. Double-click a text primitive (Select tool) → an inline TextBox appears over it, pre-filled and
   selected. Edit, press **Enter** → content updates (one undo step). **Esc** → reverts/leaves unchanged.
   Click away → commits.
2. While editing, pan/zoom (middle-drag, wheel) → the box follows the text. `F`/`S`/`R`/etc. shortcuts do
   **not** fire while the box has focus; they work again after it closes.
3. Double-clicking empty space or a non-text primitive does nothing new (normal select/drag).
4. Locked (built-in) symbols: double-click does not open an editor.

## Acceptance

- Double-click a text primitive opens an inline editor; Enter/blur commits via `SetTextPrimitiveCommand`
  (undoable), Esc cancels.
- The box tracks pan/zoom and suppresses global shortcuts while focused.
- No change to placement-time text typing, other primitives, or the schematic view.
