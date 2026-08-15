# Brief — harmonicaRF R9A: the readout strip, the menus, and four defaults

**Read first, in this order:**
`src/Ui/Harmonica/HarmonicaViewModel.cs:32–62` (the constructor's default marker set), `:407–495`
(`SetMarkerImpedance`, `AddMarkerBand`, `RemoveMarkerBand`, `RemoveMarkerAndShort`), `:1127–1194`
(`RequestFrame`, `PublishFrame`), `:1247–1252` (`RequestScheduledFrame`),
`src/Ui/Harmonica/HarmonicaSolver.cs:289–374` (where `markers` is snapshotted onto the frame) and
`:730–732`/`:790–870` (`AddMxColumn`),
`src/Ui/Harmonica/HarmonicaInputs.cs:110–232` (the input list, and `rgs` at `:217–224`),
`src/Ui/Views/Harmonica/ReadoutStripView.axaml` (all 100 lines — §3's two rules are in it) and its
`.axaml.cs:961–1130` (`SetInputsCore`), `:1117–1153` (`SettingsColumnKeys`,
`EffectiveSettingsColumnKeys`), `:1243–1294` (`UpdateSettingsColumn`), `:1401–1470`
(`SettingsWorstCaseValueText`, `UpdateSettingsColumnRow`),
`src/Ui/Views/Harmonica/HarmonicaView.axaml.cs:176–226` (`Refresh`), `:1136–1162`
(`AddAutoscaleLockedItems`), `:1164–1241` (`BuildMarkerMenu`), `:1310–1345` (`BuildSmithTitleMenu`),
`src/Ui/Harmonica/Renderers/HarmonicaPanelRenderer.cs:1044–1057` (`DrawPowerSweepPanel`) and
`:1315–1332` (`DrawOperatingCursor`),
`src/Harmonica/HarmonicaTitles.cs` (all of it — `MxHeaderRow` is §4),
`src/Ui/Harmonica/HarmonicaReadoutFormatting.cs:34–96` (`FixedWidth` and the per-quantity budgets),
`src/Harmonica/CircuitModel.cs:405–415` (`ExactCompressionSolve`),
`src/Ui/Views/Harmonica/HarmonicaMenuView.axaml` (both menu surfaces),
`src/Ui/Harmonica/HarmonicaAppMenuInjector.cs` (the third surface).

**Do NOT update any `CLAUDE.md`.** `src/Ui/RESOLVED.md` only (and `src/Harmonica/RESOLVED.md` for §8,
which touches `src/Harmonica`). **No screenshot verification** — every item below is pinned by a unit
test or it is not done.

Tag new comments `R9A §n`.

---

## 0. What this brief is, and what it is not

Eleven owner items, all small, all independent. **None of them may change a solved number** except §7
and §8, which are explicitly defaults. If a change you are about to make would move a contour, a
figure of merit or a solve count, you are in the wrong brief — the compression-search work is
`brief-harmonicarf-r9c`, and the two must not be tangled.

---

## 1. "Add Source Marker" does not draw the new marker (owner-reported bug)

### 1.1 The mechanism, stated precisely

The markers a Smith panel draws are **not** `HarmonicaViewModel.Markers`. They are a snapshot taken
inside `RequestFrame`:

```csharp
var marks = Markers.ToArray();                        // HarmonicaViewModel.cs:1133
...
_solver.Solve(ctx, terms, marks, opt, ...)
```

and carried onto the frame (`HarmonicaSolver.cs:301–302`, `:366–367`):

```csharp
SmithPanelData smithP = new() { ..., Markers = markers, ... };
...
Markers  = markers,
Readouts = BuildReadouts(ctx, sweep, at, markers, ...),
```

`HarmonicaPanelRenderer.DrawMarkers` reads `SmithPanelData.Markers`; the readout strip's Terminations
chunk reads `Frame.Readouts`. Both are therefore **as of the last published frame**.

The Add path never asks for a new frame. `HarmonicaView.axaml.cs:1287` does
`h.AddMarkerBand(side, band); Refresh();`, and `AddMarkerBand` → `SetMarkerImpedance` → `RedrawRequested`
→ `Refresh()` → `Canvas.InvalidateVisual()`. That redraws the **same frame**, whose marker snapshot
predates the new marker. The marker is immediately hit-testable (`HarmonicaHitTest.Resolve` reads the
LIVE `h.Markers`) and completely invisible — you can right-click a marker that is not drawn.

Its sibling `RemoveMarkerAndShort` (`:490–495`) does call `RequestScheduledFrame(dragging: false)`, which
is why removal appears to work; even there the marker survives on screen until the frame lands.

### 1.2 The fix: sync the snapshot, then ask for a frame

Two halves, and both are needed — the first makes the glyph appear at once, the second gives it a
readout row and an intrinsic glyph.

**(a) A new private method on `HarmonicaViewModel`,** called at the end of `AddMarkerBand` and of
`RemoveMarkerBand`, immediately before their existing `RedrawRequested?.Invoke()`:

```csharp
/// <summary>
/// R9A §1 — re-stamps the CURRENT frame's marker snapshot from the live <see cref="Markers"/> list.
/// The panels draw <c>SmithPanelData.Markers</c>, not this collection (see HarmonicaSolver's own
/// snapshot at RequestFrame), so a marker added or removed between frames is otherwise invisible
/// until the next solve completes — while being fully hit-testable, because HarmonicaHitTest reads
/// the live list. UI-thread only, and a pure re-projection of an already-published immutable frame:
/// nothing is re-solved, and PublishFrame's own `frame with { PowerSweep = ... }` is the precedent.
/// </summary>
private void SyncMarkerSnapshotIntoFrame()
{
    var snapshot = Markers.ToArray();
    Frame = Frame with
    {
        Markers         = snapshot,
        SmithPower      = Frame.SmithPower      with { Markers = snapshot },
        SmithEfficiency = Frame.SmithEfficiency with { Markers = snapshot },
    };
}
```

**(b) `AddMarkerBand`'s menu call site requests a frame,** exactly as removal already does. Add a
sibling of `RemoveMarkerAndShort` so the two read the same:

```csharp
/// <summary>R9A §1 — the context menu's "Add Load/Source Marker". Adds the band, makes it visible
/// THIS instant (§1.2a), then asks for a frame so the strip gains its row and the intrinsic glyph
/// appears. <b>The circuit does not change</b> — AddMarkerBand's own invariant — so the frame is a
/// re-read of an unchanged state, not a correction.</summary>
public HarmonicaMarker AddMarkerBandAndShow(TerminationSideKind side, int band)
{
    var marker = AddMarkerBand(side, band);
    RequestScheduledFrame(dragging: false);
    return marker;
}
```

`BuildAddMarkerMenuItem` calls `AddMarkerBandAndShow`. Leave `AddMarkerBand` itself alone — the
constructor and the load path both use it and neither may request a frame.

### 1.3 Gate

`tests/Ui.Tests/Harmonica/` — extend `HarmonicaAddMarkerMenuTests`:

- adding a Source band-1 marker with **no frame published in between** leaves
  `vm.Frame.SmithPower.Markers` containing a Source/1 marker (this fails today);
- and `vm.Frame.SmithEfficiency.Markers` likewise — both panels, or one chart draws it and the other
  does not;
- removing a band-2 marker takes it out of the snapshot the same way;
- `SyncMarkerSnapshotIntoFrame` does not disturb `Frame.SmithPower.Contours`/`GridPoints`/`Optimum`
  (assert reference equality — a `with` that accidentally rebuilt the contour layer would flash the
  chart).

---

## 2. `rgs*` sits in the wrong place — move it into the Capacitance chunk

### 2.1 Why it renders where it does

`HarmonicaInputs.Build` already emits `rgs` directly above `Cgs` (`:221–227`), so the INPUT LIST order
is right. The strip ignores that order for these rows: `SetInputsCore` partitions on membership of
`SettingsColumnKeys` (`ReadoutStripView.axaml.cs:970–977`), and `KeyRgs` is not in that array — so it
falls into `rest`, the wrapping `Inputs` `WrapPanel`, which is the one place that renders
`input.Label + "*"` for a structural row (`:1012`).

### 2.2 The fix

- Add `HarmonicaInputs.KeyRgs` to `SettingsColumnKeys` (`:1117–1130`) **immediately after
  `CapacitanceSpacerKey` and before `KeyCgs`** — that array's order is the rendered order.
- `EffectiveSettingsColumnKeys` (`:1149–1153`) gates the SDD-only tail on `named.ContainsKey(KeyCgs)`;
  leave that test exactly as it is. `rgs` and `Cgs` are emitted by the same `DutKind.Sdd` branch, so
  the Cgs probe already answers correctly for rgs, and adding a second condition would be two ways to
  ask one question.
- `BaseSettingsColumnCount` is `Array.IndexOf(SettingsColumnKeys, CapacitanceSpacerKey)` — unchanged by
  an insertion *after* the spacer. Do not touch it.
- Add a `KeyRgs` case to `SettingsWorstCaseValueText` (`:1408–1422`): `"00000.000"`. Without it the row
  falls to the `_ => "0000000000"` default and reserves a visibly wider value cell than its
  neighbours, which is exactly the column jitter R7C §1.3 exists to prevent.

Three things then follow **for free, and you must not re-implement any of them**:

- the label renders `rgs (Ω):` — `UpdateSettingsColumnRow` writes `LabelWithUnit(input.Label,
  input.Unit)`, the same convention every other chunk row uses;
- the `*` disappears — the Settings column deliberately carries no structural marker (`:1439–1442`),
  and the structural note stays in the tooltip;
- double-click opens the inline editor — `BuildSettingsColumnRow`'s `DoubleTapped` handler is generic,
  and `state.Locked` is false because `HarmonicaInputs` builds rgs through plain `Make`, never
  `CapacitanceRow`.

**Leave `IsCapacitanceKey` alone.** rgs must NOT get the nonlinear-C right-click menu — it has no
nonlinear form, which is precisely why `HarmonicaInputs` builds it with `Make` rather than
`CapacitanceRow`.

### 2.3 Gate

Extend `tests/Ui.Tests/Harmonica/HarmonicaInputsRgsTests.cs`:

- `SettingsColumnKeys` contains `KeyRgs`, and its index is exactly one less than `KeyCgs`'s;
- `EffectiveSettingsColumnKeys` for a dictionary carrying `KeyCgs` includes `KeyRgs`, and for one
  without it includes neither;
- `SettingsWorstCaseValueText(KeyRgs)` is not the `_` default string;
- `IsCapacitanceKey(KeyRgs)` is false.

These are all reachable through `internal`/`private static` members from `Ui.Tests` as the existing
file already does; do not add a live-control test — the strip cannot be instantiated headlessly.

---

## 3. The two horizontal rules between the charts and the readouts

`ReadoutStripView.axaml` has exactly two 1-px `Border`s, and they are the two lines the owner sees:

```xml
<Border x:Name="InputRule"  Height="1" Opacity="0.25" IsVisible="False"/>   <!-- line 72 -->
<Border x:Name="ColumnRule" Height="1" Opacity="0.25" IsVisible="False"/>   <!-- line 75 -->
```

Delete both elements, and delete every code-behind line that writes them:
`ReadoutStripView.axaml.cs:318–320` (`ColumnRule.IsVisible` / `.Background`) and `:996–997`
(`InputRule.IsVisible` / `.Background`). Nothing else reads either name.

Keep the surrounding `StackPanel`'s `Spacing="3"` — the chunks still need separating, and the owner
asked for the *lines* to go, not the gaps.

**Gate:** a source-scan test in `tests/Ui.Tests/Harmonica/` asserting `ReadoutStripView.axaml`
contains neither `InputRule` nor `ColumnRule`. This repo already has that pattern (H8's source-scan
tests); **strip comments before matching**, as those tests do — the removal will be described in a
comment.

---

## 4. Fewer digits in the MXP/MXE ZL readout

The header is built by `HarmonicaTitles.MxHeaderRow(label, side, harmonic, zText)`, and `zText` comes
from `HarmonicaSolver.AddMxColumn:828–829`:

```csharp
string zText = HarmonicaReadoutFormatting.FormatZ(
    HarmonicaDataSet.ImpedanceOf(optimum.Gamma, ctx.Model.Settings.Z0), format($"{label}.MxZ"));
```

`FormatZ` → `FormatComplex` → `FixedWidth(..., ComplexPartDecimals = 3, ...)`. Three decimals on an
argmax read off a fitted surface claims precision the surface does not have.

**Add a dedicated formatter rather than lowering `ComplexPartDecimals`** — that constant is shared by
every complex row in the strip (Zin, the terminations, the intrinsic spectra) and the owner asked only
for the MX headers:

```csharp
/// <summary>R9A §4 — the MXP/MXE header's own impedance, at ONE decimal. This is the argmax of a
/// fitted RBF surface, not a measured value: three decimals (<see cref="ComplexPartDecimals"/>, which
/// every other complex row uses and which stays untouched) reads as a precision the fit does not
/// carry. One named constant, so the digit count is changed in one place.</summary>
public const int MxHeaderZDecimals = 1;

public static string FormatZCompact(Complex z, ReadoutFormat format) => ...   // as FormatComplex,
                                                                              // MxHeaderZDecimals places
```

Implement it by giving `FormatComplex` an optional `decimals`/`magDecimals` pair defaulting to today's
constants rather than by copying its body — one formatter, two digit budgets.

`AddMxColumn` calls `FormatZCompact` for `zText` **only**. The MXP/MXE column's own `Zin` row is
untouched and stays at three decimals.

**Gate:** `tests/Harmonica.Tests/HarmonicaTitlesTests.cs` for the header shape plus a
`HarmonicaReadoutFormatting` test asserting `FormatZCompact(new Complex(96.3312, -0.1523), …)` is
`"96.3-j0.2 Ω"` and that `FormatZ` on the same value is still `"96.331-j0.152 Ω"`.

---

## 5. Remove the vertical compression line from the Power Sweep plot

**Owner ruling, 2026-08-15: remove it entirely.** Delete the `DrawOperatingCursor` call
(`HarmonicaPanelRenderer.cs:1055`) and the method itself (`:1315–1332`).

**State the consequence in the code comment rather than discovering it later:** this same line was the
only on-plot indicator of a USER-PLACED operating point (Display ▸ Cursor Snap to Compression off →
`HarmonicaViewModel.PlacedCursorPinDbm`). That state is still live, still drives which step the
glyphs/loadline/readouts are evaluated at, and is still readable in the strip — it simply has no mark
on the power-sweep curve any more. The owner chose that knowingly.

`PowerSweepPanelData.CursorIndex` stays — `HarmonicaViewModel.OperatingPointDbm` reads it, and
`DrawDidNotCompressNote` is unaffected. Do not remove the field.

**Gate:** `tests/Ui.Tests/Harmonica/HarmonicaPanelTests.cs` — render a power-sweep panel at a frame
with `CursorIndex >= 0` and assert no dashed vertical stroke is emitted. The existing panel tests
render to an `SKSurface` and inspect pixels; the cheapest honest oracle here is a **differential**
render: the same panel drawn at `CursorIndex = -1` and at a valid index must be pixel-identical.
(A pixel probe at one column cannot separate the cursor from a grid line — the same trap H4–H5
recorded for iso-lines vs Smith chrome.)

---

## 6. "Add Point" → "Add Grid Points", "Add Points to VSWR" → "Add Grid Points to VSWR"

`HarmonicaView.axaml.cs:1220` and `:1227`. Header text only. `AddGridPoint` /
`AddGridPointsOnVswrCircle`, both commands' names, both tooltips and the icons all stay.

**Gate:** `tests/Ui.Tests/Harmonica/` — a source-scan on `HarmonicaView.axaml.cs` for the two exact new
strings and the absence of the two old ones (comments stripped, per §3).

---

## 7. The default L1 marker is `ZL1 = 80 Ω`

`HarmonicaViewModel.cs:57`:

```csharp
SetMarkerImpedance(Markers[0], new Complex(80, 10));   // →  new Complex(80, 0)
```

Update the constructor comment at `:44` too — it names "L1 (80+j10 Ω)" as unchanged, which stops being
true. The reason belongs in that comment: **80 Ω is the default DUT's own R_opt, and is also the
default `HarmonicaSettings.Z0` (`CircuitModel.cs:323`)** — so the default document now opens with L1 at
the centre of its own Smith chart, which is what `Z0 = R_opt` is for.

Default-model path only. A loaded `.charm` goes through `RebuildMarkersFromTerminations` and is
untouched.

**Gate:** `tests/Ui.Tests/Harmonica/HarmonicaDefaultMarkerSetTests.cs` — L1's Γ is exactly zero at the
default Z0, and its impedance is `80+j0`. Check whether any existing test asserts `80+j10` and update
it in the same edit rather than leaving two disagreeing expectations.

---

## 8. `ExactCompressionSolve` defaults ON

`src/Harmonica/CircuitModel.cs:414`:

```csharp
public bool ExactCompressionSolve { get; init; } = true;     // R9A §8
```

Rewrite the doc comment: it currently says the option is off by default and costs one extra solve, and
the second half stays true.

**Scope, stated because it is not obvious.** `CharmIo` writes this field on every save and reads it
`s?.ExactCompressionSolve ?? defaults.ExactCompressionSolve` (`CharmIo.cs:243`, `:351`) — so flipping
the C# default changes new documents **and** any pre-R2B `.charm` that predates the field. Every
document saved since it landed carries its own explicit value and opens exactly as the owner left it.
That is the correct and intended blast radius; say so in `src/Harmonica/RESOLVED.md`.

**Gate:** `tests/Harmonica.Tests/PinSweepTests.cs` — a default `HarmonicaSettings` has it on; a
round-tripped `.charm` with it explicitly off still reads back off (the persisted value must win over
the new default).

---

## 9. Display ▸ Efficiency Metric ▸ "DE" reads "Drain Efficiency"

Four surfaces, display text only — **`CommandParameter` stays the string `"DE"`** everywhere, and
`HarmonicaMenuViewModel.SetEfficiencyMetric` is untouched:

| file | line | change |
|---|---|---|
| `src/Ui/Views/Harmonica/HarmonicaView.axaml.cs` | 1336 | `Toggle("DE", …)` → `Toggle("Drain Efficiency", …)` |
| `src/Ui/Views/Harmonica/HarmonicaMenuView.axaml` | 117 | `Header="DE"` → `Header="Drain Efficiency"` |
| `src/Ui/Views/Harmonica/HarmonicaMenuView.axaml` | 278 | `Header="_DE"` → `Header="_Drain Efficiency"` |
| `src/Ui/Harmonica/HarmonicaAppMenuInjector.cs` | 147 | `Item("DE", …)` → `Item("Drain Efficiency", …)` |

The strip's own `Eff` row label and `HarmonicaTitles.MetricRow`'s `"P-3dB Efficiency (%)"` are NOT
part of this — the owner named the menu item.

**Gate:** extend `tests/Ui.Tests/Harmonica/HarmonicaAppMenuInjectorTests.cs` for the injector surface,
and a source-scan for the two `.axaml` lines.

---

## 10. "Locked" uses the same checkbox glyphs as "Show Grid Points"

`AddAutoscaleLockedItems` (`HarmonicaView.axaml.cs:1144–1162`) currently gives Autoscale a dimmed
`ArrowExpandAll` and Locked a `Lock`/`LockOpenVariant`. The owner wants Locked to read as the
checkbox toggle it is — the same glyph pair `Toggle(…, glyph: MenuGlyph.Check)` produces for "Show
Grid Points", i.e. `CheckboxOutline` / `CheckboxBlankOutline`.

The two rows are a mutually-exclusive pair of one state (`autoscaleOn`), so:

- Autoscale becomes `Toggle("Autoscale", autoscaleOn, onAutoscaleClick)`;
- Locked becomes `Toggle("Locked", !autoscaleOn, onLockedClick)`.

**Keep every word of that method's existing doc comment about `ToggleType`** — R7A §2.3's finding
(the Fluent template's check glyph and `Icon` fight for one leading slot, so the ICON carries the
state and `ToggleType` is never set) is exactly what `Toggle` already does, so this is a move onto the
shared helper, not a re-litigation. Both rows stay always-enabled and always-clickable; clicking the
active one is still the harmless re-capture it is today.

This reaches the DCIV/Loadline menu and the Power Sweep title menu together — they are the only two
call sites.

**Gate:** the helper is `private static`; extract the glyph decision into an
`internal static MaterialIconKind AutoscaleLockedGlyph(bool on)` (or assert through
`HarmonicaPowerSweepFlyMenuTests`, which already reaches these menus) so the pairing is pinned
without a live menu.

---

## 11. No status messages during a drag

`HarmonicaView.Refresh` (`:189–192`) writes the message line on every refresh, including every
mid-drag frame:

```csharp
MessageText.Text = h.StatusMessage is { Length: > 0 } msg
    ? msg
    : $"{h.LastSolveCount} HB solves · … holes · {h.Frame.Quality}";
```

**The owner's rule is literal: during a drag, post nothing.** Gate the whole assignment on the live
gesture, which the view already owns:

```csharp
// R9A §11 — owner ruling: nothing is posted to the message line while a gesture is live. The idle
// solve-cost summary updated on every published mid-drag frame, which is a changing line under a
// moving hand — the one thing §2 (R1C) said this line must not be. IsLive covers a marker drag, an
// intrinsic-glyph drag, a grid-point drag and an Edit Display grab, which is every case the owner
// can be inside. The line is restored by the very next Refresh after release, so a solve error
// raised mid-drag is still reported — one frame later, when it can be read.
MessageText.Text = Canvas.Gesture is { IsLive: true }
    ? ""
    : h.StatusMessage is { Length: > 0 } msg ? msg : $"{h.LastSolveCount} HB solves · …";
```

Leave the "Solving…" text, the progress bar and the counter exactly as they are — those are gated on
`h.IsSolvingGrid`, and a drag skips the grid (`OptionsFor`'s `SkipContours = dragging || …`), so they
are already silent during a drag for a reason that has nothing to do with this change.

**Gate:** `Refresh` is not reachable headlessly. Extract the decision:

```csharp
/// <summary>R9A §11 — what the message line shows. Pure, so Ui.Tests can pin it without a control.</summary>
internal static string MessageLineText(bool gestureLive, string? statusMessage, string idleSummary)
    => gestureLive ? "" : (statusMessage is { Length: > 0 } m ? m : idleSummary);
```

and test all four combinations, including "a status message present while dragging still yields
empty".

---

## 12. Gate for the whole brief

```
dotnet build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Harmonica.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

(three commands — this SDK's `dotnet test` refuses more than one project path per invocation).

`src/Harmonica` is touched by §4 (`HarmonicaTitles`) and §8 (`CircuitModel`) only, and neither may
gain a UI reference.

Write the outcome to `src/Ui/RESOLVED.md` (and one paragraph in `src/Harmonica/RESOLVED.md` for §8's
blast radius). **No `CLAUDE.md` edits.**
