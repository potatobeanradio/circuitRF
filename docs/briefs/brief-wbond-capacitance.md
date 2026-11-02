# wBond — capacitance

**Owner request, 2026-08-18.** Add capacitance to the wBond component model, behind an
`IncludeCapacitance` boolean that defaults to **true** and that, when **false, reproduces today's
answer exactly**. Expose it as a checkbox in the wBond parameter-editor dialog. Settle what the Array
Inductance panel reports once capacitance exists, add a frequency readout to that panel if one is
needed, and add a toolbar-style toggle for capacitance. Keep it fast.

The physical model is the causal bond-wire model of Nazarian *et al.*, *IEEE Trans. MTT* **60**(12),
Dec 2012 — §II-H (capacitance) and its eqs. (15)–(18). wBond's existing inductance and internal-
impedance paths already **are** that paper's model, arrived at independently; capacitance is the one
part of it we never built.

---

## 0. The questions, answered up front

The owner asked five things during the framing of this brief. All five are settled here, and three of
them are settled by measurement rather than by argument.

**0.1 "How much will it slow down the calculation?"** — **~25 % on the cold fill, and nothing at all
on the drag path.** Measured kernel costs (Apple Silicon, Release, 2 M pairs each):

| kernel | ns / filament pair | vs. today |
|---|---|---|
| `Grover.Mutual` — what the inductance fill pays today | 106.6 | 1.00 |
| centre-to-centre `1/R` — the electrostatic **far** kernel | 21.8 | **0.205** |
| 4×4 tensor-Gauss `1/R` — the electrostatic **near** kernel | 88.9 | **0.834** |

The electrostatic pair loop is *cheaper per pair than the inductance one*, because there is no `cos ε`
factor, no four `Atanh`, and no four `Atan2` — just a reciprocal square root. With a near/far split
(accurate kernel only for same-wire and immediately-adjacent pairs, which is a few percent of pairs)
the blended fill cost is ≈ **0.25 ×** the inductance fill. At the 600-wire reference point that is
0.54 s → about **+0.13 s**. Worst case — accurate kernel on every pair, no split — is +0.45 s.

**0.2 "Does the panel need a frequency?"** — **Yes**, and today it does not have one because today it
reports a purely geometric quantity. See §3. Today's number is the frequency-independent *external*
array-basis partial inductance `L_arr = (AᵀL⁻¹A)⁻¹`; it contains neither `R(f)` nor `L_int(f)`, which
is why no frequency appears anywhere in `PanelReadout`. Once shunt capacitance exists the terminal
inductance genuinely becomes a function of frequency, so the panel must say which one it is quoting.

**0.3 "Are we adding cross-capacitance between wires? Or is that another parameter?"** — **Yes, we
add it, and no, it is not another parameter.** §4 is the whole argument. In short: the owner's
instinct that it "really slows down the calculation" is correct about the *obvious* implementation
and wrong about the one this brief specifies, and dropping the cross terms would not merely lose
inter-array coupling — it would bias every multi-wire array's own capacitance **high by tens of
percent**, in the optimistic direction, which is the exact failure mode `WBondModel`'s return-path
refusal already exists to prevent.

**0.4 "Don't add it if you think it's not worth it."** — The thing that is not worth adding is the
**second parameter**. The cross terms themselves are worth adding and are cheap. One switch ships:
`IncludeCapacitance`.

**0.5 Where the shunt capacitance returns to** — `REF` when the pin is exposed, node 0 otherwise.
Owner-confirmed 2026-08-18; §2.4 carries the reasoning and the doc-comment correction it forces.

---

## 1. What exists today, so the size of this is not overstated

| | today |
|---|---|
| `Grover` | exact filament-pair mutual, Ch. 17 parallel + Ch. 19 skew, GMD floor `√(a_p·a_q)` |
| `Filament.Image()` | mirror through z = 0 **and** reverse traversal — correct for horizontal and vertical current alike |
| `InductanceMatrix.Fill` | wire-basis `L`, `Block(wi,wj)` = ΣΣ over filament pairs, direct **+** image |
| `ArrayReduction` | `L_arr = (AᵀL⁻¹A)⁻¹` — Cholesky at N, **M** triangular solves, then one M×M inverse |
| `ImpedanceReduction` | `Z_arr(ω) = (AᵀZ(ω)⁻¹A)⁻¹`, `Z = R(ω) + jω(L + L_int(ω))` |
| `InternalImpedance` | exact `(γa/2)·I₀(γa)/I₁(γa)`, three regimes |
| `WBondModel.Stamp` | M coupled series branches, `REF` declares but never stamps |
| `PanelReadout` | pH, **frequency-independent**, external `L_arr` only |

Measured costs already recorded in the code: cold fill **~0.54 s** at 600 wires
(`InductanceMatrix.cs:177`); `CholeskyFactor.Factor` at N = 600 **~22.7 ms**; twelve triangular solves
**~2.5 ms**; incremental refill **~3.6 ms**; a drag frame **~5 ms** with a maintained factor
(`ArrayReduction.cs`, `IncrementalFill`).

**Nothing in that list changes.** Capacitance is additive: a second matrix, a second reduction, extra
stamps. `IncludeCapacitance = false` must take a branch that never touches any of it.

---

## 2. The model

### 2.1 The electrostatic problem is the dual of the fill we already run

Build a **coefficient-of-potential** matrix `P` over the same filaments and the same images, with one
charge basis function per **wire** (uniform charge per unit length along a wire — the standard single-
basis-function-per-conductor electrostatic model, and the same approximation that gives the textbook
wire-over-plane capacitance). Then

```
C_wire = P⁻¹          (N_wire × N_wire, Maxwell capacitance)
C_arr  = Aᵀ P⁻¹ A     (M × M)
```

**Three facts make this cheap, and each one is load-bearing:**

1. **`P` is filled by the same pair loop as `L`.** `Block(wi,wj)` becomes
   `PotentialBlock(wi,wj) = (1/(4πε·l_i·l_j))·ΣΣ [ K(p,q) − K(p, Image(q)) ]`. Same loops, same
   images, cheaper kernel (§0.1).
2. **The image sign FLIPS.** `L` **adds** the image term, because `Filament.Image()` bakes the current
   reversal into the returned direction vector. A charge's image in a ground plane is **negative**, and
   there is no direction vector to carry it — so the electrostatic block **subtracts**. This is the
   single easiest thing to get wrong here, it produces a plausible finite wrong answer rather than a
   NaN, and it must have its own test (§6, gate C2).
3. **`Aᵀ P⁻¹ A` needs only M solves, exactly like `ArrayReduction`, and needs NO final inverse.**
   Wires in one array share both nodes, so they share a voltage and their **charges add**:
   `Q_arr = AᵀQ = AᵀC_wire·A·u`. That is a plain congruence transform on `C_wire` — no inversion of the
   M × M result, unlike the inductive case where sharing a voltage forces `(AᵀL⁻¹A)⁻¹`. `P` is
   symmetric positive definite, so `CholeskyFactor` applies directly (unlike the complex-symmetric `Z`,
   which is why `ComplexLu` exists).

So the capacitance **reduction is strictly cheaper than the inductance one**: one Cholesky at N (~22.7 ms
at 600), M triangular solves (~2.5 ms), and then a scatter-add. Do **not** form `P⁻¹` explicitly —
`C_wire` as a full N × N inverse is N solves instead of M, and at N = 600 that is ~0.4 s of pure waste
for a quantity nothing downstream reads.

### 2.2 The near/far kernel split

`K(p,q) = ∫∫ ds ds′ / |r(s) − r(s′)|` over the two filaments.

- **Far** (axis separation > ~3 × the longer filament): centre-to-centre, `l_p·l_q/|r̄_p − r̄_q|`.
  21.8 ns.
- **Near** (same wire, consecutive filaments, or separation below the threshold): 4×4 tensor-product
  Gauss-Legendre. 88.9 ns. At the degenerate self/adjacent case apply the **same GMD floor**
  `√(a_p·a_q)` the inductance path uses — for the same reason, and stated the same way: two consecutive
  filaments of one wire have intersecting axes and d = 0, and the physically correct separation is the
  cross-section's GMD, not zero.
- **Self block** (`K(p,p)`): the closed form is available for free — `Grover.F(z,d)` **already computes
  exactly this integral**. The Neumann kernel for two *parallel* filaments is the electrostatic kernel
  times `cos ε`, so with `cos ε = 1` the four end-pair terms of `Grover.Parallel` are the electrostatic
  double integral verbatim. Make `Grover.F` public (or add a `Grover.ParallelScalarKernel` wrapper) and
  reuse it rather than writing a second copy of the same four terms.

**The far threshold is a measured knob, not a guessed one.** Gate C3 fixes it.

### 2.3 From `C_arr` to the stamp

Each array `k` gets, in the array basis:

- `C1_k` shunt at the input node, `C2_k` shunt at the output node,
- `C12_k` in series across the array (input to output),
- `C_kj / 2` between input nodes and `C_kj / 2` between output nodes, for each array pair `k ≠ j`.

**The end split uses Nazarian's (18): weighted by each segment's own self-inductance, not 50/50 and
not by length.** With `w_i` the running self-inductance up to segment `i` divided by the wire's total,

```
C1 = Σ C_i (1−w_i)²      C2 = Σ C_i w_i²      C12 = Σ C_i w_i(1−w_i)
```

which conserves total charge (`C1 + C2 + 2·C12 = Σ C_i`) and reproduces his reported `C12` two orders
of magnitude below `C1`/`C2`. **Verify this against the printed equation before shipping** — the
implementer should read (18) from the paper; the form above is a reconstruction that satisfies his
stated properties, and gate C5 pins the charge-conservation identity either way. The per-segment `C_i`
used for the *weights* come from the local analytic form (his (15)/(16), `2πε·l/acosh(h/a)` with the
tilted-segment quadrature); the **magnitudes** are then scaled so each wire's shunt total matches the
row sum of `C_wire`, so the multi-conductor solve sets the size and the local form sets only the shape.

### 2.4 Which node the shunt capacitance returns to — the one genuinely new decision

`WBondModel`'s whole design today is that `REF` **declares and does not stamp**, because `L_arr` is a
loop inductance whose return is the image plane and the return is therefore implicit in the schematic's
own ground. **Capacitance breaks that symmetry**: a shunt capacitor has to connect to something.

**Owner decision, 2026-08-18: the shunt capacitors stamp to `REF` when `HasReferencePin` is true, and
to node 0 otherwise.** Settled — do not re-open it. Reasons, in order:

1. The image plane at z = 0 *is* the reference conductor. If the user has told us which net that plane
   is, using any other node models a different circuit.
2. `RefPin` off is the default and node 0 is then the only defensible choice — it is also exactly what
   the plane-enabled configuration already assumes.
3. It makes `REF` finally *do* something in the one configuration where a reader would expect it to,
   without changing the pin's meaning or its position (still last, still renumbers nothing).

**Consequence to state in `WBondModel`'s own doc comment:** with `IncludeCapacitance` on, the component
is electrically a 2M+1 terminal device, and the sentence "REF never stamped" becomes false. Update that
paragraph rather than leaving it to be discovered.

The existing `RefuseIfReturnPathUndeclared` refusal is unchanged and still fires first: with the ground
plane disabled there is no plane to be capacitive *to*, and the refusal already covers it.

### 2.5 What `IncludeCapacitance = false` must mean

Not "compute it and stamp zeros" — **do not compute it at all**. `ImpedanceReduction` takes the flag,
and with it false it never fills `P`, never factorises it, and `WBondModel.Stamp` emits exactly the
stamps it emits today. Gate C1 is a bit-identical comparison against the pre-change build, not a
tolerance.

---

## 3. What the Array Inductance panel reports, and the frequency readout

### 3.1 Today

`PanelReadout` reports `ArrayReduction.PicoHenries(i,j)` — the **external, frequency-independent**
array-basis partial inductance. It contains no `R(f)` and no `L_int(f)`; `WBondModel.InductanceOnly()`
returns `Reduction.InductanceOnlyReduction()` precisely to keep it that way. That is why the panel has
never quoted a frequency and has never needed to.

### 3.2 With capacitance

The partial inductance matrix is still pure geometry and still frequency-independent. What changes is
that it is no longer the number a user wants: the wire now has a self-resonance, and the inductance
**seen at the terminals** rises toward it. So the panel switches to reporting an **effective
inductance at a stated frequency**:

```
L_eff,k(f) = Im( Z_in,k(f) ) / ω,   Z_in,k = the array's input impedance with its far end
                                             shorted to the reference plane
```

built from the **external `L_arr` and the capacitance only** — no `R`, no `L_int`. For a single array
that is `L_eff = L/(1 − ω²L·C1)`, which is the familiar shorted-stub result.

**That definition is chosen deliberately, and the alternative is named so nobody re-derives it.** The
other obvious candidate is the two-port series arm, `Im(−1/Y₂₁)/ω` — but for a π network that is
*identically* `L_arr` at every frequency, because the shunt capacitors do not appear in `Y₂₁` at all.
It would produce a frequency box whose value never changes anything, which is worse than having none.

**The invariant this buys, and it is the one to test:** with `IncludeCapacitance` off, `L_eff(f) =
L_arr` at **every** frequency, so the panel's number is identical to today's regardless of what the
frequency box says. Gate C6.

**Resonance.** Above the self-resonance the expression goes to ±∞ and then negative. The panel must not
print that. At `f ≥ 0.95·f_SRF` it shows the SRF instead, in the warning brush already used for the
undeclared return path: `"Above self-resonance (SRF 38.4 GHz) — the effective inductance is not
meaningful here."`

### 3.3 The frequency readout

A single line **above the self-inductance group cards**, in the top `StackPanel` of
`WBondInductancePanelView.axaml` alongside the return-path lines, using **the same font size and style
as the Loop height / Span rows** — i.e. the existing `detailLabel` (FontSize 10, Opacity 0.6) and
`detailValue` (FontSize 10, right-aligned) styles, promoted out of the card's local `StackPanel.Styles`
into the control's own `Styles` so both places share one definition.

```
Frequency        10 GHz
```

Double-click to set, matching the four settable rows the panel already has
(`OnArrayLoopHeightDoubleTapped` and friends) and reusing their `SettableRowTip` idiom. Units GHz,
always — no auto-ranging, for the same reason the panel fixes pH (`PanelReadout`'s own note).

**Where the value lives:** `WBondDesign.ReadoutFrequencyGHz`, persisted in `.wBond` and in the embedded
payload, default **10.0**. It is a *readout* setting, not a simulation input — the schematic's own
analysis sweep is what the engine stamps against, and this number must never reach `Stamp`. Say so in
its doc comment; a reader will assume otherwise.

*Why 10 GHz.* A representative 1 mm gold wire at 250 µm height is ≈ 1 nH and ≈ 15 fF, so SRF ≈ 40 GHz
and 10 GHz shows a ≈ +6 % effective-inductance bump: visible enough that the feature is not invisible,
far enough below resonance that the default never lands in the warning state. One line to change if
the owner prefers otherwise.

### 3.4 The capacitance toggle button

A `ToggleButton` in the wBond editor toolbar, in the group with `PanelToggle` and `RulerToggle`
(`WBondEditorView.axaml:192-202`), matching the layout editor's toolbar idiom exactly:

```xml
<ToggleButton x:Name="CapacitanceToggle"
              IsChecked="{Binding ViewModel.IncludeCapacitance, Mode=TwoWay}"
              ToolTip.Tip="Include capacitance to the reference plane in the reported inductance"
              Padding="6,3">
    <mi:MaterialIcon Kind="CircleMultipleOutline" Width="16" Height="16"/>
</ToggleButton>
```

`Padding="6,3"`, a 16×16 `MaterialIcon`, native Fluent `:checked` chrome as the active-state cue —
the same three properties every toggle in `LayoutEditorView.axaml:219-241` uses.

**This toggle and the component's `IncludeCapacitance` parameter are two different things and must not
be silently wired together.** The toolbar toggle belongs to the **editor's readout**; the parameter
belongs to a **placed component**. A wBond design open in the editor is not yet a component, and a
document can be placed as several components with different settings. The toggle sets
`WBondDesign.IncludeCapacitance`, which is what a newly-placed component *inherits* as its parameter
default — the same relationship `GroundPlane` already has between the design and the `GroundPlane`
override parameter. Follow that precedent exactly.

---

## 4. Cross-wire capacitance — measured, and why there is no second switch

The owner asked whether cross-wire capacitance should be a separate `IncludeCrossWireCapacitance`
parameter, expecting it to be the expensive part. **It is not, and it is not optional.**

### 4.1 It is not the expensive part

The expensive version is the one that is easy to imagine: a **filament-basis** `P`, N = 3,600, formed
and inverted. That is 3,600² × 8 B ≈ **104 MB** and ≈ 1.6 × 10¹⁰ flops for the inverse — seconds, and
it *would* wreck the tool. This brief never forms it. In the **wire basis** (§2.1) `P` is N_wire × N_wire
— the same 600 × 600 as `L` — filled by a loop that is measurably *cheaper per pair* than the inductance
loop, and reduced by M solves rather than N. Total: **+~0.13 s cold, +~25 ms of reduction**.

### 4.2 Dropping it would not just lose coupling — it would bias every array high

This is the decisive half. Ignoring inter-wire terms means using only `P`'s diagonal, i.e. summing
*isolated* wire capacitances. Real adjacent wires **shield each other**, so the sum overestimates.

For two wires at pitch *p*, height *h*, radius *a*: `P_ii ∝ ln(2h/a)` and `P_ij ∝ ln(√(4h²+p²)/p)`. At
h = 250 µm, a = 12.7 µm, p = 100 µm those are 3.67 and 2.31 — a coupling ratio of 0.63. The array's
total capacitance with the cross term is `2/(P_ii + P_ij)` against `2/P_ii` without, so **ignoring the
cross terms overestimates by ~60 %** on a two-wire array, and more as the array gets denser. A dense
parallel array at tight pitch is the case wBond exists for.

An overestimated shunt capacitance pulls the self-resonance **down** and inflates `L_eff` — wrong in the
optimistic direction, which is precisely the failure mode `RefuseIfReturnPathUndeclared` was written to
stop. A switch whose "off" position silently does that is not a performance option; it is a trap.

*(Note the distinction, because it is easy to state backwards: what vanishes within an array is the
lumped **mutual capacitor** between two of its wires, since they share both nodes and ΔV = 0. The
shielding does **not** vanish — it is already inside each wire's shunt-to-ground term, because
`C_wire[i][j] < 0` for `i ≠ j`. `C_arr[k][k] = Σ_{i,j ∈ k} C_wire[i][j]` is the correct total either
way, and it is materially smaller than `Σ_i 1/P_ii`.)*

### 4.3 Decision

**One parameter, `IncludeCapacitance`. Cross terms always included when it is on.** A second switch
would cost a parameter, a checkbox, a persisted field, a test tier and a doc paragraph, in exchange for
saving ~0.13 s and making the physics worse.

---

## 5. Performance budget — what must be true when this lands

| path | today | with capacitance | rule |
|---|---|---|---|
| cold build, 600 wires | ~0.54 s | ≤ **0.75 s** | gate C4 |
| array reduction | ~25 ms | ≤ **55 ms** | one extra Cholesky + M solves |
| **drag frame** | ~5 ms | **~5 ms — unchanged** | §5.1 |
| `IncludeCapacitance = false` | — | **bit-identical to today** | gate C1 |

### 5.1 Capacitance is NOT in the drag loop

`IncrementalFill` rank-2 updates the Cholesky factor of `L` per frame; refactorising instead costs
22.7 ms and blows the frame budget (`ArrayReduction.cs` says so with measurements). **Do not build the
same machinery for `P`.** Instead: **capacitance is recomputed on drag *end*, not per frame.**

During a drag the panel keeps the last committed `C` and updates `L` live, so `L_eff` still moves with
the geometry and the staleness is a second-order effect that clears on commit. This is sound because
`C` is far less geometry-sensitive than `L` — it depends on height above the plane and neighbour pitch
through logarithms, where `L` depends on the whole path. **State this in the panel's doc comment**; a
reader who finds `C` not updating mid-drag must find the reason there rather than file it as a bug.

If a future measurement shows the drag-stale `C` visibly moving the readout, the fix is a rank-update
for `P`, not a per-frame refactorisation. Do not build it speculatively.

---

## 6. Gates

Every gate is a test. Untagged unless noted — the routine `dotnet test` must stay the gate.

- **C1 — off is exactly today.** With `IncludeCapacitance = false`, `WBondModel.ArrayImpedance(f)` is
  **bit-identical** to the pre-change build across a frequency sweep and across every fixture design.
  Not a tolerance. This is the gate the owner's requirement literally names.
- **C2 — the image sign.** A single horizontal wire over the plane at height *h*: `C` from `P` matches
  the closed form `2πε·l/acosh(h/a)` to < 1 %. **Then flip the image sign in a copy and confirm the
  test fails** — an image-sign error is finite and plausible, so a test that cannot see it is not a
  test. Independently: raising *h* must *decrease* `C` monotonically, which a sign error inverts.
- **C3 — the near/far threshold is measured.** Sweep the threshold; assert the array-basis `C_arr`
  converges and pick the smallest threshold within 0.1 % of the all-near answer. Record the number in
  a doc comment with its measurement, the way `Grover.ParallelEpsilon` records its own.
- **C4 — cost.** Cold build at 600 wires ≤ 0.75 s; reduction ≤ 55 ms. Tag
  `[Trait("Category","Benchmark")]` if it measures over ~5 s, otherwise leave it routine.
  **A drag-frame test asserting capacitance is absent from the incremental path is routine and must
  exist** — that is the §5.1 rule, and it is the one a later refactor will quietly break.
- **C5 — charge conservation in the end split.** `C1 + C2 + 2·C12 = Σ C_i` per wire, exactly.
- **C6 — the panel's invariant.** With capacitance off, the panel's `L_eff` is independent of the
  frequency box across 0.1–100 GHz and equals `ArrayReduction.PicoHenries(k,k)`.
- **C7 — shielding is real and is captured.** A 2-wire array at 100 µm pitch has `C_arr[0][0]`
  materially below twice the single-wire value (§4.2 predicts ~×1.25, not ×2). This is the test that
  would fail if someone later "optimises" the fill down to `P`'s diagonal.
- **C8 — REF routing.** With `RefPin` on and `REF` tied to a net other than ground, the shunt
  capacitance appears at that net, not at node 0. Verified through a solve, not by reading the stamp.
- **C9 — resonance is reported, not printed.** A design driven above its SRF shows the warning string
  and no number.
- **C10 — round-trip.** `IncludeCapacitance` and `ReadoutFrequencyGHz` survive `.wBond` save/load and
  the embedded-payload encode/decode, and an **old file without either field** loads with the defaults
  (true, 10.0) rather than throwing.

---

## 7. Guardrails

- **Do not touch `src/Engine` or `src/RfCore`.** This is `src/WBond`, `src/Core/Devices/WBondModel.cs`,
  `src/Core/Devices/ComponentModelFactory.cs`, and `src/Ui`.
- **Do not change `Grover`'s existing behaviour.** Making `F` public is the only permitted edit to it;
  `Mutual`, `Parallel`, `Skew` and `SelfExternal` are unchanged, and their existing oracles must stay
  green untouched.
- **Do not form `P⁻¹` explicitly.** M solves, not N. §2.1.
- **Do not build a filament-basis `P`.** §4.1.
- **Do not put capacitance in the drag loop.** §5.1.
- **`ReadoutFrequencyGHz` must never reach `Stamp`.** It is a readout setting. Assert it in a test.
- **One parameter.** No `IncludeCrossWireCapacitance`. §4.3.
- **The `wbond.md` edit ships with the code** (§9). The design document does not currently contain the
  word "capacitance" at all, so leaving it for later makes it wrong rather than merely incomplete.
- **Report the measurement, don't assume it.** §0.1's kernel ratios were measured on one machine with a
  synthetic pair distribution. The implementation's own C4 gate is what actually holds the budget; if
  the real fill comes in materially worse than +25 %, **stop and report** rather than tuning the near/far
  threshold until the number looks right.

---

## 8. Files

| file | change |
|---|---|
| `src/WBond/PotentialCoefficients.cs` | **new** — the `P` fill, near/far kernels, images with the flipped sign |
| `src/WBond/CapacitanceReduction.cs` | **new** — `C_arr = AᵀP⁻¹A`, Cholesky + M solves, the (18) end split |
| `src/WBond/Grover.cs` | `F` made public (or a `ParallelScalarKernel` wrapper). Nothing else. |
| `src/WBond/ImpedanceReduction.cs` | takes `includeCapacitance`; owns the `P` factor alongside `L`'s |
| `src/WBond/WBondDesign.cs` | `IncludeCapacitance` (default true), `ReadoutFrequencyGHz` (default 10.0) |
| `src/WBond/WBondIo.cs` | persist both, with defaults for old files |
| `src/WBond/PanelReadout.cs` | `L_eff(f)`, the SRF, the above-resonance flag |
| `src/Core/Devices/WBondModel.cs` | shunt/coupling stamps; the `REF` doc paragraph corrected |
| `src/Core/Devices/ComponentModelFactory.cs` | read `IncludeCapacitance` through the existing `IsTrue` |
| `src/Ui/Schematic/ComponentTypeRegistry.cs` | declare the parameter, `"true"` default |
| `src/Ui/ViewModels/ParameterEditorViewModel.WBond.cs` | `WBondIncludeCapacitance`, following `WBondRefPin` exactly |
| `src/Ui/Views/ParameterEditor/ParameterEditorView.axaml` | the checkbox, beside "External reference pin" |
| `src/Ui/Views/WBond/WBondInductancePanelView.axaml` | the frequency row; `detailLabel`/`detailValue` promoted to control-level styles |
| `src/Ui/Views/WBond/WBondEditorView.axaml` | the toolbar `ToggleButton` |
| `src/Ui/WBond/WBondPanelViewModel.cs` | the frequency property, its double-tap setter, the resonance string |
| `docs/design/wbond.md` | **§9 below — part of this phase, not a follow-up** |


---

## 9. Update `docs/design/wbond.md` — part of this phase's definition of done

**`wbond.md` does not contain the word "capacitance" anywhere today** (verified 2026-08-18, zero
matches). That is not merely an omission — it means a reader cannot currently tell from the design
document whether the model has capacitance or not, and after this phase they would be reading a
document that is actively wrong about what the component stamps. **The doc edit ships with the code, in
the same change. It is not a follow-up brief and it is not optional.**

Eight specific edits. Each names what is wrong today, not just what to add.

**9.1 — New `### 3.7 Capacitance`, after §3.6.** The physics chapter's new section, written to the same
standard as §3.1–§3.5 (derive it, don't assert it):
- the electrostatic problem is the **dual** of the inductance fill — same filaments, same images, same
  pair loop, cheaper kernel;
- **one charge basis function per wire** (uniform charge per unit length), and that this is an
  approximation with a name, not an exact solve;
- **the image sign flips** — this belongs beside §3.2's own sign rule and should cross-reference it
  explicitly. §3.2 is titled "the sign rule that is easy to get wrong"; there are now **two** such
  rules, they resolve in opposite directions, and the reason is that `Filament.Image()` carries the
  current reversal in its direction vector while a charge has no direction to carry.
- `C_arr = AᵀP⁻¹A` — a **plain congruence transform, no final inverse**, and say why this differs from
  §3.4's `(AᵀL⁻¹A)⁻¹`: sharing a voltage makes charges *add* and currents *divide*. §3.4 derives its
  reduction; §3.7 must derive this one the same way rather than stating the result.
- the near/far kernel split, with the measured threshold from gate C3;
- Nazarian's inductance-weighted end split, with the charge-conservation identity;
- the shielding argument of §4.2 above, with its numbers — this is what makes the cross terms
  non-optional, and it is the part a future reader is most likely to try to "optimise" away.

**9.2 — `### 3.6` gains a bullet, and one existing bullet is now wrong to leave as-is.** §3.6 is the
"stated plainly" list of what the reduction omits. Add what capacitance's own approximations are: the
uniform-charge-per-wire basis, quasi-static (no retardation, same as the inductive coupling), and that
**proximity effect is still absent from R(f)** — neither the inductance nor the capacitance path models
it, so a dense array's high-frequency resistance remains an isolated-round-wire assumption. Also note
that §3.6's framing ("what the array reduction does not include") now has a flag attached to it: with
`IncludeCapacitance` false the list is longer by one whole term, and the section should say which
statements are unconditional and which describe the flag-off configuration.

**9.3 — `### 5.2 What it stamps`.** Currently describes M coupled series branches and nothing else. Add
the shunt capacitors, the array-pair coupling capacitors split half at each end, and the `C12` series
term.

**9.4 — `### 5.4 The reference conductor — this is not optional`.** **This section becomes factually
wrong and must be corrected, not appended to.** Its whole argument is that `REF` declares and never
stamps, because `L_arr` is a loop inductance whose return is implicit. With capacitance on that is
false: the shunt capacitors stamp to `REF` when the pin is exposed and to node 0 otherwise (owner
decision, 2026-08-18), and the component is electrically 2M+1 terminal. State the two configurations
and which one is in force. `RefuseIfReturnPathUndeclared` is unchanged and still fires first — say that
too, because a reader will ask.

**9.5 — `### 5.5 Parameters`.** Add `IncludeCapacitance`, default true, and state the thing that is not
guessable: **it is the one wBond parameter whose default changes the answer for designs that already
exist.** Every other parameter's default reproduces prior behaviour.

**9.6 — `### 6.8 The inductance panel`.** The panel's reported quantity changes. Record: today's number
was the frequency-independent external `L_arr`; it becomes `L_eff(f)` from the shorted-far-end input
impedance; the frequency readout, its default and where the value is persisted; the above-SRF warning
state; and — importantly — **why `Im(−1/Y₂₁)/ω` was rejected**, since it is the obvious alternative and
is identically `L_arr` at every frequency for a π network. Someone will propose it otherwise.

**9.7 — `## 4 Measured performance`.** §4.1 is a table of measurements; add the kernel-cost row set from
§0.1 above and the resulting fill and reduction budgets. §4.3/§4.4 are the drag-frame sections: add the
rule that **capacitance is not in the drag loop** and why (`P` gets no rank-update machinery; C is
recomputed on drag end; C is far less geometry-sensitive than L, so the readout stays live on L and the
staleness clears on commit).

**9.8 — `## 14 Decisions`.** Add to *Resolved by the owner*, dated 2026-08-18: capacitance is included
by default behind one flag; **cross-wire capacitance is not a second parameter** and the reasoning is
§4.2's shielding number, not cost; shunt capacitance returns to `REF` when the pin is exposed and to
node 0 otherwise.

**Guardrail for this section.** Do not paste the brief into the design doc. `wbond.md` is the standing
document and this brief is a work order — the doc gets the *physics and the decisions*, in its own
voice and at its own level of derivation, and the gates, file lists and budgets stay here.
