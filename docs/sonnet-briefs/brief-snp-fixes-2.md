# Sonnet Brief — SnP fixes round 2 (geometry, grid, file picker, live re-sniff, Ref label, Show button)

Eight issues from testing the SnP component. Grouped: symbol geometry (items A–E), property dialog
(F, G), live update (H). Build 0W/0E (TreatWarningsAsErrors) after each part.

Files:
- `src/Ui/Schematic/EditableSchematic.cs` — `SymbolPortDefs.GenerateSnpPorts`, `SnpBodyRect`,
  `SnpBodyHalfH` (the geometry rewrite — A, B, C, E).
- `src/Ui/Schematic/BuiltInSymbols.cs` — `BuildSnpSymbol` (body centering, leads, Ref label — D, E).
- `src/Ui/Views/ParameterEditor/ParameterEditorView.axaml.cs` — file-picker filter (F).
- `src/Ui/Views/ParameterEditor/ParameterEditorView.axaml` + `ParameterEditorViewModel.cs` — Show
  button (G).
- The inline-edit / parameter-edit path for the SnP `File` param — live re-sniff (H). See Part H for
  where.

Quick context (already on disk, confirmed):
- `GenerateSnpPorts(int n, bool refNode, SnpPinConfig cfg, SnpPitch pitch)` returns the pin tuples;
  pins[0..N−1] = ports 1..N, pins[N] = "Ref" when refNode.
- `BuildSnpSymbol` draws `RRect(0,0,w,halfH*2,12)` + per-pin lead lines + port-number text.
- `SnpBodyRect`/`SnpBodyHalfH` size the body; `LabelBaseYFor`/`LabelRowGeometry` already call
  `SnpBodyRect`, and the hitbox calls `comp.ComputeGlyphBb()` (SnP-aware). So label + hitbox already
  *read* the body size — they're wrong only because the body math is wrong (items B, C).

---

## The core geometry rewrite (items A, B, C, E) — grid-correctness first

**Root cause of the off-grid pins (item E) and the too-low / wrong hitbox+label (item B):** the current
math isn't grid-safe and isn't symmetric.
- Tight pitch (100) with an even pin count puts side pins at odd multiples of 50 → **off grid**.
- The Ref pin uses `SnpBodyHalfH(n)+100`, and `SnpBodyHalfH = (nLeft−1)*0.5*pitch + 60` → **off grid**
  (the `+60` and the `*0.5*pitch` term are not multiples of 100).
- The Ref pin extends the symbol downward only, so the glyph is vertically asymmetric → its visual
  center sits below the origin → the label (anchored at origin + baseY) and the glyph hitbox both look
  "too low".

**Fix principle:** every pin tip lands on a multiple of 100 (local), and the body is centered on the
actual pin span (not forced to origin), so the glyph is balanced and the label/hitbox track it.

### Grid helper
Add a tiny local helper used throughout:
```csharp
// Snap to the connection grid (100 local units = 1 square). All pin tips must be grid-aligned.
static float SnapG(float v) => (float)(Math.Round(v / 100.0) * 100.0);
static float CeilG(float v) => (float)(Math.Ceiling(v / 100.0) * 100.0);
```

### `GenerateSnpPorts` — rewrite
Replace the body of `GenerateSnpPorts` with this layout. `bodyX = 200` (left/right pin tips at ∓200).
```csharp
public static (string Name, float LocalX, float LocalY)[] GenerateSnpPorts(
    int n, bool refNode, SnpPinConfig cfg, SnpPitch pitch)
{
    int total = refNode ? n + 1 : n;
    var pins = new (string Name, float LocalX, float LocalY)[total];
    const float bodyX = 200f;
    float p = pitch == SnpPitch.Tight ? 100f : 200f;

    switch (n)
    {
        case 1:
            pins[0] = ("1", -bodyX, 0f);
            break;
        case 2:
            pins[0] = ("1", -bodyX, 0f);
            pins[1] = ("2", +bodyX, 0f);
            break;
        case 3:   // SPECIAL CASE (item A): 1 left-mid, 2 right-mid, 3 top-mid
            pins[0] = ("1", -bodyX,  0f);
            pins[1] = ("2", +bodyX,  0f);
            pins[2] = ("3",  0f,   -200f);   // top-middle, on grid
            break;
        default:  // n >= 4
        {
            (int[] left, int[] right) = cfg switch
            {
                SnpPinConfig.SplitLR => (
                    Enumerable.Range(0, (n + 1) / 2).ToArray(),
                    Enumerable.Range((n + 1) / 2, n / 2).ToArray()),
                SnpPinConfig.DualRow => (
                    Enumerable.Range(0, n).Where(i => i % 2 == 0).ToArray(),
                    Enumerable.Range(0, n).Where(i => i % 2 == 1).ToArray()),
                _ => ( // Standard: sequential wrap (1=TL,2=BL,3=BR,4=TR for n=4)
                    Enumerable.Range(0, (n + 1) / 2).ToArray(),
                    Enumerable.Range((n + 1) / 2, n / 2).Reverse().ToArray()),
            };
            PlaceSide(pins, left,  -bodyX, p);
            PlaceSide(pins, right, +bodyX, p);
            break;
        }
    }

    if (refNode)
    {
        if (n == 1)        pins[1] = ("Ref", +bodyX, 0f);              // right-mid (spec)
        else               pins[n] = ("Ref", 0f, RefPinY(n, cfg, p)); // bottom, grid-aligned
    }
    return pins;

    // Place `count` pins on one side, centered, every tip grid-snapped (so tight/even stays on grid
    // and distinct: snapping the centered top to the grid then stepping by p keeps 100-spacing).
    static void PlaceSide((string Name, float LocalX, float LocalY)[] pins,
        int[] portIdx, float x, float p)
    {
        int count = portIdx.Length;
        float top = SnapG(-(count - 1) * 0.5f * p);   // grid-snapped top; for loose this is exact
        for (int i = 0; i < count; i++)
            pins[portIdx[i]] = ($"{portIdx[i] + 1}", x, top + i * p);
    }
}
```

> Note: with `p=100` (tight) and an even count, `-(count-1)*0.5*100` is an odd multiple of 50; `SnapG`
> rounds it to the nearest 100, and stepping by 100 keeps every pin on grid and 100 apart (distinct).
> The stack is then off-center by 50 from the origin — that's fine, because the BODY is centered on the
> pins (below), not on the origin.

### `RefPinY`, `SnpBodyHalfH`, `SnpBodyRect` — grid-aligned, centered on pins
```csharp
// Half-height of the body, measured from the body CENTER (which is the pin-span midpoint).
// Grid-aligned so the body edges land on grid and the Ref pin can sit one square below.
private static float SnpBodyHalfH(int n, SnpPinConfig cfg, float p)
{
    if (n <= 2) return 100f;       // 1/2-port: 200-tall square
    if (n == 3) return 100f;       // 3-port special case: 200-tall square (port 3 lead goes to -200)
    int nLeft = (n + 1) / 2;
    // tallest side spans (count-1)*p; half that, rounded UP to a grid square, + nothing (leads are
    // separate). Body must enclose the pin tips' y-range on the taller side.
    float halfSpan = (nLeft - 1) * 0.5f * p;
    return Math.Max(100f, CeilG(halfSpan));
}

// Body center Y = midpoint of all signal-pin Ys, grid-snapped (keeps the glyph visually balanced
// even when tight/even shifts a side off-origin by 50).
private static float SnpBodyCenterY(int n, bool refNode, SnpPinConfig cfg, SnpPitch pitch)
{
    var pins = GenerateSnpPorts(n, refNode: false, cfg, pitch);   // signal pins only
    if (pins.Length == 0) return 0f;
    float minY = pins.Min(q => q.LocalY), maxY = pins.Max(q => q.LocalY);
    return SnapG((minY + maxY) * 0.5f);
}

// Ref pin Y: one grid square below the body's bottom edge (user's request: nearest grid point below
// the box). Body bottom edge = center + halfH; add 100 for the lead.
private static float RefPinY(int n, SnpPinConfig cfg, float p)
{
    float halfH = SnpBodyHalfH(n, cfg, p);
    // body is centered on pin midpoint; for n>=4 that midpoint may be ±50 off origin, but halfH is
    // grid-aligned and the body bottom is centerY+halfH. RefPinY computed against centerY in BodyRect.
    return 0f; // placeholder — actual value computed in builder where centerY is known (see below)
}

public static (float W, float HalfH) SnpBodyRect(int n, SnpPinConfig cfg, SnpPitch pitch)
{
    float p = pitch == SnpPitch.Tight ? 100f : 200f;
    return (200f, SnpBodyHalfH(n, cfg, p));
}
```
> `RefPinY` needs the body center, which the builder computes. Simplest: compute the Ref Y **inside
> `GenerateSnpPorts`** using `SnpBodyCenterY` + `SnpBodyHalfH`:
> ```csharp
> if (refNode) {
>     if (n == 1) pins[1] = ("Ref", +bodyX, 0f);
>     else {
>         float cy    = SnpBodyCenterY(n, refNode, cfg, pitch);
>         float halfH = SnpBodyHalfH(n, cfg, p);
>         pins[n] = ("Ref", 0f, CeilG(cy + halfH) + 100f);   // one square below the body bottom, on grid
>     }
> }
> ```
> Delete the standalone `RefPinY` stub; inline it as above. Keep `SnpBodyCenterY`/`SnpBodyHalfH`.

### Body drawing must use the computed center (item B/E) — `BuildSnpSymbol`
The body is currently `RRect(0, 0, w, halfH*2, 12)` (centered at origin). Center it on the pin span and
draw leads to the grid-aligned tips:
```csharp
private static Symbol BuildSnpSymbol(int n, bool refNode, SnpPinConfig cfg, SnpPitch pitch)
{
    var ports = SymbolPortDefs.GenerateSnpPorts(n, refNode, cfg, pitch);
    var (w, halfH) = SymbolPortDefs.SnpBodyRect(n, cfg, pitch);
    float cy = SymbolPortDefs.SnpBodyCenterYPublic(n, cfg, pitch);   // expose via a public shim (below)

    double bodyTop = cy - halfH, bodyBot = cy + halfH;
    double bodyLeft = -w * 0.5, bodyRight = +w * 0.5;

    var prims = new List<SymbolPrimitive> { RRect(0, cy, w, halfH * 2, 12) };

    foreach (var (name, lx, ly) in ports)
    {
        // Lead from the nearest body edge to the pin tip.
        if (lx < 0)            prims.Add(L(bodyLeft,  ly, lx, ly));   // left
        else if (lx > 0)       prims.Add(L(bodyRight, ly, lx, ly));   // right
        else if (ly < bodyTop) prims.Add(L(0, bodyTop, 0, ly));       // top pin (e.g. 3-port port 3)
        else                   prims.Add(L(0, bodyBot, 0, ly));       // bottom pin / Ref

        // Label inside the body. Ref gets a "Ref" label (item D); signal pins get the port number.
        if (lx < 0 || (lx == 0 && n == 1))
            prims.Add(Txt(name, bodyLeft + 20, ClampInsideBody(ly, bodyTop, bodyBot),
                SddPortLabelFontSize, SymbolTextAlign.Left, SymbolTextVAlign.Middle));
        else if (lx > 0)
            prims.Add(Txt(name, bodyRight - 20, ClampInsideBody(ly, bodyTop, bodyBot),
                SddPortLabelFontSize, SymbolTextAlign.Right, SymbolTextVAlign.Middle));
        else if (name == "Ref")
            prims.Add(Txt("Ref", 0, bodyBot - 22, SddPortLabelFontSize,
                SymbolTextAlign.Center, SymbolTextVAlign.Middle));   // just inside bottom edge
        else // top pin (3-port port 3): label just inside the top edge, centered
            prims.Add(Txt(name, 0, bodyTop + 22, SddPortLabelFontSize,
                SymbolTextAlign.Center, SymbolTextVAlign.Middle));
    }

    var pins = ports.Select((d, i) => new SymbolPin(d.LocalX, d.LocalY, i, d.Name)).ToList();
    return new Symbol(prims, pins);

    static double ClampInsideBody(double y, double top, double bot)
        => Math.Min(bot - 22, Math.Max(top + 22, y));
}
```
> Add a public shim so the builder can read the center: in `SymbolPortDefs`, make
> `SnpBodyCenterY` accessible as `public static float SnpBodyCenterYPublic(int n, SnpPinConfig cfg, SnpPitch pitch) => SnpBodyCenterY(n, false, cfg, pitch);`
> (or just make `SnpBodyCenterY` public and call it directly — match the file's existing access style).

**Result:** body centered on pins (balanced glyph), all tips on grid, Ref pin one grid square below the
box. `SnpBodyRect` now returns a grid-aligned halfH, so `LabelBaseYFor` (which does
`max(LabelBaseY, halfH + step)`) places the label correctly relative to the now-correct body, and the
hitbox (`ComputeGlyphBb`) bounds the centered glyph — fixing items B and C with no change to the
label/hitbox plumbing.

### Item C explicitly (label / hitbox / inline-editor adapt to ports)
After the above, verify:
- `SchematicComponent.LabelBaseYFor(Snp, n)` must use the component's ACTUAL cfg/pitch, not hardcoded
  Standard/Loose. Currently it calls `SnpBodyRect(portCount, SnpPinConfig.Standard, SnpPitch.Loose)`.
  Since `SnpBodyHalfH` depends on pitch (200 vs 100) and cfg only via nLeft (cfg-independent), the
  hardcoded Loose **over-estimates** for Tight (label sits a bit low) but never overlaps. Acceptable,
  BUT prefer correctness: overload `LabelBaseYFor` is static and has no component — leave the
  Standard/Loose call but switch pitch to the safe MAX (Loose) so the label always clears the body
  regardless of actual pitch. Document that choice in a comment. (Hitbox uses real params already via
  `ComputeGlyphBb`, so it's exact.)
- The inline text editor for a label uses `LabelRowGeometry` (→ `LabelBaseYFor`) for its world Y, so it
  tracks the label. Confirm by inline-editing the instance name on a 6-port SnP: the editor box sits on
  the label row, not floating low.

---

## PART D — "Ref" label inside the body (done above)
Covered in `BuildSnpSymbol`: when `name == "Ref"`, draw `Txt("Ref", 0, bodyBot−22, …)` centered just
inside the bottom edge (same treatment as the port-number labels). Only drawn when the reference pin
exists (refNode on). **Test:** a 2-port SnP with RefNode on shows "Ref" inside the box near the bottom
pin; with RefNode off there's no "Ref" text.

---

## PART F — File picker can't select .s5p / .snp etc. on macOS

**Cause:** the `FilePickerFileType` lists only `*.s1p`–`*.s4p` + `*.snp`. macOS maps these patterns to
concrete extensions and hides everything else (`.s5p`, `.s10p`, `.s6p`, …); the `*.s*p` wildcard is not
honored by the macOS picker. So higher-port files are unselectable.

**Fix:** add an explicit "All files" filter so any extension can be chosen on every OS, and broaden the
Touchstone filter. In `PickSnpFileAsync` (`ParameterEditorView.axaml.cs`):
```csharp
var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
{
    Title         = "Open Touchstone File",
    AllowMultiple = false,
    FileTypeFilter =
    [
        new FilePickerFileType("Touchstone (*.sNp, *.snp)")
        {
            // Common explicit extensions for the nice default filter…
            Patterns = ["*.s1p","*.s2p","*.s3p","*.s4p","*.s5p","*.s6p","*.s7p","*.s8p",
                        "*.s9p","*.s10p","*.s11p","*.s12p","*.snp",
                        "*.S1P","*.S2P","*.S3P","*.S4P","*.S5P","*.S6P","*.S7P","*.S8P",
                        "*.S9P","*.S10P","*.S11P","*.S12P","*.SNP"],
        },
        // …plus an All-files escape hatch so ANY .sNp (or oddly-named) file is selectable on macOS/Linux/Windows.
        FilePickerFileTypes.All,
    ],
});
```
> `FilePickerFileTypes.All` is Avalonia's built-in "*.*" type (cross-platform). Listing it as a second
> filter lets the user switch to "All files" in the picker and choose any extension — the real
> port-count is sniffed from the file contents by `TryGetPortCount` regardless of extension, so this is
> safe. Keep the explicit list first so Touchstone files are the default view.

**Test (manual, macOS):** Browse → the picker defaults to Touchstone but offers "All files"; selecting
a `.s5p` or a `.snp` file works and the port count resolves from the data.

---

## PART G — "Show" button (reveal file in the OS file manager)

Add a "Show" button to the right of "Browse" in the SnP panel. It reveals the current `File` in Finder
(macOS) / Explorer (Windows) / the default file manager (Linux).

### G1. ViewModel (`ParameterEditorViewModel.cs`)
Add a command + a callback the view sets (the VM stays Avalonia-free; the view does the OS call):
```csharp
public Func<string, Task>? RevealFileAsync { get; set; }   // set by the view
public IAsyncRelayCommand ShowSnpFileCommand { get; private set; } = null!;
// in ctor:
ShowSnpFileCommand = new AsyncRelayCommand(RevealSnpFileAsync, () => !string.IsNullOrWhiteSpace(SnpFilePath));
// keep CanExecute fresh:
partial void OnSnpFilePathChanged(string value) => ShowSnpFileCommand.NotifyCanExecuteChanged();

private async Task RevealSnpFileAsync()
{
    if (RevealFileAsync is null || string.IsNullOrWhiteSpace(SnpFilePath)) return;
    // Resolve relative paths against the schematic directory before revealing.
    string path = SnpFilePath;
    if (!System.IO.Path.IsPathRooted(path) && _schematicVm?.EditModel.SchematicDirectory is { } dir)
        path = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, path));
    await RevealFileAsync(path);
}
```

### G2. View code-behind (`ParameterEditorView.axaml.cs`)
Wire the callback (next to the `PickSnpFileAsync` wiring) and implement the per-OS reveal:
```csharp
if (DataContext is ParameterEditorViewModel vm)
{
    vm.PickSnpFileAsync = PickSnpFileAsync;
    vm.RevealFileAsync  = RevealFileAsync;
}
...
private Task RevealFileAsync(string path)
{
    try
    {
        if (OperatingSystem.IsMacOS())
            System.Diagnostics.Process.Start("open", ["-R", path]);          // -R reveals & selects in Finder
        else if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start("explorer.exe", [$"/select,\"{path}\""]);
        else // Linux: open the containing folder (most file managers have no reliable select flag)
        {
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.Diagnostics.Process.Start("xdg-open", [dir]);
        }
    }
    catch (Exception ex)
    {
        _ = ex; // swallow — a missing file/manager shouldn't crash; optional: post to message pane.
    }
    return Task.CompletedTask;
}
```
> `Process.Start(string, IEnumerable<string>)` uses arg arrays (no shell-escaping pitfalls). Don't use
> `UseShellExecute` paths. The Core/engine firewall is unaffected (this is all in `src/Ui`).

### G3. View XAML — add the button
In the SnP panel, to the right of "Browse", add:
```xml
<Button Content="Show"
        Command="{Binding ShowSnpFileCommand}"
        ToolTip.Tip="Reveal this file in the system file manager"
        Margin="4,0,0,0"/>
```
(Match the existing Browse button's style/placement — likely both in a horizontal `StackPanel` or a
`Grid` row next to the File `TextBox`.)

**Test (manual):** with a valid File set, "Show" opens Finder/Explorer with the file selected; with an
empty File the button is disabled.

---

## PART H — Inline file-name edit doesn't update the symbol's port count

**Cause:** editing the SnP `File` via the inline schematic editor (or typing into the File field) routes
through `EditParameterCommand`, which sets the expression and notifies — it never re-sniffs the file, so
`NumPorts` (and thus the symbol's port count) is stale. Only the dialog's Browse path
(`PickFileAsync`) sniffs.

**Fix:** when an edit changes the SnP `File` parameter, re-sniff the port count and update `NumPorts` in
the SAME undoable step. Two edit routes hit this; make BOTH go through one SnP-aware path.

### H1. A small SnP-aware helper
Add a command (or extend `EditParameterCommand`) that, after setting `File`, sniffs and sets `NumPorts`.
Cleanest is a dedicated command so undo restores both fields atomically:
```csharp
internal sealed class SetSnpFileCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent  _comp;
    private readonly EditableParameter  _fileParam;
    private readonly EditableParameter? _numPortsParam;
    private readonly string _newFile, _oldFile;
    private readonly string _newNumPorts, _oldNumPorts;

    public string Description => "Set SnP file";

    public SetSnpFileCommand(SchematicEditModel model, EditableComponent comp, string newFile)
    {
        _model = model; _comp = comp;
        _fileParam     = comp.Parameters.First(p => p.Name == "File");
        _numPortsParam = comp.Parameters.FirstOrDefault(p => p.Name == "NumPorts");
        _oldFile = _fileParam.Expression; _newFile = newFile;
        _oldNumPorts = _numPortsParam?.Expression ?? "";
        // Sniff: resolve relative to schematic dir, then TryGetPortCount.
        string probe = newFile;
        if (!System.IO.Path.IsPathRooted(probe) && model.SchematicDirectory is { } dir)
            probe = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, probe));
        _newNumPorts = RfCore.TouchstoneIO.TryGetPortCount(probe, out int n, out _)
            ? n.ToString() : _oldNumPorts;   // keep old count if the new file can't be read
    }

    public void Execute() { _fileParam.Expression = _newFile;
        if (_numPortsParam is not null) _numPortsParam.Expression = _newNumPorts; _model.NotifyChanged(); }
    public void Undo()    { _fileParam.Expression = _oldFile;
        if (_numPortsParam is not null) _numPortsParam.Expression = _oldNumPorts; _model.NotifyChanged(); }
}
```

### H2. Route both edit paths through it
- **Inline editor commit** for a component parameter: find where an inline `ComponentParam` edit
  commits (it builds an `EditParameterCommand`). When the target component is `SymbolKind.Snp` AND the
  edited parameter is `File`, execute `SetSnpFileCommand` instead. (Search `SchematicViewModel` for the
  inline `ComponentParam` commit — it constructs `EditParameterCommand`; add the SnP/File branch there.)
- **Dialog File field**: the `PickFileAsync` path already sniffs; for consistency and to cover the case
  where the user *types* a path into the File `TextBox` (if that field is editable), route its commit
  through `SetSnpFileCommand` too. (The current Browse path can also just call `SetSnpFileCommand` to
  remove the duplicate sniff logic in `PickFileAsync` — optional cleanup.)

> Net effect: any change to an SnP's File — inline, typed, or Browsed — re-sniffs and updates NumPorts,
> so the dynamic symbol redraws at the new port count immediately and undo restores both fields together.

**Test:** place a 2-port SnP; inline-edit its File label to a `.s4p` path → the symbol redraws as a
4-port (no need to reopen the dialog); undo restores the 2-port file AND the 2-port symbol in one step.

---

## Gate
Build 0W/0E. Tests + manual checks:
- Pin tips on grid for every (n ∈ {1,2,3,4,5,6,8}, pitch ∈ {Tight,Loose}, cfg ∈ {Standard,SplitLR,DualRow},
  refNode ∈ {off,on}). Assert each tip's local X and Y are multiples of 100.
- Ref pin: n=1 → (+200,0); n≥2 → (0, one grid square below the body bottom), on grid.
- 3-port special case: pins at (−200,0),(+200,0),(0,−200); ref at bottom on grid.
- Body centered on the pin span; glyph not visually shifted; label sits just below the body and adapts
  to port count; hitbox covers the whole glyph (click a 6-port SnP body anywhere to select).
- "Ref" text appears inside the body when refNode is on.
- macOS file picker can select `.s5p`/`.snp` via the All-files filter; port count resolves from data.
- "Show" reveals the file; disabled when File is empty.
- Inline-editing File to a different-port file updates the symbol; undo restores both file and symbol.

**Add a focused unit test** `SnpPinsAreGridAligned` enumerating the matrix above — the off-grid Ref pin
was shipped because nothing asserted grid alignment.

**STOP-and-report:** the exact location of the inline `ComponentParam` commit in `SchematicViewModel`
(method name + how it currently builds `EditParameterCommand`) so the SnP/File branch lands in the right
place; and whether the dialog's File `TextBox` is user-editable (decides whether H2's typed-path route
is needed).
