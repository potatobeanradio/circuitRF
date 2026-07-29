# Sonnet Brief — MTaper and MKlopf, with the offset enhancement

**Design:** `docs/design/pcell-contract.md` (the contract these implement), `docs/design/microstrip-models.md`
(model specification discipline — **R1's no-transcription-from-memory rule applies here too**),
`layout-view.md` §10.4 (stackup). **Consumes L5a**, which is in progress — these are additions to the same
component library and should land after it.

Two new PCells plus one genuinely novel feature that needs designing rather than implementing.

---

## 1. MTaper — the easy one

A linearly tapered line.

| Parameters | Geometry | Pins |
|---|---|---|
| `L`, `W1`, `W2` | a trapezoid: width varies linearly from `W1` at pin 1 to `W2` at pin 2 | 2 |

**Electrical model:** a non-uniform line, handled as a **cascade of N short uniform sections**, each an
MLIN evaluated at its local width. That is the standard treatment and it reuses everything L5a builds.

### 1.1 How N is determined — and why there are two discretizations, not one

This applies to **MTaper and MKlopf alike**; both are cascades.

**R-tap-1. The electrical N satisfies two criteria; take whichever is larger.**

1. **Electrically short sections.** Each section must be short against the shortest wavelength in play:
   `section_length ≤ λ_min / 20`, where `λ_min = c / (f_max · √εeff_max)`. A fixed N that is fine at 1 GHz is
   wrong at 50 GHz, and the error appears as ripple rather than as an obvious discretization artefact.
2. **Profile resolution.** Each section's width must differ little from its neighbour's — suggest
   `ΔW ≤ 2%` of the total width range. This is the binding criterion for Klopfenstein, whose profile changes
   fastest in the middle; a taper sampled uniformly on length is under-sampled exactly where it matters.

Derive the default from the analysis sweep, expose N as an advanced override, and **report the value used**.

**R-tap-2. The artwork's tessellation is a separate number from the electrical N. Do not couple them.**
The drawn outline is a polygon, and how finely it is tessellated is a *geometric* question — a chord/sagitta
tolerance, exactly like `FlattenTolDbu` elsewhere — not a frequency question. Coupling them produces visibly
faceted artwork whenever the analysis happens to be low-frequency, and needlessly heavy geometry whenever it
is high. They are independent and both should be reported.

Origin and orientation follow the contract's R4: pin 1 at the origin, taper running along +X.

## 1A. MBend — the optimally chamfered bend

L5a already specifies MBend with a `Mitered` flag. This section supplies the electrical model that was
reported missing, and tightens the chamfer geometry.

**Source: `docs/sonnet-briefs/Lecture-3-Practical-Transmission-Lines.pdf`** (Poole & Darwazeh, 2015),
slides 24–28, equations (20)–(26). The equations are reproduced below for orientation only — **read them
from the PDF**, per `microstrip-models.md` R1. It also supplies full citations for several sources that were
previously named without them.

### 1A.1 Chamfer geometry — and a correction to this brief

Douville & James's optimum mitre, eq (24):

> **M = 100·x/d = 52 + 65·e^(−1.35·W/h) percent**, valid for **W/h ≥ 0.25** and **εr ≤ 25**

**R-bnd-1. `M` is a percentage of the corner diagonal `d`, not a length.** For a right-angle bend
`d = W·√2`, so the cut is `x = (M/100)·W·√2`. **An earlier draft of this brief wrote it as a length
`≈ W·(0.52 + 0.65·e^(−1.35·W/h))`, dropping the √2 — that is wrong by ~41%.** The coefficients were right;
the interpretation was not.

Sanity check: at `W/h = 1`, `M ≈ 69%` — the optimal chamfer removes about two-thirds of the corner, not half.

### 1A.2 Electrical model — the L–C–L two-port

**R-bnd-2. The equivalent circuit is two series inductances with a shunt capacitance between them**, and its
Z-matrix is given directly in eq (25):

> `Z = [[jωL + 1/(jωC), 1/(jωC)], [1/(jωC), jωL + 1/(jωC)]]`

That is exactly the topology `microstrip-models.md` R6 requires — take the stamp from it rather than
re-deriving.

Coefficients (Kirschning, Jansen & Koster; lecture eqs 20–23):

| Case | C | L |
|---|---|---|
| **Unmitered 90°** | `W·((10.35·εr + 2.5)·W/h + (2.6·εr + 5.64))` pF | `220·h·(1 − 1.35·e^(−0.18·(W/h)^1.39))` nH |
| **50% mitred** | `W·((3.93·εr + 0.62)·W/h + (7.6·εr + 3.80))` pF | `440·h·(1 − 1.062·e^(−0.177·(W/h)^0.947))` nH |

Validity for both: **W/h = 0.2 to 6.0, εr = 2.36 to 10.4, up to 14 GHz, precision ≈ 0.3%.** That is
narrower than the line model's range — exactly what `microstrip-models.md` R13 is about.

**R-bnd-3. Verify the units against the source before writing anything.** These expressions yield pF and nH
with `W` and `h` in particular units, and a units error here is silent, large, and produces a plausible
response. It is the single most likely way to get this wrong.

### 1A.3 The gap that must not be papered over

**R-bnd-4. The published L/C coefficients exist for 0% and 50% mitre. The *optimal* mitre is typically
60–90%.** At `W/h = 1` the optimum is ~69%, so applying the 50% coefficients to an optimally chamfered bend
is an extrapolation, not an evaluation.

Offer three modes and be honest about each:

| `Miter` | Geometry | Model |
|---|---|---|
| `None` | square corner | eq (20)–(21), exact |
| `Fifty` | 50% chamfer | eq (22)–(23), exact |
| **`Optimal`** | eq (24) — measurement-derived and sound | **no matching published closed form here** |

For `Optimal`, first check whether **Douville & James's own paper** gives the compensated bend's residual
reactance — they characterised these experimentally, so it plausibly does, and the lecture only reproduces
their geometry fit. If it does not, fall back to the 50% coefficients and **report the approximation**.
Never let `Optimal` silently borrow the 50% model without saying so.

### 1A.4 Two findings that feed §3's offset work

**Curved bends (eq 26).** When the bend radius exceeds **2·W**, the dominant parasitic is simply a change in
effective length — which corroborates R-klp-10's `3·W` threshold as appropriately conservative. And for
`3 < R/W < 7` the effective radius is **`R_eff = R_inner + 0.3·W`**.

**R-bnd-5. That means the electrical path is *shorter* than the centreline arc.** The centreline radius is
`R_inner + 0.5·W`, so `R_eff = R_centre − 0.2·W` — a systematic bias, in the opposite direction to §3.2a's
+7.4% arc-length effect and partially offsetting it. It is small for gentle bends (≈1.4% at §3.2a's tightest
point) and shrinks further above `R/W = 7`, but it is a *correction to be applied*, not noise. **Refines
R-klp-9.**

For the deeper treatment: **Weisshaar, Luo, Thorburn, Tripathi, Goldfarb, Lee & Reese**, *Modeling of radial
microstrip bends*, IEEE MTT-S 1990, pp. 1051–1054 — a reference the earlier research did not surface, and the
right one for a continuously curved line.

### 1A.5 Corrected and completed citations

The lecture's bibliography settles several entries this brief and `microstrip-models.md` had only partially:

- **Silvester & Benedek**, *Equivalent capacitances of microstrip open circuits*, IEEE MTT **20**(8),
  511–516, **August 1972** — earlier notes said 1973. Corrected.
- **Kirschning, Jansen & Koster**, *Measurement and computer-aided modeling of microstrip discontinuities by
  an improved resonator method*, IEEE MTT-S 1983, pp. 495–497 — the source of §1A.2's bend coefficients.
- **Hammerstad**, *Computer-aided design of microstrip couplers with accurate discontinuity models*,
  IEEE MTT-S 1981, pp. 54–56 — the full title for the Hammerstad discontinuity work cited for MTee.
- **T. C. Edwards**, *Foundations for Microstrip Circuit Design*, John Wiley, 1992 — the step-discontinuity
  source (§1A.6).
- **R. E. Collin**, *Foundations for Microwave Engineering*, 2nd ed., Wiley, 2005.

### 1A.6 For MStep, when it is eventually built

The lecture models the width step **as a length correction, not as an L–C network** (eqs 27–28, after
Edwards):

> `l_S/h = (Δl/h)·[1 − W1/W2]`, where `Δl/h` is the open-end length correction

That differs from the series-L-plus-shunt-C form `microstrip-models.md` §4A anticipates. Note the
discrepancy there; do not implement it now.

## 2. MKlopf — the Klopfenstein transformer

### 2.1 On the repository — the licence is not the problem, and the references are the prize

`github.com/ZiadHatab/klopf-taper` is **BSD 3-Clause**, so ingesting it would be legally fine with attribution
(retain the copyright notice and disclaimer; do not imply the author endorses circuitRF). It is *not* the GPL
situation that `microstrip-models.md` R12 warns about.

**But do not take the code, for a better reason than licensing: we need it as an independent oracle.**
`microstrip-models.md` R9 requires acceptance values that do **not** originate from the implementation being
tested. An independent NumPy implementation of exactly this taper is the ideal source of that data — and it
stops being independent the moment we copy from it.

**R-klp-1. Use the repository as a validation oracle, not as a source. Take its reference list as the
specification.** Run it, generate a table of `(Z1, Z2, Γmax, L, f)` → S-parameters, record that the values came
from it with its version/commit, and check our implementation against them. Credit it in the acknowledgments
alongside Clipper2 — it earns a mention for the oracle role even though no code is taken.

**The primary sources it cites, which are what to implement from:**

- **R. W. Klopfenstein**, *A Transmission Line Taper of Improved Design*, Proc. IRE **44**(1), 31–35, Jan 1956
  — the original.
- **D. Kajfez & J. O. Prewitt**, *Correction to "A Transmission Line Taper of Improved Design"*, IEEE Trans.
  MTT **21**(5), 364, May 1973. **Do not skip this.** Implementing from the 1956 paper alone reproduces a
  known error, and a published correction is exactly the thing a from-scratch implementation misses.
- **M. A. Grossberg**, *Extremely rapid computation of the Klopfenstein impedance taper*, Proc. IEEE **56**(9),
  1629–1630, Sept 1968 — the practical evaluation of Klopfenstein's φ function, which is otherwise an awkward
  integral.
- **B. Wadell**, *Transmission Line Design Handbook*, Artech House, 1991 — line-geometry mapping.
- **M. Steer**, *Microwave and RF Design*, Vols. 2–3, 3rd ed., NC State University, 2019 — textbook treatment.

### 2.2 Parameters and the constraint that must be validated

| Parameter | Notes |
|---|---|
| `Z1`, `Z2` **or** `W1`, `W2` | either entry route — see R-klp-3a |
| `Γmax` | maximum passband reflection |
| `L` **or** `f3dB` | the two are invertible — see R-klp-3 |
| `Offset` | default 0 — §3 |
| `SmoothSteps` | default **true** — see R-klp-4a |

**R-klp-2. `Γmax` must be strictly less than `|(Z2 − Z1)/(Z2 + Z1)|`.** Above that the design is degenerate
and the response acquires gain in |S11| — physically meaningless output from a formula that keeps evaluating.
Validate on entry and refuse with the bound named, per the project's report-don't-extrapolate rule.

**R-klp-3. Offer length and cutoff as alternative inputs.** Taper length and 3 dB cutoff are analytically
invertible for this taper, and *"what length do I need for 2 GHz?"* is the question a designer actually has.
Expose both, linked, exactly as the Scale dialog links factor and target size.

**R-klp-3a. Offer impedance or width as alternative inputs, with the last-edited pair authoritative.**
Klopfenstein is *defined* in impedance, but a layout engineer thinks in width, and both are legitimate
entry points — linked live through the same synthesis R-klp-5 needs.

The two are **not** interchangeable when the substrate changes, which is the part to get right: entering
`Z1`/`Z2` fixes the impedances and lets width follow the technology; entering `W1`/`W2` fixes the geometry
and lets impedance follow it. Retarget the technology and those give different designs — both defensible,
neither guessable.

**So the last field the user edited is the authoritative one, and it is never written back from the other.**
That is exactly the rule the Scale dialog needed after its round-trip defect (a value re-derived through a
rounded display value drifts), and the same discipline applies here for the same reason. Show which pair is
driving.

### 2.3 Two properties that are easy to destroy

**R-klp-4. The *model* keeps the end discontinuities.** The Klopfenstein taper's impedance steps by ±ρ₀ at
`x = 0` and `x = L`; those steps are part of the design, not an artefact, and a cascade that smooths them
throws away the optimality the taper exists for. The electrical model always uses the discrete profile,
regardless of §R-klp-4a.

**R-klp-4a. The *artwork* may smooth them, and does by default (`SmoothSteps`, default true).** A sharp
sub-mil width step is not something etching will reproduce anyway, and it is a stress point in the copper.
Blend the width from the connecting line's value into the taper's first station with a cubic (or similar
C1-continuous) blend.

Three things this needs pinned:

- **Blend length is physically scaled, not a fraction of `L`.** Use a small multiple of the **local width** —
  the scale over which the fields actually readjust — so the blend behaves the same on a 42 mil trace and a
  400 mil one. A fraction of `L` would make the blend grow with taper length, which is backwards.
- **The blend is consumed from inside MKlopf's own extent.** The component cannot reach into its neighbour,
  so the first and last fraction of the taper deviates from Klopfenstein by construction. Say so; do not
  quietly extend past the pins.
- **Report the blend length used.**

**R-klp-4b. Smoothed artwork and stepped model now disagree, and that must be documented rather than
discovered.** The divergence is small — the steps are ρ₀-sized — but it is real, and it has one consequence
worth stating plainly: **MoM meshes the artwork.** So once EM analysis exists, the full-wave result reflects
the *smoothed* geometry while the circuit model reflects the *stepped* one, and a user comparing the two
will see a small difference that is neither tool being wrong.

Surface it where it will be met — the component documentation and `microstrip-models.md` §8 — and note that
setting `SmoothSteps = false` makes the two agree exactly, which is the right setting when correlating
circuit and EM results.

**R-klp-5. Mapping impedance to width needs the *inverse* microstrip model.** MLIN goes `W → Z₀`; MKlopf needs
`Z₀ → W`. Either use published synthesis formulas or numerically invert the analysis model — but **use the
same model family as MLIN**, or a taper's endpoints will not match the lines it connects to. State which, and
assert round-trip consistency: `W → Z₀ → W` returns the original within tolerance.

### 2.4 An honest limitation for the user docs

**Klopfenstein's derivation assumes true TEM propagation — lossless and dispersion-free.** Microstrip is
quasi-TEM and dispersive, so the realised response deviates from the ideal, more so on lossy substrates and
at high frequency. State this in `microstrip-models.md` §8's user-facing summary. It is the kind of caveat
that reads as honesty when stated up front and as a defect when discovered later.

---

## 3. The Offset enhancement — design, not just implementation

`Offset` (default 0) is the off-axis displacement between input and output, realised by two gentle bends so
the taper starts and ends at different y positions. **This is not a published component and needs designing.**

### 3.1 The ambiguity that must be resolved first

A Klopfenstein taper is defined by its **electrical length along the line**. With an offset the centreline is
longer than the axial span, so `L` is ambiguous — and getting it wrong shifts the response silently.

**R-klp-6. `L` is the axial extent (the footprint), and the impedance profile is distributed along the
centreline's *arc length*.** The user gets the placement they expect; the taper gets the electrical design it
needs. The generator computes the centreline, measures its arc length `s_total`, and evaluates the
Klopfenstein profile against normalised arc position — **not** against `x/L`.

Report the arc length alongside the axial length, since the two now differ and the difference is what a user
would otherwise be surprised by.

### 3.2 The centreline

**R-klp-7. Use a centreline with continuous curvature (G2) and zero curvature at both ends.** Reflection from
a bend arises largely from curvature *discontinuity*; a shape that starts and ends with zero curvature joins a
straight line without one.

**The raised cosine `y = (Offset/2)(1 − cos(πx/L))` is the wrong choice, despite being the obvious one.** It
has zero *slope* at both ends but **maximum curvature** there — `y'' ∝ cos(πx/L)` peaks at `x = 0` and `x = L`
and vanishes in the middle, which is exactly backwards. Joining it to a straight line puts a curvature step at
both junctions.

**Use the quintic `y = Offset·(6t⁵ − 15t⁴ + 10t³)`, `t = x/L`** — zero slope *and* zero curvature at both ends,
so it meets the straight lines with G2 continuity. Its peak curvature sits at `t = 0.5 ± 1/(2√3)` ≈ 0.211 and
0.789.

### 3.2a Worked example — what the radius check actually permits

For the quintic, `|y''|` peaks at `5.7735·Offset/L²`, giving

> **R_min ≈ L² / (5.8 · Offset)**, times a modest `(1 + y'²)^1.5` correction (≈ 1.12 at Offset/L = 1/3).

So R-klp-10's `R_min > 3·W_local` becomes a design rule the user can apply directly:

> **L > √(17.4 · Offset · W_max)**

**A 3-inch taper with a 1-inch offset** (L = 76.2 mm, Offset = 25.4 mm) gives **R_min ≈ 44 mm**. Against a
50 Ω trace on 1.6 mm FR-4 (W ≈ 3 mm, so 3W = 9 mm) that is roughly **5× clear**. Even at the wide end of a
taper down to 25 Ω (W ≈ 10 mm, 3W = 30 mm) it still passes. The check only bites on short, sharply offset
tapers on low-impedance lines — which is the right place for it to bite.

**The same example shows why R-klp-6 is not academic.** That centreline's **arc length is about 7.4% longer
than its axial length** (≈ 81.9 mm against 76.2 mm). Distributing the impedance profile against `x/L` instead
of arc length would therefore design a taper 7% shorter than the one actually fabricated, moving its cutoff
by the same proportion — a real error, silently introduced, in exactly the geometry a user would consider
unremarkable.

**R-klp-8. Width is measured perpendicular to the local tangent, not along y.** Offsetting a straight taper's
edges vertically produces a line that is too wide wherever the centreline is sloped — an error proportional
to `1/cos θ` that peaks exactly where the bend is sharpest. Generate the outline as a proper offset curve
about the centreline.

### 3.3 The electrical model — the offset costs less than it looks

**R-klp-9. The model topology does not change.** §1's cascade of N short uniform sections already walks the
line by arc length; a curved centreline changes the *geometry* and the *arc length*, not the network. Each
section still gets its local width and local length.

What that cascade does **not** capture is curvature itself — differential phase across the width, and mode
conversion. Both scale roughly as `(W/R)²` and are negligible while the bend is genuinely gentle.

**R-klp-10. Compute the minimum radius of curvature and report when it falls below a stated multiple of the
local width** (suggest `R_min < 3·W_local`). That is the condition under which "gentle" stops being true and
the model stops being trustworthy — and the user should be told to EM-simulate rather than left with a
plausible number. Note the check uses **local** width: the same curvature is gentler for the narrow end of a
taper than for the wide end.

### 3.4 Validating something with no prior art

`Offset` has no literature to check against. Three complementary checks, none sufficient alone:

1. **The `Offset = 0` limit.** With zero offset the component must reproduce the straight Klopfenstein taper
   **exactly** — same profile, same S-parameters, matching §2's oracle data. This is the strongest available
   check and it is free.
2. **Continuity in `Offset`.** Small offsets must produce small deviations from the straight case, converging
   smoothly as `Offset → 0`. A discontinuity there means a geometry or arc-length bug.
3. **Full-wave, when L8 exists.** The real validation. Record it as the outstanding item rather than implying
   the closed-form result is verified.

**R-klp-11. Document the offset variant as an extension without published validation**, in
`microstrip-models.md` §8 and the component docs. It is a genuinely useful feature and it should be honest
about its provenance — "extends the standard taper; the bend contribution is modelled as additional arc
length and is accurate while the bends stay gentle (see R10's radius check); not validated against
literature."

---

## 4. Guardrails

- **No code from the klopf-taper repository** (R-klp-1) — references and oracle data only.
- No new PCell mechanism; both components use L5a's contract unchanged.
- No changes to MLIN/MBend/MTee/MCross.
- Do not implement a curvature-aware bend model — R-klp-10 reports instead (§3.3).
- Don't touch `src/Core`'s existing stamps beyond the cascade MTaper and MKlopf both use.

## 5. Gate

Gate command is plain `dotnet test`.

1. Builds green; `dotnet test` green; no existing test regresses.
2. **MTaper** — trapezoid geometry with correct end widths; pins per contract R3/R4; cascade converges as N
   rises, and the auto-N rule keeps sections under λ/20 at `f_max` (R-tap-1).
2a. **N selection (R-tap-1)** — both criteria are applied and the larger wins: raising `f_max` raises N; a
   Klopfenstein profile (steep in the middle) raises N through the `ΔW ≤ 2%` criterion even at low frequency.
   The value used is reported.
2b. **Two discretizations (R-tap-2)** — changing the analysis frequency changes the electrical N and leaves
   the artwork tessellation **unchanged**; changing the geometric tolerance does the reverse.
3. **MKlopf profile** — matches the oracle table from the repository within tolerance, **with provenance
   recorded** (source, commit). Kajfez & Prewitt's correction is applied — assert against a case where the
   uncorrected 1956 form differs.
4. **Endpoint steps (R-klp-4)** — the ±ρ₀ discontinuities are present in the **model** at both ends and are
   not smoothed, **regardless of `SmoothSteps`**.
4a. **Artwork smoothing (R-klp-4a)** — with `SmoothSteps = true` the generated outline has no width step at
   either pin; with it false the step is present. The blend length scales with local width, not with `L` —
   assert it is unchanged when `L` doubles. The blend stays inside the component's own extent.
4b. **Divergence is reported (R-klp-4b)** — the export/analysis report notes that smoothed artwork and
   stepped model differ, and that `SmoothSteps = false` makes them agree.
4c. **Entry route (R-klp-3a)** — entering `W1`/`W2` and entering the equivalent `Z1`/`Z2` give the same
   design on one technology; after a technology change they give **different** designs, and the last-edited
   pair is the one preserved. Neither pair is ever re-derived from the other's displayed value.
5. **Γmax guard (R-klp-2)** — a value at or above `|(Z2−Z1)/(Z2+Z1)|` is refused with the bound named.
6. **Length ↔ cutoff (R-klp-3)** — the two invert consistently in both directions.
7. **Synthesis round-trip (R-klp-5)** — `W → Z₀ → W` within tolerance, using the same model family as MLIN.
8. **Offset = 0 is exact (R-klp-11 check 1)** — byte-identical profile and S-parameters to the straight case.
9. **Offset convergence** — deviation from straight shrinks smoothly as `Offset → 0`.
10. **Arc length, not axial (R-klp-6)** — a taper with a large offset has arc length > `L`, the profile is
    distributed along arc length, and the reported response matches a straight taper *of that arc length*.
    An implementation that used `x/L` fails this.
11. **Perpendicular width (R-klp-8)** — measured width normal to the centreline is correct at the steepest
    point; an implementation offsetting in y is measurably too wide there.
12. **Curvature warning (R-klp-10)** — a small `L` with a large `Offset` triggers the report, naming the
    minimum radius and the local width. Conversely, §3.2a's worked case (L = 3", Offset = 1", 50 Ω on 1.6 mm
    FR-4) must **not** warn — a check that fires on ordinary geometry is worse than none.
12a. **Bend chamfer (R-bnd-1)** — the cut length is `(M/100)·W·√2`, not `(M/100)·W`. At `W/h = 1` the optimum
    is ~69%. Assert against a hand-computed case; a missing √2 is a 41% error that still looks like a bend.
12b. **Bend model (R-bnd-2/3/4)** — `None` and `Fifty` match eqs (20)–(23) with units verified against the
    source; the L–C–L Z-matrix matches eq (25); validity is reported at the bend's own bound (W/h 0.2–6.0,
    εr 2.36–10.4, 14 GHz), not the line model's; **`Optimal` reports that its model is approximated** unless
    a matching closed form was found.
12c. **Effective radius (R-bnd-5)** — the offset taper's electrical length uses `R_eff = R_centre − 0.2·W`
    where curvature is significant, so it is measurably *shorter* than the raw centreline arc length.
13. **Centreline shape (R-klp-7)** — curvature is zero at both endpoints. A raised-cosine implementation fails
    this, since its curvature is *maximal* there.
14. **Arc length is material (R-klp-6)** — for §3.2a's geometry, assert the computed arc length exceeds the
    axial length by roughly 7%, and that the profile is distributed against it.

## 6. On completion

Add to **`docs/design/microstrip-models.md`**: a section for the taper family carrying the reference list
from §2.1 (Klopfenstein 1956, **the Kajfez & Prewitt correction**, Grossberg, Wadell, Steer), the TEM
limitation from §2.4, and the offset extension's provenance and validity condition from R-klp-11. That
document is the durable record; this brief is not.

Record in `src/Ui/CLAUDE.md`: the BSD-3 finding and **why the repository was used as an oracle rather than a
source**; R-klp-4 (the endpoint discontinuity is the design); R-klp-6 (arc length, not axial); R-klp-8
(perpendicular width); and R-klp-10's radius check as the stated limit of the offset model.
