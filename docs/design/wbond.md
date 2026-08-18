# circuitRF — wBond: the wirebond component and the wBond Editor

**Status:** Proposal — rev 1 · **rev 2 additions 2026-08-17** (WB40 revised; §5.5.1/WB44 controlling
parameters; §9.7/WB45 carried-or-linked; owner decisions O-8…O-12) ·
**Date:** 2026-08-17 · **Phase:** LW0 (design layer, ahead of LW1)

Companion to two existing documents, and it does not repeat either:

- [`mom-wirebond-kernel.md`](mom-wirebond-kernel.md) — **kernel W**, the 3D MoM solver (W1 quasi-static
  PEEC → W2 retarded thin-wire → W3 layered). Designed, not implemented.
- [`layout-view.md`](layout-view.md) — the layout editor substrate: `.clay`, the geometry model,
  hierarchy, snapping, the spatial index, LOD, DRC, the EM setup panel.

**The division of labour.** `mom-wirebond-kernel.md` owns the *solver*. This document owns the
*component and the instrument*: the data model, the array-basis algebra, the editor, the file format,
the assembly DRC, and the standalone app.

**WB1. wBond ships and is useful before kernel W exists.** The quasi-static Grover/images/array-basis
path specified in §3 is a complete instrument on its own — it is what the editor computes live, it is
what the schematic component stamps, and it needs no MoM code. Kernel W is a *second, higher-fidelity
engine behind the same component*, selected by a parameter. Nothing in §§3–11 may be blocked on it.

Spelling is **wBond** — lowercase w, capital B — everywhere in UI text, file extension, docs and type
names (`WBondComponent`, `WBondEditorViewModel`, `.wBond`).

---

## 1. The one-paragraph version

A **wBond component** is a container of 3D bond wires. Each wire is a polyline of ≥ 2 points in space
with a diameter and a metal; wires are grouped by the user into named **arrays** (G1, G2, D1, MT…).
The component's schematic symbol is **dynamic**: two pins per array, in on the left, out on the right.
It stamps a frequency-dependent coupled-branch impedance derived from the full wire-basis inductance
matrix — Grover filament formulae, method of images, Bessel skin effect — reduced onto the array basis
by a congruence transform on the *inverse* inductance matrix (§3.4). It is authored in the **wBond
Editor**, which is the Layout Editor plus a profile view and a live inductance panel, so the designer
lands wires on real bond pads and real package leads and watches the array inductance move as they
drag. It persists to a self-contained `.wBond` file and compiles as a standalone application.

---

## 2. The data model

### 2.1 The four objects

```
WBondDesign                 the root; one per wBond component instance
 ├─ GroundPlane             z = 0 by default, enable flag, and the declared reference (§3.2, §5.4)
 ├─ OperatingTemp           default 85 °C — load-bearing for R, so it is a field, not a constant
 ├─ Material[]              name, σ(20 °C ref), α₂₀, density — Au (default) / Al / Cu / Ag,
 │                          user-extensible; σ is *reported* at OperatingTemp (§2.3)
 ├─ LoopProfile[]           named, shared z-vs-span shape a wire may bind to (§6.2)
 └─ WireArray[]             name (G1, G2, …), colour, the profile it edits, member wires
     └─ Wire[]              Point3[] (≥ 2), Diameter, MaterialRef, ProfileBinding, Locked
```

**WB2. `Wire.Points` is the truth; everything else is derived.** A wire is *always* a 3D polyline —
that is what the solver consumes and what `.wBond` stores. A `LoopProfile` binding is a *generator*
that writes those points, exactly as a PCell writes shapes (`pcell-contract.md`). Breaking the binding
leaves the points untouched. This is the single most important structural decision in the document: it
is what makes "any geometry the user can imagine" (owner's requirement) compatible with "change the
whole array's profile by dragging one curve" (also the owner's requirement), and it is why §6.2's
answer works.

**WB3. Wire direction is data, not a rendering convention.** `Points[0]` is the **input** (current
enters); `Points[^1]` is the **output** (current exits). The sign of every mutual inductance depends
on it. It is rendered as a distinct off-colour dot, it is reversible by a command (`Reverse Wire`),
and reversing it negates that wire's row and column of off-diagonal mutuals — which the live panel
will show immediately, so the convention is *observable*, not merely documented.

### 2.2 Arrays

A wire belongs to **exactly one** array. That is a modelling constraint, not a UI limitation: §3.4's
reduction requires the mapping matrix **A** to have exactly one 1 per row. Wires the user has not
grouped live in an implicit array named `(ungrouped)`, one wire each — so the algebra never has a
special case, and an ungrouped wire simply gets its own pin pair.

Array names are the pin names on the symbol. Order is user-controlled (it sets pin order top to
bottom) and defaults to creation order.

### 2.3 Materials

**WB4a. The shipped default conductivity for every metal is its value at 85 °C, not 20 °C.** A wire
that carries current is never at room temperature, and 85 °C is itself optimistic for a high-power
part — but it is far closer than the handbook figure, and a default that is optimistic-but-close beats
one that is wrong by a quarter.

| | **σ at 85 °C (S/m) — the default** | σ at 20 °C (reference) | α₂₀ (1/K) | Note |
|---|---|---|---|---|
| **Gold** *(default material)* | **3.358 × 10⁷** | 4.10 × 10⁷ | 0.0034 | 4N; the RF packaging norm |
| Aluminium | 3.008 × 10⁷ | 3.77 × 10⁷ | 0.0039 | bond wire is normally Al-1%Si; σ is ~5–8 % below pure Al |
| Copper | 4.627 × 10⁷ | 5.80 × 10⁷ | 0.0039 | bare or Pd-coated; coating is thin vs. δ above ~1 GHz |
| Silver | 5.052 × 10⁷ | 6.30 × 10⁷ | 0.0038 | |

**This is a 22–25 % rise in R_dc over the room-temperature table** — not a rounding difference, and the
single strongest argument for making the change.

**WB4b. The temperature penalty is much smaller at RF than at DC, and the tool should say so rather
than let a user over-correct.** Deep in the skin regime R_ac ∝ 1/(σ·2πa·δ) with δ ∝ 1/√σ, so
**R_ac ∝ 1/√σ** — the 22 % DC penalty for gold becomes **+10.5 %** once the current is confined to a
skin. The full q-table of §3.5 traverses the whole transition automatically, so nothing needs
special-casing; the point is only that the numbers a user should *expect* differ by 2× between the DC
and the RF ends, and the panel reports which regime each frequency is in.

**WB4c. Temperature is modelled from the 20 °C reference internally; 85 °C is a default operating
point, not a new physical constant.** α_T is conventionally quoted at 20 °C, so the model stays
σ(T) = σ₂₀/(1 + α₂₀(T − 20)) — the 85 °C column above is *derived*, and shown because it is the number
that actually gets used. Setting T back to 20 °C therefore recovers the reference column exactly, with
no drift. Uses the existing `Temperature` plumbing in `src/Core/Devices/Temperature.cs`.

**WB4d. σ, α_T and the operating temperature are editable per material, and the table is a default,
not a constant.** Bond wire is alloyed and drawn, so its conductivity is not the handbook figure for
the pure metal; and 85 °C is a starting point, not a measurement of the user's part. A tool that
hard-codes either is quietly wrong at exactly the frequencies where R matters.

---

## 3. The physics

Everything here is **frequency-domain, quasi-static, and closed-form**. No meshing, no solver, no
Sommerfeld integral. That is what makes it fast enough to run inside a drag (§4).

### 3.0 Loop height — the definition

**A wire's loop height is its maximum z coordinate minus its minimum z coordinate.** Nothing else.
`Wire.LoopHeightNm` is the one place this lives; every other loop-height quantity in the codebase —
the profile's own `LoopHeightNm`, the panel readout, the profile view's vertical axis, the "Set Loop
Height…" prompt — is measured or set through it.

**It is deliberately NOT the rise above the chord, and the difference is not academic.** In
chip-and-wire the two feet are usually at different z: a die pad up to a substrate lead. The straight
line joining them is therefore tilted, and the crest's height above that tilted line is smaller than
its height above the lower foot. A wire-bonder is set up against the second number — loop height is
what an operator measures from the lower pad to the top of the loop — so reporting the first under
that name reads low on exactly the asymmetric loops where it matters most.

**Two consequences worth stating, because both are load-bearing:**

- **A wire's loop height can never be below its own foot drop.** With the feet |z₁ − z₂| apart, even
  a dead-straight wire measures that much. A requested loop height below that floor is clamped to it
  rather than refused (the wire is perfectly drawable) and rather than arched upward to fake the
  number (which would be worse).
- **A `LoopProfile` stores a normalised shape plus a loop height, so applying it must SOLVE for the
  amplitude it adds above the chord** rather than using the loop height as that amplitude directly.
  `LoopProfile.SolveAmplitudeNm` does it in closed form: every point's z is `chord(s) + A·height(s)`,
  so the wire's maximum is the maximum of a family of lines in `A` — non-decreasing and piecewise
  linear. Requiring that maximum to equal `min z + LoopHeightNm` bounds `A` by
  `(target − chordᵢ) / heightᵢ` at every point that rises at all, and the tightest of those bounds is
  the answer. One pass, exact, nothing to converge. When the feet are level the solve returns the
  loop height itself and the two definitions coincide — which is why a test suite built only on level
  feet cannot tell them apart, and why the asymmetric case is the one that must be gated.

### 3.1 Grover's two filament equations, and why there must be two

Every wire is a polyline; every polyline is a chain of straight filaments; the mutual inductance
between two wires is the double sum of the mutual inductances of their filament pairs. Grover
(*Inductance Calculations*, 2nd ed.) gives the two closed forms this needs.

**(a) The general case — skew filaments in any position** (Grover Ch. 19, after Campbell). Filaments
of lengths *l* and *m*, angle *ε* between their axes, shortest distance *d* between the axes, and
offsets *μ*, *ν* locating the filaments along their own lines:

```
M = (μ₀/4π) · 2·cos ε · [ T  −  Ω·d / (2·sin ε) ]

T = (μ+l)·atanh( m/(R₁+R₂) ) + (ν+m)·atanh( l/(R₁+R₄) )
       − μ·atanh( m/(R₃+R₄) ) −     ν·atanh( l/(R₂+R₃) )

Ω = atan2( d²cos ε + (μ+l)(ν+m)sin²ε , d·R₁·sin ε )
  − atan2( d²cos ε + (μ+l)·ν·sin²ε   , d·R₂·sin ε )
  + atan2( d²cos ε + μ·ν·sin²ε       , d·R₃·sin ε )
  − atan2( d²cos ε + μ(ν+m)·sin²ε    , d·R₄·sin ε )
```

with R₁…R₄ the four end-to-end distances. **Measured cost: 41.7 ns per filament pair**, single-
threaded, .NET 10 Release (§4.1).

> **Corrected 2026-08-07, during WB-A implementation.** Rev 1 of this document placed the Ω term
> *outside* the `2·cos ε` factor, as `(μ₀/4π)(2cos ε·T − Ω·d/sin ε)`. **That is wrong**, and the
> reason it is wrong is a one-line argument: the Neumann integral is
> M = (μ₀/4π)∮∮(dl₁·dl₂)/R with dl₁·dl₂ = cos ε·dt·ds, so **M is exactly cos ε times a strictly
> positive double integral** and must vanish identically for perpendicular filaments. The rev-1 form
> does not — it is wrong by **8 % at ε = 30°, 31 % at 55°**, and returns a large non-zero value at
> 90° where the true answer is zero.
>
> **Both forms agree to 9 digits as ε → 0**, because cos ε → 1 there. So §3.1's own skew→parallel
> convergence check — the headline oracle of rev 1 — passes with the wrong formula and cannot detect
> this. It was caught by a perpendicular-crossing test and confirmed against direct numerical
> integration of the Neumann double integral at 15°/30°/55°/120°/160° and in fully general position
> (agreement < 1e-9 relative). Both oracles are now permanent tests and neither is redundant with
> the other.

**(b) The degenerate case — parallel filaments** (Grover Ch. 17). Lateral separation *d*, filaments
occupying axial intervals [0, *l*] and [*s*, *s*+*m*]:

```
M = (μ₀/4π) · [ f(s+m) − f(s) − f(s+m−l) + f(s−l) ],   f(z) = z·asinh(z/d) − √(z²+d²)
```

**Measured cost: 28.4 ns per pair** — 32 % cheaper than (a), which matters because in a real wirebond
array *most* filament pairs are near-parallel.

**WB5. Both are implemented, with the crossover at ε < 10⁻⁶ rad — and the reason is not the one you
would guess.** Formula (a) does not blow up as sin ε → 0; the Ω·d/sin ε term is a genuine 0/0 whose
limit is finite, and `atan2` handles it gracefully. Measured convergence of (a) toward the closed-form
parallel answer (l = m = 1 mm, d = 0.2 mm, exact value 2.985 268 88 × 10⁻¹⁰ H):

| ε (rad) | formula (a) | |
|---|---|---|
| 10⁻² | 2.984 411 07 × 10⁻¹⁰ | physical ε² convergence, not numerical error |
| 10⁻³ | 2.985 260 29 × 10⁻¹⁰ | |
| 10⁻⁶ | 2.985 268 88 × 10⁻¹⁰ | **agrees to all 9 digits** |
| 10⁻⁸ | 2.985 268 87 × 10⁻¹⁰ | ~3 digits lost to cancellation in Ω |

So (b) exists for **speed and for exactness at ε ≡ 0**, not as numerical rescue, and the guard band
can be set far tighter than a cautious implementer would pick. Recording this is the point: an
unmeasured implementation typically sets the crossover at ε ~ 10⁻³ and silently eats a 3 × 10⁻⁶
relative error on every nominally-parallel pair in the design.

**WB6. `d` is never allowed below the wire's geometric mean distance, and that is a physical rule, not
a numerical guard.** Consecutive filaments of the *same wire* share an endpoint, so their axes
intersect and d = 0 — where (a) returns `NaN`. The physically correct separation is not zero: the two
filaments are the same physical conductor of radius *a*, so their effective separation is the
cross-section's GMD (§3.3). Clamping `d ← max(d, GMD)` is simultaneously the numerical guard and the
right answer, and it removes the need for Grover's third (coplanar-intersecting) form entirely.
Measured: (a) is stable down to d = 10⁻¹⁴ m and returns `NaN` only at exactly zero, so the clamp is
never load-bearing numerically — it is load-bearing *physically*.

### 3.2 Method of images — the sign rule that is easy to get wrong

The ground plane is a PEC at **z = 0**. Every wire is mirrored through it and the mutual inductance
between wires *i* and *j* becomes

```
L_ij = M( i , j )  −  M( i , image(j) )
```

**WB7. The image is constructed by mirroring the geometry through z = 0 *and reversing traversal
direction*. That single rule produces the correct sign for horizontal and vertical current alike, and
it is the only implementation that does.** The two cases people hand-derive separately:

- A **horizontal** filament (x₁,y,h) → (x₂,y,h) mirrors to (x₁,y,−h) → (x₂,y,−h); reversed, it runs
  −x. **Anti-parallel** ✓
- A **vertical** filament (x,y,0) → (x,y,h) mirrors to (x,y,0) → (x,y,−h); reversed, it runs from
  (x,y,−h) to (x,y,0), i.e. **+z**. **Parallel** ✓

This matches the finding recorded for the planar kernel's L9c phase (the current reflection
coefficient at a PEC is +1 for the z-component), and it is exactly the sign error that produces a
plausible-looking but 10–30 % wrong array inductance. **A regression test asserts both cases against
hand-derived signs**, because the failure is silent.

Validated against the closed form: a horizontal wire of length ℓ, radius *a*, at height *h*, using
d = GMD for the direct term and d = 2*h* for the image term, reduces to
L = (μ₀ℓ/2π)·ln(2h/a) — the textbook wire-over-ground result, and the `acosh(h/a)` oracle of
`mom-wirebond-kernel.md` §11.

**Doubling the work.** Images double the filament-pair count. Every cost figure in §4 already
includes the ×2.

### 3.3 Self-inductance is the mutual formula at the GMD — one code path

The partial self-inductance of a straight round filament is formula (b) evaluated against itself at
d = GMD of the circular cross-section:

- **GMD = a·e^(−1/4) = 0.7788 a** — uniform current (DC). This reproduces the internal-inductance
  term μ/8π automatically.
- **GMD = a** — full skin effect, current on the surface, no internal inductance.

Verified numerically (ℓ = 1 mm, a = 12.7 µm = 0.5 mil): formula (b) at d = 0.7788a gives
8.6385 × 10⁻¹⁰ H against Rosa's closed form (μ₀ℓ/2π)[ln(2ℓ/a) − 1 + ¼] = 8.6186 × 10⁻¹⁰ H — **0.23 %**,
which is Rosa's own ℓ ≫ a approximation error, not the GMD's.

**WB8. Use GMD = a (external inductance only) and add L_int(f) from the same Bessel evaluation that
produces R(f) (§3.5).** Do *not* make the GMD frequency-dependent to fake the transition — it gives
the right answer at both ends and the wrong one in between, and it double-counts against the Bessel
term. This keeps self and mutual on one code path and puts the entire frequency dependence in one
place.

### 3.4 The array-basis reduction — derived

This is the owner's novel array formulation. **The derivation is short and the result is exact under
the two stated assumptions.**

Let there be *N* wires and *M* arrays. Let **L** be the *N × N* wire-basis inductance matrix from §§3.1–3.3
(symmetric, positive definite, images already folded in). Let **A** be the *N × M* **0/1 mapping
matrix**, `A[i,k] = 1` iff wire *i* is in array *k* — exactly one 1 per row.

The wire-basis relation, with **V** the per-wire voltage drops and **I** the per-wire currents:

> **V** = jω **L** **I**

**Assumption 1 (equipotential bond pads).** Every wire in an array starts on one pad/lead and ends on
another, so all wires in array *k* share the same voltage drop *u_k*:

> **V** = **A** **u**,   **u** ∈ ℂ^M

**Assumption 2 (KCL at the pads).** The array's terminal current is the sum of its member currents:

> **J** = **Aᵀ** **I**,   **J** ∈ ℂ^M

Substitute. From the wire-basis relation, **I** = (jω)⁻¹ **L**⁻¹ **V** = (jω)⁻¹ **L**⁻¹ **A** **u**.
Apply assumption 2:

> **J** = **Aᵀ** **I** = (jω)⁻¹ (**Aᵀ L⁻¹ A**) **u**

Invert the *M × M* bracket:

> ### **u** = jω **L**_arr **J**,  where  **L**_arr = ( **Aᵀ L⁻¹ A** )⁻¹

That is the whole result: **a congruence transform on the inverse inductance matrix, inverted back.**
Equivalently, with the *inverse* inductance ("inductive susceptance") **Γ** = **L**⁻¹, the reduction is
a plain block sum, **Γ**_arr = **Aᵀ Γ A** — and because **A** is 0/1, that block sum is literally
"add up the sub-blocks of **Γ** belonging to each array pair". No matrix multiply is needed.

**Properties, all verified numerically:**

| | |
|---|---|
| **Symmetric by construction** | **L** symmetric ⇒ **L**⁻¹ symmetric ⇒ **AᵀL⁻¹A** symmetric. Reciprocity is *structural*, not a tolerance |
| **Positive definite** | provided **L** is SPD and every array is non-empty (**A** full column rank) |
| **Resistance never appears** | as the owner stated — the reduction consumes **L** and **A** only |
| **Reduces to the classic result** | *N* identical wires, self *L_s*, mutual *M*, one array ⇒ L_arr = (L_s + (N−1)M)/N. Confirmed: 4 wires, L_s = 1, M = 0.3 → 0.475 exactly |

**Worked check** (6 wires, 100 mil long, 0.5 mil radius, 20 mil over ground; wires 0–2 at x = 0/5/10
mil in array A0, wires 3–5 at x = 40/45/50 mil in array A1):

```
L (pH), wire basis            L_arr (pH), array basis
[2045  901  581 | 111 ...]    [ 1203.0   116.3 ]
[ 901 2045  901 | 139 ...]    [  116.3  1203.0 ]
[ 581  901 2045 | 177 ...]
```

Three 2045 pH wires in parallel *without* coupling would be 682 pH; the coupling raises it to 1203 pH.
The identity **u** = **L**_arr **Aᵀ L⁻¹ A u** was verified to machine precision for three independent
excitation vectors.

**WB9. The same algebra gives per-wire current sharing for free, and it should be surfaced.**
Back-substituting, **I** = **L**⁻¹ **A** **L**_arr **J**. For 1 A into array A0 above:

```
wire:      0        1        2   |    3        4        5
share:  0.3674   0.2648   0.3678 | −0.0187  0.0023   0.0164
```

Two things fall out that a designer pays money for. **Edge wires carry ~39 % more current than the
centre wire** — the classic array current-crowding result, here with no extra machinery. And the
*undriven* array carries a small circulating current (−0.019, +0.002, +0.016 A summing to zero),
because its wires are tied together at both ends and therefore form a shorted turn. Both are real
physics the reduction captures automatically; both are rendered as a per-wire colour ramp in the
editor.

### 3.5 Resistance and skin effect — accurate *and* fast, via one dimensionless parameter

The owner asked to spend time here. The insight that makes it cheap:

**WB10. The exact internal impedance of a round wire, normalised to its own DC resistance, is a
function of the single dimensionless parameter q = a/δ. Therefore it is a one-dimensional table, not a
per-wire-per-frequency Bessel evaluation.**

The exact solution (no approximation, no fit) is

```
Z_int(ω) = R_dc · (γa/2) · I₀(γa)/I₁(γa),   γ = √(jωμσ),  R_dc = 1/(σπa²)
```

and since γa = (1+j)·q with q = a/δ, δ = √(2/ωμσ), the ratio **Z_int/R_dc depends on q alone** — not
on radius, not on metal, not on frequency separately. Precompute `Z_int/R_dc` over q once, to double
precision, as a Chebyshev fit or spline; then every wire at every frequency is a table lookup plus two
multiplies (~10 ns) and is **exact to the fit tolerance, not to a curve-fit's tolerance**.

Range needed: for a 0.5 mil radius gold wire at 85 °C, q ≈ 1.5 at 100 MHz, 4.6 at 1 GHz, 29 at 40 GHz
(the 20 °C values are ~10 % higher — 1.6 / 5.1 / 32 — because δ ∝ 1/√σ, per WB4b). Tabulate
q ∈ [0, 60] and use the asymptote R_ac/R_dc → q/2 + ¼ + 3/(32q) beyond. Both ends have exact series so
the table can be validated against them rather than against itself.

This delivers **R(f) and L_int(f) from the same lookup**, which is what §3.3 (WB8) needs.

**Proximity effect — stated as a limit, staged as a ladder.** The above is the *isolated* wire. Real
arrays at 4–8 mil pitch with 1 mil wire have s/a ≈ 8–16, where neighbour currents redistribute the
current in each wire and raise R above the isolated value.

| tier | method | cost | when |
|---|---|---|---|
| **R1** *(v1)* | isolated Bessel table | ~10 ns/wire/freq | ships first; exact for skin, ignores proximity |
| **R2** | R1 × a two-wire Butterworth/Dwight proximity factor, superposed over neighbours | ~N·k ns | cheap correction; good to ~1 % for s/a > 6 |
| **R3** | multi-filament partition — each wire → 1 core + K ring filaments, solve the (K+1)N system | (KN)³ | exact skin *and* proximity, and it **reuses §3.1 unchanged**: ring filaments are just more filaments |

**WB11. R3 is not a new formulation — it is the same Grover kernel with more filaments — so it is
available for a verification run even if it is never the live path.** That is the honest way to bound
R1/R2's error: measure it against R3 on a real array rather than quoting a textbook.

**WB12. The mesher warns when any wire pair falls below s/a = 6** — this is `mom-wirebond-kernel.md`'s
RW17, and it applies to the quasi-static path for the same reason.

### 3.6 What the array reduction does *not* include — stated plainly

The two assumptions are good, and they are assumptions.

- **Assumption 1 fails when the bond pad is not equipotential.** A long lead frame finger, or an array
  spanning 300 mil of a package lead, has real impedance between the landing points. The reduction
  then over-couples the array — it forces a voltage equality the structure does not enforce.

  **WB9a. This is documented, not warned about.** *(Owner decision, 2026-08-07.)* No message fires in
  the editor or the simulator; the limitation lives here and in the user documentation. The reasoning
  is worth recording, because "add a warning" is the reflex: **there is no threshold that separates the
  good case from the bad one.** Whether a 60 mil landing span matters depends on the lead's sheet
  resistance, the frequency, and how much of the array's total impedance the pad represents — none of
  which the quasi-static path knows. A warning fired on span alone would be noise on most designs and
  silent on some real failures, which is worse than a clearly-stated limit that an engineer applies
  with judgement. The array's landing span *is* shown in the panel as an ordinary readout (§6.8),
  where it informs without asserting.
- **Resistance is excluded from the L reduction by construction** (owner's specification, and it is
  what makes L_arr frequency-independent and cacheable). This limits the **live readout only** — the
  simulation stamp uses the exact complex reduction **Z**_arr(ω) = (**AᵀZ**(ω)⁻¹**A**)⁻¹ (§5.3,
  WB19a), so the shipped S-parameters carry no such approximation. The readout's error is second-order
  except in high-loss / low-Q arrays, and the panel reports R/ωL so the user can see when it is being
  stressed (WB19b).
- **No radiation, no retardation.** A 100 mil arc is λ/10 at ~11.8 GHz; segmented into 6 filaments it
  is a distributed ladder good well past that, but the *coupling* is quasi-static. `mom-wirebond-kernel.md`
  §4.1 quantifies this; kernel W2 removes it.
- **The ground plane is infinite, flat and perfect.** A stepped or split ground under the wires is a
  first-order error (30–50 % on L for a plane split — `mom-wirebond-kernel.md` §7.4). The editor
  refuses to silently assume: see §5.4.

---

## 4. Measured performance, and the architecture that follows from it

The owner's stated worst case is **600 wires × 6–7 points**, dragged at high frame rate. Everything
below is **measured on this machine**, .NET 10 Release, single-threaded, not estimated.

### 4.1 The measurements

600 wires × 6 filaments = 3,600 filaments → 12,960,000 ordered filament pairs, ×2 for images.

| operation | measured | note |
|---|---|---|
| Grover skew kernel (a) | **41.7 ns** / pair | |
| Grover parallel kernel (b) | **28.4 ns** / pair | |
| **Cold full fill** of **L** (600 wires, symmetry + images) | **~0.54 s** | one-time; embarrassingly parallel → ~0.1 s on 8 cores |
| **One wire moved** — recompute 2N−1 wire-pair blocks | **~3.6 ms** | |
| **50-wire group moved** — 50(2N−50) blocks | **~173 ms** | ← the bottleneck |
| Cholesky of **L** + 12 solves ⇒ **AᵀL⁻¹A** (N = 600) | **22.9 ms** | |
| **Rank-1 Cholesky update** (N = 600) | **0.144 ms** | |
| **AᵀL⁻¹A** block sum from an explicit inverse (M = 12) | 1.05 ms | |
| Naive full 600×600 inverse (Gauss–Jordan) | 262 ms | for contrast only — never do this per frame |
| Cholesky + solves at N = 1,200 | 181 ms | headroom beyond the stated worst case |

### 4.2 The finding that shapes the design

**WB13. The linear algebra is not the bottleneck — the Grover fill is.** This inverts the intuition
that a 600 × 600 matrix problem is dominated by its factorisation. A rank-1 Cholesky update costs
0.144 ms; recomputing the *entries* that changed costs 3.6 ms for one wire and 173 ms for fifty. Every
optimisation effort belongs in the fill and its caching, and essentially none in the solve.

### 4.3 The incremental path — a single-wire drag frame

Moving wire *k* changes exactly row *k* and column *k* of **L**. That change is
Δ**L** = **e**_k **r**ᵀ + **r** **e**_kᵀ — **rank 2**, whatever N is.

```
1. recompute 2N−1 wire-pair blocks (Grover, ×2 image)      2.34 ms
2. rank-2 Cholesky update of the factor of L               0.31 ms
3. 12 triangular solves ⇒ Γ_arr = AᵀL⁻¹A                   2.62 ms
4. invert M×M, publish L_arr and the current shares        µs
                                                        ─────────
                                              total      5.27 ms  ⇒ 60 fps with headroom
```

> **Measured 2026-08-07 in WB-A**, N = 600, M = 12, replacing rev 1's estimates — which the total
> matched almost exactly, though the split differs (the solves cost more and the fill less than
> predicted). **The one trap this uncovered is worth stating: step 3 must use the factor step 2
> maintains.** Reducing with a *fresh* factorisation instead costs 22.7 ms rather than 2.62 ms and
> turns the whole frame into 25.4 ms — it throws away the entire point of the incremental path, and
> it does so silently, since the answer is identical.
>
> **Measured multi-wire crossover: 1, 2 and 5 simultaneously-moving wires fit a 16.7 ms frame; 10 do
> not** (28.8 ms). That confirms §4.4's estimate of 5–10 and fixes it at between 5 and 10 on this
> machine.
>
> **Rank-2 drift is not a concern:** after ~1,260 cumulative updates the maintained factor still
> agrees with a fresh factorisation to better than 1e-6 relative — ~1e-4 pH on a 500 pH array. A
> periodic refactorisation is therefore not needed; a regression test pins the growth so that
> conclusion cannot quietly expire.

**WB14. Maintain a Cholesky *factor* of L, not an explicit inverse.** Rank-k updating the factor is
O(kN²) and numerically stable; maintaining L⁻¹ by Sherman–Morrison is comparably fast but drifts, and
the M×M answer only ever needs M triangular solves, never the full inverse.

### 4.4 The multi-wire drag, where it actually gets hard

A 50-wire group is 173 ms of fill (6 fps) + 14 ms of rank-100 update. Parallelising the fill across 8
cores gives ~22 ms + 14 ms ≈ 28 fps. **Marginal, so the design does not pretend otherwise.** Three
mitigations, in order of value:

1. **Rigid-motion invariance.** Under a rigid *translation* of a selection, intra-selection direct
   mutuals are unchanged, and under a *horizontal* translation the intra-selection *image* mutuals are
   unchanged too (the images translate rigidly with it).

   > **Corrected 2026-08-07, measured during WB-A.** Rev 1 estimated ~8 % for a 50-of-600 selection
   > and 33 % for 200-of-600. The **measured** savings are **4.4 %** and **20.1 %** — about half,
   > because the rev-1 arithmetic counted each symmetric wire pair twice. A move of *k* wires visits
   > `k·N − k(k−1)/2` blocks and skips `k(k+1)/2`, so the saving is `≈ k/(2N)` rather than `k/N`.
   > Still worth having for a large selection, and still not decisive for a small one.

   Only the *horizontal* case saves anything in practice: recovering the direct half after a general
   rigid translation needs it cached from before the move, and recomputing the whole block in one pass
   is cheaper than two passes over the same filament pairs.
2. **Parallel fill.** The block recompute is a pure map over independent wire pairs. This is the
   largest single win and should be built in from the start, not retrofitted.
3. **WB15. The adaptive-quality ladder, with freeze-and-snap at the bottom.**

   > **Corrected 2026-08-07, WB-C3.** Rev 1 implies the ladder can pick its rung from the size of the
   > selection. **It cannot** — a cost model across (moving wires, total wires) is not calibratable
   > from the measurements: one wire of 600 costs ~3.9 µs per wire-pair block while 200 wires of 200
   > costs ~7.9 µs, a 2× disagreement. The gap is structural, not noise — past `k > N/12` the
   > incremental path stops rank-2 updating the Cholesky factor and refactorises instead, and the
   > fill's cache behaviour changes with the shape of the block set. A predictor fitted to either
   > point is wrong at the other by 2–3×, which puts the ladder on the wrong rung exactly where it
   > matters. **The ladder therefore observes measured frame times and steps**, with hysteresis
   > (immediate step down, three comfortable frames to step up) — no machine model, and it survives a
   > faster or slower one. During a large drag,
   degrade the *geometry*, not the algebra: represent each moving wire by its chord (1 filament
   instead of 6) — a **36 ×** reduction, taking the 50-wire case to ~4.8 ms — then recompute at full
   fidelity on mouse-up. The panel marks the readout *provisional* while degraded. This is the exact
   pattern harmonicaRF's `FrameScheduler` already established, and the L-readout is a scalar the user
   is watching for *trend* during a drag and for *value* after it.

**WB16. The crossover between the exact incremental path and the degraded path is measured during
implementation and exposed as a setting, not fixed now.** The measurements above put it somewhere
around 5–10 simultaneously-moving wires on this machine.

### 4.5 Rendering

3,600 line segments and 4,200 dots is trivial for Skia in a batched path — the wire overlay is not the
rendering risk. **The rendering risk is the layout underneath it**, which is the existing
`LayoutRenderer` with its spatial index, LOD, and path/instance caches, already characterised in the
root `CLAUDE.md` and gated by `LayoutSpatialIndexPerfTests`. wBond inherits that work and its
`Category=Benchmark` coverage; it does not re-solve it.

**WB17. The wire overlay is a separate render pass with its own cache, invalidated only by wire
edits.** A wire drag must not invalidate the layout's path cache — that is the one way to turn a cheap
overlay into a 500k-shape redraw.

---

## 5. The schematic component

### 5.0 The component carries its design — it does not reference a `.wBond`

**WB17b. A placed wBond stores the whole design as a hidden `Design` parameter; there is no `File`
parameter, and nothing is resolved at render time.** *(Implemented 2026-08-12; supersedes any earlier
reading of §9.2 route 2 as a file reference.)*

A `.wBond` is wires **plus**, optionally, the layout artwork they were drawn over — cells, rectangles,
MLINs. A schematic component has nowhere to put artwork, so pointing one at a whole `.wBond` asks it
to reference something most of which it cannot express. It also made a freshly-dropped component
reference nothing at all, which is what the "Not Found" placeholder was reporting: correctly, and
uselessly.

So the component owns its wires:

- A dropped wBond arrives carrying `WBondEmbedding.DefaultDesign()` — one array, one wire — which
  renders, wires up and simulates immediately. **"Not Found" is unrepresentable**, because there is
  nothing to find.
- The blank wBond editor starts from the **same** definition, so "what a new wBond is" cannot come to
  mean two different things.
- A schematic is self-contained: no workspace-relative path to break when a design is moved, copied
  or sent to someone else.
- **Layout artwork is deliberately dropped from the payload.** A schematic component is the wires;
  carrying a copy of someone's artwork inside every placed instance is exactly what this avoids. The
  artwork route is §9.2 route 3, which makes it a real layout view instead.

The payload is **base64 with the padding stripped**. Base64 because the value must survive both
`.csch` (a JSON string) and `.cnl` (whitespace-delimited, with no way to escape a quote inside a
quoted token) — a design's JSON is full of quotes. **Unpadded** because `CnlReader` reads a token
ending in `=` as `name=` with an empty value and glues the *next* token on as that value (the
empty-parameter-value defect recorded in `src/Core/CLAUDE.md`); a padded payload therefore silently
swallows whichever parameter follows it on the instance line — which a swept `LoopHeight` always
does. `WBondEmbedding.TryDecode` re-pads on the way in, so a hand-authored or older padded payload
still reads.

**A hidden `Arrays` parameter records the array list the instance's wiring was drawn against**, so a
re-import that reorders arrays is *reported* rather than silently re-pointing wires — see §9.2.

### 5.1 The dynamic symbol

**WB18. The symbol has exactly two pins per array — input on the left, output on the right — and it is
regenerated whenever the array list changes.** Pin name = array name (`G1`, `G1_out` or `G1.i` /
`G1.o`; naming to be settled with the symbol generator). Pin order follows array order. Ungrouped
wires each contribute a pair.

This is generated geometry, so it uses the same content-addressed generated-cell machinery as PCells
(`GeneratedCellStore`), and it inherits the lesson recorded there: **the generator carries a content
version, so a generator fix invalidates stale on-disk symbols.** Without it, a user who reorders arrays
gets a symbol with correctly-named pins wired to the wrong nets — silent, and exactly the MTee
orientation failure mode already logged in `project-brief-L5-followups`.

Symbol body shows the wBond name, array count, wire count, and total wire length — enough to
recognise it on a page without opening it.

### 5.2 What it stamps

**WB19. wBond stamps M coupled branches — one per array — via branch-current expansion, the same
mechanism `SnpModel` uses (`linear-engine.md` §4.1).** Branch *k* runs from array *k*'s input pin to
its output pin, and

> **u** = **Z**_arr(ω) · **J**

with **Z**_arr assembled per §5.3. It is `MutualInductanceModel` generalised from 2 coupled branches
to M, and `ModelKind.Linear`. In HB it contributes at every harmonic like any linear element; nothing
in the linear/nonlinear partition changes.

### 5.3 What the stamp reduces — the exact complex reduction

**WB19a. The simulation stamp uses the exact complex reduction**

> **Z**_arr(ω) = ( **Aᵀ Z**(ω)⁻¹ **A** )⁻¹,  **Z**(ω) = **R**(ω) + jω( **L** + **L**_int(ω) )

**not** R and L reduced independently. *(Owner decision, 2026-08-07.)*

This is exact under assumptions 1 and 2 — the *same* two assumptions that produce L_arr, applied to
the full impedance rather than to its imaginary part. The alternative, **R**_arr = (**AᵀR**⁻¹**A**)⁻¹
combined with jω**L**_arr, is correct only when R ≪ ωL, and reduces R and L on *inconsistent current
distributions*: it implicitly lets the current share one way for the resistive part and another for
the inductive part. Since the array's current sharing is physically set by R and L **together**, that
inconsistency is exactly what the exact form removes. It matters most where it is easiest to be wrong
— low frequency, lossy aluminium arrays, and the 85 °C operating point of WB4a, all of which raise
R/ωL.

**Cost is the same order:** one complex N × N factorisation per frequency point (~60 ms at N = 600
against ~23 ms real), which is fine for a swept simulation and far too slow for a drag. So the split
stands:

| | uses | why |
|---|---|---|
| **live editor readout** | **L**_arr = (**AᵀL⁻¹A**)⁻¹ | frequency-independent, incrementally updatable, ~5 ms/frame (§4.3) |
| **simulation stamp** | **Z**_arr(ω) = (**AᵀZ**(ω)⁻¹**A**)⁻¹ | exact; one factorisation per frequency point |

**Measured 2026-08-07 in WB-B.** The exact reduction differs from reducing R and L independently by
**5.3–6.0 % on resistance** and **0.4–2.6 % on reactance**, across gold and aluminium at 100 MHz–10 GHz
(R/ωL from 5.9e-2 down to 3.8e-3). Two things that were not obvious before the measurement: the
**resistance gap barely moves with frequency** — it is ~5–6 % throughout, because the current sharing
the two methods assume differs by roughly that much regardless of regime — while the **reactance gap
tracks R/ωL** and falls away at high frequency, which is the behaviour §5.3 predicted. So the
independent route is a poor approximation for R at any frequency and a good one for L above a few
GHz. Per-frequency cost of the exact route, N = 600: **55.8 ms**, i.e. 11.2 s for a 201-point sweep —
which settles §5.3's unmeasured "fine for a swept simulation" as true.

**WB19b. The two must agree in the limit, and a test asserts it.** As R → 0, Z_arr/jω → L_arr
identically — approached as **1/√σ**, not 1/σ, because deep in the skin regime R and L_int both scale
as 1/√σ (the same square-root law as WB4b). The test asserts that convergence *exponent* rather than a
fixed bound, which is a sharper statement: a wrong reduction can sit under a threshold by accident but
will not obey the exponent. This is a free consistency oracle between the editor's fast path and the simulator's
exact path — the kind of check that catches a wrong reduction long before a user does. The panel
additionally reports the array's **R/ωL at the design frequency**, so a user can see at a glance
whether the readout's L-only assumption is even being stressed.

### 5.4 The reference conductor — this is not optional

`mom-wirebond-kernel.md` RW13: *a port carries an explicit reference conductor, and the UI does not
permit a port without one.* The array reduction inherits this — **L**_arr is a *loop* inductance whose
return is the image plane at z = 0.

**WB20. The wBond symbol carries a `REF` pin that must be connected, and the component refuses to
stamp if the ground plane is disabled and no array is declared as the return.** Two legitimate
configurations:

- **Ground plane enabled** (default): return is the plane, `REF` ties to the plane's net (usually
  node 0). The image terms in §3.2 *are* the return path.
- **Ground plane disabled**: the user must nominate one or more arrays as the return (downbonds —
  RW14). The reduction then runs on the remaining arrays with the nominated arrays as reference, and
  the ground wires get their real inductance and coupling instead of a free perfect plane.

Reporting "the inductance of this bond wire" without a stated return path is the single most common way
a bondwire model is wrong, and the UI states the active return in the panel header at all times.

### 5.5 Parameters — and why they must be expression-bound

The Parameters dialog exposes: ground-plane enable and z, global diameter and material overrides,
temperature, the fidelity mode (`Quasi-static` / `MoM W1` / `MoM W2` / `Measured .snp`), the R-model
tier (§3.5), and **every bound `LoopProfile`'s parameters** — loop height, kink height, span, segment
count.

**WB21. These are ordinary circuitRF expressions, so loop height is sweepable.** `parametric_sweep`
over `X1.G1.LoopHeight` re-runs the profile generator, re-fills **L**, and re-solves — and per §4.1 a
cold fill is 0.54 s while an incremental one is milliseconds, so a 21-point loop-height sweep is
seconds, not minutes. This is the feature a PA designer will actually use the tool for, and it exists
because WB2 made the polyline generated rather than hand-placed.

#### 5.5.1 Controlling parameters on the SCHEMATIC symbol *(added 2026-08-17)*

WB21 is about the wBond editor's own dialog. The owner's question is the schematic one: *a placed wBond
exposes no loop height, so once schematic-based tuning, `VAR` sweeps and optimisation arrive there is no
handle to turn.* Owner's own framing, and it is the right one: **these are not the geometry, they are
*controlling* parameters — the 3D coordinates are the truth, and these manipulate them.**

**WB44. A placed wBond exposes CONTROLLING parameters that regenerate geometry at elaboration and never
write back into the design.** Loop height, wire diameter, wire material, operating temperature and the
ground-plane switch. Four properties make this work, and none may be traded away:

1. **They are an override layer, not an edit.** The `.wBond` (or the embedded payload) stores geometry
   **as drawn**; a controlling parameter reshapes the *decoded* design on its way to the solver. WB2 is
   untouched — `Wire.Points` is still the truth — and a 21-point sweep re-elaborates 21 times while
   mutating the stored design **zero** times. This is also what makes them survive §9.6: Layout →
   Schematic replaces the *base* geometry, and the override still sits on top of it.
2. **Absent means "as drawn."** A controlling parameter is **optional and unset by default**, never
   defaulted to a number. A wBond that shipped with `LoopHeight = 20 mil` in its defaults would silently
   regenerate every existing design's wires to 20 mil on its next run.
3. **Scope is the ARRAY, suffixed** — `LoopHeight_G1`, `Diameter_G1`, `Material_G2`; unsuffixed applies
   to every array. Array names **are** the pin names on the symbol, so the array is the only sub-scope a
   schematic user can see. (A `LoopProfile` may be shared by two arrays; overriding one array must
   therefore give that array its own copy of the profile rather than dragging the other array with it —
   free, because the override is per-elaboration and never persisted.)
4. **Loop height is a length, diameter is a length, material is a name.** So the first two sweep and the
   third parameterises. *This only became possible on 2026-08-07*: a length-dimensioned global could not
   previously be swept and failed **silently** — the unit table had no symbol for the metre, `"m"` being
   the SI prefix *milli* — so a loop-height sweep clamped to the wire's own foot drop and drew a
   perfectly plausible flat curve. WB21 was blocked on that and is no longer.

**WB44a. Span is NOT among them, and the reason is structural rather than schedule.** Span is not a
profile property at all — it is the distance between the two feet, i.e. *where the pads are*. Two
consequences follow from §6.2.1, which already settled both for the layout gesture: an array's span
scales **by factor, not to a common value** (WB24c), because an absolute span would silently flatten a
fan-out whose wires deliberately differ; and changing span **moves a bonded foot and may take it off its
pad** (WB24b), which the layout editor makes safe with live snapping and a foot highlight and the
schematic cannot, being blind to the artwork. A schematic span control is therefore honest only as a
dimensionless scale factor, with a stated pinned foot and with §8's loop-height-vs-span envelope
reported on every solve. **Owner decision, 2026-08-17: deferred** — the other five carry the use case,
and span is the only one of the six needing new geometry rules rather than exposure of existing ones.

---

## 6. The wBond Editor

### 6.1 What it is built from — the owner's own suggestion, taken

The owner asked: *"Perhaps the wBond Editor is actually the Layout Editor modified with a profile view
and array inductance panel."*

**WB22. Yes — the wBond Editor is the Layout Editor with two additions, not a new editor.** The
requirements list makes this near-inevitable: render real cells/PCells/primitives, edit them in place,
snap to corner/midpoint/centroid/intersection, descend into sub-cells, drag-drop from the project tree,
change units, marquee-select. Every one of those already exists and is tested
(`LayoutSnapFeatures.cs` already implements all four snap kinds; `LayoutUnits.cs` already handles
nm/µm/mm/mil; `CellHierarchy.cs` handles descent; `LayoutEditorViewModel.PaletteDrag.cs` handles
drag-drop). Reimplementing them would be several thousand lines of duplicated, separately-buggy code.

The layout is:

```
┌──────────────┬──────────────────────────────────────┐
│              │  PROFILE VIEW   (XZ / YZ / any az) │
│  ARRAY       │  Z is always up                      │
│  INDUCTANCE  ├──────────────────────────────────────┤
│  PANEL       │                                      │
│  (full       │  LAYOUT VIEW    (X-Y)                │
│   height)    │  = the real Layout Editor canvas     │
│              │                                      │
└──────────────┴──────────────────────────────────────┘
```

The split is user-adjustable and either view can be collapsed. The panel is docked left, full window
height, per the owner's specification.

**How wires are drawn — the same in both views.** Each wire point is a **dot**; consecutive points are
joined by a **straight line**. Both views draw the same wire from a different projection, so a wire is
recognisable across them.

**WB22a. Dot size and line thickness are user settings, and line thickness has a `= wire diameter`
mode.** In that mode the drawn line is the wire's actual diameter *in design units*, scaled with zoom
like layout geometry rather than held constant in pixels — which is the entire point: the owner wants
to see the wirebond's real bulk against the bond pad and the neighbouring wires it must clear. The two
modes are genuinely different and both are needed:

- **constant-pixel** (default) — the wire stays visible when zoomed out over a whole package, where a
  1 mil wire is sub-pixel;
- **true-diameter** — the wire reads as physical metal, which is what makes wire-to-wire and
  wire-to-pad clearance judgeable by eye before the DRC is run.

A floor keeps true-diameter mode from vanishing at low zoom (draw at `max(diameter, 1 px)`), and the
mode is per-view, since the profile view and the layout view are usually at different zooms.

**The input point is drawn in a subtle, distinct off-colour** so the start of every wire is
identifiable at a glance — this is the visual face of WB3, and it matters because the mutual-inductance
signs depend on it.

**WB23. The wire layer is an *overlay on* the layout canvas, not a shape type in `.clay`.** This
preserves `mom-wirebond-kernel.md` RW15 and the PRD's narrowed non-goal: no 3D shape enters `.clay`,
no volumetric mesher is written, and the layout canvas stays 2D. It also means a wire drag invalidates
only the overlay (WB17).

### 6.2 The profile view, and the odd-ball wire problem — recommendation

The owner's framing: wires in an array may have different profiles, different angles, even different
point counts, and the tool must not force uniformity — but dragging one profile curve must change the
whole group. Two "outs" were offered (one designated representative with best-effort propagation; or
odd-balls get their own suffixed row). **Both are heuristics layered on an under-specified model. The
recommendation replaces the model instead, and then both outs stop being needed.**

**Three ideas, composed:**

**(1) Normalised-span parameterisation.** Plot every wire's z against its position along its own XY
path — cumulative XY arc length, normalised to [0,1] or shown absolutely.

> **Confirmed load-bearing 2026-08-07, in WB-C1.** "XY path" is not a simplification, it is the only
> self-consistent choice, and the first implementation got it wrong by projecting in 3D. A 3D chord
> parameter makes a point's **loop height feed back into its own span position**: raise the loop and
> every point slides along the chord, so "scale the height" stops being a well-defined operation.
> Measured with the 3D projection, a nominal **1.5× height scale came out as 1.498×** and a 2×
> similarity scale as 1.987×. With span taken from the horizontal path the two are independent and
> both are exact to the DBU grid. `WireEdits.ChordParameter` is the single definition, shared by the
> scaling and the profile view so they cannot drift. This makes **wire angle and
wire length stop being profile differences at all.** A wire at 37° and a wire at 90°, 60 mil and 140
mil long, have the same *profile* if they have the same z-vs-span shape — which is exactly what a
packaging engineer means by "the same loop". The only genuine odd-balls left are differing point
counts and XY backtracking. The x-axis has a toggle: **absolute span** (true geometry; wires of
different length terminate at different x) and **normalised span** (shapes overlay). Default absolute
when an array's spans agree to within a tolerance, normalised when they do not.

**(2) `LoopProfile` as a first-class named, shared object** (WB2). A wire is either **bound** to a
profile — its points are generated, it moves when the profile is edited, it is drawn as part of the
group's curve — or **free**, having been individually dragged, in which case it detaches and is drawn
as its own curve. This is the owner's out (2), but arrived at as a *consequence of an explicit
binding* rather than as a heuristic classification of "odd-ball". The user always knows why a wire is
separate, and can re-bind it (resampling it onto the profile) with one command. It also gives the
parametric ball-bond and wedge-bond profiles of `mom-wirebond-kernel.md` §9.1 a home, and it is what
makes WB21's loop-height sweep possible.

**(3) Envelope rendering for clutter.** The owner asked to limit how many wires the profile view
draws. Draw, per array: **one editable profile curve** plus a translucent **min/max envelope band**
over its bound members. The user sees the spread without 200 overlaid polylines, and any free wire
outside the band is drawn individually. Cost is O(members) to compute the band and O(1) curves to
draw. Additionally, **the profile view draws only the arrays touched by the current selection plus
pinned arrays** — the default clutter level is then near zero and the user opts in to more.

> **The "one editable profile curve" half of that is load-bearing, not decorative (2026-08-16).** The
> band is a *min/max envelope over the members*, so whenever the members share a shape min equals max
> and the band is a zero-area path that fills nothing. Two ordinary cases hit it: a one-wire array, and
> any array mid-drag once `QualityLadder` has collapsed its members onto their chords (WB15). Drawing
> only the band therefore renders such an array as **nothing at all** — which is how it shipped, and
> what the owner saw as the profile view "disappearing" during a layout drag.

**WB24. Dragging the profile curve edits the `LoopProfile`, which regenerates every bound wire.
Dragging an individual wire's curve detaches that wire from the profile, with an undoable "N wires
detached" toast.** No best-effort propagation heuristic exists, because there is nothing to guess.

#### 6.2.1 Alt-drag — proportional reshaping

A plain drag in the profile view moves the grabbed point. **Alt-drag rescales the whole profile while
holding its shape**, which is how a packaging engineer actually thinks: *the same loop, taller* or
*the same loop, longer* — not *this one vertex, elsewhere*.

**WB24a. Alt + vertical drag scales loop height about the chord. The wire's highest point follows the
cursor; every other point moves in proportion to its own height above the chord, so the shape is
preserved exactly and the bonded feet never move.**

The chord is the straight 3D line from `Points[0]` to `Points[^1]`. For each point define its height
above that chord, h_i = z_i − z_chord(s_i), where s_i is its normalised span position. Then

> h_i′ = s · h_i,   s = h_target / h_max

where h_target is the cursor's height above the chord. The endpoints have h = 0, so **s multiplies
them by zero and they cannot move** — which is the property that makes this correct rather than merely
convenient. It also handles the case that motivates chip-and-wire in the first place: **the two feet
are usually at different z** (die surface to package lead), and scaling about a single flat baseline
would drag one foot off its pad. Scaling about the chord cannot.

**WB24b. Alt + horizontal drag scales span. The foot on the side being dragged follows the cursor
along the chord direction; the opposite foot is pinned; interior points keep their normalised span
positions and their absolute heights above the chord.**

So the loop keeps its *shape* and its *height* while the wire gets longer or shorter — which is the
physically honest default, because a bonder running the same loop program over a longer span does not
scale loop height linearly with span. (**Alt+Shift+drag** performs a true similarity scale, height and
span together, for the cases where that is what is wanted.) Two consequences are surfaced rather than
hidden: a span change **moves a bonded foot and may take it off its pad**, so snapping stays active
during the drag and the foot highlights when it is not on a pad; and span is the independent variable
of the loop-height-vs-span DRC envelope (§8), so the envelope check updates live and the profile turns
red the moment the pair leaves it.

**WB24c. On a bound profile curve, alt-drag rescales the entire array — and for span it scales each
member's span by the same *factor*, not to the same *value*.** This is the direct answer to the
owner's requirement that a drag in the profile view change the whole group at once. Multiplying by a
factor preserves an array whose wires deliberately have different spans (a fan-out from a common pad);
setting a common absolute span would silently destroy it.

> **Clarified 2026-08-17 — "the entire array" means the ARRAY, and it was implemented as the
> `LoopProfile`.** Those are not the same set: a profile is an editor-internal sharing mechanism a
> schematic user never sees (O-10) and it routinely spans every array in the design — a design built
> from `WBondEmbedding.DefaultDesign` has ONE profile that every array references — so alt-dragging a
> wire in G1 rescaled G2's wires as well, and wrote the new height onto the shared profile on the way
> out. The unit is now the array (`WireMesh.ArrayOfWire`), and nothing writes a profile.
>
> **The LAYOUT view deliberately does NOT promote to the array** (`ScaleSelection(wholeArray: false)`).
> The profile view draws a group as one superimposed shape under one envelope band, and a bond group is
> one loop program on one bonder — reshaping one member and leaving its siblings is not a thing the
> machine can do. In the layout each wire is drawn at its own place among the pads and an alt-drag
> stretches *that* wire's span onto *that* pad, so its members are not interchangeable.

All three operations are single undo steps, and all three go through the incremental fill of §4.3 —
they move many wires at once, so they take the WB15 quality ladder like any other large edit.

**A residual honest limit:** a wire whose XY path backtracks on itself has a non-monotone span and
cannot be drawn in the profile view without self-overlap. Such wires are drawn free, flagged in the
panel, and excluded from envelopes. They are legal geometry and they solve correctly; they are just
not profile-editable. That is a real edge and it is stated rather than prevented.

**Profile view projection.** YZ, XZ, or an arbitrary azimuth, with **Z always up**, per the owner.
The azimuth is a control on the view; the recommended default is **the mean chord azimuth of the
displayed arrays**, which makes an array of wires at 37° render as a clean side-on loop rather than a
foreshortened one.

### 6.3 Selection, drag, and keyboard

| interaction | behaviour |
|---|---|
| click point / segment | select it |
| shift-click | add to selection |
| double-click, or hold **`w`** and click | select the whole **wire** |
| triple-click, or hold **`g`** and click | select the whole **array group** |
| marquee **left → right** | enclose semantics (existing `ComputeMarqueeSelection`, solid outline) |
| marquee **right → left** | crossing semantics — **selects the entire wire for any wire with a point in the box** (dashed outline) |
| drag point or segment | free move; segments stay attached at both ends by construction (moving a segment moves its two endpoints) |
| **arrow** | move selection **1 mil** |
| **shift+arrow** | move selection **5 mil** |
| **↑** in profile view | **+z** |
| **↑** in layout view | **+y** |
| **→** in profile view | **+span — along the view's horizontal direction** (see the note below) |
| **→** in layout view | **+x** |
| **Esc** | disarm Draw Wire / Rotate (abandoning any half-placed wire) → else clear the selection → else pass through |
| **Del** / **Backspace** | delete the selected wires (undoable and redoable) |
| **V** | cycle the visible canvases: both → profile only → layout only |
| **I** | show/hide the Array Inductance panel |

> **`V`, not `Tab`** — `Tab` is the focus-navigation key every control expects, and claiming it would
> leave keyboard users unable to walk the toolbar.
>
> **The two view switches persist in the file** (2026-08-16), in the `.wBond`'s own opaque `ViewState`
> field — the field that exists so the UI can store what WB-A must not parse. **No `FormatVersion`
> bump**: an older build reads the string, understands none of it, and writes it back unaltered. The
> Array Inductance panel is never hidden by a view MODE, only by its own switch: the reason to enlarge
> a canvas is to watch geometry while watching the inductance change.

> **Added 2026-08-16, at the owner's request: the profile view's horizontal axis moves geometry.** A
> plain drag there used to be z-only, on the grounds that span is a *derived* coordinate so "move this
> point sideways" has no single answer in what is stored. It does have one — displacement **along the
> view's own horizontal direction**, which under AUTO is that wire's XY chord and under a fixed plane
> is the plane itself (`ProfileProjection.HorizontalDirection`). `WireEdits.Translate` owns the
> mapping, so the drag and the arrow-key nudge above cannot disagree, and a wire whose feet coincide
> in XY has no chord direction and is skipped rather than guessed.
>
> **The absolute axis has a FIXED origin — the world's, not the wire's own input foot.** Measuring
> from `Points[0]` pinned that point at span 0 permanently, so it could not move in this view whatever
> happened to it in the world, and any motion of it rendered as motion of everything else. `Normalised`
> keeps the foot-relative 0..1 origin: that is the mode this section's overlay argument is really
> about, and it still overlays wires of any angle and length. `Absolute`'s stated purpose is "true
> geometry", and a true picture cannot re-origin itself on the point being dragged.
>
> **The profile view's PLANE is a toolbar setting** (`ProfileAxisSetting`): `Auto`, `XZ`, `YZ`, or a
> typed angle in degrees. Auto is the default and is §6.2's own parameterisation — every wire on its
> own chord, which is the only mode in which two wires of different angle and length are directly
> comparable. A fixed plane answers the other question, "what does this array look like from over
> there", and foreshortens a perpendicular wire to nothing, which is what looking down a wire actually
> looks like. The layout view needs no such control: it is always X-Y.
>
> **Alt-drag means something different from a plain drag and always has** — it *scales* rather than
> translates (§6.2.1) — but two of its rules changed here. It now scales **span and height together**,
> live, instead of declaring one axis on the first few pixels of travel; and its span anchor is the
> foot the user is **not** dragging, matching WB26a's rotate rule. It is also offered in the **layout
> view**, where the pointer displacement is projected onto the wire's own chord.

> **Measured 2026-08-07, WB-C1.** Every geometric edit quantises to **one nanometre**, because
> `Point3` stores integer DBU — the choice that makes unit switching lossless (§6.5). So no transform
> is exact beyond ~1 nm: on a 500,000 nm loop height that is 2e-6 relative, and physically a fifth of
> a millionth of a mil. The editing tests state their tolerances in nanometres for that reason, rather
> than as relative fractions that would hide the cause. **The feet under a height scale are the one
> exception and are exact**, because their normalised height is zero and the factor multiplies them
> by zero (WB24a).

**WB25. The nudge step is 1 mil / 5 mil *regardless of the display unit*, because it is a bonder-
process quantity, not a display convenience** — but both values are settings, since a µm-native shop
will want 25 µm / 100 µm. The unit selector (§6.5) changes readouts, not nudge steps.

The existing marquee machinery is reused directly: `ComputeMarqueeSelection` already implements the
live-highlight, never-write-`_selectedIndices`-during-preview, solid-vs-dashed enclose/crossing cue
(logged in `project-brief-L1i-live-marquee-selection`). wBond adds the wire-promotion rule for
right-to-left, and nothing else.

### 6.4 Creation, duplication, and transforms

**Creating wires:** menu, toolbar button, and keyboard shortcut, plus **draw-in-layout-view**: click
the start point, click the end point, with a **real-time ghost** of the full generated loop (not just
a rubber-band line) between them. **Shift constrains to ortho.** The new wire gets the default
`LoopProfile`.

**Defaults, all in the circuitRF Settings dialog:** 7 points, 1 mil diameter, **gold**, and the default
loop profile. Gold is both the RF packaging norm and the metal of the LW1 validation set in
`mom-wirebond-kernel.md`, so the shipped default and the validated path agree.

**Transforms**, all operating on any selection (point, segment, wire, array, mixed):

| | |
|---|---|
| **Rotate** | arbitrary angle about a chosen or computed centre |
| **Rotate about end point** | **grab a wire and swing it with its opposite end pinned** — see below |
| **Mirror** | about an axis; **reverses traversal direction unless suppressed**, since a mirrored wire's input should normally stay on the input side — surfaced as a checkbox, because getting it wrong flips mutual signs (WB3) |
| **Bend** | displace interior points laterally in any direction, endpoints pinned |
| **Straighten** | collapse interior points onto the chord; retains point count so a profile can be re-applied |
| **Extend / shorten** | along the wire's projected axis, from either end |
| **Reverse wire** | swap input and output ends — see below |
| **Duplicate with pitch** | **offset in x or y, with a multiplicity count** — the array-authoring workhorse; new wires join the source wire's array by default, and the dialog shows the resulting pitch against the DRC minimum live |

**WB26a. Rotate about end point: the grabbed end follows the cursor, the opposite end stays fixed, and
the wire is carried rigidly between them.** The pivot is the end *further* from the grab point, so the
gesture needs no mode switch — grab near the end you want to move. Semantics per view:

- **Layout view** — rotation about the **vertical (z) axis** through the pinned foot, i.e. the wire's
  azimuth changes and its loop profile is carried along unaltered. This is the fan-out gesture: a
  ground array leaving one paddle at a spread of angles.
- **Profile view** — rotation **in the view plane**, which tilts the wire's rise. Because z is always
  up in that view, this is the natural way to adjust a wire climbing from a die to a taller lead.

Live readout of the angle and of the moving end's new position; **snapping stays active on the moving
end**, so a swung wire lands on a real pad rather than near one; **Shift constrains to the ortho/45°
increments** already used elsewhere in the editor. Applied to a multi-wire selection it rotates each
wire about *its own* pinned end (the fan-out case), with a modifier for rotating the selection rigidly
about a single shared pivot — the two are genuinely different operations and both are wanted. This
reuses the existing rotation machinery in `LayoutEditorViewModel.Rotate.cs`, which already carries the
fix for the R90/R270 selection-box bug logged in `project-brief-layout-label-owner-followup`.

**WB26b. `Reverse Wire` is an explicit user command, and direction is never silently re-inferred.**
Direction may be *guessed* once, at creation, from which end lands on which array's pad — but it is
then data (WB3), and only this command changes it. The reason to keep the command rather than rely on
inference: inference is wrong exactly when the wire loops back to the same pad structure, and a
silently-flipped wire negates a row and column of mutual inductances, which is a plausible-looking
wrong answer rather than a visible failure. The command works on any selection, is a single undo step,
and the input-end dot recolours immediately so the effect is visible without opening the panel.

**WB26. `Duplicate with pitch` is the primary array-creation path and must produce N wires in one
undo step, one array assignment, and one incremental fill.** Creating 200 wires as 200 separate
operations would be 200 cold fills. This is the difference between "usable" and "unusable" for the
600-wire case.

### 6.5 Units

**mil, inch, mm, µm**, switchable instantly, **independent of the `.ctech` display unit** (owner's
requirement). `LayoutUnits.cs` already has the exact-integer nm-per-unit table (`Mil = 25,400 nm`) and
the parse/format path; wBond adds `Inch` if absent and a selector in the toolbar. Storage stays in DBU
so switching units is lossless and free.

### 6.6 Layout reference geometry, snapping, and hierarchy

The layout view **renders and edits the real layout** — cells, PCells, primitives — so the designer
lands wires on actual bond pads and package leads. All four snap kinds already exist and apply to wire
points unchanged: **corner/endpoint, midpoint, centroid, intersection**.

**WB27. Descending into a sub-cell keeps the XY projection of all wires visible and editable-in-place
as a locked reference.** The owner's motivating case — jump into a cell to nudge a bond pad while
watching the wires that land on it — requires the wires to be drawn in the *sub-cell's* coordinate
frame, which means walking the instance transform chain. `LayoutCoordinateWalk.cs` and
`LayoutInstanceTransform.cs` already do exactly this walk for shapes. The wires are drawn dimmed and
are not selectable at depth (editing a wire from inside a sub-cell it does not belong to would be
ambiguous about which instance is being edited); the user ascends to edit them.

Cells can be **dragged from the project tree into the layout view** as visual references, via the
existing palette-drag path.

### 6.7 Clipboard

- **Within the editor:** copy/paste wires and wire segments, preserving array membership where the
  target array exists and creating it where it does not.
- **To other applications:** as a graphic, through **`PlotExporter.CopyPlotToClipboardAsync`** — the
  same path the schematic, layout and data display already use for PDF/SVG/bitmap. The owner notes
  this is tricky and already solved; wBond reuses it rather than re-solving it.

### 6.8 The inductance panel

Docked left, full height. Per array: **L_arr** (diagonal), the **mutual to every other array**
(off-diagonals, as a matrix or a sorted list), wire count, total length, span, R/ωL at the design
frequency (WB19b), and the **per-wire current share** ramp from WB9. Header states the **active return
path** (WB20) — and, from v2, the **coupling domain** (§7). Values update live during drags, marked *provisional*
while the quality ladder is degraded (WB15).

**WB27a. Inductance is displayed in picohenries, fixed — not auto-ranged.** Every quantity in the
panel is pH: self and mutual, arrays large and small. The reason to fix the unit rather than
auto-scale per cell is that the panel's whole job is **comparison during a drag** — across arrays, and
against the same array a second ago. A readout that silently switches nH ↔ pH mid-drag makes a number
appear to jump by 1000× when the geometry moved by a mil, which is precisely the illusion a live
readout must not create. Wirebond inductances live in the tens-to-thousands of pH (a 100 mil wire is
~2,000 pH; a ten-wire array ~400 pH), so one unit covers the whole useful range.

Mutual terms are additionally offered as the dimensionless **coupling coefficient**
k = M_ij/√(L_ii·L_jj), because it is scale-free and is the number that tells a user whether two arrays
are meaningfully coupled — a bare pH mutual does not, without mentally dividing by the selfs.

### 6.10 Owner round 3 (2026-08-16)

**Spelling.** The profile plane is **`XZ`** and **`YZ`** everywhere — no hyphen. `ProfileAxisSetting`
still *accepts* `X-Z`/`Y-Z`, because a saved `.wBond` and a user's habit both carry them and a rename
that stopped reading the old text would silently reset the plane of every design written before it.

**Shipped defaults.** The default wire runs **north/south over 30 mil**, and the profile view opens on
**YZ**, which is the plane that shows it side-on rather than foreshortened to nothing. Every number the
shipped wire is built from is a named constant on `WBondEmbedding.DefaultWire` — the owner expects to
tweak it repeatedly, so a tweak is one edit to one constant rather than a hunt through a constructor.
The default plane forced one file-format detail: `WBondViewState` now serialises **nulls explicitly**,
because null means AUTO *and* means "the key was never written", and with a non-null default those two
would collide — a design deliberately left on Auto would reopen in YZ.

**Colours are theme roles, at last.** `wBond.Wire`, `wBond.WireStart`, `wBond.Selected`,
`wBond.Envelope` and `wBond.FreeWire` join the shared `ColorRole` vocabulary with light and dark
defaults — spelled with a lowercase `w`, like the product — and `WBondRenderTheme.FromTheme` projects
them the way every other canvas's token bundle
already did (`color-themes.md` L2). Both wBond canvases previously drew **one hardcoded palette in
light and dark alike** — which is not a tuning problem but a wiring one, and is why the light-mode
selection accent was pure white on a near-white canvas. The light accent is now a deep blue; the
start-of-wire dot defaults to the wire's own colour much darkened, in both variants, so "which foot is
the input" reads as the same wire rather than a second kind of object. That dot is load-bearing: the
sign of every mutual depends on it (WB3).

**Copy writes a picture, not just JSON.** wBond's ordinary Copy wrote `SetTextAsync(json)` and nothing
else, so pasting into PowerPoint or Keynote produced raw JSON or nothing. `WBondClipboardWriter` is a
transcription of `LayoutClipboard.CopyAsync` — content-framed page, PDF + SVG + PNG best-effort with
the JSON always present as the fallback, and **the Windows bypass that writes every format in one
P/Invoke session with CF_ENHMETAFILE first**. That half took the Layout Editor several rounds to get
right across three platforms and is deliberately reused rather than re-derived.

**Paste pitch.** Paste applied one fixed 5 mil offset, so pasting the same clipboard twice put the
second copy exactly on the first — two wires on identical geometry, an inductance matrix that is
singular, and the owner's *"'The inductance matrix is not positive…' and a 3rd wire does not appear"*.
The offset is now a Settings value (`Paste pitch`, default 5 mil) and paste **steps until nothing
lands on a wire already there**, testing the feet. The pitch governs **placement only** — the wires
arrive with whatever spacing they were copied with, and a paste never re-spaces them.

**And the step runs ACROSS the wires, not along a fixed axis.** A bond array is pitched perpendicular
to its wires — that is what a pitch *is* — so an east/west wire steps in y (what paste always did), a
north/south wire steps in x, and a wire at 37° steps at 127° rather than being forced onto an axis it
does not lie on. Stepping along +y regardless slid a north/south copy **end-to-end** with its
original. The direction comes from the payload's mean chord azimuth, summed as vectors **folded onto
a half-plane** (two anti-parallel members of one array would otherwise cancel out of the mean and
leave a direction perpendicular to neither), then canonicalised to face east — or north when it is
purely vertical — so the copy lands to the right of a north/south wire rather than to its left.

**Three deletes and a regroup, on the layout view's wire context menu.** Selection commands above the
separator, then `Group Wires As…`, then `Delete Vertex` / `Delete Segment` / `Delete Wire` — resolved
against what the right-click actually landed on, and shown disabled with a reason rather than absent.
Two rules worth keeping:

- **Removing a point DETACHES the wire from its profile.** A `LoopProfile` *is* the point set, so a
  wire that has had one removed no longer follows it; leaving the binding would mean the next Re-apply
  Profile silently put the point back.
- **Delete Segment removes the segment's far endpoint**, so the wire comes back as ONE wire with
  exactly one fewer segment. Splitting is not offered: the reduction sums current along one continuous
  path (§3.4), and two disconnected halves are not something the physics can evaluate.

**An emptied group is REMOVED, and the comment that said otherwise was wrong.** Several edits carried
the note "the empty source group is left in place — a group is a named terminal, and moving the last
wire off a pin is not the same statement as deleting the pin". It is a good argument that this layer
cannot honour: `WBondDesign.Validate` rejects an array with no wires outright, because a group with no
wires is a zero row in the mapping matrix and makes the array-basis inductance singular. Leaving one
behind therefore made the whole edit unevaluable, so the refusal fired and the edit rolled back — the
command appearing to do nothing while reporting a physics error the user could not connect to it.
Every edit that can empty a group now prunes. For the same reason **the last wire in a design cannot
be deleted**, and the menu says so in those words rather than letting `Validate` report "wBond design
has no arrays".

**Profile-view visibility no longer depends on the selection.** §6.2 idea 3's clutter rule was
implemented as "hide every bound member but the representative *unless the selection touches it*",
with no geometric test at all — so members of a group whose geometry differed were invisible until a
marquee happened to catch them, and wires appeared and disappeared as the user selected. The rule is
now **geometric**: a bound member is collapsed onto the representative only when it actually *projects
onto it*, i.e. when drawing it would put a second polyline on pixels the representative already
covers. The selection is still consulted for a *coincident* member, because drawing one of those adds
no curve anywhere — it recolours a curve already on screen, which is what makes a marquee over a
same-shape array visibly do something. Presence is a function of geometry; colour is a function of
selection. §6.2's rule survives intact where it means something: under AUTO every member of a
same-shape array projects onto the same chord, so it is still one curve plus a band. Under a fixed
plane, members at different positions project to different places, and there is no honest picture in
which they are one curve.

### 6.11 The wBond cell's layout view — and what "the wBond Editor" becomes (2026-08-16)

The owner, after using the round-4 editor: *"I just tried the new geometry shape tools in wBond and
there are many many bugs (that we had previously resolved when we hardened the Layout Editor)… any
changes to the Layout editor will have to be changed in the wBond editor too - I
wish we had simply adapted the Layout Editor to work with wires."*

**WB39. That wish is §6.1/WB22, and WB22 is what was built — one layer down. Only the SHELL was
duplicated.** Counted:

| | lines | copies |
|---|---:|---|
| `LayoutEditorViewModel` (+ 10 partials), `LayoutCanvas`, `LayoutSnapQuery`, `LayoutHitTest`, renderer, commands, undo | ~9,500 | **one**, used by both editors |
| `LayoutEditorView` shell — toolbar, keyboard routing, context menu, breadcrumbs | ~1,750 | Layout Editor only |
| `WBondEditorView` shell — the same jobs, re-transcribed | ~2,690 | wBond only |

The wBond editor's layout half **is** a `LayoutEditorViewModel` inside a `LayoutCanvas` — the same
objects, not a port of them. Snapping, hit-testing, the drawing tools, the commands and the undo stack
are already shared, and always have been. What is duplicated is the ~2,700-line *view shell*, and
**that is precisely where round 4's new geometry bugs live**: the tools were exposed through a
transcribed toolbar whose keyboard, focus, context-menu and breadcrumb wiring the original shell has
and the copy does not.

So the remedy is not to re-fix the tools, and not to re-architect the model:

> **WB39a. The wBond editor HOSTS `LayoutEditorView`. It does not transcribe it.**
> The layout half of the wBond editor is the real control, over a `LayoutDocument` wrapping its own
> reference layout (`LayoutDocument(title, viewModel, path)` already takes an existing view-model).
> wBond keeps exactly two things of its own: the **profile view** and the **Array Inductance panel**.
> Anything a `LayoutEditorView` already does is reached, never re-implemented — including push-in,
> pop-out and the breadcrumb bar, which the transcribed shell never had at all.

**One rule follows, and it is the one to hold the line on.** *If a change would be needed in both
editors, it belongs in neither — it belongs in the shared control.* A second `Click=` handler that
does what a Layout Editor handler already does is the smell; the fix is to host, not to copy.

#### What a wirebond CELL is

**WB40. A wirebond cell is an ordinary cell whose `layout/` sub-folder holds a `.wBond` beside its
`.clay`, sharing that `.clay`'s stem.** The artwork (pads, traces, the die outline) is the layout view,
exactly as for any other cell. The wires are the `.wBond`. Nothing new is invented:
`LayoutEditorViewModel.WireDesign` is already the seam that puts a wire overlay over a layout, and
`WBondLayoutOverlay` already draws and edits through it — today only the wBond document sets them.

```
AmpStage/
├── .ccell
├── layout/
│      amp_v1.clay             artwork — pads, traces, die outline
│      amp_v1.wBond            the wires for THAT artwork
│      amp_v2.clay
└── schematic/…                unchanged
```

> **Revised 2026-08-17 — the `.wBond` moved from the cell root into `layout/`, stem-paired.** WB40 as
> first written put it at `<cell>/<cell>.wBond`, reasoning that it "is not a view of the cell in the way
> a schematic or a layout is." That half was right and is unchanged — it is an **attachment**, the file
> category now defined in `workspace-and-project-tree.md` §1.2.1 — but cell-root placement was a
> rationalisation of a one-`.wBond`-per-cell assumption the model never actually made:
>
> - **A cell may hold several `.clay` files.** Wires are drawn over *specific pads at specific
>   coordinates*, so "the cell's wires" stops being well formed the moment there are two layouts. Stem
>   pairing makes the association **defined** instead of assumed; cell-root placement could only guess
>   — prefer the cell-named file, else the sole one, else give up.
> - **A `wires/` view sub-folder was considered and rejected**, because a view sub-folder means *at most
>   one primary, the rest inert* — and WB28 deliberately refuses a wBond singleton, so two `.wBond` in
>   one cell means both are real and both are solved. Primacy would encode the opposite of WB28.
> - **It costs nothing structurally.** Primacy resolves per view type by extension, so a `.wBond` in
>   `layout/` can never be miscounted as a second `.clay`; there is no new view type, no
>   `primary_wires` in `.ccell`, and no fourth empty sub-folder in every resistor in the standard
>   library.
>
> **A `.wBond` still at a cell's root is read, reported and left alone** — pre-2026-08-17 workspaces
> exist, and silently ignoring their wires is the one outcome that is not acceptable. The report names
> the move (*"amp.wBond is at the cell root; move it to layout/ beside the .clay it belongs to"*).
> Symmetrically, a `.wBond` in `layout/` whose stem matches no `.clay` is an **orphan** and is reported
> rather than ignored — §1.2.1 makes that report the price of the placement, because a Finder rename of
> a `.clay` would otherwise remove wires from a simulation the user believes includes them.

**This keeps WB23 exactly as written.** No 3D shape enters `.clay`, the layout canvas stays 2D, no
volumetric mesher is written, and a wire drag still invalidates only the overlay. The alternative —
a wire shape type in `.clay` — was rejected then and is rejected again, for the same reasons plus a
new one: the `.wBond` format already round-trips, already exports to DXF for the assembly house, and
is already what the standalone application reads.

**Push-in is then the answer to the owner's own question** (*"in layout editor, I can 'jump' into via
hierarchy which shows the wBond editor (with the layout in the background)?"*): yes, and with WB40 it
needs no jump to a different editor at all. Pushing into a wirebond cell shows its artwork with its
wires over it, editable, because the layout editor draws a wire overlay for any cell that has one. The
profile view and the inductance panel become **dockable tool panels** that follow the active layout,
the way the DRC and Properties panels already do — so "the wBond Editor" stops being a separate editor
and becomes *the Layout Editor with two panels open*, which is what WB22 said in the first place.

**The standalone `wBond` application is unaffected and stays** (§11): it is the same overlay and the
same two panels, hosted without a workspace. That is a build configuration, not a second editor.

**WB40b. A layout becomes a wirebond cell by DROPPING a wBond into it, as well as by having a
schematic component written out.** (Owner, 2026-08-17: *"Cannot drag and drop a wBond component from
the Library Palette into a layout. User should be able to do that and then start editing wires in
layout."*)

Until then the only way to get wires onto artwork was §9.5's schematic-first flow — place the
component, then Update Layout from Schematic — which inverts §9.5's own claim that **the layout is the
editing source of truth**. Dropping one is the layout-first entry to the same place:

- It is **not** a PCell placement and must not be made one. A wBond has no generator and never will
  (WB23: no wire enters a `.clay`), so the generator-based drop path correctly refuses it; what a drop
  produces is the session's *wire layer*, seeded from `WBondEmbedding.DefaultDesign` — the same
  one-array, one-wire definition a freshly-placed schematic component starts from — moved so its input
  foot lands where the drop did.
- **A second drop adds another array**, not another wire layer. One `.wBond` per `.clay` is the model
  (WB40), so "another group of wires" is the only reading of the gesture that the file format can hold,
  and it never refuses a drop the cursor has already accepted.
- **The file is written by the ordinary save**, not at drop time. That is also what makes the drop work
  on a scratch layout, whose `.wBond` path is not knowable until its first save (`RetargetWiresForSaveAs`).
- **The drop is refused where the wires on screen belong to a HOST** — the wBond editor hosts the same
  canvas and its own overlay outranks the frame's, so a drop there would attach a second wire design
  that is saved to disk and never drawn.

**WB40h. Pasting wires into a layout MAKES it a wirebond cell.** (Owner, 2026-08-17: wires copied from
one and pasted into a fresh `.clay` did not arrive.) The paste path required a wire layer to already be
there, which no ordinary layout has, so the wire half of a mixed clipboard was silently dropped while
the geometry arrived. A layout being pasted wires into is a layout that has wires: the layer is created
on demand — **empty**, since the paste is about to fill it — the session is marked dirty so the
`.wBond` is written on the next save, and the change of status is reported rather than left to be
discovered. The one refusal is a layout whose wires belong to a HOST (the wBond editor's reference
layout), where a new layer would be a second design that is saved and never drawn — the same guard
WB40b's palette drop carries.

**WB40c. Deleting the last wire un-makes a wirebond cell — the component goes, and so does the file.**
(Owner, 2026-08-17: *"if all wires are deleted from a layout and the user performs an Update Schematic
from Layout, a wBond symbol remains in the schematic; there should be no wBond component. I also
recommend that the .wBond file gets removed … my only concern is that the user must be able to perform
undo/redo."*)

- **The component** is removed by §9.6's reconcile, through the schematic's own `DeleteCommand`, which
  restores it at its original list index — so undo needs no inverse of its own. A wBond carrying an
  empty payload still draws its pins and still declares its terminals, so leaving one behind is not
  untidiness: the netlist would go on modelling a bond group the layout no longer has. The nets that
  were WIRED to it are left drawn, exactly as deleting a component by hand leaves them.
- **The file** is deleted by the SAVE, not by the delete and not by Update Schematic:
  `SaveWireDesignIfDirty` writes the sidecar when the design has wires and removes it when it has none.
  That is what answers the undo concern. Nothing is detached when the last wire goes — the session keeps
  its wire editor, its overlay and its undo history — so Ctrl+Z brings the wires back in memory, marks
  the layout dirty, and the next save writes the file again. **The file mirrors the SAVED state, which
  is the only state it has ever claimed to mirror.**
- **Why an empty `.wBond` cannot simply be written instead:** WB40 resolves a layout's wires by the
  file's PRESENCE, so an empty one leaves the cell a wirebond cell for ever — three toolbar buttons, two
  panels following it, and a DRC assembly section, all about wires there are none of.

**WB40g. Delete removes what was PICKED, and two shaping commands join the wire menu.** (Owner,
2026-08-17.)

- **The Del key dispatches on the selection** rather than always deleting whole wires: wholly-selected
  wires go whole, and segments or vertices on wires that are not wholly selected remove just those
  points (a segment through its far endpoint, as §6.2's Delete Segment already defines it). The context
  menu could always do this; the key could not, however carefully a segment had been picked. One undo
  entry for the whole batch, and a wire is never taken below its two feet — that is refused by name.
- **Add Vertex** puts a point on the wire nearest the right-click, **collinear with its two neighbours
  and at their interpolated z**, so the insert changes the shape not at all and only gives the user a
  handle where there was none. It detaches the wire from its profile for the reason removing a point
  already does: a `LoopProfile` defines the point set.
- **Straighten Wire / Straighten Wires** moves every interior point onto the straight line between the
  feet, **in XY only and with the feet anchored** — lateral bow removed, loop height untouched, each
  point keeping its own position along the chord. A bound wire is already straight there
  (`LoopProfile.ApplyTo` interpolates X and Y between the feet), so it needs no detach. **With several
  wires selected it straightens all of them, each about its OWN two feet** — never a shared chord,
  which would swing a fan-out from a common pad onto one line and destroy exactly the geometry the
  flexible model exists to allow (the same reasoning WB24c gives for scaling by factor rather than to a
  common value).

Add Vertex is **click-target-scoped**, like the three deletes it sits with. Straighten is the one
command here that reads the SELECTION — and only when there is genuinely more than one wire in it, so a
single selected wire can never make a right-click on a DIFFERENT wire act somewhere the user is not
pointing. **Add Vertex is on the profile view's menu too; Straighten is not** — straightening is a
statement about the wire's path across the board, and that view's horizontal axis IS position along
that path.

**WB40f. The Layout Editor's own Rotate and Mirror carry the WIRES, and the angle-wire tool gets a
button.** (Owner, 2026-08-17: *"map the rotate button command from the hosted layout … want this to
work just like the layout primitives work"*, and *"a special Rotate button/armed tool that allows the
user to rotate only wires where the opposite end point remains anchored"*.)

Two different gestures, and keeping them apart is the point:

- **`R` / `Shift+R` and the toolbar Rotate/Mirror buttons** turn the selection 90° (or flip it) as ONE
  RIGID BODY about the selection's own centre — now including any selected wires in both the pivot and
  the transform. That shared pivot is what keeps a wire on the pad it lands on when the two are turned
  together, which is the whole basis of §6.3's "both selections at once". It is **one undo entry**: the
  wire half rides in the same `CompositeCommand` as the shapes and instances (`TransformWiresCommand`),
  because two entries would let a single Ctrl+Z put the pads back and leave the wires turned.
- **`Alt+R` and the Angle Wire button** arm WB26a's swing — grab a wire near the end you want to move,
  the far end anchored. This is the one that makes an angled wire. **The whole implementation already
  existed on the overlay** and was reachable only from the wBond editor's own tool enum; this is a UI
  entry point, not new geometry.

  **Shift snaps the wire's ABSOLUTE bearing to 15° steps** (owner, 2026-08-17) — 0, 15, 30, 45… measured
  in XY, *not* 15° from wherever the wire happened to start. Relative increments leave a wire that began
  at 7° on 22°, 37°, 52°: every one as crooked as it started, with 0/90/180/270 — the arrangement almost
  every bond array wants — unreachable by the gesture meant to reach it. 15° gives up nothing a coarser
  step could do (15 divides 45, 90 and 180) and adds the fan-out angles a 45° grid cannot express.

**`T` and the Transform button** open the wBond editor's own transform dialog on the wire selection —
again the same call, not a second implementation. Both are gated exactly like the other wire buttons:
present only on a cell that has wires, and `T` is claimed only when wires are actually selected, so it
stays free everywhere else.

**WB40e. Wire z-height is a SETTING (Settings ▸ Wirebonds), and every new wire takes it.** (Owner,
2026-08-17: *"being consistent is more important than being right, and we can't guess what height the
user wants the wire landings."*) It governs both feet of every wire the application creates: the one a
new wBond component carries, the one a palette drop attaches, an array added from the parameter dialog,
and a wire DRAWN in the layout view — which previously landed at z = 0 while a new component's wires sat
at 4 mil. Shipped default 4 mil (`WBondEmbedding.DefaultWire.FootZMils`), resolved through
`WBondDefaults.FootZNm`.

Two things about it are deliberate and easy to get wrong:

- **Zero is a value, not "unset".** A foot at z = 0 lands on the reference plane and a negative one sits
  in a cavity below it; both are geometry someone bonds. So this preference alone is honoured whenever
  the key is present, where the diameter and point count beside it treat a non-positive stored value as
  absent.
- **The PROFILE view does not read it.** There the user clicks a z, which is the whole point of drawing
  in that view; forcing the setting would make the gesture inert in the one place it means something.

**WB40d. The wire TOOL is on the Layout Editor's toolbar** (owner, 2026-08-17), to the right of the two
panel buttons and gated exactly like them. `W` arms it; `Escape` disarms to Select; a second `Escape`
deselects everything in the view — shapes, instances **and** wires. The armed state lives on the layout
SESSION rather than the view, because the toolbar toggle and Escape both have to agree with it, and it
is mutually exclusive with the layout drawing tools in both directions: the overlay gives an armed
layout tool first refusal on every press, so a wire tool armed alongside Rectangle would look armed and
never see a click.

**The schematic half of a palette drop comes from §9.6's reconcile, which now CREATES the component**
when the schematic has none: a wBond that entered through the layout has no component behind it by construction,
and telling that user to go and place one by hand is asking them to do what Update Schematic from
Layout is for. It is built through the same `WBondPlacement.BuildCarrying` the palette drop and the
wirebond import both use, so a component that arrived this way and one placed by hand are the same
component. It is **not wired to anything**, and the report says so — an array's two terminals are a pin
pair whose nets only the user knows.

> **Built 2026-08-16** (`brief-wbond-wbf-one-editor.md`; detail in `src/Ui/RESOLVED.md`). Four things
> are worth knowing before touching any of it:
> - **The seam needed nothing new.** `WBondDocumentViewModel.LayoutDocument` wraps the reference layout
>   in `OnReferenceLayoutChanged` — the single funnel all three creation points share — and the XAML
>   binds it. WB27's descent chain, which had no push-in to trigger it until now, is wired there too.
> - **The wire context menu moved onto the OVERLAY** (`ILayoutCanvasOverlay.BuildContextMenuItems`),
>   because one shared canvas means one `ContextMenu`. That is also what gives a wirebond CELL its wire
>   menu in the ordinary Layout Editor with no wBond code in that view.
> - **Ctrl+Z routes by an edit STAMP, not by focus** — a wire drag happens on the layout canvas, so
>   focus cannot tell the two histories apart, and "wires first" would undo the wrong thing.
> - **The profile view and the inductance panel are each ONE control hosted twice** — inline by the
>   wBond editor, and by a dock tool that follows the active layout. §10.1's table is what that makes
>   true.

---

## 7. Should a design ever have more than one wBond component?

The owner's concern is exactly right: coupling is only computed *within* a wBond, so two components
means the mutual inductance between their wires is silently zero.

**WB28. Do not enforce a singleton. Make the coupling domain explicit instead, and make silent loss
impossible.**

**Why a hard singleton fails:**

1. **It breaks hierarchy and reuse, which is decisive.** If cell `PA_Stage` contains a wBond and a
   Doherty places two `PA_Stage` instances, there are two wBond instances *by construction*. A
   singleton rule would forbid putting a wirebond inside a reusable cell — which is precisely what a
   packaged-transistor model is.
2. **It forces a wrong-sized problem.** Input-side bonds and output-side bonds two inches apart couple
   negligibly. Merging them makes one N = 600 matrix where two N = 300 matrices are both faster
   (O(N²) fill) and no less accurate.
3. **It is unenforceable across the boundary anyway** — a user importing a vendor package `.wBond` and
   adding board-level bonds cannot be made to merge them.

**What to do instead:**

- **One wBond per layout cell view is the default and the strong convention.** Drawing a wire adds it
  to the existing wBond; new wires never silently create a second component. Creating a second is an
  explicit action that states the consequence.
- **WB29. *(v2.)* Every wBond declares a `CouplingDomain`.** wBond instances sharing a domain tag are gathered
  at elaboration, their wire coordinates transformed into a common frame by the existing flatten
  machinery, and solved as **one matrix**. Default: every wBond in a layout shares one domain. This
  buys the singleton's correctness without its modelling straitjacket.
- **WB30. *(v1.)* A coupling audit runs on every solve and reports, never refuses:** *"wBond X1 and
  wBond X2 each model their own wires only, and have wires within 42 mil of each other. Their mutual
  coupling is not modelled."* With a distance threshold scaled to the wires' heights above ground —
  coupling falls off with lateral distance normalised by height, so a bare distance would be wrong for
  a tall loop and needlessly noisy for a flat one. This is the mechanism that makes the whole answer
  safe: the failure mode the owner is worried about becomes loud instead of silent.

**Staging — owner decision, 2026-08-07: `CouplingDomain` (WB29) is v2; the audit (WB30) is v1.**
Domains need the elaboration-layer gather (transforming several wBonds' wires into a common frame),
which is real work for a case that is not the common one.

**WB30a. The consequence must be stated rather than left implicit: in v1 the audit is the *whole*
safety mechanism, and the only remedy it can offer is manual.** With no domains, a user told that two
wBonds couple has exactly one fix — merge the wires into a single wBond by hand. That is acceptable
because v1's strong convention (one wBond per cell view, new wires joining it automatically) means most
designs never produce a second wBond in the first place. But it makes the audit **load-bearing rather
than advisory**, and it must not slip out of v1 on the grounds that domains are coming later. The
audit's message should therefore name the remedy, not just the problem.

Contrast this with WB9a two sections earlier, where the same reflex — "add a warning" — was
*declined*. The difference is that here there is a real threshold with a physical basis (lateral
distance normalised by height above ground) and a concrete action the user can take; there, neither
exists. A tool earns trust in its warnings by not firing the ones it cannot justify.

---

## 8. Wirebond DRC — where the rules live

The owner's instinct — that these do not belong in `.ctech`, because assembly happens at a different
site from fabrication — **is correct, and the reason is structural rather than organisational.**

**WB31. Assembly rules live in their own resolvable document — `.wasm` (*wirebond assembly*) —
referenced by the workspace, never embedded in `.ctech`.** *(Owner decision, 2026-08-07.)* The
argument:

**The relation is many-to-many.** One OSAT bonds GaAs, GaN and Si die from a dozen foundries; one die
technology is bonded at several houses across its life. Rules that are many-to-many with technologies
cannot live inside a technology without duplication, and duplicated rules drift.

**The lifecycles differ.** A `.ctech` changes when a process node revises. Assembly rules change when
the house buys a bonder, qualifies a wire, or a specific product gets a waiver. Different owners,
different revision cadence, different approval chains.

**But some rules genuinely *are* die-side, and the split must be drawn honestly:**

| lives in `.ctech` | lives in the assembly rule file |
|---|---|
| bond pad opening size and shape | minimum wire pitch |
| bond pad pitch as drawn | loop height vs. span envelope |
| pad metallisation / passivation opening | maximum and minimum wire span |
| keep-out from active area | wire-to-pad-edge clearance, wire-to-lead-edge clearance |
| pad layer identification | wire-to-wire 3D clearance, wire-to-die-edge clearance |
| | tail/stitch land length; allowed wire diameters and metals |
| | maximum wire angle change; reverse-bond allowance |

**In one sentence: `.ctech` owns what the pad *is*; the assembly file owns what the bonder can *do*
with it.** The DRC evaluates the union, merged by the same machinery `TechnologyMerge.cs` already uses
to merge technologies — so this is a new rule vocabulary over an existing merge, not a second DRC.

**WB32. The assembly file is structured in three sections, because the constraints have three
different sources:** `machine` (bonder capability limits — hard), `process` (what this house will
actually run for this product — the working rules, tighter than the machine), and `material` (wire
types, diameters, metals available). A rule violation reports which section it came from, because
"your bonder cannot do this" and "your assembly house prefers not to do this" have very different
answers.

**WB32b. A new workspace contains no `.wasm`. One is created ON DEMAND, the first time a check
actually needs it.** *(Owner decision, 2026-08-07.)* Most designs have no wirebonds, and shipping a
rule file into every workspace would put a document in the project tree that most users would have to
learn about only to ignore. So the file is created at the one moment the user is already asking the
question — they ran a design-rule check, the design has wires, and there are no assembly rules to
check them against. Three answers, and declining is a real one: point at an existing file (the usual
case, because the house sent one), create a starter to edit against the house's own document, or not
now. A decline is remembered for the session, because a prompt that reappears on every check is one
people learn to dismiss unread.

The starter's numbers are conventional first-pass gold-wire values and **every rule in it says
`PLACEHOLDER` in its own description**. That is load-bearing rather than cautious: a rule set a user
believes came from their assembly house, but did not, is worse than no rule set at all — it would
pass a design the house rejects. The starter exists so someone has a working file to EDIT, not a
file to trust.

### 8.1 Reuse the DRC expression parser as far as it goes

**WB32a. `.wasm` reuses `DrcLayerExprParser` and the existing rule model, waiver, results-panel and
reporting machinery. The wirebond vocabulary is added as new *operands and functions inside the
existing language*, not as a second rule language.** *(Owner decision, 2026-08-07.)* Concretely, three
things widen and nothing is replaced:

| | existing | widened to |
|---|---|---|
| **operands** | layer regions | layer regions **+ wire sets** — an array (`G1`), a wire, a segment, or a selector over them |
| **functions** | `width`, `spacing`, `enclosure`, `area`, boolean layer ops | **+ `wire_spacing`, `loop_height`, `span`, `dist_to_edge`, `wire_to_layer`, `angle_change`** |
| **geometry engine** | 2D polygon predicates | **+ a 3D predicate class** |

Only the third is genuinely new code: wire-to-wire minimum distance is a **segment-to-segment distance
in space**, not a 2D polygon spacing, and there is no way to express it as one. That is a bounded,
well-defined addition — a closed-form capsule-to-capsule distance evaluated over pairs, accelerated by
the same spatial-index pattern the 2D DRC already uses — and it does not disturb the parser, the AST,
the waiver model or the results panel above it.

The payoff of reuse is not just less code: rule *expressions* compose, so a house can write
`wire_spacing(G1, G2) >= 4mil && loop_height(G1) <= envelope(span(G1))` in the language its layout
engineers already read, with the same waiver and reporting behaviour they already trust.

**One genuine language extension: the loop-height-vs-span envelope is a *curve*, not a scalar.**
Minimum and maximum loop height are both functions of span, and assembly houses supply them as a
table. So the expression language gains a **piecewise-linear lookup** (`envelope(...)` above) as a
first-class value — a small, general addition that also serves any other tabulated limit a house
supplies.

> *One practical note on the extension, raised once and then dropped:* `.wasm` is also WebAssembly's
> extension, so OS file associations, editors and MIME sniffing may claim it, and web results for
> "wasm file" will be about something else. It is unambiguous inside circuitRF, where files are
> resolved by role rather than by extension. Adopted as specified.

---

## 9. The `.wBond` file

JSON, versioned, following the `DataDisplayConfig` / `.charm` pattern (`FormatVersion`, forward-
compatible reads), matching `project-file-formats.md`'s conventions including the **id-not-persisted**
rule.

**Stored:** wires (points, diameter, material ref), arrays and membership, loop profiles and bindings,
materials, ground plane, operating temperature, coupling domain *(v2)*, the assembly-rule-file
reference, view state (projection
azimuth, units, dot/line sizes), and the wBond's own parameter values.

**Not stored: results.** Re-derived on open, as `.charm` does — a cold fill is 0.54 s (§4.1) and it
eliminates the stale-data class of bug entirely.

### 9.1 Reference or embed — the owner's requirement, with the `.charm` precedent

The layout geometry a wBond is designed against can be **referenced** or **embedded**:

- **Referenced** — cells named by path, resolved against the workspace. Small file; requires the
  workspace.
- **Embedded** — the `.clay` geometry copied into the `.wBond`. **Once embedded it is decoupled and
  independent of its originating workspace**, which is the owner's stated goal: hand a colleague one
  file.

This is the identical question `harmonicarf.md` §8.1 answered for the DUT, and the answer should be
consistent: **embed what is self-describable, reference what is licensed or compiled.**

**WB33. PDK PCells are flattened on save to `.wBond`, and the user is told which ones and why —
before the save, not after.** A PDK PCell's generator is licensed vendor code that cannot be shipped
inside a design file. Flattening turns it into ordinary polygons that behave as a regular cell inside
wBond. The dialog names each affected cell, so the user knows the file they are about to send loses
parametricity on those cells.

**WB34. Native circuitRF PCells are *not* flattened** — their generators ship with circuitRF, so the
receiving copy can regenerate them and they stay parametric. This asymmetry is the whole reason the
distinction is worth drawing.

**WB35. Opening a `.wBond` whose referenced cells cannot be resolved reports which are missing and
offers to re-point them — it never fails silently or substitutes.** Same rule as `.charm`'s missing-DUT
path, and the `CharmIo` bug already logged in memory (a provider is a composed *name*, not a path) is
a directly transferable warning for whoever writes `WBondIo`.

### 9.2 Bringing a `.wBond` into a design

Three routes, all required by the owner. Routes 2 and 3 are **imports**, not references — per §5.0 a
placed wBond carries its own design, so bringing a `.wBond` into a schematic copies its wires in
rather than pointing at the file.

1. **Open it standalone** and edit.
2. **File ▸ Import ▸ Wirebond Wires…** — replaces the selected wBond component's wires with the
   file's, regenerating the symbol from the new array list (§5.1). With no wBond selected it places a
   new one carrying those wires. The `.wBond`'s embedded layout artwork is **not** brought across;
   that is route 3.
3. **File ▸ Import ▸ Wirebond as Cell…** — creates a new workspace cell whose **layout view** is the
   `.wBond`'s embedded geometry and whose **schematic view** is a wBond component carrying its wires.
   This is the path for "someone sent me a package model". A design carrying no embedded geometry has
   no layout view to make, so it is diverted to route 2 with a message rather than producing an empty
   one.

**WB35a. A re-import that changes the array list is REPORTED, never silently repaired.** Pin order
*is* array order, so a reorder genuinely moves every pin: each keeps its position while its name moves
to a different row, and a wire that was on `G1.i` is now on `G2.i` — correctly-named pins wired to the
wrong nets. There is no re-mapping that keeps the user's wires correct without moving the artwork they
drew, so the honest answer is to say so. The hidden `Arrays` parameter (§5.0) is what makes the
comparison possible; a reorder is named as a reorder, because "the array list changed" reads as
harmless.

### 9.3 Import

**WB36. A CSV wirebond-table importer is in scope**, per `mom-wirebond-kernel.md` RW16: from-pad /
to-pad / profile / diameter / material / array, one row per wire. Hand-placing 600 wires is not a
workflow; every packaging flow already has this table, and it is also the natural export back to the
bonder.

### 9.4 DXF — the bridge to the assembly house

**WB38. Wires travel in and out through DXF, and the layer name is the contract.** This is the
interoperability path that matters: assembly houses, package designers and mechanical CAD all speak
DXF, and a wire that cannot leave circuitRF is a wire somebody re-draws by hand.

**The convention, in both directions:**

| | |
|---|---|
| **Layer** | `Wires_<group>` — one DXF layer per wire group. The **prefix identifies wire geometry on import**; the suffix is the array name, so groups survive by name rather than by position. |
| **Wire** | one **3D polyline** (`POLYLINE` with group 70 bit 8 set, a `VERTEX` per point, `SEQEND`). |
| **Diameter, material** | **XDATA** under application name `CIRCUITRF_WBOND`: group 1000 = material, group 1040 = diameter. |
| **Feet** | a **filled circle** at each end, on the same layer, at the wire's own diameter — a `CIRCLE` for the outline plus a solid `HATCH`. |

**Why a 3D polyline and not an `LWPOLYLINE`.** `LWPOLYLINE` is 2D by definition. Writing a bond wire
as one would silently drop the loop height — the one coordinate a bond wire is actually about — and
the loss would be invisible until somebody measured the file. Bit 8 on the polyline's group 70 is
what tells a reader the Z groups are real.

**Why XDATA for the diameter and material.** XDATA is the DXF mechanism for exactly this: per-entity
data a foreign application attaches without disturbing anything else. A reader that does not know the
application name ignores it, so the file stays valid everywhere; a reader that does gets the wire
back completely. Nothing is encoded into the layer name beyond the group.

**Why a filled circle rather than one primitive.** DXF has no filled-circle entity. The alternatives
are worse — a wide zero-length polyline renders differently in every viewer, and a `SOLID` is a
quadrilateral — so the file carries outline-plus-hatch, which is what CAD tools themselves emit and
therefore draws correctly everywhere. A reader that ignores hatches still sees the circle.

**On import, wire layers are diverted from the layout entirely.** A 3D polyline on a `Wires_*` layer
becomes a wire, never a `PathShape`; anything else on a wire layer (the foot circles) is **dropped**,
because it is decoration regenerated on every export. Letting either back in as layout geometry would
add shapes to the design on every round trip, growing without bound while looking plausible each
time. Imported wires arrive **free** — bound to no profile — because a polyline carries a shape, not
the intent behind it.

**Units.** Coordinates are written in the file's own `$INSUNITS` and read back the same way.
Nanometres-per-drawing-unit is a direct property of that setting, and the host layout's DBU
resolution deliberately does not enter the conversion at all — neither a wire nor a DXF is expressed
in database units. **This is the single easiest thing here to get wrong:** a wire point is stored in
nanometres while the rest of the DXF writer works in DBU, and the two coincide *exactly* at the
1,000 DBU/µm default, so an omitted conversion produces a file that is perfect on a default document
and silently wrong by a factor of the resolution on any other. It is gated by a round trip at
several resolutions and several `$INSUNITS` values, not at the default alone.

**GDSII is deliberately not offered for wires, and that is a decision rather than a gap.** Assembly
houses do not work in GDSII; the format has no 3D polyline and no notion of a diameter, so a wire
could only be flattened to a meaningless 2D trace. Effort spent there buys nothing.

**Two entry points, because they answer different questions.** A full DXF **export** of a wBond
document writes the reference layout *and* the wires together — that is the file you send out.
**Import Wires…** reads only the 3D polylines from a DXF into the **current** document, leaving its
layout untouched and creating no new cell — that is how a bond list drawn elsewhere joins a design
you already have.

### 9.5 Schematic → Layout for a wBond (2026-08-16)

> **Built 2026-08-17** (`WBondCellSeeding`, called from `WorkspaceViewModel.UpdateLayoutFromSchematic`).
> A wBond is no longer offered to `SchematicToLayoutGenerator` at all — WB23 means there is no instance
> to place, and leaving it in reported *"no layout view — skipped"*. The sidecar is written ONCE and
> thereafter kept (a re-run never overwrites layout-driven edits, which is this section's whole point);
> a diverged array list is reported through `WBondPlacement.DriftBetween`, naming §9.6 as the remedy.
> Two wBonds in one cell view seed from the first and NAME the rest. §9.6 itself is still unbuilt, so
> the emitted sidecar carries no back-reference to the placed instance — the file's existence is the
> idempotency check.


The owner's target flow, in their own words: *"place a wBond component into the Schematic, be able to
change the number of arrays in the Components Parameters editor (and the array names). Then use
Schematic to Layout command to get to layout, then use a layout driven design approach to edit the
wires. Then go Layout-to-Schematic to simulate the wires in the Schematic editor."*

**WB41. Schematic → Layout emits a wirebond CELL (WB40), not a PCell.**

The distinction is load-bearing. A PCell is content-addressed and **regenerated** from its parameters
(`GeneratedCellStore`), which is exactly right for an `MLIN` and exactly wrong here: the whole point of
the flow above is that the user edits the wires *in the layout*, and a generator would overwrite that
on the next regeneration. Layout-driven design and PCell generation are opposite arrows, and a wBond
needs the layout arrow.

So the emitted cell is an ordinary one:

```
<workspace>/<cell>/
├── layout/<cell>.clay      artwork — pads, traces, die outline
├── layout/<cell>.wBond     the wires — stem-paired to that .clay  (WB40)
└── schematic/…             unchanged
```

seeded from the placed component's `Design` payload, and carrying the same `SchematicId` back-reference
every other schematic→layout instance carries, so a re-run recognises what it already emitted rather
than duplicating it.

### 9.6 Layout → Schematic — "Update Schematic from wBond Layout"

**WB42. The `Design` payload stays the SIMULATION source of truth; the layout is the EDITING source of
truth; Layout → Schematic is the one operation that reconciles them.**

Neither half can simply own the wires:

- **The payload has to remain what the engine reads.** §5.0/WB17b is unchanged and its reasons are
  unchanged — a schematic must be self-contained, with no workspace-relative path to break when it is
  copied or sent to someone else, and no "Not Found" state to represent.
- **The layout has to be where wires are drawn**, because that is where the pads are.

So they are synchronised explicitly and in one direction at a time, which is the idiom circuitRF
already uses for schematic↔layout on every other component. **Layout → Schematic re-encodes the cell's
`.wBond` into the placed instance's `Design` parameter**, and the array-drift report (§9.2/WB35a)
already exists for exactly the case that makes this dangerous: if the layout's array list has been
reordered, every pin keeps its position while its name moves, so the wires now connect to different
arrays. That is reported, never silently repaired.

**The staleness question is answered by making it visible, not by preventing it.** A placed wBond
whose payload no longer matches its cell's `.wBond` is a normal, recoverable state — the same state a
schematic and its layout are in between any two runs of the round trip — and the remedy is one command
away. What must never happen is simulating a payload the user believes they have edited; the drift
report is what makes that impossible to do quietly.

### 9.7 Carried or Linked — a per-instance choice *(added 2026-08-17)*

WB42 above assumes the payload is the only simulation source. It is not the only *possible* one: the
component has always accepted either a carried design **or** a path to a `.wBond`, with the carried one
winning where both are present. Which the elaborator emits was never an explicit decision, and the
owner's question — *should the netlist reference the `.wBond` instead of carrying a copy?* — is that
decision.

> **Naming, and why it is not "embedded".** §9.1 already spends the words *referenced* and *embedded* on
> a **different axis** — whether a `.wBond` file **embeds the layout artwork** it was drawn over or
> **references** the cells by path. That axis is about what is inside the `.wBond`; this one is about
> where a placed schematic component's **wires** come from. Using the same two words for both is how
> "does embedded actually mean referenced?" becomes a reasonable question (owner, 2026-08-17). This
> section therefore says **Carried**, which is §5.0's own verb — *"the component carries its design"* —
> and leaves *embedded* to mean exactly what §9.1 has always meant by it. **The two axes are
> independent:** a Linked instance may point at a `.wBond` that embeds its artwork, or at one that
> references cells, and neither choice constrains the other.

**WB45. A placed wBond declares its wire source: `Carried` or `Linked`. `Linked` is the default whenever
the instance resolves to a workspace cell whose `layout/` owns a `.wBond`; `Carried` is the default
otherwise, and remains the only option for an imported, foreign or workspace-less design.**

This is the same answer, and the same shape, as D10 gave on its own axis: **both**, chosen explicitly,
with the consequence stated where the choice is made.

- **`Linked`** — the netlist names the `.wBond` by a path relative to the schematic. **One copy of the
  wires**, so staleness becomes *unrepresentable* rather than reported: §9.6's reconcile command is
  unnecessary rather than merely convenient, which is exactly what §9.5's layout-driven flow wants. The
  netlist also becomes readable — §5.0's unpadded-base64 rule exists solely because a padded payload
  silently swallows whichever parameter follows it on the instance line, a trap a path does not have.
- **`Carried`** — today's behaviour, unchanged and still the portable one: no path to break, nothing to
  resolve, a schematic that travels alone.

**WB45a. The state changes only at a moment the user can see, and is announced there.** A freshly placed
wBond is `Carried` by construction — there is no cell and no file to link to yet. The file comes into
existence when **Update Layout from Schematic** runs (§9.5), and *that command* is where the instance
flips to `Linked` and says so. It must never flip as a silent side effect of a later scan noticing that a
file now exists: that would change which wires simulate without anything happening on screen.

**WB45b. The control is the parameter panel's LAST row** *(owner, 2026-08-17)*, below the arrays, the
per-array overrides and the artwork controls — an expert's control, and one the ordinary flow never
touches, since WB45a's flip is what sets it. It is there to go back to `Carried`, or to repair a link.
The consequence note of WB45 stays directly under the box wherever the box sits.

**Why §5.0/WB17b is not thereby overturned.** Its argument — self-containment, no path to break, no
"Not Found" state — is strongest for a schematic that travels on its own and weakest for one that lives
in a workspace cell. A schematic instantiating workspace cells is *already* not self-contained: §4 of
`workspace-and-project-tree.md` resolves cells by relative path and renders "Not Found" when they move.
A linked wBond is in exactly that boat, under machinery that already exists — and WB17b keeps governing
the case it was written for, which is now the `Carried` default rather than the only behaviour.

**One consequence must be built with it, not after it.** Under `Linked`, the array-drift check of
§9.2/WB35a becomes **more** load-bearing, not less. Carried drift is introduced by an explicit re-import;
linked drift arrives the moment someone reorders arrays in the `.wBond`, changing the symbol's pin order
live beneath an already-wired schematic — the same defect, arriving more quietly. The drift check
therefore has to run at elaboration for a linked instance and report, or linking is strictly more
dangerous than carrying on that one axis.

**What does NOT differ between the two.** Both are editable in the layout, because WB40 attaches wires to
a layout by what is on disk beside the `.clay`, and both routes put the same file there. Both accept the
controlling parameters of §5.5.1, because those are applied to the *decoded* design and cannot tell where
it came from. The only difference is **what the next Run simulates after a layout edit**: under `Linked`
it is the edit; under `Carried` it is still the payload until §9.6 is run, which is precisely the drift
WB42's report exists to make loud.

---

## 10. Entry points and lifecycle

**WB37. Three entry points, one editor, one document.**

1. **From the schematic** — place a wBond from the Library Palette, double-click the symbol → the
   wBond Editor opens on that instance, with its parent cell's layout as the reference geometry.
2. **From the layout** — draw a wire in the Layout Editor. If a wBond exists in this cell the wire
   joins it; if not, one is created (and named). The Layout Editor can then hand off to the full wBond
   Editor.
3. **From the Tools menu** — a blank wBond Editor with no layout context; the user drags cells in from
   the project tree as references.

All three land on the same `WBondDocument` and the same tear-off/dirty-tracking document shell used by
the layout, tech and data-display editors.

### 10.1 Where each surface fits, once WB39a and WB40 land (2026-08-16)

WB37's three entry points survive; what changes is that two of them stop needing a *different editor*.

| Surface | What it is | Wires come from |
|---|---|---|
| **Layout Editor, pushed into a wirebond cell** | The ordinary layout editor. Its wire overlay draws because the cell has a `.wBond` (WB40); the profile view and inductance panel are dockable tools that follow the active layout. **This is the layout-driven flow** (§9.5). | the cell's `.wBond` |
| **wBond Editor** (a `.wBond` opened on its own) | The same overlay and the same two panels, over a scratch or embedded reference layout. Still useful: a bond list arrives as a file, with no workspace and no cell. | the opened file |
| **Standalone `wBond` app** (§11) | The wBond Editor with no circuitRF workspace at all. A build configuration, not a second editor. | the opened file |
| **Schematic symbol** | Pins and simulation only. Never draws wires. | the `Design` payload |

**The fourth row is why §9.6 exists.** The schematic is the only surface that reads the payload rather
than a file, and it is the one that simulates — so "Update Schematic from wBond Layout" is not a
convenience, it is the join between the editing surfaces and the solving one.

---

## 11. The standalone application

**WB38. wBond compiles as its own app by the harmonicaRF route, which is a build configuration rather
than a project split** — and that route is already proven twice over in this repo.

```
src/WBond/           NEW. Framework-free: the data model, Grover kernels, images, the array
                     reduction, the incremental fill/factor cache, R(f), .wBond I/O, DRC
                     predicates, the published DataSet.  → tests/WBond.Tests/
                     NO project references at all.  NO Avalonia.

src/Ui/WBond/        NEW folder in the existing Ui project. The document, view-models,
                     the profile canvas, the panel, menus — hosted on the existing
                     Layout Editor canvas and renderer.

src/Ui/ProgramWBond.cs   A third entry point + build configuration.
                         dotnet build -p:CrfApp=wbond
```

**WB39. `<StartupObject>` is set explicitly for *all three* configurations.** `src/Ui` sets
`TreatWarningsAsErrors`, so a third `Main` is CS0017 the moment it compiles unless every configuration
names one — this is R-h8-5 from the harmonicaRF work, and it bites on the *third* entry point exactly
as it bit on the second.

**WB40. The assembly name must stay `CircuitRF.Ui` for all three binaries.** RfCore's `InternalsVisibleTo`
targets that name; a renamed assembly loses access silently. This is a directly-transferred lesson from
H8 and it is cheaper to write down than to rediscover.

**WB41. `src/WBond` is added to the `tests/Firewall.Tests` assembly-reference assertion** alongside
RfCore, Core, Engine, Cli and Harmonica.

> **Corrected 2026-08-07, measured in WB-A.** Rev 1 said `src/WBond` "references Core, Engine,
> RfCore". It references **none of them** — the Grover kernels, the image construction, the array
> reduction, the Cholesky factor, the internal-impedance table and `.wBond` I/O are all pure
> arithmetic over the BCL. That is not a tidiness point: **it is what lets `src/Core` reference
> `src/WBond`**, which is how the wBond `ComponentModel` reaches the physics without a circular
> reference. The arrow now runs Core → WBond, and WBond stays a leaf. If something in `src/WBond`
> ever needs Core, the type moves rather than the arrow reversing.

**The standalone app has every feature of the built-in tool**, per the owner — including drag-and-drop
from a circuitRF project tree, editing layout geometry, running MoM 3D when kernel W exists, and
**exporting Touchstone** through the existing `RfCore` writer. It opens `.wBond` files and, like
harmonicaRF, opens one document per window (no single-instance pipe — that is a workspace-application
behaviour and wrong here).

> **WB42. What a wBond PUBLISHES: an M-port, one port per wire ARRAY, port *k* being that array's own
> two terminals (`Gk.i`, `Gk.o`).** Settled in WB-E. Its impedance matrix is then *exactly*
> `WBondModel.ArrayImpedance(f)` — by definition, since the array reduction IS `v = Z_arr·i` in the
> branch basis — so this adds no physics and no assumption, and the port count matches the schematic
> symbol's own array pairs. A **2M-port** with every terminal ground-referenced would need a shunt
> model the reduction does not provide (and the ground plane is the *reference*, not a terminal); a
> **2-port per array exported separately** would throw away every off-diagonal, which is the entire
> content of a coupled bond array. Port identity is written into the file as `! Port[k] = <array
> name>`, the form `TouchstonePortLabels` already reads on the way in — a Touchstone whose port order
> is undocumented is a file somebody wires backwards. The frequency grid is the USER's: a bond array
> is broadband and has no natural band.
>
> **WB43. §11's "drag-and-drop from a circuitRF project tree" is satisfied by embedded geometry plus a
> folder picker, NOT by a second project tree.** A project tree implies a workspace, which is the
> thing this binary exists to do without. Embedding (§9.1) covers the hand-a-colleague-one-file case
> the standalone is *for*; naming the folder cells live in covers the rest. Opening a workspace folder
> read-only purely to browse cells is real work that reintroduces the avoided concept, and is its own
> later decision.

---

## 12. Validation

Following the house pattern: closed-form anchors first, self-consistency second, owner-generated
references last.

**Closed-form anchors** (all of these were used to check the derivations in this document and become
tests):

| oracle | tests |
|---|---|
| Rosa/Grover straight round wire self-inductance | §3.3, the GMD path — agreement measured at 0.23 % at ℓ/a = 79 |
| Grover parallel filaments, closed form | formula (b) directly |
| **Formula (a) → formula (b) as ε → 0** | the two kernels against *each other* — 9 digits at ε = 10⁻⁶ (§3.1) |
| Wire over infinite ground, L = (μ₀/2π)·acosh(h/a) | the image sign rule (WB7) |
| **Horizontal image anti-parallel, vertical image parallel** | asserted separately by hand-derived sign |
| N identical coupled wires ⇒ L_arr = (L_s+(N−1)M)/N | the array reduction (§3.4) — confirmed exactly |
| **u = L_arr·AᵀL⁻¹A·u for random excitations** | the reduction, to machine precision, no oracle needed |
| Round-wire internal impedance (Bessel), small-q and large-q series | the q-table (§3.5) at both ends |
| ~25 pH/mil (≈ 1 nH/mm) | smell test, not a gate |

**Self-consistency:** L_arr symmetric and positive definite for random geometries; a wire of length 2ℓ
equalling two cascaded ℓ wires; reversing a wire negating exactly its off-diagonal row and column;
segment-count convergence; array reduction invariant to wire ordering within an array; current shares
summing to the array current (verified: 1.000000).

**Against kernel W:** when kernel W1 exists, the quasi-static array path and W1's PEEC solve must agree
on the same geometry — different code, same physics. **This is the strongest available oracle and it
costs nothing to state now**, because it constrains both designs to remain comparable.

**Owner-generated 3D FEM regression set:** as `mom-wirebond-kernel.md` §11 — single wire over ground,
2-wire coupled pair at 10 and 100 mil pitch, 8-wire GSGSG array (the acceptance anchor).

**Performance gates**, all `Category=Benchmark` per the repo's tagging rule (measured wall-clock ≥ ~5 s,
or fast-but-wall-clock-sensitive):

- cold fill at 600 wires stays under 1 s single-threaded
- **single-wire drag frame stays under 10 ms at 600 wires** — the headline gate
- the 500k-shape layout underneath still meets its existing counter-based gates with the wire overlay
  active (WB17: a wire drag must not invalidate the layout path cache — assert the cache-miss counter,
  which is the mechanism `LayoutSpatialIndexPerfTests` already proved catches real regressions)

---

## 13. Phasing

| phase | content | gate |
|---|---|---|
| **WB-A — model and physics, headless** | `src/WBond`: data model, both Grover kernels, images, GMD self, Bessel q-table, array reduction, incremental fill + Cholesky cache, `.wBond` I/O, CSV import | Every §12 closed-form anchor green; cold fill < 1 s and single-wire incremental update < 10 ms at 600 wires, both measured; `Firewall.Tests` green |
| **WB-B — the component** | Dynamic symbol generation, M-coupled-branch stamp of **Z**_arr(ω) (WB19a), `REF` pin and return-path refusal, parameters and expression binding, loop-height sweep | A 2-array wBond stamps and S-params match the headless reduction; **Z_arr(ω)/jω → L_arr as R → 0** (WB19b, the free cross-oracle between the editor's fast path and the simulator's exact one); a sweep over loop height runs end-to-end via `Cli`; a wBond with no declared return refuses with a specific message; **the coupling audit (WB30) fires on a constructed two-wBond adjacency and names the manual remedy** — load-bearing in v1, so it gates here, not later |
| **WB-B2 — placing it** | `SymbolKind.WBond` + registry entry + palette tile; the symbol resolved from the referenced `.wBond` through `CellSymbolResolver`'s own seam (never a `.csym` on disk); §9.2 routes 2 and 3 | A placed wBond shows 2M+1 pins named in array order; a design with no arrays is refused by name; editing the design's array list updates the placed symbol live and a REORDER is reported rather than silently re-pointing the wiring; `NetBindings` arrive in `WBondModel`'s own terminal order (oracle: four distinct nets, not a terminal count); a loop-height sweep runs from a PLACED component; the coupling audit fires from the run |
| **WB-C — the editor** | Layout Editor + profile view + panel; `LoopProfile` binding; selection/drag/keyboard; **alt-drag height/span scaling**; draw, duplicate-with-pitch, transforms incl. **rotate-about-end-point** and **reverse wire**; units; pH readout; snapping; hierarchy descent; clipboard; envelope rendering | 600-wire drag holds 60 fps (exact path) and the degraded path is measured and its crossover recorded; profile edit propagates to bound wires and detaches on individual drag; all four snap kinds hit wire points; **alt-drag invariants asserted numerically — feet do not move under height scaling (including unequal foot z), normalised shape is bit-preserved, and array span scales by factor not to a common value**; rotate-about-end-point leaves the pinned end exactly fixed; reverse-wire negates exactly that wire's off-diagonal row and column |
| **WB-D — assembly DRC** | The assembly rule document, resolver, 3D predicates, loop-height-vs-span envelope, results panel | Every listed rule fires on a constructed violation and is clean on a constructed pass; a machine-vs-process violation reports its section |
| **WB-E — standalone app** ✅ **COMPLETE 2026-08-07** | Third entry point, build config, packaging, Touchstone export | Standalone binary opens a `.wBond`, edits, exports `.snp`; all three configurations build; `InternalsVisibleTo` intact (WB40) |
| **WB-F — one editor** ✅ **COMPLETE 2026-08-16** | The wBond editor HOSTS `LayoutEditorView` (WB39a); the transcribed toolbar row deleted; wirebond CELLS (WB40); the profile view and Array Inductance panel as dock tools following the active layout (§10.1) | Every tool on the hosted toolbar works with no handler in `src/Ui/Views/WBond/`; push-in/pop-out/breadcrumbs reachable in the wBond editor; the diff REMOVES more than it adds |
| **WB-G — kernel W integration** | Fidelity selector routes to kernel W1/W2 | Quasi-static and W1 agree on the shared oracle set. **Downstream of `mom-wirebond-kernel.md` LW1; nothing above depends on it** |

> **Naming note (2026-08-16).** The kernel-W phase was originally *WB-F*; `brief-wbond-wbf-one-editor.md`
> took that letter for a phase this roadmap did not anticipate, so the kernel one is **WB-G** from here
> on. Nothing else moved.

**WB-A → WB-B is a shippable increment** (a wBond that simulates, authored by CSV import), and
**WB-A → WB-C is the product**.

---

## 14. Decisions and open questions

### Decided in this document

| # | question | decision |
|---|---|---|
| D1 | Is the array reduction derivable? | **Yes — L_arr = (AᵀL⁻¹A)⁻¹**, a congruence transform on the *inverse* inductance matrix. Derived and verified numerically (§3.4). Per-wire current sharing falls out as **I = L⁻¹A·L_arr·J** (WB9) |
| D2 | How many Grover formulae? | **Two** — general skew and parallel — with the crossover at ε < 10⁻⁶ rad, chosen for speed not stability, and **d clamped to the GMD** for physical rather than numerical reasons (WB5, WB6) |
| D3 | Image sign convention | **Mirror geometry through z = 0 *and* reverse traversal**; one rule, both cases correct (WB7) |
| D4 | Fast accurate R(f)? | **Z_int/R_dc is a function of q = a/δ alone** ⇒ a 1-D table, exact and ~10 ns (WB10). Proximity staged R1→R2→R3, with R3 reusing the same filament kernel (WB11) |
| D5 | Can 600 wires drag at 60 fps? | **Yes for single-wire and small selections** — measured ~5 ms/frame. **The fill dominates, not the solve** (WB13). Large selections need the adaptive ladder (WB15) |
| D6 | Profile view / odd-ball wires? | **Normalised-span parameterisation + `LoopProfile` as a shared bindable object + envelope rendering.** Bound wires follow the group curve; individually-dragged wires detach explicitly. No propagation heuristic (§6.2, WB24) |
| D7 | Is the editor a new editor? | **No — the Layout Editor plus a profile view and a panel** (WB22). Wires are an overlay, not a `.clay` shape type (WB23) |
| D8 | More than one wBond per design? | **Allowed, because hierarchy makes a singleton impossible.** One per cell view by convention; a **coupling audit that reports unmodelled adjacency** in v1; `CouplingDomain` in v2. The audit is the load-bearing part and gates v1 (WB28–WB30a) |
| D9 | Where do wirebond rules live? | **Their own resolvable document, `.wasm`, not `.ctech`** — the relation is many-to-many and the lifecycles differ. `.ctech` owns what the pad *is*; the assembly file owns what the bonder can *do* (WB31), in three sections: machine / process / material (WB32) |
| D10 | `.wBond` embed vs. reference? | **Both**, per `.charm` §8.1's precedent. **PDK PCells flattened with the user told which and why; native circuitRF PCells not flattened** (WB33, WB34) |
| D11 | Standalone app? | **The harmonicaRF route** — third entry point, same assembly, framework-free half in `src/WBond` (WB38–WB41) |
| D12 | Does wBond need kernel W to ship? | **No** (WB1). Kernel W is a higher-fidelity engine behind the same component |

### Resolved by the owner, 2026-08-07

| # | question | decision | where it landed |
|---|---|---|---|
| O-1 | What does the component stamp — R and L reduced independently, or the exact complex reduction? | **The exact complex reduction**, **Z**_arr = (**AᵀZ**(ω)⁻¹**A**)⁻¹. Same cost order, exact under the two assumptions already made, and it removes the inconsistent-current-distribution error that matters most at 85 °C in lossy arrays | §5.3, WB19a/WB19b |
| O-2 | Default wire material | **Gold** — the RF packaging norm, and the metal of the LW1 validation set, so default and validated path agree | §6.4 |
| O-4 | Reuse the DRC expression parser, or a new vocabulary? | **Reuse it as far as it goes.** New operands (wire sets) and functions inside the existing language; only the 3D segment-to-segment predicate class is new code. Extension **`.wasm`** | §8.1, WB32a, WB31 |
| O-6 | Offer `Reverse Wire`, or infer direction? | **Offer the command.** Direction may be guessed once at creation, but is then data and only this command changes it — a silently-flipped wire negates a row and column of mutuals, which is a plausible-looking wrong answer | §6.4, WB26b |
| — | Default conductivities | **85 °C values, not 20 °C** — a current-carrying wire is never at room temperature. A 22–25 % rise in R_dc (only ~10 % at RF, WB4b), with the 20 °C reference retained as the internal model so T = 20 °C recovers it exactly | §2.3, WB4a–WB4d |
| — | Profile-view alt-drag | **Added:** alt+vertical scales loop height about the chord (feet pinned, shape preserved); alt+horizontal scales span; on a bound curve both rescale the whole array **by factor, not to a common value** | §6.2.1, WB24a–WB24c |
| — | Rotate about end point | **Added** as a transform: grabbed end follows the cursor, opposite end pinned; azimuth in the layout view, in-plane tilt in the profile view; per-wire pivots for a multi-selection, with a modifier for a shared pivot | §6.4, WB26a |
| — | Inductance readout units | **pH, fixed, never auto-ranged** — the panel exists for comparison during a drag, and a unit that switches mid-drag fakes a 1000× jump. Coupling coefficient *k* offered alongside the mutuals | §6.8, WB27a |

| O-3 | Staging of `CouplingDomain` | **v2.** The audit (WB30) ships in v1 and carries the whole safety burden; domains need elaboration-layer gather work for a case the one-wBond-per-cell convention makes uncommon | §7, WB29/WB30a |
| O-5 | Warn when the equipotential assumption is stretched? | **No warning — document it.** There is no threshold separating the good case from the bad without sheet resistance and frequency, so a span-based warning would be noise on most designs and silent on some real failures. Lives in this doc and the user documentation; landing span stays an ordinary panel readout | §3.6, WB9a |
| O-7 | Is 85 °C the right default? | **Yes — a flat default.** Visible, editable, and closer than 20 °C; not junction-referred, which would need thermal input the tool does not have | §2.3, WB4a |

### Resolved by the owner, 2026-08-17

| # | question | decision | where it landed |
|---|---|---|---|
| O-8 | Should the cell's `.wBond` move to a `wires/` view sub-folder? | **No — into `layout/`, stem-paired with its `.clay`.** It is an **attachment**, not a view: views are alternatives with one primary, and WB28 makes two `.wBond` in a cell *both real*, so primacy would encode the opposite. Stem pairing also answers the question cell-root placement could only guess at — *which* `.clay` are these wires drawn over | WB40 (revised), `workspace-and-project-tree.md` §1.2.1 |
| O-9 | A view sub-folder for "assembly" / "application"? | **Neither.** An **assembly** contains instances of other cells, so it is an ordinary cell with a hierarchical layout — already expressible, mixed technologies included; what it still needs is **z** on a layout instance, a model change rather than a directory. An **application** board *instantiates* this cell, so it is a sibling cell reached by an association reference, not a view of it | `workspace-and-project-tree.md` §1.2.1 |
| O-10 | Loop height, diameter and material on the schematic symbol? | **Yes — as controlling parameters**, array-scoped and suffixed, applied as an override at elaboration that never writes back, unset by default meaning "as drawn". This is what makes them sweepable and optimisable without a sweep mutating the design once | §5.5.1, WB44 |
| O-11 | Span too? | **Deferred.** Span is not a profile property but the pad positions; it scales by *factor* not to a value (WB24c), and it moves a bonded foot off its pad (WB24b) — safe under the layout editor's live snapping, blind from the schematic. The only one of the six needing new geometry rules rather than exposure of existing ones | §5.5.1, WB44a |
| O-12 | Should the netlist reference the `.wBond` rather than carry a copy? | **Per-instance choice, `Linked` by default** when the instance resolves to a workspace cell that owns one; `Carried` otherwise and for imported/foreign designs. **Named `Carried`, not `Embedded`** — §9.1 already spends *embedded/referenced* on a different axis (what is inside the `.wBond`), and reusing them made the two indistinguishable. Linking makes §9.6's reconcile unnecessary rather than merely convenient — but the array-drift check must then run at elaboration, or linked drift arrives more quietly than carried drift | §9.7, WB45 |

### Open — for the owner

**None.** Every question raised in rev 1 has been decided. Two items are deliberately deferred rather
than open: `CouplingDomain` (v2, §7) and kernel W integration (WB-F, downstream of
`mom-wirebond-kernel.md` LW1). Neither blocks WB-A through WB-E.

The two limitations that are now permanent-until-kernel-W, and that the user documentation must carry
rather than bury:

1. **The equipotential-pad assumption** (§3.6, WB9a) — silent by design, so the docs are the only
   place a user learns it.
2. **Coupling between separate wBond components is not modelled in v1** (§7, WB30a) — the audit
   reports it, and the only v1 remedy is a manual merge.
