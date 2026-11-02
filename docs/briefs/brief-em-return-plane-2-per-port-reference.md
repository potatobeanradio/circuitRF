# Sonnet Brief — RP-2: a port referenced to drawn metal

> **SPLIT, 2026-09-10, after its own first measurement was made. Do not start from this file — start
> from one of the three below.** RP-2 asks for a kernel capability, a document-format change and a
> calibration rebuild in one round; keeping them together makes R-rp2-5's bit-identity gate, which
> protects the whole L8/L9 acceptance set, impossible to run early.
>
> - **`brief-em-return-plane-2a-two-cut-port-kernel.md`** — the engine. The two-cut port,
>   `InternalDeltaGap` only, edge ports refused by name.
> - **`brief-em-return-plane-2b-port-reference-in-the-layout.md`** — `LabelShape`, the `.clay`, the
>   extraction, the per-port note, `explain`, the Port tool.
> - **`brief-em-return-plane-2c-coplanar-deembedding.md`** — the coplanar calibration standard, which
>   is 2a's refusal arriving.
>
> **R-rp2-3 was MEASURED first, as this brief demands, and it changed the design.** The mesher does
> not produce cells spanning a slot and structurally never will: a slot of nonzero width always owns
> at least one metal-free grid row, so refining adds rows rather than removing the last one. §2b's
> "the gap is across the SLOT and the mesh must carry cells that span it" is therefore not the shape
> the excitation takes — it is **two cuts, one in each conductor, driven against each other**, which
> needs no new basis family and no mesh change. Numbers, reason and the audit §6 asks for are in
> `src/Engine/Mom/RESOLVED.md`; the instrument is `tests/Engine.Tests/Mom/CoplanarSlotMeshTests.cs`.
> Everything else below still stands and the three briefs carry its requirements forward by number.


**Read RP-1 first** (`brief-em-return-plane-1-explicit-ground-layer.md`). RP-1 is a `.cem` field and
no kernel change; **this brief is a kernel capability and is much larger.** They are separate because
one is a week and the other is not, and because RP-1 must not wait on this.

**Scope: `PlanarPortReference.CoplanarGround` and `.SecondConductor` — a port whose negative terminal
is a MESHED conductor rather than the laterally-infinite plane.** Not a differential-mode
decomposition, not multi-mode ports, not co-simulation ports; §10.6 lists those and they stay there.

---

## 1. The question this answers, and the one it does not

**Asked, 2026-09-10: can the user specify the return layer per port, so different ports have
different return paths?**

**Not as a ground PLANE, and the reason is structural rather than a missing feature.** The ground
plane is not an attribute of a port. It is a boundary condition of the medium: it appears inside
every `G(r, r')` evaluation, i.e. inside every entry of the MoM matrix, and the ports are
`Y = BᵀZ⁻¹B` layered on top of that one matrix. Concretely, `PlanarExtractor.BuildMediumStack`
finishes with

```csharp
return (new LayerStack(Termination.Pec, layers, Termination.Air), levelZ, note);
```

and a `LayerStack` has exactly two terminations, bottom and top. `TerminationKind.Pec` can sit at
either end and **nowhere in between**: there is no interior PEC, so a second reference plane has no
representation at all, with or without ports. `PlanarPort.cs`' own D3 says the same thing in the
kernel's vocabulary — `PlanarPortReference.GroundPlane` is "the only one L8's kernel can represent",
and the other three enum members exist so the refusals can name them.

**What IS reachable is the same capability by the other route: a port referenced to metal that is
MESHED.** `PlanarPortKind.InternalDeltaGap` already drives a delta gap between two pieces of meshed
metal with no ground involvement — it is the same incidence matrix and the same `Y = BᵀZ⁻¹B`, cut
somewhere else. A port between a signal conductor and a drawn return conductor is that object with a
second terminal that is not the plane. Different ports then genuinely have different return paths,
paid for by meshing the return conductor instead of getting it analytically.

**R-rp2-1. This brief must not be reframed as "multiple ground planes".** If the work drifts toward a
second PEC in `LayerStack`, stop and report — that is a different and much larger question about the
spectral kernel's branch structure (L9b's own findings are the relevant precedent), and it is not
what a per-port reference needs.

---

## 2. What has to be built

### 2a. The reference reaches the port

`PlanarPort` already carries `PlanarPortReference`; today every construction site passes
`GroundPlane` and `PlanarPorts.ViaPortRefusal` refuses the rest by name. The reference must become a
real input: for `CoplanarGround` and `SecondConductor`, the port additionally names **which meshed
metal is the negative terminal**.

**R-rp2-2. The negative terminal is identified the way the positive one is** — a point, resolved
against the mesh, refused by name when ambiguous. `EmPortExtraction.NearestPolygon` already does this
for the positive terminal and reports how far the chosen gridline landed from the point asked for.
Reuse it; a second resolution rule is a second chance for the two terminals to land on different
levels without anyone noticing.

### 2b. The excitation

A ground-referenced port drives one rooftop row against the plane. A conductor-referenced port drives
the same row against **another row**, so the incidence column has two signed entries instead of one.

**R-rp2-3. The cut must be a real gap in the mesh, not a pair of points.** A delta gap is defined
across a shared cell edge; a "port" between two conductors that share no edge is a lumped assumption
about the field between them, which is exactly the thing full-wave exists not to assume. Where the
two conductors do not adjoin — the ordinary coplanar case, signal and ground separated by a slot —
the gap is across the SLOT and the mesh must carry cells that span it. **Whether the existing mesher
produces such cells is the first thing to measure, before any solver work**; if it does not, that is
the real content of this brief and the excitation is the easy half.

### 2c. De-embedding

**R-rp2-4. A conductor-referenced EDGE port has an error box like any other, and the calibration
standards must be built in the same reference.** `PlanarCalibration`'s two-line standards are
currently uniform lines over the plane. A coplanar port's standard is a coplanar line — different
`Z_c`, different `β`, different static capacitance. Getting this wrong produces s-parameters that are
plausible and referenced to nothing.

**If that is too large, restrict this brief to `InternalDeltaGap` ports, which have no error box by
construction** (`PlanarPort.cs`: "there is no feed here to remove"), ship that, and leave the edge
case refused by name. **A refusal that names where the capability arrives is a legitimate outcome for
half of this brief** — R-mom-17 — and is much better than a de-embedding that silently references to
the wrong standard.

---

## 3. What must keep working

**R-rp2-5. Every existing port is a `GroundPlane` port and must be bit-identical.** The entire L8/L9
acceptance set — the phase gates, the de-embedded points, the Hero comparisons — is ground-referenced.
Gate this first, not last: extract and solve an existing multi-port fixture before and after, and
compare the s-parameter matrices exactly, not to a tolerance.

**R-rp2-6. The medium is unchanged.** This brief adds no PEC, moves no plane, and does not touch
`BuildMediumStack`. If a change there appears necessary, the design has drifted into R-rp2-1.

**R-rp2-7. A mixed run is legal and is the interesting case.** Port 1 to the plane, port 2 to a drawn
conductor, in one solve, one matrix, one medium. That is what "different ports, different return
paths" actually means here and it must be a test, not an inference.

---

## 4. Where it is exposed

**R-rp2-8. The port's reference is a property of the port label**, not of the `.cem`. A port is drawn
in the layout; its return is part of what it IS. Adding it to the `.cem` would put two ports' identities
in two files and make a layout non-portable between setups. Additive on `LabelShape`, null = the
plane, no `FormatVersion` bump — the same shape `PortDirection` and `PortLayer` already have.

**R-rp2-9. The existing "Every port returns through …" note becomes per-port** where the ports differ.
Its current wording — "That plane is the negative terminal of every port in this run and is not
selectable per port" — is the sentence this brief makes false, and it must not be left standing.

---

## 5. Gates

1. **R-rp2-5's bit-identity**, run before anything else is built.
2. **A coplanar structure against a closed form.** A CPW line of known geometry, compared to an
   analytic CPW impedance — the one oracle here that is independent of circuitRF. If the mesher
   cannot span the slot (R-rp2-3), this gate is what says so, and reporting that is a complete result
   for this brief.
3. **R-rp2-7's mixed run**, asserting that port 2's reference actually changed the answer relative to
   the same geometry with both ports on the plane. A change of zero means the second terminal is not
   in the incidence matrix.
4. **Reciprocity and passivity** of the resulting S-matrix on a passive fixture. A wrongly-signed
   second incidence entry shows up here and almost nowhere else.
5. **The de-embedding decision is stated either way**: either R-rp2-4's standards are built and a
   two-line calibration against a coplanar standard is gated, or edge ports with a conductor
   reference are refused by name and the refusal says where the capability arrives.

---

## 6. On completion

Write the findings to `src/Engine/Mom/RESOLVED.md`, and any extraction-side findings to
`src/Design/RESOLVED.md` — **never to a `CLAUDE.md`**. Update `docs/design/`'s EM notes and §10.6's
list, which is where these port kinds are currently promised.

**Report, do not silently absorb:**
- **The R-rp2-3 measurement**, first and in full: whether the surface mesher produces cells spanning a
  slot between two conductors. Everything else in this brief depends on it and it is measurable
  before a line of solver code is written.
- Whether R-rp2-4's calibration was built or the edge case refused, and why.
- Any place `PlanarPortReference.GroundPlane` was assumed rather than checked. The enum has existed
  since L8 with three members that never occurred; code written under "there is only one" is exactly
  what this brief invalidates, and finding it is worth more than the feature.
