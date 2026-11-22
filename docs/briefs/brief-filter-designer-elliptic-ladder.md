# Brief FD-E — a shipping-grade elliptic ladder: arbitrary order, correct drawing, no stall

**Status:** proposed · **Date:** 2026-11-21 · **Area:** `src/Core/Systems` (synthesis),
`src/Ui/Filters` + `src/Ui/Match` (drawing, window), `src/Cli/FilterVerb.cs`, docs.

**Read before starting, and follow:** `CLAUDE.md` (all of it — especially *Run the test suite ONCE*,
*Validation expectations*, *Commercial Vendor References*, *Commit expectations*),
`ARCHITECTURE.md`, `docs/design/filter-designer.md`, `docs/design/match.md`,
`docs/user/src/reference/filter-designer.md`, `docs/design/cli.md` (the `filter` verb), and the
Filter Designer sections of `src/Core/RESOLVED.md` (search "Filter Designer", "ideal filter"). Findings
go to `src/Core/RESOLVED.md` (synthesis) and a `src/Ui/RESOLVED.md` entry (drawing) — never to
`CLAUDE.md`. Paraphrase the owner; do not quote. Do not commit unless the owner says so.

---

## 0. The owner's complaint, paraphrased

1. An elliptic **bandpass** ladder placed as a schematic is drawn wrongly: rails of one series arm
   run into the next arm, labels sit on the wrong glyphs, an element has no label at all. The
   expected picture is the textbook one (§3.1).
2. The elliptic family **fails at higher orders**. The bar is a commercial EDA filter-synthesis tool
   and a public online LC filter calculator, both of which realise elliptic ladders of any practical
   order — odd **and** even.
3. **High orders are slow**; the window stalls while it searches.
4. The result must be shipping-ready: correct or refused with a reason, never "approximately right".

## 1. What is actually wrong (diagnosed, not guessed — verify each before fixing)

### 1.1 The schematic shorts adjacent tanks (electrical bug, not cosmetic)

`MatchSchematicModel.AddWiring` (`src/Ui/Match/MatchSchematicModel.cs`, the `foreach (var arm in
layout.ParallelArms)` loop) and its twin `MatchSchematicCopy.SpineSegments`
(`src/Ui/Match/MatchSchematicCopy.cs`) fill an arm's rail by walking **every** element whose `Y` equals
the rail's `Y` — not only those between `arm.X0` and `arm.X1`. With two bridged arms (any elliptic
bandpass of order ≥ 5) both tanks put an element on the same rail height, so arm 1's walk:

- emits a wire from arm 1's glyph **to arm 2's glyph** — joining the two tanks' internal nets, and
- advances `run` past `arm.X1`, so arm 1's glyph is **never wired to its own right-hand drop**.

That is the long horizontal line across both tanks in the owner's screenshot. The designer preview
and the copied schematic are both affected. The ladder simulated in the window is the `.cnl`, which is
correct — so the plot looks right while the placed schematic is a different circuit. **Fix both copies
by clamping the walk to `[arm.X0, arm.X1]`, and then delete the duplication**: one wiring routine both
callers use (they are already line-for-line the same).

### 1.2 The bridged-arm layout is not the textbook one, and labels collide

`FilterLadderDrawing.PlaceBridged` puts the series resonator on the spine and the tank's L and C on
two rails 180 units apart (`ParallelStep * 0.55`) above it. Glyph labels are placed by a rule that
assumes one element per column, so the rail labels land on the spine glyphs and vice versa, and one
falls outside the picture. The arm is electrically a **three-branch parallel**: (series L–C) ∥ L ∥ C.
It must be drawn as one (§3.1).

### 1.3 Even order is refused outright

`FilterLadder.SynthesizeWithZeros` refuses every even elliptic: a type-a even elliptic keeps
`|S21(∞)| > 0`, which no LC ladder can do. That is true and beside the point — the standard answer is
the **modified elliptic types b and c** (Saal & Ulbrich), which every serious tool offers. Meanwhile
`FilterOrder.Minimum` happily returns an even order for an elliptic mask, so in *Minimum* mode the
window regularly picks an order it then refuses. That is the most common "fails at higher order".

### 1.4 The synthesis is numerically fragile and the fix attempts were heuristics

The prototype goes coefficients-first: `EllipticPrototype.Build` multiplies out `N(Ω)` and `D(Ω)`,
`FilterPrototype.FromCharacteristic` spectrally factors `D² + ε²N²` with polynomial roots, and
`FilterLadder` performs zero-shifting on expanded coefficient arrays in `double`. Expanded
coefficients of an order-11+ elliptic span many decades; every step then needs a tolerance to accept
it:

- `Divides` accepts a synthetic division whose remainder is up to `1e-3 × max|coef|`;
- `Tank` accepts a residue with an imaginary part up to `1e-4` relative;
- `SynthesizeWithZeros` accepts a ladder whose input impedance is **up to `3e-3 × order` (≈ 3 %
  at order 11) away from the prototype**, ranked by an ad-hoc `rank` with a "+1 if short" penalty.

So a ladder can be wrong by a few percent and still be returned. The 2026-11-18 attempt to reach
order 11+ by tightening these heuristics (`a7bf8db25`) was reverted (`87995bd0e`). **Do not iterate
on the search heuristics again; replace the method (§2).**

### 1.5 Exponential search on the UI thread

For order ≥ 9 the fallback `TryExtractAll`/`RecurSearch` enumerates every partial-removal order and
every zero ordering, both zero sort directions, and repeats it in a frequency-scaled plane — a search
exponential in the number of zeros. `FilterDesignerViewModel.Design()` runs it **synchronously on the
UI thread** on every property change, then elaborates and runs `SParameterEngine` on the same thread.
`FilterNetwork.Create` for the ideal curve also goes through the same expanded polynomials and fails
at high order, which is why the window sometimes shows a ladder with no ideal trace.

---

## 2. The synthesis — the required approach

### 2.1 Prototype in root form (closed form for type a; mapped for b and c)

Represent every prototype by its **roots**, not expanded coefficients: transmission zeros `±jΩ_z`,
reflection zeros `±jΩ_r`, and the left-half-plane poles `p_i` of `S21`. Expand to coefficients only
inside the extraction, and only in double-double (2.3).

- **Type a (odd order; also even order for the ideal block only).** Zeros and poles are closed form
  via Jacobi functions of complex argument (Orfanidis, *Lecture Notes on Elliptic Filter Design*,
  2006, §§ on `cd`/`sn` with complex argument and the `v0` from `sn⁻¹(j/ε_p, k1)`). The existing
  `JacobiCd`, `EllipK`, `SelectivityModulus` stay; add `sn`/`cd` at complex argument (descending
  Landen again — it is accurate at the selective end). No polynomial root finder is involved.
- **Types b and c (even order).** Apply a monotone Möbius map in `x = Ω²` to the type-a zero/pole
  locations. With `x_p` = the **largest** transmission zero squared and `x_r` = the **smallest**
  reflection zero squared:

  ```
  x_old = (a·x_new + b) / (c·x_new + 1)
  type b:  b = 0,    c = 1/(x_p − 1),          a = x_p·c
  type c:  b = x_r,  c = (1 − x_r)/(x_p − 1),  a = x_p·c
  ```

  `g(1) = 1` keeps the passband edge at 1; `g(∞) = x_p` sends the highest transmission zero to
  infinity (a double zero at ∞ — now an LC ladder exists); type c also sends the lowest reflection
  zero to DC, so `|S11(0)| = 0` and the terminations are **equal**. Each finite zero/pole maps in closed
  form, `x_new = (x_old − b)/(a − c·x_old)`; nothing is re-expanded. Type b has `|S21(0)|² =
  1/(1 + ε_p²)`, so its load ratio `r` solves `4r/(1 + r)² = 1/(1 + ε_p²)` (take `r > 1` or its
  reciprocal according to *First element*). The stopband edge moves up to `Ω_s,new² = g⁻¹(Ω_s²)` while
  `Astop` is preserved — so **minimum order for b/c is not the degree equation**: compute it by
  evaluating the mapped response at the mask's stop edge and stepping `n` up until it passes.
- **Poles of b/c** have no closed form. Get them as roots of the degree-`n` polynomial in `x` from
  `D(x)² + ε²N(x)²` — then **polish every root by Newton on the product form** (`N` and `D` evaluated
  as products of `(x − x_k)`, which is well conditioned), and select left-half-plane `s`. Assert the
  polished `|S21|` at the ripple extrema equals the stated ripple to 1e-10 dB.

### 2.2 Zero-shifting with a deterministic zero order — no search

The textbook procedure for an elliptic ladder (Saal & Ulbrich 1958; Amstadt; Zverev, *Handbook of
Filter Synthesis*, ch. on elliptic) is **one pass**: at each step, a partial removal at infinity
(series L or shunt C) that shifts a zero of the remainder onto the next transmission zero, then the
full removal of that zero as a tank (parallel LC in series, or series LC to ground), and a final full
removal at infinity. The only freedom is the **order in which zeros are taken**, and positivity
depends on it. Use a fixed short list, tried in this order, each a single linear pass:

1. lowest transmission zero (closest to the passband) in the **centre** section, remaining zeros
   alternating outward with the highest at the ends;
2. ascending; 3. descending.

Take the first ordering whose elements are all positive **and** that passes the exact verification
of 2.4. That is ≤ 3 × `n/2` extraction steps — microseconds — instead of an exponential tree.
`TryExtractAll`, `RecurSearch`, the frequency-scaled replay, `rank`/`fitRank`, and `zinCap` are
deleted. The existing all-pole path (`ClosedFormG`, Bessel/Gaussian through `PrototypeG` and
`FilterLadderHighPrecision`) is **not touched** except where it shares a deleted helper; inverse
Chebyshev moves onto the new zero path (same structure, closed-form zeros) and its tests must still
pass.

*Minimum-L vs minimum-C* is the same procedure started from impedance vs admittance; *First element*
already picks it. A request whose start end cannot be realised positively falls back to the dual and
**says so in the status**, as today.

### 2.3 Precision

Do all extraction arithmetic in **double-double** (106-bit mantissa). `FilterLadderHighPrecision`
already has a `Dd` type and `DdPoly` — promote both to a small `internal` numeric helper in
`src/Core/Systems` (or `src/Core/Matching` if Match can use it) rather than writing a second one.
Before extraction, **scale frequency** so the geometric mean of the transmission zeros is 1 (and
unscale the element values after) — this is what keeps coefficient spread manageable. Build the
initial polynomials from roots with pairwise products in `Dd`. Divisions by `(s² + Ω_z²)` are
checked with a **relative remainder bound of 1e-20 in Dd** (not `1e-3`); a step that fails it ends
that ordering.

No managed BigFloat/MPFR dependency (native dependencies need the owner's yes, and Dd suffices for
the range in §5). If an order beyond the gate needs more, stop and report the measured limit.

### 2.4 Exact verification — the ladder is correct or it is refused

After extraction, before returning, compute the ladder's own `S21` and `S11` at ≥ 401 points
(log-spaced 0.01·Ωp…100·Ωs plus every zero and ripple extremum) by the ABCD chain in `Complex`, and
compare with the prototype's `S21` from roots:

- `max |S21_ladder − S21_proto| ≤ 1e-9` and transmission-zero frequencies to 1e-9 relative;
- lossless: `|S11|² + |S21|² = 1 ± 1e-12`.

Fail → try the next ordering; all fail → `FilterLadderRefusal` naming the order, the ripple/Astop and
the measured error. **Never return a ladder that failed this.** This replaces `ZinError` and
`ZerosLand`.

### 2.5 The ideal curve

`FilterNetwork` evaluates the prototype from roots (products), not expanded coefficients, so the ideal
trace exists at every order the ladder does. It gains the elliptic type (a/b/c), so the ideal and the
ladder traces are the **same** response. The `Filter` component (`FilterModel`) gets an
`EllipticType` parameter **appended with default `a`**, so every existing `.csch`/`.cnl` keeps its
meaning (the precedent is `NetworkForm.Bandstop`, appended). *Apply to Filter* writes it. Its
registry description is what `{{table: components/Filter}}` reads — update it there.

---

## 3. The drawing

### 3.1 Target pictures (the owner's reference, described)

- **Bandpass series arm** (lowpass L∥C tank in the through path) — a box between nodes `a` and `b`
  with **three parallel branches**: top rail = series resonator L then C; middle = the parallel L **on
  the spine**; bottom rail just below the spine = the parallel C. The box is as wide as the series
  pair; both drops land on spine dots at `a` and `b`.
- **Bandpass shunt arm** (lowpass shunt C) — L and C as **two adjacent shunt legs** from one spine
  node, each to its own ground (current behaviour; keep it, with one junction dot per leg).
- **Lowpass/highpass series tank** — L∥C: C on the spine, L on one rail above (keep).
- **Shunt series-LC stub** (lowpass elliptic, shunt-first) — L above C in one column to ground (keep).
- **Bandpass shunt stub** (from a series-LC-to-ground arm) — one column: series L, series C, then an
  L∥C pair bridged to ground.
- **Bandstop** — the duals of the above, using the same primitives.

### 3.2 Rules the layout must satisfy (and tests must assert)

1. Every label is adjacent to **its own** glyph: its anchor lies inside that glyph's bounding box
   grown by one lead length, and closer to it than to any other glyph.
2. No label overlaps another label, a glyph or a wire (bounding-box test, all forms, orders 3–15).
3. Every parallel-arm wire lies within its own `[X0, X1]`.
4. **Topology equality:** extract the netlist from the generated schematic (`NetExtractor`, the path
   `circuitrf netlist` uses) and simulate it; `S21` must equal the ladder `.cnl`'s to 1e-12 at 201
   points. This is the gate that would have caught §1.1 and is the one that matters most.
5. Visual check, not just numeric: render the placed schematic with
   `dotnet run --project src/Cli -- render <cell> -o /tmp/x.png` for bandpass elliptic orders 5, 7, 9
   and bandstop 5, look at the PNGs, and keep them out of the repo.

`FilterLadderDrawing` should place by **arm**, not by pattern-matching loose elements: have
`FilterLadder` return arm structure alongside the flat `Elements` (e.g. an `Arms` list of
`{Kind, element indices}`), so the drawing never has to rediscover which four elements form a box.
The `TryBridged`/`TryStub` inference is then deleted.

---

## 4. The window and the verb

- `Design()` becomes: debounce input (~150 ms), run synthesis + ideal + `SParameterEngine` **off the UI
  thread** with a `CancellationToken` (latest request wins; a superseded result is dropped), marshal the
  result back through `Dispatcher.UIThread`. Status shows "Designing…" while running. The relay command
  stays the entry point so existing tests that call `Design()` keep a synchronous seam (expose
  `DesignAsync` and have the test await it, or keep an internal synchronous core).
- Cache the root-form prototype by `(family, order, ripple, Astop, type)` — changing frequencies,
  form, impedance, units or snap series must not re-synthesise the prototype.
- New control **Elliptic type**: *Auto* (odd → a, even → c), *a*, *b*, *c*. Shown only for Elliptic.
  Type a at even order in the ladder is refused with a sentence that names b and c.
- *Minimum* order uses the type actually being built (§2.1).
- `circuitrf filter` gains `--elliptic-type auto|a|b|c` and keeps calling the same
  `FilterLadder.Synthesize` — no second synthesis. Update `docs/design/cli.md` and the user CLI page.

## 5. Gates

References are **externally generated** (CLAUDE.md *Validation expectations*): add
`testdata/filter-elliptic/` with a Python + `mpmath` (50 digits) generator written independently of
the C# — its own Jacobi functions, its own zero-shifting — and commit its output CSV (family, type,
order, ripple, Astop, first, element values, load ratio). Note in its README how it was produced.
Cross-check two or three entries against a published elliptic table by hand and record which.

| Gate | Requirement |
|---|---|
| G1 element values | Every row of the reference CSV reproduced to **1e-8 relative** — orders 3–21, types a (odd) / b / c (even), ripple {0.01, 0.1, 0.5, 1} dB × Astop {20, 40, 60, 80} dB, both first elements. |
| G2 high order | Orders 22–31 at ripple 0.1 dB / Astop 60 dB: **either** passes §2.4 **or** refuses with a reason. A returned ladder that fails §2.4 is a test failure. Record the highest order reached in `src/Core/RESOLVED.md`. |
| G3 response | Ladder `.cnl` through `SParameterEngine` vs ideal `FilterNetwork`: `|ΔS21| ≤ 1e-9` unsnapped, lowpass/highpass/bandpass/bandstop, orders 5, 8, 13. |
| G4 drawing | §3.2 rules 1–4 for every form at orders 3–15 (elliptic) plus the existing Butterworth/Chebyshev drawing tests. |
| G5 speed | Synthesis alone: order 15 < 5 ms, order 25 < 50 ms (median of 20, Release). The window never blocks: a UI test changes Order rapidly 1→25 and asserts the UI thread is never busy > 50 ms. Tag any measurement ≥ 5 s `Category=Benchmark`; the others stay routine. |
| G6 no regressions | Existing `FilterLadderTests`, `FilterPrototypeTests`, `FilterOrderTests`, `FilterSParamTests`, `FilterHbTests`, `FilterLadderDrawingTests`, `FilterValuesTests`, the CLI filter tests. `AnEvenEllipticIsRefusedBecauseTheFloorDoesNotReachZero` is **replaced** by: even type a refused (message names b/c), even b/c built. |
| G7 minimum order | For 50 random elliptic masks, *Minimum* returns an order that builds and whose ladder meets the mask; the order is the smallest that does for the chosen type. |

Run scope (per CLAUDE.md, once each, read the TRX for failures — do not re-run to find them):
`dotnet test tests/Core.Tests`, `dotnet test tests/Ui.Tests --filter "FullyQualifiedName~Filter|FullyQualifiedName~Match"`
during work, then `dotnet test tests/Ui.Tests`, `dotnet test tests/Engine.Tests --filter
"FullyQualifiedName~Filter"` and `dotnet test tests/Firewall.Tests` at the end.

## 6. Documentation to update

`docs/design/filter-designer.md` (the mathematics section: root form, types a/b/c, zero ordering,
verification, threading), `docs/user/src/reference/filter-designer.md` (elliptic type, even orders,
the refusal sentence), `docs/user/src/reference/components.md` / `system-components.md` (the new
Filter parameter), `docs/design/cli.md` + `docs/user/src/reference/cli.md` (`--elliptic-type`), then
regenerate the HTML the way the user docs are built (`tools/DocGen`). Findings → `src/Core/RESOLVED.md`
and `src/Ui/RESOLVED.md`.

## 7. Guardrails

- No commercial tool, vendor or product names anywhere — code, tests, docs, RESOLVED, commit text.
  Say "a commercial EDA tool" or nothing. Grep before finishing.
- No GPL code ingested. Literature formulas are fine; copied code from GPL filter packages is not.
- No new native dependency.
- `src/Core` stays UI-free (Firewall.Tests).
- Do not touch Butterworth/Chebyshev closed-form paths beyond shared-helper moves.
- Stop and report (do not paper over) if: Dd is insufficient below order 21 at G1's specs; type b/c
  minimum-order stepping disagrees with the reference generator; or the topology gate (§3.2 rule 4)
  reveals other Match-drawing users (Match Designer bandpass) relying on the unbounded rail walk.

## 8. Phasing (each phase ends green on its gate)

1. **Drawing short (§1.1)** + topology gate (§3.2 rule 4). Small, independent, ships first — it is a
   wrong-circuit bug in the current product.
2. Root-form prototype + types b/c + `FilterNetwork` from roots (G3 ideal side, G7).
3. Dd zero-shifting with fixed orderings + §2.4 verification; delete the search (G1, G2, G6).
4. Arm-structured drawing (§3) (G4).
5. Background design + caching + Elliptic-type control + CLI flag (G5).
6. Docs.
