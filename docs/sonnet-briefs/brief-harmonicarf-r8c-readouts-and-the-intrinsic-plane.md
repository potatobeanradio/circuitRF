# Brief — harmonicaRF R8C: the readouts, rgs, and the end of the intrinsic solve

**Read first, in this order:**
`src/Harmonica/HarmonicaTitles.cs` (all 58 lines — `MxHeaderRow` is §1's subject),
`src/Ui/Harmonica/HarmonicaSolver.cs:680–850` (`BuildReadouts`'s termination/MXP/MXE section,
`AddMxColumn`, `AddIntrinsicColumn`, `ReadComplex`) and `:850–930` (`AddGammaRow`, `GammaFactor`),
`src/Ui/Harmonica/HarmonicaFrame.cs:100–135` (`HarmonicaReadout`) and `:200–216` (`SmithOptimum`),
`src/Ui/Harmonica/HarmonicaReadoutFormatting.cs:30–115` and `:180–200` (`FormatGammaFactor`),
`src/Ui/Views/Harmonica/ReadoutStripView.axaml.cs:520–600` (`BuildColumnRowShell`, `UpdateColumnRow`)
and `:105–135` (the row-text extraction used by the copy path),
`src/Ui/Harmonica/HarmonicaInputs.cs:100–265` (`KeyCgs`…, `CapacitanceRow`) and `:400–570`
(the probe/apply paths),
`src/Harmonica/CircuitModel.cs:80–170` (`DutSpec.Capacitances`, `DutCapacitance`, `LumpedPackage`
— note `CouplesInputAndOutput`),
`src/Harmonica/HarmonicaNetlist.cs:155–200` and `:255–285` (`AppendCapacitance`),
`src/Harmonica/HarmonicaDataSet.cs:44–105` (`Intrinsic` — the ONE definition),
`src/Harmonica/IntrinsicPlane.cs:240–320` (`SourceImpedance`, and why it is not a V/I ratio),
`src/Harmonica/InverseSolve.cs` (its header, then skim),
`src/Ui/Harmonica/HarmonicaViewModel.cs:1716–1890` (`BeginIntrinsicDrag`, `DragIntrinsicGlyph`,
`RequestInverseFrame`),
`src/Ui/Harmonica/Renderers/HarmonicaPanelRenderer.cs:729–782` (`DrawIntrinsicGlyphs`) and `:795–812`
(`DrawMarkers`'s own radius).

**Do NOT update any `CLAUDE.md`.** `src/Ui/RESOLVED.md` and `src/Harmonica/RESOLVED.md` only.
**No screenshot verification.**

Tag new comments `R8C §n`.

> **This brief overlaps R8B §2** (the extrinsic marker comes off the compressed radial scale). §4 here
> deliberately does **not** touch `IntrinsicGlyphScale` — the intrinsic glyph keeps its compression.
> If both briefs are being implemented, do R8B §2 first; nothing here depends on it, but the glyph
> sizing in §4.1 is stated relative to the marker's rendered radius and that radius is unchanged
> either way.

---

## 1. MXP and MXE headers carry their impedance

### 1.1 What ships

`HarmonicaTitles.MxHeaderRow` today produces `"MXP 1f0 Load"` / `"MXE 2f0 Source"`. The owner wants the
optimum's **actual impedance** in the header, named by the termination it corresponds to:

```
MXP 1f0 ZL1=12.500-j3.200 Ω
MXE 2f0 ZS2=8.100+j45.000 Ω
```

Naming: `Z` + side letter (`L` for Load, `S` for Source) + the harmonic index — the *same* `Z{m.Name}`
spelling the Source/Load termination rows already use (`HarmonicaSolver` builds those as
`$"Z{m.Name}"` from `sideLetter` + `m.Band`). One convention, so the MXP header and the Load column's
`ZL1` row are visibly about the same quantity.

### 1.2 It must be the REAL optimum impedance, not the marker's

The owner says so explicitly: *"Be sure use the real MXP and MXE impedance locations."* The value is
`HarmonicaDataSet.ImpedanceOf(optimum.Gamma, z0)` — `SmithPanelData.SmithOptimum.Gamma` is the
interpolated argmax on that panel's own fitted surface (`HarmonicaFrame.cs:209–211`). It is **not**
`marker.Gamma`, and it is not read from the published `DataSet`.

Signature change:

```csharp
public static string MxHeaderRow(string label, TerminationSide side, int harmonic,
                                 Complex? optimumGamma, double z0, ReadoutFormat format)
```

`HarmonicaTitles` is framework-free by design (its own header says so) — it may take `Complex` and a
`ReadoutFormat` and call `HarmonicaReadoutFormatting`… **except it cannot**: `HarmonicaTitles` lives in
`src/Harmonica` and `HarmonicaReadoutFormatting` lives in `src/Ui/Harmonica`. Do not add that
reference. Instead have `MxHeaderRow` take the **already-formatted** impedance string:

```csharp
/// <param name="zText">The optimum's impedance, already formatted by the caller
/// (HarmonicaReadoutFormatting.FormatZ, which is in src/Ui and must stay there). Null or empty
/// keeps the old plane-only header — the "no optimum" case.</param>
public static string MxHeaderRow(string label, TerminationSide side, int harmonic, string? zText)
    => zText is { Length: > 0 }
        ? $"{label} {harmonic}f0 Z{(side == TerminationSide.Source ? "S" : "L")}{harmonic}={zText}"
        : $"{label} {harmonic}f0 {(side == TerminationSide.Source ? "Source" : "Load")}";
```

`FormatZ` already appends `" Ω"`, so the example above comes out exactly as the owner wrote it.

### 1.3 The row shape must not change between the two branches

`AddMxColumn` has two branches and **R7C §1.4 requires them to emit the same rows** — the comment at
`HarmonicaSolver.cs:774–786` explains why (a column that collapses to one row on a degraded frame and
re-expands on the next is structural churn at frame rate during every drag). Keep that. The `no
optimum` branch calls `MxHeaderRow(label, side, harmonic, zText: null)` and appends `" — no optimum"`
exactly as today; the solved branch passes the formatted Z. Only the header string differs.

Which format? Use the column's own saved format key, through the resolver already threaded in:
`format($"{label}.MxZ")` → `HarmonicaReadoutFormatting.FormatZ(zOpt, ...)`. That gives the header a
real/imaginary ⇄ magnitude/angle right-click for free **if** the header row gets a format menu — it
does not today (headers are `Value.Length == 0 && Tooltip.Length == 0` and return early from
`BuildColumnRowShell`). Leave it read-only; just resolve through the same key so it matches whatever
the column's `Zin` row is showing rather than being independently spelled.

### 1.4 "Make the read selectable text"

Header rows are built as a plain `TextBlock` (`ReadoutStripView.axaml.cs:538`) and returned early
(`:541–548`), so a header is the one thing in the strip a user **cannot** select and copy — which is
precisely why it matters now that it carries a number.

Change the header path to `SelectableTextBlock`. Two things make this safe and one makes it necessary
to check:

- `UpdateColumnRow` matches `row.Children[0] is TextBlock label` (`:597`). `SelectableTextBlock`
  derives from `TextBlock`, so the match still holds — **do not** add a second branch.
- The strip's row-text extraction already handles it, and already knows the ordering trap:
  *"SelectableTextBlock derives from TextBlock, so it must be matched FIRST"* (`:125–126`). Nothing to
  do.
- The one hazard is the one R-h9r2-15 recorded (`:550–553`): `SelectableTextBlock` eats a double-tap as
  select-a-word before `DoubleTapped` fires. A header has no inline editor and no `DoubleTapped`
  handler, so there is nothing to eat. Say so in the comment, or a later reader will "fix" it back.

### 1.5 Test

`HarmonicaMxHeaderTests` (routine): `MxHeaderRow("MXP", Load, 1, "12.500-j3.200 Ω")` →
`"MXP 1f0 ZL1=12.500-j3.200 Ω"`; `("MXE", Source, 2, "…")` → `"MXE 2f0 ZS2=…"`; `zText: null` → the
old `"MXP 1f0 Load"` string, character for character (the no-optimum branch's contract). Then a
`HarmonicaSolver`-level test: build a frame with a known optimum Γ and assert the emitted header
contains `ImpedanceOf(optimumGamma, z0)`'s formatting and **not** the corresponding marker's.

---

## 2. γ's phase below the noise floor

> "If |γ| is < 1e-3, then the γ phase is basically in the noise, so report the phase in the display
> readout as '-'."

`FormatGammaFactor` (`HarmonicaReadoutFormatting.cs:185–187`) is the single place γ is rendered
(`AddGammaRow` is its only caller, from three chunks). It becomes:

```csharp
/// <summary>R8C §2 — below GammaPhaseNoiseFloor the angle is numerical noise: γ = V₂·conj(V₁)²/|V₁|³
/// divides by |V₁|³, so a vanishing 2nd harmonic leaves an angle that swings freely with the last
/// bits of V₂. The MAGNITUDE is still real information and is still shown; only the angle is
/// replaced by "—", the same em-dash every other unavailable value in this strip uses.</summary>
public const double GammaPhaseNoiseFloor = 1e-3;

public static string FormatGammaFactor(Complex g)
{
    if (double.IsNaN(g.Real) || double.IsNaN(g.Imaginary)) return "—";
    if (g.Magnitude < GammaPhaseNoiseFloor)
        return FixedWidth(g.Magnitude, ComplexMagDecimals, ComplexMagBudget) + "∠—";
    return FormatComplex(g, ReadoutFormat.MagnitudeAngle);
}
```

Two constraints on the exact string:

- **Keep the `∠`.** `FormatComplex(..., MagnitudeAngle)` produces `mag∠angle°`; dropping the separator
  would make a suppressed phase look like a different *kind* of row. Match the mag/angle shape, replace
  only the angle token.
- **Do not widen the column.** `"∠—"` is shorter than `"∠-180.0°"`, so the reserved value width
  (R7C §1.3's `ValueChars` machinery) is unaffected. Confirm γ's own entry in that table still bounds
  the new string; if γ has no entry, it takes the complex default, which is wider still.

`SplitUnit`'s whitelist is untouched — a γ row carries no unit suffix and never did (`:103`).

Test in `HarmonicaReadoutFormattingTests` (or the nearest existing file): magnitude 5e-4 with angle
137° renders with `∠—`; magnitude 1.1e-3 renders the real angle; magnitude exactly 1e-3 renders the
real angle (the comparison is `<`, stated as a test so the boundary is not re-guessed); NaN still
renders bare `"—"`.

---

## 3. rgs — a series resistance in the gate branch

> "A series rgs resistor is needed (in series with Cgs) to make the FET more realistic. Add an rgs
> parameter (default = 0 ohms) to the display readout (above Cgs). Make sure that rgs is accounted for
> when calculating the intrinsic source impedances. Make sure rgs has inline text editor capability."

### 3.1 Where it lives in the model

`DutCapacitances` (`CircuitModel.cs:125–133`) gains a sibling — **not** a member of `DutCapacitance`,
because rgs belongs to the Cgs *branch*, not to the capacitor:

```csharp
public sealed record DutCapacitances
{
    public DutCapacitance Cgs { get; init; } = DutCapacitance.None;
    public DutCapacitance Cdg { get; init; } = DutCapacitance.None;
    public DutCapacitance Cds { get; init; } = DutCapacitance.None;

    /// <summary>R8C §3 — ohms, in SERIES with Cgs between the gate terminal and the source
    /// terminal. 0 (the default) emits nothing at all, so an existing document is bit-identical.</summary>
    public double RgsOhms { get; init; }

    public bool IsIdentity => Cgs.IsAbsent && Cdg.IsAbsent && Cds.IsAbsent;   // ← see §3.2
}
```

`IsIdentity` deliberately **does not** consult `RgsOhms`: a non-zero rgs with an absent Cgs is an
open branch and emits nothing (§3.2). Comment that, or someone adds `&& RgsOhms == 0` and gets a
netlist section emitted for a resistor in series with nothing.

`CircuitModel.StructuralKey` (`:530–535`) must gain `RgsOhms` — it is a netlist element and a change to
it invalidates every cached context. Use `HarmonicaNetlist.Num` like the neighbouring parts.

`CharmIo` gains a nullable `double? Rgs` on the settings DTO with `?? 0.0` on read, mirroring how the
capacitance fields are handled. An older `.charm` loads as 0 — no migration.

### 3.2 The netlist

`HarmonicaNetlist` (`:167–181`), inside the existing `DutKind.Sdd` block:

```csharp
// R8C §3 — rgs in SERIES with Cgs. Zero emits nothing and Cgs keeps its direct gate connection,
// so a document that never set one is byte-identical (the same rule Shunt() already follows).
if (!caps.Cgs.IsAbsent && caps.RgsOhms != 0.0)
{
    sb.AppendLine($"R:RGS  {gate} n_rgs  R={Num(caps.RgsOhms)}");
    AppendCapacitance(sb, "CGS", "n_rgs", sourceTerminal, caps.Cgs);
}
else
{
    AppendCapacitance(sb, "CGS", gate, sourceTerminal, caps.Cgs);
}
```

`CDG` and `CDS` are unchanged, and `CDG`'s `{drain} {gate}` net order stays load-bearing — do not touch
that line (its comment explains why the order flips every odd coefficient's sign).

**The intrinsic plane does not move.** `IntrinsicPortMap` locates the SDD's *own* ports; `n_rgs` is an
internal node of a branch that sits in **parallel** with the SDD gate port, not between the SDD and the
gate. Confirm this by running the existing intrinsic-plane tests with a non-zero rgs and asserting
`ctx.IntrinsicPorts.GatePort` is unchanged — a shifted intrinsic plane would silently relabel every
intrinsic reading.

### 3.3 The intrinsic source impedance picks it up for free — verify that it does

`IntrinsicPlane.SourceImpedance` (`:250–300`) builds `Z_S,intr = (J′⁻¹)_gg` from the **converged
Jacobian of the whole network** and then removes the DUT's own gate-port self block. There is no
hand-written expression for the gate branch anywhere in it. So an extra series resistor in that branch
enters through the MNA stamp and the answer changes with no code change.

**That is a claim, and the owner's item asks for it specifically, so gate it rather than assert it:**
`IntrinsicRgsTests`, one Tier-1 fixture (linear Cgs, no package, no Cdg — i.e.
`LumpedPackage.None`, `Cdg.IsAbsent`), where the closed form is available by hand:

```
Z_S,intr = Z_source ∥ (rgs + 1/(jωCgs))
```

Assert the solver's `Intrinsic(...).Z[Source, 1]` matches that expression to 1e-9 for
rgs ∈ {0, 2, 25} Ω and Cgs = 1 pF at the document frequency, and that rgs = 0 reproduces the
pre-change value exactly. **Hand-derive the oracle; do not compute it with another circuitRF path** —
that is this repo's standing rule for a numeric gate and the reason several earlier rounds' oracles
were caught being wrong before the solver was.

### 3.4 The readout row

`HarmonicaInputs`: new `public const string KeyRgs = "dut.rgs";` and, in the `DutKind.Sdd` block
(`:211–216`), **above** the Cgs row:

```csharp
list.Add(Make(model, KeyRgs, "rgs", Num(caps.RgsOhms), "Ω",
     "Series resistance in the gate branch, between the gate terminal and Cgs. 0 means none — " +
     "no element is emitted at all. Affects the intrinsic source impedance.",
     HarmonicaInputEntry.Number));
list.Add(CapacitanceRow(KeyCgs, "Cgs", caps.Cgs, linearizedCgsFarads));
```

Use `Make`, **not** `CapacitanceRow` — rgs has no nonlinear form, no `Locked` state and no `(linearized)`
text, and reusing the capacitance builder would drag all three in. `Make`'s probe-based `IsStructural`
will correctly report true (rgs is a netlist element and `Apply` accepts it), so unlike
`CapacitanceRow` this one does **not** need to bypass the probe.

Then wire the two switches `CapacitanceRow`'s keys already appear in:

- the structural probe's value perturbation (`:425–427` style): `KeyRgs => Num(caps.RgsOhms + 1.0)`.
- `Apply` (`:524+`): parse ohms, reject negative and non-finite with the file's existing
  reject-and-keep convention, write `caps with { RgsOhms = v }`.

Inline editing comes free from `HarmonicaInputEntry.Number` + `Structural: true` + a non-null
`EditText` — that is the whole contract `ReadoutStripView`'s settings-row editor reads
(`BuildSettingsColumnRow` / `SettingsRowMayBeOverwritten`). Do **not** set `Locked`.

Test (`HarmonicaInputsRgsTests`): the settings list contains `dut.rgs` immediately before `dut.cgs`
for an SDD DUT and contains neither for a `NativeFet` (the SDD-only rule at `:207–210` is unchanged and
must stay — a native FET carries its own gate charge via `CapModel` and would double-count);
`Apply(KeyRgs, "25")` yields `RgsOhms == 25`; `Apply(KeyRgs, "-1")` is refused and leaves the model
untouched; the row's `EditText` is non-null and `Locked` is false.

---

## 4. The intrinsic glyphs

Both changes are in `HarmonicaPanelRenderer.DrawIntrinsicGlyphs` (`:729–782`).

### 4.1 Size — 0.9× the marker's rendered radius

Today the two are computed independently and are not in any stated ratio:

```csharp
DrawMarkers:          float r = min(W,H) * 0.020;  r = Math.Max(6f,   r);   // :797
DrawIntrinsicGlyphs:  float s = min(W,H) * 0.012;  s = Math.Max(3.5f, s);   // :735
```

0.012 / 0.020 = **0.6**. The owner wants 0.9. Derive it rather than typing a second magic number, so
they can never drift again:

```csharp
/// <summary>R8C §4.1 — the intrinsic glyph is 0.9× the termination marker's rendered radius:
/// clearly secondary, but no longer half the size. DERIVED from the marker's own constants so the
/// two cannot drift apart.</summary>
internal const double MarkerRadiusFraction   = 0.020;   // hoisted out of DrawMarkers
internal const float  MarkerRadiusFloorPx    = 6f;      // hoisted out of DrawMarkers
internal const double IntrinsicGlyphScaleOfMarker = 0.9;

float s = (float)(MarkerRadius(size) * IntrinsicGlyphScaleOfMarker);
```

with `MarkerRadius(size)` the single helper both call. The floor comes along scaled
(6 × 0.9 = 5.4 px), replacing the free-standing 3.5f.

The triangle's own proportions (`0.9f` and `0.75f` on the vertices, `:762–765`) are **shape**, not
size — leave them alone. Note the unfortunate collision: that literal `0.9f` in the path is unrelated
to `IntrinsicGlyphScaleOfMarker`'s 0.9; comment it so nobody folds them together.

### 4.2 Opacity — fully visible

```csharp
using var fill = new SKPaint { Color = c.WithAlpha(190), IsAntialias = true };   // → c, no WithAlpha
```

That is the whole change: `190/255` → opaque.

**Do not also remove the desaturation.** `Desaturate(band, theme.Background, 0.45)` (`:757`,
`:783–787`) is a separate mechanism — it keeps the glyph's band colour identifiable while pulling it
toward the background so it reads as secondary to its marker. The owner asked for transparency to go,
not for the colour to change. If the result is now too close to the marker's own colour to
distinguish, say so in your report with the two resolved colours; do not tune 0.45 on your own.

The dashed compressed-annulus outline already draws at `WithAlpha(255)` (`:773`) and is unchanged.

### 4.3 Test

`IntrinsicGlyphSizeTests` (routine, pure): for panel sizes 200, 600 and 1200 px square,
`MarkerRadius(size) * 0.9` equals the glyph half-size the renderer computes, and both respect their
floors at the smallest size. A source scan asserting `DrawIntrinsicGlyphs` contains no
`WithAlpha(190)` closes §4.2 — the renderer cannot be exercised headlessly.

---

## 5. The intrinsic drag stops solving

> "The intrinsic node solve is taking too long. Keep the code but let's stop using it. For now the
> intrinsic node glyph is no longer controlled by user using a mouse drag in the presence of nonlinear
> capacitance or feedback. The only time user can drag and move it is when linear capacitors are used
> and there is no feedback. Feedback can include feedback capacitance or mutual inductance between
> input and output. Use ABCD to back-calculate what marker impedance is needed when user drags
> intrinsic glyphs — the markers must update live. Of course, this ABCD back calculation is not used
> when DUT has nonlinear capacitance or feedback because moving an intrinsic marker is not allowed in
> that scenario."

### 5.1 What is being retired, and what is NOT

`InverseSolver` (`src/Harmonica/InverseSolve.cs`), `Reachability`, and
`HarmonicaViewModel.RequestInverseFrame` **stay in the tree, compiling and tested**. The owner said
"keep the code". What changes is that nothing calls `BeginIntrinsicDrag`/`DragIntrinsicGlyph` any
more — the drag is served by §5.3's closed form, and the inverse path becomes reachable only from its
own tests.

Leave `InverseSolveCostTests` and `HarmonicaDragCostTests` (both `Category=Benchmark`) alone; they are
what keeps the retired code honest if it is ever re-enabled.

### 5.2 The predicate: when is an intrinsic drag allowed

One place, one name, in `src/Harmonica/` next to the model so the engine and the UI read the same
answer:

```csharp
/// <summary>R8C §5.2 — whether an intrinsic glyph may be dragged. True only when the intrinsic
/// plane is separated from each terminal by a LINEAR, UNILATERAL two-port, which is exactly the
/// condition under which §5.3's ABCD inversion is exact rather than approximate.</summary>
public static bool IntrinsicDragAllowed(CircuitModel m, out string reason)
```

The conditions, each with the field that decides it:

| condition | test | why |
|---|---|---|
| DUT is an SDD | `m.Dut.Kind == DutKind.Sdd` | the Cgs/Cdg/Cds branch only exists for an SDD (`HarmonicaNetlist:167`); a `NativeFet` carries gate charge inside `CapModel` and an `External` model carries parasitics we cannot see, so no ABCD chain can be written |
| no nonlinear C | `!Cgs.IsNonlinear && !Cdg.IsNonlinear && !Cds.IsNonlinear` | a nonlinear capacitor makes the embedding a conversion matrix, not a 2×2 ABCD — harmonics couple and the per-band inversion is wrong, not merely inaccurate |
| no feedback capacitance | `m.Dut.Capacitances.Cdg.IsAbsent` | Cdg is the DUT's own gate–drain path; with it the input and output halves are one four-port |
| no package coupling | `!m.Package.CouplesInputAndOutput` | **this predicate already exists** (`CircuitModel.cs:160–167`) and is already documented as *"exactly the condition under which `Z_S,intr` departs from the passive source network"* — `Rs != 0 \|\| Ls != 0 \|\| CgdExt != 0`, i.e. a shared source lead or an external gate-drain feedback cap |
| charge is being computed | `m.Settings.ComputeCharge` | with charge off the glyph coincides with its marker (§4.5 consequence 1); a drag is a no-op, not an error — allow it, but say so in `reason` if it surprises |

**Mutual inductance between input and output is not representable in this model.** `LumpedPackage`
carries `Lg`, `Ld`, `Ls` and no coupling coefficient; `Ls` (the shared source lead) is the only
input–output inductive path and it is already covered by `CouplesInputAndOutput`. Say that in the doc
comment rather than adding a field nothing can set — the owner named it as a category, and the honest
answer is that the one representable member of it is already handled.

When the predicate is false:

- `HarmonicaHitTest.Resolve`'s **Pass 2 does not run** — the intrinsic glyph is not grabbable at all, so
  a click falls through to the VSWR circle / grid point / panel body as if the glyph were decoration.
  Gate the pass, not the drag: a grab that starts and then refuses to move is worse than no grab.
- The glyph still **renders**, still tracks the solve, and its tooltip/status text says why it is
  fixed — `reason`, surfaced through the existing `InverseMessage` channel
  (`HarmonicaViewModel.cs:1741`), which the status strip already shows.

### 5.3 The ABCD back-calculation

**Why it is exact under §5.2's predicate.** With every capacitor linear and no input–output coupling,
the network between the intrinsic gate port and the source termination plane is a fixed, passive,
linear two-port — and likewise between the intrinsic drain port and the load plane, independently.
Then at each harmonic *h*:

```
Z_intr = (A·Z_ext + B) / (C·Z_ext + D)
```

a bilinear (Möbius) map, whose inverse is another bilinear map:

```
Z_ext = (D·Z_intr − B) / (−C·Z_intr + A)
```

so "put the glyph here" has a **closed-form, per-band, per-side** answer with no HB solve, no Jacobian,
no iteration — which is what makes it live at frame rate.

This is not a new claim about the physics: `IntrinsicPlane`'s own header already records that the load
side is a V/I ratio into the passive network, and `SourceImpedance`'s comment already records that
`Z_S,intr` equals the passive source network exactly when there is no input–output coupling (§4.5.3(a),
quoted in `CouplesInputAndOutput`'s doc). §5.2's predicate is that condition, spelled as fields.

**The chains.** New file `src/Harmonica/IntrinsicAbcd.cs`. Source side, from the extrinsic source plane
inward at angular frequency ω = 2π·f·h:

1. series `Rg + jωLg`
2. shunt `jωCpg`
3. shunt branch `rgs + 1/(jωCgs)` — **§3's rgs enters here, and this is the second half of "make sure
   rgs is accounted for when calculating the intrinsic source impedances"**

Load side, from the extrinsic load plane inward:

1. series `Rd + jωLd`
2. shunt `jωCpd`
3. shunt `jωCds`

Build each as a product of 2×2 `Complex` matrices with the two standard primitives (`Series(Z)` =
`[[1,Z],[0,1]]`, `Shunt(Y)` = `[[1,0],[Y,1]]`) — one helper each, no hand-multiplied closed form, so a
reader can check the chain against the netlist line by line. `Cdg` and `Rs`/`Ls`/`CgdExt` never appear:
the predicate guarantees they are zero, and **assert that** at the top of the builder rather than
silently ignoring them.

**Wiring the drag.** Replace `BeginIntrinsicDrag`/`DragIntrinsicGlyph`'s bodies (do not delete the
methods — `HarmonicaPointer` calls them and the names are right):

```csharp
public void DragIntrinsicGlyph(HarmonicaMarker marker, Complex targetIntrinsicGamma, bool dragging)
{
    // R8C §5.3 — closed form, on the UI thread, no solve. IntrinsicDragAllowed is checked at
    // GRAB time (HarmonicaHitTest Pass 2); this is a hard assert, not a re-check.
    var zIntr = HarmonicaDataSet.ImpedanceOf(targetIntrinsicGamma, Model.Settings.Z0);
    var zExt  = IntrinsicAbcd.ExtrinsicFor(Model, marker.Side, marker.Band, zIntr);
    SetMarkerImpedance(marker, zExt);          // markers update LIVE — this is the owner's own line
    RequestScheduledFrame(dragging);           // the forward frame, exactly as an extrinsic drag does
}
```

`BeginIntrinsicDrag` becomes a no-op that clears `InverseMessage`; `EndIntrinsicDrag` likewise.
`_inverse`, `_inverseBands`, `_inverseTargets`, `_inverseMarker` and `RequestInverseFrame` stay,
unreferenced from the drag path (mark them so, or the next reader deletes them as dead code and
contradicts "keep the code").

**The reachable-region shading goes with it.** `ShowReachableRegion` sampling costs ~53 ms per drag
(`HarmonicaViewModel.cs:1731`) and exists to say which intrinsic targets the *inverse solve* can reach.
Under a closed-form inversion every target is reachable except at the map's own pole
(`C·Z_intr = A`), so the shading is answering a question that no longer has an interesting answer.
Default `ShowReachableRegion` to **false** and leave the property, the sampler and
`DrawReachableRegion` in place.

**The pole.** `−C·Z_intr + A → 0` is a genuine singularity: the requested intrinsic impedance is not
producible by any finite extrinsic termination. Refuse that frame — leave the marker exactly where it
was and set `InverseMessage` to a stated reason. R-h6-9's rule ("a failed solve moves NOTHING, no
partial application") is the right precedent and is already the user-visible behaviour; keep it.

### 5.4 Tests

`IntrinsicAbcdTests`, routine tier, and the numbers must come from **hand-derived** expressions, not
from another circuitRF path:

1. **Identity.** `LumpedPackage.None`, all three caps absent, rgs = 0 → the chain is the identity
   matrix and `ExtrinsicFor(z) == z` for every z. This is the degenerate case the shipped default
   document sits at and it must be exact, not approximate.
2. **Source, one element at a time.** Cgs only: `Z_intr = Z_ext ∥ 1/(jωCgs)` → invert and compare.
   Then rgs + Cgs: `Z_intr = Z_ext ∥ (rgs + 1/(jωCgs))`. Then add `Rg`, then `Lg`, then `Cpg`,
   checking each addition against its own hand expression. Building the chain and testing only the
   assembled result is how a transposed `Shunt`/`Series` survives.
3. **Round trip against the real solver.** On a Tier-1 fixture satisfying §5.2, drag a glyph to a
   target Γ_intr, apply `ExtrinsicFor`, run the ordinary forward solve, and read
   `HarmonicaDataSet.Intrinsic(...).Gamma[side, band]` back — it must equal the target to 1e-9.
   **This is the gate that matters**: it proves the ABCD chain and the netlist agree about what the
   circuit is. If it fails, the chain is wrong, not the solver.
4. **Both sides are independent.** Under the predicate, changing the load termination must not move
   `Γ_S,intr` at all. Assert bit-equality, not a tolerance — that independence is the predicate's whole
   content, and a tolerance would hide a small real coupling.
5. **The predicate itself.** `IntrinsicDragAllowed` is false for: a nonlinear Cgs; a non-absent Cdg;
   `Rs != 0`; `Ls != 0`; `CgdExt != 0`; `DutKind.NativeFet`; `DutKind.External`. True for the shipped
   default document — **check that one explicitly**, because H6's own brief recorded that the shipped
   default cannot exercise an intrinsic drag at all, and if that is still true this whole section ships
   untested against a real user gesture. If it is still true, say so in your report and name a fixture
   that does exercise it.

---

## 6. Gates

```
dotnet build
dotnet test tests/Harmonica.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Engine.Tests --no-build     # §3 touches the netlist; run it once at the end
```

Everything added here is routine tier — nothing approaches the ~5 s `Category=Benchmark` threshold.
`Engine.Tests` is ~3 min 24 s on its own; run it once, at the end, not per iteration.

Expect churn in `HarmonicaReadoutColumnsTests`, `HarmonicaR6cStripTests` and
`HarmonicaReadoutFormatRepaintTests` (§1 and §2 change header and γ text) and in
`HarmonicaAddedGridPointsTests` / `HarmonicaDragTests` (§5 changes what an intrinsic drag does). Update
their expectations — do not weaken their assertions to "contains".

**Report explicitly:**
- §3.3's measured `Z_S,intr` at rgs = 0, 2 and 25 Ω against the hand oracle.
- §5.4 item 3's round-trip residual, as a number.
- §5.4 item 5: whether the shipped default document can actually exercise an intrinsic drag under the
  new predicate, and if not, which fixture you used instead.
