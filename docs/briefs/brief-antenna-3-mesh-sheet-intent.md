# Brief — ANT-3: the sheet intent, for metal that is a radiator rather than a line

**Series:** `brief-antenna-0-overview.md` §1b(3). **Depends on:** ANT-2. **Blocks:** nothing, but it
is what makes an antenna mesh land in the hundreds of unknowns rather than the thousands.

`TransmissionLineMesh` asks *which way is the current going* and **declines when it cannot tell**.
A wide radiating sheet is the case it declines by construction, and it is also the case where the
question does not apply: on a patch the current varies on the scale of a wavelength in **both**
directions. Add the second intent.

---

## Testing rule

Mesher tests only — see ANT-2's rule, which applies unchanged.

---

## 1. Why the existing field declines, and why that is right

`PlanarMeshSettings.TransmissionLineMesh` takes its direction from the vector between a two-port
problem's ports; failing that, from the principal axis of the artwork's own area moment; and when
both exist and disagree by more than `DirectionAgreementDegrees` (15°) it **declines and meshes as
before, saying so**. The reasoning in its own doc comment is sound: "a bent or branched structure has
no single current direction and inventing one would under-resolve a real bend."

A patch fails this in two ways at once. It is a one-port, so the port vector is unavailable and the
area moment is the only source; and the area moment of a 41.3 × 49.4 mm rectangle is a direction in
name only — it reports the longer side, which on the measured board is **perpendicular to the feed
and to the resonant dimension**. The mesher then applies the wavelength pitch along the wrong axis
and the width pitch across it.

That it still helped enormously (704,482 → 23,416) is a measure of how bad the per-axis rule was, not
evidence that the direction was right.

## 2. What a radiating sheet actually wants

Stated once, because it is the entire specification:

- **The interior of a wide sheet needs λ_g/N and nothing more, in BOTH axes.** The dominant patch
  mode current is a half-cosine along the resonant dimension and near-uniform across the width —
  smooth on the scale of a wavelength, with no singularity in the interior to resolve.
- **The rim wants graded refinement**, and more so than a line does. The two radiating edges set the
  effective length, hence the resonant frequency, hence every number downstream; the two
  non-radiating edges carry the transverse 1/√d singularity. ANT-2 is what makes that affordable; this
  brief must not spend it back.
- **The feed wants its width resolved, locally.** `MinCellsAcrossConductor` where the metal is
  genuinely narrow, and nowhere else.
- **There is no "along" and no "across".** Removing the direction question is the change.

So: **bulk pitch = λ_g/`CellsPerWavelength` in both axes, floored locally by
local-width/`MinCellsAcrossConductor` only where the local width is genuinely below that.** Same
`PlanarMeshPitchField`, same sampling, same Lipschitz lower envelope, same aspect cap — the direction
term drops out and the field never declines, because there is nothing left to be ambiguous about.

## 3. M1 — how the intent is spelled

Two intents now exist and **they are mutually exclusive**: metal is being meshed as a line or as a
sheet, not both. Two independent booleans that can both be true is a trap, and `Auto` must not
discard either (settled taxonomy: `Auto` chooses a *resolution*, and neither of these is one).

**Recommended shape:** a `PlanarCurrentModel { None, TransmissionLine, Sheet }` on
`PlanarMeshSettings`, replacing the bool in the settings record, with `None` the default so every
recorded number stays reproducible.

**The persistence migration is the part to get right, and it is a named decision.**
`brief-em-transmission-line-mesh.md` §M4 put `PlanarMesh.TransmissionLineMesh` in the `.cem` under the
omit-at-default rule (a pre-phase file gains no byte). So:

- write the new key; read the legacy `TransmissionLineMesh: true` as `TransmissionLine` when the new
  key is absent;
- a file carrying **both**, disagreeing, is a **refusal naming both keys** — not a precedence rule.
  A precedence rule here means a user who set one control watched the other one win, which is the
  defect the transmission-line brief existed to fix;
- omit-at-default still applies, so an existing `.cem` gains no byte and its hash is unchanged.

`EmSnpProvenance.MeshHash` includes it. UI: this is a three-way choice in the mesh group where a
checkbox is today, and the label is about the *structure* — what this metal is — not about the
algorithm.

## 4. M2 — the field without a direction

- Sample local width on the existing field grid (`FieldSamples`, unchanged — it is fixed rather than
  derived so the answer does not depend on the mesh it is about to produce).
- Per axis: `cap = λ_g / CellsPerWavelength`; floor by `localWidth / MinCellsAcrossConductor` only
  where that is finer. **Never coarser than the per-axis rule would have given** — same one-way
  invariant `PlanarMeshPitchField` and `LocalConductorWidth` both already state, so the cell count is
  bounded above by today's on every input.
- Smooth with the same Lipschitz lower envelope at the same bounded growth ratio. That is what
  restored exact translation invariance for the line field and it is load-bearing here too.
- **It never declines**, so it needs no `Ok`/`Note` decline path — but the note must still say what it
  did: the bulk pitch in each axis, where the floor bound, and the worst aspect.

## 5. What it should come out at

Arithmetic, not a measurement — the implementer's job is to produce the measurement and report it
against this.

The measured patch is 41.3 × 49.4 mm; λ_g at 1.74 GHz on εᵣ 4.4 is ≈ 83 mm, so λ_g/20 ≈ 4.2 mm →
10 × 12 cells in the bulk. With ANT-2's bounded fan at three cells on four rims and a locally
resolved 349 µm feed, the expectation is **order 500-900 unknowns**, seconds per frequency point,
against 704,482 refused today and 4,854 with the edge mesh off.

**If it does not land in that range, that is the result and it goes in the write-up.** Do not tune the
default until the number is understood — the non-monotonicity in `MinCellsAcrossConductor` was
mis-diagnosed once as the edge fan spending back what the bulk saved, and was actually a marcher bug.

## 6. Gates

All mesher-only.

- **Translation invariance** — 3.7 mm, hard gate, as in ANT-2.
- **The one-way invariant**: for every fixture in the mesher's existing set, Sheet's cell count is
  ≤ the per-axis rule's. Assert it, do not assume it.
- **A plain uniform line under Sheet must not get worse** than under the per-axis rule. This is the
  safety property that says the intent cannot be catastrophically mis-chosen — a line meshed as a
  sheet is coarse along its length and correctly fine across it, which is exactly the per-axis rule.
  (A *sheet* meshed as a line is the mis-choice that hurts, and that is what the report is for.)
- **Mutual exclusion is a refusal, tested from a `.cem` carrying both keys**, and the legacy key
  round-trips.
- **Both intents survive `Auto`**, asserted, like the four controls before them.
- `MaxCellAspect` still binds; the bounded growth ratio still holds.
- `MeshHash` includes the intent.
- The report names the bulk pitch per axis and where the floor bound.
- `RESOLVED.md` write-up in `src/Engine/Mom`; `CLAUDE.md` gains the new control and the refusal.

## 7. Must NOT

- **Do not auto-detect the intent from the artwork.** It is a statement about what the current does,
  which is a modelling decision — the same test `PlanarBoundaryCells`' own doc comment sets for what
  earns a user control. An auto-detector that guesses "sheet" on a wide bend would under-resolve it
  silently, which is precisely the failure the direction decline exists to prevent.
- **Do not remove or weaken the transmission-line decline.** It is still right for its own intent.
- **Do not break the tensor product**, and do not let Sheet be an excuse to.
- **Do not default it on.** Every number in `HISTORY.md` must stay reproducible.
- **Do not let Sheet skip the rim.** The temptation is real, because the rim is where the cells go and
  a patch interior is smooth. The rim is also where the radiation comes from.

## 8. Reading order

`src/Engine/Mom/PlanarMeshPitchField.cs` end to end — it is short, and its header is the design
rationale for the line intent that this brief mirrors · `src/Engine/Mom/PlanarMeshSettings.cs`
(`TransmissionLineMesh`'s doc comment, and `Resolved`) · `src/Engine/Mom/SurfaceMesher.cs`
(`Mesh`'s pitch-field block, ~line 365) · `brief-em-transmission-line-mesh.md` §2 and §M4 ·
ANT-2, which must land first or the rim will still be the mesh.
