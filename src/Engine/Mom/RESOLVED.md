# `src/Engine/Mom` — resolved briefs (detail, off the CLAUDE.md growth path)

Completed work's detail lands here instead of `CLAUDE.md`, which stays for durable, still-true
conventions only. Same pattern as `src/Ui/DataDisplay/RESOLVED.md` and `src/Ui/Layout/Em/RESOLVED.md`.

## RP-2c: de-embedding a coplanar edge port — the STANDARD changed, the algebra did not (2026-09-10)

`brief-em-return-plane-2c-coplanar-deembedding.md`, the arrival of the refusal RP-2a left behind.
**Scope stayed where RP-2a's did — `src/Engine/Mom` only.** Gate:
`tests/Engine.Tests/Mom/CoplanarDeembedTests.cs`, 21 tests, ~8 s.

### Gate 2's number, first, because §5 asks for it first

**|S₁₁| at the two calibration lengths is 2.7e-16 … 4.0e-15 across five geometries** — L8d's own
microstrip figure (8.5e-16) and the measurement that says the standard IS the port's neighbourhood.
Four equations fix four unknowns, so it is machine zero or it is nothing; a microstrip standard for a
coplanar port would put it at the 1e-2 level.

| W | S | h | cells/λ | across | DUT N | standard N | worst \|S₁₁\| |
|---|---|---|---|---|---|---|---|
| 0.3 mm | 0.15 mm | 2.5 mm | 20 | 12 | 104 | 48 / 62 | **1.13e-15** |
| 0.3 mm | 0.15 mm | 2.5 mm | 20 | 24 | 194 | 90 / 116 | **3.97e-15** |
| 0.3 mm | 0.15 mm | 5.0 mm | 20 | 6 | 59 | 43 / 51 | **1.81e-15** |
| 0.6 mm | 0.10 mm | 5.0 mm | 20 | 12 | 104 | 76 / 90 | **1.19e-15** |
| 0.2 mm | 0.60 mm | 5.0 mm | 20 | 12 | 104 | 104 / 132 | **9.29e-16** |

### THE FIXTURES ARE CHOSEN, AND THAT IS THE ONE THING TO READ BEFORE TRUSTING THE TABLE

On other geometries the same measurement sits at **1e-6 rather than 1e-15**, and the cause is not the
coplanar standard. A standard is mirror-symmetric by construction, so its raw `S₁₁` and `S₂₂` must be
equal; where they are not, `SolveErrorBox` averages them (the right use of a known symmetry) and the
difference comes back as **exactly** the de-embedded |S₁₁| — the two track each other one for one on
every fixture measured.

**The control settles the attribution: the same mesh driven by ordinary GROUND-REFERENCED ports shows
the same |S₁₁ − S₂₂| to within a couple of per cent** (1.14e-6 vs 1.17e-6 at 30 mm; 1.96e-6 vs
1.70e-6 at 40 mm; 9.67e-7 vs 9.69e-7 at 30 mm / across 24). So it is a property of the fill and the
solve on that geometry, not of the two-cut incidence column and not of the coplanar cross-section.
`CoplanarDeembedTests.TheGate2Floor_IsARawMirrorAsymmetryTheGroundReferencedPortShowsToo` asserts that
ratio, so the claim is runnable rather than prose.

What it is *not*, ruled out by measurement rather than by argument:
- **not the factorisation** — `UseSymmetricFactorization` true and false give the identical 2.871e-6;
- **not non-determinism** — the same mesh solved twice in one process is bit-identical, and the cap-1
  and automatic-parallelism answers agree to the last bit;
- **not the quadrature settings** — extraction order Constant vs Linear and `UseRadialTable` on vs off
  move it by 0.1%, not by decades;
- **not gridline round-off in the standard's own partition** — a fixture whose x grid is *exactly*
  mirror-symmetric (residual 0.000e0) still reads 1.17e-6.

It is left as a bounded, named floor rather than chased: it is off RP-2c's path, and P5 already
records that per-entry agreement at 1e-12 is unattainable in this fill for absolute-coordinate
reasons. **A gate 2 fixture must be checked against its own raw |S₁₁ − S₂₂| before its |S₁₁| is read
as an accuracy statement.**

### What was built

**`PlanarPortCrossSection`** — the transverse metal profile at a conductor-referenced EDGE port's
reference plane, captured from the DUT's own mesh at the cut (gridlines, a metal flag per interval,
the two driven conductors' index ranges, and how many conductors cross the plane), trimmed to the
outermost metal. It hangs off `PlanarPortResolution` as a nullable companion exactly as RP-2a's
`PlanarPortTerminal` does, so every port that predates it reads the same bits. **Only an EDGE port
gets one** — an internal delta gap has no feed, no error box and no standard, so its resolution is
RP-2a's bit for bit.

**`PlanarCalibration.BuildCoplanarLine`** — D4 over that profile: the same longitudinal partition
(end run, bulk fill, mirrored end run) with cells emitted only where the profile says metal, so no
basis crosses the slot. **The standard's own two ports are two-cut EDGE ports at one station**,
resolved through `PlanarPorts` like any other, which is what puts RP-2a's skew, level and
same-conductor checks on the standard as well as on the DUT (R-rp2c-2).

**D7's electrostatics became the MODE's.** A microstrip standard's C_pul is the whole sheet at 1 V
over the plane. A coplanar port drives the voltage BETWEEN two conductors, so the problem is +½ V on
the signal and −½ V on the return and the capacitance is `(Q⁺ − Q⁻)/2` per volt — the average rather
than one plate's charge, so the answer does not depend on which conductor the user named as the
return. `PlanarStandard` carries the per-cell potential and weight; `StaticCapacitance` and
`PlanarStaticAim.ModalCapacitance` take them. **Totalling the sheet at 1 V instead measures the COMMON
mode** — a complete, plausible reference impedance for a mode the port does not drive.

### The algebra was not re-derived (R-rp2c-3), and here is the check that says so

γ from ½·tr(M), the branch continuation and the T-matrix cascade are untouched. The evidence that
they did not need touching is gate 3: **the two-line trace and a travelling-wave fit that shares no
algebra with it agree on β to 2.0e-5 … 1.1e-4** (across = 24 / 12 / 6), inside L8d's own
2.5e-4 … 3.9e-3 microstrip band.

### Gate 4 — the closed form, with the feed removed

A CPS line in air against `Z = η₀·K(k)/K(k′)`, de-embedded:

| across | N | Z_c | closed form | ratio | β/k₀ | C_pul ÷ closed form |
|---|---|---|---|---|---|---|
| 6  | 59  | 206.80 Ω | 198.21 Ω | 1.0433 | 0.9651 | 0.9250 |
| 12 | 104 | 200.40 Ω | 198.21 Ω | 1.0110 | 0.9649 | 0.9544 |
| 24 | 194 | 196.41 Ω | 198.21 Ω | **0.9909** | 0.9649 | 0.9737 |

**Read the last three columns together, not the ratio alone.** The medium is air, so β must be k₀
*exactly* and C_pul must be `1/(cZ_c)` *exactly*; the solve gets both low and **the two errors partly
cancel in their quotient**. Quoting 0.99× as the accuracy of this kernel on a coplanar line would be
reporting a cancellation. The transverse refinement is what moves them, and β/k₀ barely responds to it
at all (0.9651 → 0.9649) — that is longitudinal discretisation, and it reaches 0.981 at cells/λ = 40.
None of it is the calibration's: the travelling-wave oracle reads the same β to 1e-4.

### The mixed run, and what a coplanar standard costs (R-rp2c-4)

One solve, one medium, port 1 to the plane and port 2 to a drawn strip: it runs, and the two ports get
differently-shaped standards (Z_c 294 Ω and 202 Ω — a calibration that had quietly shared one standard
would report one number twice). **One DCIM fit still serves the DUT and every standard**, because the
fit's key is (slab, frequency) and a standard's SHAPE is not in it — which is what keeps two shapes
affordable.

**The cost is unknowns, measured on ONE port at ONE target length so nothing else differs: the
coplanar standard is N = 269 (154 cells) against the single-conductor standard's N = 94 (56 cells) —
2.86× the unknowns and 2.27× the fill-and-solve time.** Sub-quadratic because at these sizes the cost
is the cores and the quadrature rather than the factorisation. On the whole mixed run the standards
came to 4.07× the DUT's N against 2.39× for the same geometry with both ports on the plane.

### §5's third question: the coupling residual is 35-140× SMALLER with coplanar grounds

L8d identified the drift away from the calibration lengths as direct radiative and surface-wave
coupling between the ports — f², and not monotone in length. Asked again of a coplanar pair, **against
a microstrip control on the same slab, the same mesh and the same frequency** (the comparison is
controlled; the earlier L8d numbers are on FR-4 and are not):

| f | coplanar pair | microstrip control | ratio |
|---|---|---|---|
| 5 GHz | 8.2e-6 | 1.1e-3 | 134× |
| 10 GHz | 2.3e-5 | 3.2e-3 | 139× |
| 20 GHz | 1.6e-4 | 5.4e-3 | 35× |

The rough f² scaling survives; the magnitude does not. The coplanar mode's return current is beside
the line rather than in the plane below it, so much less of the field goes around the structure — and
a de-embedded coplanar answer is correspondingly better than the "few 1e-3 at 2 GHz, few 1e-2 at
10 GHz" §10.6 records for microstrip.

### What is refused, and why each is a refusal rather than a guess

- **A THIRD conductor crossing the reference plane.** The standard drives the port's PAIR; a CPW's far
  ground strip has no stated potential, and the two reasonable readings — bonded to the return, or
  floating at zero net charge — are far apart. Refused at calibration setup, naming the count and four
  remedies (join the grounds before the plane, move the station, cut an interior gap, or read the raw
  solve). **This is the narrowing worth knowing about: a three-conductor CPW whose port names one
  ground is not de-embedded today**, and the object it needs is a multiconductor reference impedance
  (a bordered electrostatic solve with a floating conductor at zero net charge, under the accelerator
  as well as the dense path), not a wider tolerance.
- **A conformal cut cell at a conductor-referenced port.** The single-conductor path absorbs a cut by
  re-centring the cross-section on the metal's own extents; re-centring two conductors independently
  moves the SLOT between them, and the slot is most of what sets a coplanar line's impedance. The
  remedy named is the setting that caused it.
- **An EDGE pair whose two cuts are on different LEVELS**, even when both were stated. RP-2a permits
  that for an internal delta gap and is right to — it has no standard. An edge port's standard is a
  uniform line on ONE level (D3), and building it on the signal's level anyway would put the return
  conductor beside the signal instead of under it: a plausible s-parameter set for a line nobody has.
  The remedy named is the internal delta gap, which supports it today.

### Three things that had to change around the edges, each of which would have failed silently

- **`SameCrossSection` compares the whole cross-section**, not just the driven run. Two ports whose
  signal conductors match cell for cell can still need different standards — a different slot, a
  different return width, a return on the other side — and sharing one would calibrate port 2 against
  port 1's line.
- **`CheckFeedClearance` asks about the cross-section's span**, not the signal run's. Asked of the
  signal alone it reports the port's own return conductor as un-removed neighbouring metal, on every
  coplanar port, always — the same unclearable-warning failure the 2026-08-12 fix removed for a
  different reason.
- **`PlanarFeedExtension` grows BOTH conductors, by the larger shortfall.** Growing the signal's lead
  alone skews the pair, and the port is then refused at resolution — with a message about the user's
  own artwork, which is no longer what moved it. If either terminal's lead is obstructed, neither
  grows and the note says why.

### The one structural change on the shipped path, and its gate

D4's longitudinal partition was lifted out of `BuildLine` into `LongitudinalPartition` so the coplanar
builder could use it rather than copy it — a second copy of that arithmetic is a second chance for the
two standards to discretise a length differently. `CoplanarDeembedTests.Gate1` writes the pre-RP-2c
body out as a named reference and compares every gridline **bit for bit**, the way RP-2a's own gate 1
wrote out the pre-RP-2a incidence loop; a ground-referenced standard also still carries no mode
potential and no cross-section, so its electrostatics is the shipped all-ones route untouched.

**Two RP-2a tests asserted the refusal this brief removes and were rewritten rather than deleted** —
`TwoCutPortTests.Gate6_…ResolvesSinceRp2cBuiltItsStandard` now asserts the capability and its
cross-section, and `PlanarPortTests.T0_6` asserts the part neither phase changed (a conductor
reference with no return point is refused; there is no nearest-conductor search). Nothing else was
un-refused: the via-to-plane port still takes no reference, and RP-2a's skew, level, same-row and
same-conductor checks all still fire — now on edge ports too, which is where they had never run. One
refusal was ADDED rather than removed (the level-spanning edge pair above), which is the shape a
capability arriving usually has: the new path has a boundary of its own.

## RP-2a: the two-cut port — what it costs, and the factor of four nobody would have seen (2026-09-10)

`brief-em-return-plane-2a-two-cut-port-kernel.md`, built on the measurement in the section below.
**Scope was the engine only** — `PlanarPort`, `PlanarPortResolution`, `PlanarExcitation` — and it
stayed there: nothing in `src/Design`, nothing in the `.clay`, no UI. Gate:
`tests/Engine.Tests/Mom/TwoCutPortTests.cs`, 22 tests, ~1 s.

### The bit-identity gate, run before a line of feature code was written (R-rp2a-7)

An existing de-embedded two-port line (`Fr4Line`, coarse mesh, 1/2/5 GHz) and a three-port fixture
mixing two edge ports with an internal delta gap were solved and their s-matrices dumped as the raw
bit patterns of all 42 doubles, before the change and again after. **Identical, every bit.** That is
the whole L8/L9 acceptance set's protection and it is why the two weights are written the way they
are: `PositiveWeight` **is** `IncidenceSign` for a ground-referenced port — the same value, not a
rounded relative of it — and the negative block is guarded by `Negative is not null` rather than
added unconditionally, because `x + 0` is `x` for every double **except −0**, and an unconditional
empty sum would silently turn a −0 component into +0.

That one-off comparison cannot live in the tree (there is no "before" to compare to once the change
lands, and a committed golden of raw doubles would be a claim about this machine's vectorisation
rather than about this code). What is committed instead is the same claim made **in process**: the
pre-RP-2a arithmetic written out as a named reference and compared bit for bit at each of the three
sites, including `Solve`'s Y assembly rebuilt from the solution's own current vectors.

### THE NORMALISATION IS A FACTOR OF FOUR, NOT TWO — and it is the whole reason §2a exists

The brief costed the wrong normalisation at a factor of two in `Z_c`. **It is four**, and the
arithmetic is worth stating because the same reasoning fixes the sign of the whole thing:

With weight `w` on the two blocks, the two gap voltages add around the loop, so the impressed EMF is
`2w` — and the port current is read back through the *same* column, `i = w(I⁺ − I⁻) = 2w·I_line` when
the return conductor carries the whole return. So `Z₁₁ = v/i = Z_true/(4w²)`, and **only `w = ½`**
makes the port report the impedance it is actually looking into. `w = 1` reports a **quarter** of it.
The single-cut ground-referenced port is the same formula with one gap — `EMF = w`, `i = w·I_line`,
`Z₁₁ = Z_true/w²` — so its `w = 1` is correct and the two constants are not the same number by
coincidence.

`w = 1` produces a complete, plausible s-matrix that is **reciprocal and passive**, so gates 3 and 4
do not catch it. Only an external oracle does.

### Gate 2: coplanar strips against conformal mapping — and it landed at 0.9999 on the first try

The fixture is a **coplanar-strips line in AIR** (ε_eff = 1 exactly, β = k₀ exactly) with the ground
plane put ~10 transverse extents below so its image is a perturbation, driven by ONE two-cut internal
delta gap at the centre. The port is a series source, so it looks into two open-circuited CPS stubs in
series: `Z₁₁ = −2j·Z_c·cot(βℓ/2)` with `Z_c = η₀·K(k)/K(k′)`, `k = S/(S+2W)` — exact for
zero-thickness perfect conductors in a homogeneous medium (its CPW dual satisfies
`Z_CPW·Z_CPS = (η₀/2)²` identically), and dependent on nothing in this repository but an AGM.

Measured, `Im(Z₁₁)` ÷ closed form, across eleven combinations:

| W | S | h | f | θ = βℓ/2 | cells/λ | min cells | N | ratio |
|---|---|---|---|---|---|---|---|---|
| 0.3 mm | 0.15 mm | 2.5 mm | 10 GHz | 1.0 | 10 | 3 | 34 | **0.9999** |
| 0.3 mm | 0.15 mm | 2.5 mm | 10 GHz | 1.0 | 40 | 3 | 90 | 1.0083 |
| 0.3 mm | 0.15 mm | 2.5 mm | 10 GHz | 1.0 | 10 | 6 | 120 | 0.9669 |
| 0.3 mm | 0.15 mm | 2.5 mm | 10 GHz | 1.0 | 40 | 8 | 374 | 0.9630 |
| 0.3 mm | 0.15 mm | 2.5 mm | 10 GHz | 0.4 | 10 | 3 | 24 | 1.0284 |
| 0.3 mm | 0.15 mm | 2.5 mm | 10 GHz | 1.4 | 10 | 3 | 44 | 1.0982 |
| 0.3 mm | 0.15 mm | 2.5 mm | 4 GHz | 1.0 | 20 | 4 | 90 | 1.0518 |
| 0.3 mm | 0.15 mm | 2.5 mm | 20 GHz | 1.0 | 20 | 4 | 90 | 0.9373 |
| 0.3 mm | 0.15 mm | 5.0 mm | 10 GHz | 1.0 | 20 | 4 | 90 | 0.9991 |
| 0.6 mm | 0.10 mm | 2.5 mm | 10 GHz | 1.0 | 20 | 4 | 90 | 1.0176 |
| 0.2 mm | 0.60 mm | 5.0 mm | 10 GHz | 1.0 | 20 | 4 | 90 | 0.9485 |

**Every one inside ±10%, and `w = 1` would have put every one at 0.25.** `Re(Z₁₁)` is under 0.05% of
`|Im|` on the well-resolved cases, as an open stub in a lossless medium must be. The residual spread
is discretisation, the finite ground plane and open-end fringing; it is not a scaling question, and
the closed form's own idealisation (zero-thickness metal, no ground plane) accounts for the sign of
the drift at the extremes. The θ = 1.4 rung is the loosest because it is nearest the λ/4 pole, where
`cot` is small and everything is amplified.

**Do not read the 0.9999 as accuracy.** It is one rung of a spread that runs 0.937–1.098, on a
34-unknown mesh; the *conclusion* it supports is which of two normalisations is right, and that
question only needs a factor of two to be resolvable.

### The audit was complete, and one of the three sites went away

R-rp2a-9 said the one-signed-block assumption lives in exactly three places in `PlanarExcitation` and
**that is where it was** — no fourth site turned up anywhere in `src`. `Solve`'s Y assembly is now a
call to `PortCurrent` rather than a second copy of the same loop, which is that file's own headline
rule (a second copy of the sign convention is where it drifts) and leaves two sites, not three.

Everything else keys off `PlanarPortResolution`, and R-rp2a-10's "grow, do not fork" is what made the
rest cheap: the negative terminal's counterparts hang off a nullable `PlanarPortTerminal`, so a
ground-referenced resolution is the record it always was, field for field, and every consumer that
never asks reads the same bits. **`PlanarPortTerminal` deliberately carries no `Side`, `Direction`,
`Z0` or `IncidenceSign`** — a port has one polarity and one reference impedance, and a second copy of
either is how the two would drift apart.

Nothing gates on `Kind == InternalDeltaGap` had to be touched: `PlanarFeedExtension`,
`PlanarGroundPath` and `PlanarMeshPitchField` all gate on `Edge` or `Internal`, and
`IsDeembeddable` already excluded an interior cut from every calibration path.

### The two cuts landed on one station on every fixture tried

§6 asks whether the mesher's own gridlines ever force a skew on correct input. **They did not, on any
of the eleven CPS geometries, the FR-4 mixed fixture or the two-level fixture.** The reason is
structural rather than luck: the longitudinal gridlines are a property of the whole mesh, not of a
conductor, so two points at one `x` see the same candidate set — and both cuts are chosen by the same
nearest-usable-gridline rule. The skew refusal fires only when the two points genuinely disagree, or
when the mesh offers one conductor a rooftop at a gridline and not the other, which refining fixes.

### What the pair checks refuse, and why each is a refusal rather than an adjustment

- **Different stations** — a port plus a length of line, which solves to a complete and plausible
  s-matrix for a structure nobody drew. The refusal names both coordinates and the distance.
- **Levels inferred apart** — L9d/D2's reason with two chances instead of one. Stating BOTH levels is
  what permits a port whose cuts span levels; inferring them and landing apart is refused.
- **One rooftop row** — the two blocks cancel exactly, which is a short across the port.
- **One conductor** — an ordinary internal delta gap wearing a costume. Tested by 4-connectivity over
  the mesh's own cells on the cut's level, deliberately **in-plane only**: two conductors joined by a
  via elsewhere are not caught, and are not claimed to be. That is a short in the structure, which the
  solve reports as one, rather than a port that resolved to the wrong thing.

`CoplanarGround` and `SecondConductor` are **one mechanism and one code path** (R-rp2a-12); both enum
members survive because the note reads differently — a ground strip against a second signal line — and
because a coplanar calibration will read them differently again.

### The refusal that stays

A conductor-referenced **EDGE** port is refused, and the refusal names the missing capability rather
than a phase (R-mom-17): a coplanar calibration standard — a coplanar line with its own `Z_c`, `β` and
static capacitance. `PlanarCalibration` builds uniform single conductors over the plane and
`PlanarPortResolution`'s cross-section fields describe one conductor, so calibrating against those
would publish s-parameters that are plausible and referenced to nothing. The refusal offers the remedy
that works today — cut it as an interior gap, which has no feed and needs no standard. An internal
(via-to-plane) port takes no reference at all: its negative terminal *is* the plane, by construction.

## RP-2's first measurement: the mesh cannot span a slot, and never will (2026-09-10)

`brief-em-return-plane-2-per-port-reference.md` asks, before any solver work, whether the surface
mesher produces cells spanning a slot between two conductors — R-rp2-3, on which the whole of that
brief rests. **It was measured, the answer is no, and the reason is structural rather than an
omission.** The brief was too large for one round on top of that, so it is superseded by RP-2a/2b/2c
(see `docs/sonnet-briefs/`); this section is the measurement and the audit those three are built on.

### The measurement

`CoplanarSlotMeshTests` meshes a conductor-backed CPW cross-section — centre strip, two slots, wide
grounds — at both the coarse and the shipping mesh settings, over slot widths from 1 mm down to
50 µm. **Bases spanning a slot: zero, in all sixteen cases.**

| W | S | settings | N | min cell edge | grid rows in slot | cells in slot | bases spanning slot |
|---|---|---|---|---|---|---|---|
| 2.9 mm | 1 mm | coarse | 86 | 600 µm | 2 | 0 | **0** |
| 2.9 mm | 1 mm | shipping | 585 | 85.2 µm | 6 | 0 | **0** |
| 2.9 mm | 0.3 mm | coarse | 86 | 600 µm | 1 | 0 | **0** |
| 2.9 mm | 0.3 mm | shipping | 585 | 85.2 µm | 3 | 0 | **0** |
| 2.9 mm | 0.1 mm | coarse | 86 | 600 µm | 1 | 0 | **0** |
| 2.9 mm | 0.1 mm | shipping | 585 | 85.2 µm | 2 | 0 | **0** |
| 1.0 mm | 50 µm | coarse | 184 | 250 µm | 1 | 0 | **0** |
| 1.0 mm | 50 µm | shipping | 1,119 | 29.4 µm | 2 | 0 | **0** |

Note the 50 µm slot at the coarse settings: the slot is **one twelfth of the bulk cell size** and
still owns a grid row of its own. That is the finding, not a coincidence.

### Why it is structural, and why refining cannot change it

Three facts of `SurfaceMesher`, composed:

1. **Every polygon edge is a HARD gridline** (`CollectBoundaryLines`), so both edges of a slot are
   gridlines regardless of the requested cell size.
2. **A cell exists only where the grid row's CENTRE is inside metal** (`RowSpans`, and the conformal
   path's equivalent). The row between two hard lines that bound a slot has its centre in the slot.
3. **A basis is a pair of GRID-ADJACENT cells** (`cellAt[iy*nx+ix]` vs `ix+1`), so two conductors
   with an empty column between them are not adjacent and produce no basis.

A slot of nonzero width therefore always contains at least one metal-free grid row, and refining the
mesh **adds** rows to the slot — it can never remove the last one. This is not a threshold that a
finer mesh crosses. **R-rp2-3 as literally written — "the gap is across the SLOT and the mesh must
carry cells that span it" — cannot be satisfied by this mesher, and making it so would mean a new
basis family over non-metal (a slot/magnetic-frill basis), which is a far larger question than a
per-port reference.**

The test asserts the state of the world rather than a wish, and it is worth keeping in that form: a
basis appearing across a slot would mean two conductors shorted through the mesh, which is worse than
a missing feature.

### What this leaves reachable, and it is the whole capability

A conductor-referenced port does **not** need a basis across the slot. It needs **two cuts, one in
each conductor**, driven against each other — the incidence column carries a signed block on the
signal conductor's row and the opposite block on the return conductor's row. Each cut is an ordinary
delta gap in its own metal, so R-rp2-3's real requirement ("a real gap in the mesh, not a pair of
points") is met per conductor, and **no new basis family and no mesh change are involved**. That is
what RP-2a builds, and it is the same `Y = BᵀZ⁻¹B` with a two-entry column instead of a one-entry one.

The normalisation of those two blocks is the part that must be **measured, not reasoned into place**:
±1 on both blocks impresses twice the loop voltage that ±½ does, and the two differ by a clean factor
of two in `Z_c` — a complete, plausible, wrong answer of exactly the shape this file already records
for the internal port's sign. RP-2a gates it against a CPW closed form.

### The audit: where `GroundPlane` was assumed rather than checked

The brief asks for this specifically, and the answer is better than expected — the "one signed row
per port" assumption is spelled in **one file, three times**, all in `PlanarExcitation`:

- `RightHandSide` — `foreach (int m in port.BasisIndices) rhs[m] = port.IncidenceSign;`
- `Solve`'s Y assembly — `sum += col[m]` over one index set, times one sign.
- `PortCurrent` — the same sum, times the same sign.

Everything else keys off `PlanarPortResolution`, whose shape is the real constraint: `BasisIndices`,
`WidthM`, `ReferencePlaneM`, `OuterEdgeM`, `TransverseLines` and `LongitudinalRunM` are all
**singular** — one conductor, one cut, one cross-section. `PlanarCalibration` copies
`TransverseLines` verbatim into its standards (D4), so a second conductor is invisible to the
calibration path until that record grows a second set. **That, not the excitation, is the reason
RP-2c (de-embedding a coplanar EDGE port) is a separate brief from RP-2a.**

Two prose sites state the plane-as-negative-terminal rule and become false for a mixed run:
`PlanarExtractor.cs`'s "That plane is the negative terminal of every port in this run and is not
selectable per port" (both spellings, overridden and inferred), and `PlanarPort.cs`'s D3 header.
No *code* was found that silently assumes it beyond the three lines above.

## The internal port — a port from the metal to the ground plane (2026-08-25)

The third of §10.6's port types, and the one the design note had written down as *not* built with a
costing attached. That costing was wrong in the encouraging direction, and the reasons are worth
keeping.

**`PlanarPortKind.Internal`.** The port is placed on the metal and means "here, referenced to
ground"; its + terminal is the metal, its − terminal is the stackup's ground plane. It drives the
ground-attachment bases of the via under it — every cell of that footprint, since they are one
conductor at one potential.

**Findings.**

1. **The excitation is D1's after all, and the design note's stated reason for doubting that was
   about the wrong integral.** §10.6 recorded that an attachment basis "has a different
   normalisation… `NetCharge` is exactly −1, and L9c's `∫∇·f dS = 0` explicitly does not hold for
   it. The incidence matrix and the reaction integral both need their own derivation." The charge
   facts are true and are properties of the FILL, which was already built and gated; a delta gap's
   reaction is an integral against the CURRENT, and the attachment basis carries unit total current
   across the connection it spans exactly as a rooftop does across its shared edge. So
   `⟨f_m, E^imp⟩ = v` holds exactly, with no footprint area and no quadrature in it, and the port is
   one more ±1 row of the same incidence matrix. **No new basis, no new excitation, no new algebra.**

2. **THE SIGN WAS DERIVED WRONG AND THE MEASUREMENT CAUGHT IT.** The first implementation took
   `IncidenceSign = −1`, from a written derivation of which lip of the gap is the + terminal (the
   impressed field points from + to −, so the + lip is the one on the −f side, so a downward-flowing
   positive current puts + on the metal…). It produced a complete, plausible s-matrix with |S₁₃|
   right to two figures and **every term through the port turned by π**. The convention is `+1` —
   the same "current flows into the structure" rule every other port here uses.
   **A port's sign is unobservable through any termination**: every reduction carries `S_i3·S_3j`
   and both factors flip together, so no amount of loading the port could have shown it. What shows
   it is a structure whose answer is known independently — a short line with a via to ground at its
   centre is three 50 Ω ports meeting at one node above the plane, S_ii = −1/3 and S_ij = **+2/3**.
   That gate exists (`InternalPortTests.ASmallStructureAtLowFrequency…`) and it is the reason the
   released sign is right.

3. **A centred internal port measures S₁₃ = +S₂₃**, against the centred delta gap's S₁₃ = −S₂₃.
   The two gates sit beside each other on purpose: it is the measurement that distinguishes a
   ground-returning port from a series one, and a magnitude plot never would.

4. **Shorting the port reproduces the plain board**, to < 1e-9 — reducing the 3-port with Γ₃ = −1
   against an ordinary 2-port solve of the same artwork. A port is a gap with a source in it, so
   terminating that gap in a short is the structure with no port on it: an end-to-end oracle for
   everything between the incidence row and the published matrix, needing no external data.

**The via is the SOLVER's to build (owner, same day).** Requiring the user to draw a via first was
the original build and was wrong for the workflow: the port is placed on the metal, and whether the
drawing happens to have a via there is a question about the drawing. `PlanarGroundPath` runs before
meshing, beside R-fed-1's feed extension and by the same three rules — a drawn via wins, a missing
one is built, and what was built is reported by port and by size. The problem reaches the mesher **by
reference** when nothing was added, so a board that draws its own vias is bit-identical.

**Its size is a PROCESS dimension, not a mesh cell**, and that choice is load-bearing: the built
via's inductance is part of the port's answer, so sizing it from the mesh would make the answer a
function of *Cells per wavelength* — refining the mesh, the one thing a user does to converge a
result, would move it for a reason unrelated to convergence. The order is the technology's default
via drill, then its default pad, then (Ui-side, reported as the rule of thumb it is) a quarter of the
substrate height. The stackup's own `Via` entries carry a fill, a wall and a span but **never a
diameter**, so "this technology declares no via" is not a reason to refuse the port.

**Cost of the four physics gates: they are `Category=Benchmark`** (5.5–11.5 s each, measured). Not
because they measure time — they assert physics — but because a via-bearing fill costs seconds per
frequency whatever the mesh: the DCIM fit count for a problem carrying vertical current is a fixed
per-frequency cost, so shrinking the board does not shrink it. The default gate keeps the resolution,
the refusals and a solve-free incidence-row wiring test, which is where a change to this area breaks
first.

**`src/Engine/Mom/CLAUDE.md` §3.4 still says "`PlanarPortKind` — TWO port types" and lists the
kinds; that sentence is superseded by this entry.** It was left alone deliberately (this repository's
standing instruction is that findings go here, never into a `CLAUDE.md`).

### What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarPort.cs` — the third enum value, `Direction`/`IncidenceSign` for it,
  `PlanarPortResolution.FootprintAreaM2`, `TryResolveInternal` (footprint walk by 4-connectivity,
  two refusals), and its own `Describe`.
- `src/Engine/Mom/PlanarGroundPath.cs` — new: the path the port grows for itself.
- `src/Engine/Mom/PlanarKernel.cs` — one call, before the feed extension, on both mesh paths.
- `src/Engine/Mom/PlanarFeedExtension.cs` — gated on `Kind != Edge` rather than on the gap alone.
- `src/Engine/Mom/PlanarSolve.cs` — the not-de-embedded note now lists the two internal kinds apart.
- Ui: `EmPortExtraction` (kind, the built path and its width, notes and refusals),
  `EmSetupModel.DeclaresInternalPort`, `EmRunService.InternalPortNeedsFullWave`, the panel row and
  its type list, `PlanarPortKindNameConverter`, and the layout mark
  (`LayoutRenderer.DrawInternalPortMarker` — a ring with a ground symbol; the mark channel now
  carries the port KIND rather than only an anchor).
- Tests: `tests/Engine.Tests/Mom/InternalPortTests.cs`, `tests/Ui.Tests/Em/InternalPortUiTests.cs`.
- Docs: `docs/design/mom-engine.md` §10.6, the user chapters `mom-engine.md` / `em-setup.md`, and a
  new one — `docs/user/src/reference/stackup.md`, which is where "what is my port's negative
  terminal" is now answered, with a cross-section figure generated from the shipped technology.

## The internal delta-gap port (2026-08-24)

The second of §10.6's two v1 port types, listed as "later" in the design note from the first draft
and as deliberately out of scope in this directory's own `CLAUDE.md` §7 until now. Both statements
were removed rather than softened; the design note's own §10.6 carries the full write-up.

**It cost almost nothing in the fill or the excitation, and that is the point.** D1 already defines a
port as a delta gap across the shared edge of two adjacent cells driving the rooftop row that spans
it — nothing in that says the cells have to be the two outermost ones. `PlanarPortKind` picks WHICH
shared edge: `Edge` marches in from the named side as before, `InternalDeltaGap` scans the run's
interior gridlines and takes the one nearest the placed point that has metal (and a rooftop) on both
sides. Everything downstream — `PlanarExcitation`, `Y = BᵀZ⁻¹B`, the s-matrix — is untouched.

**Three findings worth keeping.**

**1. A delta gap is a SERIES source, and the first gate asserted the wrong identity.** A gap at the
exact centre of a uniform line is mirror-symmetric about its own cut, so the obvious oracle is
"it couples equally to both ends", S₁₃ = S₂₃. The solve returned S₁₃ = **−**S₂₃, equal and opposite
**to sixteen digits**. That is correct and the oracle was wrong: a series gap pushes current one way
along the conductor, into the line on one side of the cut and out of it on the other, so the two
halves are driven in ANTIPHASE. A SHUNT port — current injected against the ground plane — would be
symmetric. The difference is a hard π, invisible in a magnitude plot, and the test now asserts the
antisymmetry at 1e-6 (it holds far tighter than that) precisely because a later change to the
incidence sign would otherwise pass. `CLAUDE.md`'s low-frequency-floor note already said the port is
"necessarily a **series** delta-gap"; this is the same fact arriving from the other direction.

**2. `IndexOf` CLAMPS, so "is the port on the metal?" needs the grid extent as well.** The transverse
index lookup returns the nearest cell for an out-of-range coordinate rather than a miss — right for an
edge port, whose label may legitimately sit just off the end face it names and whose longitudinal
coordinate is not read at all. For an internal port it is wrong in the worst way: a point metres from
the artwork clamps onto the outermost row, finds metal there, and cuts a gap the user never asked
for — a complete, plausible s-matrix for a structure nobody drew. The internal branch now tests both
axes against the grid's own extent before asking about metal. *(The pre-existing "outside the meshed
region entirely" refusal on the edge path is, for the same reason, effectively unreachable. Left
alone: the Ui-side extractor refuses an off-metal label before the engine sees it, and widening the
edge path's behaviour is a change to shipped port resolution that wants its own measurement.)*

**3. De-embedding stays ONE code path, via an identity error box.** An internal port takes
`PlanarSolve.IdentityBox` (a₁₁ = 0, a₂₂ = 0, a₂₁ = 1) and `Z_c = Z₀`, which makes
`PlanarDeembed.Apply` and `Renormalise` the identity on its row and column. This is not "de-embedding
an internal port": it is arithmetic that provably changes nothing, and it was chosen over partitioning
the matrix because a unit a₂₁ also leaves a de-embedded NEIGHBOUR's mixed terms untouched — the
partitioned alternative would have needed its own proof of exactly that. Gated by solving the same
problem with `Deembed` on and off and requiring < 1e-12; tolerant rather than an equality only because
the ON path still passes through an LU of the identity matrix, so asserting bit-identity would be
asserting a property of NumFlat's LU.

## `brief-em-deembed-ceiling-closeout.md` — the de-embedded path's OWN dense ceiling (2026-08-14)

Closes `brief-em-aim-ceiling.md`'s own §13 closing subsection ("The limitation this surfaced"): the
accelerated ceiling (`SurfaceMesher.AcceleratedUnknownCeiling = 12,000`) moved for the DUT's own
N×N basis system, but `PlanarDeembed.StaticCapacitance` — the m×m CELL system D7's reference
impedance needs, computed once per calibration standard — is a separate, always-dense computation
that was never wired to refuse honestly. On the owner's own reported taper (`brief-em-aim-ceiling.md`
§0; Z1 = 6.92 Ω, Z2 = 100 Ω, L = 28.575 mm), turning Accelerated solve on as the panel's own refusal
instructed produced a mesh that passed (N = 6,581) and a DUT Z-matrix that actually solved, then a
throw twenty REAL MINUTES later out of `PlanarDeembed.CapacitancePerMetre`, once the calibration
standard's own static-capacitance solve (N = 6,466 — 98% of the DUT's own N, because D4 makes a
standard reproduce the DUT's transverse gridlines verbatim) finally ran into
`PlanarFill.BuildCores`'s dense guard. R17's own contract has never been "there is a ceiling" — it
is "surface the predicted N before solving, and refuse politely above it", and this run predicted,
passed, and failed late — the one thing that contract exists to prevent.

**No physics changed.** D6/D7's algebra, `SpectralGreens.cs`, `Dcim.cs`, `SingularExtraction.cs` and
`PlanarAim.cs` are all untouched. This is a refusal-timing fix, a message-accuracy fix, and one
measurement that came back negative.

### C1 — refuse at setup, not twenty minutes in

`PlanarSolve.cs`'s `if (st.Deembed)` block already assembled every calibration standard's own
`Mesh.Bases.Count` into a `sizes` list and reported the ratio against the DUT's own N — the
prediction R17 wants was present, correct, and simply unenforced. A loop added right after that
list is built (before any calibrator's raw solve, before any dense fill) now checks each standard's
own N against `SurfaceMesher.UnknownCeiling` and throws `InvalidOperationException` naming:

- **why the accelerator does not help** — D7's static ω → 0 capacitance solve is a structurally
  different m×m system over cells, not the N×N frequency-domain basis system
  `PlanarFillSettings.Aim` covers, so turning it on (which the panel just told this same user to do)
  does not move THIS ceiling;
- **the actionable remedy, with what it costs** — turn de-embedding off and read the raw solve; those
  s-parameters include the port discontinuity rather than being the structure's own response
  (`docs/design/layout-view.md` §10.6: "A raw port excitation includes the port discontinuity;
  reporting those s-parameters as the structure's response is simply wrong"), so this is named as a
  diagnostic-only fallback, not a second success case;
- **both N's** — the DUT's own mesh count and every calibration standard's own count, not just one.

No mesh remedies (`Lower Cells per wavelength`, `turn the edge mesh off`, …) are offered — the
parent brief's own §0 already measured them inert on this class of geometry (width-ratio-driven N
growth), and naming an inert remedy is the exact defect `brief-em-aim-ceiling.md` closed once
already for the dense mesh refusal.

**Gate 1**, `EmCeilingRefusalTests.Gate1_TheOwnersReportedTaper_DeembedOn_AcceleratedOn_RefusesAtSetup_NotAfterADenseFill`:
the owner's exact reported taper, de-embedding ON, accelerated ON, through the real entry point
(`PlanarSolve.Run`) — now refuses in **88 ms** (measured), naming the standard's N, the reason the
accelerator does not help, and the de-embed-off remedy with what it costs. Before this brief the
identical call sequence appeared to succeed and only failed twenty real minutes in.

### C2 — the guard at `StaticCapacitance`'s own call site was measuring the wrong allocation

`PlanarFill.BuildCores`'s shared `GuardCeiling(n)` quotes an **n×n dense complex matrix** because
that is what its OTHER callers (`PlanarFill.Fill`, `PlanarSystem.Build`) go on to allocate.
`PlanarDeembed.StaticCapacitance` never builds one: `PlanarFill.ScalarPotentialMatrix` returns an
**m×m** `Mat<Complex>` over CELLS (D4's potential-coefficient matrix P), and `StaticCapacitance`
then builds a *second* m×m `Mat<Complex>` from it and factors that — so the megabytes the shared
guard's own message would quote are not the cost of what is actually about to be allocated at this
call site. This is §7's own "quotes 381 MB against a real run cost of ~607 MB" defect in a second
location.

Measured on a real calibration standard (`EmDeembedCeilingTests.C2_MVsN_OnARealCalibrationStandard_IsMeasuredNotAssumed`,
a 6 mm FR-4 line's own port, coarse mesh): **m = 32 cells, n = 52 bases, m/n = 0.615** — a standard
is long and thin (few cells across the port width, many along its length), which is why the ratio
sits above the square-grid asymptote of 0.5 rather than at it; either way m is always strictly under
n. On a synthetic mesh sized to straddle the dense ceiling the OLD way but not the new one
(`EmDeembedCeilingTests.C2_TheGuardAtThisCallSite_QuotesCellsAndTheRealMegabytes`, m = 4,500 cells,
n = 8,855 bases): the shared guard's own n×n formula would quote **1,196.5 MB**; what this call site
actually holds (two m×m complex matrices) is **618.0 MB** — corroborated by a real
`GC.GetAllocatedBytesForCurrentThread()` measurement on an actual solve
(`C2_TheFormula_IsCorroboratedByAMeasuredAllocation`), not left as arithmetic alone.

**Fix**: `PlanarDeembed.GuardCapacitanceCeiling(mesh)`, called at the top of `StaticCapacitance`
before `PlanarFill.BuildCores` runs. **The threshold is UNCHANGED** — it still asks
`n > SurfaceMesher.UnknownCeiling`, exactly what `BuildCores`'s own shared guard asks, per the
brief's own instruction not to tighten or loosen the shared question for every caller. Only the
MESSAGE differs: it names the cell count, the two-m×m-matrix accounting, and says explicitly that
this solve never builds the n×n matrix the shared guard's own wording describes.

### C3 — is the ω → 0 static system real? Measured, and the answer is no

The premise this milestone existed to test: if `PlanarKernelTerms.StaticScalar`'s output is
genuinely real (as its own doc comment's "there is no logarithm and no linear term" language might
suggest), `StaticCapacitance` could move from `Mat<Complex>` + a general LU to `Mat<double>` + a
symmetric factorisation — roughly 4× less memory, i.e. 2× the reachable linear dimension, which
could be the difference between refusing the owner's own 6,466-cell standard and running it
comfortably.

**Measured, not assumed, and it fails.** `StaticScalar`'s image ratio is
`k = (1 − εᵣ*)/(1 + εᵣ*)`, and `εᵣ* = εᵣ(1 − j·tanδ)` is complex for any lossy substrate — which is
every starter this repository ships (`Fr4Starter`: εᵣ = 4.4, tanδ = 0.02; `GaAsStarter`: εᵣ = 12.9,
tanδ = 0.002). `EmDeembedCeilingTests.C3_TheStaticScalarKernel_HasAMateriallyNonzeroImaginaryPart_OnALossySlab`:

| slab | tanδ | `Inverse.Imaginary / Inverse.Real` |
|---|---|---|
| FR-4 | 0.02 | **1.63e-2** |
| GaAs | 0.002 | **1.86e-3** |

Neither is floating-point noise (machine epsilon would read ~1e-16) — both scale with the
substrate's own tanδ, because they come directly from it. Carried through an actual solve on a real
calibration standard (`C3_TheDiscardedImaginaryPart_IsMaterial_OnARealCalibrationStandard`): the
total `StaticCapacitance` sums before its own `.Real` truncation has `|Im/Re| = 1.76e-2` — consistent
with the term-level ratio, confirming the discarded part is not merely an input-level artifact that
cancels during the solve.

**Why this closes the door on the representation win, not just narrows it**: the per-cell-pair
remainder term (`PlanarKernelTerms.Remainder`, the convergent image series) mixes powers of the
SAME complex `k` with REAL, ρ-dependent weights that differ per cell pair — so the resulting matrix
is not of the form `A_real·(1 + jα)` for one global loss factor α (which is the one shape where
`Re(A⁻¹b) = Re(A)⁻¹b` would hold). The matrix's complex structure is genuinely entry-dependent.
Converting to `Mat<double>` from `Re(A)` would therefore change the published C_pul, not merely
its representation — failing R-dcl-5's bit-comparison requirement outright, not by a measurable-but-
small margin. **No representation change was made.** `_cPerMetre`'s existing once-per-calibrator
cache (`PlanarPortCalibrator`'s own `if (double.IsNaN(_cPerMetre))`) already holds this cost to one
solve per standard per sweep and needed no change.

### C4 — the decision

**The de-embedded path's effective ceiling is `SurfaceMesher.UnknownCeiling` (5,000), asked of every
calibration standard's own N, exactly as it is asked of the DUT's dense path** — the accelerated
ceiling never applies to this step and, per C3, cannot be widened by a cheaper representation
either. For the class of geometry that motivated the accelerator (width-ratio-driven N growth), a
standard reproduces the DUT's own transverse gridlines verbatim (D4), so **a wide-port DUT's
calibration standard is typically comparable in size to the DUT itself** — on the owner's own file,
98%. This is not a separate, narrower "wide port" concept: it is the same N the DUT's own mesh
report already predicts, applied a second time to the standard.

**What this brief delivers**: the run now tells the user this honestly, in seconds, with a remedy —
turning the accelerated solve on genuinely helps the DUT's own Z-matrix and is offered as it should
be; de-embedding a wide-port part past the dense ceiling is not currently possible, and the run says
so instead of appearing to succeed and failing twenty minutes later.

**If `PlanarDeembed.StaticCapacitance` is ever accelerated, it is its own brief, not built here.**
It would have to compress an **m×m system over mesh CELLS**, under a **static (ω → 0), genuinely
complex kernel** (C3: not reducible to a real one for any lossy substrate) — not the **N×N system
over rooftop BASES** under a **frequency-dependent DCIM-fitted kernel** that
`PlanarFillSettings.Aim` already accelerates. AIM's own near/far split and auxiliary-grid
interpolation are built around the basis geometry and the DCIM fit's height dependence; neither
transfers directly to a cell-indexed static Green's function whose singular structure is the
`(1 + k)/(4πρ)` term plus a convergent image series, not DCIM's exponential fit. Whether a
hierarchical/low-rank compression that stays genuinely complex is worth its own engineering cost
is undecided and unstarted.

### What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarSolve.cs` — a setup-time refusal loop in the `if (st.Deembed)` block, right
  after the existing `sizes`/`totalN` computation.
- `src/Engine/Mom/PlanarDeembed.cs` — `StaticCapacitance` calls a new private
  `GuardCapacitanceCeiling(mesh)` before `PlanarFill.BuildCores`; same threshold, corrected message.
- `tests/Ui.Tests/Em/EmCeilingRefusalTests.cs` — new gate 1 test; the existing
  `…ACTUALLYSOLVES…` benchmark test's doc comment now states explicitly (§10.6) why the DUT's raw
  solve it gates is not this user's published success case.
- `tests/Engine.Tests/Mom/EmDeembedCeilingTests.cs` — new: the C2 m-vs-n measurement and guard-message
  gates, and the C3 negative-result measurements.
- Nothing in `PlanarDeembed.cs`'s D6/D7 algebra, `SpectralGreens.cs`, `Dcim.cs`,
  `SingularExtraction.cs` or `PlanarAim.cs` changed.


---

# P1 — honest memory accounting for the planar solver (2026-08-29)

`docs/sonnet-briefs/brief-em-p1-honest-memory-accounting.md`. **This brief measured and re-worded.
It changed no arithmetic** — no fill, no factorisation, no Green's function, no s-parameter moved by
a bit, and no ceiling constant moved at all.

## What was wrong

`SurfaceMesher`, `PlanarSystem` and `PlanarFill` each carried their own copy of R17's refusal and
all three quoted `16·N²` — "381 MB at the ceiling". That is exactly the dense matrix and it is
silent about everything live beside it. §7 of `CLAUDE.md` already carried the open item "381 MB
quoted against ~607 MB real — owner's call". **607 was itself an underestimate.**

The same class of defect sat in `PlanarAimReport.ApproximateBytes`, which counted the accelerator's
own arrays and stopped before the sparse LU it builds from them.

## What was measured

Every number below is in `HISTORY.md` §P1 with its table.

- **One dense frequency point holds 3.52× the 16·N² that was quoted**, and the ratio is FLAT across
  N = 552 / 1,980 / 4,836 because every term is O(N²) with a fixed coefficient. The split is one
  matrix, **two** further full matrices for the LU factors, and the cached cores at just over half a
  matrix. At the ceiling that is **1,338 MB, not 381**.
- **`NumFlat.LuDecompositionComplex` is not a packed in-place LU.** It holds `L` and `U` as two
  separate full `Mat<Complex>` of stride n — confirmed by reflection over its fields AND by
  measurement — beside the matrix `PlanarSystem` keeps for the life of the point. That is the single
  largest term in the accounting and it is the one nobody had counted.
- **The transient m×m `P` is real and is not the peak.** The fill builds it, uses it and drops it
  before anything is factored; at m ≈ N/1.95 it is a quarter of a matrix against the factorisation's
  two.
- **The accelerated point's missing term was the sparse LU**, not the near field: at the accelerated
  ceiling its factors are comparable to everything else the operator holds put together.

## Three findings the brief did not anticipate

1. **`PreconditionerNonZeros` was never the factor's fill-in.** The brief describes it as the
   fill-in "reported but never added". It is `csc.NonZerosCount` — the near MATRIX's own non-zero
   count, which (every near pair being stored exactly once) is the near ENTRY count a second time,
   under a name that reads like the factor's. There was no fill-in number to add; `FactorNonZeros`
   (`SparseLU.NonZerosCount`, L and U together) is new.

2. **`CLAUDE.md` §8's "the accelerator's own working set stays under 200 MB even at that ceiling"
   was FALSE as shipped, and releasing `_nearExact` is what makes it true.** Counted honestly with
   the exact near entries still held — which is what shipped until this brief — the operator at
   N = 11,959 holds ~269 MB. Freeing them brings it to 196.2 MB. The sentence in the refusal and in
   §8 survives, but it survives *because of* this brief rather than in spite of it, and it is now
   tight rather than comfortable.

3. **The brief's own gate could not live in the routine tier as written.** "Within 20% of the
   measured resident delta" needs `GC.GetTotalMemory`, which is PROCESS-wide, and xUnit runs test
   classes concurrently in one process — another class's collection lands inside the measurement
   window. The first version of that test read **0.925 alone and −0.245 in a full-suite run**. It is
   not a flake to be re-run; it is the wrong instrument for a parallel suite. So the resident
   comparison is asserted in the `Category=Benchmark` measurement (which carries a "measure this one
   alone" note, following the precedent `HISTORY.md` already sets for the L8d cost table), and the
   ROUTINE gate counts the same quantity by walking the operator's own object graph and adding up
   every array it holds — load-immune, and independent of the report's arithmetic in the way that
   matters, since it counts the arrays that exist rather than re-deriving them from the report's own
   fields. **It agrees to 0.3%** (reported 9.7 MB against 9.7 MB walked at N = 552).

4. **Reading the exact entries back out of CSparse's CSC — the brief's own suggestion for keeping
   `NearExactAt` alive — is strictly worse than keeping the array.** The CSC stores a 4-byte row
   index per entry that the CSR's shared column index already provides, so holding the CSC to serve
   a diagnostic costs 4 B/entry MORE for the same numbers. `PlanarAimSettings.KeepNearExact`
   (default false) keeps the array instead, and `NearExactAt` throws by name when it was not kept —
   a silent zero there is indistinguishable from "this pair is not near", which is the question the
   caller is asking.

## A measurement trap worth the paragraph, because it produced a wrong table first

**The large object heap does not compact by default**, so a released n×n matrix leaves committed
space the next allocation lands in. A per-phase `GC.GetTotalMemory(true)` DELTA therefore reads the
matrix LOW (it lands in the transient `P`'s grave) and the factorisation HIGH, and a ladder that
measures several N in one process reads the second rung's cores as **negative** — which is what the
first version of this table printed. Cumulative live from ONE baseline, paired with
`GC.GetTotalAllocatedBytes`, are both exact and are what the tables use; the same effect made the
AIM ladder's second rung read 40% low until every operator was kept reachable for the whole test.

The brief's own opening scratch measurement is the same artifact: `.Lu()` "added 530 MB" to a 137 MB
matrix (3.87×) is the LIVE figure with the factorisation's released scratch still committed. What is
RETAINED is 2× the matrix; the extra ~0.6× is real, transient, and belongs in a note rather than in
a refusal.

`Process.PeakWorkingSet64` **reads 0 on macOS** — the platform does not track it — so the tables
report `WorkingSet64` and say so.

## What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarSystem.cs` — new `CoreBytes(n, cellCount)`, `FactorBytes(n)`,
  `ResidentBytes(n, cellCount)` and `ResidentPhrase(n, cellCount)`. `GuardCeiling` gained an optional
  cell count. `PlanarSweepResult.ResidentBytes` added beside `MatrixBytes`.
- `src/Engine/Mom/PlanarFill.cs`, `src/Engine/Mom/SurfaceMesher.cs`, `src/Engine/Mom/PlanarSolve.cs`
  — the three refusals and the WARN note quote `ResidentPhrase`; the cell count is threaded to each
  from the mesh the caller already has.
- `src/Engine/Mom/PlanarAim.cs` — `ApproximateBytes` → `ResidentBytes` (renamed because it no longer
  approximates anything), plus `PeakBuildBytes`; `FactorNonZeros` and `NearExactRetained` added to
  the report; `PlanarAimSettings.KeepNearExact` added; `_nearExact` released after `FactorNear`.
- `tests/Engine.Tests/Mom/PlanarMemoryAccountingTests.cs` — new. Two `Category=Benchmark`
  measurements (the two tables, and the brief's own resident-delta assertion) and four routine
  COUNTER gates: the AIM report against every array the operator actually holds (and not vacuously
  so — without the sparse LU the same comparison misses by more than the band allows), the exact
  near entries released by default with the answer bit-identical either way, `CoreBytes` reproducing
  `PlanarFillCores.CoreBytes`, and all three refusals quoting the same sentence.
- `tests/Ui.Tests/Em/EmCeilingRefusalTests.cs` — the expected sentence updated, still asserted, and
  now also pinned against `PlanarSystem.ResidentPhrase` itself so the gate cannot drift from the
  code.
- `docs/design/mom-engine.md` §10.7 and `src/Engine/Mom/CLAUDE.md` §7/§8 — corrected in place.

## Out of scope by instruction, and left alone

`UnknownCeiling` and `AcceleratedUnknownCeiling` are untouched (P7 and P8 own those, once the memory
is what it will be). `PlanarSystem`'s factorisation is untouched (P7). **The 3.52× is a fact about
the code as it stands, and P2 and P7 are the briefs that move it.**


---

# P2 — four mechanical memory wins on the dense path (2026-08-29)

`docs/sonnet-briefs/brief-em-p2-cheap-memory-wins.md`. Four allocations removed. Three are
**bit-identical**, measured against a worktree of the pre-P2 tree rather than argued; the fourth
(M2) is a different rounding of the same system and moves a published s-parameter by ≤ 1 ulp.

## How the bit-identity claims were actually made, since the method matters

"Bit-identical" compares this build to the build before the change, and no single tree holds both.
So: `git worktree add … HEAD --detach`, drop the same fixture-driven digest test into both trees,
run, compare. The digests are then LITERALS in `PlanarP2MemoryWinsTests` — a test that recomputed
its own expected value from the code under test asserts nothing.

Running the pre-P2 worktree TWICE — once untouched, once with M2's six lines applied and nothing
else — separated the one milestone that moves arithmetic from the three that do not. That is what
lets `P2_6` assert "M1, M3 and M4 together moved not one bit of a published s-parameter" as a
measurement instead of a hope.

## Findings

**1. An O(N²) array held nothing but the outer product of an O(N) vector.** `BuildDirectionCores`
cached `mMom * nMom` per same-direction basis pair — the extracted constant term's vector core,
which factors. Its two neighbours in the same family (`∫∫ w w /R`, `∫∫ w w ln r`) genuinely do not
factor, which is why they are cached and this one need not have been. Removing it is **24% off the
cached cores at every N** and 3.5% off a whole dense frequency point, and it is exactly bit-identical
because the product formed at the point of use has the same two operands.

**2. `PlanarSolveResult.CoreFillCount` could not have caught the duplicate core build, because it
never counted builds.** It is `1 + standards` — derived from the number of standard MESHES. Over the
sweep it read 5 while the code was calling `BuildCores` 7 times, the extra two being
`StaticCapacitance` re-coring meshes whose contexts already held identical cores. A counter derived
from what SHOULD happen cannot notice what does. `PlanarCoreBuildCounter` counts the builds; the old
counter is unchanged, which is what the brief required.

**3. M4's saving is real but much narrower than the brief assumed, and the reason is structural.**
Through `PlanarSolve.Run` the calibration band IS the sweep's own frequency range, so over a full
sweep every separation is selected at some frequency and lazy coring saves nothing: 4 of 4 standards
cored on a 1–20 GHz sweep whether it has 5 points or 21. The saving appears only when the band is
wider than the frequencies actually stepped — a single-frequency check, an interrupted run, or a
direct `PlanarPortCalibrator` caller — where it is large: 2 of 4 standards cored, 50.5 kB of
240.8 kB, i.e. **79% of the standards' cores never built**. The unconditional part is that the
CONSTRUCTOR now cores 0 of 4 rather than 4 of 4.

**4. M3 and M4 pull against each other, by design, and the longest standard is the price.** D7's
`CapacitancePerMetre` differences `Standards[0]` and `Standards[^1]` — the two EXTREME lengths, not
the one this frequency solved. Handing it the contexts' cores (M3) therefore BUILDS the longest
standard's cores on every de-embedded run even when no frequency selected it. That is correct for
the static differencing and it is why the measurement above reads 2 cored rather than 1. P11 changes
that solve.

**5. The de-embedding ceiling's own refusal was undercounting, and M2 made the sentence false as
well.** It said "the two m×m complex matrices this solve holds at once (the potential-coefficient
matrix and its own copy)". M2 removed the copy; and P1 had already established that NumFlat's LU
holds `L` and `U` as two further full matrices, so "two" was low before M2 and wrong after it. It now
says three (P, L, U) and quotes 3·16·m².

**6. A geometry-only core is no longer empty, and the gate had to say what it actually meant.**
`AimAcceleratorTests.T1b` asserted `geom.CoreBytes == 0`. The accelerator's cores now carry the same
O(N) `VMoment` vector (8·N bytes, and `PlanarEntryFill` reads it instead of deriving a second copy of
the identical array). The assertion became `== 8·N`, which states the real claim — nothing QUADRATIC
— exactly rather than by proxy.

## Numbers worth keeping

| N | cells | cores before | cores after | resident before | resident after |
|---|---|---|---|---|---|
| 552 | 297 | 2.43 MB | 1.85 MB | 16.38 MB | 15.80 MB |
| 1,980 | 1,053 | 30.92 MB | 23.45 MB | 210.39 MB | 202.92 MB |
| 4,836 | 2,565 | 184.09 MB | 139.50 MB | 1,254.68 MB | 1,210.09 MB |

P1's flat resident-to-matrix ratio **3.52× → 3.39×**; the ceiling refusals quote **1,290 MB** at
N = 5,000 rather than 1,338. The ceiling CONSTANT is untouched — that is still P7's decision.

## What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarFill.cs` — `VXArea`/`VYArea` removed; `PlanarFillCores.VMoment` (one
  `double[N]`) added, built by a shared `BasisMoments` that both core builders call.
  `AddDirectionBlock`, `HorizontalVectorEntry` and `PlanarEntryFill` form the product at use.
  `PlanarCoreBuildCounter` added; `PlanarFillSettings.CoreBuilds` carries one.
- `src/Engine/Mom/PlanarSystem.cs` — `CoreBytes` is `8·(2·sp + 2·vp + n)`.
- `src/Engine/Mom/PlanarDeembed.cs` — `StaticCapacitance` solves `P q = ε₀·1` and takes optional
  cores; `CapacitancePerMetre` takes two; the ceiling refusal names three m×m.
- `src/Engine/Mom/PlanarSolve.cs` — `PlanarSolveContext.Cores` is a thread-safe `Lazy` (the R17
  guard stays eager), plus `CoresBuilt`/`CoreBuildMs`; `PlanarPortCalibrator` gained
  `CoredMeshCount`/`CoreBuildMs` and hands its contexts' cores to `CapacitancePerMetre`; the run's
  reported `CoreBuildMs` is summed from the contexts rather than measured around a constructor.
- `tests/Engine.Tests/Mom/PlanarP2MemoryWinsTests.cs` — new, 7 routine tests.
- `tests/Engine.Tests/Mom/AimAcceleratorTests.cs` (T1b) and `EmDeembedCeilingTests.cs` (C2) — the two
  assertions whose subject genuinely changed.
- `docs/design/mom-engine.md` §10.7 and `src/Engine/Mom/CLAUDE.md` §7 — the 3.39× / 1,290 MB in place.

## Not done, on purpose

Storing `P` packed — the brief's own fifth candidate. It halves a transient 4N², and it touches every
`p[a, b]` reader in three fills, so it is a milestone with its own bit-identity gate rather than a
few lines. `PlanarAim.cs`, the LU and every quadrature untouched, as instructed.

---

# P3 — the multi-level fill scales like the single-level one (2026-08-29)

`docs/sonnet-briefs/brief-em-p3-multilevel-fill-scalability.md`. **Every number is in
`HISTORY.md` §P3; this is the narrative.**

## What was asked, and what turned out to be true

The brief read `FillMultiLevel` and saw per-entry work the single-level fill hoists — a locked
kernel-set lookup and a fresh `PlanarKernelTerms` per cell pair and per basis pair, three more locked
caches per entry, an array per horizontal entry — and predicted its parallel scaling "will be far
worse". Milestone 1 measured before anything was touched: **the multi-level fill already scaled
5.5–5.9× on ten cores, the same as the single-level fill's 5.4–5.9×.** The locks were uncontended
hits on small dictionaries, ~100 ns between ~200 µs of quadrature; the allocations were ~150 bytes a
pair against the same 200 µs. The hoist was still the right thing to do — it is bit-identical, it
takes ~8× the allocation out of every fill, and it is the shape the single-level fill already has —
but the speed it bought came from somewhere the brief did not look.

## Findings

**1. The via-bearing fill's cost was in an arm that is not O(N²).** `MixedEntry` — one entry per
via basis × horizontal basis — enumerated the horizontal cell's quadrature nodes through an
iterator, once per VIA node. On a near or intermediate pair that is ~1,500 enumerators per entry;
236 MB of them per fill on the N = 514 fixture, and ~30 s of its 58 s vector phase. Inlining the
enumeration (same nodes, weights and nesting order — bit-identical) is **19% off the serial fill
and 16% at cap 10** on that fixture, 16% / 22% on the FR-4 hero with three vias. The block is
O(N·N_z), but a via cell is large, so most horizontal cells count as near or intermediate to it and
take the full graded rule. Its remaining cost is a quadrature-order question and out of scope.

**2. The strided writes were not the fall-off, and the fall-off is the box.** Milestone 4 moved
every fill to the contiguous (lower) triangle with a column-wise mirror. The serial time on the
256 mm line is unchanged to 0.6% (70.9 → 71.3 s); 25 M cache-missing writes at 100 ns is 2.5 s of a
71 s fill, and could never have produced a 40% loss at ten cores. What does: the machine has
**4 performance + 6 efficiency cores**. Efficiency is 89–95% at cap 4 on every fixture, before and
after; the loss appears exactly when the cap admits efficiency cores, and every cap-10 speedup
measured (5.2–6.4×) solves `4 + 6f` to f ≈ 0.2–0.4 — an efficiency core's share of a performance
core. That is why `CLAUDE.md` §6's Amdahl fit gave three different serial fractions: there is no
serial fraction. "Hardware, not scheduling" stands; "memory bandwidth" is struck from it.

**3. Exactly the pairings the loops visit must be resolved, and no others.** `PlanarKernelSet.FitCount`
is asserted by three tests, and a hoist that resolved every (layer, layer) for every kernel would fit
pairings the loops never asked for — a layer carrying only x̂ rooftops and one carrying only ŷ never
pair in the vector block. The horizontal table is therefore built per direction from the layers
that carry it; the ẑẑ table is keyed on the ORDERED span pair the `i ≤ j` loop produces, because
`AveragedTerms(si, sj)` is not asked in canonical order and the digests were taken on what it was
asked; and the mixed table covers every (span, horizontal layer). FitCount is unchanged on every
fixture.

**4. The allocation that remains is the via z-integral's, not the fill's.** With a per-arm counter,
the horizontal and mixed arms now allocate 0 bytes across a whole serial fill. The 27 MB left beyond
the two matrices on the N = 514 fixture is 12.6 MB of per-pairing radial tables (which `Fill` also
builds per call) and 14.4 MB from three ẑẑ entries — `ViaZIntegral.PrismCore` allocates its node
arrays per entry, ~4.8 MB each. O(N_z²), the via rule's own, left alone.

## How the gates were made

The pre-P3 build lives in a git worktree at `HEAD` with P1 and P2's uncommitted diff applied and
`PlanarFill.cs` replaced by its pre-P3 copy; the same test file runs in both trees. Digests are
literals (seven of them — P2's own hero digest is re-asserted, since milestone 4 touches `Fill`).
The routine allocation gate computes its allowance from the settings — one `MaxTableSamples`-capped
table per (kernel, level, level), plus O(N) — and **the pre-P3 worktree fails it by 2.7×**, which is
what makes it a gate rather than a description. A first draft asserted a growth LAW (double the
line, ~4× the pairs, the extra allocation must not follow) and could not discriminate: on the
fixtures a routine test can afford, the pairs grow only 1.8× and the pre-P3 extra was dominated by
the O(N·N_z) mixed-arm iterators, so both trees passed. The allowance form replaced it.

Timing tests are `Category=Benchmark`, and were run ALONE and detached — a first attempt inside the
shell's ten-minute limit was killed with no output. The three multi-level fixtures at four caps plus
warm-ups are ~12 minutes; the 256 mm line ~5.

## What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarFill.cs` — `MultiLevelPairings` resolves everything per pairing before
  `ForRows`; `FillMultiLevel`'s loops read it. `HorizontalVectorEntry` (cached halves, no array),
  `MixedEntry`/`MixedHalf` (inline node enumeration for a whole-rectangle half),
  `CellPairPotential` (cached pulses), `SingularPrismPart` (asymptote passed in).
  `MirrorLowerToUpper` is the one mirror; every fill writes the lower triangle.
- `tests/Engine.Tests/Mom/PlanarP3MultiLevelFillTests.cs` — new: four routine gates, three
  Benchmark measurements.
- `src/Engine/Mom/CLAUDE.md` §6 — the "core heterogeneity or memory bandwidth" clause narrowed in
  place, dated. `docs/design/mom-engine.md` §10.7 — a `P3 is done` note.

## Not done, on purpose

No quadrature rule, table spacing or kernel evaluation was changed; `ForRows` is still the only
parallel loop. The mixed block's quadrature ORDER (the actual cost of a via-bearing fill at small N)
and `PrismCore`'s per-entry arrays are recorded above and left for a brief of their own.

# P4 — the four ramp combinations of a cell pair are one pass (2026-08-29)

`docs/sonnet-briefs/brief-em-p4-vector-block-moment-cache.md`. **Every number is in `HISTORY.md`
§P4; this is the narrative.**

## What was asked, and what turned out to be true

The brief's premise is right and is now built: a cell's two ramps are `w_A = (u − u₀)/A` and
`w_B = (u₁ − u)/A = Δ·p − w_A`, so every (half, half) integral over a cell pair is a linear map of
four primitives per kernel and per flow direction — seven per pair in all, one outer pass. The
core build's quadrature count fell 6.6–7.3× and the per-frequency vector remainder's 5.8–6.4×;
wall clock followed at 3.6–4.0× and 2.7–3.0× on the three series fixtures. Three things in the
brief did not survive contact with the code, and each changed the design.

## Findings

**1. The ŷ block integrates the same cell pair in both orientations, so the primitives are per
ORDERED pair over a band, not per unordered pair "with the same packed index as S0".** The outer
Gauss rule and the inner closed form are not interchangeable to 1e-12 — swapping which cell is
integrated numerically moves a touching pair by its own quadrature error, ~1e-6 — and the row
loops integrate the lower-indexed BASIS's cells as the outer domain. In the x̂ block that always
puts the lower-indexed cell outside. In the ŷ block a rooftop one row down and one column to the
LEFT has the lower basis index, so cell pair (c, c′) is integrated with c outside by one basis pair
and with c′ outside by another. An unordered cache, as the brief specified, would therefore have
changed the orientation of a fraction of the ŷ entries and failed the brief's own 1e-12 gate on
them — not a sign error, a quadrature-tolerance one, and exactly the kind of "smooth, plausible,
wrong" the brief warned about. The fix is structural: `RampTopology.MinInner[a]` is the smallest
inner cell any pair with `a` as outer can ask for (a suffix minimum over basis positions), the
pass runs `c ≥ MinInner[a]` — a band `n_x` wide below the diagonal — and orientation is preserved
exactly. The band costs ≈ 10–20% over the triangle on the series fixtures (0.56–0.60 m² against
0.5), and the 1e-12 gate holds at 8e-13.

**2. The brief's storage arithmetic was off by the basis-pair-to-cell-pair ratio, so the
primitives are transient rather than stored.** "7 × 2 doubles per cell pair against today's
2 + 3 + 3" compares a per-cell-pair count with per-BASIS-pair triangles, and there are ≈ 2
same-direction basis pairs per unordered cell pair. Holding the primitives would be 14 doubles per
cell pair against the ≈ 6 P2's layout holds — 2.3× the cached cores, ≈ +250 MB at the ceiling —
in a series whose first two briefs exist to take memory off. So the primitives are never
persisted: each outer cell's pass assembles them straight into the existing per-basis-pair
triangles. That needed one more idea, because a basis pair draws on two outer cells (its A cell
and its B cell) and a cell-parallel pass would have two threads adding into one entry. The A
cell's contributions go to the entry itself and the B cell's to a transient second triangle of
the same size, and a row pass adds them; every slot has exactly one writer, in a fixed order over
inner cells, so R-fil-11 holds and the build is deterministic. The resident cores are the P2
layout to the byte (`P1_5`, `P2_2` pass unchanged). The fill does the same with four transient
`Complex` triangles for the remainder sums, ≈ 195 MB at N = 4,933 — inside the fill phase, whose
high-water mark P1 showed is below the factorisation's.

**3. The scalar block came along for free, bit-identically.** The pulse×pulse primitive `Q00` is
the scalar block's own `S0`, so the one cell pass serves D4's triangle as well as both directions
of D5 — the brief's "≈ 0.5 m²" was the vector count alone and the total is now ≈ 0.6 m² for
everything. Accumulating `Q00` with the pulse path's own expressions in the pulse path's own
order makes `S0`/`SLog` bit-identical, which is asserted exactly and is what keeps every
capacitance and static-limit gate, and the ẑẑ block that shares `S0`, untouched.

**4. What "bit-identical" could and could not mean here, and what was re-pinned.** Four
combinations of seven primitives are different floating-point operations from four quadratures
summed, so the vector block's last bits move — the brief anticipated this and the gate is 1e-12 on
the assembled matrix, held against the pre-P4 arithmetic kept in the tree as
`BuildCoresByHalves`/`FillByHalves` (a runnable reference rather than a digest printed once
against a tree that no longer exists; the internals are not visible to the test project, which is
why it is a public pair of methods with "reference" in their documentation). Three things ARE
bit-identical and are asserted so: `S0`/`SLog`; every entry touching a cut basis, because those
pairs never left the four-call path (milestone 5's gate, read as the cut PAIRS rather than the
whole conformal matrix, which contains whole pairs too); and `PlanarEntryFill.At` against `Fill`,
because both assemble from the same primitives with the same `Combine` in the same order — A half's
two inner cells ascending, then B, then A + B — which is the order the dense pass's slots
accumulate in. The consequence is that every digest P2 and P3 pinned moved and was re-printed on
this tree, with a note at each literal naming the 1e-12 gate as the bridge. That is not a
loosening of a gate: the digests pinned "nothing moved" through P2's and P3's changes, and the
1e-12 comparison is the equivalent statement for a change that moves association by design.

**5. Why the seconds lag the counts.** A seven-primitive core pass evaluates six inner closed forms
per node against a ramp×ramp call's four, so it costs ~1.5 calls and 6.6× in passes is ~4.4× at
best in seconds (3.6–4.0× measured). A seven-sum remainder pass costs about one call — the `rem`
evaluation dominates — but the fill also carries the scalar block's own remainder over the OTHER
kernel, the radial tables and the row passes, none of which P4 touches, so 5.9× in vector passes
is 2.7–3.0× on the fill. The AIM near fill at N = 3,731 went 2.5× (23.7 → 9.3 s), less than the
dense fill because a near-field row visits each cell pair from fewer rooftops than a full
triangle does.

## How the gates were made

Before any code changed, the P3 tree's `Fill` was dumped to disk for the hero, the 60 mm taper
and the 16 mm conformal taper, and its core-build, fill and AIM near-fill times recorded alone on
the machine. The P4 tree was compared against those dumps entry by entry (max 8e-13 relative; zero
cross-direction entries moved; 1e-16 relative to the largest entry). The same three numbers came
back from the in-tree reference, which is what the permanent test holds. The pass counters are
asserted as ratios against the reference's own count so the mesher's cell count is not a hidden
parameter of the gate.

## What changed, for a reader who only wants the code

See `HISTORY.md` §P4's "What was changed in code". The one-sentence version: `BuildCores` and
`Fill` each run ONE cell-parallel pass over ordered cell pairs that serves the scalar block and
both flow directions, scattering through per-basis-pair slots with one writer each; the four-call
path survives for cut pairs and as the reference; `PlanarEntryFill` memoises the same primitives
per ordered pair.

## Not done, on purpose

The multi-level fill's horizontal remainder still takes four calls per pair per frequency (its
core build got P4's factor; its per-pairing remainder tables are the reason it was not widened
into here — the brief names `AddDirectionBlock` and `PlanarEntryFill`). No quadrature rule,
clustering or closed form changed; the ẑ blocks were not touched. The 256 mm line's LU was scaled
from P1's measurement, not re-timed.

# P5 — translation classes: every cell pair that is a translate of another is one integral (2026-08-29)

`docs/sonnet-briefs/brief-em-p5-translation-class-memo.md`. **Every number is in `HISTORY.md`
§P5; this is the narrative.**

## What was asked, and what turned out to be true

The brief's premise holds exactly: on a tensor-product grid with separation-only kernels, an ordered
cell pair's seven P4 primitives depend on six numbers and a rule, and the seven fixtures reuse each
class 2.5× to 30× — the brief's table reproduced to the pair under its own counting method, all
seven rows. The class table is now the production layout: one `int` per ordered band pair, the
seven primitives once per class, no per-basis-pair triangles at all. The core build fell 3.7–41× in
wall clock and the per-frequency fill 2.8–5.8× beyond P4; AIM's near fill on the 256 mm line 6.8×.
Four things in the brief did not survive contact with the code, and one of them changed the gate.

## Findings

**1. Exact `==` on the gridline differences does not hold, so the spacing classes are quantised at
1e-12 relative — as the brief allowed, and SAID here as it asked.** The mesher writes a bulk
gridline as `a + len·i/n` and the marcher rescales the graded runs, so the hero's x axis carries 15
exactly-distinct spacings for what are 6 classes at 1e-12; the in-class spread reaches 4.0e-13 on
the GaAs line and 3.7e-13 on the 60 mm taper. A class is represented by its smallest member. Nothing
else in the key is a double: each axis's spacing LIST between the two cells is hash-consed element by
element into an integer id, and a list read downward in index shares the id of the equal list read
upward, which is what makes a symmetric line's two graded ends one class.

**2. The brief's "1e-12 relative per entry" is not a property the reference itself has, and the
gate is on the diagonal scale instead.** Every value in P4's matrix is computed at the pair's
ABSOLUTE coordinates, and the corner-summed closed forms lose about 1e-16 · (x/w) of their own value
there — measured before any P5 code existed, on the P4 tree: the self core of a 0.5 mm cell moves
2e-13 relative when the same cell is placed at x = 1 m instead of at the origin, the far core of two
cells 0.25 m apart 5e-13. A class value is computed with the outer cell at the origin, so it is the
MORE stable of the two; the two disagree by ~1e-13 of the near cores. D4's assembly then takes
signed second differences of those cores, and for an aligned far pair of an x̂ and a ŷ rooftop the
four terms cancel to 1e-10 … 1e-14 of the diagonal, so a fixed 1e-13 · |Z_ii| disagreement reads as
1e-8 to 1e-1 RELATIVE on entries that are themselves cancellation residue: on the GaAs line 0.118 on
an entry that is 1.2e-14 of the largest, on the 256 mm line 2e-3 on one that is 6e-13 of it, and
millions of entries per matrix over 1e-12 by that measure. Measured on all seven fixtures against
P4's own arithmetic: max |Δ_ij| / √(|Z_ii||Z_jj|) between 6e-14 and 4.7e-13, max |Δ| / |Z_max| ≤
3e-13, and 7e-8 relative at worst on any entry at or above 1e-6 of the largest. The gate that ships
is |Δ_ij| ≤ 1e-12 · √(|Z_ii||Z_jj|) per entry — the scale on which the factorisation reads the
entry — and the solved currents of `Z x = e_k` on the coarse hero agree to ~1e-12 relative through
the LU. The per-entry relative figures are reported beside the gate rather than tuned away. The
only bit-identity P5 keeps is the one that stands alone: the scalar core of every pair with a cut
cell (its own row, the same conformal call in the same orientation). An entry touching a cut BASIS
is not bit-identical any more, because its scalar block also sums whole-cell pairs, which are
classed — P4's cut-pair bit-identity claim is therefore re-pointed at P4's own retained arithmetic
(`PlanarP4MomentCacheTests` runs on `BuildCoresByPairs`/`FillByPairs` now) rather than weakened.

**3. Orientation is preserved and the 180° rotation is folded, but the RULE had to go into the
key.** P4's lesson — the outer Gauss rule and the inner closed form are not interchangeable to
1e-12 — means a class never swaps outer and inner; what the brief's "(Δx, Δy) ≥ 0 lexicographically"
folds is the rotation about the outer cell, under which a rising ramp becomes a falling one, so a
rotated member reads the representative's (A, B) halves swapped: `Combine(outerB ^ rot,
innerB ^ rot, …)`, one xor, no arithmetic. The trap the brief did not name: two equal cells offset
by (4, 4) cells have τ = 4·√(w²+h²)/√(w²+h²) = 4 in exact arithmetic — exactly `FarRatio` — and
floating point decides per pair which side each lands on, so a class carrying its representative's
rule would move such members by the rule change (~1e-6). The τ band, computed from the member's own
floats exactly as `RuleFor` does, is the low two bits of the key, and the class counts came out
16–17% above the brief's unordered counts for that and for the band's 20% extra ordered pairs.

**4. The representative is synthetic — a pure function of the key — and that is what keeps AIM
bit-identical to the dense fill and R-fil-11 intact.** A first-visited member as representative
would make AIM's row-parallel near fill scheduler-dependent in the last bit, and would make the
dense build and the per-entry fill integrate different members of the same class. The class
primitives are integrated on outer cell [0, w_a] × [0, h_a] and inner cell at the class's signed
offset, every dimension the representative spacing of its class, the rule the class's band. `At`
against `Fill` stays bit-identical (`P4_3`, `AimAcceleratorTests.T1`) because both read the same
class through one `WholeVectorEntry`.

**5. The memory win has a break-even, and it is stated rather than assumed.** The class layout holds
4 bytes per ordered band pair (≈ 0.6 m²) plus 112 bytes per class (seven primitives × two kernels),
where P4 held ≈ 24 m² bytes of triangles; the two meet at a reuse of ≈ 3×. Above it the win is real
— the 256 mm line 83 → 25 MB, the tapers 4.5–5×; below it the layout is slightly LARGER (the hero
1.8 → 2.6 MB, the GaAs line 3.6 → 5.9 MB, both trivial in absolute terms), and a mesh with no
translation reuse at all would hold up to 2.9× P4's bytes. A hybrid that scattered the class table
into P4's triangles when the count came out unfavourable was considered and not built: it keeps a
second production layout alive in every reader for a case none of the seven fixtures approaches.
The a-priori figure the ceiling refusals quote (`PlanarSystem.CoreBytes`) stays P4's formula,
documented as a conservative quote rather than a reconstruction; `PlanarFillCores.CoreBytes` reports
what was allocated, classifier tables included.

**6. The GaAs line's 2.1× is a MESHER finding, recorded for a separate brief and not fixed here.**
Its x grid is asymmetric: the −x end carries the graded fan (2.16, 6.47, 19.4, 58.3, 175 µm into a
208 µm bulk of 8 cells) and the +x end carries NO fan at all — 32 cells of 2.16 µm, i.e. 69 µm of a
2 mm line meshed at the edge-cell pitch. The hero's x axis shows the same asymmetry in a milder
form (86.6, 174.8, 352.7 → 711.6 µm on the left; 538.9, 173.2, 86.6, 86.6 on the right), which is
the "end grading is not exactly mirror-symmetric" `CLAUDE.md` §3.5 already records as the reason a
plain line's two ports do not share a calibration. On a symmetric grid the two ends would be one
class family and both lines would reuse more.

## How the gates were made

Before any code changed, the P4 tree's `Fill` was dumped to disk for all seven fixtures, its core
build, fill and AIM near-fill times recorded alone on the machine, the brief's class counts
reproduced with the brief's own method, and the translation-sensitivity of the closed forms probed
on a hand-built row of cells at four offsets. The class-layout matrices were compared against those
dumps entry by entry, which is where finding 2 came from; the same comparison, against the retained
`BuildCoresByPairs`/`FillByPairs` in the tree, is what the permanent tests hold. Class counts are
literals in the routine test (`P5_1`, hero and 60 mm taper) and in the Benchmark one (all seven).

## What changed, for a reader who only wants the code

See `HISTORY.md` §P5's "What was changed in code". The one-sentence version: `PairClassifier`
(new file) keys every ordered band pair on grid indices; `BuildCores` classifies the band, sorts the
keys, integrates one synthetic representative per class and keeps rows for what no class serves;
`Fill` evaluates the remainder once per class and assembles every entry from the class table
through `WholeVectorEntry`, which `PlanarEntryFill.At` shares; P4's per-pair pass survives as
`BuildCoresByPairs`/`FillByPairs`, the reference.

## Not done, on purpose

The multi-level fill reads its cores through the class table (so its core build got the full
factor) but its per-frequency horizontal remainder is still four calls per pair, as after P4 — the
brief names `Fill` and `PlanarEntryFill`. No quadrature rule, panel or closed form changed. The
mesher's end asymmetry (finding 6) is recorded, not touched — the brief forbids changing the mesher
to increase reuse. The 256 mm line's LU was not re-timed; the per-point crossover is scaled from
P1's measurement and says so.

# P6 — AIM's frequency-independent state, built once per mesh (2026-08-29)

`docs/sonnet-briefs/brief-em-p6-aim-frequency-independent-state.md`. Measured tables in
`HISTORY.md` §P6; this is what was learned.

**1. The brief's premise was stale by two phases, and the measurement was taken before the code was
touched.** It quotes §12's "25 s per frequency at N = 3,731"; on the tree P6 started from that build
was 1.75 s, because P4 and P5 had already taken the near fill from 23.7 s to 1.07 s. What P6 could
remove per frequency was therefore the projection (0.14 s), the near set (in the 0.15 s "tables"
figure) and the singular-core share of the near fill — measured afterwards at 0.3 s of the 1.07 —
not twenty seconds. The structural claim stood (the dense path runs its singular quadrature once per
mesh, the accelerator ran it once per frequency), and the honest result is **1.2–1.4× per point**,
recorded as such rather than as the order of magnitude the premise implied.

**2. The mirror index the brief listed for the geometry was measured and left out.** At 4 B per near
entry it is 18.2 MB at N = 11,959 and took the resident set from 196.2 to 214 MB — past the "under
200 MB at the ceiling" sentence `CLAUDE.md` §8 stands on — to save a rebuild that is one binary
search per lower-triangle entry: 1 / 5 / 17 ms per frequency at the three N. The transpose position
is found inline instead. A memory series should not spend 18 MB to buy 17 ms.

**3. The near cores' store is a flat array, not dictionary nodes, because the memory gate would not
have seen them otherwise.** `P1_3` counts the arrays an operator holds by walking its object graph;
values held in `ConcurrentDictionary` nodes are invisible to it, and its element sizing put every
struct at 0 bytes. The store is chunked arrays of 168-byte `CellPairMoments` addressed through a
key-to-slot index, the walker now sizes a struct element with `Unsafe.SizeOf`, and the report's
208 B per class is checked rather than asserted. The count itself is the P5 compounding the brief
anticipated: ~9–10 thousand classes at N = 552 and at N = 11,959 alike, 2–2.5 MB, against the
~130 MB the brief's per-cell-pair arithmetic would give at the ceiling.

**4. Splitting the near fill's timer showed that the AIM correction, not the remainder quadrature,
is now the largest per-frequency term** — 43–46% of the build at N ≥ 3,731, ahead of the sparse LU.
`AimEntry` does `(M+1)⁴ = 256` complex multiply-adds per near pair per block; only the kernel values
depend on frequency, and two stencils share at most `(2M+1)² = 49` distinct grid offsets, so the sum
could be 256 real products into 49 offset weights and 49 complex multiplies. Recorded for P7/P8;
not built here.

**5. The time crossover is N ≈ 1,100, measured, not the 3,700 of §12 or a scaled estimate.** One
dense point (P5 fill + LU) against one accelerated point at N = 552 / 994 / 1,912: 0.24 / 0.54 / 2.03 s
against 0.37 / 0.71 / 0.96 s. Below the crossover the accelerator is 1.3–1.5× slower and the dense
path stays the shipped default.

## How the gates were made

Before any code changed, a throwaway test on the committed tree dumped the whole accelerated matrix,
every near exact entry, the solved current and the iteration count at three frequencies on the
coarse fixture; the same test on the finished tree produced a byte-identical file. That is what
licenses storing the stencils as `double` and moving the cores across the seam without a tolerance.
The permanent gates are bit-identity between the four-argument `Build` (still geometry-and-operator
from scratch) and an operator over a shared geometry at five frequencies, the same through
`PlanarSolveContext`, the once-per-sweep counters, and the report's accounting reconstructed term by
term. The timings are one Benchmark method, run twice per rung, both readings recorded.

## What changed, for a reader who only wants the code

`PlanarAimGeometry` (new) holds the grid, the stencils, the near set and a `PlanarEntryCores` (new,
the frequency-independent half of `PlanarEntryFill`) warmed over the near set; `PlanarAimOperator`
is built per frequency over it and holds the grid kernels, the near entries' remainders and assembly,
the correction and the sparse LU. `PlanarSolveContext.AimGeometry` is lazy beside `Cores`.
`PlanarAimOperator.Build(cores, …)` still works and builds both, so every existing caller and gate is
unchanged.

## Not done, on purpose

The mirror index (finding 2). The correction's restructuring (finding 4). The projection order,
pitch, near radius, tolerance and preconditioner are untouched, as the brief requires; the
multi-level refusal moved to `PlanarAimGeometry.Build` with its text unchanged. `AimAccuracyTests`
was not re-run: it reads only the four-argument `Build`, shown bit-identical, and the owner asked
that the Benchmark tier not be spent where a cheaper measurement answers.

---

# P7 — the dense factorisation (brief-em-p7-symmetric-inplace-factorisation.md, 2026-08-29)

## What was asked, and what turned out to be true

Replace NumFlat's general LU on the dense planar path with an unpivoted, in-place, blocked, parallel
complex-symmetric LDLᵀ; gate it on a residual rather than on faith; keep the LU reachable as the
oracle. All five milestones landed. **The factorisation is 11.5× faster at N = 4,836 in the shipped
configuration and holds a third of the memory**, and no fixture came within three and a half decades
of the brief's own "stop and report" residual, so no pivoted alternative was needed.

The full tables are in `HISTORY.md` §P7. What follows is what a reader would otherwise have to
rediscover.

## Findings

**1. `dotnet test` builds DEBUG, and that INVERTS this particular comparison.** Every other timing in
this area compares managed code against managed code, so the build configuration has always cancelled
and nobody has had to think about it. It does not cancel here: NumFlat's LU is native and moves 1%
between configurations, while `SymmetricFactorization` is ordinary C# and runs **11× slower**
unoptimised. In Debug the top rung reads 143.8 s at cap 1 against an LU of 40.7 s — "the replacement
is three and a half times slower" — and in Release the same code reads 13.1 s against 41.2 s. **Any
in-suite benchmark that compares a managed implementation against a native one is measuring the build
configuration**, and this is the first one in this area that does. `PlanarP7FactorCostTests` prints
its own configuration above its table and asserts only what holds in both.

**2. A synthetic decaying matrix is not a stand-in for Z when the thing being timed is the LU.** A
complex-symmetric fixture with a `1/(1+|i−j|)` off-diagonal decay timed NumFlat's LU at **97.0 s** at
N = 4,836, reproducibly, against **41.2 s** on a real filled Z of the same size — while
`SymmetricFactorization` read 13.1 s on both. The synthetic fixture is fine for the correctness
tests, which is where it stayed; the cost table is measured on real matrices.

**3. The brief's 1e-12 residual gate cannot be applied to its own worst-conditioned fixture, and the
reason is the fixture.** At 120 MHz — just inside `Dcim.CanFitAtFrequency`'s floor — the residual is
5.5e-12 for the LDLᵀ and **3.8e-12 for the general LU on the same matrix**. That is the MPIE's
low-frequency breakdown (the vector term vanishing like ω against a scalar term growing like 1/ω), a
property of Z that a pivoted factorisation misses by the same margin. Holding the unpivoted form to a
bound the pivoted reference also misses would be measuring the fixture — this area's most expensive
recurring mistake. The gate is therefore in two parts: the three series fixtures keep the brief's
1e-10 / 1e-12 verbatim, and **every** fixture is held to 1e-8 absolute *and* to within 10× of the
general LU's own residual on the same matrix, which is the question actually being asked. Measured
ratio: **1.4–2.0× on all five**.

**4. The live-heap trap P1 documented is about SCOPE, not about the counter.** A first version of the
memory table measured a whole frequency point cumulatively (cores → fill → factor) and read a
**negative 188 MB** at the largest rung, even with a blocking compacting collect at each end. Scoped
to one factorisation on a fresh copy, with the baseline taken immediately before that copy, the same
counter is exact to the last megabyte: 357.0 MB against 356.9 MB of matrix, and 1,070.6 against
3 × 356.9 for the LU. **`GC.GetTotalAllocatedBytes` is not the safe alternative it looks like** — it
is process-wide and counts every thread, so the LDLᵀ column picks up the trailing update's own
`Parallel.For` task objects: 0.2–0.9 MB, independent of N, i.e. noise.

**5. Only the LOWER triangle is ever read, and the fill computes exactly that.** `PlanarFill.Fill`
and `FillMultiLevel` both write the lower triangle and `MirrorLowerToUpper` copies it, so the
factorisation reads precisely the entries that were computed — the multi-level path included, whose
symmetry `CLAUDE.md` describes as "structural" rather than bit-for-bit. There is no
symmetrisation and no perturbation anywhere in this change. It also means that O(N²) mirror write is
now arguably dead on the dense path; establishing that is its own piece of work (`PlanarEntryFill`,
the AIM path and several tests still read `Z[i,j]` for arbitrary (i,j)).

**6. Packing the triangle would RAISE the peak, not lower it.** 8·N² is the steady state a packed
factor would hold, but the matrix arrives from the fill as a full N×N array and packing it needs a
transient 24·N² — higher than the 16·N² the in-place full-lower form never leaves. A refusal is about
the peak, so full-lower in place is the right choice unless the FILL is changed to build packed,
which the brief forbids.

**7. `PlanarSystem.Matrix` had to become an exception rather than a comment.** The factorisation
overwrites the lower triangle and leaves the UPPER one untouched, so a stale read of a symmetric
matrix returns entirely plausible numbers from the previous frequency. Six call sites in the tests
held their own reference to the matrix they had wrapped; all six now pass `matrix.Copy()`, in the
test, which is where a reader can see what the copy is for.

## How the gates were made

The oracle is the code being replaced: `PlanarFillSettings.UseSymmetricFactorization = false` selects
NumFlat's LU, and every accuracy gate solves the same Z both ways. Bit-identity is not available and
was not asked for — two different factorisations of one matrix do different arithmetic — so the gates
are the solved current vector to 1e-10 relative, the residual to 1e-12 on the series fixtures, and
the published de-embedded s-parameters to **1e-9 absolute** over a 5-point sweep on three fixtures
(worst measured 7.4e-13). Everything routine runs on the COARSE mesh, deliberately: a factorisation
cannot tell a coarse mesh's Z from a shipping mesh's, so a finer mesh is a bigger matrix rather than
a harder question. `Category=Benchmark` carries exactly one method, the cost measurement, which is
the one place a large N genuinely is the subject.

**The whole-sweep digest gate was strengthened rather than re-pinned.** `P2_6` hashes the published S
of a de-embedded sweep against a literal, and P4 and P5 each moved that literal when they moved the
last bits. P7 moves it too — but a bare re-pin accepts any change, including a real one, so the test
now runs the same sweep through BOTH factorisations and asserts that **NumFlat's path still
reproduces P5's literal bit for bit**. That is the load-bearing half: it says the factorisation moved
and nothing else did — not the fill, the cores, the calibration, the peel or the renormalisation.

Two structural properties are asserted rather than measured: caps 1 / 2 / 4 / unbounded and a shared
`PlanarParallelBudget` produce **bit-identical** factorisations (R-fil-11's rule — the trailing
update's parallelism is over destinations each written by one iteration), and the P-right-hand-side
substitution is bit-identical to P separate solves.

## What changed, for a reader who only wants the code

`SymmetricFactorization.cs` is new and self-contained. `PlanarSystem` holds its matrix privately,
throws from `Matrix` once factored, and gained `Factor()` (which replaces `_ = system.Lu` as the way
to force and time the factorisation) and `Factorization` / `LastResidual`. `IPlanarOperator` gained a
default `Solve(IReadOnlyList<Vec<Complex>>)` that `PlanarSystem` overrides, so `Y = BᵀZ⁻¹B` streams
the factor once for all P ports. `PlanarFillSettings` gained `UseSymmetricFactorization` (true) and
`TrackFactorizationResidual` (false). `PlanarSystem.FactorBytes` now means the shipped path;
`LuFactorBytes` is its old body under its own name; `SymmetricFactorBytes` is `16·N`.

## Not done, on purpose

No native BLAS (the root `CLAUDE.md` requires asking, and the brief forbids it). No pivoting —
Bunch–Kaufman stays the NAMED follow-up and `FactorPanel` refuses by name on an exactly zero pivot
rather than falling back quietly. No vectorisation of the trailing update: it is scalar managed C#
and NumFlat's native LU still beats it per flop, so a `Vector128`/`Vector256` complex multiply-add
and destination-column register blocking are an obvious further 2–4× — recorded, not built, because
the brief's target was met by 11.5×. `PlanarDeembed.StaticCapacitance` and `ChargeSolver` both factor
a symmetric matrix over CELLS and would take the same treatment; they are outside this brief's scope
and are now the only general LUs left on a planar run. **`UnknownCeiling` was re-asked and NOT
moved** — 1 GB now buys N = 6,968 against 4,454, so memory no longer binds at 5,000, but the fill's
time and a mesh's accuracy at that size have not been measured and the constant is the owner's.

# P8 — AIM's near radius had no physical floor (2026-08-29)

**Symptom.** `brief-em-aim-ceiling.md`'s A1b ladder — refine the mesh at a fixed 64 mm footprint —
climbed GMRES 21 → 143 → 372 iterations over cells/λ 80 → 120 and did not converge at all at 140
(N = 13,967, residual 9.1e-5 against a 1e-8 tolerance). The A1a ladder, growing the board's LENGTH at
the shipping resolution, was flat over the same N range. `AcceleratedUnknownCeiling = 12,000` was set
with margin under A1b's failure.

**Cause, in one sentence.** `PlanarAimSettings.NearRadiusFactor` measures the exact near field in
**largest basis supports**, and refining a mesh shrinks the largest support — so the near field
shrinks in METRES (8.93 h at cells/λ = 20, 1.28 h at 140, and the same at every board length to four
figures) while the coupling it has to span does not, because the grounded slab's image sits at a fixed
depth 2h. The scalar kernel is `1/ρ − 1/√(ρ² + 4h²)` plus smooth terms: past ρ ≈ 2h the residue falls
like `2h²/ρ³` rather than `1/ρ`, so **2h is where the coupling stops being long-ranged** and a near
field narrower than it is missing the dominant part.

**Fix.** `PlanarAimGeometry.NearRadiusM = max(NearRadiusFactor · maxSpan, NearRadiusMinM)`, where
`NearRadiusMinM` is null by default and derives `2·h`. The A1b ladder becomes 21 → 28 → 36 and the
failing rung converges; the same ladder at 16 mm is 2, 4, 6, 12, 14, 10, 10 against 2, 4, 6, 12, 46,
144, 273. `4h` was never needed — the brief's own stopping rule is "flat at 2h".

## Four things worth keeping

**1. It is not only the preconditioner — the brief's premise was half right.** The brief said "nothing
about the AIM projection's accuracy is at stake; the preconditioner is what degraded". But the near
radius is also the boundary between entries computed EXACTLY and entries the projection approximates,
so shrinking it degrades the OPERATOR as well. `|ΔI|` against the dense solve on the same mesh:
4.90e-7 at cells/λ = 20, **5.93e-4** at 140 — a 1,200× loss, of which the floor recovers 17×
(3.58e-5). Both measured at the same converged GMRES residual, so it is the operator and not the
stopping rule. A ladder that only watched iteration counts would have missed this.

**2. The obvious alternative explanation was checked and ruled out, cheaply.** A preconditioner that
silently failed to factor would leave GMRES unpreconditioned and produce exactly this iteration
climb. `PlanarAimReport.FactorNonZeros` (P1's field) is non-zero on every rung including the one that
fails, so the factorisation succeeds throughout — it is a good factorisation of a near field that is
too narrow. Printing that one existing column cost nothing and settled it before any code changed.

**3. The floor's real cost is the sparse LU's FILL-IN, not the near set, and it is
aspect-ratio-dependent.** Widening the radius grows the near matrix ~1.5× at the top rung; what it
does to the factor depends on how much of the board the radius spans. On a 64 mm line the fill-in
ratio is 1.13× and the floor is a NET WIN (build + solve 11.0 s → 7.1 s, because the solve collapses).
On a 16 mm line the 3.2 mm radius covers the whole 2.9 mm width and a fifth of the length, the band is
wide against a small N, fill-in is 2.93× and the same change is a net LOSS (12.1 s → 19.0 s). Both are
correct answers; the short board is the artefact. Memory grows either way — 303 → 426 MB at
N = 10,708.

**4. The slab height is a REQUIRED argument, on purpose.** `PlanarAimGeometry.Build` refuses a
non-positive `slabHeightM` by name rather than defaulting to "no floor", because the failure of a
forgotten plumb is silent: the geometry takes the pre-P8 radius and returns a complete, plausible
answer that merely takes 20× longer to reach, or does not converge. Making it required turned all six
call sites into compile errors. `PlanarSolveContext` takes it as a trailing optional — the DENSE path
has no near radius to floor, and 35 dense call sites should not have to carry it — and throws when
`Fill.Aim` is set without one.

**5. It changes no answer anyone currently gets.** The floor binds on no mesh either starter
technology produces at a shipped resolution — R/h is 2.68 to 8.92 on the FR-4 hero at cells/λ 20 and
40, and **4.17 to 50.03 on the GaAs starter**, where a 72 µm conductor on a 100 µm slab has cells that
are large against h by construction. Only the PCB case, whose conductor is nearly twice its slab
height, can be walked into the floor, and only by asking for several times the default resolution.
`HISTORY.md` §P8 §M3 carries the table.

## What the ceiling question became

`AcceleratedUnknownCeiling = 12,000` is **untouched** (the brief forbids moving it, and the decision is
the owner's) — but its basis has changed, and the recommendation is to leave it there for a NEW reason.
A1a, the healthy construction it was set from, is unaffected by the floor and stands exactly as
measured. A1b, the failing construction it took its margin from, no longer fails to converge. What
replaced the convergence risk is a BUILD one: at N = 10,708 the accelerator holds 426 MB on a refined
mesh against 188 MB at N = 12,894 on a coarse one, and one rung further — N = 13,967, the near matrix
8.9% dense — **CSparse's exact sparse LU had not returned after 8 minutes of CPU and 1.43 GB, and was
stopped there**, where the unfloored one factors in 5 s. So the old failure was a GMRES that did not converge and threw; the new one would
be a preconditioner that never finishes building. **N alone no longer bounds the accelerated path** —
near entries per row does. `HISTORY.md` §P8 §M5 carries the sentence that would move the constant,
written out and not applied, together with the density guard it would need beside it.

That is also why `AimCeilingTests.A1_LadderByResolution` is now pinned to the pre-P8 radius
(`NearRadiusMinM: 0`): it exists to record the "before", and on the shipped default its top rung would
never finish. `PlanarP8NearRadiusFloorTests` is where the shipped behaviour is measured.

## Not done

`NearRadiusMinM` is exposed on `PlanarAimSettings` and reachable from no UI — nothing persists it and
`EmRunService` only ever passes `PlanarAimSettings.Default`. That is deliberate: it is a derived
physical quantity, not a knob a user should be tuning, and its one non-default use in the tree is the
`0` that turns the floor off so a test can measure the pre-P8 behaviour.

---

# P9 — adaptive frequency sampling on by default: measured, and the recommendation (2026-08-29)

**A decision brief. Nothing was flipped, and one thing turned out already to have been.**

## What was asked, and what turned out to be true

The brief asks whether `PlanarAdaptiveSettings` should be on by default, on the premise that the
feature ships off. **That premise was two days stale in the half that matters.** The ENGINE default
is still `PlanarSolveSettings.Adaptive = null`, but the shipping USER default was flipped on
2026-08-26: `EmSetupModel.AdaptiveSampling = true`, translated by `EmRunService` into
`PlanarAdaptiveSettings.Default`. Every EM run a user starts today already samples adaptively. So
the live question is not "should it be turned on" but **"was turning it on right, and are 1e-3 and
5 the right numbers"**. The measurements answer both.

## The recommendation

**Leave it on. Keep `Tolerance = 1e-3` and `InitialPoints = 5`. Leave the ENGINE default null.**
Four reasons, each measured:

1. **It never costs accuracy that matters.** On the panel's own default grid the realised worst
   |ΔS| against a fully-solved sweep is at worst **1.42e-3** across five fixtures. The one deep
   feature in the set — a −7.2 dB notch — was recovered **exactly, in all 33 configurations run**,
   at every tolerance and every seed count.
2. **It costs about 1% when it saves nothing.** Two of five fixtures solve every point anyway and
   read 0.99× — the overhead is the probes' interpolant evaluations, and the calibration replays are
   cache hits by construction.
3. **`InitialPoints = 5` is right and raising it is a pure loss.** From 5 to 17 seeds the realised
   error is unchanged to two figures; from 33 up the solved count climbs and the error does not.
4. **`Tolerance = 1e-3` is the right point on the curve.** 1e-2 saves more but its realised error
   reaches 5.6e-3, which is visible on a −40 dB return-loss plot; 1e-4 gives back most of the saving
   (1.72× where 1e-3 gets 3.67×) for an error nobody is reading.

**The engine default stays `null`, deliberately.** It is not the user-facing default and never was;
it is what makes every measured number in L8c/L8d/L9d and P1–P8 reproducible at full precision, and
R-adf-1's bit-identity gate is written against it. Flipping it would change what every engine test
measures and buy a user nothing.

## What the owner should know before agreeing, because it is not what the design note promises

**§10.7's "typically cuts solve count by 5–10×" is not what the shipped default sweep gets. On
1–20 GHz at 101 points the saving is between 1.0× and 1.73×**, and on two of five fixtures it is
exactly nothing. The explanatory number is one column wide: **adjacent points of that grid already
differ by 0.13 to 0.63 in |ΔS| — 130× to 630× the tolerance.** Adaptive sampling can skip a point
only when the interpolant predicts it inside the tolerance, and a grid whose neighbours are that far
apart is not oversampled with respect to a 1e-3 criterion at all.

**The lever that actually delivers the 5–10× is a FINER grid, which inverts the usual advice.** On
the FR-4 hero, a **401-point** adaptive sweep costs **3.01 s** and solves 81 points; a **101-point**
non-adaptive sweep of the same band costs **4.25 s** and solves 101. Four times the frequency
resolution, in less time. With adaptive sampling on, the solved count is set by the STRUCTURE and
not by the grid, so **asking for more points is close to free and asking for fewer buys almost
nothing**. The user documentation currently advises the opposite ("the useful move when in doubt …
is to reduce the number of requested points"); that sentence is now measurably backwards.

## Four things worth keeping

**1. The narrow-resonance failure mode the brief names could not be constructed, and the reason is
structural — it is the same fact as the small saving.** For a distributed structure the transmission
phase rotates at `2π√ε_eff·L/c` per Hz. For a resonance of quality factor Q to be under-resolved the
grid step must exceed about `f₀/2Q`, and at that step the background's own adjacent-point difference
is `≈ π·(L/λ_g)/Q` — below a tolerance τ only when `L/λ_g < τ·Q/π`, i.e. shorter than λ/45 at
τ = 1e-3 and the measured Q ≈ 70. A structure that short cannot host a distributed resonance.
**Whenever a resonance is sharp enough to fall between two solved points, the background is already
forcing refinement to the grid floor, and the resonance is bracketed on the way down.** Read the
other way, that is exactly why so few points can be skipped. Both halves were measured across two
grids, three tolerances and six seed counts; the argument is supported by the measurements, not
proved by them.

**2. The real accuracy caveat is a magnitude one, not a missed-feature one: `Tolerance` is a LOCAL
stopping test, not a global error bound.** On a 1001-point grid at tol 1e-3 the realised worst |ΔS|
is **1.01e-2 — ten times what was asked for**. L9e's `T1_2` already gates this at `worst < 10 × tol`;
these numbers sit at that gate rather than inside it. Anyone quoting the tolerance to a user as an
error bar would be overstating it by up to an order of magnitude.

**3. Adaptive sampling cannot rescue a notch that falls between two REQUESTED frequencies, and the
user doc's own framing of "the problem it solves" says that it can.** R-adf-2 publishes exactly the
grid the user asked for and never inserts a point. The measured fixture makes this concrete: its
notch is 80 MHz wide (Q ≈ 70 — and sweeping tanδ over 1e-5…2e-2 and the coupling gap over
0.15…2.4 mm moves the depth but not the width, so 80 MHz is what this structure class produces), and
the panel's default grid steps 190 MHz. On that grid the notch is lost with the feature on or off;
what the published curve reports as its deepest point is a different feature 5.6 GHz away.

**4. The memory the adaptive branch retains is real but never the binding term.** It keeps every
solved point's kernel, raw matrix and per-port basis currents alive at once, because the calibration
is replayed from a fresh branch state after each insertion — `16·N·P` bytes per solved point, ~4 MB
at N = 1,278 and ~16 MB at N = 5,000. Measured peak managed heap on the taper was **217 MB adaptive
against 227 MB off**: the transient fill and factorisation dominate by two orders of magnitude, and
the difference is GC timing. It is worth knowing about only if the retention ever meets a mesh where
the matrix itself is not the peak.

## How the gates were made

**No new engine test.** The brief gates a flip, and nothing was flipped; `AdaptiveSweepTests` is
unchanged. Every number above came from a standalone Release harness run one fixture at a time —
`dotnet test` builds Debug, which would have inverted the timings, and the benchmark tier's own
warning is that these measurements read ~2× slow alongside each other.

**One UI gate was missing and is now there.** Milestone 3 requires that the panel show a point was
modelled rather than solved. `WorkspaceViewModel.EmRunSummary` already does — but its only test
covered the `adaptive: false` path, so the branch that reports the solved count was ungated.
`EmRunProgressTests.WithAdaptiveSamplingOn_TheFinishedRow_SaysHowMANYPointsWereSOLVED` now asserts
the requested count, the solved count and the word "modelled" all reach the row.

## What changed, for a reader who only wants the code

```
tests/Ui.Tests/EmRunProgressTests.cs   + the adaptive branch of the run-summary row
docs/design/mom-engine.md              §10.7's "rational" and "5-10x" corrected in place
docs/user/src/reference/mom-engine.md  the interpolant, what a notch between samples does, the advice
src/Engine/Mom/HISTORY.md              §P9 — every table
```

No engine source was touched.

## Not done, on purpose

**The default was not flipped in either direction** — the brief forbids it before the owner answers,
and the user-facing default is already where the recommendation says it should be.

**`PlanarAdaptiveSettings` is still reachable only as a whole.** `EmSetup.AdaptiveSampling` is a
bool; `Tolerance`, `Interpolant` and `InitialPoints` are not persisted and no panel exposes them.
Given that `InitialPoints` measurably buys nothing and `Interpolant` was decided by measurement at
L9e, exposing them would be offering knobs whose right settings are already known. If anything here
deserves a control it is the tolerance, and only alongside a plainer statement of what it is not.

# P10 — M2's fan-out is not starving the thread pool (2026-08-29)

`brief-em-p10-fanout-starvation.md` was written as a hypothesis to test, not a finding, and the test
refutes it at milestone 1. **Nothing in the solver's parallelism was changed.** Milestones 2 (one
shared row queue drained by `cap` long-lived workers) and 3 (re-measure the overlap) are conditional
on the instrument showing a stall — it does not — so neither was built. Every table lives in
`HISTORY.md` §P10.

## What was asked

`PlanarFill.ForRows` takes a permit from `PlanarParallelBudget` in `Parallel.For`'s `localInit`. When
`PlanarFanOut` runs K solves at one frequency, each inner loop asks for up to `cap` workers, so K·cap
pool threads are requested and (K−1)·cap of them should sit blocked in `Enter()`. The .NET pool
injects threads at ~1–2 per second once its minimum is exhausted, so the run should stall for seconds
at every frequency while the pool grows — and if it does, part of the 1.09–1.15× ceiling §6 attributes
to hardware is really scheduling.

## Findings

1. **The consequence does not happen, and the wall clock is the proof.** On the brief's own fixture
   (FR-4 80 mm line, N = 1,980, de-embedded — 5 solves, `cap` = `ProcessorCount` = 10) the fill
   reaches all 10 permits **within ~30 ms of starting** and holds them for the whole fill phase. Peak
   threads parked in `Enter()`: **3**, against the 40 the hypothesis predicts. Pool thread count
   11 → 12 over the point.

2. **The mechanism IS real — it is simply gated by the pool's size, and it costs nothing.** Force the
   pool large first (`SetMinThreads(64)`) and the predicted picture appears in full: 34 threads parked
   at once, 20 thread-seconds parked, 151% of the budget's own core-seconds. **The point still takes
   1.25–1.47 s, exactly as it does with the default pool (1.23–1.37 s), and utilisation goes UP
   (84.6% against 78.7%).** A parked thread burns no CPU and holds no permit; what it costs is a pool
   thread, not time. **This is the measurement that settles the phase** — if injection were the stall,
   pre-supplying the threads would have removed it.

3. **`Parallel.For`'s unmet demand queues; it does not block.** This is why the brief's
   multiplication never materialises on a healthy pool, and it is not obvious from reading `ForRows`.
   `Parallel.For` is a replicating task: a worker queues one replica when it starts and work remains,
   and a replica becomes a thread only when the pool has one to give. The trace shows this directly —
   `PendingWorkItemCount` sits at 3–5 through the entire fill phase while parked threads stay at 0–2.
   K concurrent loops produce K·cap **queued work items**, not K·cap parked threads.

4. **The cap can never outrun the pool's minimum, which is the structural reason it works.**
   `PlanarSolve` materialises a null cap as `Environment.ProcessorCount`, and the pool's minimum
   worker count is also `ProcessorCount`. The fill therefore needs no injected thread to reach its
   cap. **A cap BELOW `ProcessorCount` parks the most threads** (at cap 4: pool 12, eight surplus
   threads, 8 parked, 111% of the budget's core-seconds) and still costs no time.

5. **The pool does grow across a sweep, and it is still not the fan-out's doing.** Over five points
   the pool went 11 → 12 → **24** in one jump, with parked threads spiking to 13. The same sweep
   started with a 64-thread minimum ran **3.05 s** against **3.01 s**. Utilisation over the sweep is
   75%, and the shortfall is the sequential Green's-function fit (0.11 s per point, which the trace
   resolves exactly) and `SymmetricFactorization`'s serial panel steps between its parallel trailing
   updates — both visible in the trace as permits-held-zero with an empty queue.

6. **§6's M3 paragraph did not become false and was not touched.** Its fall-off is attributed to core
   heterogeneity on a 4 + 6 box, and P10 removes the one competing explanation that was still open.

## How the gates were made

**No new timing test.** These are machine measurements; a threshold on them would measure the box.
What is gated instead is the instrument itself, in `ParallelBudgetTests`, both routine and together
under half a second:

- `P10_TheBudgetsCounters_SeeTheFillTheyExistToMeasure` — the counters are wired to the **budgeted**
  branch of `ForRows`, so a future re-measurement is not measuring nothing. It asserts only that they
  MOVE: they are process-wide, another test's fill can touch them at any instant, and an equality
  against zero would be a race against the runner rather than a statement about this code.
- `P10_APermitAlwaysComesBack_SoOneBudgetSurvivesASecondFill` — `PlanarParallel.cs`'s header claims a
  permit always comes back, which is what makes the scheme deadlock-free, and nothing tested it. A
  leaked permit is invisible in one fill and fatal in the next, so the gate is a second fill through
  the same **cap-1** budget, on a background thread with a 60 s join, so a regression fails rather
  than hangs.

`REmp8_TheBudgetPath_IsBitIdenticalToTheOrdinaryCappedPath` and R-emp-13's two sweep tests are
unchanged and still pass — the brief requires bit-identity to survive, and since `ForRows` was not
touched it survives by construction rather than by measurement.

Everything else came from a standalone Release harness, one fixture at a time. `dotnet test` builds
Debug, which changes the cost of the arithmetic relative to the scheduling under study.

## What changed, for a reader who only wants the code

```
src/Engine/Mom/PlanarParallel.cs               PlanarParallelBudget: + WaitingThreads, HeldPermits,
                                               EnterCount, TotalWaitSeconds, ResetCounters
tests/Engine.Tests/Mom/ParallelBudgetTests.cs  + the two P10 gates above
src/Engine/Mom/HISTORY.md                      §P10 — the five tables
```

`ForRows`, `PlanarFanOut` and the budget's semantics are untouched. No answer moved and no
provenance hash changed.

## Not done, on purpose

**The shared row queue was not built.** It is milestone 2, conditional on a stall, and there is none
to remove: `cap` long-lived workers draining one queue would arrive at the same `cap` permits' worth
of arithmetic the present scheme already keeps busy from ~30 ms in. It would also have to re-earn
R-emp-13's bit-identity, which the current scheme gets for free because a row is still written once
by one worker.

**No second cap, and no attempt to size the pool.** Both were forbidden by the brief, and the
measurements say neither would buy anything: the run is invariant to the pool's size across a 5×
range of it.

**The parked threads were not eliminated for their own sake.** They are surplus pool threads holding
no permit, and they cost about a thread stack each. If that is ever worth reclaiming it is a memory
argument on a host that already runs a large pool, not a performance one, and this phase measured no
evidence for it.

---

# P11 — the calibration standards' static capacitance, accelerated (2026-08-29)

`brief-em-p11-accelerated-static-capacitance.md`. **The last step of a de-embedded run that was
always dense is not any more.**

D7 references every de-embedded s-parameter to the line's own `Z_c = γ/(jωC_pul)`, and `C_pul` comes
from differencing two calibration standards' static capacitances. Each of those solved `P q = ε₀·1`
over CELLS with a dense m×m complex LU. Turning the accelerator on moved the DUT's ceiling and left
that one where it was, so a wide-port run whose DUT solved comfortably could still be refused —
measured on the 2026-08-14 owner report, whose wide port's calibration standard meshed at N = 6,466
against a 5,000-unknown dense ceiling.

**`P` is exactly the scalar block M5 already projects.** The accelerated charge stencil is a ± pair of
cell pulses per basis; a cell-pulse operator is the same stencil with one cell and `sign = +1`. So
this is not a second accelerator — `PlanarStaticAim` is the same projection, the same near-set rule,
the same grid FFT and the same sparse-LU-preconditioned GMRES, over CELLS, with one grid kernel and
no ω.

## What was found

1. **The near RADIUS is the only knob that acts, and it must be read in M5's unit, not the natural
   one.** Sizing it from the CELL span — the obvious choice for a cell-pulse operator — halves M5's
   radius on the same mesh, because a basis spans two cells. Measured against the dense solve as the
   relative error in `C_pul` (FR-4 hero's 30.8/90.9 mm standards / GaAs hero's 1.14/3.02 mm), with
   `NearRadiusFactor` in BASIS supports: `3 → 2.69e-7 / 8.88e-7`, `4 → 3.69e-7 / 1.61e-7`,
   `5 → 1.98e-7 / 2.89e-8`, **`6 → 1.04e-7 / 2.02e-9`**, `8 → 3.39e-8 / 7.07e-15`, at 99 / 132 / 164 /
   196 / 256 near entries per row. The cell-span reading would have left GaAs at 8.88e-7 — inside the
   brief's 1e-6 gate by 12%, which is not a margin. So the pitch is sized from the largest CELL span
   (the source support, which is what a stencil has to enclose) and the radius from the largest BASIS
   support (the kernel's own range, which is what P8's 2h floor is about); the file header derives
   both.

2. **The projection ORDER is inert here.** M = 2 / 3 / 4 / 5 give `1.05e-7 / 1.04e-7 / 1.08e-7 /
   1.08e-7` on FR-4 and `2.15e-9 / 2.02e-9 / 2.00e-9 / 2.00e-9` on GaAs. Raising it is not a remedy
   for this operator; widening the near field is, and the non-convergence refusal names both in that
   order.

3. **The GMRES tolerance is not what limits this — the projection is.** The brief asked for the
   tolerance that delivers 1e-6 on the DIFFERENCED result to be measured and set from the
   measurement. It is: `1e-6 → 9.88e-8`, then `1e-8`, `1e-10`, `1e-12` and `1e-14` give `1.04e-7`
   **identically in every printed digit**. `StaticTolerance` ships at **1e-10**, two decades under
   where the answer stopped moving so a standard pair whose lengths are close (a larger
   `C/(C₂ − C₁)` than the 1.5-1.9 these fixtures measure) still has the solve's own contribution well
   under the projection's — and deliberately not tighter, because a tolerance GMRES cannot reach
   turns into a refusal. Cost at the shipped value: 3-4 iterations.

4. **A finer auxiliary grid buys nothing here, which is the opposite of M5's own N-ladder — and the
   two knobs are not independent.** At the shipped radius the pitch ladder is flat below 0.5 cell
   spans (`0.125 → 1.19e-7`, `0.25 → 1.11e-7`, `0.5 → 1.04e-7`) and degrades above it
   (`0.75 → 1.60e-7`, `1.0 → 3.32e-7`). At a radius of 3 supports the SAME ladder has the opposite
   sign (`0.5 → 2.69e-7`, `0.25 → 1.31e-6`, `0.125 → 2.45e-6`). What that says is that refining the
   grid cannot compensate for a near field too narrow to hold the coupling — P8's finding in another
   coordinate.

5. **The setup refusal this brief was told to re-word was UNREACHABLE, and no test had ever seen its
   message.** `brief-em-deembed-ceiling-closeout.md`'s C1 put a de-embedding-specific ceiling check in
   `PlanarSolve.Run` AFTER the calibrators were constructed — but `PlanarPortCalibrator`'s constructor
   builds one `PlanarSolveContext` per standard, and that constructor's own eager
   `SurfaceMesher.GuardCeiling` throws first, with a correct sentence about a mesh that names neither
   de-embedding, nor which port, nor a remedy. (`EmDeembedCeilingTests` gates C2's `StaticCapacitance`
   guard message, which is a different one; the brief's "EmDeembedCeilingTests asserts the sentence"
   was not the case.) The check now runs BEFORE the calibrator is built, on a standard set the
   calibrator is then handed rather than rebuilding, and two tests assert it —
   `EmDeembedCeilingTests.P11_ADenseDeembeddedRun_IsRefusedAtSetup_AndTheRefusalNamesTheAccelerator`
   and its accelerated counterpart.

6. **The dense refusal's first remedy is now the accelerator, not turning de-embedding off.** It used
   to have to say the accelerated solve "will NOT help here"; since the standards' static solve is
   accelerated too, it does help, and the sentence says so and quotes the accelerated ceiling.

## The taper, re-run (the brief's M4)

Reconstructed at the reported geometry (13.1 mm into 299 µm over 28.575 mm on 20 mil RO4350B,
cells/λ 5, edge 3, mesh frequency 500 MHz, 1-5 GHz), straight-flanked rather than Klopfenstein, which
lands at N = 4,239 rather than the reported 7,749 — the exact count was never the point, the width
RATIO is, and the failure mode reproduces exactly: **the WIDE port's calibration standards mesh at
N = 1,498 / 7,603 / 4,273**, one of them past the dense ceiling on its own.

| | before P11 | after |
|---|---|---|
| dense + de-embedded | refused at setup | refused at setup, and the sentence now names the accelerator |
| **accelerated + de-embedded** | **refused at setup** | **runs**: 3 points (1 / 3 / 5 GHz) in **107 s** |

The wide standard's static solve on its own (m = 3,864 cells, n = 7,603): **9.7 s and 222 MB**, at
607 near entries per row and 15.7% fill, against **683 MB** for the three m×m complex matrices the
dense route holds — which the ceiling refuses outright rather than allocating. 9.24 s of the 9.7 s is
the sparse LU of the near matrix; the GMRES solve itself is 72 ms at 4 iterations.

What the user now gets, at the published planes: `|S₁₁|` −1.24 / −2.13 / −1.23 dB and `|S₂₁|`
−6.10 / −4.22 / −6.28 dB at 1 / 3 / 5 GHz, `|S₁₁|² + |S₂₁|²` = 0.998 / 0.991 / 0.990 (passive), with
port 1's `Z_c` measured at 6.59 → 7.00 Ω against the 6.92 Ω the taper was drawn for and port 2's at
82 → 84 Ω against 100 Ω. The port-2 figure is not a P11 result: that port is 2 basis functions wide
and the run raises its own feed-clearance note about it.

## What the gates are

`PlanarP11StaticAimTests`, 12 tests, **0.93 s together** — all routine, none tagged. Deliberately on
the COARSE standards and small uniform plates: every claim is that the accelerated OPERATOR agrees
with the dense one on the same mesh, which a coarse mesh tests exactly as hard.

- the accelerated PRODUCT against `PlanarFill.ScalarPotentialMatrix`'s own `P x` (asked of the
  operator, not only of the solved capacitance, because a solve can absorb a product error into a
  charge vector whose SUM still looks right);
- `C_pul` and each standard's total against the dense solve, to 1e-6;
- the tolerance ladder, kept as a test rather than a note so the default's justification is re-run;
- the self-kernel sentinel moved ×0.1 and ×4 — the near-set completeness gate, since G(0) is
  arbitrary and only legitimately so if every overlapping-stencil pair is near;
- `PlanarStaticLimitTests`' plate-over-ground oracle and its quadrature-separability oracle, through
  the accelerated path, plus the sharper form of the first (accelerated vs dense at each spacing);
- `Aim = null` is today's code asserted as EXACT equality, not to a tolerance;
- an accelerated call with no slab height refuses (a silent dense fallback would reinstate the
  ceiling invisibly);
- the near field is O(m) in entries per row — a COUNTER, not a stopwatch.

`EmDeembedCeilingTests` gains the two setup-refusal gates in item 5 above, on the owner's own taper
shape. `EmCeilingRefusalTests.Gate1_…` is inverted from "refuses at setup" to "no longer refuses at
setup", proved with a **pre-cancelled `RunControl`**: the first cancellation checkpoint is one
Green's-function fit into the first frequency, so an `OperationCanceledException` is positive proof
that setup completed, at a fraction of a second where solving that taper de-embedded is ~40 s per
point.

## No second implementation

The shared parts were MOVED, not copied: `AimProjection` (the Vandermonde inverse, the moment match,
the moment quadrature, the near set) and `AimGridFft` (the circulant embedding and the cyclic
convolution) came out of `PlanarAim.cs` unchanged, and `PlanarAimGeometry`/`PlanarAimOperator` call
them. The near set's exact entries are `PlanarPulsePotential`, which is `PlanarEntryFill`'s own
`P(a, b)` carved out whole — same class-keyed memo, same singular cores — so the near field is
literally the dense path's arithmetic rather than a second formulation of it.

## What changed, for a reader who only wants the code

```
src/Engine/Mom/PlanarStaticAim.cs   NEW — PlanarStaticAim, PlanarStaticAimReport
src/Engine/Mom/PlanarAim.cs         + AimProjection, AimGridFft (moved out of the two AIM classes)
                                    + PlanarAimSettings.StaticTolerance (1e-10, measured)
src/Engine/Mom/PlanarFill.cs        + PlanarPulsePotential (carved out of PlanarEntryFill.P)
                                    + PlanarEntryCores.PrepareScalarPair
src/Engine/Mom/PlanarDeembed.cs     StaticCapacitance: + the accelerated route and a slabHeightM
                                    parameter; GuardCapacitanceCeiling is now public and takes the
                                    route's flag
src/Engine/Mom/PlanarSolve.cs       the standards' ceiling refusal moved BEFORE the calibrator is
                                    built (it was unreachable) and re-worded;
                                    PlanarPortCalibrator takes a pre-built standard set
```

## Not done, on purpose

**The static accelerator does not share the DUT's `PlanarAimGeometry`.** It builds its own
`PlanarEntryCores`, so a mesh that has both pays two class-core warm-ups. Sharing would force the
LONGEST standard's basis geometry to be built even when no frequency ever selects it — which is
exactly the lazy build M4 of the sweep-performance brief put in — and the class store is cheap.
Measured cost of the duplicate on the taper's wide standard: the near fill is 219 ms of a 9.7 s
solve.

**`AimEntry`'s `(M+1)⁴` lookups per near pair were not restructured.** §8's own P6 note records the
same hotspot on M5's operator; here it is 93-354 ms against a sparse LU of 1.6-9.2 s, so it is not
what to fix first on this path.

# P12 — multi-level and vias under AIM, as a bordered system (2026-08-29)

`brief-em-p12-aim-bordered-vias.md`. The tables are in `HISTORY.md` §P12; what is here is the
narrative, the two findings that are not in a table, and the ceiling sentence the brief hands back
to the owner.

## What was refused, and what the obstacle actually was

`PlanarAimGeometry.Build` threw on any mesh carrying a ẑ basis and `PlanarSolveContext.SolveAt` threw
on the general kernel whenever `Aim` was set, so a board with one ground via ran at the **dense**
5,000-unknown ceiling however much of it was ordinary horizontal metal. The refusal's stated reason
— "a different grid kernel per height pairing and a projection with a derivative in it" — is a true
statement about PROJECTING the ẑ bases. It was never an argument for projecting them.

R-via-5 already orders every horizontal rooftop before every vertical one, so the matrix is
`Z = [Z_hh Z_hz; Z_zh Z_zz]` with `N_z` in the tens, and the three blocks have different economics:

- **`Z_hh`** is the operator M5 already accelerates, one pairing at a time. Every level shares the
  SAME auxiliary grid — the grid is in-plane and the levels differ only in z — so a second level
  costs one more kernel table and one more FFT hat pair per component, `L(L+1)/2` for L levels. The
  scatter and the gather become per level. **The stencils, the moments and the near set are unchanged
  objects**: they are in-plane, and the near-set rule (radius ∪ stencil overlap) is in-plane too, so
  it is a valid superset for a cross-level pair with no argument needed.
- **`Z_hz`, `Z_zh`, `Z_zz`** are dense, filled through a new internal seam onto `PlanarFill`'s own
  `MixedEntry`, `SingularPrismPart` and cell-pulse potential. Not a re-derivation — the same
  functions, called per entry instead of per row.

## Findings

1. **The gate that means something is EXACTNESS, not a tolerance — and the near radius is the knob
   that produces it.** Every `|ΔI|` against a dense solve mixes two things: whether the border and
   the multi-level near assembly are the dense fill's arithmetic, and how good the projection is on
   that mesh. Widening `NearRadiusFactor` until every pair is in the near set removes the second
   entirely, and the bordered operator then has to reproduce `FillMultiLevel`'s matrix to round-off.
   It does: **1.1e-15** entry-wise, **4e-11** in the solved current, on the MMIC two-level fixture
   with its via and on the FR-4 hero with a backside ground via. That is the claim worth making, and
   it needs no tolerance.

2. **The brief's "8.7e-7 at the shipped defaults" is not transferable, and quoting it at a different
   fixture would have graded P12 on somebody else's mesh.** It is `AimAccuracyTests`' figure for the
   32 mm FR-4 hero at the SHIPPING mesh. Measured on a cells/λ = 80, un-edge-meshed two-level rung
   the bordered operator reads 3.3e-6 — and the **single-level control**, the same mesh character
   with no via in it at all, going through the shipped `PlanarAimOperator` against the shipped
   single-level dense fill, reads **2.7e-5** where the shipping mesh reads 4.9e-7. **55× of spread
   from the mesh alone, on a path P12 does not touch.** On the shipping mesh the ground-via fixture
   reads **3.97e-7**, inside the brief's number. Every accuracy figure in `HISTORY.md` §P12 is
   therefore printed with its mesh, and the control table is printed beside it.

3. **`N_z ≪ N_h` keeps the border cheap in MEMORY unconditionally and cheap in TIME only
   conditionally, and the two must not be stated together.** `Z_hz` is `16·N_h·N_z` bytes — 0.5 MB at
   N = 15,192 — and stays negligible however the fixture grows. Its BUILD is `N_h × N_z` graded 4-D
   quadratures, and at a fixed via count that is flat in N (0.43 → 0.56 s across an 8.8× N ladder).
   Grow the via footprint WITH the part and `N_z` reaches 140, at which point the border is **28.6 s
   of a 34 s point** while the accelerated near fill is 0.62 s. The dense path pays the same
   `N_h × N_z` mixed entries, so this is not a regression against it — but it is what a claim about
   a via FIELD would have to be measured on, and the brief's "later brief with its own measurement"
   is the right disposition.

4. **`N_hh⁻¹ Z_hz` must not be stored, and that decides the preconditioner's shape.** §11's flat
   iteration count is a finding about the horizontal operator; a preconditioner with nothing at all
   in its `N_z` rows would be steering GMRES past a short circuit, so the border is folded in
   EXACTLY, by block elimination on an `N_z × N_z` Schur complement `S = Z_zz − Z_zh N_hh⁻¹ Z_hz`.
   The obvious implementation keeps `W = N_hh⁻¹ Z_hz` and applies the inverse with one sparse solve;
   `W` is 38 MB at N_h = 12,000, N_z = 200, on a working set whose whole point is to be small. So `S`
   is built one column at a time and discarded, and the apply pays a SECOND sparse substitution
   instead — a fraction of a percent of an iteration that already runs three FFTs over the padded
   grid. Measured: GMRES 4 → 6 iterations from N = 1,728 to 15,192.

5. **A (level, level) pairing can legitimately have no vector kernel at all, and the null must be
   handled rather than asserted away.** `MultiLevelPairings.Resolve` fills `TermsA[la, lb]` only when
   some direction has bases on BOTH levels, so a level carrying only x̂ rooftops paired with one
   carrying only ŷ has a scalar kernel and no vector one. D5 makes the vector block identically zero
   there, so the entry is the scalar half alone — which is exactly what `PlanarEntryFill.At` would
   have returned had it existed for that pairing. The bordered operator computes it directly rather
   than dereferencing a null it "cannot" reach.

## The ceiling — the brief's own hand-back, written out and NOT applied

`SurfaceMesher.AcceleratedUnknownCeiling` = 12,000 is still **single-level only**. The measurement
that would justify widening it is made (`HISTORY.md` §P12 Table 4): the length ladder is healthy to
**N = 15,192**, near entries per row 488 → 517, GMRES 4 → 6, residual 3.6e-9, a whole frequency point
~2.5 s and 327 MB against 4,927 MB for a dense point of the same size. Two lines change:

```csharp
// src/Engine/Mom/PlanarSolve.cs — PlanarSolveContext's constructor
-        bool accelerated = Settings.Aim is not null && levels is null;
+        bool accelerated = Settings.Aim is not null;
```

…except that it is not that line any more, and finding out why turned up a live defect. The brief
names `SurfaceMesher.GuardCeiling`'s `accelerated` argument as the second place; its three callers
(`PlanarKernel.Solve`, `PlanarKernel.SolveWithReport`, and `EmSetupEditorViewModel`'s pre-solve mesh)
all passed `Aim is not null` with **no level condition at all**, while `PlanarSolveContext`'s
constructor asked `Aim is not null && levels is null`. **So the report a user reads before pressing
Simulate has been judging a via-bearing accelerated mesh against 12,000, and the run then refused it
at 5,000** — quoting the dense ceiling, which is neither the number the report used nor a remedy.
Reachable before P12 and reachable after it; invisible only because the accelerator refused a via
mesh outright a few lines later and THAT was the message the user saw.

The two are now one function, and the owner's decision is one line in one place:

```csharp
// src/Engine/Mom/SurfaceMesher.cs
    public static bool UsesAcceleratedCeiling(bool aimOn, bool multiLevel) => aimOn && !multiLevel;
//                                                                        => aimOn;   ← the flip
```

`PlanarSolveContext`, `PlanarKernel`'s two mesh calls and the EM panel all read it. **Making them
agree does not pre-empt the decision** — it keeps today's shipped behaviour (a via mesh judged at
5,000) exactly as it is, and removes a report that contradicts the run.

**A fifth site was found by asking the same question of the whole file rather than of the four the
defect named**: de-embedding's setup-time refusal in `PlanarKernel.Solve` sized a port's calibration
STANDARDS against `fillSt.Aim is not null && !general` — the same decision, spelled out a second
time. It agrees with the function today, which is why nothing was visibly wrong with it, and it is
latent rather than reachable (a via mesh between the two ceilings is refused by the mesh verdict
before de-embedding is reached at all). It is now the function call too, for the reason the function
exists: the flip above must not leave a standard judged against a ceiling its own DUT is not judged
against. `PlanarDeembed.GuardCapacitanceCeiling` is NOT a sixth — it takes `accelerated` as an
argument stating which ROUTE ran (P11: the accelerated static solve holds no m x m), and its caller
`PlanarDeembed.StaticCapacitance` is inside the `Aim is { }` branch that already settled it.

**The reason it is not taken here is finding 3, not doubt about the ladder.** A ceiling stated in N
alone is a promise about a mesh whose `N_z` is unstated, and the border's time is set by `N_z`. A
board with one ground via at N = 15,192 costs 2.5 s a point; the same N with a 140-basis via field
costs 34 s, of which 28.6 s is the border — legitimate, converging, and not what "12,000 accelerated
unknowns" would lead anyone to expect. Widening the ceiling on a bound in N alone, or on a bound in
N and `N_z` together, is the owner's call.

(P8's own caveat still stands beside this one: N no longer bounds the accelerated working set on a
REFINED mesh, and moving 12,000 for either reason wants a check on near entries per row rather than
a bigger integer.)

## Not done, on purpose

- **The ẑ bases are not projected and the mixed kernel is not projected.** The brief forbids it and
  nothing measured here argues for it: at a real via count the border is 2% of a point.
- **`Z_zh` is not stored.** Z is complex-symmetric bit for bit, so the transpose is read out of
  `Z_hz` rather than held.
- **No mirror index for the near set.** P6's own 18 MB decision is unchanged; the bordered operator
  finds each transpose position by the same binary search.
- **The dense multi-level path is untouched.** `Aim = null` reaches `PlanarSystem.BuildMultiLevel`
  exactly as before, and every published multi-level number stands.

## What was changed in the code

```
src/Engine/Mom/PlanarAimBordered.cs   NEW — PlanarBorderedAimOperator, PlanarBorderedAimReport
src/Engine/Mom/PlanarAim.cs           PlanarAimGeometry gains the horizontal/vertical split and
                                      asserts R-via-5's prefix instead of refusing a via mesh;
                                      PlanarAimOperator now carries the refusal and names the
                                      bordered operator
src/Engine/Mom/PlanarFill.cs          MultiLevelPairings + Resolve made internal; SingularPrismPartOf,
                                      MixedEntryOf, CellPairSpanOf wrappers; PlanarPulsePotential
                                      takes an explicit remainder; PlanarEntryFill takes a prebuilt
                                      PlanarPulsePotential
src/Engine/Mom/PlanarSolve.cs         the general-kernel refusal replaced by the bordered operator;
                                      LastBorderedAccelerator; the standards' ceiling decision reads
                                      SurfaceMesher.UsesAcceleratedCeiling instead of restating it
tests/Engine.Tests/Mom/PlanarP12BorderedAimTests.cs   NEW — 3 routine tests (3 s), 5 Benchmark (31 s)
tests/Firewall.Tests/user-facing-text-allowlist.txt   two retired messages out, five new ones in
```

# MIM-3 — thin dielectric layers: the verdict (brief-em-mim-3-thin-layer-gate.md, 2026-08-30)

**The question.** A MIM capacitor puts two meshed conductor levels 0.05–0.5 µm apart.
`LayerStack.CanRepresent` accepts any positive thickness, so nothing refuses the structure — which
is the dangerous configuration: a complete, plausible answer with no evidence behind it. The brief
was measurement-first, and the measurement had to come before any fix.

Every table is in `HISTORY.md` §MIM-3. Run against a post-MIM-6 build, so the plate gap is the
0.2 µm the shipped technology states rather than the 3.2 µm a pre-MIM-6 tree would have measured.

## The verdict, in one paragraph

**The kernel is not the problem and the quadrature is.** `Dcim.FitAtHeights` at height pairs
straddling the capacitor dielectric is *flat in the separation* — worst 4.2e-3 of the free-space
kernel at 0.05 µm against 6.4e-3 at 3 µm, the interconnect spacing L9c already measured — so no
ρ/λ constant moves and none was moved. What degrades is the **cross-level block of the fill**,
which loses four decades between `cell size / level separation` of 1 and 20: 2.3e-7 at 1, 4.1e-3
at 5, 3.9e-2 at 10, 1.5e-1 at 20 and 4.9e-1 at 50, while the same-level block stays at 3e-6. The extracted plate
capacitance follows that ladder rung for rung — within 10% of ε₀εᵣA/d while cell/separation ≤ 5,
1.46× at 12.5, and the **wrong sign** at 25. So the shipped defaults hold over a stated range, the
range is a **mesh** condition rather than a stackup one, and outside it a NOTE reports it:
`PlanarLevels.ValidatedCellOverSeparation = 5.0` and `PlanarSolve.LevelSeparationNotes`.

**The shipped MIM technology sits outside it.** A 10 × 10 µm plate pair 0.2 µm apart meshes at
2.5 µm — cell/separation = 12.5 — so its plate capacitance reads about 1.5× high and the note
fires on it. That is stated rather than fixed: see *Not done, on purpose*.

## Findings

**1. There is no thin-layer KERNEL failure, and the control rungs are what say so.** The brief's
suspicion was §L8c's — DCIM's fitted images stop being smooth on the mesh's own scale once an image
sits closer to the metal plane than a cell is wide. Measured, that does not happen here: the scaled
error is flat from 0.05 µm to 3 µm on all four components and inside L9b's own ≤1.6e-2 envelope
throughout. The εᵣ = 1 control (same geometry, no contrast) is flat too, so the flatness is not two
effects cancelling. **A ladder without a rung in the already-measured regime could not have said
this** — the 2 µm and 3 µm rungs are what turn "0.05 µm gives 4e-3" from a number into a verdict.

**2. The failure is the recorded trap, at a scale nobody had asked it at.** §3.5 already says
*"cross-level entries have no 1/ρ but still carry logarithms"* and that *"different levels ⇒ smooth
⇒ plain quadrature"* is a trap. At d ≪ cell the cross-level kernel carries a peak of width d inside
a cell of width h, and the rule integrates over it. The trap was recorded for surface waves and the
mixed kernel's 1/k_ρ² tail; the plate regime is the same trap with the peak made arbitrarily narrow
by the stackup.

**2b. And the coarsest rungs are stated for what they are.** At cell/separation 20 and 50 the
forced-high reference is itself only 9× and 5.7× better than the shipped rule (against 150× at 5
and seven decades at 1) — the same narrow peak is starting to defeat the expensive rule too. Those
two rungs support "the error is large and grows" and not their own third significant figure.
**Nothing in the verdict rests on them**: the range is drawn at 5, where the reference has 150× of
headroom, and the 25/50 rungs of the physics ladder need no precision to say "wrong sign".

**3. Nothing downstream can see it.** Reciprocity holds to 1e-19 and passivity to 1e-5 at every
rung, *including the ones whose extracted capacitance has the wrong sign*. §10.9's oracle-free
self-consistency set is structurally blind to this: the matrix is still complex-symmetric and the
structure still does not create energy — what is wrong is a magnitude inside it. This is §L8c's
converged-looking-but-wrong mode, one tier down in z, and it is the whole reason the brief was a
measurement rather than a test.

**4. RAW S CANNOT CARRY A CAPACITANCE IN THIS ENGINE, and this is now measured on a known-good
line.** Two instruments were built and discarded before the third: a one-port shunt cap reading
`Im(Y₁₁)/ω` (negative at d ≤ 0.1 µm) and a series two-port reading `−Im(Y₂₁)/ω` (0.50 → 0.18 fF
over a 40× change in d where ε₀εᵣA/d spans 120 → 3 fF). The control that settled it is one line: a
70 µm GaAs microstrip, 800 µm long at 10 GHz — a matched ~50 Ω line — reads **|S₂₁| = 0.0706** raw.
The port is a series delta gap with `a₂₁ ∝ ω` (§5), so a raw reading is the port and not the
structure, at any topology. **The series topology's usual argument does not save it**: "Y₂₁ isolates
the through element because port discontinuities are shunt" is true of an ideal error box and
useless against an `a₂₁` two orders below unity.

> **This independently reproduces MIM-2's finding (b) — and it confirms MIM-2's RETRACTION of it,
> not the finding.** The series brief's rule (*never read a small element's value off raw S*) is
> stronger than it looked: it is not only that a ~0.3 fF discontinuity masks a small element, it is
> that a raw reading of ANY reactive element in this engine is dominated by the port. Correcting the
> instrument moved a 0.31 fF answer to 44 fF against a 30 fF closed form.

**5. De-embedded, the plate capacitance is there and is right where the mesh resolves the gap.**
With the stack truncated at the upper plate (so `LevelIsOnSlabTop` is satisfied) and the same
structure minus its lower plate subtracted as a baseline, `(C − baseline)/(ε₀εᵣA/d)` is
0.89 / 0.99 / 1.10 at cell/separation 1.25 / 2.5 / 5. The baseline itself — feed, port, plate to
ground — is 4.93 fF and frequency-stable to 8% from 20 to 140 GHz, while the plate pair is not, and
destabilises in the same order the fill ladder degrades.

**6. Kernel A settles the closed form.** On the equivalent cross-section it reproduces ε₀εᵣW/d to
**1.007 at d = 0.05 µm**, rising monotonically to 1.16 at 2 µm exactly as a fringing correction
should, and is mesh-converged to five digits. It shares no code with kernel B. So a 0.05 µm gap of
εᵣ 6.8 is not intrinsically hard, and there is no argument that the closed form is the thing in
error. The brief called this a free second opinion; it was the load-bearing one.

**7. An observation that is NOT a MIM-3 finding.** The mixed component at a same-level pairing fits
with zero images and an infinite residual, and its strict relative error is 2.07 at every rung —
*d-independent to four figures*, and worse (1.58e+1) on the εᵣ = 1 control. It is a property of that
pairing, not of thin layers, its scaled error is ≤ 7.5e-4 throughout, and the fill only asks the
mixed kernel about a via against a horizontal basis. Recorded so it is not re-found as a MIM result.

## What was built

- **`PlanarLevels.ValidatedCellOverSeparation = 5.0`** — 5 is where the two independent ladders
  agree (the fill's ≤ 4.1e-3 and the extracted capacitance's ≤ 10%), not a round number chosen
  first. Its doc comment carries both ladders.
- **`PlanarSolve.LevelSeparationNotes`** — a NOTE, never a refusal, for R-prt-13's reason: the
  answer is still produced, still reciprocal, still passive, and what is unreliable is a magnitude.
  A refusal would also take away every multi-level run whose ratio is fine. It is asked **per
  adjacent level pair, over the cells on those two levels only** (R-zz-1's discipline — a per-mesh
  question would grade a plate pair on some unrelated conductor's cell), and it **names the binding
  quantity**: the transverse pitch is `width / MinCellsAcrossConductor` and does not respond to λ,
  so it says outright that Cells per wavelength and Mesh frequency change nothing here rather than
  offering two inert knobs (§3.5's trap, and the reason `BuildRefusal` asks `waveBinds`). It also
  scopes the damage — single-level results are unaffected.
- **`MimThinLayerTests`** — 6 routine tests, ~1 s total: the cross-level block on a fixed input
  against literals (P3/P4's pattern), plus the note's firing threshold, its wording, its per-pair
  scoping, its silence on a single-level problem, and its wiring through `VerticalRangeVerdict`.
  `T7` carries the accuracy statement and is `Category=Benchmark` (1 m 5 s in Debug).

## Not done, on purpose

- **The quadrature was NOT changed.** The brief allowed "a bounded quadrature/fit change with
  milestones 1–3 rerun after it" and it is not bounded: the cross-level near rule would need the
  same singularity-extraction treatment the same-level one has (§3.3's three singular pieces), for
  a peak whose width is a stackup parameter rather than a mesh one, and then all three ladders plus
  every bit-identity digest in `PlanarP3/P4/P5` re-pinned. The brief's own instruction applies — *if
  the fix is not cheap, stop and report; the ladder itself is this brief's deliverable.*
- **No existing gate was loosened, and none needed to be.** `AimAccuracyTests`' 8.7e-7 and the L9
  gates are untouched, and so are `Dcim.ValidatedRhoOverLambdaAtHeights`, `…Layered` and
  `…InteriorHorizontal` — Table 1 says they are right where they are.
- **The shipped MIM technology was not re-dimensioned to pass its own note.** Widening the plates
  or thickening the dielectric would move cell/separation under 5 and silence the note, and it would
  be tuning the exemplar to the tool. The plate gap is what the process states.
- **The mesher was not taught to refine across a thin gap.** That is the actual remedy — a
  transverse pitch derived from the nearest level separation as well as from the conductor width —
  and it is a `SurfaceMesher` change with its own unknown-count consequences (a 10 µm plate at
  cell/separation = 5 needs 0.4 µm cells, i.e. 25 across instead of 4, and the shared tensor grid
  spreads that across the whole domain). Named here, not built; the note names it as the binding
  quantity so a user is not left guessing.
- **A de-embedded reading on the shipped stack is still MIM-4's.** Milestone 3 had to truncate the
  stack at the upper plate to get a de-embedded port at all; on the real MIM stack both plate levels
  are buried and `LevelIsOnSlabTop` refuses. That is §7's "all of Part B", unchanged.

---

# MIM-4 — the interior-height static Green's function (brief-em-mim-4-interior-static-greens.md, 2026-08-30)

§7's "all of Part B", built. Three refusals fenced the de-embedding path in, and every realistic MIM
run — whose feed arrives on upper metal — hit one of them: `PlanarSolve`'s throw for a de-embeddable
edge port off the slab top, `PlanarExtractor`'s refusal of more than one dielectric under the lowest
level, and `LayeredStaticGreens` / `DcimModel.Evaluate` / `SommerfeldIntegral.EvaluateLayered`
refusing interior sources. **The first two are retired; the third is narrowed to what is still true
of those three classes.**

## The verdict, in one paragraph

At ω = 0 the layered problem is a **Sturm-Liouville problem in z**, not a reflection referenced to a
region, and that reframing is the whole of milestone 1:
`d/dz(P dG̃/dz) − k²P G̃ = −2k·δ(z−z′)` with `P = ε*` (scalar) or `1/µ` (vector), solved as
`G̃ = −2k·ψ↓(z_<)ψ↑(z_>)/W`. Source and observer may sit anywhere. The spatial inverse is a
**two-level Prony image fit** — `∫e^{−bk}J₀(kρ)dk = 1/√(ρ²+b²)`, so a fit of the spectral remainder
IS an image expansion — with the sum rule imposed exactly. It reproduces the shipped one-slab series
to 1e-10 out to ρ = h, agrees with direct Hankel integration to 1e-8 at interior heights on a
0.2 µm-over-100 µm thin-film stack, and evaluates in **0.34 µs per ρ against the shipped image
series' own 2.5 µs**. `C_pul` through it reproduces the shipped `C_pul` to **3.5e-12** where both
apply, and a uniform line on a buried level de-embeds to a matched section. The shipped on-slab-top
path is untouched.

## Findings

**1. The inter-region scale factor has an exact cancellation, and without it the formulation is
numerically dead at small k.** Matching ψ↓ across interface *i* divides by `1 + Γ↓_{i−1}τ_i²`, which
is ψ↓'s own value at the top of a layer — and over a PEC floor that vanishes as k → 0 (Γ↓(0) = −1
through any stack, so it is `1 − e^{−2kd}`), while the numerator vanishes with it. 0/0 at k = 0
exactly, small/small beside it, with relative error the reciprocal of the layer's electrical
thickness. Substituting the reflection recursion collapses it analytically:

```
    1 + Γ↓_i = (1 + r_i)(1 + Γ↓_{i−1}τ_i²) / (1 + r_i Γ↓_{i−1}τ_i²)
```

so the vanishing factor cancels and what is left is `τ_{i+1}(1+r_i)/(1 + r_iΓ↓_{i−1}τ_i²)`, whose
denominator is bounded below by `1 − |r_i| > 0` for every passive stack. Gated at k·H = 1e-9 on a
cross-region pair, to 1e-15 absolute.

**2. Every exponential is written as a decay over its own region, and that matters more here than in
the full-wave kernel.** `e^{−k(z_hi−z)}` and `e^{−k(z−z_lo)}`, never `e^{+kz}`. The static integrand
is killed by nothing but those exponentials, so k runs out to tens of reciprocal layer thicknesses on
a thin-film stack — 1e8/m on the shipped MIM stackup — and the obvious form
`ψ↓ψ↑/(Pτ(1−Γ↓Γ↑τ²))` overflows. The same-region product is expanded so the layer's own τ cancels
against the Wronskian's algebraically rather than by division; the four terms that come out are the
four classical distances (direct, floor image, ceiling image, one round trip), each with a
non-negative exponent by construction.

**3. THE FIT'S SPECTRAL RESIDUAL DOES NOT BOUND ITS SPATIAL FAR FIELD, and the sum rule is only half
of why.** Measured, not assumed. With an unconstrained least squares the spectrum was exact to
**2.4e-15** and the spatial function was **8.5e-6** wrong at ρ = 1000 h. Two separate causes, found
in order:

- The 1/ρ tail's coefficient is `c_∞ + Σa`, which over a ground plane is exactly **zero** — a
  grounded structure's potential falls as a dipole. An unconstrained fit gets that cancellation only
  to its own residual. It is now imposed exactly, by eliminating one column
  (`a_p = rule − Σ_{j≠p}a_j`, the deepest image, so `φ_j − φ_p` is `φ_j` almost everywhere and the
  conditioning is the unconstrained problem's). Verified to 1e-16 — **and it did not move the far
  field at all**, which is what pointed at the second cause.
- The far field is set by the **second moment `Σ a b²`**, and the images cancel by a factor of ~45
  there. That is a k-derivative-like quantity at k = 0, and both Prony grids are uniform, so their
  first step IS their resolution near k = 0: the spatial far field at ρ is governed by k ≈ 1/ρ, which
  for any ρ past a few stack heights falls INSIDE that first step. A third sample block — geometric,
  40 points over five decades, added to the AMPLITUDE least squares only (Prony needs uniform
  samples, so it never sees it) — cut the far-field error **10–60×**, to 5e-7…7e-6.

What is left is a fractional error in that 45× cancellation, and it is negligible in the only terms
that matter: at ρ = 1000 h the kernel has fallen 1.6e9 from its ρ = h value, so 1e-6 of it is
**4e-14 of the near-field scale that sets a capacitance**. Constraining the second moment as well as
the sum is the named remedy if it ever binds; it needs `R″(0)` to more digits than a difference
quotient gives, and it is not built.

**4. Fit versus quadrature: quadrature is the loser, by four to six orders of magnitude, and it stays
as the oracle.** The brief asked for the decision to be measured. Per ρ: the image model costs
**0.34 µs** (9–27 images), `PlanarKernelTerms.StaticScalar`'s own shipped series costs **2.5 µs**
(130 images on GaAs), and `InteriorStaticGreens.PotentialByQuadrature` costs **1–270 ms**, rising
with ρ because the J₀ partition does. The fit costs 16–78 ms **once** per level. That is not close,
and the fill's radial table wants ~10⁴–10⁶ samples. The reference integrator is kept because it
shares no approximation with the model — its own partition is geometric as well as at the Bessel
zeros, because the integrand carries two length scales that differ by three orders of magnitude on a
thin-film stack and a uniform partition sized for the finer one costs a quarter of a million panels —
and it **refuses by name** rather than grinding when the partition it would need is absurd.

**5. The fit recovers the exact physical images where they exist, which is the strongest single
check that it is an image expansion and not a curve fit.** On the FR-4 slab the fitted depths come
back at b/2h = 1.000, 2.000, 3.006, … with amplitudes matching `−(1+K)(1−K)K^{n−1}` (−0.6035, 0.3800,
−0.2466 against −0.6036, 0.3800, −0.2393); the tail of the 130-term series is carried by a handful of
complex images. Nobody told it about `2nh`.

**6. THE C_pul ROUTE MUST BE CHOSEN BY COMPARING THE MEDIUM, NOT THE LEVEL'S HEIGHT — and the case
that proves it is the one this brief created.** A single level over a STRATIFIED sub-feed region sits
at the top of its medium AND at `slab.HeightM`, because the extractor's sizing slab is built from
that same distance. A height test would answer "on the slab top" there and put a two-dielectric
board's published reference impedance on a one-dielectric image series — plausibly and wrongly, which
is the exact failure the retired refusal existed to prevent. `PlanarPortCalibrator.DescribedByTheSlab`
compares structurally instead: one layer, of the slab's own material and height, PEC below,
half-space above, level on its top surface. `PlanarProblem.LevelIsOnSlabTop` still says something
true and now gates nothing.

**6b. That test also changes an answer the old code got wrong, and the change is deliberate and
named.** A MULTI-LEVEL problem whose port sits on the slab top but has a dielectric ABOVE that level
— an encapsulation, an interlayer — used to pass `LevelIsOnSlabTop` and take the one-slab image
series, which ignores everything above the metal. It now takes the interior route in the real stack.
Measured on the MMIC two-level fixture (100 µm GaAs + 3 µm εᵣ 2.7): the M1 port's Z_c is 43.34 Ω
where the one-slab series had it elsewhere, and the M2 port's is 48.78 Ω. **This is the one place
this brief perturbs a previously-passing de-embedded result**, and it is a correction rather than a
regression: the old value was the electrostatics of a bare slab in air, which that structure is not.
No existing gate moved — the whole `Category=Benchmark` multi-level and MIM tier passes — and the
alternative (keeping the height test so the wrong answer stays bit-identical) was rejected because
finding 6 shows the height test is not a correct rule in the first place.

**7. A stratified medium turns the general kernel on at ONE level too.** Before this brief that case
could not arise — a stratified region under the lowest level was refused at extraction, and with one
level there is nothing above it — so `PlanarExtractor` attached an explicit `MediumStack` only for a
multi-level problem. Carrying the layers without that change would have handed L8's one-slab kernel a
stack it does not describe. `generalMedium = levels.Count > 1 || mediumStack.LayerCount > 1`.

**8. The extractor's `GroundedSlab` is now purely a SIZING object where the region is stratified, and
the right average for that job is the series-capacitance equivalent.** It still sets the calibration
standards' geometry, the branch-continuation β seed, the accelerated near-radius floor and the mesh —
none of which is the published reference impedance any more. `h/ε_eff = Σ d_i/ε_i` is the exact
electrostatic equivalent of layers in series, reduces to the single layer's own εᵣ bit for bit, and
is what a wide line over the real stack converges to: **21.3% / 10.3% / 3.2% / 1.1%** difference from
the true stratified `C_pul` at W/h = 0.5 / 2 / 8 / 24. The note says out loud that the number is for
sizing and never for the reference impedance.

**9. Comparing two independently calibrated runs is not a de-embedding gate, and the first version of
G2 was one.** A₂₁'s SIGN is a continuation carried from the previous frequency; at a single frequency
there is nothing to continue from, so two `PlanarSolve.Run`s of the same cross-section landed on
opposite signs and their S₂₁ differed by exactly π (expected `<0.902, −0.425>`, got
`<−0.877, 0.461>`, magnitudes agreeing to 0.6%). Their Z_c also differed by 5.6%, because a standard
reproduces its DUT's own longitudinal gridlines (D4) and the two DUTs are different lengths. Neither
is a fact about the interior electrostatics. The cascade identity is gated the way `T4_2` gates it on
the slab top: ONE calibration, applied to both lines.

## What was built

| Where | What |
|---|---|
| `Mom/InteriorStaticGreens.cs` | **New.** The ω = 0 spectral kernel at arbitrary (z, z′) for a general `LayerStack` — the cascade (`Build`), `Spectral`, `AsymptoticConstant` (the exact 1/ρ coefficient), `SnapToInterface`, `CanEvaluateAt` (the one refusal left: a point inside a solid wall), and `PotentialByQuadrature`, the reference inverse transform |
| `Mom/InteriorStaticImages.cs` | **New.** `InteriorStaticModel` (an exact 1/ρ term plus complex images, `Evaluate`, `SmoothAtZero`, `Residual`) and the two-level Prony fit with the constrained amplitude solve and the low-k block |
| `Mom/SingularExtraction.cs` | `PlanarKernelTerms.StaticScalarAt` — `StaticScalar`'s shape for any level at any height. `StaticScalar` untouched |
| `Mom/PlanarDeembed.cs` | `CapacitancePerMetre`'s `LayerStack` + `levelZ` overload. The `GroundedSlab` overload is byte-identical |
| `Mom/PlanarSolve.cs` | The buried-level throw **retired**; `PlanarPortCalibrator` takes `mediumStack`, chooses its C_pul route via `DescribedByTheSlab`, and reports `InteriorFitResidual`. `InteriorCPulResidualCeiling = 1e-6` is what is refused in its place |
| `Design/Layout/Em/PlanarExtractor.cs` | The stratified-sub-feed refusal **retired** — the layers are carried, the sizing slab is the series equivalent, and a note says which is which. `generalMedium` attaches the stack at one level too |
| `Mom/LayeredMedium.cs`, `Dcim.cs`, `SommerfeldIntegral.cs`, `PlanarStaticAim.cs`, `PlanarProblem.cs` | Refusal wording swept: each now names the object that answers, and `DcimModel.Evaluate`'s stale "no static Green's function at interior heights in this repository" clause is gone |
| `tests/Engine.Tests/Mom/InteriorStaticSpectralTests.cs` | **New**, 13 routine tests: symmetry, flux continuity across every interface, the wall refusal, the exact reductions, split invariance at 1e-14, `1 + Γ_e`, agreement with `LayeredStaticGreens`, and an INDEPENDENT finite-volume solve of the same ODE gated on Richardson behaviour as well as on error |
| `tests/Engine.Tests/Mom/InteriorStaticImageTests.cs` | **New**, 14 routine tests: the shipped series, the sum rule, `LayeredStaticGreens`, direct Hankel integration at interior heights and on the thin-film stack, the vector reduction, the extraction constants, and the integrator's own refusal |
| `tests/Engine.Tests/Mom/InteriorCPulTests.cs` | **New**, 6 routine tests: the two kernels on the one problem where both apply, split invariance, the series-capacitance limit (both halves), the route selection, and every stack's fit residual against the ceiling |
| `tests/Engine.Tests/Mom/InteriorDeembedGateTests.cs` | **New**, 2 `Category=Benchmark` (45 s together): a uniform line on a buried level de-embeds to a matched section, and the cascade identity there |
| `tests/Ui.Tests/Em/StratifiedSubFeedExtractionTests.cs` | **New**, 4 routine tests: the stratified region extracts, both layers reach the medium, the sizing slab is the series equivalent and the medium is not, the note replaces the refusal, and one dielectric is unchanged |
| `tests/Engine.Tests/Mom/MultiLevelPortTests.cs` | `M3_2` inverted rather than deleted — the port that was refused now de-embeds, and M1 and M2 must not come back with the same Z_c |

## Not done, on purpose

- **The second-moment constraint** (finding 3). Named, measured, not built: the absolute error it
  would remove is 4e-14 of the near-field scale.
- **A static kernel for a CROSS-LEVEL cell pair.** `PlanarStaticAim.Build` and
  `PlanarDeembed.StaticCapacitance` take ONE kernel at ONE height pair; a multi-level static solve
  needs a per-pairing SET, the way `PlanarKernelSet` carries the full-wave one. A calibration standard
  is always single-level (D3), so nothing reaches it — the refusal is narrowed to say that instead of
  "this repository does not have it".
- **`LayeredStaticGreens` still refuses interior heights**, and correctly: its own partition (two
  closed terms, and a decay length its quadrature is sized from) is referenced to the top half-space.
  It is an oracle, `InteriorStaticGreens` reproduces it wherever both are defined, and rewriting it
  would delete the independent check.
- **A de-embedded reading of the shipped MIM capacitor's own capacitance.** MIM-4 removes the port
  refusal that blocked it; what still binds is MIM-3's MESH condition, and the shipped technology's
  default mesh sits outside it (`ValidatedCellOverSeparation`). That is gap 3's business, reported as
  a note, and no constant was moved here.

## The mesh frequency looked inert — because on that geometry it IS, and the report said otherwise (2026-09-09)

**Reported symptom.** Cells per wavelength held constant, Mesh frequency lowered, Mesh pressed each
time: the rendered mesh never changed and the panel reported 1,997 cells at every setting.

**Diagnosis — the plumbing is fine and the mesh is correct.** Measured on the reported `.cem` (one
polygon, FR-4, εᵣ 4.4, sweep 0.5–10 GHz, cells/λ = 5), driving `SurfaceMesher.Mesh` directly at 500
MHz, 1, 2, 5, 10 and 20 GHz: **1,997 cells and 3,835 unknowns at every one of them.** The cell size
is `Math.Min(hWave, narrow / MinCellsAcrossConductor)`, the narrowest conductor run is 360 µm, so the
transverse pitch is 90 µm — while the λ_g/5 cap at 10 GHz is 2.858 mm, **31.8× coarser**. The cap
never enters the minimum, and the mesh frequency (and cells per wavelength with it) cannot move it.
This is R-msh-4 working as designed.

**The actual defect is the reporting, and it is the same one twice.**

1. **The λ_g note claimed a cap that did not bind.** It read "Cell size capped at λ_g/5 = 2.858 mm …
   Changing it changes the unknown count" unconditionally — false on both halves here: nothing was
   capped at 2.858 mm (the largest cell built is 185 µm), and changing the frequency changes the
   count by zero. `BuildRefusal` had already been rewritten for exactly this, on exactly this
   geometry class (2026-08-14, the Klopfenstein taper) — but a refusal only fires past the unknown
   ceiling, and this mesh is 3,835 unknowns and *solves*. So the mesh that runs was still being told
   the inert story. The note now asks `capBinds` — `BuildRefusal`'s own question — and where the cap
   does not bind it says so, quotes the narrowest run and the pitch it forces, and names the knobs
   that are inert rather than the ones that are not.

2. **`PlanarMeshSummary` divided λ_g by the CAP.** `λ_g/{GuidedWavelengthM / MaxCellSizeM}` is
   `CellsPerWavelength` by construction, so the panel printed "max cell 185 µm (λ_g/5 at 10 GHz)" —
   an honest max cell beside a ratio describing a different length. It now divides by
   `MaxCellEdgeM`, the cell that was actually built (λ_g/77 here). Where the cap binds the two agree
   and nothing changes.

**The user's real question was "then how DO I coarsen it", and there is an answer.** Measured on
the same file:

| setting | cells | unknowns | verdict |
|---|---|---|---|
| as set in the `.cem` | 1,997 | 3,835 | Warn |
| **edge mesh off** | **1,171** | **2,215** | **Ok** |
| staircase instead of conformal | 1,988 | 3,838 | Warn |
| edge mesh off + staircase | 1,161 | 2,214 | Ok |

**The edge mesh is the one mesh control still live on a geometry-bound mesh — 41% of the cells and
42% of the unknowns, and it takes the budget verdict from Warn to Ok.** So the non-binding note names
it (when it is on) rather than sending the user straight to "redraw your artwork": the first version
of this note listed only the geometry remedies, which is accurate and useless. `BuildRefusal` had
always listed it; the note had not, because the note had never distinguished the two cases at all.

For scale, the part is 6.07 × 1.96 mm with a 360 µm narrowest run — **0.042 λ_g across at the 1 GHz
mesh frequency**, 0.42 λ_g at the sweep's 10 GHz top. An electrically small structure is resolved by
its geometry, never by λ, which is why the note now states the λ_g count outright.

**`MinCellsAcrossConductor` was measured as a hypothetical control and is NOT worth exposing.**
Temporarily rebuilt at 2, 3 and 4 on this file: 1,374 / 1,141 / 1,997 cells. Note it is **not
monotonic** — 2 costs more than 3, because the grid-line placement interacts with the edge grading —
so a user turning it down would sometimes get a denser mesh, which is exactly the class of confusion
this whole entry is about. The best case is ~1.75×, against the edge mesh's 1.7× from a control that
already exists and behaves monotonically. D3's "exactly three controls" stands.

**Left alone, deliberately.** The "Narrowest conductor dimension 360 µm, meshed 1 cell(s) across
(target 4)" note pairs the measured narrowness with `MinCellsAcrossRun`, which is the minimum run
over the *whole* artwork — on conformal or staircased curved metal a run of 1 comes from a boundary
tip, not from the narrowest conductor. `PlanarMeshReport`'s own parameter doc already calls that out
as honest information rather than a defect, so it is recorded here and not changed.

**Gates.** `MeshFrequencyTests.WhereTheCapDoesNotBind_TheNoteSaysSo_AndNamesTheKnobsAsInert`
reproduces the complaint (identical N at 1 and 10 GHz on a 360 µm line) and pins both wordings;
`PlanarMeshOverlayTests.Panel_MeshBuildsTheSurfaceMesh_…` pins the summary's ratio to
`MaxCellEdgeM`.


## Cells across the conductor becomes a user control (2026-09-09)

Same owner report as the entry above, one step on: having been told the wavelength knobs were inert,
the answer to "then how do I get a coarser mesh" was for a long time "you cannot". The transverse
pitch is `min(λ_g/CellsPerWavelength, narrowest/MinCellsAcrossConductor)` and the second term was a
compile-time constant of 4.

**Owner's decision: mesh density is the user's responsibility. A tool that will not give a bad mesh
when one is asked for is deciding on the user's behalf. Warnings are allowed; refusals are not.**
So `PlanarMeshSettings.MinCellsAcrossConductor` is an ordinary setting (default 4, `.cem` under the
omit-at-default rule, a panel field, and a term in `EmSnpProvenance.MeshHash` appended only when off
its default so no existing `.snp` reads as stale). 1 is reachable. Nothing clamps above 1 and nothing
refuses.

**What was measured first, and what it changed.** On a 200 µm × 10 mm FR-4 trace, de-embedded, S21
moves under 0.001 dB from 1 cell across to 8 — **accuracy is not what 4 was buying**. But the cell
count is **not monotonic** in it: 8 → 220 cells, 6 → 200, 4 → 180, 3 → 160, 2 → 180, 1 → 200.

**The cause was mis-diagnosed first, and the correction matters.** The original write-up blamed the
edge fan legitimately spending back what the bulk saved. Challenged and refuted: re-measured per axis
on one rectangle, the y-steps at 1 across read `5.94, 17.87, 53.61, 87.13, 5.94, 5.94, 5.94, 5.94,
5.94, 5.94` — the fan grades correctly away from the NEAR edge and **collapses into six uniform
finest-size cells approaching the FAR edge**. That is the `BuildGridLines` marcher defect recorded in
`brief-em-transmission-line-mesh.md` §0d (the half-step look-ahead is clamped to the interval end,
where the size field is `c0`), and coarsening the bulk pitch lengthens the collapsed run. With a
Lipschitz-consistent marcher the same ladder is **monotonic and cheaper at every rung**: 264 / 220 /
198 / 176 / 154 / 154. A straight line DOES coarsen monotonically once the marcher is right.

**Fixture trap found while re-measuring:** `PlanarMeshSettings.Default with { EdgeMesh = false }` is
inert — `Default` has `Auto = true`, and `Resolved` collapses Auto to the default edge mesh. Any
fixture varying the edge mesh must set `Auto: false`. `MinCellsAcrossConductor` survives Auto by
design, so it takes effect either way; a real `.cem` carries `Auto: false` and is unaffected.

That did not stop it shipping; it decided what the mesher REPORTS. Off its default the report always
states the pitch the setting produced; lowered with the edge mesh on it says the count may not have
fallen, names the grading defect as the reason, and names the edge mesh as what removes the fan; at 1 it states plainly that a rooftop spans a cell PAIR, so a
one-cell-wide conductor carries no transverse basis at all and the edge singularity is unresolved —
**reported, not refused**, because a line whose current is uniform across it is a legitimate thing to
ask for.

**Together with the edge mesh it is decisive.** On the reported connector cutout:

| across | edge mesh on | edge mesh off |
|---|---|---|
| 4 (default) | 1,997 cells / 3,835 N / Warn | 1,171 / 2,215 / Ok |
| 3 | 1,141 / 2,157 | 651 / 1,211 |
| 2 | 1,374 / 2,608 | 341 / 614 |
| **1** | 1,385 / 2,621 | **113 / 182** |

1,997 → 113 cells, 17.7×.

**A trap for anyone writing a test here:** do NOT assert that lowering it lowers the cell count. The
first version of the engine test did, and failed — with the edge mesh on, this fixture goes 10 grid
lines to 11 when the pitch is coarsened 4×. Assert the PITCH (or run with the edge mesh off, where
the pitch governs directly).

**It survives `Auto`**, unlike cells/λ and edge cells, and the argument is the owner's rather than
the taxonomy's: it IS a resolution, so the taxonomy would have Auto reset it, but a number the user
typed being discarded because a checkbox is ticked is the silently-ignored-setting failure the
`BoundaryCells` and `MeshFrequencyHz` paragraphs in that file already exist to prevent.

**D3's "exactly three controls" is now six**, and `PlanarMeshSettings`' class comment says so
explicitly rather than continuing to claim three. D3's REASONING still governs what may be added: a
control earns its place by being a modelling or responsibility decision that is the user's to make.

**Still open, deliberately:** whether the DEFAULT moves from 4 to 3 (cheapest rung on both fixtures,
inside §10.5's own 3–5 range) — that moves every recorded number and is a separate act. Written up as
M1 of `docs/sonnet-briefs/brief-em-transmission-line-mesh.md`, which also carries the two other
findings from this investigation: the mesh is **already anisotropic** (60:1 on a 200 µm × 50 mm
trace, so "rectangular cells" is not a new capability — DIRECTION is what is missing), and a real
grading defect in `BuildGridLines` (**110 consecutive 3 µm cells** at the far end of a 50 mm trace,
because the marcher's half-step look-ahead is clamped to the interval end where the size field is
`c0`). A Lipschitz-consistent marcher fixes it — 184 → 79 grid lines — but it moves de-embedded S21
by 0.063 dB, so it needs a convergence study before it can ship. Both are in that brief.

**Gates.** `MeshFrequencyTests.CellsAcrossTheConductor_SetsTheTransversePitch_AndIsNeverClampedOrRefused`
and `MeshFrequencyUiTests.MinCellsAcross_RoundTrips_OmitsAtItsDefault_AndMovesTheStalenessHash`.


## M0 — the grading marcher crawled into the far end of every interval (2026-09-09)

`brief-em-transmission-line-mesh.md` M0. The biggest cell-count finding of that whole investigation,
and it is a defect rather than a control: **a graded fan silently stopped grading whenever it
APPROACHED an attractor, and became a uniform run at the finest cell size instead.**

### The mechanism

`BuildGridLines` graded through `BoundaryMesher.PartitionFractions`, whose step is a half-step
look-ahead **clamped to the end of the interval**:

```
s = min( h(x), h(min(length, x + s/2)) )
```

The far end of an interval is very often an attractor — a conductor edge is both a hard gridline and
an edge-mesh attractor — and the size field AT an attractor is `c0` by definition. So the first step
whose look-ahead reaches the end reads `c0`, and so does every step after it. With the derived growth
ratio at its 3.0 ceiling (r − 1 = 2) the clamp fires the moment the remaining distance falls below
half a cell, which on a graded fan is immediately.

The NEAR end always graded correctly, because the field GROWS away from an attractor and the
look-ahead therefore never bound. **Only the approach failed** — which is why nothing caught it: a
test written on the first cells of an interval passes throughout.

Measured, 100 µm × 50 mm FR-4 at 10 GHz, cells/λ = 20: **184 gridlines in x, of which 110 were
consecutive 3 µm steps over the last 330 µm.** The near end read 3, 9, 27, 81, 243, 714 µm — a
textbook ratio-3 fan — and then the far end read 714 µm followed immediately by 6 µm, with nothing
in between.

### The fix — closed form, no look-ahead, no clamp

`SurfaceMesher.PartitionGraded`. The field `h(x) = c₀ + g·d` is Lipschitz in `g`, so the largest step
whose own far end the field still admits can be SOLVED for:

```
for each attractor at distance d AHEAD of x:
    s ≤ (d ≤ c₀)  ?  c₀  :  (c₀ + g·d) / (1 + g)
s = min(h(x), those)
```

The second branch is the `s` satisfying `s = h(x + s)`. Once `d ≤ c₀` that formula returns MORE than
`d` — the step crosses the attractor — and the binding constraint becomes the field's own floor, so
the branch is `c₀` there. The two agree at `d = c₀`, so the constraint is **continuous**, which is
what preserves the translation invariance the continuous size field was chosen for in the first place
(see `Mesh`'s growth-ratio derivation for the knife edge a discontinuity introduced last time).
**Attractors BEHIND need no term at all** — stepping forward only increases their distance.

Same rescale, same `minCells` floor, same `(0,1]` fraction contract as the marcher it replaces.
**`BoundaryMesher.PartitionFractions` is untouched** — it is kernel A's cross-section mesher and every
kernel-A number in `HISTORY.md` sits on it.

On the same trace: **184 → 79 gridlines**, tail reading 714.6, 232, 77.5, 25.8, 8.6, 2.9 µm.

### What it does to cell counts — and it is NOT uniformly cheaper

| fixture (FR-4, 10 GHz unless stated, cells/λ = 20, edge mesh on) | before | after |
|---|---|---|
| 100 µm × 50 mm | 1,656 cells / N 3,119 | **711 / 1,334** |
| 100 µm × 50 mm, cells/λ = 10 | 1,314 / 2,473 | **414 / 773** |
| 200 µm × 10 mm | 180 / 331 | 198 / 365 |
| 2.9 mm × 20 mm (hero) | 297 / 552 | 297 / 552 — unchanged |
| 2.9 mm × 20 mm at 20 GHz | 708 / 1,345 | 720 / 1,368 |
| 72 µm × 2 mm GaAs at 20 GHz | 414 / 773 | **162 / 297** |

**The brief's own sentence "monotonic and cheaper at every rung" is wrong about the second half, and
its own table said so** — 264/220/198/176/154/154 against 220/200/180/160/180/200 is MORE cells at
8, 6, 4 and 3 across. The correct statement is: a properly graded down-fan costs more cells than the
old "one big step, then two crawl cells" wherever the interval was too short for the crawl to run,
and dramatically fewer wherever it was long enough for the crawl to dominate. **Long thin artwork —
PCB traces, which is the geometry the brief is about — is entirely the second case.**

### Two non-monotonicities closed, and they were ONE bug

**`MinCellsAcrossConductor`** (200 µm × 10 mm, across = 8/6/4/3/2/1):

| | 8 | 6 | 4 | 3 | 2 | 1 |
|---|---|---|---|---|---|---|
| before | 220 | 200 | 180 | 160 | **180** | **200** |
| after | 264 | 220 | 198 | 176 | 154 | 154 |

**`MeshFrequencyHz` on a narrow conductor** — recorded in `CLAUDE.md` §6 as a measured negative
result ("on a narrow conductor, lowering the mesh frequency RAISES N"). 72 µm × 2 mm GaAs, sweep top
20 GHz, N at f_mesh = 20 / 10 / 5 GHz:

| | 20 GHz | 10 GHz | 5 GHz |
|---|---|---|---|
| before | 773 | 705 | **2,014** |
| after | **297** | **229** | **212** |

**That was never the edge fan legitimately spending back what the bulk saved** — the explanation both
CLAUDE.md and the test carried. It was this marcher: coarsening the bulk pitch widens the gap the
crawl has to cover, so the wasted cells grow. Both ladders are monotonic now.

### The accuracy question — measured once, in a scratch harness, and it is decisive

The brief flagged that the marcher moves de-embedded S21 by 0.063 dB on the 200 µm × 10 mm fixture at
2 GHz, and hypothesised that the OLD mesh — carrying 110 extra cells right at the port, where the edge
singularity is — might be accidentally MORE accurate there. **It is not.** Convergence study, same
fixture, de-embedded, mesh sized at 10 GHz, dense path:

| cells/λ | N before | S21 dB @2 GHz | N after | S21 dB @2 GHz |
|---|---|---|---|---|
| 20 | 331 | **−2.5523** | 365 | **−2.4908** |
| 40 | 841 | −2.4983 | 586 | −2.4940 |
| 80 | 1,164 | −2.4974 | 1,045 | −2.4956 |
| 100 | 1,402 | −2.4973 | 1,266 | −2.4900 |
| 120 | 1,572 | −2.4957 | 1,504 | −2.4893 |
| 160 | 1,997 | −2.4958 | 1,980 | −2.4908 |
| 200 | 2,456 | −2.4951 | 2,456 | −2.4910 |

Both sequences settle inside a ±0.005 dB band around ≈ −2.493, which is this fixture's own mesh
sensitivity at that refinement. **At the SHIPPING mesh the old marcher is 0.057 dB outside that band
and the new one is inside it** — as |ΔS| against each sequence's own finest rung, **8.2e-3 before,
1e-4 after**, and the 8.2e-3 is past this kernel's own stated de-embedding accuracy (~6.0e-3 at
10 GHz, §5). Same story on S11 @2 GHz (−3.6348 before vs −3.7188 after, against a limit of ≈ −3.712).

**At 10 GHz the hypothesis is not refuted, merely small**: limit ≈ −2.190, old reads −2.1777
(|ΔS| 1.3e-3) and new −2.1693 (|ΔS| 2.9e-3), so the extra port cells DO buy something there — about
0.008 dB, an order below what they cost at 2 GHz, and both are inside 6.0e-3.

Read per unknown rather than per rung the answer is not close: the new marcher reaches its converged
value at N = 365, and the old one is still 0.007 dB out at N = 841.

**cells/λ = 240 and above cannot be measured on the dense path** — de-embedding's own calibration
standard needs 5,720 unknowns there and is refused by `UnknownCeiling`. 200 is the finest rung.

### What MOVES, and has not been re-measured

Every kernel-B number taken on a mesh with the edge mesh ON is potentially affected; the fixtures
above are the ones measured. Specifically **`CLAUDE.md` §5's mesh-frequency table** (FR-4 hero
1–20 GHz: N = 1,345 / 552 / 348 with worst |ΔS| 2.97e-3 / 1.50e-2 / 1.58e-1) was taken on the old
marcher — its N column is now 1,368 / 552 / 314 and **its |ΔS| column has not been re-run**, because
that measurement is `MeshFrequencyAccuracyTests`, `Category=Benchmark`. The qualitative conclusion it
supports (halving is defensible, quartering is not) is not in doubt; the digits are stale.

**A mesh with the edge mesh OFF is bit-identical** — no attractors ⇒ `BuildGridLines` takes its
ungraded branch ⇒ the marcher is never reached. Asserted on three fixtures against counts pinned from
the pre-M0 tree.

### The named trap

**A marching mesher's step must be consistent with the field at the step's OWN far end, and a
look-ahead clamped to an interval boundary is not that.** The clamp reads the field at a point the
step does not reach, and where that boundary is itself an attractor it reads the field's global
minimum. The failure is invisible in every observable a mesher publishes — the tiling is exact, the
cells are valid, the solve is smooth — and shows up only as a cell count that will not fall when the
pitch is coarsened. If a grading knob ever again "does nothing" or "does the opposite", measure the
STEPS along one axis before believing any explanation about fans trading against bulk.

### Gates

`tests/Engine.Tests/Mom/MeshGradingTests.cs`, 15 tests, **29 ms**, no solve anywhere:
no collapsed run · every adjacent cell pair inside the derived growth ratio (the fan grades
approaching an edge as well as leaving one) · cell count non-increasing over the cells-across ladder ·
translation invariance at 3.7 mm in coordinates as well as counts · bit-identity to the pre-M0 mesh
with the edge mesh off. **9 of the 15 fail on the pre-M0 marcher**, verified by pointing
`BuildGridLines` back at it and running them — a gate nothing can fail is not a gate.


## M2 — the transmission-line mesh: the two settings become orthogonal (2026-09-09)

**Owner report, made three times before it was understood, and the third time was the one that
landed.** Lowering *Cells per wavelength* did not reduce the cell count. Lowering *Mesh frequency*
did not reduce it either. Not "reduced it less than expected" — **did not move it at all, at any
value.**

### The cause, and it is one line of arithmetic

```
hx = min(λ_g/CellsPerWavelength, narrowX/MinCellsAcrossConductor)
hy = min(λ_g/CellsPerWavelength, narrowY/MinCellsAcrossConductor)
```

**The SAME min in both axes.** On any artwork whose metal is narrower than a λ cell the geometry term
wins in *both* directions, and the two wavelength controls are then structurally inert — nothing the
user types can reach the mesh.

Reproduced on the owner's own `.cem` (a connector cutout, 6.07 × 1.96 mm, 370 µm narrowest in x and
360 µm in y, one polygon with a right-angle bend in it, only **5** hard/attractor grid lines per
axis so the count really is the bulk pitch):

| Transmission line | cells/λ | mesh frequency | grid | cells | N |
|---|---|---|---|---|---|
| off | 20 | 10 GHz | 77 × 39 | 1,838 | **3,521** |
| off | 20 | 100 MHz | 77 × 39 | 1,838 | **3,521** |
| off | 10 | 10 GHz | 77 × 39 | 1,838 | **3,521** |
| off | 5 | 10 GHz | 77 × 39 | 1,838 | **3,521** |
| off | 5 | 100 MHz | 77 × 39 | 1,838 | **3,521** |
| **on** | 20 | 10 GHz | 49 × 39 | 1,052 | **1,971** |
| **on** | 10 | 10 GHz | 45 × 38 | 920 | **1,715** |
| **on** | 5 | 10 GHz | 44 × 38 | 888 | **1,652** |
| **on** | 20 | 100 MHz | 44 × 38 | 889 | **1,652** |

The setup shipped with `CellsPerWavelength: 5` **and** `MeshFrequencyHz: 100 MHz` — both knobs already
at the floor. λ_g/5 at 100 MHz on that stack is ~0.37 m against 360 µm of metal: four orders of
magnitude apart, so the λ term could never bind. **A note already said so** (the non-binding-λ note,
shipped 2026-09-09). A note about a dead control is not a working control.

### The fix is the owner's own sentence

> "The 2 settings can be orthogonal to each other. For a transmission line running east-west, the
> `narrowest_metal / CellsAcross` setting is a north-south setting and the `CellsPerWavelength` is an
> east-west setting. It doesn't have to be one or the other."
> "CellsPerWavelength should always be in the direction of the current."
> "Make sure to follow the bends of the geometry to guess which way the current is going."

That is also the physics. **Across** a line the current carries the 1/√d edge singularity and the
width must be resolved. **Along** it the current varies on the scale of a wavelength, so
λ_g/CellsPerWavelength is the whole requirement, and taking a min with the narrowness there refines
for a singularity that is not present.

`PlanarMeshSettings.TransmissionLineMesh` (default **off**; `.cem` under the omit-at-default rule, a
panel checkbox, one undo entry, and a term in `EmSnpProvenance.MeshHash`).

### Why it is a FIELD and not one angle — "follow the bends"

D8 is one tensor-product grid and a cell cannot rotate. **But the grid lines need not be uniform**, and
that is enough: an east-then-north L-bend wants a coarse x pitch over its horizontal arm and a fine
one only where the vertical arm stands, and the transpose in y. Both are an ordinary non-uniform
tensor grid. So every point of the metal states what it needs in x and in y, and a column takes the
finest need in it (`PlanarMeshPitchField`).

Nothing about `PlanarCell.IX/IY`, the rooftop cell pair, or `RectangleIntegrals`' axis-aligned closed
forms is touched. Route C — cells that actually rotate — remains a second mesher and is not this.

**The direction at a point is the direction of the LONGEST local chord**, and the width is the chord
across it. The first version took the *shortest* chord as the transverse direction, which is right in
the middle of a strip and catastrophic near a rim: a scan line nearly tangent to an edge cuts a chord
one sampling step long, so the "width" collapsed to ~1 µm and a plain 10 mm line meshed at **2.27
million cells**. A setting turned on to make the mesh smaller made it 11,000× bigger.

### Three guards, each of which was needed because it failed first

1. **It may only COARSEN.** Every pitch is floored at the one the per-axis rule would have used. A
   local width is a sampled quantity and must never be allowed to drive the mesh finer than a direct
   measurement of the polygons.
2. **The floor is applied BEFORE the aspect cap, and the order is load-bearing.** Capping the along
   pitch at 64× an *unfloored* rim-sliver width pinned it to 476 µm against a λ cell of 715 µm — so
   *Cells per wavelength was still inert with the setting on*, which is the entire bug.
3. **`minCells` is 1 on the field path.** `PartitionGraded`'s floor works by *discarding* the marched
   partition for a uniform one. Any single number computed from a varying field is wrong somewhere:
   the finest value re-subdivided the whole interval at the narrowest neck (65 cells where the field
   asked for 20, i.e. exactly the mesh the setting was turned on to avoid), and the field's integral
   over-counts. The enforcement pass already guarantees the cap cell by cell against the field's own
   value, so the floor is redundant rather than merely awkward. **This was the single biggest of the
   three** — it alone took the owner's file from 3,235 to 1,971.

The field is **Lipschitz-smoothed** at the edge fan's own growth ratio, and that is not optional: a
column-wise minimum is a step function, and L8b already paid for a discontinuous size field once
(moving the same rectangle 3.7 mm changed the mesh by 33%).

### The honest cost: the edge fan grows

**It is asserted on the PITCH, not on the cell count**, and the difference is a finding. A coarser bulk
gives the graded fan at every conductor edge further to climb, and the fan's ratio is clamped at 3×
per cell, so each attractor costs about log₃(coarser/finer) extra cells. On artwork that is mostly rim
and hardly any bulk — an 8-segment taper — that outweighs the bulk saving and the count rises
slightly: **728 → 858 / 805 / 741 at cells/λ 20 / 10 / 5**. Every cell is the same size or larger; there
are simply more fan cells. The mesher says so in its own note whenever the bulk pitch spans more than
3× across the artwork, and names the edge mesh as what removes the fan.

### The ports are a CHECK, not the source — a deliberate reversal of the brief

The brief (and the owner's first suggestion) took the direction FROM the port orientation, and for a
straight line that is exactly right. **It cannot survive "follow the bends":** two ports state one
direction between them, and an east-then-north bend's two ports state 45° — the direction of neither
arm. Its area moment says 45° too, so the two agree and agreement proves nothing. So the metal is
measured per point, and the ports are used only to report a *disagreement*: a port sitting on a face
the local field says is along the current rather than across it means one of the two is wrong, and the
user is the one who can tell which.

`SurfaceMesher.Mesh` takes the ports as an optional trailing argument, and **`PlanarKernel.Mesh` and
the EM panel both pass them** — the panel's pre-solve unknown count and the run's must not disagree,
which is exactly the defect P12 fixed for the accelerated ceiling.

### Gates

`tests/Engine.Tests/Mom/TransmissionLineMeshTests.cs`, 10 tests, **2 s**, no solve anywhere. The first
two are the bug itself, written to fail if the two settings ever go back to competing — including an
**equality** assertion that the knob is inert with the setting off, so if that ever stops being true
someone is told rather than left with a pointless control. Then: it only ever coarsens the bulk cell ·
off is bit-identical, ports or no ports · it survives `Auto` · the aspect cap binds on the field ·
translation invariance at 3.7 mm · and the bend test, which asserts **each arm keeps its own
transverse resolution** rather than a cell count, because that is the property "following the bend"
actually means.

**A fixture note worth keeping:** a straight x-directed line does NOT show this defect — it is 10 mm
long measured along x, so `narrowX/4` is 2.5 mm and the λ cap does bind in x. The bug needs artwork
narrow in **both** axes, which is why the fixture here is a right-angle bend. A 10 mm line is also
fan-dominated (about 10 of its 15 x lines), so the bulk pitch cannot show through it at all; the
orthogonality test uses 50 mm.

---

## M4 — the LOCAL edge reference length (2026-09-09)

**Owner report, the fifth round of one complaint**, and the first four rounds answered the wrong
half of it. The complaint, in the owner's own terms: there is a floor on the cell count that no
setting reaches; 1 cell across the conductor still renders more than 20 across the wide metal; a
mesh frequency of 0.001 GHz still renders more than 10 cells along the current. And, once the file
was measured: *the narrow trace's edge mesh continues into the wider transmission line even though
it is not needed for accuracy there.*

### What the owner's own file measured

The owner's own connector-cutout `.cem` (a top-to-inner transition), driven headlessly through
`EmSetupResolver` + `PlanarExtractor` + `SurfaceMesher` (a scratch console, not a test):

| setting | cells |
|---|---|
| as saved (TLM on, edge on, across = 1, cells/λ = 2, f_mesh = 1 MHz) | **513** |
| cells/λ 2 → 5 → 10 → 20 | 513 / 513 / 513 / 513 |
| f_mesh 1 MHz → 1 GHz → 5 GHz → 10 GHz | 513 / 513 / 513 / 520 |
| **edge mesh OFF** | **71** |
| across 1 → 2 → 4 → 8 | 513 / 630 / 881 / 2,028 |

**Three separate facts, and only one of them was a defect.**

1. **The two λ knobs are genuinely inert here and the mesher is right to say so.** At 1 MHz
   λ_g = 143 m against a 6.065 × 1.963 mm part — **4.2e-5 λ_g across**. Nothing about that is
   fixable; wavelength is five orders of magnitude from binding.
2. **"1 cell" is unreachable, and not because of any setting.** A tensor grid must carry a gridline
   on every axis-parallel conductor edge or the metal is not where it was drawn. This artwork
   contributes **4 distinct vertical-edge X coordinates and 9 horizontal Y** — a floor of 4 × 9
   lines before any control is consulted. Measured floor with every knob at its coarsest: 71 cells.
   (It also carries two **5 µm slivers** — hard Y lines at 848/853 µm and 1213/1218 µm — which pin
   `MinCellEdgeM` at 5 µm no matter what. That is artwork, not meshing.)
3. **The 513 was the EDGE FAN, essentially in its entirety**, and that is what nobody had measured
   in four rounds. Dumping the attractors settles it: all 4 X attractors and 5 of the 9 Y attractors
   raise a fan, each ~6–7 gridlines wide (10.6, 31.8, 95.3, … / 10.8, 32.3, 96.9, 290.7, 872.1 µm),
   and 4 × 7 ≈ 29 against a `gridX` of 30. **The bulk pitch contributes a handful of lines; the fans
   contribute the rest.**

### The defect, stated exactly

`EdgeReferenceLength` returned **one scalar for the whole layout** — 3% of the narrowest conductor
*anywhere* — and that one cell was then placed at **every** conductor edge. So a single narrow
feature makes the fan at every wide trace's rim ten times finer than that rim needs, and a fan's
length is `log_r(bulk/c₀)` cells, so the surplus is spent on gridlines that **cross the entire
tensor grid**. On the owner's file: 3% of 360 µm = 10.8 µm applied to metal 3.6 mm wide.

`PlanarEdgeReference.CellSize` is not an escape — it measured the identical 513, because with the
transmission-line mesh on `min(hx, hy)` is pinned to the same global minimum.

### The fix

**`PlanarEdgeReference.LocalConductorWidth`**, now what `PlanarKernel.Mesh` and the EM panel's
preview both pass. Each attractor carries its own `c₀`, 3% of the metal measured **perpendicular to
that edge, at that edge** (`SurfaceMesher.LocalWidthAt`, `EdgeAttractor`, `GradedAttractor`).

Three things about it are load-bearing:

- **The grading rate `g` is UNCHANGED** — still derived from the global narrowest conductor. The
  size field is still `h(x) = min_i [c₀_i + g·|x − a_i|]`; only its floors move. A single `g` is
  also what keeps the field Lipschitz at one rate, which is what L8b's translation invariance rests
  on.
- **Every `c₀_i` is floored at the global `c₀`, so the mode may only COARSEN.** `h_local ≥ h_global`
  pointwise ⇒ the cell count is bounded above by `ConductorWidth`'s, structurally. This matters
  because a local width is a **sampled** quantity: without the floor a sampling artefact on unseen
  artwork could refine the mesh of someone who reached for the setting to shrink it. Same invariant,
  same reason, as `PlanarMeshPitchField`'s own floor.
- **The width is `min(perpendicular run, the edge's own length)`, and the second term is the
  end-cap case rather than a guard.** On a W × L patch the two side edges' perpendicular run is W —
  the scale the 1/√d crowding decays over, and correct. The two END CAPS' perpendicular run is L,
  and taking it would size a long line's end-cap fan on the line's *length*; the crowding there is
  set by the width. `min` is W in both cases and needs no test for which kind of edge it is.
  Sampled at 5 interior points and reduced by **MEDIAN, not minimum** — a rim carries notches,
  treads and corner slivers, and the minimum lands in one of them.

Measured on the owner's file: **513 → 420** as saved, **641 → 540** with the transmission-line mesh
off, **1,829 → 1,698** at the shipped defaults. A uniform line is **bit-identical**, as it must be.

### Two traps this hit, both caught by its own gates

- **A new enum value falls into the WRONG BRANCH of a two-way `?:` and nothing says so.**
  `EdgeReferenceLength` read `kind == ConductorWidth ? narrowest : cellSize`, so
  `LocalConductorWidth` silently took the **CellSize** arm — deriving `g` from a `c₀` four times
  finer, and making a **uniform line mesh differently from itself** (198 → 176 cells) when a uniform
  line has no local widths to find. It is now written as `kind == CellSize ? cellSize : narrowest`,
  so the DEFAULT arm is the geometry one. The gate that caught it is
  `OnAUniformLine_TheLocalReferenceChangesNothing`, which exists precisely because a uniform line is
  the one fixture where the answer is knowable in advance.
- **A pointwise coarser FIELD does not give pointwise coarser CELLS.** `PartitionGraded` rescales
  each interval to land exactly on its endpoints (`xs[i] * length / last`), so a coarser field can
  leave a slightly shorter remainder cell against a hard gridline — measured at 4% below the global
  mesh's finest cell. The **count** is the invariant; `MinCellEdgeM` is not, and the gate says so
  with a 10% band rather than pretending otherwise.

### What it does NOT do, and cannot

**It does not stop a fan crossing the part**, which is the other half of what the owner described.
An x-attractor refines a **column over the full height of the grid** — that is what a tensor product
is. Localising it in the other axis means merging cells back afterwards, which produces T-junctions,
which the rooftop basis (a pair sharing a cell edge) and `RectangleIntegrals`' axis-aligned closed
forms do not admit. That is a second mesher, not a setting. This makes each fan **shorter**; it does
not make it **narrower**.

**The user's own remedy is still the large one on this file: the edge mesh off, 513 → 71.** And it
is the consistent setting for what was asked — 1 cell across already means no transverse basis
function and no edge singularity resolved, so paying 442 cells for a fan that resolves it anyway is
buying nothing.

### The notes, and why they were part of the defect

The mesher **had been saying** cells/λ and mesh frequency were inert on this artwork, and that the
edge mesh was the one live control. Nobody read it: it was one clause in the middle of a 90-word
paragraph, under three other paragraphs. **Owner instruction: an engineer does not read a paragraph
in a side panel.** Every note in `SurfaceMesher` and `PlanarMeshPitchField` was cut to its numbers
and its remedy — the reasoning belongs in the source, which is where it now is exclusively — and the
panel's notes became **ONE `SelectableTextBlock` per list rather than an `ItemsControl` of them**,
because an `ItemsControl` selects one LINE at a time and copying a mesh report out meant dragging
each note separately. `NotesText` / `MeshNotesText` / `PlanarMeshNotesText` join with a blank line.

**Diagnosis that is built and then not read is the same defect as diagnosis that is not built.**
This one cost four rounds.

## The via electrical-length refusal names the frequency that would pass (2026-09-09)

An internal port on a 355.6 µm stackup was refused at a 20 GHz sweep top: k·ℓ = 0.3127 against
`PlanarLevels.MaxElectricalLength`'s 0.30. The refusal explained the bound thoroughly and then said
only "Lower the sweep's top" — leaving the reader to find the number by bisecting whole EM runs, with
nothing on screen saying whether the geometry was 4% over the line or 400% over.

k is proportional to frequency, so the answer is a plain ratio and exact: `CheckOne` now takes the
sweep top the wavenumber was computed at (`PlanarKernel.MidpointRuleVerdict` already had it) and
appends "(to 19.19 GHz or below, from 20 GHz)". Zero omits the phrase, which is what a unit test
constructing a bare wavenumber wants.

The other two remedies are unchanged and are the ones to reach for when the sweep top is not
negotiable: split the via across intermediate meshed levels, or make the span shorter — for a
ground-attached port, that means a ground-designated conductor closer to the signal level, since the
span is the stackup's own.

## ANT-2 — the detail floor, and an edge fan whose grading rate is local (2026-09-10)

`brief-antenna-2-mesh-detail-floor-and-edge-fan.md`. Two changes to `SurfaceMesher`, neither
antenna-specific: both fix every **imported** board, which is where sub-wavelength artwork comes
from. The antenna-specific mesh change is ANT-3.

### What was wrong

An imported patch board asked for **704,482 unknowns** on the default mesh. Two of its three causes
are the general ones:

1. **The narrowest metal was 310 µm of connector via land and aperture-rounded corner** — λ_g/265 on
   that board, sitting ~9 mm from anything electrically interesting. Every width measurement in the
   mesher (`MeasureNarrowness`'s global 5th percentile, `PlanarMeshPitchField`'s local across-chord,
   `LocalConductorWidth`'s per-edge run) answers *how narrow is the metal* and **none of them had any
   notion of a feature being too small to matter electrically**. Deleting only those features moved
   the same mesh to 51,031 — a **14×** swing bought by geometry no field responds to.
2. **The edge fan was 61 % of the mesh on a patch** (12,596 → 4,854 when the edge mesh went off), and
   on an antenna switching it off is not available: the radiating edges set the resonant frequency.
   `LocalConductorWidth` had already given each attractor its own `c₀`; its own doc comment named the
   two things it did not change, and the first of them is the whole of this: *"the grading rate g is
   unchanged — it is still derived from the global narrowest conductor."*

### M1 — the detail floor

`PlanarMeshSettings.DetailFloorDivisor`, the **eighth** control, **default λ_g/200 and ON**. Metal
narrower than λ_g ÷ this does not get to drive the cell pitch. It is applied in three places and it
had to be all three or it is only a third made: the global narrowness measurement (**per SHAPE, before
the minimum** — which is what lets a 310 µm land stop sizing the mesh while a wider conductor beside
it goes on being measured as drawn), the pitch field's local across-chord, and each attractor's own
per-edge width.

**It also suppresses the FAN of an edge shorter than the floor, and that is where nearly all of the
win is.** A feature the mesher has just decided is not worth sizing the mesh on was still asking for
four graded fans, and a fan is charged across the whole tensor grid. **Only the fan goes — the hard
gridlines stay**, so R-msh-1's exact tiling is untouched and the feature is still meshed. This is
`CapMinFractionOfExtent`'s own argument (an edge the mesh cannot represent earns no fan) restated in
wavelengths instead of in fractions of a bounding box, which is the only form of it that means the
same thing on a 1.7 GHz board and a 40 GHz one.

- **λ-relative, never absolute.** An absolute number in µm is a different decision at 1.7 GHz and at
  40 GHz and would be wrong at one of them. Taken at the same λ_g the cell-size cap uses, at the same
  `MeshFrequencyHz`. **With no sweep frequency there is no floor at all** and the report says so.
- **It floors and never refines**, so every field it touches is pointwise ≥ what it was and no
  configuration gets worse. Same invariant `LocalConductorWidth` already states for its own `c₀`.
- **It survives `Auto`**: Auto means *choose the resolution for me*, and which geometry is
  electrically real is not a resolution.
- **The report names the floor, how many shapes fell below it, and the pitch it displaced** — and
  says so explicitly when nothing fell below it, because "the floor is on and it changed nothing
  here" is the question a user reading the count will ask next.
- **`EmSnpProvenance.MeshHash` carries it UNCONDITIONALLY**, breaking that function's own
  omit-at-default rule on purpose. That rule is sound for a control whose default reproduces the
  older behaviour, which is every other one; **this is the first whose default is ON**, so a `.snp`
  stamped before ANT-2 describes a mesh built with no floor, and on any artwork carrying sub-λ_g/200
  detail that is a different mesh. Every pre-ANT-2 planar `.snp` reads as stale once, correctly.

#### It is capped at a fraction of the artwork's own extent, and that is not a guard

The floor's premise is that a feature is small **relative to the part**, so it has no meaning at all
when the part is itself orders of magnitude below a wavelength — and that is an ordinary structure
here, not a pathology. `MimThinLayerTests`' plates are **0.8 µm square at 10 GHz on GaAs**, where
λ_g/200 is 41.8 µm: fifty times the whole thing. Uncapped, the floor discarded every width on the
artwork and the mesh came back **empty** (`'rowCount' must be a positive value` out of the fill).
`CapMinFractionOfExtent` is already this file's constant for *one cell at the finest mesh this kernel
can afford*, and the argument carries over unchanged: a floor coarser than one such cell is not
declining to size on a detail, it is declining to mesh the part.

### M2 — a fan that is short because its grading rate is local

`GradedAttractor` carries its own `Growth`. The fan's length is `log_r(bulk/c₀)`, and `r` was one
global number derived against `min(hx, hy)` — which, **with the transmission-line mesh on, is the
FINEST pitch the field asks for anywhere**. So the fan was rated for a target it had already passed
and then had to climb to the actual bulk at that rate. On the fixture below, at the feed's own rim:
`c₀` = 10.5 µm against a 4.107 mm along-pitch is a climb of 391×, and at `r` = 2.05 that is **eight
cells** — eight gridlines each crossing 41 × 49 mm of artwork, for one conductor edge. Exactly the
"about eight graded steps" the brief predicted.

Each attractor now derives `r` from its **own `c₀`** against the **bulk cap where its own fan
starts**. Both `c₀` and `g` are floored at the global pair, which makes
`h(x) = min_i[c₀_i + g_i·|x − a_i|]` pointwise ≥ the old field — `h` is non-decreasing in `c₀_i`, and
in `g_i` wherever the field binds (`d ≥ c₀`) — so the count is bounded above by today's structurally,
not by hope. Measured on the same board with the floor off: **14,709 → 11,754 unknowns and a longest
fan of 6 → 4**, from this alone.

**With no pitch field this changes nothing at all, to the bit**: the local bulk *is* `hMax` there, so
every attractor derives the ratio the global one already had. Every number in `HISTORY.md` taken on
the per-axis rule is untouched, and `LocalEdgeReferenceTests`' pinned 198 still reads 198.

#### The bulk a fan is rated against is the cap WHERE IT STARTS, not the axis's coarsest pitch

Both were built and measured. Using the axis's coarsest pitch is worse twice over. It drives `r` to
its 3× clamp on any board where the two differ, so **the fan stops responding to
`MinCellsAcrossConductor` at all** — `TransmissionLineMeshTests.OnAStraightLine_TheTwoAxesAreGoverned
ByDifferentSettings` catches that as a y gridline count that will not move when Cells across does
(8 → 8). And it over-states the climb: a connector land's fan reaches its own ~80 µm neighbourhood in
three cells and never climbs to the 2.9 mm bulk out on the patch, but that reading called it eight.
The two variants mesh within 4 % of each other on the patch (11,754 against 11,305), so this is a
correctness-of-reporting and orthogonality choice rather than a cell-count one. The **reported** fan
length uses the same local bulk, for the same reason — a number describing a climb no fan makes is
the class of note this file keeps having to fix.

#### Neither of §4's secondary levers is here, and both were measured rather than skipped

- **Lever 1 — a λ-relative floor on the finest edge cell — cannot be sized.** The value that closes
  the patch board is **λ_g/540**, and that is **5× coarser than the legitimate 6 µm edge cell of a
  plain 200 µm × 10 mm line at 10 GHz**, whose bit-identity is one of this brief's own gates. No
  number does both, because the quantity that is actually wrong is the **ratio** `bulk/c₀` — 391 on
  the feed rim against 8.3 on the plain line — and not the absolute size of either.
- **Lever 2 — a fan-length cap — was BUILT as `c₀ ≥ bulk / MaxGrowthRatio^EdgeCells`, and removed.**
  Once the rate is derived against the real bulk it changes **not one cell** on the board this brief
  is about (11,754 / 5,371 / 4,048 across the floor ladder, identical with and without it), while it
  does couple `c₀` to `CellsPerWavelength` — through the bulk, which varies with cells/λ via the
  end-cap direction blend — and that is what broke the orthogonality gate. **A lever that buys nothing
  and costs a guarantee is not a backstop.**

#### So the overrun is REPORTED instead of hidden

`GrowthRatioFor` clamps `r` at 3, so a climb steeper than `3^EdgeCells` cannot be made in `EdgeCells`
cells and the fan simply runs longer — the 349.3 µm feed rim's own 10.5 µm edge cell against a λ_g/20
along-pitch of 4.107 mm is a 391× climb and takes 5. The mesher now says the number and why, rather
than coarsening the edge cell to make it come out right. The old `EffectiveEdgeCells` note is re-pointed at the realised fans for the
same reason: computed from `c₀` against `hx`/`hy`, it stated a length no fan on the artwork had.

### A regression this brief introduced, found by measurement and now gated

**`c₀` is ZERO when the edge mesh is off, and the per-edge branch computes `max(c₀, 0.03·w)` — which
is `0.03·w` even at `c₀ = 0`.** That had always been true and had always been harmless, because the
"is anything graded" gate read a single global growth rate (`ratio − 1` is negative there). A
**per-attractor** rate makes that gate read the attractors themselves, so **an edge mesh the user had
turned OFF came back on**: 40,952 unknowns with it off against 11,754 with it on — not merely wrong
but the wrong way round. Any change that moves a decision from a single scalar onto a list must
re-ask which of that list's fields are still meaningful when the control is off.
`DetailFloorTests.WithTheEdgeMeshOff_NoFanIsBuilt_UnderAnyReference`.

**And the gate is stated as "every reference gives the same mesh", not as a count comparison against
the edge mesh being on**, because that comparison is not an invariant: with the transmission-line
mesh on, switching the edge mesh OFF drops the pitch field's own Lipschitz rate to `MinGrowthRatio`
(there is no fan ratio left to borrow), which smooths the field harder and can legitimately produce
*more* cells. Pre-existing, and the mesher already says so in its own note.

### The defect in §7 — a refusal that named the one knob that works and told the user not to turn it

On the measured board the refusal said, in capitals:

> …the narrowest conductor run is 310.102 µm, and meshing it 4 cells across forces a 77.526 µm pitch
> over all 55610 µm × 49400 µm of the artwork … **LOWERING CELLS PER WAVELENGTH OR MESH FREQUENCY
> WILL NOT REDUCE THIS COUNT.**

while the pitch **field** it had just built spanned 78 µm to 3.485 mm, and lowering cells/λ was
exactly what took the same board from 23,416 unknowns to 1,909. The sentence is correct for the
per-axis rule and **false for the field** — a field's finest pitch is local to the narrowest neck,
and λ still sets the pitch along the current everywhere. It now branches on **which pitch rule
actually produced this mesh**, not on the settings, and the per-axis wording is untouched because
there it is true. The same sentence was live in two other places on the same page — the `capBinds`
note and the pre-grid `MaxGridCells` refusal — and all three branch the same way now. This is the
third time this class of defect has been found in this file (owner reports 2026-08-14 and 2026-09-09,
both recorded above); the rule it keeps breaking is **never name a remedy without asking whether it
binds**.

### Measured — the fixture, and the numbers

`brief-antenna-0-overview.md`'s board reconstructed from its own description (41.3 × 49.4 mm patch on
203.2 µm FR-4 εᵣ 4.4 / tanδ 0.02, a 349.3 µm inset feed, a rounded coax pad and eight 310 µm ground
via lands, 55.61 × 49.4 mm overall). **Not the owner's file** — that was supplied for the
investigation and is not in the repo — but it reproduces its headline number closely enough to be the
right thing to measure on: **744,545 unknowns on the default mesh against the reported 704,482.**

λ_g = 82.14 mm at 1.740 GHz, so λ_g/500 = 164 µm and λ_g/200 = 411 µm.

**Unknowns, ANT-2 applied, across the floor ladder:**

| configuration | off | λ_g/1000 | λ_g/500 | **λ_g/200** | λ_g/100 |
|---|---|---|---|---|---|
| default (TL off, 4 across, edge on) | 744,545 | 744,545 | 744,545 | **402,525** | 104,469 |
| **TL on, 4 across, edge on** | 11,754 | 11,754 | 11,754 | **5,371** | 4,048 |
| TL on, 4 across, edge off | 2,683 | 2,683 | 2,683 | **2,388** | 1,775 |

**What each half is worth, on the row a user actually runs** (TL on, 4 across, edge on):
**14,709 → 11,754** from M2's per-attractor rate (1.25×, longest fan 6 → 4; the baseline is the same
code with the rate forced back to the global one), then **11,754 → 5,371** from M1 at the shipped
λ_g/200 (2.19×, fan → 3). **2.74× together**, and 5,371 unknowns sits under the accelerated ceiling
of 12,000 where 14,709 did not.

M2 is worth comparatively little here and it is worth saying why: the count is dominated by the
*number* of fans rather than their length. The nine connector shapes raise **18 of the 23
x-attractors and 18 of the 24 in y**, and at the floor-off setting they account for **72 of the 150 x
gridlines and 62 of the 123 in y**. M1's fan suppression is what removes them.

**§3's convergence check — de-embedded |S₁₁| at the 1.740 GHz resonance** (one port, TL on, 4 across,
edge mesh off so that every rung is under the ceiling and the comparison is like for like;
accelerated solve, 8 frequencies 1.60-2.00 GHz, ~114 s a rung):

| floor | unknowns | \|S₁₁\| at 1.74 GHz | Zin |
|---|---|---|---|
| off | 2,802 | 0.7516 | 315.7 + 106.7j |
| λ_g/1000 | 2,802 | 0.7516 | 315.7 + 106.7j |
| λ_g/500 | 2,802 | 0.7516 | 315.7 + 106.7j |
| **λ_g/200** | **2,691** | **0.7527** | 314.7 + 110.5j |
| λ_g/100 | 1,858 | 0.7533 | 316.2 + 110.0j |

**The whole ladder moves the answer by 1.7e-3**, which is well inside this kernel's own de-embedding
residual (~4e-3 at 2 GHz on 1.6 mm FR-4, §5). **The connector detail is not electrically live**, which
is the answer §3 asked for and the opposite of the surprise it warned might come back. The first three
rungs are bit-identical because they are the same mesh: at λ_g/500 the floor is 164 µm against 310 µm
of connector detail and does not reach it.

**So the default is λ_g/200 rather than the λ_g/500 first written.** λ_g/500 is a default that does
nothing on the board it was written for. **λ_g/100 was measured and NOT taken**: it buys a further
1.4× and doubles the amount of genuinely drawn metal a floor may coarsen, on the evidence of one
board. λ_g/200 is 411 µm at 1.74 GHz on FR-4 and 10.4 µm at 40 GHz on GaAs, and floors nothing either
starter technology draws at a shipped resolution.

**On §4's own target row** — the brief's board with the connector already deleted, TL on, 4 across,
edge on — this is **2,227 before ANT-2, 2,114 with M2, and 1,164 at the shipped λ_g/200** (the floor
reaches the 349.3 µm feed there and takes its fan from 5 cells to 3), against **712** with the edge
mesh off. The brief asked for "near" edge-off and this is **1.63×**, not 1×.

**The remaining factor is not a defect and cannot be removed by shortening fans.** The five surviving
x-attractors are the feed's own end face, both patch edges and the notch — every one a real conductor
rim — and at `EdgeCells` = 3 each costs about three gridlines by definition. The base x grid on that
board is only 15 lines, because the transmission-line mesh makes the along pitch λ_g/20 over 55 mm;
five three-cell fans on a 15-line grid is the whole of the ratio. Going below it is asking for fewer
graded cells, which is `EdgeCells` and not a mesher change.

---

## ANT-3 — the SHEET intent, for metal that is a radiator rather than a line (2026-09-10)

`brief-antenna-3-mesh-sheet-intent.md`. Mesher-only, on ANT-2's own testing rule: no EM solve
anywhere, no de-embedded point. Gates: `tests/Engine.Tests/Mom/SheetMeshTests.cs` (11 tests, ~3 s)
and `tests/Ui.Tests/Em/SheetMeshUiTests.cs` (14 tests, 121 ms).

### What was wrong, and it was not a bug

`PlanarMeshSettings.TransmissionLineMesh` asks *which way is the current going* and **declines when
it cannot tell** — which is right, and ANT-3 does not weaken it. A wide radiating sheet is the case
it declines by construction, and it is also the case where the question does not apply: on a patch
the current varies on the scale of a wavelength in **both** directions, a half-cosine along the
resonant dimension and near-uniform across the width, with no singularity in the interior to
resolve.

A patch fails the direction test twice over. It is a one-port, so the port vector is unavailable and
the area moment is the only source; and the area moment of a 41.3 × 49.4 mm rectangle reports the
**longer side**, which on the measured board is perpendicular to both the feed and the resonant
dimension. That the directed mesh still helped enormously there is a measure of how bad the per-axis
rule was, not evidence that the direction was right.

### M1 — one three-way control, not a second boolean

`PlanarCurrentModel { None, TransmissionLine, Sheet }` **replaces** the `bool
TransmissionLineMesh` in `PlanarMeshSettings`, in place, at the same position. Two independent flags
that can both be true is a state nothing downstream could act on, and the two intents are mutually
exclusive by construction. `None` is the default, so every number in `HISTORY.md` stays reproducible.
It **survives `Auto`** on the settled taxonomy — `Auto` chooses a *resolution*, and this says what
the resolutions are FOR.

**The persistence migration is the part that is a decision.**

- The new key `PlanarMesh.CurrentModel` is written, omitted at its default, so a pre-ANT-3 `.cem`
  gains no byte and re-serialises byte-identically.
- The legacy `PlanarMesh.TransmissionLineMesh` is **read and never written**. Alone, `true` reads as
  `TransmissionLine` and `false` as `None`; a file carrying it comes back out on the new key, which
  is the whole of the migration.
- **A file carrying BOTH and disagreeing is a refusal naming both keys.** The obvious alternative is
  a precedence rule ("the new key wins") and it is the wrong answer for a reason this area has
  already paid for: a precedence rule means a user who set one control watched the other one silently
  win, which is the exact defect the transmission-line mesh was built to fix. Agreement is not a
  conflict and loads quietly.
- **`EmSnpProvenance.MeshHash` keeps the transmission-line term's exact BYTES** — `|tline=True` —
  rather than re-spelling it as the enum. That value's meaning did not change, only its name in C#,
  so an `.snp` stamped under the boolean must go on reading as current; a tidier
  `|model=TransmissionLine` would have marked every one of them stale for no reason a user could
  see. `Sheet` gets its own term, `|sheet=True`.

The panel's checkbox becomes a three-item combo, and the row reads **"This metal is …"** and
**every label names the STRUCTURE rather than the algorithm** — "Unstated", "Transmission line",
"Radiating sheet". Someone who knows they have drawn a patch can answer that; the same person has no
view on a pitch field. `None` is "Unstated" rather than "Off" for the same reason: nothing is
switched off, the question simply has not been answered.

### M2 — the field without a direction

Same `PlanarMeshPitchField`, same `FieldSamples` grid, same longest-chord width estimator, same
Lipschitz lower envelope at the edge fan's own growth ratio, same one-way floor at the per-axis rule.
The direction term drops out and nothing else does: per metal sample,
`h = max(acrossFloor, min(hWave, localWidth/MinCellsAcrossConductor))`, contributed to **both**
`needX[ix]` and `needY[iy]`.

Three things worth knowing:

- **The width estimator is REUSED, not replaced.** The temptation is to take the shortest chord now
  that there is no "along" — and it is the same trap L8b's own note records: a scan line nearly
  tangent to a rim cuts a chord one sampling step long, which collapsed the "width" to ~1 µm and
  meshed a plain 10 mm line at 2.27 million cells. The estimate does not become safer because the
  intent changed.
- **The aspect cap cannot bind, structurally.** Both axes are asked for the same number at every
  point, so the requested aspect is exactly 1:1 — `WorstAspect` is 1 and `Capped` is never true. On
  the same fixture the directed field asks for ~4,000:1 and is clamped to 64. What a realised CELL
  comes out at is still the product of two independent axis fields, which is the tensor product and
  is true of every mode here.
- **The port check is the directed mode's and only its.** It asks whether a port sits on a face the
  metal runs *into*, which is a question about a direction; asking it under `Sheet` would report a
  "disagreement" on every patch, about an answer nothing used.

It **never declines**, so the note is the only way a user sees that it acted — and the note carries
the three things §4 asks for by name: the bulk pitch in each axis, where the width floor bound, and
the worst aspect.

### "Where the floor bound" is a question about the OUTCOME, not the request

Written first as `localWidth/MinCellsAcrossConductor < hWave` — i.e. *did this sample's width term
come out finer than the λ cap*. That counts every sample the per-axis floor then raised straight back
to the λ cap, which is most of a rim on wide metal: **73 % of a 20 × 30 mm plate**, whose x pitch came
out at λ_g/20 exactly. A user reading "the width set the pitch on 73 % of this plate" beside a pitch
that IS the wavelength cap has been told something false. The predicate is `hSheet < hWave`, which
says only what actually happened; the same plate now reports the width binding where it genuinely
does and the note's other branch — "nothing on this artwork is narrow enough, so the bulk pitch is
the whole answer" — fires on a bare patch, where it is true.

### §5's expected range: MISSED by 1.14×, and the whole of the miss is the edge fan

The brief's arithmetic: λ_g at 1.74 GHz on εᵣ 4.4 is ≈ 83 mm, λ_g/20 ≈ 4.2 mm, so a 41.3 × 49.4 mm
patch is 10 × 12 cells in the bulk, and with a bounded fan on four rims and a locally resolved
349 µm feed the expectation is **order 500-900 unknowns**. Measured on the ANT-2 reconstruction of
that board (which is a fixture, not the owner's file — its per-axis count is 402,607 where the real
board's was 704,482), one frequency point, `LocalConductorWidth`, cells/λ = 20, 4 across, λ_g/200:

| board | per-axis rule | TransmissionLine | **Sheet** | Sheet, edge mesh off |
|---|---|---|---|---|
| as imported (connector present) | 402,607 — refused | 2,710 | **3,231** | 1,519 |
| connector deleted | 17,277 — refused | 1,320 | **1,029** | 521 |

**1,029 against an expected 500-900, and 521 with the edge mesh off — which lands inside the range
exactly.** So the estimate is right about the bulk and does not account for the fan's
tensor-product cost: the longest fan is 3 cells, as ANT-2 intended, but each of those cells is a
gridline across the whole part and there are six real conductor rims (four patch, two feed). Per §5
this is **reported, not tuned** — nothing about the default was changed to close it, and the number
to argue with is the number above.

**The bulk arithmetic came out as predicted, by a route worth recording.** The feed's x columns are
*not* refined to its width: `needX` is floored at the per-axis rule's own `hx`, which on this board is
`narrowX/4` = 2.577 mm (the feed's x-runs are ~10.3 mm, not its 349 µm width), so the feed gets ~4
columns in x and 4 rows in y — 16 cells, not the ~400 an isotropic reading of the feed would have
cost. The one-way floor produces the physically right answer there for a reason that is not about
physics at all, and it is worth knowing that it does: had the artwork put the feed *inside* the
patch's own x range, the column-wise minimum would have charged its width across the patch too.

### The one-way invariant: TRUE on the pitch, and NOT true on the cell count

§6 asks that "for every fixture in the mesher's existing set, Sheet's cell count is ≤ the per-axis
rule's. Assert it, do not assume it." **Asserted, and it fails — on the 8-segment taper, 702 cells
under the per-axis rule against 810 under Sheet.** The cause is not new and is not the sheet's: a
coarser bulk gives the graded edge fan further to climb, its ratio is clamped at 3× per cell, and on
artwork that is mostly rim and hardly any bulk the extra fan cells outweigh the bulk saving. The
transmission-line mesh's own version of this gate had to be stated the same way for the same
measurement (728 → 858 on the same taper), and the mesher reports the trade in its notes.

**With the edge mesh off, all three modes give 679 on that taper.** So the invariant is gated in the
three forms that are true:

1. **On the PITCH, unconditionally** — the largest cell Sheet produces is never smaller than the
   per-axis rule's, on every fixture at cells/λ 20, 10 and 5. This is the structural one.
2. **On the cell count with the fan removed, exactly** — every fixture, every cells/λ.
3. **On the cell count with the fan on, within the same 1.25× the sibling intent's gate allows.**

### A plain line as a sheet: coarse along, unchanged across — and not gridline-identical

§6's safety property, and it holds in the form that matters. Measured on the 200 µm × 10 mm FR-4
line: **23 x gridlines under the per-axis rule against 15 under Sheet, y identical at 10, cells
198 → 126.** Two things that "exactly the per-axis rule" does not literally mean:

- **The x count differs because `BuildGridLines` drops its per-interval minimum-cell floor whenever
  a field is present** (that floor throws the marched partition away for a uniform one, and a single
  number computed from a varying field is wrong somewhere — the trap is recorded in that method). The
  *cap* is identical; the marcher simply places fewer, larger cells under it.
- **The fan's interior y gridlines move by ~1.5e-5 relative** — 94.1257 µm against 94.1226 µm —
  because with a field the fan grades toward the LOCAL bulk cap rather than one global `hy` (ANT-2's
  per-attractor rate). The gate is therefore the transverse line count and the **finest** transverse
  cell, which are what say the 1/√d edge current is still resolved; the fan's internal spacing is
  not.

On the 2.9 mm line the y count itself drops 10 → 9, because there the width term (725 µm) and the λ
cap (714.6 µm) are within 1.5 % of each other and the field's own grading is free to lose one interior
line. The finest transverse cell is unchanged.

### What was NOT done

- **No auto-detection of the intent**, per §7. It is a statement about what the current does, which
  is a modelling decision and therefore the user's; a detector would guess "sheet" on a wide bend and
  under-resolve it silently, which is exactly what the direction decline exists to prevent.
- **The transmission-line decline is untouched.** Gated: on an L-bend the directed field still keeps
  each arm's own transverse resolution, which a single averaged direction cannot.
- **Sheet does not skip the rim**, per §7's named trap. Gated as "the fan is still built under Sheet,
  and turning the edge mesh off still changes the mesh" — the two radiating edges set the effective
  length, hence the resonant frequency, hence every number downstream.
- **No default was changed**, and no accuracy measurement was taken: this brief's rule is mesher-only,
  and the question of whether a sheet mesh gives a *better* resonant frequency than a directed one is
  ANT-9's (the resonance sweep), on a real solve.

---

## ANT-4 — the far field (brief-antenna-4-far-field.md, 2026-09-10)

The pivot of the antenna series: the first thing in this directory that looks **away** from the
metal. `PlanarFarField.cs` + `PlanarFarFieldTests.cs`; the sweep, the `PlanarSolveSettings` and one
new `DataSet` group are the whole of the plumbing.

### The verdict, in one paragraph

**The brief's premise held completely and the phase cost what it said it would.** The far field
needs the spectral Green's function at exactly one point per direction, which `SpectralGreens` and
`LayeredSpectralGreens` already return in closed form, and the rooftop's 2-D transform is
elementary — so the pattern is an **exact sum over N basis coefficients with no quadrature error of
its own**, and every oracle before the power balance is independent of the MoM solve. All five
oracle families passed on the first run: the εᵣ = 1 image reduction, the shorted-stub dipole oracle
over three substrates at every degree, the transform against quadrature at 1e-12, the stratified
two-section cascade, and the one-slab reduction of the general path. **Nothing on this path touches
`Dcim` or `SommerfeldIntegral`, and a source scan holds that shut.**

### The derivation, and the two places it could not be written the obvious way

The whole of it is in `PlanarFarField.cs`'s header. Two results are worth repeating here because
they are what a reader will otherwise re-derive wrongly:

- **The stationary-phase constant was PINNED, not quoted.** Applying the asymptotic operator to
  `F = 1/(2jk_z0)` must reproduce `e^{−jk₀r}/4πr`, because that is the Sommerfeld/Weyl identity this
  whole directory rests on. That fixes `C(r,θ) = j k₀cosθ e^{−jk₀r}/(2πr)` with no saddle-point
  formula transcribed from anywhere.
- **NEITHER ELEMENT FACTOR MAY BE WRITTEN WITH A 1/cosθ OR A `Z^h = ωµ/k_z` IN IT.** The obvious
  spelling of `f_TM` carries a 1/cosθ from `Ẽ_θ` and a cosθ from `C`; the obvious spelling of `f_TE`
  carries `Z^h`. Both are infinite at θ = 90°, which is a point the θ axis **contains**. Written as
  `f_TM = 2V_i^e e^{jk_z0 z}/η₀` and `f_TE = 2I_i^h e^{jk_z0 z}` every quantity is finite there.
  `BothElementFactors_AreFiniteAtGrazing` is what keeps the obvious spellings from creeping back.

**The general kernel forces a two-spelling split for `f_TE`, and it is not stylistic.**
`LineResponse` forms the region's characteristic impedance explicitly on exactly one of its two
paths: `Z^h` multiplies the SAME-region voltage and divides the CROSS-region current, and it is
infinite at grazing. So a level on the stack's top surface reads `f_TE` off the **current** (which
`LineResponse` computes with no impedance at all) and a buried or covered level reads it off the
**voltage** (whose cross-region path never divides by the top region's Z either). The two are
algebraically identical and are gated against each other.

**A second consequence of the same asymmetry: the observation height must be STRICTLY above the top
interface.** The line current is discontinuous at a shunt source, and `I_i(z′|z′)` is its two-sided
average, not the up-going wave — so evaluating at the interface where the metal sits would read
`f_TE` off the wrong side of a step. One free-space radian above the stack is used; any height gives
the same answer because the propagator is removed exactly, so this is a choice of conditioning.

### The θ axis is 0…90°, and the axis is where that is said

With a laterally infinite ground plane the field below is **identically zero by construction**, not
small. A 0…180° axis half full of those zeros invites a polar plot that reads as a measurement of a
very good antenna, and nothing in a plot can say otherwise. `PlanarFarFieldGrid.MaxThetaDeg` is the
one constant a later finite-ground phase moves; the θ values themselves are DATA, so extending the
axis is not a rewrite. A θ beyond it is refused with its reason rather than filled.

### Refused by name, and why guessing would have been worse

Each is representable in the types and genuinely not computed. All four are `EmSuitability`
verdicts, so the SWEEP still ships and the reason arrives as a note — present and refused.

- **A CUT (conformal) boundary cell.** A cut cell's metal is not its rectangle, so the rectangle's
  transform is not its own. Using it would be a smooth, plausible, wrong pattern. `Staircase` (the
  default) meshes what this can transform.
- **A VERTICAL (via or ground-attachment) basis.** A z-directed current radiates, but through a
  SERIES voltage source on the TM line rather than a shunt current one — a different element factor,
  a different transform, and its own oracle. **This is the one refusal with a real functional cost
  and it is worth naming: it rules out a probe-fed patch**, which `brief-antenna-0-overview.md` §2
  calls the cleanest feed this kernel can express, because `PlanarGroundPath.Extend` builds a via for
  an internal port. Edge-fed and inset-fed patches are unaffected. Dropping the via current silently
  would have been a pattern right in shape and wrong in level on exactly the structures where it
  matters.
- **A stack that is not PEC below.** The θ range rests on it.
- **A top half-space that is not free space.** The pattern is written in k₀ and η₀.

### Measured cost — reported, not made into a timing test

Scratch harness, Release, 10 cores, the measured board's own stackup (41.3 × 49.4 mm patch on
203.2 µm FR-4 at 1.74 GHz), a random unit current vector so the timing is the transform's alone:

| N | 1°×1° hemisphere (32,760 directions) | 2°×2° (8,280) |
|---|---|---|
| 262 | 0.46 s / 0.83 s | 0.04 s / 0.21 s |
| 1,237 | 0.68 s / 3.87 s | 0.17 s / 0.98 s |
| **3,017** | **1.69 s / 9.51 s** | 0.43 s / 2.41 s |
| 6,110 | 3.42 s / 19.56 s | 0.97 s / 4.93 s |

(parallel / single core). **Exactly linear in N and in direction count**, as an O(N) direct sum must
be, and the brief's own estimate ("N ≈ 4,000 over a 1°×1° hemisphere is seconds") is confirmed. A
single de-embedded planar point is tens of seconds, so a pattern is a fraction of one frequency
point's own solve. **Nothing was optimised**: the exact per-basis closed form is what ships, and the
gridline-table hoist that would make it ~10× faster was not needed and therefore not written.

### Power balance — reported before it is gated

On a 4 mm FR-4 stub at 5 GHz, port 1 driven at 1 V: **65.11 µW accepted, 718 nW radiated into the
upper hemisphere (1.1 %), 64.39 µW unaccounted.** The unaccounted term is dielectric loss plus
surface-wave power together; itemising them is the metrics phase's. **The copper term is identically
zero in this kernel** — the metal is a perfect conductor and `SigmaSm` is carried through the whole
pipeline and never read by the fill — so the balance closes *optimistically*, which is expected
rather than a defect and is exactly why the gate is the inequality `∫U dΩ ≤ P_accepted`.

`PlanarFarFieldPattern.RadiatedPowerW` is the one quantity in the file that is **not** exact: it is a
trapezoid in θ with the sinθ Jacobian and a periodic rectangle in φ over a sampled grid. Everything
else is closed form.

### Traps found on the way

- **A grid whose φ list is not ascending is refused**, and it caught a test of mine that wrote
  `[23, 337, 61, 299]` meaning "these four angles" — the validator is the reason that was a refusal
  rather than a silently mis-weighted power integral.
- **A relative tolerance at θ = 90° compares two spellings of nothing.** The εᵣ = 1 oracle produces
  1e-34 there and the code produces exactly 0; the comparison has to be against the pattern's own
  peak, not against the local value. The same trap will bite every metric that normalises per
  direction.
- **The far-field verdict must be taken at the LOWEST requested frequency, not at the top of the
  sweep.** The spectral kernel's only frequency-dependent refusal is its electrical-thickness FLOOR,
  so checking `fHi` passes a run that then throws at its first pattern.
- **An adaptively sampled sweep may never solve a requested pattern frequency.** A pattern needs
  basis currents and an interpolated s-parameter has none, so the pattern is taken at the nearest
  SOLVED point and the substitution is reported. Forcing the sampler to solve the requested points
  was rejected: it would make the adaptive answer depend on whether a far field was asked for.
- **The pattern belongs to the structure AS MESHED**, feed lead and all. R-fed-1 grows a uniform lead
  for the calibration and the s-parameters have it de-embedded away; a pattern cannot, because the
  lead's current is real current and it really radiates. Said in a note rather than left to be
  discovered.

### What was built

- `src/Engine/Mom/PlanarFarField.cs` — `PlanarFarFieldGrid`, `PlanarFarFieldPattern`,
  `RooftopSpectrum` (the closed-form transform), `FarFieldElementFactors` (the layered element
  factors), `PlanarFarFieldSettings`, `PlanarFarFieldSet`, `PlanarFarField`.
- `PlanarSolveSettings.FarField` (null = off, so every measured number in §L8c/§L8d/§L9d is
  reproducible by leaving it null), `PlanarSolveResult.FarField`, and the patterns computed inside
  both sweep drivers from the solution columns already in hand — **no second fill, no second
  factorisation.**
- `PlanarKernel.AddFarField` — group `"farfield"`, cubes `Etheta`/`Ephi`/`U`, axes
  `[freq, theta, phi, port]`. **R-res-6 for the sixth phase running: no new result type.** The port
  axis is present from the first commit even though every antenna fixture here is a one-port — it is
  free at construction and retro-fitting an axis onto a shipped cube is not.
- `tests/Engine.Tests/Mom/PlanarFarFieldTests.cs` — 32 tests, **638 ms**, all in the routine tier.

### Not done, on purpose

- **Vertical-current radiation** — see the refusal above. It is the one that costs a real
  capability.
- **The cut-cell transform** — the brief allowed refusing it for v1 and it is refused.
- **No far-field `measure` in the TestBench grammar.** An EM run is a `.cem` run producing a
  `DataSet`, not a TestBench analysis; these are diagnostics cubes in the group pattern kernel B
  already established.
- **No optimisation.** See the cost table: the direct sum is exact and fast enough, and an FFT or an
  interpolation over directions would trade the exactness away for a cost that has not been shown to
  matter.

---

## ANT-5 — directivity, gain, efficiency, beamwidth, and the staged front-to-back refusal
### (brief-antenna-5-metrics.md, 2026-09-10)

The numbers an antenna is judged by. `PlanarMetrics.cs` (the registry), `PlanarPowerBudget.cs` (the
itemisation and the surface-wave launch), `PlanarBeamwidth.cs` (the cut derivation and its refusals),
plus the sweep wiring and one group of cubes that already existed.

### The verdict, in one paragraph

**The conventions half of the brief held exactly and cost what it said. The loss-itemisation half is
where it was wrong twice, and both corrections are improvements rather than reductions.** §3's
"three of the four are already computable" is two computed plus a RESIDUAL, because over a laterally
infinite lossy substrate a volume dielectric-loss integral already contains the whole surface-wave
term; and §5's characterisation of the two power-balance regimes is inverted — surface-wave power
grows with substrate thickness, so on a thin board radiation dominates and the surface wave is a few
per cent, not the other way round. The surface-wave term itself, which the brief called "the term a
laterally-infinite substrate model captures uniquely well", came out right **on the first run**: the
pole-residue derivation, its constant and its sign are pinned by the lossless power balance closing
to **5.6e-5** on the shipped FR-4 cross-section. 26 tests, **3 s**, all routine tier.

### The four conventions, and why naming them was the actual deliverable

Every metric here has at least two defensible definitions. The registry's notes carry all four:

| quantity | taken as | the trap |
|---|---|---|
| Directivity | 4π·U_peak / P_radiated | none; it is the unambiguous one |
| **`GainDbi`** | D·η_rad = 4π·U_peak / P_accepted | **EXCLUDES mismatch** |
| **`RealizedGainDbi`** | that × the mismatch factor | **INCLUDES mismatch** |
| η_rad | P_radiated / P_accepted | the denominator is ACCEPTED, never incident |

- **R-ant-4. Neither is called just "gain", and each note names the other.** Asserted structurally —
  there is no cube named `Gain` and both notes cross-reference — because the failure mode is a user
  comparing one against a datasheet that quotes the other and concluding a tool is broken.
- **R-ant-5. P_accepted is ½Re(Y_jj) of the RAW self-admittance**, the matrix the transformed currents
  actually came out of. **The de-embedded `S` cube describes a different structure** with the feed
  leads removed, and weighing a pattern of one structure against the power accepted by another is a
  mistake nothing downstream could see. `PowerAccepted` is therefore published as a cube of its own,
  which the brief's table did not list: an efficiency whose denominator is not published cannot be
  reproduced.
- **R-ant-6. One mismatch factor, written as accepted-over-AVAILABLE power**,
  `4·Re(Z₀)·Re(Y)/|1 + Z₀Y|²`. Equal to 1 − |Γ|² for a real reference and to the power-wave form for
  a complex one, and written this way because it is a function of the same Y_jj and Z₀ everything
  else reads — so "the two gains differ by exactly the mismatch factor" is structural rather than two
  estimates happening to agree. On a multiport it is the reflection with the OTHER ports SHORTED,
  which is exactly the excitation the pattern belongs to.

### The surface-wave launch — the one piece of new physics, and it closed first time

The derivation is in `PlanarPowerBudget.cs`'s header. The shape of it: the complex power an impressed
surface current delivers is `−½∫E·J*dS`, which Parseval plus the transmission-line map turns into a
k-plane integral of `Σ_{L,L′} Ĵ*·V^p(z_L|z_{L′})·Ĵ` — a second, matrix-free statement of ½Re(Y_jj). A
guided mode is a simple pole of `V^p`; it must decay as it propagates, so the pole is BELOW the real
axis, and Sokhotski–Plemelj picks up `−jπ·β·Q(β, φ)` per mode:

    P_sw = Σ_modes β · Σ_j Im[Q_p(β, φ_j)] / (4·N_φ)

Three places this could have been written wrong and looked right, all of them stated in the file:

- **The residue is taken in k_ρ, not in w = k_ρ².** `V` is literally a function of `w`, so in `k_ρ` it
  is EVEN with poles at ±k_p and `R_k = R_w/(2k_p)`. A contour integral in k_ρ gets that for free; an
  analytic substitution is where a factor of 2 goes missing invisibly.
- **The contour radius is a fraction of the distance to the nearest other singularity, never a fixed
  number.** A near-cutoff mode sits essentially ON the branch point at k₀ — the measured 203 µm
  prepreg board's TM₀ is at (k_p − k₀)/k₀ = 1.4e-5 — and a mode too close for a simple pole to be
  separable from the continuum is REFUSED by name rather than given a number.
- **Ĵ is evaluated at Re(k_p)**, which makes this a small-loss expansion; the worst pole's
  |Im k_ρ|/Re k_ρ is reported with the answer and refused past 0.05.

**The one-slab branch of the line voltage is written as `(Z₀^p/2)(1 + Γ^p)` and that is an identity,
not an approximation** — the voltage a shunt source sees at the interface is `Z_up ∥ Z_down`, and
`SpectralGreens`' cross-multiplied Γ is exactly `(Z_down − Z₀)/(Z_down + Z₀)`. Gated against the
general cascade rather than asserted: **they agree to 5.7e-14** over k_ρ/k₀ ∈ [0, 10] on both
polarisations. R-ant-2 still has `PlanarProblem.RequiresGeneralKernel` choose which one a problem
takes.

**The φ rule is the periodic rectangle and is converged at 90 samples to eleven digits** (checked at
90, 180, 360 and 1,440). The default is 360 only because it costs nothing.

### Two places the brief was wrong, and the corrections

**1. `PowerDielectric` cannot be a third independent integral.** The brief asks for the volume loss
`½∫ωε₀ε″|E|²dV` alongside the surface-wave residue, and for the four terms to sum to P_accepted. They
cannot both be true: over a **laterally infinite** lossy substrate the guided mode decays as
`e^{−2αρ}/ρ`, and `∫ρdρ` of that is finite and equals *everything the mode ever carried* — so the
volume integral already CONTAINS the whole surface-wave term and adding them double-counts. What is
disjoint, and what ships, is

    PowerDielectric = PowerAccepted − PowerRadiated − PowerSurfaceWave

the dielectric loss **not** carried away by a guided mode. On a real finite board that is exactly the
distinction that matters, because the guided part instead reaches the edge and radiates.

**R-ant-3 follows: a residual cannot gate itself, so the LOSSLESS case is the gate.** With tanδ = 0,
PEC metal and a PEC floor there is nowhere for accepted power to go but the upper hemisphere and the
guided modes, so the residual must be ZERO — and the three quantities forced to meet there share no
implementation at all: ½Re(Y_jj) from the MoM factorisation, ∫U dΩ from the far field, and the pole
residues from the spectral kernel. Measured, lossless:

| substrate | N | radiated | surface wave | residual / accepted |
|---|---|---|---|---|
| **1.6 mm εᵣ 4.4** (the FR-4 starter cross-section) | 45 | 80.1 % | **19.9 %** | **5.6e-5** |
| **8 mm εᵣ 2.2** (5× thicker, low permittivity) | 49 | 68.0 % | **32.0 %** | **1.6e-4** |
| 203 µm εᵣ 4.4 (the measured board's prepreg) | 45 | 96.7 % | 2.7 % | 6.0e-3 |
| 100 µm εᵣ 12.9 (GaAs) | 45 | 96.4 % | 2.0 % | 1.6e-2 |

**2. §5's two regimes are inverted.** The brief expects "the thin FR-4 case, where surface-wave and
dielectric loss dominate". Lossless thin FR-4 books **2.7 %** into the surface wave and 96.7 % into
radiation; the surface-wave share grows with `k₀h√(εᵣ−1)`, so it is the THICK substrate that is the
surface-wave regime. The gate therefore runs on the two top rows, where the share is a real fraction
in both (20 % and 32 %) so a sign or factor error in the residue cannot hide, and the thin rows are
reported rather than gated.

**The residual is the MATRIX FILL's accuracy, not the metrics'.** Attributed rather than assumed. It
does **not** move when the far-field θ grid is refined 10× (1° → 0.1° moves the 203 µm case from
6.09e-3 to 5.97e-3 and stops), it does not converge cleanly with mesh density, and it tracks how hard
the DCIM fit is: ~1e-5 on FR-4, ~1e-2 on GaAs — which `GroundedSlab`'s own comment already calls "the
harder case for DCIM". And the argument closes: in a lossless medium Re(V^p) is identically zero for
k_ρ > k₀ away from the poles (Z₀ and Γ are both purely imaginary there), so radiation and the poles
are the only real-power channels there are, and any gap is the matrix.

### The cavity model agrees, and the agreement is the MODEL's error not the mesh's

An independent analytic oracle, written from the two-slot construction (TM₁₀ cavity, magnetic walls,
`M_s = −2E₀ŷ` on both radiating edges in phase) and sharing nothing with the MoM or with
`SpectralGreens`:

    U ∝ sinc²(k₀W sinθ sinφ/2) · cos²(k₀L sinθ cosφ/2) · (cos²φ + cos²θ sin²φ)

On a 29.18 × 36.47 mm edge-fed patch on the 1.6 mm FR-4 starter at 2.4 GHz (h/λ₀ = 0.013):

- **Directivity: 6.423 dBi against the cavity model's 5.950 — Δ 0.47 dB.**
- **And it is MESH-INDEPENDENT to 0.006 dB** from N = 237 to N = 3,831 (6.423 / 6.427 / 6.428 /
  6.429 dBi), so the 0.47 dB is the cavity model's own — it has no surface wave and no feed.
- **Cut shapes: the H-plane agrees to 0.10 dB and the E-plane to 0.57 dB** out to θ = 80°, the
  E-plane being the one the infinite ground plane changes most.
- Radiation efficiency reads 41 % there and is mesh-stable to 0.4 pp (41.21 / 41.34 / 41.20 / 40.99 %). **The realized gain is NOT**
  (−3.85 dBi at N = 237 against −1.25 at N = 536): it inherits the edge port's own Z_in convergence,
  3.2 Ω against 6.6 Ω. Worth knowing which numbers are robust to the mesh and which are not.

### The beamwidth's cut, and the three things that went wrong deriving it

`BeamwidthDeg` is per cut, and R-ant-7 is that the cut is either named or derived-and-REPORTED, never
defaulted to φ = 0. The derivation reads the major axis of the current moment's polarization ellipse,
`φ_c = ½atan2(2Re(M_x M_y*), |M_x|² − |M_y|²)`, and refuses when there is no dominant linear axis
(`minor/major` past 0.25 — a circularly polarized structure reads **1.0 exactly**).

- **It must be evaluated at the PEAK DIRECTION, not at k = 0.** At k = 0 the transform is the plain
  dipole moment and the sum is the structure's total current moment, which **cancels to round-off on
  anything electrically long**: on a 3 λ_g line the integral of a standing wave over whole periods is
  zero, so the axis would be read off noise. At the peak the transform is by construction the current
  that made the largest field there, so it cannot vanish — and for a broadside peak the two agree,
  which is why a patch is unaffected either way.
- **An axis is a LINE, so fold it toward the peak's azimuth.** Unfolded, the 3 λ_g line (peak at
  θ = 31°, φ = 0°) reported φ = 180.00° — the right plane, back to front, because the transverse
  rooftops across the line's width give the ellipse a cross term of round-off size and a SIGN. The
  cut's own peak then landed in the negative-θ branch.
- **A φ-grid tolerance must be half the FINEST step, not the coarsest.** A half-circle grid's 225°
  wrap-around gap is the symptom, not a step; counting it made such a grid look finely sampled and
  accepted a back azimuth 45° from where the cut needed one.

**And the cut dominates the answer**, which is the whole reason it may not be guessed: on the measured
patch the **E-plane reads 157° and the H-plane 84°**. The E-plane figure is also a good illustration
of the infinite ground plane — the pattern only reaches zero at exact grazing, so the half-power point
sits near 78° where a finite board puts it nearer 40°.

### The staged front-to-back refusal, and the proof that it is one predicate

`FrontToBackDb` is in the registry from this phase, refuses with its own sentence naming the reason
(the ground plane and every dielectric layer are laterally infinite, so there is no lower hemisphere)
and the phase that supplies it, and — the part that makes the staging real — **its `Evaluate` is
written and already correct**. `FrontToBack_BecomesAvailableOnTheOnePredicate_AndItsValueIsAlreadyRight`
hands the same registry entry a pattern whose θ axis reaches 180° and gets 20.000000 dB out of a
hand-built 100:1 ratio. The finite-ground phase moves `PlanarFarFieldGrid.MaxThetaDeg` and nothing in
the picker, the exporter or the cube emission changes. `LayeredMedium.CanHost`'s rule holds: the
refusal is narrowed, never deleted.

`DirectivityPeakPhiDeg` is refused the same way at a broadside peak — every azimuth names the same
direction there, and reporting the first grid value would look like a measurement of a direction. The
θ cube says where the peak is, so the refusal is informative rather than a hole. (On the edge-fed
patch the peak is at θ = 1°, not 0: the feed breaks the x symmetry, and the azimuth is therefore
published.)

### Measured cost — reported, not made into a timing test

Release, 10 cores, 1°×1° hemisphere, the 2.4 GHz patch. **The whole registry costs 20-40 ms** against
a 287-2,429 ms pattern and a 58-1,961 ms solve, from N = 237 to N = 3,831 — roughly **1 %** of the
pattern at the top end, the surface-wave integral being nearly all of it. **That is why
`PlanarFarFieldSettings` has no switch to turn the metrics off**: every one of them is a post-process
of a pattern the run has already paid for, and a pattern with no numbers attached is half an answer.

### Other traps and decisions worth having written down

- **An efficiency above 1 REFUSES and names both numbers; it is never clamped.** A clamped efficiency
  reads as 100 % and hides exactly the kind of level error the itemisation exists to catch. The
  tolerance is 2e-3 — an order of magnitude above the worst measured lossless residual — and it exists
  only because the numerator is a quadrature and the denominator comes out of a factorisation.
- **Refusing `PowerSurfaceWave` must take `PowerDielectric` with it.** The residual's meaning depends
  on the term taken out of it, so publishing accepted − radiated under the dielectric name would be
  the double count the note warns about. The combined remainder is left for the caller to form from
  the two cubes that are published.
- **A barely bound mode keeps its energy in the AIR, so a lossy substrate does not make its pole
  lossy.** 1.6 mm FR-4 reads |Im k_ρ|/Re k_ρ = 1.2e-4 at tanδ = 0.02 and still only 3.9e-3 at
  tanδ = 1. Reaching the 0.05 residue ceiling takes a mode genuinely inside the dielectric — 8 mm
  εᵣ = 10 at tanδ = 0.3 puts TM₀ at k_ρ/k₀ = 2.65 and |Im/Re| = 0.20. **The ceiling is reachable but
  fires for no ordinary board**, which is where a ceiling belongs.
- **A metric is published as ONE cube over the whole sweep or not at all.** A `DataCube` has no
  missing-value concept, so a cube with a refused point would carry either a NaN — a hole that reads
  as data — or a fabricated number. Availability depends on the medium and the grid, both constant
  across a sweep; the one frequency-dependent case (the pole's loss ceiling) is named rather than
  half-published.
- **The beamwidth cut axis must AGREE across the set.** A derived cut is derived per pattern, and a
  structure whose dominant axis moves with frequency or differs between driven ports has no single
  plane to put on one axis. Averaging one in would be the guess §4 forbids, so a disagreement refuses
  the beamwidth for the whole set and says to name a cut.
- **`tests/Firewall.Tests` was ALREADY RED at ANT-4's commit**, and that is worth recording because it
  is the failure mode the user-facing-text gate exists to catch: ANT-4 added three
  `throw new …Exception("…")` sentences in `PlanarFarField.cs` and did not list them in
  `user-facing-text-allowlist.txt`, so `NoNewUserFacingTextIsAddedBelowTheUiFirewall` failed on 3 of
  them before this phase added a fourth. All four are internal invariants no user reads — a solution
  and a mesh that are not the same solve, the two spectral-kernel choices disagreeing, a merged cell
  the closed-form rooftop transform cannot describe — so all four are allow-listed, which is what that
  gate's own message says to do when a plain exception really is right.
- **ANT-4's interim `PlanarFarField.PowerBalanceNote` is RETIRED**, one day after it landed, because
  its own closing sentence ("the unaccounted term … which this phase does not itemise") stopped being
  true. `PlanarPowerBudget.Caption` is its strict superset, and carrying both would have put two
  sentences that contradict each other into one notes list.

### What was built

- `src/Engine/Mom/PlanarMetrics.cs` — `PlanarMetric`, `PlanarMetricAxis`, `PlanarMetricSettings`,
  `PlanarPatternPeak`, `PlanarBeamCut`/`PlanarBeamCuts`, `PlanarMetricContext`,
  `PlanarMetricDefinition`, `PlanarMetricOutcome`, `PlanarMetricReport`, **`PlanarMetrics.Registry`**
  and `PlanarMetricSet`.
- `src/Engine/Mom/PlanarPowerBudget.cs` — `PlanarSpectralVoltages` (the problem's own kernel at an
  arbitrary complex k_ρ and level pair), `PlanarGuidedModePower`, `PlanarSurfaceWavePower`,
  `PlanarSurfaceWaveLaunch`, `PlanarPowerBudget`.
- `src/Engine/Mom/PlanarBeamwidth.cs` — the cut derivation, the fold, and the four refusals.
- `PlanarFarFieldSettings.Metrics`, `PlanarSolveResult.Metrics`, the reports built beside each pattern
  in both sweep drivers (the only place the pattern, its currents and its raw admittance are in hand
  at once), and `PlanarKernel.AddMetrics` — **13 metrics in ONE registry**, published as cubes in
  ANT-4's `"farfield"` group, **and no new result type for the seventh phase running.**
- `tests/Engine.Tests/Mom/PlanarMetricsTests.cs` — 26 tests, **3 s**, all routine tier.

### Not done, on purpose

- **No independent volume integral for the dielectric loss.** Refuted above: over a laterally infinite
  substrate it is not a disjoint channel from the surface wave, so it would double-count rather than
  cross-check. The lossless balance is the cross-check, and it is a stronger one.
- **No axial ratio or polarization sense** — ANT-6's, and the circular-polarization case is exactly
  where this phase's beamwidth REFUSES and points at it.
- **No front-to-back number.** Staged, not omitted.
- **Nothing reaches the CLI or the GUI**, because ANT-4's far field does not either: the registry is
  what makes the picker, the listing and the exporter cheap when a presentation phase arrives.
