# Brief — harmonicaRF R7D: Cgs / Cdg / Cds on the DUT, linear or nonlinear

**Read first:** `src/Harmonica/CircuitModel.cs` (`DutSpec` ~28, `LumpedPackage` ~82,
`HarmonicaSettings`, `StructuralKey` ~462), `src/Harmonica/HarmonicaNetlist.cs` (`Build`, `DutLine`,
`Shunt`, `Series`), `src/Harmonica/HarmonicaContext.cs` (`Rebuild`, ~line 183),
`src/Harmonica/HarmonicaDataSet.cs` (~line 118–160, what is published),
`src/Harmonica/CharmIo.cs` (`CharmDut` ~381), `src/Ui/Harmonica/HarmonicaInputs.cs` (the `Key*`
constants ~91, `Build`, `Apply`), `src/Ui/Views/Harmonica/ReadoutStripView.axaml.cs`
(`SettingsColumnKeys` ~970, `BuildSettingsColumnRow` ~1071, `UpdateSettingsColumnRow` ~1150,
`AttachChunkCopyMenu` ~78), `src/Core/Devices/NonlinearCModel.cs`,
`src/Core/Devices/ComponentModelFactory.cs` (~line 890–900, how `C0, C1, …` are read),
`src/Ui/ViewModels/NonlinearCvEditorViewModel.cs`, `src/Ui/Views/Dialogs/NonlinearCvEditorView.axaml`
(+`Dialog`), `src/Ui/ViewModels/ParameterEditorViewModel.cs` (~line 131, `ShowCvEditorButton`),
`src/Ui/Views/DataDisplay/MarkerEditorView.axaml` (~line 44) and
`src/Ui/Views/DataDisplay/PlotInspectorView.axaml` (~line 246) — the two places in this repo where a
`NumericUpDown` renders correctly.

**Do NOT update any `CLAUDE.md`.** `src/Ui/RESOLVED.md` / `src/Harmonica/RESOLVED.md` only if
something below is worth recording.

Tag new comments `R7D §n`.

**Ordering:** this brief and `brief-harmonicarf-r7c-readout-units-jitter-and-gamma-metric.md` both
touch the readout strip's rows. R7C should land first. If it has, put the unit inside the label
(`Cgs (pF): 1.23`) per R7C §1.1; if it has not, follow whatever convention is in the tree and do not
invent a third.

---

## 0. What the owner asked for

> "I want to add ability for user to add linear or 1D nonlinear capacitors Cgs, Cdg and Cds to the
> DUT when the harmonicaRF DUT is an SDD component. This helps user see the contour rotation,
> loadline shifts, and input distortion generation due to parasitic FET capacitance and will exercise
> harmonicaRF's 'intrinsic' marker termination glyphs for the first time in harmonicaRF."

> "Add 'Capacitance' data chunk to the display so User can configure the capacitance for Cgs, Cdg and
> Cds. Place this under the Z0 readout, but put a spacer in between to show that the capacitances are
> separate settings. When the capacitance is linear, the readout gives the capacitance to 2 decimal
> places with units pF. User can edit their values using the inline text editor. User can right-click
> on their display and a fly menu appears with a 'Use Nonlinear'. If user selects that option a
> parameter dialog appears to set the nonlinear characteristics. Reuse the Parameter Editor for the
> NonlinearC component for user to configure a capacitance. When nonlinear, the capacitance readouts
> display the linearized capacitance at the bias point with a ' (linearized)' suffix after pH units.
> The inline text editor is not allowed to edit nonlinear capacitance. When no capacitance is used
> for a capacitor, the readout should say '0 pF'."

> "By the way, the Poly order UI element doesn't render properly in component parameters editor for
> the NonlinearC. We've tried to fix this 3 times before, but Sonnet could not figure it out. Include
> this fix in the brief."

(`pH` in the nonlinear sentence is a slip for `pF` — the whole feature is capacitance. Use pF
throughout; do not build a picohenry readout.)

---

## 1. Where the capacitors go, electrically — this is the point of the feature

The DUT's own terminals are the nodes `HarmonicaNetlist.DutLine` receives as `gate` / `drain` /
`source` (`HarmonicaNetlist.cs:~130`, after the package's `Series`/`Shunt` have been emitted). An SDD
attaches directly there as two (or three) `±` port pairs.

Putting `C` across those same node pairs puts it **in parallel with the SDD's ports** — so the SDD
becomes the bare current generator, and the capacitors sit between the intrinsic generator and the
package plane. `IntrinsicPlane` reads the SDD component's own port voltages and currents, which do
**not** include the capacitors' displacement current, so `Z_intr` / `Γ_intr` now genuinely differ
from the terminal impedance. That is exactly the "exercise the intrinsic marker glyphs" the owner is
after, and it falls out of the topology rather than needing any new intrinsic-plane code.

Emit, after the DUT line:

| name | nets | note |
|---|---|---|
| `CGS` | `{gate} {source}` | V = Vg − Vs |
| `CDG` | `{drain} {gate}` | **V = Vd − Vg**, positive in normal operation |
| `CDS` | `{drain} {source}` | V = Vd − Vs |

`{source}` is the literal `"0"` when the package states no source lead (see `Build`'s own
`sourceTerminal` local) — that is correct and needs no special case.

**The `CDG` node order is load-bearing.** `NonlinearCModel` is a polynomial in its own terminal
voltage `V(n+) − V(n−)`; with the nets the other way round every odd coefficient changes sign, which
is invisible for a purely even C(V) and wrong for every real one. Whatever order you emit, the
linearization readout in §3.3 must use the same one. Put the convention in a comment at both places.

**SDD only.** Emit nothing for a Native FET (which has its own gate charge via `CapModel` — see the
root `CLAUDE.md`'s component list — so adding these would double-count) or an External model (which
carries its own parasitics). The strip rows follow the same rule: present when the DUT is an SDD,
absent otherwise. That shape change happens on a DUT change, never per frame, so it costs nothing.

---

## 2. Model, persistence, netlist

### 2.1 State

Add to `src/Harmonica/CircuitModel.cs`:

```csharp
/// <summary>R7D — one of the three DUT capacitances. Absent, linear, or a 1-D polynomial C(V).</summary>
public sealed record DutCapacitance
{
    /// <summary>Farads. Used when <see cref="Coefficients"/> is null. Zero means "no capacitor at all"
    /// — nothing is emitted into the netlist.</summary>
    public double Farads { get; init; }

    /// <summary>C0…Cn of C(V) = Σ Cₖ·Vᵏ, in raw SI (F, F/V, F/V², …) — the SAME spelling and the same
    /// units NonlinearC's own C0/C1/… parameters use. Null for a linear capacitor.</summary>
    public IReadOnlyList<double>? Coefficients { get; init; }

    public bool IsNonlinear => Coefficients is { Count: > 0 };
    public bool IsAbsent    => !IsNonlinear && Farads == 0.0;

    public static readonly DutCapacitance None = new();
}

public sealed record DutCapacitances
{
    public DutCapacitance Cgs { get; init; } = DutCapacitance.None;
    public DutCapacitance Cdg { get; init; } = DutCapacitance.None;
    public DutCapacitance Cds { get; init; } = DutCapacitance.None;
    public static readonly DutCapacitances None = new();
    public bool IsIdentity => Cgs.IsAbsent && Cdg.IsAbsent && Cds.IsAbsent;
}
```

Hang it off `DutSpec` — `public DutCapacitances Capacitances { get; init; } = DutCapacitances.None;`
— because it is a property of *this device*, and because that is where the SDD-only rule reads
naturally. Default `None`, so every existing construction site keeps compiling and every existing
document is unchanged.

### 2.2 StructuralKey

Add the capacitances to `CircuitModel.StructuralKey` (`CircuitModel.cs:462`).

A capacitance value is not like Vgs: it is a **netlist element**, and `HarmonicaContext.Apply` only
re-elaborates when the structural key moves (`HarmonicaContext.cs:~150`). Leave it out and editing a
capacitance changes nothing at all until some unrelated structural edit happens to rebuild — a
silent no-op, the worst possible outcome. The cost is that editing a capacitance resets the frame
ladder, exactly as changing the DUT does. That is honest and correct; say so in the tooltip.

Make the key contribution stable and readable: `"Cgs=1.2e-12"` / `"Cgs=[1.2e-12,3e-14]"`, via
`HarmonicaNetlist.Num` so it never picks up a culture separator.

### 2.3 Persistence

`CharmIo`: add a `CharmCapacitance`-shaped object per capacitor under `CharmDut`, additive and
absent-means-`None`, following `CharmIo`'s own absent-means-default rule. **An untouched document
must still re-serialise byte-for-byte** — so write nothing at all for a `DutCapacitances.None`.
Round-trip test required.

### 2.4 Netlist emission

In `HarmonicaNetlist.Build`, after `sb.AppendLine(DutLine(...))`:

```
; linear, non-zero
C:CGS  n_g 0  C=1.2E-12

; nonlinear
NonlinearC:CDG  n_d n_g  C0=… C1=… C2=…
```

`ComponentModelFactory` reads `C0, C1, …` consecutively and stops at the first absent index
(`ComponentModelFactory.cs:~897`), so emit them densely from index 0 and let trailing zeros be
omitted. Every number through `HarmonicaNetlist.Num` (G17, invariant, never a space) — the generic
instance-line parser splits on whitespace and a comma or a space in a value silently becomes a net
name.

Emit **nothing** for an absent capacitor. A `C=0` element is not free: it is another node equation
and another stamp on every solve of a tool whose whole claim is frame rate.

### 2.5 One thing to check and report — `ComputeCharge`

`HarmonicaSettings.ComputeCharge` ("Whether the DUT's charge terms are evaluated") is persisted, is
in `StructuralKey`, and is editable from the Advanced Settings dialog — but a repo-wide grep finds
**no consumer**: nothing in `src/Harmonica`, `src/Engine` or `src/Core` reads it. If it were wired
up, turning it off would make a `NonlinearC` contribute nothing at all (it has no DC/conduction term
— `NonlinearCModel.Stamp` is empty by design) and this whole feature would silently do nothing.

Confirm the grep yourself. If it really is inert, **do not wire it up in this brief** — say so in
your report and record it in `src/Harmonica/RESOLVED.md`. If it is live, make sure the capacitance
rows state the dependency in their tooltip.

---

## 3. The strip: a Capacitance sub-chunk under Z0

### 3.1 Rows

`ReadoutStripView.SettingsColumnKeys` (~line 970) is a fixed 7-key list, rendered in order, ending in
`KeyZ0`. Append: a **spacer**, then `Cgs`, `Cdg`, `Cds`.

- Three new keys in `HarmonicaInputs` beside the others (`HarmonicaInputs.cs:91`):
  `KeyCgs = "dut.cgs"`, `KeyCdg = "dut.cdg"`, `KeyCds = "dut.cds"`.
- `HarmonicaInputs.Build` emits them (only for an SDD DUT), `Label` `"Cgs"` / `"Cdg"` / `"Cds"`,
  `Unit` `"pF"`, `Entry` `Number`.
- `HarmonicaInputs.Apply` parses the typed number as **pF** and stores Farads (`× 1e-12`). It must
  refuse a negative value with a stated message, and must refuse any edit at all while that capacitor
  is nonlinear (§3.4) — a rejected edit keeps the old value, which is this file's existing contract.
- The spacer: a zero-content row of fixed height in `UpdateSettingsColumn`'s build loop (6 px is
  right at the strip's density). It must not participate in the label `SharedSizeGroup` — an empty
  cell in a shared group is harmless, but a spacer that measures text is not a spacer.
- No section header. The Settings chunk has none today, and §7.5's standing rule for this strip is
  "no section titles, no decoration" — the spacer is what says "these are separate", which is exactly
  what the owner asked the spacer to do.

### 3.2 Value text

| state | value cell |
|---|---|
| absent | `0.00` |
| linear | `1.23` (2 decimals, always — `F2`, never shortest-form) |
| nonlinear | `1.23 (linearized)` |

The unit `pF` lives in the label (`Cgs (pF):`) per R7C §1.1. Two decimals fixed means the cell's width
never changes with the value, which is R7C's whole discipline — do not use a shortest-form format
here.

### 3.3 The linearized value

C(V_bias) = Σ Cₖ · V_biasᵏ — `NonlinearCModel.CapAt`'s own Horner, evaluated at the **DC** voltage
across that capacitor.

Read the bias from what is already published; do not re-solve and do not walk nodes by name (the
terminal node names change with the package — `Series` returns its input node when it emits nothing).
The capacitors are in parallel with the SDD's own ports, so:

- `V_Cgs = Re(V_intr[GatePort, 0])`
- `V_Cds = Re(V_intr[DrainPort, 0])`
- `V_Cdg = Re(V_intr[DrainPort, 0]) − Re(V_intr[GatePort, 0])` (matching §1's `{drain} {gate}` order)

`V_intr` is the `[port, harmonic]` cube `HarmonicaDataSet.Build` already publishes
(`HarmonicaDataSet.cs:145`); harmonic 0 is DC; `HarmonicaSolver.ReadComplex` already reads that shape
and returns NaN rather than zero when it cannot. Compute in `HarmonicaSolver` where `published` and
`ctx` are already in hand — **never in a view model** (§0.3 item 1).

When the intrinsic plane is not located, or nothing has been solved yet, show the C0 coefficient with
the suffix `(at V=0)` rather than a made-up number. Do not show a linearized value you could not
compute.

### 3.4 Editing and the row context menu

- Double-click inline edit (the existing `BuildSettingsColumnRow` `DoubleTapped` → `BeginInlineEdit`
  path) works for a linear or absent capacitor. For a nonlinear one it must not open at all — extend
  the row's guard beside the existing `SettingsRowMayBeOverwritten` check with a per-key
  "is this row editable right now" predicate, and give the row a tooltip saying why.
- Right-click on a capacitance row opens its **own** `ContextMenu`, set on the row `Grid`. A row that
  carries its own menu wins over the chunk-level Copy menu — Avalonia resolves `ContextRequested`
  against the nearest ancestor that has one, and `ReadoutStripView.axaml.cs:46` already documents
  this exact mechanism for the format flyout. Build it lazily on `Opening`, the pattern every other
  menu in this file already uses.
- Items (with icons, matching R7A §2.2's `MaterialIcon` convention if that brief has landed):

  | state | menu |
  |---|---|
  | absent or linear | `Use Nonlinear…` · separator · `Copy` |
  | nonlinear | `Edit Nonlinear C(V)…` · `Use Linear` · separator · `Copy` |

  `Use Nonlinear…` opens the editor (§4) seeded from the current linear value as `C0` — so switching
  modes starts from the capacitor the user already had, not from zero. `Use Linear` drops the
  coefficients back to `C0` as the linear value and states in the status line what it discarded.

---

## 4. Reusing the NonlinearC parameter editor

### 4.1 The trap

`NonlinearCvEditorViewModel` and `ParameterEditorViewModel` are both bound to a **schematic**: they
hold a `SchematicViewModel` and an `EditableComponent`, and they commit through
`_schematicVm.Execute(...)` onto the schematic's `UndoRedoStack` (see each file's own "Undo/Redo
delegates to the owning schematic's stack" section). harmonicaRF has no schematic, no
`EditableComponent`, and no undo stack.

### 4.2 The decision

**Host a detached one.** Add a single small class, `src/Ui/Harmonica/HarmonicaNonlinearCEditor.cs`,
that:

1. constructs a throwaway `SchematicViewModel` with one `EditableComponent` of
   `SymbolKind.NonlinearC`, seeded with `C0…Cn` from the `DutCapacitance`;
2. shows the existing `NonlinearCvEditorDialog` (or `ParameterEditorDialog`, whichever actually
   presents the C-V editing surface the owner means — check both) modally over the harmonicaRF
   window;
3. on OK, reads `C0, C1, …` back off that component in index order and returns
   `IReadOnlyList<double>?` — null when cancelled;
4. throws the host away.

One class knows about the throwaway host; nothing else does. `tests/Ui.Tests` already constructs
`SchematicViewModel` and its commands headlessly (see the test project's own csproj comment), so this
is a supported construction, not a hack against the grain — but **verify** it can be built without a
document/window in scope before committing to it, and if it cannot, report that rather than
half-building a second editor.

Undo inside the dialog then operates on the throwaway stack, which is correct: the dialog's edits are
staged and only reach the harmonicaRF document through `ApplyDut`-style write-back on OK. harmonicaRF
has no undo of its own; nothing regresses.

### 4.3 Write-back

On OK, write the coefficients into `DutSpec.Capacitances` and apply through the same structural path
`Set DUT…` uses (`HarmonicaViewModel.ApplyDut`) so the rebuild, the ladder reset and the fresh frame
all fall out of `StructuralKey` moving — never a second mechanism (R-h8-1).

---

## 5. The Poly-order control that does not render

### 5.1 What it is

`src/Ui/Views/Dialogs/NonlinearCvEditorView.axaml:81` — the `Fit order` `NumericUpDown` in the C-V
editor, reached from the Parameter Editor's `Edit CV Data…` button
(`ParameterEditorViewModel.ShowCvEditorButton`, line 131). That is the only poly-order control in the
NonlinearC surfaces; grep finds no other. Confirm against the owner's wording by opening both the
Parameter Editor and the C-V dialog before you change anything.

```xml
<NumericUpDown Value="{Binding FitOrder, Converter={x:Static cvt:NumericFieldConverter.Instance}}"
               Minimum="0" Maximum="20" Increment="1"
               FormatString="0" Width="70" FontSize="11" VerticalAlignment="Center"/>
```

### 5.2 The evidence

There are exactly two other `NumericUpDown`-heavy views in this repo and **both** style the control
before using it:

```xml
<!-- MarkerEditorView.axaml:47  and  PlotInspectorView.axaml:247 -->
<Style Selector="NumericUpDown">
    <Setter Property="FontSize"                 Value="…"/>
    <Setter Property="Height"                   Value="22"/>   <!-- 16 in PlotInspector -->
    <Setter Property="MinWidth"                 Value="10"/>
    <Setter Property="Padding"                  Value="4,1"/>
    <Setter Property="ShowButtonSpinner"        Value="False"/>
    <Setter Property="VerticalContentAlignment" Value="Center"/>
</Style>
```

The C-V editor's instance sets **none** of those: no `MinWidth`, no `Height`, spinner buttons left on,
and a hard `Width="70"` inside a 400 px dialog. The Fluent theme gives `NumericUpDown` a non-trivial
default `MinWidth`, and a `Width` smaller than the effective `MinWidth` does not shrink the control —
it produces a control wider than its slot, with the spinner buttons eating the text area. That is the
most likely cause and it is also the reason three previous attempts failed if they were spent
adjusting `Width`: `Width` is the property that cannot fix it.

### 5.3 What to do

1. Apply the known-good style above (as a `<UserControl.Styles>` entry in
   `NonlinearCvEditorView.axaml`, matching the two views that work — not as five inline attributes).
   Drop the hard `Width="70"`, or keep it *with* `MinWidth="10"`.
2. If that alone does not fix it, work down these in order and report which one it was:
   - the theme's `NumericUpDownMinWidth` resource vs the requested `Width`;
   - the binding has no `Mode=TwoWay` — check `NumericUpDown.ValueProperty`'s registered default
     binding mode in Avalonia 12.0.3, and set it explicitly if it is not TwoWay;
   - `NumericFieldConverter` (`src/Ui/Converters/NumericFieldConverter.cs`) converting `int FitOrder`
     ⇄ `decimal?`, combined with `FormatString="0"` — a blank box is what a failed `Convert` looks
     like;
   - `SizeToContent="Height"` on the dialog interacting with a control whose desired size exceeds its
     column.
3. Consider whether the spinner should be **on** here — it is a small integer the user nudges, which
   is the one case spinner buttons earn their space. If you keep them, give the control enough width
   for them (≈ 90–100 px) rather than 70.

**A screenshot before and after is mandatory.** This has been "fixed" three times; only a picture
closes it.

---

## 6. Out of scope

- No 2-D / bias-dependent C(V), no Cgd(Vgs,Vds). "1-D nonlinear" is the owner's own scope.
- Do not add capacitances to `LumpedPackage` — `Cpg`/`Cpd`/`CgdExt` are the *package* shunts and
  outside the DUT terminals. These are a different set at a different plane. Do not merge them.
- Do not change `NonlinearCModel`, the HB charge path, or `IntrinsicPlane`.
- Do not wire up `ComputeCharge` (§2.5).

---

## 7. Gates

1. `dotnet test tests/Ui.Tests --no-build`, `dotnet test tests/Harmonica.Tests --no-build`,
   `dotnet test tests/Core.Tests --no-build`, `dotnet test tests/Firewall.Tests --no-build` — each on
   its own invocation.
2. **A netlist gate**: `HarmonicaNetlist.Build` for each of (absent, linear, nonlinear) × (Cgs, Cdg,
   Cds) produces text that `CnlReader` + `Elaborator` accept; absent emits no line at all; the
   nonlinear line's coefficients survive to a `NonlinearCModel` with the same `C0…Cn`.
3. **A physics gate — this is the one that proves the feature does what it is for.** Solve one frame
   with `Cgd = 0` and one with `Cgd = 0.5 pF`, everything else identical, and assert:
   - `Γ_intr` at the fundamental **moves** (the intrinsic glyph rotates) while the extrinsic marker
     Γ is unchanged — this is the "contour rotation / intrinsic glyph" claim, and it is the whole
     justification for the feature;
   - `Zin` at the operating point moves (feedback capacitance is visible at the input);
   - with all three capacitances absent, every published number is **bit-identical** to today's — an
     untouched document must not move by so much as an LSB. Run this one against a saved fixture.
4. A `.charm` round-trip test for each of the three states, including the byte-for-byte
   no-capacitance case (§2.3).
5. **Run the app** (`/run`). Screenshots: the Settings column showing Z0, the spacer, and the three
   capacitance rows in each of the three states (`0.00`, `1.23`, `1.23 (linearized)`); the row's
   right-click menu in both variants; the C-V editor open from that menu; and **before/after** of the
   Fit-order control from §5.
6. Report: whether `ComputeCharge` is inert (§2.5); whether the detached `SchematicViewModel` host
   (§4.2) worked or needed something else; and which of §5.3's causes the render bug actually was.
