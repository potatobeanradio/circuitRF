# Sonnet Brief — Schematic inline text-editor: position + UX fixes

The label-hitbox brief landed `SchematicComponent.LabelRowGeometry(...)` and `LabelBaseYFor(...)` as
the single source of truth for label geometry (world coords; renderer + hit-test both use them). The
inline edit box, however, still positions itself with a HAND-ROLLED copy of the label math in
`SchematicView.axaml.cs` — so after the hitbox change the box lands in the wrong place, and it drifts
lower as you zoom out (a fixed `+2 px` per row that doesn't scale, plus a wrong base constant). This
brief unifies the box position on `LabelRowGeometry` and fixes four related UX issues at once.

Files touched:
- `src/Ui/Views/Content/SchematicView.axaml.cs` — box positioning, focus selection, prefix measure.
- `src/Ui/ViewModels/SchematicViewModel.cs` — edit value string, selection range, includes-name flag,
  unit remap parse, VAR/SDD name commit.
- `src/Ui/Commands/Schematic/EditParameterCommand.cs` — add optional name change.

---

## FIX 1 — Position the inline box from LabelRowGeometry (fixes wrong position + zoom-out drift)

The renderer draws label row `i` left-aligned at the Skia baseline
`world = LabelRowGeometry(cx, cy, i, oDx, oDy, symbol, portCount).{BaselineX, BaselineY}`. The inline
box must sit on that exact world point, transformed by `WorldToScreen`. Replace the hand-rolled
formula entirely.

### 1a. Carry symbol + port count on the anchor
The anchor needs `Symbol` and `PortCount` so it can call `LabelBaseYFor`/`LabelRowGeometry`.
```csharp
private sealed record ComponentLabelAnchor(
    double CompX, double CompY, int Row, double ODx, double ODy,
    SymbolKind Symbol, int PortCount,
    double PrefixWorldUnits = 0);
```

### 1b. Measure the prefix at a fixed size (zoom-independent), and store symbol/port count
In `OnTextLabelDoubleTapped`, replace the prefix-measure block + anchor construction with:
```csharp
// Prefix width in WORLD units: measure "<Name> = " at the renderer's reference size (70) so it is
// zoom-independent (the renderer scales the same text by zoom*70). Multiplying happens via
// WorldToScreen at position time, so no per-zoom re-measure is needed.
double prefixWorldUnits = 0;
if (hit.Kind == SchematicHitTest.HitKind.ComponentParam
    && hit.SubIndex < editComp.Parameters.Count)
{
    var pName = editComp.Parameters[hit.SubIndex].Name;
    if (!string.IsNullOrEmpty(pName))
    {
        using var mf = new SKFont(SkiaFonts.PlexRegular, 70f);
        prefixWorldUnits = mf.MeasureText($"{pName} = ");
    }
}

_labelAnchor = new ComponentLabelAnchor(
    editComp.X, editComp.Y, row, oDx, oDy,
    editComp.Symbol, editComp.PortCount, prefixWorldUnits);
```

### 1c. Single screen-anchor helper used by both show and reposition
```csharp
// Screen position of this label row's text anchor (Skia baseline / left edge), derived from the
// SAME LabelRowGeometry the renderer uses — the single source of truth for the inline box position.
// For value-only edits the box starts past the "<Name> = " prefix; for whole-label edits (VAR/SDD,
// where the name is editable) it starts at the label's left edge.
private (double X, double Y) ComputeComponentLabelScreen()
{
    var a = _labelAnchor!;
    var (baseXw, baseYw, _, _) = SchematicComponent.LabelRowGeometry(
        a.CompX, a.CompY, a.Row, a.ODx, a.ODy, a.Symbol, a.PortCount);
    double offset = (Vm?.InlineEditIncludesName ?? false) ? 0 : a.PrefixWorldUnits;
    return SchematicCanvasCtrl.WorldToScreen(baseXw + offset, baseYw);
}
```

### 1d. Rewrite RepositionInlineEditBox (delete the 120 / +2 / 155 constants)
```csharp
private void RepositionInlineEditBox()
{
    if (_labelAnchor is null) return;

    double zoom     = SchematicCanvasCtrl.CurrentZoom;
    double fontSize = Math.Max(zoom * 70, 9.0);   // matches renderer; floor for legibility
    InlineEditBox.FontSize = fontSize;

    var (sx, sy) = ComputeComponentLabelScreen();   // sy = Skia baseline in screen px

    InlineEditBox.Width   = CalcInlineEditWidth(InlineEditBox.Text ?? "", fontSize);
    _inlineEditAnchorLeft = sx - TextBoxLeftPad;
    InlineEditBox.Margin  = new Thickness(
        _inlineEditAnchorLeft,
        sy - TextBoxTopPad - fontSize * _fontAscenderRatio,
        0, 0);
}
```

### 1e. Route the component-label initial show through the same path
Add a component-label show that positions from the anchor (not the raw hit coords), and a shared
focus+select helper:
```csharp
private void ShowInlineEditBoxForLabel()
{
    double zoom = SchematicCanvasCtrl.CurrentZoom;
    InlineEditBox.FontSize = Math.Max(zoom * 70, 9.0);
    InlineEditBox.Text     = Vm!.InlineEditValue;

    InlineEditBox.TextChanged -= OnInlineEditTextChanged;
    InlineEditBox.TextChanged += OnInlineEditTextChanged;

    InlineEditBox.IsVisible = true;
    RepositionInlineEditBox();        // position from the world anchor (single source)
    FocusAndSelectInlineEditBox();
}

private void FocusAndSelectInlineEditBox()
{
    Dispatcher.UIThread.Post(() =>
    {
        InlineEditBox.Focus();
        int selLen = Vm?.InlineEditSelLength ?? -1;
        if (selLen < 0) { InlineEditBox.SelectAll(); return; }   // -1 ⇒ select all
        var t     = InlineEditBox.Text ?? "";
        int start = Math.Clamp(Vm?.InlineEditSelStart ?? 0, 0, t.Length);
        int end   = Math.Clamp(start + selLen, start, t.Length);
        InlineEditBox.SelectionStart = start;
        InlineEditBox.SelectionEnd   = end;
    }, DispatcherPriority.Input);
}
```
In `OnTextLabelDoubleTapped`, replace the trailing `ShowInlineEditBox(e.ScreenX, e.ScreenY, Vm.InlineEditValue);`
with `ShowInlineEditBoxForLabel();`.

In the existing `ShowInlineEditBox(double, double, string)` (still used by the WIRE net-label path),
replace its inline `Dispatcher.Post { Focus(); SelectAll(); }` block with a call to
`FocusAndSelectInlineEditBox();` so wire labels honor the same selection contract (they'll select-all;
see VM below).

> The hand-rolled `cpy + zoom*120 + textSize + a.Row*(textSize+2)` is gone. The non-scaling `+2`
> per-row term was the "progressively lower when zoomed out" drift; `LabelBaseYFor` (N-aware) now also
> places SDD/ZNP param boxes correctly for free.

---

## FIX 2 — Editing a parameter selects only the VALUE, not the unit
So the user can type a new number without retyping the unit. The VM decides the selection range; the
view applies it (Fix 1e). Add to `SchematicViewModel` (inline-editing region):
```csharp
public bool InlineEditIncludesName { get; private set; }
public int  InlineEditSelStart     { get; private set; }
public int  InlineEditSelLength    { get; private set; } = -1;   // -1 = select all
```
Reset them in `CancelInlineEdit()`:
```csharp
InlineEditIncludesName = false;
InlineEditSelStart     = 0;
InlineEditSelLength    = -1;
```
In `BeginInlineEditForHit`, set selection per kind. For the non-name param case, value-only:
```csharp
case SchematicHitTest.HitKind.ComponentType:
    InlineEditIncludesName = false; InlineEditSelStart = 0; InlineEditSelLength = -1;
    SetInlineEdit(InlineEditKind.ComponentType, hit.Id,
        ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount), screenX, screenY);
    break;
case SchematicHitTest.HitKind.ComponentName:
    InlineEditIncludesName = false; InlineEditSelStart = 0; InlineEditSelLength = -1;
    SetInlineEdit(InlineEditKind.ComponentName, hit.Id, comp.InstanceName, screenX, screenY);
    break;
case SchematicHitTest.HitKind.ComponentParam:
{
    var param = comp.Parameters.ElementAtOrDefault(hit.SubIndex);
    if (param is null) return;
    _inlineEditParam = param;

    bool nameMode = comp.Symbol is SymbolKind.Var or SymbolKind.Sdd;   // FIX 4
    InlineEditIncludesName = nameMode;

    string init;
    if (nameMode)
    {
        init = FullParamLabel(param);
        InlineEditSelStart = 0; InlineEditSelLength = -1;             // select all
    }
    else
    {
        init = ParamInlineInitValue(param);
        InlineEditSelStart  = 0;
        InlineEditSelLength = string.IsNullOrEmpty(param.Unit) ? -1 : param.Expression.Length; // value only
    }
    SetInlineEdit(InlineEditKind.ComponentParam, hit.Id, init, screenX, screenY);
    break;
}
```
Add the helper next to `ParamInlineInitValue`:
```csharp
/// <summary>Full editable label "Name = Expr Unit" (used for VAR/SDD where the name is editable).</summary>
private static string FullParamLabel(EditableParameter p)
    => string.IsNullOrEmpty(p.Name) ? ParamInlineInitValue(p) : $"{p.Name} = {ParamInlineInitValue(p)}";
```
In `BeginWireNodeLabelEdit`, before `SetInlineEdit(...)`, add:
```csharp
InlineEditIncludesName = false; InlineEditSelStart = 0; InlineEditSelLength = -1;
```
In the context-menu `BeginInlineEdit(comp, param, …)`, add value-only selection:
```csharp
InlineEditIncludesName = false;
InlineEditSelStart  = 0;
InlineEditSelLength = string.IsNullOrEmpty(param.Unit) ? -1 : param.Expression.Length;
```

---

## FIX 3 — Unit remap: "1Ω" → "1 Ω" (units only; never variables)
On commit, split a trailing unit even when the user omitted the space. Add a param-aware overload of
`ParseExpressionUnit` and route the commit through it. Make these `internal` so Ui.Tests can cover
them (the project already exposes internals to tests; if not, add `[assembly: InternalsVisibleTo]`).

Add `using CircuitRF.Core.Expressions;` to SchematicViewModel.
```csharp
internal static (string Expression, string Unit) ParseExpressionUnit(string raw, EditableParameter p)
{
    raw = raw.Trim();
    // 1) Spaced unit (existing behavior): "2.5 nH" → ("2.5","nH").
    int lastSpace = raw.LastIndexOf(' ');
    if (lastSpace > 0)
    {
        string tail = raw[(lastSpace + 1)..];
        if (tail.Length > 0 && char.IsLetter(tail[0]))
            return (raw[..lastSpace].Trim(), tail);
    }
    // 2) No-space trailing unit remap: "1Ω" → ("1","Ω"), "2.5nH" → ("2.5","nH").
    if (TrySplitTrailingUnit(raw, p, out var expr2, out var unit2))
        return (expr2, unit2);
    return (raw, "");
}

private static bool TrySplitTrailingUnit(string raw, EditableParameter p,
                                         out string expr, out string unit)
{
    expr = raw; unit = "";
    int i = raw.Length;
    while (i > 0 && IsUnitGlyph(raw[i - 1])) i--;      // trailing run of unit chars
    if (i == raw.Length || i == 0) return false;        // no run, or no numeric part
    char before = raw[i - 1];
    if (!(char.IsDigit(before) || before == ')' || before == '.')) return false;
    string run = raw[i..];

    bool matchesParam = !string.IsNullOrEmpty(p.Unit)
        && string.Equals(run, p.Unit, StringComparison.OrdinalIgnoreCase);
    bool recognized = Units.IsRecognizedUnit(UnitNormalizer.ToEngineUnit(run))
        && !(run.Length == 1 && IsBareSiPrefix(run[0]));   // don't treat "100n" as 100 + prefix "n"
    if (!matchesParam && !recognized) return false;

    expr = raw[..i].Trim();
    unit = matchesParam ? p.Unit : run;                    // canonical casing from the param
    return true;
}

private static bool IsUnitGlyph(char c)
    => char.IsLetter(c) || c is 'Ω' or 'µ' or 'μ' or '%' or '°';
private static bool IsBareSiPrefix(char c)
    => "TGMkmunpf".IndexOf(c) >= 0;
```
(Keep the existing single-arg `ParseExpressionUnit(string)` for any other callers; the commit path
below uses the new overload.) After commit, the rendered label is `"{Expr} {Unit}"` = "1 Ω"
automatically — no extra display logic needed.

---

## FIX 4 — VAR (and SDD) inline edit can change the parameter NAME; VAR selects the whole string
Covered above by `nameMode` (value string includes the name, select-all, box anchored at the label's
left edge via `InlineEditIncludesName`). Handle the commit + reset, and extend the command.

Capture the flag at the top of `CommitInlineEdit` (next to the other captured locals, BEFORE
`CancelInlineEdit()`):
```csharp
var includesName = InlineEditIncludesName;
```
Replace the `ComponentParam` commit branch with:
```csharp
case InlineEditKind.ComponentParam:
{
    if (param is null) break;
    if (newVal.Length == 0) break;

    string newName = param.Name;
    string rest    = newVal;
    if (includesName)
    {
        int eq = newVal.IndexOf('=');
        if (eq >= 0)
        {
            newName = newVal[..eq].Trim();
            rest    = newVal[(eq + 1)..].Trim();
        }
        // No '=' typed ⇒ name unchanged, treat the whole text as the value.
    }

    var (expr, unit) = ParseExpressionUnit(rest, param);
    if (newName != param.Name || expr != param.Expression || unit != param.Unit)
        Execute(new EditParameterCommand(EditModel, param, expr, unit, newName));
    break;
}
```
Extend `EditParameterCommand` to optionally change the name (snapshot old name for undo):
```csharp
private readonly string _newName, _oldName;

public EditParameterCommand(SchematicEditModel model, EditableParameter param,
    string newExpression, string newUnit = "", string? newName = null)
{
    _model         = model;
    _param         = param;
    _oldExpression = param.Expression;
    _oldUnit       = param.Unit;
    _oldName       = param.Name;
    _newExpression = newExpression;
    _newUnit       = newUnit;
    _newName       = newName ?? param.Name;
}

public void Execute() { _param.Name = _newName; _param.Expression = _newExpression; _param.Unit = _newUnit; _model.NotifyChanged(); }
public void Undo()    { _param.Name = _oldName; _param.Expression = _oldExpression; _param.Unit = _oldUnit; _model.NotifyChanged(); }
```
(`Description` can stay `$"Edit {_param.Name}"`.)

> SDD param names like `Z[1,1]` are now editable inline; the parse keeps it simple (no name
> validation), matching the request. VAR variable rows get full name+value editing with select-all.

---

## Tests (Ui.Tests — pure VM/parse logic, headless; positioning is verified manually)
1. **ParseUnit_NoSpaceOhm_Remaps**: param Unit="Ω" → `ParseExpressionUnit("1Ω", p)` == ("1","Ω").
2. **ParseUnit_NoSpaceNH_Remaps**: param Unit="nH" → `("2.5nH", p)` == ("2.5","nH").
3. **ParseUnit_Spaced_Unchanged**: `("1 Ω", p)` == ("1","Ω").
4. **ParseUnit_BarePrefix_NotSplit**: param Unit="nH" → `("100n", p)` == ("100n","") (no bogus "n" unit).
5. **ParseUnit_PlainNumber_NoUnit**: `("50", p)` == ("50","").
6. **VarNameEdit_Commit_RenamesAndSetsValue**: VAR param {Name="a",Expr="1"}; drive the includesName
   commit path with "freq = 2.4 GHz" → param.Name=="freq", Expr=="2.4", Unit=="GHz"; one undo restores
   all three.
7. **ParamSelection_ValueOnly_WhenUnitPresent**: after `BeginInlineEditForHit` on a unit-bearing param,
   `InlineEditSelStart==0 && InlineEditSelLength==param.Expression.Length`.
8. **ParamSelection_SelectAll_WhenNoUnit / VarNameMode**: unit-less param and VAR/SDD param →
   `InlineEditSelLength==-1`.

Manual: place a Resistor; double-click its value at zoom 1, then zoom way out and zoom way in — the
box stays exactly on the rendered text at every zoom (no downward drift). Edit value "47" without
retyping "Ω"; type "1Ω" → commits/renders "1 Ω". Double-click a VAR variable → whole "name = value"
selected and editable. Place an SDD8 → param box sits at the correct (N-aware) Y, name editable.

## Gate
Build 0W/0E (TreatWarningsAsErrors). Tests green.

## On completion
Note in `src/Ui/CLAUDE.md`: the inline edit box derives its position solely from
`SchematicComponent.LabelRowGeometry` → `WorldToScreen` (same source as the renderer and hit-test), so
tweaking label placement is a one-line change in `SchematicComponent`. VAR/SDD parameters are
name-editable inline; other params select value-only and remap a spaceless trailing unit on commit.
