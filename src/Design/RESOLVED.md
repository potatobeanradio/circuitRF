# src/Design — resolved findings (detail, off the CLAUDE.md growth path)

## R-em-4's ground query returns null for TWO reasons, and the note claimed the wrong one (2026-08-30)

`PlanarExtractor` resolves the EM ground as **the highest ground-designated conductor BELOW the
lowest analysis level**. When that query comes back empty and `Stackup.Bottom == Ground`, it fell
back to the bottom of the stack and said:

> No conductor layer in technology 'X' is marked as a ground reference, so the ground plane was taken
> from Stackup.Bottom = Ground at the bottom of the stack.

**That sentence is only true for one of the two ways to reach it.** The query is scoped to
conductors *below the signal*, so it also returns null on a stackup that HAS a designated ground
sitting *above* — and there the message is flatly false, contradicted by a ticked checkbox on the
Stackup tab the user is looking at.

**It survived because no shipped technology could reach the false branch.** Every PCB starter was
2-layer and the MMIC's ground is its backside metal, so the only ground candidate was always the
bottom conductor: "none below the signal" and "none at all" were the same statement. The first
technology with an INNER ground plane (`pcb-4layer_FR-4_62mil_1oz`, added the same day) made them
different, and a trace on a lower layer was told its technology designates no ground at all. Worse
than the wording: the run **succeeded**, solving against a reference further away than the real one,
so there was no refusal to prompt anyone to look.

The fallback now asks which case it is and names the planes it did find, says why they cannot serve
(a port returns through a plane BENEATH the conductor it feeds), and states the cost — the reading
will be a higher impedance than the real structure. The original sentence is kept verbatim for the
genuinely-undesignated case.

### Two neighbouring messages were wrong in the same way — advice for a situation that was not this one

- **The zero-height slab refusal** said *"Check the stackup order in the technology editor."* The
  commoner way to arrive there is a correctly-ordered board whose BOTTOM conductor is being treated
  as the signal: it rests on the `Stackup.Bottom = Ground` boundary, so the slab has zero height and
  nothing is misordered at all. That case is now named, with the two things that actually help
  (mark it as a ground reference, or move the trace up a layer).
- **The no-signal-conductor refusal** said *"Draw the artwork on a conductor layer, or bind the layer
  it is on to a conductor entry."* When every shape is on a ground-designated conductor the layer IS
  bound — the advice sends the user to redo something already done. Reachable on any stackup with
  more than one plane (a 4-layer board whose only artwork so far is an inner pour), so it now says
  the plane is not meshed and points at the "Ground reference" tick.

**None of the three was found by reading the extractor.** They were found by running it on each
conductor of the new 4-layer technology in turn and printing the result — a scratch xunit probe, run
once and deleted. A message can only be checked against the state that reaches it.

Gated by `tests/Ui.Tests/Em/FourLayerGroundReferenceTests.cs`, which drives the extractor rather than
scanning source, and includes the negative: the 2-layer starter must reach neither new branch.

## A board outline refused the EM run, and the dielectric binding was the workaround (2026-08-30)

User proposal: remove the dielectric's "Drawing layer" control from the `.ctech` editor, since the
binding is never used except under the hood. **The premise was wrong and the conclusion was right,
for a reason neither of us had.**

### What the binding actually did

Nothing electrical: `PlanarExtractor.BuildMediumStack` reads only `Epsr`/`TanD`/`Mur`/`ThicknessDbu`
and every dielectric is a laterally infinite slab, so a dielectric bound to `(none)` is everywhere.
Every other consumer filters it out — `WBondClearance` reads `DrawingLayers` only after
`if (sl.Kind != StackupKind.Conductor) continue`, `PcbLayerNaming`/`DrcConnectivity`/`GerberExport`
take conductors and vias, `PcbWriter` writes dielectric thickness with no layer reference, and in
`PlanarExtractor` a dielectric-bound and an unbound shape reach the same `ignoredOther`.

Its ONE effect was in `CrossSectionExtractor.Classify`, and it was not subtle. Measured on the MMIC
starter with a Metal1 trace plus a die outline on `Substrate`: binding kept → `Ok=True` with a note;
binding removed → **hard refusal.** The field was the difference between the run working and failing.

### The defect underneath: the refusal fired on the normal case

Sweeping every layer of the shipped 2-layer PCB starter, one shape at a time beside a solvable trace:

| Layer | Result |
|---|---|
| Top Copper, Bottom Copper, Drill | Ok |
| Soldermask Top / Bottom, Silk Top / Bottom, **Outline** | **REFUSED** |

**Every PCB layout has a board outline**, so the failing case was the normal one — and the refusal's
advice was *"add this drawing layer to a conductor entry's DrawingLayers list"*, i.e. declare your
board outline to be copper. The dielectric-`DrawingLayers` binding was a narrow escape hatch from
this, applied only where the MMIC starter tripped over it.

### The discriminator was available and was not being asked for

A layer the technology **declares** but binds to no stackup entry is the technology stating the layer
is not metal. Silk, soldermask and outline are exactly that. A layer the technology **does not
declare at all** — a foreign import, a hand-edited file — is the case nobody has said anything about,
and there the original reasoning holds in full.

So the refusal is narrowed, not deleted: declared-but-unbound is ignored with a note that names every
distinct layer once and still offers the fix (*"If one of them IS metal, bind it to a conductor entry
on the Stackup tab"*); undeclared still refuses, now pointing at the Layers tab rather than telling
anyone to call it copper. Ignoring is REPORTED, never silent — a trace genuinely drawn on a forgotten
layer is still visible in the run's own output.

With the workaround unnecessary, the editor's dielectric picker is gone
(`StackupLayerRowViewModel.ShowsDrawingLayerPicker`, via only). **The model field stays**: shipped and
user `.ctech` files carrying a dielectric binding still parse, validate, round-trip through
`TechnologyMerge`, and take their original more-specific "substrate extent" note — removing a control
must not rewrite anyone's file. `IsSingleDrawingLayer` is deliberately left answering `true` for a
dielectric, because the CARDINALITY rule did not change.

Gated by `tests/Ui.Tests/Em/UnboundLayerArtworkTests.cs` (10 tests), including both halves that make
this safe rather than merely permissive: the MMIC die outline extracts with the binding removed, and
a file that still carries one behaves exactly as before.

**None of this was visible by reading the extractor.** It came from running it on each layer in turn
and printing the verdict — a scratch xunit probe, run once and deleted. The same method found the
ground-reference bug above. A refusal can only be checked against the state that reaches it.

## A laterally-finite dielectric cannot be drawn, because the kernel cannot represent one

Asked while the section above was being investigated: how does a user simulate a MIM cap built on a
GaAs substrate, if the dielectric is always everywhere? Surely the nitride must be drawn on a layer.

**It cannot be, and no binding would have helped** — this is a formulation limit, not a missing
feature. `BuildMediumStack` produces a `LayerStack` of `MediumLayer(thickness, material)`: a 1-D
stack of laterally infinite slabs, and the DCIM Green's function is derived from exactly that stack.
Unknowns live on conductor surfaces and via barrels only. A nitride island under a top plate needs
either volume-equivalent currents inside the dielectric (a VIE) or a surface-equivalence formulation
on its boundary, and neither exists in `src/Engine`'s planar kernel.

Drawn dielectric geometry is therefore ignored — before the change above it fell into
`PlanarExtractor`'s `ignoredOther`; it is now named in the declared-but-unbound note. Reported, but
inert either way. Note also that the MMIC starter's own `Cap Dielectric` and `Nitride` drawing layers
are bound to no stackup entry at all: they are artwork/DRC/GDS layers, and their presence must not be
read as EM support.

What actually works, best first: a **lumped C in the schematic** (C = ε₀εᵣA/d from the process's
capacitance density, with the EM run covering the interconnect around it — the normal MMIC flow); or
**stating the inter-metal dielectric as nitride** in the stackup and meshing both metal levels, which
gets the plate overlap right out of the solve but puts every airbridge and crossover in the same run
in nitride instead of air; or **splitting the run**, EM for the passive interconnect and lumped caps
combined in the schematic.
