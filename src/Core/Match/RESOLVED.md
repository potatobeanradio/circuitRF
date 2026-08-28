# src/Core/Match — resolved briefs

Findings from the Match component's own briefs, kept out of any `CLAUDE.md`. Same pattern as
`src/Core/RESOLVED.md`: one `##` section per brief, only what is still true, still surprising, and
would cost someone real time to rediscover.


## Round 7 — the stored payload is JSON, and the order picker is the real remedy (2026-08-20)

### `Encode` writes JSON; only `CnlWriter` makes a token

Owner: *"the .csch is showing a Match component instance with Expression all crazy text. Recall that
all circuitRF file formats are supposed to be human readable."*

Base64 was there for a real reason, and the reason belongs to exactly one format. A `.cnl` is
whitespace-delimited and its only string escape is a pair of quotes, with no way to escape a quote
inside one — so a design's JSON cannot be a `.cnl` token. **A `.csch` is JSON and never had that
problem**, and nothing else in the application writes a design to a token format. So:

- `MatchEmbedding.Encode` → the JSON (also *shorter* than the base64 it replaced, by a third).
- `MatchEmbedding.EncodeToken` / `ToToken` → base64, unpadded, for a `.cnl`.
- `CnlWriter.FormatParam` converts a brace-leading `Design` expression on its way out. Keyed on the
  PARAMETER NAME, not the component type: `"Design"` is the embedded-payload parameter for Match and
  for wBond alike, and `Core` cannot see the wBond assembly to ask it anything. Nothing else in the
  netlist writes a brace-leading expression — the expression language has no token starting with `{`.
- `TryDecode` already accepted both, so every file written before this still loads and a hand-authored
  `.cnl` may still carry base64. **The padding must stay stripped** — `CnlReader`'s spaced-assignment
  merge reads a token ending in `=` as an empty assignment and glues the next parameter on as its
  value.

Tests that hand-build `.cnl` TEXT (`MatchComponentTests`, `MatchStampTests`, `TerminationProbeTests`,
`MatchFlattenPlanTests`) must use `EncodeToken`. Anything writing a component parameter wants `Encode`.

Making the payload legible immediately exposed that four **computed** properties — `MatchDesign.Omega0`,
`W`, `Termination.HasReactance`, `AbsorbedType` — were being serialized as though they were inputs.
They have no setter, so a reader silently ignored them; a person reading the file would not have.
`[JsonIgnore]`, and anything derived from a field on that record belongs behind it too.

### 50 Ω into 5 Ω ∥ 1 pF: order 3 genuinely cannot, order 4 gives −43.5 dB

Owner: *"I find it hard to believe that the Match component cannot match a 50 ohm termination to a
parallel RC of 5 ohms // 1pF at 2 GHz. Am I doing something wrong?"*

Measured over 1.8-2.2 GHz, Chebyshev-Fano, Π N² required = 10:

| order | reachable Π N² | solutions | worst in-band RL after applying |
|------:|---------------:|----------:|--------------------------------:|
| 2     | 1              | 0         | —                               |
| 3     | 1.016          | 0         | —                               |
| 4     | 10             | 6         | **−43.5 dB**                    |
| 5     | 10             | 16        | −48.1 dB                        |

Two-ended Chebyshev reaches it at order 3 (6 solutions). Butterworth and Bessel refuse the prototype
outright at every order for an analysis-end Q of 0.0625 — a very low-Q end is the case those two
families have no positive g-vector for, which is worth knowing before reading their refusal as a bug.

So the refusal was CORRECT and the design was one picker click from working. What it lacked is what
the MoM-ceiling lesson in `src/Engine/Mom/RESOLVED.md` is about: **a remedy is only a remedy if it
binds.** "Allow negative components, change the order, or change the response" named the right knob and
left the user to find the setting by trying five. `MatchDesignerViewModel.FindWaysOut` now re-runs the
search at each permitted order and each FEASIBLE response and names the ones that work by number, so
the sentence ends *"Order 4, 5 and 6 do reach it with this response, and two-ended reaches it at this
order."* It runs only on an already-refused design, on the analysis worker, under the same
cancellation — a design with solutions pays nothing, and `MatchOrders.ValidOrders` is a short list (a
termination pair fixes the parity), never a range.

## Round 7, part 2 — a Q-adjust may inflate an end's Q, never reduce one (2026-08-20)

Owner: *"I press the probe button on Terminal 2 and the parasitic updates to 1000 pH, but the
schematic keeps rendering as 2000 pH."*

### The tell is that the ladder was insensitive to the termination

1 nH and 2 nH at termination 2 produced **element-for-element identical** networks. That is not stale
rendering; it is the synthesis not looking at the termination at all.

`Synthesize` takes `qAna = design.QAdjust > 0 ? design.QAdjust : qAnaActual` and **never checked that
the adjustment is above the end's own Q.** The design carried `QAdjust = 2` — legitimate when that
end's Q was lower — and the probe then wrote a 1 nH parallel L whose Q at band centre is 3.999. So:

1. the end arm was built for Q = 2, which is a 1999 pH shunt inductor;
2. `WithEndSplits` skips its split whenever `qSynth / qActual <= ExcessRatioThreshold`, and here the
   ratio is *below* 1 — so the element kept the **synthesis's** value;
3. …while still carrying `AbsorbedEnd = 2`, so the drawing labelled 1999 pH as supplied by a
   termination that supplies 1000 pH, and the response was computed from an inductor the circuit does
   not contain. The status strip called it matched at −37 dB.

### The far end had this refusal all along; the analysis end never got its counterpart

`FarEndNotAbsorbable` — *"the synthesis reaches Q_far = X against the termination's own Y"* — is the
same statement about the other end. `AnalysisEndNotAbsorbable` is the missing half, checked
immediately after `qAna` is chosen, before any prototype work (it depends on nothing the prototype
produces, and a refusal is cheapest the moment it is knowable). **A parasitic cannot be subtracted**:
if the end arm needs less reactance than the termination already provides there is no network, so this
is a refusal rather than a clamp — §4.6's Q-adjust *inflates* an analysis end's Q by construction.

Guard: `QAdjust > 0 && qAnaActual > 0 && QAdjust < qAnaActual * (1 - 1e-9)`, mirroring the far end's
exact-comparison guard rather than `ExcessRatioThreshold`.

`MatchSynthesis.AnalysisIsTerm1(design)` is public now. The Designer has to know which end a Q-adjust
is about before it can tell whether the stored one is still legal, and re-deriving "highest" at a
second site is how the two would come to disagree.

### Measured, on the owner's own case

50 Ω into 5 Ω ∥ 1 pF, 1.8-2.2 GHz, order 3, termination 2 = 50 Ω ∥ 1 nH (Q = 3.999):

| QAdjust | result |
|--------:|:-------|
| 0       | absorbed L3 = **1000 pH**, exactly `Term2.Value` |
| 2       | refused, `AnalysisEndNotAbsorbable` (was: silently drew 1999 pH) |
| 6       | synthesises — a genuine inflation, which is what §4.6 means |

## Match round 3 — `Discover` promised a pair `Apply` refuses (2026-08-20)

Owner-reported crash, straight out of the *+ add* menu with no dialog and no recovery:

```
System.InvalidOperationException: Making a transform pair adjacent would swap
C2_N1_1_N2_1_N3_2 past C2_N1_1_N2_1_N3_3, which have different orientations
  at NortonTransform.RequireSameOrientation
  at NortonTransform.Apply
  at MatchRebuild.ApplySequence  →  MatchDesignerViewModel.AddTransform
```

### The type test was a proxy for "same arm", and the proxy is only true on a FRESH ladder

`Discover`'s gap-3 case offers a pair three apart, which `Apply` makes adjacent by walking each end
inward one place. Both walks are response-preserving only if each stays inside an arm — that is,
`el[j]` shares an orientation with `el[j+1]`, and `el[j+3]` with `el[j+2]`. What the guard actually
tested was `el[j+1].Type == el[j+2].Type`.

On the ladder the synthesis emits, elements strictly alternate in both type and orientation, and the
type test happens to select the same pairs the orientation test would. **A transform's own three
products break that**: they are all one type and alternate in orientation by construction (pi is
shunt-series-shunt, T is series-shunt-series). A pair straddling a previous transform's triple
therefore passed the type test, was offered in the menu, and hit `Apply`'s assert. The reported names
say it exactly — `_N3_2` and `_N3_3` are products 2 and 3 of one triple, which **always** differ in
orientation.

The fix is the orientation condition, stated rather than proxied. The type test is kept: it selects a
strictly smaller set and removing it would change which transforms the solution search offers.

### The existing test could not have caught it, and the reason is worth stating

`AdjacencyMoves_NeverSwapAcrossAnOrientationBoundary` already walked *every discovered pair at every
reachable order* and applied each one — on the **freshly synthesised** ladder only, which is precisely
the ladder where the proxy holds. Nothing in the suite had ever re-discovered on a ladder that had
already been transformed. `AdjacencyMoves_StayInsideAnArm_OnLaddersThatHaveAlreadyBeenTransformed`
does: four rounds of apply-then-rediscover, both forms, three orders. Reverting the guard makes it
fail with the owner's own message, at round 4.

### A rebuild now reports an unapplicable transform instead of throwing

`MatchRebuild.ApplySequence` already drops a transform *with a note* when its pair no longer exists in
the ladder. An `Apply` that refuses is the same class of event and is handled the same way — the
Designer shows the note, keeps the rest of the sequence, and stays open. This is belt-and-braces, not
the fix: an escaping `InvalidOperationException` from a menu click is an application crash whatever
caused it, and the disagreement itself is fixed at its source in `Discover`.

## MN-1 — the synthesis core (2026-08-19)

Implements `docs/design/match.md` §4 (synthesis), §5 (inductive terminations), §6 (response shapes)
and §7 (data model and rebuild) as pure algorithm. No UI, no `ComponentModel`, no stamping.
Gate: `dotnet test tests/Core.Tests` (1,444 tests, ~4 s) and `dotnet test tests/Firewall.Tests`.

### The namespace is `CircuitRF.Core.Matching`, not `CircuitRF.Core.Match`

The directory is `src/Core/Match/` as the design doc specifies, but the namespace could not be. A
namespace named `Match` under `CircuitRF.Core` is visible as the bare identifier `Match` from every
sibling namespace, which collides with `System.Text.RegularExpressions.Match` — `PdkImporter.cs`
alone has four `foreach (Match m in ...)` loops, and the whole project stops compiling with
`CS0118: 'Match' is a namespace but is used like a type`. Renaming the namespace costs nothing and
touches no file outside this directory; renaming four loop variables in someone else's file to make
room for a namespace is the wrong trade. `Matching` also matches the palette category the design doc
already decided on (§14 item 3).

### The absolute value guards REPORT, they do not repair — and the reason is measurable

The reference implementation clamps any produced Norton value above 1 H / 1 F to exactly 1.0 and
anything below 1e-24 to 0.0. That clamp is kept as a *condition* (`TransformApplication.GuardFired`)
and dropped as an *action*, because rewriting a value is precisely what breaks the one invariant the
whole transform rack rests on: a "repaired" network no longer has the response it claims, and nothing
anywhere says so.

It is reachable, which is the part worth knowing. `N` held one part in 1e9 inside its positivity
threshold is still one part in 1e9 from a pole, so a solver that parks `N` on the bound produces a
mathematically exact, response-preserving, and completely unbuildable element. **On the design doc's
own §4.9 problem that is a 2.9 kH inductor.** The brief's own §11 sweep never sees it (it samples
2 %..98 % of each range and the guard correctly never fires there); only the solution search's drive
reaches the bound. So "a firing guard means the threshold logic is wrong" is not quite right — here
it meant the *N allocation* was wrong.

### The solution search seeds N at its equal geometric share, not at 1

Brief §8 starts every `N` at 1 and then corrects index by index. The first index then asks for the
entire ratio at once — `sqrt(119.027) = 10.91` on the golden problem — is clamped onto its own
positivity threshold of 5.989, and every later index only mops up the remainder. That is what parks
`N` on the bound and produces the 2.9 kH element above; with the guards clamping as specified, the
network is then silently corrupted and every solution is rejected. Seeding each `N` at
`required^(1/2k)` lands the same two-transform set on `N = 3.303` each, well inside both ranges, with
the same product and the same response. **The index-by-index correction after the seed is unchanged**
and is still what handles a set whose ranges cannot take equal shares.

### The §4.5/§4.6 end splits run LAST, after every transform — not inside the basis ladder

Two independent reasons, both load-bearing:

1. **An extra element inside the basis list breaks pair discovery.** Brief §7.3's move offsets are
   only response-preserving because every swap they ask for is between two elements of the *same*
   arm; that holds because the basis list alternates strictly `(L, C) x (shunt, series)` with period
   four. Insert `CFano` next to the far arm's absorbed capacitor and the period breaks: the gap-3
   move then swaps a shunt element past a series one, which is a different circuit, not a
   re-ordering. (`NortonTransform.Apply` asserts the orientation rather than trusting the table, so
   this fails loudly rather than silently — but it fails.)
2. **Applied last, the split is exact by construction.** The far port has by then reached its target
   resistance, so `X_old` comes out equal to the termination's own value to machine precision. On the
   golden problem: far arm C = 38.114 pF at 1.6803 Ω becomes 0.32022 pF at 200 Ω, and the split gives
   0.19522 pF of `CFano` plus **exactly 0.125 pF** kept. Applied to the basis instead, the kept value
   is `0.125 pF x 119.027` and only becomes the load's own value after the transforms have run.

The arm total is unchanged either way, so the response is unchanged — and that is asserted at 1e-12.

### The excess split is worked in C_eq, not in element values

Brief §5 writes `X_new = (Q_far - Q_actual)/(R*omega0)` for a shunt end. That is a *capacitance*
formula. At a shunt end with an **inductive** termination the two inductors combine reciprocally
(`1/L_tot = 1/L_old + 1/L_new`), and the raw formula is simply wrong there. Working the whole split in
`C_eq` and converting back with `MatchQ.FromCeq` makes one formula serve all four
(series|parallel) x (C|L) combinations, which is exactly the property §5.3 claims for the rule and the
reason it is stated in Q.

Worth recording alongside it: **for every end arm, `C_eq` is just the arm's C value**, whichever
element the termination supplies. A shunt arm has `L = 1/(omega0^2 C)`, so `C_eq(L_arm) = C_arm`
identically. That one identity is what makes the inductive case free everywhere downstream.

### `movable` — the general rule, and a design that proves it is needed

The reference expresses it as *"allowed if the pair's type is L, otherwise neither index may be
absorbed"*, which is correct only because in that implementation the absorbed element is always a
capacitor. The general form is

```
movable(a,b) := type(a) is not an absorbed type  ||  neither a nor b is absorbed
```

Fixture that separates them (`NortonTransformTests.MovableRule_...`): replace the golden problem's
1.25 Ω + 10 pF series load with the dual 1.25 Ω + 153.51694 pH. `L4` is then the absorbed element, and
the `L3/L4` pair must be refused — the reference's rule allows it, because its type is L, and moving
it breaks the absorption. A stronger version of the same design (200 Ω ‖ 8 nH against 1.25 Ω + 10 pF
at n = 2) makes **both** L and C absorbed types and leaves the ladder with **no transformable pair at
all**, which the reference implementation would have reported as four available transforms.

### `NoRealRoot` cannot happen for orders 2..6, and the reason is structural

Both the brief and `match.md` treat "P_n(c) has no real root" as a first-class outcome. It is
implemented, it carries its numbers, and **it is unreachable with the design doc's own coefficient
table**: n = 3 and n = 4 give a *cubic* in r, n = 5 and n = 6 a *quintic*, and a real polynomial of
odd degree always has a real root; n = 2 sets r = 1/2 without root-finding at all. Measured over 240
(n, Q, w) combinations spanning `Q` from 1e-6 to 1e6 and `w` from 1e-4 to 1.9: **zero** refusals.
The path is kept (it is the right shape for a future order or a different table, and the guard on
`d = sqrt(c^2/4 + r)` and on `D > 0` still needs somewhere to go), and `MatchRefusalTests` records the
measurement so a reader does not assume it fires.

### Root ordering, pinned

`FanoG` takes the **smallest** real root of `P_n(c)` that satisfies `c^2/4 + r >= 0`, sorted
ascending. A root finder's own ordering is an implementation detail, and a different member of the
family is a different (and generally worse) design with no error anywhere — so the choice has to be
pinned to something stable. It reproduces the design doc: at n = 4 on §4.9's problem the polynomial
has exactly one real root, r = 0.2278528, giving `D = 1.9176740` and
`g = [1, 1.311823, 1.106975, 1.717201, 0.508891, 1.344236]`.

### `FromRoots` must accumulate in complex arithmetic

The Hurwitz factor is rebuilt from a conjugate-closed root set. Taking the real part *after each
linear factor* — which looks safe, since the finished product is real — silently produces a different
polynomial: only the product over a conjugate **pair** is real. The symptom is total and silent, the
same shape as the leading-coefficient trap the brief warns about: every `Gvalues` call returns null,
every family search reports "infeasible", and `maxQFar` comes back 0.0000 for Butterworth and Bessel
at every order. Accumulate in `Complex[]`, take `Real` once at the end.

### The numerical route reproduces the closed form far better than the 0.5 % gate

`MatchPrototypes.Search` on the Chebyshev family at n = 4, scored by worst in-band |S11|:

```
  numerical: 1.000000, 1.311823, 1.107065, 1.717321, 0.509001, 1.344237
  closed:    1.000000, 1.311823, 1.106975, 1.717201, 0.508891, 1.344236
  worst relative error 0.022 %      score -16.6629 dB vs the closed form's -16.663
```

The design doc's own numerical answer was 0.4 % off; the difference is the local refinement pass
described below, not a better extractor.

### The family search refines around BOTH optima, and the second one is not optional

A coarse grid over the shape parameter finds the right neighbourhood, but two different numbers are
read out of the search and each needs its own refinement:

- the **score** optimum, which is flat near its own minimum — without refinement the winning
  g-vector lands a few per cent off the closed form (0.65 % on a 60-point grid);
- the family's **maximum Q_far**, which is what an infeasible response has to quote in its refusal.
  Un-refined it read 0.196 for Bessel at n = 4 against the design doc's 0.325 — a 40 % understatement
  of how far off the family is, in a message whose entire job is to say how far off it is. Refined:
  0.3153 (n = 4) and 0.1826 (n = 6), against the doc's 0.325 and 0.183.

### Measured: Butterworth and Bessel on the design doc's own problem

Worst in-band |S11| over 3.3-5.0 GHz, and the far-end Q against the 0.6381 required:

| n | Butterworth | Bessel |
|---|---|---|
| 2 | **-9.946 dB**, Q_far 1.1415 | **-7.800 dB**, Q_far 0.6381 |
| 4 | **-13.205 dB**, Q_far 0.6702 | *infeasible*, max Q_far 0.3153 |
| 6 | **-8.470 dB**, Q_far 0.6381 | *infeasible*, max Q_far 0.1826 |

against the design doc's 9.94 / 13.20 / 8.29 dB and 0.325 / 0.183. **The n = 6 Butterworth constraint
really does bind**: the best-return-loss member reaches -14.729 dB at Q_far 0.4985 and is rejected;
the constrained best is -8.470 dB. An unconstrained search returns a network that cannot absorb the
far termination at all, with nothing to show for it.

### Cost, measured

- **Solution enumeration at n = 6: 1.6 ms** (8 pairs, 28 candidate sets, 8 solutions). Including the
  15-step Q-adjust bisection, which re-runs the whole enumeration per step: **9.4 ms**. The brief
  asked for this number and set 1 s as the point at which to stop and explain; it is three orders of
  magnitude clear.
- **The numerical route is the expensive part, and it is per-synthesis**: Butterworth 63 / 332 / 534 ms
  at n = 2 / 4 / 6, Bessel 98 / 787 / 1,453 ms. `MatchPrototypeTests` therefore runs ~4 s as a class
  and the Bessel test alone ~2.4 s — under the ~5 s `Category=Benchmark` threshold, so untagged, but
  it is the one thing in this area that would cross it if the grids grew. The grids
  (32 shape steps, 16 scan steps, 40 bisection steps, 200 Durand-Kerner iterations) were tuned down
  from 2.5x that cost with **no** loss on any gate: the cross-check moved from 0.010 % to 0.022 %.
  MN-3 must cache the search rather than re-run it per keystroke — the Chebyshev closed form is
  microseconds, but Butterworth and Bessel are not.

### Smaller things that are still true

- **Propagation is always *away* from the analysis end.** It falls out of `NGreaterThan1` and
  `PropagateRight` together rather than being stated anywhere, and it is what makes two separate
  claims work: the analysis end's absorbed element stays at exactly the termination's own value
  however many transforms are applied, and `achieved = product(N^2)` is a statement about the **far**
  port rather than an average over both.
- **The ratio tolerance is relative.** The reference uses an absolute `1e3*epsilon` (~2.2e-13) against
  a required ratio of ~119 — that is 1e-15 of the quantity being tested, i.e. an exact-equality test
  wearing a tolerance's clothes.
- **Nets are derived by walking the element list, never stored.** Order plus orientation determines
  the topology completely, which collapses brief §7.4's "swap its nets with the neighbour it
  displaced" into a plain list swap, and makes the pi/T net table (`a-gnd, a-b, b-gnd` /
  `a-t, t-gnd, t-b`) fall out of the walk rather than needing to be implemented.
- **Reverse by ARM, not by element.** Turning an analysis-end-first ladder into a Term1-first one by
  reversing the flat element list puts C before L inside every arm; reversing the arm order and
  keeping `(L, C)` inside each preserves the strict type alternation that the move offsets need.
- **The Q-adjust bracket moves are right and their label is not.** Brief §6 annotates both bracket
  updates "move toward MORE detune"; the operations it gives (series `lo = guess`, parallel
  `hi = guess`) both narrow toward LESS detune on a successful guess, which is what finding the
  *minimum* such Q requires — and is what §4.6 asks for. The operations are implemented; the label is
  not. Note also that on the golden problem the minimum Q-adjust is essentially the true Q (the
  design already completes without one), so no `CDetune` is produced there — a test that expects one
  has to set `QAdjust` explicitly.
- **`MatchResponse` is production code, and the test oracle is a second implementation.** The
  constrained family search scores members by the worst in-band return loss of the resulting bandpass
  ladder, so a response evaluator has to exist in `src/Core`; `tests/Core.Tests/Match/MatchAbcdOracle`
  is written separately from it, so the golden numbers are not checked by the code that produced them.
- **Two files beyond the design doc's §12 list**: `MatchResponse.cs` (above) and `MatchRebuild.cs`
  (§7.3's sequential rebuild, which both the Designer's load path and the solution search's inner loop
  need). `MatchPoly` lives inside `MatchPrototypes.cs` to keep the file list otherwise exact.
- **`MatchDesign` uses properties, not fields.** The brief writes `public double F1, F2;`;
  `System.Text.Json` needs `IncludeFields` for those and nullable analysis does not track them.
  The JSON property names are identical either way.

## MN-2 — the component, the symbol and the palette (2026-08-19)

Makes a `Match` placeable, elaboratable and simulatable: `MatchModel`, the factory and elaborator
wiring, a symbol, a registry entry and a new `Matching` palette category. No Designer — MN-3.
Gate: `Core.Tests` 1,454 in 4 s, `Engine.Tests` 1,257 in 4 m 52 s, `Ui.Tests` 8,129 in 30 s,
`Firewall.Tests` 6. All green.

### The elaborator needed a SECOND edit, and without it nothing decodes

The brief allows exactly one change to an existing Core file outside `src/Core/Devices/` — the
internal-node mint — and that is not enough to run a `Match` at all. `Design` is base64 and
`Response` is an enum name; both reach `Elaborator.ResolveParameters`' generic branch, which calls
the expression evaluator on every override with **no** try/catch. Base64 tokenises as an identifier,
so a placed `Match` throws during elaboration before the factory is ever reached. `wBond` already has
exactly this branch for exactly this reason, so `ResolveMatchParameters` is that same rule applied
again: `Design` and `Response` verbatim, the numeric echoes evaluated, and — unlike the generic
branch — a failed echo **swallowed**, since an echo is display-only and `Design` carries the truth.

That branch is also what lets a refusal name the instance. The factory sees a parameter dictionary
and no instance name, so `ResolveMatchParameters` injects `MatchName` the way `ResolveChainParameters`
injects `ChainName`. Without it the message could only have named the type, which on a schematic
holding several `Match`es is not a message.

### One series ARM is one branch, and that is what fixes the internal-node count

`MatchNetwork.AssignNets` gives every SERIES ELEMENT its own node — correct as a topology
description, and not what gets stamped. The stamp groups each maximal run of through-path elements
into ONE branch carrying `Z = jwL + 1/(jwC)` (`InductorModel`'s `L=` plus `C=` shape, DC-open case
included), so `InternalNodeCount` is *series runs − 1*, not *series elements − 1*.

The grouping is not cosmetic: §4.5's `CFano` is inserted **into** an end arm with the same
orientation, so a series end arm can hold three elements and still be one branch — which is exactly
right, because `MatchQ.SplitExcess`'s series case combines reciprocally (`1/C_tot = 1/C_kept +
1/C_added`), i.e. the split element really is in series with the one it came from. Accumulating
`sum(1/C)` reproduces that identically.

Measured: the golden §4.9 design has **2 series runs → 1 internal node** and 7 stamped elements out
of 9 (two absorbed); the shipped default has **1 series run → 0 internal nodes** and 6 elements.

### The hand-built oracle is only an oracle because the node structure DIFFERS

`Engine.Tests`' comparison netlist writes one component per element and one node per series
*element* — deliberately not the component's own grouping. Agreement is then a statement about the
topology and the DC/AC limits rather than a tautology. **Measured worst |ΔS| = 5.4e-16** over nine
frequencies for the golden design and 4.3e-16 for the default, against the brief's 1e-12 gate; the
HB spectra (DC through the 3rd harmonic, driven by a rectifying diode) agree to the same order. The
same fixture stamped **with** the absorbed reactances differs by **0.99** — the invertible mistake is
not subtle once something looks for it, and nothing was looking before.

### Two DC claims need two different circuits, and only one of them is the obvious one

"Shunt arms are shorts" shows up anywhere: every bandpass ladder grounds its through nodes through a
shunt inductor, so both pins read exactly 0 V. **"Series arms are opens" is invisible in that same
circuit** — every node is already grounded, so an open and a short give the same node voltages. It
needs a ladder that STARTS and ENDS with a series arm, which means both terminations declared
`TerminationTopology.Series` (`s[1] = ana.Topology == Series` sets the whole alternation). Then each
pin's only path is through a capacitor-bearing series arm, the source sees no load at all, and the
drop across its own resistor is zero. Tolerance there is 1e-9 and not 1e-12: the DC engine's gmin
leaks ~1e-12 S across every open branch, which is 5e-11 of the source.

### The default design is 1-2 GHz / order 3 / 50 ohms both ends, and it is a real bandpass

Measured through the shipped path: −0.100 dB at both band edges (the 0.1 dB design ripple, exactly),
−0.023 dB mid-band, −27.8 dB an octave out on either side, with |S11| = −16.43 dB at the edges — the
0.1 dB-ripple Chebyshev return loss. Order 3 with both ends parallel-resistive gives
shunt-series-shunt, so it needs no internal net at all.


## Round 2 — N = 1 is a pole, and the range did not exclude it (2026-08-19)

Owner: *"If slider N1 goes to 1, the plots all fail."*

It is not a plotting bug and it is not a clamping accident — **unity is a genuine pole of all four
Norton product formulae**, and `NortonTransform.Range` returned it as a usable bound. Written out at
N = 1 exactly (`Apply`'s own expressions):

| form | products at N = 1 |
|------|-------------------|
| pi   | `N²·z1 / (1 − N)` → **±∞** |
| T    | `(1 − N)·z2` → **0**, which for a capacitor pair inverts to **∞** |

So the ladder acquired a non-finite element and there was no response left to evaluate — not a bad
number the plots could draw and the guards could flag, but no curve at all.

**The threshold end was already an open interval and the unity end was not.** `ThresholdMargin`
(1e-9) had been keeping N strictly inside its positivity threshold since MN-1, for exactly this
reason: *"at the threshold a product is infinite"*. The other end of the same interval is the other
pole of the same formulae, and it was the bare `1.0` — in **both** branches, bounded and
`allowNegative` alike. `UnityMargin` now closes it symmetrically.

**Nothing else moves, and that is checkable rather than hoped for.** At N = 1 ± 1e-9 the products are
enormous but finite, the transfer function is still exactly invariant (a near-identity transformer is
a near-open shunt arm), and the absolute guards report the element as out of range in red — which is
the designed behaviour for an N parked on a bound, not a repair. `MatchRound2Tests` evaluates the
published formula at exactly 1 as its own oracle rather than calling `Apply`, because `Apply` now
clamps and can no longer reach the pole: without that, the exclusion would read as decoration.


## Match speed — the lowpass-prototype search was ~6x slower than it needed to be, and one of those factors was a wrong answer (2026-08-20)

Owner: *"Match Designer appears slow to user when updated parameters… slowest when I change network
order or filter response type. (I believe the step that involves solving the low pass prototype.)"*
Right on both counts. Measured on the design doc's order-4 interstage problem, one specification edit
cost **1,161 ms**, of which **1,143 ms** was `MatchPrototypes.Search`.

### `MatchPoly.Roots` — the Cauchy scaling and the absolute stopping test were one fault, not two

Durand-Kerner needs the roots near the unit circle. The scaling was Cauchy's bound,
`1 + max|a_i/a_0|`, which bounds the **largest** root — so on a polynomial whose coefficients are
skewed it divides the whole set far **below** the unit circle instead of onto it. A Bessel
denominator at `alpha = 5` lands its roots near 1e-3, and the iteration then has to crawl inward from
unit-modulus seeds. The stopping test was an absolute step of 1e-14, which is a different demand at
every root modulus: it passes early on roots that are small, and is unreachable on roots that are
large.

Measured iteration counts at the same degree tell the whole story:

| family, order | degree | iterations, before | after |
|---|---|---|---|
| Butterworth n=4 | 8 | 9–10 | 9 |
| Bessel n=4 | 8 | **112–114** | 10–16 |
| Butterworth n=6 | 12 | 9–10 | 12 |
| Bessel n=6 | 12 | **196–200 (capped)** | 9–14 |

**The n=6 Bessel case was not merely slow — it hit the 200-iteration cap with the roots still
relatively wrong by a factor of 2.5**, and only the Newton polish afterwards made the answer usable at
all. `|a_n/a_0|^(1/n)` is the exact geometric mean of the root moduli (the product of the roots is
`a_n/a_0`), so it puts the set *on* the unit circle; the test is now relative. Residuals were compared
across 36 family/order/shape-parameter combinations and are equal or better everywhere. **16–24x on
the Bessel families.**

### The bracket refinement was 70 % of every `Gvalues` call, at 40 evaluations a bracket

`Search` runs 121 shape values, each scanning 17 points of the second parameter for sign changes, and
each bracket found was refined by **40 fixed bisection steps**. That is ~4,800 of ~6,900 `Gvalues`
calls. The Illinois variant of regula falsi keeps bisection's guarantee (the root stays bracketed, so
it cannot run away on a badly-shaped family the way a bare secant can) and reaches the same interval
in single figures. `SolveOther` also now returns the g-vector **with** the root, which removes the
caller's re-evaluation at the answer — the one `Gvalues` result the old loop computed and threw away.

### The denominator does not depend on the second parameter, in either family

Chebyshev and Butterworth put `K` only in the numerator (`1 + e^2 F^2` against `K + e^2 F^2`); Bessel
puts `C` only in the numerator. But the search holds the shape parameter fixed and sweeps the other
one, so the same denominator was being spectrally factored dozens of times per shape value. Since the
two `Hurwitz` calls are the whole cost of `Gvalues`, hoisting one halves it. A one-entry memo is
enough — the access pattern is a run of calls at one shape value — and it is created per `Search`
call and never shared, which is what keeps it safe on a background thread.

### Round 1 of the refinement computes a number that is only ever printed in a refusal

`Search` refines around **both** optima: the best score, and the family's maximum `Q_far`.
`MaxQFar` is read in exactly one place — `MatchSynthesis`'s `ResponseInfeasible` message — and that
branch is only reached when **no member was feasible**. So when one was, round 1 spent 44 of the
method's 121 shape values on a sentence that was not going to be written. Skipping it changes no
g-vector and no refusal.

**Together: order-4 Butterworth 102 -> 19 ms, order-4 Bessel 199 -> 30 ms, order-6 Butterworth
295 -> 51 ms, order-6 Bessel 342 -> 50 ms.** `Core.Tests`' own Match suite went from 6 s to 2 s.

### `Synthesize` was called four to twenty times per refresh, on identical inputs

`MatchRebuild.Rebuild` does one, `MatchFlattenService.Availability` rebuilds to ask whether there is a
ladder to write, `MatchSolutionSearch.Search` needs the same basis, the Designer probes each of the
four response families — of which the selected one is, again, the same call — and
`MatchSolutionSearch.FindQAdjust` bisects fifteen times with a synthesis inside every step. On a
Chebyshev design that is repetitions of ~2 us. On a Butterworth one it was repetitions of 50 ms.

The memo keys on the complete set of fields `Synthesize` actually reads: F1, F2, Order, Response,
RippleDb, QAdjust, AnalysisEnd and the two `Termination` records. **The transforms are deliberately
absent — they are applied to the basis afterwards and the synthesis never sees them, which is exactly
why this pays on a slider drag**: a non-specification refresh with Butterworth selected went from
**110 ms to 1.5 ms**, because every step of the gesture had been re-deriving a basis that cannot have
changed.

**What comes out is a copy.** The result carries a mutable `MatchNetwork` and a `double[]`; handing
one instance to two callers would let one caller's edit appear in the other's result. Every in-repo
caller happens to treat both as read-only today, and that is not something the cache gets to depend
on.


## MN-LP — lowpass and highpass form (2026-08-28)

match.md §16, §6.9 and brief `brief-match-lowpass-highpass-form.md`. Everything below is a place where
the design note and the implementation disagree, and the implementation is what was measured.

### The orientation is decided by the impedance RATIO, not by the analysis end's topology

**§16.3 and brief §3.3 both say the ladder starts shunt-first for a parallel analysis end and
series-first for a series one, "exactly as today", and that a required orientation which does not
extract is a refusal. There is no such choice to make.**

The family depends on the terminations only through `Γ₀²`, which is invariant under `r ↔ 1/r`, so one
extraction serves both — and the terminating value it produces is `max(r, 1/r)` in **every** cell
measured (5 orders × 6 bandwidths × 6 ratios × 2 families, 360 cells, exact to 1e-13). Reading that
g-vector as a series-first ladder puts `R_far = g_last · R_ana`; reading it as its dual, shunt-first,
puts `R_far = R_ana / g_last`. Exactly one lands on the requested resistance.

Said physically, which is how to remember it: **the low-impedance port sees a series inductor and the
high-impedance port sees a shunt capacitor** (lowpass; series C and shunt L for highpass). That is the
L-match rule, and every order inherits it.

**The consequence is a constraint on absorption that §16.4 does not state.** Its item 1 — a lowpass
ladder absorbs `R ‖ C` and `R + L` — is true, but *which end* takes which is fixed by the ratio. A
shunt capacitance on the LOW-impedance side of a step-up is absorbable by neither lowpass nor highpass
form, and its refusal has to blame the ratio rather than the kind, because "use highpass form" is
wrong advice there. `MatchFormSynthesis.CheckAbsorbable` writes two different sentences for the two
cases; `AnAbsorbableKindOnTheWrongEnd_IsRefusedForTheRatio_NotForTheKind` holds them apart.

A corollary worth knowing before hunting for a bug in the search: **a pair with a reactance at each end
can only ever list ONE of lowpass and highpass**, because the two forms want dual kinds. Only a purely
resistive pair lists all three forms.

### §3.7's physical golden values do not describe a 50 Ω far end

The normalised g-vector is exact and reproduces to 1e-9 (`2.485340, 0.674662, 6.761736, 0.247821,
10.000000` at a = 0.5, n = 2, r = 10, K = 1e-6). The physical line beneath it —
`C1 = 15.8222 pF, L1 = 107.376 pH, C2 = 43.0466 pF, L2 = 39.4420 pH` for *"5 Ω (analysis, parallel
side) → 50 Ω"* — is those g's denormalised at R_ana = 5 Ω and read **shunt-first**, which by the
paragraph above is the r = 0.1 network: 5 Ω down to **0.5 Ω**. Simulated as printed into 50 Ω it is a
**−0.10 dB** match, not the −10.511 dB the same block quotes. Series-first at R_ana = 5 Ω into 50 Ω,
and shunt-first at R_ana = 50 Ω into 5 Ω, both give −10.511 dB exactly.

Goldens B, C and D are all self-consistent as the 5 → 0.5 Ω problem (`g₁_actual = ω_c·R·C` uses
R = 5 Ω throughout), which is how the slip was located rather than guessed at. **Only the "→ 50 Ω"
annotation is wrong**; every number in §16.2's table, and Golden B's `K = 0.086588` with its −8.010 dB,
reproduce to the digits printed.

### The polynomial route cannot solve this family past order 4 — and the fix is two changes, not one

§16.3 says §6.2's spectral factorisation and Cauer extraction "apply unchanged". They do in principle
and do not in double precision: the polynomials are degree **4n in s** (24 at order 6) with
coefficients spanning many decades. Following `MatchPrototypes` literally — build the polynomial,
root-find it, `Extract` — **fails 144 of the 360-cell sweep, including every order-6 cell at every K.**
`MatchFormPrototype` takes it to **zero** with two independent changes; either alone is not enough:

1. **The roots are written down, not searched for.** Both polynomials are `c + ε²Φ`, so their roots
   solve `Φ(x) = −c/ε²` and map through `x → u → s` by exact arithmetic. One arccosine (Chebyshev) or
   one root of unity (Butterworth). No degree-4n polynomial is ever formed. Take the arccosine in the
   `π/2 − j·asinh(y)` form — its argument is `+jy`, the branch where `−j log(z + j√(1−z²))` has no
   cancellation; the other sign of y is the branch where it does.
2. **The continued fraction's degree drop is STRUCTURAL.** `MatchPrototypes.Extract` subtracts `g_k s`
   and then asks a tolerance which leading coefficients cancelled. The top **two** always do,
   identically — but at order 6 the second survives at ~1e-4 of its neighbours after twelve steps of
   cancellation, the length test fails, and the whole extraction returns null. Dropping both because
   they are known to be zero removes the question. **The last removal is the exception**: the remainder
   is then the terminating resistance itself, so only one comes off, which is what the
   `max(1, b.Length - 1)` is for. Getting that wrong costs the terminating ratio and nothing else
   visible — it just returns null.

None of this touched `MatchPrototypes`; the bandpass route is unchanged and still passes its own
cross-checks.

### K's floor is a RETURN-LOSS floor, and 1e-6 is too high

§16.9 is right that K = 0 is a trap (double jω-axis roots make "the left half" undefined and the
tie-break takes both copies of the lowest pairs) and right that the difference is "below 0.01 dB" — but
only above the floor it sets. **K is the in-band return-loss floor as well as a numerical guard**: the
worst `|Γ|²` is `(K + ε²)/(1 + ε²)`, so it caps the response at `10 log10 K` whatever the family could
otherwise reach. At 1e-6 that is −60 dB, which

- costs **0.12 dB** on §16.2's own −44.3 dB cell — twice the 0.05 dB the acceptance test allows, and
- puts the from-DC reduction **2.4e-3** off the textbook 0.1 dB table, against that test's 1e-5.

The error scales as √K and nothing degrades on the way down: every floor from 1e-6 to **1e-14**
extracts all 360 cells with the terminating ratio exact to 1e-13; only K = 0 fails. `KFloor = 1e-12` is
taken — the from-DC reduction reaches 2.4e-6 and the g-values are converged to five significant
figures. match.md's Golden A was computed at 1e-6 and its **Golden E at 1e-9** (its numbers are the
1e-9 values exactly, not the 1e-6 ones), which is how the inconsistency surfaced; both are asserted at
their own K rather than against the constant.

### K is not monotone in the near-end element

§16.4 item 3 calls this "a 1-D monotone problem" and both the brief and the design note prescribe a
bisection. The far element does fall monotonically. **The near element rises and then falls**, peaking
near `K = 0.95 Γ₀²` — measured, a = 0.5, n = 2, r = 10: g₁ goes 2.485 at the floor → **11.00 at
K = 0.64** → 4.35 at K = 0.66942 (Γ₀² = 0.669421). A bisection written for a monotone g₁ finds the
wrong end of the feasible interval or misses it, and it also under-reports the family's largest g₁ in
the refusal — a log grid samples the top of the range twice and gave 7.98 where the truth is 11.00.

What is implemented is a **linear** scan of 128 members over `[K_floor, Γ₀²)` followed by a geometric
bisection on the boundary it brackets. Linear because all the structure is in the top few per cent;
geometric on the refinement because K_min routinely sits decades below the first grid step.

### A bigger termination is HARDER at a higher order — the opposite of the bandpass intuition

With the ratio pinned, more elements means better return loss and a gentler ladder, so the **end
elements shrink with order**: at a = 0.5, r = 10 the near element is 2.485 at order 2 and 0.820 at
order 6. A termination sized for a bandpass order-4 network absorbs at order 2 and is refused at 5 and
6. This bit while choosing a Designer test fixture and would bite a user the same way; it is why
`MatchFormDesignerTests.Problem` uses 0.5 pF and 0.1 nH rather than the round numbers a bandpass
fixture would.

### `MatchSolutionSearch` had no way to say "finished, with no transforms"

`EnumerateSets` emits only NON-EMPTY transform sets, so a basis already on target produced zero
solutions and the panel reported `NoTransformablePairs` — a refusal — about a network that needs none.
§16.5's "each cell contributes exactly one solution" is only true with the extra emission that is now
in `SolutionsFor`, and it is written as a property of the **ratio** (`|required − 1| ≤ tolerance`)
rather than of the form, because that is what it is.

`NortonTransform.Discover` needed nothing: a single-element ladder alternates L-series with C-shunt, so
every like-kind pair shares an orientation and the existing `Opposite` test rejects all of them. It
still returns empty after the end splits, since a surplus element is the same kind and orientation as
the one it sits beside.

The Q-adjusted variant IS skipped in these forms (§16.4 item 3) and had to be skipped explicitly: it
produced a second, identical zero-transform row under a different label, and `FindQAdjust` bisects
toward "some transform set completes", which never happens here.

### The end split is linear in prototype units — all four combinations

`MatchQ.SplitExcess`'s C_eq algebra exists because a bandpass arm holds both an L and a C. A
lowpass/highpass end arm is one element, and **the surplus is `g_synth − g_actual` in every case**: a
shunt C's g is `ωRC` and parallel capacitances add; a series L's is `ωL/R` and series inductances add;
a shunt L's is `R/(ωL)` and parallel inductances add reciprocally, which is the same thing in g; a
series C's is `1/(ωRC)`, likewise. So `MatchFormSynthesis.WithEndSplits` is three lines, and the
absorbed element's value is the termination's own **exactly** rather than to a tolerance.

One deliberate consequence: the feasibility test requires `g_synth ≥ g_actual` with **no** slack. A
member that misses by one part in 1e9 would leave an element marked absorbed while carrying a value the
termination does not supply — the "a parasitic cannot be subtracted" failure §4.6's own 2026-08-20 note
records. The 1e-9 slack that does exist only widens the family's side, and only for the refusal's
wording.

### r = 1 has no member of this family, and is handled as a named special case

The pin reads `|Γ(0)| = 0`, which this family meets only at K = 0 — the trap. So for `Γ₀² ≤ 4·K_floor`
the pin is dropped, K sits on its floor and ε is fixed for a nominal −40 dB in-band, giving **one**
member and no search. Brief §3.2 says this "only matters for a purely resistive equal pair"; that is
where it is honest. A 1:1 match with a reactive end either fits that single member or is refused, and
the result carries a `Notes` line saying so. Making ε the search parameter in that branch would fix it
and was not done — it is a second search with its own bounds, for a case the brief calls degenerate.

### Two smaller things

- **§16.3's "Φ is degree n in u" is a slip.** `T_n(x)` is degree n in x and x is degree 1 in u, so the
  square is degree **2n**. Every conclusion the section draws from it (2n elements, degree 4n in s) is
  the arithmetic for 2n and is right.
- **`ValidOrders` had to take the form, and `AdjustOrderForParity` had to learn that the list can be
  EMPTY.** A like-topology pair has orders 3 and 5 in bandpass form and none in the other two, and the
  old code's `valid.OrderBy(...).First()` throws on an empty list. The solutions FILTER is built from
  the union across the three forms, not from the design's own form: a lowpass design with a like pair
  would otherwise have no order lines while the panel listed bandpass rows at 3 and 5, and `Accepts`
  shows a row whose order has no line — so every one of them would have been unhideable.
