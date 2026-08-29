# Sonnet Brief — MN-FH: feasibility hints — the Fano ceiling and the gap rise

**Design:** `docs/design/match.md` §18.1 (Fano over a union of bands — the principle this makes
visible), §18.3 (the mirror rule, whose cost this quantifies), §18.4/§18.5 (the gap rise with order),
§18.7 (what the user sees — extended here), §4.3 (the single-band Fano optimum, which the ceiling
must never be beaten by), and `src/Core/Match/RESOLVED.md` §MN-MB2. **Prerequisite: MN-MB1 and
MN-MB2 are landed** (`EffectiveBands`, `MatchDesign.Bands/Gaps`, `MatchRemez`, the status strip's gap
lines).

**One sentence:** the Designer tells the user, *before* and *beside* any synthesis, how good a match
these terminations over these bands can possibly be, which termination and which band set the
limit, what to loosen to reach a stated target — and, for multiband, whether the chosen order excludes
the gaps at all.

**Why (owner report, 2026-08-28).** A tri-band spec — 100 Ω ‖ 0.125 pF into 1.25 Ω + 5 pF series,
bands 2.5–3 / 4.5–5 / 9–10 GHz — produced two solutions, both a flat −2.6…−3.0 dB from 2.25 to
10 GHz: a single wideband match with no trace of three bands. The synthesis was correct. Two things
were true that nothing on screen said:

1. **The network sits on termination 2's Fano wall.** For a series R+C end,
   `∫₀^∞ ln(1/|Γ|) dω/ω² ≤ πRC`; with RC = 6.25 ps the ceiling is **−3.1 dB** spent over the outer span
   2.25–10 GHz, −6.4 dB over the three effective bands with ideal gap reclaim, −10.7 dB over the bands
   as typed. The mirror widening (9–10 → 7.5–10 GHz, dragging 2.5–3 → 2.25–3) cost 4.3 dB of ceiling
   by itself. Termination 1's ceiling is −45 dB over the same span — irrelevant.
2. **At orders 1 and 2 the tri-band prototype has no gaps.** The middle band maps to
   `u ∈ [0, 0.004]`, and the Remez polynomial on `[0, 0.004] ∪ [0.337, 1]` never exceeds 1 in the gap
   at degree 1 or 2 (max |p| = 0.99, 0.97): it *is* the single-band hull Chebyshev. The gap first
   rises at order 3 (×1.16, negligible) and opens at 4/5/6 (×2.9 / ×8.8 / ×17.8) — orders tri-band
   does not offer.

Both are closed-form arithmetic on the spec. Neither needs a synthesis to run. **This brief adds no
new synthesis and no optimiser** — every number below is a formula evaluated once, and the "loosen"
hints are the same formula solved for one named variable.

**Structural facts.**
1. **The ceiling is a theorem, not a measurement**, so it is an invariant every synthesised network
   must respect: the ABCD oracle's worst in-band return loss can never be *better* than the ceiling
   over the same bands. That is the acceptance test of the formula (§5), and it is free — every
   existing golden is a fixture.
2. **Four termination kinds, two weights.** In terms of `Q = Termination.QAt(ω₀)` (already the one
   number everything downstream reads):
   - **Δω-weighted** — parallel R‖C (`∫ ln(1/|Γ|) dω ≤ π/(RC)`) and series R+L (`≤ πR/L`):
     `α_max = π ω₀ / (Q · Σ_bands (ω_hi − ω_lo))` nepers.
   - **1/ω²-weighted** — series R+C (`∫ ln(1/|Γ|) dω/ω² ≤ πRC`) and parallel R‖L (`≤ πL/R`):
     `α_max = π / (Q · ω₀ · Σ_bands (1/ω_lo − 1/ω_hi))` nepers.

   Check the identities before trusting them: parallel C has `Q = ω₀RC` so `π/(RC) = πω₀/Q`; series C
   has `Q = 1/(ω₀RC)` so `πRC = π/(ω₀Q)`; the L cases follow through `CeqAt`. `ceiling_dB =
   −20·log10(e^{−α_max}) = −8.686·α_max`. A resistive end has no ceiling. The 1/ω² class is limited by
   its LOWEST band edge and the Δω class by its widest band — which is what makes the loosen hint
   able to name a specific edge.
3. **The gap rise is `MatchRemez` evaluated in the gap**, nothing more. `MatchPrototypePolynomial.At`
   exists; `EffectiveBands.Intervals` gives the union; the gap in u is the complement inside the hull.
   For dual-band the single interval `[a², 1]` makes the exchange return the shifted Chebyshev (its
   own gate says to 1e-12), so ONE code path serves both counts.

**Sequencing.** One new Core class, one Core test class, a status-strip line, a hint block under
the solutions panel, a design-note section, the user reference. Nothing in the synthesis path
changes. Milestone 3 (offering tri-band orders 4–6) is **owner-gated** and separate.

---

## 1. What already exists

| Piece | Where |
|---|---|
| `Termination.QAt(ω₀)`, `CeqAt`, `Kind`, `Topology`, `HasReactance` | `src/Core/Match/MatchDesign.cs` 118–150 |
| `MatchDesign.Effective`, `.Bands`, `.Gaps`, `.Omega0`, `.W`, `.Order`, `.BandCount` | `MatchDesign.cs` 299–340 |
| `EffectiveBands.Intervals` (the union in u), `.Note`, `.Widened` | `src/Core/Match/MatchBands.cs` |
| `MatchRemez.MinimaxScaled(n, intervals)` → `MatchPrototypePolynomial.At(u)` | `src/Core/Match/MatchRemez.cs` 30–45, 118 |
| `MatchOrders.ValidOrders(term1, term2, form, bandCount)` — tri-band returns `[1, 2, 3]` | `src/Core/Match/MatchLadder.cs` 256–275 |
| `MatchStatus` record and its `Text` — the status strip's lines | `src/Ui/Match/MatchDesignerViewModel.Network.cs` 17–120 |
| `RefreshStatus()` — where the strip is computed from `_design` and `_rebuild` | same file, ~314–345 |
| `SolutionsRefusal` / `LandSearchComplete` — the "no solutions" sentence | `src/Ui/Match/MatchDesignerViewModel.Analysis.cs` 599–615 |
| `EffectiveBandNote` and its XAML slot on the Frequency Band card | `MatchDesignerViewModel.cs` 1269; `MatchDesignerWindow.axaml` 736 |
| `MatchResponse.WorstReturnLossDb(network, bands)` | `src/Core/Match/MatchResponse.cs` 74 |
| The ABCD oracle | `tests/Core.Tests/Match/MatchAbcdOracle.cs` |
| Multiband fixtures with known return losses | `MatchMultibandSynthesisTests`, `MatchTribandSynthesisTests`, `MatchAbcdOracle.GoldenDesign()` |

---

## 2. `MatchFanoBound` (`src/Core/Match/MatchFanoBound.cs`, new)

```csharp
/// <summary>Which of Fano's two integrals a termination is bounded by (match.md §18.10).</summary>
public enum FanoWeight { None, BandWidth /* Δω */, InverseSquare /* 1/ω² */ }

/// <summary>One termination's ceiling over one band set.</summary>
public sealed record FanoCeiling(
    int End,                       // 1 or 2
    FanoWeight Weight,
    double AlphaNepers,            // α_max; +∞ for a resistive end
    double CeilingDb,              // −8.686·α_max; −∞ for a resistive end (quoted negative like WorstReturnLossDb)
    IReadOnlyList<double> BandShare); // each band's fraction of Σ weight, in band order — sums to 1

public static class MatchFanoBound
{
    /// <summary>The weight a band contributes for this kind of termination.</summary>
    public static double BandWeight(FanoWeight w, double fLo, double fHi);

    /// <summary>The ceiling of ONE termination over the given bands, at the design's ω₀.</summary>
    public static FanoCeiling For(Termination t, int end, double omega0, IReadOnlyList<(double Lo, double Hi)> bands);

    /// <summary>Both ends over the design's EFFECTIVE bands; the binding one is the less negative CeilingDb.</summary>
    public static (FanoCeiling Term1, FanoCeiling Term2, FanoCeiling Binding) Of(MatchDesign design);

    /// <summary>The same, over the bands AS TYPED (before mirroring) — the "widening cost" is Binding(effective) − Binding(typed).</summary>
    public static (FanoCeiling Term1, FanoCeiling Term2, FanoCeiling Binding) OfTypedBands(MatchDesign design);

    /// <summary>The same, over the single outer span f_lo'..f_hi' — what a prototype that does not exclude the gaps actually spends.</summary>
    public static (FanoCeiling Term1, FanoCeiling Term2, FanoCeiling Binding) OfOuterSpan(MatchDesign design);
}
```

`For` reads `Kind`/`Topology` ONCE to pick the weight — the same licence `CeqAt` has — and uses
`QAt(omega0)` for everything else, so inductive ends are covered without a second formula. Bands
must be increasing and positive; a spec that is not yet a spec (mid-edit) returns `FanoWeight.None`
for both ends rather than throwing, exactly as `MatchBands.Symmetrise` returns its inputs.

**Typed bands for `OfTypedBands`**: `(F1,F2)`, `(F3,F4)`, `(F5,F6)` straight off the design, for the
band count. `ω₀` is the design's own in every overload — the ceiling depends on Q·ω₀ only through
`R·C` (or `L/R`), so the choice of ω₀ is inert; assert that in a test rather than assuming it.

### 2.1 The loosen hints — `MatchFanoBound.Remedies`

```csharp
/// <summary>One thing the user could change to reach a target, with the value that reaches it.</summary>
public sealed record FanoRemedy(string Kind, int? End, int? Band, double Value, string Sentence);

/// <summary>What would put the BINDING ceiling at targetDb (negative, e.g. −15). Deterministic, closed form, at most four entries.</summary>
public static IReadOnlyList<FanoRemedy> Remedies(MatchDesign design, double targetDb);
```

Each remedy solves `α_target = −targetDb/8.686` for one variable with everything else held:

1. **The reactance** (`Kind = "reactance"`): the termination value that meets the target. Δω class,
   parallel C: `C ≤ π/(R · α_t · ΣΔω)`; series L: `L ≤ πR/(α_t · ΣΔω)`. 1/ω² class, series C:
   `C ≥ α_t · Σ(1/ω_lo − 1/ω_hi)/(πR)`; parallel L: `L ≥ α_t·R·Σ(…)/π`. Sentence: *"termination 2's
   capacitance at or above 11.7 pF"*. Only offered when the direction is physical (a smaller shunt C,
   a larger series C, and so on — the formula's own inequality direction).
2. **The dominant band's inner edge** (`Kind = "edge"`): for the 1/ω² class the band with the largest
   `BandShare` is the lowest one and its `f_lo` is the lever — the `f_lo` that meets the target with
   the other bands fixed, if one exists below `f_hi`; for the Δω class the widest band's width is the
   lever, narrowed symmetrically about its own centre. Sentence: *"band 1 starting at 2.86 GHz instead
   of 2.25"*, *"band 3 narrowed to 8.7–9.3 GHz"*. Omitted when no edge inside the band reaches it.
3. **Dropping the dominant band** (`Kind = "drop"`): the ceiling the remaining bands give, *whether or
   not* it meets the target — the sentence states the number: *"without band 1 the ceiling over bands
   2 and 3 is −32.1 dB"*. Only for band counts ≥ 2.
4. **Un-widening** (`Kind = "mirror"`, multiband only, only when `Effective.Widened`): the spec that
   mirrors *without* widening, keeping the middle band (tri) or the wider band (dual), and its ceiling.
   Tri: with `f₀² = f3·f4` either `f5 = f₀²/f2, f6 = f₀²/f1` or `f1 = f₀²/f6, f2 = f₀²/f5`; offer the
   one whose ceiling is better. Sentence: *"band 1 as 2.25–2.5 GHz mirrors band 3 without widening
   (ceiling −13.8 dB)"* — on the owner's fixture that beats the other choice, 2.5–3 / 4.5–5 / 7.5–9 GHz
   at −9.6 dB, and both are worse than the typed bands' −10.7 dB only because the typed bands are not
   a spec any network can have.

`Remedies` must **never search** — each entry is one formula and one sentence; if a formula has no
solution the entry is absent. Ordered as above. The target is a parameter so the UI can ask for the
design's own goal (§3).

---

## 3. What the user sees

### 3.1 Status strip — the ceiling line

`MatchStatus` gains `CeilingDb`, `CeilingEnd` (1/2/0), `CeilingOverSpanDb` and a `CeilingText`
inserted **between `ReturnLossText` and `GapText`**:

- *"Fano ceiling 6.4 dB (termination 2, over the bands)"* — quoted positive, like the RL line.
- When the achieved worst RL is within **1.0 dB** of the ceiling: *"… — at the ceiling"*. (One dB:
  §18.4's own K-insensitivity is 0.1–1 dB, so anything closer than that is not a search shortfall.)
- When neither end is reactive, no line.
- Tooltip on the line: both ends' ceilings, the typed-band ceiling, the outer-span ceiling, and the
  widening cost when there is one: *"Widening to mirror cost 4.3 dB of ceiling."*

The line is computed in `RefreshStatus()` from `_design` alone — it does not wait for the rebuild
and it is shown on a refused design too (`Text` today drops every line but Q on a refusal; the
ceiling line survives, because a refusal is exactly when the user needs it).

### 3.2 Gap-rise note — multiband only

`MatchStatus` gains `GapRise` (one factor per gap, `max|p|` over the gap at the design's order) and
`GapOpensAtOrder` (the smallest order in 1..`MatchOrders.MaxOrder` at which every gap's rise exceeds
**2.0**, or 0 when none does). Rule for the threshold: a rise of r puts the gap's `|Γ|²` at
`(K + ε²r²)/(1 + ε²r²)`; below r ≈ 2 the gap is within a few dB of the band and the design is
spending budget there as if it were band.

Each gap line gains the factor: *"gap 3–4.5 GHz: max |S11| 0.71 (−3.0 dB) · prototype rise ×0.97"*.
When **any** gap's rise is ≤ 1 + 1e-6 at the current order the Frequency Band card's note slot (the
`EffectiveBandNote` TextBlock — add a second `TextBlock` below it with the same class, bound to
`GapRiseNote`) reads:

> *"At order 2 the tri-band prototype does not exclude the gaps — this is a single-band match over
> 2.25–10 GHz (ceiling −3.1 dB). The gaps open at order 4 (rise ×2.9)."*

or, when no offered order opens them, *"… No offered order opens them for this band geometry;
widen the middle band or move the outer bands closer."* The rise table is milliseconds
(`MatchRemez` at n ≤ 6 on a 4,000-point grid); compute it in the same place `Effective` is read
and memoise on the interval edges as the synthesis does.

### 3.3 The loosen hints — under the solutions panel

A `FeasibilityHint` string (new observable property on the ViewModel), rendered in the same slot
and class as `SolutionsRefusal`, **shown when either**:

- the search landed with no solutions and the binding ceiling is above **−10 dB**; or
- the search landed with solutions whose best worst-RL is within 1.0 dB of a ceiling above −10 dB.

Text: *"The best any lossless network can do here is −6.4 dB, set by termination 2 (1.25 Ω + 5 pF
series) over 2.25–3 / 4.5–5 / 7.5–10 GHz. To reach −15 dB: termination 2's capacitance at or above
11.7 pF; or band 1 starting at 2.86 GHz instead of 2.25; or without band 1 the ceiling is −32.1 dB; or
band 1 as 2.25–2.5 GHz mirrors band 3 without widening (ceiling −13.8 dB)."* — the remedies joined with
"; or", from `Remedies(design, −15)`. **−15 dB is a constant** (`MatchFanoBound.HintTargetDb`) with
its reason beside it (a usable match; the one number every remedy is solved for so the sentences are
comparable), not a user setting — no new UI control.

Empty otherwise. This is a hint, never a refusal: solutions that exist still list.

### 3.4 Reference

`docs/user/src/reference/match.md`: a short "Feasibility" subsection stating the ceiling line, what
the two weights mean for the user (a series C or shunt L is limited by the LOWEST band edge; a shunt
C or series L by total bandwidth), the gap-rise note, and that the hints are ceilings — reaching one
still needs an order and family that fit.

---

## 4. Design note

Add **§18.10 Feasibility hints — the Fano ceiling and the gap rise** to `docs/design/match.md`:
the two integrals in Q form (structural fact 2 verbatim), the four remedies, the 1.0 dB "at the
ceiling" slack and the ×2 rise threshold with their reasons, the owner's fixture and its table
(§"Why" above), and the theorem that the oracle can never beat the ceiling. Add **§21 Owner
decisions (rev 5)** recording that hints are closed-form and never an optimiser, that −15 dB is a
constant, and milestone 3's verdict. Extend §18.7's status-strip bullet with the ceiling line. Do
not renumber anything.

---

## 5. Tests

`tests/Core.Tests/Match/MatchFanoBoundTests.cs`:

- **Goldens from the owner's fixture** (100 Ω ‖ 0.125 pF → 1.25 Ω + 5 pF series, 2.5–3 / 4.5–5 /
  9–10 GHz), to 0.1 dB: term 2 over the outer span 2.25–10 GHz **−3.1**; over the effective bands
  **−6.4**; over the typed bands **−10.7**; band 1 alone −16.1; bands 2+3 alone −32.1; term 1 over
  the outer span −44.8. `BandShare` for term 2 over the effective bands: band 1 ≈ 0.67. Widening cost
  4.3 dB. Remedies at −15 dB: C ≥ 11.7 pF; band 1 from 2.86 GHz; the un-widened spec
  2.25–2.5 / 4.5–5 / 9–10 GHz at −13.8 dB. **Recompute these independently in the test** (write the two integrals out in the test with
  R, C and 2πf — not through `QAt`), so the class is checked against the physics rather than against
  itself.
- **The dual identity**: a series R+L with `L/R` equal to a parallel R‖C's `RC` gives the same ceiling
  over any bands; likewise parallel L against series C. And **ω₀-invariance**: `For` at any ω₀ in
  0.1×..10× gives the same ceiling to 1e-12 relative.
- **The theorem** — over every synthesised network in the existing fixtures (`GoldenDesign()`, the
  MB1 dual-band goldens, the MB2 tri-band goldens, the §16.2 lowpass fixtures), the oracle's worst
  in-band RL over the effective bands is never better than the binding ceiling by more than 0.05 dB.
  If this fails, the formula is wrong, not the fixture.
- **Remedies**: each of the four kinds on the owner's fixture; the reactance remedy re-evaluated
  through `For` lands on the target to 0.01 dB; the edge remedy likewise; absence of an entry when
  the direction is unphysical (a parallel-C end asked for a LARGER C); the mirror remedy's proposed
  spec passes `Symmetrise3` with `Widened = false`.
- **Gap rise**: on the owner's fixture, the factors 0.99 / 0.97 / 1.16 / 2.9 / 8.8 / 17.8 for orders
  1–6 to two significant figures, `GapOpensAtOrder = 4`; on §18.4's dual-band fixture (a = 0.7262)
  the rise equals `cosh(n·arccosh((1+a²)/(1−a²)))` — the closed form — to 1e-9.

`tests/Ui.Tests/Match/MatchFeasibilityHintTests.cs`: the strip's `CeilingText` on the owner's
fixture reads *"Fano ceiling 6.4 dB (termination 2, over the bands) — at the ceiling"* once the
rebuild lands; the ceiling line survives a refusal; the gap-rise note appears at order 2 and names
order 4; `FeasibilityHint` is non-empty with four remedies on the owner's fixture and empty on
`GoldenDesign()` (ceiling far below any achieved RL); switching to Single clears the gap note and
keeps the ceiling line.

**No timing tests.**

---

## 6. Milestone 3 — tri-band orders 4–6 (owner-gated, do not start unasked)

The gap-rise table says orders 4–6 are where a narrow-middle-band tri-band becomes three bands.
`ValidOrders` caps tri-band at 3 (16–24 elements above). `GvaluesAtPolynomial` is proven to degree 6
on the single-interval sweep, but no extraction has been run at degree 4–6 on a UNION. **Report the
gap-rise numbers and the element counts and stop**; if the owner says go: extend `ValidOrders` for
`bandCount ≥ 3` to `[1..6]`, run every cell of MB2's three band sets at n = 4..6 through the oracle
(worst in-band vs `(K + ε²)/(1 + ε²)` to 0.05 dB), and record any cell that fails to extract as a
refusal in RESOLVED rather than lowering the cap silently.

---

## 7. Gates

```
dotnet build
dotnet test tests/Core.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

Run each ONCE; read the TRX. Grep the diff for vendor or product names before finishing.

## 8. On completion

Findings — any fixture where the oracle beat the ceiling (there must be none; if there is one, the
formula's weight class is wrong for that termination kind), the remedy sentences as they read on the
owner's fixture, the rise factors, anything about the L cases — to **`src/Core/Match/RESOLVED.md`**
(Designer findings to `src/Ui/RESOLVED.md`). **Never to any `CLAUDE.md`.** Do not commit; the owner
commits.

## 9. Out of scope, deliberately

- Any search for "the best spec" — remedies are one-variable closed forms, not an optimiser.
- A user-editable target return loss (the −15 dB constant is the whole of it).
- Fano bounds for terminations more complex than one R and one reactance (the model has no such
  termination).
- Milestone 3 without the owner's word.
