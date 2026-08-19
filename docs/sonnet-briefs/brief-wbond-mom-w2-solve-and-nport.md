# Sonnet Brief — wBond MoM WM-2: the solve, the N-port, and the analytic cross-check

**Design:** `docs/design/mom-wirebond-kernel.md` §4.1, §8, §11. **Prerequisite: WM-1
(`brief-wbond-mom-w1-mesh-and-matrices.md`) is landed and green.** This brief turns WM-1's
frequency-independent matrices into an N-port, publishes it through the export path the analytic
model already uses, and runs the owner's requested sanity check against that analytic model.

**Where findings go: `src/WBond/Mom/RESOLVED.md`** (WM-1 created it), plus a short entry in
`src/Ui/RESOLVED.md` for the export change. **Do not write in any `CLAUDE.md`.**

---

## Gate command

```
dotnet test tests/WBond.Tests    --no-build
dotnet test tests/Ui.Tests       --no-build
dotnet test tests/Firewall.Tests --no-build
```

Separate commands (`MSB1008`). `Ui.Tests` is ~27 s and is in the gate because §7 touches
`src/Ui/WBond/WBondTouchstoneExport.cs`. **You do not need `Engine.Tests`** (~3 min 24 s) — nothing in
this brief can reach it. If you think you do, you have exceeded scope; stop and report.

### Test-cost discipline

- **Every routine test in this brief uses `N_s ≤ 400` and `≤ 8` frequency points.** A dense complex
  LU at `N_s = 400` is ~4×10⁷ flops — microseconds. Eight of them is nothing. There is no excuse for
  a slow routine test here.
- The convergence test of §6.5 is the largest routine test in the brief: 8 wires × {12, 24, 48}
  segments × 3 frequencies. `N_s` tops out at 384. Keep it there.
- **The correlation study (§6.6) is a routine test too** — 4 wires, 24 segments, 7 frequencies. It
  prints a table; it does not sweep 201 points.
- Anything you measure at or above ~5 s gets `[Trait("Category","Benchmark")]` and is **measured
  alone** before you tag it. In this brief that should be *only* the 201-point sweep cost measurement
  of §6.8, if you write it at all — WM-3 is where sweep cost properly belongs, and it is fine to
  leave §6.8 to WM-3 and say so.

---

## 0. What is being built

Three things, in order of importance:

1. **`WireMomSolver`** — `Z_port(ω)` and `Y_port(ω)` for a meshed design, one dense complex
   factorisation per frequency and nothing else.
2. **The cross-check** the owner asked for: the MoM answer against `src/WBond`'s existing analytic
   (lumped, one-basis-function-per-wire) model. One part of that comparison is an **exact identity**
   and must hold to solver precision; the rest is a **correlation** that is expected to degrade with
   frequency and whose job is to be *recorded*, not to pass a tight bound.
3. **A UI entry point the owner can drive** — two surfaces, both small:
   - the existing Touchstone export gains a **Model** option (lumped / distributed), so a `.snp` from
     either engine can be plotted in Data Display;
   - a new **Design → Compare Distributed Model…** dialog in the wBond editor that shows the mesh
     report *before* solving, runs both models on a frequency grid, and puts the comparison of §6.6
     on screen as a table you can copy. **This is the surface this brief is judged on** — the
     numbers in §6.6 are the answer to the owner's question, and a number that only a test has ever
     seen is not delivered.

### 0.1 Three things that are true before you start

1. **The MoM answer and the analytic answer are already in the same basis, by construction.**
   `WBondTouchstoneExport.TerminalAdmittances` publishes a `2M × 2M` admittance with every terminal
   referenced to the ground plane at z = 0 — Touchstone's own implicit common reference node — in the
   order `G1.i, G1.o, G2.i, G2.o, …`. WM-1's terminal shorting produces **exactly that basis, that
   reference and that order**. So the comparison is a matrix-to-matrix subtraction with no
   renormalisation, no port re-mapping and no cascading. That was a deliberate WM-1 decision; do not
   spend effort re-deriving a translation layer.

2. **The analytic model is not a lower-fidelity version of the MoM model — it is the MoM model with
   two basis functions per wire instead of `2n`.** Uniform axial current, uniform charge per unit
   length. That tells you precisely where they must agree and where they must not:
   - **Current.** At low frequency the current on a bond wire *is* uniform, and partial inductance is
     additive under subdivision (WM-1 §9.2 proves it exactly). So the **series inductance must agree
     to solver precision as f → 0.** Any disagreement there is a bug, not physics.
   - **Charge.** Charge is **never** uniform along a wire — it concentrates at the ends. So the
     **capacitance must not agree**, at any frequency, and the MoM value should be the larger one.
     Expect a real difference; record its size.
   This asymmetry is the single most useful thing to have straight before you look at any number.

3. **`design.IncludeCapacitance = false` has no meaning for this kernel.** The MoM network *is* the
   coupled L–C ladder; there is no version of it with the shunt removed (setting `G⁻¹ → 0` makes
   `K̃`, `W` and `H` all vanish and the reduction degenerate). **Do not refuse and do not silently
   obey.** Include the capacitance, and attach a note to the result: *"Capacitance is intrinsic to
   the distributed model and is included. The design's `Include capacitance` setting applies to the
   lumped model only."* The series-arm accessor of §3 is what serves a caller who genuinely wants the
   no-capacitance comparison.

---

## 1. Where the code goes

```
src/WBond/Mom/WireMomSolver.cs        — M̃(ω), the factorisation, the port reduction
src/WBond/Mom/WireMomResult.cs        — per-frequency Z_port / Y_port + notes + the mesh report
src/Ui/WBond/WBondTouchstoneExport.cs — the Model option and its branch          (§7.1)
src/Ui/Views/Dialogs/WBondTouchstoneExportDialog.axaml(.cs) — Model + segments      (§7.1)
src/Ui/WBond/WBondMomCompareViewModel.cs        — the compare dialog's whole brain  (§7.3)
src/Ui/Views/Dialogs/WBondMomCompareDialog.axaml(.cs) — its shell                   (§7.3)
src/Ui/WBond/WBondMenuViewModel.cs              — one RelayCommand + one hook       (§7.3)
src/Ui/Views/WBond/WBondMenuView.axaml          — the item, in BOTH menu trees      (§7.3)
src/Ui/Views/WBond/WBondEditorView.axaml.cs     — fills the hook in                 (§7.3)
tests/WBond.Tests/Mom/…               — §6
tests/Ui.Tests/…                      — §7.5
```

`src/WBond` stays a leaf project with **no** `ProjectReference` (WM-1 §0.3 item 5). The solver returns
`Complex[]` row-major per frequency; `src/Ui` converts to `Mat<Complex>` and `RFNetwork` exactly as it
already does for the analytic path.

---

## 2. The per-frequency solve

From WM-1 §2.4, with everything but `D(ω)` precomputed:

```
M̃(ω) = (jω)² L  +  jω D(ω)  +  K̃                    N_s × N_s, complex SYMMETRIC
X     = M̃(ω)⁻¹ W                                     N_s × T,  T = 2M
Z_port(ω) = ( H − Wᵀ X ) / (jω)                       T × T
Y_port(ω) = Z_port⁻¹
```

### 2.1 Assembly

`(jω)² = −ω²`, so the `L` term is **real and negative**: `M̃ = −ω²L + K̃ + jω·D(ω)` with `D` complex
diagonal. Form it as one `Complex[]` of `N_s²` per frequency. **`L` and `K̃` are never modified** —
they are the design's, not the frequency point's.

### 2.2 Factorisation

Use `ComplexLu.Factor` (general LU with partial pivoting), `T` solves against `W`'s columns. That is
`2N_s³/3` flops and it is correct. **Do not write a complex-symmetric `LDLᵀ` in this brief** — it is
a 2× win, it is real, and it belongs in WM-3 where it can be measured against this one as the
reference. Getting the answer right first is what makes that measurement meaningful.

### 2.3 Symmetrise the result

`Z_port` is complex symmetric in exact arithmetic (WM-1 §2.6 item 1). Force it —
`Z[i,j] = Z[j,i] = ½(Z[i,j] + Z[j,i])` — exactly as `ImpedanceReduction.ArrayImpedance` already does
for its own output. Reciprocity should be structural in what you hand out, not true only to rounding.

### 2.4 Frequency independence is a hard rule

The only things that may be recomputed per frequency are `D(ω)`, `M̃`, its factorisation and the `T`
solves. **If you find yourself refilling `L`, `P` or `K̃` inside the frequency loop, stop.** That is
the one mistake that turns a 2-second sweep into a 20-minute one, and it is the mistake this whole
formulation exists to make impossible.

---

## 3. `SeriesArmImpedance(f)` — the accessor the identity gate needs

A second, tiny entry point that answers "what does this mesh say the *series arm* is?", with the shunt
path removed by construction rather than by taking a limit:

```
Z_wire[i,j] = Σ_{p ∈ wire i} Σ_{q ∈ wire j} ( jω L[p,q] + δ_pq D[p](ω) )
Z_arr       = ( Aᵀ_wire Z_wire⁻¹ A_wire )⁻¹                    M × M
```

That is the wire-basis assembly and array reduction the analytic model already does — and **on a
subdivided mesh it must produce the identical matrix**, because with no shunt path KCL forces one
current per wire, partial inductance is additive under subdivision, and `D` scales with length.

This is not a duplicate implementation for its own sake: it is the bridge that lets §6.2 compare the
segment mesh against `ImpedanceReduction.ArrayImpedance` at **solver precision** rather than at a
tolerance. Keep it ~30 lines and reuse `ComplexLu`.

---

## 4. Results and notes

`WireMomResult` carries, per frequency, `Z_port` and `Y_port` row-major; plus, once:

- the WM-1 mesh report (`N_s`, `N_n`, `N_r`, `T`, memory, the s/a warnings, the clamp count);
- the terminal names;
- **notes**, in the style `src/Engine/Mom` already uses — one line each, user-readable:
  - the capacitance note of §0.1 item 3, whenever `IncludeCapacitance` is false;
  - every s/a warning from WM-1;
  - **the validity note**, always: *"Quasi-static: this model has no radiation and its mutual coupling
    is instantaneous. A wire pair separated by more than λ/10 (X mm at the top frequency) is
    increasingly optimistic about their coupling."* Compute X from the highest requested frequency and
    the largest wire-pair separation and put both numbers in the note — `mom-wirebond-kernel.md` §4.1
    is explicit that the error term is largest where the coupling is smallest, so this is a caveat,
    not an alarm, and it should read like one.

---

## 5. Low frequency — the named risk from WM-1 §2.6 item 2

`M̃(ω) → K̃` as ω → 0, and `K̃` is singular whenever terminal shorting created a loop (any array with
≥2 wires). The blow-up is projected out of `Z_port` analytically, but `M̃`'s **condition number grows
like 1/ω**, so there is a frequency below which the answer is noise.

**Find that frequency by measurement, do not guess it.** Sweep a 2-wire single-array design from
1 kHz to 1 GHz, decade by decade, and watch the series inductance extracted from `Y_port` against the
analytic `ArrayReduction` value (which has no such limit). Report where it departs by 0.1 %.

Then: **`WireMomSettings.MinimumFrequencyHz` refuses below that, with the measured number in the
message and the analytic model named as the thing to use there instead.** A default of 1 MHz is a
reasonable expectation, but the shipped value is whatever you measured, and the measurement goes in
`RESOLVED.md`. If it turns out there is no such floor down to 1 kHz, say that — it is a better result
and it costs a settings knob rather than earning one.

---

## 6. The gates

### 6.1 Structure — free, and they catch transposes
On a 2-array × 2-wire design at 3 frequencies:
- `Z_port` symmetric to 1e-12 **before** §2.3's forced symmetrisation (assert on the raw matrix, or
  the symmetrisation hides the bug it exists to make structural);
- `Y_port = Z_port⁻¹` round-trips to 1e-10;
- `T = 2M` and the terminal names match WM-1's.

### 6.2 The identity gate — write this one first
`SeriesArmImpedance(f)` on a mesh subdivided to 24 segments/wire **equals**
`ImpedanceReduction.Create(design).ArrayImpedance(f)` to **1e-10 relative**, at f = 10 MHz, 1 GHz and
20 GHz, on a 4-wire / 2-array ball-bond design with images on.

This validates the segment `L`, the segment `D(ω)`, the wire grouping and the array reduction against
already-validated code, in a test that runs in milliseconds. **If it fails, debug nothing else.**

### 6.3 The low-frequency series gate — the end-to-end one
At **10 MHz**, extract each array's series impedance from the full `Y_port`:

```
Z_series[k] = −1 / Y_port[2k, 2k+1]
```

(valid because at 10 MHz a ~35 fF shunt is ~455 kΩ against a ~0.1 Ω series arm — six orders of
margin). Its imaginary part over ω must match `ArrayReduction.PicoHenries(k,k)` to **0.1 %**, and its
real part must match the analytic `R` to **0.1 %**.

**This is the gate that proves the whole chain** — mesh, `P`, `G`, `K̃`, `W`, `H`, `M̃`, the
factorisation, the port reduction — because it goes through every one of them and lands on a number
computed by an entirely different route. Record the *actual* agreement in `RESOLVED.md`; if it is
1e-6 rather than 1e-3, that is worth knowing and worth tightening the gate to.

### 6.4 Passivity, reciprocity, losslessness
- **Passivity**: the Hermitian part of `Z_port` is positive semidefinite at every tested frequency
  (eigenvalues ≥ −1e-12 relative). `T = 4`, so this is free.
- **Losslessness**: with `sigma` set to 1e12 S/m (so `R → 0`, `L_int → 0`) and no dielectric loss
  anywhere in this kernel, the S-matrix at Z₀ = 50 Ω must be unitary to 1e-9. Convert via
  `RFNetwork` — `tests/WBond.Tests` already references `RfCore`.
- **Reciprocity**: covered by §6.1.

### 6.5 Convergence of the network
8-wire, 2-array design at `TargetSegmentsPerWire` ∈ {12, 24, 48} (`N_s ≤ 384`), 3 frequencies
(1, 10, 40 GHz). Assert `max|S(48) − S(24)| < max|S(24) − S(12)|` at every frequency, and record all
three deltas. **This is what tells you whether WM-1's default of 24 is right for the network, not
just for one wire's capacitance.** If 24 is not converged at 40 GHz, say so and raise the default —
and record the cost that costs.

### 6.6 The correlation study — the owner's sanity check
A **4-wire, 2-array** design (two wires per array, 10 mil pitch, 100 mil span, 30 mil loop, 1 mil gold
over ground), 24 segments/wire, at **0.01, 0.1, 1, 5, 10, 20, 40 GHz**. For each frequency compute
both the MoM `Y_port` and `WBondTouchstoneExport.TerminalAdmittances`'s analytic one, and record a
table of:

| f | L_series MoM vs analytic (%) | C_shunt MoM vs analytic (%) | max \|ΔY\|/\|Y\| (%) | \|S21\| MoM vs analytic (dB) |

**What the test asserts** — deliberately little, because the owner's expectation is *correlation, not
agreement*:

- `L_series` agrees within **0.5 %** at 0.01 GHz and 0.1 GHz. *(A hard gate. This is §6.3 restated;
  if it fails, something is broken.)*
- `C_shunt` from the MoM is **larger** than the analytic one, by a factor in **[1.0, 2.0]**, at every
  frequency. *(A loose sanity band on a difference that is real physics — uniform charge per unit
  length underestimates the end concentration. If the MoM value comes out smaller, that is a sign
  error, not a modelling difference.)*
- `max |ΔY|/|Y|` is **monotonically non-decreasing** in frequency across the seven points. *(This is
  the real content of "they should be correlated": the two models must diverge smoothly. A
  non-monotone divergence means a bug at one frequency, and it is much easier to see than to
  reason about.)*

**What the test does not assert:** any bound on the high-frequency difference. Print it, record it in
`RESOLVED.md`, and say in your report what it turned out to be. A 30 % difference at 40 GHz is a
finding; a passing test that hid it is not.

### 6.7 Refusals
- Below `MinimumFrequencyHz` (§5) → refuse, message carries the measured number and names the
  analytic model.
- `WBondPortBasis.ArrayPairs` requested with the MoM engine → **refuse**, because a floating terminal
  pair has no reference for the shunt current to leave by and the distributed model's whole content is
  the shunt. Message: *"The distributed (MoM) model publishes on the terminal basis only — an
  array-pair port is a floating pair, and this model's shunt capacitance has no terminal to return
  through. Use the terminal basis, or the lumped model if you want an array-pair file."*
- Ground plane disabled → already refused at mesh time in WM-1; assert the solver surfaces it rather
  than throwing something else.

### 6.8 Sweep cost — optional here, mandatory in WM-3
If you write it: 201 points on a 40-wire design, tagged `Benchmark`, measured alone. If you do not,
**say so explicitly in the report** and leave it to WM-3. Do not write a half-measured version.

---

## 7. The UI — one export option, and one entry point you can actually drive

Two things, and the second is the one that matters for hands-on testing: **the owner must be able to
open a `.wBond`, run the MoM, and see it next to the analytic answer, without leaving the editor.**

### 7.1 The export option

`src/Ui/WBond/WBondTouchstoneExport.cs` already does everything: builds the terminal-basis `Y`,
converts through `RFNetwork`, writes with `TouchstoneExporter`, and labels ports in the header. Add
**one** thing.

```csharp
public enum WBondNetworkModel
{
    /// <summary>The lumped array-basis model — one current and one charge basis function per wire.
    /// Frequency-independent matrices, effectively instant. The default.</summary>
    Lumped,

    /// <summary>The distributed MoM model — one current unknown per segment. Sees the wire as a
    /// transmission line rather than as a lumped L with an end capacitance, at the cost of one dense
    /// complex factorisation per frequency point.</summary>
    Distributed,
}
```

on `Options`, defaulting to `Lumped`, plus `Options.SegmentsPerWire` (default = WM-1's
`TargetSegmentsPerWire`). `TerminalAdmittances` branches on it.

**`Lumped` must stay bit-identical to today.** Its code path is not to be touched, reorganised or
"shared" with the new one. There are round-trip tests against a real solve holding it shut, and a
refactor that makes them still pass while changing the last bits is the kind of change nobody catches
for a year.

Add a **Model** `ComboBox` to `WBondTouchstoneExportDialog.axaml`, next to the existing port-basis
control, with a **Segments per wire** `NumericUpDown` that is enabled only for `Distributed`. Tooltip
carries the cost: *"Distributed solves a dense matrix at every frequency point — a 201-point export of
a 40-wire array takes seconds, not milliseconds."* Selecting `Distributed` + `ArrayPairs` disables the
export button and shows §6.7's refusal text; **do not silently switch the port basis for the user.**

### 7.2 The header says which engine wrote the file

Add to `HeaderComments`:

```
Model: distributed (MoM), 24 segments per wire, 96 current unknowns.
```

or

```
Model: lumped (analytic) — one current and one charge basis function per wire.
```

A `.snp` outlives the session that made it. Two files of the same design from two engines that do not
say which is which is a support ticket waiting to happen.

### 7.3 **Design → Compare Distributed Model… — the entry point to test with**

A new menu item in the wBond menu's existing **Design** submenu, next to *Check Assembly Rules*
(`WBondMenuView.axaml` has both the `NativeMenuItem` and the `MenuItem` tree — **add it to both**, or
it appears on one platform only), wired the same way: a `[RelayCommand]` on `WBondMenuViewModel` with
a `CompareDistributedModelHook` that `WBondEditorView` fills in. Copy that pattern exactly; do not
invent a second one.

It opens `WBondMomCompareDialog`, which has **three parts, in this order**:

**(a) The mesh report, before anything is solved.** This is `mom-wirebond-kernel.md` RW2 made visible,
and it is why the dialog exists rather than the work happening silently behind a progress bar:

> Segments per wire: **24** (Fast 8 · Balanced 24 · Accurate 48)
> 4 wires → **96 current unknowns**, 100 charge unknowns, 4 ports (G1.i, G1.o, G2.i, G2.o)
> Predicted: setup ~40 ms, ~1 ms per point, **~0.3 s for 7 points**. Peak memory ~1 MB.
> ⚠ Wires `A1` and `A2` approach to 4.2 a — the thin-wire kernel is a few percent optimistic below 6 a.

The report **updates live** as the segment count or the frequency grid changes, and it is shown
*before* the Run button is pressed. A user who is about to wait fourteen minutes finds out here.

**(b) The frequency grid.** Reuse `WBondTouchstoneExport.BuildFrequencies` and the export dialog's own
Start / Stop / Points / Log controls verbatim — same code, same validation. Default to the seven
points of §6.6 (0.01, 0.1, 1, 5, 10, 20, 40 GHz) via a **Log** grid from 0.01 to 40 GHz at 7 points,
so the dialog opens on exactly the comparison this brief was written to produce.

**(c) The comparison table**, one row per frequency, after Run:

| f (GHz) | L series, lumped (pH) | L series, MoM (pH) | Δ % | C shunt, lumped (fF) | C shunt, MoM (fF) | Δ % | max ΔY/Y % | \|S21\| lumped (dB) | \|S21\| MoM (dB) |

with an array selector when there is more than one array (the `L`/`C`/`S21` columns are per-array;
`max ΔY/Y` is over the whole matrix and does not change with the selection). A **Copy** button puts the
table on the clipboard as tab-separated text — that is what makes it a thing the owner can paste
somewhere and look at, and it costs one handler.

**Bounded scope, stated so it stays bounded.** No plot, no chart, no Data Display document, no
docking panel, no persistence of the dialog's settings, no progress bar beyond a busy cursor and a
disabled Run button. **The Run happens off the UI thread and honours cancel** (the dialog's Close
cancels it) — that much is not optional, because §6.5's sizes make a several-second run normal and a
frozen window is how a user concludes the feature is broken.

**Why a table and not a plot.** The question this dialog answers is *"do the two models agree, and
where do they stop agreeing?"* — which is seven numbers, not a curve. The user who wants the curve
already has §7.1: export both models and plot them in Data Display, which is what that surface is for.
Building a second plotting surface inside the wBond editor is scope this brief does not have.

### 7.4 It must work in the standalone `wBond` binary too

`src/Ui/ProgramWBond.cs` is a second entry point into the same assembly, and the wBond editor is the
same view in both. The menu item and the dialog therefore come along for free — **but check it**, by
running the standalone app, because Avalonia binds one `NativeMenu` per window for that window's
lifetime and the menu tree is built in two places (§7.3). This repo has already paid for a
menu-not-shown bug that turned out to be the same defect as a crash. Report that you ran both
`circuitRF` and `wBond` and saw the item in each.

### 7.5 `Ui.Tests`

- The port-name parity assertion WM-1 §9.9 deferred: `WireMomMesh.TerminalNames(design)` equals
  `WBondTouchstoneExport.PortNames(design, Terminals)` element for element.
- A `Distributed` export of a 2-wire design writes a valid `.s4p` that reads back through
  `TouchstoneIO` with 4 ports and the right labels. **Two frequency points, not 201.**
- The header of a distributed file contains the segment count and the unknown count.
- `Distributed` + `ArrayPairs` refuses with the §6.7 message.
- `Lumped` output is unchanged from before this brief (value-compare against the existing tests'
  expectations).
- **The compare dialog's view model**, headlessly: given a design and a 3-point grid, `RunAsync`
  produces 3 rows with both models populated and a non-empty mesh report; the report is non-empty
  *before* `RunAsync` is called; the s/a warning appears for a design that has one. **Test the view
  model, not the XAML** — the dialog is a shell over it, and that is what makes this cheap.
- Both menu trees in `WBondMenuView.axaml` contain the new item (a source-level assertion on the XAML
  is fine and is the cheapest way to catch the one-platform-only miss; strip comments first —
  source-scan tests in this repo have been fooled by commented-out markup before).

## 8. What is explicitly NOT in this brief

- **The schematic component does not change.** `WBondModel.Stamp` keeps using
  `ImpedanceReduction`/`CapacitanceReduction`. Wiring the MoM model into a simulation stamp is a
  separate decision with its own cost story (a `Stamp` call happens per frequency, per sweep point,
  per analysis) and it is not made here. If you touch `src/Core/Devices/WBondModel.cs`, you have
  exceeded scope.
- No `Cli` verb, no EM panel, no `.cem`, no `IEmKernel`, no registry entry.
- **No plot and no Data Display document.** §7.3 ships a table; the curve comes from exporting both
  models with §7.1 and plotting them where plotting already lives.
- No docking panel, no persisted dialog settings, no progress bar beyond a busy cursor.
- No complex-symmetric factorisation, no frequency parallelism, no memory tuning. → **WM-3**
- No retarded kernel, no meshed surfaces, no overmold, no stepped ground.

---

## 9. Report back

In `src/WBond/Mom/RESOLVED.md`:

1. **§6.6's full correlation table**, verbatim. This is the answer to the question that motivated the
   whole tranche; it is worth more than any prose summary of it.
2. **§6.3's actual agreement** (not just "passed") and whether you tightened the gate.
3. **§5's measured low-frequency floor**, and whether a `MinimumFrequencyHz` refusal was needed at
   all.
4. **§6.5's convergence deltas**, and whether WM-1's default of 24 segments survived.
5. **The measured per-frequency solve cost** at `N_s` = 192, 384 and 960, measured alone — WM-3 needs
   this as its baseline, and taking it now while the code is simple is worth more than taking it later
   after an optimisation has muddied it.
6. **Confirmation that you ran both binaries** (§7.4) — `circuitRF` and the standalone `wBond` — and
   saw the Compare item in each, plus the mesh report and one filled comparison table. Paste the
   table you got into `RESOLVED.md`; it is the same table §6.6 asserts on, and having the two agree
   is worth more than either alone.
7. **Anything in §2 or §3 that was wrong.** Correct it in `RESOLVED.md`, in bold, and leave this brief
   file alone.
