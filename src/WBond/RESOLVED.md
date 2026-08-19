# src/WBond — resolved work (detail, off the CLAUDE.md growth path)

One `##` section per piece of completed work, sparingly — only findings that are still true, still
surprising, and would cost someone real time to rediscover. Mirrors `src/WBond/Mom/RESOLVED.md` and
`src/Ui/RESOLVED.md`.

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
