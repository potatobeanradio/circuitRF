# src/WBond — resolved work (detail, off the CLAUDE.md growth path)

One `##` section per piece of completed work, sparingly — only findings that are still true, still
surprising, and would cost someone real time to rediscover. Mirrors `src/WBond/Mom/RESOLVED.md` and
`src/Ui/RESOLVED.md`.

## A wire lying in the ground plane, and the crash it caused mid-drag (2026-08-19)

Owner: *"I was dragging a wire in the wBond host layout, but circuitRF crashed"* —
`InvalidOperationException: The inductance matrix is not positive definite (pivot 0.000E+000 at wire 6)`
out of `CapacitanceReduction.Compute`, through `WBondViewModel.Republish` and `OnPointerMoved`.
Second report, same session: *"when I drag wires overtop of other wires, the dragged wires move back
to their old position during the drag and my mouse is no longer overtop of the wires that I was
dragging."* **Both are the singular-matrix refusal**, seen at its two ends — the editor half is in
`src/Ui/RESOLVED.md`.

### The message named the wrong matrix and neither of the right causes

`CholeskyFactor.Factor` is used by four call sites and hard-coded *"The inductance matrix"* into its
failure for all of them. The factorisation that actually failed was **P**, the potential-coefficient
matrix, and the geometry that failed it is not either of the two the message offers. It now takes a
`matrixName` and a `hint`, and `CapacitanceReduction` supplies both.

### The fingerprint: `0.000E+000` says which degeneracy it was

**A wire lying in the plane is held at zero potential by it** — that is the whole content of the image
method — so its entire row of **P** is analytically zero and the matrix is singular by a rank. Two
wires merely *sharing geometry* is also singular, but arrives as a tiny **negative** pivot (measured
-4.1e-25). So the two causes are distinguishable from the log alone, and the report said `0.000E+000`.

How exactly zero it comes out is a rounding accident worth knowing, because it decides whether a
diagnosis keyed on the sign fires at all:

| case | measured |
|---|---|
| single-filament flat wire, alone | `P₀₀` **bit-exactly** 0 |
| flat wire at index 6 among 8 looped ones | `P₆₆` 0, pivot **-2.446E-019** |
| looped wire flattened in place (many filaments) | `P₁₁` **-6.0e-4**, against diagonals of ~1e13 |

Exact wherever direct and image evaluate identically (the far kernel takes both at the same
centre-to-centre distance; a lone self term is subtracted from itself); off by the last bits where near
pairs sum the two by quadrature in different orders. Hence `RefuseWiresLyingInThePlane` uses a
**relative floor** (1e-15 of the largest diagonal) and not `<= 0` — `P_ii` falls only logarithmically
with height, so nothing physical spans fifteen orders and the test cannot false-positive on a genuinely
small capacitance.

### The obvious story about L is wrong, and the correction is the actual mechanism

It is tempting to conclude the inductance is untouched, since its image term **adds** where the charge
image subtracts. **Measured, `L` goes exactly singular on the same wire** (`L₀₀ = 0`): a horizontal
filament's image is anti-parallel *and* coincident, so it cancels too. The capacitance is not where the
physics is special —

> **it is where the arithmetic is redone from scratch.**

`IncrementalFill` maintains **L**'s factor by rank-2 updates and revisits only the rows of wires that
*moved*; `RefreshCapacitance` refills and refactorises **P** over the *whole* mesh on every republish.
So a degenerate wire that is not the one under the cursor is invisible to every inductance-side guard
in the editor and fatal to the capacitance — which is exactly the shape of the crash, and why the
report named a wire (6) the owner was not dragging.

### The matrix outlives its factor, and that is the useful asymmetry

`L` is well defined at every position the wires can occupy; only its Cholesky **factor** ceases to exist
while two of them coincide. `MoveWiresUnfactored` + `TryRefactor` exist to exploit exactly that — the
editor keeps the matrix exact across a degenerate stretch of a drag and retries the factorisation each
frame, so the panel recovers the instant the wires separate. Recovery is one fresh factorisation,
O(N³/3) and ~23 ms at N = 600, against the O(N²) **fill** a rebuild would repeat — which is the
expensive half, and the reason "just rebuild the fill each frame" is not an option on a real design.

`Reduce()` therefore refactorises when `FactorIsStale`, rather than reducing against a factor the matrix
has moved past: that would be silently wrong, which is worse than the throw a genuinely singular matrix
earns.

### `IncrementalFill.MoveWires` is not transactional, and that is what let a refusal poison the mesh

It re-flattens the moved wires into the mesh and writes their rows into **L** *before* the factor update
discovers the matrix is singular and throws. So "the edit was refused" never meant "nothing happened":
the degenerate geometry was already in the mesh, and the mesh is what the capacitance is refilled from
on every later frame. Pinned by `WireInThePlaneTests.AFailedMoveWires_HasAlreadyMutatedTheMesh` rather
than fixed here — making the fill transactional means snapshotting a mesh row, an **L** row and the
whole factor on *every* drag frame, a cost paid always to serve a path taken almost never. The caller
rebuilding once on the error path (`WBondViewModel.RebuildAfterFailedFill`) is the cheaper half of the
same guarantee.

## Plastic overmold: `WBondDesign.OvermoldEr` (2026-08-19)

Owner: *"How do we add plastic over-mold effects to the wire bond MoM kernel? (And also to the lumped
model when it calculates capacitance?) Perhaps a simple overmold permittivity parameter `er` should be
added to the wBond component."*

### The physics change is one division, and that is not a simplification

Both wirebond kernels — the lumped wire-basis model and the distributed MoM one — are **quasi-static**.
Neither carries a retardation factor: `M̃(ω) = (jω)²L + jω D(ω) + K̃` has no `e^{−jkR}` anywhere in it,
and `L` comes from Grover's filament integrals. A mold compound is **non-magnetic** (μ_r = 1), so the
whole effect of an encapsulant is `ε → ε₀·ε_r`, which divides **P** by ε_r and touches nothing else.

Every capacitance is therefore **exactly ε_r × the air value**, every inductance is **bit-identical**,
and the self-resonance falls as **1/√ε_r**. `OvermoldTests` asserts all three, and the third is the one
worth keeping: it fails if either half is wrong, *including* the failure mode where both L and C get
scaled and the ratio survives.

**Applied in exactly two places, both of them the `P` fill** — `PotentialCoefficients.Fill` (wire
basis) and `Mom.NodePotential.Fill` (node basis). Not in `Block`, not in `Kernel`: the kernel is
geometry and the permittivity is the medium, and keeping them apart is what lets the near/far
threshold gates and the `Bᵀ P B` identity gate compare kernels with no material in the way.

### `LocalSegmentCapacitance` still uses bare ε₀, and that is correct

`CapacitanceReduction.EndSplit` rescales each wire's local analytic `C_i` so the per-wire total matches
that wire's row sum of `C_wire` — which already carries ε_r from the multi-conductor solve. The local
form sets only the **shape** of the split. A uniform factor on a shape that is then normalised is
exactly nothing, so applying ε_r there would be a second, cancelling copy of the same physics. The
next reader will see an ε₀ that looks forgotten; it is not.

### The `Bᵀ P B` identity gate does NOT agree to 1e-10 — and never did

Writing a "the two bases agree in a medium" test found **6.5e-3** worst relative difference. That is
not the medium: the MoM mesh re-segments each wire, so the two discretisations are genuinely different
and disagree by that much **in air too**. The claim worth gating is therefore that the medium leaves
the disagreement *unchanged* (`TheMedium_DoesNotChangeHowTheTwoBasesAgree`) — which is precisely what
would break if ε_r were applied in one file and not the other, the actual risk of splitting the change
across two fills.

### `WireMesh` now holds its design, so the medium cannot go stale

`WireMomMesh` already held `Design`; `WireMesh` did not. Rather than pass ε_r down every call chain, or
snapshot it into the mesh (where an editor changing ε_r would silently keep filling in the old medium),
`WireMesh.Build` keeps the design **live**. The geometry stays a snapshot — `RefreshWire` is still how a
moved wire reaches the mesh — but a scalar *setting* is read at fill time. That is the same relationship
the MoM mesh already had, and it is why `PotentialCoefficients.Fill` needs no new argument at its call
sites.

### Below 1 is refused, not clamped

`WBondDesign.Validate` throws. Clamping would let a design report a capacitance it did not ask for and
say nothing about it; **zero or negative is worse** — `P` is divided by this, so it would produce an
infinite or sign-inverted capacitance and surface as a Cholesky breakdown a long way from its cause.
The prompt dialog, the editor view-model and the schematic→layout write-back each decline it earlier,
so the design-level throw is a backstop rather than the user-facing message.

### What this deliberately does NOT model

**One homogeneous medium filling all space above the ground plane** — not a mold cap of finite
thickness with air above it. That is what makes it a single number and what keeps the image method
exact; a layered ε needs a layered Green's function, which is a different kernel entirely
(`src/Engine/Mom` has one, for planar structures). A loop well inside a mold body is described by
this; one whose apex breaks the mold surface is **bounded** by it, at the pessimistic (high-C) end.

Also worth stating rather than discovering: the quasi-static assumption gets **stricter** as ε_r rises,
because the wavelength in the medium shortens by √ε_r. At ε_r = 4 a 1 mm wire is electrically twice as
long as it was in air, so the lumped and distributed models part company sooner than they do in air.
Nothing refuses on that account — the distributed model exists for exactly that regime.

## Observation for the owner, not a fix: `MaterialFor` falls back silently (brief-em3d-4, 2026-09-25)

`WBondDesign.MaterialFor` resolves an unknown metal name to the design's first material, then to gold,
with no diagnostic. Kernel W therefore solves a wire whose `Material` is a typo as gold (or whatever the
file lists first). That behaviour is out of scope to change in brief-em3d-4. The 3D generator does not
call it: it resolves names itself and refuses a name found nowhere.

## The ball/wedge designation is back, and kernel W still does not read it (brief-em3d-4)

`Wire.CrossSection`, `StartBond`, `EndBond`, `FootLengthNm` and `WireArray.FootLengthNm` are read only by
the 3D generator. `tests/Ui.Tests/Em3d/Em3dWireTests.cs` gate 7 holds that with a source scan of
`src/WBond/` (outside `WBondDesign.cs`/`WBondIo.cs`) and by comparing `WireMesh.Build` on every repo
`.wBond` with and without the fields set. `Wire.Reverse` swaps `StartBond`/`EndBond` with the points,
because a ball stays on the pad it was bonded to. Duplicate, paste and the duplicate ghost carry the
fields with the shape.

## brief-em3d-78 — the array share, from a design or from centrelines (2026-09-28)

**One path, and the design is it.** A thermal run needs each wire's share of its array's RF current — a column of X·L_arr
(`ArrayReduction.CurrentShares` with one array driven), which for a single array is X = L⁻¹A scaled to unit current. `ArrayShare`
gives it for a `WBondDesign` through the design's own `WireMesh.Build` → `InductanceMatrix.Fill` → `ArrayReduction.Reduce`,
and for a `.c3d`'s drawn wires by BUILDING a design from their resolved axes (points rounded to the nanometre DBU, ground
plane as the caller says) and calling the same function. A second entry point computing filaments from metres directly would
have been the drift the brief warns about; rounding a drawn wire to the nanometre is physically nothing and is what makes a
drawn wire on a `.wBond`'s points the SAME design, bit for bit (gate 2 compares with `Assert.Equal` on doubles).

**Bit identity needs the same filament ORDER, not just the same wires.** The first version oriented every drawn wire of an
array from the alphabetically first pad, so a whole array drawn pad → lead was reversed before the fill: the same physics, a
different summation order in `Grover`'s pair loop, and shares 1 ulp apart. A drawn array is now oriented as its first wire
runs, and only a wire running the other way is reversed (it must be: a reversed wire's mutuals change sign).

`InternalImpedance` gained `NormalizedZSlope` and `ResistanceWithSigmaSlope` for the thermal Newton step. Nothing wBond itself
computes moved: its 85 °C evaluation and every wBond test are unchanged.

## Wire metals read the shipped σ(T) tables; Temp clamps and warns (2026-10-05)

**Before this, a placed wBond's `Temp` reached the model but never a table.** `ImpedanceReduction` evaluated
`WireMaterial.SigmaAt`, which was the α₂₀ formula only, with the four metals' σ₂₀/α₂₀ written as literals here. The
temperature-dependent tables added to `generic-materials.cmat` were read by the thermal solver and by nothing in
wBond, so there was no range to clamp to and nothing to warn about.

- **`WireMaterials` reads `generic-materials.cmat`**, linked into this assembly as `CircuitRF.WBond.generic-materials.cmat`
  (a leaf cannot reach `src/Design`'s copy). `All` is still the four bond-wire metals, the list a new design declares;
  `Library` is every shipped conductor (nine), and `ByName` searches it. A metal with no `Alpha20` gets 0, so its σ does
  not move with temperature.
- **A table wins over the formula, and is HELD at its ends** — `ThermalProperties.SigmaAt`'s order and rule, so a wire
  and a thermal run read one conductivity for one metal. `WBondDesign.ConductivityClampNotes` names each metal in use whose
  table does not reach the operating temperature; the factory appends them to the model's notes, so they reach the
  Messages panel through `IReportsWarnings` with the instance path. Clamped, never refused.
- **Old files keep their size and gain the tables.** Every `.wBond` and carried payload already stores its metals as
  σ₂₀/α₂₀/density. `WireMaterials.Adopt` swaps a stored copy for the shipped record when the name AND both numbers match,
  and the writer omits a shipped metal's table (`HasShippedTable`), so `WBondEmbedding.DefaultPayload` is unchanged byte
  for byte. A user metal's own table is written as `SigmaVsTemp: [[°C, S/m], …]`.
- **A `Material` override may name any shipped conductor**, declared by the design or not: `ControllingParameters`
  adds it to the design, so it resolves everywhere the design is read afterwards. `WBondDesign.MaterialChoices` is the
  one list every material picker offers (the Inspector, the editor's wire properties, Set Material).
- **Numbers that moved:** at 85 °C gold's table reads 0.015 % from the formula. `WBondTouchstoneExportTests`' full-matrix
  gate compared at `precision: 12`, which ROUNDS both sides, and the shifted value straddled a rounding boundary 6e-14
  apart; it now uses an absolute 1e-12. The Thermal Output Wires numbers did not move: the 3D path takes a wire's σ
  from the technology's materials first (`Em3dWires.ResolveMetal`), which already carried these tables.

## A wBond instance's own material library — New Material… (2026-10-05)

**The instance names its library**: `MaterialLibrary`, relative to the schematic in the document and absolute (quoted when
needed) in the netlist — a linked `File`'s rule. The elaborator keeps it verbatim like `File` and injects `WBondName` (Match's
`MatchName` pattern) so `ComponentModelFactory.ApplyMaterialLibrary` can refuse BY INSTANCE: a stated library that cannot be
read is a refusal naming the instance, the file and the two ways out, never a fall-back to a shipped metal of the same name.
A Material naming a library record with no σ₂₀ is refused as "not a wire", not as "not declared". The library's conductors
REPLACE same-named design metals: the workspace's definition is the one the user can see.

`WireMaterials.ReadLibrary` reads any `.cmat` (gzipped or not, case-insensitive keys) and is what the shipped list is built from
too. The reuse order and the creation live in `src/Design/Schematic/WBondMaterialLibrary` so they are testable headless; the
Inspector only calls them (`SchematicViewModel.NewWBondMaterial`, installed by the workspace). **A pristine
`generic-materials.cmat` copy is never reused** — `MaterialLibraries.CopyGenericBeside` refuses a changed one, so writing a
user metal into it would break Add Generic Materials later. A technology open in its editor takes the new library through
`TechEditorViewModel.AddLibrary` (undoable, dirty); otherwise the `.ctech` is written and the cache invalidated.

**A latent .cnl bug this surfaced:** a metal NAME with a space ("Gold-tin solder (80/20)", or any user metal) was written to
the netlist unquoted and the reader took its tail as a unit. `NetExtractor` now quotes `Material`/`Material_*` and
`MaterialLibrary` when they contain whitespace; the elaborator already unquoted them.

## "Index was outside the bounds of the array" from a linked wBond whose layout gained an array (2026-10-05)

A LINKED instance simulates the `.wBond` beside the layout, but its symbol's pins are drawn from the arrays it was placed
with. A second array added in the layout gave the model 2M = 4 terminals against the symbol's 2 nets, and `Stamp` read
`c.Nodes[2]` — an unnamed crash at the first frequency. `ReportArrayDrift` already noticed the change, but only as a
note queued for AFTER the stamp. `WBondModel.RefuseIfPinsDisagree` now runs first and refuses naming the instance, the
file, the arrays and the count, and sends the user to Design ▸ Update Schematic from Layout (`WBondSchematicReconcile`),
which gives the symbol the pins. Only FEWER nets are refused: a hand-written `.cnl` legitimately lists the reference net
last with RefPin off (`wBond:WB1 p1 p2 0`, every `WBondStampTests` netlist), and refusing "not equal" broke nine of them.

## A newly placed wBond starts at Temp = 125 °C (2026-10-05)

Owner change: the Inspector's blank `Temp` gave no way to tell what temperature a run used. The registry now declares
`Temp = 125` (`ComponentTypeRegistry.WBondDefaultTempC`). This is NOT the §2.2 trap the controlling parameters guard
against: a `.csch` is loaded exactly as written (nothing fills declared parameters on load), so only a NEW placement —
palette, wire import, Layout→Schematic — gets 125, and every existing instance answers bit-identically. An existing
blank `Temp` still means the design's own `OperatingTempC` (85 °C by default), and its row now shows that value as
placeholder text (`ParameterRowViewModel.ExpressionPlaceholder`). The design's own default and the wBond editor's readout
stay at 85 °C; the instance parameter wins at run time, as before.

## 125 °C is the default wire temperature everywhere (2026-10-05, supersedes the entry above in part)

Owner: the default bond-wire temperature is 125 °C throughout circuitRF — the wBond editor and a new design included, not
only a new placement. `WireMaterials.DefaultOperatingTempC` is 125, and `ComponentTypeRegistry.WBondDefaultTempC` is now
derived from it rather than a second literal.

**A stored 85 reads as 125** (`WireMaterials.StoredOperatingTempC`, in `WBondIo.FromDocument`, which every payload decode
goes through). The writer has always written `OperatingTempC` out and nothing in the application ever set it to anything
but the default, so every `.wBond` and carried payload from before this states 85 — the old default, not a choice. Without
this, a placed component's blank `Temp` showed and ran at 85 while the owner expected 125. A hand-typed 85 is
indistinguishable and reads the same way (stated in the CLI's `reference wbond` page); any other value is kept. This DOES
move existing answers: an instance with a blank `Temp` now runs at 125 °C instead of 85.

The blank-row placeholder is the bare number (the unit column says °C). The phrase "the design's own" was removed from every
user-facing string and the user docs at the owner's request; the remaining hits are code comments.

`WBondSchematicPlacementTests.TheEmbeddedPayload_CarriesNoBase64Padding…` padded the DEFAULT payload and asserted padding
appeared — true only while its byte count was not a multiple of three; one more digit made it one. It now picks a design
whose encoding genuinely needs padding.

## Wire temperature solved from a DC or large-signal run (2026-10-05, brief-wbond-wire-temperature)

A solved-mode wBond (`FixedTemp=false`, what a new placement writes) reports `WireTemp`/`WireTempState` from every
DC, HB, loadpull and pursuit run; the solver is `Thermal/WireConductiveBalance.cs`, the orchestration
`Thermal/WBondWireTemperature.cs`, the engine side `src/Engine/WireTemperatureCubes.cs`. Design: `docs/design/wbond.md`
§5.6. What is worth knowing:

- **Element count: 16 per wire**, by measurement (`WireTemperatureTests.ElementCount_Converged`): a 1 mil, 1 mm gold
  wire at 1.7 A DC plus harmonics, both tables on, reads 566.194 / 566.260 / 566.258 °C at 8 / 16 / 32 elements —
  8 → 16 moves it 0.066 K, 16 → 32 moves it 0.0026 K. The maximum is taken from each element's quadratic, not only its
  nodes, so a peak falling between two nodes is not read low.
- **Cost per point** (scratch harness, Release, Apple M4; 6 wires in 2 arrays, DC plus 5 harmonics): **3.9 ms cold,
  0.83 ms warm** (the previous point's state), 0.12 ms with no current. The brief expected well under a millisecond;
  cold is not. Nearly all of it is the exact Bessel R′_ac at every quadrature point of every wire at every harmonic
  (720 evaluations per assembly here, ~5 assemblies cold). `InternalImpedance.ResistanceWithSigmaSlope` ran the
  continued fraction twice — value and slope — and now runs it once (`NormalizedZWithSlope`, bit-identical): that
  halved it from 7.2 / 1.6 ms. Tabulating R′_ac would cut it further and would no longer be the 3D run's own
  evaluation, so it was not done. Against that, the whole 10-point `Amplifier Wires` sweep runs in 0.4 s.
- **Gold runs away just below 2 A** in a 1 mil, 1 mm wire held at 125/85 °C: it reaches the σ(T) table's top row
  (1,027 °C, melting), where a state stops being physical. A test drive of 2.2 A that "should" have been merely hot was
  past it.
- **A warm start from a lower drive must take the new drive's current split first.** From a zero-current state the
  line search's heat scale is round-off, so every step that heats the wire looked like a blow-up and was refused down
  to nothing. `ShareCurrent` now runs on every start (the predictor); a drive with no current at all always starts
  from the exact cold state, which is its answer.
- **With unequal ends a gently heated wire reads its hotter end exactly.** Two wires sharing 1 A at 125/85 °C never
  lift an interior point above 125 °C, so `WireTemp` is 125.000 — correct, and it read like a bug in the first test.
  The engine gates hold both ends equal where they need the heat to show.
- **The constants are restated, not referenced**: `src/WBond` is a leaf and the brief allowed no new reference, so
  `Absurd`, `StallStep`, `SpanFloorK`, the Newton tolerance and iteration limit, and `Bracket` are copied, and
  `tests/Thermal.Tests/WireTemperatureCrossCheckTests.TheRestatedConstants_AreConductiveBalances` holds each equal to
  its original (`SpanFloorK` by reflection — it is internal). G3 in the same file: one gold wire, DC plus a 2 GHz
  harmonic, against `ConductiveBalance` on the W1 bench (no Gmsh) — 268.628 °C against 268.620 °C.

### Four things found on the way, outside the solver

- **`__WireTempState` would have been wrong in every sweep.** `DataSet.StackSweepAxis` passes a `__` cube through
  UNSTACKED — point 0's copy for every point — so the brief's metadata spelling would have reported the first point's
  states beside a later point's NaN. The cube is `WireTempState`, a per-point result like `Converged`.
- **`DataCube.PrependAxis` dropped the value unit**, and so did `ParametricSweepEngine`'s ragged-grid pad: a swept
  `WireTemp` lost its °C. Both now keep it (stacking only when every point states the same one).
- **`GroundPlane=true` from the Inspector's picker never reached the model.** The elaborator evaluated it as an
  expression, `true` failed to resolve, the catch dropped it, and the instance took its payload's own plane — so a
  `.wBond` stored with the plane off (the Output Wires example's `Pads.wBond`) was refused for an undeclared return
  path whatever the picker said. A boolean spelling now passes verbatim, as `IncludeCapacitance` does; anything else
  (a `VAR`) is still evaluated. Gate: `WBondWireTemperatureTests.GroundPlaneWrittenAsAWord_ReachesTheModel`.
- **Two- and multi-tone HB already solve the full network at every mixing product** (the IProbe currents come from
  it), so the wBond's branch rows are read there rather than computing Z_arr⁻¹·(V_in − V_out) a second time. An
  IProbe in series with a capacitance-on wBond is NOT the wires' current — it also carries the input shunt — so the
  engine gates that compare against a probe switch capacitance off.

Pursuit carries `WireTemp` through its follow-on loadpull, the only place it has a Pout layout; a pursuit run with
`CreateLoadpullResult=false` has neither cube. The `New Material…` record already has a ThermalK field (blank, since it
copies no metal; Duplicate copies it), so R-wbt-1c needed no code.

### Review follow-up (2026-10-05)

- **A pursuit's search paid for wBond currents it never read.** `LoadpullEngine.SolveAt` read every solved wBond's
  branch rows (K+1 full-network back-solves) on every Pin step, and the pursuit's search calls `RunOneTermination`
  directly, query after query, without ever building `WireTemp`. Collection is now on only inside `LoadpullEngine.Run`
  (`_collectWBondCurrents`), which is also what the follow-on loadpull is, so the pursuit's `WireTemp` is unchanged.
- **The Inspector's checkbox read `FixedTemp` with its own spelling list** (`false`/`0` only) while the factory reads
  `BooleanParameter`'s (`off`/`no` too): `FixedTemp=off` ran solved under a checked box. It reads
  `BooleanParameter.FalseSpellings` now.
- **The GroundPlane-as-a-word branch had also landed in `ResolveMatchParameters`**, which has no GroundPlane; removed.
- **State 3 now says so in the Messages panel** (overturning D5's "no message of its own"). A DC run whose only return
  for a current source was a `Term` — open in DC — did not converge; the wBond showed `WireTemp` NaN beside
  `WireTempState` 3, and the circuit's own non-convergence message names nodes, not the wBond, so the two were not
  connected. `WireTemperatureCubes.Compute` warns once per solved instance per run, naming the point.
  Gate: `WBondWireTemperatureTests.ACircuitThatDoesNotConverge_IsState3_AndSaysSoByName`.

## Silicon by resistivity is not a wire metal (suite review, 2026-11-10)

- SI-SIG added four silicon grades to the shipped library with `Epsr` AND `Sigma20`, and `WireMaterials.ReadLibrary` took
  every record stating `Sigma20` as a metal, so the four were offered as bond-wire metals. A record stating a permittivity
  is a dielectric with a conductivity, the rule `Em3dLayoutSolids` applies to the same record; it is listed among the
  library's non-conductors, and the refusal a wire asking for one gets now says "or as a dielectric (it states εr)".
  Gates: `WireMaterialTableTests`, `WBondTemperatureAndMaterialTests`.
