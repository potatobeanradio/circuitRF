# Sonnet Brief — Component instance-label hitbox + SDD/ZNP label clearance

Two independent label bugs in the schematic editor. Both stem from label-position geometry being
computed in more than one place. Fix A unifies the hitbox with the renderer; Fix B makes the SDD/ZNP
label base-Y a function of port count so text clears the N-grown symbol.

READ FIRST (do not skip — these are the files the fix touches or must stay consistent with):
- `src/Ui/Schematic/SchematicModel.cs` — `SchematicComponent` label constants (the single source of truth).
- `src/Ui/Renderers/SchematicRenderer.cs` — `DrawLabels` (AUTHORITATIVE render geometry).
- `src/Ui/Schematic/SchematicHitTest.cs` — `TestComponentLabels` (Bug A lives here).
- `src/Ui/Schematic/EditableSchematic.cs` — `EditableComponent.ToRenderComponent` (the LIVE FullBb build) + `SymbolPortDefs.SddBodyRect`.
- `src/Ui/Schematic/SchematicModelBuilder.cs` — demo builder (keep consistent; NOT the live path).

---

## Background: how labels are positioned today

`SchematicComponent` (in SchematicModel.cs) holds the label-layout constants, declared as the single
source of truth:
```
LabelBaseOffsetX = -155.0;  // label anchor X from component center
LabelBaseY       =  280.0;  // first-row Skia BASELINE Y from center
LabelWorldHeight =   70.0;  // font cap-height (world units)
LabelWorldStep   =   72.0;  // line-to-line spacing
LabelWidthEstimate = 500.0; // conservative width estimate
```
The renderer's `DrawLabels` is authoritative. For label `i` it draws **left-aligned at the baseline**:
```
worldX = cx + LabelBaseOffsetX + oDx
worldY = cy + LabelBaseY + oDy + i*LabelWorldStep      // (oDx,oDy) = LabelOffsets[i] (+ drag delta)
```
`ToRenderComponent` and `SchematicModelBuilder` both compute the FullBb from these SAME constants, so
they stay in sync automatically. **The hit-test does NOT** — it is the one divergent copy.

---

## BUG A — label hitbox drifts from the rendered text

`SchematicHitTest.TestComponentLabels` computes the clickable zone from its OWN private constants:
```
LabelRowHeight = 72.0;   LabelStartOffY = 134.0;   CharWidthWorld = 38.5;
centerY  = comp.Y + LabelStartOffY + row*LabelRowHeight + LabelRowHeight*0.5 + oDy;
textLeft  = comp.X + oDx - 165;
textRight = comp.X + oDx - 155 + len*CharWidthWorld + 50;
```
This is a parallel re-derivation: `LabelStartOffY=134` was hand-tuned to approximate the renderer's
`LabelBaseY=280` baseline-to-row-center conversion (see the comment block above the constants). It
already only roughly matches, and any change to the renderer constants — or interaction with a
user's manual label tweak — makes the hit zone drift off the glyph text. The user reports exactly
this after manually moving a label.

### Fix A — derive the hitbox from the SAME canonical constants the renderer uses

Add a shared static geometry helper on `SchematicComponent` (SchematicModel.cs) that returns label
`i`'s world anchor and a hit band, computed from the canonical constants + a supplied `(oDx,oDy)`
offset. Both `DrawLabels` and `TestComponentLabels` reference it so they can never drift.

```csharp
// In SchematicComponent (SchematicModel.cs), alongside the label constants:

/// <summary>
/// Canonical world geometry for label row <paramref name="i"/>, given the per-label offset
/// (LabelOffsets[i] plus any live drag delta). Single source of truth shared by the renderer
/// (DrawLabels) and the hit-test (TestComponentLabels) so the clickable zone always tracks the
/// rendered text. Returns the left-aligned text anchor (BaselineX, BaselineY) — matching Skia's
/// left/baseline draw in DrawLabels — and the vertical hit band [BandTopY, BandBotY] centered on
/// the visual row. Caller supplies the text's world width for the horizontal extent.
/// </summary>
public static (double BaselineX, double BaselineY, double BandTopY, double BandBotY)
    LabelRowGeometry(double cx, double cy, int i, double oDx, double oDy)
{
    double baselineX = cx + LabelBaseOffsetX + oDx;
    double baselineY = cy + LabelBaseY + oDy + i * LabelWorldStep;
    // Hit band: cap-height above the baseline, ~28% descender below, plus small click comfort.
    // (Matches the visual ink of the row the renderer draws at this baseline.)
    const double comfort = 6.0;
    double bandTopY = baselineY - LabelWorldHeight - comfort;
    double bandBotY = baselineY + LabelWorldHeight * 0.28 + comfort;
    return (baselineX, baselineY, bandTopY, bandBotY);
}
```

Then rewrite `TestComponentLabels` to use it. Key change: iterate using the SAME label-row layout the
renderer/`ToRenderComponent` use (`i` = index into the Labels list: 0=type, 1=name, 2+=shown params),
get geometry from `LabelRowGeometry`, and test the band + a left-aligned horizontal extent. Keep the
existing suppression rules (Ground row 0/1, `!ShowTypeLabel`, `!ShowInstanceName`) and keep returning
`SubIndex` = the FULL Parameters index for param rows.

```csharp
private static HitResult TestComponentLabels(EditableComponent comp, double wx, double wy)
{
    // Build the displayed rows in the SAME order the renderer draws them:
    // row 0 = type, row 1 = instance name, row 2+ = params with ShowOnSchematic && non-empty Expr.
    var shownParams = new List<(int FullIndex, EditableParameter Param)>();
    for (int pi = 0; pi < comp.Parameters.Count; pi++)
    {
        var p = comp.Parameters[pi];
        if (p.ShowOnSchematic && !string.IsNullOrEmpty(p.Expression))
            shownParams.Add((pi, p));
    }

    int totalRows = 2 + shownParams.Count;
    for (int row = 0; row < totalRows; row++)
    {
        bool suppressed = row switch
        {
            0 => comp.Symbol == SymbolKind.Ground || !comp.ShowTypeLabel,
            1 => comp.Symbol == SymbolKind.Ground || !comp.ShowInstanceName,
            _ => false,
        };
        if (suppressed) continue;

        var (oDx, oDy) = row < comp.LabelOffsets.Count ? comp.LabelOffsets[row] : (0.0, 0.0);
        var (baseX, _, bandTop, bandBot) =
            SchematicComponent.LabelRowGeometry(comp.X, comp.Y, row, oDx, oDy);

        if (wy < bandTop || wy > bandBot) continue;

        string labelText = row switch
        {
            0 => ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount),
            1 => comp.Symbol == SymbolKind.Ground ? "" : comp.InstanceName,
            _ => ParamLabelText(shownParams[row - 2].Param),
        };

        // Left-aligned horizontal extent (matches DrawLabels' SKTextAlign.Left at baseX).
        double textLeft  = baseX - 10;                                   // small left comfort
        double textRight = baseX + labelText.Length * CharWidthWorld + 10;
        if (wx < textLeft || wx > textRight) continue;

        double centerY = (bandTop + bandBot) * 0.5;
        return row switch
        {
            0 => new HitResult(HitKind.ComponentType,  comp.Id, 0, baseX, centerY),
            1 => new HitResult(HitKind.ComponentName,  comp.Id, 0, baseX, centerY),
            _ => new HitResult(HitKind.ComponentParam, comp.Id, shownParams[row - 2].FullIndex, baseX, centerY),
        };
    }
    return new HitResult(HitKind.None, "");
}
```
Notes:
- DELETE the now-unused private `LabelRowHeight` and `LabelStartOffY` constants from SchematicHitTest.
  KEEP `CharWidthWorld` (still used for the horizontal extent). The net-label constants
  (`NetLabel*`) are unrelated — leave them.
- The old code anchored X at `comp.X + oDx - 165 / -155`; the renderer anchors at
  `comp.X + LabelBaseOffsetX(=-155) + oDx`. Using `baseX` from the helper fixes the ~10-unit X skew
  for free (the old `-165` left pad becomes the explicit `-10` comfort).
- Also update `DrawLabels` to call `LabelRowGeometry` for its `worldX/worldY` so the renderer and
  hit-test are provably the same source (it currently inlines the same formula — switch it to the
  helper, folding the drag delta into `oDx/oDy` before the call). This is a small refactor, not a
  behavior change; verify pixel output is identical.

---

## BUG B — SDD/ZNP instance label overlaps the N-grown symbol

`LabelBaseY = 280.0` is a fixed constant. For `Sdd`/`ZPort` the glyph grows DOWNWARD with port count:
`GenerateSddPorts(n)` (in EditableSchematic.cs) spreads port centers at `(p - (nLeft-1)/2)*400` with
±100 for the ± pins, so the body half-height grows with N. `SymbolPortDefs.SddBodyRect(n)` already
computes this: `HalfH = max|pinY| + 60`. For N≥4 the label rows starting at y=280 collide with the
glyph body.

### Fix B — push the SDD/ZNP label base-Y below the glyph, as a function of N

The label base-Y for these two variadic types must clear the glyph's bottom edge. Reuse the existing
`SddBodyRect` half-height so the label math and the symbol body can never disagree.

Add a small static helper on `SchematicComponent` that returns the effective label base-Y for a given
symbol + port count, defaulting to the constant for everything except SDD/ZPort:

```csharp
// In SchematicComponent (SchematicModel.cs):

/// <summary>
/// First-row label baseline Y (from component center) for this symbol and port count.
/// For fixed-geometry symbols this is the constant LabelBaseY. For the variadic SDD/ZPort
/// symbols whose body grows with port count, the base-Y is pushed just below the glyph's
/// bottom edge (SymbolPortDefs.SddBodyRect(n).HalfH) so the label never overlaps the symbol.
/// </summary>
public static double LabelBaseYFor(SymbolKind symbol, int portCount)
{
    if (symbol is SymbolKind.Sdd or SymbolKind.ZPort)
    {
        double halfH = SymbolPortDefs.SddBodyRect(portCount).HalfH;
        // Clear the body bottom edge with a one-row gap; never tighter than the default.
        return Math.Max(LabelBaseY, halfH + LabelWorldStep);
    }
    return LabelBaseY;
}
```
(`SddBodyRect` lives in `SymbolPortDefs` in EditableSchematic.cs and is already used by the symbol
body geometry — this reuse keeps label clearance and body height locked together, which is the point.)

Then make every label-Y computation use `LabelBaseYFor(symbol, portCount)` in place of the bare
`LabelBaseY` constant. Three call sites + the two helpers above:

1. **`SchematicComponent.LabelRowGeometry`** (the new Fix-A helper) — it must take the symbol+port
   count (or the resolved base-Y) so the hit band moves with the label. Simplest: add params and
   compute base-Y inside:
   ```csharp
   public static (double BaselineX, double BaselineY, double BandTopY, double BandBotY)
       LabelRowGeometry(double cx, double cy, int i, double oDx, double oDy,
                        SymbolKind symbol, int portCount)
   {
       double baseY = LabelBaseYFor(symbol, portCount);
       double baselineX = cx + LabelBaseOffsetX + oDx;
       double baselineY = cy + baseY + oDy + i * LabelWorldStep;
       ...
   }
   ```
   Update both callers (`DrawLabels`, `TestComponentLabels`) to pass `comp.Symbol`/`c.Symbol` and the
   port count. In the renderer, `SchematicComponent` doesn't carry a port-count field directly — use
   `c.Ports.Count / 2` for SDD/ZPort (matches how the renderer already derives N elsewhere, e.g.
   `BuiltInSymbols.Primitives(c.Symbol, c.Ports.Count / 2)`), and it's harmless for other kinds since
   `LabelBaseYFor` ignores portCount unless SDD/ZPort. In the hit-test, `EditableComponent` has
   `PortCount` directly.

2. **`EditableComponent.ToRenderComponent`** (live FullBb loop) — replace
   `Y + SchematicComponent.LabelBaseY + oDy + li*…` with
   `Y + SchematicComponent.LabelBaseYFor(Symbol, PortCount) + oDy + li*…`.

3. **`SchematicModelBuilder.MakeComponent`** (demo FullBb loop) — same substitution, using the
   builder's local `kind`/`n`. (Demo only, but keep it consistent so demo SDDs don't render with
   overlapping labels.)

DO NOT change `LabelWorldStep`, `LabelBaseOffsetX`, or `LabelWorldHeight` — only the base-Y becomes
N-aware. Non-SDD/ZPort components are completely unaffected (LabelBaseYFor returns the constant).

---

## Tests (Ui.Tests, headless — no Avalonia/Skia needed; these are pure geometry)

1. **LabelHitbox_TracksRenderer_NoOffset**: a Resistor at (0,0); for each visible row, compute the
   renderer baseline via `SchematicComponent.LabelRowGeometry(...)` and assert
   `TestComponentLabels` returns that row's HitKind when probed at `(baselineX + small, centerY)`,
   and `None` just outside the band.
2. **LabelHitbox_TracksRenderer_WithOffset**: set `LabelOffsets[1] = (40, 30)` (move the instance-name
   label); assert the hit zone for row 1 moves by exactly (40,30) and rows 0/2 do not move. (This is
   the user's bug — the regression guard.)
3. **LabelBaseY_Constant_ForFixedSymbols**: `LabelBaseYFor(Resistor, _) == LabelBaseY`;
   `LabelBaseYFor(Vdc, _) == LabelBaseY`.
4. **LabelBaseY_GrowsWithPorts_ForSdd**: `LabelBaseYFor(Sdd, 2) <= LabelBaseYFor(Sdd, 4) <
   LabelBaseYFor(Sdd, 8)`, and for N where `SddBodyRect(N).HalfH + LabelWorldStep > LabelBaseY`,
   assert `LabelBaseYFor(Sdd, N) == SddBodyRect(N).HalfH + LabelWorldStep` (clears the glyph).
5. **SddLabel_ClearsGlyph**: for N in {4,6,8}, assert the first label baseline
   (`cy + LabelBaseYFor(Sdd,N)`) is strictly greater than the glyph bottom edge
   (`cy + SddBodyRect(N).HalfH`).
6. **DrawLabels_HitTest_SameBaseline** (consistency): for a handful of (symbol, N, offset) cases,
   assert the baseline `DrawLabels` would use equals `LabelRowGeometry`'s `BaselineY` — i.e. the two
   call the same source. (If you refactor DrawLabels to call the helper, this is trivially true; the
   test pins it so a future inline edit can't reintroduce drift.)

## Gate
Build 0W/0E (TreatWarningsAsErrors). All tests green. Manual: place an SDD8, confirm labels sit
clearly below the (tall) body with no overlap; place a Resistor, move its value label with F5, then
double-click exactly on the moved text — the inline editor opens (hitbox tracked the move).

## On completion
Note in `src/Ui/CLAUDE.md`: label row geometry has a single source of truth —
`SchematicComponent.LabelRowGeometry` (anchor + hit band) and `SchematicComponent.LabelBaseYFor`
(N-aware base-Y for SDD/ZPort, reusing `SymbolPortDefs.SddBodyRect`). The renderer (`DrawLabels`),
the hit-test (`TestComponentLabels`), and both FullBb builders (`ToRenderComponent`,
`SchematicModelBuilder`) all derive from these, so the clickable zone always tracks the rendered text
and SDD/ZNP labels always clear the port-count-grown body. Do not reintroduce a parallel copy of the
label-layout constants in the hit-test.
