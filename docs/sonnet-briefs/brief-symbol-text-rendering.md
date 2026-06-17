# Sonnet Brief — Text rendering for symbol primitives (color role + font size)

Adds a `ColorRole` to `TextPrimitive`, makes the standard-library `+` polarity text render in the
`SymbolPlus` color (the `−` stays regular), fixes a static-init-order bug that made the
`polarityFontSize` edit produce invisible text, makes the SDD/ZPort `+` port labels use `SymbolPlus`,
and exposes the text color role + (existing) font size in the Symbol Editor Properties Inspector so
users can set them on custom-symbol text. Build 0W/0E (TreatWarningsAsErrors) after each part.

Files:
- `src/Ui/Schematic/SymbolModel.cs` — add `ColorRole` to `TextPrimitive` (Part 1).
- `src/Ui/Schematic/BuiltInSymbols.cs` — fix font-size init bug; `Txt(...)` gains a role param;
  `+` text → SymbolPlus; SDD/ZPort `+` labels → SymbolPlus; font-size constants (Parts 2–4).
- `src/Ui/Renderers/SchematicRenderer.cs` — `DrawSymbol` text color resolves from the role (Part 1).
- `src/Ui/ViewModels/SymbolPrimitiveInspectorViewModel.cs` — text ColorRole binding (Part 5).
- `src/Ui/Views/Properties/SymbolPrimitiveInspectorView.axaml` — ColorRole ComboBox in the text panel (Part 5).

Key facts (already confirmed in the code):
- Every vector primitive (`Line`, `Circle`, `Polygon`, …) has a `ColorRole` property, but
  **`TextPrimitive` does NOT** — it's the only primitive without one.
- `SchematicRenderer.DrawSymbol` renders text with
  `SKColor textColor = overridePaint?.Color ?? theme.SymbolLine;` — it **always** uses `SymbolLine`,
  ignoring any role. So even if a text role existed today it wouldn't be honored.
- `SymbolColorRole` enum (in `SymbolModel.cs`) = `{ SymbolLine, SymbolText, SymbolPlus }`. The theme
  exposes `theme.SymbolLine` and `theme.SymbolPlus` (used by the plus-segment path already).
- `TextPrimitive.FontSize` **is** plumbed: `DrawSymbol` computes
  `float fontSize = Math.Max(1f, (float)(txt.FontSize * zoom));`. Font size is NOT ignored.

---

## PART 1 — Add `ColorRole` to `TextPrimitive` and honor it in the renderer

### 1a. Model (`SymbolModel.cs`)
`TextPrimitive` has no `ColorRole`. Add one, defaulting to `SymbolLine` so existing `.csym` files and
all current symbols render exactly as today (text is currently drawn in `SymbolLine` color):
```csharp
public sealed class TextPrimitive : SymbolPrimitive
{
    public string Content   { get; set; } = "";
    // ... existing properties ...

    /// <summary>Color role for this text. Default SymbolLine preserves legacy rendering
    /// (text historically drew in the SymbolLine color). Set SymbolPlus for "+" polarity marks;
    /// SymbolText for regular label text that should track the dedicated text color.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolColorRole ColorRole { get; set; } = SymbolColorRole.SymbolLine;
}
```
(Default = `SymbolLine` is the no-visual-change default; old files deserialize to it.)

### 1b. Renderer (`SchematicRenderer.DrawSymbol`)
In the `if (prim is TextPrimitive txt)` block, the line that picks the color is currently:
```csharp
SKColor textColor = overridePaint?.Color ?? theme.SymbolLine;
```
Make it role-aware (still letting `overridePaint` — ghost/selection — win):
```csharp
SKColor textColor = overridePaint?.Color ?? txt.ColorRole switch
{
    SymbolColorRole.SymbolPlus => theme.SymbolPlus,
    SymbolColorRole.SymbolText => theme.SymbolText,
    _                          => theme.SymbolLine,
};
```
> Confirm `theme.SymbolText` exists on `SchematicRenderTheme`. If only `SymbolLine`/`SymbolPlus` exist,
> map `SymbolText → theme.SymbolLine` (or add a `SymbolText` theme color — check the theme type and
> report which you did). The important pair for this brief is SymbolPlus vs not-SymbolPlus.

**Part 1 test:** a `TextPrimitive { ColorRole = SymbolPlus }` renders in the SymbolPlus color; default
(`SymbolLine`) renders unchanged. (Unit-test the color-resolution switch if it's extractable, else
verify via the SDD/Vdc visual in Parts 3–4.)

---

## PART 2 — Fix the invisible-text font-size bug (`BuiltInSymbols.cs`)

**Symptom (user):** setting `fontSize: polarityFontSize` on the `+/−` `Txt(...)` calls makes the text
disappear.

**Root cause (confirmed — static initialization order):** `polarityFontSize` is a
`private static readonly double` declared **after** the symbol fields (`_resistor`, `_vdcSrc`, …).
C# initializes static fields in textual order. `_vdcSrc = BuildVdc()` (and `_toneSrc`, `_term`,
`_p1Tone`) run their builders during field init — calling `Txt("+", …, fontSize: polarityFontSize)`
**before** `polarityFontSize` is assigned, so it reads the default `0.0`. The renderer then does
`Math.Max(1f, 0f*zoom) = 1f` → a ~1px glyph = effectively invisible. (The literal `36.0` inline
worked because there was no ordering dependency.)

**Fix:** make the polarity font size a `const` (compile-time constant — no init-order hazard) and put
it at the top of the class, above the symbol fields. While here, add named consts for the other text
sizes so font size is easy to change in one place (Part 4):
```csharp
public static class BuiltInSymbols
{
    // ── Font sizes (compile-time consts — safe to reference from field initializers) ──
    /// <summary>Font size for the +/− polarity marks on 2-terminal sources/terminations.</summary>
    public const double PolarityFontSize = 36.0;
    /// <summary>Font size for SDD/ZPort port-number labels ("1+", "2−", …).</summary>
    public const double SddPortLabelFontSize = 10.0;
    /// <summary>Font size for the "VAR" body label.</summary>
    public const double VarLabelFontSize = 48.0;

    private static readonly Symbol _resistor = BuildResistor();
    // ... rest unchanged ...
```
Then DELETE the old `private static readonly double polarityFontSize = 36.0;` line (now replaced by
the const above), and update every `Txt(..., fontSize: polarityFontSize)` call to
`fontSize: PolarityFontSize`.

> Why const (not just moving the field up): a `const double` is inlined at compile time and has no
> initialization-order semantics at all, so it cannot regress even if the field order changes later.
> A moved `static readonly` would also work but is fragile to future reordering.

**Part 2 test:** assert `BuildVdc().Primitives.OfType<TextPrimitive>()` all have `FontSize == 36.0`
(not 0). Add an init-order regression note in a comment. Visually: the `+/−` marks render at the
intended size, not invisibly.

---

## PART 3 — Standard-library `+` text → SymbolPlus; `−` stays regular

**Requirement:** any component symbol with a `Txt("+", …)` should render the `+` in the SymbolPlus
color. The `−` should NOT be SymbolPlus (regular).

The `Txt(...)` helper currently can't carry a role. Extend it:
```csharp
private static TextPrimitive Txt(string content, double ax, double ay,
                                  double fontSize = 12,
                                  SymbolTextAlign align = SymbolTextAlign.Center,
                                  SymbolTextVAlign vAlign = SymbolTextVAlign.Middle,
                                  SymbolColorRole colorRole = SymbolColorRole.SymbolLine)
    => new() { Content = content, AnchorX = ax, AnchorY = ay,
               FontSize = fontSize, Align = align, VAlign = vAlign,
               ColorRole = colorRole };
```
Then in every builder with a `+` polarity mark (`BuildVdc`, `BuildToneSource`, `BuildP1Tone`,
`BuildTerm`), set the `+` text to `SymbolPlus` and leave the `−` at default (`SymbolLine` = regular):
```csharp
Txt("+", -25, -100, fontSize: PolarityFontSize, colorRole: SymbolColorRole.SymbolPlus),
Txt("−", -25, +100, fontSize: PolarityFontSize),   // regular (SymbolLine), NOT SymbolPlus
```
Apply to ALL four builders' `+` marks (Vdc, ToneSource, P1Tone, Term). Do NOT change the `−` marks.

> Note: if the sibling brief `brief-component-library-rendering.md` (P1Tone redraw) has already
> removed P1Tone's `+/−` text, skip P1Tone here — coordinate so you don't re-add text the other brief
> deleted. For Vdc/ToneSource/Term the `+` → SymbolPlus change always applies.

**Part 3 test:** assert the `+` `TextPrimitive` in `BuildVdc`/`BuildToneSource`/`BuildTerm` has
`ColorRole == SymbolPlus` and the `−` has `ColorRole == SymbolLine`.

---

## PART 4 — SDD / ZPort: `+` port labels → SymbolPlus, controllable font size

**Requirement:** SDD and ZNP port labels — the `+` ones should use SymbolPlus; control their font size.

SDD/ZPort port labels are built in `BuildSddVariadicSymbol`: each port is one `Txt(name, …, fontSize: 10)`
where `name` is the port name like `"1+"`, `"1-"`, `"2+"`, `"2-"` (ASCII hyphen `-`). Each label is its
own primitive, so role-assign per-label by whether the name marks a `+` terminal:
```csharp
foreach (var (name, lx, ly) in ports)
{
    bool isLeft = lx < 0;
    double ax   = isLeft ? -75.0 : 75.0;
    // "+" terminal labels (name ends with '+') render in the SymbolPlus color; "−"/others regular.
    var role = name.EndsWith("+", StringComparison.Ordinal)
        ? SymbolColorRole.SymbolPlus
        : SymbolColorRole.SymbolLine;
    prims.Add(Txt(name, ax, ly, fontSize: SddPortLabelFontSize,
        align: isLeft ? SymbolTextAlign.Left : SymbolTextAlign.Right,
        vAlign: SymbolTextVAlign.Middle,
        colorRole: role));
}
```
This uses the `SddPortLabelFontSize` const from Part 2 (was the literal `10`), so SDD/ZPort label size
is now a one-line change. The `+` labels get SymbolPlus; the `-` labels stay regular.

> The label strings come from `SymbolPortDefs.GenerateSddPorts`, which formats `$"{pn}+"` / `$"{pn}-"`
> (ASCII `-`). The `EndsWith("+")` test is exact and safe. Don't try to recolor a single glyph inside
> one label — the `+`/`−` is the whole terminal's suffix and the per-primitive role is the right grain.

**Part 4 test:** build an SDD with N≥2; assert every port-label `TextPrimitive` whose `Content` ends
in `+` has `ColorRole == SymbolPlus` and those ending in `-` have `ColorRole == SymbolLine`; assert all
port labels have `FontSize == SddPortLabelFontSize`.

---

## PART 5 — Expose text ColorRole in the Symbol Editor Properties Inspector

**Requirement:** in the Symbol Editor, text primitives should have a `ColorRole` property the user can
change in the Properties Inspector (so custom symbols can use SymbolPlus / regular text).

Font size is ALREADY editable in the inspector (the "Font size" `NumericUpDown` bound to
`TextFontSize`), so the user's "control font size for any text primitive" is already satisfied for
the editor — no change needed there. Add the missing ColorRole control:

### 5a. ViewModel (`SymbolPrimitiveInspectorViewModel.cs`)
- Add a static options array next to the others:
  ```csharp
  public static SymbolColorRole[] ColorRoleOptions { get; } = Enum.GetValues<SymbolColorRole>();
  ```
- Add an observable + change handler in the Text fields region (next to `_textFontStyle`, etc.):
  ```csharp
  [ObservableProperty] private SymbolColorRole _textColorRole;

  partial void OnTextColorRoleChanged(SymbolColorRole oldValue, SymbolColorRole newValue)
  {
      if (_isRefreshing || _prim is not TextPrimitive tp || _vm is null || oldValue == newValue) return;
      _vm.Execute(new SetSymbolPrimitiveFieldCommand<SymbolColorRole>(
          _vm.EditableSymbol, "Color Role", oldValue, newValue, v => tp.ColorRole = v));
  }
  ```
  (Reuses the existing generic `SetSymbolPrimitiveFieldCommand<T>` — same pattern as Align/VAlign/
  Rotation. No new command type. Confirm that command's signature matches the others'
  `(EditableSymbol, description, old, new, Action<T>)`.)
- In `SetPrimView`, the `case TextPrimitive t:` block — populate it:
  ```csharp
  TextColorRole = t.ColorRole;
  ```

### 5b. View (`SymbolPrimitiveInspectorView.axaml`)
In the text-fields `StackPanel` (the one bound to `IsTextPrimitive`), add a ColorRole ComboBox row,
mirroring the existing "Style"/"Align" rows. Put it after "Style":
```xml
<Grid ColumnDefinitions="56,*">
    <TextBlock Grid.Column="0" Text="Color" VerticalAlignment="Center" FontSize="11"
               Foreground="{DynamicResource SystemControlForegroundBaseMediumBrush}"
               ToolTip.Tip="Text color role: SymbolPlus = the '+'/plus accent color; SymbolText/SymbolLine = regular."/>
    <ComboBox Grid.Column="1"
              ItemsSource="{x:Static vm:SymbolPrimitiveInspectorViewModel.ColorRoleOptions}"
              SelectedItem="{Binding TextColorRole, Mode=TwoWay}"
              HorizontalAlignment="Stretch"
              FontSize="11" Padding="4,2"/>
</Grid>
```

**Part 5 check:** in the Symbol Editor, select a text primitive → the inspector shows a "Color" combo
with SymbolLine/SymbolText/SymbolPlus; changing it recolors the text live and is undoable; the value
round-trips through `.csym` save/load (it serializes via the `[JsonConverter(JsonStringEnumConverter)]`
on the new property).

## Gate
Build 0W/0E (TreatWarningsAsErrors). Tests green. Verify on disk: the `+/−` polarity marks render at
36 px (not invisible); the `+` marks (Vdc/ToneSource/Term, and SDD/ZPort `+` port labels) render in
the SymbolPlus color while `−` labels are regular; the Symbol Editor inspector has a working Color
(role) combo for text primitives; existing `.csym` files load unchanged (default role = SymbolLine).

**STOP-and-report checkpoints:**
- Whether `SchematicRenderTheme` has a `SymbolText` color (so the `SymbolText → theme.SymbolText` map
  is valid) or only `SymbolLine`/`SymbolPlus` (then map SymbolText→SymbolLine and say so).
- Confirm `SetSymbolPrimitiveFieldCommand<T>`'s constructor signature so the `SymbolColorRole` call in
  5a matches exactly.
- If the P1Tone redraw brief already removed P1Tone's `+/−` text, report that you skipped P1Tone in
  Part 3 (no conflict).
