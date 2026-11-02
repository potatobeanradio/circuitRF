# Brief MIM-9 — say what the run actually did: the three notes that sent a user the wrong way

**FIRST of the second round, and deliberately so: it is the only one that costs days rather than
weeks, and every brief after it is easier to trust once it lands.** The defect MIM-12 will fix reached
a user as days of work on the wrong thing — not because the engine was silent (it prints more than
thirty notes — 32 on the small fixture below)
but because the sentences that fired **named the wrong cause**, and the one sentence that could have
helped **said the run was fine**.

**Every item below is a reporting change on top of an answer that does not move**, so none of it
depends on MIM-12 landing, and none of it has to be revisited if MIM-12 is deferred. The measured
evidence it all rests on is already taken and is in MIM-12's tables; nothing here needs a new
measurement.

## 1. The NOT PASSIVE sentence blames the de-embedding, and the de-embedding is innocent

What ships today, on every affected run:

> NOT PASSIVE: … **The usual cause is the de-embedding rather than the fill**: D6's peel divides by
> a₂₁² (~1e4 at 1 GHz), so a small error in the error box becomes a large one in the answer. Check the
> port notes above for a feed the calibration could not be measured on, narrow the sweep to where the
> two-line calibration is well conditioned, **or raise Cells per wavelength**.

Three of those four clauses are wrong on this class of run, and MIM-12's controls measure each one:

- **"The usual cause is the de-embedding."** The identical structure with the second level removed —
  same Metal1 artwork, same feeds, same ports, same technology — is passive at σ_max = 0.9986 and
  reads a fringing capacitance flat to 0.4 % over 3:1 in frequency.
- **"Narrow the sweep to where the calibration is well conditioned."** There is no such region. The
  run is non-passive at 1, 2 and 3 GHz and incoherent below 1 GHz. Meanwhile the de-embed's own
  counters say it worked: `DeembedResidual` **1.3e-7**, `DeembedErrorFloor` **1.6e-3**,
  `CalibrationUsable` = 1, `DeembedRejected` 5e-4 (on the owner's own spiral, residual 7.7e-9 to
  1.4e-7 with rejection flat at 0). **The peel's own error estimate is 1.6e-3 against a
  non-passivity excess of 0.73.** It cannot be the cause and the engine already computes the number
  that says so.
- **"Raise Cells per wavelength."** Inert, measured directly: **20 → 400** on the isolated capacitor
  leaves σ_max at **1.0443**, unchanged to four decimals, and leaves the straddling cell over the
  plates at **40 µm** — because that knob sets the pitch ALONG the current and the plate's own width
  sets it across. The run's own mesh note says as much on the same screen. Refining the OTHER knob is
  inert too: 2 → 4 → 8 cells across moves σ_max from 1.7322 to 1.7491, i.e. slightly the wrong way.

**The change:** when the run carries a conductor pair below the full-wave floor, the non-passivity
sentence must say so, name the pair and the separation, and **not** offer the three remedies that do
not act. The de-embed's own residual and rejection counters are already computed per point — a
sentence that blames the peel while the peel's own error floor is three decades below the excess
should not be reachable. Make the attribution follow the counters rather than a fixed guess.

## 2. The level-separation note actively reassures, on a quantity that was never in question

What ships today, verbatim, on the isolated-capacitor fixture of MIM-12 (the owner's own file reads
170 and 33.992 µm and is the same sentence):

> The closest conductor levels **are resolved by the mesh**: cell/separation = 200 at levels 0 and 1
> (0.2 µm apart, largest straddling cell 40 µm), **inside the 200 MIM-8 measured the cross-level fill
> over** (≤ 1.7e-4 against forced-high quadrature; **the extracted plate capacitance within 1 % of
> ε₀εᵣA/d**).

Every clause is true and the note is still the most misleading sentence in the run. **MIM-8's
validation is electrostatic** — `PlanarFill.ScalarPotentialMatrix` with a 1 V / 0 V instrument and no
port in it — and MIM-8's own finding 5 says so plainly: *"a capacitance is now readable without a
port, and that is what made the ladder cheap."* The quantity it certifies is not the quantity the
user is reading. A run whose de-embedded capacitor has the **wrong sign** is being told its
capacitance is within 1 %.

**The change:** the note states what was validated and what was not. Something with the shape of:

> …cell/separation = 200. The cross-level FILL is measured over this range (≤ 1.7e-4 against
> forced-high quadrature) and so is the electrostatic plate capacitance (within 1 % of ε₀εᵣA/d).
> **Neither of those is a de-embedded s-parameter.** The full-wave two-port for a pair this close is
> measured accurate only to cell/separation ≈ 40; past that the published capacitance loses its
> magnitude and then its sign. See §MIM-12.

And it must stop being a `note`. On a run past the full-wave floor it is the most important line on
the screen.

## 3. The floor should be a REFUSAL, and R-emsev-4 already named it

MIM-8's *Not done, on purpose* records: *"`R-emsev-4`'s refusal was not built, per
`brief-em-run-severity-and-check.md`'s own instruction: it is conditional on MIM-8 being declined.
The note past the bound now says **unmeasured** rather than **wrong**, which is what the measurement
supports."*

**That condition has now been met from the other side.** The measurement exists, it is in MIM-12, and
it does not say *unmeasured* — it says the answer inverts the sign of the element between
cell/separation 40 and 80, and is noise at 200. A structure past the floor should be refused with
the number in the sentence, exactly as `PlanarKernel` (the `EmSuitability.No` at the foot of its via
span check) refuses a via crossing a dielectric interface. That refusal's own words are the model to
follow:

> Approximating it would give a plausible wrong inductance rather than an obvious failure, which is
> why it is refused.

**Build R-emsev-4 now, at TODAY's floor.** MIM-12's ladder already measures it: correct to
cell/separation 40, sign-inverted by 80, noise at 200. Set the refusal at 40 and leave the constant
named and in one place, because **MIM-12 will move it and that is the expected outcome, not a
rework**. Refusing at a floor that later rises is the right way round; publishing a sign-inverted
capacitor while waiting is not.

The refusal names the remedies that act: thicken the film in the technology, remove the plate level
from the run and model the capacitor as a circuit element (MIM-10), or wait for MIM-12. It must not
name the three that do not — see item 1.

## 4. One more, found on the way and smaller

**The guess is written twice, in two files, independently.** `PlanarSolve.cs:4359` builds the panel
sentence; `EmSnpProvenance.cs` builds a second one for the `.s2p` header —

> …the excess is this analysis rather than the design: **the de-embedding's own peel divides by a21
> squared**, which vanishes with frequency, so the bottom of a band is where it shows. Raise the
> sweep's lower edge, or read those rows as unanswered.

— which outlives the session and is the first thing anyone reads six months later. Both must take the
attribution from one place, or item 1 gets fixed in the panel and stays wrong in every file already
written.

## Gates

- A run past the floor **refuses**, names the level pair, the separation, the straddling cell and the
  measured floor, and offers only remedies that act.
- A run whose de-embed residual is ≥ the error floor still gets the de-embed sentence — the existing
  text is right for the case it was written for and must not be lost.
- The reassuring clause cannot be produced for a run whose full-wave path is past the floor. A test
  that asserts the two sentences cannot both fire.
- The `.s2p` header caveat and the panel text come from ONE function. A source scan for a second copy,
  on `Authoring.cs`' terms.
