# Sonnet Brief — MN-LP: lowpass and highpass forms for Match, and the family rename

**Design:** `docs/design/match.md` **§16** (all of it) and **§6.9**, plus §4.4 (the bandpass Build this
mirrors), §4.5 (the excess-element rule §16.4 reuses), §6.2 (the numerical route §16.3 runs on), §6.7
and §16.9 (numerical traps), §7.1–§7.3 (persistence), §13.2 (the seven new test rows). Read §16 first
and in full — this brief implements it and does not re-argue it. §6.8 says why elliptic and inverse
Chebyshev are **not** part of this; do not add them.

**One sentence:** a `Match` can now be a lowpass-form or highpass-form ladder of single elements,
matched between F1 and F2 with the impedance ratio pinned by transparency at DC (or ∞), synthesised
through the existing spectral-factor + Cauer route with a one-dimensional search, absorbing the
termination kinds each form can hold — and the Solutions panel lists all three forms side by side.

**The single most important structural fact:** in lowpass and highpass form **there are no Norton
transforms**. Not "few", none — a Norton pair needs like-kind elements of opposite orientation and a
single-element ladder has no such pair (§16.1). The far-end resistance therefore comes out *equal to
the target* from the prototype itself (the DC pin, §16.3), `RequiredTransformRatio` is 1, and each
(form, order, family) cell yields exactly one solution with zero transforms. If you find yourself
writing a Norton special case for these forms, stop: `NortonTransform.Discover` must return empty on
its own, and a test asserts that it does.

**Second structural fact:** "Order" keeps its meaning — *n in-band match points* — and the element
count is **2n in every form** (§16.2). A lowpass-form order-4 network has 8 elements, exactly like a
bandpass order-4 network. Do not reinterpret Order as an element count.

**Sequencing.** Self-contained. Touches `src/Core/Match` (model, synthesis, orders, serialization),
`src/Ui/Match` (search cross-product, filter, cards, rack empty state, labels), the component
registry's parameter defaults and search terms, and the user reference. No dependency on unlanded work.
No change to `NortonTransform`, `MatchLinkage`, `MatchFlatten*`, the stamp, or the elaborator.

---

## 1. What already exists — read this first

| Piece | Where |
|---|---|
| **The numerical prototype route** — two-parameter `|Γ|²` family → Hurwitz factor → Cauer extraction; `Family`, `SubstituteOmega`, `Hurwitz`, `Extract`, `DenominatorMemo` | `src/Core/Match/MatchPrototypes.cs` (`Gvalues` 216, `Extract` 296, `Search` 327, `Family` 481) |
| **Polynomial toolkit** (trim-by-tolerance, roots with polish, `FromRoots`) | `MatchPoly`, same file, 9–200 |
| **The bandpass Build** — arms from g's, orientation vector `s[]`, arm reversal for Term2-first, `AbsorbedEnd` marking | `MatchSynthesis.Build` 580 |
| **The end-split rule** (synthesised ≥ actual → added element; < actual → refuse), stated in Q and C_eq | `MatchSynthesis.Synthesize` 268–370, `MatchSynthesis.WithEndSplits` 505, `MatchQ.SplitExcess` in `MatchLadder.cs` |
| **The two refusals with numbers** you will mirror (near end not absorbable, far end not absorbable) | `MatchSynthesis.cs` 253–265 and 349–360 |
| **Order parity** | `MatchOrders.ValidOrders`, `MatchLadder.cs` |
| **`MatchDesign`** and its clone, the enum comments, `Version = 1` | `src/Core/Match/MatchDesign.cs` |
| **Additive-field persistence + default design** | `src/Core/Match/MatchEmbedding.cs`; the round-trip tests in `tests/Core.Tests/Match/MatchSerializationTests.cs` |
| **The search cross-product that STREAMS** — `AllShapes`, per-cell publish, insert-by-sort-key, `MatchSpecKey` | `src/Ui/Match/MatchDesignerViewModel.Analysis.cs` 43–52, 336–340, and the file's own header remarks |
| **The solutions filter** — order lines rebuilt by `SetOrders` preserving state, family lines from the cards' `FamilyName`, `Accepts` | `src/Ui/Match/MatchSolutionFilterViewModel.cs`; its flyout at `src/Ui/Views/Match/MatchDesignerWindow.axaml` 708–745 |
| **The single place a family is spelled** | `MatchSolutionRowViewModel.FamilyName` 130; the long names in `MatchDesignerViewModel.cs` 90–101 and `ParameterEditorViewModel.Match.cs` 151–157 |
| **Card heading and footer line** | `MatchSolutionRowViewModel.TitleText` 122; `MatchDesignerViewModel.Network.cs` 308 |
| **Echo parameters on the instance** (`F1, F2, Order, Response, R1, R2`) and the registry's default `Design` | `src/Ui/Schematic/ComponentTypeRegistry.cs` 869–885 |
| **Palette search terms** | `ComponentTypeRegistry.cs` 292–298 |
| **The independent ABCD cascade** the golden tests are checked against | `tests/Core.Tests/Match/MatchAbcdOracle.cs` |
| **The user reference** | `docs/user/src/reference/match.md` (table at ~134, the *Response* row) |

---

## 2. Model (`src/Core/Match/MatchDesign.cs`)

```csharp
/// <summary>The network form (match.md §16). Bandpass is §4's synthesis; the other two are ladders of
/// single elements matched between F1 and F2 with the ratio pinned by transparency at DC / infinity.</summary>
public enum NetworkForm { Bandpass, Lowpass, Highpass }
```

- `MatchDesign.Form { get; set; } = NetworkForm.Bandpass`. **Additive**: a payload without it decodes as
  `Bandpass`; `Version` stays 1. Include it in `Clone()` and in the design's fingerprint inputs wherever
  `Order`/`Response` already are (§7.3 — a stored transform list is name-keyed against the basis, and a
  basis in a different form is a different basis).
- `MatchOrders.ValidOrders(term1, term2, form)`: for `Bandpass` unchanged; for `Lowpass`/`Highpass`
  return `[2,3,4,5,6]` unless **both** ends carry a reactance **and** share a topology, in which case
  return `[]` (§16.4 item 2). Keep the two-argument overload delegating with `Bandpass` so nothing
  else moves.
- `Termination` gains nothing. The form-vs-kind gate (§16.4 item 1) is a synthesis refusal, not a
  model constraint — the same termination is valid in another form.

## 3. Synthesis (`src/Core/Match`)

Add `MatchFormSynthesis` (new file) and dispatch from `MatchSynthesis.Synthesize` on `design.Form`
**before** any of the bandpass Q/decrement work — the lowpass form has no `w`, no `ω₀` and no Fano
root, and none of §4.1–§4.3's quantities apply. Keep the result type: it returns a
`MatchSynthesisResult` with `Network`, `G`, `RFarSynthesised == RFarTarget`, `QFar*` fields set from the
end elements so the status strip's existing bindings read sensibly, and `NeedsExcessElement` when an
end was split.

### 3.1 The family (§16.3)

Normalise to `ω_c = 2π·F2` and `R_ana`; `a = F1/F2` (F1 = 0 allowed). In `u = ω²`:

```
x(u) = (2u − 1 − a²)/(1 − a²)           x₀ = x(0) = −(1 + a²)/(1 − a²)
Φ(u) = T_n(x(u))²   (ChebyshevFano)  |  x(u)^(2n)   (Butterworth)
|Γ|² = (K + ε²Φ)/(1 + ε²Φ)
```

Build `Φ` as a polynomial in `u` by composing `T_n` with the linear map (a Horner loop over
`MatchPoly.Mul`/`Add`), then substitute `u → −s²` with `SubstituteOmega`'s convention (every power is
even, so it is real). `Φ` is degree n in u ⇒ the Hurwitz factors are degree 2n in s ⇒ `Extract(…, 2n)`
returns 2n elements plus the terminating ratio. **The polynomials are degree 4n in s; §6.7's
tolerance-relative trimming is not optional here.**

Families offered in these forms: **`ChebyshevFano` (labelled just "Chebyshev") and `Butterworth`**.
`ChebyshevTwoEnded` and `Bessel` are refused with a one-line reason (§16.2): the lowpass form has one
free parameter, so neither a second prescribed Q nor a Bessel delay target exists. `RippleDb` and
`QAdjust` are ignored (§16.4 item 3) — do not refuse on them, just do not read them.

### 3.2 The DC pin and the one free parameter

Given `r = R_far_target / R_ana` (the target, not a prototype output) and `Γ₀ = (r − 1)/(r + 1)`:

```
ε² = (Γ₀² − K) / ( Φ(0) · (1 − Γ₀²) )          Φ(0) = T_n(x₀)²  or  x₀^(2n)
```

K is the single free parameter, `0 < K < Γ₀²`. **Floor K at 1e-6** (§16.9 — exact zero puts double
roots on the jω axis and the Hurwitz tie-break degenerates; verified). As K → Γ₀², ε → 0 and the
family degenerates (extraction fails) — treat "does not extract" as infeasible and let the bracket
stop there. `r = 1` exactly (Γ₀ = 0) has no pin: use K = 1e-6 and ε chosen so the worst in-band
`|Γ|² = (K + ε²)/(1 + ε²)` equals a nominal −40 dB — record that choice in a comment; it is the
degenerate "wire with a ripple" case and only matters for a purely resistive equal pair.

### 3.3 Orientation and extraction

`Gvalues` today tries both signs of Γ and returns whichever extracts. Here the orientation is
**required**, not discovered: shunt-first when the analysis end is `Parallel`, series-first when
`Series`, shunt-first for a resistive analysis end. Add an orientation parameter to a new overload of
the private extractor (leave the existing public `Gvalues` behaviour untouched for bandpass) and
refuse if the required orientation does not extract. The convention for `g_{2n+1}` is `Build`'s own:
a resistance ratio when the last element is series, its inverse when shunt — and by the pin it equals
`r` (or `1/r`) to ~1e-9. **Assert that in a test; do not "correct" it in code.**

### 3.4 Choosing K (§16.4 item 3)

Both end elements are functions of K; `g₁` rises with K and the far-end element falls (verified,
a = 0.5, n = 2, r = 10: K 1e-6 → 0.6 takes g₁ 2.485 → 10.56 and g_far 0.248 → 0.069). Return loss
worsens with K. So:

1. Convert each reactive termination to its normalised prototype value at the end it sits on:
   shunt C → `ω_c·R·C`; series L → `ω_c·L/R` (lowpass); highpass via §3.6's mirror.
2. If a termination's *kind* is not absorbable in this form (lowpass: only `Parallel+C`, `Series+L`;
   highpass: only `Parallel+L`, `Series+C`), refuse — **`MatchRefusalKind.FormCannotAbsorb`** (new),
   message per §16.4 item 1, naming the end, the kind and the two forms that can.
3. Bracket K in `[1e-6, K_hi]` where `K_hi` is the largest K that still extracts (walk down from
   `Γ₀² − 1e-6` by halving the gap; a dozen probes). `K_min` = smallest K with
   `g_near(K) ≥ g_near_actual`; `K_max` = largest K with `g_far(K) ≥ g_far_actual`. Bisection on each,
   ~40 steps; cheap.
4. Empty interval ⇒ refuse **`FarEndNotAbsorbable`** (existing kind) with both numbers: *"…the far
   end can absorb at most X against the Y its termination supplies, once the near end's Z is met."*
   `K_min` above `K_hi` ⇒ refuse **`AnalysisEndNotAbsorbable`** naming `g_near_actual` against the
   family's largest `g₁`.
5. Otherwise `K = K_min` (or 1e-6 when nothing constrains it). Build; split each reactive end with
   `MatchQ.SplitExcess`'s *sibling* in prototype units (a shunt C surplus is a parallel C, a series L
   surplus is a series L — no reciprocal case arises because the form only absorbs the like kind);
   mark `AbsorbedEnd`, `IsExcess` exactly as bandpass does. The near-end surplus is *not* `IsDetune`
   — §4.6's detune is a bandpass concept; it is `IsExcess` at end 1 or 2.

### 3.5 Build (lowpass)

Denormalise from `(ω_c, R_ana)`: shunt element k → `C = g_k/(ω_c·R_ana)`; series element k →
`L = g_k·R_ana/ω_c`. Names `C1, L1, C2, …` in Term1-first order, `ArmIndex` = element index, reversal
by element (there are no two-element arms to keep together). `R1`/`R2` are the two *targets*.

### 3.6 Highpass is the mirror (§16.6)

Do not write a second family. For `Highpass`: design the lowpass form over `a' = F1/F2` **with the
roles of the band edges swapped in the mapping** — i.e. work in `v = 1/u` with `ω_ref = 2π·F1`
(`F2 = ∞` allowed, `a' = 0`) — then every prototype shunt C becomes a shunt L `= R_ana/(g·ω_ref)` and
every series L a series C `= 1/(g·ω_ref·R_ana)`. Absorbable kinds swap: `Parallel+L`, `Series+C`;
normalised termination values: shunt L → `R/(ω_ref·L)`, series C → `1/(ω_ref·R·C)`. The pin is at ∞,
which is the same equation. The duality test in §6 holds this.

### 3.7 Golden values (computed while writing §16; independent of this code)

Normalised, a = 0.5, n = 2 (4 elements), r = 10, shunt-first, K = 1e-6:

```
g = [2.485340, 0.674662, 6.761736, 0.247821, 10.000000]      worst in-band |S11| = −10.511 dB
closed form  Γ₀²/(Γ₀² + T₂(x₀)²(1 − Γ₀²)) → −10.511 dB        (x₀ = −5/3, T₂(x₀) = 4.5556)
```

Physical, 5 Ω (analysis, parallel side) → 50 Ω, 2.5–5 GHz, lowpass:
`C1 = 15.8222 pF, L1 = 107.376 pH, C2 = 43.0466 pF, L2 = 39.4420 pH`, R_far = 50.000 Ω exactly.

Absorbing, same problem, analysis end **5 Ω ‖ 25 pF** (`g₁_actual = 3.92699`):
`K = 0.086588, g = [3.926991, 0.462564, 8.892688, 0.169822, 10]`, worst −8.010 dB, and `C1` = 25.0000 pF
is the load's own (`AbsorbedEnd = 1`, nothing added).
Same with **5 Ω ‖ 3 pF** (`g₁_actual = 0.4712 < g₁(K=1e-6) = 2.4853`): K stays 1e-6, the arm is
15.8222 pF of which 3 pF is absorbed and **12.8222 pF is an `IsExcess` element**.
Same with **5 Ω ‖ 80 pF** (`g₁_actual = 12.566`): refused, `AnalysisEndNotAbsorbable`, the message
naming 12.566 against the family's largest g₁.

From-DC reduction: `Lowpass, F1 = 0, n = 2, r = coth²(B/4) = 1.355383` for 0.1 dB gives
`g = [1.108895, 1.306153, 1.770419, 0.818019, 1.355383]` — **the textbook 0.1 dB n = 4 table**, and
`RippleG(4, 0.1)` must agree to 1e-5.

Butterworth, a = 0.5, r = 10: 4 elements −6.82 dB, 6 elements −10.64 dB. Chebyshev a = 0.66, r = 10,
4 elements: −18.54 dB; Butterworth −13.36 dB.

## 4. The Designer (`src/Ui/Match`)

### 4.1 Search cross-product (`MatchDesignerViewModel.Analysis.cs`)

The cell key becomes **(form, order, family)**. Enumerate: `Bandpass × ValidOrders × AllShapes` as
today, then `Lowpass × ValidOrders(form) × {ChebyshevFano, Butterworth}`, then `Highpass` likewise. The
design's own (form, order, family) is still searched first. `MatchSpecKey` is unchanged — the form is
a cell coordinate, not a spec input. Lowpass/highpass cells skip `FindQAdjust` (§16.4 item 3). The
response-verdict probe runs per form for the design's *current* form only (the verdict tooltip is a
per-family sentence; the other forms' feasibility is visible as rows).

### 4.2 Filter (`MatchSolutionFilterViewModel` + the flyout)

Add `Forms : ObservableCollection<MatchSolutionFilterToggle>` — three lines, **"Bandpass",
"Lowpass", "Highpass"**, all on by default — with a `Form` on the toggle (nullable, like `Shape`).
`Accepts` consults it. Place the group **first** in the flyout, above Orders, with a `Separator`.
`Summary` (the button tooltip) names hidden forms the way it names hidden families. Order lines: call
`SetOrders` with the union of the forms' valid orders so a like-topology pair (which lowpass cannot
serve) still lists its bandpass orders.

### 4.3 Cards, footer, summary, labels

- `TitleText` → `"{FamilyName} · {form} · order {n}"` for **every** row, bandpass included, so the
  three forms read alike. Form word lower-case: `bandpass`, `lowpass`, `highpass`.
- In lowpass/highpass rows `FamilyName(ChebyshevFano)` reads **"Chebyshev"** — the single/double
  distinction is bandpass-only. Give `FamilyName` a form parameter; the filter's *family* lines keep the
  bandpass spellings.
- Footer line and the Properties-panel summary (`ParameterEditorViewModel.Match.cs`) carry the form.
- **Rename per §6.9** (display only; enum and payload untouched): selector
  `"Chebyshev — single-match (optimum)"` / `"Chebyshev — double-match (exact)"`; card/filter
  `"Chebyshev (single-match)"` / `"Chebyshev (double-match)"`; tooltips per §6.9's table. If the owner
  has amended §6.9's wording since this brief was written, §6.9 wins.

### 4.4 Transform rack empty state

When the basis is lowpass/highpass, the rack shows one note line — *"Lowpass and highpass networks
have no Norton pairs: every value is the prototype's."* — and hides `+ add`/`− remove`/link. It must
**not** show the bandpass "no pairs available" wording, which reads as a fault.

### 4.5 Applying a solution sets `Form`

Exactly as applying one sets `Order` and `Response` today, and through the same undo entry.

### 4.6 Ladder preview / grid / plots / Flatten / stamp

Single-element arms are already representable (`MatchNetwork` is a flat list). Verify by test that
`MatchLadderLayout` places a lowpass ladder without assuming two elements per arm, and that
`MatchFlatten` and the stamp produce the same S-parameters as the Designer's ABCD for a lowpass
design — extend the existing "component ≡ flattened cell" test with one lowpass and one highpass
fixture. No drawing changes are expected.

## 5. Registry and reference

- `ComponentTypeRegistry.cs` ~879: add the `Form` echo parameter (`"Bandpass"` default) beside
  `Response`; add `"lowpass"`, `"highpass"`, `"bandpass"` to the palette search terms.
- `docs/user/src/reference/match.md`: the *Response* row lists the four families under their §6.9
  names; add a *Form* row and a short paragraph after the Solutions-panel description stating §16.2's
  trade in two sentences (tame values at wide bandwidth and a DC path, at the cost of return loss
  that depends on the ratio — with the a = 0.5 row of §16.2's table). Do **not** regenerate the
  figures unless a figure's content changed; if `DocMatchFixtures` needs a lowpass fixture, add one
  rather than editing the existing ones.

## 6. Tests

`tests/Core.Tests/Match` (new `MatchFormSynthesisTests.cs`) — every row of §13.2 marked rev 2:

1. **Golden A** — §3.7's normalised g to 1e-5, physical values to 1e-4 relative, `R2 = 50` to 1e-9.
2. **Closed-form oracle** — for a in {0.33, 0.5, 0.66} × n in {2, 3} × r in {2, 10}, the worst
   in-band |S11| from `MatchAbcdOracle` (not `MatchResponse`) matches
   `10·log10(Γ₀²/(Γ₀² + T_n(x₀)²(1 − Γ₀²)))` to 0.05 dB. This is the §16.2 table.
3. **From-DC reduction** — Golden E against `RippleG(4, 0.1)` to 1e-5.
4. **Absorption identity** — Golden B (`C1 == 25 pF` to 1e-12 relative, `AbsorbedEnd = 1`, no
   `IsExcess`), Golden C (excess 12.8222 pF, `IsExcess`), Golden D (refused, kind and both numbers in
   the message).
5. **Form-vs-kind refusals** — `Parallel+L` in lowpass and `Parallel+C` in highpass refuse with
   `FormCannotAbsorb`, message naming the other two forms.
6. **Like-topology pair** — `ValidOrders(term1, term2, Lowpass)` is empty for two parallel ends; the
   Designer lists only bandpass rows for that design.
7. **No Norton pairs** — `NortonTransform.Discover` returns empty for every lowpass/highpass basis in
   the sweep above, and `RequiredTransformRatio == 1` to 1e-9. Written against the *unchanged* scan.
8. **Duality** — highpass over [F1, F2] equals lowpass over the mirrored band with L↔C, same worst RL to
   1e-9 dB.
9. **Old payload** — a hand-written pre-rev-2 JSON (no `Form`) decodes as `Bandpass` and rebuilds the
   identical ladder.
10. **K = 0 trap** — K = 0 exactly does not extract and K = 1e-6 does (pins §16.9 so the floor is not
    "cleaned up").

`tests/Ui.Tests/Match` (new `MatchFormDesignerTests.cs`): the filter has three form lines on by
default; turning `Lowpass` off hides lowpass rows and leaves the applied row; applying a lowpass row
sets `Design.Form`, empties the rack with the §4.4 note, and one undo restores the bandpass design;
card titles carry the form word; `FamilyName(ChebyshevFano, Lowpass) == "Chebyshev"`; the
component ≡ flattened-cell test passes on a lowpass and a highpass fixture.

**No timing tests.** The lowpass cells are a bracketed 1-D solve; if the full cross-product search
measurably slows a spec edit, report the number rather than adding a benchmark.

## 7. Gates

```
dotnet build
dotnet test tests/Core.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

Run each ONCE and read `TestResults/last-run.trx` for any failure (repo `CLAUDE.md`, "Run the test
suite ONCE"). Before finishing, grep the diff for vendor or product names (repo `CLAUDE.md`,
*Commercial Vendor References*) — none are expected, but the user reference is prose.

## 8. On completion

Write what was learned — anything §16 got wrong, any number that did not reproduce, any trap not in
§16.9 — to **`src/Core/Match/RESOLVED.md`** (create it if absent) and, for Designer findings,
`src/Ui/RESOLVED.md`. **Never to any `CLAUDE.md`.** Do not commit; the owner commits.

## 9. Out of scope, deliberately

- Odd element counts in lowpass/highpass form (§16.3 — a weighted Remez family; recorded follow-up).
- Bessel or double-match Chebyshev in these forms (§16.2 — no free parameter to spend).
- Elliptic / inverse Chebyshev anywhere (§6.8).
- Q-adjusted solutions in these forms (§16.4 item 3 — the near-end surplus already is one).
- A form selector in the specification pane (§16.7 — the form is chosen by applying a solution).
- A "notch at f_z" feature (§6.8, last paragraph).
