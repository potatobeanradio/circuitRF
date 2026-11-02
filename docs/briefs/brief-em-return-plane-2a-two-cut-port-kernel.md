# Sonnet Brief — RP-2a: a port cut in two conductors

**Read `brief-em-return-plane-2-per-port-reference.md` first — this is one third of it**, together
with RP-2b (the layout exposure) and RP-2c (de-embedding a coplanar edge port). RP-2 was measured on
2026-09-10 and then split: it is a kernel capability, a document/format change and a calibration
rebuild, and merging them makes the bit-identity gate that protects the whole L8/L9 acceptance set
impossible to run early.

**Scope: the ENGINE only.** `PlanarPort` learns a second terminal, `PlanarPortResolution` learns a
second row, and `PlanarExcitation`'s incidence column learns a second signed block. Nothing in
`src/Design`, nothing in the `.clay`, no UI. Every RP-2a test constructs its ports directly, the way
`MultiLevelPortTests` and `InternalDeltaGapPortTests` already do.

---

## 1. The measurement that shaped this brief — read it, do not re-derive it

RP-2's R-rp2-3 asked whether the surface mesher produces cells spanning a slot between two
conductors. **It was measured and the answer is no, structurally.** Full numbers and the three-line
reason are in `src/Engine/Mom/RESOLVED.md` ("RP-2's first measurement"); the instrument is
`tests/Engine.Tests/Mom/CoplanarSlotMeshTests.cs` and it runs in 20 ms. The short form:

> Every polygon edge is a hard gridline, a cell exists only where a grid row's centre is inside
> metal, and a basis is a pair of grid-adjacent cells. A slot of nonzero width therefore always owns
> at least one empty grid row — a 50 µm slot does so even at a 600 µm bulk cell — and refining the
> mesh **adds** rows to the slot. There is no threshold a finer mesh crosses.

**R-rp2a-1. Do not attempt to make a basis span the slot.** A basis over non-metal is a new basis
family (a slot / magnetic-frill unknown) with its own singular-integral treatment and its own charge
bookkeeping, and it is a much larger question than a per-port reference. If the work drifts there,
stop and report. RP-2's own R-rp2-1 says the same thing about a second PEC and this is its twin.

---

## 2. What a conductor-referenced port IS here

**Two cuts, one in each conductor, driven against each other.** The + terminal is a rooftop row
spanning a real delta gap in the signal conductor — exactly what a port is today. The − terminal is a
second rooftop row spanning a real delta gap in the RETURN conductor, at the same station. The
incidence column has a signed block on each.

Nothing else changes: same mesh, same basis set, same `Z`, same factorisation, same
`Y = BᵀZ⁻¹B`. R-rp2-3's actual requirement — *"the cut must be a real gap in the mesh, not a pair of
points"* — is satisfied **per conductor**, which is the only way it can be satisfied at all.

**R-rp2a-2. The two cuts must be at the same station, and a skewed pair is a refusal by name.** A
port whose signal cut is at x = a and whose return cut is at x = b is a port plus a length of line,
and it produces a complete, plausible s-matrix for a structure nobody drew. Refuse, naming both
coordinates and the distance between them.

**R-rp2a-3. The two cuts must be on the same LEVEL unless the level was stated for each.** L9d made a
port's level part of its identity for exactly this class of reason; two terminals is two chances to
land on different levels silently. Reuse `TryResolveOnLayer` and the ambiguity refusal it already has
— do not write a second resolution rule (RP-2's R-rp2-2).

**R-rp2a-4. The two terminals must be different conductors.** Both cuts landing on the same rooftop
row is a short; both landing on the same polygon is an `InternalDeltaGap` port wearing a costume.
Refuse by name in both cases, and say which.

### 2a. The normalisation is MEASURED, not reasoned into place

±1 on both blocks impresses twice the loop voltage that ±½ does, and the two answers differ by a
clean factor of two in `Z_c`. That is a complete, plausible, wrong result with no visible symptom —
the same shape of failure `src/Engine/Mom/RESOLVED.md` already records for the internal port's SIGN,
which was derived backwards in prose and caught only by a structure with a known answer.

**R-rp2a-5. Gate the normalisation against an independent closed form before believing it**, and say
in the code comment which measurement fixed it. §5's gate 2 is that measurement.

**R-rp2a-6. The same B is used for the excitation and for reading the current back.** This is already
`PlanarExcitation`'s own headline rule and it now has to hold over two blocks rather than one. The
port current is the signed sum over BOTH index sets. A code that impresses over two blocks and reads
back over one is a plausible, wrong `Y` that is symmetric anyway, so reciprocity will not catch it.

---

## 3. What must keep working

**R-rp2a-7. Every existing port is a `GroundPlane` port and must be bit-identical. Gate this FIRST,
before anything is built.** Extract and solve an existing multi-port fixture, dump the s-parameter
matrix, and compare exactly — not to a tolerance — before and after. The whole L8/L9 acceptance set
is ground-referenced.

**R-rp2a-8. The medium is untouched.** No PEC is added, no plane moves, `BuildMediumStack` is not
opened. If a change there appears necessary the design has drifted into RP-2's R-rp2-1.

**R-rp2a-9. The audit is already done — use it.** The one-signed-row assumption is spelled in exactly
three places, all in `PlanarExcitation` (`RightHandSide`, `Solve`'s Y assembly, `PortCurrent`). See
`src/Engine/Mom/RESOLVED.md`. If a fourth turns up, **that is a finding worth more than the feature**
and it must be reported rather than quietly fixed.

**R-rp2a-10. `PlanarPortResolution` grows; it does not fork.** `WidthM`, `ReferencePlaneM`,
`OuterEdgeM`, `TransverseLines` and `LongitudinalRunM` are all singular today and `PlanarCalibration`
copies `TransverseLines` verbatim (D4). Add the negative terminal's counterparts as a nullable
companion so a ground-referenced resolution is the record it already is, field for field.

---

## 4. Which port kinds this brief ships

**`InternalDeltaGap` only.** An internal cut has no feed outside it, therefore no error box, therefore
no calibration standard to build (`PlanarPort.cs`: *"there is no feed here to remove"*). That is the
whole of RP-2's own §2c fallback and it is the half that can ship correctly on its own.

**R-rp2a-11. A conductor-referenced EDGE port stays refused, and the refusal names where the
capability arrives** — RP-2c, and by name, not by phase number (R-mom-17). A coplanar edge port's
error box is a coplanar line with its own `Z_c`, its own β and its own static capacitance; calibrating
it against the uniform microstrip standards `PlanarCalibration` builds today produces s-parameters
that are plausible and referenced to nothing, which is strictly worse than a refusal.

**R-rp2a-12. `PlanarPortReference.SecondConductor` and `.CoplanarGround` are the same mechanism here**
and both are built. They differ in what the user MEANS, not in what the kernel does: one names a
ground strip, the other names a second signal line. Do not build two code paths for one object — but
do keep the two enum members, because RP-2b's notes and RP-2c's calibration will read differently for
them.

---

## 5. Gates

1. **R-rp2a-7's bit-identity, run before a line of feature code is written.** An existing multi-port
   fixture's S-matrix, exactly equal before and after.
2. **A coplanar structure against a closed form.** A conductor-backed CPW of known geometry driven by
   two `InternalDeltaGap` ports referenced to the coplanar grounds, compared against an analytic CBCPW
   impedance. **This is the one oracle here independent of circuitRF**, and it is what fixes §2a's
   factor of two. State the closed form used and its own accuracy.
3. **RP-2's R-rp2-7 mixed run.** Port 1 to the plane, port 2 to a drawn conductor, one solve, one
   matrix, one medium — and assert that port 2's reference **changed the answer** relative to the same
   geometry with both ports on the plane. A change of zero means the second block never reached the
   incidence matrix.
4. **Reciprocity and passivity** of the S-matrix on a lossless passive fixture. A wrongly-signed second
   block shows up here and almost nowhere else.
5. **Each of R-rp2a-2, -3 and -4 refuses, and each refusal names the coordinates and the remedy.**
   Assert the sentence, not `Ok == false`.
6. **R-rp2a-11's refusal exists and names RP-2c.**

---

## 6. On completion

Findings to `src/Engine/Mom/RESOLVED.md` — **never a `CLAUDE.md`**. Update `docs/design/mom-engine.md`
§10.6, whose "Ground reference … *(v1 builds the ground-plane reference only, for BOTH port types; the
other two are refused by name.)*" is the sentence this brief makes half-false.

**Report, do not silently absorb:**
- **The gate-2 number and which normalisation it selected**, first. If the closed form and the solve
  disagree by anything other than 1 or 2, say so and stop — that is not a scaling question.
- Whether R-rp2a-9's audit turned out to be complete.
- Whether the two cuts could in fact be placed at the same station on every fixture tried, or whether
  the mesher's own gridlines forced a skew. If they did, R-rp2a-2's refusal fires on correct input and
  the brief has found something.
