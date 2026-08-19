# src/Engine — resolved briefs (detail, off the CLAUDE.md growth path)

Mirrors `src/Engine/Loadpull/RESOLVED.md`'s pattern: a completed brief's detail lands here, one `##`
section per brief, sparingly — only for findings that are still true, still surprising, and would cost
someone real time to rediscover. `CLAUDE.md` stays for durable, still-true conventions.

## The commensurability error named the source; the fix is on the ANALYSIS (2026-08-19)

**Reported as:** a frequency sweep would not run — `Commensurability check failed: source 'P1'
Freq=3E+09 Hz is not on the HB tone grid {f0=2E+09 Hz, MaxHarm=3}` at a sweep point, blocking work in
the Data Display.

**Not an engine defect.** The owner's netlist had

```
analysis HB1 type=hb Tone="2" ToneUnit=GHz ...
P1Tone:P1 ... Freq=RFfreq GHz
analysis HB1_sweep_RFfreq type=parametric_sweep Var=RFfreq Start=2 Stop=4 Step=1
```

The **source** follows the swept variable and the **analysis's own Tone is a literal**, so the HB grid
stays where it started and every point past the first is legitimately off-grid. `Tone` has always
accepted an expression (`HbEngine.Resolve` → `FreqUnit.ResolveHz`, which also gets var-unit-wins right),
so `Tone="RFfreq"` is the entire fix — verified end to end through `Cli hb` on the owner's netlist.

**What WAS wrong is the message.** It named the source, which is the half that is right, and therefore
sent the reader to the one place with nothing to fix. `HbEngine.SweptToneHint` now searches
`_netlist.ResolvedGlobals` for a variable whose current value equals the off-grid tone — accepting the
unit-less spelling too (`Freq=RFfreq GHz` applies the unit at the use SITE, so the global holds 3, not
3e9) — and appends the variable's name and the fix. Test:
`P1ToneTests.T7b_OffGridSourceFollowingASweptVariable_NamesTheVariableAndTheFix`. Fuller write-up in
`src/Ui/DataDisplay/RESOLVED.md` (it surfaced during the plot-versus work).

## A parametric sweep's unit: SCALE and MARK must come from the same place (2026-08-18)

**Reported as:** a Loadpull Pursuit with a frequency parametric sweep over `RFfreq` ran at the wrong
frequency. **Pinned by the user's own artifact rather than by reading the source** —
`circuitRF_demo/results/anotherLP.npy` carries `__Freq = 2.0`, i.e. the pursuit solved at **2 Hz** for
a design meaning 2 GHz. That number is what identified the mechanism, because only one path produces
exactly 2.0 rather than 2e9 or the loadpull engines' 1e9 fallback.

### The defect

`ParametricSweepEngine.Run`'s re-injection does **two** things per sweep point:

1. multiplies the values into base SI, and
2. attaches the scale-1 base symbol (`GHz`→`Hz`) so `Elaborator` calls `MarkGlobalHasUnit`, which
   puts the variable in `GlobalsWithExplicitUnit` → `FreqUnit.ResolveHz` fires **var-unit-wins** →
   the use site's own unit (`ToneUnit=GHz`) is *not* applied a second time.

The effective unit was `Spec.Unit`, **else the swept VAR's declared unit** — but that fallback fed
only step 2. `SweepSpec` stores raw coefficients and only `ParametricSweepAnalysis`'s spec ctor scales
them, from `Spec.Unit`. So with `Spec.Unit == ""` and `RFfreq` declared `GHz`, the values stayed
`2, 2.5, 3` while the mark asserted they were already base SI. Var-unit-wins then suppressed the GHz
that would have rescued them. The result axis was even *labelled* `"Hz"` on values of 2 — the two
halves of one decision disagreeing, in the same method, four lines apart.

### Why brief-sweep-range-units did not already cover it

That brief fixed the **UI** (`SweepAxisRowViewModel.EffectiveUnit` bakes the inherited unit into
`Spec.Unit` at build time) and concluded Part B needed **"no behavioral change"** in the engine. True
for a UI-authored spec, which arrives with `Spec.Unit` filled in and its values already scaled. False
for a `.cnl`-authored sweep with no `Unit=`, and for any spec written by an editor build predating the
brief — both still arrive with `Spec.Unit == ""`. The reported schematic was the second kind.

**Fix:** an empty `Spec.Unit` now inherits the VAR's unit for the SCALE as well as the mark, which is
the rule the editor has always applied (owner decision 3: "the unit defaults to the swept VAR's
declared unit"). Scaling the materialized points is exactly equivalent to scaling Start/Stop before
expansion, Linear and Log alike. `Values=` list sweeps are excluded: those are base-unit by definition.

### The trap worth remembering

**A test asserted the bug as intended behaviour, with a stated rationale.**
`SweptLengthUnitTests.AUnitlessSpecOverAUnitBearingGlobal_StillLandsInMetres` pinned 10 mil → **10
metres** on the reasoning *"the point of the property is that the re-attach adds nothing, not that a
unit-less spec magically acquires one."* That sentence describes the re-attach correctly and the
sweep wrongly — the re-attach does not merely "add nothing", it MARKS. The 4,000× length error and
the 10⁹ frequency error are one defect wearing two units. A test's prose is not evidence that the two
halves of a mechanism agree; only checking them against each other is.

## Multi-tone HB (3–6 tones): the FFT route was measured out, not designed out (2026-08-19)

**What shipped:** HB now takes 3 to 6 excitation tones. `T = 2` is untouched — it still runs
`MixingGrid`/`HbFft2D`/`HbNewton2D` verbatim. `T ≥ 3` runs a new path
(`MixingLattice`/`HbApft`/`HbNewtonNd`) producing an identically-shaped `DataSet`. Detail in
`docs/design/harmonic-balance.md` §6.4–§6.6; only the findings are here.

**The design doc's own plan for n-tone does not work, and the arithmetic is the reason.** §6.4 had
recorded n-tone as "a dimensionality refactor lifting the 2-tone FFT to a separable `N₁×…×N_T` real
FFT — moderate, mechanical". That grid is `nextpow2(4·order)^T` samples:

| tones | order-3 grid | one Newton iteration's arrays |
|---|---|---|
| 4 | 16⁴ = 65,536 | ~7 MB |
| 5 | 16⁵ = 1,048,576 | ~117 MB |
| 6 | 16⁶ = **16,777,216** | **~1.9 GB** |

(~14 arrays per iteration: `v`/`i`/`q` per interface node, `dg`/`dc` per node pair.) So the
"mechanical" route cannot reach the tone count that was asked for, and it is also nearly all waste:
it computes a box to retain a diamond. The APFT needs **1,512 samples** for that same 6-tone
order-3 case. Worth keeping visible because the refactor reads plausible right up until someone
writes down `16⁶`.

**The Jacobian stopped needing the `4·order` grid at all, which is the non-obvious half.** Under the
APFT the residual's nonlinear term is literally `i_nl = A·i(Γ·V)`, so its exact derivative is the
triple product `A·diag(dg)·Γ` — no difference/sum-frequency spectrum lookups. The `4·order` per-axis
rule existed *precisely* to give the two-tone convolution its `2·MaxMixOrder` reach (§5.2); with the
triple product there is nothing left for it to reach. Two substitutions, one consequence.

**"The two formulations disagree" was truncation, and the test asserts the trend instead of a
tolerance.** Running the same two-tone problem through both paths, the carriers agree to ~4e-8 but
IM3 differed by 0.46% at `MaxMixOrder=3`. Not a defect: the FFT aliases everything above the diamond
back onto it by periodic wrap while the APFT least-squares-projects it, so the product on the diamond
EDGE is the most exposed and each path discards it differently. Measured across orders:

```
IM3 (2,-1) relative disagreement:  order 3: 5.3e-3   order 4: 2.9e-5   order 5: 3.1e-7
```

`HbNewtonNdVs2DTests` therefore asserts the **convergence** (≥10× per order, ≥1000× over 3→5), which
a genuine formulation error would fail and a loose tolerance would have absorbed. Oversampling the
APFT barely moves it (5.3e-3 → 3.0e-3 → 4.5e-4 at oversample 2/4/8) — confirming diamond truncation,
not sample-set error, as the cause.

**The 600-product ceiling is measured, not guessed.** Hero-5 six-tone, single point, dense Jacobian:
order 2 (43 products) **0.4 s**; order 3 (189 products) **4.3 s**; order 4 (**645** products) was
still running at **>17 minutes** of 100%-CPU when it was killed. 645 is just over the cap, and that
gap — seconds to tens of minutes across one order step — is what the cap is placed in. The refusal is
at setup time and names the largest order that fits, because building the lattice + APFT for an
over-cap request would itself allocate hundreds of MB before failing.

**`EffectiveSettings` silently drops any `AnalysisSettings` field not listed in it** — it is a
hand-written field-by-field copy taken whenever the directive's `MaxIter` differs from the settings
default. It had been dropping `HbConsoleDiagnostics`, so `--diag` went quiet for any netlist whose
`MaxIter=` was non-default: the flag appeared to work on some netlists and not others with nothing to
indicate why. Fixed, and the missing fields (`HbSweepWarmStart` and the three new multi-tone ones)
added. Anything added to `AnalysisSettings` that HB reads must be added there too.

**Equal tone spacing puts distinct products on one frequency, and that is fine.** At
1.99/2.00/2.01 GHz — the ordinary three-carrier stimulus — `(1,-1,0)` and `(0,1,-1)` both sit at
−10 MHz. They remain independent unknowns because each tone owns its own phase axis, so the torus
basis functions stay orthogonal regardless of what the frequencies do; the solve is not singular.
`hero5_3tone.cnl` is the fixture, and `HbMultiToneRunTests` asserts both that they share a frequency
and that they are not the same unknown. The spectrum plot shows two stems at one x; they are not
summed.

**One lattice class serves every tone count because the T ≥ 3 enumeration rule REPRODUCES the locked
two-tone order.** `MixingGrid`'s "k₁ descending, then k₂ descending within the upper half-plane" is
exactly lexicographic-descending under the half-space rule ("first nonzero component positive"), so
`MixingLattice(2, O)` matches `MixingGrid(O)` element for element (pinned, orders 0–6). That is what
let the generalization avoid renumbering an index map the measurement library and every existing
two-tone cube already depend on.
