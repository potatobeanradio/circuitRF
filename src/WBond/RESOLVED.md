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
