# Sonnet Brief — MN-MB1: dual-band Match (resonated multiband, route A)

**Design:** `docs/design/match.md` **§18** (all of it, §18.6 included — it says what is *not* here)
and **§19**, plus §16.3 (the prototype family this resonates), §4.4 (the Build this feeds), §4.3 and
§4.5 (the single-match rule and the excess-element rule, both reused verbatim), §7.1–§7.3
(persistence), §13.2 (the six new test rows marked rev 3), and `src/Core/Match/RESOLVED.md` §MN-LP
(the numerical traps of the family you are reusing — read "K is not monotone in the near-end element"
and "K's floor is a RETURN-LOSS floor"). Read §18 first and in full; this brief implements it and does
not re-argue it.

**One sentence:** a `Match` can now be matched over **two bands at once** — f1–f2 and f3–f4,
geometrically mirrored about the gap centre, the gap deliberately unmatched — by taking the
lowpass-form prototype `MatchFormPrototype.GvaluesAt` already produces and feeding it to the bandpass
`MatchSynthesis.Build` that already exists, with a one-parameter search for the Fano-style optimum.

**The single most important structural fact:** **there is no new synthesis.** The dual-band ladder is
`Build(g, 2n, …)` applied to the g-vector `GvaluesAt(family, n, a, K, ε²)` returns, with
`ω₀ = 2π√(f1·f4)`, `w = (f4 − f1)/√(f1·f4)` and `a = (f3 − f2)/(f4 − f1)` (§18.2). Norton transforms,
`MatchLinkage`, `MatchSolutionSearch`, `WithEndSplits`, Flatten, the stamp and the elaborator are
**unchanged and must stay unchanged**. If you find yourself writing a dual-band special case in
`NortonTransform` or in the stamp, stop — the ladder is an ordinary alternating bandpass ladder of 2n
arms and everything downstream already handles one.

**Second structural fact:** the bands **must** satisfy `f1·f4 = f2·f3`, and the user's will not. §18.3's
rule — keep the wider band, widen the narrower one *away from the gap* — is applied in Core, once, in a
pure function, and the Designer shows the effective bands. Never design to the requested bands when
they do not mirror, and never silently.

**Third:** "Order" is **match points per band**, the element count is **4n**, and the arm count 2n is
**always even** — so both ends absorb only for a mixed termination pair or with a resistive end
(§18.2, Parity). A like pair is a refusal, not a silent single-ended match.

**Sequencing.** Self-contained; MN-LP is landed. Touches `src/Core/Match` (model, bands helper, a new
synthesis file, orders, the memo key, serialization), `src/Ui/Match` (spec key, search combinations,
spec pane, status strip, plot shading, cards/footer/summary), the component registry's echo
parameters, and the user reference. Nothing in `src/Engine`, `RfCore` or `src/Design`.

---

## 1. What already exists — read this first

| Piece | Where |
|---|---|
| **The prototype you resonate** — `GvaluesAt(shape, n, a, k, eps2)` returns `2n` elements followed by the terminating ratio (no leading 1); roots written down, LHP factor, Cauer `Extract` | `src/Core/Match/MatchFormSynthesis.cs`, `MatchFormPrototype` 47–309 |
| **`KFloor = 1e-12`** and why it is that low | same file 78; RESOLVED §MN-LP |
| **The bandpass Build** — takes `g` with `g[0] = 1`, elements `g[1..n]`, ratio `g[n+1]`, arm count `n`, orientation from the analysis end's topology, `AbsorbedEnd` marks on arms 0 and n−1, Term2-first reversal | `MatchSynthesis.Build` 607–675 (**private today — make it `internal`**) |
| **How the lowpass form prepends the 1** | `MatchFormSynthesis.Synthesize`, `double[] g5 = [1.0, .. chosen]` |
| **The single-match rule** — `g₁ = Q·w` fixed, one free parameter, best flat loss — and the refusal texts you mirror | `MatchSynthesis.SynthesizeUncached` 188–372, `FanoG` 375 |
| **The far-end reconciliation** (`QFar` from `g[n]·g[n+1]/w`, `ExcessRatioThreshold`, `NeedsExcessElement`, `WithEndSplits`) | `MatchSynthesis` 336–372, 527 |
| **`Termination.QAt(omega)`**, `HasReactance`, `AbsorbedType`, `Topology` | `src/Core/Match/MatchDesign.cs` |
| **`MatchDesign.Omega0` / `W`** — derived from F1/F2 today; become band-count-aware (§2 below) | `MatchDesign.cs` 263–267 |
| **The synthesis memo key** — every spec input must be in it or a stale result is served | `MatchSynthesis.Synthesize` 151–163, `SynthesisKey` |
| **Order parity** | `MatchOrders.ValidOrders(term1, term2, form)`, `MatchLadder.cs` 193–240 |
| **Additive-field persistence + default design** | `src/Core/Match/MatchEmbedding.cs`; round-trips in `tests/Core.Tests/Match/MatchSerializationTests.cs` |
| **The Designer's spec key and search cross-product** — `MatchSpecKey.From`, `Combinations(design)`, `AllShapes`/`FormShapes`, the streaming publish | `src/Ui/Match/MatchDesignerViewModel.Analysis.cs` 44–52, 337, 421–441 |
| **The Frequency Band & Ripple card** (`F1Entry`/`F2Entry` bindings) | `src/Ui/Views/Match/MatchDesignerWindow.axaml` 594–660 |
| **The solutions filter's Form group** | `src/Ui/Match/MatchSolutionFilterViewModel.cs` 81–93 |
| **Card heading / family name / footer** | `MatchSolutionRowViewModel.TitleText` 133, `FamilyName(shape, form)` 171; `MatchDesignerViewModel.Network.cs` ~308 |
| **Worst return loss and the point response** | `MatchResponse.WorstReturnLossDb(network, f1, f2, points)` and `MatchResponse.At` in `src/Core/Match/MatchResponse.cs` |
| **Echo parameters on the instance** (`F1, F2, Order, Response, Form, R1, R2`) | `src/Ui/Schematic/ComponentTypeRegistry.cs` 869–885 |
| **The independent ABCD oracle** — `Abcd`, `S`, `WorstS11Db`, `LadderFromG(design, g, n)`, `GoldenDesign` | `tests/Core.Tests/Match/MatchAbcdOracle.cs` |
| **The user reference** | `docs/user/src/reference/match.md` (the *forms* section at ~235 is the model for a new *bands* section) |

---

## 2. Model (`src/Core/Match/MatchDesign.cs`, new `MatchBands.cs`)

```csharp
/// <summary>How many bands the network is matched over (match.md §18). 1 is the single band of §4;
/// 2 is dual-band, f1–f2 and f3–f4 mirrored about the gap centre (§18.3). Additive: absent → 1.</summary>
public int BandCount { get; set; } = 1;

/// <summary>The second band, Hz (BandCount ≥ 2). 0 when unused.</summary>
public double F3 { get; set; }
public double F4 { get; set; }
/// <summary>Reserved for tri-band (MN-MB2). Serialized now so MB2 adds no payload change.</summary>
public double F5 { get; set; }
public double F6 { get; set; }
```

Clone them; `Version` stays 1; absent fields decode to the defaults (the round-trip tests get a
hand-written pre-rev-3 JSON, §6).

**`MatchBands`** (new static class) — the pure symmetrisation of §18.3:

```csharp
public sealed record EffectiveBands(double F1, double F2, double F3, double F4, bool Widened,
                                    int WidenedBand, string? Note);
public static EffectiveBands Symmetrise(double f1, double f2, double f3, double f4);
```

Rule, exactly: ratios `r1 = f2/f1`, `r2 = f4/f3`. If `|r1 − r2| ≤ 1e-9·max` → unchanged, `Widened =
false`, `Note = null`. If `r2 > r1` → band 1 is the narrower: `f1' = f2 / r2` (widened *downward*, away
from the gap), band 2 untouched. If `r1 > r2` → `f4' = f3 · r1` (widened *upward*). Then `f1'·f4' = f2·f3`
holds to rounding; assert it in a test. `Note` is the sentence §18.7 shows: *"Band 1 widened to
2.201–2.5 GHz to mirror band 2 about 3.588 GHz."* (format frequencies with the repo's unit formatter,
three significant figures). Validation (`0 < f1 < f2 < f3 < f4`) is a refusal in the synthesis, not an
exception here.

**`MatchDesign` derived properties become band-aware**, so that `Build` and every caller of `Omega0`/`W`
see the effective outer pair without knowing about bands:

```csharp
public EffectiveBands Effective => BandCount >= 2 ? MatchBands.Symmetrise(F1, F2, F3, F4) : single;
public double Omega0 => 2π·√(Effective.F1 · Effective.F4)      // == √(F1·F2) when BandCount == 1
public double W      => (Effective.F4 − Effective.F1) / √(Effective.F1 · Effective.F4)
public double A      => BandCount >= 2 ? (Effective.F3 − Effective.F2)/(Effective.F4 − Effective.F1) : 0
```

`Termination.QAt(Omega0)` then reads Q at the gap centre with no change (§18.9).

---

## 3. Synthesis (`src/Core/Match/MatchMultibandSynthesis.cs`, new)

### 3.1 Dispatch

In `MatchSynthesis.SynthesizeUncached`, after termination validation: `if (design.BandCount >= 2)
return MatchMultibandSynthesis.Synthesize(design);` — *before* the `Form` dispatch, and refuse
`Form != Bandpass` with `BandCount >= 2` as `ResponseInfeasible`: *"Lowpass and highpass multiband
networks are not offered (match.md §18.6); use bandpass form."* Add `BandCount, F3, F4, F5, F6` to
`SynthesisKey`.

### 3.2 Validation and orders

- `0 < F1 < F2 < F3 < F4`, else `InvalidTermination` with the four numbers.
- `MatchOrders.ValidOrders(term1, term2, form, bandCount)` (new overload; the three-argument one
  forwards with 1): for `bandCount == 2`, orders are **1, 2, 3** (4, 8, 12 elements — the same 12-element
  ceiling `MaxOrder = 6` gives single-band) when the pair is mixed or either end is resistive, and
  **empty** for a like pair with both ends reactive. `MinOrder` stays 2 for single-band; do not change
  the single-band picker.
- Empty → `InvalidOrder`: *"Both terminations are parallel; a dual-band network has an even arm count
  (match.md §18.2) and cannot absorb two ends of the same topology. Single-band bandpass form absorbs
  both; tri-band with odd counts is MN-MB2."*
- `Response` is `ChebyshevFano` or `Butterworth`; `ChebyshevTwoEnded` and `Bessel` refuse
  `ResponseInfeasible` naming §18.2. When **neither** end has a reactance, the ripple case applies
  (§3.4).

### 3.3 The search — g₁ = Q·w, one free parameter (§18.2, §18.9)

```
  ana/far, anaIsTerm1, qAnaActual, qFarActual, qAna (QAdjust honoured exactly as SynthesizeUncached)
  target = qAna · W                                     (W is the OUTER fractional bandwidth)
  family = Butterworth ? Butterworth : ChebyshevFano
  for K in a 64-point log scan over [KFloor, 0.9]:
      find ε² with g₁(K, ε²) = target:
          log grid ε² ∈ [1e-7, 1e6], 64 points; g = GvaluesAt(family, n, A, K, ε²); skip nulls;
          bracket the sign change of g[0] − target; bisect in log ε² to 1e-12 relative
      if found: worst = (K + ε²)/(1 + ε²); keep the smallest
  refine: bounded golden-section in log K around the best scan point (±1 grid step), 40 iterations
```

g₁ was monotone in ε² for fixed K in every case measured; if a K row shows two brackets, take the
one with the smaller ε² and record it in RESOLVED. No row brackets at any K → refuse `NoRealRoot`
(*"no dual-band member at order n puts g₁ = target at the analysis end"*) with the family's reachable
g₁ range in the numbers — compute it from the scan, it is what the user needs to pick another order.

The chosen `g` (2n elements + ratio) is prepended with 1 and handed to **`Build(g5, 2n, ana, far,
design, anaIsTerm1)`** — arm count `2n`, not `n`. Then the far end exactly as `SynthesizeUncached`:
`QFar` from `Build`, refuse `FarEndNotAbsorbable` below actual, `NeedsExcessElement` above
`ExcessRatioThreshold`; `RFarSynthesised`, `RequiredTransformRatio`, `Fingerprint` — all as today.
`Notes` carries the effective-band sentence when `Effective.Widened`.

### 3.4 Resistive ends

With no reactance at either end there is no g₁ to pin: `K = KFloor`, `ε² = 10^(−RL/10)/(1 − 10^(−RL/10))`
where RL is the in-band worst return loss implied by `RippleDb` (convert as `RippleG` does — the
ripple is an insertion-loss figure; `|Γ|² = 1 − 10^(−ripple/10)`), `UsedRipplePrototype = true`, and
the same note the bandpass path emits.

### 3.5 Golden values (from §18.4, computed independently of this code)

```
  f1..f4 = 2.4, 2.5, 5.15, 5.85 GHz  →  effective 2.2008547, 2.5, 5.15, 5.85 GHz (band 1 widened)
  ω₀/2π = 3.5881750 GHz   W = 1.0169920   A = 0.7261974
  Term1 = 20 Ω ‖ 2.5 pF (parallel, analysis end, Q = 1.1272584)   Term2 = 50 Ω resistive
  n = 2 (8 elements), ChebyshevFano:
      member at K = 3.654984e-5, ε² = 6.255720e-4:
      g = [1.1464128, 0.9341372, 2.4818781, 0.4175330, 2.6060058]
      shunt  L = 786.96065 pH  C = 2.5000000 pF   (AbsorbedEnd = 1)
      series L = 814.83495 pH  C = 2.4144787 pF
      shunt  L = 363.50768 pH  C = 5.4122698 pF
      series L = 364.20823 pH  C = 5.4018594 pF
      R_far = 7.674580 Ω,  required Π N² = 6.515014
      worst |S11| over both effective bands −31.793 dB;  max |S11| in 2.5–5.15 GHz = 0.4454
  n = 1 (4 elements): K = 6.039617e-4, ε² = 1.079797e-2, g = [1.1464128, 0.5512576, 1.9376596],
      R_far = 10.321731 Ω, worst −19.477 dB, gap max 0.3192
  n = 3 (12 elements): worst −41.505 dB, gap max 0.7122, R_far = 3.361512 Ω
```

The *member* values are exact at the stated (K, ε²); the *optimum* your search finds may sit at a
slightly different K — the worst return loss moves ≤ 0.1 dB per decade of K (§18.4) — so the golden
test asserts the member through `GvaluesAt` directly and asserts the search's result to 0.05 dB.

---

## 4. The Designer (`src/Ui/Match`)

### 4.1 Spec key and search (`MatchDesignerViewModel.Analysis.cs`)

`MatchSpecKey` gains `BandCount, F3, F4` (and F5, F6 — cheap, and MB2 then touches nothing here).
`Combinations(design)`: when `BandCount >= 2`, `forms = [Bandpass]` only, `shapes = [ChebyshevFano,
Butterworth]`, orders from the new `ValidOrders` overload. The per-cell worst return loss shown on a
card is the **worst over both effective bands** — add `MatchResponse.WorstReturnLossDb(network,
IReadOnlyList<(double, double)> bands, points)` and use it; the single-band overload stays.

### 4.2 The Frequency Band card (`MatchDesignerWindow.axaml`, `MatchDesignerViewModel`)

- A **Bands** selector — `Single · Dual` (Tri is MB2; do not show a disabled Tri, add it then) — as a
  segmented control in the card header row, bound to `BandCount`.
- For Dual, a second row **f3 / f4** with the same value+unit fields as f1/f2 (`F3Entry`, `F4Entry`,
  same commit-on-lost-focus and validation path). Hidden, not disabled, when Single.
- Beneath the rows, the **effective-band note** from `MatchBands` when `Widened`, otherwise collapsed.
  It is one `TextBlock` with the note text; no icon, no colour.
- The **Order** picker's element-count hint reads `(4n)` in dual-band and its choices come from the
  overload; switching Single ↔ Dual re-validates the order and, if it must change, changes it with
  the one-line note §9.2 already uses.

### 4.3 Status strip, plots (`MatchDesignerViewModel.Response.cs`, `.Network.cs`)

- Status: worst in-band RL is over both bands; add **"gap f2–f3: max |S11| 0.445 (−7.0 dB)"** after it
  when dual. The gap maximum is `max |S11|` from `MatchResponse.At` on 201 points across `(f2, f3)`.
- Plot band default becomes `Effective.F1 … Effective.F4 ± PlotBandFraction`. Shade the two in-band
  spans on the |S11| plot using whatever band-shading `PlotControl` already offers for the single band;
  if it offers none, draw nothing rather than adding a renderer feature — say so in RESOLVED.

### 4.4 Cards, footer, summary

`TitleText`: *"Chebyshev · dual-band · order 2"*. Footer and the Properties-panel summary
(`ParameterEditorViewModel.Match.cs`) show *"2.201–2.5 & 5.15–5.85 GHz"* (effective), with the
requested bands in the tooltip when they differ. The filter's Form group, while dual, shows the single
line *"Dual-band networks are bandpass only (match.md §18.6)."* in place of the three toggles.

### 4.5 Applying, undo, Flatten, probe, stamp

Nothing new: `BandCount`/F3/F4 are spec inputs edited in the pane and covered by the existing
spec-edit undo; solutions carry order/family as today. Flatten writes the ladder; the annotation's
design record gains the band line. The probe reads terminations at `Omega0`, which is now the gap
centre — correct by §18.9, and worth one sentence in the user reference.

---

## 5. Registry and reference

- `ComponentTypeRegistry` echo parameters: add `Bands` (int, default 1), `F3`, `F4` (frequency, default
  0), read-only like the others; `MatchEmbedding` writes them from the design.
- Palette search terms: add `dual-band`, `multiband`.
- `docs/user/src/reference/match.md`: a new **Dual-band** section after *forms* — what it is, the
  mirror rule and the widen-away-from-the-gap consequence with the 2.4/5 GHz example, "the gap
  mismatch is the point", 4n elements, mixed-pair parity, and that lowpass/highpass and asymmetric
  bands are not offered yet. Model it on the *forms* section's length and register.

---

## 6. Tests

`tests/Core.Tests/Match` (new `MatchMultibandSynthesisTests.cs`), every §13.2 row marked rev 3:

1. **Golden member** — `GvaluesAt(ChebyshevFano, 2, 0.7261974, 3.654984e-5, 6.255720e-4)` equals §3.5's
   g to 1e-6; `Build` (via `MatchAbcdOracle.LadderFromG` or the internal `Build`) gives the pH/pF to
   1e-6 relative; `MatchAbcdOracle.WorstS11Db` over both effective bands is −31.79 ± 0.05 and the gap
   maximum is 0.4454 ± 0.002.
2. **The search** — `MatchSynthesis.Synthesize` on §3.5's design (requested 2.4–2.5 / 5.15–5.85) yields
   worst −31.79 ± 0.05 dB, `RFarSynthesised = 7.6746 ± 1e-3`, first shunt C = 2.5 pF to 1e-12 relative
   with `AbsorbedEnd = 1`, `Notes` containing the widening sentence.
3. **Beats single-band** — the same terminations over 2.2009–5.85 GHz at order 4 (`FanoG`): the
   dual-band n = 2 worst RL over the two bands is at least 10 dB better (the scratch figure is 13 dB).
4. **Symmetrise** — the 2.4/5 GHz case widens band 1 to 2.2008547 GHz (1e-6 relative) and leaves band
   2; the mirrored input (band 2 narrower) widens f4 upward; already-mirrored input is untouched with
   `Note == null`; `f1'·f4' == f2·f3` to 1e-12 relative in all three.
5. **Parity** — two parallel reactive ends: `ValidOrders(..., Bandpass, 2)` is empty and `Synthesize`
   refuses `InvalidOrder` naming parity; mixed pair offers {1, 2, 3}; one resistive end offers {1, 2, 3}.
6. **Far end** — a far-end parallel C smaller than the synthesised end arm produces an `IsExcess`
   element after `WithEndSplits`; a larger one refuses `FarEndNotAbsorbable` with both numbers.
7. **Norton on the new ladder** — every pair `NortonTransform.Discover` finds on the 8-element basis
   applies with S11/S21 unchanged to 1e-9 (the §13.2 invariance test, on this ladder), and
   `MatchSolutionSearch` reaches `R2 = 50 Ω` (Π N² = 6.515 ± 1e-3) with at least one solution.
8. **Resistive ends** — 20 → 50 Ω real-to-real, 0.1 dB ripple, dual n = 2: extracts, `UsedRipplePrototype`,
   worst RL consistent with the ripple to 0.05 dB.
9. **Butterworth** — extracts for §3.5's problem and is worse than Chebyshev at the same n (report the
   number in RESOLVED).
10. **Old payload** — a hand-written pre-rev-3 JSON (no `BandCount`) decodes as single-band and rebuilds
    the identical ladder; a dual-band design round-trips encode → decode → identical ladder.
11. **Multiband + non-bandpass form** refuses `ResponseInfeasible` naming §18.6.
12. **Component ≡ flattened cell** and **absorbed elements are not stamped**, on a dual-band fixture
    (the existing tests, parameterised or duplicated — do not skip them because "the ladder is the
    same"; that they pass is the claim).

`tests/Ui.Tests/Match` (new `MatchMultibandDesignerTests.cs`): switching to Dual shows the f3/f4 row
and the effective-band note for the 2.4/5 GHz spec; the note is absent for a mirrored spec; the
search publishes bandpass rows only, with `Chebyshev · dual-band · order n` titles; the status strip
carries the gap line; the order picker offers {1, 2, 3} and adjusts on a parity change with the note;
one undo restores Single.

**No timing tests.** A dual-band cell is a 64 × ~20 member scan of microsecond members; if the search
measurably slows a spec edit, report the number.

## 7. Gates

```
dotnet build
dotnet test tests/Core.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

Run each ONCE and read `TestResults/last-run.trx` for any failure (repo `CLAUDE.md`, "Run the test
suite ONCE"). Before finishing, grep the diff for vendor or product names (repo `CLAUDE.md`,
*Commercial Vendor References*).

## 8. On completion

Write what was learned — anything §18 got wrong, any number that did not reproduce, a K row with two
brackets, the Butterworth figure, whether `PlotControl` could shade bands — to
**`src/Core/Match/RESOLVED.md`** and, for Designer findings, `src/Ui/RESOLVED.md`. **Never to any
`CLAUDE.md`.** Do not commit; the owner commits.

## 9. Out of scope, deliberately

- Tri-band, any Remez family, odd element counts, the like-pair parity case (MN-MB2, §18.5).
- Lowpass/highpass multiband and asymmetric bands (§18.6, route B — recorded, not designed).
- Double-match (two-ended) and Bessel in dual-band (§18.2).
- Any change to `NortonTransform`, `MatchLinkage`, `MatchSolutionSearch`, `MatchFlatten*`, the stamp,
  or the elaborator — if one seems needed, stop and report.
