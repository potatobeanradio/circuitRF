# circuitRF — Microstrip Model Specifications

**Status:** Partially shipped, gap documented · **Date:** 2026-07-29, implemented 2026-07-28 · **Phase:** L5a

**Implementation note (L5a completion — read before trusting any rule below as "done"):**

- **§2's five-layer MLIN model is fully implemented and independently cross-checked.** Layers 1
  (Hammerstad-Jensen static Z0/eeff, `src/Core/Devices/Microstrip/HammerstadJensen.cs`) and 3
  (Kirschning-Jansen dispersion, `KirschningJansen.cs`) were verified by hand against the one
  independently-sourced worked example located during implementation (M. Steer, *Fundamentals of
  Microwave and RF Design*, open-access, Example 4.2/3.5.2: W=600µm h=635µm εr=4.1 → εeff=2.967,
  Z0≈75.3-75.4Ω) — the implementation reproduces this exactly (see
  `tests/Core.Tests/Devices/Microstrip/HammerstadJensenTests.cs`). Layer 3's exact coefficients were
  fetched verbatim from scikit-rf's own source (BSD-3-Clause) rather than a secondary summary, after
  an initial secondary-source transcription of one detail (which frequency feeds the R13 coefficient)
  was caught as wrong and corrected against the real source — recorded in
  `KirschningJansen.cs`'s own doc comment as the concrete example of why R1's "don't transcribe from
  memory" rule matters. Layers 2/4/5 (thickness correction, Wheeler+Hammerstad-Bekkadal conductor
  loss, dielectric loss) are implemented per §2's own rows, cross-checked against 2+ independent
  sources each. **R2's five decisions, stated:** Hammerstad-Jensen (not the simpler 1975 form);
  Kirschning-Jansen (not Getsinger); Wheeler + Hammerstad-Bekkadal roughness INCLUDED (not skipped);
  dielectric loss weighted by the filling factor `q`. Surface-roughness defaults to 0 (smooth) unless
  a caller supplies a nonzero RMS value — no `.ctech` field carries one yet.

- **§4's discontinuity models — the Douville-James miter fraction is confirmed; the Garg-Bahl
  bend/T/cross fitted equivalent-circuit VALUES and the Hammerstad-Bekkadal T-junction
  reference-plane formula are a genuine, documented gap.** Three dedicated research passes
  (WebSearch/WebFetch against non-GPL sources — textbook excerpts, Google Books, IEEE abstract
  pages, secondary calculator sites) confirmed the Douville-James miter fraction
  `W·(0.52+0.65·e^(−1.35·W/h))` independently (4 separate non-GPL sources reproduce it identically,
  resolving the brief's own "verify, don't trust the brief" instruction in the affirmative) — see
  `MicrostripDiscontinuities.cs`. The actual Garg & Bahl 1978 fitted capacitance/inductance formulas
  and the Hammerstad & Bekkadal T-junction reference-plane displacement formula were **not**
  obtainable from any accessible non-GPL source in three passes — both papers (and the 1996
  Gupta/Garg/Bahl/Bhartia textbook that reproduces them) are paywalled/inaccessible from this
  environment. Per R1/R-pc-15's own explicit instruction, **this is reported rather than filled
  with a fabricated or from-memory value.** `MicrostripBendModel`/`MicrostripTeeModel`/
  `MicrostripCrossModel` (`src/Core/Devices/`) each carry this exact caveat in their own doc
  comments and emit a runtime warning (once per instance) naming the gap; MBend additionally uses
  the CONFIRMED miter geometry to distinguish mitered from unmitered electrically (a principled,
  non-fabricated length correction — not the real Garg-Bahl reactance), while MTee/MCross stamp as
  genuinely ideal (lossless, reactance-free) junctions — precisely the case this document's own §4
  intro calls "earns nothing" — reported loudly, not silently. **Gates 11a/11b (discontinuity
  reactance accuracy against source-paper curves) and R6/R7 (equivalent-circuit topology with real
  values) are NOT met by this implementation** and should not be represented as passing until a
  real source for these two formula sets is found. R11 (MCross opposing-mean-approximation
  reporting) IS fully implemented and tested, independent of the missing reactance data.

- **§7's acceptance table** currently has exactly the one row above (Steer's worked example) plus
  the edge-of-range/validity-boundary tests in `HammerstadJensenTests.cs` (W/h and εr outside
  [0.01,100]/[1,128] correctly report, once per violation). A multi-row table spanning several
  (W/h, εr, f) combinations from an independent source was not assembled — a stated gap against R9's
  own "4-6 rows including edge cases" ask, not a silent shortfall.

Specifies **which published models** circuitRF's microstrip components implement, **which variant** of each,
their **validity ranges**, and their **published accuracy**. Written before the first implementation so that
"the MLIN model" names one specific thing rather than whatever was convenient.

**Reads with:** `pcell-contract.md` (the components' artwork side), `mom-engine.md` §10.9 (validation
oracles), §10.4 (the stackup that supplies substrate parameters), `linear-engine.md` (the transmission-line
stamp these models feed).

**Three consumers depend on this being precise:**

1. **The implementation** — "implement Hammerstad-Jensen" leaves four further decisions open (§2), and two
   engineers would answer them differently.
2. **The MoM validation gate** — §10.9 makes Hammerstad-Jensen the oracle for L7's ±2% check on Z₀ and εeff.
   **A tolerance against an unpinned reference is not a test**; the solver would be validated against
   whatever the closed-form implementation happened to do.
3. **User documentation** — an RF engineer's first question about any tool is *which model, over what range,
   to what accuracy*. §8 is written to be quotable into user docs directly.

---

## 1. A rule about this document

**R1. This document names models, variants, validity ranges and accuracy. It does not transcribe their
algebra, and neither should an implementer work from memory.** Equations and acceptance values come from the
cited primary sources.

This is deliberate. A closed-form expression written down from recollection looks authoritative, gets
implemented faithfully, and is then validated against a test table produced by the same recollection — so the
error is invisible and self-confirming. Hammerstad-Jensen in particular has several nested sub-expressions
that are easy to get subtly wrong and impossible to spot by inspection.

**Every acceptance value in §7 must carry its provenance** — which source, which table or figure — so a
future reader can re-derive it.

## 2. MLIN — the five layers, each a separate decision

"The microstrip line model" is five stacked choices. Naming only the first leaves four unspecified.

| Layer | Model | Notes |
|---|---|---|
| **1. Static Z₀ and εeff** | **Hammerstad & Jensen**, *Accurate Models for Microstrip Computer-Aided Design*, IEEE MTT-S 1980 | The accuracy-improved successor to Hammerstad's 1975 synthesis formulas. **Cite which**, because both circulate as "Hammerstad." |
| **2. Finite conductor thickness** | An effective-width correction `W → W_e` for non-zero `t` | The stackup supplies `t` (§10.4), so ignoring it would discard data we already have. State the correction's source. |
| **3. Dispersion — Z₀(f), εeff(f)** | **Kirschning & Jansen**, 1982 | The standard accurate choice. Getsinger's earlier model is simpler and less accurate; if it is used instead, say so and why. |
| **4. Conductor loss** | Wheeler's incremental-inductance rule, with skin effect from the stackup's σ | Surface roughness (Hammerstad-Bekkadal correction) is **optional** — decide and record, since it materially changes loss at high frequency and PCB copper is not smooth. |
| **5. Dielectric loss** | From the stackup's `tanδ`, weighted by the filling factor `q = (εeff − 1)/(εr − 1)` | Microstrip is partly air-filled, so unweighted `tanδ` overstates loss. |

**R2. Record the variant decision for every one of the five**, in the code and in §8's user-facing summary.
"We use Hammerstad-Jensen" is not a specification.

**R3. The result feeds the existing transmission-line stamp.** Once Z₀, εeff and α are computed, MLIN is the
ideal `TLIN` with those parameters (`pcell-contract.md`, and the L5a brief's R-pc-11). No second stamp.

## 3. Validity ranges, and what happens outside them

Every one of these models is published with a range over which its accuracy is claimed. Outside it the
formulas still evaluate — they simply return numbers that mean nothing.

**R4. Each model's validity range is recorded with it, and a parameter outside that range is reported, never
silently extrapolated.** A user sweeping `W` off the end of a model's range must be told, because the output
will look entirely plausible.

Report once per distinct violation rather than per frequency point, and name the parameter and the bound.

**R5. Ranges are transcribed from the sources, not estimated.** They belong in §7's table alongside the
acceptance values.

## 4. Discontinuity models — MBend, MTee, MCross

These are **built in L5a**, alongside MLIN. An earlier draft deferred them; that was wrong on two counts.
The stated reason — no oracle until full-wave — was already incorrect, since these models come with published
validation data. And the fallback reason, scope, does not survive the product question: **a placed component
with an ideal model earns nothing.** MLIN–MBend–MLIN with an ideal bend is electrically identical to one
longer MLIN, so a user would rightly ask what the component is for. The junction *is* the model.

**R6. Specify the equivalent circuit, not only the formulas.** What determines the MNA stamp is the network
topology — how many ports, which reactive elements, which reference-plane shifts — and two implementations
of "the T-junction model" with different topologies are different models. Record the topology per component.

**R7. Reference-plane shifts are part of the model and must not be silently absorbed.** Each of these models
moves its ports relative to the physical junction centre. If a shift is dropped, the error appears as a small
length discrepancy that looks like a drafting mistake rather than a modelling one, and it grows with
frequency. State where each model's reference planes sit, and make explicit how they relate to the pin
positions `pcell-contract.md` R3 defines.

| | Ports | Primary references | Notes |
|---|---|---|---|
| **MBend** | 2 | **Douville & James**, *Experimental study of symmetric microstrip bends and their compensation*, IEEE Trans. MTT **26**(3), 175–182, 1978 — the optimal-miter source, for **both** the geometry and the compensation behaviour. **Garg & Bahl 1978** also covers the right-angle bend. | Mitered and unmitered are **distinct discontinuities**; the electrical model must match the geometry actually generated. |
| **MTee** | 3 | **Hammerstad**, *Discontinuities in Microstrip* — the canonical CAD T-junction model, giving a corrected equation for the **reference-plane displacement in the stub arm** plus corrections to the other equivalent-circuit parameters. **Garg & Bahl 1978** for the closed-form curve fits. **Kirschning, Jansen & Koster** for frequency-dependent behaviour from a waveguide model. | Hammerstad's remains the model commercial CAD is measured against; later work is generally framed as improving on it. |
| **MCross** | 4 | **Garg & Bahl 1978**. Newer: *An Improved Equivalent Model for Microstrip Cross-Junction* (IEEE), a planar rectangular-patch analysis claiming to lift the geometry and frequency limits of earlier models. | **See R11 — the published models are symmetry-restricted, which constrains the component's parameters.** |

**One paper covers all four.** **Garg & Bahl**, *Microstrip discontinuities*, Int. J. Electronics **45**(1), 81–87, 1978, derives closed-form expressions by curve-fitting available data for open ends, gaps, step in width, right-angle bend, T-junction **and** cross-junction, stated accurate to within about **5%** over commonly used parameter ranges. Start there; it is the most efficient single entry point, and its 5% figure is the accuracy claim to quote in §8 unless a better model is used.

The underlying data those fits rest on: **Silvester & Benedek (1973)** for discontinuity capacitance and **Gupta & Gopinath (1977)** for discontinuity inductance. **Gupta, Garg, Bahl & Bhartia**, *Microstrip Lines and Slotlines*, 2nd ed., Artech House, 1996, collects the whole family and is the practical desk reference.

**R11. The published cross-junction models require opposite arms to have equal widths, and MCross's four
independent `W1`–`W4` do not satisfy that.** The usual workaround is to substitute the arithmetic mean of
each opposing pair, which is a first-order approximation valid only while opposing widths are similar — a
genuinely unsymmetrical cross has no well-established closed-form model.

**Decide this explicitly rather than letting it happen**, and record the choice: either constrain the
parameters (`W1 = W3`, `W2 = W4`), or accept all four and **report** when the approximation is being used and
how far the widths diverge. The second is preferable — it keeps the component usable and tells the truth —
but a silent mean substitution would be the worst of the three, since the answer stays plausible as accuracy
degrades.

**R12. Do not take equations from GPL-licensed implementations.** The most accessible written-up survey of
these models is a third-party simulator's technical documentation, and it is GPL. Reading it to identify *which papers* to
consult is fine; taking equations or code from it is not, per the root `CLAUDE.md` licensing rule. Work from
the primary sources above.

**R13. Each model carries its own validity range, and they are narrower than the line model's.** §3's
out-of-range reporting applies per model, not once for the component set. A bend model valid to `εr ≤ 20`
sitting inside a component whose line model reaches `εr ≤ 128` must report at the *bend's* bound.

**R14. Validate against the source papers' published curves first, and against full-wave second** once L8
exists. Two independent references beat one, and the papers are available now.

**R15. The mitered and unmitered cases are separately gated.** It is easy to implement one and let the other
fall through to it, and the resulting error is small, plausible and frequency-dependent — the hardest kind to
notice.

### 4.1 What is in hand

**Primary source for all three: `docs/sonnet-briefs/extract.pdf`** — Gupta, Garg & Chadha, *Computer-Aided
Design of Microwave Circuits*, Artech House, 1981, §4.5 (*Microstrip Discontinuities*), pp. 185 onward. It is
an **image-only scan with no text layer**, so it must be read visually; `pdftoppm -r 320` renders it legibly.

**All three components now have published closed forms.** The gap that blocked MTee and MCross is closed.

| | Section | Equations | Gives |
|---|---|---|---|
| **MBend** | §4.5.3 | (4.34), (4.35) | `C`, `L` |
| **MTee** | §4.5.4 | (4.36)–(4.39) | `C`, `L₁`, `L₂`, turns ratio `n²`, and a frequency term `f_p` |
| **MCross** | §4.5.5 | (4.40)–(4.42) | `C`, `L₁` (`L₂` by interchanging `W₁`/`W₂`), `L₃` |

All trace to the book's reference **[17]**, the same Garg & Bahl lineage §4 already identified — so this is
that paper's content reproduced in CAD-ready form, which is exactly what was wanted.

**R19. Read the equations from the scan; do not take them from this document.** R1 applies with extra force
here: these are dense expressions on a 1981 scan, and a transcription error would be invisible. The tables
above record *what exists and where*, deliberately not the algebra.

#### Three findings that change how these must be implemented

**R20. Validity is per *equation*, not per model — finer than R13 assumed.** The cross alone carries three
different ranges: its capacitance, its `L₁` and its `L₃` are each stated valid over different `W₁/h` and
`W₂/h` spans. So a single "is this cross in range?" check is wrong; each parameter must be checked against
its own bound and reported separately. Confirm the bend's and tee's ranges the same way while reading.

**R21. The cross imposes an ordering constraint, not just a range: `W₂ ≥ W₁` for `L₁`.** That is a different
kind of precondition from an interval and is easy to miss when only interval checks are implemented. Since
`L₂` is obtained by interchanging `W₁` and `W₂`, the ordering must be handled deliberately rather than assumed
away.

**R22. The cross model is parameterised by `W₁` and `W₂` only — confirming R11's symmetry restriction from the
source itself.** Opposite arms are equal by construction in this formulation. The owner's four independent
`W₁`–`W₄` therefore cannot be evaluated directly, and R11's decision (constrain, or accept and *report* the
mean-substitution approximation) stands, now on firm footing rather than inference.

#### A discrepancy that must be resolved before the bend is implemented

**R23. The two books disagree on the bend inductance's normalisation.** This extract gives it as **`L/h`
(nH/m)**; *Microstrip Lines and Slotlines* gives what reads as **`Lb/W` (nH/m)** for the same expression
`100(4√(W/h) − 4.21)`.

One of them is a typo, or one reading is wrong. **This must be settled against a third source before the
bend ships** — the two differ by a factor of `W/h`, which at the edges of the valid range is an
order-of-magnitude error that still produces a plausible-looking response.

This is R17's cross-check earning its keep on its first use, and it is the argument for keeping both sources
rather than picking one: a single source would have been implemented faithfully and silently wrongly.

#### Still worth obtaining

- **The correction to Silvester & Benedek [8]** (R18) — the underlying capacitance data.
- **Akello et al. 1977**, *Equivalent Circuit of the Asymmetric Cross Over Junction* — the only lead on a
  genuinely unsymmetrical cross (R22).
- **Easter 1975** — measured data, for independent validation of the acceptance table (§7).

#### Corrected: there are three Silvester/Benedek papers, and I conflated them

- **[8] Silvester & Benedek, "Microstrip Discontinuity Capacitances for Right-Angle Bends, T-Junctions and
  Crossings," IEEE Trans. MTT-21, 1973, pp. 341–346** — the junction paper, **and the book notes it carries a
  published correction.**
- **[9] Benedek & Silvester, "Equivalent Capacitance for Microstrip Gaps and Steps," MTT-20, 1972,
  pp. 729–733.**
- *"Equivalent capacitances of microstrip open circuits,"* MTT-20(8), 1972, pp. 511–516 — the one the lecture
  PDF cites.

An earlier note here "corrected" the junction paper from 1973 to 1972. **That correction was itself wrong** —
it had swapped in the gaps-and-steps paper. Three adjacent papers by the same authors in consecutive years is
an easy conflation and worth naming so it is not repeated.

**R18. Obtain the correction to [8], not just the paper.** This is the second published correction to surface
in this work — Kajfez & Prewitt's to Klopfenstein being the first — and a from-scratch implementation misses
both in exactly the same way.

#### Other sources the book identifies

- **[14] Thompson & Gopinath**, *Calculation of Microstrip Discontinuity Inductances*, MTT-23, 1975,
  pp. 648–655 — the inductance behind Figs. 3.26 and 3.29 (bend and tee).
- **[15] Gopinath et al.**, *Equivalent Circuit Parameters of Microstrip Step Change in Width and Cross
  Junctions*, MTT-24, 1976, pp. 142–144 — the cross inductances of Fig. 3.33.
- **[34] Akello et al.**, *Equivalent Circuit of the Asymmetric Cross Over Junction*, Electron. Lett. 13,
  1977, pp. 117–118 — **directly addresses the asymmetric cross**, the case R11 flagged as having no
  well-established model. Worth chasing before settling for R11's mean-substitution workaround.
- **[33] Easter**, *The Equivalent Circuit of Some Microstrip Discontinuities*, MTT-23, 1975, pp. 655–660 —
  measured data, useful as independent validation.

#### Fallback if Garg & Bahl cannot be obtained

**Digitize Figures 3.28, 3.29, 3.32 and 3.33 into interpolation tables.** Legitimate — it is what CAD tools
historically did — but laborious, error-prone, and it makes the acceptance table (§7) harder to source
independently, since the same curves would supply both implementation and test. Treat it as a last resort and
say so in the completion note if taken.

The book also offers one usable crude closed form for the cross: **`C+ ≈ 0.75·Cm`**, where `Cm` is the
capacitance of the uniform line of the larger width. It is explicitly labelled crude; use it only as a sanity
check on whatever is implemented.

**R16. That third-party documentation contains the complete tee equations — reference-plane displacements,
both transformers, the shunt susceptance, port numbering, and the Z-to-MNA derivation — and it is precisely
the source R12 forbids taking mathematics from.** It will be the first thing anyone finds, and it is
complete, well-organised and free, which is exactly why this needs saying. Use it *only* to confirm which
papers to obtain.

## 4A. The step — the one discontinuity deliberately not built

**MStep is not a component and is not modelled in L5a.** It is the one junction that carries no information
the schematic does not already have — it is fully determined by the two widths — which is why it would have
to be *synthesized from connectivity* rather than placed. That is a different question from §4's, and it was
settled separately: a per-component flag double-counts, since a junction has two sides and any tie-break
between them is arbitrary, so if it is ever built it belongs in the **elaborator**, behind a single switch on
the analysis, with junctions classified by arm count (2 = step, 3 = tee, 4 = cross).

When built, the model is a series inductance plus shunt capacitance with reference-plane offsets — **Garg &
Bahl 1978** covers the step in width alongside the other four — validated the same way as §4's.

**State the omission where a user meets it** — component documentation and the analysis report: *"Width-step
discontinuities are not modelled; use EM simulation where the transition matters."*

## 5. Relationship to the MoM validation oracle

§10.9 uses closed-form microstrip as the reference for L7's ±2% agreement on Z₀ and εeff.

**R16 (renumbered from a duplicate R7 during L5a implementation — §4's own R7, "reference-plane shifts,"
is the correct owner of that number; this rule was mis-numbered in the original draft). One
implementation serves both the MLIN component and the MoM oracle.** Two implementations that
disagree is the worst possible outcome for a validation reference — the gate would pass or fail on which one
the test happened to call.

**R17. The oracle comparison is only meaningful within §3's validity range**, and only against the variant
this document names. A ±2% claim against "Hammerstad-Jensen" with the dispersion and thickness decisions
unstated is not reproducible.

## 6. What is deliberately not modelled

State these plainly, because their absence is invisible in the output:

- **Radiation and surface-wave loss** — significant on thick or high-εr substrates at high frequency.
- **Enclosure and cover effects** — the models assume an open half-space above the line.
- **Width-step discontinuities** (§4A).
- **Coupled lines** — no MCLIN in this phase.
- **Anisotropic substrates** — εr is scalar.

## 7. Acceptance data

**R9. A table of `(W/h, εr, f)` → expected `Z₀`, `εeff` — each row carrying its source** — is the gate for
both the component and the oracle (§5). Populate it from the primary sources or from an independent tool,
and record which; do **not** populate it from the implementation being tested.

Include rows at the **edges** of each validity range, not only comfortable mid-range values. Closed-form
models are at their worst near their bounds, and a table that samples only the middle will pass an
implementation that fails exactly where users will notice.

## 8. For user documentation

This section is written to be quoted. Per component, the user should be able to read:

- **Which model**, with its citation.
- **The validity range**, in the parameters they control (`W/h`, `εr`, frequency).
- **The published accuracy** over that range.
- **What is excluded** — for L5a, junction discontinuities, radiation, coupling (§6).
- **What to use instead when accuracy matters** — EM simulation, which captures every one of the excluded
  effects natively.

That last line matters. A user who knows the closed-form model's limits and knows the tool has an EM path is
well served; one who discovers the limits from a measurement that disagrees is not.

## 10. The taper family — MTaper, MKlopf, and the Offset extension

Built per `docs/sonnet-briefs/brief-mtaper-mklopf.md`. Two new components — **MTaper** (linear taper)
and **MKlopf** (Klopfenstein taper, with an optional Offset/off-axis variant) — plus an enhancement to
**MBend**'s own electrical model (§4's table above; the L-C-L two-port replaces the earlier
length-correction stand-in).

### 10.1 MTaper — a cascade, not a new closed form

MTaper has no dedicated published model of its own: its electrical behaviour is a cascade of `N`
uniform-line sections (`MicrostripAbcd.UniformSection` chained via ABCD matrices), each section's Z₀/εeff
computed by the **same** Hammerstad-Jensen + Kirschning-Jansen stack §2 already specifies for MLIN — "one
implementation," literally, not a second line model for tapered geometry. `N` is resolved by
`MicrostripCascadeSectioning`'s dual criterion: the electrical λ/20-per-section rule at the analysis
frequency, and a geometric ΔW ≤ 2%-per-section rule, taking whichever is larger (R-tap-1). The PCell's own
artwork tessellation (R-tap-2) is a separate, purely geometric decision — a fixed vertex count for a
smooth-looking trapezoid-with-curved-taper outline — deliberately uncoupled from the electrical `N`, so a
coarse-electrical/fine-artwork or fine-electrical/coarse-artwork combination is never accidentally forced
to agree.

**Validated:** the degenerate case `W1 == W2` (a uniform line disguised as a zero-taper MTaper) is checked
against `MicrostripLineModel`'s own already-validated Y-stamp directly
(`MicrostripTaperModelTests.UniformTaper_W1EqualsW2_MatchesPlainMlinOfTheSameWidth`) — this is the taper
family's own "does the general case reduce to the known-good special case" gate, since no independent
published taper-cascade reference exists to check against directly.

### 10.2 MKlopf — the Klopfenstein taper, and the sources behind it

**Primary references** (per R1/R-pc-15 — every formula below is cross-checked against at least two of
these, not transcribed from memory):

- **R. W. Klopfenstein**, *A Transmission Line Taper of Improved Design*, Proc. IRE **44**(1), 31–35,
  1956 — the original taper: minimum length for a given passband ripple `Γmax`, derived from a
  Dolph-Chebyshev-style reflection-coefficient synthesis.
- **D. Kajfez & J. O. Prewitt**, correction to Klopfenstein's own endpoint formula — the `−1` term inside
  the `Φ(w,A)` bracket that forces the profile to hit `Z1`/`Z2` **exactly** at the taper's own ends. The
  uncorrected 1956 form overshoots the endpoints (confirmed numerically in this codebase — see
  `KlopfensteinTaperTests.ImpedanceAt_WithoutTheMinusOneEndpointTerm_OvershootsTheEndpoints`, which pins
  the exact overshoot value, 53.264 Ω instead of the intended 50 Ω, for a 50→120 Ω/30 dB-return-loss
  design). `KlopfensteinTaper.ImpedanceAt` always applies this correction; there is no uncorrected code
  path.
- **B. Grossberg**, *Extremely Rapid Computation of the Klopfenstein Impedance Taper*, Proc. IEEE **56**(9),
  1629–1630, 1968 — the rapid series for `Φ(w,A)` (`KlopfensteinTaper.Phi`) used instead of the original
  paper's own slower integral form; verified identical between M. Steer's textbook digitisation and the
  independently-authored klopf-taper oracle's own code.
- **B. C. Wadell**, *Transmission Line Design Handbook*, Artech House, 1991 — a secondary cross-check
  source for the taper's own length/bandwidth-product duality (R-klp-3), consulted alongside Steer.
- **M. Steer**, *Fundamentals of Microwave and RF Design* (open-access LibreTexts edition) — the taper's
  worked example and its own `A = cosh⁻¹(ρ₀/Γm)` formula (eq. 11 in that text), which turned out to carry
  a **dropped factor of 2** relative to both a first-principles derivation and the independent klopf-taper
  oracle's own code (`KlopfensteinTaper.cs`'s own doc comment records this as a resolved, documented
  discrepancy — not silently "fixed" — with both the corrected and the erratum values pinned as separate
  regression tests, `ComputeA_MatchesOracleValue_WithTheFactorOfTwo` /
  `ComputeA_DiffersFromTheDroppedFactorOfTwoErratum`).

**Oracle, used strictly as a validation reference, never as a code source (R-klp-1):**
[github.com/ZiadHatab/klopf-taper](https://github.com/ZiadHatab/klopf-taper) — BSD-3-Clause, commit
`4b6fa1778b0c5df07d3088650c7952aac11c8f00`, fetched and actually **run** in this environment (not merely
read) to generate a genuine numerical oracle dataset with recorded provenance. Attribution: BSD-3-Clause
requires it, given here. This exercise also surfaced a second, independent finding: **the oracle's own
`klopf()` profile function and its own `klopf_l2f`/`klopf_f2l` length↔bandwidth functions use two
*different* conventions for the parameter `A`** (the profile function uses the refined/estimate-based `A`;
the length/frequency functions use the exact-`ρ₀` convention) — an internal inconsistency in the oracle
itself, not a bug in this implementation. `MicrostripKlopfModel`/`KlopfensteinTaper` deliberately use ONE
consistent `A` throughout (the refined/estimate-based one, matching the profile function) for internal
self-consistency, accepting a small (~1.7%, bounded and tested) divergence from the oracle's own
length/f3dB duality functions specifically — recorded in `KlopfensteinTaper.cs`'s own doc comment and
pinned by `F3dbFromLength_UsesTheRefinedA_NotTheOraclesExactRho0Convention`.

**TEM limitation, stated per this document's own §6 convention:** the Klopfenstein synthesis (like
Grossberg's rapid series and every one of the sources above) is derived for an **ideal TEM line** — a
lossless, non-dispersive `Z0(z)` profile with no frequency dependence of its own. MKlopf's own
implementation feeds this ideal-TEM profile the **dispersive, lossy** per-section Z₀(f)/εeff(f) from the
SAME Hammerstad-Jensen + Kirschning-Jansen + loss stack §2 already specifies (via
`HammerstadJensen.SynthesizeWidth`'s per-section width synthesis, then a forward `Compute`+
`KirschningJansen.Compute` pass) — the taper's *shape* (how Z varies along its length) is the ideal-TEM
Klopfenstein profile; the *microstrip physics at each point along that shape* is the real, dispersive
model. This is the same approximation every closed-form microstrip taper design makes in practice (a true
non-TEM taper synthesis does not exist in closed form) and is stated here explicitly per this document's
own "state what is deliberately not modelled" convention (§6) rather than left implicit.

**Γmax validation (R-klp-2):** `KlopfensteinTaper.ValidateGammaMax` throws (constructor-time, not a
silent clamp) when the requested `GammaMax` is at or above the taper's own theoretical bound
`|(Z2−Z1)/(Z2+Z1)|` — a taper cannot be *more* reflective at its own passband edge than a bare, untapered
step discontinuity between the same two impedances would be; requesting that is a modelling error, named
directly rather than producing a nonsensical (or infinitely long) taper.

### 10.3 The Offset extension — a novel, unpublished geometry, stated as such (R-klp-11)

**No source is cited for the Offset (off-axis) taper variant because none exists** — this is a genuine,
brief-specified extension built for this codebase, not a transcription of any published work, and R1's
"verify against a source" rule does not apply to it the way it applies to §10.2's core Klopfenstein
physics. What IS specified, in `MicrostripOffsetCenterline.cs`'s own doc comment (the class's formulas ARE
the specification for this feature):

- **A quintic, G2-continuous centerline** `y(x) = Offset·(6t⁵−15t⁴+10t³)`, `t = x/L` — zero slope AND zero
  curvature at both ends, so it joins straight input/output lines without even a slope discontinuity (a
  raised-cosine alternative was considered and rejected: it has zero slope but *maximum* curvature at the
  ends, exactly backwards from what a clean mechanical/electrical transition wants).
- **The Klopfenstein impedance profile is distributed along ARC LENGTH, not axial position**
  (`AxialPositionAtArcFraction`) — the taper's electrical length is genuinely longer than its axial span
  once `Offset ≠ 0` (a worked example in the class's own tests: `L=76.2mm, Offset=25.4mm` → arc length
  ~7.4% longer than the axial length).
- **Width is measured perpendicular to the local tangent** (R-klp-8), not vertically in Y — a taper drawn
  with vertical width measurement would overshoot wherever the centerline has non-zero slope.
- **A minimum-radius-of-curvature warning** (R-klp-10) fires when the sharpest point along the offset
  centerline curves more tightly than 3× the local trace width there — the same "3× trace width" rule of
  thumb used elsewhere in microstrip layout for bend/curve minimum radii, applied here as a geometric
  sanity check rather than an electrical one (a too-tight offset curve is a manufacturing/mechanical
  concern, not one this taper's electrical cascade model itself would show any sign of).

**Honesty note, per R-klp-11's own explicit instruction:** this extension has no published validation
data of any kind. Its own worked-example checks (arc length, minimum radius) in
`MicrostripOffsetCenterlineTests.cs` verify the geometry is *internally consistent* with the brief's own
stated formulas — they are not, and cannot be, evidence that the Offset taper's real-world electrical
behaviour has been independently confirmed against a measurement or a second implementation. Treat it as
an unpublished, self-consistent geometric construction layered on top of the well-sourced Klopfenstein
core (§10.2), not as a peer-reviewed model in its own right.

### 10.4 MBend's own enhancement — the L-C-L two-port

`MBend`'s original stand-in (a geometry-only length correction) is replaced by a real published
electrical model: **Kirschning, Jansen & Koster**'s bend equivalent circuit (an inductor-shunt-capacitor-
inductor two-port, eqs. 20–25 of the same lecture-note source this brief drew from) — see §4's own table
above (the MBend row is unchanged in citation; this section records that its *implementation* moved from
a length-correction stand-in to this real topology). Three miter modes exist: `None` (no chamfer, uses
the unmitered L-C-L coefficients), `Fifty` (a fixed 50%-chamfer per-edge leg, using the mitered
coefficients), and `Optimal` (Douville-James's own W/h-dependent optimum cut length — §4's confirmed
Douville-James geometry — but **electrically borrows the `Fifty` coefficients**, since no published
L-C-L data exists for the true Douville-James optimal percentage specifically; this borrowing is reported
via a runtime warning naming it an approximation, per this document's own §4/R15 "each case gated
separately, nothing silently falls through" convention, not left implicit).

## 9. Open / deferred

- **Surface-roughness correction** (§2 layer 4) — include or not; decide during implementation and record.
- **Stripline**, and the warning that fires when the selected layers form one (`pcell-contract.md` R12).
- **Coupled lines** (MCLIN) and the even/odd-mode models they need.
- **Discontinuity models** (§4).
- **Whether dispersion is applied to the loss terms** as well as to Z₀ and εeff.
- **MKlopf's Z1/Z2↔W1/W2 and L↔F3db entry routes now have a real UI switch** (2026-07-29 follow-up,
  `src/Ui/CLAUDE.md`'s own entry) — a Parameter Editor toggle converts the current design to the other
  route's equivalent values (via the new shared `MicrostripKlopfEntryConversion`, `src/Core/Devices/
  Microstrip/`) rather than resetting it, and undoes cleanly. **Still not built**: the fully interactive
  "last-edited-field-wins" live-linking behavior a `ScaleFieldLinker`-style two-way-bound pair of fields
  would give (today's switch is an explicit, discrete toggle action, not continuous field-to-field
  syncing while typing) — a smaller, genuinely optional polish item, not a blocking gap.
