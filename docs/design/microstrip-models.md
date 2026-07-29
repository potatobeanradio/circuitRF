# circuitRF — Microstrip Model Specifications

**Status:** Draft (rev 1) for review · **Date:** 2026-07-29 · **Phase:** L5a

Specifies **which published models** circuitRF's microstrip components implement, **which variant** of each,
their **validity ranges**, and their **published accuracy**. Written before the first implementation so that
"the MLIN model" names one specific thing rather than whatever was convenient.

**Reads with:** `pcell-contract.md` (the components' artwork side), `layout-view.md` §10.9 (validation
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

**R7. One implementation serves both the MLIN component and the MoM oracle.** Two implementations that
disagree is the worst possible outcome for a validation reference — the gate would pass or fail on which one
the test happened to call.

**R8. The oracle comparison is only meaningful within §3's validity range**, and only against the variant
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

## 9. Open / deferred

- **Surface-roughness correction** (§2 layer 4) — include or not; decide during implementation and record.
- **Stripline**, and the warning that fires when the selected layers form one (`pcell-contract.md` R12).
- **Coupled lines** (MCLIN) and the even/odd-mode models they need.
- **Discontinuity models** (§4).
- **Whether dispersion is applied to the loss terms** as well as to Z₀ and εeff.
